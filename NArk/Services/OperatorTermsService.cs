using Ark.V1;
using Microsoft.Extensions.Logging;
using NArk.Extensions;
using NArk.Services.Abstractions;
using NArk.Models;

namespace NArk.Services;

public class OperatorTermsService(
    ArkService.ArkServiceClient arkClient, 
    ILogger<OperatorTermsService> logger)
    : IOperatorTermsService
{
    public virtual async Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await arkClient.GetInfoAsync(new GetInfoRequest() , cancellationToken: cancellationToken);
            var terms = info.ArkOperatorTerms();
            return terms;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update operator terms.");
            throw;
        }
    }
}