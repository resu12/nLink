param([string]$RepoRoot,[string]$SettingsPath)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$evaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPrimaryEvaluationInputs.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPrimaryPublicationWitness.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPrimaryPublicationCompletion.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPublicationFailureWitness.ps1')
if(-not [object]::ReferenceEquals($evaluator,(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock)) { throw 'Optional module redefined primary evaluator' }
$owner=$null; $source=$null; $context=$null; $heldWriter=$null
Set-Variable -Name context -Option Private -Value $context
Set-Variable -Name prepared -Option Private -Value $null
$private:localPrimaryCustody=$null; $private:localCompletedEvaluations=$null
try {
    $owner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,[guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID,
        $settings.path,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
    if($null -eq $owner -or $null -eq $owner.WriterContext) { throw 'Actual writer-bound worker missing' }
    $source=$owner.TryOpenSource()
    $events=@(Read-FileTransferLogEvents -LogFiles @($settings.path) -TailMinutes 0 -OwnedSource $source)
    $Index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($settings.path)
    $snapshotCatalog=Get-FileTransferCoordinatorEvidenceCatalog
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
    $writer=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Write-FileTransferCompactRetainedAnalysisArtifacts'},$true))[0]
    function Assignment([string]$Name) {
        $nodes=@($writer.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq $Name},$true))
        if($nodes.Count -ne 1) { throw "Actual assembly/evaluator seam ambiguous: $Name" }; return $nodes[0].Extent.Text
    }
    Invoke-Expression (Assignment '[string[]]$primaryFullSnapshotLines')
    Invoke-Expression (Assignment '[string[]]$primaryCompactSnapshotLines')
    $context=New-FileTransferPrimaryEvaluationInputs $owner $source $Index $snapshotCatalog $primaryFullSnapshotLines $primaryCompactSnapshotLines
    if($null -eq $context) { throw 'Generated native preparation omitted' }
    if($settings.mode -ceq 'valid-0') {
        # Primitive cap+1 admission only, not fabricated provenance or a view.
        $script:originOverCapVisits=0
        $poison=[pscustomobject]@{}
        $poison | Add-Member ScriptProperty RawLine { $script:originOverCapVisits++; throw 'Over-cap origins were visited' }
        $count=[NLink.Tools.LocalPublicationInputLimits]::InputRecords+1
        $overEvents=[object[]](@($poison)*$count);$overLines=[string[]]::new($count);$overOrigins=[int[]]::new($count)
        if($owner.ValidateFullInputOrigins($source,$context.NativeOwner,$context.NativeInputs,$overEvents,$overLines,$overLines,$overOrigins,$context.Work) -or
            $script:originOverCapVisits -ne 0) {throw 'Origin cap checked after traversal'}
    }
    $private:localPrimaryCustody=$owner.TryBeginPrimary($source,$context,$primaryFullSnapshotLines,$primaryCompactSnapshotLines)
    if($null -eq $private:localPrimaryCustody) { throw 'Actual private custody missing' }
    if(-not $private:localPrimaryCustody.BindNativeInputs($context.NativeOwner,$context.NativeInputs)) { throw 'Actual native owner binding missing' }
    function RecordActualEvaluation([string]$Method) {
        $nodes=@($writer.FindAll({param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
            $n.Member.Extent.Text -ceq $Method},$true))
        if($nodes.Count -ne 1) { throw "Original record seam changed: $Method" }
        $statement=$nodes[0]
        while($statement -isnot [Management.Automation.Language.IfStatementAst]) { $statement=$statement.Parent }
        # Execute in this test's caller scope so private capability visibility
        # matches the actual orchestration, not a helper's dynamic scope.
        return $statement.Extent.Text
    }
    $script:evaluations=0
    function Get-FileTransferCoordinatorSnapshotEvaluation { param([AllowEmptyCollection()][string[]]$Lines)
        $script:evaluations++; & $evaluator -Lines $Lines }
    Invoke-Expression (Assignment '$fullSnapshotEvaluation')
    Invoke-Expression (RecordActualEvaluation 'RecordFullEvaluation')
    Invoke-Expression (Assignment '$compactSnapshotEvaluation')
    Invoke-Expression (RecordActualEvaluation 'RecordCompactEvaluation')
    if(-not (Complete-FileTransferPrimaryEvaluationInputs $context $primaryFullSnapshotLines $primaryCompactSnapshotLines $fullSnapshotEvaluation $compactSnapshotEvaluation)) {
        throw 'Actual generated evaluation association omitted'
    }
    $private:localCompletedEvaluations=$private:localPrimaryCustody.Take($context,$primaryFullSnapshotLines,
        $primaryCompactSnapshotLines,$fullSnapshotEvaluation,$compactSnapshotEvaluation)
    if(-not $private:localPrimaryCustody.Owns($private:localCompletedEvaluations,$context,$context.FullLines,
        $context.CompactLines,$context.FullView,$context.CompactView)) { throw 'Actual generated views lost invocation custody' }
    function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Additional authority evaluation forbidden' }
    $uses=@($events | Where-Object { $_.RawLine -match '; publication_use_kind=' })
    if($uses.Count -ne 8) { throw 'Generated captured uses changed' }
    $use=0; if($settings.mode -like 'valid-*') { $use=[int]$settings.mode.Substring(6) }
    $failure=[pscustomobject]@{Class='synthetic_generated_failure';Reason='synthetic_selection_control';Event=$uses[$use]}
    $witness=Resolve-FileTransferPrimaryFailureWitness $Index $failure
    if($null -eq $witness) { throw 'Exact generated witness unavailable' }
    # Negative view mutations use detached test views. They do not mutate the
    # original completed authority objects retained for the no-effect assertion.
    $fullBefore=ConvertTo-Json -InputObject $fullSnapshotEvaluation -Depth 64 -Compress
    $compactBefore=ConvertTo-Json -InputObject $compactSnapshotEvaluation -Depth 64 -Compress
    if($settings.mode -cin @('boundary-offset','duplicate-boundary','missing-effect','effect-offset','effect-content','view-parity','view-budget','operation-budget')) {
        $context.CompactView=ConvertFrom-Json -InputObject $compactBefore
    }
    switch($settings.mode) {
        'wrong-failure' { $failure.Event=$failure.Event.PSObject.Copy() }
        'wrong-witness' { $witness.WriterSequence++ }
        'wrong-event-name' { $witness.EventName='wrong' }
        'native-mutated' { $context.FullLines[0]+=':changed' }
        'compact-mutated' { $context.CompactLines[0]+=':changed' }
        'full-origin' { $context.FullOrigins[0]=999999 }
        'compact-origin' { $context.CompactOrigins[0]=999999 }
        'event-raw-reference' { $Index.AllEvents[0].RawLine=[string]::new($Index.AllEvents[0].RawLine.ToCharArray()) }
        'event-path' { $Index.AllEvents[0].FilePath+=':changed' }
        'event-line' { $Index.AllEvents[0].LineNumber++ }
        'event-sequence' { $Index.AllEvents[0].Sequence++ }
        'event-sequence-compatible' { $Index.AllEvents[0].Sequence=[long]$Index.AllEvents[0].Sequence }
        'boundary-offset' { $context.CompactView.Boundaries[0].evidenceLineIndex=999999 }
        'duplicate-boundary' {
            $target=@($context.CompactView.Boundaries | Where-Object {
                $_.boundaryId -ceq $failure.Event.Fields.publication_evidence_boundary_id -and
                $_.coordinatorInstanceId -ceq $failure.Event.Fields.publication_evidence_coordinator_id })
            if($target.Count -ne 1) { throw 'Selected boundary fixture is not unique' }
            $context.CompactView.Boundaries+=@($target[0])
        }
        'missing-effect' { $context.CompactView.__RoutePublicationEffects=@() }
        'effect-offset' { foreach($effect in $context.CompactView.__RoutePublicationEffects) { $effect.LineIndex=999999 } }
        'effect-content' { foreach($effect in $context.CompactView.__RoutePublicationEffects) { $effect.RuntimeGeneration=999999 } }
        'view-parity' { $context.CompactView.BoundaryHash='0'*64 }
        'view-budget' { $context.CompactView.Boundaries=@($context.CompactView.Boundaries[0])*8193 }
        'operation-budget' { $context.CompactView.Boundaries[0].operations=@([pscustomobject]@{})*65 }
        'record-budget' { $context.RecordVisitCeiling=[NLink.Tools.LocalPublicationInputLimits]::ProducerRecords }
        'time-budget' {
            $context.Work.Stop()
            $elapsed=@($context.Work.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object Name -In @('elapsed','_elapsed'))
            if($elapsed.Count -ne 1) {throw 'Witness test clock shape changed'}
            $elapsed[0].SetValue($context.Work,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds+1)/1000))
        }
        'native-cancel' { $context.NativeOwner.Cancel() }
        'source-cancel' { $source.Dispose() }
        'no-failure' { $failure=$null; $witness=$null }
    }
    $prepared=Resolve-FileTransferPrimaryPublicationWitness $context $Index $failure $witness
    $expected=$settings.mode -like 'valid-*' -or $settings.mode -ceq 'event-sequence-compatible' -or $settings.lane -cin @('completion','channel')
    if(($null -ne $prepared) -ne $expected) { throw "Primary witness result drift: $($settings.mode) : $($context.PublicationOmissionReason)" }
    if(-not $expected -and $settings.mode -cnotin @('time-budget','record-budget','source-cancel','native-cancel') -and
        $context.PublicationOmissionReason -eq 'primary_witness_owner_or_budget') { throw 'Work-budget omission masked a negative control' }
    if($settings.mode -ceq 'no-failure') {
        $unopened=$owner.TryOpenWriter()
        if($null -eq $unopened -or $unopened.IsVerified) { throw 'Absent failure consumed writer verification' }
        $unopened.Dispose()
    }
    if($expected) {
        if(-not $private:localPrimaryCustody.Owns($private:localCompletedEvaluations,$context,$context.FullLines,
            $context.CompactLines,$context.FullView,$context.CompactView)) { throw 'Prepared witness lost actual invocation custody' }
        $body=$prepared.Json | ConvertFrom-Json
        if($body.witness.sourceLine -ne $witness.SourceLine -or $body.row.useWriterSequence -ne $witness.WriterSequence -or
            $body.row.useContentSha256 -cne $witness.ContentSha256 -or $body.provenance.full.publicationLine -eq $body.provenance.compact.publicationLine -or
            $body.peerLineageStatus -cne 'not_evaluated' -or $prepared.PublicationCompletionIssued -or -not $context.WriterReader.IsVerified) { throw 'Prepared semantics/original offsets changed' }
        $heldWriter=$context.WriterReader
        # C1d is a test oracle only. Extract its function without executing its
        # old transitive imports, then supply the actual owned inputs/views.
        $oracleAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeInputBinding.ps1'),[ref]$tokens,[ref]$errors)
        $oracle=@($oracleAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Resolve-FileTransferLocalNativeInputBinding'},$true))
        if($oracle.Count -ne 1) { throw 'C1d oracle missing' }; Invoke-Expression $oracle[0].Extent.Text
        $writerEvidence=@{WriterLineCount=$owner.WriterContext.Outputs[1].LineCount;WriterGapCount=0;WriterDuplicateCount=0
            WriterIdentityMismatchCount=0;ChronologyInversionCount=0;ChronologyCollisionCount=0;ChronologyBatchInvalid=$false
            Writers=@($owner.WriterContext.Writers | ForEach-Object { @{WriterId=$_.WriterId;FirstSequence=1L;LastSequence=$_.LastSequence
                ObservedCount=$_.LastSequence;AcceptanceRunId=$owner.AcceptanceRunId;ScenarioRunId=$owner.ScenarioRunId} })}
        $native=@{fullCapture=$context.FullLines;fullSnapshot=$context.FullLines;compactCapture=$context.FullLines;compactSnapshot=$context.CompactLines}
        $origins=@{fullCapture=$context.FullOrigins;fullSnapshot=$context.FullOrigins;compactCapture=$context.FullOrigins;compactSnapshot=$context.CompactOrigins}
        $reference=Resolve-FileTransferLocalNativeInputBinding -Request @{RequestId=$owner.RequestId;AcceptanceRunId=$owner.AcceptanceRunId
            ScenarioRunId=$owner.ScenarioRunId;Selection=@($witness)} -SourceLines ([IO.File]::ReadAllLines($owner.SourcePath)) `
            -WriterLines ([IO.File]::ReadAllLines($owner.WriterContext.Outputs[1].Path)) -WriterEvidence $writerEvidence `
            -NativeOwner $context.NativeOwner -NativeInputs $context.NativeInputs -NativeLines $native -Origins $origins `
            -FullView $fullSnapshotEvaluation -CompactView $compactSnapshotEvaluation
        if($reference.Status -cne 'matched_local_native_binding') { throw "C1d reference omitted: $($reference.Reason)" }
        $expectedBody=$reference.Json | ConvertFrom-Json
        if((ConvertTo-FileTransferLocalCarrierJson $body.row) -cne (ConvertTo-FileTransferLocalCarrierJson $expectedBody.rows[0]) -or
            (ConvertTo-FileTransferLocalCarrierJson $body.provenance.full) -cne (ConvertTo-FileTransferLocalCarrierJson $expectedBody.provenance[0].full) -or
            (ConvertTo-FileTransferLocalCarrierJson $body.provenance.compact) -cne (ConvertTo-FileTransferLocalCarrierJson $expectedBody.provenance[0].compact) -or
            $body.provenance.publicationSourceLine -ne $expectedBody.provenance[0].publicationSourceLine -or
            $body.provenance.effectSourceLine -ne $expectedBody.provenance[0].effectSourceLine) { throw 'Prepared row differs from C1d oracle' }
        if($null -ne (Resolve-FileTransferPrimaryPublicationWitness $context $Index $failure $witness)) { throw 'Preparation replay admitted' }
    }
    if($script:evaluations -ne 2 -or (ConvertTo-Json -InputObject $fullSnapshotEvaluation -Depth 64 -Compress) -cne $fullBefore -or
        (ConvertTo-Json -InputObject $compactSnapshotEvaluation -Depth 64 -Compress) -cne $compactBefore) { throw 'Original evaluations/views changed' }
    $completionResult=$null
    if($settings.lane -ceq 'completion') {
        $completionResult=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PrimaryPublicationCompletionChecks.ps1') `
            -Mode $settings.mode -Custody $private:localPrimaryCustody -Evaluations $private:localCompletedEvaluations -Context $context -Prepared $prepared
    }
    if($settings.lane -ceq 'channel') {
        $completionResult=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PrimaryPublicationChannelWorkerChecks.ps1') `
            -Mode $settings.mode -Custody $private:localPrimaryCustody -Evaluations $private:localCompletedEvaluations -Context $context -Prepared $prepared
    }
    $result=@{Mode=$settings.mode;Prepared=$null -ne $prepared;Reason=$context.PublicationOmissionReason;Evaluations=$script:evaluations
        NativeRecordVisits=$context.NativeOwner.RecordVisits;NativeCodeUnits=$context.NativeOwner.CodeUnitVisits;OptionalMilliseconds=$context.Work.ElapsedMilliseconds}
    if($expected) { $result.Work=$prepared.Work }
    if($null -ne $completionResult) { $result.Completion=$completionResult }
    $result.Catalog=@{contractVersion=$fullSnapshotEvaluation.ContractVersion;catalogVersion=$fullSnapshotEvaluation.CatalogVersion
        catalogHash=$fullSnapshotEvaluation.CatalogHash;boundaryHash=$fullSnapshotEvaluation.BoundaryHash}
    Remove-FileTransferPrimaryEvaluationInputs $context
    if($null -ne $heldWriter -and $heldWriter.IsVerified) { throw 'Writer ownership survived primary cleanup' }
    [IO.File]::WriteAllText($settings.result,($result | ConvertTo-Json -Depth 4 -Compress))
} finally {
    if($null -ne $private:localPrimaryCustody) { $private:localPrimaryCustody.Dispose() }
    if($null -ne $context) { Remove-FileTransferPrimaryEvaluationInputs $context }
    if($null -ne $source) { $source.Dispose() }; if($null -ne $owner) { $owner.Dispose() }
}
if($settings.lane -ceq 'channel' -and $settings.mode -ceq 'channel-nonzero') { exit 23 }
