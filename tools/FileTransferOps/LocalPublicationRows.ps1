Set-StrictMode -Version Latest
# Pure row/codec helpers only. The caller already owns the Snapshot helpers.
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'GuiSmokeStructuredFields.ps1')

function Test-FileTransferPublicationUseInteger {
    param([string]$Value, [long]$Minimum = 0)
    $number = 0L
    return $Value -cmatch '^(0|[1-9][0-9]*)$' -and
        [long]::TryParse($Value, [ref]$number) -and $number -ge $Minimum
}

function Resolve-FileTransferPublicationUseEvidence {
    param($Index, [string]$Path, [long]$LineNumber, [string]$RawLine, [string]$NativeRole = '')
    function Unproven([string]$Reason) { [pscustomobject]@{ Status='unproven'; Reason=$Reason; Reference=$null } }
    try {
        if ($null -eq $Index -or $Index.Reason) { return Unproven 'input_evidence_invalid' }
        # Native callers supply an already bound in-memory index. Never describe
        # its ordinals as physical positions in a file that does not exist.
        if ($NativeRole) {
            if ($NativeRole -cnotin @('fullCapture','compactCapture') -or $Index.NativeRole -cne $NativeRole -or $Path) {
                return Unproven 'native_role_mismatch'
            }
        }
        elseif ([IO.Path]::GetFullPath($Path) -cne $Index.Path) { return Unproven 'record_not_in_input' }
        if ($LineNumber -le 0 -or $LineNumber -gt $Index.Lines.Length -or
            $RawLine -cne $Index.Lines[$LineNumber - 1]) { return Unproven 'record_not_in_input' }
        $f = ConvertFrom-GuiSmokeLogLedgerStructuredFields $RawLine
        if ($f.ContainsKey('__nlink_duplicate_structured_field')) { return Unproven 'duplicate_field' }
        if ($f['publication_evidence_version'] -cne '1' -or $f['security_context_status'] -cne 'captured') {
            return Unproven 'capture_unavailable'
        }
        foreach ($name in @('coordinator_id','session_id','transfer_id','boundary_id','token_fingerprint')) {
            if ($f['publication_evidence_' + $name] -cnotmatch '^fte1-[0-9a-f]{24}$') { return Unproven 'reference_shape_invalid' }
        }
        $shapes = @{ security_transport_id='ftst1-[0-9a-f]{32}'; security_installation_id='ftsi1-[0-9a-f]{32}';
            security_establishment_fingerprint='ftest1-[0-9a-f]{64}'; security_payload_fingerprint='ftpay1-[0-9a-f]{64}';
            data_path_envelope_fingerprint='ftenv1-[0-9a-f]{64}' }
        foreach ($name in $shapes.Keys) {
            if ([string]$f[$name] -cnotmatch ('^' + $shapes[$name] + '$')) { return Unproven 'capture_shape_invalid' }
        }
        foreach ($name in @('publication_evidence_state_generation','publication_evidence_observed_state_generation',
                'security_installation_generation','security_sequence','route_publication_generation','runtime_generation','leg_generation')) {
            if (-not (Test-FileTransferPublicationUseInteger ([string]$f[$name]) 1)) { return Unproven 'generation_invalid' }
        }
        foreach ($name in @('terminal_generation','route_commit_generation','live_route_epoch')) {
            if (-not (Test-FileTransferPublicationUseInteger ([string]$f[$name]))) { return Unproven 'generation_invalid' }
        }
        foreach ($scope in @('session','transfer')) {
            $projected = [string]$f["publication_evidence_${scope}_id"]
            if ($f["security_${scope}_id"] -cne $projected -or $f["data_path_${scope}_id"] -cne $projected) {
                return Unproven 'projected_scope_mismatch'
            }
            $raw = [string]$f["${scope}_id"]
            if ([string]::IsNullOrWhiteSpace($raw)) { return Unproven 'raw_scope_unavailable' }
            if ($raw -ceq '[redacted]' -or ($scope -ceq 'session' -and $raw -ceq 'sess_[redacted]')) { continue }
            if ($raw.Contains('[redacted]') -or (ConvertTo-FileTransferCoordinatorEvidenceToken $raw) -cne $projected) {
                return Unproven 'raw_scope_mismatch'
            }
        }
        $kind = [string]$f['publication_use_kind']; $event = [string]$f.event
        if ($kind -cin @('v4_physical_submission','v4_pre_pump_request')) {
            if ($event -cne 'filetransfer_data_path_send_result' -or $f.direction -cne 'outbound') { return Unproven 'use_kind_mismatch' }
        }
        elseif ($kind -ceq 'receive_guard') {
            if ($event -cnotin @('filetransfer_data_path_receive_result','filetransfer_tuna_data_path_mismatch','filetransfer_data_frame_ignored') -or
                $f.direction -cne 'inbound' -or $f['receive_disposition'] -cne 'rejected_before_dispatch' -or
                $f['authority_effect'] -cne 'none') { return Unproven 'use_kind_mismatch' }
        }
        elseif ($kind -ceq 'receive_dispatch') {
            if ($event -cnotin @('filetransfer_data_path_receive_result','filetransfer_tuna_data_path_peer_visible') -or
                $f.direction -cne 'inbound' -or $f['receive_disposition'] -cne 'dispatch_entered') { return Unproven 'use_kind_mismatch' }
        }
        else { return Unproven 'use_kind_mismatch' }
        $coordinator = [string]$f.publication_evidence_coordinator_id
        $generation = [long]$f.publication_evidence_state_generation
        $observed = [long]$f.publication_evidence_observed_state_generation
        $key = "$coordinator|$($f.publication_evidence_boundary_id)"; $stateKey = "$coordinator|$observed"
        $p = $Index.Publications
        if ($observed -lt $generation -or -not $p.Boundaries.ContainsKey($key) -or $p.Boundaries[$key].Count -ne 1 -or
            -not $p.States.ContainsKey($stateKey) -or $p.States[$stateKey].Count -eq 0) { return Unproven 'local_boundary_unbound' }
        $boundary = $p.Boundaries[$key][0]; $observations = @($p.States[$stateKey])
        # Idempotent decisions may repeat a generation, but cannot supply a
        # different state or duplicate boundary. Validate all, never pick one.
        $stateHashes = @($observations.afterStateHash | Sort-Object -Unique)
        if ($stateHashes.Count -ne 1 -or $stateHashes[0] -cnotmatch '^fte1-[0-9a-f]{24}$' -or
            @($observations.boundaryId | Sort-Object -Unique).Count -ne $observations.Count) { return Unproven 'observed_state_ambiguous' }
        if ([long]$boundary.afterStateGeneration -ne $generation) { return Unproven 'local_boundary_unbound' }
        $operations = @($boundary.operations | Where-Object operationKey -CEQ $f.publication_evidence_operation_key)
        if ($operations.Count -ne 1) { return Unproven 'publication_receipt_unbound' }
        $op = $operations[0]; $fingerprint = [string]$f.publication_evidence_token_fingerprint
        if ($op.family -cne 'route_runtime' -or $op.operationRole -cne 'route_publication' -or $op.afterState -cne 'Published' -or
            $op.operationKind -cnotin @('RoutePublication','RouteCommitPublication') -or $op.receiptValidated -ne $true -or
            $op.receiptStatus -cne 'Accepted' -or $op.receiptIdentity -cne $op.authorityId -or $op.authorityId -cnotmatch '^fte1-[0-9a-f]{24}$' -or
            [string]$op.operationGeneration -cne $f.route_publication_generation -or [string]$op.receiptGeneration -cne $f.route_publication_generation -or
            $op.receiptFingerprint -cne $fingerprint -or $op.routeRuntime.role -cne 'route_publication' -or
            $op.routeRuntime.tokenFingerprint -cne $fingerprint -or
            $op.operationKey -cne "route_runtime|route_publication|$($op.authorityId)|$($op.operationGeneration)|$($op.operationKind)") {
            return Unproven 'publication_receipt_unbound'
        }
        $protocols = @{regular_nkn_v4_fast='4';file_tuna_v4='4';post_tuna_fallback_v6='6'}
        if (-not $protocols.ContainsKey([string]$f.route) -or $f.protocol_version -cne $protocols[$f.route]) { return Unproven 'route_scope_mismatch' }
        $mapping = @{route='route';protocol_version='protocolVersion';live_route_epoch='liveRouteEpoch';runtime_generation='runtimeGeneration';
            terminal_generation='terminalGeneration';leg_generation='legGeneration';route_publication_generation='routePublicationGeneration';route_commit_generation='routeCommitGeneration'}
        foreach ($tuple in @($op.routeRuntime.boundary,$boundary.afterBoundary) + @($observations.afterBoundary)) {
            foreach ($name in $mapping.Keys) {
                if ([string]$tuple.($mapping[$name]) -cne [string]$f[$name]) { return Unproven 'route_scope_mismatch' }
            }
        }
        foreach ($b in @($boundary) + $observations) {
            if ($b.writer -cne $f.log_writer_id -or $b.coordinatorInstanceId -cne $coordinator -or $b.direction -cne $f.direction -or
                $b.sessionId -cne $f.publication_evidence_session_id -or $b.transferId -cne $f.publication_evidence_transfer_id) {
                return Unproven 'local_owner_mismatch'
            }
        }
        if ($f.ContainsKey('continuity_coordinator_id') -and $f.continuity_coordinator_id -cne $coordinator) { return Unproven 'local_owner_mismatch' }
        # Data-session overlays can change without advancing the Coordinator
        # state. Check every observation when carried; absence never selects an
        # incarnation or upgrades this local publication-reference result.
        foreach ($observation in $observations) {
            if ($f.ContainsKey('data_session_generation') -and
                (-not (Test-FileTransferPublicationUseInteger $f.data_session_generation) -or
                 $f.data_session_generation -cne [string]$observation.afterBoundary.dataSessionGeneration)) { return Unproven 'data_session_mismatch' }
            if ($f.ContainsKey('data_session_identity') -and
                ([string]::IsNullOrWhiteSpace($f.data_session_identity) -or $f.data_session_identity.Contains('[redacted]') -or
                 (ConvertTo-FileTransferCoordinatorEvidenceToken $f.data_session_identity) -cne $observation.afterBoundary.dataSessionIdentity)) {
                return Unproven 'data_session_mismatch'
            }
        }
        $reference = [pscustomobject]@{
            CoordinatorId=$coordinator; BoundaryId=[string]$boundary.boundaryId; OperationKey=[string]$op.operationKey
            TokenFingerprint=$fingerprint; AuthoredStateGeneration=$generation; ObservedStateGeneration=$observed
            Writer=[string]$f.log_writer_id; WriterSequence=[long]$f.log_writer_sequence; UseKind=$kind
            SessionId=[string]$f.publication_evidence_session_id; TransferId=[string]$f.publication_evidence_transfer_id
            SourcePath=$Index.Path; SourceLine=$LineNumber
        }
        if ($NativeRole) { $reference.SourcePath=''; $reference | Add-Member NoteProperty NativeRole $NativeRole }
        return [pscustomobject]@{ Status='matched_local_reference'; Reason=''; Reference=$reference }
    }
    catch { return Unproven 'malformed_evidence' }
}

