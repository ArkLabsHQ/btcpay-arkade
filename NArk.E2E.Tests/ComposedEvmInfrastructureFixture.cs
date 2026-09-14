using System.Globalization;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace NArk.E2E.Tests;

public sealed class ComposedEvmInfrastructureFixture : IDisposable
{
    public const string EnableVariable = "ARKADE_COMPOSED_EVM_E2E";
    public const string RegtestRootVariable = "ARKADE_COMPOSED_EVM_REGTEST_ROOT";
    public const string SecretsFileVariable = "ARKADE_COMPOSED_EVM_SECRETS_FILE";

    private const string SwapAddress = "0x00000000000000000000000000000000deadbeef";
    private const string TokenAddress = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    private const string XOnlyGenerator = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private static readonly string[] RequiredSecrets =
    [
        "ARKD_WALLET_SIGNER_KEY",
        "ARKD_PASSWORD",
        "BITCOIN_RPC_USER",
        "BITCOIN_RPC_PASSWORD",
        "EMULATOR_SECRET_KEY",
        "EMULATOR_PUBKEY",
        "COVCLAIMD_SECRET_KEY",
        "INTENT_SOLVER_MNEMONIC",
        "INTENT_SOLVER_EVM_SEND_MNEMONIC",
        "INTENT_SOLVER_EVM_RECEIVE_MNEMONIC",
        "EVM_SEND_PRIVATE_KEY",
        "EVM_SEND_ADDRESS",
        "EVM_RECEIVE_PRIVATE_KEY",
        "EVM_CLIENT_PRIVATE_KEY",
        "EVM_CLIENT_ADDRESS"
    ];

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _childEnvironment;
    private string? _regtestRoot;
    private string? _tempDirectory;
    private bool _started;
    private bool _startAttempted;
    private bool _disposed;
    private bool _preserveDiagnostics;

