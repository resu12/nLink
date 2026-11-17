param($Mode, [string]$LogPath, [string]$ArtifactDir, $TailMinutes, $LiveRouteProofMode)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText((Join-Path $ArtifactDir 'case.json')) | ConvertFrom-Json
if($null -ne $settings.PSObject.Properties['slotName']) {
    . (Join-Path $settings.repoRoot 'tools/FileTransferOps/LocalEvidenceExportSlot.ps1')
    $slotBytes=[Text.Encoding]::UTF8.GetBytes('prepared-lifecycle-only-payload')
}
if($null -ne $settings.PSObject.Properties['nativeCapture']) {
    . (Join-Path $settings.repoRoot 'tools/FileTransferOps/LocalNativeInputCapture.ps1')
}
function Save([string]$Name,[string]$Text) {
    [IO.File]::WriteAllText((Join-Path $ArtifactDir $Name),$Text,[Text.UTF8Encoding]::new($false))
}
if($null -ne $settings.PSObject.Properties['sourceRequest']) {
    # Test-only observation before primary reports. The request is created and
    # bound by the actual extracted parent, not manufactured in this worker.
    $sourceFacts=@{attached=$false;complete=$false;requestId='';sourceSha256='';lines=0;owner='';name=[string]$env:NLINK_RETAINED_SOURCE_REQUEST}
    if($null -ne $settings.PSObject.Properties['writerBinding']) {
        $sourceFacts.writerContext=$false; $sourceFacts.writerOpened=$false; $sourceFacts.writerVerified=$false
        $sourceFacts.guiRequest=''; $sourceFacts.writerLines=0L
    }
    if($settings.optionalMode -cne 'none') {
        . (Join-Path $settings.repoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
        . (Join-Path $settings.repoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        $sourceOwner=$null; $sourceReader=$null
        try {
            $sourceId=if($settings.optionalMode -ceq 'caught-failure') { [guid]::NewGuid() } else { [guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID }
            $sourceOwner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,$sourceId,$LogPath,
                $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
            if($null -ne $sourceOwner) {
                $sourceFacts.attached=$true; $sourceFacts.requestId=$sourceOwner.RequestId.ToString('D')
                $sourceFacts.sourceSha256=$sourceOwner.SourceSha256; $sourceFacts.owner=$sourceOwner.Owner
                $sourceReader=$sourceOwner.TryOpenSource()
                if($null -ne $sourceReader) {
                    $null=Read-FileTransferLogEvents -LogFiles @($LogPath) -TailMinutes 0 -OwnedSource $sourceReader
                    $sourceFacts.complete=$sourceReader.InputReadComplete; $sourceFacts.lines=$sourceReader.LinesRead
                }
                if($null -ne $settings.PSObject.Properties['writerBinding']) {
                    $sourceFacts.writerContext=$null -ne $sourceOwner.WriterContext
                    if($sourceFacts.writerContext) { $sourceFacts.guiRequest=$sourceOwner.WriterContext.RequestId.ToString('D') }
                    $writer=$sourceOwner.TryOpenWriter()
                    if($null -ne $writer) {
                        $sourceFacts.writerOpened=$true
                        try { $null=@($writer.VerifyAndReadLines()); $sourceFacts.writerVerified=$writer.IsVerified; $sourceFacts.writerLines=$writer.LinesRead }
                        catch { } # Optional candidate failure must not replace primary reports.
                        finally { $writer.Dispose() }
                    }
                }
            }
        }
        finally { if($null -ne $sourceReader) { $sourceReader.Dispose() }; if($null -ne $sourceOwner) { $sourceOwner.Dispose() } }
    }
    Save 'source-request-control.json' (ConvertTo-Json -InputObject $sourceFacts -Compress)
}
# Explicit lifecycle-only outputs; these are not generated Snapshot evidence or
# a C0 carrier. The real parent functions evaluate their normal completion gate.
$verdict=if($settings.proofPass) { 'PASS' } else { 'FAIL' }
$hard=if($settings.proofPass) { 0 } else { 1 }
$gate=if($settings.proofPass) { 'pass' } else { 'fail' }
Save 'filetransfer-operator-verdict.txt' "verdict=$verdict`nhard_failure_count=$hard`n"
Save 'stability-gates-summary.txt' "gate_status=$gate`nhard_failure_count=$hard`n"
Save 'filetransfer-route-consistency-summary.txt' @'
route_consistency_verdict=pass
live_route_epoch_proof_verdict=pass
fallback_leg_authority_proof_verdict=pass
clean_fallback_leg_proof_verdict=pass
bridge_liveness_integration_verdict=pass
control_plane_isolation_verdict=pass
'@
$snapshot=@{Mode='SnapshotV1Authoritative';StreamComplete=$true;ContractVersion=1;CatalogVersion='lifecycle-only';CatalogHash='fixture'
    BoundaryHash='fixture';ArtifactEvidenceVerdict='pass';LegacySelectionCount=0;RouteFamilyLegacySelectionCount=0
    RuntimeUnlockRetryFamilyLegacySelectionCount=0;ReceiveRecoveryFamilyLegacySelectionCount=0;FallbackTailYieldFamilyLegacySelectionCount=0
    RegularV4FrontierRepairPressureFamilyLegacySelectionCount=0;ResponderProbeHandoffFamilyLegacySelectionCount=0} | ConvertTo-Json -Compress
Save 'filetransfer-coordinator-snapshot-full-summary.json' $snapshot
Save 'filetransfer-coordinator-snapshot-compact-summary.json' $snapshot
Save 'filetransfer-coordinator-snapshot-parity-summary.json' '{"Verdict":"pass"}'
Save 'filetransfer-retained-compact-summary.json' ('{"verdict":"'+$verdict+'","fixture":"lifecycle-only"}')
Save 'filetransfer-retained-analysis-stage-diagnostics.json' '{"stage":"primary-reports-persisted"}'
Save 'primary-persisted.json' ('{"processId":'+$PID+',"optionalNotStarted":true}')
Write-Output 'controlled-primary-finished'

switch($settings.optionalMode) {
    'none' { }
    'completed' { Save 'optional.json' '{"fixture":"not-a-C0-carrier","status":"completed"}' }
    'caught-failure' { try { throw 'optional fixture failure' } catch { } }
    'malformed' { Save 'optional.json' '{' }
    'nonzero' { exit 9 }
    { $_ -cin @('slot-completed','slot-contended','slot-cancelled') } {
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $offered=[NLink.Tools.LocalEvidenceExportSlot]::TryOffer($settings.slotName,[guid]$settings.slotRequest,$slotBytes)
        $watch.Stop()
        Save 'slot-offer.json' (ConvertTo-Json -InputObject @{offered=$offered;elapsedMilliseconds=$watch.ElapsedMilliseconds} -Compress)
    }
    { $_ -cin @('native-completed','native-budget','native-cancelled') } {
        $nativeOwner=[NLink.Tools.LocalNativeInputCapture]::new([guid]$settings.slotRequest)
        try {
            if($settings.optionalMode -ceq 'native-cancelled') { $nativeOwner.Cancel() }
            if($settings.optionalMode -ceq 'native-budget') {
                # Controlled exhausted-budget state, not a slow-work timing claim.
                $nativeOwner.GetType().GetField('activeTicks',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($nativeOwner,
                    [long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalNativeInputCapture]::MaximumActiveMilliseconds+1)/1000))
            }
            foreach($role in @('fullCapture','fullSnapshot','compactCapture','compactSnapshot')) {
                if(-not $nativeOwner.BeginInput($role,1)) { break }
                if(-not $nativeOwner.AppendLine(1,'lifecycle-only-native-input') -or -not $nativeOwner.EndInput()) { break }
            }
            $nativeInputs=$nativeOwner.Take()
            $prepared=$null -ne $nativeInputs -and $nativeOwner.Owns($nativeInputs)
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $offered=$false
            if($prepared) { $offered=[NLink.Tools.LocalEvidenceExportSlot]::TryOffer($settings.slotName,[guid]$settings.slotRequest,$slotBytes) }
            $watch.Stop()
            Save 'native-work.json' (ConvertTo-Json -InputObject @{prepared=$prepared;recordVisits=$nativeOwner.RecordVisits;reason=$nativeOwner.OmissionReason} -Compress)
            Save 'slot-offer.json' (ConvertTo-Json -InputObject @{offered=$offered;elapsedMilliseconds=$watch.ElapsedMilliseconds} -Compress)
        }
        finally { $nativeOwner.Dispose() }
    }
    'stall' {
        Save 'optional-started.json' '{"fixture":"pre-exit-blocking-work"}'
        [Threading.Thread]::Sleep(10000)
        Save 'late-optional.json' '{"fixture":"must-not-survive-cancellation"}'
    }
    default { throw 'Unknown lifecycle test mode' }
}
exit 0