function Resolve-FileTransferPublicationForwardingEvidence {
    param($Index, [string]$Path, [long]$LineNumber, [string]$RawLine, [string]$NativeRole = '')
    function Unproven([string]$Reason) { [pscustomobject]@{Status='unproven'; Reason=$Reason; Forwarding=$null} }
    try {
        if ($null -eq $Index -or $Index.Reason) { return Unproven 'forwarding_input_unproven' }
        if ($NativeRole -and $Index.SnapshotRole -cne $NativeRole.Replace('Capture','Snapshot')) {
            return Unproven 'native_role_mismatch'
        }
        $use = Resolve-FileTransferPublicationUseEvidence $Index.Local $Path $LineNumber $RawLine $NativeRole
        if ($use.Status -cne 'matched_local_reference') { return Unproven 'local_use_unproven' }
        $reference = $use.Reference
        $boundary = $Index.Local.Publications.Boundaries["$($reference.CoordinatorId)|$($reference.BoundaryId)"][0]
        $operation = @($boundary.operations | Where-Object operationKey -CEQ $reference.OperationKey)[0]
        $matches = @($Index.Effects | Where-Object {
            $_.CoordinatorInstanceId -ceq $reference.CoordinatorId -and $_.TransitionId -ceq $reference.BoundaryId
        })
        if ($matches.Count -ne 1) { return Unproven 'forwarding_effect_ambiguous_or_missing' }
        $effect = $matches[0]
        $offset = 0L
        if (-not (Test-FileTransferPublicationUseInteger ([string]$effect.LineIndex)) -or
            -not [long]::TryParse([string]$effect.LineIndex, [ref]$offset) -or
            $offset -ge $Index.SnapshotLines.Length) { return Unproven 'forwarding_location_invalid' }
        $fields = ConvertFrom-GuiSmokeLogLedgerStructuredFields $Index.SnapshotLines[$offset]
        if ($fields.ContainsKey('__nlink_duplicate_structured_field')) { return Unproven 'forwarding_duplicate_field' }
        $mapping = @{
            Writer='log_writer_id'; WriterSequence='log_writer_sequence'; CoordinatorInstanceId='coordinator_instance_id'
            TransitionId='transition_id'; StateGeneration='coordinator_state_generation'; Family='operation_family'
            OperationKey='operation_key'; AuthorityId='operation_authority_id'; OperationGeneration='operation_generation'
            EffectKind='effect_kind'; EffectIdentity='effect_identity'; ReceiptIdentity='receipt_identity'
            ReceiptGeneration='receipt_generation'; ReceiptStatus='receipt_status'; ReceiptFingerprint='receipt_fingerprint'
            Route='route'; ProtocolVersion='protocol_version'; LiveRouteEpoch='live_route_epoch'
            RoutePublicationGeneration='route_publication_generation'; RouteCommitGeneration='route_commit_generation'
            RuntimeGeneration='runtime_generation'; TerminalGeneration='terminal_generation'; LegGeneration='leg_generation'
            TransportEpoch='transport_epoch'; BridgeRecoveryGeneration='bridge_recovery_generation'
            CheckpointGeneration='checkpoint_generation'; CheckpointRequestId='checkpoint_request_id'
            DataSessionIdentity='data_session_identity'; DataSessionGeneration='data_session_generation'
        }
        foreach ($name in $mapping.Keys) {
            if ([string]::IsNullOrWhiteSpace([string]$fields[$mapping[$name]]) -or
                [string]$effect.$name -cne [string]$fields[$mapping[$name]]) { return Unproven 'forwarding_content_mismatch' }
        }
        foreach ($name in @('log_writer_sequence','coordinator_state_generation','operation_generation','effect_generation',
                'receipt_generation','protocol_version','route_publication_generation','runtime_generation','leg_generation')) {
            if (-not (Test-FileTransferPublicationUseInteger $fields[$name] 1)) { return Unproven 'forwarding_generation_invalid' }
        }
        foreach ($name in @('live_route_epoch','route_commit_generation','terminal_generation','transport_epoch',
                'bridge_recovery_generation','checkpoint_generation','data_session_generation')) {
            if (-not (Test-FileTransferPublicationUseInteger $fields[$name])) { return Unproven 'forwarding_generation_invalid' }
        }
        if ($fields.event -cne 'filetransfer_coordinator_authority_effect' -or
            $effect.EffectKind -cne 'route_publication_forwarded' -or $fields.authority_effect -cne 'applied' -or
            $fields.session_id -cne $reference.SessionId -or $fields.transfer_id -cne $reference.TransferId -or
            $fields.direction -cne $boundary.direction -or $effect.Writer -cne $reference.Writer -or
            $effect.EffectIdentity -cne $operation.authorityId -or
            $fields.effect_generation -cne [string]$operation.operationGeneration -or
            -not (Test-FileTransferCoordinatorSnapshotExactEffectBinding $boundary $operation $effect)) {
            return Unproven 'forwarding_receipt_or_scope_mismatch'
        }
        if ([long]$effect.WriterSequence -le [long]$boundary.writerSequence -or
            [long]$effect.WriterSequence -ge $reference.WriterSequence) { return Unproven 'forwarding_local_order_unproven' }
        $effectReference = [pscustomobject]@{
            EffectIdentity=[string]$effect.EffectIdentity; Writer=[string]$effect.Writer
            WriterSequence=[long]$effect.WriterSequence; BoundaryId=[string]$effect.TransitionId
            AuthoredStateGeneration=[long]$effect.StateGeneration
            SourcePath=$Index.SnapshotPath; SourceLine=$offset+1
        }
        if ($NativeRole) { $effectReference.SourcePath=''; $effectReference | Add-Member NoteProperty NativeRole $Index.SnapshotRole }
        return [pscustomobject]@{Status='matched_local_forwarding'; Reason=''; Forwarding=[pscustomobject]@{
            Use=$reference
            Effect=$effectReference
        }}
    }
    catch { return Unproven 'malformed_forwarding_evidence' }
}

