namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierWitnessResolutionDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task CallbackProfilePreservesOriginalDefinitionAndFailure(string host)
    {
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierWitnessCallbackProfileControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("witness-callback-profile-controls=8", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DiagnosticsPreserveResolverControlFlowAndFirstRejection(string host)
    {
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierWitnessResolutionDiagnostics.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("witness-resolution-diagnostics-controls=12", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task AttemptProfileCompilesOriginalResolverWithScalarObserver(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot
            $type=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            if($null -eq $type.GetMethod('ObserveWitnessResolutionForTest',$flags)) {throw 'Actual resolver observer not compiled'}
            foreach($name in @('Stage','Reason','Calls','State','FullCount','CompactCount','WorkTicks','Visits')) {
                $field=$type.GetField(('WitnessResolution'+$name+'ForTest'),$flags)
                if($null -eq $field -or $field.FieldType -notin @([string],[int],[long])) {throw 'Observer retained a non-scalar or omitted field'}
            }
            foreach($name in @('Stages','Callback')) {
                $field=$type.GetField(('WitnessResolution'+$name+'ForTest'),$flags)
                if($null -eq $field -or $field.FieldType -ne [long[][]]) {throw 'Profile counters omitted or non-scalar'}
            }
            if($null -eq $type.GetMethod('RecordWitnessCallbackForTest')) {throw 'Callback counter profile not compiled'}
            'original-witness-observer-profile=compiled'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("original-witness-observer-profile=compiled", result.Stdout);
    }
}
