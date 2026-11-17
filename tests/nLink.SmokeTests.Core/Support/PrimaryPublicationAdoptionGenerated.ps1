param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string[]]$CaseSelection=@(),[string[]]$OwnerSelection=@('Wrapper','RouteAcceptance'))
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
function Parse([string]$Path) {
    $t=$null; $e=$null; $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$t,[ref]$e)
    if($e.Count) { throw "Parse failed: $Path" }; return $ast
}
function Definition($Ast,[string]$Name) {
    $found=@($Ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($found.Count -ne 1) { throw "Function ambiguous: $Name" }; return $found[0]
}
$route=Parse (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1')
$wrapper=Parse (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess')) { Invoke-Expression (Definition $route $name).Extent.Text }
foreach($name in @('Read-TunaGuiReportValue','Write-TunaGuiRetainedAnalysisTimeoutArtifacts',
    'Test-TunaGuiRetainedAnalysisCompletedProofArtifacts','Stop-TunaGuiRetainedAnalysisProcess')) { Invoke-Expression (Definition $wrapper $name).Extent.Text }
foreach($pair in @(@($route,'Invoke-RouteAcceptanceRetainedAnalysis'),@($wrapper,'Invoke-TunaGuiRetainedAnalysis'))) {
    $definition=Definition $pair[0] $pair[1]; $text=$definition.Extent.Text
    $deadline=@($definition.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$analysisTimeoutSeconds'},$true))
    $expectedWatchdog=if($pair[1] -ceq 'Invoke-RouteAcceptanceRetainedAnalysis') {'3600'} else {'1800'}
    if($deadline.Count -ne 1 -or $deadline[0].Right.Extent.Text -cne $expectedWatchdog) { throw 'Original watchdog changed' }
    $text=$text.Replace($deadline[0].Extent.Text,'$analysisTimeoutSeconds = 15').Replace('while (-not $process.WaitForExit(5000))','while (-not $process.WaitForExit(25))')
    $text=$text.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
    $loader="Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'"
    $text=$text.Replace($loader,"Join-Path `$script:moduleRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
    $before='$private:localTerminalReceipt = Write-FileTransferRetainedAnalysisAttemptReceipt'
    $inject=@'
if ($script:caseMode -ceq 'early-take') { if ($null -ne $private:localAdoption.Take($null)) { throw 'Early adoption succeeded' } }
if ($script:caseMode -ceq 'budget') {
    $private:clock=$private:localAdoption.GetType().GetField('work',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($private:localAdoption)
    $private:clock.Start(); [Threading.Thread]::Sleep(260); $private:clock.Stop()
}
'@
    if($text.Split([string[]]@($before),[StringSplitOptions]::None).Length -ne 2) { throw 'Actual receipt call changed' }
    $text=$text.Replace($before,$inject+"`n"+$before)
    $take='$private:localAdopted = $private:localAdoption.Take($private:localTerminalReceipt)'
    $inject=@'
if ($script:caseMode -ceq 'receipt-copy') { $private:localTerminalReceipt=$private:localTerminalReceipt | ConvertTo-Json -Depth 8 | ConvertFrom-Json }
if ($script:caseMode -ceq 'return-mutation') { $private:localTerminalReceipt.status='completed' }
if ($script:caseMode -ceq 'receipt-replay') { $null=$private:localAdoption.Take($private:localTerminalReceipt) }
'@
    if($text.Split([string[]]@($take),[StringSplitOptions]::None).Length -ne 2) { throw 'Actual adoption call changed' }
    $text=$text.Replace('-LocalPublicationCustody $private:localAdoption',
        '-LocalPublicationCustody $(if ($script:caseMode -ceq ''forged-custody'') { New-ForgedAdoptionCustody } else { $private:localAdoption })')
    Invoke-Expression $text.Replace($take,$inject+"`n"+$take+"`n`$script:adoptionReason=`$private:localAdoption.OmissionReason")
}
$script:moduleRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'; $RouteMode='live-regular-activation-cycle'
$script:atomic=(Get-Command Write-FileTransferEvidenceAtomicJson).ScriptBlock
$script:hash=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock
$script:lines=(Get-Command Get-FileTransferEvidenceLineCount).ScriptBlock
function Write-FileTransferEvidenceAtomicJson {
    param([string]$Path,$Value)
    if($Path.EndsWith('filetransfer-retained-analysis-attempt-receipt.json')) {
        $script:receiptWrites++
        foreach($name in @('LocalPublicationCustody','localAdoption','sourceRequest','LocalPublication','LocalWriterContext','localTerminalReceipt','localReceiptReturn')) {
            if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) { throw "Private adoption state leaked to writer: $name" }
        }
        if($script:caseMode -ceq 'receipt-write-fails') { throw 'injected_receipt_write_failure' }
        & $script:atomic -Path $Path -Value $Value
        if($script:caseMode -ceq 'receipt-mutation') { $Value.status='forged-after-write' }
    } else { & $script:atomic -Path $Path -Value $Value }
}
function Get-FileTransferEvidenceSha256 {
    param([string]$Path,$ReadLease=$null)
    if($Path -ceq $script:currentLog) { $script:sourceHashes++ }
    $value=& $script:hash -Path $Path -ReadLease $ReadLease
    if($Path -ceq $script:currentLog -and $script:caseMode -ceq 'input-mismatch' -and $script:sourceHashes -eq 2) { return '0'*64 }
    return $value
}
function Get-FileTransferEvidenceLineCount {
    param([string]$Path)
    if($Path -ceq $script:currentLog) { $script:sourceCounts++ }
    & $script:lines -Path $Path
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'No parent authority evaluation allowed' }
function New-ForgedAdoptionCustody {
    $value=[pscustomobject]@{Claim='copied-receipt-owner'}
    foreach($method in @('PrepareReceipt','RecordWrittenReceipt','Dispose')) {
        $value | Add-Member ScriptMethod $method { $script:forgedCalls++; throw 'Forged custody executed' }
    }
    return $value
}
$modes=@('baseline','completed','proof-incomplete','no-publication','nonzero','timeout','missing-artifact','wrong-catalog',
    'receipt-copy','receipt-mutation','receipt-write-fails','early-take','budget','formatter-isolation','receipt-replay','return-mutation','input-mismatch','prefilled','forged-custody')
if($CaseSelection.Count) {
    if(@($CaseSelection | Where-Object { $_ -cnotin $modes }).Count -or @($CaseSelection | Select-Object -Unique).Count -ne $CaseSelection.Count) { throw 'Unknown/duplicate adoption case' }
    $modes=@($modes | Where-Object { $_ -cin $CaseSelection })
}
if(@($OwnerSelection | Where-Object { $_ -cnotin @('Wrapper','RouteAcceptance') }).Count) { throw 'Invalid owner selection' }
$suite=Join-Path $Directory ('adoption-'+$PSVersionTable.PSVersion.Major+'-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $suite 'tools'))
[IO.File]::Copy((Join-Path $PSScriptRoot 'PrimaryPublicationAdoptionWorker.ps1'),(Join-Path $suite 'tools/FileTransfer-Ops.ps1'))
$originalLocation=Get-Location; $rows=[Collections.Generic.List[object]]::new()
try {
    Set-Location -LiteralPath $suite
    foreach($owner in $OwnerSelection) { foreach($mode in $modes) {
        $script:caseMode=$mode; $script:receiptWrites=0; $script:sourceHashes=0; $script:sourceCounts=0; $script:currentLog=''; $script:adoptionReason=''; $script:forgedCalls=0
        $completion=$null
        $null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $suite ($owner+'-'+$mode)) `
            -AcceptedCompletion ([ref]$completion) -GeneratedSource (Join-Path $Directory 'nlink.log') -GeneratedRun $RunId -GeneratedScenario $ScenarioId
        if($null -eq $completion) { throw 'Genuine GUI completion missing' }
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$RunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$ScenarioId
        $output=$completion.ArtifactRoot; $log=$completion.Outputs[3].Path
        $settings=Join-Path $output 'worker-settings.json'
        $workerMode=if($mode -cin @('baseline','no-publication','prefilled')) {'channel-no-completion'} else {'valid-0'}
        [IO.File]::WriteAllText($settings,(@{mode=$workerMode;lane='channel';path=$log;result=(Join-Path $output 'worker.json')} | ConvertTo-Json))
        [IO.File]::WriteAllText((Join-Path $output 'case.json'),(@{mode=$mode;repoRoot=$RepoRoot;workerSettings=$settings} | ConvertTo-Json))
        [IO.File]::WriteAllText((Join-Path $output 'filetransfer-tuna-gui-summary.json'),'{"completed":true,"integrityOk":true,"inboundTerminalState":"Completed","outboundTerminalState":"Completed"}')
        $publication=$null; if($mode -ceq 'prefilled') { $publication='preserve-existing' }
        $arguments=@{CaptureLocalSourceOwnership=$true;LocalWriterContext=$completion}
        if($mode -cne 'baseline') { $arguments.LocalPublication=[ref]$publication }
        $script:currentLog=$log; $failure=''
        try {
            if($owner -ceq 'Wrapper') { Invoke-TunaGuiRetainedAnalysis -RepoRoot $suite -AnalysisDir $output -LogPath $log -LiveRouteProofMode RegularActivationCycle @arguments }
            else { Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $output -LiveRouteProofMode RegularActivationCycle @arguments }
        } catch { $failure=$_.Exception.Message }
        $expectedAdoption=$mode -cin @('completed','proof-incomplete','formatter-isolation','return-mutation')
        if(($null -ne $publication -and $publication -isnot [string]) -ne $expectedAdoption) { throw "Adoption result drift: $owner/$mode : $failure : $script:adoptionReason" }
        if($mode -ceq 'prefilled' -and $publication -cne 'preserve-existing') { throw 'Existing output replaced' }
        if($script:forgedCalls -ne 0) { throw 'Caller-authored custody methods executed' }
        $expectedFailure=$mode -cin @('proof-incomplete','return-mutation','nonzero','timeout','missing-artifact','receipt-write-fails')
        if((-not [string]::IsNullOrEmpty($failure)) -ne $expectedFailure) { throw "Primary failure drift: $owner/$mode : $failure" }
        $receiptPath=Join-Path $output 'filetransfer-retained-analysis-attempt-receipt.json'
        if($mode -ceq 'receipt-write-fails') {
            if($failure -cne 'injected_receipt_write_failure' -or (Test-Path -LiteralPath $receiptPath)) { throw 'Failed receipt write was adopted or changed' }
            $status='write_failed'
        } else {
            $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
            $status=if($mode -ceq 'timeout') {'timed_out'} elseif($mode -ceq 'nonzero') {'failed'} elseif($mode -ceq 'input-mismatch') {'input_mutated'}
                elseif($mode -cin @('proof-incomplete','return-mutation','missing-artifact')) {'proof_incomplete'} else {'completed'}
            if($receipt.status -cne $status -or $receipt.owner -cne $owner -or $receipt.acceptanceRunId -cne $RunId -or $receipt.scenarioRunId -cne $ScenarioId) { throw "Terminal receipt drift: $owner/$mode" }
            if($expectedAdoption) {
                if($publication.TerminalReceipt.Status -cne $status -or $publication.TerminalReceipt.Path -cne $receiptPath -or
                    $publication.Completion.RequestId.ToString('N') -cne $receipt.attemptId -or $publication.Completion.WorkerProcessId -ne $receipt.processId -or
                    $publication.TerminalReceipt.CatalogSha256 -cne $publication.Completion.CatalogSha256) { throw 'Adoption lost exact terminal/worker/catalog binding' }
                foreach($value in @($publication,$publication.TerminalReceipt)) {
                    if($value.GetType().GetConstructors().Count) { throw 'Public adoption constructor' }
                    foreach($property in $value.GetType().GetProperties()) { if($null -ne $property.GetSetMethod()) { throw 'Mutable adoption metadata' } }
                }
                $bytes=$publication.CopyPayload(); $bytes[0]=0
                if($publication.CopyPayload()[0] -ne [byte][char]'{') { throw 'Mutable adopted payload' }
            }
        }
        if($script:receiptWrites -ne 1 -or $script:sourceHashes -ne 2 -or $script:sourceCounts -ne 2) { throw "Extra receipt write/source traversal: $owner/$mode : $script:receiptWrites/$script:sourceHashes/$script:sourceCounts" }
        if(Test-Path -LiteralPath (Join-Path $output 'late-optional.json')) { throw 'Timed-out worker survived' }
        $worker=Get-Content -LiteralPath (Join-Path $output 'worker.json') -Raw | ConvertFrom-Json
        if($worker.Evaluations -ne 2) { throw 'Authority evaluation count changed' }
        $facts=@{Owner=$owner;Mode=$mode;Status=$status;Adopted=$expectedAdoption;OriginalFailure=$expectedFailure;SourceHashes=2;SourceCounts=2;AuthorityEvaluations=2}
        [IO.File]::WriteAllText((Join-Path $output 'adoption-result.json'),($facts | ConvertTo-Json -Compress)); $rows.Add($facts)
    } }
} finally { Set-Location -LiteralPath $originalLocation.Path }
'publication-adoption='+([ordered]@{Cases=$rows.Count;Adopted=@($rows | Where-Object Adopted).Count;AuthorityEvaluationsPerWorker=2
    OriginalReceiptWritesPerParent=1;OriginalSourceHashesPerParent=2;OriginalSourceCountsPerParent=2;ProductionWatchdogSeconds=@{Wrapper=1800;RouteAcceptance=3600}
    TestWatchdogSeconds=15;LiveExport=$false;Reports='controlled_lifecycle_with_original_generated_catalog'} | ConvertTo-Json -Compress)
