param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-fallback'
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='checkpoint-control'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='checkpoint-control:1'
$support=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryCheckpoints.ps1'
. $support $Directory
$path=Join-Path $Directory 'test-primary-checkpoints.ndjson'
switch($Case) {
    'timeout-child' {
        [IO.File]::WriteAllText((Join-Path $Directory 'child-pid.txt'),[string]$PID)
        Write-FrontierPrimaryCheckpoint 'source-profile' 'fixture-only interruption control'
        [Console]::Out.WriteLine('original stdout'); [Console]::Error.WriteLine('original stderr')
        Start-Sleep -Seconds 60
        throw 'Timed-out child survived'
    }
    'normal' {
        $output=@(Write-FrontierPrimaryCheckpoint 'started' "original text `n quotes `" and unicode €")
        Write-FrontierPrimaryCheckpoint 'finished' 'original fixture assertions passed'
        $lines=[IO.File]::ReadAllLines($path)
        if($output.Count -or $lines.Length -ne 2) {throw 'Diagnostic sink changed output or record count'}
        for($i=0;$i -lt $lines.Length;$i++) {
            $record=$lines[$i] | ConvertFrom-Json
            if($record.sequence -ne $i+1 -or -not $record.diagnosticOnly -or $record.processId -ne $PID -or
                $record.acceptanceRunId -cne 'checkpoint-control' -or $record.scenarioRunId -cne 'checkpoint-control:1') {throw 'Checkpoint metadata changed'}
        }
        if(($lines[0] | ConvertFrom-Json).text -cne "original text `n quotes `" and unicode €") {throw 'Diagnostic text changed'}
    }
    'inactive' {
        foreach($profile in @('canonical-source','valid','CANONICAL-FALLBACK','')) {
            $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$profile
            Write-FrontierPrimaryCheckpoint 'started' 'must not be written'
        }
        if(Test-Path -LiteralPath $path) {throw 'Checkpoints escaped canonical fallback profile'}
    }
    'count-limit' {
        for($i=0;$i -lt 20;$i++) {Write-FrontierPrimaryCheckpoint 'source-profile' ('x'*4096)}
        if([IO.File]::ReadAllLines($path).Length -ne 16 -or (Get-Item -LiteralPath $path).Length -gt 131072) {throw 'Checkpoint count/size limit changed'}
    }
    'byte-limit' {
        Write-FrontierPrimaryCheckpoint 'source-profile' ''
        $overhead=(Get-Item -LiteralPath $path).Length
        foreach($size in @(8192,8193)) {
            $target=Join-Path $Directory ([string]$size); [void][IO.Directory]::CreateDirectory($target)
            . $support $target
            Write-FrontierPrimaryCheckpoint 'source-profile' ('x'*($size-$overhead))
            $targetPath=Join-Path $target 'test-primary-checkpoints.ndjson'
            if($size -eq 8192) {
                if((Get-Item -LiteralPath $targetPath).Length -ne 8192) {throw 'Exact record-byte boundary changed'}
            } elseif(Test-Path -LiteralPath $targetPath) {throw 'Oversized checkpoint was written'}
        }
    }
    'existing' {
        [IO.File]::WriteAllText($path,'existing evidence')
        Write-FrontierPrimaryCheckpoint 'started' 'must not overwrite'
        if([IO.File]::ReadAllText($path) -cne 'existing evidence') {throw 'Existing evidence overwritten'}
    }
    'write-failure' {
        [void][IO.Directory]::CreateDirectory($path)
        $caught=''
        try {throw 'original failure'} catch {
            Write-FrontierPrimaryCheckpoint 'worker-error' $_.Exception.Message
            $caught=$_.Exception.Message
        }
        if($caught -cne 'original failure' -or -not $script:primaryCheckpointStopped) {throw 'Write failure masked original error'}
    }
    'mutation' {
        Write-FrontierPrimaryCheckpoint 'started' 'original'
        [IO.File]::AppendAllText($path,'external mutation')
        $before=[IO.File]::ReadAllText($path)
        Write-FrontierPrimaryCheckpoint 'finished' 'must not append'
        if([IO.File]::ReadAllText($path) -cne $before -or -not $script:primaryCheckpointStopped) {throw 'Changed diagnostic file was extended'}
    }
    default {throw 'Unknown checkpoint control'}
}
[Console]::Out.WriteLine('original stdout'); [Console]::Error.WriteLine('original stderr')
'primary-checkpoint-control='+$Case
