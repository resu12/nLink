param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$private:owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($env:NLINK_GUI_WRITER_REQUEST,
    [guid]$env:NLINK_GUI_WRITER_REQUEST_ID,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $owner -or $null -eq $owner.TryCreateLedgerOrigin()) {throw 'Original GUI owner missing'}
$private:requests=@($owner.TryCreateAppRequest(),$owner.TryCreateAppRequest())
$apps=@(); $reports=@(); $signals=@(); $events=@()
function Wait-Observation($Event,[string]$Message) {if(-not $Event.WaitOne(15000)) {throw $Message}}
try {
    for($ordinal=0;$ordinal -lt 2;$ordinal++) {
        $parts=@{}
        foreach($part in @('started','ready','capture','finish','finished')) {
            $event=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$requests[$ordinal].Name+'.test.lifetime.'+$part)
            $parts[$part]=$event; $events+=@($event)
        }
        $signals+=@($parts)
        $start=[Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -ErrorAction Stop).Source)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $arguments=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAppLifetimeApp.ps1'),
            '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case,'-Ordinal',$ordinal.ToString())
        $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='frontier-gui-run'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='frontier-gui-scenario'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH']=Join-Path $Directory 'shared.log'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR']=$Directory
        $app=[Diagnostics.Process]::new(); $app.StartInfo=$start; $apps+=@($app)
        $callback=[Func[int,string[]]]{param($id) return [string[]]@((Join-Path $Directory ($id.ToString()+'.stdout.log')),(Join-Path $Directory ($id.ToString()+'.stderr.log')))}
        if($Case -ceq 'early-start-exit') {
            # Isolate cancellation before startup-ready publication. Bind the
            # real process, but deliberately do not publish its ready event.
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            $native=$requests[$ordinal].GetType().GetField('native',$flags).GetValue($requests[$ordinal])
            if(-not $native.GetType().GetMethod('PrepareStart',$flags).Invoke($native,@($start,$true))) {throw 'Startup preparation failed'}
            if(-not $app.Start()) {throw 'App start failed'}
            $reports+=@(@{Stdout=$app.StandardOutput.ReadToEndAsync();Stderr=$app.StandardError.ReadToEndAsync()})
            if($null -eq $requests[$ordinal].BindApp($app)) {throw 'Actual app binding failed'}
        } else {
            $reports+=@($requests[$ordinal].StartAppWithReports($app,$callback))
            if(-not $reports[$ordinal].NativeLaunchReady) {throw 'App native launch missing'}
        }
        # Same dependency as real GUI: first app startup must remain available
        # before the GUI starts app two and creates their file channels.
        Wait-Observation $parts.started 'App startup blocked before second launch'
    }
    if($Case -cin @('early-exit','early-start-exit','disabled','setup-rejected','service-unready')) {
        if($Case -ceq 'service-unready') {
            foreach($signal in $signals) {[void]$signal.capture.Set()}
            foreach($signal in $signals) {Wait-Observation $signal.ready 'Early admission did not reject'}
        }
        foreach($signal in $signals) {[void]$signal.finish.Set()}
    } else {
        if($Case -ceq 'delayed-channel' -and $signals[0].ready.WaitOne(300)) {throw 'Catalog ready before file channels'}
        if(-not $owner.SealLaunches() -or -not $owner.TryEnableFileHandoff()) {throw 'Original file channels unavailable'}
        foreach($signal in $signals) {
            if($Case -cin @('both-positive','service-both-positive') -or $Case.StartsWith('producer-')) {[void]$signal.capture.Set()}
            Wait-Observation $signal.ready 'App setup/capture did not finish'
        }
        foreach($signal in $signals) {[void]$signal.finish.Set()}
        if($Case -cin @('incomplete','catalog-cancel','scope-change','early-drain','service-pause','service-source','service-drained','service-context-race','service-data-race','service-shutdown-timeout','producer-timeout','producer-late-service')) {
            Wait-Observation $signals[0].finished 'Invalid app contribution did not finish'
            if($requests[0].FileSubmissionAvailable) {throw 'Invalid contribution submitted files'}
            $owner.Dispose()
        } else {
            foreach($request in $requests) {
                $submitted=[Threading.EventWaitHandle]::OpenExisting($request.Name+'.files-v1.submitted')
                try {Wait-Observation $submitted 'Owner did not submit original files'} finally {$submitted.Dispose()}
            }
            if($Case -ceq 'cancel-adoption') {$owner.Dispose()}
            else {
                if($Case -ceq 'delayed-adoption' -and $signals[0].finished.WaitOne(300)) {throw 'Owner exited before adoption'}
                foreach($request in $requests) {
                    if(-not $request.FileSubmissionAvailable -or -not $request.TryAdoptFiles()) {throw 'Original file adoption failed'}
                }
            }
        }
    }
    foreach($app in $apps) {if(-not $app.WaitForExit(30000)) {throw 'App lifetime exit deadline'}}
    foreach($report in $reports) {
        foreach($task in @($report.Stdout,$report.Stderr)) {if(-not $task.Wait(5000)) {throw 'Actual pipe EOF missing'}; [void]$task.GetAwaiter().GetResult()}
    }
    if($Case -ceq 'disabled') {
        # Inspect only after both ordinary writers have exited. A test reader
        # with FileShare.Read during the other app's drain would block writes.
        $text=[IO.File]::ReadAllText((Join-Path $Directory 'shared.log'))
        if([regex]::Matches($text,'event=operational_log_writer_drained;').Count -ne 2) {throw 'Ordinary app drains missing'}
    }
    $accepted=$Case -cin @('valid','both-positive','writer-only','delayed-channel','delayed-adoption','copies-repeat','service-valid','service-both-positive','service-reset','service-shutdown-natural','service-shutdown-delayed','producer-writer','producer-delayed','producer-retired','producer-rejected','producer-dispose-race','producer-positive-delayed')
    for($ordinal=0;$ordinal -lt 2;$ordinal++) {
        $path=Join-Path $Directory ('app-'+$ordinal+'.json')
        if(-not [IO.File]::Exists($path) -or $apps[$ordinal].ExitCode -ne 0) {
            throw ('App lifetime failed: '+[IO.File]::ReadAllText((Join-Path $Directory ($apps[$ordinal].Id.ToString()+'.stderr.log'))))
        }
        $result=[IO.File]::ReadAllText($path) | ConvertFrom-Json
        if($result.Completed -ne $accepted -or -not $result.Flushed) {throw 'Optional handoff changed completion/drain result'}
    }
    if($accepted -and (-not $owner.AllAppRequestsCompletedSuccessfully -or -not $owner.AllAppOutputsDrainedSuccessfully)) {throw 'Original app completion/output set incomplete'}
    if($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) {throw 'App lifetime granted authority'}
    'app-lifetime-gui-ok'
} catch {
    foreach($ordinal in @(0,1)) {
        $path=Join-Path $Directory ('app-'+$ordinal+'-error.txt')
        if([IO.File]::Exists($path)) {[Console]::Error.WriteLine([IO.File]::ReadAllText($path))}
    }
    throw
} finally {
    $owner.Dispose()
    foreach($app in $apps) {try {if(-not $app.HasExited) {$app.Kill(); [void]$app.WaitForExit(5000)}} finally {$app.Dispose()}}
    foreach($event in $events) {$event.Dispose()}
}
