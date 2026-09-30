param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$ExePath='',[int]$TimeoutSeconds=0)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$attemptMode=$env:NLINK_TEST_ATTEMPT_DISPATCH -ceq '1'
$canonicalRolesMode=-not [string]::IsNullOrEmpty($env:NLINK_TEST_CANONICAL_ROLES_CASE)
if($canonicalRolesMode) {
    . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchGui.ps1') -Stage Entry
} elseif($attemptMode) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchGui.ps1') -Stage Entry
    if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'listener-*') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPayloadListenerProbe.ps1') -Stage Gui
        if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -ceq 'listener-gui-nonzero') {exit 7}
        exit 0
    }
} else {
    . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
    $private:owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($env:NLINK_GUI_WRITER_REQUEST,
        [guid]$env:NLINK_GUI_WRITER_REQUEST_ID,'scenario-launch-run','scenario-launch-scope',$Directory)
    if($null -eq $owner) {throw 'Original scenario owner missing'}
    $private:origin=$owner.TryCreateLedgerOrigin()
    if($null -eq $origin) {throw 'Original scenario origin missing'}
}
# Test-only handles for assertions in mocked UI boundaries. Production context,
# formatters and environment must never carry these objects.
$script:testOwner=$owner; $script:testOrigin=$origin
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Original scenario script parse failed'}
foreach($name in @('Run-ScenarioFileTransferTunaHandoffFallback','Start-HelpeeFlow','Start-HelperFlow','Start-AppInstance',
    'Set-GuiPreparationProcessIdentity','New-ScenarioContext','New-GuiSmokeProcessOutputCapture','Get-GuiSmokeProcessOutputRoot','Wait-GuiSmokeProcessOutputCaptureDrain',
    'New-TunaGuiFileTransferFailureClassificationResult','New-TunaGuiFailureClassificationEnrichmentReceipt')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw 'Original scenario function ambiguous'}
    $source=$definition[0].Extent.Text
    if($name -ceq 'Start-AppInstance') {
        $needle='$startInfo.UseShellExecute = $false'
        if(($source.Split(@($needle),[StringSplitOptions]::None)).Count -ne 2) {throw 'Headless launch seam changed'}
        $source=$source.Replace($needle,$needle+"`n"+'$startInfo.CreateNoWindow=$true; $startInfo.Arguments=$script:appArguments')
    }
    if($name -ceq 'New-GuiSmokeProcessOutputCapture') {
        $needle='$root = Get-GuiSmokeProcessOutputRoot'
        if(($source.Split(@($needle),[StringSplitOptions]::None)).Count -ne 2) {throw 'Original report setup seam changed'}
        $source=$source.Replace($needle,@'
$script:allStarted.Add([Diagnostics.Process]::GetProcessById($ProcessId))
if($Case -ceq ($RoleName+'-report-failure')) {throw ($RoleName+' original report setup failure')}
$root = Get-GuiSmokeProcessOutputRoot
'@)
    }
    Invoke-Expression $source
}
$writer=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Write-FileTransferGuiLogLedgerEvidence'},$true))[0]
$script:drainBlock=@($writer.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Extent.Text.StartsWith('if ($null -ne $ProcessOutputDrainAction)')},$true))[0].Extent.Text
if(-not $script:drainBlock.Contains('-FrontierReports $FrontierReports') -or
    -not $writer.Extent.Text.Contains('Set-Variable -Name FrontierReports -Option Private')) {throw 'Original finalizer lost private report forwarding'}
$liveCalls=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and
    $n.GetCommandName() -ceq 'Run-ScenarioFileTransferTunaHandoffFallback'},$true))
if($liveCalls.Count -ne 1 -or $liveCalls[0].Extent.Text -cne
    'Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx -FrontierOwner $private:scenarioFrontierOwner -CanonicalAttempt $CanonicalAttempt') {throw 'Guarded GUI scenario call changed'}