    public bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);

    public string? ProjectName { get; private set; }
    public string? StartupLogPath { get; private set; }
    public string? RuntimeLogPath { get; private set; }
    public string? RuntimeRoutePath { get; private set; }
    public string? RuntimeSolverAdminPath { get; private set; }
    public string? RuntimeIngressSolverAdminPath { get; private set; }
    public string? RuntimeLightningPath { get; private set; }
    public Uri? ArkadeUri { get; private set; }
    public Uri? IntentSolverUri { get; private set; }
    public Uri? EvmSendSolverUri { get; private set; }
    public Uri? EvmSendSolverAdminUri { get; private set; }
    public Uri? EvmReceiveSolverUri { get; private set; }
    public Uri? EvmRpcUri { get; private set; }
    public string MerchantEvmAddress => _childEnvironment?["EVM_CLIENT_ADDRESS"]
        ?? throw new InvalidOperationException("The composed EVM stack has not started.");

    public async Task ConfigureMerchantSettlementAsync(HttpClient client, string storeId,
        IReadOnlyCollection<string> sourceRails, CancellationToken cancellationToken = default)
    {
        RequireStarted();
        var source = sourceRails.ToArray();
        if (source.Length == 0 || source.Except(["ARKADE", "BTC-LN", "BTC-CHAIN"]).Any())
            throw new ArgumentException("Specify one or more supported composed source rails.", nameof(sourceRails));

        var policy = new
        {
            enabledSourceRails = source,
            outgoingSolver = new { mode = "explicit", endpoint = EvmSendSolverUri!.AbsoluteUri },
            lightningIngressSolver = source.Contains("BTC-LN", StringComparer.Ordinal)
                ? new { mode = "explicit", endpoint = IntentSolverUri!.AbsoluteUri } : null,
            onchainIngressSolver = source.Contains("BTC-CHAIN", StringComparer.Ordinal)
                ? new { mode = "explicit", endpoint = IntentSolverUri!.AbsoluteUri } : null,
            swapContractAddress = SwapAddress,
            fastestSecondsPerBlock = 1,
            slowestSecondsPerBlock = 1,
            minConfirmations = 1,
            minAgeSeconds = 1,
            minimumClaimWindowSeconds = 30,
            arkadeRefundMarginSeconds = 30,
            requireEmulatorRefundPath = true
        };
        var update = new
        {
            assetId = $"eip155:31337/erc20:{TokenAddress}",
            destination = MerchantEvmAddress,
            enabled = true,
            routePolicy = policy,
            rpcEndpoint = new { action = "replace", uri = EvmRpcUri!.AbsoluteUri },
            expectedSenderAddress = MerchantEvmAddress,
            maxFeePerGasWei = "100000000000",
            maxPriorityFeePerGasWei = "1000000000",
            maxGasLimit = "500000",
            gasPayerPrivateKey = new { action = "replace", privateKey = _childEnvironment!["EVM_CLIENT_PRIVATE_KEY"] }
        };
        using var response = await client.PutAsync($"api/v1/stores/{storeId}/arkade/evm-settlement",
            new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        AssertPublicProjectionSafe(body);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The composed EVM merchant configuration was rejected with HTTP {(int)response.StatusCode}.");
    }

    public async Task SendArkadePaymentAsync(string destination, long amountSats,
        CancellationToken cancellationToken = default)
    {
        RequireStarted();
        if (string.IsNullOrWhiteSpace(destination) || amountSats <= 0)
            throw new ArgumentException("A nonempty destination and positive amount are required.");
        var result = await RunAsync("docker", ["exec", $"{ProjectName}-arkd", "ark", "send", "--to", destination,
            "--amount", amountSats.ToString(CultureInfo.InvariantCulture), "--password", _childEnvironment!["ARKD_PASSWORD"]],
            _regtestRoot!, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("The live Arkade customer payment failed.");
    }

    public async Task PayLightningInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        RequireStarted();
        if (string.IsNullOrWhiteSpace(bolt11)) throw new ArgumentException("A BOLT11 invoice is required.", nameof(bolt11));
        var result = await RunAsync("docker", ["exec", $"{ProjectName}-lnd-peer", "lncli", "--network=regtest", "payinvoice",
            "--force", bolt11], _regtestRoot!, cancellationToken);
        if (result.ExitCode == 0) return;
        await PreserveDiagnosticsAsync();
        await PreserveLightningFailureDiagnosticsAsync(bolt11, result);
        throw new InvalidOperationException($"The live Lightning customer payment failed. Runtime logs: {RuntimeLogPath}; " +
                                            $"ingress solver admin: {RuntimeIngressSolverAdminPath}; LND diagnostics: {RuntimeLightningPath}");
    }

    public async Task WaitForLightningInvoiceAcceptedAsync(string bolt11, Task payment,
        CancellationToken cancellationToken = default)
    {
        RequireStarted();
        if (string.IsNullOrWhiteSpace(bolt11)) throw new ArgumentException("A BOLT11 invoice is required.", nameof(bolt11));
        ArgumentNullException.ThrowIfNull(payment);

        await PollAsync("recipient Lightning invoice acceptance", async ct =>
        {
            if (payment.IsCompleted)
            {
                await payment;
                throw new InvalidOperationException("The Lightning payment completed before the BTCPay restart checkpoint.");
            }

            var result = await RunAsync("docker",
                ["exec", $"{ProjectName}-lnd", "lncli", "--network=regtest", "listinvoices", "--pending_only"],
                _regtestRoot!, ct);
            if (result.ExitCode != 0) return false;
            try
            {
                using var invoices = JsonDocument.Parse(result.StandardOutput);
                return invoices.RootElement.GetProperty("invoices").EnumerateArray().Any(invoice =>
                    invoice.GetProperty("payment_request").GetString() == bolt11 &&
                    invoice.GetProperty("state").GetString() == "ACCEPTED" &&
                    long.TryParse(invoice.GetProperty("amt_paid_sat").GetString(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var paidSats) && paidSats > 0);
            }
            catch (JsonException)
            {
                return false;
            }
        }, cancellationToken);
    }

    public async Task SendOnchainPaymentAsync(string destination, long amountSats, CancellationToken cancellationToken = default)
    {
        RequireStarted();
        if (string.IsNullOrWhiteSpace(destination) || amountSats <= 0)
            throw new ArgumentException("A nonempty destination and positive amount are required.");
        var bitcoin = $"{ProjectName}-bitcoin";
        var send = await RunAsync("docker", ["exec", bitcoin, "bitcoin-cli", "-regtest", "-rpcuser=admin1",
            "-rpcpassword=123", "sendtoaddress", destination,
            (amountSats / 100_000_000m).ToString("0.00000000", CultureInfo.InvariantCulture)], _regtestRoot!, cancellationToken);
        if (send.ExitCode != 0) throw new InvalidOperationException("The live onchain customer payment failed.");
        var address = await RunAsync("docker", ["exec", bitcoin, "bitcoin-cli", "-regtest", "-rpcuser=admin1",
            "-rpcpassword=123", "getnewaddress"], _regtestRoot!, cancellationToken);
        if (address.ExitCode != 0 || string.IsNullOrWhiteSpace(address.StandardOutput))
            throw new InvalidOperationException("Could not obtain a regtest mining address.");
        var mine = await RunAsync("docker", ["exec", bitcoin, "bitcoin-cli", "-regtest", "-rpcuser=admin1",
            "-rpcpassword=123", "generatetoaddress", "1", address.StandardOutput.Trim()], _regtestRoot!, cancellationToken);
        if (mine.ExitCode != 0) throw new InvalidOperationException("Could not confirm the live onchain customer payment.");
    }

    public async Task<BigInteger> GetErc20BalanceAsync(string address, CancellationToken cancellationToken = default)
    {
        RequireStarted();
        var addressHex = NormalizeAddress(address);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var value = await RpcAsync(client, "eth_call", [new
        {
            to = TokenAddress,
            data = "0x70a08231" + addressHex.PadLeft(64, '0')
        }, "latest"], cancellationToken);
        return ParseQuantity(value.GetString());
    }

    public async Task AssertExactErc20ReceiptAsync(string transactionId, string destination, BigInteger amount,
        CancellationToken cancellationToken = default)
    {
        RequireStarted();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var receipt = await RpcAsync(client, "eth_getTransactionReceipt", [transactionId], cancellationToken);
        if (receipt.ValueKind == JsonValueKind.Null || receipt.GetProperty("status").GetString() != "0x1")
            throw new InvalidOperationException("The EVM claim transaction has no successful receipt.");
        var expectedDestination = "0x" + NormalizeAddress(destination).PadLeft(64, '0');
        var transferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
        var exact = receipt.GetProperty("logs").EnumerateArray().Any(log =>
            string.Equals(log.GetProperty("address").GetString(), TokenAddress, StringComparison.OrdinalIgnoreCase) &&
            log.GetProperty("topics").GetArrayLength() >= 3 &&
            string.Equals(log.GetProperty("topics")[0].GetString(), transferTopic, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(log.GetProperty("topics")[2].GetString(), expectedDestination, StringComparison.OrdinalIgnoreCase) &&
            ParseQuantity(log.GetProperty("data").GetString()) == amount);
        if (!exact) throw new InvalidOperationException("The EVM receipt does not prove the exact configured ERC20 delivery.");
    }

    public void AssertPublicProjectionSafe(string body)
    {
        RequireStarted();
        foreach (var secret in PrivateEnvironmentValues())
            if (body.Contains(secret.Value, StringComparison.Ordinal))
                throw new InvalidOperationException($"A composed EVM public projection exposed {secret.Key}.");
        using var json = JsonDocument.Parse(body);
        AssertNoPrivateFields(json.RootElement);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        if (!IsEnabled) throw new InvalidOperationException($"Set {EnableVariable}=1 to start the composed EVM stack.");

        _regtestRoot = RequireDirectory(RegtestRootVariable);
        RequireFile(Path.Combine(_regtestRoot, "regtest.mjs"));
        RequireFile(Path.Combine(_regtestRoot, "docker", "compose.evm.yml"));
        var profile = ReadDotEnv(RequireFile(Path.Combine(_regtestRoot, ".env.evm-e2e")));
        var secrets = ReadRegtestDefaults(_regtestRoot);
        var secretsPath = Environment.GetEnvironmentVariable(SecretsFileVariable);
        if (!string.IsNullOrWhiteSpace(secretsPath))
            foreach (var setting in ReadDotEnv(RequireFile(secretsPath, SecretsFileVariable))) secrets[setting.Key] = setting.Value;
        var missing = RequiredSecrets.Where(key => !secrets.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"{SecretsFileVariable} is missing required names: {string.Join(", ", missing)}.");
        ValidateSecrets(secrets);

        ProjectName = CreateProjectName();
        var publicSettings = CreatePublicSettings(ProjectName);
        _childEnvironment = new Dictionary<string, string>(profile, StringComparer.Ordinal);
        foreach (var secret in secrets) _childEnvironment[secret.Key] = secret.Value;
        foreach (var setting in publicSettings) _childEnvironment[setting.Key] = setting.Value;

        _tempDirectory = Path.Combine(Path.GetTempPath(), $"nark-composed-evm-{RandomNumberGenerator.GetHexString(12).ToLowerInvariant()}");
        Directory.CreateDirectory(_tempDirectory);
        ProtectDirectory(_tempDirectory);
        var generatedEnvironment = Path.Combine(_tempDirectory, "regtest.env");
        await WriteEnvironmentAsync(generatedEnvironment, _childEnvironment, cancellationToken);
        StartupLogPath = Path.Combine(_tempDirectory, "startup.log");

        await AssertProjectAssetsAsync(expectNone: true, cancellationToken);
        _startAttempted = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        BufferedCommandResult result;
        try
        {
            result = await RunAsync("node", ["regtest.mjs", "start", "--env", generatedEnvironment,
                "--profile", "covclaimd,intent-solver,evm-e2e"], _regtestRoot, timeout.Token);
        }
        finally
        {
            RemoveGeneratedEnvironmentForDiagnostics(_tempDirectory);
        }
        var diagnostics = Redact(result.StandardOutput + Environment.NewLine + result.StandardError, secrets.Values);
        await File.WriteAllTextAsync(StartupLogPath, diagnostics, cancellationToken);
        ProtectFile(StartupLogPath);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"The composed EVM stack failed to start. Restricted diagnostics: {StartupLogPath}");

        _started = true;
        ConfigureEndpoints(publicSettings);
        ConfigureBtCPayEnvironment(publicSettings);
        await AssertProjectAssetsAsync(expectNone: false, cancellationToken, requireContainers: true);
        await AssertExternalReadinessAsync(cancellationToken);
    }

    public async Task AssertBtCPayReadinessAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        await PollAsync("BTCPay API", async ct =>
        {
            using var response = await client.GetAsync("api/v1/health", ct);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return body.RootElement.TryGetProperty("synchronized", out var synchronized) && synchronized.GetBoolean();
        }, cancellationToken);
    }

    public async Task PreserveDiagnosticsAsync(string? routeJson = null)
    {
        if (!_startAttempted || _regtestRoot is null || ProjectName is null || _tempDirectory is null) return;
        RemoveGeneratedEnvironmentForDiagnostics(_tempDirectory);
        var docker = Path.Combine(_regtestRoot, "docker");
        var result = await RunAsync("docker",
            ["compose", "-p", ProjectName, "-f", Path.Combine(docker, "compose.base.yml"),
                "-f", Path.Combine(docker, "compose.ark.yml"), "-f", Path.Combine(docker, "compose.evm.yml"),
                "--profile", "base", "--profile", "ark", "--profile", "lightning", "--profile", "emulator",
                "--profile", "covclaimd", "--profile", "intent-solver", "--profile", "nostr", "--profile", "evm-e2e",
                "logs", "--no-color", "--tail", "1000"], _regtestRoot, CancellationToken.None);
        RuntimeLogPath = Path.Combine(_tempDirectory, "runtime.log");
        var secrets = _childEnvironment is null ? [] : PrivateEnvironmentValues().Select(pair => pair.Value).ToArray();
        await File.WriteAllTextAsync(RuntimeLogPath,
            Redact(result.StandardOutput + Environment.NewLine + result.StandardError, secrets), CancellationToken.None);
        ProtectFile(RuntimeLogPath);
        await PreserveEvmSendSolverAdminAsync(secrets);
        await PreserveIngressSolverAdminAsync(secrets);
        if (!string.IsNullOrWhiteSpace(routeJson))
        {
            AssertPublicProjectionSafe(routeJson);
            RuntimeRoutePath = Path.Combine(_tempDirectory, "route.json");
            await File.WriteAllTextAsync(RuntimeRoutePath, routeJson, CancellationToken.None);
            ProtectFile(RuntimeRoutePath);
        }
        _preserveDiagnostics = true;
    }

    private async Task AssertExternalReadinessAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        await RequireOkAsync(client, new Uri(ArkadeUri!, "v1/info"), "Arkade", cancellationToken);
        await RequireOkAsync(client, new Uri(IntentSolverUri!, "healthz"), "intent solver", cancellationToken);
        await RequireOkAsync(client, new Uri(EvmSendSolverUri!, "healthz"), "EVM send solver", cancellationToken);
        await RequireOkAsync(client, new Uri(EvmReceiveSolverUri!, "healthz"), "EVM receive solver", cancellationToken);

        var covclaimd = new Uri(Environment.GetEnvironmentVariable("ARKADE_E2E_COVCLAIMD_URL")!);
        await RequireOkAsync(client, new Uri(covclaimd, "v1/preimage/covclaimd-pubkey"), "covclaimd", cancellationToken);
        await AssertEvmChainAndContractsAsync(client, cancellationToken);
    }

    private async Task AssertEvmChainAndContractsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var chainId = await RpcAsync(client, "eth_chainId", [], cancellationToken);
        if (chainId.GetString() != "0x7a69") throw new InvalidOperationException("Anvil returned an unexpected chain id.");

        var swapCode = await RpcAsync(client, "eth_getCode", [SwapAddress, "latest"], cancellationToken);
        var tokenCode = await RpcAsync(client, "eth_getCode", [TokenAddress, "latest"], cancellationToken);
        var expectedSwap = NormalizeHexPrefix((await File.ReadAllTextAsync(Path.Combine(_regtestRoot!, "docker", "evm", "erc20swap.runtime.hex"), cancellationToken)).Trim());
        var expectedToken = NormalizeHexPrefix((await File.ReadAllTextAsync(Path.Combine(_regtestRoot!, "docker", "evm", "weth9.runtime.hex"), cancellationToken)).Trim());
        if (!string.Equals(swapCode.GetString(), expectedSwap, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tokenCode.GetString(), expectedToken, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Anvil contract runtime does not match the pinned fixtures.");
    }

    private async Task AssertEvmRfqAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var arkAddress = await ReadArkAddressAsync(cancellationToken);
        var rfqId = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var paymentHash = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var request = new
        {
            v = 1,
            type = "rfq_request",
            rfq_id = rfqId,
            pair = $"arkade:BTC->ethereum:{TokenAddress}",
            amount_side = "from",
            amount = 100_000,
            profile = new
            {
                payment_hash = paymentHash,
                evm_claim_address = _childEnvironment!["EVM_CLIENT_ADDRESS"],
                refund_address = arkAddress,
                client_refund_pubkey = XOnlyGenerator
            }
        };
        using var response = await PostJsonAsync(client, new Uri(EvmSendSolverUri!, "v1/swap"), request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (var secret in PrivateEnvironmentValues())
                detail = detail.Replace(secret.Value, "[redacted]", StringComparison.Ordinal);
            throw new InvalidOperationException(
                $"The EVM send solver refused its readiness RFQ: HTTP {(int)response.StatusCode} {detail[..Math.Min(detail.Length, 2_000)]}");
        }
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = body.RootElement;
        if (root.GetProperty("type").GetString() != "rfq_quote" ||
            root.GetProperty("rfq_id").GetString() != rfqId ||
            root.GetProperty("pair").GetString() != request.pair)
            throw new InvalidOperationException("The EVM send solver returned a malformed readiness quote.");
    }

    private async Task<string> ReadArkAddressAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("docker", ["exec", $"{ProjectName}-arkd", "ark", "receive"], _regtestRoot!, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("Arkade did not provide a readiness address.");
        try
        {
            using var body = JsonDocument.Parse(result.StandardOutput);
            foreach (var name in new[] { "offchain_address", "address", "boarding_address" })
                if (body.RootElement.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!;
        }
        catch (JsonException)
        {
        }
        var marker = result.StandardOutput.IndexOf("tark1", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var end = marker;
            while (end < result.StandardOutput.Length && char.IsAsciiLetterOrDigit(result.StandardOutput[end])) end++;
            return result.StandardOutput[marker..end];
        }
        throw new InvalidOperationException("Arkade readiness address was missing.");
    }

    private async Task<JsonElement> RpcAsync(HttpClient client, string method, object[] parameters, CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync(client, EvmRpcUri!,
            new { jsonrpc = "2.0", id = 1, method, @params = parameters }, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"EVM RPC {method} failed.");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (body.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException($"EVM RPC {method} returned an error.");
        return body.RootElement.GetProperty("result").Clone();
    }

    private static async Task RequireOkAsync(HttpClient client, Uri endpoint, string name, CancellationToken cancellationToken)
    {
        await PollAsync(name, async ct =>
        {
            using var response = await client.GetAsync(endpoint, ct);
            return response.IsSuccessStatusCode;
        }, cancellationToken);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, Uri endpoint, object value,
        CancellationToken cancellationToken) => client.PostAsync(endpoint,
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"), cancellationToken);

    private static async Task PollAsync(string name, Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try { if (await probe(cancellationToken)) return; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { last = exception; }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"{name} did not become ready.", last);
    }

    private Dictionary<string, string> CreatePublicSettings(string projectName)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REGTEST_PROJECT"] = projectName,
            ["REGTEST_CONTAINER_PREFIX"] = projectName + "-",
            ["REGTEST_PROFILES"] = "covclaimd,intent-solver,evm-e2e",
            ["AUTOMINE_INTERVAL"] = "0"
        };
        var ports = new HashSet<int>();
        foreach (var name in PortNames)
        {
            int port;
            do { port = ReservePort(); } while (!ports.Add(port));
            settings[name] = port.ToString(CultureInfo.InvariantCulture);
        }
        return settings;
    }

    private void ConfigureEndpoints(IReadOnlyDictionary<string, string> settings)
    {
        ArkadeUri = Loopback(settings["ARKD_PORT"]);
        IntentSolverUri = Loopback(settings["INTENT_SOLVER_PORT"]);
        EvmSendSolverUri = Loopback(settings["EVM_SEND_SOLVER_PORT"]);
        EvmSendSolverAdminUri = Loopback(settings["EVM_SEND_SOLVER_ADMIN_PORT"]);
        EvmReceiveSolverUri = Loopback(settings["EVM_RECEIVE_SOLVER_PORT"]);
        EvmRpcUri = Loopback(settings["EVM_RPC_PORT"]);
    }

    private void ConfigureBtCPayEnvironment(IReadOnlyDictionary<string, string> settings)
    {
        SetEnvironment("TESTS_BTCRPCCONNECTION",
            $"server=http://127.0.0.1:{settings["BITCOIN_RPC_PORT"]};{_childEnvironment!["BITCOIN_RPC_USER"]}:{_childEnvironment["BITCOIN_RPC_PASSWORD"]}");
        SetEnvironment("TESTS_BTCNBXPLORERURL", $"http://127.0.0.1:{settings["NBXPLORER_PORT"]}/");
        SetEnvironment("TESTS_POSTGRES", $"Host=127.0.0.1;Port={settings["POSTGRES_PORT"]};Database=btcpay_composed_evm;Username=postgres");
        SetEnvironment("TESTS_EXPLORER_POSTGRES", $"Host=127.0.0.1;Port={settings["POSTGRES_PORT"]};Database=nbxplorer;Username=postgres");
        SetEnvironment("TESTS_HOSTNAME", "127.0.0.1");
        SetEnvironment(SharedPluginTestFixture.SolverUrlVariable, IntentSolverUri!.ToString());
        SetEnvironment("ARKADE_E2E_COVCLAIMD_URL", Loopback(settings["COVCLAIMD_HTTP_PORT"]).ToString());
        SetEnvironment("ARKADE_E2E_EMULATOR_URL", Loopback(settings["EMULATOR_PORT"]).ToString());
        SetEnvironment("ARKADE_E2E_EVM_SEND_SOLVER_URL", EvmSendSolverUri!.ToString());
        SetEnvironment("ARKADE_E2E_EVM_RECEIVE_SOLVER_URL", EvmReceiveSolverUri!.ToString());
        SetEnvironment("ARKADE_E2E_EVM_RPC_URL", EvmRpcUri!.ToString());
        SetEnvironment("ARKADE_E2E_EVM_SWAP_ADDRESS", SwapAddress);
        SetEnvironment("ARKADE_E2E_EVM_TOKEN_ADDRESS", TokenAddress);
        SetEnvironment("ARKADE_E2E_ARK_URL", ArkadeUri!.ToString());
        SetEnvironment("ARKADE_E2E_ARKADE_WALLET_URL", Loopback(settings["WALLET_PORT"]).ToString());
        SetEnvironment("ARKADE_E2E_EXPLORER_URL", Loopback(settings["EXPLORER_PORT"]).ToString());
        SetEnvironment("ARKADE_E2E_ESPLORA_URL", new Uri(Loopback(settings["MEMPOOL_WEB_PORT"]), "api").ToString());
        SetEnvironment("ARKADE_E2E_ELECTRUM_WS_URL", $"ws://127.0.0.1:{settings["FULCRUM_WS_PORT"]}");
        SetEnvironment("ARKADE_E2E_ELECTRUM_TCP_URL", $"tcp://127.0.0.1:{settings["FULCRUM_TCP_PORT"]}");
        SetEnvironment("ARKADE_CHEAT_ARK_CONTAINER", $"{ProjectName}-arkd");
        SetEnvironment("BTCPAY_ARKINTENTPOLLSECONDS", "5");
    }

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private async Task AssertProjectAssetsAsync(bool expectNone, CancellationToken cancellationToken,
        bool requireContainers = false)
    {
        foreach (var kind in new[] { "container", "volume", "network" })
        {
            var noun = kind == "container" ? "ps" : kind;
            var args = kind == "container"
                ? new[] { noun, "-a", "--filter", $"label=com.docker.compose.project={ProjectName}", "--format", "{{.Label \"com.docker.compose.project\"}}" }
                : new[] { noun, "ls", "--filter", $"label=com.docker.compose.project={ProjectName}", "--format", "{{.Label \"com.docker.compose.project\"}}" };
            var result = await RunAsync("docker", args, _regtestRoot!, cancellationToken, includeChildEnvironment: false);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Could not audit Docker {kind} labels.");
            var labels = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (labels.Any(label => !string.Equals(label, ProjectName, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Docker {kind} label audit escaped project {ProjectName}.");
            if (expectNone && labels.Length != 0)
                throw new InvalidOperationException($"Docker project {ProjectName} already owns {kind} resources.");
            if (requireContainers && kind == "container" && labels.Length == 0)
                throw new InvalidOperationException($"Docker project {ProjectName} has no containers after startup.");
        }
    }

    private async Task CleanupAsync()
    {
        if (!_startAttempted || _regtestRoot is null || ProjectName is null) return;
        var hasAssets = await HasProjectAssetsAsync(CancellationToken.None);
        if (!hasAssets) return;
        await AssertProjectAssetsAsync(expectNone: false, CancellationToken.None);
        var docker = Path.Combine(_regtestRoot, "docker");
        var result = await RunAsync("docker",
            ["compose", "-p", ProjectName, "-f", Path.Combine(docker, "compose.base.yml"),
                "-f", Path.Combine(docker, "compose.ark.yml"), "-f", Path.Combine(docker, "compose.evm.yml"),
                "--profile", "base", "--profile", "ark", "--profile", "lightning", "--profile", "emulator",
                "--profile", "covclaimd", "--profile", "intent-solver", "--profile", "nostr", "--profile", "evm-e2e",
                "down", "--remove-orphans", "--volumes"], _regtestRoot, CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Cleanup of Docker project {ProjectName} failed.");
        _started = false;
        _startAttempted = false;
        await AssertProjectAssetsAsync(expectNone: true, CancellationToken.None);
    }

    private async Task<bool> HasProjectAssetsAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { "container", "volume", "network" })
        {
            var args = kind == "container"
                ? new[] { "ps", "-a", "--filter", $"label=com.docker.compose.project={ProjectName}", "--format", "{{.ID}}" }
                : new[] { kind, "ls", "--filter", $"label=com.docker.compose.project={ProjectName}", "--format", kind == "volume" ? "{{.Name}}" : "{{.ID}}" };
            var result = await RunAsync("docker", args, _regtestRoot!, cancellationToken, includeChildEnvironment: false);
            if (result.ExitCode != 0) throw new InvalidOperationException("Could not inspect the composed EVM Docker project.");
            if (!string.IsNullOrWhiteSpace(result.StandardOutput)) return true;
        }
        return false;
    }

    private async Task<BufferedCommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, CancellationToken cancellationToken, bool includeChildEnvironment = true)
    {
        var command = Cli.Wrap(executable).WithArguments(arguments).WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);
        if (includeChildEnvironment && _childEnvironment is not null)
            command = command.WithEnvironmentVariables(
                _childEnvironment.ToDictionary(setting => setting.Key, setting => (string?)setting.Value));
        return await command.ExecuteBufferedAsync(cancellationToken);
    }

    private static Dictionary<string, string> ReadDotEnv(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in File.ReadLines(path))
        {
            var line = source.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new InvalidOperationException($"Invalid line in {SecretsFileVariable}.");
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
                value = value[1..^1];
            if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                throw new InvalidOperationException($"Invalid name in {SecretsFileVariable}.");
            result[name] = value;
        }
        return result;
    }

    private static Dictionary<string, string> ReadRegtestDefaults(string regtestRoot)
    {
        var values = ReadDotEnv(Path.Combine(regtestRoot, ".env.defaults"));
        foreach (var name in RequiredSecrets.Where(name => !values.ContainsKey(name)))
        {
            var fallback = name switch
            {
                "BITCOIN_RPC_USER" => "admin1",
                "BITCOIN_RPC_PASSWORD" => "123",
                _ => ReadComposeDefault(Path.Combine(regtestRoot, "docker", "compose.ark.yml"), name)
                     ?? ReadComposeDefault(Path.Combine(regtestRoot, "docker", "compose.evm.yml"), name)
            };
            if (fallback is not null) values[name] = fallback;
        }
        return values;
    }

    private static string? ReadComposeDefault(string path, string name)
    {
        var text = File.ReadAllText(path);
        var match = Regex.Match(text, @"\$\{" + Regex.Escape(name) + @":-([^}]+)\}", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : null;
    }

    private static void ValidateSecrets(IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var name in new[]
                 {
                     "ARKD_WALLET_SIGNER_KEY", "EMULATOR_SECRET_KEY", "COVCLAIMD_SECRET_KEY",
                     "EVM_SEND_PRIVATE_KEY", "EVM_RECEIVE_PRIVATE_KEY", "EVM_CLIENT_PRIVATE_KEY"
                 })
            RequireHex(secrets[name], 32, name);
        RequireHex(secrets["EMULATOR_PUBKEY"], 33, "EMULATOR_PUBKEY");
        foreach (var name in new[] { "EVM_SEND_ADDRESS", "EVM_CLIENT_ADDRESS" })
        {
            var value = secrets[name];
            if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length != 42 ||
                value[2..].Any(character => !Uri.IsHexDigit(character)) || value[2..].All(character => character == '0') ||
                !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidOperationException($"{name} must be a lowercase, nonzero 20-byte hex address.");
        }
    }

    private static void RequireHex(string value, int bytes, string name)
    {
        var hex = value.StartsWith("0x", StringComparison.Ordinal) ? value[2..] : value;
        if (hex.Length != bytes * 2 || hex.Any(character => !Uri.IsHexDigit(character)) || hex.All(character => character == '0'))
            throw new InvalidOperationException($"{name} must be a nonzero {bytes}-byte hex value.");
    }

    private static async Task WriteEnvironmentAsync(string path, IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        var contents = new StringBuilder();
        foreach (var setting in settings)
        {
            if (setting.Value.IndexOfAny(['\r', '\n']) >= 0) throw new InvalidOperationException("Environment values must be single-line.");
            contents.Append(setting.Key).Append('=').Append('"').Append(setting.Value.Replace("\\", "\\\\").Replace("\"", "\\\""))
                .Append('"').AppendLine();
        }
        await File.WriteAllTextAsync(path, contents.ToString(), new UTF8Encoding(false), cancellationToken);
        ProtectFile(path);
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                        ?? throw new InvalidOperationException("The current Windows identity has no owner SID.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                        ?? throw new InvalidOperationException("The current Windows identity has no owner SID.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Redact(string value, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).OrderByDescending(secret => secret.Length))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }

    internal static string RedactDiagnosticContent(string value, IEnumerable<string> secrets) => Redact(value, secrets);

    internal static void RemoveGeneratedEnvironmentForDiagnostics(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        File.Delete(Path.Combine(directory, "regtest.env"));
    }

    private static string CreateProjectName() =>
        $"btcpay-composed-evm-{Environment.ProcessId}-{RandomNumberGenerator.GetHexString(4).ToLowerInvariant()}";

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Uri Loopback(string port) => new($"http://127.0.0.1:{port}/");

    private async Task PreserveEvmSendSolverAdminAsync(IReadOnlyCollection<string> secrets)
    {
        if (_tempDirectory is null || EvmSendSolverAdminUri is null) return;
        RuntimeSolverAdminPath = Path.Combine(_tempDirectory, "evm-send-solver-admin.log");
        var output = new StringBuilder();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var listUri = new Uri(EvmSendSolverAdminUri, "api/swaps?limit=100");
            using var listResponse = await client.GetAsync(listUri, CancellationToken.None);
            var listBody = await listResponse.Content.ReadAsStringAsync(CancellationToken.None);
            output.AppendLine($"GET {listUri.PathAndQuery} HTTP {(int)listResponse.StatusCode}");
            output.AppendLine(listBody);
            if (listResponse.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(listBody);
                if (document.RootElement.TryGetProperty("swaps", out var swaps) && swaps.ValueKind == JsonValueKind.Array)
                    foreach (var swap in swaps.EnumerateArray())
                    {
                        if (!swap.TryGetProperty("corridor", out var corridor) || !swap.TryGetProperty("id", out var id) ||
                            string.IsNullOrWhiteSpace(corridor.GetString()) || string.IsNullOrWhiteSpace(id.GetString())) continue;
                        var detailUri = new Uri(EvmSendSolverAdminUri,
                            $"api/swaps/{Uri.EscapeDataString(corridor.GetString()!)}/{Uri.EscapeDataString(id.GetString()!)}");
                        using var detailResponse = await client.GetAsync(detailUri, CancellationToken.None);
                        output.AppendLine($"GET {detailUri.PathAndQuery} HTTP {(int)detailResponse.StatusCode}");
                        output.AppendLine(await detailResponse.Content.ReadAsStringAsync(CancellationToken.None));
                    }
            }
        }
        catch (Exception exception)
        {
            output.AppendLine($"admin capture exception: {exception.GetType().Name}: {exception.Message}");
        }
        await File.WriteAllTextAsync(RuntimeSolverAdminPath, Redact(output.ToString(), secrets), CancellationToken.None);
        ProtectFile(RuntimeSolverAdminPath);
    }

    private async Task PreserveIngressSolverAdminAsync(IReadOnlyCollection<string> secrets)
    {
        if (_tempDirectory is null || IntentSolverUri is null) return;
        RuntimeIngressSolverAdminPath = Path.Combine(_tempDirectory, "ingress-solver-admin.log");
        await PreserveSolverAdminAsync(IntentSolverUri, RuntimeIngressSolverAdminPath, secrets);
    }

    private async Task PreserveSolverAdminAsync(Uri endpoint, string path, IReadOnlyCollection<string> secrets)
    {
        var output = new StringBuilder();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var listUri = new Uri(endpoint, "api/swaps?limit=100");
            using var listResponse = await client.GetAsync(listUri, CancellationToken.None);
            var listBody = await listResponse.Content.ReadAsStringAsync(CancellationToken.None);
            output.AppendLine($"GET {listUri.PathAndQuery} HTTP {(int)listResponse.StatusCode}");
            output.AppendLine(listBody);
            if (listResponse.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(listBody);
                if (document.RootElement.TryGetProperty("swaps", out var swaps) && swaps.ValueKind == JsonValueKind.Array)
                    foreach (var swap in swaps.EnumerateArray())
                    {
                        if (!swap.TryGetProperty("corridor", out var corridor) || !swap.TryGetProperty("id", out var id) ||
                            string.IsNullOrWhiteSpace(corridor.GetString()) || string.IsNullOrWhiteSpace(id.GetString())) continue;
                        var detailUri = new Uri(endpoint,
                            $"api/swaps/{Uri.EscapeDataString(corridor.GetString()!)}/{Uri.EscapeDataString(id.GetString()!)}");
                        using var detailResponse = await client.GetAsync(detailUri, CancellationToken.None);
                        output.AppendLine($"GET {detailUri.PathAndQuery} HTTP {(int)detailResponse.StatusCode}");
                        output.AppendLine(await detailResponse.Content.ReadAsStringAsync(CancellationToken.None));
                    }
            }
        }
        catch (Exception exception)
        {
            output.AppendLine($"admin capture exception: {exception.GetType().Name}: {exception.Message}");
        }
        await File.WriteAllTextAsync(path, Redact(output.ToString(), secrets), CancellationToken.None);
        ProtectFile(path);
    }

    private async Task PreserveLightningFailureDiagnosticsAsync(string bolt11, BufferedCommandResult payment)
    {
        if (_tempDirectory is null || ProjectName is null || _regtestRoot is null) return;
        RuntimeLightningPath = Path.Combine(_tempDirectory, "lightning-payment.log");
        var output = new StringBuilder();
        output.AppendLine($"[lnd-peer] payinvoice exit {payment.ExitCode}");
        output.AppendLine(payment.StandardOutput);
        output.AppendLine(payment.StandardError);
        var decoded = await CaptureLncliAsync(output, "lnd", "decodepayreq", ["--network=regtest", "decodepayreq", bolt11]);
        foreach (var node in new[] { "lnd", "lnd-peer" })
        {
            await CaptureLncliAsync(output, node, "getinfo", ["--network=regtest", "getinfo"]);
            await CaptureLncliAsync(output, node, "channelbalance", ["--network=regtest", "channelbalance"]);
            await CaptureLncliAsync(output, node, "listchannels", ["--network=regtest", "listchannels"]);
        }
        if (decoded.ExitCode == 0)
            await TryAppendRouteDiagnosticsAsync(output, decoded.StandardOutput);
        var secrets = PrivateEnvironmentValues().Select(pair => pair.Value).ToArray();
        await File.WriteAllTextAsync(RuntimeLightningPath, Redact(output.ToString(), secrets), CancellationToken.None);
        ProtectFile(RuntimeLightningPath);
    }

    private async Task<BufferedCommandResult> CaptureLncliAsync(StringBuilder output, string node, string label,
        IReadOnlyList<string> arguments)
    {
        var result = await RunAsync("docker", ["exec", $"{ProjectName}-{node}", "lncli", .. arguments], _regtestRoot!,
            CancellationToken.None);
        output.AppendLine($"[{node}] {label} exit {result.ExitCode}");
        output.AppendLine(result.StandardOutput);
        output.AppendLine(result.StandardError);
        return result;
    }

    private async Task TryAppendRouteDiagnosticsAsync(StringBuilder output, string decoded)
    {
        try
        {
            using var invoice = JsonDocument.Parse(decoded);
            var destination = invoice.RootElement.GetProperty("destination").GetString();
            var amount = invoice.RootElement.GetProperty("num_satoshis").GetString();
            if (destination is null || destination.Length != 66 || destination.Any(c => !Uri.IsHexDigit(c)) ||
                !long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var sats) || sats <= 0) return;
            await CaptureLncliAsync(output, "lnd", "queryroutes", ["--network=regtest", "queryroutes", destination, sats.ToString(CultureInfo.InvariantCulture)]);
            await CaptureLncliAsync(output, "lnd-peer", "queryroutes", ["--network=regtest", "queryroutes", destination, sats.ToString(CultureInfo.InvariantCulture)]);
        }
        catch (JsonException)
        {
        }
    }

    private void RequireStarted()
    {
        if (!_started || _childEnvironment is null || _regtestRoot is null)
            throw new InvalidOperationException("The composed EVM stack has not started.");
    }

    private static string NormalizeAddress(string value)
    {
        if (value.Length != 42 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            value[2..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Specify a 20-byte EVM address.", nameof(value));
        return value[2..].ToLowerInvariant();
    }

    private static BigInteger ParseQuantity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("0x", StringComparison.Ordinal) ||
            value[2..].Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The EVM RPC returned a malformed quantity.");
        return BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static string NormalizeHexPrefix(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? value : "0x" + value;

    private IEnumerable<KeyValuePair<string, string>> PrivateEnvironmentValues() =>
        SensitiveEnvironmentValues(_childEnvironment!);

    internal static IEnumerable<KeyValuePair<string, string>> SensitiveEnvironmentValues(
        IEnumerable<KeyValuePair<string, string>> environment) => environment
        .Where(pair => pair is not { Key: "BITCOIN_RPC_PASSWORD", Value: "123" })
        .Where(pair => !string.IsNullOrEmpty(pair.Value) &&
                       (pair.Key.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("MNEMONIC", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("SIGNER_KEY", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("SEED", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)));

    private static void AssertNoPrivateFields(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains("preimage", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("protected", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A composed EVM public projection exposed a private field.");
                    AssertNoPrivateFields(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) AssertNoPrivateFields(item);
                break;
        }
    }

    private static string RequireDirectory(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value))
            throw new InvalidOperationException($"{variable} must name the derived arkade-regtest EVM worktree.");
        return Path.GetFullPath(value);
    }

    private static string RequireFile(string? path, string? variable = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(variable is null ? $"Required file is missing: {path}." : $"{variable} must name an existing file.");
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Exception? cleanupError = null;
        try { CleanupAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { cleanupError = exception; }
        finally
        {
            foreach (var original in _originalEnvironment)
                Environment.SetEnvironmentVariable(original.Key, original.Value);
            if (_tempDirectory is not null)
                RemoveGeneratedEnvironmentForDiagnostics(_tempDirectory);
            if (cleanupError is null && !_preserveDiagnostics && _tempDirectory is not null && Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
            _disposed = true;
        }
        if (cleanupError is not null)
            throw new InvalidOperationException($"Scoped cleanup failed; restricted state remains at {_tempDirectory}.", cleanupError);
    }

    private static readonly string[] PortNames =
    [
        "BITCOIN_RPC_PORT", "BITCOIN_P2P_PORT", "BITCOIN_ZMQ_BLOCK_PORT", "BITCOIN_ZMQ_TX_PORT",
        "NBXPLORER_PORT", "POSTGRES_PORT", "FULCRUM_TCP_PORT", "FULCRUM_WS_PORT", "MEMPOOL_WEB_PORT",
        "MEMPOOL_API_PORT", "LND_P2P_PORT", "LND_RPC_PORT", "ARKD_PORT", "ARKD_ADMIN_PORT", "ARKD_WALLET_PORT",
        "LND_PEER_P2P_PORT", "LND_PEER_RPC_PORT", "WALLET_PORT", "EXPLORER_PORT", "EMULATOR_PORT",
        "COVCLAIMD_HTTP_PORT", "INTENT_SOLVER_PORT", "STRFRY_PORT", "EVM_RPC_PORT", "EVM_PRICEFEED_PORT",
        "EVM_SEND_SOLVER_PORT", "EVM_RECEIVE_SOLVER_PORT", "EVM_SEND_SOLVER_ADMIN_PORT", "EVM_RECEIVE_SOLVER_ADMIN_PORT"
    ];
}
