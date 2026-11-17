param([string]$Definition,[switch]$RetainOutputs)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Test-only seams in the actual finalizer. Preserve its sole attachment,
# validation, original Complete call and successful-child-exit requirement.
$body=$Definition.Replace("`r`n","`n")
function Replace-One([string]$Old,[string]$New) {
    if($script:body.Split([string[]]@($Old),[StringSplitOptions]::None).Length -ne 2) {throw "Collector finalizer seam changed: $Old"}
    $script:body=$script:body.Replace($Old,$New)
}
Replace-One '[AllowNull()]$FrontierOwner = $null' '[AllowNull()]$TerminalCollector = $null, [AllowNull()]$FrontierOwner = $null'
Replace-One 'Set-Variable -Name FrontierOwner -Option Private' @'
Set-Variable -Name FrontierOwner -Option Private
    Set-Variable -Name TerminalCollector -Option Private
'@
$old=@'
            $nativeStreamingResult =
                [NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::$nativeWriterMethod(
                    [string]$script:GuiSmokeAppLogLedger.AppendOnlyPath,
                    [long]$frozenWriterEvidenceEnd,
                    [long]$Bookmark,
                    $retainedSliceTempPath,
                    $writerEvidenceTempPath,
                    $expectedAcceptanceRunId,
                    $expectedScenarioRunId,
                    [string[]]@($deferredMarkersByBatch.Keys),
                    $semanticEvidenceTempPath,
                    [string[]](Get-GuiSmokeSemanticEvidenceEventNames))
'@
Replace-One $old @'
            if($null -eq $localWriterIssuer -or $null -eq $TerminalCollector) {throw 'collector_finalizer_owner_missing'}
            $copyMethod=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
            $private:copiedCollector=$copyMethod.Invoke($TerminalCollector,@())
            $private:copiedIssuer=$copyMethod.Invoke($localWriterIssuer,@())
            $events=[string[]](Get-GuiSmokeSemanticEvidenceEventNames)
            $keys=[string[]]@($deferredMarkersByBatch.Keys)
            $source=[string]$script:GuiSmokeAppLogLedger.AppendOnlyPath
            $rejects=0
            foreach($control in @('collector','issuer','source','lines')) {
                $private:testCollector=if($control -ceq 'collector') {$copiedCollector} else {$TerminalCollector}
                $private:testIssuer=if($control -ceq 'issuer') {$copiedIssuer} else {$localWriterIssuer}
                $testSource=if($control -ceq 'source') {$source+'.foreign'} else {$source}
                $testLines=if($control -ceq 'lines') {[long]$frozenWriterEvidenceEnd-1} else {[long]$frozenWriterEvidenceEnd}
                if($null -ne $testIssuer.ProcessCollectorForTest($testCollector,$testSource,$testLines,[long]$Bookmark,
                    $retainedSliceTempPath,$writerEvidenceTempPath,$keys,$semanticEvidenceTempPath,$events)) {throw "Collector read substitution admitted: $control"}
                $rejects++
            }
            $copiedCollector.Dispose(); $copiedIssuer.CloseCollectorForTest()
            if(-not $TerminalCollector.OutputIsCurrent) {throw 'Copied owner retired original collector'}
            $nativeStreamingResult=$localWriterIssuer.ProcessCollectorForTest($TerminalCollector,$source,[long]$frozenWriterEvidenceEnd,[long]$Bookmark,
                $retainedSliceTempPath,$writerEvidenceTempPath,$keys,$semanticEvidenceTempPath,$events)
            if($null -eq $nativeStreamingResult) {throw 'Owned collector read unavailable'}
            if($null -ne $localWriterIssuer.ProcessCollectorForTest($TerminalCollector,$source,[long]$frozenWriterEvidenceEnd,[long]$Bookmark,
                $retainedSliceTempPath,$writerEvidenceTempPath,$keys,$semanticEvidenceTempPath,$events)) {throw 'Repeated finalizer processing admitted'}
            $script:collectorFinalizerEvidence=@{RejectedBeforeRead=$rejects;RepeatedProcessingRejected=$true}
'@
Replace-One '$outputs = @($nativeCapture.Retained, $nativeCapture.Writer, $nativeCapture.Semantic)' @'
                            if(-not $localWriterIssuer.OwnsCollectorCaptureForTest($TerminalCollector,$nativeStreamingResult,$nativeCapture)) {
                                throw 'Original finalizer lost collector capture'
                            }
                            $rejected=0
                            foreach($control in @('collector','issuer','result','capture','null')) {
                                $private:testCollector=if($control -ceq 'collector') {$copiedCollector} else {$TerminalCollector}
                                $private:testIssuer=if($control -ceq 'issuer') {$copiedIssuer} else {$localWriterIssuer}
                                $testResult=if($control -ceq 'result') {$copyMethod.Invoke($nativeStreamingResult,@())} else {$nativeStreamingResult}
                                $testCapture=if($control -ceq 'capture') {$copyMethod.Invoke($nativeCapture,@())} elseif($control -ceq 'null') {$null} else {$nativeCapture}
                                if($testIssuer.OwnsCollectorCaptureForTest($testCollector,$testResult,$testCapture)) {throw "Collector capture substitution admitted: $control"}
                                $rejected++
                            }
                            $script:collectorFinalizerEvidence.RejectedCaptureSubstitutions=$rejected
                            $script:collectorFinalizerEvidence.FinalizerOwnedStreamCapture=$true
                            $script:collectorFinalizerEvidence.FinalizerOwnedNativeCapture=$true
                            $outputs = @($nativeCapture.Retained, $nativeCapture.Writer, $nativeCapture.Semantic)
'@
Replace-One 'finally { if ($null -ne $localWriterIssuer) { $localWriterIssuer.Dispose() } }' @'
        finally {
            try {
                if($null -ne $localWriterIssuer) {
                    $localWriterIssuer.CloseCollectorForTest()
                    if($TerminalCollector.OutputIsCurrent -or ($null -ne $nativeStreamingResult -and
                        $localWriterIssuer.OwnsCollectorCaptureForTest($TerminalCollector,$nativeStreamingResult,$nativeStreamingResult.NativeCapture))) {
                        throw 'Closed collector capture remained current'
                    }
                    $script:collectorFinalizerEvidence.ClosedAfterOriginalFinalization=$true
                }
            } finally {if($null -ne $localWriterIssuer) {$localWriterIssuer.Dispose()}}
        }
'@
if($RetainOutputs) {
    Replace-One '$writerEvidenceFailure = "atomic_ledger_evidence_failed:$($_.Exception.Message)"' @'
        $writerEvidenceFailure = "atomic_ledger_evidence_failed:$($_.Exception.Message)"
        # Preserve the original failure before the unchanged generic throw.
        # Diagnostic text is bounded and never consumed as evidence/authority.
        $failureText=$_.Exception.ToString()+[Environment]::NewLine+$_.ScriptStackTrace
        if($failureText.Length -gt 8192) {$failureText=$failureText.Substring(0,8192)}
        try {[IO.File]::WriteAllText((Join-Path $ArtifactDir 'collector-capture-failure.txt'),$failureText)} catch { }
        if($mode -ceq 'capture-expired') {
            if($null -ne $localWriterIssuer.ProcessCollectorForTest($TerminalCollector,$source,[long]$frozenWriterEvidenceEnd,[long]$Bookmark,
                $retainedSliceTempPath,$writerEvidenceTempPath,$keys,$semanticEvidenceTempPath,$events,$true,$false)) {throw 'Expired collector read retried'}
            $script:collectorFinalizerEvidence.ExpiredReadRetryRejected=$true
        }
'@
    Replace-One '$source=[string]$script:GuiSmokeAppLogLedger.AppendOnlyPath' @'
            # Retained native handles prohibit renaming. CreateNew at the exact
            # final paths keeps custody continuous without changing production.
            $retainedSliceTempPath=$retainedSlicePath
            $writerEvidenceTempPath=$writerEvidencePath
            $semanticEvidenceTempPath=$semanticEvidencePath
            $source=[string]$script:GuiSmokeAppLogLedger.AppendOnlyPath
'@
    $arguments='$keys,$semanticEvidenceTempPath,$events)'
    if($body.Split([string[]]@($arguments),[StringSplitOptions]::None).Length -ne 4) {throw 'Retained processor seams changed'}
    $body=$body.Replace($arguments,'$keys,$semanticEvidenceTempPath,$events,$true,($mode -ceq ''capture-expired''))')
    foreach($pair in @(@('retainedSliceTempPath','retainedSlicePath'),@('writerEvidenceTempPath','writerEvidencePath'),@('semanticEvidenceTempPath','semanticEvidencePath'))) {
        Replace-One ('[System.IO.File]::Move($'+$pair[0]+', $'+$pair[1]+')') '# Exact final path already created and retained by this processor.'
    }
    Replace-One @'
        $analyzerInput = Copy-GuiSmokeFileAtomicCreateNew `
            -SourcePath ([string]$(if ($null -ne $semanticEvidenceSlice) {
                        $semanticEvidenceSlice.Path
                    }
                    else {
                        $retainedSlice.Path
                    })) `
            -DestinationPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log')
'@ @'
        $private:collectorSemanticCopy=$localWriterIssuer.CopyCollectorSemanticForTest($TerminalCollector,$nativeStreamingResult,
            (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'))
        if($null -eq $collectorSemanticCopy) {throw 'Owned semantic copy missing'}
        $analyzerInput=[pscustomobject]@{Path=$collectorSemanticCopy.CreationPath;Sha256=$collectorSemanticCopy.Sha256;
            ByteCount=$collectorSemanticCopy.ByteCount;LineCount=$collectorSemanticCopy.LineCount}
'@
    Replace-One '$outputs = @($nativeCapture.Retained, $nativeCapture.Writer, $nativeCapture.Semantic)' @'
                            $rejectedOffers=0
                            foreach($control in @('collector','issuer','result','copy')) {
                                $private:testCollector=if($control -ceq 'collector') {$copiedCollector} else {$TerminalCollector}
                                $private:testIssuer=if($control -ceq 'issuer') {$copiedIssuer} else {$localWriterIssuer}
                                $testResult=if($control -ceq 'result') {$copyMethod.Invoke($nativeStreamingResult,@())} else {$nativeStreamingResult}
                                $testCopy=if($control -ceq 'copy') {$copyMethod.Invoke($collectorSemanticCopy,@())} else {$collectorSemanticCopy}
                                if($testIssuer.PublishCollectorOutputsForTest($testCollector,$testResult,$testCopy)) {throw "Copied output offer admitted: $control"}
                                $rejectedOffers++
                            }
                            if(-not $localWriterIssuer.PublishCollectorOutputsForTest($TerminalCollector,$nativeStreamingResult,$collectorSemanticCopy)) {
                                throw 'Parent did not retain owned outputs'
                            }
                            if($localWriterIssuer.PublishCollectorOutputsForTest($TerminalCollector,$nativeStreamingResult,$collectorSemanticCopy)) {
                                throw 'Repeated owned output offer admitted'
                            }
                            $script:collectorFinalizerEvidence.ParentRetainedOutputsBeforeCompletion=$true
                            $script:collectorFinalizerEvidence.RejectedOutputOffers=$rejectedOffers
                            $script:collectorFinalizerEvidence.RepeatedOutputOfferRejected=$true
                            $outputs = @($nativeCapture.Retained, $nativeCapture.Writer, $nativeCapture.Semantic)
'@
}
$body
