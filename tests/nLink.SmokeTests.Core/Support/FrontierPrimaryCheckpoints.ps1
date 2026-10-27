param([string]$ArtifactDir)
# Fixture diagnostics only: never an input, receipt, or authority channel.
$script:primaryCheckpointPath=Join-Path $ArtifactDir 'test-primary-checkpoints.ndjson'
$script:primaryCheckpointCount=0; $script:primaryCheckpointBytes=0; $script:primaryCheckpointStopped=$false
$script:primaryCheckpointClock=[Diagnostics.Stopwatch]::StartNew() # Diagnostic clock only; never a work budget.
function Write-FrontierPrimaryCheckpoint([string]$Stage,[string]$Text) {
    try {
        $profile=$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE
        if($profile -cnotin @('canonical-fallback','canonical-tail-late-publication') -or $script:primaryCheckpointStopped) {return}
        $stages=if($profile -ceq 'canonical-fallback') {@('started','source-origins','source-profile','binding','cleanup','bind-error','cleanup-error','worker-error','finished')}
            else {@('started','compact-enter','arrays-ready','origins-ready','registration-start','registration-end','full-start','full-end','compact-start','compact-end','payloads-ready','parity-ready','pre-correlator','worker-error','finished')}
        if($script:primaryCheckpointCount -ge 16 -or $Stage -cnotin $stages) {
            $script:primaryCheckpointStopped=$true; return
        }
        $record=[ordered]@{schemaVersion=1;diagnosticOnly=$true;processId=$PID;
            acceptanceRunId=[string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID;
            scenarioRunId=[string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID;
            sequence=$script:primaryCheckpointCount+1;timestampUtc=[DateTime]::UtcNow.ToString('O');stage=$Stage;text=$Text}
        if($profile -ceq 'canonical-tail-late-publication') {$record['elapsedMilliseconds']=$script:primaryCheckpointClock.ElapsedMilliseconds}
        $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes(($record | ConvertTo-Json -Compress)+"`n")
        if($bytes.Length -gt 8192) {$script:primaryCheckpointStopped=$true; return}
        $mode=if($script:primaryCheckpointCount -eq 0) {[IO.FileMode]::CreateNew} else {[IO.FileMode]::Open}
        $stream=[IO.File]::Open($script:primaryCheckpointPath,$mode,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try {
            if($stream.Length -ne $script:primaryCheckpointBytes) {throw 'checkpoint length changed'}
            [void]$stream.Seek(0,[IO.SeekOrigin]::End)
            $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true)
        } finally {$stream.Dispose()}
        $script:primaryCheckpointCount++; $script:primaryCheckpointBytes+=$bytes.Length
    } catch {$script:primaryCheckpointStopped=$true} # Never mask the original result or error.
}
