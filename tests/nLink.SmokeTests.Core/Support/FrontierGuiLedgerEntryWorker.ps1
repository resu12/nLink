param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$toolsRoot=Join-Path $RepoRoot 'tools'
. (Join-Path $toolsRoot 'GuiSmokeLogLedger.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $toolsRoot 'GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Original GUI entry parse failed'}
foreach($name in @('Get-GuiSmokeFrontierLaunchOwner','Clear-AppLogsIfPresent')) {
    $definitions=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definitions.Count -ne 1) {throw 'Original GUI entry function ambiguous'}
    Invoke-Expression $definitions[0].Extent.Text.Replace('$PSScriptRoot',("'"+$toolsRoot.Replace("'","''")+"'"))
}
$main=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst] -and $_.Body.Extent.Text.Contains('# GUI smoke intentionally')})
if($main.Count -ne 1) {throw 'Original GUI entry lifetime ambiguous'}
$entry=@($main[0].Body.Statements | Where-Object {$_.Extent.Text.StartsWith('$guiFrontierOwner =') -or
    $_.Extent.Text.StartsWith('Clear-AppLogsIfPresent ') -or $_.Extent.Text.StartsWith('if ($null -ne $guiFrontierOwner')})
if($entry.Count -ne 3) {throw 'Original GUI entry startup changed'}
$cleanup=$main[0].Finally.Statements[0].Extent.Text
if(-not $cleanup.Contains('$guiFrontierOwner.Dispose()')) {throw 'Original GUI cleanup not retained'}
function Reset-ScenarioContext {param($Context) if($Case -ceq 'cleanup-failure') {throw 'original cleanup failure'} }
$ctx=[pscustomobject]@{}
$script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
$private:guiFrontierOwner=$null; $private:guiFrontierOrigin=$null; $private:savedOwner=$null; $private:savedOrigin=$null
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$originalRequestId=$env:NLINK_GUI_WRITER_REQUEST_ID
$path=Join-Path $Directory 'live-operational-log/nlink.log'
$localLogs=Join-Path $env:LOCALAPPDATA 'nLink/logs'; [void][IO.Directory]::CreateDirectory($localLogs)
[IO.File]::WriteAllText((Join-Path $localLogs 'stale.log'),'fixture stale')
$expectedRoot=[IO.Path]::GetFullPath((Join-Path $Directory 'fixture-local-app-data'))
if([IO.Path]::GetFullPath($env:LOCALAPPDATA) -cne $expectedRoot) {throw 'Fixture log cleanup escaped test directory'}
if($Case -cin @('default','existing-legacy')) {Remove-Item Env:NLINK_GUI_FRONTIER_START_V1 -ErrorAction SilentlyContinue}
if($Case -ceq 'selector-invalid') {$env:NLINK_GUI_FRONTIER_START_V1='true'}
if($Case -ceq 'malformed-id') {$env:NLINK_GUI_WRITER_REQUEST_ID='invalid'}
if($Case -ceq 'wrong-scope') {$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='wrong-scenario'}
if($Case -ceq 'missing-scope') {$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=''}
if($Case -cin @('existing-native','existing-legacy')) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)); [IO.File]::WriteAllText($path,'preserve-existing')
}
if($Case -ceq 'postcreate-failure') {
    # Only this case compiles a fault-instrumented copy: fail immediately after
    # the genuine CREATE_NEW handle was obtained, before native retention.
    function Add-Type {
        param([string[]]$Path,$CompilerParameters,[string]$CompilerOptions,[string]$ErrorAction)
        $arguments=@{Path=$Path;ErrorAction='Stop'}
        if($null -ne $CompilerParameters) {$arguments.CompilerParameters=$CompilerParameters}
        if($CompilerOptions) {$arguments.CompilerOptions=$CompilerOptions}
        for($i=0;$i -lt $Path.Count;$i++) {
            if([IO.Path]::GetFileName($Path[$i]) -ceq 'LocalGuiWriterLedgerOrigin.cs') {
                $source=[IO.File]::ReadAllText($Path[$i]); $needle='if (GetLedgerFileType(handle) != 1)'
                if(($source.Split(@($needle),[StringSplitOptions]::None)).Count -ne 2) {throw 'Creation fault seam changed'}
                $copy=Join-Path $Directory 'LocalGuiWriterLedgerOrigin.fault.cs'
                [IO.File]::WriteAllText($copy,$source.Replace($needle,'if (GetLedgerFileType(handle) == 1)'),[Text.UTF8Encoding]::new($false))
                $Path[$i]=$copy
            }
        }
        Microsoft.PowerShell.Utility\Add-Type @arguments
    }
}
if($Case -ceq 'cancel-after-create') {
    $definition=(Get-Command Enable-GuiSmokeAppendOnlyLogLedgerState).Definition
    $needle='$private:origin = $FrontierOwner.TryCreateLedgerOriginForGui($resolvedPath, [ref]$sourceCreated)'
    if(($definition.Split(@($needle),[StringSplitOptions]::None)).Count -ne 2) {throw 'Cancellation seam changed'}
    $fault='$closed=[Threading.EventWaitHandle]::OpenExisting($env:NLINK_GUI_WRITER_REQUEST+".closed"); try {[void]$closed.Set()} finally {$closed.Dispose()}'
    Invoke-Expression ('function Enable-GuiSmokeAppendOnlyLogLedgerState {'+$definition.Replace($needle,$needle+"`n"+$fault)+'}')
}
# A formatter called during initialization must not inherit native capabilities.
$hashBody=(Get-Command Get-GuiSmokeLogLedgerHash).Definition
$hashNeedle='    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)'
if(($hashBody.Split(@($hashNeedle),[StringSplitOptions]::None)).Count -ne 2) {throw 'Formatter privacy seam changed'}
Invoke-Expression ('function Get-GuiSmokeLogLedgerHash {'+$hashBody.Replace($hashNeedle,@'
    foreach($name in @('guiFrontierOwner','guiFrontierOrigin','FrontierOwner','FrontierOrigin','origin')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw 'Native ledger capability leaked into formatter'}
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
'@)+'}')
$failure=''; $cleanupFailure=''
try {
    Invoke-Expression $entry[0].Extent.Text
    $savedOwner=$guiFrontierOwner
    $attached=$null -ne $guiFrontierOwner
    $expectedAttach=$Case -cnotin @('default','existing-legacy','selector-invalid','malformed-id','wrong-scope','missing-scope','collision-ready')
    if($attached -ne $expectedAttach) {throw 'Original readiness selector/scope outcome changed'}
    if($Case -cin @('default','existing-legacy','selector-invalid','collision-ready') -and $null -ne ('NLink.Tools.LocalGuiWriterRequest' -as [type])) {throw 'Default GUI startup loaded optional native types'}
    if($Case -ceq 'replay' -and ($null -ne (Get-GuiSmokeFrontierLaunchOwner) -or -not $guiFrontierOwner.IsCurrent)) {throw 'GUI entry replay replaced/retired owner'}
    if($Case -ceq 'budget') {
        $watch=$guiFrontierOwner.GetType().GetField('frontierWork',$flags).GetValue($guiFrontierOwner)
        $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    if($Case -ceq 'wrong-path') {
        $created=$true
        $wrong=$guiFrontierOwner.TryCreateLedgerOriginForGui((Join-Path $Directory 'foreign.log'),[ref]$created)
        if($created -or $null -ne $wrong -or -not $guiFrontierOwner.IsCurrent -or [IO.File]::Exists($path)) {throw 'Wrong ledger locator consumed original creation'}
    }
    if($Case -ceq 'invalid-output') {
        $invalid='occupied'; $rejected=$false
        try {Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $path -FrontierOwner $guiFrontierOwner -FrontierOrigin ([ref]$invalid)}
        catch {if($_.Exception.Message -cne 'gui_smoke_frontier_ledger_owner_invalid') {throw}; $rejected=$true}
        if(-not $rejected -or [IO.File]::Exists($path) -or -not $guiFrontierOwner.IsCurrent) {throw 'Invalid output consumed creation'}
    }
    try {Invoke-Expression $entry[1].Extent.Text; Invoke-Expression $entry[2].Extent.Text} catch {$failure=$_.Exception.Message}
    $savedOrigin=$guiFrontierOrigin
    if($Case -cin @('existing-native','existing-legacy')) {
        if($failure -cne "gui_smoke_append_only_log_preexisting:$path" -or [IO.File]::ReadAllText($path) -cne 'preserve-existing') {throw 'Original preexisting ledger failure changed'}
    } else {
        if($failure) {throw $failure}
        if([IO.File]::Exists((Join-Path $localLogs 'stale.log'))) {throw 'Original log cleanup skipped'}
        if($Case -ceq 'missing-scope') {
            if([IO.File]::Exists($path) -or $script:GuiSmokeAppLogLedger.StorageMode -cne 'RollingMemory' -or $env:NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH) {throw 'Missing scope created a ledger'}
        } else {
            $expectOrigin=$Case -cin @('valid','replay','wrong-path','cleanup-failure','invalid-output')
            if(($null -ne $guiFrontierOrigin) -ne $expectOrigin -or ($null -ne $guiFrontierOwner) -ne $expectOrigin) {throw 'Optional origin failure changed GUI startup'}
            if($script:GuiSmokeAppLogLedger.StorageMode -cne 'AppendOnlyFile' -or $env:NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH -cne $path -or
                [IO.File]::ReadAllText($path) -cne '') {throw 'Original append-only initialization changed'}
            if($expectOrigin) {
                if(-not $guiFrontierOrigin.IsCurrent -or $guiFrontierOrigin.SourcePath -cne $path -or -not $guiFrontierOrigin.WasCreatedEmpty -or
                    $guiFrontierOrigin.CursorHistoryValidated -or $guiFrontierOrigin.LiveProofEligible -or $guiFrontierOwner.NativeWriterCustodyEstablished) {throw 'Native origin missing or promoted'}
                $file=$guiFrontierOrigin.GetType().GetField('file',$flags).GetValue($guiFrontierOrigin)
                if(-not $file.CanRead -or $file.CanWrite -or $file.Position -ne 0) {throw 'Creation handle lost'}
                $blocked=$false; try {[IO.File]::Move($path,$path+'.moved')} catch [IO.IOException] {$blocked=$true}
                if(-not $blocked) {throw 'Native creation allowed replacement'}
            }
            # State remains ordinary mutable diagnostics, with no native object.
            if(@($script:GuiSmokeAppLogLedger.PSObject.Properties | Where-Object {$null -ne $_.Value -and $_.Value.GetType().FullName -like '*Frontier*'}).Count) {throw 'Native capability leaked into ledger state'}
            [IO.File]::AppendAllText($path,"ordinary-one`nordinary-two`n",[Text.UTF8Encoding]::new($false))
            Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($path)
            if((Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger) -ne 2 -or
                (@(Get-GuiSmokeLogLedgerLines -State $script:GuiSmokeAppLogLedger) -join '|') -cne 'ordinary-one|ordinary-two') {throw 'Original ledger readers changed'}
            if($expectOrigin -and $file.Position -ne 0) {throw 'Ordinary reader fabricated native cursor work'}
        }
    }
    if($Case -cin @('default','selector-invalid','malformed-id')) {
        $env:NLINK_GUI_FRONTIER_START_V1='1'; $env:NLINK_GUI_WRITER_REQUEST_ID=$originalRequestId
        $late=Get-GuiSmokeFrontierLaunchOwner
        if($null -eq $late) {throw 'Ignored selector or malformed GUID consumed native attachment'}
        $late.Dispose()
    }
    if($Case -cin @('wrong-scope','missing-scope')) {
        $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='gui-ledger-scenario'
        if($null -ne (Get-GuiSmokeFrontierLaunchOwner)) {throw 'Rejected original scope allowed an attachment retry'}
    }
} finally {
    try {Invoke-Expression $cleanup} catch {$cleanupFailure=$_.Exception.Message}
}
if($cleanupFailure -cne $(if($Case -ceq 'cleanup-failure') {'original cleanup failure'} else {''})) {throw 'Original cleanup failure changed'}
if(($null -ne $savedOwner -and $savedOwner.IsCurrent) -or ($null -ne $savedOrigin -and $savedOrigin.IsCurrent)) {throw 'GUI cleanup leaked native owner'}
if([IO.File]::Exists($path)) {[IO.File]::Move($path,$path+'.released')}
[Console]::Out.WriteLine('ordinary ledger entry stdout'); [Console]::Error.Write('ordinary ledger entry stderr')
