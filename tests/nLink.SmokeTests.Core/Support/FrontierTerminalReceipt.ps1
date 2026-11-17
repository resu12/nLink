$terminalCase=$pipeCase.Substring('terminal-'.Length)
$usingReports=$terminalCase.StartsWith('reports-')
if($usingReports) {$terminalCase=$terminalCase.Substring('reports-'.Length)}
$persistenceCase=if($terminalCase.StartsWith('persist-')) {$terminalCase.Substring('persist-'.Length)} else {''}
$terminalProofIncomplete=$terminalCase -ceq 'proof-incomplete' -or $persistenceCase -ceq 'proof-incomplete'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
Set-Variable -Name terminalCap -Option Private -Value ($joined.TryBeginTerminalReceipt())
if($null -eq $terminalCap) {throw 'Terminal custody unavailable'}
$sourceHandle=$primary.GetType().GetField('frozenSource',$parentFlags).GetValue($primary).SafeFileHandle
$sourcePosition=[PrimaryPositionProbe]::Position($sourceHandle)
if($terminalCase -ceq 'foreign-source') {
    $copy=$parentClone.Invoke($primary,$null)
    if($terminalCap.PrepareReceipt([ordered]@{},'unused',$copy) -or $terminalCap.PrepareReceipt([ordered]@{},'unused')) {throw 'Foreign/missing source request acquired terminal preparation'}
}
if($terminalCase -ceq 'source-reader') {
    $copy=$parentClone.Invoke($primary,$null)
    if($null -ne $copy.TryOpenTerminalSource($completion.Outputs[3].Path) -or $null -ne $primary.TryOpenTerminalSource($stdoutPath)) {throw 'Foreign source reader accepted'}
    $read=$primary.TryOpenTerminalSource($completion.Outputs[3].Path)
    if($null -eq $read -or $read.CanSeek -or $read.CanWrite) {throw 'Original terminal source reader unavailable/writable'}
    $copy=$parentClone.Invoke($read,$null); $copy.Dispose()
    if($copy.CanRead -or -not $read.CanRead) {throw 'Copied source reader acquired/retired original'}
    $buffer=[byte[]]::new(1); if($read.Read($buffer,0,1) -ne 1) {throw 'Terminal source read failed'}
    $read.Dispose(); if($read.CanRead) {throw 'Disposed source reader remained active'}
}
foreach($item in @(@($stdoutPath,'filetransfer-retained-analysis.stdout.txt',$nativeProcess.StandardOutput.CurrentEncoding),
    @($stderrPath,'filetransfer-retained-analysis.stderr.txt',$nativeProcess.StandardError.CurrentEncoding))) {
    $stream=[IO.FileStream]::new($item[0],[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader=[IO.StreamReader]::new($stream,$item[2],$true)
    try {$rendered=$reader.ReadToEnd()} finally {$reader.Dispose()}
    if($usingReports) {
        $task=if($item[1] -ceq 'filetransfer-retained-analysis.stdout.txt') {$reportRun.Stdout} else {$reportRun.Stderr}
        $originalReport=$task.GetAwaiter().GetResult()
        if($originalReport -cne $rendered) {throw 'Original report task differs from native-captured bytes'}
        $rendered=$originalReport
    }
    Set-Content -LiteralPath (Join-Path $Directory $item[1]) -Value $rendered -Encoding UTF8
}
foreach($name in @('filetransfer-retained-analysis-diagnostics.json','filetransfer-retained-analysis-stage-diagnostics.json',
    'filetransfer-retained-compact-summary.json','filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','stability-gates-summary.txt')) {
    Set-Content -LiteralPath (Join-Path $Directory $name) -Value 'original-controlled-report' -Encoding UTF8
}
switch($terminalCase) {
    'stdout-changed' {Add-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis.stdout.txt') -Value 'changed' -Encoding UTF8}
    'stderr-changed' {Add-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis.stderr.txt') -Value 'changed' -Encoding UTF8}
    'catalog-changed' {
        $path=Join-Path $Directory 'filetransfer-coordinator-snapshot-full-summary.json'
        $value=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; $value.CatalogHash='0'*64
        $value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8
    }
    'copied' {
        $copy=$parentClone.Invoke($joined,$null)
        if($null -ne $copy.TryBeginTerminalReceipt()) {throw 'Copied join acquired terminal custody'}
        $copy=$parentClone.Invoke($terminalCap,$null)
        if($copy.PrepareReceipt([ordered]@{},'unused') -or $null -ne $copy.Take($null)) {throw 'Copied terminal custody acquired writer'}
        $copy.Dispose()
    }
    'repeat-begin' {if($null -ne $joined.TryBeginTerminalReceipt()) {throw 'Terminal begin replayed'}}
    'budget' {
        $watch=$terminalCap.GetType().GetField('work',$parentFlags).GetValue($terminalCap)
        $elapsed=@($watch.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    'preexisting' {Set-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis-attempt-receipt.json') -Value 'preserve-existing' -Encoding UTF8}
}
$script:terminalAtomic=(Get-Command Write-FileTransferEvidenceAtomicJson).ScriptBlock
$script:terminalHash=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock
$script:terminalLines=(Get-Command Get-FileTransferEvidenceLineCount).ScriptBlock
$script:terminalWrites=0; $script:terminalSourceHashes=0; $script:terminalSourceCounts=0; $script:terminalForgedCalls=0
$script:terminalSourcePath=$completion.Outputs[3].Path
function Write-FileTransferEvidenceAtomicJson {
    param([string]$Path,$Value)
    foreach($name in @('FrontierTerminalCustody','frontierReceiptPrepared','frontierReceiptRecorded','terminalCap','localReceiptReturn')) {
        if($null -ne (Get-Variable -Name $name -ErrorAction SilentlyContinue)) {throw ('Private terminal custody leaked: '+$name)}
    }
    $script:terminalWrites++
    if($terminalCase -ceq 'write-fails') {throw 'injected_terminal_write_failure'}
    & $script:terminalAtomic -Path $Path -Value $Value
    if($terminalCase -ceq 'dictionary-mutation') {$Value.status='forged-after-write'}
}
function Get-FileTransferEvidenceSha256 {
    param([string]$Path,$OwnedSource=$null)
    if($Path -ceq $script:terminalSourcePath) {$script:terminalSourceHashes++}
    & $script:terminalHash -Path $Path -OwnedSource $OwnedSource
}
function Get-FileTransferEvidenceLineCount {
    param([string]$Path,$OwnedSource=$null)
    if($Path -ceq $script:terminalSourcePath) {$script:terminalSourceCounts++}
    & $script:terminalLines -Path $Path -OwnedSource $OwnedSource
}
function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Terminal gate must not evaluate authority'}
$preparationForReceipt=Get-Variable sourcePreparation -ValueOnly -ErrorAction SilentlyContinue
if($null -ne $preparationForReceipt) {
    $terminalOwner=$prepareOwner; $terminalInputHash=$preparationForReceipt.InputSha256; $terminalInputLines=$preparationForReceipt.InputLineCount
} else {
    $terminalOwner='Wrapper'; $terminalInputHash=$hashRead.Sha256; $terminalInputLines=$completion.Outputs[3].LineCount
}
$args=@{ArtifactDir=$Directory;Owner=$terminalOwner;AttemptId=$primary.RequestId.ToString('N');AcceptanceRunId='frontier-gui-run';ScenarioRunId='frontier-gui-scenario';
    LogPath=$completion.Outputs[3].Path;InputSha256Before=$terminalInputHash;InputLineCountBefore=$terminalInputLines;
    LiveRouteProofMode='RegularActivationCycle';CompactRequested=$true;ProcessId=$nativeProcess.Id;StartedUtc=$terminalStarted;EndedUtc=[DateTimeOffset]::UtcNow.ToString('O');
    TimedOut=$false;ExitCodeAvailable=$true;ExitCode=0;CompletedProofArtifacts=(-not $terminalProofIncomplete);FrontierTerminalCustody=$terminalCap;FrontierSourceRequest=$primary}
if($terminalCase -ceq 'source-mismatch') {$args.InputSha256Before='0'*64}
if($terminalCase -ceq 'pid-mismatch') {$args.ProcessId++}
if($terminalCase -ceq 'route-mismatch') {$args.LiveRouteProofMode='other-route'}
if($terminalCase -ceq 'no-hook') {$args.FrontierTerminalCustody=$null}
if($terminalCase -ceq 'forged-custody') {
    $forged=[pscustomobject]@{}
    foreach($method in @('PrepareReceipt','RecordWrittenReceipt','Dispose')) {$forged | Add-Member ScriptMethod $method {$script:terminalForgedCalls++; throw 'Forged terminal method executed'}}
    $args.FrontierTerminalCustody=$forged
}
$returned=$null; $failure=''
try {$returned=Write-FileTransferRetainedAnalysisAttemptReceipt @args} catch {$failure=$_.Exception.Message}
$expectedFailure=$terminalCase -cin @('write-fails','preexisting')
if((-not [string]::IsNullOrEmpty($failure)) -ne $expectedFailure) {throw ('Original terminal write outcome changed: '+$failure)}
if($terminalCase -ceq 'return-copy') {$returned=$returned | ConvertTo-Json -Depth 8 | ConvertFrom-Json}
if($terminalCase -ceq 'return-mutation') {$returned.status='proof_incomplete'}
if($terminalCase -ceq 'return-property') {$returned | Add-Member NoteProperty status $returned.status -Force}
$accepted=$terminalCap.Take($returned)
$expected=$persistenceCase.Length -gt 0 -or $terminalCase -cin @('valid','proof-incomplete','copied','repeat-take','parent-cancel','pin-after-take','source-reader','foreign-source')
if(($null -ne $accepted) -ne $expected) {throw ('Terminal take mismatch: '+$terminalCase+' '+$terminalCap.OmissionReason)}
if($null -ne $accepted) {
    $status=if($terminalProofIncomplete) {'proof_incomplete'} else {'completed'}
    if(-not $accepted.IsCurrent -or $accepted.Status -cne $status -or $accepted.LiveProofEligible -or $accepted.NativeWriterCustodyEstablished -or $accepted.PublicationCompletionIssued) {throw 'Terminal binding changed status/authority'}
    if($terminalCase -ceq 'copied') {if($parentClone.Invoke($accepted,$null).IsCurrent) {throw 'Copied terminal receipt acquired original'}}
    if($terminalCase -ceq 'repeat-take' -and $null -ne $terminalCap.Take($returned)) {throw 'Terminal receipt replayed'}
    if($terminalCase -ceq 'parent-cancel') {
        $signal=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed'); try {[void]$signal.Set()} finally {$signal.Dispose()}
    }
    if($terminalCase -ceq 'pin-after-take') {$retained[0].GetType().GetField('file',$parentFlags).GetValue($retained[0]).Dispose()}
    if($terminalCase -cin @('repeat-take','parent-cancel','pin-after-take') -and $accepted.IsCurrent) {throw 'Revoked terminal receipt remained current'}
}
$count=if($terminalCase -ceq 'preexisting') {0} else {1}
if($script:terminalWrites -ne $count -or $script:terminalSourceHashes -ne $count -or $script:terminalSourceCounts -ne $count -or $script:terminalForgedCalls) {throw 'Terminal gate added write/source traversal or invoked forged custody'}
if([PrimaryPositionProbe]::Position($sourceHandle) -ne $sourcePosition) {throw 'Terminal source passes moved original native position'}
if($persistenceCase.Length -gt 0) {. (Join-Path $PSScriptRoot 'FrontierTerminalPersistence.ps1')}
$primary.Dispose(); if($null -ne $accepted -and $accepted.IsCurrent) {throw 'Disposed terminal request retained acceptance'}
'frontier-terminal-receipt='+$terminalCase
