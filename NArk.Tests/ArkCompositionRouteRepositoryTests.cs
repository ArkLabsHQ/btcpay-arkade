using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionRouteRepositoryTests
{
    [Fact]
    public async Task IndexRoundTripsWithoutAnySwapState()
    {
        await using var database = await RouteDatabase.Create();
        var route = ArkCompositionRoute.Create("store", "invoice", "BTC-LN", "wallet",
            new string('a', 64), new string('b', 64), new string('c', 64), 1000,
            "lnbc1", 1800000040, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        await database.Repository.Add("store", route);

        var stored = await database.Repository.Get("store", route.RouteId);

        Assert.NotSame(route, stored);
        Assert.Equal(route.RouteId, stored!.RouteId);
        Assert.Equal("invoice", stored.InvoiceId);
        Assert.Equal(new string('a', 64),
            (await database.Repository.FindByPaymentHash("store", new string('C', 64)))!.OutgoingSwapId);
        Assert.Equal(route.RouteId, Assert.Single(await database.Repository.List("store", "invoice", "BTC-LN")).RouteId);
    }

    [Fact]
    public async Task RoutesAreStoreScopedAndInvoicesAttachOnce()
    {
        await using var database = await RouteDatabase.Create();
        var route = ArkCompositionRoute.Create("store", null, "ARKADE", "wallet",
            new string('a', 64), null, new string('c', 64), 1000, "ark1", null,
            DateTimeOffset.FromUnixTimeSeconds(1800000000));
        await database.Repository.Add("store", route);

        Assert.Null(await database.Repository.Get("other-store", route.RouteId));
        await database.Repository.AttachInvoice("store", route.RouteId, "invoice");
        Assert.Equal("invoice", (await database.Repository.Get("store", route.RouteId))!.InvoiceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Repository.AttachInvoice("store", route.RouteId, "other-invoice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Repository.AttachInvoice("other-store", route.RouteId, "invoice"));
    }

    [Fact]
    public async Task OnlyPromptedRoutesAreListedForExecution()
    {
        await using var database = await RouteDatabase.Create();
        await database.Repository.Add("store", ArkCompositionRoute.Create("store", null, "BTC-LN", "wallet",
            new string('a', 64), new string('b', 64), new string('c', 64), 1000, "lnbc1", 1800000040,
            DateTimeOffset.FromUnixTimeSeconds(1800000000)));
        await database.Repository.Add("store", ArkCompositionRoute.Create("store", null, "BTC-LN", "wallet",
            new string('d', 64), new string('e', 64), new string('f', 64), 1000, null, null,
            DateTimeOffset.FromUnixTimeSeconds(1800000000)));

        var prompted = await database.Repository.ListAllWithPrompts(0, 100);

        Assert.Equal(2, (await database.Repository.List("store")).Count);
        Assert.Single(prompted);
    }
}

internal sealed class RouteDatabase : IDbContextFactory<ArkPluginDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    internal ArkCompositionRouteRepository Repository => new(this);

    internal static async Task<RouteDatabase> Create()
    {
        var database = new RouteDatabase();
        await database._connection.OpenAsync();
        await using var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
        return database;
    }

    public ArkPluginDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ArkPluginDbContext>()
        .UseSqlite(_connection).Options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
