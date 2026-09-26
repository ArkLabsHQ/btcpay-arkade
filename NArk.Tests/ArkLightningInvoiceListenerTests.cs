using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NBitcoin;
using Xunit;

namespace NArk.Tests;

public class ArkLightningInvoiceListenerTests
{
    private const string Bolt11 =
        "lnbcrt21u1p5tqtaypp56yzglgfgwsm5pd49996jqvtmpf8fqdk7cq2znnjw5c2j5t8ua38qdql2djkuepqw3hjqs2jfvsxzerywfjhxuccqz95xqztfsp586s5vpsdxt05rm7hr6ycwq5ffmnx2gngv820seugky6j6z2wxqwq9qxpqysgqepuxr82pvlp8lgj7nqu8yp2f5q32323jxddx9qgtjhfhsyzvftgkwx8qv4772fzz46pwyw5ex3u7lf7na8a8403ur3gyeu22gv29rpspefzz2y";

    private const string WalletId = "wallet-1";

    [Fact]
    public async Task AFulfilledReceive_IsHandedToTheListener()
    {
        var storage = new FakeIntentStorage();
        using var listener = NewListener(storage, CancellationToken.None);

        var waiting = listener.WaitInvoice(CancellationToken.None);
        storage.Raise(Receive(ArkadeSwapIntentStatus.Fulfilled));

        var invoice = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("swap-1", invoice.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
    }

    // BTCPay reads notification.Id straight into a dictionary that rejects a null key, so handing it a
    // blank invoice took down the notification path and left every receive to the one-minute poll.
    [Fact]
    public async Task Cancellation_ThrowsInsteadOfHandingBackABlankInvoice()
    {
        var storage = new FakeIntentStorage();
        using var listener = NewListener(storage, CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = listener.WaitInvoice(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AListenerShutDownByItsOwnToken_AlsoThrows()
    {
        var storage = new FakeIntentStorage();
        using var cts = new CancellationTokenSource();
        using var listener = NewListener(storage, cts.Token);

        var waiting = listener.WaitInvoice(CancellationToken.None);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AnUnfulfilledReceive_KeepsTheListenerWaiting()
    {
        var storage = new FakeIntentStorage();
        using var listener = NewListener(storage, CancellationToken.None);

        var waiting = listener.WaitInvoice(CancellationToken.None);
        storage.Raise(Receive(ArkadeSwapIntentStatus.Pending));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    private static ArkLightningInvoiceListener NewListener(IArkadeIntentStorage storage, CancellationToken token) =>
        new(WalletId, NullLogger<ArkLightningInvoiceListener>.Instance, storage, Network.RegTest, token);

    private static ArkadeSwapIntent Receive(ArkadeSwapIntentStatus status) => new ArkadeSwapIntent
    {
        Id = "swap-1",
        WalletId = WalletId,
        Type = ArkadeSwapIntentType.LightningToBtc,
        OfferAmount = Money.Satoshis(2_100),
        WantAmount = Money.Satoshis(2_100),
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = "5120" + new string('a', 64),
        SwapAddress = "tark1example",
    }.WithLightningMetadata(new LightningSwapMetadata(Bolt11, null));

    private sealed class FakeIntentStorage : IArkadeIntentStorage
    {
        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;

        public void Raise(ArkadeSwapIntent intent) => SwapsChanged?.Invoke(this, intent);

        public event EventHandler? ActiveScriptsChanged;

        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
            string? id = null, ArkadeSwapIntentStatus? status = null, ArkadeSwapIntentStatus[]? statuses = null,
            string? swapPkScript = null, string[]? walletIds = null, int? skip = null, int? take = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>([]);

        public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> UpdateStatus(
            string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
