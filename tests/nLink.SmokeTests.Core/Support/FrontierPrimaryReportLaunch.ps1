$launchCase=$pipeCase.Substring('terminal-reports-launch-'.Length)
$expectedOut="ordinary stdout $([char]0x20ac)`r`n"; $expectedErr='ordinary stderr'
[IO.File]::WriteAllBytes((Join-Path $Directory 'stdout.bin'),[Text.Encoding]::UTF8.GetBytes($expectedOut))
[IO.File]::WriteAllBytes((Join-Path $Directory 'stderr.bin'),[Text.Encoding]::UTF8.GetBytes($expectedErr))
$start.StandardOutputEncoding=[Text.Encoding]::UTF8; $start.StandardErrorEncoding=[Text.Encoding]::UTF8
$arguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'FrontierPrimaryReportWorker.ps1'),'-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Signal',($primary.Name+'.test.worker'),'-Case',$launchCase)
$start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
$startedUtc=[DateTimeOffset]::UtcNow.ToString('O')
if($launchCase -ceq 'guards') {
    foreach($property in @('RedirectStandardOutput','RedirectStandardError')) {
        $start.$property=$false; $rejected=$false
        try {[void]$primary.StartFrontierWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejected=$true}
        if(-not $rejected -or $null -ne $nativeProcess.Id) {throw 'Invalid redirects started worker'}
        $start.$property=$true
    }
    $copy=$parentClone.Invoke($primary,$null); $rejected=$false
    try {[void]$copy.StartFrontierWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejected=$true}
    if(-not $rejected -or $null -ne $nativeProcess.Id -or -not $inputs.IsCurrent) {throw 'Copied request launched/consumed original'}
}
$collision=Join-Path $Directory ('frontier-primary-output-'+$primary.RequestId.ToString('N')+'-stdout.log')
if($launchCase -ceq 'capture-open-failure') {[IO.File]::WriteAllText($collision,'preserve-capture-collision')}
if($launchCase -ceq 'bind-failure') {$primary.GetType().GetField('gate',$parentFlags).GetValue($primary).Dispose()}
if($launchCase -ceq 'start-failure') {$start.FileName=Join-Path $Directory 'missing-report-worker.exe'}
$run=$null; $failure=''
try {$run=$primary.StartFrontierWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$failure=$_.Exception.Message}
if($launchCase -ceq 'start-failure') {
    if(-not $failure -or $null -ne $run -or $null -ne $nativeProcess.Id) {throw 'Start failure hidden/restarted'}
} else {
    if($failure -or $null -eq $run) {throw ('Optional failure lost started primary reports: '+$failure)}
    if($run.SourceBound -ne ($launchCase -cne 'bind-failure') -or ($null -ne $run.DiagnosticOutput) -ne ($launchCase -ceq 'guards')) {throw 'Optional report/native binding result mismatch'}
    if($launchCase -ceq 'guards') {
        $rejected=$false
        try {[void]$primary.StartFrontierWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejected=$true}
        if(-not $rejected) {throw 'Repeated launch allowed'}
    }
    if($launchCase -ceq 'bind-failure') {$primary.Dispose()}
    [void]$signals.begin.Set()
    if(-not $nativeProcess.WaitForExit(20000) -or $nativeProcess.ExitCode -ne 0) {throw 'Ordinary report worker failed'}
    if($run.Stdout.GetAwaiter().GetResult() -cne $expectedOut -or $run.Stderr.GetAwaiter().GetResult() -cne $expectedErr) {throw 'Optional launch failure changed ordinary reports'}
    if($launchCase -ceq 'guards' -and $run.DiagnosticOutput.OutputsDrainedSuccessfully) {throw 'Repeated launch retained diagnostic capture'}
    if(@([IO.File]::ReadAllLines((Join-Path $Directory 'starts.txt'))).Count -ne 1) {throw 'Report worker launched more than once'}
    if($launchCase -ceq 'capture-open-failure' -and [IO.File]::ReadAllText($collision) -cne 'preserve-capture-collision') {throw 'Capture collision overwritten'}
}
$primary.Dispose()
'frontier-primary-report-launch='+$launchCase
