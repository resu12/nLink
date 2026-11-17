# Dot-sourced only by the fresh generated full/reduced evaluator harness.
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeInputBinding.ps1')
$bindingOrigins=@{}
foreach($role in @('fullCapture','fullSnapshot','compactCapture','compactSnapshot')) {
    $offsets=[Collections.Generic.List[long]]::new()
    foreach($raw in $script:nativeInputs[$role]) {
        $matches=@(for($j=0; $j -lt $lines.Length; $j++) { if($lines[$j] -ceq $raw) { $j+1L } })
        if($matches.Count -ne 1) { throw 'Fixture does not have unique original membership' }
        $offsets.Add($matches[0])
    }
    $bindingOrigins[$role]=$offsets.ToArray()
}
$selection=@(for($j=0; $j -lt $nativeFull.Length; $j++) {
    if($nativeFull[$j] -notmatch '; publication_use_kind=') { continue }
    $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $nativeFull[$j]
    @{SourceLine=$bindingOrigins.fullCapture[$j];Writer=[string]$f.log_writer_id;WriterSequence=[long]$f.log_writer_sequence
      ContentSha256=(Get-FileTransferLocalCarrierHash $nativeFull[$j])}
})
$bindingRequest=@{RequestId=$request;AcceptanceRunId=$RunId;ScenarioRunId=$ScenarioId;Selection=$selection}
$bindingArgs=@{Request=$bindingRequest;SourceLines=$lines;WriterLines=[IO.File]::ReadAllLines($writerPath)
    WriterEvidence=$ledger;NativeOwner=$owner;NativeInputs=$prepared;NativeLines=$script:nativeInputs
    Origins=$bindingOrigins;FullView=$full;CompactView=$compact}
