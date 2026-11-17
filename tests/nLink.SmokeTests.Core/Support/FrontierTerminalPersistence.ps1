$diagnosticPath=Join-Path $Directory 'filetransfer-frontier-terminal-diagnostic.json'
$packetExpected=$transport.CopyPayload()
if($null -eq $packetExpected) {throw 'Original packet unavailable before persistence test'}
switch($persistenceCase) {
    'copied' {
        if($null -ne $parentClone.Invoke($accepted,$null).TryPersistDiagnostic()) {throw 'Copied terminal persisted'}
    }
    'after-cleanup' {$primary.Dispose()}
    'mutation' {$returned.status='changed-after-take'}
    'preexisting' {Set-Content -LiteralPath $diagnosticPath -Value 'preserve-existing' -Encoding UTF8}
    'write-fails' {[void][IO.Directory]::CreateDirectory($diagnosticPath)}
    'budget' {
        $watch=$terminalCap.GetType().GetField('work',$parentFlags).GetValue($terminalCap)
        $elapsed=@($watch.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    'pin-closed' {$retained[0].GetType().GetField('file',$parentFlags).GetValue($retained[0]).Dispose()}
    'cancel' {
        $signal=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed'); try {[void]$signal.Set()} finally {$signal.Dispose()}
    }
}
$saved=$accepted.TryPersistDiagnostic()
$shouldPersist=$persistenceCase -cin @('valid','proof-incomplete','copied','replay','file-closed')
if(($null -ne $saved) -ne $shouldPersist) {throw ('Diagnostic persistence mismatch: '+$persistenceCase)}
if($persistenceCase -cin @('preexisting','write-fails') -and (-not $accepted.IsCurrent -or $returned.status -cne $status)) {throw 'Optional persistence failure changed terminal outcome'}
if($null -ne $saved) {
    if(-not $saved.IsCurrent -or $saved.LiveProofEligible -or $saved.NativeWriterCustodyEstablished -or $saved.PublicationCompletionIssued) {throw 'Persistence acquired authority/lost ownership'}
    if($saved.Output.CreationPath -cne $diagnosticPath -or $saved.Output.ByteCount -gt 6MB) {throw 'Persistence path/cap changed'}
    $clone=$parentClone.Invoke($saved,$null); $clone.Dispose()
    if($clone.IsCurrent -or -not $saved.IsCurrent) {throw 'Copied persistence result acquired/retired original'}
    $capResource=$terminalCap.GetType().GetField('diagnosticFile',$parentFlags).GetValue($terminalCap)
    $file=$capResource.GetType().GetField('file',$parentFlags).GetValue($capResource)
    if(-not $file.CanRead -or $file.CanWrite) {throw 'Persisted evidence native file is writable'}
    if($persistenceCase -ceq 'replay') {
        if($null -ne $accepted.TryPersistDiagnostic() -or $saved.IsCurrent) {throw 'Persistence replay retained/reissued result'}
    }
    if($persistenceCase -ceq 'file-closed') {
        $file.Dispose(); if($saved.IsCurrent) {throw 'Closed diagnostic file retained result'}
    }
}
$primary.Dispose()
if($null -ne $saved) {
    if($saved.IsCurrent -or $accepted.IsCurrent) {throw 'Parent cleanup retained diagnostic capability'}
    $saved.Dispose()
    $bytes=[IO.File]::ReadAllBytes($diagnosticPath)
    if($bytes.Length -ne $saved.Output.ByteCount) {throw 'Persisted byte count changed'}
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$digest=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
    if($digest -cne $saved.Output.Sha256) {throw 'Persisted exact native file hash mismatch'}
    $record=[Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json
    if($record.schemaVersion -ne 1 -or $record.event -cne 'filetransfer_frontier_terminal_diagnostic' -or -not $record.diagnosticOnly -or
        $record.liveProofEligible -or $record.nativeWriterCustodyEstablished -or $record.publicationCompletionIssued -or
        $record.status -cne $status -or $record.attemptId -cne $returned.attemptId -or $record.owner -cne $returned.owner -or
        $record.acceptanceRunId -cne 'frontier-gui-run' -or $record.scenarioRunId -cne 'frontier-gui-scenario' -or
        $record.stdoutReportSha256 -cne $returned.stdout.sha256 -or $record.stderrReportSha256 -cne $returned.stderr.sha256 -or
        $record.analyzerInputSha256After -cne $returned.analyzerInputSha256After -or $record.witnessCount -lt 1 -or $record.witnessCount -gt 64 -or
        $record.packetFormat -cne 'nlink-frontier-diagnostic-v2') {throw 'Diagnostic schema/binding/status mismatch'}
    $packet=[Convert]::FromBase64String($record.packetBase64)
    if($record.packetByteCount -ne $packet.Length -or [Convert]::ToBase64String($packetExpected) -cne $record.packetBase64) {throw 'Original diagnostic packet changed'}
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$digest=[BitConverter]::ToString($sha.ComputeHash($packet)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
    if($record.packetSha256 -cne $digest) {throw 'Diagnostic packet digest mismatch'}
    if($null -ne $accepted.TryPersistDiagnostic()) {throw 'Artifact/cleanup reissued persistence'}
}
elseif($persistenceCase -ceq 'preexisting') {
    if((Get-Content -LiteralPath $diagnosticPath -Raw).Trim() -cne 'preserve-existing') {throw 'Preexisting diagnostic was overwritten'}
}
elseif($persistenceCase -ceq 'write-fails') {
    if(-not [IO.Directory]::Exists($diagnosticPath)) {throw 'Destination directory was changed'}
}
elseif(Test-Path -LiteralPath $diagnosticPath) {throw 'Rejected persistence published an artifact'}
if($script:terminalWrites -ne 1 -or $script:terminalSourceHashes -ne 1 -or $script:terminalSourceCounts -ne 1) {throw 'Diagnostic persistence repeated primary work'}
'frontier-terminal-persistence='+$persistenceCase
