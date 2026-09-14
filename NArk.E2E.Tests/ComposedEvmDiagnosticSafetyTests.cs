using Xunit;

namespace NArk.E2E.Tests;

public sealed class ComposedEvmDiagnosticSafetyTests
{
    [Fact]
    public void Workflow_failure_upload_uses_only_sanitized_diagnostic_allowlist()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "composed-evm-e2e.yml"));
        var normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        var workflowLines = workflow.Split('\n').Select(line => line.Trim()).ToArray();
        var uploadedPaths = workflowLines
            .Where(line => line.StartsWith("/tmp/nark-composed-evm-", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([
            "/tmp/nark-composed-evm-*/startup.log",
            "/tmp/nark-composed-evm-*/runtime.log",
            "/tmp/nark-composed-evm-*/route.json",
            "/tmp/nark-composed-evm-*/evm-send-solver-admin.log",
            "/tmp/nark-composed-evm-*/ingress-solver-admin.log",
            "/tmp/nark-composed-evm-*/lightning-payment.log"
        ], uploadedPaths);
        Assert.DoesNotContain("regtest.env", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NArk.E2E.Tests/TestResults/", workflowLines);
        Assert.Contains("  pull_request:\n  workflow_dispatch:", normalizedWorkflow, StringComparison.Ordinal);
        Assert.Contains("ref: 76572b31a90bfe7daa619a7656bb044bfeade6d6", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_workflow_runs_for_stacked_pull_request_bases()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "dotnet.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("  pull_request:\n\njobs:", workflow, StringComparison.Ordinal);
        Assert.Contains("realpath \"$PACKAGE\"", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void General_e2e_workflow_uses_the_supported_ark_profile()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "e2e.yml"));

        Assert.Contains("regtest.mjs start --profile ark", workflow, StringComparison.Ordinal);
        Assert.Contains("ARKD_VTXO_TREE_EXPIRY: \"21600\"", workflow, StringComparison.Ordinal);
        Assert.Contains("ARKD_CHECKPOINT_EXIT_DELAY: \"1536\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("boltz", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retained_diagnostics_remove_environment_and_redact_private_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nark-diagnostic-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var environment = Path.Combine(directory, "regtest.env");
            var privateKey = new string('a', 64);
            var mnemonic = "abandon ability able about above absent absorb abstract absurd abuse access accident";
            var walletSeed = "solver-wallet-seed-sentinel";
            var password = "diagnostic-password-sentinel";
            var shortPassword = "short";
            File.WriteAllText(environment,
                $"EVM_CLIENT_PRIVATE_KEY={privateKey}\nINTENT_SOLVER_MNEMONIC={mnemonic}\nARKD_PASSWORD={password}\n");

            ComposedEvmInfrastructureFixture.RemoveGeneratedEnvironmentForDiagnostics(directory);
            var detectedSecrets = ComposedEvmInfrastructureFixture.SensitiveEnvironmentValues(new Dictionary<string, string>
            {
                ["EVM_CLIENT_PRIVATE_KEY"] = privateKey,
                ["INTENT_SOLVER_MNEMONIC"] = mnemonic,
                ["SOLVER_WALLET_SEED"] = walletSeed,
                ["ARKD_PASSWORD"] = shortPassword,
                ["BITCOIN_RPC_PASSWORD"] = "123",
                ["PUBLIC_ENDPOINT"] = "https://example.test"
            }).Select(pair => pair.Value).ToArray();
            var sanitized = ComposedEvmInfrastructureFixture.RedactDiagnosticContent(
                $"key={privateKey} mnemonic={mnemonic} seed={walletSeed} password={shortPassword}", detectedSecrets);

            Assert.False(File.Exists(environment));
            Assert.DoesNotContain(privateKey, sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain(mnemonic, sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain(walletSeed, sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain(shortPassword, sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("123", detectedSecrets);
            Assert.DoesNotContain("https://example.test", detectedSecrets);
            Assert.Equal(4, sanitized.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not locate repository file {Path.Combine(path)}.");
    }
}
