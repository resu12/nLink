Set-StrictMode -Version Latest

function Get-FileTransferAuthorityLogField {
    param(
        [AllowNull()][string]$Line,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Line) -or [string]::IsNullOrWhiteSpace($Name)) {
        return ''
    }

    $pattern = '(?:^|;\s*)' + [regex]::Escape($Name) + '=([^;]*)'
    $match = [regex]::Match($Line, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return ''
    }

    return $match.Groups[1].Value.Trim()
}

function ConvertTo-FileTransferAuthorityNormalizedValue {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or
        [string]::Equals($Value.Trim(), 'none', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Value.Trim(), '(none)', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Value.Trim(), 'null', [System.StringComparison]::OrdinalIgnoreCase)) {
        return '(none)'
    }

    return $Value.Trim()
}

function Get-FileTransferAuthoritySchema {
    param([AllowNull()][string]$Line)

    $value = Get-FileTransferAuthorityLogField -Line $Line -Name 'authority_schema'
    $schema = 0
    if ([int]::TryParse($value, [ref]$schema)) {
        return $schema
    }

    return 1
}

function Test-FileTransferAuthorityFieldsEqual {
    param(
        [AllowNull()][string]$LeftLine,
        [AllowNull()][string]$RightLine,
        [Parameter(Mandatory = $true)][string[]]$Fields
    )

    foreach ($field in @($Fields)) {
        $leftRaw = Get-FileTransferAuthorityLogField -Line $LeftLine -Name $field
        $rightRaw = Get-FileTransferAuthorityLogField -Line $RightLine -Name $field
        if ([string]::IsNullOrWhiteSpace($leftRaw) -or [string]::IsNullOrWhiteSpace($rightRaw)) {
            return $false
        }

        $left = ConvertTo-FileTransferAuthorityNormalizedValue $leftRaw
        $right = ConvertTo-FileTransferAuthorityNormalizedValue $rightRaw
        if (-not [string]::Equals($left, $right, [System.StringComparison]::Ordinal)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferAuthorityTupleMatch {
    param(
        [AllowNull()][string]$LeftLine,
        [AllowNull()][string]$RightLine,
        [switch]$PeerCorrelation
    )

    if ([string]::IsNullOrWhiteSpace($LeftLine) -or [string]::IsNullOrWhiteSpace($RightLine)) {
        return $false
    }

    $leftSchema = Get-FileTransferAuthoritySchema -Line $LeftLine
    $rightSchema = Get-FileTransferAuthoritySchema -Line $RightLine
    $leftCurrent = $leftSchema -ge 2
    $rightCurrent = $rightSchema -ge 2
    if ($leftCurrent -ne $rightCurrent) {
        return $false
    }

    if ($leftCurrent) {
        if ($PeerCorrelation) {
            $correlationFields = @(
                'transfer_id',
                'session_id',
                'route',
                'protocol_version',
                'live_route_epoch',
                'leg_generation',
                'transport_epoch',
                'bridge_recovery_generation',
                'checkpoint_request_id',
                'repair_request_id',
                'repair_request_key',
                'range_fingerprint',
                'request_correlation_id')
            return Test-FileTransferAuthorityFieldsEqual `
                -LeftLine $LeftLine `
                -RightLine $RightLine `
                -Fields $correlationFields
        }

        $authorityFields = @(
            'authority_id',
            'predecessor_authority_id',
            'transfer_id',
            'session_id',
            'direction',
            'route',
            'protocol_version',
            'live_route_epoch',
            'runtime_generation',
            'terminal_generation',
            'leg_generation',
            'transport_epoch',
            'bridge_recovery_generation',
            'bridge_decision_generation',
            'checkpoint_request_id',
            'route_publication_generation',
            'route_commit_generation',
            'repair_generation',
            'repair_request_key',
            'range_fingerprint',
            'frontier_chunk_index',
            'durable_received_highest_chunk_index',
            'range_count',
            'requested_chunk_count',
            'source_kind',
            'primary_protocol_repair_request_id',
            'primary_request_correlation_id',
            'priority',
            'recovery_mode',
            'target_transport',
            'authority_decision_reason')
        return Test-FileTransferAuthorityFieldsEqual `
            -LeftLine $LeftLine `
            -RightLine $RightLine `
            -Fields $authorityFields
    }

    $legacyFields = @(
        'transfer_id',
        'session_id',
        'route',
        'protocol_version',
        'live_route_epoch')
    if (-not (Test-FileTransferAuthorityFieldsEqual `
            -LeftLine $LeftLine `
            -RightLine $RightLine `
            -Fields $legacyFields)) {
        return $false
    }

    $leftCorrelation = ConvertTo-FileTransferAuthorityNormalizedValue (
        Get-FileTransferAuthorityLogField -Line $LeftLine -Name 'request_correlation_id')
    $rightCorrelation = ConvertTo-FileTransferAuthorityNormalizedValue (
        Get-FileTransferAuthorityLogField -Line $RightLine -Name 'request_correlation_id')
    if ($leftCorrelation -ne '(none)' -and $rightCorrelation -ne '(none)') {
        if (-not [string]::Equals($leftCorrelation, $rightCorrelation, [System.StringComparison]::Ordinal)) {
            return $false
        }

        return Test-FileTransferAuthorityFieldsEqual `
            -LeftLine $LeftLine `
            -RightLine $RightLine `
            -Fields @('repair_request_id', 'first_start_chunk_index', 'first_chunk_count', 'range_count')
    }

    return Test-FileTransferAuthorityFieldsEqual `
        -LeftLine $LeftLine `
        -RightLine $RightLine `
        -Fields @('leg_generation', 'bridge_recovery_generation', 'transport_epoch', 'checkpoint_request_id')
}
