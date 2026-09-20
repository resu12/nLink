param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$FinalizerCase = '')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$private:owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($env:NLINK_GUI_WRITER_REQUEST,
    [guid]$env:NLINK_GUI_WRITER_REQUEST_ID,'cleanup-adoption-run','cleanup-adoption-scope',$Directory)
if($null -eq $owner) {throw 'Original cleanup owner missing'}
$persistenceFixture=$FinalizerCase.StartsWith('persistence-')
$private:origin=$null
if($persistenceFixture) {
    . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
    $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
    Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger `
        -Path (Join-Path $Directory 'live-operational-log/nlink.log') -FrontierOwner $owner -FrontierOrigin ([ref]$origin)
} else {$origin=$owner.TryCreateLedgerOrigin()}
if($null -eq $origin) {throw 'Original cleanup origin missing'}
$reportRoot=if($persistenceFixture) {Join-Path $Directory 'process-output'} else {$Directory}
[void][IO.Directory]::CreateDirectory($reportRoot)
$private:requests=@($owner.TryCreateAppRequest(),$owner.TryCreateAppRequest())
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Original cleanup script parse failed'}
$definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Cleanup-ProcessesWithOperationalLogDrain'},$true))
if($definition.Count -ne 1) {throw 'Original cleanup function ambiguous'}
Invoke-Expression $definition[0].Extent.Text
$script:rundownObserved=$false
$apps=@(); $reports=@(); $signals=@(); $events=@(); $targets=@()
$script:actions=[Collections.Generic.List[string]]::new()
$script:waits=[Collections.Generic.List[int]]::new()
$script:stopped=[Collections.Generic.List[int]]::new()
$script:allowedPids=[Collections.Generic.List[int]]::new()
function Assert-CleanupPrivacy {
    foreach($name in @('owner','origin','requests','FrontierOwner')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw "Cleanup capability leaked: $name"}
    }
}
function Stop-Process {
    param([int]$Id,[switch]$Force,[string]$ErrorAction)
    Assert-CleanupPrivacy
    if(-not $script:allowedPids.Contains($Id)) {throw 'Cleanup escaped fixture child set'}
    $script:stopped.Add($Id)
    Microsoft.PowerShell.Management\Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue
}
function Stop-BridgeNodeProcessesForExePath {param($ExePath) Assert-CleanupPrivacy; $script:actions.Add('bridge')}
function Wait-Observation($Event,[string]$Message) {if(-not $Event.WaitOne(20000)) {throw $Message}}
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
try {
    for($ordinal=0;$ordinal -lt 2;$ordinal++) {
        $parts=@{}
        foreach($part in @('started','ready','finish')) {
            $event=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$requests[$ordinal].Name+'.test.cleanup.'+$part)
            $parts[$part]=$event; $events+=@($event)
        }
        $signals+=@($parts)
        $start=[Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -ErrorAction Stop).Source)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $arguments=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCleanupAdoptionApp.ps1'),
            '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case,'-Ordinal',$ordinal.ToString())
        $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='cleanup-adoption-run'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='cleanup-adoption-scope'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH']=Join-Path $Directory 'live-operational-log/nlink.log'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR']=$Directory
        $app=[Diagnostics.Process]::new(); $app.StartInfo=$start; $apps+=@($app)
        $callback=[Func[int,string[]]]{param($id)
            $paths=[string[]]@((Join-Path $reportRoot ($id.ToString()+'.stdout.log')),(Join-Path $reportRoot ($id.ToString()+'.stderr.log')))
            if($persistenceFixture) {foreach($path in $paths) {$file=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $file.Dispose()}}
            return $paths
        }
        $reports+=@($requests[$ordinal].StartAppWithReports($app,$callback))
        if(-not $reports[$ordinal].NativeLaunchReady) {throw 'Original app launch unavailable'}
        $script:allowedPids.Add($app.Id)
        Wait-Observation $parts.started 'App startup waited for peer launch'
        if($ordinal -eq 0 -and $null -eq $owner.TryCaptureInitialLedgerObservation()) {throw 'Original initial cursor missing'}
        if($ordinal -eq 0 -and $persistenceFixture) {
            Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger
            $script:ordinaryInitial=Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger
            Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $script:ordinaryInitial.bookmark
        }
        # Only window closure is simulated. Exit state and waits use the actual
        # original process, and all native handoff operations remain unmodified.
        $target=[pscustomobject]@{Process=$app;Id=$app.Id;Finish=$parts.finish;Ordinal=$ordinal}
        $target | Add-Member ScriptProperty HasExited {
            if($Case -ceq 'exit-rundown' -and $this.Ordinal -eq 1 -and $this.Finish.WaitOne(0)) {
                if(-not $this.Process.HasExited) {$script:rundownObserved=$true}
                return $true
            }
            return $this.Process.HasExited
        }
        $target | Add-Member ScriptMethod CloseMainWindow {
            Assert-CleanupPrivacy; $script:actions.Add('close:'+ $this.Ordinal)
            [void]$this.Finish.Set(); return $true
        }
        $target | Add-Member ScriptMethod WaitForExit {param([int]$Milliseconds)
            Assert-CleanupPrivacy; $script:actions.Add('wait:'+ $this.Ordinal); $script:waits.Add($Milliseconds)
            return $this.Process.WaitForExit($Milliseconds)
        }
        $targets+=@($target)
    }
    if(-not $owner.SealLaunches() -or -not $owner.TryEnableFileHandoff()) {throw 'Original cleanup file channels missing'}
    foreach($parts in $signals) {Wait-Observation $parts.ready 'App producer setup failed'}
    $adoption=$owner.GetType().GetField('fileAdoption',$flags).GetValue($owner)
    if($Case -ceq 'cancel-before') {$owner.Dispose()}
    if($Case -ceq 'copies-repeat') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)
        if($copy.WaitForFileAdoptions(1) -or -not $owner.IsCurrent -or
            $owner.GetType().GetField('fileAdoptionWaitAttempted',$flags).GetValue($owner)) {throw 'Copied wait changed original ownership'}
    }
    if($Case -ceq 'premature-signal') {
        $channels=$owner.GetType().GetField('fileChannels',$flags).GetValue($owner)
        [void]$channels[0].GetType().GetField('SubmittedEvent',$flags).GetValue($channels[0]).Set()
    }
    $budget=if($Case -ceq 'timeout') {400} elseif($Case -ceq 'zero-budget') {0} else {7000}
    if($FinalizerCase) {
        if($persistenceFixture) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFinalizerPersistence.ps1')}
        else {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFinalizerSource.ps1')}
        'cleanup-adoption-gui-ok'
        return
    }
    $detail=$null; $clock=[Diagnostics.Stopwatch]::StartNew()
    $output=@(Cleanup-ProcessesWithOperationalLogDrain -Processes $targets -ExePath fixture-only -TimeoutMs $budget `
        -CleanupDiagnostic ([ref]$detail) -FrontierOwner $owner)
    $elapsed=$clock.ElapsedMilliseconds
    if($output.Count -or $null -eq $detail -or $detail.sharedWaitBudgetMilliseconds -ne $budget -or
        ($script:actions[0..1] -join '|') -cne 'close:0|close:1') {throw 'Cleanup output/order/budget changed'}
    foreach($app in $apps) {if(-not $app.WaitForExit(5000)) {throw 'Cleanup left an app alive'}}
    foreach($report in $reports) {foreach($task in @($report.Stdout,$report.Stderr)) {if(-not $task.Wait(5000)) {throw 'Original report EOF missing'}; [void]$task.GetAwaiter().GetResult()}}
    $accepted=$Case -cin @('writer-only','positive','second-first','delayed-emitter','copies-repeat')
    if($accepted) {
        if(-not $owner.IsCurrent -or -not $owner.AllFileHandoffsAcknowledged -or -not $owner.AllAppRequestsCompletedSuccessfully -or
            -not $owner.AllAppOutputsDrainedSuccessfully -or $script:stopped.Count) {throw 'Original handoff did not complete before app exit'}
        if($Case -ceq 'copies-repeat' -and ($owner.WaitForFileAdoptions(7000) -or -not $owner.IsCurrent)) {throw 'Repeated wait changed successful original handoff'}
        $contributions=$adoption.GetType().GetField('adopted',$flags).GetValue($adoption)
        if($Case -ceq 'second-first') {
            $manifest=$contributions[0].GetType().GetField('Manifest',$flags).GetValue($contributions[0])
            if($manifest.GetType().GetField('AppId',$flags).GetValue($manifest) -ne $apps[1].Id) {throw 'First pending app blocked second submission'}
        }
    } elseif($owner.IsCurrent) {throw 'Invalid/expired cleanup retained optional ownership'}
    if($Case -ceq 'premature-signal' -and $owner.GetType().GetField('fileAdoptAttempted',$flags).GetValue($owner)[0]) {
        throw 'Signal without original submission consumed an adoption attempt'
    }
    for($ordinal=0;$ordinal -lt 2;$ordinal++) {
        if($script:stopped.Contains($apps[$ordinal].Id)) {continue}
        $path=Join-Path $Directory ('app-'+$ordinal+'.json')
        if($apps[$ordinal].ExitCode -ne 0 -or -not [IO.File]::Exists($path)) {
            throw ('App cleanup failed: '+[IO.File]::ReadAllText((Join-Path $Directory ($apps[$ordinal].Id.ToString()+'.stderr.log'))))
        }
        $result=[IO.File]::ReadAllText($path) | ConvertFrom-Json
        if(-not $result.Flushed -or ($accepted -and -not $result.Completed)) {throw 'Ordinary app drain lost'}
    }
    if($Case -ceq 'timeout' -and ($elapsed -lt 350 -or $elapsed -gt 2000 -or -not $script:stopped.Contains($apps[1].Id) -or
        @($script:waits | Where-Object {$_ -gt 20}).Count)) {throw 'Adoption renewed or exceeded shared exit-wait budget'}
    if($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) {throw 'Cleanup granted authority'}
    'cleanup-adoption-gui-ok'
} finally {
    $owner.Dispose()
    foreach($event in $events) {[void]$event.Set()}
    foreach($app in $apps) {try {if(-not $app.HasExited) {$app.Kill(); [void]$app.WaitForExit(5000)}} finally {$app.Dispose()}}
    foreach($event in $events) {$event.Dispose()}
}
