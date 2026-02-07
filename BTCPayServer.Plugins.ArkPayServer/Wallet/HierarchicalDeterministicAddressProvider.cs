using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Swaps.Helpers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class HierarchicalDeterministicAddressProvider(
    IClientTransport transport,
    ISafetyService safetyService,
    EfCoreWalletStorage walletStorage,
    ArkWallet wallet,
    Network network,
    ArkAddress? sweepDestination)
    : IArkadeAddressProvider
{
    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        OutputDescriptor.Parse(wallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"), network);
        var index = descriptor.Extract().DerivationPath?.Indexes.Last().ToString();
        if (index is null)
        {
            return false;
        }
        var expected = GetDescriptorFromIndex(network, wallet.AccountDescriptor, Convert.ToInt32(index));
        return expected.Equals(descriptor);
    }

    public async Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
    {
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{wallet.Id}", cancellationToken);

        // Reload wallet from DB to get current LastUsedIndex (avoids stale in-memory copy)
        var freshWallet = await walletStorage.GetWalletByIdAsync(wallet.Id, cancellationToken)
            ?? throw new Exception("Wallet not found");

        var descriptor =
            GetDescriptorFromIndex(
                network,
                freshWallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"),
                freshWallet.LastUsedIndex++
            );

        await walletStorage.SaveWallet(freshWallet.Id, freshWallet, freshWallet.AccountDescriptor, cancellationToken);

        // Update local copy for consistency
        wallet.LastUsedIndex = freshWallet.LastUsedIndex;

        return descriptor;
    }

    private static OutputDescriptor GetDescriptorFromIndex(Network network, string descriptor, int index)
    {
        //TODO: the checksum may need to be recomputed?
        return OutputDescriptor.Parse(descriptor.Replace("/*", $"/{index}"), network);
    }

    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        ArkContract[]? inputContracts = null,
        CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        ArkContract? result = null;

        if (purpose == NextContractPurpose.SendToSelf && sweepDestination is not null)
        {
            // Static sweeping address is reusable - always keep it Inactive
            result = new UnknownArkContract(sweepDestination, info.SignerKey, info.Network.ChainName == ChainName.Mainnet);
            activityState = ContractActivityState.Inactive;
        }
        else if (purpose == NextContractPurpose.SendToSelf)
        {
            // For SendToSelf (change), try to recycle a descriptor from inputs to avoid index bloat
            var recycledDescriptor = inputContracts is not null
                ? await TryGetRecyclableDescriptor(inputContracts, cancellationToken)
                : null;

            if (recycledDescriptor is not null)
            {
                // Recycling from inputs - mark as Inactive since we control this transaction
                result = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, recycledDescriptor);
                activityState = ContractActivityState.Inactive;
            }
            else
            {
                // No recycling possible, will use new descriptor below
                // Mark as AwaitingFundsBeforeDeactivate so it's tracked until funded
                activityState = ContractActivityState.AwaitingFundsBeforeDeactivate;
            }
        }

        result ??= new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            await GetNextSigningDescriptor(cancellationToken)
        );

        return (result, result.ToEntity(wallet.Id, info.SignerKey, null, activityState));
    }

    /// <summary>
    /// Tries to find a reusable descriptor from the input contracts.
    /// This avoids HD index bloat when creating change outputs.
    /// </summary>
    private async Task<OutputDescriptor?> TryGetRecyclableDescriptor(ArkContract[] inputs, CancellationToken cancellationToken)
    {
        // Check ArkPaymentContracts first (most common)
        foreach (var payment in inputs.OfType<ArkPaymentContract>())
        {
            if (await IsOurs(payment.User, cancellationToken))
            {
                return payment.User;
            }
        }

        // Check VHTLCContracts (from swaps)
        foreach (var htlc in inputs.OfType<VHTLCContract>())
        {
            if (await IsOurs(htlc.Receiver, cancellationToken))
            {
                return htlc.Receiver;
            }
            if (await IsOurs(htlc.Sender, cancellationToken))
            {
                return htlc.Sender;
            }
        }

        return null;
    }
}
