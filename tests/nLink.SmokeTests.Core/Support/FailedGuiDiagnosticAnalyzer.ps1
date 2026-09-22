param([string]$Mode,[string]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$repo=$env:NLINK_FAILED_DIAGNOSTIC_TEST_REPO
$null=Get-Command Get-FileHash -ErrorAction Stop
. (Join-Path $repo 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
if($env:NLINK_RETAINED_SOURCE_REQUIRED -cne '1'){throw 'Required-source selector missing'}
$owner=Open-FileTransferRetainedSourceOwner -Name $env:NLINK_RETAINED_SOURCE_REQUEST `
    -RequestId ([guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID) -Path $LogPath `
    -Run $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -Scenario $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
if($null -eq $owner){throw 'Owned source attachment failed'}
$reader=$null
try {
    $reader=$owner.TryOpenSource()
    if($null -eq $reader){throw 'Owned reader missing'}
    foreach($line in $reader.ReadLines()) { }
    if(-not $owner.OwnsCompletedSource($reader)){throw 'Owned source was not read to completion'}
    [IO.File]::AppendAllText((Join-Path $ArtifactDir 'stub-launches.txt'),"started`n")
} finally {if($null -ne $reader){$reader.Dispose()};$owner.Dispose()}
if($env:NLINK_FAILED_DIAGNOSTIC_TEST_OUTCOME -ceq 'timeout'){[Threading.Thread]::Sleep(30000)}
if($env:NLINK_FAILED_DIAGNOSTIC_TEST_OUTCOME -ceq 'failed'){exit 42}
# Clearly synthetic proof artifacts, exercising only canonical receipt/report
# validation. No analyzer/coordinator evaluation or live input is used here.
foreach($name in @('filetransfer-retained-analysis-stage-diagnostics.json','filetransfer-retained-compact-summary.json',
    'filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','stability-gates-summary.txt')) {
    [IO.File]::WriteAllText((Join-Path $ArtifactDir $name),'synthetic fixture')
}
$snapshot='{"Mode":"SnapshotV1Shadow","ContractVersion":1,"CatalogVersion":"snapshot-authority-v1","CatalogHash":"synthetic-catalog","BoundaryHash":"synthetic-boundary","ArtifactEvidenceVerdict":"pass"}'
foreach($name in @('filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json')) {
    [IO.File]::WriteAllText((Join-Path $ArtifactDir $name),$snapshot)
}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-parity-summary.json'),'{"Verdict":"pass"}')
exit 0
