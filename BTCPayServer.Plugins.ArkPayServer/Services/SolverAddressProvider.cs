using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class SolverAddressProvider(ArkadeSolverService solverService): IArkadeAddressProvider
{
    public Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(NextContractPurpose purpose, ContractActivityState activityState,
        ArkContract[]? inputContracts = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}