function Assert-PrivateScenarioInputs {
    foreach($name in @('owner','origin','FrontierOwner','frontierHelpeeRequest','frontierHelperRequest',
        'frontierHelpeeReports','frontierHelperReports','FrontierRequest','FrontierReports','createReports',
        'attemptTicket','projection','guiFrontierOwner','guiFrontierOrigin','ownerCopy','ticketCopy','CanonicalAttempt',
        'scenarioFrontierOwner','guiCanonicalAttempt')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw "Scenario capability leaked: $name"}
    }
}
function Get-AppLaunchEnvironmentOverrides {
    param($RoleName)
    Assert-PrivateScenarioInputs
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierScenarioLaunchApp.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Role',$RoleName,'-Signal',$env:NLINK_GUI_WRITER_REQUEST,'-Case',$Case)
    $script:appArguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    return @{}
}
function Wait-Window {
    param($Process,$RoleName,$TimeoutMs)
    Assert-PrivateScenarioInputs
    if(-not $script:signals[$RoleName+'.ready'].WaitOne(20000)) {throw 'Headless app startup deadline'}
    if($Case -ceq ($RoleName+'-window-failure')) {throw ($RoleName+' original window failure')}
    return [pscustomobject]@{Role=$RoleName}
}
function Set-ScenarioRoleWindowIdentity {param($Context,$RoleName,$Window) $Context.($RoleName+'Window')=$Window}
function Click-HomeButton {param($Window,$Text) Assert-PrivateScenarioInputs}
function Try-ClickRoleButtonIfPresent {param($Window,$RoleButtonText) return $false}
function Get-StartupWindowTimeoutMs {return 20000}
function Reset-ScenarioContext {param($Context) Assert-PrivateScenarioInputs}
function Clear-HelperIdentityArtifact {}
function Get-AppLogBookmark {Assert-PrivateScenarioInputs; return 77}
function Get-GuiSmokeLogLedgerSnapshot {
    param($State,$Bookmark)
    Assert-PrivateScenarioInputs
    if($Case -ceq 'cancel-between') {$script:testOwner.Dispose()}
    if($Case -ceq 'cursor-failure') {$script:testOrigin.GetType().GetField('file',$flags).GetValue($script:testOrigin).Dispose()}
    return [pscustomobject]@{activeGenerationKey='ordinary-generation';generations=@('ordinary-generation');bookmark=$Bookmark}
}
function Set-GuiSmokeLogLedgerWriterEvidenceBookmark {param($State,$Bookmark) if($Bookmark -ne 77) {throw 'Ordinary bookmark changed'}}
function Connect-HelperAndHelpee {
    param($Context)
    Assert-PrivateScenarioInputs
    $initial=$script:testOrigin.GetType().GetField('initialObservation',$flags).GetValue($script:testOrigin)
    if($Case -ceq 'valid') {
        if($null -eq $initial -or -not $initial.IsCurrent -or $initial.Bookmark -ne 1 -or $initial.LineCount -ne 1 -or
            $initial.BytesRead -ne 7 -or $initial.CursorHistoryValidated) {throw 'Original initial cursor missing or replaced by scalar bookmark'}
        if(-not $script:testOwner.GetType().GetField('sealedLaunches',$flags).GetValue($script:testOwner)) {throw 'Original pair not sealed'}
    } elseif($null -ne $initial) {throw 'Rejected/default scenario captured native initial observation'}
    if($script:testOwner.LiveProofEligible -or $script:testOwner.NativeWriterCustodyEstablished -or
        $script:testOwner.PublicationCompletionIssued) {throw 'Launch granted authority'}
    $channels=$script:testOwner.GetType().GetField('fileChannels',$flags).GetValue($script:testOwner)
    if(($null -ne $channels) -ne ($Case -ceq 'valid')) {throw 'Scenario channel readiness changed'}
}
function Invoke-FileTransferTunaHandoffFallbackCycle {Assert-PrivateScenarioInputs; $script:cycleCalled=$true}
function Cleanup-ProcessesWithOperationalLogDrain {
    param($Processes,$ExePath,$CleanupDiagnostic,$FrontierOwner)
    Set-Variable -Name FrontierOwner -Option Private
    Assert-PrivateScenarioInputs
    if($Case -ceq 'default') {
        if($null -ne $FrontierOwner) {throw 'Default cleanup received native ownership'}
    } elseif($Case -cne 'copied-owner' -and -not [object]::ReferenceEquals($FrontierOwner,$script:testOwner)) {
        throw 'Original owner was lost at the private cleanup callback boundary'
    }
    # These launch-only children do not own Core writers or submit files. The
    # separate cleanup-adoption fixture exercises that real app-lifetime path.
    if($null -ne $FrontierOwner) {$FrontierOwner.Dispose()}
    foreach($role in @('helpee','helper')) {[void]$script:signals[$role+'.exit'].Set()}
}
function Write-FileTransferGuiLogLedgerEvidence {
    param($ArtifactDir,$Bookmark,$InitialSnapshot,$ProcessOutputDrainAction,$FrontierReports,$FrontierOwner)
    Set-Variable -Name FrontierReports,FrontierOwner -Option Private
    $script:reportCount=@($FrontierReports | Where-Object {$null -ne $_}).Count
    if($Case -cnotin @('helpee-window-failure','helpee-report-failure') -and ($Bookmark -ne 77 -or $InitialSnapshot.bookmark -ne 77)) {throw 'Ordinary initial snapshot changed'}
    $processOutputDrainFailure=''
    # Execute the original finalizer's complete cleanup/drain try/catch block.
    Invoke-Expression $script:drainBlock
    if($processOutputDrainFailure) {throw $processOutputDrainFailure}
    $script:drained=$true
}
function Test-GuiSmokeEnvEnabled {param($Name) return $Name -ceq 'NLINK_RUN_TUNA_GUI_FILETRANSFER'}
function Get-IsNknTransport {return $true} # no NKN transport is created by this fixture
function Get-FileTransferSoakArtifactDir {return $Directory}
function Resolve-TunaGuiWalletPath {return (Join-Path $Directory 'unused-wallet')}
function Resolve-TunaGuiSidecarPath {return (Join-Path $Directory 'unused-sidecar')}
function Initialize-TunaGuiRuntimeState {param($ArtifactDir,$WalletPath,$SidecarPath) return $Directory}
function Get-FileTransferSoakDirection {return 'helpee-to-helper'}
function Get-FileTransferSoakSeed {return 1}
function Get-FileTransferSoakCycleTimeoutMs {return 1000}
function Get-FileTransferSoakStartupTimeoutMs {return 1000}
function Get-FileTransferSoakProgressTimeoutMs {return 1000}
function Get-TunaGuiPayerRole {return 'helpee'}
function Get-TunaGuiFaultMode {return 'none'}
function Get-TunaGuiRouteMode {return 'regular'}
function Update-FileTransferGuiEvidenceFinalizationStage {param($Stage)}
function Get-TunaGuiExplicitFailureClassification {param($ErrorMessage) return [pscustomobject]@{Phase='fixture';Reason=$ErrorMessage}}
function Write-GuiSmokeAtomicJson {param($Path,$Value)}
function Invoke-TunaGuiDataPathDiagnosticCompanion {throw 'No diagnostic network work in fixture'}