function ConvertTo-FileTransferLocalCarrierJson {
    param($Value)
    # A deliberately small canonical codec, not Snapshot normalization: retain
    # arrays and physical locations, and never accept culture-dependent numbers.
    $buffer = [Text.StringBuilder]::new()
    function Append([string]$Text) {
        if ($buffer.Length + $Text.Length -gt 262144) { throw 'carrier_size_limit' }
        [void]$buffer.Append($Text)
    }
    function Quoted([string]$Text) {
        if ($Text.Length -gt 262144) { throw 'carrier_size_limit' }
        Append '"'
        if ($Text -cnotmatch '["\\\x00-\x1f\x7f-\uffff]') { Append $Text; Append '"'; return }
        [void][Text.UTF8Encoding]::new($false, $true).GetByteCount($Text)
        foreach ($character in $Text.ToCharArray()) {
            $code = [int]$character
            if ($code -eq 34) { Append '\"' }
            elseif ($code -eq 92) { Append '\\' }
            elseif ($code -lt 32 -or $code -gt 126) { Append ('\u' + $code.ToString('x4')) }
            else { Append ([string]$character) }
        }
        Append '"'
    }
    function Emit($Node, [int]$Depth) {
        if ($Depth -gt 16) { throw 'carrier_depth_limit' }
        if ($null -eq $Node) { Append 'null'; return }
        if ($Node -is [string]) { Quoted $Node; return }
        if ($Node -is [bool]) { if ($Node) { Append 'true' } else { Append 'false' }; return }
        if ($Node -is [byte] -or $Node -is [sbyte] -or $Node -is [int16] -or $Node -is [uint16] -or
            $Node -is [int32] -or $Node -is [uint32] -or $Node -is [int64] -or $Node -is [uint64]) {
            Append ([long]$Node).ToString([Globalization.CultureInfo]::InvariantCulture); return
        }
        if ($Node -is [array]) {
            Append '['; $separator = ''
            foreach ($item in $Node) { Append $separator; Emit $item ($Depth+1); $separator = ',' }
            Append ']'; return
        }
        if ($Node -is [Collections.IDictionary]) {
            foreach ($key in $Node.Keys) { if ($key -isnot [string]) { throw 'carrier_key_type' } }
            $keys = [string[]]@($Node.Keys)
        }
        elseif ($Node -is [pscustomobject]) { $keys = [string[]]@($Node.PSObject.Properties | ForEach-Object Name) }
        else { throw 'carrier_value_type' }
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        Append '{'; $separator = ''
        foreach ($key in $keys) {
            Append $separator; Quoted $key; Append ':'
            if ($Node -is [Collections.IDictionary]) { Emit $Node[$key] ($Depth+1) }
            else { Emit $Node.$key ($Depth+1) }
            $separator = ','
        }
        Append '}'
    }
    Emit $Value 0
    return $buffer.ToString()
}

