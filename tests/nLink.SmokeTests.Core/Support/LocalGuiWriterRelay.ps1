param([string]$RepoRoot,[string]$Directory,[string[]]$CaseSelection=@())
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$t=$null;$e=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$t,[ref]$e)
if($e.Count) { throw 'Route parent parse failed' }
$found=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-RouteAcceptanceChildScriptNoThrow'},$true))
if($found.Count -ne 1) { throw 'Actual launcher ambiguous' }
$text=$found[0].Extent.Text.Replace("Join-Path `$PSScriptRoot 'FileTransferOps/LocalGuiWriterRequest.ps1'","Join-Path `$script:moduleRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1'")
$bind='[void]$private:relayRequest.BindWorker($private:relayProcess)'
$text=$text.Replace($bind,@'
if ($script:mode -ceq 'wrong-worker') { [void]$private:relayRequest.BindWorker((Get-Process -Id $PID)) }
else { [void]$private:relayRequest.BindWorker($private:relayProcess) }
if ($script:mode -ceq 'early-take') { if ($null -ne $private:relayRequest.TakeRelayAfterExit()) { throw 'Running Wrapper adopted' } }
if ($script:mode -ceq 'cancelled') { $private:relayRequest.Dispose() }
'@)
$take='$private:relayedWriter = $private:relayRequest.TakeRelayAfterExit()'
$text=$text.Replace($take,@'
if ($script:mode -ceq 'repeat-take') { $null=$private:relayRequest.TakeRelayAfterExit() }
if ($script:mode -ceq 'wrong-api') { if ($null -ne $private:relayRequest.TakeAfterExit()) { throw 'Relay interpreted as GUI finalizer completion' } }
if ($script:mode -cin @('bad-magic','partial','truncated','oversize','wrong-pid')) {
    $private:map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($private:relayRequest.Name)
    $private:view=$private:map.CreateViewAccessor()
    try {
        switch ($script:mode) {
            'bad-magic' { $private:view.Write(0L,0) }
            'partial' { $private:view.Write(48L,1) }
            'truncated' { $private:view.Write(52L,1) }
            'oversize' { $private:view.Write(52L,65537) }
            'wrong-pid' { $private:view.Write(16L,$PID) }
        }
    } finally { $private:view.Dispose(); $private:map.Dispose() }
}
$private:relayedWriter = $private:relayRequest.TakeRelayAfterExit()
'@)
$text=$text.Replace('$private:relayOutputCaptured = $true',"if (`$script:mode -ceq 'output-fails') { throw 'injected_output_capture_failure' }; `$private:relayOutputCaptured = `$true")
Invoke-Expression $text
$script:moduleRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='gui-writer-parent-run'; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='gui-writer-parent-run:scenario:1'
$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT='preserve-parent'
$env:NLINK_GUI_WRITER_RELAY='unrelated-inherited-request'; $env:NLINK_GUI_WRITER_RELAY_ID=[guid]::NewGuid().ToString('D')
function Write-Host {
    param($Object)
    foreach($name in @('relayRequest','relayProcess','relayedWriter','LocalWriterRelay','LocalPublication')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) { throw "Private relay state leaked: $name" }
    }
    if($script:mode -ceq 'host-output-fails') { throw 'injected_host_output_failure' }
    $script:output.Add([string]$Object)
}
foreach($type in @([NLink.Tools.LocalGuiWriterRequest+Relay],[NLink.Tools.LocalGuiWriterRequest+RelayedCompletion])) {
    if($type.GetConstructors().Count -or @($type.GetProperties() | Where-Object { $null -ne $_.GetSetMethod() }).Count) { throw 'Public relay constructor/setter' }
}
$modes=@('baseline','valid','prefilled','no-completion','wrong-run','wrong-root','forged','decoded-copy','repeat-forward',
    'wrapper-nonzero','gui-nonzero','gui-timeout','gui-output-fails','wrong-worker','early-take','cancelled','repeat-take',
    'bad-magic','partial','truncated','oversize','wrong-pid','output-fails','host-output-fails','wrong-api')
if($CaseSelection.Count) {
    if(@($CaseSelection | Where-Object { $_ -cnotin $modes }).Count) { throw 'Unknown relay case selection' }
    $modes=@($modes | Where-Object { $_ -cin $CaseSelection })
}
$rows=[Collections.Generic.List[object]]::new()
$genuine=$null
foreach($script:mode in $modes) {
    $case=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($case)
    $guiMode=if($mode -ceq 'gui-nonzero') {'nonzero'} elseif($mode -ceq 'gui-timeout') {'timeout'} elseif($mode -ceq 'gui-output-fails') {'output-fails'} else {'valid'}
    $root=Join-Path (Join-Path $case 'gui') $guiMode
    $value=$null; if($mode -ceq 'prefilled') { $value='existing-owner' }
    $probe='space "quoted" tail\ unicode-'+[char]0x17e
    if($mode -cin @('baseline','prefilled')) { $probe='space tail\ unicode-'+[char]0x17e }
    $arguments=@('-RepoRoot',$RepoRoot,'-Directory',$case,'-Mode',$mode,'-Probe',$probe)
    $parameters=@{ScriptPath=(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRelayWorker.ps1');Arguments=$arguments}
    if($mode -cne 'baseline') { $parameters.LocalWriterRelay=[ref]$value; $parameters.LocalWriterArtifactDir=$root }
    $script:output=[Collections.Generic.List[string]]::new(); $failure=''; $code=-999
    try { $code=Invoke-RouteAcceptanceChildScriptNoThrow @parameters } catch { $failure=$_.Exception.Message }
    $expected=$mode -cin @('valid','repeat-forward')
    if(($null -ne $value -and $value -isnot [string]) -ne $expected) { throw "Relay adoption drift $mode : $code : $failure : $($script:output -join ' | ')" }
    if($mode -ceq 'prefilled' -and $value -cne 'existing-owner') { throw 'Prefilled output replaced' }
    if($mode -ceq 'host-output-fails') { if($failure -cne 'injected_host_output_failure') { throw 'Host output failure changed' } }
    elseif($failure -or $code -ne $(if($mode -ceq 'wrapper-nonzero'){9}elseif($mode -ceq 'output-fails'){1}else{0})) { throw "Original Wrapper outcome drift $mode : $code : $failure : $($script:output -join ' | ')" }
    $worker=Get-Content -LiteralPath (Join-Path $case 'worker.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($worker.Probe -cne $probe) { throw "Argument quoting drift: $mode" }
    if($expected) {
        if($value.WrapperProcessId -ne $worker.WrapperPid -or $value.WriterContext.GuiProcessId -ne $worker.GuiPid -or
            $value.WrapperProcessId -eq $value.WriterContext.GuiProcessId -or $value.WrapperProcessCreationFileTime -le 0 -or
            $value.WriterContext.ArtifactRoot -cne $root -or $value.WriterContext.AcceptanceRunId -cne $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID) { throw 'Relay lost separate original process/scope identity' }
        $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold($value.WriterContext.Outputs[3].Path)
        $request=$null
        try {
            $request=[NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithPublication([guid]::NewGuid(),'RouteAcceptance',
                $value.WriterContext.AcceptanceRunId,$value.WriterContext.ScenarioRunId,$hash.Lease,$value.WriterContext.Outputs[3].LineCount,$value.WriterContext)
            if($null -eq $request) { throw 'Real retained source request rejected the relayed GUI owner' }
        } finally { if($null -ne $request){$request.Dispose()}; if($null -ne $hash.Lease){$hash.Lease.Dispose()} }
        if($mode -ceq 'valid') { $genuine=$value }
    }
    if($mode -cnotin @('output-fails','host-output-fails')) {
        foreach($line in @('relay-stdout-1','relay-stdout-2','relay-stderr-1','relay-stderr-2')) {
            if(@($script:output | Where-Object { $_ -ceq $line }).Count -ne 1) { throw "Wrapper output lost/duplicated: $mode/$line" }
        }
        if($script:output.IndexOf('relay-stdout-1') -ge $script:output.IndexOf('relay-stdout-2') -or
            $script:output.IndexOf('relay-stderr-1') -ge $script:output.IndexOf('relay-stderr-2')) { throw 'Per-stream output order changed' }
    }
    if($env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT -cne 'preserve-parent' -or $env:NLINK_GUI_WRITER_RELAY -cne 'unrelated-inherited-request') { throw 'Parent environment changed' }
    $facts=@{Mode=$mode;Adopted=$expected;ExitCode=$code;OriginalFailure=$failure;GuiMode=$guiMode}
    [IO.File]::WriteAllText((Join-Path $case 'relay-result.json'),($facts | ConvertTo-Json -Compress)); $rows.Add($facts)
}
$workflowCases=0
if($null -ne $genuine) {
    $workflow=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-TunaRouteAcceptanceRun'},$true))
    if($workflow.Count -ne 1) { throw 'Actual RouteAcceptance workflow ambiguous' }
    Invoke-Expression $workflow[0].Extent.Text
    $script:genuine=$genuine
    function Invoke-RouteAcceptanceChildScriptNoThrow {
        param($ScriptPath,$Arguments,$LocalWriterRelay=$null,$LocalWriterArtifactDir='')
        $script:calls.Add('wrapper')
        if($script:workflowMode -ceq 'child-fail') { return 9 }
        if($LocalWriterRelay -is [ref] -and $script:workflowMode -cne 'missing-relay') { $LocalWriterRelay.Value=$script:genuine }
        return 0
    }
    function Invoke-RouteAcceptanceRetainedAnalysis {
        param($ArtifactDir,$LiveRouteProofMode,[switch]$CaptureLocalSourceOwnership,$LocalWriterContext=$null,$LocalPublication=$null)
        $script:calls.Add('analysis')
        $expected=$script:workflowMode -cin @('enabled','proof-fail')
        if($CaptureLocalSourceOwnership.IsPresent -ne $expected -or ($null -ne $LocalWriterContext) -ne $expected) { throw 'Route workflow ownership opt-in drift' }
        if($expected) {
            if(-not [object]::ReferenceEquals($LocalWriterContext,$script:genuine.WriterContext)) { throw 'Route workflow copied GUI context' }
            $LocalPublication.Value='test-only-adopted-diagnostic'
        }
        if($script:workflowMode -ceq 'proof-fail') { throw 'original_proof_failure' }
    }
    function Update-FileTransferTunaDirectEvidenceManifestFromReceipt { $script:calls.Add('manifest'); return [pscustomobject]@{CurrentRuntimeProof=$true} }
    foreach($script:workflowMode in @('baseline','enabled','prefilled','child-fail','missing-relay','proof-fail')) {
        $script:calls=[Collections.Generic.List[string]]::new(); $publication=$null; $failure=''
        $parameters=@{RepoRoot=$RepoRoot;ArtifactDir=$genuine.WriterContext.ArtifactRoot;ResolvedExePath='unused';ResolvedWalletPath='unused';ResolvedSidecarPath='unused'
            EffectiveWalletPassword='test-only';RouteMode='live-regular-activation-cycle';Fault='none';LiveProofMode='RegularActivationCycle'}
        $TimeoutSeconds=30; $ProgressTimeoutSeconds=10
        if($workflowMode -ceq 'prefilled') { $publication='existing' }
        if($workflowMode -cne 'baseline') { $parameters.LocalPublication=[ref]$publication }
        try { Invoke-TunaRouteAcceptanceRun @parameters } catch { $failure=$_.Exception.Message }
        $expectedFailure=$workflowMode -cin @('child-fail','proof-fail')
        if((-not [string]::IsNullOrEmpty($failure)) -ne $expectedFailure) { throw "Workflow failure drift $workflowMode : $failure" }
        $expectedCalls=if($workflowMode -ceq 'child-fail') {'wrapper'} elseif($workflowMode -ceq 'proof-fail') {'wrapper,analysis'} else {'wrapper,analysis,manifest'}
        if(($script:calls -join ',') -cne $expectedCalls) { throw 'Original workflow ordering changed' }
        if($workflowMode -ceq 'proof-fail' -and $failure -cne 'original_proof_failure') { throw 'Primary proof failure changed' }
        if($workflowMode -cin @('enabled','proof-fail') -and $publication -cne 'test-only-adopted-diagnostic') { throw 'Failed-proof diagnostic lost' }
        if($workflowMode -ceq 'prefilled' -and $publication -cne 'existing') { throw 'Existing publication replaced' }
        $workflowCases++
    }
}
'gui-writer-relay='+(@{Cases=$rows.Count;WorkflowCases=$workflowCases;Adopted=@($rows | Where-Object Adopted).Count;LiveExport=$false;AuthorityEvaluations=0;SeparateWrapperAndGuiIdentity=$true} | ConvertTo-Json -Compress)
