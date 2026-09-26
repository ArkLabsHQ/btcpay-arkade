using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;
using Xunit;

namespace NArk.Tests;

public class SolverFallbackTests
{
    [Fact]
    public async Task ASolverThatDoesNotQuote_HandsOverToTheNext()
    {
        var service = NewService();
        var tried = new List<Uri>();

        var reached = await service.NegotiateAcrossAsync(
            [Rendezvous("http://down.example"), Rendezvous("http://up.example")],
            (_, _) =>
            {
                tried.Add(new Uri($"http://{(tried.Count == 0 ? "down" : "up")}.example"));
                return tried.Count == 1
                    ? throw new HttpRequestException("refused")
                    : Task.FromResult("quoted");
            });

        Assert.Equal("quoted", reached);
        Assert.Equal(2, tried.Count);
    }

    [Fact]
    public async Task TheFirstSolverThatQuotes_IsTheOnlyOneAsked()
    {
        var service = NewService();
        var asked = 0;

        await service.NegotiateAcrossAsync(
            [Rendezvous("http://a.example"), Rendezvous("http://b.example")],
            (_, _) =>
            {
                asked++;
                return Task.FromResult("quoted");
            });

        Assert.Equal(1, asked);
    }

    // The last refusal is the interesting one; swallowing it would report "no solver" for a solver that
    // answered and said no.
    [Fact]
    public async Task WhenNoneQuote_TheLastRefusalSurfaces()
    {
        var service = NewService();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.NegotiateAcrossAsync<string>(
            [Rendezvous("http://a.example"), Rendezvous("http://b.example")],
            (_, _) => throw new HttpRequestException("still refused")));
    }

    [Fact]
    public async Task CancellationIsNotARefusal_AndStopsTheSearch()
    {
        var service = NewService();
        var asked = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.NegotiateAcrossAsync<string>(
            [Rendezvous("http://a.example"), Rendezvous("http://b.example")],
            (_, _) =>
            {
                asked++;
                throw new OperationCanceledException();
            }));

        Assert.Equal(1, asked);
    }

    private static ArkadeSolverService NewService()
    {
        var options = new ArkadeSolverOptions();
        return new ArkadeSolverService(
            options,
            new ArkadeSolverSelector(options, "regtest"),
            new SingleClientFactory(),
            NullLogger<ArkadeSolverService>.Instance);
    }

    private static SolverRendezvous Rendezvous(string relay) => new("", new Uri(relay), null);

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
