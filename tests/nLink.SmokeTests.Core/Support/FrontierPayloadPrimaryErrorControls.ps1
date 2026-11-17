param([string]$RepoRoot,[string]$Case,[string]$CheckpointDirectory='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryGuards.ps1')
Add-Type -TypeDefinition @'
using System;
public sealed class PrimaryErrorHandle {
    public bool Closed { get; set; }
    public int Reads { get; private set; }
    public bool IsClosed { get { Reads++; return Closed; } }
}
public sealed class PrimaryErrorEvaluation {
    public bool Current { get; set; }
    public int Reads { get; private set; }
    public bool IsCurrent { get { Reads++; return Current; } }
}
public sealed class PrimaryErrorCustody : IDisposable {
    public bool FailDispose { get; set; }
    public int Disposals { get; private set; }
    public void Dispose() { Disposals++; if (FailDispose) throw new InvalidOperationException("injected disposal failure"); }
}
'@
$script:primaryPayloadChecked=$false; $script:primaryPayloadBound=$false; $script:primaryPayloadBindError=$null
$script:testCustody=[PrimaryErrorCustody]::new(); $script:testCustody.FailDispose=$Case -ceq 'bind-dispose-failure'
$script:workerPayloadHandles=@([PrimaryErrorHandle]::new(),[PrimaryErrorHandle]::new())
foreach($handle in $script:workerPayloadHandles) {$handle.Closed=$true}
if($Case -ceq 'cleanup-open') {$script:workerPayloadHandles[1].Closed=$false}
$script:transferEvaluation=[PrimaryErrorEvaluation]::new()
$script:transferEvaluation.Current=$Case -cin @('cleanup-open','cleanup-current')
if($Case -ceq 'cleanup-absent') {$script:transferEvaluation=$null}
$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=if($CheckpointDirectory) {'canonical-fallback'} else {'diagnostic-control'}
if($CheckpointDirectory) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryCheckpoints.ps1') $CheckpointDirectory}
$definition=@'
function Invoke-PrimaryErrorControl {
    $private:nativePrimaryCustody=$script:testCustody
    $private:nativePrimaryPair=$null; $private:nativePrimaryPayloads=$null
    try {
        if($Case.StartsWith('bind-')) {
            # Only an admitted bounded private context retains these array references.
        } else {$script:primaryPayloadBound=$true}
    } finally {
        if($null -ne $private:nativePrimaryCustody) {
            try { $private:nativePrimaryCustody.Dispose() } catch { }
        }
    }
}
'@
Invoke-Expression (Add-AttemptPayloadPrimaryProbeGuards $definition)
$caught=$null
try {Invoke-PrimaryErrorControl} catch {$caught=$_}
$expected=switch($Case) {
    'bind-failure' {'Original native evaluator/payload catalog missing'}
    'bind-dispose-failure' {'Original native evaluator/payload catalog missing'}
    'cleanup-open' {'Original evaluation retirement left actual payload handles open'}
    'cleanup-current' {'Original evaluation remained current after retirement'}
    default {''}
}
if(($null -ne $caught) -ne [bool]$expected -or ($expected -and $caught.Exception.Message -cne $expected)) {throw ('Original failure changed: '+$caught)}
if($script:testCustody.Disposals -ne 1) {throw 'Original disposal omitted or repeated'}
$bindFailed=$Case.StartsWith('bind-')
if(($null -ne $script:primaryPayloadBindError) -ne $bindFailed) {throw 'Bind error record was lost or fabricated'}
if($bindFailed -and $caught.ScriptStackTrace -notmatch 'FrontierAttemptPayloadPrimaryProbe.ps1') {throw 'Original Bind stack was replaced'}
foreach($handle in $script:workerPayloadHandles) {if($handle.Reads -ne [int](-not $bindFailed)) {throw 'Cleanup handle observation reordered or repeated'}}
$expectedReads=[int]($Case -cin @('cleanup-pass','cleanup-current'))
if($null -ne $script:transferEvaluation -and $script:transferEvaluation.Reads -ne $expectedReads) {throw 'Cleanup authority observation reordered or repeated'}
if($script:primaryPayloadChecked -ne ($Case -cin @('cleanup-pass','cleanup-absent'))) {throw 'Failed cleanup was accepted or successful cleanup was rejected'}
'primary-binding-error-control='+$Case
