param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Route parent parse failed' }
$definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-TunaRouteAcceptanceRun'},$true))[0]
Invoke-Expression $definition.Extent.Text
$dispatch=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-TunaRouteAcceptanceRun' -and
    $n.Extent.Text -match '-CaptureLocalPublicationDiagnostics:'},$true))
if($dispatch.Count -ne 2 -or @($dispatch | Where-Object { $_.Extent.Text -notmatch 'AcceptancePhase -eq ''phase5''' }).Count) { throw 'Top-level opt-in scope widened' }
$topDispatch=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-Phase4RouteAcceptanceScenario'},$true))
if($topDispatch.Count -ne 1 -or $topDispatch[0].Extent.Text -notmatch 'acceptancePhase -eq ''phase5'' -and -not \$fakeMode') { throw 'Fake/legacy entrypoint opted in' }
if($definition.Extent.Text.IndexOf('$private:persistenceStarted = 0L') -lt $definition.Extent.Text.IndexOf('throw $failureMessage')) { throw 'Input-rewriting retry path opted in' }
$parameter=@($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'CaptureLocalPublicationDiagnostics' })
if($parameter.Count -ne 1 -or $null -ne $parameter[0].DefaultValue) { throw 'Diagnostic switch is not default-off' }
if([NLink.Tools.LocalRetainedSourceRequest+AdoptedPublication].GetConstructors().Count -ne 0) { throw 'Adopted publication gained a public factory' }
$TimeoutSeconds=30; $ProgressTimeoutSeconds=20
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=[guid]::NewGuid().ToString('D')
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='offline-workflow:1'
function Invoke-RouteAcceptanceChildScriptNoThrow {
    param($ScriptPath,$Arguments,$LocalWriterRelay=$null,$LocalWriterArtifactDir='')
    $script:calls.Add('wrapper')
    if($mode -ceq 'child-fail') { return 9 }
    if($LocalWriterRelay -is [ref]) { $LocalWriterRelay.Value=[pscustomobject]@{WriterContext='controlled-workflow-owner'} }
    return 0
}
function Invoke-RouteAcceptanceRetainedAnalysis {
    param($ArtifactDir,$LiveRouteProofMode,[switch]$CaptureLocalSourceOwnership,$LocalWriterContext=$null,$LocalPublication=$null)
    $script:calls.Add('analysis')
    if($null -ne (Get-Variable localPersistentPublication -ErrorAction SilentlyContinue) -or
        $null -ne (Get-Variable persistenceStarted -ErrorAction SilentlyContinue)) { throw 'Persistence locals leaked' }
    if($CaptureLocalSourceOwnership.IsPresent -ne ($mode -cnotin @('disabled','other-route','prefilled'))) { throw 'Persistence opt-in changed' }
    if($mode -ceq 'external-ref') { $LocalPublication.Value='controlled-external-result' }
    if($mode -cin @('copied','analysis-fail')) {
        $copy='{"TerminalReceipt":{"Status":"completed"}}' | ConvertFrom-Json
        $copy | Add-Member ScriptMethod TryPersistForAttempt { throw 'Lookalike method executed' }
        $LocalPublication.Value=$copy
    }
    if($mode -ceq 'analysis-fail') { throw 'original_analysis_failure' }
}
function Update-FileTransferTunaDirectEvidenceManifestFromReceipt { $script:calls.Add('manifest'); [pscustomobject]@{CurrentRuntimeProof=$true} }
foreach($mode in @('disabled','missing','copied','analysis-fail','child-fail','other-route','prefilled','external-ref')) {
    $script:calls=[Collections.Generic.List[string]]::new(); $failure=''
    $parameters=@{RepoRoot=$RepoRoot;ArtifactDir=$Directory;ResolvedExePath='unused';ResolvedWalletPath='unused';ResolvedSidecarPath='unused'
        EffectiveWalletPassword='offline';RouteMode='live-regular-activation-cycle';Fault='none'}
    if($mode -cne 'disabled') { $parameters.CaptureLocalPublicationDiagnostics=$true }
    if($mode -ceq 'other-route') { $parameters.RouteMode='preactivated' }
    $external=$null
    if($mode -ceq 'prefilled') { $external='existing-result' }
    if($mode -cin @('prefilled','external-ref')) { $parameters.LocalPublication=[ref]$external }
    try { Invoke-TunaRouteAcceptanceRun @parameters } catch { $failure=$_.Exception.Message }
    if($mode -ceq 'analysis-fail') { if($failure -cne 'original_analysis_failure') { throw 'Original analysis error changed' } }
    elseif($mode -ceq 'child-fail') { if($failure -notlike 'Tuna GUI wrapper *failed with exit code 9*') { throw 'Original child error changed' } }
    elseif($failure) { throw $failure }
    if($mode -ceq 'prefilled' -and $external -cne 'existing-result') { throw 'Prefilled caller output changed' }
    if($mode -ceq 'external-ref' -and $external -cne 'controlled-external-result') { throw 'External caller output changed' }
    $expected=if($mode -ceq 'child-fail'){'wrapper'}elseif($mode -ceq 'analysis-fail'){'wrapper,analysis'}else{'wrapper,analysis,manifest'}
    if(($script:calls -join ',') -cne $expected) { throw 'Primary order changed' }
    if([IO.Directory]::GetFileSystemEntries($Directory).Length -ne 0) { throw 'Missing/copied/disabled publication wrote files' }
}
'persistence-workflow={"Cases":8,"OriginalErrorsPreserved":true,"LookalikesRejected":true,"Live":false}'
