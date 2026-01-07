using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Wallets;
using NArk.Transport;
using NBitcoin;
using BTCPayServer.Plugins.ArkPayServer.Wallet;
using PluginArkWallet = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkWallet;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Service for managing wallet signers. Loads signers at startup and automatically
/// registers/unregisters signers when wallets are created/deleted via storage events.
/// For wallet creation, use <see cref="WalletFactory"/>.
/// For data queries, use storage classes directly.
/// </summary>
public class WalletSignerService(
    EfCoreWalletStorage walletStorage,
    IClientTransport clientTransport,
    ILogger<WalletSignerService> logger) : IHostedService, IArkadeMultiWalletSigner
{
    private TaskCompletionSource _started = new();
    private ConcurrentDictionary<string, ISigningEntity> _walletSigners = new();
    private Network? _network;

    #region IHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Get network from server info
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        _network = serverInfo.Network;

        // Subscribe to storage events
        walletStorage.WalletSaved += OnWalletSaved;
        walletStorage.WalletDeleted += OnWalletDeleted;

        // Load all wallets that have a secret (nsec or mnemonic)
        var wallets = await walletStorage.GetSignableWalletsAsync(cancellationToken);
        foreach (var wallet in wallets)
        {
            RegisterSignerForWallet(wallet);
        }

        logger.LogInformation("Loaded {Count} wallet signers", _walletSigners.Count);
        _started.SetResult();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe from storage events
        walletStorage.WalletSaved -= OnWalletSaved;
        walletStorage.WalletDeleted -= OnWalletDeleted;

        _started = new TaskCompletionSource();
        _walletSigners = new();
        _network = null;
        return Task.CompletedTask;
    }

    #endregion

    #region IArkadeMultiWalletSigner

    public async Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default)
    {
        await _started.Task;
        return _walletSigners.ContainsKey(walletId);
    }

    public Task<ISigningEntity> CreateSigner(string walletId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_walletSigners[walletId]);
    }

    #endregion

    #region Event Handlers

    private void OnWalletSaved(object? sender, PluginArkWallet wallet)
    {
        RegisterSignerForWallet(wallet);
    }

    private void OnWalletDeleted(object? sender, string walletId)
    {
        if (_walletSigners.TryRemove(walletId, out _))
        {
            logger.LogDebug("Unregistered signer for deleted wallet {WalletId}", walletId);
        }
    }

    private void RegisterSignerForWallet(PluginArkWallet wallet)
    {
        if (_network == null)
        {
            logger.LogWarning("Cannot register signer for wallet {WalletId}: network not initialized", wallet.Id);
            return;
        }

        var signer = WalletFactory.CreateSigner(wallet, _network);
        if (signer != null)
        {
            _walletSigners[wallet.Id] = signer;
            logger.LogDebug("Registered signer for wallet {WalletId} (type: {WalletType})",
                wallet.Id, wallet.WalletType);
        }
    }

    #endregion
}
