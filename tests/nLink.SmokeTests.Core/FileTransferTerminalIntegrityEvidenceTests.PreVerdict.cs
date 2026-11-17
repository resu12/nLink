using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalPreVerdict_PowerShell7ConsumesBeforeVerdict()
        => RunSourceBindingCaseAsync("preverdict-pwsh", nameof(TerminalPreVerdict_PowerShell7ConsumesBeforeVerdict));
    [Fact]
    public Task TerminalPreVerdict_WindowsPowerShellConsumesBeforeVerdict()
        => RunSourceBindingCaseAsync("preverdict-powershell", nameof(TerminalPreVerdict_WindowsPowerShellConsumesBeforeVerdict));
    [Fact]
    public Task TerminalPreVerdict_PreservesExplicitIntegrityConflict()
        => RunSourceBindingCaseAsync("preverdict-false", nameof(TerminalPreVerdict_PreservesExplicitIntegrityConflict));
    [Fact]
    public Task TerminalPreVerdict_PreservesIndependentFailureWithAgreeingGui()
        => RunSourceBindingCaseAsync("preverdict-true", nameof(TerminalPreVerdict_PreservesIndependentFailureWithAgreeingGui));
    [Fact]
    public Task TerminalPreVerdict_RejectsRecordBudgetOverflow()
        => RunSourceBindingCaseAsync("preverdict-budget", nameof(TerminalPreVerdict_RejectsRecordBudgetOverflow));
    [Fact]
    public Task TerminalPreVerdict_RejectsCodeUnitBudgetOverflow()
        => RunSourceBindingCaseAsync("preverdict-units", nameof(TerminalPreVerdict_RejectsCodeUnitBudgetOverflow));
    [Fact]
    public Task TerminalPreVerdict_RejectsPostJoinExpiry()
        => RunSourceBindingCaseAsync("preverdict-expired", nameof(TerminalPreVerdict_RejectsPostJoinExpiry));
    [Fact]
    public Task TerminalPreVerdict_RejectsDisposedMembership()
        => RunSourceBindingCaseAsync("preverdict-disposed", nameof(TerminalPreVerdict_RejectsDisposedMembership));
    [Fact]
    public Task TerminalPreVerdict_RejectsStoppedClock()
        => RunSourceBindingCaseAsync("preverdict-stopped", nameof(TerminalPreVerdict_RejectsStoppedClock));
    [Fact]
    public Task TerminalPreVerdict_RejectsUnjoinedPayload()
        => RunSourceBindingCaseAsync("preverdict-unjoined", nameof(TerminalPreVerdict_RejectsUnjoinedPayload));
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public async Task TerminalPreVerdict_ReaderInventoryAndSeamControls(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "pre-verdict-integrity-20260926", host + "-controls-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var process = new Process { StartInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root,
        } };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "TerminalPreVerdictControls.ps1"), root, directory })
        { process.StartInfo.ArgumentList.Add(argument); }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.txt"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), await stderr);
        Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "controls.json")));
        Assert.Equal(42, result.RootElement.GetProperty("CountFactorControls").GetInt32());
        Assert.Equal(10, result.RootElement.GetProperty("GuiReadControls").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("AuthorityEvaluations").GetInt32());
        Assert.False(result.RootElement.GetProperty("AcceptanceChanged").GetBoolean());
    }

    private static void CheckTerminalPreVerdictResult(string directory, JsonElement primary, string mode)
    {
        var shadow = primary.GetProperty("PreVerdictIntegrity");
        var consumed = mode is "pwsh" or "powershell" or "true" or "false";
        Assert.Equal(consumed, shadow.GetProperty("Consumed").GetBoolean());
        Assert.Equal(mode == "false" ? "conflict" : consumed ? "owned_integrity_proven" : "owned_integrity_unproven",
            shadow.GetProperty("State").GetString());
        Assert.Equal(mode is "true" or "false" ? "read" : "absent", shadow.GetProperty("GuiReadStatus").GetString());
        Assert.True(shadow.GetProperty("BeforeDecisionReports").GetBoolean());
        Assert.Equal(3, shadow.GetProperty("Evaluations").GetInt32());
        Assert.Equal(12, shadow.GetProperty("RejectedOwnerControls").GetInt32());
        Assert.True(shadow.GetProperty("RepeatedConsumptionRejected").GetBoolean());
        Assert.Equal(0, shadow.GetProperty("AdditionalWriterReads").GetInt32());
        Assert.Equal(0, shadow.GetProperty("PrimaryPayloadHashPasses").GetInt32());
        Assert.InRange(shadow.GetProperty("CumulativeRecordVisits").GetInt64(), 1, 4096);
        Assert.InRange(shadow.GetProperty("CumulativeMilliseconds").GetInt64(), 0, 20000);
        Assert.False(shadow.GetProperty("AcceptanceChanged").GetBoolean());
        Assert.False(shadow.GetProperty("CanonicalScenarioProven").GetBoolean());
        var inventory = shadow.GetProperty("Inventory");
        var factors = inventory.GetProperty("Factors");
        Assert.Equal(58, factors.EnumerateObject().Count());
        Assert.Equal("fail", factors.GetProperty("LiveRouteProof").GetString());
        Assert.Equal("pass", factors.GetProperty("RouteConsistency").GetString());
        Assert.Equal("pass", factors.GetProperty("ArtifactVerdict").GetString());
        Assert.Equal("pass", factors.GetProperty("ParityVerdict").GetString());
        Assert.Equal(mode == "true", factors.GetProperty("GuiIntegrityOk").GetBoolean());
        Assert.Equal(-1, inventory.GetProperty("FirstSelectedIndex").GetInt32());
        Assert.Empty(inventory.GetProperty("Candidates").EnumerateArray());
        Assert.Empty(inventory.GetProperty("MismatchCandidates").EnumerateArray());
        Assert.Empty(inventory.GetProperty("EffectiveMismatchCandidates").EnumerateArray());
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "filetransfer-retained-compact-summary.json")));
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", report.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(mode == "true" ? 1 : 0, report.RootElement.GetProperty("gui_integrity_ok").GetInt32());
        Assert.Equal("(none)", report.RootElement.GetProperty("first_proof_chain_failure_class").GetString());
        using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "terminal-integrity-cleanup.json")));
        Assert.Equal(consumed, cleanup.RootElement.GetProperty("WasConsumed").GetBoolean());
        Assert.True(cleanup.RootElement.GetProperty("Retired").GetBoolean());
    }
}

