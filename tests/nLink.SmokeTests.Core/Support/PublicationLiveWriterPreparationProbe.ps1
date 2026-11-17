param($Context,$Index,[string]$ArtifactDir)
# Test-only primitive traversal, before any authority evaluation. The first
# original input is only a membership lookup, not a selected reported failure.
$writer=$null;$membership=$null;$lease=$null
$facts=[ordered]@{Prepared=$null -ne $Context;AuthorityEvaluations=0;PublicationCompletionIssued=$false}
$originProbe=$env:NLINK_OFFLINE_PUBLICATION_ORIGIN_PROBE -ceq '1'
$i=-1
if($originProbe) {
    # Setup outside the measured clock. Execute only the original input-origin
    # check and its Check function, never a view or witness resolver.
    $ast=(Get-Command Resolve-FileTransferPrimaryPublicationWitness).ScriptBlock.Ast
    $checks=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Check'},$true))
    $calls=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Member.Extent.Text -ceq 'ValidateFullInputOrigins'},$true))
    if($checks.Count -ne 1 -or $calls.Count -ne 1) {throw 'Original origin probe seam changed'}
    $statement=$calls[0]
    while($statement -isnot [Management.Automation.Language.IfStatementAst]) {$statement=$statement.Parent}
    . ([scriptblock]::Create($checks[0].Extent.Text))
    $originLoop=[scriptblock]::Create($statement.Extent.Text+"`nCheck")
}
try {
    if($null -eq $Context) {throw 'writer_probe_preparation_missing'}
    $Context.Work.Start()
    if($originProbe) {
        $before=$Context.Work.ElapsedMilliseconds
        if(-not $Context.SourceOwner.ValidateInputReferences($Context.SourceReader,$Context.FullLines,$Context.FullOriginal,
            $Context.CompactLines,$Context.CompactOriginal,$Context.Work)) {throw 'origin_probe_reference_recheck_failed'}
        $facts.ReferenceRecheckMilliseconds=$Context.Work.ElapsedMilliseconds-$before
    }
    $before=$Context.Work.ElapsedMilliseconds
    $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[0]
    $writer=$Context.SourceOwner.TryOpenWriter()
    if($null -eq $writer) {throw 'writer_probe_open_failed'}
    $lease=$writer.GetType().GetField('lease',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($writer)
    $membership=$writer.TryVerifyMembership($Context.FullLines,$Context.FullOriginal,
        [string]$fields.log_writer_id,[long]$fields.log_writer_sequence,$Context.NativeOwner.CodeUnitVisits,$Context.Work)
    $facts.TraversalMilliseconds=$Context.Work.ElapsedMilliseconds-$before
    $facts.Membership=$null -ne $membership
    if($originProbe -and $null -ne $membership) {
        $facts.OriginStarted=$true;$facts.OriginCompleted=$false
        $facts.BeforeOriginsMilliseconds=$Context.Work.ElapsedMilliseconds
        $previous=0
        . $originLoop
        $facts.OriginCompleted=$true
        $i=$Context.FullLines.Length
    }
} catch {$facts.Error=$_.Exception.Message}
finally {
    if($null -ne $lease) {
        $facts.WriterOmission=$lease.OmissionReason
        $facts.WriterVerificationState=$lease.GetType().GetField('verification',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($lease)
        $facts.WriterRecordVisits=$lease.VerificationRecordVisits
        $facts.WriterCodeUnits=$lease.VerificationCodeUnitVisits
    }
    if($null -ne $writer) {$writer.Dispose()}
    if($null -ne $Context) {
        $Context.Work.Stop()
        $facts.SourceOwned=$Context.SourceOwner.OwnsCompletedSource($Context.SourceReader)
        $facts.OptionalMilliseconds=$Context.Work.ElapsedMilliseconds
    }
    if($originProbe) {$facts.OriginValidatedRecords=$i}
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'owned-writer-observation.json'),($facts | ConvertTo-Json -Compress))
}
