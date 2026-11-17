Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PublicationForwardingEvidence.ps1')



function Assert-FileTransferLocalCarrierShape {
    param($Body)
    function Keys($Object, [string]$Names) {
        if ($Object -isnot [pscustomobject]) { throw 'carrier_object_type' }
        $expected = $Names.Split(','); $actual = @($Object.PSObject.Properties.Name)
        if ($actual.Count -ne $expected.Count) { throw 'carrier_schema' }
        foreach ($key in $actual) { if ($key -cnotin $expected) { throw 'carrier_schema' } }
    }
    function Text($Value) {
        if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) { throw 'carrier_text' }
    }
    function Shape($Value, [string]$Pattern) {
        Text $Value
        if ($Value -cnotmatch $Pattern) { throw 'carrier_reference_shape' }
    }
    function Number($Value, [long]$Minimum = 0) {
        if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum) { throw 'carrier_integer' }
    }
    Keys $Body 'format,requestId,scope,peerLineageStatus,acceptanceRunId,scenarioRunId,snapshot,inputs,rows,provenance'
    $request = [guid]::Empty
    if (-not [guid]::TryParseExact($Body.requestId, 'D', [ref]$request) -or $request -eq [guid]::Empty -or
        $request.ToString('D') -cne $Body.requestId -or $Body.format -cne 'nlink-local-publication-forwarding-v1' -or
        $Body.scope -cne 'local_publication_forwarding' -or $Body.peerLineageStatus -cne 'not_evaluated') { throw 'carrier_scope' }
    Text $Body.acceptanceRunId; Text $Body.scenarioRunId
    Keys $Body.snapshot 'contractVersion,catalogVersion,catalogHash,boundaryHash'
    Number $Body.snapshot.contractVersion 1
    if ($Body.snapshot.contractVersion -ne 1) { throw 'carrier_contract' }
    Text $Body.snapshot.catalogVersion
    Shape $Body.snapshot.catalogHash '^[0-9a-f]{64}$'; Shape $Body.snapshot.boundaryHash '^[0-9a-f]{64}$'
    Keys $Body.inputs 'writer,fullCapture,fullSnapshot,compactCapture,compactSnapshot'
    foreach ($role in $Body.inputs.PSObject.Properties.Name) {
        $descriptor = $Body.inputs.$role
        Keys $descriptor 'absolutePath,decodedLineCount,sha256,domain'
        Text $descriptor.absolutePath; Number $descriptor.decodedLineCount 1
        Shape $descriptor.sha256 '^[0-9a-f]{64}$'
        if ($descriptor.domain -cne 'sha256-file-bytes-v1' -or
            -not [IO.Path]::IsPathRooted($descriptor.absolutePath) -or
            [IO.Path]::GetFullPath($descriptor.absolutePath) -cne $descriptor.absolutePath) { throw 'carrier_input_descriptor' }
    }
    if ($Body.rows -isnot [array] -or $Body.provenance -isnot [array] -or $Body.rows.Count -lt 1 -or
        $Body.rows.Count -gt 64 -or $Body.provenance.Count -ne $Body.rows.Count) { throw 'carrier_row_limit' }
    $previous = $null; $protocols = @{regular_nkn_v4_fast=4; file_tuna_v4=4; post_tuna_fallback_v6=6}
    for ($i=0; $i -lt $Body.rows.Count; $i++) {
        $row = $Body.rows[$i]
        Keys $row 'coordinatorId,writer,sessionId,transferId,direction,boundaryId,boundaryHash,boundaryWriterSequence,authoredStateGeneration,observedStateGeneration,operationKey,operationKind,authorityId,operationGeneration,receiptIdentity,receiptGeneration,receiptStatus,receiptFingerprint,routeState,useKind,useWriterSequence,useContentSha256,effectIdentity,effectWriterSequence'
        foreach ($key in @('coordinatorId','sessionId','transferId','boundaryId','authorityId','receiptIdentity','receiptFingerprint','effectIdentity')) {
            Shape $row.$key '^fte1-[0-9a-f]{24}$'
        }
        Shape $row.boundaryHash '^[0-9a-f]{64}$'; Shape $row.useContentSha256 '^[0-9a-f]{64}$'; Text $row.writer
        foreach ($key in @('boundaryWriterSequence','authoredStateGeneration','observedStateGeneration','operationGeneration',
            'receiptGeneration','useWriterSequence','effectWriterSequence')) { Number $row.$key 1 }
        $writerOrder = if ($i -gt 0) { [StringComparer]::Ordinal.Compare($previous.writer, $row.writer) } else { -1 }
        if (($i -gt 0 -and ($writerOrder -gt 0 -or ($writerOrder -eq 0 -and $previous.useWriterSequence -ge $row.useWriterSequence))) -or
            $row.boundaryWriterSequence -ge $row.effectWriterSequence -or $row.effectWriterSequence -ge $row.useWriterSequence -or
            $row.useWriterSequence -gt $Body.inputs.writer.decodedLineCount -or
            $row.observedStateGeneration -lt $row.authoredStateGeneration -or $row.receiptStatus -cne 'Accepted' -or
            $row.receiptIdentity -cne $row.authorityId -or $row.effectIdentity -cne $row.authorityId -or
            $row.receiptGeneration -ne $row.operationGeneration -or $row.operationKind -cnotin @('RoutePublication','RouteCommitPublication') -or
            $row.operationKey -cne "route_runtime|route_publication|$($row.authorityId)|$($row.operationGeneration)|$($row.operationKind)") {
            throw 'carrier_local_reference'
        }
        $previous = $row
        if (($row.direction -ceq 'outbound' -and $row.useKind -cin @('v4_pre_pump_request','v4_physical_submission')) -or
            ($row.direction -ceq 'inbound' -and $row.useKind -cin @('receive_dispatch','receive_guard'))) { }
        else { throw 'carrier_use_kind' }
        $tuple = $row.routeState
        Keys $tuple 'route,protocolVersion,liveRouteEpoch,routePublicationGeneration,routeCommitGeneration,runtimeGeneration,terminalGeneration,legGeneration,transportEpoch,bridgeRecoveryGeneration,checkpointGeneration,checkpointRequestId,dataSessionIdentity,dataSessionGeneration'
        foreach ($key in @('protocolVersion','routePublicationGeneration','runtimeGeneration','legGeneration')) { Number $tuple.$key 1 }
        foreach ($key in @('liveRouteEpoch','routeCommitGeneration','terminalGeneration','transportEpoch','bridgeRecoveryGeneration',
            'checkpointGeneration','dataSessionGeneration')) { Number $tuple.$key }
        foreach ($key in @('checkpointRequestId','dataSessionIdentity')) { Shape $tuple.$key '^(\(none\)|fte1-[0-9a-f]{24})$' }
        if (-not $protocols.ContainsKey([string]$tuple.route) -or $tuple.protocolVersion -ne $protocols[$tuple.route] -or
            $tuple.routePublicationGeneration -ne $row.operationGeneration) { throw 'carrier_route' }
        $locations = $Body.provenance[$i]
        Keys $locations 'full,compact'
        foreach ($view in @('full','compact')) {
            $location = $locations.$view
            Keys $location 'useLine,publicationLine,effectLine'
            foreach ($key in @('useLine','publicationLine','effectLine')) {
                Number $location.$key 1
                $inputRole = if ($key -ceq 'useLine') { $view+'Capture' } else { $view+'Snapshot' }
                if ($location.$key -gt $Body.inputs.$inputRole.decodedLineCount) { throw 'carrier_location' }
            }
            if ($location.publicationLine -ge $location.effectLine) { throw 'carrier_location_order' }
        }
    }
}

