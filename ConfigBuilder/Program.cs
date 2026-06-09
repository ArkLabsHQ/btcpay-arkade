// Writes appsettings.dev.json files that point BTCPay at the Arkade
// plugin's build output via DEBUG_PLUGINS. BTCPay's Program.cs loads
// appsettings.dev.json under #if DEBUG; PluginManager pre-loads the
// listed DLL(s).
//
// BTCPay.Tests.BTCPayServerTester does `confBuilder.SetBasePath(TestUtils.TestDirectory)`
// where TestDirectory == AppContext.BaseDirectory — i.e., the test project's
// bin folder. We write the file there so ServerTester picks it up when
// running BTCPay in-process. We also write it to BTCPay's source dir so
// `dotnet run` from the submodule (local dev) honours it too.

using System.Reflection;
using System.Text.Json;

var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
var repoRoot = FindRepoRoot(exeDir) ?? throw new InvalidOperationException(
    $"Could not locate repo root (looking for NArk.sln) starting from {exeDir}.");

var configuration = args.Length > 0 ? args[0] : "Debug";

var pluginDll = Path.GetFullPath(Path.Combine(
    repoRoot,
    "BTCPayServer.Plugins.ArkPayServer",
    "bin", configuration, "net10.0",
    "BTCPayServer.Plugins.ArkPayServer.dll"));

if (!File.Exists(pluginDll))
{
    Console.Error.WriteLine($"Plugin DLL not found: {pluginDll}");
    Console.Error.WriteLine("Build the plugin first (dotnet build NArk.sln).");
    return 1;
}

var settings = new Dictionary<string, string>
{
    ["DEBUG_PLUGINS"] = pluginDll
};
var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

// 1) BTCPay's source dir — used by `dotnet run` workflows (local dev).
var sourcePath = Path.Combine(
    repoRoot, "submodules", "btcpayserver", "BTCPayServer", "appsettings.dev.json");
await File.WriteAllTextAsync(sourcePath, json);
Console.WriteLine($"Wrote {sourcePath}");

// 2) NArk.E2E.Tests bin folder — used by BTCPayServerTester in tests
//    (it calls SetBasePath(AppContext.BaseDirectory), which lands here).
var testBin = Path.Combine(
    repoRoot, "NArk.E2E.Tests", "bin", configuration, "net10.0");
if (Directory.Exists(testBin))
{
    var testPath = Path.Combine(testBin, "appsettings.dev.json");
    await File.WriteAllTextAsync(testPath, json);
    Console.WriteLine($"Wrote {testPath}");
}
else
{
    Console.WriteLine($"NArk.E2E.Tests bin not built yet ({testBin}); skipping test-bin copy.");
}

Console.WriteLine($"  DEBUG_PLUGINS = {pluginDll}");
return 0;

static string? FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "NArk.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
