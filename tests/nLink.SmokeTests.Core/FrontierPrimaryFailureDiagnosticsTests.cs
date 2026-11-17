namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPrimaryFailureDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task FailureCapture_UsesExactProfilesAndOnlyPassiveRetainedDiagnostics(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryWorker.ps1')
            $capture=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Clauses[0].Item1.Extent.Text.StartsWith('$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cin ') -and
                $n.Clauses[0].Item2.Extent.Text.Contains("canonical-source-failure; reason=")},$true))
            if($capture.Count -ne 1) {throw 'Failure capture selector is ambiguous'}
            Add-Type -TypeDefinition @'
            using System;
            using System.Diagnostics;
            public sealed class FailureDiagnosticOwner {
                internal readonly string PrimaryBaseRejectionForTest = "parent-exit";
                public bool IsCurrent { get { throw new Exception("extra owner currentness"); } }
            }
            public sealed class FailureDiagnosticInputs {
                internal readonly Stopwatch work = new Stopwatch();
                internal readonly string PrimarySourceRejectionForTest = "resource:base";
                internal readonly string PrimarySourceSnapshotForTest = "work_ms=123; lines=5; units=456; started=True; ended=False";
                internal readonly string PrimarySourceCloseForTest = "ReadSource";
                internal readonly string PrimarySourceExceptionForTest = "System.IO.IOException:frontier_source_not_current";
                internal readonly string PrimarySourceCloseSnapshotForTest = "work_ms=124; lines=5; units=456; started=True; ended=False";
                public int ProfileCalls;
                public bool IsCurrent { get { throw new Exception("extra input currentness"); } }
                internal string GetPrimaryWorkProfileForTest() { ProfileCalls++; return "primary-work-profile; retained-control"; }
                public bool ClockUnchanged { get { return work.ElapsedTicks == 0 && !work.IsRunning; } }
            }
            public sealed class FailureDiagnosticSource {
                internal readonly FailureDiagnosticInputs frontier;
                internal readonly FailureDiagnosticOwner worker = new FailureDiagnosticOwner();
                public int LinesRead { get { return 5; } }
                public bool IsCurrent { get { throw new Exception("extra source currentness"); } }
                public FailureDiagnosticSource(FailureDiagnosticInputs inputs) { frontier = inputs; }
            }
            '@
            function Write-FrontierPrimaryCheckpoint {param($Stage,$Diagnostic) $script:checkpointCalls++}
            $cases=@(
                @('canonical-source','native'), @('canonical-fallback','native'),
                @('canonical-tail-late-publication','native'), @('canonical-tail-late-publication','absent'),
                @('canonical-tail-late-publication','non-native'), @('valid','native'),
                @('diagnostic-control','native'), @('CANONICAL-TAIL-LATE-PUBLICATION','native'),
                @('unknown','native'), @('','native'))
            foreach($case in $cases) {
                $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$case[0]
                $observedInputs=[FailureDiagnosticInputs]::new()
                $script:observedSource=if($case[1] -ceq 'absent') {$null}
                    elseif($case[1] -ceq 'non-native') {[FailureDiagnosticSource]::new($null)}
                    else {[FailureDiagnosticSource]::new($observedInputs)}
                $script:checkpointCalls=0
                $text='try {throw $script:originalFailure} catch {$failure=$_.Exception.Message; throw}'
                $originalText=$text
                Invoke-Expression $capture[0].Extent.Text
                $enabled=$case[0] -cin @('canonical-source','canonical-fallback','canonical-tail-late-publication')
                if(($text -cne $originalText) -ne $enabled) {throw 'Failure capture profile widened or omitted'}
                $script:originalFailure=[IO.IOException]::new('frontier_source_not_current')
                $writer=[IO.StringWriter]::new()
                $oldError=[Console]::Error
                $caught=$null; $failure=''
                try {
                    [Console]::SetError($writer)
                    try {& ([scriptblock]::Create($text))} catch {$caught=$_}
                } finally {[Console]::SetError($oldError)}
                $diagnostic=$writer.ToString(); $writer.Dispose()
                if($null -eq $caught -or -not [object]::ReferenceEquals($caught.Exception,$script:originalFailure)) {throw 'Original exception identity changed'}
                $emitted=$enabled -and $case[1] -ceq 'native'
                if($emitted) {
                    $lines=@($diagnostic -split '\r?\n' | Where-Object {$_})
                    if($lines.Count -ne 4 -or
                        $lines[0] -cne 'canonical-source-failure; reason=frontier_source_not_current; active_ms=0; lines_read=5' -or
                        $lines[1] -cne 'primary-source-currentness; rejected=resource:base; base=parent-exit; close=ReadSource; exception=System.IO.IOException:frontier_source_not_current; work_ms=123; lines=5; units=456; started=True; ended=False' -or
                        $lines[2] -cne 'primary-source-first-close; work_ms=124; lines=5; units=456; started=True; ended=False' -or
                        $lines[3] -cne 'primary-work-profile; retained-control') {throw ('Retained diagnostics changed: '+$diagnostic)}
                } elseif($diagnostic) {throw 'Missing/foreign/noncanonical source gained diagnostics'}
                if(-not $observedInputs.ClockUnchanged -or $observedInputs.ProfileCalls -ne [int]$emitted) {throw 'Logging changed original work or repeated its profile'}
                $expectedCheckpoints=if($case[0] -ceq 'canonical-fallback') {1+[int]$emitted} else {0}
                if($script:checkpointCalls -ne $expectedCheckpoints) {throw 'Canonical-tail gained checkpoint side effects'}
            }
            'primary-failure-capture-controls-ok; cases=10'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("primary-failure-capture-controls-ok; cases=10", result.Stdout);
    }
}
