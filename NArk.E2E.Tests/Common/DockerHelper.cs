using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Minimal docker-exec helper for this plugin's E2E tests. Mirrors the subset of
/// <c>submodules/NNark/NArk.Tests.End2End/Common/DockerHelper.cs</c> we actually call into
/// (<see cref="Exec"/> + <see cref="CreateLndInvoice"/>) without taking a transitive dependency
/// on the SDK-side <c>FulmineLiquidityHelper</c> / <c>System.Net.Http.Json</c> surface that
/// collides with <c>Microsoft.AspNet.WebApi.Client</c>'s <c>PostAsJsonAsync</c> in the
/// BTCPayServer transitive graph.
/// </summary>
public static class DockerHelper
{
    public static async Task<string> Exec(string container, string[] args, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", container, .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return result.StandardOutput;
    }

    public static async Task<string> CreateLndInvoice(long amtSats = 10000, int expirySecs = 30,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "lncli", "--network=regtest", "addinvoice", "--amt", amtSats.ToString()
        };
        if (expirySecs > 0)
        {
            args.AddRange(["--expiry", expirySecs.ToString(CultureInfo.InvariantCulture)]);
        }

        var output = await Exec("lnd", args.ToArray(), ct);
        var invoice = JsonSerializer.Deserialize<JsonObject>(output)?["payment_request"]
                          ?.GetValue<string>()
                      ?? throw new InvalidOperationException($"Invoice creation on LND failed. Output: {output}");
        return invoice.Trim();
    }
}
