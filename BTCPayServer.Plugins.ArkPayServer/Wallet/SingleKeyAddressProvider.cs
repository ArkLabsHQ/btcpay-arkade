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
    OutputDescriptor outputDescriptor,
    ArkAddress? sweepingAddress
) : IArkadeAddressProvider
{
    public OutputDescriptor Descriptor { get; } = outputDescriptor;
    

    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        return descriptor.Extract().XOnlyPubKey.ToBytes().SequenceEqual(Descriptor.Extract().XOnlyPubKey.ToBytes()); 
    }


    public Task<OutputDescriptor> GetNextSigningDescriptor(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Descriptor);
    }

    public async Task<ArkContract> GetNextContract(string identifier, NextContractPurpose purpose, CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        if (purpose == NextContractPurpose.SendToSelf && sweepingAddress is not null)
        {
            return new UnknownArkContract(sweepingAddress, info.SignerKey, info.Network.ChainName == ChainName.Mainnet);
        }
        var signingDescriptor = await GetNextSigningDescriptor(identifier, cancellationToken);
        //TODO: lets actually make use of the LastUsedIndex and derives bytes deterministically 
        var bytes = RandomUtils.GetBytes(32);
        return new HashLockedArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signingDescriptor,
            bytes,
            HashLockTypeOption.Hash160 //FIXME: i forgot which type we used before in master
        );
    }
}