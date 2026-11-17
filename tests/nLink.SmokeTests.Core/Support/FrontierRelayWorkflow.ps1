# The real workflow receives original resources from the completed real launcher.
# Replace only its external calls to check opt-in routing and invocation order.
$tokens=$null; $errors=$null
$workflowAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
$definition=@($workflowAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-TunaRouteAcceptanceRun'},$true))
if($errors.Count -or $definition.Count -ne 1) {throw 'Original workflow parse failed'}
Invoke-Expression $definition[0].Extent.Text
$script:workflowRelay=$completion; $script:workflowCustody=$exported
function Invoke-RouteAcceptanceChildScriptNoThrow {
    param($ScriptPath,$Arguments,$LocalWriterRelay=$null,$LocalWriterArtifactDir='',$FrontierRelayCustody=$null)
    $script:workflowCalls.Add('wrapper')
    if(($FrontierRelayCustody -is [ref]) -ne ($script:workflowMode -ceq 'native')) {throw 'Workflow widened or lost native relay opt-in'}
    if($LocalWriterRelay -is [ref]) {$LocalWriterRelay.Value=$script:workflowRelay}
    if($FrontierRelayCustody -is [ref]) {$FrontierRelayCustody.Value=$script:workflowCustody}
    return 0
}
function Invoke-RouteAcceptanceRetainedAnalysis {
    param($ArtifactDir,$LiveRouteProofMode,[switch]$CaptureLocalSourceOwnership,[switch]$CaptureFrontierReports,
        $LocalWriterContext=$null,$LocalPublication=$null,$FrontierRelayCustody=$null)
    $script:workflowCalls.Add('analysis')
    $native=$script:workflowMode -ceq 'native'
    if($CaptureFrontierReports.IsPresent -ne $native -or ($null -ne $FrontierRelayCustody) -ne $native) {throw 'Workflow changed native primary opt-in'}
    if($native -and (-not [object]::ReferenceEquals($FrontierRelayCustody,$script:workflowCustody) -or
        -not [object]::ReferenceEquals($LocalWriterContext,$script:workflowRelay.WriterContext) -or -not $FrontierRelayCustody.IsCurrent)) {throw 'Workflow replaced original accepted native ownership'}
}
function Update-FileTransferTunaDirectEvidenceManifestFromReceipt {
    $script:workflowCalls.Add('manifest'); return [pscustomobject]@{CurrentRuntimeProof=$true}
}
$TimeoutSeconds=100; $ProgressTimeoutSeconds=30
foreach($script:workflowMode in @('disabled','wrong-route','wrong-proof','prefilled','native')) {
    $script:workflowCalls=[Collections.Generic.List[string]]::new()
    $publication=if($script:workflowMode -ceq 'prefilled') {'preserve-existing'} else {$null}
    $parameters=@{RepoRoot=$RepoRoot;ArtifactDir=$Directory;ResolvedExePath='unused';ResolvedWalletPath='unused';ResolvedSidecarPath='unused';
        EffectiveWalletPassword='test-only';RouteMode='live-regular-activation-cycle';Fault='none';LiveProofMode='RegularActivationCycle';
        LocalPublication=[ref]$publication;CaptureLocalPublicationDiagnostics=($script:workflowMode -cne 'disabled')}
    if($script:workflowMode -ceq 'wrong-route') {$parameters.RouteMode='preactivated'}
    if($script:workflowMode -ceq 'wrong-proof') {$parameters.LiveProofMode='None'}
    Invoke-TunaRouteAcceptanceRun @parameters
    if(($script:workflowCalls -join ',') -cne 'wrapper,analysis,manifest') {throw 'Workflow reordered original analysis or reconciliation'}
    if($script:workflowMode -ceq 'prefilled' -and $publication -cne 'preserve-existing') {throw 'Workflow replaced existing output'}
    if($script:workflowCustody.IsCurrent -ne ($script:workflowMode -cne 'native')) {throw 'Workflow lost or retained native custody outside its scope'}
}
'frontier-relay-workflow=5'