function Get-FileTransferLocalCarrierHash {
    param([string]$Text)
    Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false, $true).GetBytes($Text))
}

function Get-FileTransferLocalPublicationRow {
    param($Index, [long]$Line, [string]$NativeRole = '')
    if ($Line -lt 1 -or $Line -gt $Index.Local.Lines.Length) { throw 'carrier_use_location' }
    $raw = $Index.Local.Lines[$Line-1]
    $resolved = Resolve-FileTransferPublicationForwardingEvidence $Index $Index.Local.Path $Line $raw $NativeRole
    if ($resolved.Status -cne 'matched_local_forwarding') { throw 'carrier_forwarding_unproven' }
    $use = $resolved.Forwarding.Use; $effect = $resolved.Forwarding.Effect
    $boundary = $Index.Local.Publications.Boundaries["$($use.CoordinatorId)|$($use.BoundaryId)"][0]
    $operation = @($boundary.operations | Where-Object operationKey -CEQ $use.OperationKey)[0]
    $tuple = @{}
    foreach ($key in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
        'runtimeGeneration','terminalGeneration','legGeneration','transportEpoch','bridgeRecoveryGeneration',
        'checkpointGeneration','checkpointRequestId','dataSessionIdentity','dataSessionGeneration')) {
        $tuple[$key] = $operation.routeRuntime.boundary.$key
    }
    $row = @{
        coordinatorId=$use.CoordinatorId; writer=$use.Writer; sessionId=$use.SessionId; transferId=$use.TransferId
        direction=[string]$boundary.direction; boundaryId=$use.BoundaryId; boundaryHash=[string]$boundary.boundaryHash
        boundaryWriterSequence=[long]$boundary.writerSequence; authoredStateGeneration=$use.AuthoredStateGeneration
        observedStateGeneration=$use.ObservedStateGeneration; operationKey=$use.OperationKey; operationKind=[string]$operation.operationKind
        authorityId=[string]$operation.authorityId; operationGeneration=[long]$operation.operationGeneration
        receiptIdentity=[string]$operation.receiptIdentity; receiptGeneration=[long]$operation.receiptGeneration
        receiptStatus=[string]$operation.receiptStatus; receiptFingerprint=$use.TokenFingerprint; routeState=$tuple
        useKind=$use.UseKind; useWriterSequence=$use.WriterSequence; useContentSha256=(Get-FileTransferLocalCarrierHash $raw)
        effectIdentity=$effect.EffectIdentity; effectWriterSequence=$effect.WriterSequence
    }
    return @{Row=$row; Raw=$raw; Location=@{useLine=$Line; publicationLine=([long]$boundary.evidenceLineIndex+1); effectLine=$effect.SourceLine}}
}
