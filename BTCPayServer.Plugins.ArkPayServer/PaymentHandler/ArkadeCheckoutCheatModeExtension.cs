using System.Diagnostics;
using System.Runtime.InteropServices;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NBitcoin;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler
{
    public class ArkadeCheckoutCheatModeExtension(Cheater cheater) : ICheckoutCheatModeExtension
    {
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // The arkd container provides the in-container ark CLI used by cheat mode.
        // Nigiri's own `ark` subcommand hardcodes a container literally named "ark",
        // which no longer matches the v0.9-split topology (daemon container is "arkd",
        // signer-wallet sidecar is "ark-wallet"). Hit the daemon container directly so
        // cheat mode is decoupled from the nigiri binary's expectations.
        private static string ArkContainer =>
            Environment.GetEnvironmentVariable("ARKADE_CHEAT_ARK_CONTAINER") ?? "arkd";

        public bool Handle(PaymentMethodId paymentMethodId) => paymentMethodId == ArkadePlugin.ArkadePaymentMethodId;

        public async Task<string> Execute(string nigiriCommand)
        {
            var (fileName, arguments) = IsWindows
                ? ("wsl", $"nigiri {nigiriCommand}")
                : ("nigiri", nigiriCommand);
            return await RunProcess(fileName, arguments, $"nigiri {nigiriCommand}");
        }

        private async Task<string> ExecuteArk(string arkSubcommand)
        {
            var dockerArgs = $"exec {ArkContainer} ark {arkSubcommand}";
            var (fileName, arguments) = IsWindows
                ? ("wsl", $"docker {dockerArgs}")
                : ("docker", dockerArgs);
            return await RunProcess(fileName, arguments, $"docker {dockerArgs}");
        }

        private static async Task<string> RunProcess(string fileName, string arguments, string display)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode != 0
                ? throw new ExternalProcessFailedException(display, error + output)
                : output;
        }

        public async Task<ICheckoutCheatModeExtension.MineBlockResult> MineBlock(
            ICheckoutCheatModeExtension.MineBlockContext mineBlockContext)
        {
            await Execute($"rpc --generate {mineBlockContext.BlockCount}");
            return new ICheckoutCheatModeExtension.MineBlockResult();
        }

        public async Task<ICheckoutCheatModeExtension.PayInvoiceResult> PayInvoice(ICheckoutCheatModeExtension.PayInvoiceContext payInvoiceContext)
        {
            var destination = payInvoiceContext.PaymentPrompt.Destination;
            var amt = Money.Coins(payInvoiceContext.Amount).Satoshi;

            var arkArgs = $"send --to {destination} --amount {amt} --password secret";
            try
            {
               var output = await ExecuteArk(arkArgs);
               var arkOutput = JObject.Parse(output);
               var txId = arkOutput.GetValue("txid")?.Value<string>();
               if (txId is not null)
                   return new ICheckoutCheatModeExtension.PayInvoiceResult(txId);
               throw new Exception(output);
            }
            catch (ExternalProcessFailedException e) when(e.Message.Contains("not enough funds"))
            {
                var receiveJson = await ExecuteArk("receive");
                var onchainAddr = JObject.Parse(receiveJson).GetValue("onchain_address")?.Value<string>()
                    ?? throw new Exception("ark receive returned no onchain_address");
                await Execute($"faucet {onchainAddr} 2");
                await ExecuteArk("settle --password secret");
                return await PayInvoice(payInvoiceContext);
            }
            catch (ExternalProcessFailedException e) when(e.Message.Contains("VTXO_RECOVERABLE"))
            {
                await ExecuteArk("settle --password secret");
                return await PayInvoice(payInvoiceContext);
            }
        }
    }
}
