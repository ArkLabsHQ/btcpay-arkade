using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Enums;
using NArk.Swaps.Helpers;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class SingleKeyAddressProvider(
    IClientTransport transport,
    ArkWallet wallet,
    Network network,
    ArkAddress? sweepingAddress
) : IArkadeAddressProvider
{
    public OutputDescriptor Descriptor { get; } = OutputDescriptor.Parse(wallet.AccountDescriptor, network);
    

    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        return descriptor.Extract().XOnlyPubKey.ToBytes().SequenceEqual(Descriptor.Extract().XOnlyPubKey.ToBytes()); 
    }
    public Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Descriptor);
    }

    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        ArkContract? result = null;
        if (purpose == NextContractPurpose.SendToSelf && sweepingAddress is not null)
        {
            // Static sweeping address is reusable - always keep it Active
            result = new UnknownArkContract(sweepingAddress, info.SignerKey, info.Network.ChainName == ChainName.Mainnet);
            activityState =  ContractActivityState.Inactive;
        }
        var signingDescriptor = await GetNextSigningDescriptor(cancellationToken);
        //TODO: lets actually make use of the LastUsedIndex and derives bytes deterministically
        var bytes = RandomUtils.GetBytes(32);
        result??= new HashLockedArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signingDescriptor,
            bytes,
            HashLockTypeOption.Hash160 //FIXME: i forgot which type we used before in master
        );
        return (result, result.ToEntity(wallet.Id, null, activityState));
    }
}