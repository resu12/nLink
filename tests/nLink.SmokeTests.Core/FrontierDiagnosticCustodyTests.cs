namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierDiagnosticCustodyTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("valid")]
    [InlineData("open-pin")]
    [InlineData("retained-text")]
    [InlineData("supported-failure")]
    [InlineData("unexpected-resolver")]
    [InlineData("unexpected-resolution")]
    [InlineData("unexpected-witness")]
    public async Task DiagnosticCleanup_WritesObservationWithoutParentHelpersAndRejectsInvalidState(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-diagnostic-probe-tests-20260928", fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$ArtifactDir,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            Add-Type @'
            public sealed class DiagnosticProbeFixture {
                private bool resolutionAttempted, witnessesAttempted;
                public DiagnosticProbeFixture(bool resolution, bool witness) {resolutionAttempted=resolution; witnessesAttempted=witness;}
            }
            '@
            if(Get-Command Write-FileTransferEvidenceAtomicJson -ErrorAction SilentlyContinue) {throw 'Parent helper unexpectedly loaded'}
            $nativePrimaryCustody=[DiagnosticProbeFixture]::new(($Fault -ceq 'unexpected-resolution'),($Fault -ceq 'unexpected-witness'))
            $script:diagnosticObservation=[ordered]@{Fixture='deterministic-cleanup-only'}
            $script:diagnosticPins=@([pscustomobject]@{IsClosed=$Fault -cne 'open-pin'})
            $script:diagnosticPayload=[pscustomobject]@{Text=$(if($Fault -ceq 'retained-text') {'retained'} else {$null})}
            $script:diagnosticResolverCalls=if($Fault -ceq 'unexpected-resolver') {1} else {0}
            $script:diagnosticChecked=$false
            $verdict='INCONCLUSIVE'; $gateStatus='fail'
            $firstCompactProofChainFailure=if($Fault -ceq 'supported-failure') {
                [pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing'}
            } else {$null}
            $failure=''
            try {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticPrimaryProbe.ps1') -Stage Closed}
            catch {$failure=$_.Exception.Message}
            $observed=Get-Content -LiteralPath (Join-Path $ArtifactDir 'diagnostic-primary-observation.json') -Raw | ConvertFrom-Json
            if($observed.Fixture -cne 'deterministic-cleanup-only' -or $observed.PrimaryVerdict -cne 'INCONCLUSIVE' -or
                $observed.GateStatus -cne 'fail') {throw 'Observation replaced original result'}
            $expected=switch($Fault) {
                'valid' {''}
                {$_ -cin @('open-pin','retained-text')} {'Diagnostic primary retained native handles or payload text'}
                'supported-failure' {'Positive diagnostic receipt selected an unresolved primary failure; preserve original result for review'}
                default {'Ineligible primary failure invoked diagnostic witness selection'}
            }
            if($failure -cne $expected -or $script:diagnosticChecked -ne ($Fault -ceq 'valid')) {throw ('Cleanup gate changed: '+$failure)}
            'diagnostic-cleanup-ok'
            """, [root, directory, fault], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("diagnostic-cleanup-ok", result.Stdout);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("capture", false)]
    [InlineData("closed", false)]
    [InlineData("both", false)]
    [InlineData("original", false)]
    [InlineData("none", true)]
    public async Task DiagnosticGuards_PreserveOriginalBranchReportsAndCleanup(string fault, bool eligible)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Fault,[string]$EligibleText)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticProbeGuards.ps1')
            $original=Get-AttemptFunction (Read-AttemptAst (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')) 'Write-FileTransferCompactRetainedAnalysisArtifacts'
            $guarded=Add-FrontierDiagnosticProbeGuards $original
            $tokens=$null; $errors=$null
            $null=[Management.Automation.Language.Parser]::ParseInput($guarded,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw ($errors | Out-String)}
            foreach($operation in @('TryCapturePayloads','Get-FileTransferCoordinatorSnapshotEvaluation','Resolve-FileTransferNativeFrontierWitness')) {
                if([regex]::Matches($guarded,[regex]::Escape($operation)).Count -ne [regex]::Matches($original,[regex]::Escape($operation)).Count) {throw 'Probe introduced an evaluation or capture'}
            }
            $definition=@'
            function Invoke-OriginalForTest {
                $private:nativePrimaryCustody=$script:fakeCustody
                try {
                    # Only an admitted bounded private context retains these array references.
                    if($EligibleText -ceq 'True') {
                        $private:nativeWitness = Resolve-FileTransferNativeFrontierWitness
                    }
                    $script:reports++
                    if($Fault -ceq 'original') {throw 'original report failed'}
                    return 'original-result'
                } finally {
                    try { $private:nativePrimaryCustody.Dispose() } catch { }
                }
            }
            '@
            $definition=Add-FrontierDiagnosticProbeGuards $definition
            $probe=". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticPrimaryProbe.ps1')"
            $definition=$definition.Replace($probe,'Invoke-ProbeForTest')
            $script:diagnosticProbeError=$null; $script:diagnosticResolverCalls=0
            $script:reports=0; $script:disposed=0; $script:resolutions=0
            $script:stages=[Collections.Generic.List[string]]::new()
            function Invoke-ProbeForTest([string]$Stage) {
                $script:stages.Add($Stage)
                if($Stage -ceq 'Closed' -and $script:disposed -ne 1) {throw 'Probe preceded original disposal'}
                if($Fault -ceq 'both' -or $Stage -ieq $Fault) {throw ('probe-'+$Stage)}
            }
            function Resolve-FileTransferNativeFrontierWitness {$script:resolutions++; return $null}
            $script:fakeCustody=[pscustomobject]@{}
            $script:fakeCustody | Add-Member ScriptMethod Dispose {$script:disposed++}
            Invoke-Expression $definition
            $originalFailure=''; $value=$null
            try {$value=Invoke-OriginalForTest} catch {$originalFailure=$_.Exception.Message}
            if($script:reports -ne 1 -or $script:disposed -ne 1 -or ($script:stages -join ',') -cne 'Capture,Closed') {throw 'Original reports/cleanup lost'}
            if($Fault -ceq 'original') {if($originalFailure -cne 'original report failed') {throw 'Original error masked'}}
            elseif($originalFailure -or $value -cne 'original-result') {throw 'Probe replaced original outcome'}
            $expected=if($Fault -cin @('capture','both')) {'probe-Capture'} elseif($Fault -ceq 'closed') {'probe-Closed'} else {''}
            $actual=if($null -eq $script:diagnosticProbeError) {''} else {$script:diagnosticProbeError.Exception.Message}
            if($actual -cne $expected) {throw 'First probe error lost'}
            $calls=if($EligibleText -ceq 'True') {1} else {0}
            if($script:diagnosticResolverCalls -ne $calls -or $script:resolutions -ne $calls) {throw 'Original witness gate changed'}
            'diagnostic-guards-ok'
            """, [FindRepoRoot(), fault, eligible.ToString()], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("diagnostic-guards-ok", result.Stdout);
    }

    [Theory]
    [InlineData("writer-valid", "", "", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-schedule", "writer-valid", "", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-schedule", "", "valid", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-schedule", "", "", "canonical-fallback")]
    public async Task DiagnosticProfile_RejectsOtherTransferOrPairHooks(string scenario, string wrapper, string parent, string primary)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), "unused", scenario, wrapper, parent, primary, "frontier-schedule"], "pwsh");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Diagnostic custody requires the separate small profile", result.Stderr);
    }
}
