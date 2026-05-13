// Writes BTCPay's appsettings.dev.json with a DEBUG_PLUGINS pointer at
// the Arkade plugin's build output. BTCPay's Program.cs unconditionally
// loads appsettings.dev.json under #if DEBUG and PluginManager preloads
// each ;-separated path before running its normal Plugins-folder scan.
//
// Mirrors the pattern from rockstardev's BTCPayServerPlugins.RockstarDev:
// a one-shot console app, run as a pre-test step, that resolves plugin
// DLL paths at the current configuration and writes them out. This keeps
// the test fixtures simple — they just `dotnet test`, BTCPay finds its
// plugin via the standard config file.

using System.Reflection;
using System.Text.Json;

var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
var repoRoot = FindRepoRoot(exeDir) ?? throw new InvalidOperationException(
    $"Could not locate repo root (looking for NArk.sln) starting from {exeDir}.");

// The plugin's build configuration matches what test runners use — Debug
// for local + CI by default. If the caller wants Release, they can pass it
// as args[0]; we'll honour it.
var configuration = args.Length > 0 ? args[0] : "Debug";

var pluginDll = Path.Combine(
    repoRoot,
    "BTCPayServer.Plugins.ArkPayServer",
    "bin", configuration, "net10.0",
    "BTCPayServer.Plugins.ArkPayServer.dll");

if (!File.Exists(pluginDll))
{
    Console.Error.WriteLine($"Plugin DLL not found: {pluginDll}");
    Console.Error.WriteLine("Build the plugin first (dotnet build NArk.sln).");
    return 1;
}

var settingsPath = Path.Combine(
    repoRoot,
    "submodules", "btcpayserver", "BTCPayServer",
    "appsettings.dev.json");

var settings = new Dictionary<string, string>
{
    ["DEBUG_PLUGINS"] = Path.GetFullPath(pluginDll)
};

var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(settingsPath, json);

Console.WriteLine($"Wrote {settingsPath}");
Console.WriteLine($"  DEBUG_PLUGINS = {settings["DEBUG_PLUGINS"]}");
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
