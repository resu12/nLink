param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchWrapper.ps1'
$source=[IO.File]::ReadAllText($path); $ast=Read-AttemptAst $path
$outer=@($ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})
if($outer.Count -ne 1) {throw 'Original wrapper terminal scope ambiguous'}
$start=$source.IndexOf('$script:guiReports=$null;',[StringComparison]::Ordinal)
if($start -lt 0) {throw 'Original wrapper initialization missing'}
$tail=$source.Substring($start,$outer[0].Extent.EndOffset-$start)
# Exercise the actual wrapper tail without a launcher, app, native owner or network.
$tail=Replace-AttemptSeam $tail ". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionWrapperPayloadProbe.ps1') -Stage Finished" 'Invoke-WrapperFollowUp -Stage Finished'
$tail=Replace-AttemptSeam $tail ". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPayloadListenerProbe.ps1') -Stage AfterFinally" 'Invoke-WrapperListenerControl -Stage AfterFinally'
foreach($name in @('NLINK_TEST_ATTEMPT_CASE','NLINK_TEST_PAYLOAD_WRAPPER_CASE','NLINK_TEST_PAYLOAD_PARENT_CASE','NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD')) {
    Set-Item -LiteralPath ('Env:'+$name) -Value ''
}
# Inject the historical error here; the real deadline contract has separate controls.
function Request-TunaGuiScenarioDeadline {throw 'Unexpected attempt GUI deadline'}
Add-Type -TypeDefinition @'
using System;
public sealed class WrapperErrorTask {
    public int CompletionReads, ResultReads;
    public bool IsCompleted { get { CompletionReads++; return true; } }
    public WrapperErrorTask GetAwaiter() { return this; }
    public string GetResult() { ResultReads++; return "attempt-dispatch-gui-ok"; }
}
public sealed class WrapperErrorResult {
    public bool Timeout;
    public int TimeoutReads, AvailableReads, ExitReads;
    public bool TimedOut { get { TimeoutReads++; return Timeout; } }
    public bool ExitCodeAvailable { get { AvailableReads++; return true; } }
    public int ExitCode { get { ExitReads++; return 0; } }
}
public sealed class WrapperErrorRelay : IDisposable {
    public int Disposals;
    public bool Fail;
    public Exception Failure = new InvalidOperationException("original cleanup failure");
    public void Dispose() { Disposals++; if (Fail) throw Failure; }
}
'@
$relay=[WrapperErrorRelay]::new(); $relay.Fail=$Case.EndsWith('cleanup-error',[StringComparison]::Ordinal)
$script:stdout=[WrapperErrorTask]::new();$script:stderr=[WrapperErrorTask]::new();$script:result=[WrapperErrorResult]::new()
$script:result.Timeout=$Case.StartsWith('gate-',[StringComparison]::Ordinal)
$script:invocations=0;$script:followUps=0;$script:listeners=0;$script:originalRecord=$null;$script:originalSnapshot=$null
$script:target=[object]::new();$TimeoutSeconds=210;$ExePath='unused-headless-control';$handoff=$null
if($Case -ceq 'expected-argv') {$env:NLINK_TEST_ATTEMPT_CASE='argv-changed'}
if($Case.StartsWith('listener-',[StringComparison]::Ordinal)) {$env:NLINK_TEST_PAYLOAD_WRAPPER_CASE='listener-watchdog-unwind'}
if($Case -ceq 'primary-probe-error') {$env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD='valid'}
function Invoke-OriginalWrapperError {
    [CmdletBinding()]param()
    $record=[Management.Automation.ErrorRecord]::new([InvalidOperationException]::new('original wrapper failure'),
        'original-wrapper-id',[Management.Automation.ErrorCategory]::InvalidOperation,$script:target)
    $record.ErrorDetails=[Management.Automation.ErrorDetails]::new('original detailed failure')
    $PSCmdlet.ThrowTerminatingError($record)
}
function Invoke-TunaGuiSmokeWithHardDeadline {
    param($RepoRoot,$ArtifactDir,$ExePath,$TimeoutSeconds,$LocalWriterRelay,$FrontierRelayHandoff)
    $script:invocations++
    if($TimeoutSeconds -ne 210 -or -not [object]::ReferenceEquals($LocalWriterRelay,$relay)) {throw 'Original invocation arguments changed'}
    $script:guiLaunches=1;$script:guiReports=[pscustomobject]@{Stdout=$script:stdout;Stderr=$script:stderr}
    try {
        if($Case.StartsWith('primary-',[StringComparison]::Ordinal)) {Invoke-OriginalWrapperError}
        if($Case -ceq 'deadline-missing' -or $Case.StartsWith('listener-',[StringComparison]::Ordinal)) {Request-TunaGuiScenarioDeadline}
        if($Case -ceq 'expected-argv') {$script:guiLaunches=0;throw 'attempt_wrapper_projection_rejected'}
    } catch {
        $script:originalRecord=$_
        $script:originalSnapshot=[pscustomobject]@{exception=$_.Exception;id=$_.FullyQualifiedErrorId;category=$_.CategoryInfo.ToString();
            target=$_.TargetObject;stack=$_.ScriptStackTrace;details=$(if($null -ne $_.ErrorDetails){$_.ErrorDetails.Message}else{''})}
        throw
    }
    return $script:result
}
function Invoke-WrapperFollowUp {param($Stage) $script:followUps++;throw 'secondary completion probe failure'}
function Invoke-WrapperListenerControl {
    param($Stage)
    $script:listeners++
    if($Stage -cne 'AfterFinally' -or $failure -cne 'Unexpected attempt GUI deadline') {throw 'Expected listener unwind changed'}
}
$stderrPath=Join-Path $Directory 'gui-smoke-stderr.log';$lock=$null;$beforeContent=''
if($Case.EndsWith('-readable',[StringComparison]::Ordinal) -or $Case.EndsWith('-unreadable',[StringComparison]::Ordinal)) {
    [IO.File]::WriteAllText($stderrPath,'original stderr evidence',[Text.UTF8Encoding]::new($false))
    $beforeContent=[Convert]::ToBase64String([IO.File]::ReadAllBytes($stderrPath))
    if($Case.EndsWith('-unreadable',[StringComparison]::Ordinal)) {$lock=[IO.File]::Open($stderrPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
}
$caught=$null;$output=@()
try {
    Invoke-Expression ("function Invoke-WrapperErrorControl {`n"+$tail+"`n}")
    try {$output=@(Invoke-WrapperErrorControl)} catch {$caught=$_}
} finally {if($null -ne $lock){$lock.Dispose()}}
if($script:invocations -ne 1 -or $relay.Disposals -ne 1) {throw 'Original invocation/cleanup omitted or repeated'}
if($beforeContent -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($stderrPath)) -cne $beforeContent) {throw 'Existing stderr evidence changed'}
if(!$beforeContent -and (Test-Path -LiteralPath $stderrPath)) {throw 'Missing stderr evidence was fabricated'}
$primary=$Case.StartsWith('primary-',[StringComparison]::Ordinal) -or $Case -ceq 'deadline-missing'
$gate=$Case.StartsWith('gate-',[StringComparison]::Ordinal)
$cleanupOnly=$Case -cin @('success-cleanup-error','listener-cleanup-error')
if($primary) {
    $snapshot=$script:originalSnapshot
    if($null -eq $caught -or -not [object]::ReferenceEquals($caught.Exception,$snapshot.exception) -or
        $caught.FullyQualifiedErrorId -cne $snapshot.id -or $caught.CategoryInfo.ToString() -cne $snapshot.category -or
        -not [object]::ReferenceEquals($caught.TargetObject,$snapshot.target) -or $caught.ScriptStackTrace -cne $snapshot.stack -or
        $(if($null -ne $caught.ErrorDetails){$caught.ErrorDetails.Message}else{''}) -cne $snapshot.details) {throw ('Original ErrorRecord changed: '+$caught)}
} elseif($gate) {
    $expected='Attempt GUI failed:  / '+$(if($Case -ceq 'gate-readable'){'original stderr evidence'}else{''})
    if($null -eq $caught -or $caught.Exception.Message -cne $expected) {throw ('Original gate failure changed: '+$caught)}
} elseif($cleanupOnly) {
    if($null -eq $caught -or -not [object]::ReferenceEquals($caught.Exception.GetBaseException(),$relay.Failure)) {throw 'Successful/handled path hid cleanup failure'}
} else {
    if($null -ne $caught -or $output.Count -ne 1 -or $output[0] -cne 'attempt-dispatch-wrapper-ok') {throw ('Original successful/handled path changed: '+$caught)}
}
if($Case -ceq 'primary-probe-error' -and $script:followUps -ne 1) {throw 'Original completion probe omitted'}
if($script:listeners -ne [int]($Case.StartsWith('listener-',[StringComparison]::Ordinal))) {throw 'Expected listener validation changed'}
$success=$Case -cin @('success','success-cleanup-error')
if($success -and ($script:stdout.CompletionReads -ne 1 -or $script:stderr.CompletionReads -ne 1 -or $script:stdout.ResultReads -ne 1 -or
    $script:stderr.ResultReads -ne 0 -or $script:result.TimeoutReads -ne 1 -or $script:result.AvailableReads -ne 1 -or $script:result.ExitReads -ne 1)) {throw 'Original successful observations reordered or repeated'}
[pscustomobject]@{case=$Case;invocations=$script:invocations;disposals=$relay.Disposals;primaryErrorPreserved=$primary;
    gateFailurePreserved=$gate;cleanupOnlyFailurePreserved=$cleanupOnly;originalErrorId=$(if($script:originalSnapshot){$script:originalSnapshot.id}else{''})}|ConvertTo-Json -Compress
'wrapper-first-error-control='+$Case
