param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$private:owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($env:NLINK_GUI_WRITER_REQUEST,
    [guid]$env:NLINK_GUI_WRITER_REQUEST_ID,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $owner -or $null -eq $owner.TryCreateLedgerOrigin()) {throw 'Original GUI owner missing'}
# Match the existing synthetic file-handoff fixture's source, not a producer log.
[IO.File]::WriteAllBytes((Join-Path $Directory 'shared.log'),[byte[]]::new(1024))
$private:requests=@($owner.TryCreateAppRequest(),$owner.TryCreateAppRequest())
$apps=@(); $reports=@(); $signals=@(); $events=@(); $flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Wait-Observation($Event,[string]$Message) {if(-not $Event.WaitOne(15000)) {throw $Message}}
function Exit-NativeGui {
    [IO.File]::WriteAllText((Join-Path $Directory 'children.json'),(@($apps | ForEach-Object {@{Id=$_.Id;Creation=$_.StartTime.ToUniversalTime().ToFileTimeUtc()}}) | ConvertTo-Json -Compress))
    'file-rendezvous-gui-ok'; [Environment]::Exit(0)
}
try {
    for($ordinal=0;$ordinal -lt 2;$ordinal++) {
        $parts=@{}
        foreach($part in @('files-wait','attached','submitted','finished')) {
            $event=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$requests[$ordinal].Name+'.test.rendezvous.'+$part)
            $parts[$part]=$event; $events+=@($event)
        }
        $signals+=@($parts)
        $start=[Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -ErrorAction Stop).Source)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $arguments=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFileRendezvousApp.ps1'),
            '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case,'-Ordinal',$ordinal.ToString())
        $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='frontier-gui-run'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='frontier-gui-scenario'
        $app=[Diagnostics.Process]::new(); $app.StartInfo=$start; $apps+=@($app)
        $callback=[Func[int,string[]]]{param($id) return [string[]]@((Join-Path $Directory ($id.ToString()+'.stdout.log')),(Join-Path $Directory ($id.ToString()+'.stderr.log')))}
        $reports+=@($requests[$ordinal].StartAppWithReports($app,$callback))
        if(-not $reports[$ordinal].NativeLaunchReady) {throw 'App native launch missing'}
    }
    foreach($signal in $signals) {Wait-Observation $signal['files-wait'] 'App did not reach native file readiness wait'}
    if($signals[0].attached.WaitOne(150)) {throw 'App attached before channels existed'}
    $gate=[Threading.Mutex]::OpenExisting($requests[0].Name+'.gate')
    try {if(-not $gate.WaitOne(0)) {throw 'Idle file readiness held request mutex'}; $gate.ReleaseMutex()} finally {$gate.Dispose()}
    if($Case -ceq 'parent-death-channel') {Exit-NativeGui}
    if($Case -ceq 'cancel-channel') {$owner.Dispose()}
    elseif($Case -ceq 'forged-ready') {
        $ready=[Threading.EventWaitHandle]::OpenExisting($requests[0].Name+'.files-ready-v1')
        try {[void]$ready.Set()} finally {$ready.Dispose()}
        Wait-Observation $signals[0].finished 'Forged readiness did not finish'
        $owner.Dispose()
    } elseif($Case -cne 'timeout-channel') {
        if($Case -ceq 'delayed-channel') {Start-Sleep -Milliseconds 250}
        if(-not $owner.SealLaunches()) {throw 'Original app registry not sealed'}
        if($Case -ceq 'bad-channel-scope') {
            # Hold the pre-existing request wakeups unsignalled while the GUI
            # creates the channel, then mutate before waking the app. No attach retry.
            foreach($request in $requests) {
                $native=$request.GetType().GetField('native',$flags).GetValue($request)
                $field=$native.GetType().GetField('filesReady',$flags)
                $events+=@($field.GetValue($native)); $field.SetValue($native,[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset))
            }
        }
        if(-not $owner.TryEnableFileHandoff()) {throw 'Original GUI file enable failed'}
        if($Case -ceq 'bad-channel-scope') {
            $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($requests[0].Name+'.files-v1'); $view=$map.CreateViewAccessor()
            try {$view.Write(80L,$view.ReadInt64(80L)+1L)} finally {$view.Dispose(); $map.Dispose()}
            $ready=[Threading.EventWaitHandle]::OpenExisting($requests[0].Name+'.files-ready-v1')
            try {[void]$ready.Set()} finally {$ready.Dispose()}
            Wait-Observation $signals[0].finished 'Changed channel scope did not reject'
            $owner.Dispose()
        } else {
            foreach($signal in $signals) {Wait-Observation $signal.submitted 'App did not submit files'}
            if($Case -ceq 'parent-death-adoption') {Exit-NativeGui}
            if($Case -ceq 'cancel-adoption') {$owner.Dispose()}
            elseif($Case -ceq 'catalog-cancel') {Wait-Observation $signals[0].finished 'Catalog cancellation did not finish'; $owner.Dispose()}
            elseif($Case -ceq 'timeout-adoption') { }
            elseif($Case -cin @('forged-adopted','bad-adoption-scope')) {
                $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($requests[0].Name+'.files-v1'); $view=$map.CreateViewAccessor()
                try {if($Case -ceq 'bad-adoption-scope') {$view.Write(80L,$view.ReadInt64(80L)+1L)}} finally {$view.Dispose(); $map.Dispose()}
                $adopted=[Threading.EventWaitHandle]::OpenExisting($requests[0].Name+'.files-v1.adopted')
                try {[void]$adopted.Set()} finally {$adopted.Dispose()}
                Wait-Observation $signals[0].finished 'Unproved adoption signal did not reject'; $owner.Dispose()
            } else {
                if($Case -ceq 'delayed-adoption' -and $signals[0].finished.WaitOne(250)) {throw 'App consumed acknowledgment before adoption'}
                foreach($request in $requests) {
                    if(-not $request.FileSubmissionAvailable -or -not $request.TryAdoptFiles()) {throw 'Original GUI file adoption failed'}
                }
            }
        }
    }
    foreach($app in $apps) {if(-not $app.WaitForExit(30000)) {throw 'App rendezvous exit deadline'}}
    foreach($report in $reports) {
        foreach($task in @($report.Stdout,$report.Stderr)) {if(-not $task.Wait(5000)) {throw 'Actual app pipe EOF missing'}; [void]$task.GetAwaiter().GetResult()}
    }
    $accepted=$Case -cin @('valid','delayed-channel','delayed-adoption','repeat-waits','copied-waits')
    for($ordinal=0;$ordinal -lt 2;$ordinal++) {
        $path=Join-Path $Directory ('app-'+$ordinal+'.json')
        if(-not [IO.File]::Exists($path) -or $apps[$ordinal].ExitCode -ne 0) {
            $errorPath=Join-Path $Directory ($apps[$ordinal].Id.ToString()+'.stderr.log')
            throw ('App rendezvous failed: '+[IO.File]::ReadAllText($errorPath))
        }
        $result=[IO.File]::ReadAllText($path) | ConvertFrom-Json
        if($result.Completed -ne $accepted -or $result.Acknowledged -ne $accepted) {throw 'Readiness changed acknowledgment/completion result'}
    }
    if($accepted -and (-not $owner.AllAppRequestsCompletedSuccessfully -or -not $owner.AllAppOutputsDrainedSuccessfully)) {throw 'Original app completion/output set incomplete'}
    if($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) {throw 'Rendezvous granted authority'}
    'file-rendezvous-gui-ok'
} finally {
    $owner.Dispose()
    foreach($app in $apps) {try {if(-not $app.HasExited) {$app.Kill(); [void]$app.WaitForExit(5000)}} finally {$app.Dispose()}}
    foreach($event in $events) {$event.Dispose()}
}