$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR=$Directory
if(-not $attemptMode -and -not $canonicalRolesMode) {$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES=''}
$env:NLINK_TUNA_TEST_WALLET_PASSWORD='unused-headless-fixture'
$script:GuiSmokeProcessOutputRoot=$null
$script:GuiSmokeProcessOutputFiles=[Collections.Generic.List[string]]::new()
$script:GuiSmokeAppLogLedger=[pscustomobject]@{LedgerId='ordinary-ledger'}
$script:cycleCalled=$false; $script:drained=$false; $script:reportCount=0; $script:appArguments=''
$script:signals=@{}
$script:allStarted=[Collections.Generic.List[object]]::new()
foreach($role in @('helpee','helper')) {
    foreach($part in @('ready','exit')) {
        $script:signals[$role+'.'+$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$env:NLINK_GUI_WRITER_REQUEST+'.'+$role+'.'+$part)
    }
}
$appExe=if($attemptMode -and $env:NLINK_TEST_ATTEMPT_CASE.StartsWith('writer-')) {$ExePath} else {(Get-Process -Id $PID).Path}
$ctx=New-ScenarioContext -ExePath $appExe
$private:selected=$owner
if($Case -ceq 'default') {$selected=$null}
if($Case -ceq 'cancel-before') {$owner.Dispose()}
if($Case -ceq 'copied-owner') {$selected=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)}
if($Case -ceq 'request-overflow') {$null=$owner.TryCreateAppRequest(); $null=$owner.TryCreateAppRequest()}
$failure=''
try {
    try {
        if($canonicalRolesMode) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalRolesGui.ps1')}
        elseif($attemptMode) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchGui.ps1') -Stage Dispatch}
        else {Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx -FrontierOwner $selected}
    } catch {$failure=$_.Exception.Message}
    $setupFailure=$Case.EndsWith('-window-failure') -or $Case.EndsWith('-report-failure')
    if($setupFailure) {
        $expectedFailure=$Case.Split('-')[0]+$(if($Case.EndsWith('-report-failure')) {' original report setup failure'} else {' original window failure'})
        if(-not $failure.Contains($expectedFailure) -or $script:cycleCalled) {throw "Original scenario error lost: $failure"}
    } elseif($failure -or -not $script:cycleCalled) {throw "Scenario failed: $failure"}
    $expectedApps=if($Case -cin @('helpee-window-failure','helpee-report-failure')) {1} else {2}
    if(-not $script:drained -or $ctx.Processes.Count -ne $expectedApps -or
        @([IO.File]::ReadAllLines((Join-Path $Directory 'launches.txt'))).Count -ne $expectedApps) {throw 'Launch count/ordinary drain changed'}
    $expectedReports=if($Case -cin @('default','cancel-before','request-overflow','copied-owner')) {0} else {$expectedApps}
    if($Case.EndsWith('-report-failure')) {$expectedReports--}
    if($script:reportCount -ne $expectedReports) {throw 'Original report tasks lost during optional rejection'}
    $expectedAttachments=if($Case -cin @('default','cancel-before','request-overflow','copied-owner')) {0}
        elseif($Case -ceq 'helpee-report-failure') {0}
        elseif($Case -cin @('cancel-between','cursor-failure','helpee-window-failure','helper-report-failure')) {1} else {2}
    if(@(Get-ChildItem -LiteralPath $Directory -Filter '*.attached').Count -ne $expectedAttachments) {throw 'Original app request delivery changed'}
    foreach($process in $ctx.Processes) {if(-not $process.HasExited -or $process.ExitCode -ne 0) {throw 'Original child failed'}}
    foreach($path in $script:GuiSmokeProcessOutputFiles) {
        if([IO.File]::ReadAllText($path) -notmatch '(ordinary|error)-(helpee|helper)') {throw 'Ordinary report missing'}
    }
    if(-not $attemptMode -and $Case -cin @('default','copied-owner') -and -not $owner.IsCurrent) {throw 'Default/copied branch retired original owner'}
    if(@($ctx.PSObject.Properties.Name | Where-Object {$_ -match 'Frontier'}).Count) {throw 'Scenario context exported native capabilities'}
    'scenario-launch-gui-ok'
    if($attemptMode) {'attempt-dispatch-gui-ok'}
} finally {
    if(($attemptMode -or $canonicalRolesMode) -and $null -ne $attemptTicket) {$attemptTicket.Dispose()}
    $owner.Dispose()
    foreach($event in $script:signals.Values) {[void]$event.Set()}
    foreach($process in $ctx.Processes) {try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} finally {$process.Dispose()}}
    foreach($process in $script:allStarted) {try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} finally {$process.Dispose()}}
    foreach($event in $script:signals.Values) {$event.Dispose()}
}
if($attemptMode -and ($env:NLINK_TEST_ATTEMPT_CASE -ceq 'gui-nonzero' -or $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -ceq 'gui-nonzero')) {exit 7}
