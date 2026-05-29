Set-StrictMode -Version Latest

$script:FileTransferRegularNknTargetGoodputBytesPerSecond = 1500000D
$script:FileTransferExpectedBridgeControlSubClients = 4
$script:FileTransferExpectedBridgeMediaSubClients = 8
$script:FileTransferExpectedBridgeBulkSubClients = 4
$script:FileTransferExpectedBridgeBulkSendConcurrency = 4
$script:FileTransferExpectedBridgeBulkSendMode = 'fanout'

function Write-FileTransferArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    New-Item -ItemType Directory -Path $ArtifactDir -Force | Out-Null
    $path = Join-Path $ArtifactDir $FileName
    $Lines | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Join-FileTransferValues {
    param([object[]]$Values)

    $items = @($Values | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ })
    if ($items.Count -eq 0) {
        return '(none)'
    }

    return ($items -join ',')
}

function Get-FileTransferArtifactEvidenceLines {
    param(
        [object[]]$Events,
        [int]$Limit = 20
    )

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($event in @($Events | Select-Object -First $Limit)) {
        $lines.Add((Format-FileTransferEvidenceLine -Event $event)) | Out-Null
    }

    if ($lines.Count -eq 0) {
        return @('(none)')
    }

    return $lines.ToArray()
}

function Get-FileTransferEventsForSummary {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string[]]$Names
    )

    return @($Summary.TransferEvents | Where-Object { $Names -contains $_.EventName })
}

function Get-FileTransferLatestEvent {
    param(
        [object[]]$Events,
        [string]$Name
    )

    $matches = @($Events | Where-Object { $_.EventName -eq $Name } | Sort-Object Sequence)
    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[-1]
}

function Test-FileTransferDiagnosticBridgeTopologyProfile {
    param([string]$ExternalTopologyProfile = '')

    return -not [string]::IsNullOrWhiteSpace($ExternalTopologyProfile) -and
        -not [string]::Equals($ExternalTopologyProfile, 'Default', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($ExternalTopologyProfile, 'DefaultKeepAlive', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-FileTransferBridgeConfigSnapshot {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string]$ExternalTopologyProfile = ''
    )

    $allEvents = @($Summary.AllEvents)
    $healthEvents = @($allEvents | Where-Object { $_.EventName -eq 'screenshare_bridge_transport_health_summary' })
    $bundleEvents = @($allEvents | Where-Object { $_.EventName -eq 'bridge_bundle_loaded' })
    $latestHealth = Get-FileTransferLatestEvent -Events $healthEvents -Name 'screenshare_bridge_transport_health_summary'
    $latestBundle = Get-FileTransferLatestEvent -Events $bundleEvents -Name 'bridge_bundle_loaded'
    $effectiveProfile = if ([string]::IsNullOrWhiteSpace($ExternalTopologyProfile)) {
        [System.Environment]::GetEnvironmentVariable('NLINK_FILETRANSFER_EXTERNAL_TOPOLOGY_PROFILE')
    }
    else {
        $ExternalTopologyProfile
    }
    if ([string]::IsNullOrWhiteSpace($effectiveProfile)) {
        $effectiveProfile = 'Default'
    }

    $controlSubClients = -1L
    $mediaSubClients = -1L
    $bulkSubClients = -1L
    $bulkSendConcurrency = -1L
    $bulkSendMode = '(unknown)'
    if ($null -ne $latestHealth) {
        $controlSubClients = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'control_subclients' -Default -1
        if ($controlSubClients -lt 0) {
            $controlSubClients = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'csc' -Default -1
        }

        $mediaSubClients = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'media_subclients' -Default -1
        if ($mediaSubClients -lt 0) {
            $mediaSubClients = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'msc' -Default -1
        }

        $bulkSubClients = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'bulk_subclients' -Default -1
        if ($bulkSubClients -lt 0) {
            $bulkSubClients = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'bsc' -Default -1
        }

        $bulkSendConcurrency = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'bulk_send_concurrency' -Default -1
        if ($bulkSendConcurrency -lt 0) {
            $bulkSendConcurrency = Get-FileTransferEventInt64Field -Event $latestHealth -Name 'bcc' -Default -1
        }

        $parsedMode = Get-FileTransferEventField -Event $latestHealth -Name 'bulk_send_mode' -Default ''
        if ([string]::IsNullOrWhiteSpace($parsedMode)) {
            $parsedMode = Get-FileTransferEventField -Event $latestHealth -Name 'bsm' -Default ''
        }

        if (-not [string]::IsNullOrWhiteSpace($parsedMode)) {
            $bulkSendMode = $parsedMode.Trim().ToLowerInvariant()
        }
    }

    $expectedTopology = ('{0}/{1}/{2}' -f $script:FileTransferExpectedBridgeControlSubClients, $script:FileTransferExpectedBridgeMediaSubClients, $script:FileTransferExpectedBridgeBulkSubClients)
    $observedTopology = if ($controlSubClients -ge 0 -or $mediaSubClients -ge 0 -or $bulkSubClients -ge 0) {
        ('{0}/{1}/{2}' -f $controlSubClients, $mediaSubClients, $bulkSubClients)
    }
    else {
        '(unknown)'
    }
    $expectedMode = $script:FileTransferExpectedBridgeBulkSendMode
    $modeMatches = [string]::Equals($bulkSendMode, '(unknown)', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($bulkSendMode, $expectedMode, [System.StringComparison]::OrdinalIgnoreCase)
    $settingsMatchExpected =
        $controlSubClients -eq $script:FileTransferExpectedBridgeControlSubClients -and
        $mediaSubClients -eq $script:FileTransferExpectedBridgeMediaSubClients -and
        $bulkSubClients -eq $script:FileTransferExpectedBridgeBulkSubClients -and
        $bulkSendConcurrency -eq $script:FileTransferExpectedBridgeBulkSendConcurrency -and
        $modeMatches
    $diagnosticProfile = Test-FileTransferDiagnosticBridgeTopologyProfile -ExternalTopologyProfile $effectiveProfile
    $status = if ($null -eq $latestHealth) {
        'unknown'
    }
    elseif ($diagnosticProfile) {
        'diagnostic_override'
    }
    elseif ($settingsMatchExpected) {
        'expected'
    }
    else {
        'unexpected_drift'
    }

    $overrideKeys = @(
        'NLINK_NKN_NUM_SUBCLIENTS',
        'NLINK_NKN_MEDIA_NUM_SUBCLIENTS',
        'NLINK_NKN_BULK_NUM_SUBCLIENTS',
        'NLINK_NKN_BULK_SEND_CONCURRENCY',
        'NLINK_NKN_BULK_SEND_MODE',
        'NLINK_FILETRANSFER_EXTERNAL_TOPOLOGY_PROFILE'
    )
    $overrideEvidence = @(
        foreach ($key in $overrideKeys) {
            $value = [System.Environment]::GetEnvironmentVariable($key)
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                ('{0}={1}' -f $key, $value)
            }
        }
    )
    $bridgeScriptPath = if ($null -ne $latestBundle) { Get-FileTransferEventField -Event $latestBundle -Name 'bridge_script_path' -Default '(unknown)' } else { '(unknown)' }
    $bridgeScriptHash = if ($null -ne $latestBundle) { Get-FileTransferEventField -Event $latestBundle -Name 'bridge_script_sha256' -Default '(unknown)' } else { '(unknown)' }
    $manifestStatus = if ($null -ne $latestBundle) { Get-FileTransferEventField -Event $latestBundle -Name 'manifest_status' -Default '(unknown)' } else { '(unknown)' }
    $manifestAppVersion = if ($null -ne $latestBundle) { Get-FileTransferEventField -Event $latestBundle -Name 'app_version' -Default '(unknown)' } else { '(unknown)' }
    $nodeVersion = if ($null -ne $latestBundle) { Get-FileTransferEventField -Event $latestBundle -Name 'node_version' -Default '(unknown)' } else { '(unknown)' }

    return [pscustomobject]@{
        Status = $status
        ExternalTopologyProfile = $effectiveProfile
        ExpectedTopology = $expectedTopology
        ObservedTopology = $observedTopology
        ExpectedBulkSendConcurrency = $script:FileTransferExpectedBridgeBulkSendConcurrency
        ObservedBulkSendConcurrency = $bulkSendConcurrency
        ExpectedBulkSendMode = $expectedMode
        ObservedBulkSendMode = $bulkSendMode
        SettingsMatchExpected = $settingsMatchExpected
        DiagnosticProfile = $diagnosticProfile
        HealthSummaryCount = $healthEvents.Count
        BundleLoadedCount = $bundleEvents.Count
        BridgeScriptPath = $bridgeScriptPath
        BridgeScriptSha256 = $bridgeScriptHash
        ManifestStatus = $manifestStatus
        ManifestAppVersion = $manifestAppVersion
        NodeVersion = $nodeVersion
        OverrideEvidence = @($overrideEvidence)
        EvidenceEvents = @($bundleEvents + $healthEvents | Sort-Object Sequence)
    }
}

function Get-FileTransferSumField {
    param(
        [object[]]$Events,
        [string]$FieldName
    )

    $sum = 0L
    foreach ($event in @($Events)) {
        $sum += Get-FileTransferEventInt64Field -Event $event -Name $FieldName -Default 0
    }

    return $sum
}

function Get-FileTransferEventFieldValueCount {
    param(
        [object[]]$Events,
        [string]$FieldName,
        [string]$Value
    )

    return @($Events | Where-Object { (Get-FileTransferEventField -Event $_ -Name $FieldName -Default '') -eq $Value }).Count
}

function Get-FileTransferV4MissingRangeDueStateMismatchCount {
    param(
        [object[]]$DueEvents,
        [object[]]$StateSentEvents
    )

    $count = 0
    foreach ($due in @($DueEvents)) {
        $epoch = Get-FileTransferEventInt64Field -Event $due -Name 'epoch' -Default -1
        $frontier = Get-FileTransferEventInt64Field -Event $due -Name 'start_chunk_index' -Default -1
        if ($epoch -lt 0) {
            continue
        }
        $dueSequence = if ($null -ne $due -and $null -ne $due.PSObject.Properties['Sequence']) { [long]$due.Sequence } else { -1L }

        $matchingEmptyState = @($StateSentEvents | Where-Object {
            (Get-FileTransferEventInt64Field -Event $_ -Name 'epoch' -Default -2) -eq $epoch -and
            ($dueSequence -lt 0 -or (
                $null -ne $_ -and
                $null -ne $_.PSObject.Properties['Sequence'] -and
                [long]$_.Sequence -gt $dueSequence)) -and
            ($frontier -lt 0 -or
                (Get-FileTransferEventInt64Field -Event $_ -Name 'contiguous_committed_chunk_index' -Default -2) -eq $frontier) -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'missing_range_count' -Default 0) -eq 0
        })
        if ($matchingEmptyState.Count -gt 0) {
            $count++
        }
    }

    return $count
}

function Get-FileTransferActiveSampleDurationMs {
    param(
        [object[]]$Events,
        [string]$ActivityFieldName
    )

    $sum = 0L
    foreach ($event in @($Events)) {
        if ((Get-FileTransferEventInt64Field -Event $event -Name $ActivityFieldName -Default 0) -le 0) {
            continue
        }

        $sampleWindowMs = Get-FileTransferEventInt64Field -Event $event -Name 'sample_window_ms' -Default 0
        if ($sampleWindowMs -gt 0) {
            $sum += $sampleWindowMs
        }
    }

    return $sum
}

function Get-FileTransferObservedDurationMs {
    param([object[]]$Events)

    $timestamped = @($Events | Where-Object { $null -ne $_.TimestampUtc } | Sort-Object TimestampUtc)
    if ($timestamped.Count -lt 2) {
        return 0L
    }

    return [long][Math]::Max(0, ($timestamped[-1].TimestampUtc - $timestamped[0].TimestampUtc).TotalMilliseconds)
}

function Get-FileTransferSparseCreditStats {
    param([object[]]$GrantEvents)

    $eventsWithSparseCreditEvidence = @($GrantEvents | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'sparse_credit_mode' -Default '')) -or
        -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'sparse_credit_eligible' -Default '')) -or
        -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default ''))
    })
    $eligible = @($eventsWithSparseCreditEvidence | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_eligible' -Default '0') -eq '1' -or
        (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'sparse_base'
    })
    $used = @($GrantEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'sparse_base' })
    $blocked = @($eventsWithSparseCreditEvidence | Where-Object {
        $reason = Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default ''
        -not [string]::IsNullOrWhiteSpace($reason) -and $reason -ne '(none)' -and
        (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -ne 'sparse_base'
    })
    $reorderEligible = @($eventsWithSparseCreditEvidence | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'late_arrival_distance' -Default 0) -gt 0 })
    $reorderUsed = @($reorderEligible | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'sparse_base' })
    $ratio = 0.0
    if ($reorderEligible.Count -gt 0) {
        $ratio = [Math]::Round(($reorderUsed.Count * 100.0) / $reorderEligible.Count, 3)
    }

    return [pscustomobject]@{
        EligibleCount = $eligible.Count
        UsedCount = $used.Count
        BlockedCount = $blocked.Count
        ReorderEligibleCount = $reorderEligible.Count
        ReorderUsedCount = $reorderUsed.Count
        ReorderUseRatioPercent = $ratio.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
        BlockedNoSparseAheadCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'no_sparse_ahead' }).Count
        BlockedGapStallCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'gap_stall' }).Count
        BlockedRepairPressureCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'repair_pressure' }).Count
        BlockedReceiverPressureCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'receiver_buffer_pressure' }).Count
        BlockedTimeoutCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'timeout' }).Count
        BlockedAccountingDisabledCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'accounting_disabled' }).Count
        BlockedModeCurrentCount = @($blocked | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'sparse_credit_block_reason' -Default '') -eq 'mode_current' }).Count
    }
}

function Get-FileTransferProactiveRepairPressureStats {
    param([object[]]$Events)

    $eventsWithState = @($Events | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default ''))
    })
    $hardLimitedEvents = @($Events | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'limited' -or
        (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_limited' -or
        (Get-FileTransferEventField -Event $_ -Name 'grant_policy_after_repair' -Default '') -eq 'healthy_limited'
    })
    $benignStates = @($eventsWithState | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '') -eq 'benign_grace'
    })
    $repeatedStates = @($eventsWithState | Where-Object {
        $state = Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default ''
        $state -eq 'repeated_unfilled' -or
        $state -eq 'hard_gap_stall' -or
        $state -eq 'grace_expired'
    })

    return [pscustomobject]@{
        BenignCount = $benignStates.Count
        GraceActiveCount = $benignStates.Count
        RepeatedUnfilledCount = $repeatedStates.Count
        HardLimitedCount = @($hardLimitedEvents | Where-Object {
            $state = Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default ''
            -not [string]::IsNullOrWhiteSpace($state) -and $state -ne '(none)' -and $state -ne 'benign_grace'
        }).Count
        HardLimitedDuringGraceCount = @($hardLimitedEvents | Where-Object {
            $state = Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default ''
            if ($state -eq 'benign_grace') {
                $true
            }
            else {
                $graceMs = Get-FileTransferEventInt64Field -Event $_ -Name 'proactive_repair_grace_ms' -Default 0
                $sameFrontierUnfilledMs = Get-FileTransferEventInt64Field -Event $_ -Name 'same_frontier_unfilled_ms' -Default 0
                $graceMs -gt 0 -and $sameFrontierUnfilledMs -gt 0 -and $sameFrontierUnfilledMs -lt $graceMs
            }
        }).Count
        MaxAgeMs = Get-FileTransferMaxField -Events $eventsWithState -FieldName 'proactive_repair_age_ms'
        MaxSameFrontierUnfilledMs = Get-FileTransferMaxField -Events $eventsWithState -FieldName 'same_frontier_unfilled_ms'
    }
}

function Get-FileTransferCycleGoodputStats {
    param([string]$ArtifactDir = '')

    $empty = [pscustomobject]@{
        Count = 0
        Min = '0.000'
        Average = '0.000'
        Max = '0.000'
        HelperToHelpeeAverage = '0.000'
        HelpeeToHelperAverage = '0.000'
    }
    if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
        return $empty
    }

    function Format-CycleAverage {
        param([object[]]$Items)
        if ($Items.Count -eq 0) {
            return '0.000'
        }

        return (($Items | ForEach-Object { [double]$_.goodput_bytes_per_second } | Measure-Object -Average).Average).ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
    }

    function Convert-GoodputText {
        param([object]$Value)
        $parsed = 0.0
        if ($null -ne $Value -and [double]::TryParse(([string]$Value), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $parsed
        }

        return 0.0
    }

    $path = Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl'
    if (Test-Path -LiteralPath $path) {
        $cycles = @(
            foreach ($line in @(Get-Content -LiteralPath $path -ErrorAction SilentlyContinue)) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                try {
                    $line | ConvertFrom-Json -ErrorAction Stop
                }
                catch {
                    continue
                }
            }
        )

        $completed = @($cycles | Where-Object { $_.completed -eq $true -and $null -ne $_.goodput_bytes_per_second })
        if ($completed.Count -gt 0) {
            $values = @($completed | ForEach-Object { [double]$_.goodput_bytes_per_second })
            return [pscustomobject]@{
                Count = $completed.Count
                Min = (($values | Measure-Object -Minimum).Minimum).ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                Average = (($values | Measure-Object -Average).Average).ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                Max = (($values | Measure-Object -Maximum).Maximum).ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                HelperToHelpeeAverage = Format-CycleAverage -Items @($completed | Where-Object { $_.direction -eq 'helper-to-helpee' })
                HelpeeToHelperAverage = Format-CycleAverage -Items @($completed | Where-Object { $_.direction -eq 'helpee-to-helper' })
            }
        }
    }

    $summaryJsonPath = Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json'
    if (Test-Path -LiteralPath $summaryJsonPath) {
        try {
            $summary = Get-Content -LiteralPath $summaryJsonPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $count = [int](Convert-GoodputText -Value $summary.cycles_completed)
            $average = Convert-GoodputText -Value $summary.average_goodput_bytes_per_second
            $minimum = Convert-GoodputText -Value $summary.min_goodput_bytes_per_second
            if ($count -gt 0 -and $average -gt 0) {
                return [pscustomobject]@{
                    Count = $count
                    Min = $minimum.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                    Average = $average.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                    Max = $average.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                    HelperToHelpeeAverage = '0.000'
                    HelpeeToHelperAverage = '0.000'
                }
            }
        }
        catch {
        }
    }

    $summaryTxtPath = Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt'
    if (Test-Path -LiteralPath $summaryTxtPath) {
        $values = @{}
        foreach ($line in @(Get-Content -LiteralPath $summaryTxtPath -ErrorAction SilentlyContinue)) {
            if ($line -notmatch '^\s*([^=]+)=(.*)$') {
                continue
            }

            $values[$Matches[1].Trim()] = $Matches[2].Trim()
        }

        $count = [int](Convert-GoodputText -Value ($(if ($values.ContainsKey('cycles_completed')) { $values['cycles_completed'] } else { 0 })))
        $average = Convert-GoodputText -Value ($(if ($values.ContainsKey('average_goodput_bytes_per_second')) { $values['average_goodput_bytes_per_second'] } else { 0 }))
        $minimum = Convert-GoodputText -Value ($(if ($values.ContainsKey('min_goodput_bytes_per_second')) { $values['min_goodput_bytes_per_second'] } else { 0 }))
        if ($count -gt 0 -and $average -gt 0) {
            return [pscustomobject]@{
                Count = $count
                Min = $minimum.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                Average = $average.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                Max = $average.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
                HelperToHelpeeAverage = '0.000'
                HelpeeToHelperAverage = '0.000'
            }
        }
    }

    return $empty
}

function Read-FileTransferPromotionKeyValueArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $values
    }

    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $values[$key] = $value
        }
    }

    return $values
}

function ConvertTo-FileTransferPromotionDouble {
    param(
        $Values,
        [Parameter(Mandatory = $true)][string]$Name,
        [double]$Default = 0
    )

    if ($null -eq $Values -or -not $Values.ContainsKey($Name)) {
        return $Default
    }

    $text = [string]$Values[$Name]
    if ([string]::IsNullOrWhiteSpace($text) -or $text -eq '(none)') {
        return $Default
    }

    $result = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$result)) {
        return $result
    }

    return $Default
}

function Get-FileTransferPromotionValue {
    param(
        [object[]]$Sources,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = '(none)'
    )

    foreach ($source in @($Sources)) {
        if ($null -eq $source -or -not $source.ContainsKey($Name)) {
            continue
        }

        $value = [string]$source[$Name]
        if (-not [string]::IsNullOrWhiteSpace($value) -and $value -ne '(none)') {
            return $value
        }
    }

    return $Default
}

function Get-FileTransferPromotionLiveMatrixStats {
    param([string]$ArtifactDir = '')

    $result = [ordered]@{
        CycleCount = 0
        CompletedCount = 0
        SixteenMiBCompletedCount = 0
        SixtyFourMiBCompletedCount = 0
        MatrixComplete = 0
    }

    if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
        return [pscustomobject]$result
    }

    $path = Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]$result
    }

    foreach ($line in @(Get-Content -LiteralPath $path -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $cycle = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }

        $result.CycleCount++
        if ($cycle.completed -ne $true -or $cycle.integrity_ok -ne $true) {
            continue
        }

        $result.CompletedCount++
        $payloadBytes = 0L
        [long]::TryParse([string]$cycle.payload_bytes, [ref]$payloadBytes) | Out-Null
        if ($payloadBytes -eq 16777216L) {
            $result.SixteenMiBCompletedCount++
        }
        elseif ($payloadBytes -eq 67108864L) {
            $result.SixtyFourMiBCompletedCount++
        }
    }

    if ($result.SixteenMiBCompletedCount -ge 2 -and $result.SixtyFourMiBCompletedCount -ge 2) {
        $result.MatrixComplete = 1
    }

    return [pscustomobject]$result
}

function Get-FileTransferEventDoubleField {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)][string]$Name,
        [double]$Default = 0
    )

    $text = Get-FileTransferEventField -Event $Event -Name $Name -Default ''
    $value = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        return $value
    }

    return $Default
}

