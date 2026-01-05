using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Newtonsoft.Json.Linq;

namespace NArk.E2E.Tests.Helpers;

/// <summary>
/// Helper for Lightning Network operations in tests.
/// Uses docker exec to interact with LND containers.
/// Based on create-invoice.sh and pay-invoice.sh scripts.
/// </summary>
public class LightningHelper
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// LND nodes available in the test environment.
    /// </summary>
    public enum LndNode
    {
        /// <summary>Main user LND node (container: lnd)</summary>
        Lnd,
        /// <summary>Boltz LND node (container: boltz-lnd)</summary>
        BoltzLnd
    }

    /// <summary>
    /// Create a Lightning invoice on the specified LND node.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis</param>
    /// <param name="memo">Optional invoice memo</param>
    /// <param name="node">Which LND node to create the invoice on</param>
    /// <returns>BOLT11 payment request string</returns>
    public async Task<string> CreateInvoiceAsync(long amountSats, string? memo = null, LndNode node = LndNode.Lnd)
    {
        var container = node == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";
        TestContext.Progress.WriteLine($"Creating invoice for {amountSats} sats on {container}...");

        var lncliArgs = $"--network=regtest addinvoice --amt {amountSats}";
        if (!string.IsNullOrEmpty(memo))
        {
            lncliArgs += $" --memo \"{memo}\"";
        }

        var result = await ExecuteDockerExecAsync(container, "lncli", lncliArgs);

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to create invoice: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        var paymentRequest = json["payment_request"]?.Value<string>();

        if (string.IsNullOrEmpty(paymentRequest))
        {
            throw new Exception($"No payment_request in response: {result.StandardOutput}");
        }

        TestContext.Progress.WriteLine($"Created invoice: {paymentRequest[..50]}...");
        return paymentRequest;
    }

    /// <summary>
    /// Pay a Lightning invoice from the specified LND node.
    /// Auto-detects which node to pay from if not specified.
    /// </summary>
    /// <param name="bolt11">BOLT11 payment request</param>
    /// <param name="fromNode">Node to pay from (auto-detect if null)</param>
    /// <returns>Payment preimage</returns>
    public async Task<string> PayInvoiceAsync(string bolt11, LndNode? fromNode = null)
    {
        // Auto-detect which node should pay
        if (fromNode == null)
        {
            fromNode = await DetectPayerNodeAsync(bolt11);
        }

        var container = fromNode == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";
        TestContext.Progress.WriteLine($"Paying invoice from {container}...");

        var result = await ExecuteDockerExecAsync(
            container,
            "lncli",
            $"--network=regtest payinvoice --force {bolt11}");

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to pay invoice: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        var preimage = json["payment_preimage"]?.Value<string>();

        TestContext.Progress.WriteLine($"Payment successful. Preimage: {preimage?[..20]}...");
        return preimage ?? "";
    }

    /// <summary>
    /// Detect which node should pay based on the invoice destination.
    /// If the invoice is for boltz-lnd, pay from lnd. If for lnd, pay from boltz-lnd.
    /// </summary>
    private async Task<LndNode> DetectPayerNodeAsync(string bolt11)
    {
        // Decode the invoice on lnd to get the destination
        var result = await ExecuteDockerExecAsync(
            "lnd",
            "lncli",
            $"--network=regtest decodepayreq {bolt11}");

        if (result.ExitCode != 0)
        {
            // Default to paying from lnd
            return LndNode.Lnd;
        }

        var json = JObject.Parse(result.StandardOutput);
        var destination = json["destination"]?.Value<string>();

        // Get lnd's pubkey to compare
        var lndInfo = await GetNodeInfoAsync(LndNode.Lnd);

        // If destination matches lnd, pay from boltz-lnd. Otherwise pay from lnd.
        if (destination == lndInfo.PubKey)
        {
            return LndNode.BoltzLnd;
        }

        return LndNode.Lnd;
    }

    /// <summary>
    /// Get node info (pubkey, alias).
    /// </summary>
    public async Task<(string PubKey, string Alias)> GetNodeInfoAsync(LndNode node = LndNode.Lnd)
    {
        var container = node == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";

        var result = await ExecuteDockerExecAsync(container, "lncli", "--network=regtest getinfo");

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to get node info: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        return (
            json["identity_pubkey"]?.Value<string>() ?? "",
            json["alias"]?.Value<string>() ?? ""
        );
    }

    /// <summary>
    /// Get channel balance for a node.
    /// </summary>
    public async Task<long> GetChannelBalanceAsync(LndNode node = LndNode.Lnd)
    {
        var container = node == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";

        var result = await ExecuteDockerExecAsync(container, "lncli", "--network=regtest channelbalance");

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to get channel balance: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        return json["balance"]?.Value<long>() ?? 0;
    }

    /// <summary>
    /// Get wallet (on-chain) balance for a node.
    /// </summary>
    public async Task<long> GetWalletBalanceAsync(LndNode node = LndNode.Lnd)
    {
        var container = node == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";

        var result = await ExecuteDockerExecAsync(container, "lncli", "--network=regtest walletbalance");

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to get wallet balance: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        return json["total_balance"]?.Value<long>() ?? 0;
    }

    /// <summary>
    /// Get a new on-chain address from the node.
    /// </summary>
    public async Task<string> GetNewAddressAsync(LndNode node = LndNode.Lnd)
    {
        var container = node == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";

        var result = await ExecuteDockerExecAsync(container, "lncli", "--network=regtest newaddress p2wkh");

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to get new address: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        return json["address"]?.Value<string>() ?? throw new Exception("No address in response");
    }

    /// <summary>
    /// List channels for a node.
    /// </summary>
    public async Task<JArray> ListChannelsAsync(LndNode node = LndNode.Lnd)
    {
        var container = node == LndNode.BoltzLnd ? "boltz-lnd" : "lnd";

        var result = await ExecuteDockerExecAsync(container, "lncli", "--network=regtest listchannels");

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to list channels: {result.StandardError}");
        }

        var json = JObject.Parse(result.StandardOutput);
        return json["channels"] as JArray ?? new JArray();
    }

    /// <summary>
    /// Check if there's an active channel between the two LND nodes.
    /// </summary>
    public async Task<bool> HasActiveChannelAsync()
    {
        var channels = await ListChannelsAsync(LndNode.Lnd);
        return channels.Count > 0 && channels.Any(c => c["active"]?.Value<bool>() == true);
    }

    /// <summary>
    /// Wait for a channel to become active.
    /// </summary>
    public async Task WaitForActiveChannelAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await HasActiveChannelAsync())
            {
                TestContext.Progress.WriteLine("Channel is active.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException("No active channel found within timeout");
    }

    /// <summary>
    /// Execute a command in a docker container.
    /// </summary>
    private async Task<BufferedCommandResult> ExecuteDockerExecAsync(
        string container,
        string command,
        string arguments)
    {
        var dockerArgs = $"exec {container} {command} {arguments}";

        return await Cli.Wrap("docker")
            .WithArguments(dockerArgs)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
    }
}
