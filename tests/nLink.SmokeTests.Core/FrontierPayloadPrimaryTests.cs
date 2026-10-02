namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPayloadPrimaryTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh")]
    public async Task CanonicalFallback_RequiresOriginalHandoffsBeyondOldOriginCap(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "origin-capacity-implementation-20260927", host + "-canonical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-carrier-full-gui-valid", "writer-valid", "valid", "canonical-fallback"], host, timeout: TimeSpan.FromMinutes(10));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-destination-gui-adoption-ok", gui);
        var error = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stderr.log"));
        Assert.True(gui.Contains("attempt-payload-native-writer-handoff-ok", StringComparison.Ordinal), gui + error);
        Assert.DoesNotContain("payload-writer-processing-failed", error);
        // Admit overlapping references without trimming any original output.
        // Any later native/work/primary refusal must remain a failed integration.
        var retained = Assert.Single(Directory.GetFiles(directory, "frontier-cursor-*-retained.tmp"));
        var writer = Assert.Single(Directory.GetFiles(directory, "frontier-cursor-*-writer.tmp"));
        var semantic = Assert.Single(Directory.GetFiles(directory, "frontier-cursor-*-semantic.tmp"));
        var sourceLines = File.ReadLines(Path.Combine(directory, "live-operational-log", "nlink.log")).Count();
        var counts = new[] { File.ReadLines(retained).Count(), File.ReadLines(writer).Count(), File.ReadLines(semantic).Count() };
        Assert.Equal(sourceLines, counts[1]);
        Assert.Equal(sourceLines - 2, counts[0]);
        Assert.All(counts, count => Assert.InRange(count, 0, 262144));
        Assert.InRange(counts.Sum(), 262145, 786432);
        string? identity = null;
        foreach (var role in new[] { "helpee", "helper" })
        {
            var path = Assert.Single(Directory.GetFiles(Path.Combine(directory, "process-output"), role + "-*.stdout.log"));
            var text = await File.ReadAllTextAsync(path);
            var line = Assert.Single(text.Split('\n').Where(line => line.StartsWith("cross-process-nkn-", StringComparison.Ordinal)));
            Assert.StartsWith("cross-process-nkn-" + role + "=268435456; route=post_tuna_fallback_v6; sha256=", line);
            var pair = line[line.IndexOf("; sha256=", StringComparison.Ordinal)..].Trim();
            if (identity is not null) Assert.Equal(identity, pair);
            identity = pair;
        }
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-carrier-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-payload-canonical-fallback-stopped-before-route-acceptance", result.Stdout);
        var analysis = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis.stdout.txt"));
        Assert.Contains("original parent analyzer evaluations=3 nativeReads=1", analysis);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task CanonicalSource_PreservesActualCoordinatorEvidenceWithoutInventingWitness(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "payload-canonical-source-20260927", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-gui-valid", "writer-valid", "valid", "canonical-source"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("attempt-payload-canonical-source-stopped-before-route-acceptance", result.Stdout);
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis.stdout.txt"));
        Assert.Contains("original parent analyzer evaluations=3 nativeReads=1", output);
        Assert.Contains("attempt-payload-canonical-source; selected_failure=", output);
        Assert.Contains("diagnostic_witness=unavailable", output);
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "foreign-completion")]
    [InlineData("pwsh", "pair-retired")]
    [InlineData("pwsh", "owner-retired")]
    [InlineData("powershell", "evaluation-extended")]
    [InlineData("pwsh", "evaluation-extended")]
    [InlineData("powershell", "evaluation-expired")]
    [InlineData("pwsh", "evaluation-expired")]
    [InlineData("powershell", "evaluation-channel-retired")]
    [InlineData("pwsh", "evaluation-channel-retired")]
    public async Task OriginalAnalyzer_BindsActualPairOnlyToOriginalEvaluation(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "payload-primary-20260927", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-gui-valid", "writer-valid", "valid", fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-payload-primary-analysis=" + fault, result.Stdout);
        Assert.Contains("attempt-payload-primary-proof-incomplete-retained", result.Stdout);
        if (fault.StartsWith("evaluation-", StringComparison.Ordinal))
        {
            var analysis = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis.stdout.txt"));
            Assert.Contains("attempt-payload-evaluation-budget=" + fault, analysis);
            Assert.Contains("original parent analyzer evaluations=3 nativeReads=1", analysis);
        }
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
    }

    [Fact]
    public async Task PairCurrentness_UsesRetainedAllowanceWithoutExtendingProducerAttachment()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalFrontierPrimaryTransfer.cs"));
        var start = source.IndexOf("private void AttachTransferPair", StringComparison.Ordinal);
        var guard = source.IndexOf("private bool TransferPairCurrent", start, StringComparison.Ordinal);
        var end = source.IndexOf("private void CloseTransferPair", guard, StringComparison.Ordinal);
        Assert.Contains("!ResourceCurrent || !transferChannel.MarkPulled()", source[start..guard]);
        Assert.DoesNotContain("ResourceCurrentWithin", source[start..guard]);
        Assert.Contains("RetainedResourceCurrent && transferPair != null && transferPair.IsCurrent && transferChannel != null && transferChannel.Current && transferChannel.State == 2", source[guard..end]);
        Assert.Equal(1, source.Split("RetainedResourceCurrent").Length - 1);
    }

    [Fact]
    public async Task OriginalBinding_PreservesReferenceGuardsWithoutReevaluatingAnalyzer()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalFrontierPrimaryTransfer.cs"));
        var start = source.IndexOf("public CanonicalTransferEvaluation TryBindTransferPair(", StringComparison.Ordinal);
        var end = source.IndexOf("internal bool TransferCurrent(", start, StringComparison.Ordinal);
        var body = System.Text.RegularExpressions.Regex.Replace(source[start..end], @"\s+", " ");
        Assert.Contains("if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) || transferBindingAttempted || !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView)) return null;", body);
        Assert.Contains("transferBindingAttempted = true; inputs.work.Start();", body);
        Assert.Contains("if (!Current() || !inputs.TransferPairCurrent) return null;", body);
        Assert.Contains("finally { inputs.work.Stop(); }", body);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"(?<!\w)Current\(\)"));
        foreach (var operation in new[] { "BeginEvaluation(", "RecordFull(", "RecordCompact(", "VisitEvaluation(", "Restart(", "Reset(", "new Stopwatch" })
            Assert.DoesNotContain(operation, body);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesOriginalTransferBindingWithoutPublicationAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
            $custody=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]
            $bind=$custody.GetMethod('TryBindTransferPair')
            $result='NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+CanonicalTransferEvaluation' -as [type]
            if($null -eq $bind -or $null -eq $result) {throw 'Production original transfer binding missing'}
            if($bind.ReturnType -ne $result -or $bind.GetParameters().Count -ne 6 -or $result.GetConstructors().Count -ne 0) {throw 'Original transfer binding contract changed'}
            $unowned=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($result)
            foreach($name in @('LiveProofEligible','TransferIntegrityEstablished','PublicationCompletionIssued')) {
                $property=$result.GetProperty($name)
                if($null -eq $property -or $property.CanWrite -or $property.GetValue($unowned,$null)) {throw 'Transfer association gained publication authority'}
            }
            if($null -ne $custody.GetMethod('TryCheckCanonicalSamePairWitnessForTest')) {throw 'Test-only same-pair API leaked into default loader'}
            if($null -ne ('NLink.Tools.TransferPairForTest' -as [type]) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody].GetMethod('TryBindTransferPairForTest')) {throw 'Actual transfer evaluation leaked into default loader'}
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            if($null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetField('PrimarySourceRejectionForTest',$flags) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetMethod('PrimarySourceCheckForTest',$flags) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetMethod('GetPrimaryWorkProfileForTest',$flags) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetField('SourceReadProbeForTest',$flags) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetField('OriginBatchProbeForTest',$flags) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetField('PrimaryOriginBatchCallsForTest',$flags) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+Worker].GetField('PrimaryBaseRejectionForTest',$flags)) {throw 'Primary currentness diagnostics leaked into default loader'}
            'payload-primary-default-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("payload-primary-default-ok", result.Stdout);
    }
}
