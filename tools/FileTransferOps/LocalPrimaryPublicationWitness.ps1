Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalPublicationRows.ps1')

# Called at the primary's first-failure seam, before reports. This prepares
# detached local diagnostics; it does not issue completion or route authority.
function Resolve-FileTransferPrimaryPublicationWitness {
    param($Context, $Index, $Failure, $Witness)
    $writer = $null; $matched = $false
    $work = @{records=0L;codeUnits=0L;viewRows=0L;operationRows=0L}
    function Check {
        if ($Context.Work.ElapsedMilliseconds -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -or $Context.Disposed -or
            -not $Context.SourceOwner.OwnsCompletedSource($Context.SourceReader) -or
            -not $Context.NativeOwner.Owns($Context.NativeInputs)) { throw 'primary_witness_owner_or_budget' }
    }
    function Visit([string]$Line) {
        Check
        if ($null -eq $Line -or $Line.Length -gt 262144 -or $work.records -ge [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords -or
            $Line.Length -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits - $work.codeUnits) { throw 'primary_witness_input_budget' }
        $work.records++; $work.codeUnits += $Line.Length
    }
    function Fields([string]$Line) {
        Visit $Line
        # Bound split/field allocations before calling the unchanged shared
        # parser. The last split element retains the unvisited remainder.
        if ($Line.Split([char[]]@(';'),257).Length -gt 256) { throw 'primary_witness_field_budget' }
        return ConvertFrom-GuiSmokeLogLedgerStructuredFields $Line
    }
    function Dictionary { return ,([Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)) }
    function Integer($Value,[long]$Minimum=1) { return ($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum }
    try {
        if ($null -eq $Context -or $Context.Disposed -or -not $Context.Associated -or $Context.PublicationAttempted) { return $null }
        $Context.PublicationAttempted = $true; $Context.Work.Start()
        Check
        if ($null -eq $Witness -or $null -eq $Failure -or $null -eq $Failure.Event -or
            $Witness.Policy -cne 'primary_reported_failure_witnesses_v1' -or
            $Witness.ReportClass -cne $Failure.Class -or $Witness.ReportReason -cne $Failure.Reason -or
            $Witness.EventName -cne $Failure.Event.EventName -or
            $Witness.SourcePath -cne $Context.SourceOwner.SourcePath -or -not (Integer $Witness.SourceLine) -or
            $Witness.SourceLine -gt $Context.SourceOwner.SourceLines -or
            $Index.LogFiles.Count -ne 1 -or $Index.LogFiles[0] -cne $Context.SourceOwner.SourcePath -or
            $Index.AllEvents.Count -ne $Context.FullLines.Length) { return $null }
        $selected = [Array]::BinarySearch($Context.FullOrigins,[int]$Witness.SourceLine)
        if ($selected -lt 0 -or -not [object]::ReferenceEquals($Failure.Event,$Index.AllEvents[$selected]) -or
            -not [object]::ReferenceEquals($Failure.Event.RawLine,$Context.FullOriginal[$selected]) -or
            $Failure.Event.LineNumber -ne $Witness.SourceLine) { return $null }

        $fullView = $Context.FullView; $compactView = $Context.CompactView
        foreach ($key in @('ContractVersion','CatalogVersion','CatalogHash','BoundaryHash')) {
            if ((ConvertTo-FileTransferLocalCarrierJson $fullView.$key) -cne
                (ConvertTo-FileTransferLocalCarrierJson $compactView.$key)) { throw 'primary_witness_view_parity' }
        }
        foreach ($view in @($fullView,$compactView)) {
            if (-not (Integer $view.ContractVersion) -or $view.ContractVersion -ne 1 -or
                $view.Mode -cne 'SnapshotV1Authoritative' -or $view.ArtifactEvidenceVerdict -cne 'pass' -or
                $view.StreamComplete -ne $true -or 'route_runtime' -cnotin $view.AuthoritativeFamilies -or
                'route_transition' -cnotin $view.AuthoritativeFamilies -or
                $view.CatalogVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($view.CatalogVersion) -or
                $view.CatalogHash -cnotmatch '\A[0-9a-f]{64}\z' -or $view.BoundaryHash -cnotmatch '\A[0-9a-f]{64}\z' -or
                $view.Boundaries -isnot [array] -or $view.__RoutePublicationEffects -isnot [array] -or
                $work.viewRows + $view.Boundaries.Count + $view.__RoutePublicationEffects.Count -gt 8192) { throw 'primary_witness_view_invalid' }
            $work.viewRows += $view.Boundaries.Count + $view.__RoutePublicationEffects.Count
        }
        $descriptor = $Context.SourceOwner.WriterContext.Outputs[1]
        $work.records = $Context.RecordVisitCeiling; $work.codeUnits = $Context.NativeOwner.CodeUnitVisits
        if ($work.records + $Context.FullLines.Length + $Context.CompactLines.Length + 2L*$descriptor.LineCount -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords) {
            throw 'primary_witness_record_budget'
        }
        $writer = $Context.SourceOwner.TryOpenWriter()
        if ($null -eq $writer) { throw 'primary_witness_writer_unavailable' }
        $membership = $writer.TryVerifyMembership($Context.FullLines,$Context.FullOriginal,$Witness.Writer,
            $Witness.WriterSequence,$work.codeUnits,$Context.Work)
        if ($null -eq $membership -or -not $writer.IsVerified) { throw 'primary_witness_writer_unverified' }
        $work.records += $membership.RecordVisits; $work.codeUnits = $membership.CodeUnitVisits
        if ($work.records -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords) { throw 'primary_witness_record_budget' }

        if (-not $Context.SourceOwner.ValidateFullInputOrigins($Context.SourceReader,$Context.NativeOwner,$Context.NativeInputs,
            $Index.AllEvents,$Context.FullLines,$Context.FullOriginal,$Context.FullOrigins,$Context.Work)) {
            Check
            throw 'primary_witness_native_changed'
        }
        Check
        $previous = 0
        for ($i=0; $i -lt $Context.CompactLines.Length; $i++) {
            Check; $work.records++
            $physical = $Context.CompactOrigins[$i]
            $fullPosition = [Array]::BinarySearch($Context.FullOrigins,$physical)
            if ($work.records -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords -or $physical -le $previous -or $fullPosition -lt 0 -or
                -not [object]::ReferenceEquals($Context.CompactLines[$i],$Context.FullLines[$fullPosition]) -or
                -not [object]::ReferenceEquals($Context.CompactLines[$i],$Context.CompactOriginal[$i])) {
                throw 'primary_witness_compact_changed'
            }
            $previous = $physical
        }
        if ($membership.SelectedIndex -ne $selected -or
            (Get-FileTransferLocalCarrierHash $Context.FullLines[$selected]) -cne $Witness.ContentSha256) { throw 'primary_witness_selection_changed' }

        $selectedFields = Fields $Context.FullLines[$selected]
        $selectedKeys = @{
            Boundaries="$($selectedFields.publication_evidence_coordinator_id)|$($selectedFields.publication_evidence_boundary_id)"
            States="$($selectedFields.publication_evidence_coordinator_id)|$($selectedFields.publication_evidence_observed_state_generation)"
        }
        $indexes = Dictionary
        foreach ($prefix in @('full','compact')) {
            $view = $fullView; $snapshot = $Context.FullLines
            if ($prefix -ceq 'compact') { $view = $compactView; $snapshot = $Context.CompactLines }
            $publications = @{Boundaries=(Dictionary);States=(Dictionary)}
            foreach ($boundary in $view.Boundaries) {
                Check
                if (-not (Integer $boundary.evidenceLineIndex 0) -or $boundary.evidenceLineIndex -ge $snapshot.Length -or
                    $boundary.operations -isnot [array] -or $boundary.operations.Count -gt 64 -or
                    $work.operationRows + $boundary.operations.Count -gt [NLink.Tools.LocalPublicationInputLimits]::OperationRows) { throw 'primary_witness_boundary_budget' }
                $work.operationRows += $boundary.operations.Count
                $fields = Fields $snapshot[$boundary.evidenceLineIndex]
                if ($fields.ContainsKey('__nlink_duplicate_structured_field') -or $fields.event -cne 'filetransfer_coordinator_transition_snapshot' -or
                    $fields.transition_id -cne $boundary.boundaryId -or $fields.snapshot_sha256 -cne $boundary.boundaryHash -or
                    $fields.log_writer_id -cne $boundary.writer -or $fields.log_writer_sequence -cne [string]$boundary.writerSequence -or
                    $boundary.verdict -cne 'pass') { throw 'primary_witness_boundary_origin' }
                foreach ($entry in @(@('Boundaries',"$($boundary.coordinatorInstanceId)|$($boundary.boundaryId)"),
                    @('States',"$($boundary.coordinatorInstanceId)|$($boundary.afterStateGeneration)"))) {
                    $map = $publications[$entry[0]]; $key = $entry[1]
                    # Validate every original boundary above, but retain only
                    # the selected use's keys. Keep all selected alternatives;
                    # unrelated idempotent history cannot exhaust their cap.
                    if ($key -cne $selectedKeys[$entry[0]]) { continue }
                    if (-not $map.ContainsKey($key)) { $map[$key] = @() }
                    if ($map[$key].Count -ge 64) { throw 'primary_witness_observation_budget' }
                    $map[$key] += @($boundary)
                }
            }
            $indexes[$prefix] = @{Reason='';SnapshotPath='';SnapshotRole=$prefix+'Snapshot';SnapshotLines=$snapshot
                Effects=$view.__RoutePublicationEffects
                Local=@{Reason='';Path='';NativeRole=$prefix+'Capture';Lines=$Context.FullLines;Publications=$publications}}
        }
        $full = Get-FileTransferLocalPublicationRow $indexes.full ($selected+1L) 'fullCapture'
        Check
        $compact = Get-FileTransferLocalPublicationRow $indexes.compact ($selected+1L) 'compactCapture'
        $fullRowJson = ConvertTo-FileTransferLocalCarrierJson $full.Row
        $compactRowJson = ConvertTo-FileTransferLocalCarrierJson $compact.Row
        if ($fullRowJson -cne $compactRowJson -or
            $Context.FullOrigins[$full.Location.publicationLine-1] -ne $Context.CompactOrigins[$compact.Location.publicationLine-1] -or
            $Context.FullOrigins[$full.Location.effectLine-1] -ne $Context.CompactOrigins[$compact.Location.effectLine-1]) { throw 'primary_witness_row_parity' }
        $witnessData = @{reportClass=$Witness.ReportClass;reportReason=$Witness.ReportReason;eventName=$Witness.EventName
            sourceLine=$Witness.SourceLine;writer=$Witness.Writer;writerSequence=$Witness.WriterSequence;contentSha256=$Witness.ContentSha256}
        $provenanceData = @{full=$full.Location;compact=$compact.Location
            publicationSourceLine=$Context.FullOrigins[$full.Location.publicationLine-1];effectSourceLine=$Context.FullOrigins[$full.Location.effectLine-1]}
        $witnessJson = ConvertTo-FileTransferLocalCarrierJson $witnessData
        $provenanceJson = ConvertTo-FileTransferLocalCarrierJson $provenanceData
        $json = ConvertTo-FileTransferLocalCarrierJson @{
            requestId=$Context.SourceOwner.RequestId.ToString('D'); scope='local_publication_forwarding';peerLineageStatus='not_evaluated'
            acceptanceRunId=$Context.SourceOwner.AcceptanceRunId;scenarioRunId=$Context.SourceOwner.ScenarioRunId
            witness=$witnessData;row=$full.Row;provenance=$provenanceData
        }
        Check
        if (-not $writer.IsVerified) { throw 'primary_witness_writer_lifetime' }
        $Context.WriterReader = $writer; $matched = $true
        return [pscustomobject]@{Status='prepared_primary_publication_witness';Json=$json;Work=$work;PublicationCompletionIssued=$false
            FullRowJson=$fullRowJson;CompactRowJson=$compactRowJson;WitnessJson=$witnessJson;ProvenanceJson=$provenanceJson}
    }
    catch { if ($null -ne $Context) { $Context.PublicationOmissionReason = $_.Exception.Message }; return $null }
    finally {
        if (-not $matched -and $null -ne $writer) { $writer.Dispose() }
        if ($null -ne $Context) { $Context.Work.Stop() }
    }
}
