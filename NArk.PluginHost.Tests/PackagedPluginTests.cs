using System.IO.Compression;
using System.Runtime.Loader;
using BTCPayServer.Plugins.Dotnet;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NArk.PluginHost.Tests;

public class PackagedPluginTests(ITestOutputHelper output)
{
    [Fact]
    public void PackageBuildsItsDatabaseModelWithBtcpay242Dependencies()
    {
        var package = Environment.GetEnvironmentVariable("ARKADE_PLUGIN_PACKAGE");
        Assert.False(string.IsNullOrWhiteSpace(package), "Set ARKADE_PLUGIN_PACKAGE to the Release .btcpay package.");
        Assert.True(File.Exists(package), $"Plugin package does not exist: {package}");

        var hostEf = typeof(DbContext).Assembly;
        output.WriteLine($"Host EF: {hostEf.FullName} at {hostEf.Location}");
        Assert.Equal(new Version(10, 0, 10, 0), hostEf.GetName().Version);
        Assert.Equal(new Version(10, 0, 10, 0), typeof(DeleteBehavior).Assembly.GetName().Version);
        Assert.Equal(new Version(10, 0, 10, 0), typeof(RelationalDatabaseFacadeExtensions).Assembly.GetName().Version);

        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "packaged-plugin", Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(package, pluginDirectory);
        var pluginPath = Path.Combine(pluginDirectory, "BTCPayServer.Plugins.ArkPayServer.dll");
        using var loader = PluginLoader.CreateFromAssemblyFile(pluginPath, config =>
        {
            config.PreferSharedTypes = true;
            config.IsUnloadable = false;
            config.LoadAssembliesInDefaultLoadContext = false;
        });
        var pluginAssembly = loader.LoadDefaultAssembly();
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(pluginAssembly));
        output.WriteLine($"Packaged plugin: {pluginAssembly.FullName} at {pluginAssembly.Location}");

        var factoryType = pluginAssembly.GetType(
            "BTCPayServer.Plugins.ArkPayServer.Data.DesignTimeDbContextFactory", throwOnError: true)!;
        var factory = Activator.CreateInstance(factoryType);
        using var context = Assert.IsAssignableFrom<DbContext>(
            factoryType.GetMethod("CreateDbContext")!.Invoke(factory, [Array.Empty<string>()]));

        var walletEntity = context.Model.FindEntityType("NArk.Storage.EfCore.Entities.ArkWalletEntity");
        Assert.NotNull(walletEntity);
        Assert.NotNull(context.Model.FindEntityType("NArk.Storage.EfCore.Entities.ArkadeSwapIntentEntity"));
        Assert.Same(hostEf, context.GetType().BaseType!.Assembly);
        Assert.Same(AssemblyLoadContext.GetLoadContext(pluginAssembly),
            AssemblyLoadContext.GetLoadContext(walletEntity.ClrType.Assembly));
        var migrations = context.Database.GetMigrations().ToArray();
        Assert.NotEmpty(migrations);
        output.WriteLine($"Loaded database model with {context.Model.GetEntityTypes().Count()} entities and {migrations.Length} migrations.");
        foreach (var loadContext in AssemblyLoadContext.All)
        foreach (var assembly in loadContext.Assemblies.Where(a =>
                     a.GetName().Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                     a.GetName().Name!.StartsWith("NArk.", StringComparison.Ordinal)))
            output.WriteLine($"{loadContext.Name}: {assembly.FullName} at {assembly.Location}");
    }
}