function Bind($Overrides=@{}) {
    $arguments=$bindingArgs.Clone()
    foreach($key in $Overrides.Keys) { $arguments[$key]=$Overrides[$key] }
    # No file-based index construction, hashing or authority evaluation is
    # permitted inside the new binding consumer, even on a malformed request.
    function New-FileTransferPublicationUseIndex { $script:bindingForbiddenCalls++; throw 'Binder constructed file use index' }
    function New-FileTransferPublicationForwardingIndex { $script:bindingForbiddenCalls++; throw 'Binder constructed file forwarding index' }
    function New-FileTransferLocalPublicationEvidenceCarrier { $script:bindingForbiddenCalls++; throw 'Binder invoked whole-input C0 producer' }
    function Get-FileTransferEvidenceSha256 { $script:bindingForbiddenCalls++; throw 'Binder read a file' }
    function Get-FileTransferCoordinatorSnapshotEvaluation { $script:bindingForbiddenCalls++; throw 'Binder reevaluated authority' }
    Resolve-FileTransferLocalNativeInputBinding @arguments
}
function Clone($Value) { return ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Bad([string]$Name,[hashtable]$Overrides) {
    $result=Bind $Overrides
    if($result.Status -cne 'omitted' -or $null -ne $result.Json -or $result.PublicationCompletionIssued -or
        $result.FileOwnershipVerified -or $result.EvaluatorInputOwnershipVerified) { throw "Accepted invalid binding: $Name" }
    if($result.Reason -cin @('binding_work_budget','binding_native_verification_omitted')) {
        throw "Budget omission masked negative control: $Name"
    }
    $script:bindingNegatives.Add($Name)
}
$script:bindingNegatives=[Collections.Generic.List[string]]::new()
$script:bindingForbiddenCalls=0
$bindingWatch=[Diagnostics.Stopwatch]::StartNew()
$binding=Bind
$bindingWatch.Stop()
if($binding.Status -cne 'matched_local_native_binding') { throw "Generated binding omitted: $($binding.Reason)" }
$body=$binding.Json | ConvertFrom-Json
if($body.rows.Count -ne 8 -or $binding.FileOwnershipVerified -or $binding.EvaluatorInputOwnershipVerified -or
    $binding.PublicationCompletionIssued) { throw 'Binding promoted diagnostic association to completion ownership' }
for($j=0; $j -lt $selection.Count; $j++) {
    $p=$body.provenance[$j]; $nativeLine=$p.full.useLine
    $oracle=Get-FileTransferLocalPublicationRow $fullIndex $nativeLine
    if((ConvertTo-FileTransferLocalCarrierJson $body.rows[$j]) -cne (ConvertTo-FileTransferLocalCarrierJson $oracle.Row) -or
        $p.sourceLine -ne $selection[$j].SourceLine -or $p.sourceLine -eq $nativeLine -or
        $p.full.effectLine -eq $p.compact.effectLine) { throw 'Original/native row association disagrees with A/B1 oracle' }
}
# All mutations act on detached copies, never the primary view or owner.
$r=$bindingRequest.Clone(); $r.RequestId=[guid]::NewGuid(); Bad 'cross-request' @{Request=$r}
$r=$bindingRequest.Clone(); $r.AcceptanceRunId='other-run'; Bad 'wrong-run' @{Request=$r}
$r=$bindingRequest.Clone(); $r.ScenarioRunId='other-scenario'; Bad 'wrong-scenario' @{Request=$r}
$r=$bindingRequest.Clone(); $r.AcceptanceRunId=1L; Bad 'non-string-run' @{Request=$r}
$r=$bindingRequest.Clone(); $r.Selection=@(); Bad 'empty-selection' @{Request=$r}
$r=$bindingRequest.Clone(); $r.Selection=@($selection[0],$selection[0]); Bad 'duplicate-selection' @{Request=$r}
$r=$bindingRequest.Clone(); $r.Selection=@(1..65 | ForEach-Object { $selection[0] }); Bad 'selection-cap' @{Request=$r}
foreach($key in @('SourceLine','Writer','WriterSequence','ContentSha256')) {
    $s=$selection[0].Clone()
    $s[$key]=switch($key) {
        'SourceLine' {1L}
        'Writer' {'other-writer'}
        'WriterSequence' {1L}
        'ContentSha256' {('0'*64)}
    }
    $r=$bindingRequest.Clone(); $r.Selection=@($s); Bad ('selection-'+$key) @{Request=$r}
}
foreach($key in @('WriterGapCount','WriterDuplicateCount','WriterIdentityMismatchCount','ChronologyInversionCount','ChronologyCollisionCount')) {
    $l=Clone $ledger; $l.$key=1; Bad ('ledger-'+$key) @{WriterEvidence=$l}
}
$l=Clone $ledger; $l.ChronologyBatchInvalid=$true; Bad 'ledger-batch-invalid' @{WriterEvidence=$l}
$l=Clone $ledger; $l.Writers[0].AcceptanceRunId='other'; Bad 'ledger-writer-owner' @{WriterEvidence=$l}
$s=$lines.Clone(); $s[$selection[0].SourceLine-1]+=' '; Bad 'source-content-changed' @{SourceLines=$s}
$s=[string[]]@($lines + $lines[$selection[0].SourceLine-1]); Bad 'duplicate-source-membership' @{SourceLines=$s}
$w=$bindingArgs.WriterLines.Clone(); $w[0]+='; log_writer_sequence=1'; Bad 'duplicate-writer-field' @{WriterLines=$w}
$w=$bindingArgs.WriterLines.Clone(); $w[1]=$w[0]; Bad 'duplicate-writer-sequence' @{WriterLines=$w}
foreach($role in @('fullCapture','fullSnapshot','compactCapture','compactSnapshot')) {
    $o=$bindingOrigins.Clone(); $o[$role]=$bindingOrigins[$role].Clone(); $o[$role][0]=0L
    Bad ('synthetic-origin-'+$role) @{Origins=$o}
    $o=$bindingOrigins.Clone(); $o[$role]=$bindingOrigins[$role].Clone(); $o[$role][1]=$o[$role][0]
    Bad ('duplicate-origin-'+$role) @{Origins=$o}
    $n=$script:nativeInputs.Clone(); $n[$role]=$script:nativeInputs[$role].Clone(); $n[$role][0]+=' '
    Bad ('native-content-'+$role) @{NativeLines=$n}
}
$o=$bindingOrigins.Clone(); $o.fullCapture=[long[]]@(1..$nativeFull.Length)
Bad 'physical-native-offset-confusion' @{Origins=$o}
$n=$script:nativeInputs.Clone(); $n.Remove('fullCapture'); $n['FullCapture']=$script:nativeInputs.fullCapture
Bad 'role-case' @{NativeLines=$n}
$n=$script:nativeInputs.Clone(); $n.fullCapture=[string[]]::new($nativeFull.Length)
Bad 'null-native-lines' @{NativeLines=$n}
$n=$script:nativeInputs.Clone(); $n.fullCapture=[object[]]::new($nativeFull.Length)
[Array]::Copy($script:nativeInputs.fullCapture,$n.fullCapture,$nativeFull.Length)
Bad 'coerced-native-array' @{NativeLines=$n}
$n=$script:nativeInputs.Clone(); $o=$bindingOrigins.Clone()
$n.fullCapture=[string[]]$script:nativeInputs.fullCapture[1..($nativeFull.Length-1)]
$o.fullCapture=[long[]]$bindingOrigins.fullCapture[1..($nativeFull.Length-1)]
Bad 'valid-subset-wrong-native-token' @{NativeLines=$n;Origins=$o}
foreach($viewName in @('FullView','CompactView')) {
    $v=Clone $bindingArgs[$viewName]; $v.ArtifactEvidenceVerdict='fail'; Bad ($viewName+'-failed') @{$viewName=$v}
    $v=Clone $bindingArgs[$viewName]; $v.Boundaries[0].evidenceLineIndex=0; Bad ($viewName+'-wrong-boundary-offset') @{$viewName=$v}
    $v=Clone $bindingArgs[$viewName]; $v.__RoutePublicationEffects=@(); Bad ($viewName+'-missing-effects') @{$viewName=$v}
    $v=Clone $bindingArgs[$viewName]; $v.__RoutePublicationEffects+=@($v.__RoutePublicationEffects[0])
    Bad ($viewName+'-duplicate-effect') @{$viewName=$v}
    $v=Clone $bindingArgs[$viewName]; $v.__RoutePublicationEffects[0].LineIndex=0
    Bad ($viewName+'-wrong-effect-offset') @{$viewName=$v}
    $v=Clone $bindingArgs[$viewName]; $v.Boundaries+=@($v.Boundaries | Where-Object boundaryId -CEQ $body.rows[0].boundaryId)[0]
    Bad ($viewName+'-duplicate-boundary') @{$viewName=$v}
}
$v=Clone $compact; $v.CatalogHash='0'*64; Bad 'catalog-parity' @{CompactView=$v}
$fv=Clone $full; $cv=Clone $compact; $fv.ContractVersion=2; $cv.ContractVersion=2
Bad 'matching-unsupported-view-contract' @{FullView=$fv;CompactView=$cv}
$fv=Clone $full; $cv=Clone $compact; $fv.CatalogHash='invalid'; $cv.CatalogHash='invalid'
Bad 'matching-invalid-catalog-hash' @{FullView=$fv;CompactView=$cv}
$v=Clone $compact
$b=@($v.Boundaries | Where-Object boundaryId -CEQ $body.rows[0].boundaryId)[0]
$op=@($b.operations | Where-Object operationKey -CEQ $body.rows[0].operationKey)[0]
$op.routeRuntime.boundary.route='file_tuna_v6'
Bad 'invalid-active-tuna-v6' @{CompactView=$v}
$v=Clone $compact; $v.Boundaries=@(1..8193 | ForEach-Object { $compact.Boundaries[0] })
Bad 'view-row-budget' @{CompactView=$v}
$s=[string[]]::new(262145); Bad 'source-record-budget' @{SourceLines=$s}
$s=$lines.Clone(); $s[0]='x'*262145; Bad 'line-code-unit-budget' @{SourceLines=$s}
$otherOwner=[NLink.Tools.LocalNativeInputCapture]::new($request)
try { Bad 'same-guid-wrong-owner' @{NativeOwner=$otherOwner} }
finally { $otherOwner.Dispose() }
# Detachment: mutating a completed-view copy after binding cannot change bytes.
$v=Clone $full; $detached=Bind @{FullView=$v}
if($detached.Status -cne 'matched_local_native_binding') { throw "Detached copy omitted: $($detached.Reason)" }
$saved=$detached.Json; $v.__RoutePublicationEffects[0].Writer='changed'
if($detached.Json -cne $saved -or $saved -cne $binding.Json) { throw 'Result retains a mutable private graph' }
if((ConvertTo-Json -InputObject $full -Depth 64 -Compress) -cne $fullBefore -or
    (ConvertTo-Json -InputObject $compact -Depth 64 -Compress) -cne $compactBefore -or $script:evaluations -ne 2) {
    throw 'Binding mutated/re-evaluated the primary views'
}
# Force only the real checkpoint's clock reading; this is not a measured
# deadline test and does not introduce a production budget override.
$bindingCode=(Get-Command Resolve-FileTransferLocalNativeInputBinding).ScriptBlock.ToString()
$clockLine='$watch = [Diagnostics.Stopwatch]::StartNew()'
if(($bindingCode.Split([string[]]@($clockLine),[StringSplitOptions]::None)).Length -ne 2) { throw 'Binding clock seam changed' }
$budgetCode=$bindingCode.Replace($clockLine,'$watch = [pscustomobject]@{ElapsedMilliseconds=2001}; $watch | Add-Member ScriptMethod Stop {}')
$budgetResult=& ([scriptblock]::Create($budgetCode)) @bindingArgs
if($budgetResult.Status -cne 'omitted' -or $budgetResult.Reason -cne 'binding_work_budget' -or $budgetResult.Work.records -ne 0) {
    throw 'Exhausted binding budget did not omit before traversal'
}
$script:bindingNegatives.Add('controlled-work-budget')
$script:bindingCancellationOwner=$owner
$cancelClock=@'
$watch = [pscustomobject]@{Reads=0}
$watch | Add-Member ScriptProperty ElapsedMilliseconds {
    $this.Reads++
    if($this.Reads -eq 10) { $script:bindingCancellationOwner.Cancel() }
    return 0
}
$watch | Add-Member ScriptMethod Stop {}
'@
$cancelCode=$bindingCode.Replace($clockLine,$cancelClock)
$cancelResult=& ([scriptblock]::Create($cancelCode)) @bindingArgs
if($cancelResult.Status -cne 'omitted' -or $cancelResult.Reason -cne 'binding_owner_inactive' -or
    $cancelResult.Work.records -lt 1 -or $cancelResult.Work.records -ge $bindingArgs.WriterLines.Length -or $null -ne $cancelResult.Json) {
    throw 'Cancellation during traversal did not omit all rows at the next checkpoint'
}
$script:bindingNegatives.Add('controlled-cancel-during-preparation')
Bad 'cancelled-owner' @{}
if($script:bindingForbiddenCalls -ne 0) { throw 'A binding path attempted forbidden I/O, reconstruction or evaluation' }
[IO.File]::WriteAllText((Join-Path $output 'binding.json'),$binding.Json)
[IO.File]::WriteAllText((Join-Path $output 'binding-work.json'),(ConvertTo-Json -InputObject @{
    bindingWallMilliseconds=$bindingWatch.Elapsed.TotalMilliseconds;work=$binding.Work
} -Compress))
'native-input-binding='+(ConvertTo-Json -InputObject ([ordered]@{Uses=$body.rows.Count;NegativeControls=$script:bindingNegatives.Count
    Names=$script:bindingNegatives.ToArray();Work=[ordered]@{records=$binding.Work.records;codeUnits=$binding.Work.codeUnits
        viewRows=$binding.Work.viewRows;operationRows=$binding.Work.operationRows};AuthorityEvaluations=2;AdditionalAuthorityEvaluations=0
    ForbiddenConsumerCalls=$script:bindingForbiddenCalls
    PrimaryViewsUnchanged=$true;FileOwnershipVerified=$false;EvaluatorInputOwnershipVerified=$false
    PublicationCompletionIssued=$false;ProductionCaller=$false}) -Depth 5 -Compress)
