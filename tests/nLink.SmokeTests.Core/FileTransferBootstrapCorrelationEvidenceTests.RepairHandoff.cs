using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class FileTransferBootstrapCorrelationEvidenceTests
{
    [Theory]
    [InlineData("restore")]
    [InlineData("adopt")]
    [InlineData("rollback")]
    [Trait("RetainedFallbackSuite", "FallbackRepairHandoffEvidence")]
    public async Task RepairHandoff_ProductionProjectionPreservesExactFullCompactProof(string terminal)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "fallback-reactivation-handoff-20261002",
            "evidence", terminal + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "repair-handoff.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateRepairHandoffEvidenceFixture(terminal)),
            new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/FallbackRepairHandoffEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
            """;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("fallback-repair-handoff-evidence-ok", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("-unknown", false)]
    public void RepairHandoff_RedactorPreservesOnlyTheExplicitChildProfile(string suffix, bool preserved)
    {
        const string profile = "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1-receiver-pre-admission-v1-initial-data-session-v1-early-checkpoint-proof-v1-fallback-repair-handoff-v1";
        foreach (var field in new[] { "snapshot_catalog_version", "continuity_profile", "repair_owner_profile" })
        {
            var eventName = field == "snapshot_catalog_version" ? "filetransfer_coordinator_transition_snapshot" : "filetransfer_fallback_frontier_repair_authority_granted";
            var line = SensitiveDataRedactor.Redact($"event={eventName}; {field}={profile}{suffix}; reason=test");
            Assert.Equal(preserved, line.Contains($"{field}={profile}{suffix};", StringComparison.Ordinal));
        }
    }
}