function Read-FileTransferLocalPublicationEvidenceCarrier {
    param([string]$Json, [string]$ExpectedRequestId, [string]$ExpectedSha256, [string]$ExpectedInputsSha256)
    try {
        if ([string]::IsNullOrEmpty($Json) -or $Json.Length -gt 262144 -or
            $ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$' -or $ExpectedInputsSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            (Get-FileTransferLocalCarrierHash $Json) -cne $ExpectedSha256) { throw 'carrier_transport_binding' }
        $body = ConvertFrom-Json -InputObject $Json
        # Equality rejects duplicate keys, alternate escapes/numbers/whitespace
        # and parser-specific coercions. A self-declared digest is never trusted.
        if ((ConvertTo-FileTransferLocalCarrierJson $body) -cne $Json) { throw 'carrier_noncanonical' }
        Assert-FileTransferLocalCarrierShape $body
        if ($body.requestId -cne $ExpectedRequestId -or
            (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.inputs)) -cne $ExpectedInputsSha256) {
            throw 'carrier_request_or_inputs_binding'
        }
        return [pscustomobject]@{Status='validated_local_carrier'; Reason=''; Carrier=$body}
    }
    catch { return [pscustomobject]@{Status='omitted'; Reason='carrier_invalid'; Carrier=$null} }
}


