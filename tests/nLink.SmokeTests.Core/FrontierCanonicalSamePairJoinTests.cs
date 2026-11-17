namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FrontierCanonicalSamePairJoinTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh", "scope", 29)] [InlineData("powershell", "scope", 29)]
    [InlineData("pwsh", "ownership", 16)] [InlineData("powershell", "ownership", 16)]
    public async Task OriginalHelperRejectsForeignScopeAndUnownedJoins(string host, string mode, int controls)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierCanonicalSamePairControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, mode], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains($"canonical-same-pair-{mode}-controls={controls}", result.Stdout);
    }

    [Theory]
    [InlineData("pwsh")] [InlineData("powershell")]
    public async Task NormalLoaderExposesSamePairCheckWithoutAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
            $type=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]
            $check=$type.GetMethod('TryCheckCanonicalSamePairWitness')
            if($null -eq $check) {throw 'Production same-pair witness check missing'}
            if($check.ReturnType -ne [bool] -or $check.GetParameters().Count -ne 7) {throw 'Same-pair check contract changed'}
            foreach($name in @('CanonicalTransferEvaluation','NativeWitnessBatch')) {
                $result=$type.Assembly.GetType('NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+'+$name,$true)
                if($result.GetConstructors().Count -ne 0) {throw 'Public custody constructor exposed'}
                $unowned=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($result)
                foreach($flag in @('LiveProofEligible','PublicationCompletionIssued',$(if($name -ceq 'CanonicalTransferEvaluation') {'TransferIntegrityEstablished'} else {'NativeWriterCustodyEstablished'}))) {
                    $property=$result.GetProperty($flag)
                    if($null -eq $property -or $property.CanWrite -or $property.GetValue($unowned,$null)) {throw 'Same-pair check gained authority'}
                }
            }
            if($null -ne $type.GetMethod('TryCheckCanonicalSamePairWitnessForTest') -or
                $null -ne $type.GetField('WitnessResolutionStageForTest',[Reflection.BindingFlags]'Instance,NonPublic')) {throw 'Same-pair diagnostics leaked into ordinary loader'}
            'canonical-same-pair-default-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("canonical-same-pair-default-ok", result.Stdout);
    }

    [Fact]
    public async Task JoinKeepsOriginalBudgetCurrentnessAndDoesNotResolveOrRehashFiles()
    {
        var root = FindRepoRoot();
        var text = await File.ReadAllTextAsync(Path.Combine(root, "tools/FileTransferOps/LocalFrontierCanonicalSamePair.cs"));
        var body = text[text.IndexOf("public bool TryCheckCanonicalSamePairWitness", StringComparison.Ordinal)..];
        Assert.Contains("!Object.ReferenceEquals(transferEvaluation, transfer)", body);
        Assert.Contains("!Object.ReferenceEquals(issued, transfer.Pair) || !OriginalWitnesses(batch)", body);
        Assert.Contains("!batch.HasDiagnosticState || !Object.ReferenceEquals(batch.Payload, receiptPayload)", body);
        Assert.True(body.IndexOf("canonicalSamePairAttempted = true;", StringComparison.Ordinal) >
            body.IndexOf("!Object.ReferenceEquals(batch.Payload, receiptPayload)", StringComparison.Ordinal));
        Assert.Contains("bool accepted = false; inputs.work.Start();", body);
        Assert.Contains("if (!Current() || !inputs.TransferPairCurrent) return false;", body);
        Assert.Contains("accepted = Current() && inputs.TransferPairCurrent;", body);
        Assert.Contains("finally { inputs.work.Stop(); if (!accepted) inputs.Close(); }", body);
        Assert.Equal(2, body.Split("inputs.VisitEvaluation();").Length - 1);
        Assert.DoesNotContain("TryResolve", text); Assert.DoesNotContain("TryCapture", text);
        Assert.DoesNotContain("new Stopwatch", text); Assert.DoesNotContain("Restart", text);
        Assert.DoesNotContain("FileStream", text); Assert.DoesNotContain("File.Read", text);
        Assert.DoesNotContain("ConvertFrom-Json", text);
        Assert.DoesNotContain("BeginEvaluation", text); Assert.DoesNotContain("RecordFull", text);
        Assert.DoesNotContain("RecordCompact", text); Assert.DoesNotContain("TryCommit", text);
    }
}