function Get-FileTransferAverageDoubleField {
    param(
        [object[]]$Events,
        [string]$FieldName
    )

    $values = @(
        foreach ($event in @($Events)) {
            $text = Get-FileTransferEventField -Event $event -Name $FieldName -Default ''
            $value = 0.0
            if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
                $value
            }
        }
    )
    if ($values.Count -eq 0) {
        return '0.00'
    }

    return (($values | Measure-Object -Average).Average).ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-FileTransferMaxDoubleField {
    param(
        [object[]]$Events,
        [string]$FieldName
    )

    $max = 0.0
    foreach ($event in @($Events)) {
        $value = Get-FileTransferEventDoubleField -Event $event -Name $FieldName -Default 0
        if ($value -gt $max) {
            $max = $value
        }
    }

    return $max.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-FileTransferPercentileDoubleField {
    param(
        [object[]]$Events,
        [string]$FieldName,
        [double]$Percentile
    )

    $values = @(
        foreach ($event in @($Events)) {
            $text = Get-FileTransferEventField -Event $event -Name $FieldName -Default ''
            $value = 0.0
            if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
                $value
            }
        }
    )
    $values = @($values | Sort-Object)

    if ($values.Count -eq 0) {
        return '0.00'
    }

    $index = [int]([Math]::Ceiling(($Percentile / 100D) * $values.Count) - 1)
    if ($index -lt 0) {
        $index = 0
    }
    elseif ($index -ge $values.Count) {
        $index = $values.Count - 1
    }

    return ([double]$values[$index]).ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-FileTransferMaxDeliveryToCommitRatio {
    param([object[]]$ReceiverEvents)

    $maxRatio = 0D
    foreach ($event in @($ReceiverEvents)) {
        $rawBps = Get-FileTransferEventInt64Field -Event $event -Name 'raw_bytes_received_per_second' -Default 0
        $commitBps = Get-FileTransferEventInt64Field -Event $event -Name 'contiguous_bytes_committed_per_second' -Default 0
        if ($rawBps -le 0) {
            continue
        }

        $ratio = if ($commitBps -le 0) { [double]$rawBps } else { $rawBps / [double]$commitBps }
        if ($ratio -gt $maxRatio) {
            $maxRatio = $ratio
        }
    }

    return $maxRatio.ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-FileTransferSkippedRepairChunkReasonCount {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    return @(
        Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_sender_repair_chunk_skipped') |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq $Reason }
    ).Count
}

function Get-FileTransferPayloadEfficiencyProfile {
    param(
        [object[]]$ProfileEvents,
        [object[]]$BudgetEvents,
        [object[]]$BatchEvents
    )

    $profileCounts = @{}
    $sawV4RepairBatchProfile = $false
    foreach ($event in @($BatchEvents + $BudgetEvents)) {
        $profile = Get-FileTransferEventField -Event $event -Name 'batch_profile' -Default ''
        if (Test-FileTransferV4RepairBatchProfile -Profile $profile) {
            $sawV4RepairBatchProfile = $true
        }

        if (Test-FileTransferPayloadEfficiencyBatchProfile -Profile $profile) {
            if (-not $profileCounts.ContainsKey($profile)) {
                $profileCounts[$profile] = 0
            }

            $profileCounts[$profile] = [int]$profileCounts[$profile] + 1
        }
    }

    $dominantBatchProfile = ''
    if ($profileCounts.Count -gt 0) {
        $profiles = @(
            foreach ($key in $profileCounts.Keys) {
                [pscustomobject]@{
                    Profile = [string]$key
                    Count = [int]$profileCounts[$key]
                    IsDefault = (($key -eq 'Auto') -or ($key -eq 'Current'))
                }
            }
        )

        $dominantConcreteProfile = @($profiles | Where-Object { -not $_.IsDefault } | Sort-Object Count, Profile -Descending | Select-Object -First 1)
        if ($dominantConcreteProfile.Count -gt 0) {
            $dominantBatchProfile = [string]$dominantConcreteProfile[0].Profile
        } else {
            $dominantAnyProfile = @($profiles | Sort-Object Count, Profile -Descending | Select-Object -First 1)
            if ($dominantAnyProfile.Count -gt 0) {
                $dominantBatchProfile = [string]$dominantAnyProfile[0].Profile
            }
        }
    }

    foreach ($event in @($ProfileEvents | Sort-Object Sequence -Descending)) {
        $profile = Get-FileTransferEventField -Event $event -Name 'profile' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($profile)) {
            if (($profile -eq 'Auto' -or $profile -eq 'Current') -and
                -not [string]::IsNullOrWhiteSpace($dominantBatchProfile) -and
                $dominantBatchProfile -ne $profile) {
                return $dominantBatchProfile
            }

            return $profile
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($dominantBatchProfile)) {
        return $dominantBatchProfile
    }

    if ($sawV4RepairBatchProfile) {
        return 'v4_default_21k'
    }

    return '(unknown)'
}

function Test-FileTransferPayloadEfficiencyBatchProfile {
    param([string]$Profile)

    if ([string]::IsNullOrWhiteSpace($Profile)) {
        return $false
    }

    return -not $Profile.StartsWith('v4_repair_', [StringComparison]::OrdinalIgnoreCase)
}

function Test-FileTransferV4RepairBatchProfile {
    param([string]$Profile)

    if ([string]::IsNullOrWhiteSpace($Profile)) {
        return $false
    }

    return $Profile.StartsWith('v4_repair_', [StringComparison]::OrdinalIgnoreCase)
}

function Get-FileTransferBulkFramesPerMiB {
    param([Parameter(Mandatory = $true)]$Summary)

    $bulkFrameEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch'))
    $binaryBulkFrames = @(
        Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_sent') |
            Where-Object {
                $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
                $frameType -eq 'filetransfer.chunk_batch.v6' -or
                    $frameType -eq 'filetransfer.chunk_batch.v4'
            }
    )

    $frameCount = [Math]::Max($bulkFrameEvents.Count, $binaryBulkFrames.Count)
    $terminalBytes = Get-FileTransferMaxField -Events $Summary.TerminalEvents -FieldName 'bytes_transferred'
    if ($terminalBytes -le 0) {
        $terminalBytes = Get-FileTransferSumField -Events $binaryBulkFrames -FieldName 'raw_chunk_bytes'
    }

    if ($terminalBytes -le 0 -or $frameCount -le 0) {
        return '0.00'
    }

    $mib = $terminalBytes / 1048576D
    return ($frameCount / $mib).ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)
}

function New-FileTransferTerminalSummaryLines {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$GateResult
    )

    $states = @($Summary.TerminalEvents | ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'state' -Default '(unknown)' })
    $errors = @($Summary.TerminalEvents | ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)' })
    return @(
        ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
        ("verdict={0}" -f $GateResult.Verdict),
        ("inbound_terminal_count={0}" -f $Summary.InboundTerminalEvents.Count),
        ("outbound_terminal_count={0}" -f $Summary.OutboundTerminalEvents.Count),
        ("terminal_states={0}" -f (Join-FileTransferValues -Values $states)),
        ("terminal_error_codes={0}" -f (Join-FileTransferValues -Values $errors)),
        ("observed_start_utc={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.FirstTimestamp)) { '(unknown)' } else { $Summary.FirstTimestamp }))),
        ("observed_end_utc={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.LastTimestamp)) { '(unknown)' } else { $Summary.LastTimestamp }))),
        ("analyzed_file_count={0}" -f $Summary.LogFiles.Count),
        '',
        'terminal_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $Summary.TerminalEvents -Limit 20)
}

function New-FileTransferThroughputSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $binarySent = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_sent'))
    $binaryReceived = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_received'))
    $sentRawBytes = 0L
    foreach ($event in @($binarySent)) {
        $sentRawBytes += Get-FileTransferEventInt64Field -Event $event -Name 'raw_chunk_bytes' -Default 0
    }

    $receivedRawBytes = 0L
    foreach ($event in @($binaryReceived)) {
        $receivedRawBytes += Get-FileTransferEventInt64Field -Event $event -Name 'raw_chunk_bytes' -Default 0
    }

    $throughput = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_throughput_summary'))
    $maxUsefulPayloadBps = Get-FileTransferMaxField -Events $throughput -FieldName 'useful_payload_bytes_per_second'
    $senderThroughput = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_throughput_summary'))
    $senderPipeline = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_pipeline_summary'))
    $senderFeed = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_feed_summary'))
    $senderCacheEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_sender_repair_cache_policy', 'filetransfer_sender_repair_cache_summary', 'filetransfer_sender_repair_cache_pressure_entered', 'filetransfer_sender_repair_cache_pressure_exited', 'filetransfer_sender_cache_exhausted', 'filetransfer_sender_repair_unavailable'))
    $receiverFeedbackEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_feedback_pump_started', 'filetransfer_v4_receiver_feedback_enqueued', 'filetransfer_v4_receiver_feedback_coalesced', 'filetransfer_v4_receiver_feedback_sent', 'filetransfer_v4_receiver_feedback_summary', 'filetransfer_v4_receiver_feedback_failed'))
    $receiverThroughput = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_throughput_summary'))
    $gapStalls = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_gap_stall_summary'))
    $sparseEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_receiver_sparse_mode_selected', 'filetransfer_receiver_sparse_write_summary', 'filetransfer_receiver_sparse_commit_summary'))
    $bridgeBulkSummaries = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_bulk_send_summary' })
    $payloadEfficiencyProfileEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_payload_efficiency_profile_selected'))
    $payloadBudgetEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_transport_payload_budget'))
    $payloadBatchEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch'))
    $payloadTransportSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_transport_summary'))
    $payloadBinaryEvents = @(
        Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_sent') |
            Where-Object {
                $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
                $frameType -eq 'filetransfer.chunk_batch.v6' -or
                    $frameType -eq 'filetransfer.chunk_batch.v4'
            }
    )
    $payloadShapeEvents = @($payloadBatchEvents + $payloadBudgetEvents + $payloadBinaryEvents + $payloadTransportSummaryEvents)
    $profileChanged = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_profile_changed'))
    $reorderPolicy = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_reorder_policy_decision'))
    $grantSummaries = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_grant_window_summary'))
    $sparseCreditStats = Get-FileTransferSparseCreditStats -GrantEvents $grantSummaries
    $frontierGapRepairEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_frontier_gap_repair_eligible', 'filetransfer_frontier_gap_repair_requested', 'filetransfer_frontier_gap_repair_skipped', 'filetransfer_frontier_gap_repair_suppressed', 'filetransfer_frontier_gap_repair_sender_received', 'filetransfer_frontier_gap_repair_sender_scheduled', 'filetransfer_frontier_gap_repair_sender_sent', 'filetransfer_frontier_gap_repair_filled', 'filetransfer_proactive_frontier_repair_state_reset'))
    $proactiveRepairPressureStats = Get-FileTransferProactiveRepairPressureStats -Events @($reorderPolicy + $grantSummaries + $frontierGapRepairEvents)
    $frontierGapRepairSkippedEvents = @($frontierGapRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_frontier_gap_repair_skipped' })
    $frontierGapRepairResetEvents = @($frontierGapRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_proactive_frontier_repair_state_reset' })
    $benignGapSkipLimitedPolicyCount = @($frontierGapRepairSkippedEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' -and
        (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '(none)') -eq '(none)' -and
        (Get-FileTransferEventField -Event $_ -Name 'grant_policy_after_repair' -Default '') -eq 'healthy_limited'
    }).Count
    $startupProbeCount = @($profileChanged | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'startup_probe' }).Count
    $startupFastCleanCount = @($profileChanged | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'startup_fast_clean' }).Count
    $startupAdverseCount = @($profileChanged | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'startup_adverse' }).Count
    $maxOldestGapAgeMs = [Math]::Max(
        (Get-FileTransferMaxField -Events $receiverThroughput -FieldName 'oldest_gap_age_ms'),
        (Get-FileTransferMaxField -Events $gapStalls -FieldName 'stall_duration_ms'))
    $senderFeedActiveDurationMs = Get-FileTransferActiveSampleDurationMs -Events $senderFeed -ActivityFieldName 'raw_bytes_prepared'
    $senderFeedCreditWaitRatioPercent = if ($senderFeedActiveDurationMs -gt 0) {
        [Math]::Round(($Summary.SenderFeedCreditWaitDurationMs * 100.0) / $senderFeedActiveDurationMs, 3)
    }
    else {
        0.0
    }
    $observedWindowMs = Get-FileTransferObservedDurationMs -Events $Summary.TransferEvents
    $grantSendRatePerSecond = if ($observedWindowMs -gt 0) {
        [Math]::Round(($grantSummaries.Count * 1000.0) / $observedWindowMs, 3)
    }
    else {
        0.0
    }
    $grantDeliveryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_data_frame_dispatched') |
        Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -eq 'filetransfer.receiver_state.v6' -or
                $frameType -eq 'filetransfer.state.v4'
        })
    $v6ControlHealthEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @(
        'filetransfer_v6_receiver_state_sent',
        'filetransfer_v6_receiver_state_received',
        'filetransfer_v6_receiver_state_deferred',
        'filetransfer_v6_receiver_state_coalesced',
        'filetransfer_v6_receiver_request_window_sent',
        'filetransfer_v6_frontier_request_sent',
        'filetransfer_v6_frontier_request_failed',
        'filetransfer_v6_frontier_request_deferred',
        'filetransfer_v6_frontier_request_received',
        'filetransfer_v6_frontier_request_coalesced',
        'filetransfer_v6_frontier_request_duplicate_ignored',
        'filetransfer_v6_frontier_request_preempted_normal_pipeline',
        'filetransfer_v6_post_tuna_fallback_survival_policy_enabled',
        'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested',
        'filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued',
        'filetransfer_v6_post_tuna_fallback_send_timeout_requeued',
        'filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline',
        'filetransfer_v6_normal_refill_deferred',
        'filetransfer_v6_normal_send_ahead_limited',
        'filetransfer_v6_regular_nkn_frontier_pressure_entered',
        'filetransfer_v6_regular_nkn_frontier_pressure_cleared',
        'filetransfer_v6_sender_waiting_for_requests',
        'filetransfer_v6_unsolicited_chunk_ignored',
        'filetransfer_v6_chunk_batch_sent',
        'filetransfer_v6_chunk_batch_send_deferred_for_recovery',
        'filetransfer_v6_chunk_batch_send_timeout',
        'filetransfer_v6_chunk_batch_send_canceled_for_pipeline',
        'filetransfer_v6_chunk_batch_send_late_completed',
        'filetransfer_v6_chunk_batch_send_late_canceled',
        'filetransfer_v6_chunk_batch_send_late_failed'
    ))
    $v6ChunkBatchSentEvents = @($v6ControlHealthEvents | Where-Object { $_.EventName -eq 'filetransfer_v6_chunk_batch_sent' })
    $ackDeliveryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_data_frame_dispatched') |
        Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -eq 'filetransfer.receiver_state.v6' -or
                $frameType -eq 'filetransfer.state.v4'
        })
    $v4BatchEvents = @($payloadBatchEvents | Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -eq 'filetransfer.chunk_batch.v6' -or
                $frameType -eq 'filetransfer.chunk_batch.v4' -or
                (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -eq 'v4_default_21k'
        })
    $v4BudgetEvents = @($payloadBudgetEvents | Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -eq 'filetransfer.chunk_batch.v6' -or
                $frameType -eq 'filetransfer.chunk_batch.v4' -or
                (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -eq 'v4_default_21k'
        })
    $v4SplitEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_split_for_transport') |
        Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'original_frame_type' -Default ''
            $frameType -eq 'filetransfer.chunk_batch.v6' -or
                $frameType -eq 'filetransfer.chunk_batch.v4'
        })
    $v4RepairSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_sent'))
    $v4RepairBatchEvents = @($payloadBatchEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -like 'v4_repair_*' })
    $v4TransportSummaryBatchCount = Get-FileTransferMaxField -Events $payloadTransportSummaryEvents -FieldName 'batch_frames_sent_total'
    $v4BatchNumerator = if ($v4BatchEvents.Count -gt 0) { $v4BatchEvents.Count } else { $v4TransportSummaryBatchCount }
    $v4BatchDenominator = $v4BatchNumerator + $v4SplitEvents.Count
    $v4BatchRatio = if ($v4BatchDenominator -gt 0) { ($v4BatchNumerator / [double]$v4BatchDenominator).ToString('F6', [System.Globalization.CultureInfo]::InvariantCulture) } else { '0.000000' }
    $v4AverageBatchChunkCount = Get-FileTransferAverageDoubleField -Events @($v4BatchEvents + $v4BudgetEvents) -FieldName 'batch_chunk_count'
    if ($v4AverageBatchChunkCount -eq '0.00') {
        $v4AverageBatchChunkCount = Get-FileTransferAverageDoubleField -Events $payloadTransportSummaryEvents -FieldName 'average_batch_chunk_count'
    }
    $v4MaxBatchChunkCount = Get-FileTransferMaxField -Events @($v4BatchEvents + $v4BudgetEvents) -FieldName 'batch_chunk_count'
    if ($v4MaxBatchChunkCount -eq 0) {
        $v4MaxBatchChunkCount = Get-FileTransferMaxField -Events $payloadTransportSummaryEvents -FieldName 'max_batch_chunk_count'
    }
    $v4MixedEnabledEvidenceCount =
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_mixed_screenshare_enabled') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_mixed_enabled') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_mixed_enabled') +
        (Get-FileTransferEventFieldValueCount -Events $Summary.TransferEvents -FieldName 'mixed_screenshare' -Value '1')
    $dataProtocolVersion = if (
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_negotiated') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_sender_started') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_receiver_started') -gt 0 -or
        $Summary.FrameTypeCounts.ContainsKey('filetransfer.chunk_batch.v6') -or
        $Summary.FrameTypeCounts.ContainsKey('filetransfer.receiver_state.v6')) {
        '6'
    }
    elseif (
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_negotiated') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_sender_started') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_receiver_started') -gt 0 -or
        $v4BatchEvents.Count -gt 0) {
        '4'
    }
    elseif ($Summary.FrameTypeCounts.ContainsKey('filetransfer.chunk_batch.v4') -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_throughput_summary') -gt 0) {
        '3'
    }
    else {
        '(unknown)'
    }

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("data_protocol_version={0}" -f $dataProtocolVersion),
        ("binary_frames_sent={0}" -f $binarySent.Count),
        ("binary_frames_received={0}" -f $binaryReceived.Count),
        ("raw_bytes_sent_from_binary_frames={0}" -f $sentRawBytes),
        ("raw_bytes_received_from_binary_frames={0}" -f $receivedRawBytes),
        ("throughput_summary_count={0}" -f $throughput.Count),
        ("max_useful_payload_bytes_per_second={0}" -f $maxUsefulPayloadBps),
        ("sender_throughput_summary_count={0}" -f $senderThroughput.Count),
        ("sender_pipeline_summary_count={0}" -f $senderPipeline.Count),
        ("sender_feed_summary_count={0}" -f $senderFeed.Count),
        ("receiver_throughput_summary_count={0}" -f $receiverThroughput.Count),
        ("gap_stall_summary_count={0}" -f $gapStalls.Count),
        ("max_sender_raw_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $senderThroughput -FieldName 'raw_bytes_per_second')),
        ("max_receiver_raw_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $receiverThroughput -FieldName 'raw_bytes_received_per_second')),
        ("max_contiguous_commit_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $receiverThroughput -FieldName 'contiguous_bytes_committed_per_second')),
        ("receiver_delivery_to_commit_ratio_max={0}" -f (Get-FileTransferMaxDeliveryToCommitRatio -ReceiverEvents $receiverThroughput)),
        ("max_oldest_gap_age_ms={0}" -f $maxOldestGapAgeMs),
        ("max_gap_stall_duration_ms={0}" -f (Get-FileTransferMaxField -Events $gapStalls -FieldName 'stall_duration_ms')),
        ("max_sparse_write_bytes_per_second={0}" -f $Summary.MaxReceiverSparseWriteBytesPerSecond),
        ("max_sparse_written_ahead_bytes={0}" -f $Summary.MaxReceiverSparseWrittenAheadBytes),
        ("max_sparse_gap_count={0}" -f $Summary.MaxReceiverSparseGapCount),
        ("payload_efficiency_profile={0}" -f (Get-FileTransferPayloadEfficiencyProfile -ProfileEvents $payloadEfficiencyProfileEvents -BudgetEvents $payloadBudgetEvents -BatchEvents $payloadBatchEvents)),
        ("v4_batch_ratio={0}" -f $v4BatchRatio),
        ("v4_state_feedback_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_state_sent')),
        ("v4_mixed_enabled_count={0}" -f $v4MixedEnabledEvidenceCount),
        ("v4_feedback_redundant_success_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_feedback_first_success')),
        ("v4_feedback_both_failed_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_feedback_both_failed')),
        ("v4_repair_delivery_bulk_only_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_mode' -Value 'bulk_only')),
        ("v4_repair_delivery_control_bulk_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_mode' -Value 'control_bulk_escalated')),
        ("v4_repair_delivery_retry_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'retry')),
        ("v4_repair_delivery_credit_stall_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'credit_stall')),
        ("v4_repair_delivery_frontier_not_advanced_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'frontier_not_advanced')),
        ("v4_repair_delivery_primary_regular_nkn_frontier_first_send_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'primary_regular_nkn_frontier_first_send')),
        ("v6_receiver_state_sent_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_receiver_state_sent')),
        ("v6_receiver_state_received_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_receiver_state_received')),
        ("v6_receiver_request_window_sent_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_receiver_request_window_sent')),
        ("v6_receiver_state_deferred_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_receiver_state_deferred')),
        ("v6_receiver_state_coalesced_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_receiver_state_coalesced')),
        ("v6_frontier_request_sent_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_sent')),
        ("v6_frontier_request_failed_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_failed')),
        ("v6_frontier_request_deferred_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_deferred')),
        ("v6_frontier_request_received_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_received')),
        ("v6_frontier_request_coalesced_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_coalesced')),
        ("v6_frontier_request_duplicate_ignored_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_duplicate_ignored')),
        ("v6_frontier_request_preempted_normal_pipeline_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_frontier_request_preempted_normal_pipeline')),
        ("v6_post_tuna_fallback_survival_policy_enabled_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_post_tuna_fallback_survival_policy_enabled')),
        ("v6_post_tuna_fallback_frontier_rescue_requested_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested')),
        ("v6_post_tuna_fallback_sender_frontier_rescue_queued_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued')),
        ("v6_post_tuna_fallback_send_timeout_requeued_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_post_tuna_fallback_send_timeout_requeued')),
        ("v6_receiver_state_frontier_preempted_normal_pipeline_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline')),
        ("v6_normal_refill_deferred_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_normal_refill_deferred')),
        ("v6_normal_send_ahead_limited_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_normal_send_ahead_limited')),
        ("v6_regular_nkn_frontier_pressure_entered_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_regular_nkn_frontier_pressure_entered')),
        ("v6_regular_nkn_frontier_pressure_cleared_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_regular_nkn_frontier_pressure_cleared')),
        ("v6_sender_waiting_for_requests_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_sender_waiting_for_requests')),
        ("v6_unsolicited_chunk_ignored_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_unsolicited_chunk_ignored')),
        ("v6_chunk_batch_sent_count={0}" -f $v6ChunkBatchSentEvents.Count),
        ("v6_normal_chunk_batch_sent_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v6ChunkBatchSentEvents -FieldName 'priority' -Value '0')),
        ("v6_priority_chunk_batch_sent_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v6ChunkBatchSentEvents -FieldName 'priority' -Value '1')),
        ("v6_regular_nkn_redundant_chunk_batch_sent_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v6ChunkBatchSentEvents -FieldName 'regular_nkn_redundant' -Value '1')),
        ("v6_chunk_batch_send_deferred_for_recovery_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_chunk_batch_send_deferred_for_recovery')),
        ("v6_chunk_batch_send_timeout_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_chunk_batch_send_timeout')),
        ("v6_chunk_batch_send_canceled_for_pipeline_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_chunk_batch_send_canceled_for_pipeline')),
        ("v6_chunk_batch_send_late_completed_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_chunk_batch_send_late_completed')),
        ("v6_chunk_batch_send_late_canceled_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_chunk_batch_send_late_canceled')),
        ("v6_chunk_batch_send_late_failed_count={0}" -f (Get-FileTransferEventCount -Events $v6ControlHealthEvents -Name 'filetransfer_v6_chunk_batch_send_late_failed')),
        ("v4_repair_batch_bulk_only_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairBatchEvents -FieldName 'repair_delivery_mode' -Value 'bulk_only')),
        ("v4_repair_batch_control_bulk_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairBatchEvents -FieldName 'repair_delivery_mode' -Value 'control_bulk_escalated')),
        ("v4_average_batch_chunk_count={0}" -f $v4AverageBatchChunkCount),
        ("v4_max_batch_chunk_count={0}" -f $v4MaxBatchChunkCount),
        ("v4_average_bridge_payload_fill_percent={0}" -f (Get-FileTransferAverageDoubleField -Events @($v4BatchEvents + $v4BudgetEvents + $payloadTransportSummaryEvents) -FieldName 'bridge_payload_fill_percent')),
        ("v4_p95_bridge_payload_fill_percent={0}" -f (Get-FileTransferPercentileDoubleField -Events @($v4BatchEvents + $v4BudgetEvents + $payloadTransportSummaryEvents) -FieldName 'bridge_payload_fill_percent' -Percentile 95)),
        ("v4_raw_to_bridge_payload_ratio_max={0}" -f (Get-FileTransferMaxDoubleField -Events @($v4BatchEvents + $v4BudgetEvents + $payloadTransportSummaryEvents) -FieldName 'raw_to_bridge_payload_ratio')),
        ("average_batch_chunk_count={0}" -f (Get-FileTransferAverageDoubleField -Events $payloadShapeEvents -FieldName 'batch_chunk_count')),
        ("max_batch_chunk_count={0}" -f (Get-FileTransferMaxField -Events $payloadShapeEvents -FieldName 'batch_chunk_count')),
        ("average_bridge_payload_fill_percent={0}" -f (Get-FileTransferAverageDoubleField -Events @($payloadBatchEvents + $payloadBudgetEvents + $payloadTransportSummaryEvents) -FieldName 'bridge_payload_fill_percent')),
        ("p95_bridge_payload_fill_percent={0}" -f (Get-FileTransferPercentileDoubleField -Events @($payloadBatchEvents + $payloadBudgetEvents + $payloadTransportSummaryEvents) -FieldName 'bridge_payload_fill_percent' -Percentile 95)),
        ("raw_to_bridge_payload_ratio_max={0}" -f (Get-FileTransferMaxDoubleField -Events @($payloadBatchEvents + $payloadBudgetEvents + $payloadTransportSummaryEvents) -FieldName 'raw_to_bridge_payload_ratio')),
        ("bulk_frames_per_mib={0}" -f (Get-FileTransferBulkFramesPerMiB -Summary $Summary)),
        ("max_sender_remote_granted_window_bytes={0}" -f (Get-FileTransferMaxField -Events $senderThroughput -FieldName 'remote_granted_window_bytes')),
        ("max_sender_sent_cache_bytes={0}" -f (Get-FileTransferMaxField -Events $senderThroughput -FieldName 'sent_cache_bytes')),
        ("max_sender_pipeline_configured_depth={0}" -f $Summary.MaxSenderPipelineConfiguredDepth),
        ("max_sender_pipeline_effective_depth={0}" -f $Summary.MaxSenderPipelineEffectiveDepth),
        ("max_sender_pipeline_in_flight_frames={0}" -f $Summary.MaxSenderPipelineInFlightFrames),
        ("max_sender_pipeline_in_flight_bytes={0}" -f $Summary.MaxSenderPipelineInFlightBytes),
        ("sender_pipeline_scheduled_frames={0}" -f $Summary.SenderPipelineScheduledFrames),
        ("sender_pipeline_completed_frames={0}" -f $Summary.SenderPipelineCompletedFrames),
        ("sender_pipeline_failed_frames={0}" -f $Summary.SenderPipelineFailedFrames),
        ("max_sender_pipeline_fifo_wait_ms={0}" -f $Summary.MaxSenderPipelineFifoWaitMs),
        ("max_sender_pipeline_accepted_progress_lag_bytes={0}" -f $Summary.MaxSenderPipelineAcceptedProgressLagBytes),
        ("sender_feed_raw_bytes_prepared={0}" -f $Summary.SenderFeedRawBytesPrepared),
        ("sender_feed_read_duration_ms={0}" -f $Summary.SenderFeedReadDurationMs),
        ("sender_feed_batch_prepare_duration_ms={0}" -f $Summary.SenderFeedBatchPrepareDurationMs),
        ("sender_feed_schedule_duration_ms={0}" -f $Summary.SenderFeedScheduleDurationMs),
        ("max_sender_feed_inter_schedule_gap_p95_ms={0}" -f $Summary.MaxSenderFeedInterScheduleGapP95Ms),
        ("max_sender_feed_inter_schedule_gap_ms={0}" -f $Summary.MaxSenderFeedInterScheduleGapMs),
        ("sender_feed_credit_wait_duration_ms={0}" -f $Summary.SenderFeedCreditWaitDurationMs),
        ("sender_feed_credit_wait_ratio_percent={0}" -f $senderFeedCreditWaitRatioPercent),
        ("sender_feed_pipeline_slot_wait_duration_ms={0}" -f $Summary.SenderFeedPipelineSlotWaitDurationMs),
        ("sender_feed_source_read_error_count={0}" -f $Summary.SenderFeedSourceReadErrorCount),
        ("receiver_feedback_pump_started_count={0}" -f $Summary.ReceiverFeedbackPumpStartedCount),
        ("receiver_feedback_pump_active_count={0}" -f $Summary.ReceiverFeedbackPumpActiveCount),
        ("slice_started_after_pump_start={0}" -f $Summary.ReceiverFeedbackSliceStartedAfterPumpStart),
        ("receiver_feedback_enqueued_count={0}" -f $Summary.ReceiverFeedbackEnqueuedCount),
        ("receiver_feedback_sent_count={0}" -f $Summary.ReceiverFeedbackSentCount),
        ("receiver_feedback_coalesced_count={0}" -f $Summary.ReceiverFeedbackCoalescedCount),
        ("receiver_feedback_summary_count={0}" -f $Summary.ReceiverFeedbackSummaryCount),
        ("receiver_feedback_failed_count={0}" -f $Summary.ReceiverFeedbackFailedCount),
        ("max_receiver_feedback_queue_depth={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackQueueDepth, $Summary.MaxReceiverFeedbackSummaryQueueDepth))),
        ("max_receiver_feedback_enqueue_to_send_age_ms={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackEnqueueToSendAgeMs, $Summary.MaxReceiverFeedbackSummaryEnqueueToSendAgeMs))),
        ("max_receiver_feedback_send_duration_ms={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackSendDurationMs, $Summary.MaxReceiverFeedbackSummarySendDurationMs))),
        ("max_sender_repair_cache_bytes={0}" -f $Summary.MaxSenderRepairCacheBytes),
        ("max_sender_repair_cache_hard_limit_bytes={0}" -f $Summary.MaxSenderRepairCacheHardLimitBytes),
        ("sender_repair_cache_hit_count={0}" -f $Summary.SenderRepairCacheHitCount),
        ("sender_repair_cache_miss_count={0}" -f $Summary.SenderRepairCacheMissCount),
        ("sender_repair_source_reread_count={0}" -f $Summary.SenderRepairSourceRereadCount),
        ("sender_repair_cache_eviction_count={0}" -f $Summary.SenderRepairCacheEvictionCount),
        ("max_bridge_bulk_payload_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkSummaries -FieldName 'payload_bytes_per_second')),
        ("max_bridge_bulk_in_flight={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $bridgeBulkSummaries -FieldName 'in_flight'), (Get-FileTransferMaxField -Events $bridgeBulkSummaries -FieldName 'in_flight_max')))),
        ("max_bridge_bulk_configured_concurrency={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkSummaries -FieldName 'configured_concurrency')),
        ("max_bridge_bulk_effective_concurrency={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkSummaries -FieldName 'effective_concurrency')),
        ("file_only_reorder_policy_decision_count={0}" -f $reorderPolicy.Count),
        ("file_only_grant_window_summary_count={0}" -f $grantSummaries.Count),
        ("grant_send_count={0}" -f $grantSummaries.Count),
        ("grant_send_rate_per_second={0}" -f $grantSendRatePerSecond),
        ("grant_delivery_control_count={0}" -f (@($grantDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'control' }).Count)),
        ("grant_delivery_bulk_count={0}" -f (@($grantDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'bulk' }).Count)),
        ("ack_delivery_control_count={0}" -f (@($ackDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'control' }).Count)),
        ("ack_delivery_bulk_count={0}" -f (@($ackDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'bulk' }).Count)),
        ("average_effective_grant_window_bytes={0}" -f (Get-FileTransferAverageDoubleField -Events $grantSummaries -FieldName 'effective_granted_window_bytes')),
        ("grant_credit_base_sparse_count={0}" -f (@($grantSummaries | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'sparse_base' }).Count)),
        ("grant_credit_base_contiguous_count={0}" -f (@($grantSummaries | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'contiguous_frontier' }).Count)),
        ("grant_base_blocked_by_gap_count={0}" -f (@($grantSummaries | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'gap_stall' }).Count)),
        ("sparse_credit_topup_count={0}" -f (@($grantSummaries | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'sparse_credit_topup' }).Count)),
        ("average_credit_remaining_bytes={0}" -f (Get-FileTransferAverageDoubleField -Events $grantSummaries -FieldName 'credit_remaining_bytes')),
        ("max_sparse_credit_advance_bytes={0}" -f (Get-FileTransferMaxField -Events $grantSummaries -FieldName 'sparse_credit_advance_bytes')),
        ("sparse_credit_eligible_count={0}" -f $sparseCreditStats.EligibleCount),
        ("sparse_credit_used_count={0}" -f $sparseCreditStats.UsedCount),
        ("sparse_credit_blocked_count={0}" -f $sparseCreditStats.BlockedCount),
        ("sparse_credit_reorder_eligible_count={0}" -f $sparseCreditStats.ReorderEligibleCount),
        ("sparse_credit_reorder_used_count={0}" -f $sparseCreditStats.ReorderUsedCount),
        ("sparse_credit_reorder_use_ratio_percent={0}" -f $sparseCreditStats.ReorderUseRatioPercent),
        ("sparse_credit_blocked_no_sparse_ahead_count={0}" -f $sparseCreditStats.BlockedNoSparseAheadCount),
        ("sparse_credit_blocked_gap_stall_count={0}" -f $sparseCreditStats.BlockedGapStallCount),
        ("sparse_credit_blocked_repair_pressure_count={0}" -f $sparseCreditStats.BlockedRepairPressureCount),
        ("proactive_frontier_repair_eligible_count={0}" -f $Summary.ProactiveFrontierRepairEligibleCount),
        ("proactive_frontier_repair_requested_count={0}" -f $Summary.ProactiveFrontierRepairRequestedCount),
        ("proactive_frontier_repair_sender_received_count={0}" -f $Summary.ProactiveFrontierRepairSenderReceivedCount),
        ("proactive_frontier_repair_sender_scheduled_count={0}" -f $Summary.ProactiveFrontierRepairSenderScheduledCount),
        ("proactive_frontier_repair_sender_sent_count={0}" -f $Summary.ProactiveFrontierRepairSenderSentCount),
        ("proactive_frontier_repair_filled_count={0}" -f $Summary.ProactiveFrontierRepairFilledCount),
        ("max_frontier_repair_request_to_fill_ms={0}" -f $Summary.MaxFrontierRepairRequestToFillMs),
        ("proactive_frontier_repair_skipped_count={0}" -f $Summary.ProactiveFrontierRepairSkippedCount),
        ("proactive_frontier_repair_skipped_gap_age_below_min_count={0}" -f (@($frontierGapRepairSkippedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' }).Count)),
        ("proactive_frontier_repair_skipped_duplicate_recent_count={0}" -f (@($frontierGapRepairSkippedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'duplicate_recent' }).Count)),
        ("stale_proactive_repair_state_reset_count={0}" -f $frontierGapRepairResetEvents.Count),
        ("benign_gap_skip_limited_policy_count={0}" -f $benignGapSkipLimitedPolicyCount),
        ("max_proactive_frontier_repair_gap_age_ms={0}" -f $Summary.MaxProactiveFrontierRepairGapAgeMs),
        ("proactive_repair_benign_count={0}" -f $proactiveRepairPressureStats.BenignCount),
        ("proactive_repair_grace_active_count={0}" -f $proactiveRepairPressureStats.GraceActiveCount),
        ("proactive_repair_repeated_unfilled_count={0}" -f $proactiveRepairPressureStats.RepeatedUnfilledCount),
        ("proactive_repair_hard_limited_count={0}" -f $proactiveRepairPressureStats.HardLimitedCount),
        ("proactive_repair_hard_limited_during_grace_count={0}" -f $proactiveRepairPressureStats.HardLimitedDuringGraceCount),
        ("max_proactive_repair_age_ms={0}" -f $proactiveRepairPressureStats.MaxAgeMs),
        ("max_same_frontier_unfilled_ms={0}" -f $proactiveRepairPressureStats.MaxSameFrontierUnfilledMs),
        ("file_only_grant_low_watermark_count={0}" -f (@($grantSummaries | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'low_watermark' }).Count)),
        ("file_only_grant_target_changed_count={0}" -f (@($grantSummaries | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'target_changed' }).Count)),
        ("conservative_startup_probe_count={0}" -f $startupProbeCount),
        ("conservative_startup_fast_clean_count={0}" -f $startupFastCleanCount),
        ("conservative_startup_adverse_count={0}" -f $startupAdverseCount),
        ("max_conservative_startup_duration_ms={0}" -f $Summary.MaxConservativeStartupDurationMs),
        ("max_bytes_before_startup_exit={0}" -f $Summary.MaxBytesBeforeStartupExit),
        ("max_startup_probe_window_bytes={0}" -f $Summary.MaxStartupProbeWindowBytes),
        ("first_repair_or_timeout_before_startup_exit_count={0}" -f $Summary.FirstRepairOrTimeoutBeforeStartupExitCount),
        '',
        'throughput_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events @($throughput + $senderThroughput + $senderPipeline + $senderFeed + $senderCacheEvents + $receiverFeedbackEvents + $receiverThroughput + $gapStalls + $sparseEvents + $bridgeBulkSummaries + $profileChanged + $reorderPolicy + $grantSummaries + $frontierGapRepairEvents + $v6ControlHealthEvents | Sort-Object Sequence) -Limit 40)
}

function New-FileTransferProtocolShapeSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $profiles = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_profile', 'filetransfer_profile_selected', 'filetransfer_profile_step_up', 'filetransfer_profile_step_down', 'filetransfer_v4_profile_changed'))
    $legacyNegotiationRejectedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_legacy_negotiation_rejected'))
    $v6NegotiatedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_negotiated'))
    $v6SessionOpenRejectedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_session_open_rejected'))
    $v6ReceiverStartedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_receiver_started'))
    $v6SenderStartedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_sender_started'))
    $v4NegotiatedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_negotiated'))
    $v4SessionOpenRejectedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_session_open_rejected'))
    $v4ReceiverStartedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_started'))
    $v4ManifestEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_manifest_received'))
    $v4SparseModeEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sparse_mode_selected'))
    $v4StateEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_sent'))
    $v4BatchEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_chunk_batch_received'))
    $v4CompleteEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_complete_sent'))
    $v4ReceiverFailedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_failed'))
    $v4SenderStartedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_started'))
    $v4ManifestSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_manifest_sent'))
    $v4StateReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_received'))
    $v4ChunkBatchSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_chunk_batch_sent'))
    $v4SenderPumpEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_pump_summary'))
    $v4EfficiencyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_efficiency_summary'))
    $v4OutboundEfficiencyEvents = @($v4EfficiencyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' })
    $v4InboundEfficiencyEvents = @($v4EfficiencyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' })
    $v4ChunkBatchCount = $v4BatchEvents.Count
    if ($v4ChunkBatchCount -eq 0) {
        $v4ChunkBatchCount = Get-FileTransferMaxField -Events $v4InboundEfficiencyEvents -FieldName 'raw_batch_frames_received_total'
    }
    $v4ChunkBatchSentCount = $v4ChunkBatchSentEvents.Count
    if ($v4ChunkBatchSentCount -eq 0) {
        $v4ChunkBatchSentCount = [Math]::Max(
            (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'batch_frames_sent_total'),
            (Get-FileTransferMaxField -Events $v4OutboundEfficiencyEvents -FieldName 'batch_frames_sent_total'))
    }
    $v4RepairScheduledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_scheduled'))
    $v4RepairSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_sent'))
    $v4CompleteReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_complete_received'))
    $v4SenderFailedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_failed'))
    $v4FeedbackFirstSuccessEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_feedback_first_success'))
    $v4FeedbackBothFailedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_feedback_both_failed'))
    $v4FeedbackSecondaryCompletedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_feedback_secondary_completed'))

    $unexpectedLegacyFrameEventsDuringV4 = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_sent', 'filetransfer_binary_frame_received', 'filetransfer_data_frame_dispatched') |
        Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -like 'filetransfer.*' -and
                $frameType -notlike 'filetransfer.*.v4' -and
                $frameType -notlike 'filetransfer.*.v6'
        })
    $legacyDataProtocolStartedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_session_opened') |
        Where-Object {
            $protocolVersion = Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default ''
            -not [string]::IsNullOrWhiteSpace($protocolVersion) -and $protocolVersion -ne '4' -and $protocolVersion -ne '6'
        })
    $frameTypeLines = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($Summary.FrameTypeCounts.Keys | Sort-Object)) {
        $frameTypeLines.Add(("frame_type_count.{0}={1}" -f $key, $Summary.FrameTypeCounts[$key])) | Out-Null
    }

    if ($frameTypeLines.Count -eq 0) {
        $frameTypeLines.Add('frame_type_count.(none)=0') | Out-Null
    }

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("profile_event_count={0}" -f $profiles.Count),
        ("profile_step_up_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_profile_step_up')),
        ("profile_step_down_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_profile_step_down')),
        ("v4_profile_changed_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_profile_changed')),
        ("legacy_negotiation_rejected_count={0}" -f $legacyNegotiationRejectedEvents.Count),
        ("v6_negotiated_count={0}" -f $v6NegotiatedEvents.Count),
        ("v6_session_open_rejected_count={0}" -f $v6SessionOpenRejectedEvents.Count),
        ("v6_receiver_started_count={0}" -f $v6ReceiverStartedEvents.Count),
        ("v6_sender_started_count={0}" -f $v6SenderStartedEvents.Count),
        ("v4_negotiated_count={0}" -f $v4NegotiatedEvents.Count),
        ("v4_session_open_rejected_count={0}" -f $v4SessionOpenRejectedEvents.Count),
        ("v4_receiver_started_count={0}" -f $v4ReceiverStartedEvents.Count),
        ("v4_manifest_count={0}" -f $v4ManifestEvents.Count),
        ("v4_sparse_receiver_selected_count={0}" -f $v4SparseModeEvents.Count),
        ("v4_state_sent_count={0}" -f $v4StateEvents.Count),
        ("v4_chunk_batch_count={0}" -f $v4ChunkBatchCount),
        ("v4_complete_count={0}" -f $v4CompleteEvents.Count),
        ("v4_receiver_failed_count={0}" -f $v4ReceiverFailedEvents.Count),
        ("v4_sender_started_count={0}" -f $v4SenderStartedEvents.Count),
        ("v4_manifest_sent_count={0}" -f $v4ManifestSentEvents.Count),
        ("v4_state_received_count={0}" -f $v4StateReceivedEvents.Count),
        ("v4_chunk_batch_sent_count={0}" -f $v4ChunkBatchSentCount),
        ("v4_sender_pump_summary_count={0}" -f $v4SenderPumpEvents.Count),
        ("v4_repair_scheduled_count={0}" -f $v4RepairScheduledEvents.Count),
        ("v4_repair_sent_count={0}" -f $v4RepairSentEvents.Count),
        ("v4_complete_received_count={0}" -f $v4CompleteReceivedEvents.Count),
        ("v4_sender_failed_count={0}" -f $v4SenderFailedEvents.Count),
        ("v4_feedback_redundant_success_count={0}" -f $v4FeedbackFirstSuccessEvents.Count),
        ("v4_feedback_both_failed_count={0}" -f $v4FeedbackBothFailedEvents.Count),
        ("v4_feedback_secondary_completed_count={0}" -f $v4FeedbackSecondaryCompletedEvents.Count),
        ("unexpected_legacy_data_frame_during_v4_count={0}" -f $unexpectedLegacyFrameEventsDuringV4.Count),
        ("legacy_data_protocol_started_count={0}" -f $legacyDataProtocolStartedEvents.Count)
    ) + @($frameTypeLines) + @(
        '',
        'protocol_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events @($profiles + $legacyNegotiationRejectedEvents + $v6NegotiatedEvents + $v6SessionOpenRejectedEvents + $v6ReceiverStartedEvents + $v6SenderStartedEvents + $v4NegotiatedEvents + $v4SessionOpenRejectedEvents + $v4ReceiverStartedEvents + $v4ManifestEvents + $v4SparseModeEvents + $v4StateEvents + $v4BatchEvents + $v4CompleteEvents + $v4ReceiverFailedEvents + $v4SenderStartedEvents + $v4ManifestSentEvents + $v4StateReceivedEvents + $v4ChunkBatchSentEvents + $v4SenderPumpEvents + $v4RepairScheduledEvents + $v4RepairSentEvents + $v4CompleteReceivedEvents + $v4SenderFailedEvents + $v4FeedbackFirstSuccessEvents + $v4FeedbackBothFailedEvents + $v4FeedbackSecondaryCompletedEvents + $unexpectedLegacyFrameEventsDuringV4 + $legacyDataProtocolStartedEvents) -Limit 40)
}

function Get-FileTransferReorderProfileLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $events = @($Summary.TransferEvents | Sort-Object Sequence)
    $currentProfile = '(unknown)'
    $buckets = @{}
    foreach ($event in @($events)) {
        if ($event.EventName -eq 'filetransfer_v4_throughput_summary') {
            $currentProfile = Get-FileTransferEventField -Event $event -Name 'profile' -Default $currentProfile
            continue
        }

        if ($event.EventName -eq 'filetransfer_v4_profile_changed') {
            $currentProfile = Get-FileTransferEventField -Event $event -Name 'updated_profile' -Default $currentProfile
            continue
        }

        if ($event.EventName -ne 'filetransfer_reorder_pressure') {
            continue
        }

        if (-not $buckets.ContainsKey($currentProfile)) {
            $buckets[$currentProfile] = New-Object System.Collections.ArrayList
        }

        $buckets[$currentProfile].Add($event) | Out-Null
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($profile in @($buckets.Keys | Sort-Object)) {
        $profileEvents = @($buckets[$profile])
        $safeProfile = $profile -replace '[^A-Za-z0-9_.-]', '_'
        $lines.Add(("reorder_by_profile.{0}.count={1}" -f $safeProfile, $profileEvents.Count)) | Out-Null
        $lines.Add(("reorder_by_profile.{0}.max_late_arrival_distance={1}" -f $safeProfile, (Get-FileTransferMaxField -Events $profileEvents -FieldName 'late_arrival_distance'))) | Out-Null
        $lines.Add(("reorder_by_profile.{0}.p95_late_arrival_distance={1}" -f $safeProfile, (Get-FileTransferPercentileField -Events $profileEvents -FieldName 'late_arrival_distance' -Percentile 95))) | Out-Null
    }

    if ($lines.Count -eq 0) {
        $lines.Add('reorder_by_profile.(none).count=0') | Out-Null
    }

    return $lines.ToArray()
}

function New-FileTransferRepairReorderSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $events = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @(
        'filetransfer_reorder_pressure',
        'filetransfer_v4_gap_stall_summary',
        'filetransfer_v4_profile_changed',
        'filetransfer_v4_reorder_policy_decision',
        'filetransfer_v4_grant_window_summary',
        'filetransfer_v4_throughput_summary',
        'filetransfer_receiver_buffer_pressure_entered',
        'filetransfer_receiver_buffer_pressure_exited',
        'filetransfer_receiver_grant_clamped_for_buffer',
        'filetransfer_receiver_write_batch_committed',
        'filetransfer_receiver_sparse_mode_selected',
        'filetransfer_receiver_sparse_write_summary',
        'filetransfer_receiver_sparse_commit_summary',
        'filetransfer_v4_receiver_feedback_pump_started',
        'filetransfer_v4_receiver_feedback_enqueued',
        'filetransfer_v4_receiver_feedback_coalesced',
        'filetransfer_v4_receiver_feedback_sent',
        'filetransfer_v4_receiver_feedback_summary',
        'filetransfer_v4_receiver_feedback_failed',
        'filetransfer_sender_repair_cache_policy',
        'filetransfer_sender_repair_cache_summary',
        'filetransfer_sender_repair_cache_pressure_entered',
        'filetransfer_sender_repair_cache_pressure_exited',
        'filetransfer_sender_cache_exhausted',
        'filetransfer_sender_repair_unavailable',
        'filetransfer_sender_repair_chunk_skipped',
        'filetransfer_frontier_gap_repair_eligible',
        'filetransfer_frontier_gap_repair_requested',
        'filetransfer_frontier_gap_repair_skipped',
        'filetransfer_frontier_gap_repair_suppressed',
        'filetransfer_frontier_gap_repair_sender_received',
        'filetransfer_frontier_gap_repair_sender_scheduled',
        'filetransfer_frontier_gap_repair_sender_sent',
        'filetransfer_frontier_gap_repair_filled',
        'filetransfer_proactive_frontier_repair_state_reset',
        'filetransfer_repair_set_requested',
        'filetransfer_repair_set_received',
        'filetransfer_repair_set_sent',
        'filetransfer_repair_request_suppressed',
        'filetransfer_request_timeout_detected',
        'filetransfer_chunk_retry_requested',
        'filetransfer_chunk_retry_sent',
        'filetransfer_chunk_retry_gate_blocked',
        'filetransfer_request_duplicate_ignored',
        'filetransfer_chunk_resend_suppressed',
        'filetransfer_session_degraded_entered',
        'filetransfer_session_degraded_exited',
        'filetransfer_bulk_unhealthy_detected',
        'filetransfer_bulk_fallback_entered',
        'filetransfer_bulk_fallback_exited',
        'filetransfer_v4_state_received',
        'filetransfer_v4_repair_scheduled',
        'filetransfer_v4_repair_sent'))
    $reorderEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_reorder_pressure'))
    $gapStallEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_gap_stall_summary'))
    $firstReorder = @($reorderEvents | Sort-Object Sequence | Select-Object -First 1)
    $lastReorder = @($reorderEvents | Sort-Object Sequence | Select-Object -Last 1)
    $firstGapStall = @($gapStallEvents | Sort-Object Sequence | Select-Object -First 1)
    $lastGapStall = @($gapStallEvents | Sort-Object Sequence | Select-Object -Last 1)
    $throughputEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_throughput_summary'))
    $profileChangedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_profile_changed'))
    $reorderPolicyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_reorder_policy_decision'))
    $grantSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_grant_window_summary'))
    $sparseCreditStats = Get-FileTransferSparseCreditStats -GrantEvents $grantSummaryEvents
    $frontierGapRepairEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_frontier_gap_repair_eligible', 'filetransfer_frontier_gap_repair_requested', 'filetransfer_frontier_gap_repair_skipped', 'filetransfer_frontier_gap_repair_suppressed', 'filetransfer_frontier_gap_repair_sender_received', 'filetransfer_frontier_gap_repair_sender_scheduled', 'filetransfer_frontier_gap_repair_sender_sent', 'filetransfer_frontier_gap_repair_filled', 'filetransfer_proactive_frontier_repair_state_reset'))
    $proactiveRepairPressureStats = Get-FileTransferProactiveRepairPressureStats -Events @($reorderPolicyEvents + $grantSummaryEvents + $frontierGapRepairEvents)
    $frontierGapRepairSkippedEvents = @($frontierGapRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_frontier_gap_repair_skipped' })
    $frontierGapRepairResetEvents = @($frontierGapRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_proactive_frontier_repair_state_reset' })
    $benignGapSkipLimitedPolicyCount = @($frontierGapRepairSkippedEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' -and
        (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '(none)') -eq '(none)' -and
        (Get-FileTransferEventField -Event $_ -Name 'grant_policy_after_repair' -Default '') -eq 'healthy_limited'
    }).Count
    $maxSkippedFrontierGapAgeMs = Get-FileTransferMaxField -Events $frontierGapRepairSkippedEvents -FieldName 'gap_stall_age_ms'
    $maxUnrepairedFrontierGapAgeMs = $maxSkippedFrontierGapAgeMs
    if ($Summary.ProactiveFrontierRepairRequestedCount -eq 0) {
        $maxUnrepairedFrontierGapAgeMs = [Math]::Max(
            $maxUnrepairedFrontierGapAgeMs,
            (Get-FileTransferMaxField -Events $gapStallEvents -FieldName 'stall_duration_ms'))
    }
    $singleRepairRequestEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_sent', 'filetransfer_binary_frame_received') |
        Where-Object { (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.repair_request.v4' })
    $v4StateReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_received'))
    $v4StateWithMissingRangesEvents = @($v4StateReceivedEvents | Where-Object {
        (Get-FileTransferEventInt64Field -Event $_ -Name 'missing_range_count' -Default 0) -gt 0 -or
        (Get-FileTransferEventInt64Field -Event $_ -Name 'missing_ranges_count' -Default 0) -gt 0
    })
    $v4RepairRequestedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_requested'))
    $v4RepairScheduledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_scheduled'))
    $v4RepairSuppressedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_suppressed'))
    $v4RepairSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_sent'))
    $v4RepairObservedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_chunk_observed'))
    $v4RepairFilledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_filled'))
    $v4RepairBatchEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch') |
        Where-Object { (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -like 'v4_repair_*' })
    $v4FrontierTailRepairDueEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_due'))
    $v4FrontierTailRepairSuppressedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_suppressed'))
    $v4FrontierTailRepairFilledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_filled'))
    $v4RepairClearedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_cleared'))
    $v4MissingRangeDueStateMismatchCount = Get-FileTransferV4MissingRangeDueStateMismatchCount -DueEvents $v4FrontierTailRepairDueEvents -StateSentEvents (Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_sent'))

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("reorder_event_count={0}" -f $Summary.ReorderEventCount),
        ("max_late_arrival_distance={0}" -f $Summary.MaxLateArrivalDistance),
        ("p95_late_arrival_distance={0}" -f (Get-FileTransferPercentileField -Events $reorderEvents -FieldName 'late_arrival_distance' -Percentile 95)),
        ("first_reorder_event={0}" -f ($(if ($firstReorder.Count -gt 0) { Format-FileTransferEvidenceLine -Event $firstReorder[0] } else { '(none)' }))),
        ("last_reorder_event={0}" -f ($(if ($lastReorder.Count -gt 0) { Format-FileTransferEvidenceLine -Event $lastReorder[0] } else { '(none)' }))),
        ("gap_stall_summary_count={0}" -f $gapStallEvents.Count),
        ("first_gap_stall_event={0}" -f ($(if ($firstGapStall.Count -gt 0) { Format-FileTransferEvidenceLine -Event $firstGapStall[0] } else { '(none)' }))),
        ("last_gap_stall_event={0}" -f ($(if ($lastGapStall.Count -gt 0) { Format-FileTransferEvidenceLine -Event $lastGapStall[0] } else { '(none)' }))),
        ("max_gap_stall_duration_ms={0}" -f (Get-FileTransferMaxField -Events $gapStallEvents -FieldName 'stall_duration_ms')),
        ("max_gap_stall_late_arrival_distance={0}" -f (Get-FileTransferMaxField -Events $gapStallEvents -FieldName 'late_arrival_distance')),
        ("max_gap_stall_pending_bytes={0}" -f (Get-FileTransferMaxField -Events $gapStallEvents -FieldName 'pending_bytes')),
        ("max_v4_granted_window_bytes={0}" -f (Get-FileTransferMaxField -Events $throughputEvents -FieldName 'granted_window_bytes')),
        ("max_v4_profile_target_window_bytes={0}" -f (Get-FileTransferMaxField -Events $profileChangedEvents -FieldName 'target_window_bytes')),
        ("v4_profile_changed_count={0}" -f $profileChangedEvents.Count),
        ("file_only_reorder_policy_decision_count={0}" -f $reorderPolicyEvents.Count),
        ("file_only_reorder_tolerated_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'tolerated' }).Count)),
        ("file_only_reorder_soft_limited_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'soft_limited' }).Count)),
        ("file_only_reorder_limited_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'limited' }).Count)),
        ("file_only_reorder_conservative_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'conservative' }).Count)),
        ("file_only_grant_window_summary_count={0}" -f $grantSummaryEvents.Count),
        ("file_only_grant_low_watermark_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'low_watermark' }).Count)),
        ("file_only_grant_target_changed_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'target_changed' }).Count)),
        ("sparse_credit_eligible_count={0}" -f $sparseCreditStats.EligibleCount),
        ("sparse_credit_used_count={0}" -f $sparseCreditStats.UsedCount),
        ("sparse_credit_blocked_count={0}" -f $sparseCreditStats.BlockedCount),
        ("sparse_credit_reorder_eligible_count={0}" -f $sparseCreditStats.ReorderEligibleCount),
        ("sparse_credit_reorder_used_count={0}" -f $sparseCreditStats.ReorderUsedCount),
        ("sparse_credit_reorder_use_ratio_percent={0}" -f $sparseCreditStats.ReorderUseRatioPercent),
        ("sparse_credit_blocked_no_sparse_ahead_count={0}" -f $sparseCreditStats.BlockedNoSparseAheadCount),
        ("sparse_credit_blocked_gap_stall_count={0}" -f $sparseCreditStats.BlockedGapStallCount),
        ("sparse_credit_blocked_repair_pressure_count={0}" -f $sparseCreditStats.BlockedRepairPressureCount),
        ("max_conservative_startup_duration_ms={0}" -f $Summary.MaxConservativeStartupDurationMs),
        ("max_bytes_before_startup_exit={0}" -f $Summary.MaxBytesBeforeStartupExit),
        ("max_startup_probe_window_bytes={0}" -f $Summary.MaxStartupProbeWindowBytes),
        ("first_repair_or_timeout_before_startup_exit_count={0}" -f $Summary.FirstRepairOrTimeoutBeforeStartupExitCount),
        ("receiver_buffer_pressure_entered_count={0}" -f $Summary.ReceiverBufferPressureEnteredCount),
        ("receiver_buffer_pressure_exited_count={0}" -f $Summary.ReceiverBufferPressureExitedCount),
        ("receiver_buffer_grant_clamped_count={0}" -f $Summary.ReceiverBufferGrantClampedCount),
        ("receiver_write_batch_committed_count={0}" -f $Summary.ReceiverBufferWriteBatchCommittedCount),
        ("receiver_sparse_mode_selected_count={0}" -f $Summary.ReceiverSparseModeSelectedCount),
        ("receiver_sparse_write_summary_count={0}" -f $Summary.ReceiverSparseWriteSummaryCount),
        ("receiver_sparse_commit_summary_count={0}" -f $Summary.ReceiverSparseCommitSummaryCount),
        ("max_sparse_write_bytes_per_second={0}" -f $Summary.MaxReceiverSparseWriteBytesPerSecond),
        ("max_sparse_written_ahead_bytes={0}" -f $Summary.MaxReceiverSparseWrittenAheadBytes),
        ("max_sparse_gap_count={0}" -f $Summary.MaxReceiverSparseGapCount),
        ("sender_cache_exhausted_count={0}" -f $Summary.SenderCacheExhaustedCount),
        ("sender_repair_unavailable_count={0}" -f $Summary.SenderRepairUnavailableCount),
        ("sender_repair_chunk_skipped_count={0}" -f $Summary.SenderRepairChunkSkippedCount),
        ("sender_repair_chunk_skipped_obsolete_count={0}" -f (Get-FileTransferSkippedRepairChunkReasonCount -Summary $Summary -Reason 'obsolete')),
        ("sender_repair_chunk_skipped_not_yet_sent_count={0}" -f (Get-FileTransferSkippedRepairChunkReasonCount -Summary $Summary -Reason 'not_yet_sent')),
        ("sender_repair_chunk_skipped_out_of_bounds_count={0}" -f (Get-FileTransferSkippedRepairChunkReasonCount -Summary $Summary -Reason 'out_of_bounds')),
        ("max_sender_repair_cache_bytes={0}" -f $Summary.MaxSenderRepairCacheBytes),
        ("sender_repair_cache_hit_count={0}" -f $Summary.SenderRepairCacheHitCount),
        ("sender_repair_cache_miss_count={0}" -f $Summary.SenderRepairCacheMissCount),
        ("sender_repair_source_reread_count={0}" -f $Summary.SenderRepairSourceRereadCount),
        ("sender_repair_cache_eviction_count={0}" -f $Summary.SenderRepairCacheEvictionCount),
        ("max_receiver_pending_bytes={0}" -f $Summary.MaxReceiverPendingBytes),
        ("max_receiver_write_batch_bytes={0}" -f $Summary.MaxReceiverWriteBatchBytes),
        ("max_receiver_write_duration_ms={0}" -f $Summary.MaxReceiverWriteDurationMs),
        ("receiver_feedback_pump_started_count={0}" -f $Summary.ReceiverFeedbackPumpStartedCount),
        ("receiver_feedback_pump_active_count={0}" -f $Summary.ReceiverFeedbackPumpActiveCount),
        ("slice_started_after_pump_start={0}" -f $Summary.ReceiverFeedbackSliceStartedAfterPumpStart),
        ("receiver_feedback_enqueued_count={0}" -f $Summary.ReceiverFeedbackEnqueuedCount),
        ("receiver_feedback_sent_count={0}" -f $Summary.ReceiverFeedbackSentCount),
        ("receiver_feedback_coalesced_count={0}" -f $Summary.ReceiverFeedbackCoalescedCount),
        ("receiver_feedback_failed_count={0}" -f $Summary.ReceiverFeedbackFailedCount),
        ("max_receiver_feedback_queue_depth={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackQueueDepth, $Summary.MaxReceiverFeedbackSummaryQueueDepth))),
        ("max_receiver_feedback_enqueue_to_send_age_ms={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackEnqueueToSendAgeMs, $Summary.MaxReceiverFeedbackSummaryEnqueueToSendAgeMs))),
        ("max_receiver_feedback_send_duration_ms={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackSendDurationMs, $Summary.MaxReceiverFeedbackSummarySendDurationMs))),
        ("request_timeout_count={0}" -f $Summary.RequestTimeoutCount),
        ("retry_requested_count={0}" -f $Summary.RetryRequestedCount),
        ("retry_sent_count={0}" -f $Summary.RetrySentCount),
        ("single_repair_request_count={0}" -f $singleRepairRequestEvents.Count),
        ("v4_state_received_count={0}" -f $v4StateReceivedEvents.Count),
        ("v4_state_with_missing_ranges_count={0}" -f $v4StateWithMissingRangesEvents.Count),
        ("v4_max_missing_range_count={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $v4StateReceivedEvents -FieldName 'missing_range_count'), (Get-FileTransferMaxField -Events $v4StateReceivedEvents -FieldName 'missing_ranges_count')))),
        ("v4_repair_requested_count={0}" -f $v4RepairRequestedEvents.Count),
        ("v4_missing_range_repair_scheduled_count={0}" -f $v4RepairScheduledEvents.Count),
        ("v4_repair_suppressed_count={0}" -f $v4RepairSuppressedEvents.Count),
        ("v4_missing_range_repair_sent_count={0}" -f $v4RepairSentEvents.Count),
        ("v4_repair_delivery_bulk_only_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_mode' -Value 'bulk_only')),
        ("v4_repair_delivery_control_bulk_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_mode' -Value 'control_bulk_escalated')),
        ("v4_repair_delivery_retry_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'retry')),
        ("v4_repair_delivery_credit_stall_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'credit_stall')),
        ("v4_repair_delivery_frontier_not_advanced_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'frontier_not_advanced')),
        ("v4_repair_delivery_primary_regular_nkn_frontier_first_send_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'primary_regular_nkn_frontier_first_send')),
        ("v4_repair_batch_bulk_only_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairBatchEvents -FieldName 'repair_delivery_mode' -Value 'bulk_only')),
        ("v4_repair_batch_control_bulk_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairBatchEvents -FieldName 'repair_delivery_mode' -Value 'control_bulk_escalated')),
        ("v4_repair_chunk_observed_count={0}" -f $v4RepairObservedEvents.Count),
        ("v4_repair_observed_accepted_chunk_count={0}" -f (Get-FileTransferSumField -Events $v4RepairObservedEvents -FieldName 'accepted_chunk_count')),
        ("v4_repair_observed_duplicate_or_stale_chunk_count={0}" -f (Get-FileTransferSumField -Events $v4RepairObservedEvents -FieldName 'duplicate_or_stale_chunk_count')),
        ("v4_repair_observed_frontier_advanced_count={0}" -f (@($v4RepairObservedEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'frontier_advanced' -Default 0) -gt 0 }).Count)),
        ("v4_repair_filled_count={0}" -f $v4RepairFilledEvents.Count),
        ("v4_repair_cleared_count={0}" -f $v4RepairClearedEvents.Count),
        ("v4_frontier_tail_repair_due_count={0}" -f $v4FrontierTailRepairDueEvents.Count),
        ("v4_frontier_tail_repair_suppressed_count={0}" -f $v4FrontierTailRepairSuppressedEvents.Count),
        ("v4_frontier_tail_repair_filled_count={0}" -f $v4FrontierTailRepairFilledEvents.Count),
        ("v4_max_frontier_stall_age_ms={0}" -f (Get-FileTransferMaxField -Events @($v4FrontierTailRepairDueEvents + $v4FrontierTailRepairSuppressedEvents) -FieldName 'frontier_stall_age_ms')),
        ("v4_missing_range_due_state_mismatch_count={0}" -f $v4MissingRangeDueStateMismatchCount),
        ("v4_repair_requested_chunk_count={0}" -f (Get-FileTransferSumField -Events $v4RepairScheduledEvents -FieldName 'requested_chunk_count')),
        ("v4_repair_sent_chunk_count={0}" -f (Get-FileTransferSumField -Events $v4RepairSentEvents -FieldName 'sent_chunk_count')),
        ("v4_repair_request_to_fill_p95_ms={0}" -f (Get-FileTransferPercentileField -Events $v4RepairFilledEvents -FieldName 'request_to_fill_ms' -Percentile 95)),
        ("v4_repair_skipped_obsolete_count={0}" -f (Get-FileTransferSumField -Events @($v4RepairScheduledEvents + $v4RepairSentEvents) -FieldName 'skipped_obsolete_count')),
        ("v4_repair_skipped_future_count={0}" -f (Get-FileTransferSumField -Events @($v4RepairScheduledEvents + $v4RepairSentEvents) -FieldName 'skipped_future_count')),
        ("v4_repair_skipped_out_of_bounds_count={0}" -f (Get-FileTransferSumField -Events @($v4RepairScheduledEvents + $v4RepairSentEvents) -FieldName 'skipped_out_of_bounds_count')),
        ("proactive_frontier_repair_eligible_count={0}" -f $Summary.ProactiveFrontierRepairEligibleCount),
        ("proactive_frontier_repair_requested_count={0}" -f $Summary.ProactiveFrontierRepairRequestedCount),
        ("proactive_frontier_repair_sender_received_count={0}" -f $Summary.ProactiveFrontierRepairSenderReceivedCount),
        ("proactive_frontier_repair_sender_scheduled_count={0}" -f $Summary.ProactiveFrontierRepairSenderScheduledCount),
        ("proactive_frontier_repair_sender_sent_count={0}" -f $Summary.ProactiveFrontierRepairSenderSentCount),
        ("proactive_frontier_repair_filled_count={0}" -f $Summary.ProactiveFrontierRepairFilledCount),
        ("max_frontier_repair_request_to_fill_ms={0}" -f $Summary.MaxFrontierRepairRequestToFillMs),
        ("proactive_frontier_repair_skipped_count={0}" -f $Summary.ProactiveFrontierRepairSkippedCount),
        ("proactive_frontier_repair_skipped_gap_age_below_min_count={0}" -f (@($frontierGapRepairSkippedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' }).Count)),
        ("proactive_frontier_repair_skipped_duplicate_recent_count={0}" -f (@($frontierGapRepairSkippedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'duplicate_recent' }).Count)),
        ("proactive_frontier_repair_suppressed_count={0}" -f $Summary.ProactiveFrontierRepairSuppressedCount),
        ("stale_proactive_repair_state_reset_count={0}" -f $frontierGapRepairResetEvents.Count),
        ("benign_gap_skip_limited_policy_count={0}" -f $benignGapSkipLimitedPolicyCount),
        ("max_proactive_frontier_repair_gap_age_ms={0}" -f $Summary.MaxProactiveFrontierRepairGapAgeMs),
        ("max_unrepaired_frontier_gap_age_ms={0}" -f $maxUnrepairedFrontierGapAgeMs),
        ("proactive_repair_benign_count={0}" -f $proactiveRepairPressureStats.BenignCount),
        ("proactive_repair_grace_active_count={0}" -f $proactiveRepairPressureStats.GraceActiveCount),
        ("proactive_repair_repeated_unfilled_count={0}" -f $proactiveRepairPressureStats.RepeatedUnfilledCount),
        ("proactive_repair_hard_limited_count={0}" -f $proactiveRepairPressureStats.HardLimitedCount),
        ("proactive_repair_hard_limited_during_grace_count={0}" -f $proactiveRepairPressureStats.HardLimitedDuringGraceCount),
        ("max_proactive_repair_age_ms={0}" -f $proactiveRepairPressureStats.MaxAgeMs),
        ("max_same_frontier_unfilled_ms={0}" -f $proactiveRepairPressureStats.MaxSameFrontierUnfilledMs),
        ("repair_set_requested_count={0}" -f $Summary.RepairSetRequestedCount),
        ("repair_set_received_count={0}" -f $Summary.RepairSetReceivedCount),
        ("repair_set_sent_count={0}" -f $Summary.RepairSetSentCount),
        ("repair_request_suppressed_count={0}" -f $Summary.RepairRequestSuppressedCount),
        ("max_repair_set_ranges={0}" -f $Summary.MaxRepairSetRanges),
        ("max_repair_set_chunks={0}" -f $Summary.MaxRepairSetChunks),
        ("total_repair_control_frame_count={0}" -f ($singleRepairRequestEvents.Count + $Summary.RepairSetRequestedCount)),
        ("retry_gate_blocked_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_chunk_retry_gate_blocked')),
        ("duplicate_request_ignored_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_request_duplicate_ignored')),
        ("resend_suppressed_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_chunk_resend_suppressed')),
        ("degraded_entered_count={0}" -f $Summary.DegradedEnteredCount),
        ("degraded_exited_count={0}" -f $Summary.DegradedExitedCount),
        ("bulk_unhealthy_count={0}" -f $Summary.BulkUnhealthyCount),
        ("bulk_fallback_entered_count={0}" -f $Summary.BulkFallbackEnteredCount),
        ("bulk_fallback_exited_count={0}" -f $Summary.BulkFallbackExitedCount)
    ) + (Get-FileTransferReorderProfileLines -Summary $Summary) + @(
        '',
        'repair_reorder_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $events -Limit 40)
}

function Get-FileTransferGateWarningCap {
    param($GateResult)

    if ($null -eq $GateResult) {
        return $null
    }

    $property = $GateResult.PSObject.Properties['WarningCap']
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-FileTransferGateFallbackDiagnostics {
    param($GateResult)

    if ($null -eq $GateResult) {
        return $null
    }

    $property = $GateResult.PSObject.Properties['FallbackDiagnostics']
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function New-FileTransferRouteConsistencySummaryLines {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    $routeConsistency = $Summary.RouteConsistency
    [object[]]$routeSelectedEvents = @()
    [object[]]$routeAwareEvents = @()
    [object[]]$findings = @()
    [object[]]$evidenceEvents = @()
    if ($null -ne $routeConsistency) {
        $routeSelectedEvents = @($routeConsistency.RouteSelectedEvents)
        $routeAwareEvents = @($routeConsistency.RouteAwareEvents)
        $findings = @($routeConsistency.Findings)
        $evidenceEvents = @($routeConsistency.EvidenceEvents)
    }
    [object[]]$selectedRoutes = @(
        $routeSelectedEvents |
            ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'route' -Default '' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )
    [object[]]$selectedRouteSequence = @(
        $routeSelectedEvents |
            Sort-Object Sequence |
            ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'route' -Default '' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $selectedRouteChanges = New-Object System.Collections.Generic.List[string]
    foreach ($route in @($selectedRouteSequence)) {
        if ($selectedRouteChanges.Count -eq 0 -or
            -not [string]::Equals($selectedRouteChanges[$selectedRouteChanges.Count - 1], [string]$route, [System.StringComparison]::OrdinalIgnoreCase)) {
            $selectedRouteChanges.Add([string]$route) | Out-Null
        }
    }

    $liveRouteProof = Get-FileTransferLiveRouteEpochProof -TransferEvents $Summary.TransferEvents -Mode $LiveRouteProofMode
    [object[]]$liveRouteEpochSequence = @($liveRouteProof.Sequence)
    [object[]]$liveRouteEpochRouteChanges = @($liveRouteProof.RouteChanges)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(("transfer_id={0}" -f $Summary.TransferId)) | Out-Null
    $lines.Add(("route_consistency_verdict={0}" -f ($(if ($null -ne $routeConsistency) { $routeConsistency.Verdict } else { 'legacy' })))) | Out-Null
    $lines.Add(("route_selected_count={0}" -f $routeSelectedEvents.Length)) | Out-Null
    $lines.Add(("route_aware_event_count={0}" -f $routeAwareEvents.Length)) | Out-Null
    $lines.Add(("route_mismatch_count={0}" -f $findings.Length)) | Out-Null
    $lines.Add(("selected_routes={0}" -f ($(if ($selectedRoutes.Length -gt 0) { $selectedRoutes -join ',' } else { '(none)' })))) | Out-Null
    $lines.Add(("selected_route_sequence={0}" -f ($(if ($selectedRouteSequence.Length -gt 0) { $selectedRouteSequence -join ',' } else { '(none)' })))) | Out-Null
    $lines.Add(("selected_route_changes={0}" -f ($(if ($selectedRouteChanges.Count -gt 0) { $selectedRouteChanges.ToArray() -join ',' } else { '(none)' })))) | Out-Null
    $lines.Add(("live_route_epoch_proof_mode={0}" -f $LiveRouteProofMode)) | Out-Null
    $lines.Add(("live_route_epoch_proof_verdict={0}" -f $liveRouteProof.Verdict)) | Out-Null
    $lines.Add(("live_route_epoch_event_count={0}" -f $liveRouteProof.CompleteEventCount)) | Out-Null
    $lines.Add(("live_route_epoch_explicit_event_count={0}" -f $liveRouteProof.ExplicitEventCount)) | Out-Null
    $lines.Add(("live_route_epoch_metadata_missing_count={0}" -f $liveRouteProof.MetadataMissingCount)) | Out-Null
    $lines.Add(("live_route_epoch_transport_only_count={0}" -f $liveRouteProof.TransportOnlyCount)) | Out-Null
    $lines.Add(("live_route_epoch_sequence={0}" -f ($(if ($liveRouteEpochSequence.Length -gt 0) { $liveRouteEpochSequence -join ',' } else { '(none)' })))) | Out-Null
    $lines.Add(("live_route_epoch_route_changes={0}" -f ($(if ($liveRouteEpochRouteChanges.Length -gt 0) { $liveRouteEpochRouteChanges -join ',' } else { '(none)' })))) | Out-Null

    $index = 0
    foreach ($event in @($routeSelectedEvents | Sort-Object Sequence)) {
        $index++
        $prefix = "selected.$index"
        $lines.Add(("{0}.direction={1}" -f $prefix, (Get-FileTransferEventField -Event $event -Name 'direction' -Default '(none)'))) | Out-Null
        $lines.Add(("{0}.route={1}" -f $prefix, (Get-FileTransferEventField -Event $event -Name 'route' -Default '(none)'))) | Out-Null
        $lines.Add(("{0}.protocol_version={1}" -f $prefix, (Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default '(none)'))) | Out-Null
        $lines.Add(("{0}.runtime_profile={1}" -f $prefix, (Get-FileTransferEventField -Event $event -Name 'runtime_profile' -Default '(none)'))) | Out-Null
        $lines.Add(("{0}.bridge_recovery_policy={1}" -f $prefix, (Get-FileTransferEventField -Event $event -Name 'bridge_recovery_policy' -Default '(none)'))) | Out-Null
        $lines.Add(("{0}.selection_reason={1}" -f $prefix, (Get-FileTransferEventField -Event $event -Name 'selection_reason' -Default '(none)'))) | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('mismatches:') | Out-Null
    if ($findings.Length -gt 0) {
        $mismatchIndex = 0
        foreach ($finding in @($findings)) {
            $mismatchIndex++
            $lines.Add(("mismatch.{0}={1}" -f $mismatchIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('live_route_epoch_findings:') | Out-Null
    if ($liveRouteProof.Findings.Count -gt 0) {
        $proofIndex = 0
        foreach ($finding in @($liveRouteProof.Findings)) {
            $proofIndex++
            $lines.Add(("proof.{0}={1}" -f $proofIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('route_evidence:') | Out-Null
    foreach ($line in @(Get-FileTransferArtifactEvidenceLines -Events @($routeSelectedEvents + $evidenceEvents) -Limit 40)) {
        $lines.Add($line) | Out-Null
    }

    return $lines.ToArray()
}

function New-FileTransferTransportBudgetSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $events = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @(
        'filetransfer_chunk_batch_sent_as_batch',
        'filetransfer_chunk_batch_transport_summary',
        'filetransfer_chunk_batch_split_for_transport',
        'filetransfer_transport_payload_budget',
        'filetransfer_transport_payload_rejected',
        'filetransfer_data_frame_decode_failed',
        'filetransfer_chunk_rejected',
        'filetransfer_message_rejected'))
    $transportSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_transport_summary'))
    $budgetEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_transport_payload_budget', 'filetransfer_transport_payload_rejected'))
    $budgetMetricEvents = @($budgetEvents + $transportSummaryEvents)

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("chunk_batch_sent_as_batch_count={0}" -f $Summary.BatchSentAsBatchCount),
        ("chunk_batch_split_count={0}" -f $Summary.BatchSplitCount),
        ("payload_budget_count={0}" -f $Summary.PayloadBudgetCount),
        ("payload_rejected_count={0}" -f $Summary.PayloadRejectedCount),
        ("data_frame_decode_failed_count={0}" -f $Summary.DataFrameDecodeFailedCount),
        ("chunk_rejected_count={0}" -f $Summary.ChunkRejectedCount),
        ("message_rejected_count={0}" -f $Summary.MessageRejectedCount),
        ("max_bridge_command_bytes={0}" -f (Get-FileTransferMaxField -Events $budgetEvents -FieldName 'bridge_command_bytes')),
        ("max_bridge_payload_bytes={0}" -f (Get-FileTransferMaxField -Events $budgetEvents -FieldName 'bridge_payload_bytes')),
        ("average_batch_chunk_count={0}" -f (Get-FileTransferAverageDoubleField -Events $budgetMetricEvents -FieldName 'batch_chunk_count')),
        ("max_batch_chunk_count={0}" -f (Get-FileTransferMaxField -Events $budgetMetricEvents -FieldName 'max_batch_chunk_count')),
        ("average_bridge_payload_fill_percent={0}" -f (Get-FileTransferAverageDoubleField -Events $budgetMetricEvents -FieldName 'bridge_payload_fill_percent')),
        ("p95_bridge_payload_fill_percent={0}" -f (Get-FileTransferPercentileDoubleField -Events $budgetMetricEvents -FieldName 'bridge_payload_fill_percent' -Percentile 95)),
        ("raw_to_bridge_payload_ratio_max={0}" -f (Get-FileTransferMaxDoubleField -Events $budgetMetricEvents -FieldName 'raw_to_bridge_payload_ratio')),
        '',
        'transport_budget_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $events -Limit 40)
}

function New-FileTransferPayloadEfficiencySummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $profileEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_payload_efficiency_profile_selected'))
    $batchEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch'))
    $transportSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_transport_summary'))
    $budgetEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_transport_payload_budget'))
    $binaryEvents = @(
        Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_binary_frame_sent') |
            Where-Object {
                $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
                $frameType -eq 'filetransfer.chunk_batch.v4'
            }
    )
    $shapeEvents = @($batchEvents + $budgetEvents + $binaryEvents + $transportSummaryEvents)
    $bridgeBulkEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_bulk_send_summary' })
    $evidence = @($profileEvents + $batchEvents + $transportSummaryEvents + $budgetEvents + $binaryEvents + $bridgeBulkEvents | Sort-Object Sequence)

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("payload_efficiency_profile={0}" -f (Get-FileTransferPayloadEfficiencyProfile -ProfileEvents $profileEvents -BudgetEvents $budgetEvents -BatchEvents $batchEvents)),
        ("profile_selected_count={0}" -f $profileEvents.Count),
        ("batch_sent_as_batch_count={0}" -f $Summary.BatchSentAsBatchCount),
        ("average_batch_chunk_count={0}" -f (Get-FileTransferAverageDoubleField -Events $shapeEvents -FieldName 'batch_chunk_count')),
        ("max_batch_chunk_count={0}" -f (Get-FileTransferMaxField -Events $shapeEvents -FieldName 'max_batch_chunk_count')),
        ("average_bridge_payload_fill_percent={0}" -f (Get-FileTransferAverageDoubleField -Events @($batchEvents + $budgetEvents + $transportSummaryEvents) -FieldName 'bridge_payload_fill_percent')),
        ("p95_bridge_payload_fill_percent={0}" -f (Get-FileTransferPercentileDoubleField -Events @($batchEvents + $budgetEvents + $transportSummaryEvents) -FieldName 'bridge_payload_fill_percent' -Percentile 95)),
        ("raw_to_bridge_payload_ratio_max={0}" -f (Get-FileTransferMaxDoubleField -Events @($batchEvents + $budgetEvents + $transportSummaryEvents) -FieldName 'raw_to_bridge_payload_ratio')),
        ("bulk_frames_per_mib={0}" -f (Get-FileTransferBulkFramesPerMiB -Summary $Summary)),
        ("max_bridge_bulk_payload_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'payload_bytes_per_second')),
        ("reorder_event_count={0}" -f $Summary.ReorderEventCount),
        ("request_timeout_count={0}" -f $Summary.RequestTimeoutCount),
        ("retry_requested_count={0}" -f $Summary.RetryRequestedCount),
        ("payload_rejected_count={0}" -f $Summary.PayloadRejectedCount),
        ("decode_failure_count={0}" -f $Summary.DataFrameDecodeFailedCount),
        ("message_rejected_count={0}" -f $Summary.MessageRejectedCount),
        '',
        'payload_efficiency_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $evidence -Limit 50)
}

function New-FileTransferBridgeBulkSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $events = @($Summary.GlobalEvents | Where-Object { $_.EventName -like 'nkn_bridge_bulk_*' })
    $summaries = @($events | Where-Object { $_.EventName -eq 'nkn_bridge_bulk_send_summary' })
    $states = @($events | Where-Object { $_.EventName -eq 'nkn_bridge_bulk_queue_state' })
    $failures = 0L
    $clears = 0L
    foreach ($event in @($summaries)) {
        $failures += Get-FileTransferEventInt64Field -Event $event -Name 'send_failures' -Default 0
        $clears += Get-FileTransferEventInt64Field -Event $event -Name 'queue_clears' -Default 0
    }
    foreach ($event in @($states)) {
        $clears += Get-FileTransferEventInt64Field -Event $event -Name 'cleared_since_last' -Default 0
    }

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("bulk_queue_state_count={0}" -f $states.Count),
        ("bulk_send_summary_count={0}" -f $summaries.Count),
        ("bulk_queue_waiting_count={0}" -f (Get-FileTransferEventCount -Events $events -Name 'nkn_bridge_bulk_queue_waiting')),
        ("bulk_send_failures={0}" -f $failures),
        ("bulk_queue_clears={0}" -f $clears),
        ("bulk_payload_bytes_sent={0}" -f (Get-FileTransferSumField -Events $summaries -FieldName 'payload_bytes_sent')),
        ("bulk_frames_enqueued={0}" -f (Get-FileTransferSumField -Events $summaries -FieldName 'frames_enqueued')),
        ("bulk_payload_bytes_enqueued={0}" -f (Get-FileTransferSumField -Events $summaries -FieldName 'payload_bytes_enqueued')),
        ("max_bulk_payload_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'payload_bytes_per_second')),
        ("max_bulk_payload_bytes_enqueued_per_second={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'payload_bytes_enqueued_per_second')),
        ("p95_bulk_payload_bytes_per_second={0}" -f (Get-FileTransferPercentileField -Events $summaries -FieldName 'payload_bytes_per_second' -Percentile 95)),
        ("max_bulk_inter_enqueue_gap_p95_ms={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'inter_enqueue_gap_p95_ms')),
        ("max_bulk_inter_enqueue_gap_ms={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'inter_enqueue_gap_max_ms')),
        ("max_bulk_send_p95_ms={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'send_p95_ms')),
        ("max_bulk_send_max_ms={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'send_max_ms')),
        ("max_bulk_in_flight={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'in_flight')),
        ("max_bulk_in_flight_bytes={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'in_flight_bytes')),
        ("max_bulk_in_flight_summary={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'in_flight_max')),
        ("max_bulk_in_flight_bytes_summary={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'in_flight_bytes_max')),
        ("max_bulk_configured_concurrency={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $states -FieldName 'configured_concurrency'), (Get-FileTransferMaxField -Events $summaries -FieldName 'configured_concurrency')))),
        ("max_bulk_effective_concurrency={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $states -FieldName 'effective_concurrency'), (Get-FileTransferMaxField -Events $summaries -FieldName 'effective_concurrency')))),
        ("max_bulk_worker_utilization_percent={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'worker_utilization_percent')),
        ("bulk_worker_idle_slot_samples={0}" -f (Get-FileTransferSumField -Events $summaries -FieldName 'worker_idle_slot_samples')),
        ("max_bulk_worker_saturation_percent={0}" -f (Get-FileTransferMaxField -Events $summaries -FieldName 'worker_saturation_percent')),
        ("bulk_drain_wake_count={0}" -f (Get-FileTransferSumField -Events $summaries -FieldName 'drain_wake_count')),
        ("max_bulk_queue_depth={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'queue_depth')),
        ("max_bulk_queued_bytes={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'queued_bytes')),
        ("max_bulk_oldest_queued_age_ms={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'oldest_queued_age_ms')),
        '',
        'bridge_bulk_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $events -Limit 40)
}

function New-FileTransferBridgeConfigSummaryLines {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string]$ExternalTopologyProfile = ''
    )

    $snapshot = Get-FileTransferBridgeConfigSnapshot -Summary $Summary -ExternalTopologyProfile $ExternalTopologyProfile
    $overrideEvidence = if ($snapshot.OverrideEvidence.Count -gt 0) { $snapshot.OverrideEvidence } else { @('(none)') }

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("bridge_config_status={0}" -f $snapshot.Status),
        ("external_topology_profile={0}" -f $snapshot.ExternalTopologyProfile),
        ("expected_topology={0}" -f $snapshot.ExpectedTopology),
        ("observed_topology={0}" -f $snapshot.ObservedTopology),
        ("expected_bulk_send_concurrency={0}" -f $snapshot.ExpectedBulkSendConcurrency),
        ("observed_bulk_send_concurrency={0}" -f $snapshot.ObservedBulkSendConcurrency),
        ("expected_bulk_send_mode={0}" -f $snapshot.ExpectedBulkSendMode),
        ("observed_bulk_send_mode={0}" -f $snapshot.ObservedBulkSendMode),
        ("settings_match_expected={0}" -f ($(if ($snapshot.SettingsMatchExpected) { 1 } else { 0 }))),
        ("diagnostic_profile={0}" -f ($(if ($snapshot.DiagnosticProfile) { 1 } else { 0 }))),
        ("bridge_health_summary_count={0}" -f $snapshot.HealthSummaryCount),
        ("bridge_bundle_loaded_count={0}" -f $snapshot.BundleLoadedCount),
        ("bridge_script_path={0}" -f $snapshot.BridgeScriptPath),
        ("bridge_script_sha256={0}" -f $snapshot.BridgeScriptSha256),
        ("manifest_status={0}" -f $snapshot.ManifestStatus),
        ("manifest_app_version={0}" -f $snapshot.ManifestAppVersion),
        ("node_version={0}" -f $snapshot.NodeVersion),
        '',
        'bridge_override_evidence:'
    ) + $overrideEvidence + @(
        '',
        'bridge_config_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $snapshot.EvidenceEvents -Limit 40)
}

function New-FileTransferCoexistenceSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $transferEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_clamped_for_screenshare', 'filetransfer_recovered_after_screenshare'))
    $mediaEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'screenshare_bridge_media_send_summary' -or $_.EventName -eq 'screenshare_bridge_queue_state' })
    $drops = 0L
    $failures = 0L
    foreach ($event in @($mediaEvents)) {
        $drops += Get-FileTransferEventInt64Field -Event $event -Name 'queue_drops' -Default 0
        $failures += Get-FileTransferEventInt64Field -Event $event -Name 'send_failures' -Default 0
    }

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("clamped_for_screenshare_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_clamped_for_screenshare')),
        ("recovered_after_screenshare_count={0}" -f (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_recovered_after_screenshare')),
        ("media_summary_count={0}" -f (@($mediaEvents | Where-Object { $_.EventName -eq 'screenshare_bridge_media_send_summary' }).Count)),
        ("media_queue_drop_count={0}" -f $drops),
        ("media_send_failure_count={0}" -f $failures),
        ("media_queue_severe_count={0}" -f (@($mediaEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'severe' -Default 0) -gt 0 -or (Get-FileTransferEventField -Event $_ -Name 'queue_mode' -Default 'normal') -eq 'severe' }).Count)),
        '',
        'coexistence_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events (@($transferEvents + $mediaEvents | Sort-Object Sequence)) -Limit 40)
}

function New-FileTransferExternalTransportHealthSummaryLines {
    param([Parameter(Mandatory = $true)]$Summary)

    $events = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'screenshare_bridge_transport_health_summary' })
    $disconnects = 0L
    $connectFailures = 0L
    $wsErrors = 0L
    $rpcFallbacks = 0L
    $messagesReceived = 0L
    $bytesReceived = 0L
    $zeroReceiveWindows = 0L
    $readyZeroReceiveWindows = 0L
    $readySendingZeroReceiveWindows = 0L
    $inboundDeliveryEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_inbound_delivery_summary' })
    $inboundDeliveryFailedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_inbound_delivery_failed' })
    $inboundEnvelopeReceivedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_inbound_envelope_received' })
    $inboundEnvelopeDropEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_inbound_envelope_drop' })
    $secureFileTransferDataEnvelopeReceivedEvents = @(@($Summary.GlobalEvents + $Summary.TransferEvents) | Where-Object {
        $_.EventName -eq 'filetransfer_envelope_received' -and
        (Get-FileTransferEventField -Event $_ -Name 'message_type' -Default '') -eq 'file_transfer_data_frame'
    })
    $fileTransferDataFrameDispatchedEvents = @(@($Summary.GlobalEvents + $Summary.TransferEvents) | Where-Object {
        $_.EventName -eq 'filetransfer_data_frame_dispatched'
    })
    $receiveStallDetectedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_detected' })
    $receiveStallRecoveryStartedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_started' })
    $receiveStallRecoveryCompletedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_completed' })
    $receiveStallRecoveryFailedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_failed' })
    $receiveStallRecoveryCooldownBypassedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_cooldown_bypassed' })
    $receiveStallRecoveryUnprovenEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_unproven' })
    $receiveStallRecoveryReceiveResumedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_receive_resumed' })
    $controlReceiveDegradedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_control_receive_degraded' })
    $controlReceiveRecoverySuppressedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed' })
    $receiveLivenessEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'filetransfer_v4_receive_liveness_summary' })
    $reorderPolicyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_reorder_policy_decision'))
    $grantSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_grant_window_summary'))
    foreach ($event in @($events)) {
        $disconnects += Get-FileTransferEventInt64Field -Event $event -Name 'disconnect_count_since_last' -Default 0
        $connectFailures += Get-FileTransferEventInt64Field -Event $event -Name 'connect_failed_count_since_last' -Default 0
        $wsErrors += Get-FileTransferEventInt64Field -Event $event -Name 'ws_error_count_since_last' -Default 0
        $rpcFallbacks += Get-FileTransferEventInt64Field -Event $event -Name 'rpc_fallback_attempt_count_since_last' -Default 0
        $totalMessages = Get-FileTransferEventInt64Field -Event $event -Name 'total_messages_received_since_last' -Default (
            (Get-FileTransferEventInt64Field -Event $event -Name 'control_messages_received_since_last' -Default 0) +
            (Get-FileTransferEventInt64Field -Event $event -Name 'media_messages_received_since_last' -Default 0) +
            (Get-FileTransferEventInt64Field -Event $event -Name 'bulk_messages_received_since_last' -Default 0))
        $totalBytes = Get-FileTransferEventInt64Field -Event $event -Name 'total_bytes_received_since_last' -Default (
            (Get-FileTransferEventInt64Field -Event $event -Name 'control_bytes_received_since_last' -Default 0) +
            (Get-FileTransferEventInt64Field -Event $event -Name 'media_bytes_received_since_last' -Default 0) +
            (Get-FileTransferEventInt64Field -Event $event -Name 'bulk_bytes_received_since_last' -Default 0))
        $messagesReceived += $totalMessages
        $bytesReceived += $totalBytes
        if ($totalMessages -eq 0) {
            $zeroReceiveWindows += 1
            $ready = (Get-FileTransferEventInt64Field -Event $event -Name 'control_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $event -Name 'media_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $event -Name 'bulk_ready' -Default 0) -gt 0
            if ($ready) {
                $readyZeroReceiveWindows += 1
                if ((Get-FileTransferEventInt64Field -Event $event -Name 'frames_sent_since_last' -Default 0) -gt 0) {
                    $readySendingZeroReceiveWindows += 1
                }
            }
        }
    }

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("transport_health_summary_count={0}" -f $events.Count),
        ("disconnect_count={0}" -f $disconnects),
        ("connect_failed_count={0}" -f $connectFailures),
        ("ws_error_count={0}" -f $wsErrors),
        ("rpc_fallback_attempt_count={0}" -f $rpcFallbacks),
        ("messages_received_since_last_total={0}" -f $messagesReceived),
        ("bytes_received_since_last_total={0}" -f $bytesReceived),
        ("zero_receive_window_count={0}" -f $zeroReceiveWindows),
        ("ready_zero_receive_window_count={0}" -f $readyZeroReceiveWindows),
        ("ready_sending_zero_receive_window_count={0}" -f $readySendingZeroReceiveWindows),
        ("receive_stall_detected_count={0}" -f $receiveStallDetectedEvents.Count),
        ("receive_stall_reason.all_channels_zero_receive_count={0}" -f (@($receiveStallDetectedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'all_channels_zero_receive' }).Count)),
        ("receive_stall_reason.bulk_receive_stalled_count={0}" -f (@($receiveStallDetectedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'bulk_receive_stalled' }).Count)),
        ("receive_stall_reason.control_receive_stalled_count={0}" -f (@($receiveStallDetectedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'control_receive_stalled' }).Count)),
        ("receive_stall_recovery_started_count={0}" -f $receiveStallRecoveryStartedEvents.Count),
        ("receive_stall_recovery_completed_count={0}" -f $receiveStallRecoveryCompletedEvents.Count),
        ("receive_stall_recovery_failed_count={0}" -f $receiveStallRecoveryFailedEvents.Count),
        ("receive_stall_recovery_cooldown_bypassed_count={0}" -f $receiveStallRecoveryCooldownBypassedEvents.Count),
        ("receive_stall_recovery_unproven_count={0}" -f $receiveStallRecoveryUnprovenEvents.Count),
        ("receive_stall_recovery_receive_resumed_count={0}" -f $receiveStallRecoveryReceiveResumedEvents.Count),
        ("control_receive_degraded_count={0}" -f $controlReceiveDegradedEvents.Count),
        ("control_receive_recovery_suppressed_count={0}" -f $controlReceiveRecoverySuppressedEvents.Count),
        ("control_receive_recovery_suppressed_bulk_active_count={0}" -f (@($controlReceiveRecoverySuppressedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -in @('bulk_receive_active', 'filetransfer_bulk_receive_active') }).Count)),
        ("control_receive_recovery_suppressed_bulk_fresh_count={0}" -f (@($controlReceiveRecoverySuppressedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'filetransfer_bulk_receive_fresh' }).Count)),
        ("control_receive_recovery_suppressed_bulk_not_idle_count={0}" -f (@($controlReceiveRecoverySuppressedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -in @('bulk_not_idle', 'filetransfer_bulk_not_idle') }).Count)),
        ("max_receive_stall_recovery_resume_after_ms={0}" -f (Get-FileTransferMaxField -Events $receiveStallRecoveryReceiveResumedEvents -FieldName 'resume_after_recovery_ms')),
        ("inbound_delivery_summary_count={0}" -f $inboundDeliveryEvents.Count),
        ("inbound_delivery_bulk_messages={0}" -f (Get-FileTransferSumField -Events (@($inboundDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'channel' -Default '') -eq 'bulk' })) -FieldName 'messages')),
        ("inbound_delivery_subscriber_missing_count={0}" -f (Get-FileTransferSumField -Events $inboundDeliveryEvents -FieldName 'subscriber_missing_count')),
        ("inbound_delivery_handler_failure_count={0}" -f ((Get-FileTransferSumField -Events $inboundDeliveryEvents -FieldName 'handler_failure_count') + $inboundDeliveryFailedEvents.Count)),
        ("inbound_delivery_source_matches_any_local_count={0}" -f (Get-FileTransferSumField -Events $inboundDeliveryEvents -FieldName 'source_matches_any_local_count')),
        ("inbound_envelope_received_count={0}" -f $inboundEnvelopeReceivedEvents.Count),
        ("inbound_bulk_envelope_received_count={0}" -f (@($inboundEnvelopeReceivedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'channel' -Default '') -eq 'bulk' }).Count)),
        ("inbound_filetransfer_data_frame_envelope_received_count={0}" -f (@($inboundEnvelopeReceivedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'envelope_type' -Default '') -eq 'file_transfer_data_frame' }).Count)),
        ("filetransfer_secure_data_frame_envelope_received_count={0}" -f $secureFileTransferDataEnvelopeReceivedEvents.Count),
        ("filetransfer_data_frame_dispatched_count={0}" -f $fileTransferDataFrameDispatchedEvents.Count),
        ("filetransfer_data_frame_dispatch_missing_count={0}" -f ($(if ($secureFileTransferDataEnvelopeReceivedEvents.Count -gt 0 -and $fileTransferDataFrameDispatchedEvents.Count -eq 0) { 1 } else { 0 }))),
        ("inbound_envelope_drop_count={0}" -f $inboundEnvelopeDropEvents.Count),
        ("inbound_envelope_drop_parse_failed_count={0}" -f (@($inboundEnvelopeDropEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'parse_failed' }).Count)),
        ("inbound_envelope_drop_duplicate_count={0}" -f (@($inboundEnvelopeDropEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'duplicate' }).Count)),
        ("inbound_envelope_drop_self_source_count={0}" -f (@($inboundEnvelopeDropEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'self_source' }).Count)),
        ("max_control_last_received_age_ms={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'control_last_received_age_ms')),
        ("max_media_last_received_age_ms={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'media_last_received_age_ms')),
        ("max_bulk_last_received_age_ms={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'bulk_last_received_age_ms')),
        ("max_total_messages_received_since_last={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'total_messages_received_since_last')),
        ("max_total_bytes_received_since_last={0}" -f (Get-FileTransferMaxField -Events $events -FieldName 'total_bytes_received_since_last')),
        '',
        'external_transport_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events (@($events + $inboundDeliveryEvents + $inboundDeliveryFailedEvents + $inboundEnvelopeReceivedEvents + $inboundEnvelopeDropEvents + $receiveStallDetectedEvents + $receiveStallRecoveryStartedEvents + $receiveStallRecoveryCompletedEvents + $receiveStallRecoveryFailedEvents + $receiveStallRecoveryCooldownBypassedEvents + $receiveStallRecoveryReceiveResumedEvents + $controlReceiveDegradedEvents + $controlReceiveRecoverySuppressedEvents | Sort-Object Sequence)) -Limit 60)
}

function Resolve-FileTransferThroughputLimiter {
    param(
        [object[]]$SenderEvents,
        [object[]]$ReceiverEvents,
        [object[]]$GapStallEvents,
        [object[]]$BridgeBulkEvents,
        [object[]]$ExternalHealthEvents,
        [Parameter(Mandatory = $true)]$Summary,
        [string]$ArtifactDir = ''
    )

    $explicitReceiveStallEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_detected' })
    $explicitReceiveStallCount = @($explicitReceiveStallEvents | Where-Object {
        $reason = Get-FileTransferEventField -Event $_ -Name 'reason' -Default ''
        [string]::IsNullOrWhiteSpace($reason) -or $reason -ne 'control_receive_stalled'
    }).Count
    $preSampleReceiveStallCount = @(
        $ExternalHealthEvents |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'total_messages_received_since_last' -Default '')) -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'control_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'media_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'bulk_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'frames_sent_since_last' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'total_messages_received_since_last' -Default 1) -eq 0
            }
    ).Count

    if ($explicitReceiveStallCount -gt 0) {
        return 'external_transport_limited'
    }

    $allEvents = @($Summary.GlobalEvents + $Summary.TransferEvents)
    $secureDataEnvelopeReceivedEvents = @($allEvents | Where-Object {
        $_.EventName -eq 'filetransfer_envelope_received' -and
        (Get-FileTransferEventField -Event $_ -Name 'message_type' -Default '') -eq 'file_transfer_data_frame'
    })
    $transportDataEnvelopeReceivedEvents = @($Summary.GlobalEvents | Where-Object {
        $_.EventName -eq 'nkn_inbound_envelope_received' -and
        (Get-FileTransferEventField -Event $_ -Name 'envelope_type' -Default '') -eq 'file_transfer_data_frame'
    })
    $dataFrameDispatchedEvents = @($allEvents | Where-Object { $_.EventName -eq 'filetransfer_data_frame_dispatched' })
    if ($Summary.LiveProgressTimeoutCount -gt 0 -and
        ($secureDataEnvelopeReceivedEvents.Count -gt 0 -or $transportDataEnvelopeReceivedEvents.Count -gt 0) -and
        $dataFrameDispatchedEvents.Count -eq 0) {
        return 'filetransfer_data_session_dispatch_missing'
    }

    $v4SenderPumpEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_pump_summary'))
    $v4StateSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_sent'))
    $v4StateReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_received'))
    $v4ChunkBatchSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_chunk_batch_sent'))
    $v4ChunkBatchReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_chunk_batch_received'))
    $v4RepairRequestedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_requested'))
    $v4RepairScheduledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_scheduled'))
    $v4RepairSuppressedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_suppressed'))
    $v4RepairSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_sent'))
    $v4RepairObservedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_chunk_observed'))
    $v4RepairFilledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_filled'))
    $v4RepairBatchEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch') |
        Where-Object { (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -like 'v4_repair_*' })
    $v4FrontierTailRepairDueEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_due'))
    $v4FrontierTailRepairSuppressedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_suppressed'))
    $v4FrontierTailRepairFilledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_filled'))
    $v4CompleteEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_complete_sent', 'filetransfer_v4_complete_received'))
    $v4FailureEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_failed', 'filetransfer_v4_receiver_failed', 'filetransfer_v4_feedback_both_failed'))
    $v4EvidenceCount = (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_negotiated') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_sender_started') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_receiver_started') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_negotiated') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_sender_started') +
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_receiver_started') +
        $v4SenderPumpEvents.Count +
        $v4StateSentEvents.Count +
        $v4StateReceivedEvents.Count +
        $v4ChunkBatchSentEvents.Count +
        $v4ChunkBatchReceivedEvents.Count
    if ($v4EvidenceCount -gt 0) {
        $v4CycleStats = Get-FileTransferCycleGoodputStats -ArtifactDir $ArtifactDir
        $v4CycleGoodputAverage = 0D
        [double]::TryParse(
            [string]$v4CycleStats.Average,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$v4CycleGoodputAverage) | Out-Null
        $v4MaxBridgePayloadBps = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'payload_bytes_per_second'
        $v4ObservedGoodputBytesPerSecond = if ($v4CycleStats.Count -gt 0 -and $v4CycleGoodputAverage -gt 0D) {
            $v4CycleGoodputAverage
        }
        else {
            [double]$v4MaxBridgePayloadBps
        }
        $v4TargetGoodputBytesPerSecond = $script:FileTransferRegularNknTargetGoodputBytesPerSecond
        $v4BridgeFailures = @($BridgeBulkEvents | Where-Object {
            ($_.EventName -eq 'nkn_bridge_bulk_send_summary' -and
                ((Get-FileTransferEventInt64Field -Event $_ -Name 'send_failures' -Default 0) -gt 0 -or
                 (Get-FileTransferEventInt64Field -Event $_ -Name 'queue_clears' -Default 0) -gt 0)) -or
            ($_.EventName -eq 'nkn_bridge_bulk_queue_state' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'cleared_since_last' -Default 0) -gt 0)
        }).Count
        $v4MaxBridgeInFlight = [Math]::Max(
            (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'in_flight'),
            (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'in_flight_max'))
        $v4MaxBridgeConfiguredConcurrency = [Math]::Max(
            (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'configured_concurrency'),
            (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'effective_concurrency'))
        $v4MaxBridgeWorkerUtilizationPercent = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'worker_utilization_percent'
        $v4MaxBridgeQueueDepth = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'queue_depth'
        $v4MaxBridgeSendP95Ms = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'send_p95_ms'
        $v4MaxBridgeWorkerSaturationPercent = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'worker_saturation_percent'
        $v4MaxPumpInFlight = Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'in_flight_frames'
        $v4MaxPumpAvailableCreditBytes = Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'available_credit_bytes'
        $v4CreditExhaustedSummaryCount = @($v4SenderPumpEvents | Where-Object {
            (Get-FileTransferEventInt64Field -Event $_ -Name 'credit_exhausted_time_ms' -Default 0) -gt 0 -or
            (
                -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'available_credit_bytes' -Default '')) -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'available_credit_bytes' -Default 1) -eq 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'terminal_ready' -Default 0) -eq 0
            )
        }).Count
        $v4PumpScheduledFrames = Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'scheduled_frames'
        $v4PumpCompletedFrames = Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'completed_frames'
        $v4PayloadShapeEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch', 'filetransfer_transport_payload_budget') |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.chunk_batch.v6' -or
                (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.chunk_batch.v4' -or
                (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -eq 'v4_default_21k'
            })
        $v4MaxPayloadFillPercent = Get-FileTransferMaxDoubleField -Events $v4PayloadShapeEvents -FieldName 'bridge_payload_fill_percent'
        $v4CleanHardEvidence = $v4FailureEvents.Count -eq 0 -and
            $Summary.PayloadRejectedCount -eq 0 -and
            $Summary.DataFrameDecodeFailedCount -eq 0 -and
            $Summary.MessageRejectedCount -eq 0 -and
            $v4BridgeFailures -eq 0
        $v4MissingRangeDueStateMismatchCount = Get-FileTransferV4MissingRangeDueStateMismatchCount -DueEvents $v4FrontierTailRepairDueEvents -StateSentEvents $v4StateSentEvents
        $v4RepairObservedAcceptedChunks = Get-FileTransferSumField -Events $v4RepairObservedEvents -FieldName 'accepted_chunk_count'
        $v4RepairObservedFrontierAdvancedCount = @($v4RepairObservedEvents | Where-Object {
            (Get-FileTransferEventInt64Field -Event $_ -Name 'frontier_advanced' -Default 0) -gt 0
        }).Count
        $v4RepairRequestToFillP95Ms = Get-FileTransferPercentileField -Events $v4RepairFilledEvents -FieldName 'request_to_fill_ms' -Percentile 95
        $v4MaxFrontierStallAgeMs = Get-FileTransferMaxField -Events @($v4FrontierTailRepairDueEvents + $v4FrontierTailRepairSuppressedEvents) -FieldName 'frontier_stall_age_ms'
        $v4PumpRepairSendCount = Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'repair_send_count'

        if ($v4CleanHardEvidence -and
            $Summary.HasTerminalEvidence -and
            $v4ObservedGoodputBytesPerSecond -ge $v4TargetGoodputBytesPerSecond) {
            return 'v4_capacity_proven'
        }

        if ($v4MissingRangeDueStateMismatchCount -gt 0 -and $v4CleanHardEvidence) {
            return 'v4_missing_range_due_state_mismatch'
        }

        if ($v4StateSentEvents.Count -gt 0 -and
            $v4StateReceivedEvents.Count -eq 0 -and
            $v4CleanHardEvidence) {
            return 'v4_state_feedback_limited'
        }

        $v4TailStateWithoutMissingCount = @($v4StateSentEvents | Where-Object {
            $frontier = Get-FileTransferEventInt64Field -Event $_ -Name 'contiguous_committed_chunk_index' -Default 0
            $highest = Get-FileTransferEventInt64Field -Event $_ -Name 'durable_received_highest_chunk_index' -Default -1
            $credit = Get-FileTransferEventInt64Field -Event $_ -Name 'credit_until_chunk_index_exclusive' -Default 0
            (Get-FileTransferEventInt64Field -Event $_ -Name 'missing_range_count' -Default 0) -eq 0 -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'terminal_ready' -Default 0) -eq 0 -and
            $credit -gt $frontier -and
            $highest -lt $frontier
        }).Count

        if ($Summary.LiveProgressTimeoutCount -gt 0 -and
            $v4CleanHardEvidence -and
            $v4TailStateWithoutMissingCount -gt 0 -and
            $v4CreditExhaustedSummaryCount -gt 0 -and
            $v4RepairRequestedEvents.Count -eq 0 -and
            $v4FrontierTailRepairDueEvents.Count -eq 0) {
            return 'v4_frontier_tail_repair_needed'
        }

        $v4RepairScheduledKeys = @($v4RepairScheduledEvents | ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'repair_request_key' -Default '' } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $v4UniqueRepairScheduledKeyCount = @($v4RepairScheduledKeys | Sort-Object -Unique).Count
        if ($v4RepairScheduledEvents.Count -ge 3 -and
            $v4UniqueRepairScheduledKeyCount -gt 0 -and
            $v4UniqueRepairScheduledKeyCount -le [Math]::Max(1, [int][Math]::Floor($v4RepairScheduledEvents.Count / 2D)) -and
            $v4RepairScheduledEvents.Count -gt [Math]::Max(2, $v4RepairSentEvents.Count * 2) -and
            $v4CleanHardEvidence) {
            return 'v4_missing_range_repair_spam_limited'
        }

        if ($v4RepairRequestedEvents.Count -gt 0 -and
            $v4RepairScheduledEvents.Count -eq 0 -and
            $v4RepairSentEvents.Count -eq 0 -and
            $v4CleanHardEvidence) {
            return 'v4_repair_requested_not_received_by_sender'
        }

        if ($v4RepairSentEvents.Count -gt 0 -and
            $v4RepairFilledEvents.Count -eq 0 -and
            $v4CompleteEvents.Count -eq 0 -and
            $v4CleanHardEvidence) {
            if ($v4RepairObservedEvents.Count -eq 0) {
                return 'v4_repair_sent_not_observed_by_receiver'
            }

            if ($v4RepairObservedAcceptedChunks -eq 0) {
                return 'v4_repair_observed_but_not_accepted'
            }

            if ($v4RepairObservedFrontierAdvancedCount -eq 0) {
                return 'v4_repair_accepted_but_frontier_not_advanced'
            }

            if ($v4FrontierTailRepairDueEvents.Count -gt 0 -or
                @($v4RepairSentEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'frontier_tail_repair' -Default 0) -gt 0 }).Count -gt 0) {
                return 'v4_frontier_tail_repair_not_filled'
            }

            return 'v4_repair_sent_but_not_filled'
        }

        if ($v4RepairScheduledEvents.Count -gt 0 -and
            ($v4RepairSentEvents.Count -eq 0 -or $v4CompleteEvents.Count -eq 0) -and
            $v4CleanHardEvidence) {
            return 'v4_missing_range_repair_limited'
        }

        if ($v4CleanHardEvidence -and
            $Summary.HasTerminalEvidence -and
            $v4ObservedGoodputBytesPerSecond -gt 0 -and
            $v4ObservedGoodputBytesPerSecond -lt $v4TargetGoodputBytesPerSecond -and
            $v4RepairSentEvents.Count -gt 0 -and
            $v4RepairFilledEvents.Count -gt 0 -and
            ($v4RepairRequestToFillP95Ms -ge 1500 -or $v4MaxFrontierStallAgeMs -ge 2500) -and
            ($v4CreditExhaustedSummaryCount -gt 0 -or $v4PumpRepairSendCount -gt 0)) {
            return 'v4_missing_range_repair_limited'
        }

        if ($v4SenderPumpEvents.Count -gt 0 -and
            $v4ObservedGoodputBytesPerSecond -gt 0 -and
            $v4ObservedGoodputBytesPerSecond -lt $v4TargetGoodputBytesPerSecond -and
            $v4MaxPumpInFlight -le 1 -and
            $v4MaxPumpAvailableCreditBytes -gt 0 -and
            $v4PumpScheduledFrames -le [Math]::Max(1, $v4PumpCompletedFrames + 1) -and
            $v4CleanHardEvidence) {
            return 'v4_sender_pump_underfed'
        }

        if ($v4MaxBridgeConfiguredConcurrency -gt 1 -and
            $v4MaxBridgeQueueDepth -eq 0 -and
            $v4MaxBridgeSendP95Ms -ge 0 -and
            $v4MaxBridgeSendP95Ms -lt 50 -and
            $v4MaxBridgeWorkerUtilizationPercent -le 35 -and
            $v4MaxBridgeWorkerSaturationPercent -le 5 -and
            $v4MaxPayloadFillPercent -ge 90D -and
            $v4ObservedGoodputBytesPerSecond -gt 0 -and
            $v4ObservedGoodputBytesPerSecond -lt $v4TargetGoodputBytesPerSecond -and
            $v4CleanHardEvidence) {
            return 'nkn_bulk_underutilized'
        }

        if ($preSampleReceiveStallCount -ge 2) {
            return 'external_transport_limited'
        }

        return 'inconclusive'
    }

    if ($SenderEvents.Count -eq 0 -or $ReceiverEvents.Count -eq 0 -or $BridgeBulkEvents.Count -eq 0) {
        return 'inconclusive'
    }

    $maxSenderRawBps = Get-FileTransferMaxField -Events $SenderEvents -FieldName 'raw_bytes_per_second'
    $maxReceiverRawBps = Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'raw_bytes_received_per_second'
    $maxCommitBps = Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'contiguous_bytes_committed_per_second'
    $maxGapAgeMs = [Math]::Max(
        (Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'oldest_gap_age_ms'),
        (Get-FileTransferMaxField -Events $GapStallEvents -FieldName 'stall_duration_ms'))
    $maxWriteDurationMs = Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'write_duration_ms'
    $maxSampleWindowMs = Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'sample_window_ms'
    $maxBridgeQueueDepth = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'queue_depth'
    $maxBridgeQueuedBytes = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'queued_bytes'
    $maxBridgeOldestQueuedAgeMs = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'oldest_queued_age_ms'
    $maxBridgeSendP95Ms = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'send_p95_ms'
    $maxBridgeInFlight = [Math]::Max(
        (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'in_flight'),
        (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'in_flight_max'))
    $maxBridgeConfiguredConcurrency = [Math]::Max(
        (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'configured_concurrency'),
        (Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'effective_concurrency'))
    $maxBridgeWorkerUtilizationPercent = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'worker_utilization_percent'
    $maxBridgeWorkerSaturationPercent = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'worker_saturation_percent'
    $maxBridgePayloadBps = Get-FileTransferMaxField -Events $BridgeBulkEvents -FieldName 'payload_bytes_per_second'
    $maxSenderWindowBytes = Get-FileTransferMaxField -Events $SenderEvents -FieldName 'remote_granted_window_bytes'
    $maxReceiverPendingBytes = Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'pending_bytes'
    $maxSparseMode = Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'sparse_mode'
    $maxSparseWriteBps = [Math]::Max(
        (Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'sparse_write_bytes_per_second'),
        $Summary.MaxReceiverSparseWriteBytesPerSecond)
    $senderFeedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_feed_summary'))
    $senderGrantApplyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_grant_apply_summary'))
    $senderCreditStallEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_credit_stall_summary'))
    $receiverGrantDecisionEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_grant_decision_summary'))
    $receiverFeedbackEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_feedback_pump_started', 'filetransfer_v4_receiver_feedback_enqueued', 'filetransfer_v4_receiver_feedback_coalesced', 'filetransfer_v4_receiver_feedback_sent', 'filetransfer_v4_receiver_feedback_summary', 'filetransfer_v4_receiver_feedback_failed'))
    $senderFeedDurationMs = Get-FileTransferActiveSampleDurationMs -Events $senderFeedEvents -ActivityFieldName 'raw_bytes_prepared'
    $senderFeedPrepareDurationMs = (Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'read_duration_ms') +
        (Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'batch_prepare_duration_ms') +
        (Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'send_async_schedule_duration_ms')
    $senderFeedCreditWaitMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'credit_wait_duration_ms'
    $senderFeedPipelineSlotWaitMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'pipeline_slot_wait_duration_ms'
    $senderCreditWaitRatioHigh = $senderFeedEvents.Count -gt 0 -and
        $senderFeedDurationMs -gt 0 -and
        $senderFeedCreditWaitMs -ge [Math]::Max(1, [long]($senderFeedDurationMs / 4))
    $maxReceiverFeedbackSendDurationMs = [Math]::Max(
        $Summary.MaxReceiverFeedbackSendDurationMs,
        $Summary.MaxReceiverFeedbackSummarySendDurationMs)
    $maxReceiverFeedbackEnqueueToSendAgeMs = [Math]::Max(
        $Summary.MaxReceiverFeedbackEnqueueToSendAgeMs,
        $Summary.MaxReceiverFeedbackSummaryEnqueueToSendAgeMs)
    $receiverFeedbackDirectSentCount = @($receiverFeedbackEvents | Where-Object {
        $_.EventName -eq 'filetransfer_v4_receiver_feedback_sent' -and
        (Get-FileTransferEventField -Event $_ -Name 'mode' -Default '') -eq 'direct'
    }).Count
    $receiverFeedbackPumpModeEventCount = @($receiverFeedbackEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'mode' -Default '') -eq 'pump'
    }).Count
    $receiverFeedbackPumpActive = $Summary.ReceiverFeedbackPumpStartedCount -gt 0 -or $receiverFeedbackPumpModeEventCount -gt 0
    $grantSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_grant_window_summary'))
    $sparseCreditStats = Get-FileTransferSparseCreditStats -GrantEvents $grantSummaryEvents
    $reorderPolicyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_reorder_policy_decision'))
    $proactiveFrontierRepairEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_frontier_gap_repair_eligible', 'filetransfer_frontier_gap_repair_requested', 'filetransfer_frontier_gap_repair_skipped', 'filetransfer_frontier_gap_repair_suppressed', 'filetransfer_frontier_gap_repair_sender_received', 'filetransfer_frontier_gap_repair_sender_scheduled', 'filetransfer_frontier_gap_repair_sender_sent', 'filetransfer_frontier_gap_repair_filled', 'filetransfer_proactive_frontier_repair_state_reset'))
    $proactiveRepairPressureStats = Get-FileTransferProactiveRepairPressureStats -Events @($reorderPolicyEvents + $grantSummaryEvents + $proactiveFrontierRepairEvents)
    $proactiveFrontierRepairSkippedEvents = @($proactiveFrontierRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_frontier_gap_repair_skipped' })
    $proactiveFrontierRepairResetEvents = @($proactiveFrontierRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_proactive_frontier_repair_state_reset' })
    $benignGapSkipLimitedPolicyCount = @($proactiveFrontierRepairSkippedEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' -and
        (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '(none)') -eq '(none)' -and
        (Get-FileTransferEventField -Event $_ -Name 'grant_policy_after_repair' -Default '') -eq 'healthy_limited'
    }).Count
    $softLimitedReorderDecisionCount = @($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'soft_limited' }).Count
    $limitedReorderDecisionCount = @($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'limited' }).Count
    $softLimitedGrantProfileCount = @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_file_only_soft_limited' }).Count
    $limitedGrantProfileCount = @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_limited' }).Count
    $stickyLimitedWithoutPressureCount = @($grantSummaryEvents | Where-Object {
        $limitedRecoveryCleanMs = Get-FileTransferEventInt64Field -Event $_ -Name 'limited_recovery_clean_ms' -Default 0
        $limitedRecoveryHoldMs = Get-FileTransferEventInt64Field -Event $_ -Name 'limited_recovery_hold_ms' -Default 750
        (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_limited' -and
        $limitedRecoveryCleanMs -ge $limitedRecoveryHoldMs -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'limited_recovery_block_reason' -Default '') -eq '(none)' -or
            (
                [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'limited_recovery_block_reason' -Default '')) -and
                (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '(none)') -eq '(none)' -and
                (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -ne 'gap_stall' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'late_arrival_distance' -Default 0) -lt 64 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'pending_bytes' -Default 0) -lt (4 * 1024 * 1024)
            )
        )
    }).Count
    $grantBaseGapStallCount = @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'gap_stall' }).Count
    $maxSparseReorderDistance = [Math]::Max(
        (Get-FileTransferMaxField -Events $ReceiverEvents -FieldName 'late_arrival_distance'),
        (Get-FileTransferMaxField -Events $reorderPolicyEvents -FieldName 'late_arrival_distance'))
    $controlReceiveDegradedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_control_receive_degraded' })
    $controlDegradedBulkActiveCount = @($controlReceiveDegradedEvents | Where-Object {
        (Get-FileTransferEventInt64Field -Event $_ -Name 'bulk_messages_received_since_last' -Default 0) -gt 0 -or
        (Get-FileTransferEventInt64Field -Event $_ -Name 'total_messages_received_since_last' -Default 0) -gt 0
    }).Count
    $cycleStats = Get-FileTransferCycleGoodputStats -ArtifactDir $ArtifactDir
    $cycleGoodputAverage = 0D
    [double]::TryParse(
        [string]$cycleStats.Average,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$cycleGoodputAverage) | Out-Null
    $targetGoodputBytesPerSecond = $script:FileTransferRegularNknTargetGoodputBytesPerSecond
    $observedGoodputBytesPerSecond = if ($cycleStats.Count -gt 0 -and $cycleGoodputAverage -gt 0D) {
        $cycleGoodputAverage
    }
    else {
        [Math]::Min(
            [double]$maxSenderRawBps,
            [Math]::Min([double]$maxReceiverRawBps, [double]$maxBridgePayloadBps))
    }
    $maxTargetWindowBytes = Get-FileTransferMaxField -Events $grantSummaryEvents -FieldName 'target_window_bytes'
    $averageEffectiveGrantWindowBytes = Get-FileTransferAverageDoubleField -Events $grantSummaryEvents -FieldName 'effective_granted_window_bytes'
    $maxFixedFileOnlyWindowBytes = Get-FileTransferMaxField -Events $grantSummaryEvents -FieldName 'fixed_file_only_window_bytes'
    $fixedFileOnlyWindowActiveCount = @($grantSummaryEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'fixed_file_only_window_active' -Default 0) -gt 0 }).Count
    $fileOnlySparse16MiBWindowActive =
        $maxTargetWindowBytes -ge (15 * 1024 * 1024) -or
        $averageEffectiveGrantWindowBytes -ge (14 * 1024 * 1024) -or
        ($fixedFileOnlyWindowActiveCount -gt 0 -and $maxFixedFileOnlyWindowBytes -ge (15 * 1024 * 1024))
    $bridgeBulkFailureCount = @($BridgeBulkEvents | Where-Object {
        ($_.EventName -eq 'nkn_bridge_bulk_send_summary' -and
            ((Get-FileTransferEventInt64Field -Event $_ -Name 'send_failures' -Default 0) -gt 0 -or
             (Get-FileTransferEventInt64Field -Event $_ -Name 'queue_clears' -Default 0) -gt 0)) -or
        ($_.EventName -eq 'nkn_bridge_bulk_queue_state' -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'cleared_since_last' -Default 0) -gt 0)
    }).Count
    $cleanWindowCapacityEvidence =
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0 -and
        $Summary.ReceiverBufferPressureEnteredCount -eq 0 -and
        $Summary.PayloadRejectedCount -eq 0 -and
        $Summary.DataFrameDecodeFailedCount -eq 0 -and
        $bridgeBulkFailureCount -eq 0 -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeQueuedBytes -eq 0
    $payloadShapeEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch', 'filetransfer_transport_payload_budget'))
    $maxPayloadFillPercent = 0D
    foreach ($event in @($payloadShapeEvents)) {
        $fill = Get-FileTransferEventDoubleField -Event $event -Name 'bridge_payload_fill_percent' -Default 0
        if ($fill -gt $maxPayloadFillPercent) {
            $maxPayloadFillPercent = $fill
        }
    }
    $externalIssueCount = @(
        $ExternalHealthEvents |
            Where-Object {
                (Get-FileTransferEventInt64Field -Event $_ -Name 'disconnect_count_since_last' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $_ -Name 'connect_failed_count_since_last' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $_ -Name 'ws_error_count_since_last' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $_ -Name 'rpc_fallback_attempt_count_since_last' -Default 0) -gt 0
            }
    ).Count
    $externalReceiveStallCount = @(
        $ExternalHealthEvents |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'total_messages_received_since_last' -Default '')) -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'control_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'media_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'bulk_ready' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'frames_sent_since_last' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'total_messages_received_since_last' -Default 1) -eq 0
            }
    ).Count

    $bridgeCongested = $maxBridgeQueueDepth -ge 64 -or
        $maxBridgeQueuedBytes -ge (4 * 1024 * 1024) -or
        $maxBridgeOldestQueuedAgeMs -ge 250 -or
        $maxBridgeSendP95Ms -ge 250
    if ($bridgeCongested) {
        return 'bridge_bulk_limited'
    }

    if ($Summary.LiveProgressTimeoutCount -gt 0 -and
        -not $Summary.HasTerminalEvidence -and
        ($maxSparseMode -gt 0 -or $Summary.ReceiverSparseModeSelectedCount -gt 0 -or $Summary.ReceiverSparseWriteSummaryCount -gt 0) -and
        $GapStallEvents.Count -gt 0 -and
        $maxGapAgeMs -ge 10000 -and
        $Summary.ProactiveFrontierRepairRequestedCount -gt 1 -and
        $Summary.PayloadRejectedCount -eq 0 -and
        $Summary.DataFrameDecodeFailedCount -eq 0 -and
        $bridgeBulkFailureCount -eq 0 -and
        $Summary.ReceiverBufferPressureEnteredCount -eq 0 -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0 -and
        $externalReceiveStallCount -eq 0) {
        return 'sparse_frontier_gap_repair_stalled'
    }

    if ($Summary.ProactiveFrontierRepairRequestedCount -gt 0 -and
        $Summary.ProactiveFrontierRepairSenderReceivedCount -eq 0 -and
        ($maxSparseMode -gt 0 -or $Summary.ReceiverSparseModeSelectedCount -gt 0) -and
        ($maxGapAgeMs -ge 750 -or $senderCreditWaitRatioHigh) -and
        $limitedReorderDecisionCount -eq 0 -and
        $limitedGrantProfileCount -eq 0 -and
        $proactiveRepairPressureStats.RepeatedUnfilledCount -eq 0 -and
        $proactiveRepairPressureStats.HardLimitedCount -eq 0 -and
        $proactiveRepairPressureStats.HardLimitedDuringGraceCount -eq 0 -and
        $Summary.PayloadRejectedCount -eq 0 -and
        $Summary.DataFrameDecodeFailedCount -eq 0 -and
        $bridgeBulkFailureCount -eq 0 -and
        $Summary.ReceiverBufferPressureEnteredCount -eq 0 -and
        $externalReceiveStallCount -eq 0) {
        return 'frontier_repair_request_not_served'
    }

    if ($Summary.ProactiveFrontierRepairSenderSentCount -gt 0 -and
        $Summary.ProactiveFrontierRepairFilledCount -eq 0 -and
        ($maxSparseMode -gt 0 -or $Summary.ReceiverSparseModeSelectedCount -gt 0) -and
        ($maxGapAgeMs -ge 1500 -or $senderCreditWaitRatioHigh -or $Summary.LiveProgressTimeoutCount -gt 0) -and
        $proactiveRepairPressureStats.RepeatedUnfilledCount -eq 0 -and
        $proactiveRepairPressureStats.HardLimitedCount -eq 0 -and
        $proactiveRepairPressureStats.HardLimitedDuringGraceCount -eq 0 -and
        $Summary.PayloadRejectedCount -eq 0 -and
        $Summary.DataFrameDecodeFailedCount -eq 0 -and
        $bridgeBulkFailureCount -eq 0 -and
        $Summary.ReceiverBufferPressureEnteredCount -eq 0) {
        return 'frontier_repair_sent_but_not_filled'
    }

    if ($fileOnlySparse16MiBWindowActive -and
        $cleanWindowCapacityEvidence -and
        $observedGoodputBytesPerSecond -ge $targetGoodputBytesPerSecond) {
        return 'file_only_sparse_window_capacity_proven'
    }

    if ($senderCreditWaitRatioHigh -and
        $stickyLimitedWithoutPressureCount -gt 0 -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0 -and
        $Summary.ReceiverBufferPressureEnteredCount -eq 0 -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeQueuedBytes -eq 0) {
        return 'sticky_limited_without_pressure'
    }

    if ($senderCreditWaitRatioHigh -and
        $maxGapAgeMs -ge 1500 -and
        ($maxSparseMode -gt 0 -or $Summary.ReceiverSparseModeSelectedCount -gt 0) -and
        $maxSparseWriteBps -gt 0 -and
        $Summary.ProactiveFrontierRepairRequestedCount -eq 0 -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0 -and
        $maxBridgeQueueDepth -eq 0 -and
        ($grantBaseGapStallCount -gt 0 -or $limitedReorderDecisionCount -gt 0 -or $limitedGrantProfileCount -gt 0)) {
        return 'sparse_frontier_gap_unrepaired_limited'
    }

    if ($senderCreditWaitRatioHigh -and
        $Summary.ProactiveFrontierRepairRequestedCount -gt 0 -and
        ($maxSparseMode -gt 0 -or $Summary.ReceiverSparseModeSelectedCount -gt 0) -and
        ($limitedReorderDecisionCount -gt 0 -or $limitedGrantProfileCount -gt 0) -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0 -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeQueuedBytes -eq 0 -and
        $Summary.MaxProactiveFrontierRepairGapAgeMs -gt 0 -and
        ($Summary.MaxProactiveFrontierRepairGapAgeMs -lt 2500 -or
            $proactiveRepairPressureStats.RepeatedUnfilledCount -gt 0 -or
            $proactiveRepairPressureStats.HardLimitedCount -gt 0)) {
        if ($proactiveRepairPressureStats.HardLimitedDuringGraceCount -gt 0) {
            return 'proactive_frontier_repair_overlimited'
        }

        if ($proactiveRepairPressureStats.RepeatedUnfilledCount -gt 0 -or $proactiveRepairPressureStats.HardLimitedCount -gt 0) {
            return 'proactive_frontier_gap_repeated_limited'
        }

        return 'proactive_frontier_repair_overlimited'
    }

    if ($Summary.ProactiveFrontierRepairRequestedCount -gt 0 -and
        $maxGapAgeMs -ge 750 -and
        $maxSenderWindowBytes -gt 0 -and
        $maxSenderWindowBytes -le (1024 * 1024)) {
        return 'frontier_gap_repair_limited'
    }

    if ($senderFeedEvents.Count -gt 0 -and
        $senderFeedDurationMs -gt 0 -and
        $senderFeedPrepareDurationMs -ge [Math]::Max(1, [long]($senderFeedDurationMs / 4)) -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeWorkerUtilizationPercent -lt 50) {
        return 'sender_prepare_limited'
    }

    $maxReceiverFeedbackQueueDepth = [Math]::Max($Summary.MaxReceiverFeedbackQueueDepth, $Summary.MaxReceiverFeedbackSummaryQueueDepth)
    $receiverFeedbackDirectBlocking = (-not $receiverFeedbackPumpActive -or $receiverFeedbackDirectSentCount -gt 0) -and
        ($maxReceiverFeedbackSendDurationMs -ge 250 -or $maxReceiverFeedbackEnqueueToSendAgeMs -ge 500)
    $receiverFeedbackPumpQueueBlocking = $receiverFeedbackPumpActive -and
        ($maxReceiverFeedbackQueueDepth -ge 32 -or $maxReceiverFeedbackEnqueueToSendAgeMs -ge 2000)
    if ($senderCreditWaitRatioHigh -and
        $receiverFeedbackEvents.Count -gt 0 -and
        $Summary.ReceiverFeedbackFailedCount -eq 0 -and
        ($receiverFeedbackDirectBlocking -or $receiverFeedbackPumpQueueBlocking) -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeQueuedBytes -eq 0 -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0) {
        return 'receiver_feedback_blocking_limited'
    }

    if ($senderCreditWaitRatioHigh -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeQueuedBytes -eq 0 -and
        $maxBridgeOldestQueuedAgeMs -eq 0 -and
        $maxBridgeSendP95Ms -lt 50 -and
        $maxSenderWindowBytes -ge (2 * 1024 * 1024) -and
        $maxReceiverPendingBytes -lt (4 * 1024 * 1024) -and
        $maxGapAgeMs -lt 2500 -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0) {
        if (($softLimitedReorderDecisionCount -gt 0 -or $softLimitedGrantProfileCount -gt 0) -and
            $maxSparseReorderDistance -ge 64 -and
            $maxPayloadFillPercent -ge 90D -and
            [double]::Parse($sparseCreditStats.ReorderUseRatioPercent, [System.Globalization.CultureInfo]::InvariantCulture) -lt 70D) {
            return 'sparse_reorder_credit_limited'
        }

        if (($maxTargetWindowBytes -gt 0 -and $maxTargetWindowBytes -lt (15 * 1024 * 1024)) -or
            ($maxTargetWindowBytes -eq 0 -and $maxSenderWindowBytes -gt 0 -and $maxSenderWindowBytes -lt (15 * 1024 * 1024))) {
            return 'adaptive_window_underprovisioned'
        }

        if ($senderGrantApplyEvents.Count -gt 0 -and
            $senderCreditStallEvents.Count -gt 0 -and
            (Get-FileTransferMaxField -Events $senderGrantApplyEvents -FieldName 'credit_wait_active_ms') -ge 500) {
            return 'sender_feedback_loop_blocked'
        }

        if ($receiverGrantDecisionEvents.Count -gt 0 -and
            $grantSummaryEvents.Count -eq 0) {
            return 'grant_generation_limited'
        }

        if ($receiverGrantDecisionEvents.Count -gt 0 -and
            $senderGrantApplyEvents.Count -gt 0 -and
            $grantSummaryEvents.Count -gt ($senderGrantApplyEvents.Count * 2)) {
            return 'grant_delivery_limited'
        }

        if ($grantSummaryEvents.Count -gt 0 -or $controlDegradedBulkActiveCount -gt 0) {
            return 'sender_credit_wait_limited'
        }

        return 'sender_credit_wait_limited'
    }

    if ($senderFeedEvents.Count -gt 0 -and
        $senderFeedCreditWaitMs -ge [Math]::Max(1, [long]($senderFeedDurationMs / 4)) -and
        $maxSenderWindowBytes -le (2 * 1024 * 1024) -and
        $maxBridgeQueueDepth -eq 0) {
        return 'sender_credit_wait_limited'
    }

    if ($maxBridgeConfiguredConcurrency -gt 1 -and
        $maxBridgeInFlight -le 1 -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxSenderRawBps -gt 0) {
        if ($Summary.SenderPipelineSummaryCount -eq 0 -or $Summary.MaxSenderPipelineEffectiveDepth -le 1) {
            return 'sender_transport_serialized'
        }

        return 'sender_bridge_underfed'
    }

    if ($maxBridgeConfiguredConcurrency -gt 1 -and
        $Summary.MaxSenderPipelineEffectiveDepth -gt 1 -and
        $Summary.MaxSenderPipelineInFlightFrames -gt 1 -and
        $maxSenderWindowBytes -ge (2 * 1024 * 1024) -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxBridgeQueuedBytes -eq 0 -and
        $maxBridgeSendP95Ms -ge 0 -and
        $maxBridgeSendP95Ms -lt 50 -and
        $maxBridgeWorkerUtilizationPercent -gt 0 -and
        $maxBridgeWorkerUtilizationPercent -le 35 -and
        $maxBridgeWorkerSaturationPercent -le 10 -and
        $maxBridgeInFlight -le [Math]::Max(2, [long][Math]::Ceiling($maxBridgeConfiguredConcurrency / 2D)) -and
        $maxPayloadFillPercent -ge 90D -and
        $maxGapAgeMs -lt 1000 -and
        $maxReceiverPendingBytes -lt (4 * 1024 * 1024) -and
        $Summary.RequestTimeoutCount -eq 0 -and
        $Summary.RepairSetRequestedCount -eq 0 -and
        $Summary.RetryRequestedCount -eq 0 -and
        $maxBridgePayloadBps -gt 0) {
        return 'nkn_bulk_underutilized'
    }

    if ($externalReceiveStallCount -ge 2) {
        return 'external_transport_limited'
    }

    if ($externalIssueCount -gt 0 -and ($maxReceiverRawBps -eq 0 -or $maxReceiverRawBps -lt ($maxSenderRawBps / 2))) {
        return 'external_transport_limited'
    }

    if ($Summary.RequestTimeoutCount -gt 0 -or
        $Summary.RepairSetRequestedCount -gt 0 -or
        $Summary.RetryRequestedCount -gt 0) {
        return 'repair_or_timeout_limited'
    }

    if (($maxSparseMode -gt 0 -or $Summary.ReceiverSparseModeSelectedCount -gt 0) -and
        $maxSparseWriteBps -gt 0 -and
        $maxGapAgeMs -gt 1000) {
        return 'receiver_gap_stalled'
    }

    if ($maxSampleWindowMs -gt 0 -and $maxWriteDurationMs -ge [Math]::Max(1, [long]($maxSampleWindowMs / 4))) {
        return 'disk_write_limited'
    }

    if ($maxGapAgeMs -gt 1000 -and $maxReceiverRawBps -gt 0 -and ($maxCommitBps -eq 0 -or $maxReceiverRawBps -ge ($maxCommitBps * 2))) {
        return 'receiver_gap_stalled'
    }

    if ($maxSenderWindowBytes -gt 0 -and
        $maxSenderWindowBytes -le (1024 * 1024) -and
        $maxBridgeQueueDepth -eq 0 -and
        $maxReceiverPendingBytes -lt (4 * 1024 * 1024)) {
        return 'sender_window_limited'
    }

    if ($maxSenderRawBps -gt 0 -and
        $maxReceiverRawBps -gt 0 -and
        $maxSenderRawBps -ge ($maxReceiverRawBps * 2) -and
        $externalIssueCount -eq 0) {
        return 'nkn_delivery_limited'
    }

    return 'inconclusive'
}

function New-FileTransferThroughputDecompositionSummaryLines {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string]$ArtifactDir = ''
    )

    $senderEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_throughput_summary'))
    $senderPipelineEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_pipeline_summary'))
    $senderFeedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_feed_summary'))
    $senderGrantApplyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_grant_apply_summary'))
    $senderCreditStallEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_credit_stall_summary'))
    $receiverGrantDecisionEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_grant_decision_summary'))
    $receiverFeedbackEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_feedback_pump_started', 'filetransfer_v4_receiver_feedback_enqueued', 'filetransfer_v4_receiver_feedback_coalesced', 'filetransfer_v4_receiver_feedback_sent', 'filetransfer_v4_receiver_feedback_summary', 'filetransfer_v4_receiver_feedback_failed'))
    $receiverEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receiver_throughput_summary'))
    $gapStallEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_gap_stall_summary'))
    $sparseEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_receiver_sparse_mode_selected', 'filetransfer_receiver_sparse_write_summary', 'filetransfer_receiver_sparse_commit_summary'))
    $senderCacheEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_sender_repair_cache_policy', 'filetransfer_sender_repair_cache_summary', 'filetransfer_sender_repair_cache_pressure_entered', 'filetransfer_sender_repair_cache_pressure_exited', 'filetransfer_sender_cache_exhausted', 'filetransfer_sender_repair_unavailable'))
    $bridgeBulkEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_bulk_send_summary' -or $_.EventName -eq 'nkn_bridge_bulk_queue_state' })
    $externalHealthEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'screenshare_bridge_transport_health_summary' })
    $inboundDeliveryEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_inbound_delivery_summary' })
    $inboundEnvelopeReceivedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_inbound_envelope_received' })
    $inboundEnvelopeDropEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_inbound_envelope_drop' })
    $secureFileTransferDataEnvelopeReceivedEvents = @(@($Summary.GlobalEvents + $Summary.TransferEvents) | Where-Object {
        $_.EventName -eq 'filetransfer_envelope_received' -and
        (Get-FileTransferEventField -Event $_ -Name 'message_type' -Default '') -eq 'file_transfer_data_frame'
    })
    $fileTransferDataFrameDispatchedEvents = @(@($Summary.GlobalEvents + $Summary.TransferEvents) | Where-Object {
        $_.EventName -eq 'filetransfer_data_frame_dispatched'
    })
    $receiveStallDetectedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_detected' })
    $receiveStallRecoveryStartedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_started' })
    $receiveStallRecoveryCompletedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_completed' })
    $receiveStallRecoveryFailedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_failed' })
    $receiveStallRecoveryCooldownBypassedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_cooldown_bypassed' })
    $receiveStallRecoveryUnprovenEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_unproven' })
    $receiveStallRecoveryReceiveResumedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_receive_stall_recovery_receive_resumed' })
    $controlReceiveDegradedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_control_receive_degraded' })
    $controlReceiveRecoverySuppressedEvents = @($Summary.GlobalEvents | Where-Object { $_.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed' })
    $receiveLivenessEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_receive_liveness_summary'))
    $profileChangedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_profile_changed'))
    $reorderPolicyEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_reorder_policy_decision'))
    $grantSummaryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_grant_window_summary'))
    $v4SenderPumpEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_sender_pump_summary'))
    $v4StateSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_sent'))
    $v4StateReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_state_received'))
    $v4RepairRequestedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_requested'))
    $v4RepairScheduledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_scheduled'))
    $v4RepairSuppressedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_suppressed'))
    $v4RepairSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_sent'))
    $v4RepairObservedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_chunk_observed'))
    $v4RepairFilledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_repair_filled'))
    $v4RepairBatchEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_chunk_batch_sent_as_batch') |
        Where-Object { (Get-FileTransferEventField -Event $_ -Name 'batch_profile' -Default '') -like 'v4_repair_*' })
    $v4FrontierTailRepairDueEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_due'))
    $v4FrontierTailRepairSuppressedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_suppressed'))
    $v4FrontierTailRepairFilledEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_frontier_stall_missing_range_filled'))
    $v4CompleteSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_complete_sent'))
    $v4CompleteReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_complete_received'))
    $v4FeedbackFirstSuccessEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_feedback_first_success'))
    $v4FeedbackBothFailedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_feedback_both_failed'))
    $v6SenderWaitingForRequestsEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_sender_waiting_for_requests'))
    $v6ReceiverRequestWindowEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_receiver_request_window_sent'))
    $v6ReceiverStateSentEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_receiver_state_sent'))
    $v6ReceiverStateReceivedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_receiver_state_received'))
    $v6UnsolicitedChunkIgnoredEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v6_unsolicited_chunk_ignored'))
    $v4LatestSenderPump = Get-FileTransferLatestEvent -Events $v4SenderPumpEvents -Name 'filetransfer_v4_sender_pump_summary'
    $v4LatestReceiverStateMatches = @($v4StateSentEvents + $v4StateReceivedEvents | Sort-Object Sequence | Select-Object -Last 1)
    $v4LatestReceiverState = if ($v4LatestReceiverStateMatches.Count -gt 0) { $v4LatestReceiverStateMatches[0] } else { $null }
    $v4LatestSenderNextUnsentChunkIndex = if ($null -ne $v4LatestSenderPump) { Get-FileTransferEventInt64Field -Event $v4LatestSenderPump -Name 'next_unsent_chunk_index' -Default -1 } else { -1 }
    $v4LatestSenderCreditCeilingChunkIndex = if ($null -ne $v4LatestSenderPump) { Get-FileTransferEventInt64Field -Event $v4LatestSenderPump -Name 'credit_ceiling_chunk_index' -Default -1 } else { -1 }
    $v4LatestSenderRemoteFrontierChunkIndex = if ($null -ne $v4LatestSenderPump) { Get-FileTransferEventInt64Field -Event $v4LatestSenderPump -Name 'remote_frontier_chunk_index' -Default -1 } else { -1 }
    $v4LatestReceiverFrontierChunkIndex = if ($null -ne $v4LatestReceiverState) { Get-FileTransferEventInt64Field -Event $v4LatestReceiverState -Name 'contiguous_committed_chunk_index' -Default -1 } else { -1 }
    $v4LatestReceiverDurableHighestChunkIndex = if ($null -ne $v4LatestReceiverState) { Get-FileTransferEventInt64Field -Event $v4LatestReceiverState -Name 'durable_received_highest_chunk_index' -Default -1 } else { -1 }
    $v4LatestReceiverCreditUntilChunkIndex = if ($null -ne $v4LatestReceiverState) { Get-FileTransferEventInt64Field -Event $v4LatestReceiverState -Name 'credit_until_chunk_index_exclusive' -Default -1 } else { -1 }
    $v4FrontierRepairBacklogChunks = if ($v4LatestReceiverDurableHighestChunkIndex -ge $v4LatestReceiverFrontierChunkIndex -and $v4LatestReceiverFrontierChunkIndex -ge 0) {
        [Math]::Max(0, $v4LatestReceiverDurableHighestChunkIndex - $v4LatestReceiverFrontierChunkIndex + 1)
    }
    else {
        0
    }
    $v4FullNormalPayloadSent = if ($v4LatestSenderNextUnsentChunkIndex -ge 0 -and
        $v4LatestSenderCreditCeilingChunkIndex -ge 0 -and
        $v4LatestSenderNextUnsentChunkIndex -ge $v4LatestSenderCreditCeilingChunkIndex -and
        (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'normal_raw_bytes_sent_total') -gt 0) {
        1
    }
    else {
        0
    }
    $sparseCreditStats = Get-FileTransferSparseCreditStats -GrantEvents $grantSummaryEvents
    $proactiveFrontierRepairEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_frontier_gap_repair_eligible', 'filetransfer_frontier_gap_repair_requested', 'filetransfer_frontier_gap_repair_skipped', 'filetransfer_frontier_gap_repair_suppressed', 'filetransfer_frontier_gap_repair_sender_received', 'filetransfer_frontier_gap_repair_sender_scheduled', 'filetransfer_frontier_gap_repair_sender_sent', 'filetransfer_frontier_gap_repair_filled', 'filetransfer_proactive_frontier_repair_state_reset'))
    $proactiveRepairPressureStats = Get-FileTransferProactiveRepairPressureStats -Events @($reorderPolicyEvents + $grantSummaryEvents + $proactiveFrontierRepairEvents)
    $proactiveFrontierRepairSkippedEvents = @($proactiveFrontierRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_frontier_gap_repair_skipped' })
    $proactiveFrontierRepairResetEvents = @($proactiveFrontierRepairEvents | Where-Object { $_.EventName -eq 'filetransfer_proactive_frontier_repair_state_reset' })
    $benignGapSkipLimitedPolicyCount = @($proactiveFrontierRepairSkippedEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' -and
        (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '(none)') -eq '(none)' -and
        (Get-FileTransferEventField -Event $_ -Name 'grant_policy_after_repair' -Default '') -eq 'healthy_limited'
    }).Count
    $cycleStats = Get-FileTransferCycleGoodputStats -ArtifactDir $ArtifactDir
    $senderActiveSampleDurationMs = Get-FileTransferActiveSampleDurationMs -Events $senderEvents -ActivityFieldName 'raw_bytes_sent'
    $senderFeedActiveDurationMs = Get-FileTransferActiveSampleDurationMs -Events $senderFeedEvents -ActivityFieldName 'raw_bytes_prepared'
    $senderFeedCreditWaitRatioPercent = if ($senderFeedActiveDurationMs -gt 0) {
        [Math]::Round(($Summary.SenderFeedCreditWaitDurationMs * 100.0) / $senderFeedActiveDurationMs, 3)
    }
    else {
        0.0
    }
    $bridgeBulkActiveSampleDurationMs = Get-FileTransferActiveSampleDurationMs -Events $bridgeBulkEvents -ActivityFieldName 'payload_bytes_sent'
    $observedTransferWindowMs = Get-FileTransferObservedDurationMs -Events $Summary.TransferEvents
    $estimatedIdleTransferMs = [Math]::Max(0, $observedTransferWindowMs - [Math]::Max($senderActiveSampleDurationMs, $bridgeBulkActiveSampleDurationMs))
    $grantSendRatePerSecond = if ($observedTransferWindowMs -gt 0) {
        [Math]::Round(($grantSummaryEvents.Count * 1000.0) / $observedTransferWindowMs, 3)
    }
    else {
        0.0
    }
    $grantDeliveryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_data_frame_dispatched') |
        Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -eq 'filetransfer.receiver_state.v6' -or
                $frameType -eq 'filetransfer.state.v4'
        })
    $ackDeliveryEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_data_frame_dispatched') |
        Where-Object {
            $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
            $frameType -eq 'filetransfer.receiver_state.v6' -or
                $frameType -eq 'filetransfer.state.v4'
        })
    $progressTimeoutWithReceiverGapStall = if ($Summary.LiveProgressTimeoutCount -gt 0 -and $gapStallEvents.Count -gt 0) { 1 } else { 0 }
    $limiter = Resolve-FileTransferThroughputLimiter `
        -SenderEvents $senderEvents `
        -ReceiverEvents $receiverEvents `
        -GapStallEvents $gapStallEvents `
        -BridgeBulkEvents $bridgeBulkEvents `
        -ExternalHealthEvents $externalHealthEvents `
        -Summary $Summary `
        -ArtifactDir $ArtifactDir

    return @(
        ("transfer_id={0}" -f $Summary.TransferId),
        ("likely_limiter={0}" -f $limiter),
        ("data_protocol_version={0}" -f ($(if ((Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_negotiated') -gt 0 -or (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_sender_started') -gt 0 -or (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_receiver_started') -gt 0 -or $Summary.FrameTypeCounts.ContainsKey('filetransfer.chunk_batch.v6') -or $Summary.FrameTypeCounts.ContainsKey('filetransfer.receiver_state.v6')) { '6' } elseif ((Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_negotiated') -gt 0 -or $v4SenderPumpEvents.Count -gt 0 -or $v4StateSentEvents.Count -gt 0 -or $v4StateReceivedEvents.Count -gt 0) { '4' } elseif ($senderEvents.Count -gt 0 -or $receiverEvents.Count -gt 0) { '3' } else { '(unknown)' }))),
        ("sender_sample_count={0}" -f $senderEvents.Count),
        ("sender_pipeline_sample_count={0}" -f $senderPipelineEvents.Count),
        ("sender_feed_sample_count={0}" -f $senderFeedEvents.Count),
        ("receiver_sample_count={0}" -f $receiverEvents.Count),
        ("gap_stall_summary_count={0}" -f $gapStallEvents.Count),
        ("bridge_bulk_sample_count={0}" -f $bridgeBulkEvents.Count),
        ("max_sender_raw_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $senderEvents -FieldName 'raw_bytes_per_second')),
        ("max_receiver_raw_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $receiverEvents -FieldName 'raw_bytes_received_per_second')),
        ("max_contiguous_commit_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $receiverEvents -FieldName 'contiguous_bytes_committed_per_second')),
        ("receiver_delivery_to_commit_ratio_max={0}" -f (Get-FileTransferMaxDeliveryToCommitRatio -ReceiverEvents $receiverEvents)),
        ("max_oldest_gap_age_ms={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $receiverEvents -FieldName 'oldest_gap_age_ms'), (Get-FileTransferMaxField -Events $gapStallEvents -FieldName 'stall_duration_ms')))),
        ("max_gap_stall_duration_ms={0}" -f (Get-FileTransferMaxField -Events $gapStallEvents -FieldName 'stall_duration_ms')),
        ("receiver_sparse_mode_selected_count={0}" -f $Summary.ReceiverSparseModeSelectedCount),
        ("max_sparse_write_bytes_per_second={0}" -f $Summary.MaxReceiverSparseWriteBytesPerSecond),
        ("max_sparse_written_ahead_bytes={0}" -f $Summary.MaxReceiverSparseWrittenAheadBytes),
        ("max_sparse_gap_count={0}" -f $Summary.MaxReceiverSparseGapCount),
        ("max_sender_remote_granted_window_bytes={0}" -f (Get-FileTransferMaxField -Events $senderEvents -FieldName 'remote_granted_window_bytes')),
        ("max_sender_sent_cache_bytes={0}" -f (Get-FileTransferMaxField -Events $senderEvents -FieldName 'sent_cache_bytes')),
        ("max_sender_pipeline_configured_depth={0}" -f $Summary.MaxSenderPipelineConfiguredDepth),
        ("max_sender_pipeline_effective_depth={0}" -f $Summary.MaxSenderPipelineEffectiveDepth),
        ("max_sender_pipeline_in_flight_frames={0}" -f $Summary.MaxSenderPipelineInFlightFrames),
        ("max_sender_pipeline_in_flight_bytes={0}" -f $Summary.MaxSenderPipelineInFlightBytes),
        ("sender_pipeline_scheduled_frames={0}" -f $Summary.SenderPipelineScheduledFrames),
        ("sender_pipeline_completed_frames={0}" -f $Summary.SenderPipelineCompletedFrames),
        ("sender_pipeline_failed_frames={0}" -f $Summary.SenderPipelineFailedFrames),
        ("max_sender_pipeline_fifo_wait_ms={0}" -f $Summary.MaxSenderPipelineFifoWaitMs),
        ("max_sender_pipeline_accepted_progress_lag_bytes={0}" -f $Summary.MaxSenderPipelineAcceptedProgressLagBytes),
        ("sender_feed_raw_bytes_prepared={0}" -f $Summary.SenderFeedRawBytesPrepared),
        ("sender_feed_read_duration_ms={0}" -f $Summary.SenderFeedReadDurationMs),
        ("sender_feed_batch_prepare_duration_ms={0}" -f $Summary.SenderFeedBatchPrepareDurationMs),
        ("sender_feed_schedule_duration_ms={0}" -f $Summary.SenderFeedScheduleDurationMs),
        ("max_sender_feed_inter_schedule_gap_p95_ms={0}" -f $Summary.MaxSenderFeedInterScheduleGapP95Ms),
        ("max_sender_feed_inter_schedule_gap_ms={0}" -f $Summary.MaxSenderFeedInterScheduleGapMs),
        ("sender_feed_credit_wait_duration_ms={0}" -f $Summary.SenderFeedCreditWaitDurationMs),
        ("sender_feed_credit_wait_ratio_percent={0}" -f $senderFeedCreditWaitRatioPercent),
        ("sender_feed_pipeline_slot_wait_duration_ms={0}" -f $Summary.SenderFeedPipelineSlotWaitDurationMs),
        ("sender_feed_source_read_error_count={0}" -f $Summary.SenderFeedSourceReadErrorCount),
        ("sender_grant_apply_count={0}" -f $senderGrantApplyEvents.Count),
        ("sender_grant_apply_async_pump_count={0}" -f (@($senderGrantApplyEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'async_sender_pump' -Default 0) -gt 0 }).Count)),
        ("max_sender_grant_apply_credit_wait_active_ms={0}" -f (Get-FileTransferMaxField -Events $senderGrantApplyEvents -FieldName 'credit_wait_active_ms')),
        ("max_sender_grant_apply_available_credit_bytes_after={0}" -f (Get-FileTransferMaxField -Events $senderGrantApplyEvents -FieldName 'available_credit_bytes_after')),
        ("sender_credit_stall_summary_count={0}" -f $senderCreditStallEvents.Count),
        ("max_sender_credit_stall_active_ms={0}" -f (Get-FileTransferMaxField -Events $senderCreditStallEvents -FieldName 'credit_wait_active_ms')),
        ("max_sender_credit_stall_last_grant_age_ms={0}" -f (Get-FileTransferMaxField -Events $senderCreditStallEvents -FieldName 'last_grant_age_ms')),
        ("receiver_grant_decision_summary_count={0}" -f $receiverGrantDecisionEvents.Count),
        ("receiver_grant_decision_should_grant_count={0}" -f (@($receiverGrantDecisionEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'should_grant' -Default 0) -gt 0 }).Count)),
        ("receiver_grant_decision_no_send_count={0}" -f (@($receiverGrantDecisionEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'should_grant' -Default 0) -eq 0 -and (Get-FileTransferEventInt64Field -Event $_ -Name 'should_ack_only' -Default 0) -eq 0 }).Count)),
        ("receiver_grant_decision_coalesce_blocked_count={0}" -f (@($receiverGrantDecisionEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'ack_coalesce_blocked' -Default 0) -gt 0 }).Count)),
        ("receiver_feedback_pump_started_count={0}" -f $Summary.ReceiverFeedbackPumpStartedCount),
        ("receiver_feedback_pump_active_count={0}" -f $Summary.ReceiverFeedbackPumpActiveCount),
        ("slice_started_after_pump_start={0}" -f $Summary.ReceiverFeedbackSliceStartedAfterPumpStart),
        ("receiver_feedback_enqueued_count={0}" -f $Summary.ReceiverFeedbackEnqueuedCount),
        ("receiver_feedback_sent_count={0}" -f $Summary.ReceiverFeedbackSentCount),
        ("receiver_feedback_coalesced_count={0}" -f $Summary.ReceiverFeedbackCoalescedCount),
        ("receiver_feedback_summary_count={0}" -f $Summary.ReceiverFeedbackSummaryCount),
        ("receiver_feedback_failed_count={0}" -f $Summary.ReceiverFeedbackFailedCount),
        ("max_receiver_feedback_queue_depth={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackQueueDepth, $Summary.MaxReceiverFeedbackSummaryQueueDepth))),
        ("max_receiver_feedback_enqueue_to_send_age_ms={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackEnqueueToSendAgeMs, $Summary.MaxReceiverFeedbackSummaryEnqueueToSendAgeMs))),
        ("max_receiver_feedback_send_duration_ms={0}" -f ([Math]::Max($Summary.MaxReceiverFeedbackSendDurationMs, $Summary.MaxReceiverFeedbackSummarySendDurationMs))),
        ("v4_sender_pump_summary_count={0}" -f $v4SenderPumpEvents.Count),
        ("v4_state_sent_count={0}" -f $v4StateSentEvents.Count),
        ("v4_state_received_count={0}" -f $v4StateReceivedEvents.Count),
        ("v4_missing_range_repair_scheduled_count={0}" -f $v4RepairScheduledEvents.Count),
        ("v4_repair_requested_count={0}" -f $v4RepairRequestedEvents.Count),
        ("v4_repair_suppressed_count={0}" -f $v4RepairSuppressedEvents.Count),
        ("v4_missing_range_repair_sent_count={0}" -f $v4RepairSentEvents.Count),
        ("v4_repair_delivery_bulk_only_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_mode' -Value 'bulk_only')),
        ("v4_repair_delivery_control_bulk_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_mode' -Value 'control_bulk_escalated')),
        ("v4_repair_delivery_retry_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'retry')),
        ("v4_repair_delivery_credit_stall_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'credit_stall')),
        ("v4_repair_delivery_frontier_not_advanced_escalated_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'frontier_not_advanced')),
        ("v4_repair_delivery_primary_regular_nkn_frontier_first_send_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairSentEvents -FieldName 'repair_delivery_escalation_reason' -Value 'primary_regular_nkn_frontier_first_send')),
        ("v4_repair_batch_bulk_only_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairBatchEvents -FieldName 'repair_delivery_mode' -Value 'bulk_only')),
        ("v4_repair_batch_control_bulk_count={0}" -f (Get-FileTransferEventFieldValueCount -Events $v4RepairBatchEvents -FieldName 'repair_delivery_mode' -Value 'control_bulk_escalated')),
        ("v4_repair_chunk_observed_count={0}" -f $v4RepairObservedEvents.Count),
        ("v4_repair_observed_accepted_chunk_count={0}" -f (Get-FileTransferSumField -Events $v4RepairObservedEvents -FieldName 'accepted_chunk_count')),
        ("v4_repair_observed_duplicate_or_stale_chunk_count={0}" -f (Get-FileTransferSumField -Events $v4RepairObservedEvents -FieldName 'duplicate_or_stale_chunk_count')),
        ("v4_repair_observed_frontier_advanced_count={0}" -f (@($v4RepairObservedEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'frontier_advanced' -Default 0) -gt 0 }).Count)),
        ("v4_repair_filled_count={0}" -f $v4RepairFilledEvents.Count),
        ("v4_frontier_tail_repair_due_count={0}" -f $v4FrontierTailRepairDueEvents.Count),
        ("v4_frontier_tail_repair_suppressed_count={0}" -f $v4FrontierTailRepairSuppressedEvents.Count),
        ("v4_frontier_tail_repair_filled_count={0}" -f $v4FrontierTailRepairFilledEvents.Count),
        ("v4_max_frontier_stall_age_ms={0}" -f (Get-FileTransferMaxField -Events @($v4StateSentEvents + $v4FrontierTailRepairDueEvents + $v4FrontierTailRepairSuppressedEvents) -FieldName 'frontier_stall_age_ms')),
        ("v4_missing_range_due_state_mismatch_count={0}" -f (Get-FileTransferV4MissingRangeDueStateMismatchCount -DueEvents $v4FrontierTailRepairDueEvents -StateSentEvents $v4StateSentEvents)),
        ("v4_repair_request_to_fill_p95_ms={0}" -f (Get-FileTransferPercentileField -Events $v4RepairFilledEvents -FieldName 'request_to_fill_ms' -Percentile 95)),
        ("v4_complete_sent_count={0}" -f $v4CompleteSentEvents.Count),
        ("v4_complete_received_count={0}" -f $v4CompleteReceivedEvents.Count),
        ("v4_feedback_redundant_success_count={0}" -f $v4FeedbackFirstSuccessEvents.Count),
        ("v4_feedback_both_failed_count={0}" -f $v4FeedbackBothFailedEvents.Count),
        ("v4_max_sender_pump_in_flight_frames={0}" -f (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'in_flight_frames')),
        ("v4_sender_pump_scheduled_frames={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'scheduled_frames')),
        ("v4_sender_pump_completed_frames={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'completed_frames')),
        ("v4_sender_pump_failed_frames={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'failed_frames')),
        ("v4_sender_pump_raw_bytes_sent={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'raw_bytes_sent')),
        ("v4_sender_pump_repair_send_count={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'repair_send_count')),
        ("v4_sender_pump_normal_scheduled_frames={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'normal_scheduled_frames')),
        ("v4_sender_pump_repair_scheduled_frames={0}" -f (Get-FileTransferSumField -Events $v4SenderPumpEvents -FieldName 'repair_scheduled_frames')),
        ("v4_max_sender_pump_credit_exhausted_time_ms={0}" -f (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'credit_exhausted_time_ms')),
        ("v4_max_sender_pump_available_credit_bytes={0}" -f (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'available_credit_bytes')),
        ("v4_max_sender_pump_normal_raw_bytes_sent_total={0}" -f (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'normal_raw_bytes_sent_total')),
        ("v4_max_sender_pump_repair_raw_bytes_sent_total={0}" -f (Get-FileTransferMaxField -Events $v4SenderPumpEvents -FieldName 'repair_raw_bytes_sent_total')),
        ("v4_latest_sender_next_unsent_chunk_index={0}" -f $v4LatestSenderNextUnsentChunkIndex),
        ("v4_latest_sender_credit_ceiling_chunk_index={0}" -f $v4LatestSenderCreditCeilingChunkIndex),
        ("v4_latest_sender_remote_frontier_chunk_index={0}" -f $v4LatestSenderRemoteFrontierChunkIndex),
        ("v4_latest_receiver_frontier_chunk_index={0}" -f $v4LatestReceiverFrontierChunkIndex),
        ("v4_latest_receiver_durable_highest_chunk_index={0}" -f $v4LatestReceiverDurableHighestChunkIndex),
        ("v4_latest_receiver_credit_until_chunk_index={0}" -f $v4LatestReceiverCreditUntilChunkIndex),
        ("v4_max_frontier_lag_chunks={0}" -f (Get-FileTransferMaxField -Events $v4StateSentEvents -FieldName 'frontier_lag_chunks')),
        ("v4_full_normal_payload_sent={0}" -f $v4FullNormalPayloadSent),
        ("v4_frontier_repair_backlog_chunks={0}" -f $v4FrontierRepairBacklogChunks),
        ("v6_sender_waiting_for_requests_count={0}" -f $v6SenderWaitingForRequestsEvents.Count),
        ("v6_receiver_request_window_sent_count={0}" -f $v6ReceiverRequestWindowEvents.Count),
        ("v6_receiver_state_sent_count={0}" -f $v6ReceiverStateSentEvents.Count),
        ("v6_receiver_state_received_count={0}" -f $v6ReceiverStateReceivedEvents.Count),
        ("v6_unsolicited_chunk_ignored_count={0}" -f $v6UnsolicitedChunkIgnoredEvents.Count),
        ("v6_unsolicited_behind_committed_frontier_count={0}" -f (@($v6UnsolicitedChunkIgnoredEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'behind_committed_frontier' }).Count)),
        ("v6_max_receiver_requested_chunk_count={0}" -f (Get-FileTransferMaxField -Events $v6ReceiverRequestWindowEvents -FieldName 'requested_chunk_count')),
        ("v6_max_receiver_requested_until_chunk_index_exclusive={0}" -f (Get-FileTransferMaxField -Events $v6ReceiverRequestWindowEvents -FieldName 'requested_until_chunk_index_exclusive')),
        ("v6_max_receiver_request_window_chunks={0}" -f (Get-FileTransferMaxField -Events $v6ReceiverRequestWindowEvents -FieldName 'request_window_chunks')),
        ("max_sender_repair_cache_bytes={0}" -f $Summary.MaxSenderRepairCacheBytes),
        ("sender_repair_cache_hit_count={0}" -f $Summary.SenderRepairCacheHitCount),
        ("sender_repair_cache_miss_count={0}" -f $Summary.SenderRepairCacheMissCount),
        ("sender_repair_source_reread_count={0}" -f $Summary.SenderRepairSourceRereadCount),
        ("sender_repair_cache_eviction_count={0}" -f $Summary.SenderRepairCacheEvictionCount),
        ("max_receiver_pending_bytes={0}" -f (Get-FileTransferMaxField -Events $receiverEvents -FieldName 'pending_bytes')),
        ("max_receiver_write_duration_ms={0}" -f (Get-FileTransferMaxField -Events $receiverEvents -FieldName 'write_duration_ms')),
        ("max_bridge_bulk_payload_bytes_per_second={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'payload_bytes_per_second')),
        ("max_bridge_bulk_payload_bytes_enqueued_per_second={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'payload_bytes_enqueued_per_second')),
        ("max_bridge_bulk_inter_enqueue_gap_p95_ms={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'inter_enqueue_gap_p95_ms')),
        ("max_bridge_bulk_inter_enqueue_gap_ms={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'inter_enqueue_gap_max_ms')),
        ("max_bridge_bulk_queue_depth={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'queue_depth')),
        ("max_bridge_bulk_send_p95_ms={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'send_p95_ms')),
        ("max_bridge_bulk_in_flight={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'in_flight'), (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'in_flight_max')))),
        ("max_bridge_bulk_in_flight_bytes={0}" -f ([Math]::Max((Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'in_flight_bytes'), (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'in_flight_bytes_max')))),
        ("max_bridge_bulk_configured_concurrency={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'configured_concurrency')),
        ("max_bridge_bulk_effective_concurrency={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'effective_concurrency')),
        ("max_bridge_bulk_worker_utilization_percent={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'worker_utilization_percent')),
        ("bridge_bulk_worker_idle_slot_samples={0}" -f (Get-FileTransferSumField -Events $bridgeBulkEvents -FieldName 'worker_idle_slot_samples')),
        ("max_bridge_bulk_worker_saturation_percent={0}" -f (Get-FileTransferMaxField -Events $bridgeBulkEvents -FieldName 'worker_saturation_percent')),
        ("bridge_bulk_drain_wake_count={0}" -f (Get-FileTransferSumField -Events $bridgeBulkEvents -FieldName 'drain_wake_count')),
        ("cycle_goodput_count={0}" -f $cycleStats.Count),
        ("cycle_goodput_min_bytes_per_second={0}" -f $cycleStats.Min),
        ("cycle_goodput_average_bytes_per_second={0}" -f $cycleStats.Average),
        ("cycle_goodput_max_bytes_per_second={0}" -f $cycleStats.Max),
        ("cycle_goodput_helper_to_helpee_average_bytes_per_second={0}" -f $cycleStats.HelperToHelpeeAverage),
        ("cycle_goodput_helpee_to_helper_average_bytes_per_second={0}" -f $cycleStats.HelpeeToHelperAverage),
        ("gui_progress_timeout_count={0}" -f $Summary.LiveProgressTimeoutCount),
        ("gui_progress_timeout_reason={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.GuiProgressTimeoutReason)) { '(none)' } else { $Summary.GuiProgressTimeoutReason }))),
        ("last_receiver_next_chunk={0}" -f $Summary.LastReceiverNextChunk),
        ("last_receiver_highest_chunk={0}" -f $Summary.LastReceiverHighestChunk),
        ("last_progress_event_count={0}" -f $Summary.LastProgressEventCount),
        ("terminal_missing_after_progress_timeout={0}" -f $Summary.TerminalMissingAfterProgressTimeout),
        ("progress_timeout_with_receiver_gap_stall={0}" -f $progressTimeoutWithReceiverGapStall),
        ("artifact_slice_start_reason={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.ArtifactSliceStartReason)) { '(unknown)' } else { $Summary.ArtifactSliceStartReason }))),
        ("artifact_slice_end_reason={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.ArtifactSliceEndReason)) { '(unknown)' } else { $Summary.ArtifactSliceEndReason }))),
        ("sender_active_sample_duration_ms={0}" -f $senderActiveSampleDurationMs),
        ("bridge_bulk_active_sample_duration_ms={0}" -f $bridgeBulkActiveSampleDurationMs),
        ("observed_transfer_window_ms={0}" -f $observedTransferWindowMs),
        ("estimated_idle_transfer_ms={0}" -f $estimatedIdleTransferMs),
        ("transport_ready_sending_zero_receive_window_count={0}" -f (@($externalHealthEvents | Where-Object {
            -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'total_messages_received_since_last' -Default '')) -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'control_ready' -Default 0) -gt 0 -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'media_ready' -Default 0) -gt 0 -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'bulk_ready' -Default 0) -gt 0 -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'frames_sent_since_last' -Default 0) -gt 0 -and
            (Get-FileTransferEventInt64Field -Event $_ -Name 'total_messages_received_since_last' -Default 1) -eq 0
        }).Count)),
        ("receive_stall_detected_count={0}" -f $receiveStallDetectedEvents.Count),
        ("receive_stall_recovery_started_count={0}" -f $receiveStallRecoveryStartedEvents.Count),
        ("receive_stall_recovery_completed_count={0}" -f $receiveStallRecoveryCompletedEvents.Count),
        ("receive_stall_recovery_failed_count={0}" -f $receiveStallRecoveryFailedEvents.Count),
        ("receive_stall_recovery_cooldown_bypassed_count={0}" -f $receiveStallRecoveryCooldownBypassedEvents.Count),
        ("receive_stall_recovery_unproven_count={0}" -f $receiveStallRecoveryUnprovenEvents.Count),
        ("receive_stall_recovery_receive_resumed_count={0}" -f $receiveStallRecoveryReceiveResumedEvents.Count),
        ("control_receive_degraded_count={0}" -f $controlReceiveDegradedEvents.Count),
        ("control_receive_recovery_suppressed_count={0}" -f $controlReceiveRecoverySuppressedEvents.Count),
        ("inbound_delivery_bulk_messages={0}" -f (Get-FileTransferSumField -Events (@($inboundDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'channel' -Default '') -eq 'bulk' })) -FieldName 'messages')),
        ("inbound_delivery_subscriber_missing_count={0}" -f (Get-FileTransferSumField -Events $inboundDeliveryEvents -FieldName 'subscriber_missing_count')),
        ("inbound_delivery_source_matches_any_local_count={0}" -f (Get-FileTransferSumField -Events $inboundDeliveryEvents -FieldName 'source_matches_any_local_count')),
        ("inbound_envelope_received_count={0}" -f $inboundEnvelopeReceivedEvents.Count),
        ("inbound_filetransfer_data_frame_envelope_received_count={0}" -f (@($inboundEnvelopeReceivedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'envelope_type' -Default '') -eq 'file_transfer_data_frame' }).Count)),
        ("filetransfer_secure_data_frame_envelope_received_count={0}" -f $secureFileTransferDataEnvelopeReceivedEvents.Count),
        ("filetransfer_data_frame_dispatched_count={0}" -f $fileTransferDataFrameDispatchedEvents.Count),
        ("filetransfer_data_frame_dispatch_missing_count={0}" -f ($(if ($secureFileTransferDataEnvelopeReceivedEvents.Count -gt 0 -and $fileTransferDataFrameDispatchedEvents.Count -eq 0) { 1 } else { 0 }))),
        ("inbound_envelope_drop_count={0}" -f $inboundEnvelopeDropEvents.Count),
        ("max_transport_control_last_received_age_ms={0}" -f (Get-FileTransferMaxField -Events $externalHealthEvents -FieldName 'control_last_received_age_ms')),
        ("max_transport_media_last_received_age_ms={0}" -f (Get-FileTransferMaxField -Events $externalHealthEvents -FieldName 'media_last_received_age_ms')),
        ("max_transport_bulk_last_received_age_ms={0}" -f (Get-FileTransferMaxField -Events $externalHealthEvents -FieldName 'bulk_last_received_age_ms')),
        ("file_only_reorder_tolerated_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'tolerated' }).Count)),
        ("file_only_reorder_soft_limited_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'soft_limited' }).Count)),
        ("file_only_reorder_soft_limited_sample_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'soft_limited' }).Count)),
        ("file_only_reorder_limited_count={0}" -f (@($reorderPolicyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'decision' -Default '') -eq 'limited' }).Count)),
        ("healthy_limited_grant_summary_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_limited' }).Count)),
        ("healthy_file_only_soft_limited_grant_summary_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_file_only_soft_limited' }).Count)),
        ("sticky_limited_without_pressure_count={0}" -f (@($grantSummaryEvents | Where-Object {
            $limitedRecoveryCleanMs = Get-FileTransferEventInt64Field -Event $_ -Name 'limited_recovery_clean_ms' -Default 0
            $limitedRecoveryHoldMs = Get-FileTransferEventInt64Field -Event $_ -Name 'limited_recovery_hold_ms' -Default 750
            (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_limited' -and
            $limitedRecoveryCleanMs -ge $limitedRecoveryHoldMs -and
            (
                (Get-FileTransferEventField -Event $_ -Name 'limited_recovery_block_reason' -Default '') -eq '(none)' -or
                (
                    [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'limited_recovery_block_reason' -Default '')) -and
                    (Get-FileTransferEventField -Event $_ -Name 'proactive_repair_pressure_state' -Default '(none)') -eq '(none)' -and
                    (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -ne 'gap_stall' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'late_arrival_distance' -Default 0) -lt 64 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'pending_bytes' -Default 0) -lt (4 * 1024 * 1024)
                )
            )
        }).Count)),
        ("limited_recovery_fast_exit_count={0}" -f (@($profileChangedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'file_only_sparse_limited_recovered' }).Count)),
        ("stale_proactive_repair_state_reset_count={0}" -f $proactiveFrontierRepairResetEvents.Count),
        ("benign_gap_skip_limited_policy_count={0}" -f $benignGapSkipLimitedPolicyCount),
        ("max_limited_recovery_clean_ms={0}" -f (Get-FileTransferMaxField -Events $grantSummaryEvents -FieldName 'limited_recovery_clean_ms')),
        ("limited_recovery_block_none_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'limited_recovery_block_reason' -Default '') -eq '(none)' }).Count)),
        ("max_file_only_target_window_bytes={0}" -f (Get-FileTransferMaxField -Events $grantSummaryEvents -FieldName 'target_window_bytes')),
        ("file_only_sparse_window_capacity_proven={0}" -f ($(if ($limiter -eq 'file_only_sparse_window_capacity_proven') { 1 } else { 0 }))),
        ("adaptive_window_underprovisioned_signal={0}" -f ($(if ($limiter -eq 'adaptive_window_underprovisioned') { 1 } else { 0 }))),
        ("fixed_file_only_window_active_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'fixed_file_only_window_active' -Default 0) -gt 0 }).Count)),
        ("max_fixed_file_only_window_bytes={0}" -f (Get-FileTransferMaxField -Events $grantSummaryEvents -FieldName 'fixed_file_only_window_bytes')),
        ("file_only_grant_low_watermark_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'low_watermark' }).Count)),
        ("file_only_grant_target_changed_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'target_changed' }).Count)),
        ("grant_base_sparse_ahead_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'sparse_ahead' }).Count)),
        ("grant_base_contiguous_frontier_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'contiguous_frontier' }).Count)),
        ("grant_base_gap_stall_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'gap_stall' }).Count)),
        ("grant_base_blocked_by_gap_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'gap_stall' }).Count)),
        ("grant_base_sparse_ahead_disabled_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'grant_base_reason' -Default '') -eq 'sparse_ahead_disabled' }).Count)),
        ("grant_credit_base_sparse_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'sparse_base' }).Count)),
        ("grant_credit_base_contiguous_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'credit_base_reason' -Default '') -eq 'contiguous_frontier' }).Count)),
        ("sparse_credit_topup_count={0}" -f (@($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'sparse_credit_topup' }).Count)),
        ("average_credit_remaining_bytes={0}" -f (Get-FileTransferAverageDoubleField -Events $grantSummaryEvents -FieldName 'credit_remaining_bytes')),
        ("max_sparse_credit_advance_bytes={0}" -f (Get-FileTransferMaxField -Events $grantSummaryEvents -FieldName 'sparse_credit_advance_bytes')),
        ("sparse_credit_eligible_count={0}" -f $sparseCreditStats.EligibleCount),
        ("sparse_credit_used_count={0}" -f $sparseCreditStats.UsedCount),
        ("sparse_credit_blocked_count={0}" -f $sparseCreditStats.BlockedCount),
        ("sparse_credit_reorder_eligible_count={0}" -f $sparseCreditStats.ReorderEligibleCount),
        ("sparse_credit_reorder_used_count={0}" -f $sparseCreditStats.ReorderUsedCount),
        ("sparse_credit_reorder_use_ratio_percent={0}" -f $sparseCreditStats.ReorderUseRatioPercent),
        ("sparse_credit_blocked_no_sparse_ahead_count={0}" -f $sparseCreditStats.BlockedNoSparseAheadCount),
        ("sparse_credit_blocked_gap_stall_count={0}" -f $sparseCreditStats.BlockedGapStallCount),
        ("sparse_credit_blocked_repair_pressure_count={0}" -f $sparseCreditStats.BlockedRepairPressureCount),
        ("sparse_credit_blocked_receiver_pressure_count={0}" -f $sparseCreditStats.BlockedReceiverPressureCount),
        ("sparse_credit_blocked_timeout_count={0}" -f $sparseCreditStats.BlockedTimeoutCount),
        ("sparse_credit_blocked_accounting_disabled_count={0}" -f $sparseCreditStats.BlockedAccountingDisabledCount),
        ("sparse_credit_blocked_mode_current_count={0}" -f $sparseCreditStats.BlockedModeCurrentCount),
        ("grant_send_count={0}" -f $grantSummaryEvents.Count),
        ("grant_send_rate_per_second={0}" -f $grantSendRatePerSecond),
        ("grant_delivery_control_count={0}" -f (@($grantDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'control' }).Count)),
        ("grant_delivery_bulk_count={0}" -f (@($grantDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'bulk' }).Count)),
        ("ack_delivery_control_count={0}" -f (@($ackDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'control' }).Count)),
        ("ack_delivery_bulk_count={0}" -f (@($ackDeliveryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'lane' -Default '') -eq 'bulk' }).Count)),
        ("average_effective_grant_window_bytes={0}" -f (Get-FileTransferAverageDoubleField -Events $grantSummaryEvents -FieldName 'effective_granted_window_bytes')),
        ("average_effective_grant_window_bytes_healthy_expanded={0}" -f (Get-FileTransferAverageDoubleField -Events @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_expanded' }) -FieldName 'effective_granted_window_bytes')),
        ("average_effective_grant_window_bytes_healthy_file_only_soft_limited={0}" -f (Get-FileTransferAverageDoubleField -Events @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_file_only_soft_limited' }) -FieldName 'effective_granted_window_bytes')),
        ("max_effective_grant_window_bytes_healthy_expanded={0}" -f (Get-FileTransferMaxField -Events @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_expanded' }) -FieldName 'effective_granted_window_bytes')),
        ("max_effective_grant_window_bytes_healthy_file_only_soft_limited={0}" -f (Get-FileTransferMaxField -Events @($grantSummaryEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'profile' -Default '') -eq 'healthy_file_only_soft_limited' }) -FieldName 'effective_granted_window_bytes')),
        ("proactive_frontier_repair_eligible_count={0}" -f $Summary.ProactiveFrontierRepairEligibleCount),
        ("proactive_frontier_repair_requested_count={0}" -f $Summary.ProactiveFrontierRepairRequestedCount),
        ("proactive_frontier_repair_sender_received_count={0}" -f $Summary.ProactiveFrontierRepairSenderReceivedCount),
        ("proactive_frontier_repair_sender_scheduled_count={0}" -f $Summary.ProactiveFrontierRepairSenderScheduledCount),
        ("proactive_frontier_repair_sender_sent_count={0}" -f $Summary.ProactiveFrontierRepairSenderSentCount),
        ("proactive_frontier_repair_filled_count={0}" -f $Summary.ProactiveFrontierRepairFilledCount),
        ("max_frontier_repair_request_to_fill_ms={0}" -f $Summary.MaxFrontierRepairRequestToFillMs),
        ("proactive_frontier_repair_skipped_count={0}" -f $Summary.ProactiveFrontierRepairSkippedCount),
        ("proactive_frontier_repair_skipped_gap_age_below_min_count={0}" -f (@($proactiveFrontierRepairSkippedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'gap_age_below_min' }).Count)),
        ("proactive_frontier_repair_skipped_duplicate_recent_count={0}" -f (@($proactiveFrontierRepairSkippedEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'duplicate_recent' }).Count)),
        ("proactive_frontier_repair_suppressed_count={0}" -f $Summary.ProactiveFrontierRepairSuppressedCount),
        ("max_proactive_frontier_repair_gap_age_ms={0}" -f $Summary.MaxProactiveFrontierRepairGapAgeMs),
        ("proactive_repair_benign_count={0}" -f $proactiveRepairPressureStats.BenignCount),
        ("proactive_repair_grace_active_count={0}" -f $proactiveRepairPressureStats.GraceActiveCount),
        ("proactive_repair_repeated_unfilled_count={0}" -f $proactiveRepairPressureStats.RepeatedUnfilledCount),
        ("proactive_repair_hard_limited_count={0}" -f $proactiveRepairPressureStats.HardLimitedCount),
        ("proactive_repair_hard_limited_during_grace_count={0}" -f $proactiveRepairPressureStats.HardLimitedDuringGraceCount),
        ("max_proactive_repair_age_ms={0}" -f $proactiveRepairPressureStats.MaxAgeMs),
        ("max_same_frontier_unfilled_ms={0}" -f $proactiveRepairPressureStats.MaxSameFrontierUnfilledMs),
        ("request_timeout_count={0}" -f $Summary.RequestTimeoutCount),
        ("repair_set_requested_count={0}" -f $Summary.RepairSetRequestedCount),
        ("retry_requested_count={0}" -f $Summary.RetryRequestedCount),
        '',
        'throughput_decomposition_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events @($senderEvents + $senderPipelineEvents + $senderFeedEvents + $senderCacheEvents + $receiverFeedbackEvents + $receiverEvents + $gapStallEvents + $sparseEvents + $bridgeBulkEvents + $externalHealthEvents + $inboundDeliveryEvents + $inboundEnvelopeReceivedEvents + $inboundEnvelopeDropEvents + $receiveStallDetectedEvents + $receiveStallRecoveryStartedEvents + $receiveStallRecoveryCompletedEvents + $receiveStallRecoveryFailedEvents + $receiveStallRecoveryCooldownBypassedEvents + $receiveStallRecoveryReceiveResumedEvents + $controlReceiveDegradedEvents + $controlReceiveRecoverySuppressedEvents + $receiveLivenessEvents + $reorderPolicyEvents + $grantSummaryEvents + $proactiveFrontierRepairEvents + $v4SenderPumpEvents + $v4StateSentEvents + $v4StateReceivedEvents + $v4RepairScheduledEvents + $v4RepairSentEvents + $v4CompleteSentEvents + $v4CompleteReceivedEvents + $v4FeedbackFirstSuccessEvents + $v4FeedbackBothFailedEvents + $v6SenderWaitingForRequestsEvents + $v6ReceiverRequestWindowEvents + $v6ReceiverStateSentEvents + $v6ReceiverStateReceivedEvents + $v6UnsolicitedChunkIgnoredEvents | Sort-Object Sequence) -Limit 70)
}

function New-FileTransferStabilityGateSummaryLines {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$GateResult
    )

    $gapStallEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_gap_stall_summary'))
    $progressTimeoutWithReceiverGapStall = if ($Summary.LiveProgressTimeoutCount -gt 0 -and $gapStallEvents.Count -gt 0) { 1 } else { 0 }
    $warningCap = Get-FileTransferGateWarningCap -GateResult $GateResult
    $fallbackDiagnostics = Get-FileTransferGateFallbackDiagnostics -GateResult $GateResult
    $recoveryClassification = Get-FileTransferRecoveryFailureClassification -Summary $Summary

    return @(
        ("verdict={0}" -f $GateResult.Verdict),
        ("gate_status={0}" -f $GateResult.GateStatus),
        ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
        ("recovery_failure_class={0}" -f $recoveryClassification.Class),
        ("runtime_unlock_offer_not_observed_count={0}" -f $recoveryClassification.RuntimeUnlockOfferNotObservedCount),
        ("runtime_unlock_retry_scheduled_count={0}" -f $recoveryClassification.RuntimeUnlockRetryScheduledCount),
        ("runtime_unlock_retry_queued_behind_active_negotiation_count={0}" -f $recoveryClassification.RuntimeUnlockRetryQueuedBehindActiveNegotiationCount),
        ("session_liveness_timeout_after_runtime_unlock_count={0}" -f $recoveryClassification.SessionLivenessTimeoutAfterRuntimeUnlockCount),
        ("hard_failure_count={0}" -f $GateResult.HardFailures.Count),
        ("warning_count={0}" -f $GateResult.Warnings.Count),
        ("warning_cap_policy={0}" -f ($(if ($null -ne $warningCap) { $warningCap.Policy } else { 'strict_small' }))),
        ("warning_cap_count_unit={0}" -f ($(if ($null -ne $warningCap) { $warningCap.CountUnit } else { 'incident' }))),
        ("warning_cap_count_limit={0}" -f ($(if ($null -ne $warningCap) { $warningCap.CountLimit } else { 3 }))),
        ("warning_cap_rate_limit_per_second={0}" -f ($(if ($null -ne $warningCap) { $warningCap.RateLimitPerSecond } else { '0.05' }))),
        ("warning_kind_counts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.KindCounts)) { $warningCap.KindCounts } else { '(none)' }))),
        ("warning_kind_raw_event_counts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.RawKindCounts)) { $warningCap.RawKindCounts } else { '(none)' }))),
        ("warning_kind_rates_per_second={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.KindRatesPerSecond)) { $warningCap.KindRatesPerSecond } else { '(none)' }))),
        ("warning_cap_contexts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.KindContexts)) { $warningCap.KindContexts } else { '(none)' }))),
        ("warning_cap_exceeded_kinds={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.ExceededKindsText)) { $warningCap.ExceededKindsText } else { '(none)' }))),
        ("warning_cap_exceeded_contexts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.ExceededContextsText)) { $warningCap.ExceededContextsText } else { '(none)' }))),
        ("warning_cap_exempted_kinds={0}" -f ($(if ($null -ne $warningCap -and $null -ne $warningCap.PSObject.Properties['ExemptedKindsText'] -and -not [string]::IsNullOrWhiteSpace($warningCap.ExemptedKindsText)) { $warningCap.ExemptedKindsText } else { '(none)' }))),
        ("fallback_v6_terminal_missing_reason={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.TerminalMissingReason } else { '(none)' }))),
        ("fallback_v6_last_committed_chunk_index={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.LastCommittedChunkIndex } else { -1 }))),
        ("fallback_v6_highest_observed_chunk_index={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.HighestObservedChunkIndex } else { -1 }))),
        ("fallback_v6_oldest_unrecovered_gap_age_ms={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.OldestUnrecoveredGapAgeMs } else { -1 }))),
        ("fallback_v6_chunk_send_timeout_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.V6ChunkSendTimeoutCount } else { 0 }))),
        ("fallback_v6_frontier_request_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.FrontierRequestCount } else { 0 }))),
        ("fallback_v6_receiver_state_deferred_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.ReceiverStateDeferredCount } else { 0 }))),
        ("fallback_v6_receiver_state_coalesced_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.ReceiverStateCoalescedCount } else { 0 }))),
        ("fallback_v6_sender_repair_active_evidence_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.SenderRepairActiveEvidenceCount } else { 0 }))),
        ("fallback_v6_sender_still_repairing={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.SenderStillRepairing } else { 0 }))),
        ("next_artifact={0}" -f $GateResult.NextArtifact),
        ("gui_progress_timeout_count={0}" -f $Summary.LiveProgressTimeoutCount),
        ("terminal_missing_after_progress_timeout={0}" -f $Summary.TerminalMissingAfterProgressTimeout),
        ("progress_timeout_with_receiver_gap_stall={0}" -f $progressTimeoutWithReceiverGapStall),
        '',
        'hard_failures:'
    ) + ($(if ($GateResult.HardFailures.Count -gt 0) { @($GateResult.HardFailures) } else { @('(none)') })) + @(
        '',
        'warnings:'
    ) + ($(if ($GateResult.Warnings.Count -gt 0) { @($GateResult.Warnings) } else { @('(none)') })) + @(
        '',
        'top_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $GateResult.EvidenceEvents -Limit 30)
}

function New-FileTransferV4PromotionDecisionLines {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$SafeBaselineArtifactDir = ''
    )

    $operatorVerdict = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt')
    $liveSummary = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt')
    $localSummary = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-local-soak-summary.txt')
    $throughput = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'throughput-summary.txt')
    $decomposition = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'throughput-decomposition-summary.txt')
    $payload = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'payload-efficiency-summary.txt')
    $protocol = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'protocol-shape-summary.txt')
    $bridge = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'bridge-bulk-summary.txt')
    $baseline = Read-FileTransferPromotionKeyValueArtifact -Path (Join-Path $ArtifactDir 'baseline-comparison.txt')

    $safeSummary = @{}
    if (-not [string]::IsNullOrWhiteSpace($SafeBaselineArtifactDir)) {
        $safeLiveSummaryPath = Join-Path $SafeBaselineArtifactDir 'filetransfer-live-nkn-summary.txt'
        $safeLocalSummaryPath = Join-Path $SafeBaselineArtifactDir 'filetransfer-local-soak-summary.txt'
        if (Test-Path -LiteralPath $safeLiveSummaryPath -PathType Leaf) {
            $safeSummary = Read-FileTransferPromotionKeyValueArtifact -Path $safeLiveSummaryPath
        }
        elseif (Test-Path -LiteralPath $safeLocalSummaryPath -PathType Leaf) {
            $safeSummary = Read-FileTransferPromotionKeyValueArtifact -Path $safeLocalSummaryPath
        }
    }

    $sources = @($liveSummary, $localSummary, $throughput, $decomposition, $payload, $protocol, $bridge)
    $verdict = Get-FileTransferPromotionValue -Sources @($operatorVerdict, $liveSummary, $localSummary) -Name 'verdict' -Default '(unknown)'
    $dataProtocolVersion = Get-FileTransferPromotionValue -Sources @($liveSummary, $localSummary, $throughput, $decomposition, $baseline) -Name 'data_protocol_version' -Default '(unknown)'
    if ($dataProtocolVersion -eq '(unknown)' -and $baseline.ContainsKey('current_data_protocol_version')) {
        $dataProtocolVersion = [string]$baseline['current_data_protocol_version']
    }

    $payloadProfile = Get-FileTransferPromotionValue -Sources @($liveSummary, $localSummary, $payload) -Name 'payload_efficiency_profile' -Default '(unknown)'
    $limiter = Get-FileTransferPromotionValue -Sources @($decomposition) -Name 'likely_limiter' -Default 'inconclusive'
    $targetGoodput = $script:FileTransferRegularNknTargetGoodputBytesPerSecond
    $currentAverageGoodput = ConvertTo-FileTransferPromotionDouble -Values $liveSummary -Name 'average_goodput_bytes_per_second'
    if ($currentAverageGoodput -le 0) {
        $currentAverageGoodput = ConvertTo-FileTransferPromotionDouble -Values $localSummary -Name 'average_goodput_bytes_per_second'
    }
    if ($currentAverageGoodput -le 0) {
        $currentAverageGoodput = ConvertTo-FileTransferPromotionDouble -Values $decomposition -Name 'cycle_goodput_average_bytes_per_second'
    }

    $safeAverageGoodput = ConvertTo-FileTransferPromotionDouble -Values $safeSummary -Name 'average_goodput_bytes_per_second'
    $currentMatrix = Get-FileTransferPromotionLiveMatrixStats -ArtifactDir $ArtifactDir
    $safeMatrix = Get-FileTransferPromotionLiveMatrixStats -ArtifactDir $SafeBaselineArtifactDir
    $longProofMatrixComplete = if ($currentMatrix.MatrixComplete -eq 1 -or $safeMatrix.MatrixComplete -eq 1) { 1 } else { 0 }
    $longProofAverageGoodput = if ($currentMatrix.MatrixComplete -eq 1) {
        $currentAverageGoodput
    }
    elseif ($safeMatrix.MatrixComplete -eq 1) {
        $safeAverageGoodput
    }
    else {
        $currentAverageGoodput
    }
    $goodputTargetMet = if ($longProofAverageGoodput -ge $targetGoodput) { 1 } else { 0 }

    $safeBaselineAvailable = if ($baseline.ContainsKey('safe_baseline_available')) { [string]$baseline['safe_baseline_available'] } else { '0' }
    $baselineProtocolMismatch = if ($baseline.ContainsKey('baseline_protocol_mismatch')) { [string]$baseline['baseline_protocol_mismatch'] } else { '0' }
    $baselineRegressionFailed = if ($baseline.ContainsKey('regression_failed')) { [string]$baseline['regression_failed'] } else { '0' }
    $safeDataProtocolVersion = if ($baseline.ContainsKey('safe_data_protocol_version')) { [string]$baseline['safe_data_protocol_version'] } else { '(unknown)' }
    $sameProtocolV6BaselinePass = if (
        $safeBaselineAvailable -eq '1' -and
        $baselineProtocolMismatch -eq '0' -and
        $baselineRegressionFailed -eq '0' -and
        $dataProtocolVersion -eq '6' -and
        $safeDataProtocolVersion -eq '6') {
        1
    }
    else {
        0
    }

    $hardCounterNames = @(
        'payload_rejected_count',
        'decode_failure_count',
        'message_rejected_count',
        'bridge_bulk_send_failure_count',
        'bridge_bulk_queue_clear_count',
        'gui_progress_timeout_count',
        'terminal_missing_after_progress_timeout',
        'v4_feedback_both_failed_count',
        'v4_sender_failed_count',
        'v4_receiver_failed_count',
        'legacy_data_protocol_started_count',
        'unexpected_legacy_data_frame_during_v4_count')
    $hardCounterCount = 0
    foreach ($counterName in $hardCounterNames) {
        if ((ConvertTo-FileTransferPromotionDouble -Values $liveSummary -Name $counterName) -gt 0 -or
            (ConvertTo-FileTransferPromotionDouble -Values $localSummary -Name $counterName) -gt 0 -or
            (ConvertTo-FileTransferPromotionDouble -Values $protocol -Name $counterName) -gt 0 -or
            (ConvertTo-FileTransferPromotionDouble -Values $bridge -Name $counterName) -gt 0) {
            $hardCounterCount++
        }
    }
    $progressTimeoutCounter = [Math]::Max(
        (ConvertTo-FileTransferPromotionDouble -Values $liveSummary -Name 'gui_progress_timeout_count'),
        (ConvertTo-FileTransferPromotionDouble -Values $decomposition -Name 'gui_progress_timeout_count'))
    $cleanCorrectness = $verdict -eq 'PASS' -and $hardCounterCount -eq 0
    $decision = 'hold_inconclusive'
    $status = 'hold'
    $reason = 'inconclusive'
    $nextFocus = 'fix_harness_or_analyzer_evidence'

    if ($limiter -eq 'filetransfer_data_session_dispatch_missing') {
        $reason = 'filetransfer_data_session_dispatch_missing'
        $nextFocus = 'transport_data_session_lifecycle_dispatch'
    }
    elseif ($dataProtocolVersion -ne '6') {
        $reason = 'non_v6_protocol'
    }
    elseif ($progressTimeoutCounter -gt 0 -or
        (Get-FileTransferPromotionValue -Sources @($liveSummary, $decomposition) -Name 'terminal_missing_after_progress_timeout' -Default '0') -ne '0') {
        $reason = 'progress_timeout_incomplete_long_proof'
        switch ($limiter) {
            'v4_frontier_tail_repair_needed' {
                $nextFocus = 'frontier_tail_missing_range_generation'
            }
            'v4_frontier_tail_repair_not_filled' {
                $nextFocus = 'frontier_tail_repair_fill_delivery'
            }
            'v4_missing_range_repair_limited' {
                $nextFocus = 'missing_range_request_to_fill_latency'
            }
            'v4_repair_requested_not_received_by_sender' {
                $nextFocus = 'missing_range_repair_request_delivery'
            }
            'v4_repair_sent_not_observed_by_receiver' {
                $nextFocus = 'missing_range_repair_transport_delivery'
            }
            'v4_repair_observed_but_not_accepted' {
                $nextFocus = 'missing_range_repair_receiver_acceptance'
            }
            'v4_repair_accepted_but_frontier_not_advanced' {
                $nextFocus = 'missing_range_repair_frontier_advancement'
            }
            'v4_missing_range_due_state_mismatch' {
                $nextFocus = 'missing_range_state_reporting_consistency'
            }
            default {
                $nextFocus = 'fix_harness_or_analyzer_evidence'
            }
        }
    }
    elseif (-not $cleanCorrectness) {
        $reason = 'hard_failure_blocks_promotion'
    }
    elseif ($longProofMatrixComplete -ne 1) {
        $reason = 'long_live_matrix_incomplete'
    }
    elseif ($goodputTargetMet -ne 1) {
        switch ($limiter) {
            'v4_sender_pump_underfed' {
                $decision = 'iterate_sender_pump'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'sender_pump_scheduling_feed_capacity'
            }
            'v4_state_feedback_limited' {
                $decision = 'iterate_state_feedback'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'state_feedback_delivery_epoch_handling'
            }
            'v4_missing_range_repair_limited' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_request_to_fill_latency'
            }
            'v4_frontier_tail_repair_needed' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'frontier_tail_missing_range_generation'
            }
            'v4_frontier_tail_repair_not_filled' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'frontier_tail_repair_fill_delivery'
            }
            'v4_missing_range_repair_spam_limited' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_deduplication'
            }
            'v4_repair_sent_but_not_filled' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_fill_delivery'
            }
            'v4_repair_request_not_served' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_sender_service'
            }
            'v4_repair_requested_not_received_by_sender' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_request_delivery'
            }
            'v4_repair_sent_not_observed_by_receiver' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_transport_delivery'
            }
            'v4_repair_observed_but_not_accepted' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_receiver_acceptance'
            }
            'v4_repair_accepted_but_frontier_not_advanced' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_repair_frontier_advancement'
            }
            'v4_missing_range_due_state_mismatch' {
                $decision = 'iterate_missing_range_repair'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'missing_range_state_reporting_consistency'
            }
            'nkn_bulk_underutilized' {
                $decision = 'iterate_nkn_bulk'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'bridge_nkn_bulk_utilization'
            }
            'external_transport_limited' {
                $decision = 'iterate_external_transport'
                $status = 'iterate'
                $reason = 'goodput_target_not_met'
                $nextFocus = 'nkn_bridge_receive_health'
            }
            default {
                $reason = 'goodput_target_not_met_with_inconclusive_limiter'
            }
        }
    }
    elseif ($sameProtocolV6BaselinePass -ne 1) {
        if ($safeBaselineAvailable -ne '1') {
            $reason = 'baseline_rerun_required'
        }
        elseif ($baselineProtocolMismatch -ne '0') {
            $reason = 'baseline_protocol_mismatch_report_only'
        }
        elseif ($baselineRegressionFailed -ne '0') {
            $reason = 'safe_baseline_regression'
        }
        else {
            $reason = 'same_protocol_v6_baseline_not_confirmed'
        }
    }
    else {
        $decision = 'promote_v6_file_only'
        $status = 'promote'
        $reason = 'long_proof_and_baseline_clean'
        $nextFocus = 'capture_safe_v6_file_only_baseline'
    }

    return @(
        ("decision={0}" -f $decision),
        ("promotion_status={0}" -f $status),
        ("reason={0}" -f $reason),
        ("next_focus={0}" -f $nextFocus),
        ("required_live_matrix=16MiB,64MiB_x2"),
        ("required_cycle_count=4"),
        ("target_goodput_bytes_per_second={0}" -f $targetGoodput.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)),
        ("data_protocol_version={0}" -f $dataProtocolVersion),
        ("payload_efficiency_profile={0}" -f $payloadProfile),
        ("operator_verdict={0}" -f $verdict),
        ("likely_limiter={0}" -f $limiter),
        ("current_average_goodput_bytes_per_second={0}" -f $currentAverageGoodput.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)),
        ("long_proof_average_goodput_bytes_per_second={0}" -f $longProofAverageGoodput.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)),
        ("goodput_target_met={0}" -f $goodputTargetMet),
        ("current_long_proof_cycle_count={0}" -f $currentMatrix.CycleCount),
        ("current_long_proof_completed_cycle_count={0}" -f $currentMatrix.CompletedCount),
        ("current_long_proof_16m_completed_count={0}" -f $currentMatrix.SixteenMiBCompletedCount),
        ("current_long_proof_64m_completed_count={0}" -f $currentMatrix.SixtyFourMiBCompletedCount),
        ("current_long_proof_matrix_complete={0}" -f $currentMatrix.MatrixComplete),
        ("safe_long_proof_cycle_count={0}" -f $safeMatrix.CycleCount),
        ("safe_long_proof_completed_cycle_count={0}" -f $safeMatrix.CompletedCount),
        ("safe_long_proof_16m_completed_count={0}" -f $safeMatrix.SixteenMiBCompletedCount),
        ("safe_long_proof_64m_completed_count={0}" -f $safeMatrix.SixtyFourMiBCompletedCount),
        ("safe_long_proof_matrix_complete={0}" -f $safeMatrix.MatrixComplete),
        ("long_proof_matrix_complete={0}" -f $longProofMatrixComplete),
        ("safe_baseline_available={0}" -f $safeBaselineAvailable),
        ("same_protocol_v6_baseline_pass={0}" -f $sameProtocolV6BaselinePass),
        ("baseline_protocol_mismatch={0}" -f $baselineProtocolMismatch),
        ("baseline_regression_failed={0}" -f $baselineRegressionFailed),
        ("safe_data_protocol_version={0}" -f $safeDataProtocolVersion),
        ("promotion_blocking_hard_counter_count={0}" -f $hardCounterCount),
        ("v4_feedback_both_failed_count={0}" -f (Get-FileTransferPromotionValue -Sources @($liveSummary, $protocol) -Name 'v4_feedback_both_failed_count' -Default '0')),
        ("v4_sender_failed_count={0}" -f (Get-FileTransferPromotionValue -Sources @($liveSummary, $protocol) -Name 'v4_sender_failed_count' -Default '0')),
        ("v4_receiver_failed_count={0}" -f (Get-FileTransferPromotionValue -Sources @($liveSummary, $protocol) -Name 'v4_receiver_failed_count' -Default '0')),
        ("legacy_data_protocol_started_count={0}" -f (Get-FileTransferPromotionValue -Sources @($liveSummary, $protocol) -Name 'legacy_data_protocol_started_count' -Default '0')),
        ("unexpected_legacy_data_frame_during_v4_count={0}" -f (Get-FileTransferPromotionValue -Sources @($liveSummary, $protocol) -Name 'unexpected_legacy_data_frame_during_v4_count' -Default '0'))
    )
}

function Write-FileTransferV4PromotionDecision {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$SafeBaselineArtifactDir = ''
    )

    $lines = New-FileTransferV4PromotionDecisionLines -ArtifactDir $ArtifactDir -SafeBaselineArtifactDir $SafeBaselineArtifactDir
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'v4-promotion-decision.txt' -Lines $lines | Out-Null

    $object = [ordered]@{}
    foreach ($line in @($lines)) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $object[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
    }

    $object | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'v4-promotion-decision.json') -Encoding UTF8
}

function Get-FileTransferRecoveryFailureClassification {
    param(
        [Parameter(Mandatory = $true)]$Summary
    )

    [object[]]$events = @()
    if ($null -ne $Summary.PSObject.Properties['AllEvents']) {
        $events = @($Summary.AllEvents)
    }

    if ($events.Count -eq 0) {
        $events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    $runtimeUnlockOfferNotObservedEvents = @($events | Where-Object {
        $_.EventName -eq 'tuna_acceleration_activation_offer_not_observed' -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'trigger' -Default '') -eq 'runtime_unlock' -or
            (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -like '*runtime_unlock*' -or
            (Get-FileTransferEventField -Event $_ -Name 'retry_reason' -Default '') -like '*runtime_unlock*'
        )
    })
    $runtimeUnlockRetryScheduledEvents = @($events | Where-Object {
        $_.EventName -eq 'tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled'
    })
    $runtimeUnlockRetryQueuedBehindActiveNegotiationEvents = @($runtimeUnlockRetryScheduledEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'queued_behind_active_negotiation' -Default '0') -eq '1'
    })
    $sessionLivenessTimeoutEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout' })
    $peerDisconnectedTerminalEvents = @($events | Where-Object {
        ($_.EventName -eq 'file_transfer_outbound_terminal' -or
         $_.EventName -eq 'file_transfer_inbound_terminal' -or
         $_.EventName -eq 'transfer_terminal') -and
        (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '') -eq 'peer_disconnected'
    })
    $fileTunaV6Events = @($events | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v6'
    })

    $routeChanges = New-Object System.Collections.Generic.List[string]
    $lastRoute = ''
    foreach ($event in @($events | Sort-Object Sequence)) {
        if ($event.EventName -ne 'filetransfer_route_selected') {
            continue
        }

        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        if ([string]::IsNullOrWhiteSpace($route) -or $route -eq $lastRoute) {
            continue
        }

        $routeChanges.Add($route) | Out-Null
        $lastRoute = $route
    }

    $class = '(none)'
    if ($fileTunaV6Events.Count -gt 0) {
        $class = 'active_file_tuna_v6_evidence'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0 -and
        $runtimeUnlockRetryScheduledEvents.Count -gt 0 -and
        $runtimeUnlockRetryQueuedBehindActiveNegotiationEvents.Count -gt 0 -and
        $sessionLivenessTimeoutEvents.Count -gt 0 -and
        $peerDisconnectedTerminalEvents.Count -gt 0 -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast') {
        $class = 'runtime_unlock_recovery_coordination'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0 -and $sessionLivenessTimeoutEvents.Count -gt 0) {
        $class = 'runtime_unlock_liveness_timeout'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0) {
        $class = 'runtime_unlock_offer_not_observed'
    }
    elseif ($sessionLivenessTimeoutEvents.Count -gt 0) {
        $class = 'session_liveness_timeout'
    }

    [pscustomobject]@{
        Class = $class
        RuntimeUnlockOfferNotObservedCount = $runtimeUnlockOfferNotObservedEvents.Count
        RuntimeUnlockRetryScheduledCount = $runtimeUnlockRetryScheduledEvents.Count
        RuntimeUnlockRetryQueuedBehindActiveNegotiationCount = $runtimeUnlockRetryQueuedBehindActiveNegotiationEvents.Count
        SessionLivenessTimeoutAfterRuntimeUnlockCount = if ($runtimeUnlockOfferNotObservedEvents.Count -gt 0) { $sessionLivenessTimeoutEvents.Count } else { 0 }
    }
}

function Write-FileTransferDiagnosticsArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$GateResult,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None',
        [switch]$IncludeRawSlices
    )

    $analyzedFiles = if ($Summary.LogFiles.Count -gt 0) { $Summary.LogFiles -join ';' } else { '(none)' }
    $warningKinds = @(@(
        foreach ($warning in @($GateResult.Warnings)) {
            $text = [string]$warning
            if ($text -eq 'external bridge/NKN health churn overlapped the completed transfer') {
                'external_transport_churn'
            }
            elseif ($text -eq 'recovered post-Tuna fallback bridge queue clear overlapped the completed transfer') {
                'recovered_post_tuna_fallback_bridge_clear'
            }
            elseif ($text -eq 'recovered runtime-unlock bridge queue clear overlapped the completed transfer' -or
                $text -eq 'recovered runtime unlock bridge queue clear overlapped the completed transfer') {
                'recovered_runtime_unlock_bridge_clear'
            }
            elseif ($text -eq 'post-Tuna fallback V6 send timeout churn recovered before terminal completion') {
                'fallback_v6_send_timeout_churn'
            }
            elseif ($text -eq 'post-Tuna fallback frontier repair churn recovered before terminal completion') {
                'fallback_frontier_repair_churn'
            }
            elseif ($text -eq 'post-Tuna fallback receiver state churn recovered before terminal completion') {
                'fallback_receiver_state_churn'
            }
            elseif ($text -eq 'screen-share media pressure overlapped the completed transfer') {
                'cohabitation_pressure'
            }
            elseif ($text -eq 'repair/reorder/degraded pressure recovered before terminal completion') {
                'recovered_pressure'
            }
            elseif ($text -eq 'progress_timeout_with_receiver_gap_stall') {
                'progress_timeout_with_receiver_gap_stall'
            }
            elseif (-not [string]::IsNullOrWhiteSpace($text)) {
                ($text.ToLowerInvariant() -replace '[^a-z0-9]+', '_' -replace '^_+|_+$', '')
            }
        }
    ) | Select-Object -Unique)
    $warningCap = Get-FileTransferGateWarningCap -GateResult $GateResult
    $fallbackDiagnostics = Get-FileTransferGateFallbackDiagnostics -GateResult $GateResult
    $liveRouteProof = Get-FileTransferLiveRouteEpochProof -TransferEvents $Summary.TransferEvents -Mode $LiveRouteProofMode
    $recoveryClassification = Get-FileTransferRecoveryFailureClassification -Summary $Summary

    $verdictLines = @(
        ("verdict={0}" -f $GateResult.Verdict),
        ("gate_status={0}" -f $GateResult.GateStatus),
        ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
        ("next_artifact={0}" -f $GateResult.NextArtifact),
        ("recovery_failure_class={0}" -f $recoveryClassification.Class),
        ("runtime_unlock_offer_not_observed_count={0}" -f $recoveryClassification.RuntimeUnlockOfferNotObservedCount),
        ("runtime_unlock_retry_scheduled_count={0}" -f $recoveryClassification.RuntimeUnlockRetryScheduledCount),
        ("runtime_unlock_retry_queued_behind_active_negotiation_count={0}" -f $recoveryClassification.RuntimeUnlockRetryQueuedBehindActiveNegotiationCount),
        ("session_liveness_timeout_after_runtime_unlock_count={0}" -f $recoveryClassification.SessionLivenessTimeoutAfterRuntimeUnlockCount),
        ("observed_start_utc={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.FirstTimestamp)) { '(unknown)' } else { $Summary.FirstTimestamp }))),
        ("observed_end_utc={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.LastTimestamp)) { '(unknown)' } else { $Summary.LastTimestamp }))),
        ("analyzed_files={0}" -f $analyzedFiles),
        ("hard_failure_count={0}" -f $GateResult.HardFailures.Count),
        ("warning_count={0}" -f $GateResult.Warnings.Count),
        ("warning_kinds={0}" -f ($(if ($warningKinds.Count -gt 0) { $warningKinds -join ',' } else { '(none)' }))),
        ("warning_cap_policy={0}" -f ($(if ($null -ne $warningCap) { $warningCap.Policy } else { 'strict_small' }))),
        ("warning_cap_count_unit={0}" -f ($(if ($null -ne $warningCap) { $warningCap.CountUnit } else { 'incident' }))),
        ("warning_cap_count_limit={0}" -f ($(if ($null -ne $warningCap) { $warningCap.CountLimit } else { 3 }))),
        ("warning_cap_rate_limit_per_second={0}" -f ($(if ($null -ne $warningCap) { $warningCap.RateLimitPerSecond } else { '0.05' }))),
        ("warning_kind_counts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.KindCounts)) { $warningCap.KindCounts } else { '(none)' }))),
        ("warning_kind_raw_event_counts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.RawKindCounts)) { $warningCap.RawKindCounts } else { '(none)' }))),
        ("warning_kind_rates_per_second={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.KindRatesPerSecond)) { $warningCap.KindRatesPerSecond } else { '(none)' }))),
        ("warning_cap_contexts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.KindContexts)) { $warningCap.KindContexts } else { '(none)' }))),
        ("warning_cap_exceeded_kinds={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.ExceededKindsText)) { $warningCap.ExceededKindsText } else { '(none)' }))),
        ("warning_cap_exceeded_contexts={0}" -f ($(if ($null -ne $warningCap -and -not [string]::IsNullOrWhiteSpace($warningCap.ExceededContextsText)) { $warningCap.ExceededContextsText } else { '(none)' }))),
        ("warning_cap_exempted_kinds={0}" -f ($(if ($null -ne $warningCap -and $null -ne $warningCap.PSObject.Properties['ExemptedKindsText'] -and -not [string]::IsNullOrWhiteSpace($warningCap.ExemptedKindsText)) { $warningCap.ExemptedKindsText } else { '(none)' }))),
        ("fallback_v6_terminal_missing_reason={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.TerminalMissingReason } else { '(none)' }))),
        ("fallback_v6_last_committed_chunk_index={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.LastCommittedChunkIndex } else { -1 }))),
        ("fallback_v6_highest_observed_chunk_index={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.HighestObservedChunkIndex } else { -1 }))),
        ("fallback_v6_oldest_unrecovered_gap_age_ms={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.OldestUnrecoveredGapAgeMs } else { -1 }))),
        ("fallback_v6_chunk_send_timeout_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.V6ChunkSendTimeoutCount } else { 0 }))),
        ("fallback_v6_frontier_request_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.FrontierRequestCount } else { 0 }))),
        ("fallback_v6_receiver_state_deferred_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.ReceiverStateDeferredCount } else { 0 }))),
        ("fallback_v6_receiver_state_coalesced_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.ReceiverStateCoalescedCount } else { 0 }))),
        ("fallback_v6_sender_repair_active_evidence_count={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.SenderRepairActiveEvidenceCount } else { 0 }))),
        ("fallback_v6_sender_still_repairing={0}" -f ($(if ($null -ne $fallbackDiagnostics) { $fallbackDiagnostics.SenderStillRepairing } else { 0 }))),
        ("live_route_epoch_proof_mode={0}" -f $LiveRouteProofMode),
        ("live_route_epoch_proof_verdict={0}" -f $liveRouteProof.Verdict),
        ("live_route_epoch_metadata_missing_count={0}" -f $liveRouteProof.MetadataMissingCount),
        ("live_route_epoch_transport_only_count={0}" -f $liveRouteProof.TransportOnlyCount),
        '',
        'hard_failures:'
    ) + ($(if ($GateResult.HardFailures.Count -gt 0) { @($GateResult.HardFailures) } else { @('(none)') })) + @(
        '',
        'warnings:'
    ) + ($(if ($GateResult.Warnings.Count -gt 0) { @($GateResult.Warnings) } else { @('(none)') })) + @(
        '',
        'top_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $GateResult.EvidenceEvents -Limit 30)

    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'filetransfer-operator-verdict.txt' -Lines $verdictLines | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'transfer-terminal-summary.txt' -Lines (New-FileTransferTerminalSummaryLines -Summary $Summary -GateResult $GateResult) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'throughput-summary.txt' -Lines (New-FileTransferThroughputSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'throughput-decomposition-summary.txt' -Lines (New-FileTransferThroughputDecompositionSummaryLines -Summary $Summary -ArtifactDir $ArtifactDir) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'payload-efficiency-summary.txt' -Lines (New-FileTransferPayloadEfficiencySummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'protocol-shape-summary.txt' -Lines (New-FileTransferProtocolShapeSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'filetransfer-route-consistency-summary.txt' -Lines (New-FileTransferRouteConsistencySummaryLines -Summary $Summary -LiveRouteProofMode $LiveRouteProofMode) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'repair-reorder-summary.txt' -Lines (New-FileTransferRepairReorderSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'transport-budget-summary.txt' -Lines (New-FileTransferTransportBudgetSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'bridge-config-summary.txt' -Lines (New-FileTransferBridgeConfigSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'bridge-bulk-summary.txt' -Lines (New-FileTransferBridgeBulkSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'coexistence-summary.txt' -Lines (New-FileTransferCoexistenceSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'external-transport-health-summary.txt' -Lines (New-FileTransferExternalTransportHealthSummaryLines -Summary $Summary) | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'stability-gates-summary.txt' -Lines (New-FileTransferStabilityGateSummaryLines -Summary $Summary -GateResult $GateResult) | Out-Null
    Write-FileTransferV4PromotionDecision -ArtifactDir $ArtifactDir | Out-Null

    if ($IncludeRawSlices) {
        $rawLines = @(
            ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
            '',
            'matched_evidence:'
        ) + (Get-FileTransferArtifactEvidenceLines -Events $Summary.EvidenceEvents -Limit 250)
        Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'raw-log-slices.txt' -Lines $rawLines | Out-Null
    }
}