function New-FileTransferLocalPublicationEvidenceCarrier {
    param([hashtable]$Full, [hashtable]$Compact, [string]$RequestId, $UseLines)
    $reason = 'carrier_selection_invalid'
    try {
        $request = [guid]::Empty
        if (-not [guid]::TryParseExact($RequestId, 'D', [ref]$request) -or $request -eq [guid]::Empty -or
            $request.ToString('D') -cne $RequestId -or $UseLines -isnot [array] -or $UseLines.Count -lt 1 -or $UseLines.Count -gt 64) { throw $reason }
        foreach ($line in $UseLines) {
            if (($line -isnot [int] -and $line -isnot [long]) -or $line -lt 1) { throw $reason }
        }
        $reason = 'carrier_inputs_unproven'
        $fullIndex = New-FileTransferPublicationForwardingIndex @Full
        $compactIndex = New-FileTransferPublicationForwardingIndex @Compact
        if ($fullIndex.Reason -or $compactIndex.Reason) { throw $reason }
        foreach ($key in @('AcceptanceRunId','ScenarioRunId')) { if ($Full[$key] -cne $Compact[$key]) { throw $reason } }
        foreach ($key in @('ContractVersion','CatalogVersion','CatalogHash','BoundaryHash')) {
            if ((ConvertTo-FileTransferLocalCarrierJson $Full.SnapshotEvidence.$key) -cne
                (ConvertTo-FileTransferLocalCarrierJson $Compact.SnapshotEvidence.$key)) { throw $reason }
        }
        if ([IO.Path]::GetFullPath($Full.WriterPath) -cne [IO.Path]::GetFullPath($Compact.WriterPath) -or
            $Full.WriterEvidence.WriterSha256 -cne $Compact.WriterEvidence.WriterSha256 -or
            $Full.WriterEvidence.WriterLineCount -ne $Compact.WriterEvidence.WriterLineCount) { throw $reason }
        function Descriptor([string]$Path, [long]$Count, [string]$Hash) {
            @{absolutePath=[IO.Path]::GetFullPath($Path); decodedLineCount=$Count; sha256=$Hash; domain='sha256-file-bytes-v1'}
        }
        $inputs = @{
            writer=(Descriptor $Full.WriterPath $Full.WriterEvidence.WriterLineCount $Full.WriterEvidence.WriterSha256)
            fullCapture=(Descriptor $fullIndex.Local.Path $fullIndex.Local.Lines.Length $Full.InputSha256)
            fullSnapshot=(Descriptor $fullIndex.SnapshotPath $fullIndex.SnapshotLines.Length $Full.SnapshotInputSha256)
            compactCapture=(Descriptor $compactIndex.Local.Path $compactIndex.Local.Lines.Length $Compact.InputSha256)
            compactSnapshot=(Descriptor $compactIndex.SnapshotPath $compactIndex.SnapshotLines.Length $Compact.SnapshotInputSha256)
        }
        function UseLocations($Index) {
            $locations = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
            for ($i=0; $i -lt $Index.Local.Lines.Length; $i++) {
                $fields = ConvertFrom-GuiSmokeLogLedgerStructuredFields $Index.Local.Lines[$i]
                if (-not $fields.ContainsKey('publication_use_kind')) { continue }
                $key = "$($fields.log_writer_id)|$($fields.log_writer_sequence)"
                if (-not $locations.ContainsKey($key)) { $locations[$key] = @() }
                $locations[$key] += @($i+1L)
            }
            return ,$locations
        }
        $fullUses = UseLocations $fullIndex; $compactUses = UseLocations $compactIndex
        $reason = 'carrier_projection_unproven'
        $selected = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($line in $UseLines) {
            $fullRow = Get-FileTransferLocalPublicationRow $fullIndex $line
            $key = "$($fullRow.Row.writer)|$($fullRow.Row.useWriterSequence)"
            if ($fullUses[$key].Count -ne 1 -or -not $compactUses.ContainsKey($key) -or $compactUses[$key].Count -ne 1) { throw $reason }
            $compactRow = Get-FileTransferLocalPublicationRow $compactIndex $compactUses[$key][0]
            if ($fullRow.Raw -cne $compactRow.Raw -or
                (ConvertTo-FileTransferLocalCarrierJson $fullRow.Row) -cne (ConvertTo-FileTransferLocalCarrierJson $compactRow.Row)) { throw $reason }
            $writer = $fullRow.Row.writer; $sequence = $fullRow.Row.useWriterSequence
            if (-not $selected.ContainsKey($writer)) { $selected[$writer] = [Collections.Generic.SortedDictionary[long,object]]::new() }
            if ($selected[$writer].ContainsKey($sequence)) { throw $reason }
            $selected[$writer][$sequence] = @{Row=$fullRow.Row; Provenance=@{full=$fullRow.Location; compact=$compactRow.Location}}
        }
        $ordered = @(foreach ($group in $selected.Values) { foreach ($entry in $group.Values) { $entry } })
        $rows = @($ordered | ForEach-Object { $_.Row })
        $provenance = @($ordered | ForEach-Object { $_.Provenance })
        $body = @{
            format='nlink-local-publication-forwarding-v1'; requestId=$RequestId; scope='local_publication_forwarding'; peerLineageStatus='not_evaluated'
            acceptanceRunId=$Full.AcceptanceRunId; scenarioRunId=$Full.ScenarioRunId; inputs=$inputs; rows=$rows; provenance=$provenance
            snapshot=@{contractVersion=$Full.SnapshotEvidence.ContractVersion; catalogVersion=$Full.SnapshotEvidence.CatalogVersion
                catalogHash=$Full.SnapshotEvidence.CatalogHash; boundaryHash=$Full.SnapshotEvidence.BoundaryHash}
        }
        $reason = 'carrier_serialization_invalid'
        $json = ConvertTo-FileTransferLocalCarrierJson $body
        $hash = Get-FileTransferLocalCarrierHash $json
        $inputsHash = Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $inputs)
        if ((Read-FileTransferLocalPublicationEvidenceCarrier $json $RequestId $hash $inputsHash).Status -cne 'validated_local_carrier') { throw $reason }
        return [pscustomobject]@{Status='ready'; Reason=''; Json=$json; Sha256=$hash; InputsSha256=$inputsHash
            SemanticSha256=(Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $rows))}
    }
    catch { return [pscustomobject]@{Status='omitted'; Reason=$reason; Json=$null; Sha256=$null; InputsSha256=$null; SemanticSha256=$null} }
}
