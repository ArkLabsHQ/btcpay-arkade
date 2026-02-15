using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Newtonsoft.Json.Linq;

namespace NArk.E2E.Tests.Helpers;

/// <summary>
/// Helper for funding Ark wallets in tests.
/// Uses `nigiri ark send` CLI matching the cheat mode pattern.
/// </summary>
public class ArkFundingHelper
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Fund a wallet by sending VTXOs to a contract address.
    /// Uses `nigiri ark send` matching the cheat mode extension pattern.
    /// </summary>
    /// <param name="destination">The Ark address to fund (e.g., tark1...)</param>
    /// <param name="amountSats">Amount in satoshis to send</param>
    /// <returns>Transaction ID from the send operation</returns>
    public async Task<string?> FundWalletAsync(string destination, long amountSats)
    {
        TestContext.Progress.WriteLine($"Funding {destination} with {amountSats} sats via nigiri ark send...");

        var nigiriArgs = $"ark send --to {destination} --amount {amountSats} --password secret";
        var (fileName, arguments) = GetProcessInfo(nigiriArgs);

        var result = await Cli.Wrap(fileName)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"nigiri ark send failed (exit code {result.ExitCode}):\n{result.StandardError}");
        }

        TestContext.Progress.WriteLine($"Funded successfully: {result.StandardOutput.Trim()}");

        // Parse txid from JSON output
        try
        {
            var output = JObject.Parse(result.StandardOutput);
            return output.GetValue("txid")?.Value<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fund a wallet with a specific BTC amount.
    /// </summary>
    public async Task<string?> FundWalletAsync(string destination, decimal amountBtc)
    {
        var amountSats = (long)(amountBtc * 100_000_000);
        return await FundWalletAsync(destination, amountSats);
    }

    /// <summary>
    /// Mine Bitcoin blocks (for onchain settlements or confirmations).
    /// Uses `nigiri rpc --generate` matching the cheat mode pattern.
    /// </summary>
    public async Task MineBlocksAsync(int blockCount = 1)
    {
        TestContext.Progress.WriteLine($"Mining {blockCount} blocks...");

        var nigiriArgs = $"rpc --generate {blockCount}";
        var (fileName, arguments) = GetProcessInfo(nigiriArgs);

        var result = await Cli.Wrap(fileName)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to generate blocks: {result.StandardError}");
        }

        TestContext.Progress.WriteLine($"Mined {blockCount} blocks.");
    }

    /// <summary>
    /// Trigger an Ark round by waiting for the daemon's round interval.
    /// In regtest, rounds happen approximately every 10 seconds.
    /// </summary>
    public async Task TriggerRoundAsync()
    {
        TestContext.Progress.WriteLine("Waiting for Ark round...");

        // The Ark daemon processes rounds automatically on a schedule.
        // In regtest, this is typically every 10 seconds.
        await Task.Delay(TimeSpan.FromSeconds(12));

        TestContext.Progress.WriteLine("Ark round should be complete.");
    }

    /// <summary>
    /// Get Ark daemon's receive address via nigiri.
    /// </summary>
    public async Task<string> GetDaemonReceiveAddressAsync()
    {
        var nigiriArgs = "ark receive";
        var (fileName, arguments) = GetProcessInfo(nigiriArgs);

        var result = await Cli.Wrap(fileName)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to get receive address: {result.StandardError}");
        }

        // Parse address from output (might be JSON or plain text)
        var output = result.StandardOutput.Trim();
        try
        {
            var json = JObject.Parse(output);
            return json.GetValue("address")?.Value<string>() ?? output;
        }
        catch
        {
            return output;
        }
    }

    /// <summary>
    /// Get Ark daemon's balance via nigiri.
    /// </summary>
    public async Task<long> GetDaemonBalanceAsync()
    {
        var nigiriArgs = "ark balance";
        var (fileName, arguments) = GetProcessInfo(nigiriArgs);

        var result = await Cli.Wrap(fileName)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (result.ExitCode != 0)
        {
            throw new Exception($"Failed to get balance: {result.StandardError}");
        }

        var output = result.StandardOutput.Trim();
        try
        {
            var json = JObject.Parse(output);
            return json.GetValue("balance")?.Value<long>() ?? 0;
        }
        catch
        {
            if (long.TryParse(output, out var balance))
                return balance;
            return 0;
        }
    }

    /// <summary>
    /// Fund the Ark daemon via Bitcoin faucet if needed.
    /// </summary>
    public async Task EnsureDaemonFundedAsync(long minimumSats = 10_000_000)
    {
        var balance = await GetDaemonBalanceAsync();

        if (balance >= minimumSats)
        {
            TestContext.Progress.WriteLine($"Daemon has sufficient funds: {balance} sats");
            return;
        }

        TestContext.Progress.WriteLine($"Daemon balance {balance} < {minimumSats}, funding via faucet...");

        // Get daemon's receive address
        var address = await GetDaemonReceiveAddressAsync();

        // Fund via nigiri faucet
        var (fileName, arguments) = GetProcessInfo($"faucet {address}");
        await Cli.Wrap(fileName)
            .WithArguments(arguments)
            .ExecuteAsync();

        // Mine blocks and wait for daemon to process
        await MineBlocksAsync(6);
        await Task.Delay(TimeSpan.FromSeconds(5));

        TestContext.Progress.WriteLine("Daemon funded via faucet.");
    }

    /// <summary>
    /// Returns the appropriate process info for executing nigiri commands.
    /// On Windows, uses WSL. On Linux/macOS, executes directly.
    /// Matches the pattern from ArkadeCheckoutCheatModeExtension.
    /// </summary>
    private static (string FileName, string Arguments) GetProcessInfo(string nigiriArgs)
    {
        if (IsWindows)
        {
            // On Windows, use WSL to execute nigiri
            return ("wsl", $"nigiri {nigiriArgs}");
        }

        // On Linux/macOS, execute nigiri directly
        return ("nigiri", nigiriArgs);
    }
}
