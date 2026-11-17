param($Context,$Custody,$Evaluations,$Index,[string]$ArtifactDir,
    [bool]$GuiCompleted,[bool]$GuiIntegrityOk,[string]$PrimaryVerdict,[string]$PrimaryGateStatus,[scriptblock]$MembershipProbe,[scriptblock]$PayloadProbe,[switch]$DeferOutput)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Terminal source probe cannot evaluate authority'}
function Require($Condition,[string]$Reason) {if(-not $Condition) {throw $Reason}}
function Check($C,$Pair,$Full,$Compact,$Origins) {
    Require ($null -ne $C -and -not $C.Disposed -and $null -ne $Custody -and
        $Custody.Owns($Pair,$C,$Full,$Compact,$C.FullView,$C.CompactView) -and
        $C.NativeOwner.Owns($C.NativeInputs) -and $C.SourceOwner.OwnsCompletedSource($C.SourceReader)) 'primary_custody'
    Require ($C.SourceOwner.ValidateInputReferences($C.SourceReader,$Full,$C.FullOriginal,$Compact,$C.CompactOriginal,$C.Work) -and
        $C.SourceOwner.ValidateFullInputOrigins($C.SourceReader,$C.NativeOwner,$C.NativeInputs,$Index.AllEvents,$Full,$C.FullOriginal,$Origins,$C.Work)) 'primary_origins'
}
Require ($null -ne $Context) 'primary_custody'
$Context.Work.Start()
try {
    Check $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullOrigins
    $nativeFiles=$null; $nativeControls=0; $nativeDescriptions=$null; $collectorFiles=$null; $collectorControls=0; $collectorProtection=0
    $collectorOrigins=[Collections.Generic.List[object]]::new()
    if($env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1') {
        $nativeFiles=$Context.SourceOwner.TerminalFilesForTest
        function Check-Native($Files,$Owner,$Reader,$C,$Pair,$Full,$Compact,$FullView,$CandidateCustody) {
            Require ($null -ne $Files -and $Files.OwnsPrimary($Owner,$Reader,$CandidateCustody,$Pair,$C,$Full,$Compact,
                $FullView,$Context.CompactView)) 'native_primary_custody'
        }
        Check-Native $nativeFiles $Context.SourceOwner $Context.SourceReader $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Custody
        $clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
        foreach($case in @('file_owner','source_owner','source_reader','context','pair','full','compact','view','custody')) {
            $f=$nativeFiles; $o=$Context.SourceOwner; $r=$Context.SourceReader; $c=$Context; $p=$Evaluations
            $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView; $cust=$Custody
            switch($case) {
                'file_owner' {$f=$clone.Invoke($f,@()); $f.Dispose()}
                'source_owner' {$o=$clone.Invoke($o,@())}
                'source_reader' {$r=$clone.Invoke($r,@())}
                'context' {$c=$Context.PSObject.Copy()}
                'pair' {$p=$null}
                'full' {$full=$full.Clone()}
                'compact' {$compact=$compact.Clone()}
                'view' {$view=$null}
                'custody' {$cust=$clone.Invoke($cust,@())}
            }
            $reason=''; try {Check-Native $f $o $r $c $p $full $compact $view $cust} catch {$reason=$_.Exception.Message}
            Require ($reason -ceq 'native_primary_custody') ('native_control:'+ $case+':'+$reason)
            $nativeControls++
        }
        Check-Native $nativeFiles $Context.SourceOwner $Context.SourceReader $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Custody
        $nativeDescriptions=$nativeFiles.Describe()
        if($env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1') {
            $collectorFiles=$Context.SourceOwner.CollectorOutputsForTest
            Check-Native $collectorFiles $Context.SourceOwner $Context.SourceReader $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Custody
            foreach($case in @('file_owner','source_owner','source_reader','context','pair','full','compact','view','custody')) {
                $f=$collectorFiles; $o=$Context.SourceOwner; $r=$Context.SourceReader; $c=$Context; $p=$Evaluations
                $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView; $cust=$Custody
                switch($case) {
                    'file_owner' {$f=$clone.Invoke($f,@()); $f.Dispose()}
                    'source_owner' {$o=$clone.Invoke($o,@())}
                    'source_reader' {$r=$clone.Invoke($r,@())}
                    'context' {$c=$Context.PSObject.Copy()}
                    'pair' {$p=$null}
                    'full' {$full=$full.Clone()}
                    'compact' {$compact=$compact.Clone()}
                    'view' {$view=$null}
                    'custody' {$cust=$clone.Invoke($cust,@())}
                }
                $reason=''; try {Check-Native $f $o $r $c $p $full $compact $view $cust} catch {$reason=$_.Exception.Message}
                Require ($reason -ceq 'native_primary_custody') ('collector_control:'+ $case+':'+$reason)
                $collectorControls++
            }
            Check-Native $collectorFiles $Context.SourceOwner $Context.SourceReader $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Custody
            foreach($descriptor in $Context.SourceOwner.WriterContext.Outputs) {
                try {$write=[IO.File]::Open($descriptor.Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]'ReadWrite,Delete'); $write.Dispose()}
                catch [IO.IOException] {if(($_.Exception.HResult -band 0xffff) -ne 32) {throw}; $collectorProtection++}
                try {[IO.File]::Move($descriptor.Path,$descriptor.Path+'.denied-replacement')}
                catch [IO.IOException] {if(($_.Exception.HResult -band 0xffff) -ne 32) {throw}; $collectorProtection++}
            }
            Require ($collectorProtection -eq 8) 'collector_primary_protection'
            $copiedFiles=$clone.Invoke($collectorFiles,@())
            $copiedReader=$clone.Invoke($Context.SourceReader,@())
            Require ($null -eq $copiedFiles.DescribeLineOriginForTest($Context.SourceReader,1) -and
                $null -eq $collectorFiles.DescribeLineOriginForTest($copiedReader,1) -and
                $null -eq $collectorFiles.DescribeLineOriginForTest($Context.SourceReader,0) -and
                $null -eq $collectorFiles.DescribeLineOriginForTest($Context.SourceReader,[int]$Context.SourceOwner.SourceLines+1)) 'collector_origin_query_controls'
            foreach($physical in $Context.FullOrigins) {
                Require ($Context.Work.ElapsedMilliseconds -le 20000) 'collector_origin_work_limit'
                $origin=$collectorFiles.DescribeLineOriginForTest($Context.SourceReader,$physical)
                Require ($null -ne $origin -and $origin.SemanticLine -eq $physical -and
                    $origin.ProducerLine -gt 0 -and $origin.WriterLine -gt 0 -and $origin.RetainedLine -gt 0) 'collector_origin_primary_line'
                $collectorOrigins.Add($origin)
            }
            Check-Native $collectorFiles $Context.SourceOwner $Context.SourceReader $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Custody
        }
    }
    Require ($Context.FullLines.Length -le 4096 -and $Context.FullView.Boundaries.Count -le 256 -and
        $Context.CompactView.Boundaries.Count -le 256) 'input_limit'
    $before=ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress
    $terminals=@($Index.AllEvents | Where-Object {$_.EventName -ceq 'transfer_terminal'})
    # These are the original source-owned records, not appended test evidence.
    # Their presence does not supply file-owner custody or whole-file integrity.
    Require ($terminals.Count -eq $(if($env:NLINK_TEST_TERMINAL_PAYLOAD_MUTATION -ceq 'foreign'){4}else{2})) 'terminal_pair_count'
    $writer=$Context.SourceOwner.WriterContext
    Require ($null -ne $writer -and $writer.AcceptanceRunId -ceq $Context.SourceOwner.AcceptanceRunId -and
        $writer.ScenarioRunId -ceq $Context.SourceOwner.ScenarioRunId) 'writer_scope'
    $controls=0
    foreach($case in @('context','pair','full','compact','origin','empty')) {
        $c=$Context; $pair=$Evaluations; $full=$Context.FullLines; $compact=$Context.CompactLines; $origins=$Context.FullOrigins
        switch($case) {
            'context' {$c=$Context.PSObject.Copy()}
            'pair' {$pair=$null}
            'full' {$full=$full.Clone()}
            'compact' {$compact=$compact.Clone()}
            'origin' {$origins=$origins.Clone(); $origins[0]++}
            'empty' {$c=$null}
        }
        $reason=''; try {Check $c $pair $full $compact $origins} catch {$reason=$_.Exception.Message}
        $expected=if($case -ceq 'origin') {'primary_origins'} else {'primary_custody'}
        Require ($reason -ceq $expected) ('custody_control:'+ $case+':'+$reason); $controls++
    }
    Check $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullOrigins
    $membership=$null
    if($env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1' -or $env:NLINK_TEST_NATIVE_WITNESS_ADAPTER -ceq '1') {
        Require ($null -ne $collectorFiles -and $null -ne $MembershipProbe) 'collector_membership_missing_probe'
        $membership=& $MembershipProbe $Context $Custody $Evaluations $collectorFiles ([string]$env:NLINK_TEST_COLLECTOR_MEMBERSHIP_MUTATION)
        Check $Context $Evaluations $Context.FullLines $Context.CompactLines $Context.FullOrigins
    }
    Require ($before -ceq (ConvertTo-Json @($Context.FullView,$Context.CompactView) -Depth 64 -Compress)) 'views_changed'
    Require ($Context.Work.ElapsedMilliseconds -le 20000) 'work_limit'
    $result=[ordered]@{Schema='nlink.test-primary-terminal-source/v2';Status='verified_primary_terminal_source_only'
        FirstMissingEdge='file_owner_handoff_unavailable';TerminalIntegrityGate='unproven'
        RequestId=$Evaluations.RequestId.ToString('D');WriterRequestId=$writer.RequestId.ToString('D')
        FullInvocationId=$Evaluations.FullInvocationId.ToString('D');CompactInvocationId=$Evaluations.CompactInvocationId.ToString('D')
        RunId=$Context.SourceOwner.AcceptanceRunId;ScenarioId=$Context.SourceOwner.ScenarioRunId
        SourcePath=$Context.SourceOwner.SourcePath;SourceSha256=$Context.SourceOwner.SourceSha256
        SourceIdentity=$Context.SourceOwner.SourceIdentity;SourceBytes=$Context.SourceOwner.SourceBytes;SourceLines=$Context.SourceOwner.SourceLines
        TerminalRecordCount=$terminals.Count;RejectedCustodyControls=$controls;InputsUnchanged=$true;WorkMilliseconds=$Context.Work.ElapsedMilliseconds
        Terminals=@($terminals | ForEach-Object {[ordered]@{SourceLine=$_.LineNumber;RawLine=$_.RawLine}})
        GuiCompleted=$GuiCompleted;GuiIntegrityOk=$GuiIntegrityOk;PrimaryVerdict=$PrimaryVerdict;PrimaryGateStatus=$PrimaryGateStatus
        WriterMembershipVerified=($null -ne $Context.WriterReader -and $Context.WriterReader.IsVerified)
        ProbeChangesAcceptance=$false;AlternativeAcceptanceAdopted=$false;CanonicalScenarioProven=$false;Live=$false}
    if($null -ne $nativeFiles) {
        $result.FirstMissingEdge='file_writer_provenance_unproven'
        $result.PrimaryNativeFileAdoptionProven=$true; $result.PrimaryFileIntegrityJoinProven=$false
        $result.NativeFileStatus='verified_offline_original_primary_native_files_only'
        $result.NativeFiles=$nativeDescriptions; $result.RejectedNativeCustodyControls=$nativeControls
        $result.HashesAreProducerMeasurements=$true; $result.WriterCopyProvenanceProven=$false
    }
    if($null -ne $collectorFiles) {
        Require ([bool]$script:collectorRawReadUsed) 'collector_primary_raw_read_not_reused'
        $result.PrimaryCollectorOutputCustodyProven=$true
        $result.CollectorOutputControls=$collectorControls; $result.CollectorOutputProtection=$collectorProtection
        $result.OriginalSourceReaderUsed=$true; $result.PrimaryProvenanceHandoffProven=$false
        $result.RawEvidenceUsesOriginalCompleteRead=$true
        $result.PrimaryCollectorLineOriginsProven=$true; $result.CollectorOriginQueryControls=4
        $result.CollectorLineOrigins=$collectorOrigins.ToArray()
    }
    if($null -ne $membership) {$result.CollectorWriterMembership=$membership}
    if($env:NLINK_TEST_TERMINAL_PAYLOAD_JOIN -ceq '1') {
        Require ($null -ne $PayloadProbe -and $null -ne $membership -and $membership.Verified) 'terminal_payload_probe_missing'
        $payload=& $PayloadProbe $Context $Custody $Evaluations $nativeFiles $terminals ([string]$env:NLINK_TEST_TERMINAL_PAYLOAD_MUTATION)
        $result.TerminalPayloadJoin=$payload
        $result.PrimaryFileIntegrityJoinProven=$payload.Joined
        $result.FirstMissingEdge=if($payload.Joined){'primary_integrity_gate_not_integrated'}elseif($membership.OriginJoin.Joined){'terminal_payload_binding_unproven'}else{'file_writer_provenance_unproven'}
        Require ($Context.Work.ElapsedMilliseconds -le 20000) 'terminal_payload_work_limit'
        $result.WorkMilliseconds=$Context.Work.ElapsedMilliseconds
    }
} finally {$Context.Work.Stop()}
if($DeferOutput) {return $result}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'terminal-source-primary-observation.json'),($result | ConvertTo-Json -Depth 8 -Compress))
