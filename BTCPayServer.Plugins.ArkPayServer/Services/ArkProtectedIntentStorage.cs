using Microsoft.AspNetCore.DataProtection;
using NArk.Abstractions.Scripts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkProtectedIntentStorage : IArkadeIntentStorage, IDisposable
{
    private const string Prefix = "protected:v1:";
    private readonly IArkadeIntentStorage _inner;
    private readonly IDataProtectionProvider _protection;
    public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public ArkProtectedIntentStorage(IArkadeIntentStorage inner, IDataProtectionProvider protection)
    {
        _inner = inner;
        _protection = protection;
        _inner.SwapsChanged += OnChanged;
        _inner.ActiveScriptsChanged += OnScriptsChanged;
    }

    public async Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(string? id = null,
        ArkadeSwapIntentStatus? status = null, ArkadeSwapIntentStatus[]? statuses = null, string? swapPkScript = null,
        string[]? walletIds = null, int? skip = null, int? take = null, CancellationToken cancellationToken = default) =>
        (await _inner.GetArkadeSwapIntents(id, status, statuses, swapPkScript, walletIds, skip, take, cancellationToken))
        .Select(intent => Transform(intent, false)).ToArray();

    public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default) =>
        _inner.SaveArkadeSwapIntent(Transform(intent, true), cancellationToken);

    public Task<bool> UpdateStatus(string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
        CancellationToken cancellationToken = default) => _inner.UpdateStatus(swapPkScript, status, spentTxid, cancellationToken);

    public Task<HashSet<string>> GetActiveScripts(CancellationToken cancellationToken = default) =>
        ((IActiveScriptsProvider)_inner).GetActiveScripts(cancellationToken);

    private ArkadeSwapIntent Transform(ArkadeSwapIntent intent, bool protect)
    {
        var metadata = new Dictionary<string, string>(intent.Metadata);
        TransformSecret(metadata, intent, ArkadeSwapMetadataKeys.Preimage,
            "BTCPayServer.Plugins.ArkPayServer.IntentPreimage.v1", protect);
        TransformSecret(metadata, intent, ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction,
            "BTCPayServer.Plugins.ArkPayServer.IntentPreparedEvmTransaction.v1", protect);
        return new ArkadeSwapIntent
        {
            Id = intent.Id, WalletId = intent.WalletId, Type = intent.Type, OfferAmount = intent.OfferAmount,
            WantAmount = intent.WantAmount, Status = intent.Status, CreatedAt = intent.CreatedAt,
            SwapPkScript = intent.SwapPkScript, SwapAddress = intent.SwapAddress, Metadata = metadata,
            FromAssetId = intent.FromAssetId, ToAssetId = intent.ToAssetId, PaymentHash = intent.PaymentHash,
            RefundLocktime = intent.RefundLocktime, SpentTxid = intent.SpentTxid
        };
    }

    private void TransformSecret(IDictionary<string, string> metadata, ArkadeSwapIntent intent, string key,
        string purpose, bool protect)
    {
        if (!metadata.TryGetValue(key, out var secret) || secret.Length == 0) return;
        var protector = _protection.CreateProtector(purpose, intent.WalletId, intent.Id);
        if (protect)
        {
            if (secret.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"SDK storage requires unprotected '{key}' metadata at its write boundary.");
            metadata[key] = Prefix + protector.Protect(secret);
        }
        else if (secret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            metadata[key] = protector.Unprotect(secret[Prefix.Length..]);
        }
    }

    private void OnChanged(object? sender, ArkadeSwapIntent intent) => SwapsChanged?.Invoke(this, Transform(intent, false));
    private void OnScriptsChanged(object? sender, EventArgs args) => ActiveScriptsChanged?.Invoke(this, args);
    public void Dispose()
    {
        _inner.SwapsChanged -= OnChanged;
        _inner.ActiveScriptsChanged -= OnScriptsChanged;
    }
}
