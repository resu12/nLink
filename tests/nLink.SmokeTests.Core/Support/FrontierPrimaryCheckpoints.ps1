param([string]$ArtifactDir)
# Fixture diagnostics only: never an input, receipt, or authority channel.
$script:primaryCheckpointPath=Join-Path $ArtifactDir 'test-primary-checkpoints.ndjson'
$script:primaryCheckpointCount=0; $script:primaryCheckpointBytes=0; $script:primaryCheckpointStopped=$false
function Write-FrontierPrimaryCheckpoint([string]$Stage,[string]$Text) {
    try {
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cne 'canonical-fallback' -or $script:primaryCheckpointStopped) {return}
        if($script:primaryCheckpointCount -ge 16 -or $Stage -cnotin @('started','source-origins','source-profile','binding','cleanup','bind-error','cleanup-error','worker-error','finished')) {
            $script:primaryCheckpointStopped=$true; return
        }
        $record=[ordered]@{schemaVersion=1;diagnosticOnly=$true;processId=$PID;
            acceptanceRunId=[string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID;
            scenarioRunId=[string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID;
            sequence=$script:primaryCheckpointCount+1;timestampUtc=[DateTime]::UtcNow.ToString('O');stage=$Stage;text=$Text}
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
