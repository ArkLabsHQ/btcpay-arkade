using System.Globalization;
using System.Numerics;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.SolverRegistry;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionEvmContextFactory(IArkCompositionContextSource source,
    ArkEvmRpcEndpointProtector endpoints, ArkEvmGasPayerProtector keys, IHttpClientFactory http)
{
    public Task<ArkCompositionEvmContext> OpenAsync(string storeId, string walletId, string assetId,
        string destination, ArkEvmRoutePolicy policy, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(storeId, walletId, assetId, destination, policy, cancellationToken);

    /// <summary>
    /// Opens execution against the live store policy. Proof bounds come from current
    /// configuration; the asset, destination, wallet and contract match below still
    /// bind execution to this route.
    /// </summary>
    public Task<ArkCompositionEvmContext> OpenForExecutionAsync(string storeId, string walletId, string assetId,
        string destination, string rail, string swapContractAddress,
        CancellationToken cancellationToken = default) =>
        OpenCoreAsync(storeId, walletId, assetId, destination,
            new ArkEvmRoutePolicy
            {
                EnabledSourceRails = [rail], SwapContractAddress = swapContractAddress
            }, cancellationToken);

    private async Task<ArkCompositionEvmContext> OpenCoreAsync(string storeId, string walletId, string assetId,
        string destination, ArkEvmRoutePolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = await source.FindStoreAsync(storeId)
            ?? throw new InvalidOperationException("The composition store is unavailable.");
        var configuration = ArkCompositionPromptService.Configuration(store);
        var settings = configuration?.EvmSettlement?.Validate();
        if (settings is null || configuration?.WalletId != walletId || settings is not { Enabled: true, RoutePolicy: not null }
            || settings.AssetId != assetId || settings.Destination != destination
            || !settings.RoutePolicy.EnabledSourceRails.Contains(policy.EnabledSourceRails.Single())
            || settings.RoutePolicy.SwapContractAddress != policy.SwapContractAddress)
            throw new InvalidOperationException("The route differs from the store's settlement configuration.");
        var endpoint = endpoints.TryUnprotect(storeId, settings.ProtectedRpcUri)
            ?? throw new InvalidOperationException("The store's EVM RPC configuration is unavailable.");
        if (!keys.IsAvailable(storeId, settings.ProtectedGasPayerPrivateKey, settings.ExpectedSenderAddress)
            || settings.MaxFeePerGasWei is null || settings.MaxPriorityFeePerGasWei is null || settings.MaxGasLimit is null)
            throw new InvalidOperationException("The store's EVM gas-payer configuration is unavailable.");
        var client = http.CreateClient("ArkCompositionEvm");
        try
        {
            var rpc = new EvmJsonRpcClient(client, endpoint);
            var sender = keys.UseKey(storeId, settings.ProtectedGasPayerPrivateKey!, settings.ExpectedSenderAddress,
                key => new EvmLocalTransactionSender(rpc, key.Span, new EvmTransactionSenderOptions
                {
                    ExpectedSenderAddress = settings.ExpectedSenderAddress!,
                    MaxFeePerGasWei = BigInteger.Parse(settings.MaxFeePerGasWei, CultureInfo.InvariantCulture),
                    MaxPriorityFeePerGasWei = BigInteger.Parse(settings.MaxPriorityFeePerGasWei, CultureInfo.InvariantCulture),
                    MaxGasLimit = BigInteger.Parse(settings.MaxGasLimit, CultureInfo.InvariantCulture)
                }));
            return new ArkCompositionEvmContext(client, rpc, sender, Policy(assetId, policy));
        }
        catch { client.Dispose(); throw; }
    }

    public static EvmSendPolicy Policy(string assetId, ArkEvmRoutePolicy policy)
    {
        var asset = AssetIdentifier.Parse(assetId);
        if (asset.Namespace != "eip155" || !asset.Asset.StartsWith("erc20:", StringComparison.Ordinal))
            throw new ArgumentException("An exact ERC20 asset is required.");
        policy = policy.Validate();
        return new EvmSendPolicy
        {
            ChainId = BigInteger.Parse(asset.ChainReference, CultureInfo.InvariantCulture),
            TokenAddress = asset.Asset["erc20:".Length..], SwapContractAddress = policy.SwapContractAddress,
            FastestSecondsPerBlock = policy.FastestSecondsPerBlock, SlowestSecondsPerBlock = policy.SlowestSecondsPerBlock,
            MinConfirmations = policy.MinConfirmations, MinAgeSeconds = policy.MinAgeSeconds,
            MinimumClaimWindowSeconds = policy.MinimumClaimWindowSeconds,
            ArkadeRefundMarginSeconds = policy.ArkadeRefundMarginSeconds,
            RequireEmulatorRefundPath = policy.RequireEmulatorRefundPath
        };
    }
}

public sealed class ArkCompositionEvmContext(HttpClient http, EvmJsonRpcClient rpc,
    EvmLocalTransactionSender sender, EvmSendPolicy policy) : IDisposable
{
    public EvmJsonRpcClient Rpc { get; } = rpc;
    public EvmLocalTransactionSender Sender { get; } = sender;
    public EvmSendPolicy Policy { get; } = policy;
    public void Dispose() => http.Dispose();
    public override string ToString() => nameof(ArkCompositionEvmContext);
}
