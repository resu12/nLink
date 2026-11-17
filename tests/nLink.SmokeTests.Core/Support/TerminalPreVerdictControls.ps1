param([string]$RepoRoot,[string]$Output)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TerminalPreVerdictInventory.ps1')
function Require($Condition,[string]$Reason) {if(-not $Condition){throw $Reason}}
function Reject([scriptblock]$Action,[string]$Reason) {
    $message=''; try {& $Action | Out-Null} catch {$message=$_.Exception.Message}
    Require ($message.Contains($Reason)) ('expected_rejection:'+ $Reason+':'+$message)
}
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
Require ($errors.Count -eq 0) 'analyzer_parse'
function Body([string]$Name) {
    $nodes=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    Require ($nodes.Count -eq 1) 'source_function'
    $text=$nodes[0].Body.Extent.Text
    return $text.Substring(1,$text.Length-2).Replace("`r`n","`n")
}
$primary=Body 'Write-FileTransferCompactRetainedAnalysisArtifacts'
$reader=Body 'Read-FileTransferCompactGuiSummary'
$observer=Join-Path $PSScriptRoot 'TerminalPreVerdictObserver.ps1'
$instrumented=& $observer -Primary $primary -GuiReader $reader -SourceProbe '& { throw ''unexpected_probe'' }'
[void][Management.Automation.Language.Parser]::ParseInput($instrumented.Primary,[ref]$tokens,[ref]$errors)
Require ($errors.Count -eq 0) 'instrumented_primary_parse'
Set-Item Function:Read-FileTransferCompactGuiSummary -Value ([scriptblock]::Create($instrumented.GuiReader))
$rows=@(
    @('absent',$null,'absent','owned_integrity_proven'),
    @('true','{"integrityOk":true}','read','owned_integrity_proven'),
    @('false','{"integrityOk":false}','read','conflict'),
    @('missing','{}','read','invalid_input'),
    @('null','{"integrityOk":null}','read','invalid_input'),
    @('string','{"integrityOk":"false"}','read','invalid_input'),
    @('number','{"integrityOk":1}','read','invalid_input'),
    @('malformed','{','unreadable','invalid_input'),
    @('locked','{"integrityOk":true}','unreadable','invalid_input'),
    @('null-document','null','read','invalid_input')
)
foreach($row in $rows) {
    $dir=Join-Path $Output $row[0]; [void][IO.Directory]::CreateDirectory($dir)
    $path=Join-Path $dir 'filetransfer-tuna-gui-summary.json'; $lock=$null
    try {
        if($null -ne $row[1]) {[IO.File]::WriteAllText($path,$row[1])}
        if($row[0] -ceq 'locked') {$lock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
        $gui=Read-FileTransferCompactGuiSummary $dir
        Require ($script:terminalGuiReadStatus -ceq $row[2]) ('gui_read:'+ $row[0])
        Require ((Get-TerminalIntegrityShadowState $script:terminalGuiReadStatus $gui $true) -ceq $row[3]) ('gui_state:'+ $row[0])
        $without=if($row[0] -ceq 'false'){'explicit_failure'}elseif($row[0] -cin @('absent','true')){'owned_integrity_unproven'}else{'invalid_input'}
        Require ((Get-TerminalIntegrityShadowState $script:terminalGuiReadStatus $gui $false) -ceq $without) ('gui_without_owner:'+ $row[0])
    } finally {if($null -ne $lock){$lock.Dispose()}}
}
Require ((Get-TerminalIntegrityShadowState 'unknown' $null $true) -ceq 'invalid_input') 'unknown_read'
$counts=[ordered]@{}; foreach($key in $script:TerminalIntegrityCountFactors){$counts[$key]=0L}
$frame=[ordered]@{
    GuiCompleted=$true;GuiIntegrityOk=$false;HardFailureCount=0;ActiveFileTunaV6Count=0
    RouteConsistency=[pscustomobject]@{Verdict='pass'};LiveRouteProof=[pscustomobject]@{Verdict='fail'}
    LiveRouteProofMode='RegularActivationCycle';PreTerminalLivenessTimeoutCount=0;WarningCap=$null
    WarningKinds=@();Candidates=@();CandidateArray=@();FirstFailure=$null
    Mismatch=[pscustomobject]@{CandidateRecords=@();EffectiveCandidateRecords=@()}
    Snapshot=[pscustomobject]@{Mode='SnapshotV1Authoritative';ArtifactEvidenceVerdict='pass'};Parity=[pscustomobject]@{Verdict='pass'}
    FallbackTailAuthoritative=$false;ReceiveRecoverySelected=$true;Counts=$counts
}
foreach($key in $script:TerminalIntegrityCountFactors) {
    $counts[$key]=1L
    $result=Get-TerminalPreVerdictInventory $frame
    Require ($result.Factors.Count -eq 58 -and $result.Factors[$key] -eq 1 -and $result.Factors.LiveRouteProof -ceq 'fail' -and -not $result.Factors.GuiIntegrityOk) 'independent_count_factor'
    $counts[$key]=0L
}
# Independent simultaneous failures remain visible even though first-edge output is singular.
Reject {Get-TerminalPreVerdictInventory $frame -AvailableVisits 57} 'integrity_inventory_remaining_budget'
Reject {Get-TerminalPreVerdictInventory $frame -AvailableUnits 0} 'integrity_inventory_code_unit_limit'
$frame.HardFailureCount=1; $frame.ActiveFileTunaV6Count=1; $frame.PreTerminalLivenessTimeoutCount=1
$frame.WarningCap=[pscustomobject]@{ExceededKinds=@('test_cap')}; $frame.WarningKinds=@('test_warning')
$frame.Snapshot.ArtifactEvidenceVerdict='fail'; $frame.Parity.Verdict='fail'; $frame.GuiCompleted=$false
$first=[pscustomobject]@{Class='file_tuna_data_path_mismatch';Reason='first';Variant='test';Event=[pscustomobject]@{EventName='one';Sequence=1L;LineNumber=3}}
$later=[pscustomobject]@{Class='route_failure';Reason='later';Event=[pscustomobject]@{EventName='two';Sequence=2L;LineNumber=5}}
$frame.Candidates=@($first,$later); $frame.CandidateArray=@($first,$later); $frame.FirstFailure=$first
$recovered=[pscustomobject]@{Recovered=$true;Event=$first.Event;RejectionDiagnostic=[pscustomobject]@{Reason='recovered_diagnostic'}}
$unresolved=[pscustomobject]@{Recovered=$false;Event=$later.Event;RejectionDiagnostic=[pscustomobject]@{Reason='unresolved_diagnostic'}}
$frame.Mismatch.CandidateRecords=@($recovered,$unresolved); $frame.Mismatch.EffectiveCandidateRecords=@($unresolved)
$result=Get-TerminalPreVerdictInventory $frame
Require ($result.Candidates.Count -eq 2 -and $result.FirstSelectedIndex -eq 0 -and $result.MismatchCandidates.Count -eq 2 -and
    $result.MismatchCandidates[0].Recovered -and $result.EffectiveMismatchCandidates.Count -eq 1 -and
    $result.Factors.HardFailureCount -eq 1 -and $result.Factors.ActiveFileTunaV6Count -eq 1 -and
    $result.Factors.PreTerminalLivenessTimeoutCount -eq 1 -and $result.Factors.ArtifactVerdict -ceq 'fail' -and
    $result.Factors.ParityVerdict -ceq 'fail' -and -not $result.Factors.GuiCompleted -and
    $result.WarningCapExceededKinds[0] -ceq 'test_cap' -and $result.WarningKinds[0] -ceq 'test_warning') 'simultaneous_and_recovered'
$counts.Remove($script:TerminalIntegrityCountFactors[0])
Reject {Get-TerminalPreVerdictInventory $frame} 'integrity_inventory_count_fields'
$counts[$script:TerminalIntegrityCountFactors[0]]=0L
$frame.CandidateArray=@($later,$first)
Reject {Get-TerminalPreVerdictInventory $frame} 'integrity_inventory_candidate_identity'
$frame.Candidates=@($first)*700; $frame.CandidateArray=$frame.Candidates
Reject {Get-TerminalPreVerdictInventory $frame} 'integrity_inventory_record_limit'
foreach($changed in @(
    $primary.Replace("    `$verdict = 'PASS'","    `$verdict = 'changed'"),
    $primary.Replace('$private:localPrimaryInputs = $null','$private:localPrimaryInputs = $null'+"`n"+'$private:localPrimaryInputs = $null'),
    $primary.Replace('$runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount','$newUnmappedProof.UnresolvedCount')
)) {Reject {& $observer -Primary $changed -GuiReader $reader -SourceProbe 'unused'} 'pre_verdict_'}
# An exception before the optional seam is not masked by its deferred cleanup.
$start=$primary.IndexOf("    `$verdict = 'PASS'")
$end=$primary.IndexOf('    $recoveryFailureClass = if ($null -ne $firstCompactProofChainFailure) {')
$early='$private:localPrimaryInputs = $null'+"`ntry { throw 'original_early_exit'`n"+$primary.Substring($start,$end-$start)+
    '    $recoveryFailureClass = if ($null -ne $firstCompactProofChainFailure) { ''unused'' }'+"`n} finally {`n"+
    '$private:localPublicationWitness = $null'+"`n"+'        $private:localCompletedEvaluations = $null'+"`n}"
$earlyHook=& $observer -Primary $early -GuiReader $reader -SourceProbe 'unused'
Reject {& ([scriptblock]::Create($earlyHook.Primary))} 'original_early_exit'
$selectedDir=Join-Path $Output 'selected-failure'; [void][IO.Directory]::CreateDirectory($selectedDir)
$selected=$early.Replace("throw 'original_early_exit'",('$firstCompactProofChainFailure=[pscustomobject]@{Class=''test''}; $ArtifactDir='''+$selectedDir.Replace("'","''")+''''))
$script:unexpectedConsumption=0
$selectedHook=& $observer -Primary $selected -GuiReader $reader -SourceProbe '& { $script:unexpectedConsumption++; throw ''unexpected_consumption'' }'
$selectedHook.Primary=$selectedHook.Primary.Replace("    `$verdict = 'PASS'",("    return 'original_selected_failure_continues'`n    `$verdict = 'PASS'"))
Require ((& ([scriptblock]::Create($selectedHook.Primary))) -ceq 'original_selected_failure_continues') 'selected_failure_original_continues'
Require ($script:unexpectedConsumption -eq 0 -and [IO.File]::ReadAllText((Join-Path $selectedDir 'terminal-source-probe-error.txt')).Contains('integrity_shadow_selected_failure_writer_reserved')) 'selected_failure_writer_reserved'
[IO.File]::WriteAllText((Join-Path $Output 'controls.json'),([ordered]@{CountFactorControls=42;GuiReadControls=$rows.Count;
    UnknownReadRejected=$true;MissingFactorRejected=$true;CandidateIdentityRejected=$true;BudgetRejected=$true;
    SimultaneousFailuresPreserved=$true;RecoveredCandidatesPreserved=$true;SeamControls=3;EarlyExceptionPreserved=$true;
    SelectedFailureWriterReserved=$true;AuthorityEvaluations=0;AcceptanceChanged=$false;ControlsAreSynthetic=$true} | ConvertTo-Json -Compress))
