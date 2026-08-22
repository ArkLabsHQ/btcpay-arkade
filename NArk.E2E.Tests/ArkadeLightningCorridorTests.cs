using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Client;
using Microsoft.Extensions.DependencyInjection;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;
using Microsoft.Playwright;
using NArk.Tests.End2End.Common;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// The Arkade Lightning corridors, end to end against a real solver.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not part of CI, and deliberately so.</b> These need a swap solver, a claim daemon and a
/// covenant emulator running beside the regtest stack, and CI starts none of them. They are skipped
/// unless <c>ARKADE_E2E_SOLVER_URL</c> names a solver, so a normal run is unaffected.
/// </para>
/// <para>
/// To run them:
/// <code>
/// # 1. the stack. The overrides are not optional — arkd's defaults are block counts and the
/// #    corridors need seconds. VTXO_TREE_EXPIRY must exceed the solver's own 7200s refund
/// #    horizon, not merely clear the SDK's 6000s floor: at 6144 the solver has no float that
/// #    outlives the horizon and refuses to fund, which it reports only in its own log.
/// ARKD_VTXO_TREE_EXPIRY=15360 ARKD_UNILATERAL_EXIT_DELAY=512 \
/// ARKD_PUBLIC_UNILATERAL_EXIT_DELAY=512 ARKD_BOARDING_EXIT_DELAY=2048 \
/// ARKD_CHECKPOINT_EXIT_DELAY=1536 COVCLAIMD_IMAGE=ghcr.io/arkade-os/covclaimd:v0.0.1-rc.4 \
/// node submodules/NNark/regtest/regtest.mjs start --clean --profile emulator,covclaimd,boltz
///
/// # 2. the solver (arkade-os/lightning-swap-service). Rebuild it — a stale dist/ predating the
/// #    decimal-string amount encoding refuses every request with unsupported_payload — and
/// #    re-copy the stack's LND credentials, which a --clean regenerates:
/// #      docker cp boltz-lnd:/root/.lnd/tls.cert ./boltz-lnd-tls.cert
/// #      docker cp boltz-lnd:/root/.lnd/data/chain/bitcoin/regtest/admin.macaroon ./boltz-lnd-admin.macaroon
/// #    Then fund its Arkade wallet and serve:
/// #      node scripts/regtest-fund.mjs &lt;regtest-dir&gt; 0.05 &amp;&amp; node scripts/regtest-settle.mjs
/// PORT=7095 node --experimental-eventsource --env-file=.env.regtest.lnd dist/cli.js serve
///
/// # 3. the suite's own prerequisites, both easy to miss because the failures name neither:
/// dotnet run --project ConfigBuilder/ConfigBuilder.csproj    # writes DEBUG_PLUGINS
/// find NArk.E2E.Tests/bin -name playwright.ps1               # then install chromium
///
/// # 4. this suite, with BTCPay pointed at the stack's backing services
/// TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;admin1:123" \
/// TESTS_BTCNBXPLORERURL="http://127.0.0.1:32838/" \
/// TESTS_POSTGRES="Host=localhost;Port=39372;Database=btcpay_e2e_test;Username=postgres" \
/// TESTS_HOSTNAME=127.0.0.1 ARKADE_E2E_SOLVER_URL=http://127.0.0.1:7095 \
///   dotnet test NArk.E2E.Tests --filter "Category=LightningCorridors"
/// </code>
/// </para>
/// <para>
/// <b>Status as last run.</b> The send corridor and the invoice-amount check pass against a live
/// solver. <see cref="PaidLightningInvoice_CreditsTheStoreOnArkade"/> does not: the solver quotes
/// and mints the invoice, then fails to fund its lockup with
/// <c>Invalid Arkade address: undefined</c>. That is solver-side and visible at compile time —
/// <c>src/receive/fundLockup.ts</c> calls <c>wallet.send({ recipients: [...] })</c> where its
/// pinned ts-sdk takes <c>{ address, amount }</c>, so the build errors and the emitted call passes
/// an address the SDK reads as undefined.
/// </para>
/// <para>
/// An <c>http://</c> solver URL selects the HTTP transport. Production solvers run outbound-only
/// behind a relay, which is what a <c>ws://</c> URL selects — that also needs
/// <c>ARKADE_E2E_SOLVER_PUBKEY</c>, since a relay carries everyone's traffic and the key is what
/// picks one counterparty out.
/// </para>
/// </remarks>
[Collection("Arkade Plugin Tests")]
[Trait("Category", "LightningCorridors")]
public class ArkadeLightningCorridorTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ArkadeLightningCorridorTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The invoice a customer is handed is for the order amount, not the order amount plus the
    /// solver's spread.
    /// </summary>
    /// <remarks>
    /// The regression this file exists for. The corridor can pin either leg of the swap: pinning the
    /// payout bills the customer the spread on top, pinning the charge takes it out of what the
    /// store receives. Only the second is payable — a LUD-06 wallet compares the invoice against the
    /// amount its user approved and refuses anything larger, so the first loses the sale outright,
    /// and on a checkout that skips that comparison it overcharges silently instead.
    ///
    /// Asserted on the BOLT11 itself rather than on anything the plugin reports, because the invoice
    /// is what actually reaches the customer's wallet and it is the solver, not the plugin, that
    /// mints it.
    /// </remarks>
    [Fact]
    public async Task CreateInvoice_BillsThePayerTheOrderAmount()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        const long orderSats = 25_000;

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, orderSats);
        var decoded = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);
        var invoiceSats = (long)decoded.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);

        Assert.Equal(orderSats, invoiceSats);
    }

    /// <summary>
    /// A customer pays the invoice over Lightning and the store is credited on Arkade.
    /// </summary>
    /// <remarks>
    /// The whole receive corridor in one pass: the solver mints a hold invoice, the payment arrives,
    /// the solver funds a covenant lockup on Arkade, and the plugin claims it — which is both how
    /// delivery is taken and how the solver gets paid, since the claim publishes the preimage.
    ///
    /// The balance is asserted to have grown rather than to equal the order amount: the solver's
    /// spread comes out of the payout on this leg, and its size is a quote-time decision this test
    /// has no business predicting.
    /// </remarks>
    [Fact]
    public async Task PaidLightningInvoice_CreditsTheStoreOnArkade()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        const long orderSats = 25_000;

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var before = await ReadAvailableBalanceSatsAsync();

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, orderSats);

        // The local node pays it. The hold clears only once our claim reveals the preimage, so this
        // returning at all already means the corridor completed its round trip.
        await DockerHelper.Exec("lnd", ["lncli", "--network=regtest", "payinvoice", "--force", bolt11]);

        var after = await PollForBalanceAsync(storeId, before + 1, TimeSpan.FromMinutes(5));

        Assert.True(after > before, $"balance did not grow after the invoice was paid ({before} -> {after})");
    }

    /// <summary>
    /// The store pays a Lightning invoice out of its Arkade balance.
    /// </summary>
    /// <remarks>
    /// The send corridor, and the replacement for <c>SwapsTests.PayLightningInvoice_CreatesSubmarineSwap</c>
    /// — that one asserts a Boltz submarine swap, which nothing produces any more.
    ///
    /// Asserted on the payee being paid rather than on any row the plugin wrote: funding the lockup
    /// is all the plugin does, and whether the payment happened is a fact about the payee's node.
    /// </remarks>
    [Fact]
    public async Task PayLightningInvoice_SettlesAtThePayee()
    {
        RequireSolver();

        var (storeId, _) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        await FundWalletViaNoteAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider, walletId!, 200_000);

        // Swap-eligible coins are the non-recoverable ones; wait on them being spendable rather than
        // on the rendered balance, which moves earlier.
        //
        // Generously timed, and the slow part is not the corridor. Funding here redeems an arkd note,
        // which needs a batch round, and the redemption intent is refused with AMOUNT_TOO_LOW until
        // the wallet holds a shape arkd will take — retried every few seconds meanwhile. Observed
        // between twenty seconds and over five minutes on the same stack, so a five-minute ceiling
        // fails on the corridor's behalf for something upstream of it.
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 30_000, TimeSpan.FromMinutes(10));
        Assert.NotEmpty(outpoints);

        var bolt11 = await DockerHelper.CreateLndInvoice(amtSats: 20_000, expirySecs: 1800);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        var settled = await SpendToLightningAsync(storeId, bolt11, outpoints, token);
        Assert.True(settled, "the payee never saw the invoice settle");

        // And the swap's own record agrees, with the txid of the spend that settled it.
        var intent = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Fulfilled);
        Assert.False(string.IsNullOrEmpty(intent.SpentTxid), "a fulfilled send swap must record the spend that settled it");

        // Deliberately not asserted: intent.Preimage, which is empty here. The monitor recovers the
        // preimage out of the solver's claim witness — that recovery is precisely what proves the
        // fill — and then discards it, because ProvesFill
        // (NArk.ArkadeIntents/Services/ArkadeSwapIntentMonitoringService.cs) returns a bool rather
        // than the value it found. Nothing on this leg ever assigns Preimage, so the receipt a
        // Lightning node would hand back is lost, and BTCPay's LightningPayment.Preimage is null on
        // every completed Arkade payment. Worth fixing in the SDK; asserting it here would only fail
        // a suite for a gap it cannot close.
    }


    // ─── What happens when it does not work ───────────────────────────

    /// <summary>
    /// A size no solver will quote yields no invoice at all, rather than one nothing can settle.
    /// </summary>
    /// <remarks>
    /// The property worth holding is negative: when the corridor cannot serve an order, the customer
    /// must not be handed a BOLT11. A payer who pays an invoice the solver never agreed to back has
    /// moved real money into a swap that will never be funded, and the only way out is the payer's
    /// own HTLC lapsing — a refund they wait for rather than one anybody issues.
    ///
    /// Driven with an amount past the solver's float rather than a malformed request, because that
    /// is the refusal a real deployment meets: a solver that is up, honest and simply too small for
    /// this order.
    /// </remarks>
    [Fact]
    public async Task CreateInvoice_ForMoreThanTheSolverCanFund_HandsOutNoInvoice()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateLightningInvoiceAsync(client, storeId, 5_000_000_000L));

        // Either BTCPay refuses the payment method outright, or it creates the invoice and no
        // Lightning destination ever appears. Both are acceptable; handing out a BOLT11 is not.
        Assert.True(
            ex is GreenfieldAPIException or TimeoutException,
            $"expected a refusal or an absent destination, got {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>A sub-dust order is refused before any solver is contacted.</summary>
    /// <remarks>
    /// Refused locally on purpose. An amount below the Arkade dust floor cannot become a VTXO no
    /// matter what any solver quotes, so opening a negotiation for one spends a round trip to learn
    /// something already known — and leaves a quote the solver has to expire.
    /// </remarks>
    [Fact]
    public async Task CreateInvoice_BelowDust_IsRefusedWithoutNegotiating()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateLightningInvoiceAsync(client, storeId, 100));
    }

    /// <summary>An invoice nobody pays credits nobody.</summary>
    /// <remarks>
    /// The mirror of the round-trip test, and the one that would catch a corridor reporting success
    /// on the strength of having negotiated rather than having been paid. Worth asserting explicitly
    /// because the failure mode is silent and expensive: a store that books unpaid orders as settled
    /// ships goods for free.
    ///
    /// Asserted on both surfaces a merchant would look at — the balance and the swap's own state —
    /// since either agreeing with the truth while the other does not is still a defect.
    /// </remarks>
    [Fact]
    public async Task UnpaidInvoice_CreditsNothingAndStaysUnfulfilled()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var before = await ReadAvailableBalanceSatsAsync();

        await CreateLightningInvoiceAsync(client, storeId, 25_000);

        // Long enough for a funded lockup to have been observed and claimed, had one existed: the
        // monitor is event-driven and the round trip takes seconds when a payer actually pays.
        await Task.Delay(TimeSpan.FromSeconds(45));

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var after = await ReadAvailableBalanceSatsAsync();
        Assert.Equal(before, after);

        var intents = await ReadIntentsAsync(walletId!);
        Assert.DoesNotContain(intents, i => i.Status == ArkadeSwapIntentStatus.Fulfilled);
    }

    // ─── What happens as it progresses ────────────────────────────────

    /// <summary>A receive swap is recorded before its invoice is handed out, and settles to Fulfilled.</summary>
    /// <remarks>
    /// The ordering is the point, not the endpoint. The swap's row carries the preimage, and the
    /// preimage is the only thing that can claim the solver's lockup — we chose it, and the only
    /// other copy is sealed to a key we do not hold. So the row has to exist before a payer can
    /// possibly pay, or a crash in that window strands funds nobody can take.
    /// </remarks>
    [Fact]
    public async Task ReceiveSwap_IsRecordedBeforePaying_ThenReachesFulfilled()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);

        var bolt11 = await CreateLightningInvoiceAsync(client, storeId, 25_000);

        // Before anybody pays: the swap exists, knows its invoice, and holds the preimage.
        var recorded = Assert.Single(await ReadIntentsAsync(walletId!, ArkadeSwapIntentType.LightningToBtc));
        Assert.Equal(bolt11, recorded.Invoice);
        Assert.False(string.IsNullOrEmpty(recorded.Preimage), "the preimage must be stored before the invoice is payable");
        Assert.NotEqual(ArkadeSwapIntentStatus.Fulfilled, recorded.Status);

        await DockerHelper.Exec("lnd", ["lncli", "--network=regtest", "payinvoice", "--force", bolt11]);

        var settled = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Fulfilled);
        Assert.Equal(recorded.Id, settled.Id);
    }

    // ─── LNURL, which is where LUD-06 actually bites ──────────────────

    /// <summary>
    /// The BOLT11 an LNURL callback returns is for the amount the customer approved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same property as <see cref="CreateInvoice_BillsThePayerTheOrderAmount"/>, but through the
    /// path where it is actually enforced. LUD-06 is an LNURL rule, and a wallet applies it here: it
    /// asks the callback for a specific number of millisatoshis and compares what comes back. Going
    /// through Greenfield asserts what the plugin minted; going through the callback asserts what a
    /// customer's wallet would accept.
    /// </para>
    /// <para>
    /// Worth having both. The corridor could mint correctly and still be handed a different amount by
    /// the LNURL layer — BTCPay records the requested amount and passes the solver's invoice through
    /// without comparing them (UILNURLController), so nothing between here and the customer checks
    /// this but the customer's own wallet.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LnurlCallback_ReturnsAnInvoiceForTheAmountAsked()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        const long orderSats = 25_000;

        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = orderSats,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["BTC-LNURL"] }
        });

        // What a wallet fetches first: the pay request, carrying the callback and the range.
        var payRequest = await GetJsonAsync($"/BTC/lnurl/pay/i/{invoice.Id}");
        var callback = payRequest.GetProperty("callback").GetString();
        Assert.False(string.IsNullOrEmpty(callback), "the LNURL pay request carried no callback");

        var minSendable = payRequest.GetProperty("minSendable").GetInt64();
        var maxSendable = payRequest.GetProperty("maxSendable").GetInt64();
        var askMsat = orderSats * 1000;
        Assert.InRange(askMsat, minSendable, maxSendable);

        // And what it does next: ask for exactly that, then check what it got.
        var separator = callback!.Contains('?') ? "&" : "?";
        var callbackResponse = await GetJsonAsync($"{callback}{separator}amount={askMsat}");

        var pr = callbackResponse.TryGetProperty("pr", out var prValue) ? prValue.GetString() : null;
        Assert.False(string.IsNullOrEmpty(pr),
            $"the callback returned no invoice: {callbackResponse}");

        var decoded = BOLT11PaymentRequest.Parse(pr!, Network.RegTest);
        Assert.Equal(askMsat, (long)decoded.MinimumAmount.MilliSatoshi);
    }

    // ─── Deadlines the customer is shown ──────────────────────────────

    /// <summary>
    /// The invoice's life is the solver's to set, and a longer checkout window does not extend it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The solver mints the invoice and picks its own expiry — a fixed thirty minutes, derived from
    /// its refund horizon less the claim window — and the expiry BTCPay asks for is dropped, because
    /// nothing in the RFQ request carries one. That is fine while a store's checkout window is
    /// shorter, and BTCPay's default fifteen minutes is.
    /// </para>
    /// <para>
    /// <b>A merchant who lengthens it has a real problem, and this test records it rather than
    /// fixing it.</b> Measured here: a sixty-minute checkout hands out an invoice that dies after
    /// thirty, so half the window a customer is shown counting down cannot be paid in. Sixty minutes
    /// is an ordinary setting for larger orders. A customer who waits and then pays, pays into an
    /// expired invoice.
    /// </para>
    /// <para>
    /// Left as a characterisation because the fix is a product decision, not a defect to patch
    /// quietly: refusing to mint above the solver's window would break those stores outright, and
    /// capping BTCPay's own checkout from a payment method is a bigger reach than a Lightning client
    /// should make on its own. What the plugin does do correctly is report the real deadline —
    /// ArkadeIntentLightningMapper reads ExpiresAt off the minted BOLT11, not off the request — so
    /// the information is there for whoever decides.
    /// </para>
    /// <para>
    /// Asserted as the relationship rather than the literal thirty minutes, so it still means
    /// something if the solver's window changes, and fails loudly if it ever grows past the checkout.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CheckoutWindow_OutlastsTheInvoiceItOffers()
    {
        RequireSolver();

        var (storeId, client) = await SetUpStoreAsync();
        var checkoutWindow = TimeSpan.FromMinutes(60);

        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = 25_000,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = ["BTC-LN"],
                Expiration = checkoutWindow
            }
        });

        var bolt11 = await ReadLightningDestinationAsync(client, invoice.Id);
        var decoded = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);

        var checkoutEnds = invoice.ExpirationTime;
        var invoiceEnds = decoded.ExpiryDate;

        // The gap is the unpayable tail of the checkout. Recorded, not tolerated silently.
        var unpayable = checkoutEnds - invoiceEnds;

        Assert.True(
            unpayable > TimeSpan.Zero,
            $"the invoice now outlives the {checkoutWindow.TotalMinutes:F0}-minute checkout " +
            $"(checkout ends {checkoutEnds:u}, invoice {invoiceEnds:u}) — the constraint this test " +
            "documents has changed, and the remarks above are stale");

        TestLogs.LogInformation(
            $"checkout window {checkoutWindow.TotalMinutes:F0} min, invoice window " +
            $"{(invoiceEnds - DateTimeOffset.UtcNow).TotalMinutes:F0} min, " +
            $"unpayable tail {unpayable.TotalMinutes:F0} min");
    }

    // ─── Spending somebody else's wallet ──────────────────────────────

    /// <summary>
    /// A store holding only a receive-only connection string cannot mint invoices or pay them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capability is what separates a store that may spend an Arkade wallet from one that merely
    /// references it by id. Both connection strings name the same wallet, so without the check the
    /// second store would be spending the first store's money.
    /// </para>
    /// <para>
    /// Asserted through the corridor rather than against the capability service, because the gate
    /// lives at the top of Pay and CreateInvoice and was grafted onto this client during a rebase —
    /// the unit tests cover the capability, not that the corridor consults it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReceiveOnlyConnectionString_CannotMintInvoices()
    {
        RequireSolver();

        var (ownerStoreId, client) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(ownerStoreId);
        Assert.False(string.IsNullOrEmpty(walletId));

        // A second store pointed at the same wallet, by id and nothing else.
        var borrowerStoreId = await CreateStore("borrower");
        await client.UpdateStorePaymentMethod(borrowerStoreId, "BTC-LN", new UpdatePaymentMethodRequest
        {
            Enabled = true,
            Config = JObject.FromObject(new { connectionString = $"type=arkade;wallet-id={walletId}" })
        });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateLightningInvoiceAsync(client, borrowerStoreId, 25_000));

        Assert.True(
            ex is GreenfieldAPIException or TimeoutException,
            $"a receive-only store minted an invoice instead of being refused ({ex.GetType().Name}: {ex.Message})");
    }

    // ─── The money-safety path ────────────────────────────────────────

    /// <summary>
    /// A send swap the solver never fills is refunded to us, and the sats come back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one path where being wrong costs the merchant their own money rather than a sale. Every
    /// other failure here ends with a payer's HTLC lapsing; this one ends with our sats sitting in a
    /// covenant, and only our own refund gets them out.
    /// </para>
    /// <para>
    /// The non-fill is arranged with a HOLD invoice whose preimage is never revealed, which is the
    /// honest version of this scenario rather than a simulated one: the solver really does pay, the
    /// HTLC really is accepted, and it genuinely cannot claim our lockup — claiming means revealing
    /// the preimage, and the payee has not released it. Stopping the solver would have produced the
    /// same states without exercising that.
    /// </para>
    /// <para>
    /// <b>Needs a solver quoting a short refund horizon, and skips without one.</b> Mining ahead of
    /// the locktime is not enough, which is worth writing down because it looks like it should be:
    /// the two clocks are read by different halves. Executing a refund is gated on median-time-past
    /// (<c>AssertLocktimeReachedAsync</c>), so the chain must genuinely be past the locktime — but
    /// deciding a swap has BECOME refundable is gated on the wall clock
    /// (<c>ArkadeSwapIntentMonitoringService</c> passes <c>_time.GetUtcNow()</c> into
    /// <c>SwapObservation.PastLocktime</c>). Moving the chain forward with <c>setmocktime</c>
    /// therefore never triggers the transition, and the swap sits at Pending until real time
    /// catches up. The two agree in production, where both clocks advance together; they only come
    /// apart on a regtest someone is fast-forwarding.
    /// </para>
    /// <para>
    /// So this waits out the solver's horizon for real, and the only way to make that quick is a
    /// solver built with a shorter one (<c>MAX_REFUND_HORIZON</c> in <c>src/core/receive.ts</c>).
    /// Set <c>ARKADE_E2E_SHORT_REFUND_HORIZON</c> to the horizon in seconds once such a solver is
    /// running. The refund logic itself is covered by the SDK's own unit tests
    /// (<c>LightningSendRefundTests</c>, <c>RefundMaturityTests</c>, <c>UnilateralLadderTests</c>);
    /// what is uncovered without this is the plugin's wiring to them.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "LightningCorridorsDestructive")]
    public async Task UnfilledSendSwap_IsRefundedOnceItsLocktimePasses()
    {
        RequireSolver();

        var horizonRaw = Environment.GetEnvironmentVariable("ARKADE_E2E_SHORT_REFUND_HORIZON");
        Assert.SkipWhen(
            !int.TryParse(horizonRaw, out var horizonSeconds) || horizonSeconds <= 0,
            "needs a solver quoting a short refund horizon; set ARKADE_E2E_SHORT_REFUND_HORIZON to it " +
            "(see this test's remarks for why mining ahead does not substitute)");

        var (storeId, _) = await SetUpStoreAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        await FundWalletViaNoteAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider, walletId!, 200_000);

        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 30_000, TimeSpan.FromMinutes(10));
        Assert.NotEmpty(outpoints);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var fundedBalance = await ReadAvailableBalanceSatsAsync();
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        // A hold invoice: paid, accepted, never settled — so the preimage the solver needs to take
        // our lockup is never published.
        var preimage = RandomNumberGenerator.GetBytes(32);
        var paymentHash = Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
        await DockerHelper.Exec(
            "lnd", ["lncli", "--network=regtest", "addholdinvoice", paymentHash, "20000"]);

        var held = await ReadHoldInvoiceAsync(paymentHash);
        Assert.False(string.IsNullOrEmpty(held), "lnd did not return a hold invoice");

        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"StoreId={Uri.EscapeDataString(storeId)}" +
                       $"&VtxoOutpointsRaw={Uri.EscapeDataString(string.Join(",", outpoints))}" +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(held!)}"
            });
        Assert.True(resp.Ok, $"build-intent (LN) returned {resp.Status}: {await resp.TextAsync()}");

        // The lockup is funded and waiting on a fill that cannot come.
        var funded = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending);
        var locktime = funded.RefundLocktime
            ?? throw new InvalidOperationException("the swap recorded no refund locktime");

        // Necessary but not sufficient: the spend cannot confirm until median-time-past crosses the
        // locktime, and mining is what moves it. The transition that starts the refund waits on the
        // wall clock regardless — see this test's remarks.
        await AdvanceChainPastAsync(locktime);

        var waitFor = TimeSpan.FromSeconds(horizonSeconds) + TimeSpan.FromMinutes(2);

        var refunded = await PollForIntentStatusAsync(
            walletId!, ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Cancelled, waitFor);
        Assert.False(string.IsNullOrEmpty(refunded.SpentTxid), "a refunded swap must record the spend that returned the sats");

        // And the sats are ours again, less what the refund itself cost to make.
        var returned = await PollForBalanceAsync(storeId, fundedBalance - 20_000, TimeSpan.FromMinutes(5));
        Assert.True(returned > 0, "the refund did not restore a spendable balance");
    }

    /// <summary>Fetches JSON from the running BTCPay, absolute or server-relative.</summary>
    /// <param name="url">The URL to fetch.</param>
    /// <returns>The parsed body.</returns>
    /// <remarks>
    /// Uses Playwright's request context rather than a bare HttpClient so the call carries the same
    /// session as the browser. LNURL itself is unauthenticated, but the callback BTCPay hands back is
    /// absolute and points at the tester's own host, and reusing one client keeps that consistent.
    /// </remarks>
    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var absolute = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(ServerUri!, url).AbsoluteUri;

        var response = await Page!.Context.APIRequest.GetAsync(absolute);
        var body = await response.TextAsync();

        Assert.True(response.Ok, $"GET {absolute} returned {response.Status}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Reads back the BOLT11 of a hold invoice by its payment hash.</summary>
    private static async Task<string?> ReadHoldInvoiceAsync(string paymentHash)
    {
        var raw = await DockerHelper.Exec(
            "lnd", ["lncli", "--network=regtest", "lookupinvoice", paymentHash]);

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("payment_request", out var pr) ? pr.GetString() : null;
    }

    /// <summary>
    /// Drags the chain's median-time-past beyond <paramref name="locktime"/>.
    /// </summary>
    /// <param name="locktime">The unix second the refund path opens at.</param>
    /// <remarks>
    /// Median-time-past is the median of the last eleven block times, so setting the clock forward
    /// moves nothing on its own — the blocks have to be mined for the median to follow. Mined well
    /// past the locktime rather than exactly to it, since the median trails the tip by design.
    /// </remarks>
    private static async Task AdvanceChainPastAsync(long locktime)
    {
        var target = locktime + (long)TimeSpan.FromMinutes(30).TotalSeconds;
        string[] cli = ["bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123"];

        await DockerHelper.Exec("bitcoin", [.. cli, "setmocktime", target.ToString()]);

        var address = (await DockerHelper.Exec(
            "bitcoin", [.. cli, "-rpcwallet=default", "getnewaddress"])).Trim();

        await DockerHelper.Exec("bitcoin", [.. cli, "generatetoaddress", "15", address]);
    }

    /// <summary>Registers an admin, creates a store with a fresh Arkade wallet, and returns both.</summary>
    private async Task<(string StoreId, BTCPayServerClient Client)> SetUpStoreAsync()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStoreWithArkWalletAsync(GenerateRandomNsec());
        return (storeId, new BTCPayServerClient(ServerUri, CreatedUser, Password));
    }

    /// <summary>
    /// Creates a store invoice payable over Lightning and returns the BOLT11 the customer is shown.
    /// </summary>
    /// <param name="client">A Greenfield client for the store's owner.</param>
    /// <param name="storeId">The store to bill through.</param>
    /// <param name="amountSats">The order amount.</param>
    /// <returns>The BOLT11 minted for it.</returns>
    /// <remarks>
    /// Read back off the payment method rather than off the checkout page: it is the same string the
    /// page renders, and taking it from the API keeps the assertion about the invoice rather than
    /// about how a QR code happens to be laid out.
    /// </remarks>
    private async Task<string> CreateLightningInvoiceAsync(
        BTCPayServerClient client, string storeId, long amountSats)
    {
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = amountSats,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = ["BTC-LN"]
            }
        });

        return await ReadLightningDestinationAsync(client, invoice.Id);
    }

    /// <summary>Waits for an invoice's Lightning destination to appear, and returns it.</summary>
    /// <param name="client">A Greenfield client for the store's owner.</param>
    /// <param name="invoiceId">The invoice to read.</param>
    /// <returns>The BOLT11.</returns>
    /// <exception cref="TimeoutException">No destination ever appeared.</exception>
    private static async Task<string> ReadLightningDestinationAsync(
        BTCPayServerClient client, string invoiceId)
    {
        var invoice = new { Id = invoiceId };

        // Minting means a negotiation with the solver, so the destination is not there the instant
        // the invoice is created.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var methods = await client.GetInvoicePaymentMethods(invoice.Id);
            var lightning = methods.FirstOrDefault(m =>
                m.PaymentMethodId.Contains("LN", StringComparison.OrdinalIgnoreCase));

            if (lightning?.Destination is { Length: > 0 } bolt11) return bolt11;

            await Task.Delay(2_000);
        }

        throw new TimeoutException(
            $"invoice {invoice.Id} never got a Lightning destination — the solver did not quote. " +
            "Check the solver is reachable and funded.");
    }

    /// <summary>
    /// Spends the given coins to a BOLT11 and waits for the payee to report it settled.
    /// </summary>
    /// <param name="storeId">The paying store.</param>
    /// <param name="bolt11">The invoice to pay.</param>
    /// <param name="outpoints">The coins to fund the lockup from.</param>
    /// <param name="token">An antiforgery token for the POST.</param>
    /// <returns>Whether the payee saw it settle before the timeout.</returns>
    private async Task<bool> SpendToLightningAsync(
        string storeId, string bolt11, IEnumerable<string> outpoints, string token)
    {
        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"StoreId={Uri.EscapeDataString(storeId)}" +
                       $"&VtxoOutpointsRaw={Uri.EscapeDataString(string.Join(",", outpoints))}" +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(bolt11)}"
            });

        Assert.True(resp.Ok, $"build-intent (LN) returned {resp.Status}: {await resp.TextAsync()}");

        // Funding the lockup is all the plugin does; the solver then pays the invoice and takes the
        // lockup with the preimage. Only the payee can say whether that happened.
        var paymentHash = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest)
            .PaymentHash!.ToString();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var raw = await DockerHelper.Exec(
                "lnd", ["lncli", "--network=regtest", "lookupinvoice", paymentHash]);

            if (InvoiceState(raw) is "SETTLED") return true;

            await Task.Delay(3_000);
        }

        return false;
    }

    /// <summary>Reads this wallet's swaps straight out of the plugin's own storage.</summary>
    /// <param name="walletId">The wallet to read.</param>
    /// <param name="type">Narrow to one corridor, or all when omitted.</param>
    /// <returns>The matching swaps.</returns>
    /// <remarks>
    /// Read in-process rather than through a page or an endpoint, because there is no UI for these
    /// yet and a corridor's state is exactly what these tests are about. It also keeps the assertion
    /// on the record the money path actually consults.
    /// </remarks>
    private async Task<IReadOnlyCollection<ArkadeSwapIntent>> ReadIntentsAsync(
        string walletId, ArkadeSwapIntentType? type = null)
    {
        var storage = _fixture.ServerTester!.PayTester.ServiceProvider
            .GetRequiredService<IArkadeIntentStorage>();

        var all = await storage.GetArkadeSwapIntents(walletIds: [walletId]);
        return type is null ? all : all.Where(i => i.Type == type).ToList();
    }

    /// <summary>Waits for one of this wallet's swaps to reach a status.</summary>
    /// <param name="walletId">The wallet to watch.</param>
    /// <param name="type">Which corridor.</param>
    /// <param name="status">The status being waited on.</param>
    /// <param name="timeout">How long to wait; two minutes by default.</param>
    /// <returns>The swap that reached it.</returns>
    /// <exception cref="TimeoutException">It never got there.</exception>
    private async Task<ArkadeSwapIntent> PollForIntentStatusAsync(
        string walletId,
        ArkadeSwapIntentType type,
        ArkadeSwapIntentStatus status,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        ArkadeSwapIntentStatus? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = (await ReadIntentsAsync(walletId, type))
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefault();

            last = match?.Status;
            if (match is not null && match.Status == status) return match;

            await Task.Delay(2_000);
        }

        throw new TimeoutException(
            $"no {type} swap for wallet {walletId} reached {status} (last seen: {last?.ToString() ?? "none"}).");
    }

    /// <summary>Reads the lifecycle state out of an <c>lncli lookupinvoice</c> reply.</summary>
    /// <param name="raw">The command's stdout.</param>
    /// <returns>The state, or <c>null</c> when the reply carried none.</returns>
    /// <remarks>
    /// Parsed rather than substring-matched, and the difference is not stylistic: the reply of an
    /// UNPAID invoice contains <c>"settled": false</c>, so a case-insensitive search for "SETTLED"
    /// matches the field name and reports every invoice as paid. This assertion passed against a
    /// deployment with no solver configured at all before it was written this way.
    /// </remarks>
    private static string? InvoiceState(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("state", out var state) ? state.GetString() : null;
        }
        catch (JsonException)
        {
            // lncli prints diagnostics to stdout on some failures; not JSON, so not a state.
            return null;
        }
    }

    /// <summary>Skips the test unless a solver was named for this run.</summary>
    private static void RequireSolver()
    {
        var solver = Environment.GetEnvironmentVariable(SharedPluginTestFixture.SolverUrlVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(solver),
            $"no solver configured; set {SharedPluginTestFixture.SolverUrlVariable} " +
            "(see this class's remarks for the stack it needs)");
    }
}
