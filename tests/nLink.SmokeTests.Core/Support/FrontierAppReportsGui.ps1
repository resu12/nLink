param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$private:owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($env:NLINK_GUI_WRITER_REQUEST,
    [guid]$env:NLINK_GUI_WRITER_REQUEST_ID,'app-reports-run','app-reports-scenario',$Directory)
if($null -eq $owner -or $null -eq $owner.TryCreateLedgerOrigin()) {throw 'Original GUI ledger owner missing'}
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Original app harness parse failed'}
foreach($name in @('Start-AppInstance','Set-GuiPreparationProcessIdentity','New-GuiSmokeProcessOutputCapture','Get-GuiSmokeProcessOutputRoot','Wait-GuiSmokeProcessOutputCaptureDrain')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw 'Original app function ambiguous'}
    $source=$definition[0].Extent.Text
    if($name -ceq 'Start-AppInstance') {
        $needle='$startInfo.UseShellExecute = $false'
        if(($source.Split(@($needle),[StringSplitOptions]::None)).Count -ne 2) {throw 'Headless app arguments seam changed'}
        $source=$source.Replace($needle,$needle+"`n"+'$startInfo.CreateNoWindow=$true; $startInfo.Arguments=$script:appArguments')
        $source=$source.Replace('$process.StartInfo = $startInfo','$process.StartInfo = $startInfo' + "`n" + '$script:launchedApp=$process')
    }
    if($name -ceq 'New-GuiSmokeProcessOutputCapture') {
        $source=$source.Replace('$root = Get-GuiSmokeProcessOutputRoot',@'
Assert-PrivateAppInputs
if($Case -ceq 'setup-failure') {throw 'original report setup failed'}
if($Case -ceq 'native-ready') {
    if(-not $events.waiting.WaitOne(15000) -or $events.attached.WaitOne(150)) {throw 'App did not wait before original binding'}
}
$root = Get-GuiSmokeProcessOutputRoot
'@)
    }
    Invoke-Expression $source
}
function Assert-PrivateAppInputs {
    foreach($name in @('owner','request','reports','FrontierRequest','FrontierReports','createReports')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw "App capability leaked: $name"}
    }
}
function Get-AppLaunchEnvironmentOverrides {param($RoleName) Assert-PrivateAppInputs; return @{} }
function Wait-AppCondition([scriptblock]$Check,[string]$Message) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not (& $Check)) {if($watch.ElapsedMilliseconds -gt 10000) {throw $Message}; Start-Sleep -Milliseconds 15}
}
function Read-NativeCaptureBytes([string]$Path) {
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $bytes=[IO.MemoryStream]::new()
    try {$file.CopyTo($bytes); return ,$bytes.ToArray()} finally {$bytes.Dispose(); $file.Dispose()}
}
$script:GuiSmokeProcessOutputRoot=$null
$script:GuiSmokeProcessOutputFiles=[Collections.Generic.List[string]]::new()
$script:launchedApp=$null; $script:appArguments=''
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$private:requests=@($owner.TryCreateAppRequest())
if($Case -ceq 'two-apps') {$requests+=@($owner.TryCreateAppRequest())}
$private:request=$null; $private:reports=$null
$processes=[Collections.Generic.List[object]]::new(); $allEvents=[Collections.Generic.List[object]]::new(); $heldFile=$null; $foreignReady=$null
try {
    for($ordinal=0;$ordinal -lt $requests.Count;$ordinal++) {
        $request=$requests[$ordinal]; $reports=$null
        if($null -eq $request) {throw 'App request missing'}
        $signal=$env:NLINK_GUI_WRITER_REQUEST+'.app-reports.'+$ordinal
        $events=@{}
        foreach($part in @('emit','emitted','exit','held','holder-exit','waiting','attached')) {
            $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.'+$part)
            $allEvents.Add($events[$part])
        }
        $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAppReportsApp.ps1'),
            '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Signal',$signal,'-Case',$Case)
        if($Case -cnotin @('default','native-ready','ready-collision-ready','ready-collision-failed')) {$arguments+=@('-Name',$request.Name,'-RequestId',$request.RequestId.ToString('D'))}
        $script:appArguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
        $prefix=Join-Path $Directory ('frontier-process-output-'+$owner.RequestId.ToString('N')+'-'+$ordinal)
        if($Case.StartsWith('collision-')) {[IO.File]::WriteAllText($prefix+'-'+$Case.Substring(10)+'.log','preserve-foreign')}
        if($Case.StartsWith('ready-collision-')) {$foreignReady=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.app-start-v1.'+$Case.Substring('ready-collision-'.Length))}
        if($Case -ceq 'budget-before') {
            $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
        }
        if($Case -ceq 'owner-disposed') {$owner.Dispose()}
        if($Case -ceq 'bind-failure') {$request.GetType().GetField('native',$flags).GetValue($request).GetType().GetField('gate',$flags).GetValue($request.GetType().GetField('native',$flags).GetValue($request)).Dispose()}
        if($Case -cin @('guards','cross-api')) {
            $probe=[Diagnostics.Process]::new(); $probe.StartInfo.FileName=Join-Path $Directory 'missing.exe'
            $probe.StartInfo.UseShellExecute=$false; $probe.StartInfo.RedirectStandardOutput=$true; $probe.StartInfo.RedirectStandardError=$true
            try {
                if($Case -ceq 'cross-api') {
                    if($null -ne $request.StartAppWithOutput($probe)) {throw 'Old start unexpectedly succeeded'}
                } else {
                    $callback=[Func[int,string[]]]{param($id) throw 'Guard invoked reports callback'}
                    $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($request,$null); $rejected=$false
                    try {$null=$copy.StartAppWithReports($probe,$callback)} catch {$rejected=$true}
                    $copy.Dispose(); if(-not $rejected -or -not $request.IsCurrent) {throw 'Copied request changed original launch'}
                    foreach($property in @('RedirectStandardOutput','RedirectStandardError')) {
                        $probe.StartInfo.$property=$false; $rejected=$false
                        try {$null=$request.StartAppWithReports($probe,$callback)} catch {$rejected=$true}
                        $probe.StartInfo.$property=$true; if(-not $rejected) {throw 'Invalid redirect accepted'}
                    }
                    $rejected=$false; try {$null=$request.StartAppWithReports($probe,$null)} catch {$rejected=$true}
                    if(-not $rejected -or $request.GetType().GetField('outputLaunchAttempted',$flags).GetValue($request)) {throw 'Invalid arguments consumed launch'}
                    $occupied='occupied'; $rejected=$false
                    try {$null=Start-AppInstance -ExePath (Get-Process -Id $PID).Path -RoleName 'invalid' -FrontierRequest $request -FrontierReports ([ref]$occupied)}
                    catch {$rejected=$_.Exception.Message -eq 'gui_smoke_frontier_app_request_invalid'}
                    if(-not $rejected -or -not $request.IsCurrent -or $script:GuiSmokeProcessOutputFiles.Count) {throw 'Invalid GUI report output consumed launch'}
                }
            } finally {$probe.Dispose()}
        }
        # Match the existing raw-output fixture: the app for explicit native
        # handle closure runs on .NET 8. Windows PowerShell keeps other host
        # pipe references alive after its standard handles are closed.
        $exe=if($Case -ceq 'start-failure') {Join-Path $Directory 'missing-app.exe'}
            elseif($Case -ceq 'early-eof') {(Get-Command pwsh).Source}
            else {(Get-Process -Id $PID).Path}
        $failure=$null; $process=$null
        try {
            if($Case -ceq 'default') {$process=Start-AppInstance -ExePath $exe -RoleName 'ordinary'}
            else {$process=Start-AppInstance -ExePath $exe -RoleName 'native' -FrontierRequest $request -FrontierReports ([ref]$reports)}
        } catch {$failure=$_.Exception; $process=$script:launchedApp}
        if($Case -cin @('start-failure','cross-api')) {
            if($null -eq $failure -or $null -ne $reports -or $script:GuiSmokeProcessOutputFiles.Count -ne 0) {throw 'Start/one-shot failure hidden'}
            if($Case -ceq 'start-failure') {
                $inner=$failure; while($null -ne $inner.InnerException) {$inner=$inner.InnerException}
                if($inner -isnot [ComponentModel.Win32Exception] -or $inner.NativeErrorCode -ne 2) {throw 'Original start error lost'}
            } elseif($failure.Message -notmatch 'frontier_app_launch_repeated_or_foreign') {throw 'Cross-API retry not rejected'}
            if($null -ne $process) {$process.Dispose()}
            continue
        }
        if($null -eq $process) {throw "Missing original process: $failure"}; $processes.Add($process)
        if($Case -ceq 'setup-failure') {if($null -eq $failure -or $failure.Message -notmatch 'original report setup failed') {throw 'Report setup error hidden'}}
        elseif($null -ne $failure) {throw $failure}
        $noNative=$Case -cin @('default','collision-stdout','collision-stderr','bind-failure','budget-before','owner-disposed','setup-failure','ready-collision-ready','ready-collision-failed')
        if($Case -cne 'setup-failure' -and $Case -cne 'default' -and (($null -ne $reports.Launch) -eq $noNative)) {throw 'Wrong native report binding outcome'}
        if($null -ne $reports -and $reports.NativeLaunchReady -eq $noNative) {throw 'Wrong native app readiness outcome'}
        if($Case -ceq 'native-ready' -or $Case.StartsWith('ready-collision-')) {
            if(-not $events.attached.WaitOne(15000)) {throw 'Delivered app readiness did not finish'}
            if($null -ne $foreignReady -and $foreignReady.WaitOne(0)) {throw 'Foreign readiness event was modified'}
        }
        if($Case -ceq 'replay') {
            $unused=[Diagnostics.Process]::new(); $rejected=$false
            try {$null=$request.StartAppWithReports($unused,[Func[int,string[]]]{param($id) throw 'Replay callback invoked'})} catch {$rejected=$_.Exception.Message -match 'frontier_app_launch_repeated_or_foreign'} finally {$unused.Dispose()}
            if(-not $rejected -or -not $reports.Launch.IsCurrent) {throw 'Replay changed original binding'}
        }
        if($Case -ceq 'guards') {
            $private:extra=$owner.TryCreateAppRequest(); $rejected=$false
            try {$null=$extra.StartAppWithReports($process,[Func[int,string[]]]{param($id) throw 'Associated process invoked reports callback'})}
            catch {$rejected=$_.Exception.Message -match 'frontier_app_already_started'}
            if(-not $rejected -or -not $extra.IsCurrent -or $extra.GetType().GetField('outputLaunchAttempted',$flags).GetValue($extra)) {throw 'Associated process consumed new launch'}
        }
        if($Case -cin @('native-write-failure','native-seal-failure')) {
            $capture=$reports.Launch.GetType().GetField('Output',$flags).GetValue($reports.Launch)
            if($Case -ceq 'native-write-failure') {$capture.GetType().GetField('stdoutTarget',$flags).GetValue($capture).Dispose()}
            else {$capture.GetType().GetField('RecoveryOutputs',$flags).GetValue($capture)[0].GetType().GetMethod('Revoke',$flags).Invoke($capture.GetType().GetField('RecoveryOutputs',$flags).GetValue($capture)[0],$null)}
        }
        if($Case -ceq 'ordinary-write-failure') {$heldFile=[IO.File]::Open($script:GuiSmokeProcessOutputFiles[0],[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
        [void]$events.emit.Set()
        if(-not $events.emitted.WaitOne(20000)) {throw 'App emission failed'}
        if($Case -ceq 'cancel-after') {$owner.Dispose()}
        if($Case -cnotin @('empty','ordinary-write-failure','setup-failure')) {
            $ordinaryPath=$script:GuiSmokeProcessOutputFiles[$script:GuiSmokeProcessOutputFiles.Count-2]
            Wait-AppCondition {([IO.File]::ReadAllText($ordinaryPath)).Contains($(if($Case -ceq 'large') {'xxxx'} else {'ordinary-one'}))} 'No live ordinary lines before exit'
        }
        if($Case -ceq 'early-eof') {
            $watch=[Diagnostics.Stopwatch]::StartNew()
            while(-not ($reports.Stdout.IsCompleted -and $reports.Stderr.IsCompleted)) {
                if($watch.ElapsedMilliseconds -gt 10000) {throw 'Early EOF not observed'}
                Start-Sleep -Milliseconds 15
            }
            if($process.HasExited -or $reports.Launch.ExitedSuccessfully) {throw 'Pipe EOF became successful process exit'}
        }
        [void]$events.exit.Set(); if(-not $process.WaitForExit(15000)) {throw 'App exit deadline'}
        if($process.ExitCode -ne $(if($Case -ceq 'nonzero') {7} else {0})) {throw 'Original app exit code changed'}
        if($Case -ceq 'held-pipe') {
            $timedOut=$false
            try {Wait-GuiSmokeProcessOutputCaptureDrain -Processes @($process) -FrontierReports @($reports) -TimeoutMs 200 -QuietPeriodMs 20}
            catch {$timedOut=$_.Exception.Message -match 'process_output_eof_timeout:frontier_app_reports'}
            if(-not $timedOut -or $reports.Stdout.IsCompleted -or $reports.Launch.OutputDrainedSuccessfully) {throw 'Process exit/quiet files became pipe EOF'}
            [void]$events['holder-exit'].Set()
        }
        if($Case -ceq 'setup-failure') {continue}
        $drainFailure=$null
        try {Wait-GuiSmokeProcessOutputCaptureDrain -Processes @($process) -FrontierReports @($reports) -TimeoutMs 10000 -QuietPeriodMs 50}
        catch {$drainFailure=$_.Exception}
        if($Case -ceq 'ordinary-write-failure') {
            if($null -eq $drainFailure -or -not $reports.Stdout.IsFaulted -or $reports.Launch.OutputDrainedSuccessfully) {throw 'Ordinary write failure masked or promoted'}
            $heldFile.Dispose(); $heldFile=$null; continue
        }
        if($null -ne $drainFailure) {throw $drainFailure}
        $raw=[IO.File]::ReadAllBytes((Join-Path $Directory 'expected-raw.bin'))
        $encoding=if($Case -ceq 'default') {[Console]::OutputEncoding} else {$process.StandardOutput.CurrentEncoding}
        $decoded=$encoding.GetString($raw)
        $lines=[Collections.Generic.List[string]]::new(); $reader=[IO.StringReader]::new($decoded)
        try {while($null -ne ($line=$reader.ReadLine())) {$lines.Add($line)}} finally {$reader.Dispose()}
        $expected=[Environment]::NewLine+[string]::Join([Environment]::NewLine,$lines)
        if($lines.Count) {$expected+=[Environment]::NewLine}
        foreach($path in @($script:GuiSmokeProcessOutputFiles | Select-Object -Last 2)) {
            # ReadAllText removes the ordinary file's encoding preamble, not the
            # raw pipe's leading U+FEFF inside its first reported line.
            if($Case -ceq 'default') {
                # Existing event jobs can reorder callbacks. Check their original
                # line multiset; the new single reader must preserve pipe order.
                if((@([IO.File]::ReadAllLines($path) | Sort-Object) -join [char]31) -cne (@(@('')+$lines.ToArray() | Sort-Object) -join [char]31)) {throw 'Default ordinary lines changed'}
            } elseif([IO.File]::ReadAllText($path) -cne $expected) {throw 'Ordinary line framing/encoding changed'}
        }
        if(-not $noNative) {
            $expectedCapture=$Case -cnotin @('native-write-failure','native-seal-failure','cancel-after')
            if($reports.Launch.OutputDrainedSuccessfully -ne $expectedCapture) {throw 'Wrong optional output EOF/capture outcome'}
            if($Case -ceq 'nonzero' -and $reports.Launch.ExitedSuccessfully) {throw 'Nonzero exit promoted'}
            if($expectedCapture) {
                foreach($suffix in @('stdout','stderr')) {
                    if([Convert]::ToBase64String((Read-NativeCaptureBytes ($prefix+'-'+$suffix+'.log'))) -cne [Convert]::ToBase64String($raw)) {throw 'Native raw pipe bytes changed'}
                }
            }
        }
        if($Case.StartsWith('collision-') -and [IO.File]::ReadAllText($prefix+'-'+$Case.Substring(10)+'.log') -cne 'preserve-foreign') {throw 'Foreign native capture overwritten'}
    }
    if($Case -ceq 'two-apps' -and (-not $owner.SealLaunches() -or -not $owner.AllAppOutputsDrainedSuccessfully -or -not $owner.AllAppsExitedSuccessfully -or -not $owner.AllAppRequestsCompletedSuccessfully)) {throw 'Two-app original output/exit set incomplete'}
    if($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) {throw 'Report capture granted authority'}
    $launchPath=Join-Path $Directory 'app-launches.txt'
    $launches=if([IO.File]::Exists($launchPath)) {@([IO.File]::ReadAllLines($launchPath)).Count} else {0}
    if($launches -ne $(if($Case -cin @('start-failure','cross-api')) {0} elseif($Case -ceq 'two-apps') {2} else {1})) {throw 'Duplicate or missing app launch'}
    'original-app-reports-ok'
} finally {
    foreach($event in $allEvents) {[void]$event.Set()}
    if($null -ne $heldFile) {$heldFile.Dispose()}
    foreach($process in $processes) {try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} finally {$process.Dispose()}}
    $owner.Dispose(); foreach($event in $allEvents) {$event.Dispose()}
    if($null -ne $foreignReady) {$foreignReady.Dispose()}
}
