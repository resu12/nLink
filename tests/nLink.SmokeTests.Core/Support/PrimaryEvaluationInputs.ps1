param([string]$RepoRoot,[string]$Directory,[string]$Lane='inputs')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$completion=$null
$null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $Directory 'gui') -AcceptedCompletion ([ref]$completion)
if($null -eq $completion) { throw 'Accepted GUI context missing' }
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$completion.AcceptanceRunId
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$completion.ScenarioRunId
$modes=@('controls')
if($PSVersionTable.PSVersion.Major -eq 5) { $modes+=@('primary-baseline','primary-owned','primary-report-fails','primary-input-mutated','primary-evaluator-fails') }
if($Lane -ceq 'custody') {
    $modes=@('valid','early-source','source-only','record-cap','duplicate-begin','reversed-order','wrong-full-array',
        'wrong-compact-array','missing-full-view','missing-compact-view','duplicate-full','duplicate-compact',
        'premature-take','copied-context','swapped-views','copied-view','copied-array','forged-token',
        'source-disposed','worker-disposed','custody-disposed')
}
$rows=[Collections.Generic.List[object]]::new(); $baseline=@{}
foreach($mode in $modes) {
    $output=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($output)
    $lease=$null; $request=$null; $process=$null
    try {
        $lease=([NLink.Tools.LocalInputReadLease]::HashAndHold($completion.Outputs[3].Path)).Lease
        $candidate=$completion
        if($mode -cin @('primary-baseline','source-only')) { $candidate=$null }
        $request=New-FileTransferRetainedSourceRequest -ReadLease $lease -AttemptId ([guid]::NewGuid().ToString('N')) -Owner Wrapper -LineCount $completion.Outputs[3].LineCount -WriterContext $candidate
        if($null -eq $request) { throw 'Retained request missing' }
        $settings=Join-Path $output 'settings.json'; $resultPath=Join-Path $output 'result.json'
        [IO.File]::WriteAllText($settings,(@{mode=$mode;path=$completion.Outputs[3].Path;result=$resultPath;output=$output} | ConvertTo-Json))
        $process=[Diagnostics.Process]::new()
        $process.StartInfo.FileName=(Get-Process -Id $PID).Path
        $worker='PrimaryEvaluationInputsWorker.ps1'
        if($Lane -ceq 'custody') { $worker='PrimaryEvaluationCustodyWorker.ps1' }
        $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$worker))+'" "'+$RepoRoot+'" "'+$settings+'"'
        $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$request.RequestId.ToString('D')
        if(-not $process.Start()) { throw 'Worker start failed' }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $request.BindWorker($process)) { throw 'Worker bind failed' }
        if(-not $process.WaitForExit(45000)) { throw 'Worker test timeout' }
        if($process.ExitCode -ne 0) { throw "$mode : $($stdout.Result) $($stderr.Result)" }
        $row=[IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
        if($mode -like 'primary-*' -and $mode -cne 'primary-evaluator-fails') {
            foreach($name in @('filetransfer-operator-verdict.txt','filetransfer-retained-compact-summary.json',
                'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
                'filetransfer-coordinator-snapshot-parity-summary.json')) {
                $value=[IO.File]::ReadAllText((Join-Path $output $name))
                if($name -ceq 'filetransfer-retained-compact-summary.json') { $value=$value -replace '("data_path_correlation_(index|total)_ms"\s*:\s*)[0-9]+','${1}0' }
                if($mode -ceq 'primary-baseline') { $baseline[$name]=$value }
                elseif($baseline[$name] -cne $value) { throw "Primary report changed: $mode/$name" }
            }
        }
        $rows.Add($row)
    } finally {
        if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
        if($null -ne $request) { $request.Dispose() }; if($null -ne $lease) { $lease.Dispose() }
    }
}
'primary-evaluation-inputs='+(@{Cases=$rows.ToArray();LiveExport=$false;PublicationCompletionIssued=$false} | ConvertTo-Json -Depth 6 -Compress)
