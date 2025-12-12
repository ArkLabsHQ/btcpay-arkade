using Microsoft.Extensions.Logging;
using NArk.Contracts;
using NArk.Scripts;
using NArk.Models;

namespace NArk.Services;

public static class ContractUtils
{
    
    public static ArkContract DerivePaymentContract(DeriveContractRequest request)
    {

        if (request.Tweak is null)
        {
            return new ArkPaymentContract(request.OperatorTerms.SignerKey, request.OperatorTerms.UnilateralExit,
                request.User);
        }

        return new HashLockedArkPaymentContract(
            request.OperatorTerms.SignerKey, 
            request.OperatorTerms.UnilateralExit, 
            request.User, 
            request.Tweak,
            HashLockTypeOption.HASH160);
    }
}