Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalNativeInputCapture.ps1')
. (Join-Path $PSScriptRoot 'LocalPublicationEvidenceCarrier.ps1')

# Diagnostic association over primary-owner-supplied objects, not a completion
# issuer. No file reads, authority evaluation, whole-view clone or transport.
# This bounded reference consumer is NOT wired into pre-exit finalization.
function Resolve-FileTransferLocalNativeInputBinding {
    param($Request, $SourceLines, $WriterLines, $WriterEvidence, $NativeOwner, $NativeInputs,
        [hashtable]$NativeLines, [hashtable]$Origins, $FullView, $CompactView)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $work = @{records=0L; codeUnits=0L; viewRows=0L; operationRows=0L}
    $verification = $null
    function Check {
        if ($watch.ElapsedMilliseconds -gt 2000) { throw 'binding_work_budget' }
        if (-not $NativeOwner.Owns($NativeInputs)) { throw 'binding_owner_inactive' }
    }
    function Visit([string]$Line) {
        Check
        if ($null -eq $Line -or $Line.Length -gt 262144 -or $work.records -ge 262144 -or
            $Line.Length -gt 67108864 - $work.codeUnits) { throw 'binding_input_budget' }
        $work.records++; $work.codeUnits += $Line.Length
    }
    function Dictionary { return ,([Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)) }
    function Integer($Value, [long]$Minimum=1) {
        return ($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum
    }
    try {
        if ($SourceLines -isnot [string[]] -or $WriterLines -isnot [string[]] -or
            $null -eq $NativeInputs -or $null -eq $NativeOwner -or
            $Request.RequestId -isnot [guid] -or $Request.RequestId -ne $NativeInputs.RequestId -or
            $Request.Selection -isnot [array] -or $Request.Selection.Count -lt 1 -or $Request.Selection.Count -gt 64 -or
            $Request.AcceptanceRunId -isnot [string] -or $Request.ScenarioRunId -isnot [string] -or
            [string]::IsNullOrWhiteSpace($Request.AcceptanceRunId) -or [string]::IsNullOrWhiteSpace($Request.ScenarioRunId) -or
            $Request.AcceptanceRunId -ceq '(none)' -or $Request.ScenarioRunId -ceq '(none)' -or
            $SourceLines.Length + $WriterLines.Length -gt 262144) { throw 'binding_request_invalid' }
        Check
        # Recheck the supplied ledger's run/writer facts against the supplied
        # writer records. File-byte hash/freeze ownership is deliberately absent.
        if ($WriterEvidence.WriterLineCount -ne $WriterLines.Length -or
            $WriterEvidence.WriterGapCount -ne 0 -or $WriterEvidence.WriterDuplicateCount -ne 0 -or
            $WriterEvidence.WriterIdentityMismatchCount -ne 0 -or $WriterEvidence.ChronologyInversionCount -ne 0 -or
            $WriterEvidence.ChronologyCollisionCount -ne 0 -or $WriterEvidence.ChronologyBatchInvalid -ne $false -or
            $WriterEvidence.Writers.Count -lt 1 -or $WriterEvidence.Writers.Count -gt 64) { throw 'binding_writer_invalid' }
        $owners=Dictionary; $counts=Dictionary; $members=Dictionary
        foreach ($writer in $WriterEvidence.Writers) {
            $id=[string]$writer.WriterId
            if ([string]::IsNullOrWhiteSpace($id) -or $owners.ContainsKey($id) -or
                $writer.AcceptanceRunId -cne $Request.AcceptanceRunId -or $writer.ScenarioRunId -cne $Request.ScenarioRunId -or
                $writer.FirstSequence -ne 1 -or $writer.LastSequence -ne $writer.ObservedCount -or $writer.ObservedCount -le 0) {
                throw 'binding_writer_invalid'
            }
            $owners[$id]=$writer; $counts[$id]=0L
        }
        foreach ($line in $WriterLines) {
            if ($null -eq $line) { throw 'binding_null_line' }; Visit $line
            $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
            $id=[string]$f.log_writer_id; $sequence=[string]$f.log_writer_sequence
            if ($f.ContainsKey('__nlink_duplicate_writer_evidence_field') -or -not $owners.ContainsKey($id) -or
                -not (Test-FileTransferPublicationUseInteger $sequence 1)) { throw 'binding_writer_record_invalid' }
            $counts[$id]++
            if ([long]$sequence -ne $counts[$id]) { throw 'binding_writer_order_invalid' }
            if ($sequence -ceq '1') {
                if ($f['event'] -cne 'operational_log_writer_started' -or $f.acceptance_run_id -cne $Request.AcceptanceRunId -or
                    $f.scenario_run_id -cne $Request.ScenarioRunId) { throw 'binding_writer_start_invalid' }
            }
            elseif ($f['event'] -ceq 'operational_log_writer_started') { throw 'binding_writer_start_invalid' }
            $members["$id|$sequence"]=$line
        }
        foreach ($id in $owners.Keys) { if ($counts[$id] -ne $owners[$id].ObservedCount) { throw 'binding_writer_count_invalid' } }
        $sourceMembers=Dictionary; $sourceKeys=[string[]]::new($SourceLines.Length)
        for ($i=0; $i -lt $SourceLines.Length; $i++) {
            $line=$SourceLines[$i]; if ($null -eq $line) { throw 'binding_null_line' }; Visit $line
            $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
            if (-not $f.ContainsKey('log_writer_id') -and -not $f.ContainsKey('log_writer_sequence')) { continue }
            $key="$($f.log_writer_id)|$($f.log_writer_sequence)"
            if ($f.ContainsKey('__nlink_duplicate_writer_evidence_field') -or -not $members.ContainsKey($key) -or
                $members[$key] -cne $line -or $sourceMembers.ContainsKey($key)) { throw 'binding_source_membership_invalid' }
            $sourceMembers[$key]=$i+1L; $sourceKeys[$i]=$key
        }
        $selected=Dictionary
        foreach ($selection in $Request.Selection) {
            if (-not (Integer $selection.SourceLine) -or $selection.SourceLine -gt $SourceLines.Length -or
                -not (Integer $selection.WriterSequence) -or $selection.Writer -isnot [string] -or
                $selection.ContentSha256 -isnot [string] -or $selection.ContentSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
                throw 'binding_selection_invalid'
            }
            $line=[long]$selection.SourceLine; $key="$($selection.Writer)|$($selection.WriterSequence)"
            if ($selected.ContainsKey([string]$line) -or -not $sourceMembers.ContainsKey($key) -or
                $sourceMembers[$key] -ne $line -or
                (Get-FileTransferLocalCarrierHash $SourceLines[$line-1]) -cne $selection.ContentSha256) { throw 'binding_selection_invalid' }
            $selected[[string]$line]=$selection
        }
        $roles=@('fullCapture','fullSnapshot','compactCapture','compactSnapshot')
        if ($NativeLines.Count -ne 4 -or $Origins.Count -ne 4) { throw 'binding_roles_invalid' }
        $verification=[NLink.Tools.LocalNativeInputCapture]::new($Request.RequestId)
        $locations=Dictionary
        foreach ($role in $roles) {
            if (-not ($NativeLines.Keys -ccontains $role) -or -not ($Origins.Keys -ccontains $role) -or
                $NativeLines[$role] -isnot [string[]] -or
                ($Origins[$role] -isnot [long[]] -and $Origins[$role] -isnot [int[]])) { throw 'binding_native_shape_invalid' }
            $native=$NativeLines[$role]; $origin=$Origins[$role]
            if ($native.Length -ne $origin.Length -or $work.records + $native.Length -gt 262144 -or
                -not $verification.BeginInput($role,$native.Length)) { throw 'binding_native_budget_or_shape' }
            $map=Dictionary; $previous=0L
            for ($i=0; $i -lt $native.Length; $i++) {
                $line=$native[$i]; if ($null -eq $line) { throw 'binding_null_line' }; Visit $line
                $physical=$origin[$i]
                if ($physical -le $previous -or $physical -gt $SourceLines.Length -or
                    [string]::IsNullOrEmpty($sourceKeys[$physical-1]) -or $line -cne $SourceLines[$physical-1]) {
                    throw 'binding_native_origin_invalid'
                }
                $previous=$physical; $map[[string]$physical]=$i+1L
                if (-not $verification.AppendLine($i+1,$line)) { throw 'binding_native_verification_omitted' }
            }
            if (-not $verification.EndInput()) { throw 'binding_native_verification_omitted' }
            $locations[$role]=$map
        }
        $verified=$verification.Take()
        if ($null -eq $verified) { throw 'binding_native_verification_omitted' }
        foreach ($role in $roles) {
            $a=$NativeInputs.Get($role); $b=$verified.Get($role)
            if ($null -eq $a -or $a.RecordCount -ne $b.RecordCount -or $a.CodeUnitCount -ne $b.CodeUnitCount -or
                $a.Sha256 -cne $b.Sha256) { throw 'binding_native_identity_changed' }
        }
        foreach ($key in @('ContractVersion','CatalogVersion','CatalogHash','BoundaryHash')) {
            if ((ConvertTo-FileTransferLocalCarrierJson $FullView.$key) -cne
                (ConvertTo-FileTransferLocalCarrierJson $CompactView.$key)) { throw 'binding_view_parity_invalid' }
        }
        $indexes=Dictionary
        foreach ($pair in @(@('full',$FullView),@('compact',$CompactView))) {
            Check
            $prefix=$pair[0]; $view=$pair[1]; $snapshot=$NativeLines[$prefix+'Snapshot']
            if (-not (Integer $view.ContractVersion) -or $view.ContractVersion -ne 1 -or
                $view.CatalogVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($view.CatalogVersion) -or
                $view.CatalogHash -cnotmatch '\A[0-9a-f]{64}\z' -or $view.BoundaryHash -cnotmatch '\A[0-9a-f]{64}\z' -or
                $view.Mode -cne 'SnapshotV1Authoritative' -or $view.ArtifactEvidenceVerdict -cne 'pass' -or
                $view.StreamComplete -ne $true -or 'route_runtime' -cnotin $view.AuthoritativeFamilies -or
                'route_transition' -cnotin $view.AuthoritativeFamilies -or $view.Boundaries -isnot [array] -or
                $view.__RoutePublicationEffects -isnot [array] -or
                $work.viewRows + $view.Boundaries.Count + $view.__RoutePublicationEffects.Count -gt 8192) { throw 'binding_view_invalid_or_budget' }
            $publications=@{Boundaries=(Dictionary);States=(Dictionary)}
            foreach ($boundary in $view.Boundaries) {
                Check; $work.viewRows++
                if (-not (Integer $boundary.evidenceLineIndex 0) -or $boundary.evidenceLineIndex -ge $snapshot.Length -or
                    $boundary.operations -isnot [array] -or $boundary.operations.Count -gt 64 -or
                    $work.operationRows + $boundary.operations.Count -gt 4096) { throw 'binding_boundary_shape_or_budget' }
                $work.operationRows += $boundary.operations.Count
                $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $snapshot[$boundary.evidenceLineIndex]
                if ($f.ContainsKey('__nlink_duplicate_structured_field') -or $f.event -cne 'filetransfer_coordinator_transition_snapshot' -or
                    $f.transition_id -cne $boundary.boundaryId -or $f.snapshot_sha256 -cne $boundary.boundaryHash -or
                    $f.log_writer_id -cne $boundary.writer -or $f.log_writer_sequence -cne [string]$boundary.writerSequence) {
                    throw 'binding_boundary_origin_invalid'
                }
                if ($boundary.verdict -cne 'pass') { throw 'binding_boundary_failed' }
                foreach ($entry in @(@('Boundaries',"$($boundary.coordinatorInstanceId)|$($boundary.boundaryId)"),
                    @('States',"$($boundary.coordinatorInstanceId)|$($boundary.afterStateGeneration)"))) {
                    $map=$publications[$entry[0]]; $key=$entry[1]
                    if (-not $map.ContainsKey($key)) { $map[$key]=@() }
                    if ($map[$key].Count -ge 64) { throw 'binding_observation_budget' }
                    $map[$key] += @($boundary)
                }
            }
            $work.viewRows += $view.__RoutePublicationEffects.Count
            $indexes[$prefix]=@{Reason=''; SnapshotPath=''; SnapshotRole=$prefix+'Snapshot'; SnapshotLines=$snapshot
                Effects=$view.__RoutePublicationEffects
                Local=@{Reason='';Path='';NativeRole=$prefix+'Capture';Lines=$NativeLines[$prefix+'Capture'];Publications=$publications}}
        }
        $rows=[Collections.Generic.List[object]]::new(); $provenance=[Collections.Generic.List[object]]::new()
        foreach ($selection in $Request.Selection) {
            Check
            $physical=[string]$selection.SourceLine
            if (-not $locations.fullCapture.ContainsKey($physical) -or -not $locations.compactCapture.ContainsKey($physical)) {
                throw 'binding_selected_use_missing'
            }
            $full=Get-FileTransferLocalPublicationRow $indexes.full $locations.fullCapture[$physical] 'fullCapture'
            $compact=Get-FileTransferLocalPublicationRow $indexes.compact $locations.compactCapture[$physical] 'compactCapture'
            if ((ConvertTo-FileTransferLocalCarrierJson $full.Row) -cne (ConvertTo-FileTransferLocalCarrierJson $compact.Row)) {
                throw 'binding_selected_row_parity_invalid'
            }
            $rows.Add($full.Row)
            $provenance.Add(@{sourceLine=$selection.SourceLine;full=$full.Location;compact=$compact.Location
                publicationSourceLine=$Origins.fullSnapshot[$full.Location.publicationLine-1]
                effectSourceLine=$Origins.fullSnapshot[$full.Location.effectLine-1]})
            if ($Origins.compactSnapshot[$compact.Location.publicationLine-1] -ne $Origins.fullSnapshot[$full.Location.publicationLine-1] -or
                $Origins.compactSnapshot[$compact.Location.effectLine-1] -ne $Origins.fullSnapshot[$full.Location.effectLine-1]) {
                throw 'binding_selected_origin_parity_invalid'
            }
        }
        $nativeDescriptors=@{}
        foreach ($role in $roles) {
            $input=$verified.Get($role)
            $nativeDescriptors[$role]=@{domain=[NLink.Tools.LocalNativeInputCapture]::Domain
                recordCount=$input.RecordCount;codeUnitCount=$input.CodeUnitCount;sha256=$input.Sha256}
        }
        $json=ConvertTo-FileTransferLocalCarrierJson @{requestId=$Request.RequestId.ToString('D')
            acceptanceRunId=$Request.AcceptanceRunId;scenarioRunId=$Request.ScenarioRunId
            scope='local_publication_forwarding';peerLineageStatus='not_evaluated'
            nativeInputs=$nativeDescriptors;rows=$rows.ToArray();provenance=$provenance.ToArray()}
        Check
        return [pscustomobject]@{Status='matched_local_native_binding';Reason='';Json=$json;Work=$work
            FileOwnershipVerified=$false;EvaluatorInputOwnershipVerified=$false;PublicationCompletionIssued=$false}
    }
    catch {
        return [pscustomobject]@{Status='omitted';Reason=$_.Exception.Message;Json=$null;Work=$work
            FileOwnershipVerified=$false;EvaluatorInputOwnershipVerified=$false;PublicationCompletionIssued=$false}
    }
    finally { if ($null -ne $verification) { $verification.Dispose() }; $watch.Stop() }
}
