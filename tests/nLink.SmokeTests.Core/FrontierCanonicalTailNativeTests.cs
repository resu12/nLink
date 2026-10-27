using System.Security.Cryptography;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FrontierCanonicalTailBudgetTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task CanonicalTailBudget_LeavesOriginalFinalizationAndOuterReserve()
    {
        var root = FindRepoRoot();
        var source = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/FrontierCanonicalTailNativeTests.cs"));
        var composition = source[source.LastIndexOf("public async Task OriginalApps_PreserveIneligibleCanonicalTailInPrimary", StringComparison.Ordinal)..];
        Assert.Contains("\"pwsh\", timeout: TimeSpan.FromMinutes(70)", composition);
        var dispatch = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        Assert.Contains("elseif($Case.StartsWith('writer-source-read-cross-process-carrier-full-')) {210}", dispatch);
        var wrapper = await File.ReadAllTextAsync(Path.Combine(root, "tools/Run-FileTransferTunaGuiSmoke.ps1"));
        Assert.Contains("$defaultSeconds = 120", wrapper);
        Assert.Contains("$maximumHardDeadlineSeconds = (2L * [long]$TimeoutSeconds) + [long]$finalizationGraceSeconds", wrapper);
        var acceptance = await File.ReadAllTextAsync(Path.Combine(root, "tools/Run-FileTransferRouteAcceptance.ps1"));
        Assert.Contains("$analysisTimeoutSeconds = 3600", acceptance);
        Assert.Equal(TimeSpan.FromMinutes(5), ScriptProcessTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(70) - TimeSpan.FromSeconds(2 * 210 + 120) - TimeSpan.FromSeconds(3600));
    }
}

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierCanonicalTailNativeTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RequiredConfiguration", "Debug")]
    public async Task OriginalApps_PreserveIneligibleCanonicalTailInPrimary()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "canonical-tail-native-characterization-20260929",
            "pwsh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        const string scenario = "writer-source-read-cross-process-carrier-full-canonical-tail-late-publication";
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, "writer-valid", "valid", "canonical-tail-late-publication", "", "Debug", "canonical-ineligible-v1"],
            "pwsh", timeout: TimeSpan.FromMinutes(70));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        Assert.Contains("attempt-canonical-tail-stopped-before-route-acceptance", result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-destination-gui-adoption-ok", gui);
        Assert.Contains("attempt-payload-native-writer-handoff-ok", gui);
        var analysis = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis.stdout.txt"));
        Assert.Contains("original parent analyzer evaluations=3 nativeReads=1", analysis);
        Assert.Contains("attempt-canonical-tail-ineligible-primary-checked", analysis);
        Assert.DoesNotContain("canonical-production-pair-check-observed", analysis);
        using var observation = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "late-publication-primary-observation.json")));
        var proof = observation.RootElement;
        Assert.Equal("filetransfer_tuna_activation_delivery_unproven", proof.GetProperty("SelectedFailure").GetString());
        Assert.Equal("INCONCLUSIVE", proof.GetProperty("PrimaryVerdict").GetString());
        Assert.Equal("fail", proof.GetProperty("GateStatus").GetString());
        Assert.Equal(0, proof.GetProperty("ResolverCalls").GetInt32());
        Assert.Equal(0, proof.GetProperty("WitnessCount").GetInt32());
        foreach (var name in new[] { "SamePairJoined", "TransferBindingAttempted", "OriginalOutputCommitted",
            "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
            Assert.False(proof.GetProperty(name).GetBoolean());
        Assert.True(proof.GetProperty("CanonicalTailIneligibleChecked").GetBoolean());
        Assert.Equal(0, proof.GetProperty("OpenPinsAfterCleanup").GetInt32());
        Assert.True(proof.GetProperty("TextCleared").GetBoolean());
        var reports = proof.GetProperty("ReportsBefore").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reports));
        Assert.Equal(reports, proof.GetProperty("ReportsAfter").GetString());
        using var cleanup = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "canonical-tail-ineligible-cleanup.json")));
        Assert.Equal(2, cleanup.RootElement.GetProperty("HandleCount").GetInt32());
        Assert.Equal(0, cleanup.RootElement.GetProperty("OpenHandles").GetInt32());
        Assert.False(cleanup.RootElement.GetProperty("EvaluationCurrent").GetBoolean());
        string? pair = null;
        foreach (var role in new[] { "helpee", "helper" })
        {
            var output = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(
                Path.Combine(directory, "process-output"), role + "-*.stdout.log")));
            var line = Assert.Single(output.Split('\n').Where(line => line.StartsWith("cross-process-nkn-", StringComparison.Ordinal)));
            Assert.StartsWith("cross-process-nkn-" + role + "=268435456; route=post_tuna_fallback_v6; sha256=", line);
            var identity = line[line.IndexOf("; sha256=", StringComparison.Ordinal)..].Trim();
            if (pair is not null) Assert.Equal(pair, identity);
            pair = identity;
            if (role == "helpee")
                Assert.Single(output.Split('\n').Where(line => line.StartsWith("carrier-canonical-tail=armed;", StringComparison.Ordinal)));
            else
            {
                Assert.Contains("carrier-frontier-hold-policy=late-publication-fixed-4s-v1; max_hold_ms=4000", output);
                Assert.Contains("carrier-frontier-late-publication=closed-within-bound; physical-copies=3; root=unchanged", output);
            }
        }
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-carrier-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
            Assert.Equal("3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699",
                Convert.ToHexString(SHA256.HashData(released)).ToLowerInvariant());
        }
    }
}
