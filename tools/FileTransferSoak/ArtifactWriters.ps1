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

function Get-FileTransferInvariantText {
    param(
        $Value,
        [string]$Default = 'unknown'
    )

    if ($null -eq $Value) {
        return $Default
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text) -or $text -eq '(none)') {
        return $Default
    }

    return $text.Trim()
}

function Get-FileTransferInvariantRuntimeFamily {
    param(
        [string]$Route,
        [string]$RuntimeProfile,
        [string]$ProtocolVersion
    )

    $routeText = (Get-FileTransferInvariantText -Value $Route -Default '').ToLowerInvariant()
    $profileText = (Get-FileTransferInvariantText -Value $RuntimeProfile -Default '').ToLowerInvariant()
    if ($routeText -eq 'regular_nkn_v4_fast' -or $profileText -like '*regular_nkn_v4*') {
        return 'regular_v4'
    }

    if ($routeText -eq 'file_tuna_v4' -or $profileText -like '*file_tuna_v4*') {
        return 'file_tuna_v4'
    }

    if ($routeText -eq 'post_tuna_fallback_v6' -or $profileText -like '*post_tuna_fallback*') {
        return 'post_tuna_fallback_v6'
    }

    if ($routeText -eq 'diagnostic_regular_nkn_v6' -or $profileText -like '*diagnostic_regular_nkn_v6*') {
        return 'diagnostic_v6'
    }

    return 'unknown'
}

function Get-FileTransferInvariantFrameFamily {
    param(
        [string]$Route,
        [string]$ProtocolVersion
    )

    $routeText = (Get-FileTransferInvariantText -Value $Route -Default '').ToLowerInvariant()
    if ($routeText -eq 'regular_nkn_v4_fast' -or $routeText -eq 'file_tuna_v4') {
        return 'v4'
    }

    if ($routeText -eq 'post_tuna_fallback_v6' -or $routeText -eq 'diagnostic_regular_nkn_v6') {
        return 'v6'
    }

    $protocolText = Get-FileTransferInvariantText -Value $ProtocolVersion -Default ''
    if ($protocolText -eq '4') {
        return 'v4'
    }

    if ($protocolText -eq '6') {
        return 'v6'
    }

    return 'unknown'
}

function Get-FileTransferInvariantObservedFrameFamily {
    param([Parameter(Mandatory = $true)]$Summary)

    $hasV4 = $false
    $hasV6 = $false
    foreach ($event in @($Summary.TransferEvents)) {
        $eventName = [string]$event.EventName
        $frameType = Get-FileTransferEventField -Event $event -Name 'frame_type' -Default ''
        $protocol = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        if ($eventName -like 'filetransfer_v4_*' -or
            $frameType -like 'filetransfer.*.v4' -or
            ($protocol -eq '4' -and ($eventName -eq 'filetransfer_runtime_started' -or $eventName -eq 'filetransfer_session_opened'))) {
            $hasV4 = $true
        }

        if ($eventName -like 'filetransfer_v6_*' -or
            $frameType -like 'filetransfer.*.v6' -or
            ($protocol -eq '6' -and ($eventName -eq 'filetransfer_runtime_started' -or $eventName -eq 'filetransfer_session_opened'))) {
            $hasV6 = $true
        }
    }

    if ($hasV4 -and $hasV6) {
        return 'mixed'
    }

    if ($hasV4) {
        return 'v4'
    }

    if ($hasV6) {
        return 'v6'
    }

    return 'unknown'
}

function Get-FileTransferInvariantLatestTerminalState {
    param([object[]]$Events)

    $latest = @($Events | Sort-Object Sequence | Select-Object -Last 1)
    if ($latest.Count -eq 0) {
        return 'unknown'
    }

    return Get-FileTransferInvariantText -Value (Get-FileTransferEventField -Event $latest[0] -Name 'state' -Default '') -Default 'unknown'
}

function Get-FileTransferInvariantShaOk {
    param(
        [object[]]$TerminalEvents,
        [string]$GateVerdict
    )

    $integrityValues = @(
        $TerminalEvents |
            ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'integrity_ok' -Default '' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    if (@($integrityValues | Where-Object { [string]$_ -eq '0' -or [string]::Equals([string]$_, 'false', [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        return '0'
    }

    if (@($integrityValues | Where-Object { [string]$_ -eq '1' -or [string]::Equals([string]$_, 'true', [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        return '1'
    }

    $states = @($TerminalEvents | ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'state' -Default '' })
    $errors = @($TerminalEvents | ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)' })
    $hasCompletedTerminal = @($states | Where-Object { [string]::Equals([string]$_, 'Completed', [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    $hasTerminalFailure = @($errors | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_) -and
            -not [string]::Equals([string]$_, '(none)', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals([string]$_, 'none', [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
    if ($hasCompletedTerminal -and -not $hasTerminalFailure) {
        return '1'
    }

    if ($hasCompletedTerminal) {
        return 'unknown'
    }

    return '0'
}

function Get-FileTransferInvariantTerminalWinner {
    param(
        [object[]]$TerminalEvents,
        [string]$ShaOk
    )

    if ($TerminalEvents.Count -eq 0) {
        return 'none'
    }

    foreach ($event in @($TerminalEvents | Sort-Object Sequence)) {
        $state = Get-FileTransferEventField -Event $event -Name 'state' -Default ''
        $error = Get-FileTransferEventField -Event $event -Name 'error_code' -Default ''
        $statusMessage = Get-FileTransferEventField -Event $event -Name 'status_message' -Default ''
        if ([string]::Equals($state, 'Completed', [System.StringComparison]::OrdinalIgnoreCase) -and $ShaOk -ne '0') {
            return 'completed'
        }

        if ([string]::Equals($state, 'CanceledRemote', [System.StringComparison]::OrdinalIgnoreCase)) {
            if ([string]::Equals($statusMessage, 'session_end', [System.StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($error, 'session_end', [System.StringComparison]::OrdinalIgnoreCase)) {
                return 'remote_session_end'
            }

            return 'remote_cancel'
        }

        if ([string]::Equals($state, 'CanceledLocal', [System.StringComparison]::OrdinalIgnoreCase)) {
            if ([string]::Equals($statusMessage, 'session_end', [System.StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($error, 'session_end', [System.StringComparison]::OrdinalIgnoreCase)) {
                return 'local_session_end'
            }

            return 'local_cancel'
        }

        if ([string]::Equals($error, 'peer_disconnected', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'peer_disconnected'
        }

        if (-not [string]::IsNullOrWhiteSpace($error) -and $error -ne '(none)') {
            return 'failure'
        }
    }

    return 'unknown'
}

function Get-FileTransferInvariantRuntimeUnlockOwnerState {
    param($RecoveryClassification)

    if ($null -eq $RecoveryClassification) {
        return 'none'
    }

    if ($RecoveryClassification.RuntimeUnlockTransactionCommittedCount -gt 0) {
        return 'committed'
    }

    if ($RecoveryClassification.RuntimeUnlockTransactionFailedCount -gt 0) {
        return 'failed'
    }

    if ($RecoveryClassification.RuntimeUnlockTransactionCommitPendingCount -gt 0) {
        return 'commit_pending'
    }

    if ($RecoveryClassification.RuntimeUnlockTransactionStartedCount -gt 0) {
        return 'active'
    }

    return 'none'
}

function Get-FileTransferInvariantFallbackOwnerState {
    param(
        [string]$FinalRoute,
        $FallbackAuthorityProof
    )

    if ($null -eq $FallbackAuthorityProof) {
        if ([string]::Equals($FinalRoute, 'post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'active'
        }

        return 'none'
    }

    if ([string]::Equals($FinalRoute, 'post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($FallbackAuthorityProof.CheckpointAcceptedCount -gt 0) {
            return 'active_checkpoint_accepted'
        }

        if ($FallbackAuthorityProof.StartedCount -gt 0) {
            return 'active_checkpoint_pending'
        }

        return 'active'
    }

    if ($FallbackAuthorityProof.CompletedCount -gt 0) {
        return 'completed'
    }

    if ($FallbackAuthorityProof.StartedCount -gt 0) {
        return 'superseded_or_retired'
    }

    return 'none'
}

function Get-FileTransferInvariantFirstTerminalByTransferId {
    param([object[]]$TerminalEvents)

    $result = @{}
    foreach ($event in @($TerminalEvents | Sort-Object Sequence)) {
        $transferId = [string]$event.TransferId
        if ([string]::IsNullOrWhiteSpace($transferId) -or $result.ContainsKey($transferId)) {
            continue
        }

        $result[$transferId] = $event
    }

    return $result
}

function Test-FileTransferInvariantLateFrameEvent {
    param($Event)

    if ($null -eq $Event) {
        return $false
    }

    $eventName = [string]$Event.EventName
    if ($eventName -eq 'filetransfer_data_frame_ignored' -or
        $eventName -eq 'filetransfer_message_rejected' -or
        $eventName -eq 'filetransfer_binary_frame_sent' -or
        $eventName -eq 'filetransfer_binary_frame_received' -or
        $eventName -eq 'filetransfer_data_frame_dispatched') {
        return $true
    }

    return $false
}

function Get-FileTransferInvariantLateFrameClassification {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string]$TerminalWinner,
        [string]$ShaOk
    )

    $terminalByTransferId = Get-FileTransferInvariantFirstTerminalByTransferId -TerminalEvents @($Summary.TerminalEvents)
    $lateEvents = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($Summary.TransferEvents | Sort-Object Sequence)) {
        if (-not (Test-FileTransferInvariantLateFrameEvent -Event $event)) {
            continue
        }

        $transferId = [string]$event.TransferId
        if ([string]::IsNullOrWhiteSpace($transferId) -or -not $terminalByTransferId.ContainsKey($transferId)) {
            continue
        }

        if ($event.Sequence -gt [int]$terminalByTransferId[$transferId].Sequence) {
            $lateEvents.Add($event) | Out-Null
        }
    }

    $terminalStaleUpdateRejectedCount = 0
    if ($Summary.PSObject.Properties.Name -contains 'TerminalStaleUpdateRejectedCount') {
        $terminalStaleUpdateRejectedCount = [int]$Summary.TerminalStaleUpdateRejectedCount
    }

    if ($lateEvents.Count -le 0 -and $terminalStaleUpdateRejectedCount -le 0) {
        return [pscustomobject]@{
            Classification = 'none'
            Count = 0
            EvidenceEvents = @()
        }
    }

    $classification = if ($TerminalWinner -eq 'completed' -and $ShaOk -eq '1') {
        'benign_after_clean_completion'
    }
    else {
        'non_benign_after_terminal'
    }

    return [pscustomobject]@{
        Classification = $classification
        Count = $lateEvents.Count + $terminalStaleUpdateRejectedCount
        EvidenceEvents = @($lateEvents.ToArray() | Select-Object -First 10)
    }
}

function Get-FileTransferInvariantRouteLawResult {
    param([Parameter(Mandatory = $true)]$Summary)

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    [object[]]$routeEvents = @($Summary.TransferEvents | Where-Object { $_.EventName -eq 'filetransfer_route_selected' })

    foreach ($event in @($routeEvents | Sort-Object Sequence)) {
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $protocol = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        $runtimeProfile = Get-FileTransferEventField -Event $event -Name 'runtime_profile' -Default ''
        $frameFamily = Get-FileTransferEventField -Event $event -Name 'frame_family' -Default ''
        $diagnosticMarker = Get-FileTransferEventField -Event $event -Name 'diagnostic_regular_nkn_v6' -Default ''
        $routeOk = $true

        if ([string]::IsNullOrWhiteSpace($route) -or [string]::IsNullOrWhiteSpace($protocol)) {
            $routeOk = $false
            $findings.Add(("route law missing route/protocol metadata: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        }
        elseif ($route -eq 'regular_nkn_v4_fast') {
            $routeOk = $protocol -eq '4' -and
                ($frameFamily -eq '' -or $frameFamily -eq '(none)' -or $frameFamily -eq 'v4') -and
                ($runtimeProfile -like '*regular_nkn_v4*' -or $runtimeProfile -eq 'regular_nkn_v4_fast')
            if (-not $routeOk) {
                $findings.Add(("regular NKN route law mismatch: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            }
        }
        elseif ($route -eq 'file_tuna_v4') {
            $routeOk = $protocol -eq '4' -and
                ($frameFamily -eq '' -or $frameFamily -eq '(none)' -or $frameFamily -eq 'v4') -and
                $runtimeProfile -like '*file_tuna_v4*'
            if (-not $routeOk) {
                $findings.Add(("active Tuna route law mismatch: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            }
        }
        elseif ($route -eq 'post_tuna_fallback_v6') {
            $routeOk = $protocol -eq '6' -and
                ($frameFamily -eq '' -or $frameFamily -eq '(none)' -or $frameFamily -eq 'v6')
            if (-not $routeOk) {
                $findings.Add(("post-Tuna fallback route law mismatch: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            }
        }
        elseif ($route -eq 'diagnostic_regular_nkn_v6') {
            $routeOk = $protocol -eq '6' -and $diagnosticMarker -eq '1'
            if (-not $routeOk) {
                $findings.Add(("diagnostic regular NKN V6 route used without explicit opt-in: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            }
        }
        else {
            $routeOk = $false
            $findings.Add(("unknown or disallowed route selected: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        }

        if (-not $routeOk) {
            $evidence.Add($event) | Out-Null
        }
    }

    foreach ($event in @($Summary.TransferEvents | Sort-Object Sequence)) {
        $eventName = [string]$event.EventName
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $protocol = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        if ($eventName -like 'filetransfer_v5_*' -or $protocol -eq '5') {
            $findings.Add(("obsolete V5 evidence: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            $evidence.Add($event) | Out-Null
            continue
        }

        if ($route -eq 'file_tuna_v6') {
            $findings.Add(("active file_tuna_v6 evidence: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            $evidence.Add($event) | Out-Null
            continue
        }

        if (($eventName -eq 'filetransfer_runtime_started' -or $eventName -like 'filetransfer_v*_sender_started' -or $eventName -like 'filetransfer_v*_receiver_started') -and
            -not [string]::IsNullOrWhiteSpace($route) -and
            -not [string]::IsNullOrWhiteSpace($protocol)) {
            if (($route -eq 'regular_nkn_v4_fast' -or $route -eq 'file_tuna_v4') -and $protocol -ne '4') {
                $findings.Add(("V4 route entered non-V4 runtime: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
                $evidence.Add($event) | Out-Null
            }
            elseif (($route -eq 'post_tuna_fallback_v6' -or $route -eq 'diagnostic_regular_nkn_v6') -and $protocol -ne '6') {
                $findings.Add(("V6 route entered non-V6 runtime: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
                $evidence.Add($event) | Out-Null
            }
        }
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if ($routeEvents.Count -eq 0 -and $findings.Count -eq 0) {
        $verdict = 'legacy'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        FailureClass = if ($findings.Count -gt 0) { 'route_runtime' } else { 'none' }
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
}

function Get-FileTransferInvariantTerminalLifecycleResult {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string]$LateFrameClassification
    )

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    if ($LateFrameClassification -eq 'non_benign_after_terminal') {
        $findings.Add('late frame/control evidence after non-clean terminal state') | Out-Null
        foreach ($event in @($Summary.TransferEvents | Where-Object { Test-FileTransferInvariantLateFrameEvent -Event $_ } | Select-Object -First 10)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($Summary.TransferEvents.Count -gt 0 -and $Summary.TerminalEvents.Count -eq 0) {
        $findings.Add('insufficient terminal evidence') | Out-Null
    }

    return [pscustomobject]@{
        Verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
        FailureClass = if ($findings.Count -gt 0) { 'terminal_lifecycle' } else { 'none' }
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
}

function Get-FileTransferInvariantControlPriorityResult {
    param([Parameter(Mandatory = $true)]$Summary)

    [object[]]$events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    [object[]]$controlEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'session_end_redundant_sent' -or
                $_.EventName -eq 'filetransfer_cancel_redundant_sent' -or
                $_.EventName -eq 'filetransfer_control_priority_delivery_result' -or
                ($_.EventName -eq 'filetransfer_control_plane_delivery_result' -and
                    ((Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'cancel' -or
                     (Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'session_end' -or
                     (Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'pause' -or
                     (Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'resume'))
            }
    )

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    $peerVisibleCount = 0
    $localOnlyCount = 0
    foreach ($event in @($controlEvents | Sort-Object Sequence)) {
        $peerVisible =
            (Get-FileTransferEventInt64Field -Event $event -Name 'peer_visible_any' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $event -Name 'delivered_any' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $event -Name 'control_ack' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $event -Name 'control_copy' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $event -Name 'bulk_copy' -Default 0) -gt 0
        $underBacklog =
            (Get-FileTransferEventInt64Field -Event $event -Name 'data_backlog' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $event -Name 'bulk_backlog' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $event -Name 'under_data_backlog' -Default 0) -gt 0
        $localOnly =
            (Get-FileTransferEventInt64Field -Event $event -Name 'local_only_rejected' -Default 0) -gt 0 -or
            (
                (Get-FileTransferEventInt64Field -Event $event -Name 'control_queue' -Default 0) -gt 0 -and
                -not $peerVisible
            )

        if ($peerVisible) {
            $peerVisibleCount++
        }

        if ($localOnly) {
            $localOnlyCount++
            if ($underBacklog) {
                $findings.Add(("control-priority lifecycle delivery was local-only under data backlog: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
                $evidence.Add($event) | Out-Null
            }
        }
    }

    return [pscustomobject]@{
        Verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
        FailureClass = if ($findings.Count -gt 0) { 'lifecycle_control_priority' } else { 'none' }
        PeerVisibleCount = $peerVisibleCount
        LocalOnlyCount = $localOnlyCount
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
}

function Get-FileTransferInvariantCombinedGateResult {
    param(
        [Parameter(Mandatory = $true)]$RouteLaw,
        [Parameter(Mandatory = $true)]$TerminalLifecycle,
        [Parameter(Mandatory = $true)]$ControlPriority,
        $RuntimeOwner = $null,
        $StaleRejectedActionEffect = $null,
        $FallbackRuntimeUnlockSeparation = $null,
        $PublicationAuthorityFieldCoverage = $null,
        $FileTunaV4DataPathProof = $null
    )

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    $failureClass = 'none'
    foreach ($result in @($RouteLaw, $TerminalLifecycle, $ControlPriority, $RuntimeOwner, $StaleRejectedActionEffect, $FallbackRuntimeUnlockSeparation, $PublicationAuthorityFieldCoverage, $FileTunaV4DataPathProof)) {
        if ($null -eq $result) {
            continue
        }

        if ($result.Verdict -eq 'fail') {
            if ($failureClass -eq 'none') {
                $failureClass = $result.FailureClass
            }

            foreach ($finding in @($result.Findings)) {
                $findings.Add($finding) | Out-Null
            }
            foreach ($event in @($result.EvidenceEvents)) {
                $evidence.Add($event) | Out-Null
            }
        }
    }

    return [pscustomobject]@{
        Verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
        FailureClass = $failureClass
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
}

function Get-FileTransferInvariantRuntimeOwnerResult {
    param([Parameter(Mandatory = $true)]$Summary)

    $staleRejectedEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_runtime_owner_stale_update_rejected'))
    $workerRetiredEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_runtime_owner_workers_retired'))
    $forbiddenEffectEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_runtime_owner_forbidden_stale_effect'))

    foreach ($event in @($staleRejectedEvents)) {
        $forbidden = Get-FileTransferEventField -Event $event -Name 'forbidden_effect' -Default '0'
        if ($forbidden -eq '1' -or $forbidden -eq 'true') {
            $forbiddenEffectEvents += $event
        }
    }

    $ownerEvents = @($staleRejectedEvents + $workerRetiredEvents + $forbiddenEffectEvents)
    $runtimeOwnerGeneration = Get-FileTransferMaxField -Events $ownerEvents -FieldName 'current_runtime_generation'
    if ($runtimeOwnerGeneration -le 0) {
        $runtimeOwnerGeneration = Get-FileTransferMaxField -Events $ownerEvents -FieldName 'runtime_generation'
    }

    $terminalGeneration = Get-FileTransferMaxField -Events $ownerEvents -FieldName 'current_terminal_generation'
    if ($terminalGeneration -le 0) {
        $terminalGeneration = Get-FileTransferMaxField -Events $ownerEvents -FieldName 'terminal_generation'
    }

    $findings = New-Object System.Collections.Generic.List[string]
    if ($forbiddenEffectEvents.Count -gt 0) {
        $findings.Add(("runtime_owner_forbidden_stale_effect_count={0}" -f $forbiddenEffectEvents.Count)) | Out-Null
    }

    return [pscustomobject]@{
        Verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
        FailureClass = if ($findings.Count -eq 0) { 'none' } else { 'runtime_owner' }
        RuntimeOwnerGeneration = [int64]$runtimeOwnerGeneration
        TerminalGeneration = [int64]$terminalGeneration
        StaleUpdateRejectedCount = [int]$staleRejectedEvents.Count
        ForbiddenStaleEffectCount = [int]$forbiddenEffectEvents.Count
        WorkerRetiredCount = [int]$workerRetiredEvents.Count
        Findings = $findings
        EvidenceEvents = @($forbiddenEffectEvents | Select-Object -First 20)
    }
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
    $terminalStaleUpdateRejectedCount = 0
    if ($Summary.PSObject.Properties.Name -contains 'TerminalStaleUpdateRejectedCount') {
        $terminalStaleUpdateRejectedCount = $Summary.TerminalStaleUpdateRejectedCount
    }

    return @(
        ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
        ("verdict={0}" -f $GateResult.Verdict),
        ("inbound_terminal_count={0}" -f $Summary.InboundTerminalEvents.Count),
        ("outbound_terminal_count={0}" -f $Summary.OutboundTerminalEvents.Count),
        ("terminal_states={0}" -f (Join-FileTransferValues -Values $states)),
        ("terminal_error_codes={0}" -f (Join-FileTransferValues -Values $errors)),
        ("terminal_stale_update_rejected_count={0}" -f $terminalStaleUpdateRejectedCount),
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

function Get-FileTransferFallbackLegAuthorityProof {
    param(
        [object[]]$TransferEvents
    )

    [object[]]$authorityEvents = @(
        $TransferEvents |
            Where-Object { ([string]$_.EventName).StartsWith('filetransfer_fallback_leg_authority_', [System.StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object Sequence
    )
    [object[]]$startedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_started' })
    [object[]]$checkpointAcceptedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' })
    [object[]]$bridgeRequestedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_bridge_recovery_requested' })
    [object[]]$bridgeEscalatedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_bridge_recovery_escalated' })
    [object[]]$completedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_completed' })
    [object[]]$sendBlockedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_send_blocked' })
    [object[]]$supersededEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_superseded_by_route_hint' })
    [object[]]$proofAuthorityEvents = @(
        @($startedEvents) +
        @($checkpointAcceptedEvents) +
        @($bridgeRequestedEvents) +
        @($bridgeEscalatedEvents) +
        @($completedEvents) +
        @($sendBlockedEvents)
    )
    [object[]]$staleProofIgnoredEvents = @(
        $TransferEvents |
            Where-Object { $_.EventName -eq 'filetransfer_fallback_stale_proof_ignored' } |
            Sort-Object Sequence
    )
    [object[]]$continuousReceiverStateProofEvents = @(
        $TransferEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_control_plane_delivery_result' -and
                (Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'receiver_state' -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'protocol_version' -Default 0) -eq 6 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'leg_generation' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'live_route_epoch' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'transport_epoch' -Default -1) -ge 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'local_only_rejected' -Default 0) -le 0
            } |
            Sort-Object Sequence
    )

    $metadataMissing = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($authorityEvents)) {
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $protocol = Get-FileTransferEventInt64Field -Event $event -Name 'protocol_version' -Default 0
        $legGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
        $liveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default 0
        $transportEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'transport_epoch' -Default 0
        $checkpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
        if ($route -ne 'post_tuna_fallback_v6' -or
            $protocol -ne 6 -or
            $legGeneration -le 0 -or
            $liveRouteEpoch -le 0 -or
            $transportEpoch -lt 0 -or
            [string]::IsNullOrWhiteSpace($checkpointId)) {
            $metadataMissing.Add($event) | Out-Null
        }
    }

    $sequence = @(
        $startedEvents |
            ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'leg_generation' -Default '' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($metadataMissing.ToArray())) {
        $findings.Add(("fallback leg authority metadata missing: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        $evidence.Add($event) | Out-Null
    }

    $postTunaRouteSelected = @(
        $TransferEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_route_selected' -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6'
            }
    )
    [object[]]$postTunaOutboundRouteSelected = @(
        $postTunaRouteSelected |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound'
            }
    )
    if ($postTunaOutboundRouteSelected.Count -gt 0 -and
        $authorityEvents.Count -eq 0 -and
        $continuousReceiverStateProofEvents.Count -eq 0) {
        $findings.Add('fallback leg authority missing for post_tuna_fallback_v6 route') | Out-Null
        foreach ($event in @($postTunaOutboundRouteSelected | Select-Object -First 3)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if ($postTunaOutboundRouteSelected.Count -eq 0 -and $proofAuthorityEvents.Count -eq 0) {
        $verdict = 'none'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        Sequence = @($sequence)
        StartedCount = $startedEvents.Count
        CheckpointAcceptedCount = $checkpointAcceptedEvents.Count
        BridgeRecoveryRequestedCount = $bridgeRequestedEvents.Count
        BridgeRecoveryEscalatedCount = $bridgeEscalatedEvents.Count
        CompletedCount = $completedEvents.Count
        SendBlockedCount = $sendBlockedEvents.Count
        ContinuousReceiverStateProofCount = $continuousReceiverStateProofEvents.Count
        StaleProofIgnoredCount = $staleProofIgnoredEvents.Count
        MetadataMissingCount = $metadataMissing.Count
        Findings = $findings
        EvidenceEvents = @(@($evidence.ToArray()) + @($authorityEvents) + @($continuousReceiverStateProofEvents) | Select-Object -First 20)
    }
}

function Get-FileTransferFallbackTailReconciliationProof {
    param(
        [Parameter(Mandatory = $true)]$Summary
    )

    [object[]]$events = @($Summary.TransferEvents)
    [object[]]$postTunaRouteSelected = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_route_selected' -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6'
            }
    )
    $hasPostTunaFallbackEvidence = $postTunaRouteSelected.Count -gt 0 -or
        @($events | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' }).Count -gt 0
    [object[]]$zeroCreditBreakerEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_zero_credit_breaker' })
    [object[]]$staleFrontierRetiredEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_stale_frontier_retired' })
    [object[]]$requestedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_reconciliation_requested' })
    [object[]]$acceptedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_reconciliation_accepted' })
    [object[]]$progressForcedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced' })
    [object[]]$progressCoalescedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_progress_coalesced' })
    [object[]]$terminalProofConsumedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_terminal_proof_consumed' })
    [object[]]$quiescentYieldEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_quiescent_yield_granted' })
    [object[]]$yieldProofSentEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_yield_proof_sent' })
    [object[]]$yieldProofReceivedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_yield_proof_received' })
    [object[]]$pairedYieldGrantedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_paired_yield_granted' })
    [object[]]$ownerGenerationMismatchEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_owner_generation_mismatch' })
    [object[]]$localYieldBlockedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_local_yield_blocked' })
    [object[]]$staleYieldProofIgnoredEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_yield_proof_stale_ignored' })
    [object[]]$frontierRepairAuthorityGrantedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_frontier_repair_authority_granted' })
    [object[]]$frontierRepairAuthorityRejectedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_frontier_repair_authority_rejected' })
    [object[]]$frontierRepairDispatchedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_frontier_repair_dispatched' })
    [object[]]$frontierRepairStalledEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_frontier_repair_stalled' })
    [object[]]$frontierRepairProgressObservedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_fallback_frontier_repair_progress_observed' -or
        $_.EventName -eq 'filetransfer_v4_frontier_stall_missing_range_filled' -or
        $_.EventName -eq 'filetransfer_v4_frontier_repair_batch_applied' -or
        $_.EventName -eq 'filetransfer_frontier_gap_repair_filled'
    })
    [object[]]$outboundQuiescentYieldEvents = @(
        $quiescentYieldEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' }
    )
    [object[]]$inboundQuiescentYieldEvents = @(
        $quiescentYieldEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' }
    )
    [object[]]$quiescenceTimeoutEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_tail_quiescence_timeout' })
    [object[]]$runtimeUnlockWaitingForFallbackEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_runtime_unlock_waiting_for_fallback_survival' })
    [object[]]$runtimeUnlockProbeRejectedByFallbackEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'runtime_unlock_precommit_probe_rejected_for_fallback_survival' -or
                ($_.EventName -eq 'runtime_unlock_tuna_probe_ack_sent' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted' -Default 1) -eq 0 -and
                    ((Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -like '*fallback*'))
            }
    )
    [object[]]$runtimeUnlockProbeRejectedByInboundFallbackEvents = @(
        $runtimeUnlockProbeRejectedByFallbackEvents |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' -or
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'inbound_fallback_tail_not_yielded'
            }
    )
    [object[]]$sendSlotRetiredEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired' -and
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'tail_reconciliation_forced'
            }
    )
    [object[]]$receiveRecoveryRequestedEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'tail_reconciliation' -Default 0) -gt 0
            }
    )
    [object[]]$normalZeroCreditTailStateRefreshEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_v6_regular_nkn_state_refresh_requested' -and
                (Get-FileTransferEventField -Event $_ -Name 'priority' -Default '') -eq 'state_refresh' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'available_credit_chunks' -Default 1) -le 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'highest_accepted_chunk_index' -Default -1) -ge
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'remote_frontier_chunk_index' -Default 0)
            }
    )
    [object[]]$failureEvents = @(
        $events |
            Where-Object {
                if ($_.EventName -eq 'session_liveness_timeout' -or
                    $_.EventName -eq 'filetransfer_gui_progress_timeout') {
                    return $true
                }

                if ($_.EventName -eq 'file_transfer_inbound_terminal' -or
                    $_.EventName -eq 'file_transfer_outbound_terminal') {
                    return (
                        (Get-FileTransferEventField -Event $_ -Name 'state' -Default '') -ne 'Completed' -or
                        (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -ne '(none)'
                    )
                }

                if ($_.EventName -eq 'transfer_terminal') {
                    return (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -ne '(none)'
                }

                return $false
            }
    )

    function Test-FallbackFrontierRepairScopeMatch {
        param(
            [Parameter(Mandatory = $true)]$Anchor,
            [Parameter(Mandatory = $true)]$Candidate
        )

        foreach ($fieldName in @('transfer_id', 'session_id')) {
            $anchorValue = Get-FileTransferEventField -Event $Anchor -Name $fieldName -Default ''
            $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
            if (-not [string]::IsNullOrWhiteSpace($anchorValue) -and
                $anchorValue -ne '(none)' -and
                -not [string]::IsNullOrWhiteSpace($candidateValue) -and
                $candidateValue -ne '(none)' -and
                $anchorValue -ne $candidateValue) {
                return $false
            }
        }

        return $true
    }

    function Test-FallbackFrontierRepairTupleMatch {
        param(
            [Parameter(Mandatory = $true)]$Anchor,
            [Parameter(Mandatory = $true)]$Candidate
        )

        if (-not (Test-FallbackFrontierRepairScopeMatch -Anchor $Anchor -Candidate $Candidate)) {
            return $false
        }

        $anchorRepairKey = Get-FileTransferEventField -Event $Anchor -Name 'repair_request_key' -Default ''
        $candidateRepairKey = Get-FileTransferEventField -Event $Candidate -Name 'repair_request_key' -Default ''
        if ([string]::IsNullOrWhiteSpace($anchorRepairKey) -or
            $anchorRepairKey -eq '(none)' -or
            [string]::IsNullOrWhiteSpace($candidateRepairKey) -or
            $candidateRepairKey -eq '(none)') {
            return $false
        }

        return $anchorRepairKey -eq $candidateRepairKey
    }

    function Test-FallbackFrontierRepairHasLaterProgress {
        param([Parameter(Mandatory = $true)]$DispatchEvent)

        $dispatchSequence = [int64]$DispatchEvent.Sequence
        return @($frontierRepairProgressObservedEvents | Where-Object {
            [int64]$_.Sequence -gt $dispatchSequence -and
            (Test-FallbackFrontierRepairTupleMatch -Anchor $DispatchEvent -Candidate $_)
        }).Count -gt 0
    }

    function Test-FallbackFrontierRepairHasLaterDispatch {
        param([Parameter(Mandatory = $true)]$AuthorityEvent)

        $authoritySequence = [int64]$AuthorityEvent.Sequence
        return @($frontierRepairDispatchedEvents | Where-Object {
            [int64]$_.Sequence -gt $authoritySequence -and
            (Test-FallbackFrontierRepairTupleMatch -Anchor $AuthorityEvent -Candidate $_)
        }).Count -gt 0
    }

    function Test-FallbackFrontierRepairHasLaterFailure {
        param([Parameter(Mandatory = $true)]$Anchor)

        $anchorSequence = [int64]$Anchor.Sequence
        return @($failureEvents | Where-Object {
            [int64]$_.Sequence -gt $anchorSequence -and
            (Test-FallbackFrontierRepairScopeMatch -Anchor $Anchor -Candidate $_)
        }).Count -gt 0
    }

    $metadataMissing = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($requestedEvents + $acceptedEvents + $staleFrontierRetiredEvents)) {
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $protocol = Get-FileTransferEventInt64Field -Event $event -Name 'protocol_version' -Default 0
        $legGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
        $liveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default 0
        $transportEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'transport_epoch' -Default -1
        if ($route -ne 'post_tuna_fallback_v6' -or
            $protocol -ne 6 -or
            $legGeneration -le 0 -or
            $liveRouteEpoch -le 0 -or
            $transportEpoch -lt 0) {
            $metadataMissing.Add($event) | Out-Null
        }
    }

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($metadataMissing.ToArray())) {
        $findings.Add(("fallback tail reconciliation metadata missing: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        $evidence.Add($event) | Out-Null
    }

    $tailReconciliationStarted = $zeroCreditBreakerEvents.Count -gt 0 -or
        $staleFrontierRetiredEvents.Count -gt 0 -or
        $requestedEvents.Count -gt 0 -or
        $sendSlotRetiredEvents.Count -gt 0 -or
        $receiveRecoveryRequestedEvents.Count -gt 0
    $tailFailureCandidate = $postTunaRouteSelected.Count -gt 0 -and
        $failureEvents.Count -gt 0 -and
        $normalZeroCreditTailStateRefreshEvents.Count -gt 0 -and
        $requestedEvents.Count -eq 0 -and
        $acceptedEvents.Count -eq 0

    if ($tailReconciliationStarted -and $requestedEvents.Count -eq 0) {
        $findings.Add('fallback tail reconciliation started without request proof') | Out-Null
        foreach ($event in @($zeroCreditBreakerEvents + $staleFrontierRetiredEvents + $sendSlotRetiredEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($requestedEvents.Count -gt 0 -and $acceptedEvents.Count -eq 0 -and $failureEvents.Count -gt 0) {
        $findings.Add('fallback tail reconciliation request was not accepted before terminal failure') | Out-Null
        foreach ($event in @($requestedEvents + $failureEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($tailFailureCandidate) {
        $findings.Add('post-Tuna fallback zero-credit tail stall ended without tail reconciliation proof') | Out-Null
        foreach ($event in @($normalZeroCreditTailStateRefreshEvents + $failureEvents | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if (-not $tailReconciliationStarted -and -not $tailFailureCandidate) {
        $verdict = 'none'
    }

    $quiescenceFindings = New-Object System.Collections.Generic.List[string]
    $quiescenceEvidence = New-Object System.Collections.Generic.List[object]
    $hasTailActivity = $tailReconciliationStarted -or
        $progressForcedEvents.Count -gt 0 -or
        $runtimeUnlockWaitingForFallbackEvents.Count -gt 0 -or
        $runtimeUnlockProbeRejectedByFallbackEvents.Count -gt 0 -or
        $yieldProofSentEvents.Count -gt 0 -or
        $yieldProofReceivedEvents.Count -gt 0 -or
        $pairedYieldGrantedEvents.Count -gt 0 -or
        $ownerGenerationMismatchEvents.Count -gt 0 -or
        $localYieldBlockedEvents.Count -gt 0 -or
        $frontierRepairAuthorityGrantedEvents.Count -gt 0 -or
        $frontierRepairAuthorityRejectedEvents.Count -gt 0 -or
        $frontierRepairDispatchedEvents.Count -gt 0 -or
        $frontierRepairStalledEvents.Count -gt 0 -or
        $frontierRepairProgressObservedEvents.Count -gt 0 -or
        $staleYieldProofIgnoredEvents.Count -gt 0
    $hasLegacyLocalPairedYield = $outboundQuiescentYieldEvents.Count -gt 0 -and
        $inboundQuiescentYieldEvents.Count -gt 0
    $hasPairedYield = $pairedYieldGrantedEvents.Count -gt 0 -or
        ($hasLegacyLocalPairedYield -and $ownerGenerationMismatchEvents.Count -eq 0)
    $hasTailOutcome = $terminalProofConsumedEvents.Count -gt 0 -or
        $hasPairedYield

    if ($ownerGenerationMismatchEvents.Count -gt 0) {
        $quiescenceFindings.Add('fallback tail yield owner generation mismatch was observed') | Out-Null
        foreach ($event in @($ownerGenerationMismatchEvents | Select-Object -First 5)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }

    if ($quiescenceTimeoutEvents.Count -gt 0) {
        $quiescenceFindings.Add('fallback tail reached deterministic quiescence timeout') | Out-Null
        foreach ($event in @($quiescenceTimeoutEvents | Select-Object -First 5)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }

    if ($localYieldBlockedEvents.Count -gt 0 -and
        $yieldProofReceivedEvents.Count -gt 0 -and
        $pairedYieldGrantedEvents.Count -eq 0 -and
        $terminalProofConsumedEvents.Count -eq 0) {
        $quiescenceFindings.Add('fallback tail peer yield was accepted but current local yield remained blocked') | Out-Null
        foreach ($event in @($localYieldBlockedEvents + $yieldProofReceivedEvents | Select-Object -First 10)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }

    if ($hasPostTunaFallbackEvidence -and
        $runtimeUnlockWaitingForFallbackEvents.Count -gt 0 -and
        $progressForcedEvents.Count -gt 0 -and
        -not $hasTailOutcome -and
        $failureEvents.Count -gt 0) {
        $quiescenceFindings.Add('fallback tail progressed while runtime unlock waited, but no terminal proof or explicit quiescent yield was recorded') | Out-Null
        foreach ($event in @($runtimeUnlockWaitingForFallbackEvents + $progressForcedEvents + $failureEvents | Select-Object -First 10)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }

    if ($quiescentYieldEvents.Count -gt 0 -and
        -not $hasPairedYield -and
        $terminalProofConsumedEvents.Count -eq 0) {
        $quiescenceFindings.Add('fallback tail yield was one-sided; paired inbound/outbound yield or terminal proof is required before Tuna reactivation') | Out-Null
        foreach ($event in @($quiescentYieldEvents + $runtimeUnlockProbeRejectedByFallbackEvents | Select-Object -First 10)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }

    [object[]]$frontierRepairDispatchedWithoutProgressEvents = @($frontierRepairDispatchedEvents | Where-Object {
        (Test-FallbackFrontierRepairHasLaterFailure -Anchor $_) -and
        -not (Test-FallbackFrontierRepairHasLaterProgress -DispatchEvent $_)
    })
    [object[]]$frontierRepairAuthorityGrantedWithoutDispatchEvents = @($frontierRepairAuthorityGrantedEvents | Where-Object {
        (Test-FallbackFrontierRepairHasLaterFailure -Anchor $_) -and
        -not (Test-FallbackFrontierRepairHasLaterDispatch -AuthorityEvent $_)
    })
    [object[]]$frontierRepairProgressedDispatchEvents = @($frontierRepairDispatchedEvents | Where-Object {
        Test-FallbackFrontierRepairHasLaterProgress -DispatchEvent $_
    })

    $frontierRepairVerdict = 'none'
    if ($frontierRepairStalledEvents.Count -gt 0) {
        $frontierRepairVerdict = 'fail'
        $quiescenceFindings.Add('fallback frontier repair explicitly stalled before terminal progress') | Out-Null
        foreach ($event in @($frontierRepairStalledEvents + $failureEvents | Select-Object -First 10)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }
    elseif ($frontierRepairDispatchedWithoutProgressEvents.Count -gt 0) {
        $frontierRepairVerdict = 'fail'
        $quiescenceFindings.Add('fallback frontier repair was dispatched but no progress proof was observed before failure') | Out-Null
        foreach ($event in @($frontierRepairDispatchedWithoutProgressEvents + $failureEvents | Select-Object -First 10)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }
    elseif ($frontierRepairAuthorityGrantedWithoutDispatchEvents.Count -gt 0) {
        $frontierRepairVerdict = 'fail'
        $quiescenceFindings.Add('fallback frontier repair authority was granted but no dispatch proof was observed before failure') | Out-Null
        foreach ($event in @($frontierRepairAuthorityGrantedWithoutDispatchEvents + $failureEvents | Select-Object -First 10)) {
            $quiescenceEvidence.Add($event) | Out-Null
        }
    }
    elseif ($frontierRepairProgressedDispatchEvents.Count -gt 0) {
        $frontierRepairVerdict = 'pass'
    }
    elseif ($frontierRepairAuthorityRejectedEvents.Count -gt 0) {
        $frontierRepairVerdict = 'rejected'
    }

    $quiescenceVerdict = 'none'
    if ($hasTailOutcome) {
        $quiescenceVerdict = 'pass'
    }
    elseif ($quiescenceFindings.Count -gt 0) {
        $quiescenceVerdict = 'fail'
    }
    elseif ($hasTailActivity) {
        $quiescenceVerdict = 'pending'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        QuiescenceVerdict = $quiescenceVerdict
        RequestedCount = $requestedEvents.Count
        AcceptedCount = $acceptedEvents.Count
        ProgressForcedCount = $progressForcedEvents.Count
        ProgressCoalescedCount = $progressCoalescedEvents.Count
        TerminalProofConsumedCount = $terminalProofConsumedEvents.Count
        QuiescentYieldGrantedCount = $quiescentYieldEvents.Count
        OutboundQuiescentYieldGrantedCount = $outboundQuiescentYieldEvents.Count
        InboundQuiescentYieldGrantedCount = $inboundQuiescentYieldEvents.Count
        YieldProofSentCount = $yieldProofSentEvents.Count
        YieldProofReceivedCount = $yieldProofReceivedEvents.Count
        PairedYieldGrantedCount = $pairedYieldGrantedEvents.Count
        OwnerGenerationMismatchCount = $ownerGenerationMismatchEvents.Count
        LocalYieldBlockedCount = $localYieldBlockedEvents.Count
        StaleYieldProofIgnoredCount = $staleYieldProofIgnoredEvents.Count
        PairedYieldVerdict = $(if ($terminalProofConsumedEvents.Count -gt 0 -or $hasPairedYield) { 'pass' } elseif ($hasTailActivity) { 'fail' } else { 'none' })
        FrontierRepairAuthorityGrantedCount = $frontierRepairAuthorityGrantedEvents.Count
        FrontierRepairAuthorityRejectedCount = $frontierRepairAuthorityRejectedEvents.Count
        FrontierRepairDispatchedCount = $frontierRepairDispatchedEvents.Count
        FrontierRepairStalledCount = $frontierRepairStalledEvents.Count
        FrontierRepairProgressObservedCount = $frontierRepairProgressObservedEvents.Count
        FrontierRepairVerdict = $frontierRepairVerdict
        RuntimeUnlockProbeRejectedByFallbackOwnerCount = $runtimeUnlockProbeRejectedByFallbackEvents.Count
        RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount = $runtimeUnlockProbeRejectedByInboundFallbackEvents.Count
        QuiescenceTimeoutCount = $quiescenceTimeoutEvents.Count
        ZeroCreditBreakerCount = $zeroCreditBreakerEvents.Count
        StaleFrontierRetiredCount = $staleFrontierRetiredEvents.Count
        SendSlotRetiredCount = $sendSlotRetiredEvents.Count
        ReceiveRecoveryRequestedCount = $receiveRecoveryRequestedEvents.Count
        NormalZeroCreditTailStateRefreshCount = $normalZeroCreditTailStateRefreshEvents.Count
        MetadataMissingCount = $metadataMissing.Count
        Findings = $findings
        QuiescenceFindings = $quiescenceFindings
        EvidenceEvents = @(@($evidence.ToArray()) + @($zeroCreditBreakerEvents + $requestedEvents + $acceptedEvents + $normalZeroCreditTailStateRefreshEvents) | Select-Object -First 20)
        QuiescenceEvidenceEvents = @(@($quiescenceEvidence.ToArray()) + @($terminalProofConsumedEvents + $quiescentYieldEvents + $pairedYieldGrantedEvents + $ownerGenerationMismatchEvents + $localYieldBlockedEvents + $quiescenceTimeoutEvents + $progressForcedEvents + $frontierRepairAuthorityGrantedEvents + $frontierRepairDispatchedEvents + $frontierRepairStalledEvents + $frontierRepairProgressObservedEvents) | Select-Object -First 20)
    }
}

function Get-FileTransferControlPlaneIsolationProof {
    param(
        [Parameter(Mandatory = $true)]$Summary
    )

    $staleGenerationTreadmillScoreLimit = 6
    [object[]]$events = @($Summary.TransferEvents)
    [object[]]$postTunaRouteSelected = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_route_selected' -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6'
            }
    )
    [object[]]$deliveryResultEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_result' } | Sort-Object Sequence)
    [object[]]$observedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_observed' } | Sort-Object Sequence)
    [object[]]$failedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_failed' } | Sort-Object Sequence)
    [object[]]$unavailableEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_unavailable' } | Sort-Object Sequence)
    [object[]]$retiredStaleSendEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_send_retired_stale_generation' } | Sort-Object Sequence)
    [object[]]$checkpointExchangeRetiredEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_checkpoint_exchange_retired' } | Sort-Object Sequence)
    [object[]]$checkpointOwnerViolationEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_checkpoint_owner_violation' } | Sort-Object Sequence)
    [object[]]$staleDeferredReplayDiscardedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_deferred_discarded' } | Sort-Object Sequence)
    [object[]]$checkpointNotPendingReissueSuppressedEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_fallback_checkpoint_reissue_suppressed' -and
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'checkpoint_not_pending'
            } |
            Sort-Object Sequence
    )
    [object[]]$staleProofIgnoredEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_stale_proof_ignored' } | Sort-Object Sequence)
    [object[]]$staleProofReplaySuppressedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_stale_proof_replay_suppressed' } | Sort-Object Sequence)
    [object[]]$sessionLivenessTimeoutEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout' } | Sort-Object Sequence)
    [object[]]$terminalFailureEvents = @(
        $events |
            Where-Object {
                if ($_.EventName -eq 'file_transfer_inbound_terminal' -or
                    $_.EventName -eq 'file_transfer_outbound_terminal' -or
                    $_.EventName -eq 'transfer_terminal') {
                    return (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -ne '(none)'
                }

                return $false
            } |
            Sort-Object Sequence
    )
    [object[]]$terminalCompletedEvents = @(
        $events |
            Where-Object {
                ($_.EventName -eq 'file_transfer_inbound_terminal' -or
                    $_.EventName -eq 'file_transfer_outbound_terminal' -or
                    $_.EventName -eq 'transfer_terminal') -and
                (Get-FileTransferEventField -Event $_ -Name 'state' -Default '') -eq 'Completed' -and
                (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -eq '(none)'
            } |
            Sort-Object Sequence
    )

    $fallbackControlPlaneKinds = @(
        'fallback_checkpoint_request',
        'fallback_checkpoint_proof',
        'receiver_state',
        'frontier_request'
    )
    $checkpointOwnerEventNames = @(
        'filetransfer_fallback_leg_authority_started',
        'filetransfer_fallback_checkpoint_reissue_requested',
        'filetransfer_fallback_tail_reconciliation_requested'
    )
    $checkpointOwnerKinds = @(
        'fallback_checkpoint_request',
        'fallback_checkpoint_proof',
        'frontier_request'
    )
    $checkpointIdsByLeg = @{}
    $activeCheckpointByLeg = @{}
    $bridgeGenerationByLeg = @{}
    $inferredCheckpointOwnerViolationEvents = New-Object System.Collections.Generic.List[object]
    $nonCurrentLegGenerationSendEvents = New-Object System.Collections.Generic.List[object]
    $currentRoute = ''
    $currentLiveRouteEpoch = 0
    $currentFallbackLegGeneration = 0
    $currentRouteByDirection = @{}
    $currentFallbackLegGenerationByDirection = @{}

    function Get-FileTransferControlPlaneDirectionKey {
        param($Event)

        $direction = Get-FileTransferEventField -Event $Event -Name 'direction' -Default ''
        if ([string]::IsNullOrWhiteSpace($direction) -or
            $direction -eq '(none)' -or
            $direction -eq 'none') {
            return ''
        }

        return $direction.ToLowerInvariant()
    }

    function Add-FileTransferCheckpointIdForLeg {
        param(
            [int64]$LegGeneration,
            [string]$CheckpointId
        )

        if ($LegGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace($CheckpointId) -or
            $CheckpointId -eq '(none)' -or
            $CheckpointId -eq 'none') {
            return
        }

        $key = [string]$LegGeneration
        if (-not $checkpointIdsByLeg.ContainsKey($key)) {
            $checkpointIdsByLeg[$key] = New-Object System.Collections.Generic.List[string]
        }

        if (-not $checkpointIdsByLeg[$key].Contains($CheckpointId)) {
            $checkpointIdsByLeg[$key].Add($CheckpointId) | Out-Null
        }
    }

    function Clear-FileTransferActiveCheckpointForLeg {
        param(
            [int64]$LegGeneration,
            [string]$CheckpointId = ''
        )

        if ($LegGeneration -le 0) {
            return
        }

        $key = [string]$LegGeneration
        if (-not $activeCheckpointByLeg.ContainsKey($key)) {
            return
        }

        if ([string]::IsNullOrWhiteSpace($CheckpointId) -or
            $CheckpointId -eq '(none)' -or
            $CheckpointId -eq 'none' -or
            [string]::Equals([string]$activeCheckpointByLeg[$key], $CheckpointId, [System.StringComparison]::OrdinalIgnoreCase)) {
            $activeCheckpointByLeg.Remove($key)
        }
    }

    foreach ($event in @($events | Sort-Object Sequence)) {
        if ($event.EventName -eq 'filetransfer_route_selected' -or
            $event.EventName -eq 'filetransfer_live_route_epoch_started' -or
            $event.EventName -eq 'filetransfer_live_route_epoch_recovered') {
            $directionKey = Get-FileTransferControlPlaneDirectionKey -Event $event
            $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
            if (-not [string]::IsNullOrWhiteSpace($route)) {
                $currentRoute = $route
                if (-not [string]::IsNullOrWhiteSpace($directionKey)) {
                    $currentRouteByDirection[$directionKey] = $route
                }
            }

            $liveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default $currentLiveRouteEpoch
            if ($liveRouteEpoch -gt 0) {
                $currentLiveRouteEpoch = $liveRouteEpoch
            }

            if ($route -ne 'post_tuna_fallback_v6') {
                $currentFallbackLegGeneration = 0
                if (-not [string]::IsNullOrWhiteSpace($directionKey)) {
                    $currentFallbackLegGenerationByDirection[$directionKey] = 0
                }
            }

            continue
        }

        if ($event.EventName -eq 'filetransfer_leg_started' -or
            $event.EventName -eq 'filetransfer_fallback_leg_authority_started') {
            $directionKey = Get-FileTransferControlPlaneDirectionKey -Event $event
            $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
            $legGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
            $checkpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
            $bridgeGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'bridge_recovery_generation' -Default -1
            if ($route -eq 'post_tuna_fallback_v6' -and $legGeneration -gt 0) {
                $currentFallbackLegGeneration = $legGeneration
                if (-not [string]::IsNullOrWhiteSpace($directionKey)) {
                    $currentFallbackLegGenerationByDirection[$directionKey] = $legGeneration
                    $currentRouteByDirection[$directionKey] = $route
                }
                Add-FileTransferCheckpointIdForLeg -LegGeneration $legGeneration -CheckpointId $checkpointId
                if (-not [string]::IsNullOrWhiteSpace($checkpointId) -and
                    $checkpointId -ne '(none)' -and
                    $checkpointId -ne 'none') {
                    $activeCheckpointByLeg[[string]$legGeneration] = $checkpointId
                }
                if ($bridgeGeneration -ge 0) {
                    $bridgeGenerationByLeg[[string]$legGeneration] = $bridgeGeneration
                }
            }

            continue
        }

        if ($event.EventName -eq 'filetransfer_fallback_checkpoint_exchange_retired') {
            $retiredLegGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'retired_leg_generation' -Default 0
            $currentLegGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'current_leg_generation' -Default 0
            $retiredCheckpointId = Get-FileTransferEventField -Event $event -Name 'retired_checkpoint_request_id' -Default ''
            $currentCheckpointId = Get-FileTransferEventField -Event $event -Name 'current_checkpoint_request_id' -Default ''
            Clear-FileTransferActiveCheckpointForLeg -LegGeneration $retiredLegGeneration -CheckpointId $retiredCheckpointId
            Add-FileTransferCheckpointIdForLeg -LegGeneration $retiredLegGeneration -CheckpointId $retiredCheckpointId
            Add-FileTransferCheckpointIdForLeg -LegGeneration $currentLegGeneration -CheckpointId $currentCheckpointId
            if ($currentLegGeneration -gt 0 -and
                -not [string]::IsNullOrWhiteSpace($currentCheckpointId) -and
                $currentCheckpointId -ne '(none)' -and
                $currentCheckpointId -ne 'none') {
                $activeCheckpointByLeg[[string]$currentLegGeneration] = $currentCheckpointId
            }

            continue
        }

        $eventKind = Get-FileTransferEventField -Event $event -Name 'kind' -Default ''
        $eventRoute = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $eventProtocol = Get-FileTransferEventInt64Field -Event $event -Name 'protocol_version' -Default 0
        $eventLegGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
        $eventLiveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default 0
        $eventBridgeGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'bridge_recovery_generation' -Default -1
        $eventCheckpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
        $eventDirectionKey = Get-FileTransferControlPlaneDirectionKey -Event $event

        if ($event.EventName -eq 'filetransfer_control_plane_delivery_result' -and
            $fallbackControlPlaneKinds -contains $eventKind) {
            $routeForEvent = $currentRoute
            $fallbackLegGenerationForEvent = $currentFallbackLegGeneration
            if (-not [string]::IsNullOrWhiteSpace($eventDirectionKey)) {
                if ($currentRouteByDirection.ContainsKey($eventDirectionKey)) {
                    $routeForEvent = [string]$currentRouteByDirection[$eventDirectionKey]
                }
                if ($currentFallbackLegGenerationByDirection.ContainsKey($eventDirectionKey)) {
                    $fallbackLegGenerationForEvent = [int64]$currentFallbackLegGenerationByDirection[$eventDirectionKey]
                }
            }

            if ($eventRoute -eq 'post_tuna_fallback_v6' -and
                $eventProtocol -eq 6 -and
                $eventLiveRouteEpoch -gt 0 -and
                $currentLiveRouteEpoch -eq $eventLiveRouteEpoch -and
                $currentRoute -eq 'post_tuna_fallback_v6') {
                $routeForEvent = $currentRoute
                $fallbackLegGenerationForEvent = $currentFallbackLegGeneration
            }

            $currentFallbackStateKnown = (-not [string]::IsNullOrWhiteSpace($routeForEvent)) -or
                $fallbackLegGenerationForEvent -gt 0
            $isNonCurrentFallbackSend = $eventRoute -eq 'post_tuna_fallback_v6' -and
                $currentFallbackStateKnown -and
                (
                    $routeForEvent -ne 'post_tuna_fallback_v6' -or
                    ($fallbackLegGenerationForEvent -gt 0 -and $eventLegGeneration -gt 0 -and $eventLegGeneration -ne $fallbackLegGenerationForEvent)
                )
            if ($isNonCurrentFallbackSend) {
                $nonCurrentLegGenerationSendEvents.Add($event) | Out-Null
            }
        }

        if ($eventRoute -eq 'post_tuna_fallback_v6' -and
            $eventProtocol -eq 6 -and
            $eventLegGeneration -gt 0) {
            Add-FileTransferCheckpointIdForLeg -LegGeneration $eventLegGeneration -CheckpointId $eventCheckpointId

            $legKey = [string]$eventLegGeneration
            if ($eventBridgeGeneration -ge 0) {
                if ($bridgeGenerationByLeg.ContainsKey($legKey) -and
                    $eventBridgeGeneration -gt [int64]$bridgeGenerationByLeg[$legKey]) {
                    $activeCheckpointByLeg.Remove($legKey)
                }

                $bridgeGenerationByLeg[$legKey] = $eventBridgeGeneration
            }

            if (($checkpointOwnerEventNames -contains $event.EventName) -or
                ($event.EventName -eq 'filetransfer_control_plane_delivery_result' -and $checkpointOwnerKinds -contains $eventKind)) {
                if (-not [string]::IsNullOrWhiteSpace($eventCheckpointId) -and
                    $eventCheckpointId -ne '(none)' -and
                    $eventCheckpointId -ne 'none') {
                    if ($activeCheckpointByLeg.ContainsKey($legKey) -and
                        -not [string]::Equals([string]$activeCheckpointByLeg[$legKey], $eventCheckpointId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $inferredCheckpointOwnerViolationEvents.Add($event) | Out-Null
                    }

                    $activeCheckpointByLeg[$legKey] = $eventCheckpointId
                }
            }

            if ($event.EventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' -or
                $event.EventName -eq 'filetransfer_fallback_tail_reconciliation_accepted') {
                Clear-FileTransferActiveCheckpointForLeg -LegGeneration $eventLegGeneration -CheckpointId $eventCheckpointId
            }
        }
    }

    $checkpointIdsByLegTextParts = New-Object System.Collections.Generic.List[string]
    $checkpointIdCountMaxPerLeg = 0
    foreach ($entry in @($checkpointIdsByLeg.GetEnumerator() | Sort-Object { [int64]$_.Key })) {
        [object[]]$ids = @($entry.Value)
        if ($ids.Length -gt $checkpointIdCountMaxPerLeg) {
            $checkpointIdCountMaxPerLeg = $ids.Length
        }

        $checkpointIdsByLegTextParts.Add(("{0}:{1}" -f $entry.Key, ($ids -join '|'))) | Out-Null
    }
    $checkpointIdsByLegText = if ($checkpointIdsByLegTextParts.Count -gt 0) {
        $checkpointIdsByLegTextParts.ToArray() -join ';'
    }
    else {
        '(none)'
    }

    [object[]]$peerVisibleEvents = @(
        $deliveryResultEvents |
            Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0 }
    )
    [object[]]$localOnlyRejectedEvents = @(
        $deliveryResultEvents |
            Where-Object {
                (Get-FileTransferEventInt64Field -Event $_ -Name 'local_only_rejected' -Default 0) -gt 0 -or
                (
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'control_queue' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -le 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -le 0
                )
            }
    )
    [object[]]$acceptedFailureEvents = @(
        $deliveryResultEvents |
            Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -le 0 }
    )
    [object[]]$checkpointExchangeEvents = @(
        $deliveryResultEvents |
            Where-Object {
                $kind = Get-FileTransferEventField -Event $_ -Name 'kind' -Default ''
                ($kind -eq 'fallback_checkpoint_request' -or $kind -eq 'fallback_checkpoint_proof') -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0
            }
    )
    [object[]]$continuousReceiverStateProofEvents = @(
        $deliveryResultEvents |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'receiver_state' -and
                    (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'protocol_version' -Default 0) -eq 6 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'leg_generation' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'live_route_epoch' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'transport_epoch' -Default -1) -ge 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'local_only_rejected' -Default 0) -le 0
            }
    )
    [object[]]$metadataCheckEvents = @(
        $deliveryResultEvents |
            Where-Object {
                $kind = Get-FileTransferEventField -Event $_ -Name 'kind' -Default ''
                $kind -eq 'fallback_checkpoint_request' -or
                    $kind -eq 'fallback_checkpoint_proof' -or
                    $kind -eq 'receiver_state' -or
                    $kind -eq 'frontier_request'
            }
    )

    $metadataMissing = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($metadataCheckEvents)) {
        $kind = Get-FileTransferEventField -Event $event -Name 'kind' -Default ''
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $protocol = Get-FileTransferEventInt64Field -Event $event -Name 'protocol_version' -Default 0
        $legGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
        $liveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default 0
        $transportEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'transport_epoch' -Default -1
        $checkpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
        $requiresCheckpointId = $kind -eq 'fallback_checkpoint_request' -or
            $kind -eq 'fallback_checkpoint_proof'
        $isRegularV4ControlPlaneProof =
            $route -eq 'regular_nkn_v4_fast' -and
            $protocol -eq 4 -and
            ($kind -eq 'receiver_state' -or $kind -eq 'frontier_request')
        if ($isRegularV4ControlPlaneProof) {
            continue
        }

        if ($route -ne 'post_tuna_fallback_v6' -or
            $protocol -ne 6 -or
            $legGeneration -le 0 -or
            $liveRouteEpoch -le 0 -or
            $transportEpoch -lt 0 -or
            ($requiresCheckpointId -and
                ([string]::IsNullOrWhiteSpace($checkpointId) -or
                    $checkpointId -eq '(none)'))) {
            $metadataMissing.Add($event) | Out-Null
        }
    }

    $timeoutDuringValidExchangeCount = 0
    if ($postTunaRouteSelected.Count -gt 0 -and
        $deliveryResultEvents.Count -gt 0 -and
        $sessionLivenessTimeoutEvents.Count -gt 0) {
        $timeoutDuringValidExchangeCount = $sessionLivenessTimeoutEvents.Count
    }

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($metadataMissing.ToArray())) {
        $findings.Add(("control-plane metadata missing: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        $evidence.Add($event) | Out-Null
    }

    if ($localOnlyRejectedEvents.Count -gt 0) {
        $findings.Add(("control-plane peer-visible proof rejected local-only delivery count={0}" -f $localOnlyRejectedEvents.Count)) | Out-Null
        foreach ($event in @($localOnlyRejectedEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $deliveryFailureCount = $failedEvents.Count + $acceptedFailureEvents.Count
    $recoveredDeliveryFailureCount = 0
    if ($deliveryFailureCount -gt 0 -and
        ($checkpointExchangeEvents.Count -gt 0 -or $continuousReceiverStateProofEvents.Count -gt 0) -and
        $localOnlyRejectedEvents.Count -eq 0 -and
        $metadataMissing.Count -eq 0 -and
        $timeoutDuringValidExchangeCount -eq 0 -and
        $terminalFailureEvents.Count -eq 0 -and
        $terminalCompletedEvents.Count -gt 0) {
        $recoveredDeliveryFailureCount = $deliveryFailureCount
    }

    if ($deliveryFailureCount -gt 0 -and $recoveredDeliveryFailureCount -eq 0) {
        $findings.Add(("control-plane delivery failed count={0}" -f $deliveryFailureCount)) | Out-Null
        foreach ($event in @($failedEvents + $acceptedFailureEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($postTunaRouteSelected.Count -gt 0 -and
        $checkpointExchangeEvents.Count -eq 0 -and
        $continuousReceiverStateProofEvents.Count -eq 0) {
        $findings.Add('post-Tuna fallback route missing peer-visible control-plane checkpoint exchange') | Out-Null
        foreach ($event in @($postTunaRouteSelected + $sessionLivenessTimeoutEvents + $terminalFailureEvents | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($timeoutDuringValidExchangeCount -gt 0) {
        $findings.Add(("session liveness timeout occurred during a fallback control-plane exchange count={0}" -f $timeoutDuringValidExchangeCount)) | Out-Null
        foreach ($event in @($sessionLivenessTimeoutEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $checkpointOwnerViolationCount = $checkpointOwnerViolationEvents.Count + $inferredCheckpointOwnerViolationEvents.Count
    $staleDeferredReplayDiscardedCount = 0
    foreach ($event in @($staleDeferredReplayDiscardedEvents)) {
        $reason = Get-FileTransferEventField -Event $event -Name 'reason' -Default ''
        if ($reason -eq 'checkpoint_request_mismatch' -or
            $reason -eq 'transport_epoch_mismatch' -or
            $reason -eq 'checkpoint_not_pending') {
            $staleDeferredReplayDiscardedCount++
        }
    }
    $staleGenerationTreadmillScore =
        $staleProofIgnoredEvents.Count +
        $staleProofReplaySuppressedEvents.Count +
        $checkpointOwnerViolationCount +
        $staleDeferredReplayDiscardedCount
    if ($checkpointOwnerViolationCount -gt 0) {
        $findings.Add(("fallback checkpoint owner violation count={0}; checkpoint_ids_by_leg_generation={1}" -f $checkpointOwnerViolationCount, $checkpointIdsByLegText)) | Out-Null
        foreach ($event in @($checkpointOwnerViolationEvents + $inferredCheckpointOwnerViolationEvents.ToArray() | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($nonCurrentLegGenerationSendEvents.Count -gt 0) {
        $findings.Add(("fallback control-plane send used non-current leg generation count={0}" -f $nonCurrentLegGenerationSendEvents.Count)) | Out-Null
        foreach ($event in @($nonCurrentLegGenerationSendEvents.ToArray() | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($staleGenerationTreadmillScore -gt $staleGenerationTreadmillScoreLimit) {
        $findings.Add(("fallback control-plane stale generation treadmill: score={0}; limit={1}; stale_ignored={2}; stale_replay_suppressed={3}; retired_stale_send_diagnostic={4}; owner_violation={5}; stale_deferred_replay_discarded={6}; checkpoint_exchange_count={7}" -f $staleGenerationTreadmillScore, $staleGenerationTreadmillScoreLimit, $staleProofIgnoredEvents.Count, $staleProofReplaySuppressedEvents.Count, $retiredStaleSendEvents.Count, $checkpointOwnerViolationCount, $staleDeferredReplayDiscardedCount, $checkpointExchangeEvents.Count)) | Out-Null
        foreach ($event in @($retiredStaleSendEvents + $staleProofIgnoredEvents + $staleProofReplaySuppressedEvents + $staleDeferredReplayDiscardedEvents | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if ($postTunaRouteSelected.Count -eq 0 -and $deliveryResultEvents.Count -eq 0 -and $observedEvents.Count -eq 0 -and $failedEvents.Count -eq 0 -and $unavailableEvents.Count -eq 0 -and $retiredStaleSendEvents.Count -eq 0) {
        $verdict = 'none'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        DeliveryResultCount = $deliveryResultEvents.Count
        PeerVisibleCount = $peerVisibleEvents.Count
        LocalOnlyRejectedCount = $localOnlyRejectedEvents.Count
        CheckpointExchangeCount = $checkpointExchangeEvents.Count
        ContinuousReceiverStateProofCount = $continuousReceiverStateProofEvents.Count
        DeliveryFailureCount = $deliveryFailureCount
        RecoveredDeliveryFailureCount = $recoveredDeliveryFailureCount
        UnavailableCount = $unavailableEvents.Count
        MetadataMissingCount = $metadataMissing.Count
        LivenessTimeoutDuringValidExchangeCount = $timeoutDuringValidExchangeCount
        RetiredStaleSendCount = $retiredStaleSendEvents.Count
        CheckpointExchangeRetiredCount = $checkpointExchangeRetiredEvents.Count
        CheckpointOwnerViolationCount = $checkpointOwnerViolationCount
        StaleDeferredReplayDiscardedCount = $staleDeferredReplayDiscardedCount
        CheckpointNotPendingReissueSuppressedCount = $checkpointNotPendingReissueSuppressedEvents.Count
        StaleProofIgnoredCount = $staleProofIgnoredEvents.Count
        StaleProofReplaySuppressedCount = $staleProofReplaySuppressedEvents.Count
        StaleReplayTreadmillCount = $staleGenerationTreadmillScore
        StaleGenerationTreadmillScore = $staleGenerationTreadmillScore
        StaleGenerationTreadmillScoreLimit = $staleGenerationTreadmillScoreLimit
        CheckpointIdsByLegGeneration = $checkpointIdsByLegText
        CheckpointIdCountMaxPerLeg = $checkpointIdCountMaxPerLeg
        ControlPlaneNonCurrentLegGenerationSendCount = $nonCurrentLegGenerationSendEvents.Count
        Findings = $findings
        EvidenceEvents = @(@($evidence.ToArray()) + @($deliveryResultEvents) | Select-Object -First 20)
    }
}

function Test-FileTransferStaleRejectedProofMeaningfulValue {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return $false
    }

    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $false
    }

    return -not ($text -in @('(none)', '(unknown)', 'unknown', 'none'))
}

function Get-FileTransferStaleRejectedProofFirstField {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    foreach ($name in @($Names)) {
        $value = Get-FileTransferEventField -Event $Event -Name $name -Default ''
        if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $value) {
            return [string]$value
        }
    }

    return ''
}

function Get-FileTransferStaleRejectedProofTransferId {
    param([Parameter(Mandatory = $true)]$Event)

    if ($null -ne $Event.PSObject.Properties['TransferId'] -and
        (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $Event.TransferId)) {
        return [string]$Event.TransferId
    }

    return Get-FileTransferStaleRejectedProofFirstField -Event $Event -Names @('transfer_id')
}

function Get-FileTransferStaleRejectedProofActionFamily {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -in @(
            'filetransfer_route_publication_rejected',
            'filetransfer_route_hint_ignored_missing_publication_authority',
            'filetransfer_route_hint_ignored_stale_publication_authority',
            'filetransfer_route_hint_ignored_invalid_publication_authority')) {
        return 'route_publication'
    }

    if ($Event.EventName -eq 'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery') {
        return 'control_plane'
    }

    if ($Event.EventName -eq 'runtime_unlock_retry_dispatch_authority_rejected') {
        return 'control_plane'
    }

    $actionKind = Get-FileTransferStaleRejectedProofFirstField -Event $Event -Names @('action_kind')
    if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $actionKind) {
        switch ($actionKind.ToLowerInvariant()) {
            'livenessdeferral' { return 'liveness' }
            'repairreplay' { return 'repair' }
            'progressmutation' { return 'progress' }
            'fallbackcheckpointbegin' { return 'checkpoint' }
            'fallbackcheckpointaccept' { return 'checkpoint' }
            'fallbackcheckpointreissue' { return 'checkpoint' }
            'fallbackcheckpointretire' { return 'checkpoint' }
            'fallbacktailquiescent' { return 'checkpoint' }
            'fallbacktailfailed' { return 'checkpoint' }
            'controlplanesendcompletion' { return 'control_plane' }
            'fallbackcontrolplanepressure' { return 'control_plane' }
            'bridgerecovery' { return 'bridge_recovery' }
            'receiverecovery' { return 'bridge_recovery' }
            'bridgereceivestallrecovery' { return 'bridge_recovery' }
            'controlplanesend' { return 'control_plane' }
            'transportepochadoption' { return 'route_publication' }
            'peerepochpromotion' { return 'route_publication' }
            'unmatchedfrontierproof' { return 'repair' }
            'routemismatchmark' { return 'route_publication' }
            'routepublication' { return 'route_publication' }
            'recoverylivenessleasebegin' { return 'liveness' }
            'recoverylivenessleasevalidate' { return 'liveness' }
            'recoverylivenessleaseretire' { return 'liveness' }
            'runtimeunlockoffercallback' { return 'control_plane' }
            'runtimeunlockanswercallback' { return 'control_plane' }
            'runtimeunlockprobestart' { return 'control_plane' }
            'runtimeunlockprobeack' { return 'control_plane' }
            'runtimeunlocktransactioncallback' { return 'control_plane' }
            'routecommitpublication' { return 'route_publication' }
            'starttransferleg' { return 'route_publication' }
            'runtimeownervalidation' { }
            default { return 'unknown' }
        }
    }

    $textParts = @(
        (Get-FileTransferEventField -Event $Event -Name 'attempted_action' -Default ''),
        (Get-FileTransferEventField -Event $Event -Name 'source' -Default ''),
        (Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''),
        (Get-FileTransferEventField -Event $Event -Name 'rejection_reason' -Default '')
    )
    $text = ($textParts -join ' ').ToLowerInvariant()

    if ($text -match 'checkpoint|tail_quiescent|tail_failed|tail|yield') {
        return 'checkpoint'
    }

    if ($text -match 'control_plane|control-plane|delivery|receiver_state_control|frontier_request_control|runtime_unlock|precommit_probe|probe_ack|offer_callback|answer_callback|transaction_callback') {
        return 'control_plane'
    }

    if ($text -match 'bridge_recovery|bridge-recovery|receive_recovery|receive-recovery|recovery_requested|recovery-requested') {
        return 'bridge_recovery'
    }

    if ($text -match 'repair|replay|requeue|frontier|peer_silence|peer-silence|rebind') {
        return 'repair'
    }

    if ($text -match 'liveness|heartbeat|defer_liveness|deferral') {
        return 'liveness'
    }

    if ($text -match 'progress|state_update|state-update|state_forced|state-forced') {
        return 'progress'
    }

    if ($text -match 'route|publication|publish|mismatch|leg_start|leg-start|leg_frozen|leg-frozen') {
        return 'route_publication'
    }

    return 'unknown'
}

function Get-FileTransferStaleRejectedProofFallbackSubfamily {
    param([Parameter(Mandatory = $true)]$Event)

    $actionKind = Get-FileTransferStaleRejectedProofFirstField -Event $Event -Names @('action_kind')
    if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $actionKind) {
        switch ($actionKind.ToLowerInvariant()) {
            'controlplanesend' { return 'fallback_send' }
            'controlplanesendcompletion' { return 'fallback_send' }
            'fallbackcheckpointbegin' { return 'fallback_checkpoint' }
            'fallbackcheckpointaccept' { return 'fallback_checkpoint' }
            'fallbackcheckpointretire' { return 'fallback_checkpoint' }
            'fallbackcheckpointreissue' { return 'fallback_reissue' }
            'fallbacktailquiescent' { return 'fallback_tail_yield' }
            'fallbacktailfailed' { return 'fallback_tail_yield' }
            'fallbackcontrolplanepressure' { return 'fallback_pressure' }
        }
    }

    $textParts = @(
        (Get-FileTransferEventField -Event $Event -Name 'attempted_action' -Default ''),
        (Get-FileTransferEventField -Event $Event -Name 'source' -Default ''),
        (Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''),
        (Get-FileTransferEventField -Event $Event -Name 'rejection_reason' -Default '')
    )
    $text = ($textParts -join ' ').ToLowerInvariant()

    if ($text -match 'fallback.*pressure|control_plane_pressure|control-plane-pressure|pressure_authority') {
        return 'fallback_pressure'
    }

    if ($text -match 'checkpoint.*reissue|reissue.*checkpoint|checkpoint_request_retry|checkpoint_request_replay') {
        return 'fallback_reissue'
    }

    if ($text -match 'tail|yield') {
        return 'fallback_tail_yield'
    }

    if ($text -match 'checkpoint') {
        return 'fallback_checkpoint'
    }

    if ($text -match 'control_plane|control-plane|delivery|receiver_state_control|frontier_request_control') {
        return 'fallback_send'
    }

    return ''
}

function Test-FileTransferStaleRejectedProofScopeMatches {
    param(
        [Parameter(Mandatory = $true)]$Rejection,
        [Parameter(Mandatory = $true)]$Effect
    )

    $rejectionTransferId = Get-FileTransferStaleRejectedProofTransferId -Event $Rejection
    $effectTransferId = Get-FileTransferStaleRejectedProofTransferId -Event $Effect
    $hasScope = $false
    if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $rejectionTransferId) {
        $hasScope = $true
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $effectTransferId) -or
            -not [string]::Equals($rejectionTransferId, $effectTransferId, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    $rejectionSessionId = Get-FileTransferEventField -Event $Rejection -Name 'session_id' -Default ''
    $effectSessionId = Get-FileTransferEventField -Event $Effect -Name 'session_id' -Default ''
    if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $rejectionSessionId) {
        $hasScope = $true
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $effectSessionId) -or
            -not [string]::Equals($rejectionSessionId, $effectSessionId, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    if (-not $hasScope) {
        return $false
    }

    $rejectionDirection = Get-FileTransferEventField -Event $Rejection -Name 'direction' -Default ''
    $effectDirection = Get-FileTransferEventField -Event $Effect -Name 'direction' -Default ''
    if ((Test-FileTransferStaleRejectedProofMeaningfulValue -Value $rejectionDirection) -and
        (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $effectDirection) -and
        -not [string]::Equals($rejectionDirection, $effectDirection, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $true
}

function Test-FileTransferStaleRejectedProofAuthorityMatches {
    param(
        [Parameter(Mandatory = $true)]$Rejection,
        [Parameter(Mandatory = $true)]$Effect
    )

    $authorityFields = @(
        [pscustomobject]@{ Stale = @('attempted_route', 'route'); Current = @('current_route'); Effect = @('route') },
        [pscustomobject]@{ Stale = @('attempted_protocol_version', 'protocol_version'); Current = @('current_protocol_version'); Effect = @('protocol_version') },
        [pscustomobject]@{ Stale = @('attempted_live_route_epoch', 'live_route_epoch'); Current = @('current_live_route_epoch'); Effect = @('live_route_epoch') },
        [pscustomobject]@{ Stale = @('attempted_runtime_generation', 'runtime_generation'); Current = @('current_runtime_generation'); Effect = @('runtime_generation') },
        [pscustomobject]@{ Stale = @('attempted_terminal_generation', 'terminal_generation'); Current = @('current_terminal_generation'); Effect = @('terminal_generation') },
        [pscustomobject]@{ Stale = @('attempted_leg_generation', 'leg_generation'); Current = @('current_leg_generation'); Effect = @('leg_generation') },
        [pscustomobject]@{ Stale = @('attempted_transport_epoch', 'transport_epoch'); Current = @('current_transport_epoch'); Effect = @('transport_epoch') },
        [pscustomobject]@{ Stale = @('attempted_bridge_recovery_generation', 'bridge_recovery_generation'); Current = @('current_bridge_recovery_generation'); Effect = @('bridge_recovery_generation') },
        [pscustomobject]@{ Stale = @('attempted_checkpoint_request_id', 'checkpoint_request_id'); Current = @('current_checkpoint_request_id'); Effect = @('checkpoint_request_id') },
        [pscustomobject]@{ Stale = @('attempted_checkpoint_generation', 'checkpoint_generation'); Current = @('current_checkpoint_generation'); Effect = @('checkpoint_generation') },
        [pscustomobject]@{ Stale = @('attempted_control_plane_token_generation', 'control_plane_token_generation', 'fallback_control_plane_token_generation', 'token_generation'); Current = @('current_control_plane_token_generation', 'current_fallback_control_plane_token_generation', 'current_token_generation'); Effect = @('control_plane_token_generation', 'fallback_control_plane_token_generation', 'token_generation') },
        [pscustomobject]@{ Stale = @('attempted_checkpoint_lease_generation', 'checkpoint_lease_generation', 'fallback_checkpoint_lease_generation', 'lease_generation'); Current = @('current_checkpoint_lease_generation', 'current_fallback_checkpoint_lease_generation', 'current_lease_generation'); Effect = @('checkpoint_lease_generation', 'fallback_checkpoint_lease_generation', 'lease_generation') },
        [pscustomobject]@{ Stale = @('attempted_pressure_generation', 'pressure_generation', 'fallback_control_plane_pressure_generation'); Current = @('current_pressure_generation', 'current_fallback_control_plane_pressure_generation'); Effect = @('pressure_generation', 'fallback_control_plane_pressure_generation') },
        [pscustomobject]@{ Stale = @('attempted_route_publication_generation', 'route_publication_generation', 'publication_generation'); Current = @('current_route_publication_generation', 'current_publication_generation'); Effect = @('route_publication_generation', 'publication_generation') },
        [pscustomobject]@{ Stale = @('attempted_route_commit_generation', 'route_commit_generation'); Current = @('current_route_commit_generation'); Effect = @('route_commit_generation') },
        [pscustomobject]@{ Stale = @('attempted_target_route', 'target_route'); Current = @('current_target_route'); Effect = @('target_route') },
        [pscustomobject]@{ Stale = @('attempted_target_protocol_version', 'target_protocol_version'); Current = @('current_target_protocol_version'); Effect = @('target_protocol_version') },
        [pscustomobject]@{ Stale = @('attempted_target_transport', 'target_transport'); Current = @('current_target_transport'); Effect = @('target_transport') },
        [pscustomobject]@{ Stale = @('attempted_contract_generation', 'contract_generation'); Current = @('current_contract_generation'); Effect = @('contract_generation') },
        [pscustomobject]@{ Stale = @('attempted_offer_generation', 'offer_generation'); Current = @('current_offer_generation'); Effect = @('offer_generation') },
        [pscustomobject]@{ Stale = @('attempted_retired_generation', 'retired_generation', 'retired_offer_generation'); Current = @('current_retired_generation', 'current_retired_offer_generation'); Effect = @('retired_generation', 'retired_offer_generation') },
        [pscustomobject]@{ Stale = @('attempted_regular_v4_liveness_generation', 'regular_v4_liveness_generation'); Current = @('current_regular_v4_liveness_generation'); Effect = @('regular_v4_liveness_generation') },
        [pscustomobject]@{ Stale = @('attempted_bridge_decision_generation', 'bridge_decision_generation'); Current = @('current_bridge_decision_generation'); Effect = @('bridge_decision_generation') }
    )

    $matchedStaleAuthority = $false
    foreach ($field in @($authorityFields)) {
        $staleValue = Get-FileTransferStaleRejectedProofFirstField -Event $Rejection -Names $field.Stale
        $currentValue = Get-FileTransferStaleRejectedProofFirstField -Event $Rejection -Names $field.Current
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $staleValue) -or
            -not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $currentValue) -or
            [string]::Equals($staleValue, $currentValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $effectValue = Get-FileTransferStaleRejectedProofFirstField -Event $Effect -Names $field.Effect
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $effectValue)) {
            continue
        }

        if ([string]::Equals($effectValue, $currentValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }

        if ([string]::Equals($effectValue, $staleValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matchedStaleAuthority = $true
        }
        else {
            return $false
        }
    }

    if (-not $matchedStaleAuthority -and
        $Rejection.EventName -in @(
            'filetransfer_runtime_unlock_callback_rejected',
            'filetransfer_runtime_unlock_precommit_probe_pending_rejected')) {
        $runtimeUnlockFields = @(
            @('transaction_generation'),
            @('offer_generation'),
            @('tuna_path_lease_generation'),
            @('probe_id'),
            @('same_path_tuna_probe_ack_id'),
            @('route_commit_generation'),
            @('pending_probe_generation')
        )

        $matchedRuntimeUnlockAuthority = $false
        foreach ($fieldNames in @($runtimeUnlockFields)) {
            $staleValue = Get-FileTransferStaleRejectedProofFirstField -Event $Rejection -Names $fieldNames
            if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $staleValue)) {
                continue
            }

            $effectValue = Get-FileTransferStaleRejectedProofFirstField -Event $Effect -Names $fieldNames
            if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $effectValue)) {
                continue
            }

            if (-not [string]::Equals($effectValue, $staleValue, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }

            $matchedRuntimeUnlockAuthority = $true
        }

        if ($matchedRuntimeUnlockAuthority) {
            return $true
        }
    }

    return $matchedStaleAuthority
}

function Test-FileTransferStaleRejectedProofForbiddenEffect {
    param(
        [Parameter(Mandatory = $true)][string]$Family,
        [Parameter(Mandatory = $true)]$Event,
        [string]$FallbackSubfamily = ''
    )

    $name = [string]$Event.EventName
    if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $FallbackSubfamily) {
        switch ($FallbackSubfamily) {
            'fallback_send' {
                return $name -in @(
                    'filetransfer_control_plane_delivery_result',
                    'filetransfer_control_plane_delivery_observed',
                    'filetransfer_control_plane_delivery_failed',
                    'filetransfer_control_plane_delivery_unavailable',
                    'filetransfer_fallback_checkpoint_requested',
                    'filetransfer_fallback_checkpoint_accepted',
                    'filetransfer_fallback_checkpoint_retired',
                    'filetransfer_fallback_checkpoint_exchange_retired',
                    'filetransfer_fallback_checkpoint_retry_state_cleared',
                    'filetransfer_fallback_checkpoint_recovery_leg_started')
            }
            'fallback_checkpoint' {
                return $name -in @(
                    'filetransfer_fallback_checkpoint_requested',
                    'filetransfer_fallback_checkpoint_accepted',
                    'filetransfer_fallback_checkpoint_retired',
                    'filetransfer_fallback_checkpoint_exchange_retired',
                    'filetransfer_fallback_checkpoint_retry_state_cleared',
                    'filetransfer_fallback_checkpoint_recovery_leg_started',
                    'filetransfer_fallback_leg_authority_checkpoint_accepted',
                    'filetransfer_live_route_epoch_recovered')
            }
            'fallback_reissue' {
                if ($name -in @(
                        'filetransfer_fallback_checkpoint_reissue_requested',
                        'filetransfer_fallback_checkpoint_response_timeout_reissued',
                        'filetransfer_fallback_checkpoint_request_sent',
                        'filetransfer_v6_regular_nkn_state_refresh_sent',
                        'filetransfer_v6_frontier_request_sent')) {
                    return $true
                }

                if ($name -in @('filetransfer_control_plane_delivery_result', 'filetransfer_control_plane_delivery_observed')) {
                    $kind = (Get-FileTransferEventField -Event $Event -Name 'kind' -Default '').ToLowerInvariant()
                    $reason = (Get-FileTransferEventField -Event $Event -Name 'reason' -Default '').ToLowerInvariant()
                    return $kind -match 'fallback_checkpoint_request|frontier_request' -or $reason -match 'reissue|retry|replay|checkpoint_request'
                }

                return $false
            }
            'fallback_tail_yield' {
                return $name -in @(
                    'filetransfer_fallback_tail_quiescent_yield_granted',
                    'filetransfer_fallback_tail_failed',
                    'filetransfer_fallback_tail_yield_proof_sent',
                    'filetransfer_fallback_tail_paired_yield_granted',
                    'filetransfer_fallback_tail_reconciliation_requested',
                    'filetransfer_fallback_tail_reconciliation_accepted',
                    'filetransfer_runtime_unlock_admission_granted',
                    'filetransfer_normal_to_tuna_handoff_route_hint_deferred',
                    'filetransfer_route_selected',
                    'filetransfer_live_route_epoch_started',
                    'filetransfer_live_route_epoch_recovered',
                    'filetransfer_runtime_started')
            }
            'fallback_pressure' {
                return $name -in @(
                    'filetransfer_post_tuna_fallback_control_plane_pressure_marked',
                    'filetransfer_post_tuna_fallback_control_plane_pressure_cleared',
                    'filetransfer_runtime_unlock_admission_granted',
                    'runtime_unlock_precommit_probe_started',
                    'runtime_unlock_precommit_probe_sent')
            }
        }
    }

    switch ($Family) {
        'liveness' {
            return $name -in @(
                'session_liveness_timeout_deferred_for_current_filetransfer_recovery',
                'session_liveness_timeout_deferred_for_bridge_filetransfer_recovery',
                'bridge_receive_stall_recovery_exhausted_deferred_for_filetransfer_recovery',
                'bridge_receive_stall_recovery_exhausted_deferred_for_active_filetransfer_progress',
                'bridge_receive_stall_recovery_exhausted_suppressed_by_active_filetransfer_deferral',
                'bridge_receive_stall_recovery_exhausted_suppressed_by_sibling_deferral')
        }
        'repair' {
            return $name -in @(
                'filetransfer_v4_repair_scheduled',
                'filetransfer_v4_repair_requested',
                'filetransfer_v4_frontier_stall_missing_range_due',
                'filetransfer_v4_emergency_frontier_repair_requested',
                'filetransfer_v4_post_fallback_frontier_backfill_requested',
                'filetransfer_v6_frontier_repair_requested',
                'filetransfer_v6_frontier_request_sent',
                'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested',
                'filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued',
                'filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued',
                'filetransfer_regular_v4_peer_silence_safety_replay_started',
                'filetransfer_regular_v4_receive_recovery_peer_silence_replay_armed',
                'filetransfer_transport_rebind_safety_replay_started',
                'filetransfer_v4_post_reactivation_frontier_replay_started',
                'filetransfer_rebind_duplicate_state_repair_enqueued',
                'runtime_unlock_tuna_probe_sidecar_closed_fresh_transaction_scheduled',
                'tuna_acceleration_runtime_unlock_unobserved_offer_reset_retry_scheduled',
                'tuna_acceleration_runtime_unlock_interrupted_offer_reset_retry_scheduled')
        }
        'progress' {
            return $name -in @(
                'filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced',
                'filetransfer_v6_receiver_state_coalesced',
                'filetransfer_v6_receiver_state_sent',
                'filetransfer_v4_state_sent')
        }
        'checkpoint' {
            return $name -in @(
                'filetransfer_fallback_checkpoint_requested',
                'filetransfer_fallback_checkpoint_accepted',
                'filetransfer_fallback_checkpoint_retired',
                'filetransfer_fallback_checkpoint_reissue_requested',
                'filetransfer_fallback_checkpoint_exchange_retired',
                'filetransfer_fallback_checkpoint_retry_state_cleared',
                'filetransfer_fallback_checkpoint_recovery_leg_started',
                'filetransfer_fallback_leg_authority_checkpoint_accepted',
                'filetransfer_fallback_tail_quiescent_yield_granted',
                'filetransfer_fallback_tail_failed',
                'filetransfer_fallback_tail_yield_proof_sent',
                'filetransfer_fallback_tail_paired_yield_granted',
                'filetransfer_fallback_tail_reconciliation_requested',
                'filetransfer_fallback_tail_reconciliation_accepted')
        }
        'bridge_recovery' {
            return $name -in @(
                'filetransfer_fallback_leg_authority_bridge_recovery_requested',
                'filetransfer_fallback_leg_authority_bridge_recovery_escalated',
                'filetransfer_receive_recovery_requested',
                'filetransfer_regular_v4_receive_recovery_requested',
                'nkn_bridge_receive_stall_recovery_requested',
                'nkn_bridge_receive_stall_recovery_started',
                'bridge_receive_stall_recovery_started')
        }
        'control_plane' {
            return $name -in @(
                'filetransfer_control_plane_delivery_result',
                'filetransfer_control_plane_delivery_observed',
                'filetransfer_control_plane_delivery_failed',
                'filetransfer_control_plane_delivery_unavailable',
                'filetransfer_fallback_checkpoint_request_sent',
                'filetransfer_fallback_checkpoint_proof_sent',
                'filetransfer_fallback_checkpoint_response_timeout_reissued',
                'filetransfer_v6_regular_nkn_state_refresh_sent',
                'filetransfer_v6_frontier_request_sent',
                'filetransfer_v6_receiver_state_sent',
                'filetransfer_data_frame_dispatched',
                'runtime_unlock_precommit_probe_started',
                'runtime_unlock_precommit_probe_sent',
                'runtime_unlock_tuna_probe_sent',
                'runtime_unlock_tuna_probe_received',
                'runtime_unlock_tuna_probe_ack_sent',
                'runtime_unlock_precommit_probe_ack_delivery_result',
                'runtime_unlock_tuna_probe_ack_received',
                'runtime_unlock_precommit_probe_ack_sent',
                'runtime_unlock_precommit_probe_commit_confirmation_sent',
                'runtime_unlock_precommit_probe_acked',
                'runtime_unlock_precommit_probe_failed',
                'runtime_unlock_probe_started',
                'runtime_unlock_probe_acked',
                'runtime_unlock_probe_failed',
                'runtime_unlock_paired_commit_started',
                'runtime_unlock_paired_commit_confirmed',
                'runtime_unlock_paired_commit_aborted',
                'runtime_unlock_fallback_unavailable_suppressed',
                'filetransfer_runtime_unlock_marked_fallback_unavailable',
                'runtime_unlock_retry_released_after_regular_v4_receive_recovery',
                'runtime_unlock_retry_dispatch_authority_granted',
                'runtime_unlock_retry_dispatch_authority_rejected',
                'runtime_unlock_retry_dispatch_observed',
                'tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled',
                'tuna_acceleration_retry_scheduled',
                'runtime_unlock_offer_dispatched_after_listener_rearm',
                'tuna_acceleration_activation_offer_not_observed')
        }
        'route_publication' {
            return $name -in @(
                'filetransfer_route_selected',
                'filetransfer_live_route_epoch_started',
                'filetransfer_live_route_epoch_recovered',
                'filetransfer_peer_post_tuna_fallback_v6_proof_promoted_route',
                'filetransfer_v6_peer_epoch_promoted_live_route',
                'filetransfer_route_transitioned',
                'filetransfer_leg_started',
                'filetransfer_leg_frozen',
                'filetransfer_v6_epoch_started',
                'filetransfer_v6_epoch_reused',
                'filetransfer_v6_epoch_state_changed',
                'filetransfer_runtime_unlock_route_commit_accepted',
                'filetransfer_runtime_unlock_route_commit_publication_deferred',
                'runtime_unlock_precommit_probe_paired_commit_confirmed',
                'runtime_unlock_paired_route_commit_publication_enabled',
                'filetransfer_route_hint_authorized_publication_accepted',
                'filetransfer_runtime_started')
        }
        default { return $false }
    }
}

function Test-FileTransferPublicationMissingAuthorityRejection {
    param([Parameter(Mandatory = $true)]$Event)

    return $Event.EventName -eq 'filetransfer_route_hint_ignored_missing_publication_authority'
}

function Test-FileTransferPublicationAuthorityAcceptanceEvent {
    param([Parameter(Mandatory = $true)]$Event)

    return $Event.EventName -eq 'filetransfer_route_hint_authorized_publication_accepted'
}

function Test-FileTransferPublicationScopeRouteMatches {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if (-not (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $Left -Effect $Right)) {
        return $false
    }

    foreach ($fieldName in @('route', 'protocol_version', 'target_route', 'target_protocol_version', 'target_transport')) {
        $leftValue = Get-FileTransferEventField -Event $Left -Name $fieldName -Default ''
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $leftValue)) {
            continue
        }

        $rightValue = Get-FileTransferEventField -Event $Right -Name $fieldName -Default ''
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $rightValue)) {
            continue
        }

        if (-not [string]::Equals($leftValue, $rightValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferPublicationEffectHasPriorAuthorizedAcceptance {
    param(
        [Parameter(Mandatory = $true)]$Rejection,
        [Parameter(Mandatory = $true)]$Effect,
        [Parameter(Mandatory = $true)][object[]]$Events
    )

    foreach ($candidate in @($Events | Where-Object {
                $_.Sequence -gt $Rejection.Sequence -and
                $_.Sequence -lt $Effect.Sequence -and
                (Test-FileTransferPublicationAuthorityAcceptanceEvent -Event $_)
            } | Sort-Object Sequence)) {
        if ((Test-FileTransferPublicationScopeRouteMatches -Left $Rejection -Right $candidate) -and
            (Test-FileTransferPublicationScopeRouteMatches -Left $candidate -Right $Effect)) {
            return $true
        }
    }

    return $false
}

function Test-FileTransferPublicationAuthorityFieldEvent {
    param([Parameter(Mandatory = $true)]$Event)

    return $Event.EventName -in @(
        'filetransfer_route_selected',
        'filetransfer_live_route_epoch_started',
        'filetransfer_live_route_epoch_recovered',
        'filetransfer_runtime_started',
        'filetransfer_route_transitioned',
        'filetransfer_route_hint_authorized_publication_accepted')
}

function Get-FileTransferPublicationAuthorityRequiredFields {
    param([Parameter(Mandatory = $true)]$Event)

    $fields = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @(
            'session_id',
            'transfer_id',
            'direction',
            'route',
            'protocol_version',
            'live_route_epoch',
            'runtime_generation',
            'terminal_generation',
            'leg_generation',
            'route_publication_generation',
            'route_commit_generation',
            'target_route',
            'target_protocol_version',
            'target_transport',
            'route_publication_action_kind',
            'coordinator_decision_reason')) {
        $fields.Add($fieldName) | Out-Null
    }

    return $fields.ToArray()
}

function Test-FileTransferPublicationAuthorityFieldValue {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)][string]$FieldName
    )

    $value = Get-FileTransferEventField -Event $Event -Name $FieldName -Default ''
    if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $value)) {
        return $false
    }

    if ($FieldName -eq 'route_publication_action_kind') {
        return $value -eq 'RoutePublication' -or $value -eq 'RouteCommitPublication'
    }

    if ($FieldName -match '(^protocol_version$|^target_protocol_version$|generation$|epoch$)') {
        $number = 0L
        if (-not [long]::TryParse([string]$value, [ref]$number)) {
            return $false
        }

        if ($number -lt 0) {
            return $false
        }

        if ($FieldName -eq 'route_commit_generation') {
            $actionKind = Get-FileTransferEventField -Event $Event -Name 'route_publication_action_kind' -Default ''
            if ($actionKind -eq 'RouteCommitPublication' -and $number -le 0) {
                return $false
            }
        }
    }

    return $true
}

function Test-FileTransferFileTunaV4DataPathEvent {
    param([Parameter(Mandatory = $true)]$Event)

    return (Get-FileTransferEventField -Event $Event -Name 'route' -Default '') -eq 'file_tuna_v4' -and
        (Get-FileTransferEventField -Event $Event -Name 'protocol_version' -Default '') -eq '4' -and
        (Get-FileTransferEventField -Event $Event -Name 'target_transport' -Default '') -eq 'tuna' -and
        (Get-FileTransferEventInt64Field -Event $Event -Name 'route_publication_generation' -Default 0) -gt 0
}

function Test-FileTransferFileTunaV4DataPathIntentEvent {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -ne 'filetransfer_v4_chunk_batch_sent' -and
        $Event.EventName -ne 'filetransfer_v4_repair_sent') {
        return $false
    }

    if (-not (Test-FileTransferFileTunaV4DataPathEvent -Event $Event)) {
        return $false
    }

    $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'raw_bytes' -Default 0
    $sentChunkCount = Get-FileTransferEventInt64Field -Event $Event -Name 'sent_chunk_count' -Default 0
    $batchChunkCount = Get-FileTransferEventInt64Field -Event $Event -Name 'batch_chunk_count' -Default 0
    return $rawBytes -gt 0 -or $sentChunkCount -gt 0 -or $batchChunkCount -gt 0
}

function Test-FileTransferFileTunaV4PayloadDataPathEvent {
    param([Parameter(Mandatory = $true)]$Event)

    $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'raw_payload_bytes' -Default -1
    if ($rawBytes -lt 0) {
        $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'raw_bytes' -Default -1
    }
    if ($rawBytes -lt 0) {
        $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'payload_bytes' -Default 0
    }

    $batchChunkCount = Get-FileTransferEventInt64Field -Event $Event -Name 'batch_chunk_count' -Default 0
    if ($batchChunkCount -le 0) {
        $batchChunkCount = Get-FileTransferEventInt64Field -Event $Event -Name 'sent_chunk_count' -Default 0
    }

    if ($rawBytes -le 0 -or $batchChunkCount -le 0) {
        return $false
    }

    $frameType = Get-FileTransferEventField -Event $Event -Name 'frame_type' -Default ''
    if ($frameType -eq 'filetransfer.chunk_batch.v4') {
        return $true
    }

    $batchProfile = Get-FileTransferEventField -Event $Event -Name 'batch_profile' -Default ''
    return -not [string]::IsNullOrWhiteSpace($batchProfile) -and
        $batchProfile.StartsWith('v4_', [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-FileTransferFileTunaV4DataPathActualEvent {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -ne 'filetransfer_data_path_send_result' -and
        $Event.EventName -ne 'filetransfer_data_path_receive_result' -and
        $Event.EventName -ne 'filetransfer_tuna_data_path_peer_visible' -and
        $Event.EventName -ne 'filetransfer_tuna_data_path_mismatch') {
        return $false
    }

    return (Test-FileTransferFileTunaV4DataPathEvent -Event $Event) -and
        (Test-FileTransferFileTunaV4PayloadDataPathEvent -Event $Event)
}

function Test-FileTransferFileTunaV4DataPathScopeMatches {
    param(
        [Parameter(Mandatory = $true)]$Intent,
        [Parameter(Mandatory = $true)]$Actual
    )

    foreach ($fieldName in @('transfer_id', 'session_id', 'route_publication_generation', 'runtime_generation', 'terminal_generation', 'leg_generation', 'live_route_epoch')) {
        $intentValue = Get-FileTransferEventField -Event $Intent -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($intentValue)) {
            continue
        }

        $actualValue = Get-FileTransferEventField -Event $Actual -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($actualValue) -or
            -not [string]::Equals($intentValue, $actualValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Get-FileTransferFileTunaV4DataPathScopeKey {
    param([Parameter(Mandatory = $true)]$Event)

    $values = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @('transfer_id', 'session_id', 'route_publication_generation', 'runtime_generation', 'terminal_generation', 'leg_generation', 'live_route_epoch')) {
        $value = Get-FileTransferEventField -Event $Event -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($value)) {
            return ''
        }

        $values.Add($value.ToLowerInvariant()) | Out-Null
    }

    return [string]::Join('|', $values)
}

function Get-FileTransferFileTunaV4DataPathRangeKey {
    param([Parameter(Mandatory = $true)]$Event)

    $scopeKey = Get-FileTransferFileTunaV4DataPathScopeKey -Event $Event
    if ([string]::IsNullOrWhiteSpace($scopeKey) -or
        -not (Test-FileTransferFileTunaV4DataPathHasRange -Event $Event)) {
        return ''
    }

    $start = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    $endExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Event
    if ($start -lt 0 -or $endExclusive -le $start) {
        return ''
    }

    return ("{0}|{1}|{2}" -f $scopeKey, $start, $endExclusive)
}

function Get-FileTransferFileTunaV4DataPathRangeStart {
    param([Parameter(Mandatory = $true)]$Event)

    $start = Get-FileTransferEventInt64Field -Event $Event -Name 'start_chunk_index' -Default -1
    if ($start -ge 0) {
        return $start
    }

    $start = Get-FileTransferEventInt64Field -Event $Event -Name 'first_start_chunk_index' -Default -1
    if ($start -ge 0) {
        return $start
    }

    $chunkIndex = Get-FileTransferEventField -Event $Event -Name 'chunk_index' -Default ''
    if ($chunkIndex -match '^(\d+)(?:-(\d+))?$') {
        return [int64]$Matches[1]
    }

    return -1
}

function Get-FileTransferFileTunaV4DataPathRangeEndExclusive {
    param([Parameter(Mandatory = $true)]$Event)

    $endExclusive = Get-FileTransferEventInt64Field -Event $Event -Name 'last_end_chunk_exclusive' -Default -1
    if ($endExclusive -gt 0) {
        return $endExclusive
    }

    $start = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    if ($start -lt 0) {
        return -1
    }

    $chunkIndex = Get-FileTransferEventField -Event $Event -Name 'chunk_index' -Default ''
    if ($chunkIndex -match '^\d+-(\d+)$') {
        return [int64]$Matches[1] + 1
    }

    $count = Get-FileTransferEventInt64Field -Event $Event -Name 'batch_chunk_count' -Default 0
    if ($count -le 0) {
        $count = Get-FileTransferEventInt64Field -Event $Event -Name 'sent_chunk_count' -Default 0
    }
    if ($count -le 0) {
        $count = Get-FileTransferEventInt64Field -Event $Event -Name 'frame_chunk_count' -Default 0
    }

    if ($count -le 0) {
        return -1
    }

    return $start + $count
}

function Test-FileTransferFileTunaV4DataPathHasRange {
    param([Parameter(Mandatory = $true)]$Event)

    $start = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    $endExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Event
    return $start -ge 0 -and $endExclusive -gt $start
}

function Test-FileTransferFileTunaV4DataPathSequenceAllowsMatch {
    param(
        [Parameter(Mandatory = $true)]$Intent,
        [Parameter(Mandatory = $true)]$Actual
    )

    if ((Test-FileTransferFileTunaV4DataPathHasRange -Event $Intent) -and
        (Test-FileTransferFileTunaV4DataPathHasRange -Event $Actual)) {
        return $true
    }

    if ($null -ne $Intent.PSObject.Properties['Sequence'] -and
        $null -ne $Actual.PSObject.Properties['Sequence'] -and
        [int64]$Actual.Sequence -lt [int64]$Intent.Sequence) {
        return $false
    }

    return $true
}

function Test-FileTransferFileTunaV4DataPathRangeMatches {
    param(
        [Parameter(Mandatory = $true)]$Intent,
        [Parameter(Mandatory = $true)]$Actual
    )

    if (-not (Test-FileTransferFileTunaV4DataPathHasRange -Event $Intent) -or
        -not (Test-FileTransferFileTunaV4DataPathHasRange -Event $Actual)) {
        return $true
    }

    $intentStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Intent
    $intentEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Intent
    $actualStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Actual
    $actualEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Actual

    if ($Intent.EventName -eq 'filetransfer_v4_repair_sent') {
        return $actualStart -ge $intentStart -and
            $actualEndExclusive -le $intentEndExclusive
    }

    return $actualStart -eq $intentStart -and
        $actualEndExclusive -eq $intentEndExclusive
}

function Test-FileTransferFileTunaV4DataPathSendShapeMatches {
    param(
        [Parameter(Mandatory = $true)]$Intent,
        [Parameter(Mandatory = $true)]$Actual
    )

    $proofDirection = Get-FileTransferEventField -Event $Actual -Name 'proof_direction' -Default ''
    if ($proofDirection -ne 'receive' -and $proofDirection -ne 'send') {
        return $false
    }

    $intentBatchChunkCount = Get-FileTransferEventInt64Field -Event $Intent -Name 'batch_chunk_count' -Default 0
    $intentSentChunkCount = Get-FileTransferEventInt64Field -Event $Intent -Name 'sent_chunk_count' -Default 0
    $actualBatchChunkCount = Get-FileTransferEventInt64Field -Event $Actual -Name 'batch_chunk_count' -Default 0
    if ($actualBatchChunkCount -gt 0) {
        if ($Intent.EventName -eq 'filetransfer_v4_chunk_batch_sent' -and
            $intentBatchChunkCount -gt 0 -and
            $intentBatchChunkCount -ne $actualBatchChunkCount) {
            return $false
        }

        if ($Intent.EventName -eq 'filetransfer_v4_repair_sent' -and
            $intentSentChunkCount -gt 0 -and
            $actualBatchChunkCount -gt $intentSentChunkCount) {
            return $false
        }
    }

    return Test-FileTransferFileTunaV4DataPathRangeMatches -Intent $Intent -Actual $Actual
}

function Get-FileTransferFileTunaV4DataPathProof {
    param([Parameter(Mandatory = $true)]$Summary)

    $cachePropertyName = '__FileTransferFileTunaV4DataPathProof'
    if ($null -ne $Summary.PSObject.Properties[$cachePropertyName]) {
        return $Summary.PSObject.Properties[$cachePropertyName].Value
    }

    [object[]]$events = @()
    if ($null -ne $Summary.PSObject.Properties['AllEvents']) {
        $events = @($Summary.AllEvents)
    }
    if ($events.Count -eq 0) {
        $events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    $events = @($events | Sort-Object Sequence)
    [object[]]$intentEvents = @($events | Where-Object { Test-FileTransferFileTunaV4DataPathIntentEvent -Event $_ })
    [object[]]$actualEvents = @($events | Where-Object { Test-FileTransferFileTunaV4DataPathActualEvent -Event $_ })
    [object[]]$mismatchEvents = @($actualEvents | Where-Object {
            $_.EventName -eq 'filetransfer_tuna_data_path_mismatch' -or
            (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'regular_nkn' -or
            (Get-FileTransferEventField -Event $_ -Name 'delivery_transport' -Default '') -eq 'regular_nkn'
        })
    [object[]]$tunaActualEvents = @($actualEvents | Where-Object {
            (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'tuna' -or
            (Get-FileTransferEventField -Event $_ -Name 'delivery_transport' -Default '') -eq 'tuna'
        })
    [object[]]$peerVisibleEvents = @($actualEvents | Where-Object { $_.EventName -eq 'filetransfer_tuna_data_path_peer_visible' })

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    $missingActualProofCount = 0
    $usedPeerVisibleEventIndexes = New-Object 'System.Collections.Generic.HashSet[int]'
    $usedRepairPeerVisibleEventIndexes = New-Object 'System.Collections.Generic.HashSet[int]'
    $peerVisibleAvailableChunkCountsByScopeKey = @{}
    $peerVisibleRangeCountsByRangeKey = @{}
    foreach ($peerVisible in @($peerVisibleEvents)) {
        if (-not (Test-FileTransferFileTunaV4DataPathHasRange -Event $peerVisible)) {
            continue
        }

        $rangeKey = Get-FileTransferFileTunaV4DataPathRangeKey -Event $peerVisible
        if (-not [string]::IsNullOrWhiteSpace($rangeKey)) {
            if (-not $peerVisibleRangeCountsByRangeKey.ContainsKey($rangeKey)) {
                $peerVisibleRangeCountsByRangeKey[$rangeKey] = 0
            }

            $peerVisibleRangeCountsByRangeKey[$rangeKey] = [int]$peerVisibleRangeCountsByRangeKey[$rangeKey] + 1
        }

        $scopeKey = Get-FileTransferFileTunaV4DataPathScopeKey -Event $peerVisible
        if ([string]::IsNullOrWhiteSpace($scopeKey)) {
            continue
        }

        if (-not $peerVisibleAvailableChunkCountsByScopeKey.ContainsKey($scopeKey)) {
            $peerVisibleAvailableChunkCountsByScopeKey[$scopeKey] = New-Object 'System.Collections.Generic.Dictionary[int64,int]'
        }

        $availableChunkCounts = $peerVisibleAvailableChunkCountsByScopeKey[$scopeKey]
        $actualStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $peerVisible
        $actualEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $peerVisible
        for ($chunkIndex = $actualStart; $chunkIndex -lt $actualEndExclusive; $chunkIndex++) {
            $chunkKey = [int64]$chunkIndex
            if (-not $availableChunkCounts.ContainsKey($chunkKey)) {
                $availableChunkCounts[$chunkKey] = 0
            }

            $availableChunkCounts[$chunkKey] = [int]$availableChunkCounts[$chunkKey] + 1
        }
    }

    foreach ($intent in @($intentEvents)) {
        if ($intent.EventName -eq 'filetransfer_v4_repair_sent') {
            $requiredRepairChunkCount = Get-FileTransferEventInt64Field -Event $intent -Name 'sent_chunk_count' -Default 0
            if ($requiredRepairChunkCount -le 0) {
                $requiredRepairChunkCount = Get-FileTransferEventInt64Field -Event $intent -Name 'batch_chunk_count' -Default 0
            }

            $intentScopeKey = Get-FileTransferFileTunaV4DataPathScopeKey -Event $intent
            if ($requiredRepairChunkCount -gt 0 -and
                (Test-FileTransferFileTunaV4DataPathHasRange -Event $intent) -and
                -not [string]::IsNullOrWhiteSpace($intentScopeKey) -and
                $peerVisibleAvailableChunkCountsByScopeKey.ContainsKey($intentScopeKey)) {
                $intentStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $intent
                $intentEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $intent
                $availableChunkCounts = $peerVisibleAvailableChunkCountsByScopeKey[$intentScopeKey]
                $matchedRepairChunks = New-Object 'System.Collections.Generic.List[int64]'
                $matchedRepairCoverageCount = 0L
                for ($chunkIndex = $intentStart; $chunkIndex -lt $intentEndExclusive; $chunkIndex++) {
                    $chunkKey = [int64]$chunkIndex
                    if ($availableChunkCounts.ContainsKey($chunkKey) -and [int]$availableChunkCounts[$chunkKey] -gt 0) {
                        [void]$matchedRepairChunks.Add($chunkKey)
                        $matchedRepairCoverageCount++
                    }
                }

                if ($matchedRepairCoverageCount -lt $requiredRepairChunkCount) {
                    $missingActualProofCount++
                    $findings.Add(("file_tuna_v4 V4 repair intent had insufficient peer-visible Tuna data-path proof: required_chunks={0}; matched_chunks={1}; intent={2}" -f $requiredRepairChunkCount, $matchedRepairCoverageCount, (Format-FileTransferEvidenceLine -Event $intent))) | Out-Null
                    $evidence.Add($intent) | Out-Null
                }
                else {
                    foreach ($chunkKey in @($matchedRepairChunks)) {
                        $availableChunkCounts[$chunkKey] = [int]$availableChunkCounts[$chunkKey] - 1
                    }
                }

                continue
            }

            $matchedRepairChunkCount = 0L
            $matchedRepairCoveredChunks = New-Object 'System.Collections.Generic.HashSet[int64]'
            $matchedRepairHasRangeProof = $false
            $matchedRepairPeerVisibleIndexes = New-Object 'System.Collections.Generic.List[int]'
            for ($peerVisibleIndex = 0; $peerVisibleIndex -lt $peerVisibleEvents.Count; $peerVisibleIndex++) {
                if ($usedRepairPeerVisibleEventIndexes.Contains([int]$peerVisibleIndex)) {
                    continue
                }

                $peerVisible = $peerVisibleEvents[$peerVisibleIndex]
                if (-not (Test-FileTransferFileTunaV4DataPathSequenceAllowsMatch -Intent $intent -Actual $peerVisible)) {
                    continue
                }

                if ((Test-FileTransferFileTunaV4DataPathScopeMatches -Intent $intent -Actual $peerVisible) -and
                    (Test-FileTransferFileTunaV4DataPathSendShapeMatches -Intent $intent -Actual $peerVisible)) {
                    $actualBatchChunkCount = Get-FileTransferEventInt64Field -Event $peerVisible -Name 'batch_chunk_count' -Default 0
                    if ($actualBatchChunkCount -le 0) {
                        continue
                    }

                    [void]$matchedRepairPeerVisibleIndexes.Add([int]$peerVisibleIndex)
                    $matchedRepairChunkCount += $actualBatchChunkCount
                    if ((Test-FileTransferFileTunaV4DataPathHasRange -Event $intent) -and
                        (Test-FileTransferFileTunaV4DataPathHasRange -Event $peerVisible)) {
                        $matchedRepairHasRangeProof = $true
                        $actualStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $peerVisible
                        $actualEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $peerVisible
                        for ($chunkIndex = $actualStart; $chunkIndex -lt $actualEndExclusive; $chunkIndex++) {
                            [void]$matchedRepairCoveredChunks.Add([int64]$chunkIndex)
                        }
                    }

                    $matchedRepairEffectiveChunkCount = if ($matchedRepairHasRangeProof) { [int64]$matchedRepairCoveredChunks.Count } else { $matchedRepairChunkCount }
                    if ($requiredRepairChunkCount -gt 0 -and $matchedRepairEffectiveChunkCount -ge $requiredRepairChunkCount) {
                        break
                    }
                }
            }

            $matchedRepairEffectiveChunkCount = if ($matchedRepairHasRangeProof) { [int64]$matchedRepairCoveredChunks.Count } else { $matchedRepairChunkCount }
            if ($requiredRepairChunkCount -gt 0 -and $matchedRepairEffectiveChunkCount -ge $requiredRepairChunkCount) {
                foreach ($peerVisibleIndex in @($matchedRepairPeerVisibleIndexes)) {
                    [void]$usedRepairPeerVisibleEventIndexes.Add([int]$peerVisibleIndex)
                }
            }
            else {
                $missingActualProofCount++
                $findings.Add(("file_tuna_v4 V4 repair intent had insufficient peer-visible Tuna data-path proof: required_chunks={0}; matched_chunks={1}; intent={2}" -f $requiredRepairChunkCount, $matchedRepairEffectiveChunkCount, (Format-FileTransferEvidenceLine -Event $intent))) | Out-Null
                $evidence.Add($intent) | Out-Null
            }

            continue
        }

        $intentRangeKey = Get-FileTransferFileTunaV4DataPathRangeKey -Event $intent
        if (-not [string]::IsNullOrWhiteSpace($intentRangeKey)) {
            if ($peerVisibleRangeCountsByRangeKey.ContainsKey($intentRangeKey) -and
                [int]$peerVisibleRangeCountsByRangeKey[$intentRangeKey] -gt 0) {
                $peerVisibleRangeCountsByRangeKey[$intentRangeKey] = [int]$peerVisibleRangeCountsByRangeKey[$intentRangeKey] - 1
                continue
            }

            $missingActualProofCount++
            $findings.Add(("file_tuna_v4 V4 send intent had no matching peer-visible Tuna data-path proof: intent={0}" -f (Format-FileTransferEvidenceLine -Event $intent))) | Out-Null
            $evidence.Add($intent) | Out-Null
            continue
        }

        $matchedPeerVisibleIndex = -1
        for ($peerVisibleIndex = 0; $peerVisibleIndex -lt $peerVisibleEvents.Count; $peerVisibleIndex++) {
            if ($usedPeerVisibleEventIndexes.Contains([int]$peerVisibleIndex)) {
                continue
            }

            $peerVisible = $peerVisibleEvents[$peerVisibleIndex]
            if (-not (Test-FileTransferFileTunaV4DataPathSequenceAllowsMatch -Intent $intent -Actual $peerVisible)) {
                continue
            }

            if ((Test-FileTransferFileTunaV4DataPathScopeMatches -Intent $intent -Actual $peerVisible) -and
                (Test-FileTransferFileTunaV4DataPathSendShapeMatches -Intent $intent -Actual $peerVisible)) {
                $matchedPeerVisibleIndex = $peerVisibleIndex
                break
            }
        }

        if ($matchedPeerVisibleIndex -ge 0) {
            [void]$usedPeerVisibleEventIndexes.Add([int]$matchedPeerVisibleIndex)
        }
        else {
            $missingActualProofCount++
            $findings.Add(("file_tuna_v4 V4 send intent had no matching peer-visible Tuna data-path proof: intent={0}" -f (Format-FileTransferEvidenceLine -Event $intent))) | Out-Null
            $evidence.Add($intent) | Out-Null
        }
    }

    foreach ($mismatch in @($mismatchEvents)) {
        $findings.Add(("file_tuna_v4 V4 data path resolved to regular NKN: effect={0}" -f (Format-FileTransferEvidenceLine -Event $mismatch))) | Out-Null
        $evidence.Add($mismatch) | Out-Null
    }

    $peerVisiblePayloadBytes = 0L
    foreach ($event in @($peerVisibleEvents)) {
        $peerVisiblePayloadBytes += Get-FileTransferEventInt64Field -Event $event -Name 'payload_bytes' -Default 0
    }

    $verdict = if ($intentEvents.Count -eq 0 -and $actualEvents.Count -eq 0) { 'none' } elseif ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    $proof = [pscustomobject]@{
        Verdict = $verdict
        FailureClass = if ($verdict -eq 'fail') { 'file_tuna_data_path_mismatch' } else { 'none' }
        IntendedSendCount = [int]$intentEvents.Count
        ActualTunaSendCount = [int]$tunaActualEvents.Count
        ActualRegularNknSendCount = [int]$mismatchEvents.Count
        MismatchCount = [int]$mismatchEvents.Count
        MissingActualProofCount = [int]$missingActualProofCount
        PeerVisiblePayloadBytes = [long]$peerVisiblePayloadBytes
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
    $Summary | Add-Member -NotePropertyName $cachePropertyName -NotePropertyValue $proof -Force
    return $proof
}

function Get-FileTransferPublicationAuthorityFieldCoverageProof {
    param([Parameter(Mandatory = $true)]$Summary)

    [object[]]$events = @()
    if ($null -ne $Summary.PSObject.Properties['AllEvents']) {
        $events = @($Summary.AllEvents)
    }
    if ($events.Count -eq 0) {
        $events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    $events = @($events | Sort-Object Sequence)
    [object[]]$publicationEvents = @($events | Where-Object { Test-FileTransferPublicationAuthorityFieldEvent -Event $_ })
    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    $missingFieldCount = 0

    foreach ($event in @($publicationEvents)) {
        foreach ($fieldName in @(Get-FileTransferPublicationAuthorityRequiredFields -Event $event)) {
            if (Test-FileTransferPublicationAuthorityFieldValue -Event $event -FieldName $fieldName) {
                continue
            }

            $missingFieldCount++
            $findings.Add(("publication authority field missing or invalid field={0}: {1}" -f $fieldName, (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            $evidence.Add($event) | Out-Null
        }
    }

    $verdict = if ($publicationEvents.Count -eq 0) { 'none' } elseif ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    return [pscustomobject]@{
        Verdict = $verdict
        FailureClass = if ($verdict -eq 'fail') { 'publication_authority_missing' } else { 'none' }
        CheckedEventCount = [int]$publicationEvents.Count
        MissingFieldCount = [int]$missingFieldCount
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
}

function Test-FileTransferFallbackLocalDeliveryWithoutPeerVisibleProof {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -ne 'filetransfer_control_plane_delivery_result' -and
        $Event.EventName -ne 'filetransfer_control_plane_delivery_observed') {
        return $false
    }

    $acceptedAny = Get-FileTransferEventField -Event $Event -Name 'accepted_any' -Default '0'
    $peerVisibleAny = Get-FileTransferEventField -Event $Event -Name 'peer_visible_any' -Default '0'
    $controlQueue = Get-FileTransferEventField -Event $Event -Name 'control_queue' -Default '0'
    $localOnlyRejected = Get-FileTransferEventField -Event $Event -Name 'local_only_rejected' -Default '0'
    if ($peerVisibleAny -eq '1') {
        return $false
    }

    if ($acceptedAny -ne '1' -and $controlQueue -ne '1' -and $localOnlyRejected -ne '1') {
        return $false
    }

    $route = Get-FileTransferEventField -Event $Event -Name 'route' -Default ''
    $kind = (Get-FileTransferEventField -Event $Event -Name 'kind' -Default '').ToLowerInvariant()
    $reason = (Get-FileTransferEventField -Event $Event -Name 'reason' -Default '').ToLowerInvariant()
    return ($route -eq 'post_tuna_fallback_v6' -or
        $kind -match 'fallback|receiver_state|frontier_request' -or
        $reason -match 'fallback|checkpoint|receiver_state|frontier')
}

function Test-FileTransferFallbackPeerVisibleCompletion {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -ne 'filetransfer_control_plane_delivery_result' -and
        $Event.EventName -ne 'filetransfer_control_plane_delivery_observed') {
        return $false
    }

    $peerVisibleAny = Get-FileTransferEventField -Event $Event -Name 'peer_visible_any' -Default '0'
    if ($peerVisibleAny -ne '1') {
        return $false
    }

    $route = Get-FileTransferEventField -Event $Event -Name 'route' -Default ''
    $kind = (Get-FileTransferEventField -Event $Event -Name 'kind' -Default '').ToLowerInvariant()
    $reason = (Get-FileTransferEventField -Event $Event -Name 'reason' -Default '').ToLowerInvariant()
    return ($route -eq 'post_tuna_fallback_v6' -or
        $kind -match 'fallback|receiver_state|frontier_request' -or
        $reason -match 'fallback|checkpoint|receiver_state|frontier')
}

function Get-FileTransferStaleRejectedActionEffectProof {
    param([Parameter(Mandatory = $true)]$Summary)

    [object[]]$events = @()
    if ($null -ne $Summary.PSObject.Properties['AllEvents']) {
        $events = @($Summary.AllEvents)
    }
    if ($events.Count -eq 0) {
        $events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    $events = @($events | Sort-Object Sequence)
    $rejections = @($events | Where-Object {
            $_.EventName -eq 'filetransfer_runtime_owner_stale_update_rejected' -or
            $_.EventName -eq 'filetransfer_coordinator_leg_mutation_rejected' -or
            $_.EventName -eq 'filetransfer_runtime_unlock_callback_rejected' -or
            $_.EventName -eq 'filetransfer_runtime_unlock_precommit_probe_pending_rejected' -or
            $_.EventName -eq 'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery' -or
            $_.EventName -eq 'runtime_unlock_retry_dispatch_authority_rejected' -or
            $_.EventName -eq 'filetransfer_recovery_authority_rejected' -or
            $_.EventName -eq 'filetransfer_route_publication_rejected' -or
            $_.EventName -eq 'filetransfer_route_hint_ignored_missing_publication_authority' -or
            $_.EventName -eq 'filetransfer_route_hint_ignored_stale_publication_authority' -or
            $_.EventName -eq 'filetransfer_route_hint_ignored_invalid_publication_authority'
        })
    $families = @('liveness', 'repair', 'progress', 'checkpoint', 'bridge_recovery', 'control_plane', 'route_publication')
    $familyCounts = @{}
    foreach ($family in @($families)) {
        $familyCounts[$family] = 0
    }
    $fallbackSubfamilies = @('fallback_send', 'fallback_checkpoint', 'fallback_reissue', 'fallback_tail_yield', 'fallback_pressure')
    $fallbackSubfamilyCounts = @{}
    foreach ($subfamily in @($fallbackSubfamilies)) {
        $fallbackSubfamilyCounts[$subfamily] = 0
    }

    $unknownActionCount = 0
    $forbiddenEffectCount = 0
    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]

    foreach ($rejection in @($rejections | Sort-Object Sequence)) {
        $family = Get-FileTransferStaleRejectedProofActionFamily -Event $rejection
        $fallbackSubfamily = Get-FileTransferStaleRejectedProofFallbackSubfamily -Event $rejection
        if ($family -eq 'unknown') {
            $unknownActionCount++
            $actionKind = Get-FileTransferEventField -Event $rejection -Name 'action_kind' -Default '(none)'
            $attemptedAction = Get-FileTransferEventField -Event $rejection -Name 'attempted_action' -Default '(none)'
            $findings.Add(("unknown stale rejected authority action action_kind={0} attempted_action={1}: {2}" -f $actionKind, $attemptedAction, (Format-FileTransferEvidenceLine -Event $rejection))) | Out-Null
            $evidence.Add($rejection) | Out-Null
            continue
        }

        foreach ($effect in @($events | Where-Object { $_.Sequence -gt $rejection.Sequence })) {
            if (-not (Test-FileTransferStaleRejectedProofForbiddenEffect -Family $family -Event $effect -FallbackSubfamily $fallbackSubfamily)) {
                continue
            }

            if (-not (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $rejection -Effect $effect)) {
                continue
            }

            $missingPublicationAuthority = Test-FileTransferPublicationMissingAuthorityRejection -Event $rejection
            if ($missingPublicationAuthority) {
                if (Test-FileTransferPublicationAuthorityAcceptanceEvent -Event $effect) {
                    continue
                }

                if (Test-FileTransferPublicationEffectHasPriorAuthorizedAcceptance -Rejection $rejection -Effect $effect -Events $events) {
                    continue
                }
            }
            elseif (-not (Test-FileTransferStaleRejectedProofAuthorityMatches -Rejection $rejection -Effect $effect)) {
                continue
            }

            $forbiddenEffectCount++
            $familyCounts[$family] = [int]$familyCounts[$family] + 1
            if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $fallbackSubfamily) {
                $fallbackSubfamilyCounts[$fallbackSubfamily] = [int]$fallbackSubfamilyCounts[$fallbackSubfamily] + 1
            }
            $displayFamily = if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $fallbackSubfamily) { $fallbackSubfamily } else { $family }
            $findings.Add(("stale rejected {0} action was followed by matching effect: rejection={1}; effect={2}" -f $displayFamily, (Format-FileTransferEvidenceLine -Event $rejection), (Format-FileTransferEvidenceLine -Event $effect))) | Out-Null
            $evidence.Add($rejection) | Out-Null
            $evidence.Add($effect) | Out-Null
        }
    }

    [object[]]$localDeliveries = @($events | Where-Object { Test-FileTransferFallbackLocalDeliveryWithoutPeerVisibleProof -Event $_ } | Sort-Object Sequence)
    foreach ($localDelivery in @($localDeliveries)) {
        [object[]]$laterAdmissionGrants = @($events | Where-Object {
                $_.Sequence -gt $localDelivery.Sequence -and
                $_.EventName -eq 'filetransfer_runtime_unlock_admission_granted' -and
                (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $localDelivery -Effect $_)
            } | Sort-Object Sequence)

        foreach ($admissionGrant in @($laterAdmissionGrants)) {
            [object[]]$peerVisibleCompletions = @($events | Where-Object {
                    $_.Sequence -gt $localDelivery.Sequence -and
                    $_.Sequence -lt $admissionGrant.Sequence -and
                    (Test-FileTransferFallbackPeerVisibleCompletion -Event $_) -and
                    (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $localDelivery -Effect $_)
                })
            if ($peerVisibleCompletions.Count -gt 0) {
                continue
            }

            $forbiddenEffectCount++
            $familyCounts['control_plane'] = [int]$familyCounts['control_plane'] + 1
            $fallbackSubfamilyCounts['fallback_send'] = [int]$fallbackSubfamilyCounts['fallback_send'] + 1
            $findings.Add(("local fallback control-plane acceptance was followed by runtime unlock admission without matching peer-visible completion: delivery={0}; admission={1}" -f (Format-FileTransferEvidenceLine -Event $localDelivery), (Format-FileTransferEvidenceLine -Event $admissionGrant))) | Out-Null
            $evidence.Add($localDelivery) | Out-Null
            $evidence.Add($admissionGrant) | Out-Null
        }
    }

    $verdict = if ($rejections.Count -eq 0 -and $localDeliveries.Count -eq 0) { 'none' } elseif ($findings.Count -eq 0) { 'pass' } else { 'fail' }

    return [pscustomobject]@{
        Verdict = $verdict
        FailureClass = if ($verdict -eq 'fail') { 'stale_rejected_action_effect' } else { 'none' }
        RejectedActionCount = [int]$rejections.Count
        ForbiddenEffectCount = [int]$forbiddenEffectCount
        UnknownActionCount = [int]$unknownActionCount
        LivenessForbiddenEffectCount = [int]$familyCounts['liveness']
        RepairForbiddenEffectCount = [int]$familyCounts['repair']
        ProgressForbiddenEffectCount = [int]$familyCounts['progress']
        CheckpointForbiddenEffectCount = [int]$familyCounts['checkpoint']
        BridgeRecoveryForbiddenEffectCount = [int]$familyCounts['bridge_recovery']
        ControlPlaneForbiddenEffectCount = [int]$familyCounts['control_plane']
        RoutePublicationForbiddenEffectCount = [int]$familyCounts['route_publication']
        FallbackSendForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_send']
        FallbackCheckpointForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_checkpoint']
        FallbackReissueForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_reissue']
        FallbackTailYieldForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_tail_yield']
        FallbackPressureForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_pressure']
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
}

function Test-FileTransferRuntimeUnlockFallbackUnavailableDenialScopeMatches {
    param(
        [Parameter(Mandatory = $true)]$Denial,
        [Parameter(Mandatory = $true)]$FallbackUnavailable
    )

    $denialSessionId = Get-FileTransferEventField -Event $Denial -Name 'session_id' -Default ''
    $fallbackSessionId = Get-FileTransferEventField -Event $FallbackUnavailable -Name 'session_id' -Default ''
    if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $denialSessionId) -or
        -not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $fallbackSessionId) -or
        -not [string]::Equals($denialSessionId, $fallbackSessionId, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $denialTransferId = Get-FileTransferStaleRejectedProofTransferId -Event $Denial
    $fallbackTransferId = Get-FileTransferStaleRejectedProofTransferId -Event $FallbackUnavailable
    if ((Test-FileTransferStaleRejectedProofMeaningfulValue -Value $denialTransferId) -and
        (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $fallbackTransferId) -and
        -not [string]::Equals($denialTransferId, $fallbackTransferId, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $true
}

function Test-FileTransferRuntimeUnlockCoordinatorDenialEvent {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -in @(
            'filetransfer_runtime_unlock_admission_denied',
            'filetransfer_runtime_unlock_callback_rejected',
            'filetransfer_runtime_unlock_precommit_probe_pending_rejected',
            'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery',
            'runtime_unlock_retry_dispatch_authority_rejected',
            'filetransfer_runtime_unlock_route_commit_rejected')) {
        return $true
    }

    if ($Event.EventName -eq 'filetransfer_runtime_owner_stale_update_rejected') {
        $text = @(
            (Get-FileTransferEventField -Event $Event -Name 'attempted_action' -Default ''),
            (Get-FileTransferEventField -Event $Event -Name 'source' -Default ''),
            (Get-FileTransferEventField -Event $Event -Name 'reason' -Default '')
        ) -join ' '
        return $text.ToLowerInvariant() -match 'runtime_unlock|precommit_probe|route_commit'
    }

    return $false
}

function Test-FileTransferRuntimeUnlockResumeSettlementMatches {
    param(
        [Parameter(Mandatory = $true)]$Blocked,
        [Parameter(Mandatory = $true)]$Settlement
    )

    if ([int64]$Settlement.Sequence -le [int64]$Blocked.Sequence) {
        return $false
    }

    if (-not (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $Blocked -Effect $Settlement)) {
        return $false
    }

    foreach ($fieldName in @(
            'admission_generation',
            'transaction_generation',
            'offer_generation',
            'tuna_path_lease_generation',
            'pending_probe_generation')) {
        $blockedValue = Get-FileTransferEventField -Event $Blocked -Name $fieldName -Default ''
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $blockedValue)) {
            continue
        }

        $settlementValue = Get-FileTransferEventField -Event $Settlement -Name $fieldName -Default ''
        if (-not (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $settlementValue)) {
            continue
        }

        if (-not [string]::Equals($blockedValue, $settlementValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferRuntimeUnlockFallbackUnavailableHasCoordinatorDenial {
    param(
        [Parameter(Mandatory = $true)]$FallbackUnavailable,
        [Parameter(Mandatory = $true)][object[]]$Events
    )

    foreach ($event in @($Events | Where-Object { $_.Sequence -lt $FallbackUnavailable.Sequence } | Sort-Object Sequence -Descending)) {
        if (-not (Test-FileTransferRuntimeUnlockCoordinatorDenialEvent -Event $event)) {
            continue
        }

        if (Test-FileTransferRuntimeUnlockFallbackUnavailableDenialScopeMatches -Denial $event -FallbackUnavailable $FallbackUnavailable) {
            return $true
        }
    }

    return $false
}

function Get-FileTransferFallbackRuntimeUnlockSeparationProof {
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

    $fallbackAuthorityProof = Get-FileTransferFallbackLegAuthorityProof -TransferEvents $Summary.TransferEvents
    $waitingEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_runtime_unlock_waiting_for_fallback_survival' })
    $activationWindowGrantedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_runtime_unlock_activation_window_granted' })
    $fallbackResumedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_runtime_unlock_probe_failed_fallback_resumed' })
    $preCommitProbeStartedEvents = @($events | Where-Object { $_.EventName -eq 'runtime_unlock_precommit_probe_started' })
    $preCommitProbeFailedEvents = @($events | Where-Object { $_.EventName -eq 'runtime_unlock_precommit_probe_failed' })
    $regularNknProbeRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_ignored' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'not_tuna_transport'
    })
    $fallbackUnavailableMarkEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_marked_fallback_unavailable' -or
        (
            $_.EventName -ne 'runtime_unlock_fallback_unavailable_suppressed' -and
            (
                (Get-FileTransferEventField -Event $_ -Name 'trigger' -Default '') -eq 'post_tuna_fallback_offer_send_prepare' -or
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'post_tuna_fallback_offer_send_prepare'
            )
        )
    })
    $fallbackUnavailableSuppressedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_fallback_unavailable_suppressed'
    })
    $fallbackUnavailableEvents = @($fallbackUnavailableMarkEvents + $fallbackUnavailableSuppressedEvents)
    $fallbackUnavailableWithoutCoordinatorDenialEvents = @($fallbackUnavailableEvents | Where-Object {
        -not (Test-FileTransferRuntimeUnlockFallbackUnavailableHasCoordinatorDenial -FallbackUnavailable $_ -Events $events)
    })

    $postTunaRouteSelected = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_route_selected' -and
        (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6'
    })

    $probeStartedWithoutActivationWindowCount = 0
    $inFallback = $false
    $activationWindowOpen = $false
    foreach ($event in @($events | Sort-Object Sequence)) {
        if ($event.EventName -eq 'filetransfer_route_selected') {
            $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
            if ($route -eq 'post_tuna_fallback_v6') {
                $inFallback = $true
                $activationWindowOpen = $false
            }
            elseif ($route -eq 'file_tuna_v4' -or $route -eq 'regular_nkn_v4_fast') {
                $inFallback = $false
                $activationWindowOpen = $false
            }
        }
        elseif ($event.EventName -eq 'filetransfer_runtime_unlock_activation_window_granted' -and $inFallback) {
            $activationWindowOpen = $true
        }
        elseif ($event.EventName -eq 'runtime_unlock_precommit_probe_started' -and $inFallback -and -not $activationWindowOpen) {
            $probeStartedWithoutActivationWindowCount++
        }
    }

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    if ($fallbackUnavailableWithoutCoordinatorDenialEvents.Count -gt 0) {
        $findings.Add(("runtime unlock fallback-unavailable outcome without coordinator denial count={0}" -f $fallbackUnavailableWithoutCoordinatorDenialEvents.Count)) | Out-Null
        foreach ($event in @($fallbackUnavailableWithoutCoordinatorDenialEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($probeStartedWithoutActivationWindowCount -gt 0) {
        $findings.Add(("runtime unlock precommit probe started while fallback owned recovery without activation window count={0}" -f $probeStartedWithoutActivationWindowCount)) | Out-Null
        foreach ($event in @($preCommitProbeStartedEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($postTunaRouteSelected.Count -gt 0 -and
        ($preCommitProbeFailedEvents.Count -gt 0 -or $regularNknProbeRejectedEvents.Count -gt 0) -and
        $fallbackAuthorityProof.Verdict -eq 'fail') {
        $findings.Add('runtime unlock probe failed while fallback leg authority proof was missing or failed') | Out-Null
        foreach ($event in @($postTunaRouteSelected + $preCommitProbeFailedEvents + $regularNknProbeRejectedEvents | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if ($postTunaRouteSelected.Count -eq 0 -and
        $waitingEvents.Count -eq 0 -and
        $activationWindowGrantedEvents.Count -eq 0 -and
        $fallbackResumedEvents.Count -eq 0 -and
        $preCommitProbeStartedEvents.Count -eq 0 -and
        $preCommitProbeFailedEvents.Count -eq 0 -and
        $fallbackUnavailableEvents.Count -eq 0) {
        $verdict = 'none'
    }

    [pscustomobject]@{
        Verdict = $verdict
        FailureClass = if ($verdict -eq 'fail' -and $fallbackUnavailableWithoutCoordinatorDenialEvents.Count -gt 0) {
            'stale_rejected_action_effect'
        }
        elseif ($verdict -eq 'fail') {
            'runtime_fallback_separation_violation'
        }
        else {
            'none'
        }
        WaitingForFallbackSurvivalCount = $waitingEvents.Count
        ActivationWindowGrantedCount = $activationWindowGrantedEvents.Count
        ProbeFailedFallbackResumedCount = $fallbackResumedEvents.Count
        ProbeStartedWithoutActivationWindowCount = $probeStartedWithoutActivationWindowCount
        RegularNknProbeRejectedCount = $regularNknProbeRejectedEvents.Count
        FallbackUnavailableMarkCount = $fallbackUnavailableMarkEvents.Count
        FallbackUnavailableSuppressedCount = $fallbackUnavailableSuppressedEvents.Count
        FallbackUnavailableWithoutCoordinatorDenialCount = $fallbackUnavailableWithoutCoordinatorDenialEvents.Count
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray())
    }
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
    $fallbackAuthorityProof = Get-FileTransferFallbackLegAuthorityProof -TransferEvents $Summary.TransferEvents
    $fallbackTailProof = Get-FileTransferFallbackTailReconciliationProof -Summary $Summary
    $bridgeLivenessProof = Get-FileTransferBridgeLivenessIntegrationProof -Summary $Summary
    $controlPlaneProof = Get-FileTransferControlPlaneIsolationProof -Summary $Summary
    $staleRejectedActionEffectProof = Get-FileTransferStaleRejectedActionEffectProof -Summary $Summary
    $fallbackRuntimeUnlockSeparationProof = Get-FileTransferFallbackRuntimeUnlockSeparationProof -Summary $Summary
    $publicationAuthorityFieldCoverageProof = Get-FileTransferPublicationAuthorityFieldCoverageProof -Summary $Summary
    $fileTunaV4DataPathProof = Get-FileTransferFileTunaV4DataPathProof -Summary $Summary
    [object[]]$fallbackAuthoritySequence = @($fallbackAuthorityProof.Sequence)

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
    $lines.Add(("fallback_leg_authority_proof_verdict={0}" -f $fallbackAuthorityProof.Verdict)) | Out-Null
    $lines.Add(("clean_fallback_leg_proof_verdict={0}" -f $fallbackAuthorityProof.Verdict)) | Out-Null
    $lines.Add(("fallback_leg_authority_generation_sequence={0}" -f ($(if ($fallbackAuthoritySequence.Length -gt 0) { $fallbackAuthoritySequence -join ',' } else { '(none)' })))) | Out-Null
    $lines.Add(("fallback_leg_authority_started_count={0}" -f $fallbackAuthorityProof.StartedCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_checkpoint_accepted_count={0}" -f $fallbackAuthorityProof.CheckpointAcceptedCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_bridge_recovery_requested_count={0}" -f $fallbackAuthorityProof.BridgeRecoveryRequestedCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_bridge_recovery_escalated_count={0}" -f $fallbackAuthorityProof.BridgeRecoveryEscalatedCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_completed_count={0}" -f $fallbackAuthorityProof.CompletedCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_send_blocked_count={0}" -f $fallbackAuthorityProof.SendBlockedCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_continuous_receiver_state_proof_count={0}" -f $fallbackAuthorityProof.ContinuousReceiverStateProofCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_stale_proof_ignored_count={0}" -f $fallbackAuthorityProof.StaleProofIgnoredCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_metadata_missing_count={0}" -f $fallbackAuthorityProof.MetadataMissingCount)) | Out-Null
    $lines.Add(("fallback_tail_reconciliation_verdict={0}" -f $fallbackTailProof.Verdict)) | Out-Null
    $lines.Add(("fallback_tail_quiescence_verdict={0}" -f $fallbackTailProof.QuiescenceVerdict)) | Out-Null
    $lines.Add(("fallback_tail_reconciliation_requested_count={0}" -f $fallbackTailProof.RequestedCount)) | Out-Null
    $lines.Add(("fallback_tail_reconciliation_accepted_count={0}" -f $fallbackTailProof.AcceptedCount)) | Out-Null
    $lines.Add(("fallback_tail_progress_forced_count={0}" -f $fallbackTailProof.ProgressForcedCount)) | Out-Null
    $lines.Add(("fallback_tail_progress_coalesced_count={0}" -f $fallbackTailProof.ProgressCoalescedCount)) | Out-Null
    $lines.Add(("fallback_tail_terminal_proof_consumed_count={0}" -f $fallbackTailProof.TerminalProofConsumedCount)) | Out-Null
    $lines.Add(("fallback_tail_quiescent_yield_granted_count={0}" -f $fallbackTailProof.QuiescentYieldGrantedCount)) | Out-Null
    $lines.Add(("fallback_tail_outbound_yield_granted_count={0}" -f $fallbackTailProof.OutboundQuiescentYieldGrantedCount)) | Out-Null
    $lines.Add(("fallback_tail_inbound_yield_granted_count={0}" -f $fallbackTailProof.InboundQuiescentYieldGrantedCount)) | Out-Null
    $lines.Add(("fallback_tail_yield_proof_sent_count={0}" -f $fallbackTailProof.YieldProofSentCount)) | Out-Null
    $lines.Add(("fallback_tail_yield_proof_received_count={0}" -f $fallbackTailProof.YieldProofReceivedCount)) | Out-Null
    $lines.Add(("fallback_tail_paired_yield_granted_count={0}" -f $fallbackTailProof.PairedYieldGrantedCount)) | Out-Null
    $lines.Add(("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailProof.OwnerGenerationMismatchCount)) | Out-Null
    $lines.Add(("fallback_tail_local_yield_blocked_count={0}" -f $fallbackTailProof.LocalYieldBlockedCount)) | Out-Null
    $lines.Add(("fallback_tail_stale_yield_proof_ignored_count={0}" -f $fallbackTailProof.StaleYieldProofIgnoredCount)) | Out-Null
    $lines.Add(("fallback_tail_paired_yield_verdict={0}" -f $fallbackTailProof.PairedYieldVerdict)) | Out-Null
    $lines.Add(("fallback_frontier_repair_authority_granted_count={0}" -f $fallbackTailProof.FrontierRepairAuthorityGrantedCount)) | Out-Null
    $lines.Add(("fallback_frontier_repair_authority_rejected_count={0}" -f $fallbackTailProof.FrontierRepairAuthorityRejectedCount)) | Out-Null
    $lines.Add(("fallback_frontier_repair_dispatched_count={0}" -f $fallbackTailProof.FrontierRepairDispatchedCount)) | Out-Null
    $lines.Add(("fallback_frontier_repair_stalled_count={0}" -f $fallbackTailProof.FrontierRepairStalledCount)) | Out-Null
    $lines.Add(("fallback_frontier_repair_progress_observed_count={0}" -f $fallbackTailProof.FrontierRepairProgressObservedCount)) | Out-Null
    $lines.Add(("fallback_frontier_repair_verdict={0}" -f $fallbackTailProof.FrontierRepairVerdict)) | Out-Null
    $lines.Add(("runtime_unlock_probe_rejected_by_fallback_owner_count={0}" -f $fallbackTailProof.RuntimeUnlockProbeRejectedByFallbackOwnerCount)) | Out-Null
    $lines.Add(("runtime_unlock_probe_rejected_by_inbound_fallback_owner_count={0}" -f $fallbackTailProof.RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount)) | Out-Null
    $lines.Add(("fallback_tail_quiescence_timeout_count={0}" -f $fallbackTailProof.QuiescenceTimeoutCount)) | Out-Null
    $lines.Add(("fallback_tail_zero_credit_breaker_count={0}" -f $fallbackTailProof.ZeroCreditBreakerCount)) | Out-Null
    $lines.Add(("fallback_tail_stale_frontier_retired_count={0}" -f $fallbackTailProof.StaleFrontierRetiredCount)) | Out-Null
    $lines.Add(("fallback_tail_state_refresh_send_slot_retired_count={0}" -f $fallbackTailProof.SendSlotRetiredCount)) | Out-Null
    $lines.Add(("fallback_tail_receive_recovery_requested_count={0}" -f $fallbackTailProof.ReceiveRecoveryRequestedCount)) | Out-Null
    $lines.Add(("fallback_tail_normal_zero_credit_state_refresh_count={0}" -f $fallbackTailProof.NormalZeroCreditTailStateRefreshCount)) | Out-Null
    $lines.Add(("fallback_tail_reconciliation_metadata_missing_count={0}" -f $fallbackTailProof.MetadataMissingCount)) | Out-Null
    $lines.Add(("bridge_liveness_integration_verdict={0}" -f $bridgeLivenessProof.Verdict)) | Out-Null
    $lines.Add(("session_liveness_deferred_for_current_recovery_count={0}" -f $bridgeLivenessProof.CurrentRecoveryDeferralCount)) | Out-Null
    $lines.Add(("session_liveness_deferred_for_bridge_recovery_count={0}" -f $bridgeLivenessProof.BridgeRecoveryDeferralCount)) | Out-Null
    $lines.Add(("session_liveness_timeout_during_valid_recovery_count={0}" -f $bridgeLivenessProof.TimeoutDuringValidRecoveryCount)) | Out-Null
    $lines.Add(("bridge_recovery_receive_resumed_count={0}" -f $bridgeLivenessProof.ReceiveResumedCount)) | Out-Null
    $lines.Add(("bridge_recovery_exhausted_without_proof_count={0}" -f $bridgeLivenessProof.RecoveryExhaustedWithoutProofCount)) | Out-Null
    $lines.Add(("fallback_leg_authority_liveness_deferral_count={0}" -f $bridgeLivenessProof.FallbackLegAuthorityLivenessDeferralCount)) | Out-Null
    $lines.Add(("bridge_liveness_stale_deferral_count={0}" -f $bridgeLivenessProof.StaleDeferralCount)) | Out-Null
    $lines.Add(("bridge_exhausted_sibling_deferral_suppressed_count={0}" -f $bridgeLivenessProof.SiblingDeferralSuppressedCount)) | Out-Null
    $lines.Add(("bridge_exhausted_sibling_deferral_expired_count={0}" -f $bridgeLivenessProof.SiblingDeferralExpiredCount)) | Out-Null
    $lines.Add(("bridge_exhausted_terminal_during_valid_deferral_count={0}" -f $bridgeLivenessProof.TerminalDuringValidSiblingDeferralCount)) | Out-Null
    $lines.Add(("control_plane_isolation_verdict={0}" -f $controlPlaneProof.Verdict)) | Out-Null
    $lines.Add(("control_plane_delivery_result_count={0}" -f $controlPlaneProof.DeliveryResultCount)) | Out-Null
    $lines.Add(("control_plane_peer_visible_count={0}" -f $controlPlaneProof.PeerVisibleCount)) | Out-Null
    $lines.Add(("control_plane_local_only_rejected_count={0}" -f $controlPlaneProof.LocalOnlyRejectedCount)) | Out-Null
    $lines.Add(("control_plane_checkpoint_exchange_count={0}" -f $controlPlaneProof.CheckpointExchangeCount)) | Out-Null
    $lines.Add(("control_plane_continuous_receiver_state_proof_count={0}" -f $controlPlaneProof.ContinuousReceiverStateProofCount)) | Out-Null
    $lines.Add(("control_plane_delivery_failure_count={0}" -f $controlPlaneProof.DeliveryFailureCount)) | Out-Null
    $lines.Add(("control_plane_delivery_failure_recovered_count={0}" -f $controlPlaneProof.RecoveredDeliveryFailureCount)) | Out-Null
    $lines.Add(("control_plane_unavailable_count={0}" -f $controlPlaneProof.UnavailableCount)) | Out-Null
    $lines.Add(("control_plane_metadata_missing_count={0}" -f $controlPlaneProof.MetadataMissingCount)) | Out-Null
    $lines.Add(("control_plane_liveness_timeout_during_valid_exchange_count={0}" -f $controlPlaneProof.LivenessTimeoutDuringValidExchangeCount)) | Out-Null
    $lines.Add(("control_plane_stale_send_retired_count={0}" -f $controlPlaneProof.RetiredStaleSendCount)) | Out-Null
    $lines.Add(("fallback_checkpoint_exchange_retired_count={0}" -f $controlPlaneProof.CheckpointExchangeRetiredCount)) | Out-Null
    $lines.Add(("fallback_checkpoint_owner_violation_count={0}" -f $controlPlaneProof.CheckpointOwnerViolationCount)) | Out-Null
    $lines.Add(("fallback_stale_deferred_replay_discarded_count={0}" -f $controlPlaneProof.StaleDeferredReplayDiscardedCount)) | Out-Null
    $lines.Add(("fallback_checkpoint_not_pending_reissue_suppressed_count={0}" -f $controlPlaneProof.CheckpointNotPendingReissueSuppressedCount)) | Out-Null
    $lines.Add(("fallback_stale_proof_ignored_count={0}" -f $controlPlaneProof.StaleProofIgnoredCount)) | Out-Null
    $lines.Add(("fallback_stale_proof_replay_suppressed_count={0}" -f $controlPlaneProof.StaleProofReplaySuppressedCount)) | Out-Null
    $lines.Add(("fallback_control_plane_stale_replay_treadmill_count={0}" -f $controlPlaneProof.StaleReplayTreadmillCount)) | Out-Null
    $lines.Add(("fallback_stale_generation_treadmill_score={0}" -f $controlPlaneProof.StaleGenerationTreadmillScore)) | Out-Null
    $lines.Add(("fallback_stale_generation_treadmill_score_limit={0}" -f $controlPlaneProof.StaleGenerationTreadmillScoreLimit)) | Out-Null
    $lines.Add(("fallback_checkpoint_ids_by_leg_generation={0}" -f $controlPlaneProof.CheckpointIdsByLegGeneration)) | Out-Null
    $lines.Add(("fallback_checkpoint_id_count_max_per_leg={0}" -f $controlPlaneProof.CheckpointIdCountMaxPerLeg)) | Out-Null
    $lines.Add(("fallback_control_plane_non_current_leg_generation_send_count={0}" -f $controlPlaneProof.ControlPlaneNonCurrentLegGenerationSendCount)) | Out-Null
    $lines.Add(("stale_rejected_action_effect_isolation_verdict={0}" -f $staleRejectedActionEffectProof.Verdict)) | Out-Null
    $lines.Add(("stale_rejected_action_count={0}" -f $staleRejectedActionEffectProof.RejectedActionCount)) | Out-Null
    $lines.Add(("stale_rejected_action_forbidden_effect_count={0}" -f $staleRejectedActionEffectProof.ForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_liveness_effect_count={0}" -f $staleRejectedActionEffectProof.LivenessForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_repair_effect_count={0}" -f $staleRejectedActionEffectProof.RepairForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_progress_effect_count={0}" -f $staleRejectedActionEffectProof.ProgressForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_checkpoint_effect_count={0}" -f $staleRejectedActionEffectProof.CheckpointForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_bridge_recovery_effect_count={0}" -f $staleRejectedActionEffectProof.BridgeRecoveryForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_control_plane_effect_count={0}" -f $staleRejectedActionEffectProof.ControlPlaneForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_route_publication_effect_count={0}" -f $staleRejectedActionEffectProof.RoutePublicationForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_fallback_send_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackSendForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_fallback_checkpoint_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackCheckpointForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_fallback_reissue_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackReissueForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_fallback_tail_yield_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackTailYieldForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_fallback_pressure_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackPressureForbiddenEffectCount)) | Out-Null
    $lines.Add(("stale_rejected_unknown_action_count={0}" -f $staleRejectedActionEffectProof.UnknownActionCount)) | Out-Null
    $lines.Add(("publication_authority_field_coverage_verdict={0}" -f $publicationAuthorityFieldCoverageProof.Verdict)) | Out-Null
    $lines.Add(("publication_authority_checked_event_count={0}" -f $publicationAuthorityFieldCoverageProof.CheckedEventCount)) | Out-Null
    $lines.Add(("publication_authority_missing_field_count={0}" -f $publicationAuthorityFieldCoverageProof.MissingFieldCount)) | Out-Null
    $lines.Add(("file_tuna_v4_data_path_proof_verdict={0}" -f $fileTunaV4DataPathProof.Verdict)) | Out-Null
    $lines.Add(("file_tuna_v4_intended_send_count={0}" -f $fileTunaV4DataPathProof.IntendedSendCount)) | Out-Null
    $lines.Add(("file_tuna_v4_tuna_send_result_count={0}" -f $fileTunaV4DataPathProof.ActualTunaSendCount)) | Out-Null
    $lines.Add(("file_tuna_v4_regular_nkn_send_result_count={0}" -f $fileTunaV4DataPathProof.ActualRegularNknSendCount)) | Out-Null
    $lines.Add(("file_tuna_v4_data_path_mismatch_count={0}" -f $fileTunaV4DataPathProof.MismatchCount)) | Out-Null
    $lines.Add(("file_tuna_v4_data_path_missing_actual_proof_count={0}" -f $fileTunaV4DataPathProof.MissingActualProofCount)) | Out-Null
    $lines.Add(("file_tuna_v4_peer_visible_tuna_payload_bytes={0}" -f $fileTunaV4DataPathProof.PeerVisiblePayloadBytes)) | Out-Null
    $lines.Add(("fallback_runtime_unlock_separation_verdict={0}" -f $fallbackRuntimeUnlockSeparationProof.Verdict)) | Out-Null
    $lines.Add(("runtime_fallback_separation_verdict={0}" -f $fallbackRuntimeUnlockSeparationProof.Verdict)) | Out-Null
    $lines.Add(("runtime_fallback_arbitration_verdict={0}" -f $fallbackRuntimeUnlockSeparationProof.Verdict)) | Out-Null
    $lines.Add(("fallback_runtime_unlock_waiting_count={0}" -f $fallbackRuntimeUnlockSeparationProof.WaitingForFallbackSurvivalCount)) | Out-Null
    $lines.Add(("runtime_unlock_activation_window_granted_count={0}" -f $fallbackRuntimeUnlockSeparationProof.ActivationWindowGrantedCount)) | Out-Null
    $lines.Add(("runtime_unlock_probe_failed_fallback_resumed_count={0}" -f $fallbackRuntimeUnlockSeparationProof.ProbeFailedFallbackResumedCount)) | Out-Null
    $lines.Add(("runtime_unlock_probe_started_without_activation_window_count={0}" -f $fallbackRuntimeUnlockSeparationProof.ProbeStartedWithoutActivationWindowCount)) | Out-Null
    $lines.Add(("runtime_unlock_regular_nkn_probe_rejected_count={0}" -f $fallbackRuntimeUnlockSeparationProof.RegularNknProbeRejectedCount)) | Out-Null
    $lines.Add(("runtime_unlock_fallback_unavailable_mark_count={0}" -f $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableMarkCount)) | Out-Null
    $lines.Add(("runtime_unlock_fallback_unavailable_suppressed_count={0}" -f $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableSuppressedCount)) | Out-Null
    $lines.Add(("runtime_unlock_fallback_unavailable_without_denial_count={0}" -f $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount)) | Out-Null

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
    $lines.Add('fallback_leg_authority_findings:') | Out-Null
    if ($fallbackAuthorityProof.Findings.Count -gt 0) {
        $authorityIndex = 0
        foreach ($finding in @($fallbackAuthorityProof.Findings)) {
            $authorityIndex++
            $lines.Add(("authority.{0}={1}" -f $authorityIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('fallback_tail_reconciliation_findings:') | Out-Null
    if ($fallbackTailProof.Findings.Count -gt 0) {
        $tailIndex = 0
        foreach ($finding in @($fallbackTailProof.Findings)) {
            $tailIndex++
            $lines.Add(("tail.{0}={1}" -f $tailIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('fallback_tail_quiescence_findings:') | Out-Null
    if ($fallbackTailProof.QuiescenceFindings.Count -gt 0) {
        $tailQuiescenceIndex = 0
        foreach ($finding in @($fallbackTailProof.QuiescenceFindings)) {
            $tailQuiescenceIndex++
            $lines.Add(("tail_quiescence.{0}={1}" -f $tailQuiescenceIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('bridge_liveness_integration_findings:') | Out-Null
    if ($bridgeLivenessProof.Findings.Count -gt 0) {
        $bridgeLivenessIndex = 0
        foreach ($finding in @($bridgeLivenessProof.Findings)) {
            $bridgeLivenessIndex++
            $lines.Add(("bridge_liveness.{0}={1}" -f $bridgeLivenessIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('control_plane_isolation_findings:') | Out-Null
    if ($controlPlaneProof.Findings.Count -gt 0) {
        $controlPlaneIndex = 0
        foreach ($finding in @($controlPlaneProof.Findings)) {
            $controlPlaneIndex++
            $lines.Add(("control_plane.{0}={1}" -f $controlPlaneIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('stale_rejected_action_effect_findings:') | Out-Null
    if ($staleRejectedActionEffectProof.Findings.Count -gt 0) {
        $staleEffectIndex = 0
        foreach ($finding in @($staleRejectedActionEffectProof.Findings)) {
            $staleEffectIndex++
            $lines.Add(("stale_rejected_action_effect.{0}={1}" -f $staleEffectIndex, $finding)) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    $lines.Add('') | Out-Null
    $lines.Add('file_tuna_v4_data_path_findings:') | Out-Null
    if ($fileTunaV4DataPathProof.Findings.Count -gt 0) {
        $dataPathIndex = 0
        foreach ($finding in @($fileTunaV4DataPathProof.Findings)) {
            $dataPathIndex++
            $lines.Add(("file_tuna_v4_data_path.{0}={1}" -f $dataPathIndex, $finding)) | Out-Null
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
    $fallbackAuthorityProof = Get-FileTransferFallbackLegAuthorityProof -TransferEvents $Summary.TransferEvents
    $fallbackTailProof = Get-FileTransferFallbackTailReconciliationProof -Summary $Summary
    $bridgeLivenessProof = Get-FileTransferBridgeLivenessIntegrationProof -Summary $Summary
    $controlPlaneProof = Get-FileTransferControlPlaneIsolationProof -Summary $Summary

    return @(
        ("verdict={0}" -f $GateResult.Verdict),
        ("gate_status={0}" -f $GateResult.GateStatus),
        ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
        ("recovery_failure_class={0}" -f $recoveryClassification.Class),
        ("runtime_unlock_offer_not_observed_count={0}" -f $recoveryClassification.RuntimeUnlockOfferNotObservedCount),
        ("runtime_unlock_retry_scheduled_count={0}" -f $recoveryClassification.RuntimeUnlockRetryScheduledCount),
        ("runtime_unlock_retry_queued_behind_active_negotiation_count={0}" -f $recoveryClassification.RuntimeUnlockRetryQueuedBehindActiveNegotiationCount),
        ("runtime_unlock_retry_dispatched_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchedCount),
        ("runtime_unlock_retry_authority_granted_count={0}" -f $recoveryClassification.RuntimeUnlockRetryAuthorityGrantedCount),
        ("runtime_unlock_retry_authority_observed_count={0}" -f $recoveryClassification.RuntimeUnlockRetryAuthorityObservedCount),
        ("runtime_unlock_retry_authority_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRetryAuthorityFailedCount),
        ("runtime_unlock_retry_dispatch_proof_verdict={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchProofVerdict),
        ("runtime_unlock_retry_release_count={0}" -f $recoveryClassification.RuntimeUnlockRetryReleaseCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchAuthorityGrantedCount),
        ("runtime_unlock_retry_dispatch_authority_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchAuthorityRejectedCount),
        ("runtime_unlock_retry_dispatch_observed_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchObservedCount),
        ("runtime_unlock_retry_dispatch_missing_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchMissingCount),
        ("runtime_unlock_retry_missing_authority_after_release_count={0}" -f $recoveryClassification.RuntimeUnlockRetryMissingAuthorityAfterReleaseCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_rejected_after_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockRetryRejectedAfterRegularV4RecoveryCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $recoveryClassification.RuntimeUnlockRetryRegularV4RecoverySettlementMissingCount),
        ("runtime_unlock_broken_state_fresh_retry_consumed_count={0}" -f $recoveryClassification.RuntimeUnlockBrokenStateFreshRetryConsumedCount),
        ("runtime_unlock_broken_state_retry_suppressed_count={0}" -f $recoveryClassification.RuntimeUnlockBrokenStateRetrySuppressedCount),
        ("runtime_unlock_broken_state_transaction_failed_count={0}" -f $recoveryClassification.RuntimeUnlockBrokenStateTransactionFailedCount),
        ("runtime_unlock_cutthrough_attempt_count={0}" -f $recoveryClassification.RuntimeUnlockCutThroughAttemptCount),
        ("runtime_unlock_cutthrough_peer_received_count={0}" -f $recoveryClassification.RuntimeUnlockCutThroughPeerReceivedCount),
        ("runtime_unlock_cutthrough_timeout_count={0}" -f $recoveryClassification.RuntimeUnlockCutThroughTimeoutCount),
        ("runtime_unlock_cutthrough_verdict={0}" -f $recoveryClassification.RuntimeUnlockCutThroughVerdict),
        ("runtime_unlock_transaction_started_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionStartedCount),
        ("runtime_unlock_transaction_observed_send_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionObservedSendCount),
        ("runtime_unlock_transaction_peer_proof_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionPeerProofCount),
        ("runtime_unlock_probe_started_count={0}" -f $recoveryClassification.RuntimeUnlockProbeStartedCount),
        ("runtime_unlock_probe_acked_count={0}" -f $recoveryClassification.RuntimeUnlockProbeAckedCount),
        ("runtime_unlock_probe_failed_count={0}" -f $recoveryClassification.RuntimeUnlockProbeFailedCount),
        ("runtime_unlock_precommit_probe_started_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeStartedCount),
        ("runtime_unlock_precommit_probe_sent_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeSentCount),
        ("runtime_unlock_precommit_probe_waiting_for_transport_proof_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeWaitingForTransportProofCount),
        ("runtime_unlock_precommit_probe_pending_held_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbePendingHeldCount),
        ("runtime_unlock_precommit_probe_acked_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckedCount),
        ("runtime_unlock_precommit_probe_ack_missing_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckMissingCount),
        ("runtime_unlock_precommit_probe_ack_authority_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckAuthorityRejectedCount),
        ("runtime_unlock_precommit_probe_failed_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeFailedCount),
        ("runtime_unlock_precommit_probe_protocol_mismatch_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeProtocolMismatchCount),
        ("runtime_unlock_precommit_probe_delivery_result_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeDeliveryResultCount),
        ("runtime_unlock_tuna_probe_sent_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSentCount),
        ("runtime_unlock_tuna_probe_received_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeReceivedCount),
        ("runtime_unlock_tuna_probe_ack_sent_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeAckSentCount),
        ("runtime_unlock_precommit_probe_ack_delivery_result_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckDeliveryResultCount),
        ("runtime_unlock_precommit_probe_ack_receive_result_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveResultCount),
        ("runtime_unlock_precommit_probe_ack_receive_queued_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveQueuedCount),
        ("runtime_unlock_precommit_probe_ack_receive_dispatched_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveDispatchedCount),
        ("runtime_unlock_precommit_probe_ack_receive_dispatch_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveDispatchRejectedCount),
        ("runtime_unlock_tuna_probe_ack_received_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeAckReceivedCount),
        ("runtime_unlock_fallback_quiescent_admission_granted_count={0}" -f $recoveryClassification.RuntimeUnlockFallbackQuiescentAdmissionGrantedCount),
        ("runtime_unlock_fallback_proof_replay_pending_after_paired_yield_count={0}" -f $recoveryClassification.RuntimeUnlockFallbackProofReplayPendingAfterPairedYieldCount),
        ("runtime_unlock_fallback_proof_replay_pending_after_paired_yield_unresolved_count={0}" -f $recoveryClassification.RuntimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedCount),
        ("post_fallback_runtime_unlock_resume_blocked_count={0}" -f $recoveryClassification.PostFallbackRuntimeUnlockResumeBlockedCount),
        ("runtime_unlock_tuna_probe_sidecar_failure_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSidecarFailureCount),
        ("runtime_unlock_tuna_probe_sidecar_fresh_transaction_scheduled_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSidecarFreshTransactionScheduledCount),
        ("runtime_unlock_tuna_probe_sidecar_fresh_transaction_started_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSidecarFreshTransactionStartedCount),
        ("runtime_unlock_tuna_listener_rearm_after_probe_close_failed_count={0}" -f $recoveryClassification.RuntimeUnlockListenerRearmAfterProbeCloseFailedCount),
        ("regular_v4_terminal_before_tuna_activation_commit_count={0}" -f $recoveryClassification.RegularV4TerminalBeforeTunaActivationCommitCount),
        ("runtime_unlock_paired_commit_started_count={0}" -f $recoveryClassification.RuntimeUnlockPairedCommitStartedCount),
        ("runtime_unlock_paired_commit_confirmed_count={0}" -f $recoveryClassification.RuntimeUnlockPairedCommitConfirmedCount),
        ("runtime_unlock_paired_commit_aborted_count={0}" -f $recoveryClassification.RuntimeUnlockPairedCommitAbortedCount),
        ("runtime_unlock_probe_commit_verdict={0}" -f $recoveryClassification.RuntimeUnlockProbeCommitVerdict),
        ("runtime_unlock_make_before_break_verdict={0}" -f $recoveryClassification.RuntimeUnlockMakeBeforeBreakVerdict),
        ("runtime_unlock_transaction_commit_pending_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionCommitPendingCount),
        ("runtime_unlock_transaction_committed_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionCommittedCount),
        ("runtime_unlock_transaction_failed_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionFailedCount),
        ("runtime_unlock_transaction_local_only_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionLocalOnlyRejectedCount),
        ("runtime_unlock_transaction_proof_verdict={0}" -f $recoveryClassification.RuntimeUnlockTransactionProofVerdict),
        ("runtime_unlock_tuna_path_lease_started_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseStartedCount),
        ("runtime_unlock_tuna_path_lease_ready_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseReadyCount),
        ("runtime_unlock_tuna_path_lease_failed_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseFailedCount),
        ("runtime_unlock_tuna_path_lease_retired_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseRetiredCount),
        ("runtime_unlock_tuna_path_lease_stale_listener_ignored_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseStaleListenerIgnoredCount),
        ("runtime_unlock_answer_rejected_tuna_path_lease_unavailable_count={0}" -f $recoveryClassification.RuntimeUnlockAnswerRejectedTunaPathLeaseUnavailableCount),
        ("regular_v4_efficiency_pressure_class={0}" -f $recoveryClassification.RegularV4EfficiencyPressureClass),
        ("regular_v4_frontier_rebind_count={0}" -f $recoveryClassification.RegularV4FrontierRebindCount),
        ("regular_v4_frontier_rebind_rejected_count={0}" -f $recoveryClassification.RegularV4FrontierRebindRejectedCount),
        ("regular_v4_frontier_stall_missing_range_count={0}" -f $recoveryClassification.RegularV4FrontierStallMissingRangeCount),
        ("regular_v4_repair_requested_count={0}" -f $recoveryClassification.RegularV4RepairRequestedCount),
        ("regular_v4_frontier_repair_authority_granted_count={0}" -f $recoveryClassification.RegularV4FrontierRepairAuthorityGrantedCount),
        ("regular_v4_frontier_repair_authority_rejected_count={0}" -f $recoveryClassification.RegularV4FrontierRepairAuthorityRejectedCount),
        ("regular_v4_frontier_repair_dispatched_count={0}" -f $recoveryClassification.RegularV4FrontierRepairDispatchedCount),
        ("regular_v4_frontier_repair_progress_observed_count={0}" -f $recoveryClassification.RegularV4FrontierRepairProgressObservedCount),
        ("regular_v4_frontier_repair_stalled_count={0}" -f $recoveryClassification.RegularV4FrontierRepairStalledCount),
        ("regular_v4_frontier_repair_verdict={0}" -f $recoveryClassification.RegularV4FrontierRepairVerdict),
        ("regular_v4_repair_cache_pressure_count={0}" -f $recoveryClassification.RegularV4RepairCachePressureCount),
        ("listener_ready_unavailable_contradiction_count={0}" -f $recoveryClassification.ListenerReadyUnavailableContradictionCount),
        ("listener_rearm_required_count={0}" -f $recoveryClassification.ListenerRearmRequiredCount),
        ("listener_rearm_completed_count={0}" -f $recoveryClassification.ListenerRearmCompletedCount),
        ("listener_rearm_failed_count={0}" -f $recoveryClassification.ListenerRearmFailedCount),
        ("runtime_unlock_offer_dispatched_after_listener_rearm_count={0}" -f $recoveryClassification.RuntimeUnlockOfferDispatchedAfterListenerRearmCount),
        ("runtime_unlock_dispatch_deferred_for_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockDispatchDeferredForRegularV4RecoveryCount),
        ("runtime_unlock_regular_v4_activation_window_started_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4ActivationWindowStartedCount),
        ("runtime_unlock_regular_v4_activation_window_peer_visible_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4ActivationWindowPeerVisibleCount),
        ("runtime_unlock_regular_v4_activation_window_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4ActivationWindowFailedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_started_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowStartedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_delivery_started_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowDeliveryStartedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_proof_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowProofCount),
        ("runtime_unlock_regular_v4_peer_visible_window_delivery_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowDeliveryFailedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowLateProofIgnoredCount),
        ("runtime_unlock_regular_v4_peer_visible_window_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowFailedCount),
        ("regular_v4_repair_spam_coalesced_count={0}" -f $recoveryClassification.RegularV4RepairSpamCoalescedCount),
        ("post_tuna_fallback_control_plane_pressure_marked_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureMarkedCount),
        ("post_tuna_fallback_control_plane_pressure_cleared_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureClearedCount),
        ("post_tuna_fallback_control_plane_pressure_allowed_recovery_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureAllowedRecoveryCount),
        ("post_tuna_fallback_control_plane_pressure_budget_suppressed_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureBudgetSuppressedCount),
        ("post_tuna_fallback_bulk_active_suppressed_during_pressure_count={0}" -f $recoveryClassification.PostTunaFallbackBulkActiveSuppressedDuringPressureCount),
        ("post_tuna_fallback_legacy_control_lane_starvation_count={0}" -f $recoveryClassification.PostTunaFallbackLegacyControlLaneStarvationCount),
        ("runtime_unlock_regular_v4_recovery_arbitration_verdict={0}" -f $recoveryClassification.RuntimeUnlockRegularV4RecoveryArbitrationVerdict),
        ("runtime_unlock_offer_observation_blocked_count={0}" -f $recoveryClassification.RuntimeUnlockOfferObservationBlockedCount),
        ("session_liveness_timeout_after_runtime_unlock_count={0}" -f $recoveryClassification.SessionLivenessTimeoutAfterRuntimeUnlockCount),
        ("classification_fallback_tail_reconciliation_requested_count={0}" -f $recoveryClassification.FallbackTailReconciliationRequestedCount),
        ("classification_fallback_tail_reconciliation_accepted_count={0}" -f $recoveryClassification.FallbackTailReconciliationAcceptedCount),
        ("classification_fallback_tail_normal_zero_credit_state_refresh_count={0}" -f $recoveryClassification.FallbackTailNormalZeroCreditStateRefreshCount),
        ("classification_fallback_tail_quiescence_verdict={0}" -f $recoveryClassification.FallbackTailQuiescenceVerdict),
        ("classification_fallback_tail_progress_forced_count={0}" -f $recoveryClassification.FallbackTailProgressForcedCount),
        ("classification_fallback_tail_progress_coalesced_count={0}" -f $recoveryClassification.FallbackTailProgressCoalescedCount),
        ("classification_fallback_tail_terminal_proof_consumed_count={0}" -f $recoveryClassification.FallbackTailTerminalProofConsumedCount),
        ("classification_fallback_tail_quiescent_yield_granted_count={0}" -f $recoveryClassification.FallbackTailQuiescentYieldGrantedCount),
        ("classification_fallback_tail_outbound_yield_granted_count={0}" -f $recoveryClassification.FallbackTailOutboundYieldGrantedCount),
        ("classification_fallback_tail_inbound_yield_granted_count={0}" -f $recoveryClassification.FallbackTailInboundYieldGrantedCount),
        ("classification_fallback_tail_yield_proof_sent_count={0}" -f $recoveryClassification.FallbackTailYieldProofSentCount),
        ("classification_fallback_tail_yield_proof_received_count={0}" -f $recoveryClassification.FallbackTailYieldProofReceivedCount),
        ("classification_fallback_tail_paired_yield_granted_count={0}" -f $recoveryClassification.FallbackTailPairedYieldGrantedCount),
        ("classification_fallback_tail_owner_generation_mismatch_count={0}" -f $recoveryClassification.FallbackTailOwnerGenerationMismatchCount),
        ("classification_fallback_tail_local_yield_blocked_count={0}" -f $recoveryClassification.FallbackTailLocalYieldBlockedCount),
        ("classification_fallback_tail_stale_yield_proof_ignored_count={0}" -f $recoveryClassification.FallbackTailStaleYieldProofIgnoredCount),
        ("classification_fallback_tail_paired_yield_verdict={0}" -f $recoveryClassification.FallbackTailPairedYieldVerdict),
        ("classification_fallback_frontier_repair_authority_granted_count={0}" -f $recoveryClassification.FallbackFrontierRepairAuthorityGrantedCount),
        ("classification_fallback_frontier_repair_authority_rejected_count={0}" -f $recoveryClassification.FallbackFrontierRepairAuthorityRejectedCount),
        ("classification_fallback_frontier_repair_dispatched_count={0}" -f $recoveryClassification.FallbackFrontierRepairDispatchedCount),
        ("classification_fallback_frontier_repair_stalled_count={0}" -f $recoveryClassification.FallbackFrontierRepairStalledCount),
        ("classification_fallback_frontier_repair_progress_observed_count={0}" -f $recoveryClassification.FallbackFrontierRepairProgressObservedCount),
        ("classification_fallback_frontier_repair_verdict={0}" -f $recoveryClassification.FallbackFrontierRepairVerdict),
        ("classification_runtime_unlock_probe_rejected_by_fallback_owner_count={0}" -f $recoveryClassification.RuntimeUnlockProbeRejectedByFallbackOwnerCount),
        ("classification_runtime_unlock_probe_rejected_by_inbound_fallback_owner_count={0}" -f $recoveryClassification.RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount),
        ("classification_fallback_tail_quiescence_timeout_count={0}" -f $recoveryClassification.FallbackTailQuiescenceTimeoutCount),
        ("classification_control_plane_isolation_verdict={0}" -f $recoveryClassification.ControlPlaneIsolationVerdict),
        ("classification_control_plane_peer_visible_count={0}" -f $recoveryClassification.ControlPlanePeerVisibleCount),
        ("classification_control_plane_local_only_rejected_count={0}" -f $recoveryClassification.ControlPlaneLocalOnlyRejectedCount),
        ("classification_control_plane_checkpoint_exchange_count={0}" -f $recoveryClassification.ControlPlaneCheckpointExchangeCount),
        ("classification_control_plane_delivery_failure_count={0}" -f $recoveryClassification.ControlPlaneDeliveryFailureCount),
        ("classification_control_plane_liveness_timeout_during_valid_exchange_count={0}" -f $recoveryClassification.ControlPlaneLivenessTimeoutDuringValidExchangeCount),
        ("classification_control_plane_stale_send_retired_count={0}" -f $recoveryClassification.ControlPlaneRetiredStaleSendCount),
        ("classification_fallback_checkpoint_exchange_retired_count={0}" -f $recoveryClassification.FallbackCheckpointExchangeRetiredCount),
        ("classification_fallback_checkpoint_owner_violation_count={0}" -f $recoveryClassification.FallbackCheckpointOwnerViolationCount),
        ("classification_fallback_stale_deferred_replay_discarded_count={0}" -f $recoveryClassification.FallbackStaleDeferredReplayDiscardedCount),
        ("classification_fallback_checkpoint_not_pending_reissue_suppressed_count={0}" -f $recoveryClassification.FallbackCheckpointNotPendingReissueSuppressedCount),
        ("classification_fallback_stale_proof_ignored_count={0}" -f $recoveryClassification.FallbackStaleProofIgnoredCount),
        ("classification_fallback_stale_proof_replay_suppressed_count={0}" -f $recoveryClassification.FallbackStaleProofReplaySuppressedCount),
        ("classification_fallback_control_plane_stale_replay_treadmill_count={0}" -f $recoveryClassification.FallbackControlPlaneStaleReplayTreadmillCount),
        ("classification_fallback_stale_generation_treadmill_score={0}" -f $recoveryClassification.FallbackStaleGenerationTreadmillScore),
        ("classification_fallback_stale_generation_treadmill_score_limit={0}" -f $recoveryClassification.FallbackStaleGenerationTreadmillScoreLimit),
        ("classification_fallback_checkpoint_ids_by_leg_generation={0}" -f $recoveryClassification.FallbackCheckpointIdsByLegGeneration),
        ("classification_fallback_checkpoint_id_count_max_per_leg={0}" -f $recoveryClassification.FallbackCheckpointIdCountMaxPerLeg),
        ("classification_fallback_control_plane_non_current_leg_generation_send_count={0}" -f $recoveryClassification.FallbackControlPlaneNonCurrentLegGenerationSendCount),
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
        ("fallback_leg_authority_proof_verdict={0}" -f $fallbackAuthorityProof.Verdict),
        ("clean_fallback_leg_proof_verdict={0}" -f $fallbackAuthorityProof.Verdict),
        ("fallback_leg_authority_started_count={0}" -f $fallbackAuthorityProof.StartedCount),
        ("fallback_leg_authority_checkpoint_accepted_count={0}" -f $fallbackAuthorityProof.CheckpointAcceptedCount),
        ("fallback_leg_authority_bridge_recovery_requested_count={0}" -f $fallbackAuthorityProof.BridgeRecoveryRequestedCount),
        ("fallback_leg_authority_bridge_recovery_escalated_count={0}" -f $fallbackAuthorityProof.BridgeRecoveryEscalatedCount),
        ("fallback_leg_authority_completed_count={0}" -f $fallbackAuthorityProof.CompletedCount),
        ("fallback_leg_authority_continuous_receiver_state_proof_count={0}" -f $fallbackAuthorityProof.ContinuousReceiverStateProofCount),
        ("fallback_leg_authority_stale_proof_ignored_count={0}" -f $fallbackAuthorityProof.StaleProofIgnoredCount),
        ("fallback_leg_authority_metadata_missing_count={0}" -f $fallbackAuthorityProof.MetadataMissingCount),
        ("fallback_tail_reconciliation_verdict={0}" -f $fallbackTailProof.Verdict),
        ("fallback_tail_quiescence_verdict={0}" -f $fallbackTailProof.QuiescenceVerdict),
        ("fallback_tail_reconciliation_requested_count={0}" -f $fallbackTailProof.RequestedCount),
        ("fallback_tail_reconciliation_accepted_count={0}" -f $fallbackTailProof.AcceptedCount),
        ("fallback_tail_progress_forced_count={0}" -f $fallbackTailProof.ProgressForcedCount),
        ("fallback_tail_progress_coalesced_count={0}" -f $fallbackTailProof.ProgressCoalescedCount),
        ("fallback_tail_terminal_proof_consumed_count={0}" -f $fallbackTailProof.TerminalProofConsumedCount),
        ("fallback_tail_quiescent_yield_granted_count={0}" -f $fallbackTailProof.QuiescentYieldGrantedCount),
        ("fallback_tail_outbound_yield_granted_count={0}" -f $fallbackTailProof.OutboundQuiescentYieldGrantedCount),
        ("fallback_tail_inbound_yield_granted_count={0}" -f $fallbackTailProof.InboundQuiescentYieldGrantedCount),
        ("fallback_tail_yield_proof_sent_count={0}" -f $fallbackTailProof.YieldProofSentCount),
        ("fallback_tail_yield_proof_received_count={0}" -f $fallbackTailProof.YieldProofReceivedCount),
        ("fallback_tail_paired_yield_granted_count={0}" -f $fallbackTailProof.PairedYieldGrantedCount),
        ("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailProof.OwnerGenerationMismatchCount),
        ("fallback_tail_local_yield_blocked_count={0}" -f $fallbackTailProof.LocalYieldBlockedCount),
        ("fallback_tail_stale_yield_proof_ignored_count={0}" -f $fallbackTailProof.StaleYieldProofIgnoredCount),
        ("fallback_tail_paired_yield_verdict={0}" -f $fallbackTailProof.PairedYieldVerdict),
        ("fallback_frontier_repair_authority_granted_count={0}" -f $fallbackTailProof.FrontierRepairAuthorityGrantedCount),
        ("fallback_frontier_repair_authority_rejected_count={0}" -f $fallbackTailProof.FrontierRepairAuthorityRejectedCount),
        ("fallback_frontier_repair_dispatched_count={0}" -f $fallbackTailProof.FrontierRepairDispatchedCount),
        ("fallback_frontier_repair_stalled_count={0}" -f $fallbackTailProof.FrontierRepairStalledCount),
        ("fallback_frontier_repair_progress_observed_count={0}" -f $fallbackTailProof.FrontierRepairProgressObservedCount),
        ("fallback_frontier_repair_verdict={0}" -f $fallbackTailProof.FrontierRepairVerdict),
        ("runtime_unlock_probe_rejected_by_fallback_owner_count={0}" -f $fallbackTailProof.RuntimeUnlockProbeRejectedByFallbackOwnerCount),
        ("runtime_unlock_probe_rejected_by_inbound_fallback_owner_count={0}" -f $fallbackTailProof.RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount),
        ("fallback_tail_quiescence_timeout_count={0}" -f $fallbackTailProof.QuiescenceTimeoutCount),
        ("fallback_tail_zero_credit_breaker_count={0}" -f $fallbackTailProof.ZeroCreditBreakerCount),
        ("fallback_tail_stale_frontier_retired_count={0}" -f $fallbackTailProof.StaleFrontierRetiredCount),
        ("fallback_tail_state_refresh_send_slot_retired_count={0}" -f $fallbackTailProof.SendSlotRetiredCount),
        ("fallback_tail_receive_recovery_requested_count={0}" -f $fallbackTailProof.ReceiveRecoveryRequestedCount),
        ("fallback_tail_normal_zero_credit_state_refresh_count={0}" -f $fallbackTailProof.NormalZeroCreditTailStateRefreshCount),
        ("fallback_tail_reconciliation_metadata_missing_count={0}" -f $fallbackTailProof.MetadataMissingCount),
        ("bridge_liveness_integration_verdict={0}" -f $bridgeLivenessProof.Verdict),
        ("session_liveness_deferred_for_current_recovery_count={0}" -f $bridgeLivenessProof.CurrentRecoveryDeferralCount),
        ("session_liveness_deferred_for_bridge_recovery_count={0}" -f $bridgeLivenessProof.BridgeRecoveryDeferralCount),
        ("session_liveness_timeout_during_valid_recovery_count={0}" -f $bridgeLivenessProof.TimeoutDuringValidRecoveryCount),
        ("bridge_recovery_receive_resumed_count={0}" -f $bridgeLivenessProof.ReceiveResumedCount),
        ("bridge_recovery_exhausted_without_proof_count={0}" -f $bridgeLivenessProof.RecoveryExhaustedWithoutProofCount),
        ("fallback_leg_authority_liveness_deferral_count={0}" -f $bridgeLivenessProof.FallbackLegAuthorityLivenessDeferralCount),
        ("bridge_liveness_stale_deferral_count={0}" -f $bridgeLivenessProof.StaleDeferralCount),
        ("bridge_exhausted_sibling_deferral_suppressed_count={0}" -f $bridgeLivenessProof.SiblingDeferralSuppressedCount),
        ("bridge_exhausted_sibling_deferral_expired_count={0}" -f $bridgeLivenessProof.SiblingDeferralExpiredCount),
        ("bridge_exhausted_terminal_during_valid_deferral_count={0}" -f $bridgeLivenessProof.TerminalDuringValidSiblingDeferralCount),
        ("control_plane_isolation_verdict={0}" -f $controlPlaneProof.Verdict),
        ("control_plane_peer_visible_count={0}" -f $controlPlaneProof.PeerVisibleCount),
        ("control_plane_local_only_rejected_count={0}" -f $controlPlaneProof.LocalOnlyRejectedCount),
        ("control_plane_checkpoint_exchange_count={0}" -f $controlPlaneProof.CheckpointExchangeCount),
        ("control_plane_continuous_receiver_state_proof_count={0}" -f $controlPlaneProof.ContinuousReceiverStateProofCount),
        ("control_plane_delivery_failure_count={0}" -f $controlPlaneProof.DeliveryFailureCount),
        ("control_plane_delivery_failure_recovered_count={0}" -f $controlPlaneProof.RecoveredDeliveryFailureCount),
        ("control_plane_liveness_timeout_during_valid_exchange_count={0}" -f $controlPlaneProof.LivenessTimeoutDuringValidExchangeCount),
        ("control_plane_stale_send_retired_count={0}" -f $controlPlaneProof.RetiredStaleSendCount),
        ("fallback_checkpoint_exchange_retired_count={0}" -f $controlPlaneProof.CheckpointExchangeRetiredCount),
        ("fallback_checkpoint_owner_violation_count={0}" -f $controlPlaneProof.CheckpointOwnerViolationCount),
        ("fallback_stale_deferred_replay_discarded_count={0}" -f $controlPlaneProof.StaleDeferredReplayDiscardedCount),
        ("fallback_checkpoint_not_pending_reissue_suppressed_count={0}" -f $controlPlaneProof.CheckpointNotPendingReissueSuppressedCount),
        ("fallback_stale_proof_ignored_count={0}" -f $controlPlaneProof.StaleProofIgnoredCount),
        ("fallback_stale_proof_replay_suppressed_count={0}" -f $controlPlaneProof.StaleProofReplaySuppressedCount),
        ("fallback_control_plane_stale_replay_treadmill_count={0}" -f $controlPlaneProof.StaleReplayTreadmillCount),
        ("fallback_stale_generation_treadmill_score={0}" -f $controlPlaneProof.StaleGenerationTreadmillScore),
        ("fallback_stale_generation_treadmill_score_limit={0}" -f $controlPlaneProof.StaleGenerationTreadmillScoreLimit),
        ("fallback_checkpoint_ids_by_leg_generation={0}" -f $controlPlaneProof.CheckpointIdsByLegGeneration),
        ("fallback_checkpoint_id_count_max_per_leg={0}" -f $controlPlaneProof.CheckpointIdCountMaxPerLeg),
        ("fallback_control_plane_non_current_leg_generation_send_count={0}" -f $controlPlaneProof.ControlPlaneNonCurrentLegGenerationSendCount),
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
    $runtimeUnlockRetryReleasedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_released_after_regular_v4_receive_recovery'
    })
    $runtimeUnlockRetryBlockedByRegularV4RecoveryEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_blocked_by_regular_v4_receive_recovery'
    })
    $runtimeUnlockRetryHeldByRegularV4RecoveryEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery'
    })
    $runtimeUnlockRetryRejectedAfterRegularV4RecoveryEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery'
    })
    $runtimeUnlockRetryRegularV4RecoverySettlementMissingEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
    })
    $runtimeUnlockRetryDispatchAuthorityGrantedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_dispatch_authority_granted'
    })
    $runtimeUnlockRetryDispatchAuthorityRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_dispatch_authority_rejected'
    })
    $runtimeUnlockRetryDispatchObservedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_dispatch_observed'
    })
    $runtimeUnlockRetryDispatchHeldByActiveNegotiationEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_retry_dispatch_held_by_active_negotiation'
    })
    $runtimeUnlockRetryQueuedBehindActiveNegotiationEvents = @(@($runtimeUnlockRetryScheduledEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'queued_behind_active_negotiation' -Default '0') -eq '1'
    }) + $runtimeUnlockRetryDispatchHeldByActiveNegotiationEvents)
    $runtimeUnlockBrokenStateFreshRetryConsumedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_broken_state_fresh_retry_consumed'
    })
    $runtimeUnlockBrokenStateRetrySuppressedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_broken_state_retry_suppressed'
    })
    $sessionRecoveryContractRetryDispatchedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_retry_dispatched'
    })
    $sessionRecoveryContractRetryAuthorityGrantedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_retry_authority_granted'
    })
    $sessionRecoveryContractRetryAuthorityObservedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_retry_authority_observed'
    })
    $sessionRecoveryContractRetryAuthorityFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_retry_authority_failed'
    })
    $runtimeUnlockOfferPeerResponseTimeoutEvents = @($events | Where-Object {
        $_.EventName -eq 'tuna_acceleration_runtime_unlock_offer_peer_response_timeout'
    })
    $runtimeUnlockCutThroughStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_cutthrough_started'
    })
    $runtimeUnlockCutThroughPeerReceivedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_cutthrough_peer_received' -or
        $_.EventName -eq 'runtime_unlock_cutthrough_answer_received' -or
        $_.EventName -eq 'runtime_unlock_cutthrough_completed'
    })
    $runtimeUnlockCutThroughFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_cutthrough_failed'
    })
    $listenerRearmRequiredEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_listener_rearm_required'
    })
    $listenerRearmCompletedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_listener_rearm_completed'
    })
    $listenerRearmFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_listener_rearm_failed' -or
        (
            $_.EventName -eq 'session_recovery_contract_failed' -and
            (Get-FileTransferEventField -Event $_ -Name 'authority_failure_reason' -Default '') -eq 'runtime_unlock_listener_rearm_failed'
        )
    })
    $runtimeUnlockOfferDispatchedAfterListenerRearmEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_offer_dispatched_after_listener_rearm'
    })
    $runtimeUnlockDispatchDeferredForRegularV4RecoveryEvents = @($events | Where-Object {
        $_.EventName -eq 'tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery'
    })
    $runtimeUnlockRegularV4ActivationWindowStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_regular_v4_activation_window_started'
    })
    $runtimeUnlockRegularV4ActivationWindowPeerVisibleEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_regular_v4_activation_window_peer_visible'
    })
    $runtimeUnlockRegularV4ActivationWindowFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_regular_v4_activation_window_failed'
    })
    $runtimeUnlockRegularV4PeerVisibleWindowDeliveryStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_regular_v4_peer_visible_window_delivery_started'
    })
    $runtimeUnlockRegularV4PeerVisibleWindowDeliveryFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_regular_v4_peer_visible_window_delivery_failed' -or
        $_.EventName -eq 'runtime_unlock_control_plane_observed_send_failed'
    })
    $runtimeUnlockRegularV4PeerVisibleWindowLateProofIgnoredEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored'
    })
    $regularV4RepairSpamCoalescedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_repair_spam_coalesced'
    })
    $postTunaFallbackControlPlanePressureMarkedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_post_tuna_fallback_control_plane_pressure_marked'
    })
    $postTunaFallbackControlPlanePressureClearedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_post_tuna_fallback_control_plane_pressure_cleared'
    })
    $postTunaFallbackControlPlanePressureAllowedEvents = @($events | Where-Object {
        $_.EventName -eq 'nkn_bridge_control_receive_recovery_allowed' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'post_tuna_fallback_control_plane_pressure'
    })
    $postTunaFallbackControlPlanePressureBudgetSuppressedEvents = @($events | Where-Object {
        $_.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'post_tuna_fallback_control_plane_pressure_budget_exhausted'
    })
    $postTunaFallbackBulkActiveSuppressedDuringPressureEvents = @($events | Where-Object {
        $_.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -in @('bulk_receive_active', 'filetransfer_bulk_receive_active') -and
        (Get-FileTransferEventField -Event $_ -Name 'post_tuna_fallback_control_plane_pressure' -Default '0') -eq '1' -and
        (Get-FileTransferEventField -Event $_ -Name 'pressure_grace_exhausted' -Default '0') -eq '1'
    })
    $postTunaFallbackLegacyControlLaneStarvationEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_control_coalesced' -or
        $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested' -or
        $_.EventName -eq 'filetransfer_post_tuna_fallback_replay_accepted_tail_preserved'
    })
    $listenerReadyUnavailableContradictionEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_tuna_gui_handoff_fallback_failure' -and
        (Get-FileTransferEventField -Event $_ -Name 'failureReason' -Default '') -eq 'listener_ready_unavailable_contradiction'
    })
    $tunaPathLeaseStartedEvents = @($events | Where-Object { $_.EventName -eq 'tuna_path_lease_started' })
    $tunaPathLeaseReadyEvents = @($events | Where-Object { $_.EventName -eq 'tuna_path_lease_listener_ready' })
    $tunaPathLeaseFailedEvents = @($events | Where-Object { $_.EventName -eq 'tuna_path_lease_failed' })
    $tunaPathLeaseRetiredEvents = @($events | Where-Object { $_.EventName -eq 'tuna_path_lease_retired' })
    $tunaPathLeaseStaleListenerIgnoredEvents = @($events | Where-Object { $_.EventName -eq 'tuna_path_lease_stale_listener_event_ignored' })
    $runtimeUnlockAnswerRejectedTunaPathLeaseUnavailableEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_answer_rejected_tuna_path_lease_unavailable'
    })
    $runtimeUnlockTunaPathLeaseRouteCommitRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_route_commit_rejected' -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'tuna_path_lease_failed' -or
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'tuna_path_lease_unavailable' -or
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'tuna_path_lease_retired'
        )
    })
    $runtimeUnlockOfferRejectedWithoutObservationEvents = @($events | Where-Object {
        $_.EventName -eq 'tuna_acceleration_offer_rejected' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'runtime_unlock' -and
        (Get-FileTransferEventField -Event $_ -Name 'observed_lane' -Default '') -eq '(none)'
    })
    $runtimeUnlockReceiveRecoveryBlockedOfferEvents = @($events | Where-Object {
        ($_.EventName -eq 'tuna_activation_control_send_waiting_for_regular_v4_recovery') -or
        ($_.EventName -eq 'tuna_activation_control_send_regular_v4_receive_stall_bypassed') -or
        (
            $_.EventName -eq 'tuna_acceleration_control_bulk_queue_fallback_skipped' -and
            (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'runtime_unlock_active_filetransfer_requires_direct_observed_send'
        )
    })
    $runtimeUnlockTransactionStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_transaction_offer_generation_created'
    })
    $runtimeUnlockTransactionObservedSendEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_transaction_observed_send'
    })
    $runtimeUnlockTransactionPeerProofEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_transaction_peer_received' -or
        $_.EventName -eq 'runtime_unlock_transaction_answer_received'
    })
    $runtimeUnlockProbeStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_probe_started'
    })
    $runtimeUnlockProbeAckedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_probe_acked'
    })
    $runtimeUnlockProbeFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_probe_failed'
    })
    $runtimeUnlockPreCommitProbeProtocolMismatchEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_data_frame_ignored' -and
        (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.transport_probe.v6' -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'protocol_not_v4' -or
            (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'protocol_not_v6'
        )
    })
    $runtimeUnlockPreCommitProbeStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_started'
    })
    $runtimeUnlockPreCommitProbeSentEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_sent'
    })
    $runtimeUnlockPreCommitProbeWaitingForTransportProofEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_precommit_probe_waiting_for_transport_proof' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'fallback_tail_quiescent'
    })
    $runtimeUnlockPreCommitProbePendingHeldEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_precommit_probe_pending_held' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'fallback_tail_quiescent'
    })
    $runtimeUnlockPreCommitProbePendingRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_precommit_probe_pending_rejected'
    })
    $runtimeUnlockPreCommitProbeDeliveryResultEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_delivery_result'
    })
    $runtimeUnlockPreCommitProbeAckedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_acked'
    })
    $runtimeUnlockTunaProbeSentEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_sent'
    })
    $runtimeUnlockTunaProbeReceivedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_received'
    })
    $runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_ack_authority_rejected'
    })
    $runtimeUnlockTunaProbeAckSentEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_ack_sent'
    })
    $runtimeUnlockPreCommitProbeAckDeliveryResultEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_ack_delivery_result'
    })
    $runtimeUnlockPreCommitProbeAckReceiveResultEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_data_path_receive_result' -and
        (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -eq 'filetransfer.runtime_unlock.precommit_probe_ack'
    })
    $runtimeUnlockPreCommitProbeAckReceiveDispatchRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_ack_receive_dispatch_rejected'
    })
    $runtimeUnlockPreCommitProbeAckReceiveQueuedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_ack_receive_queued'
    })
    $runtimeUnlockPreCommitProbeAckReceiveDispatchedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_ack_receive_dispatched'
    })
    $runtimeUnlockTunaProbeAckReceivedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_ack_received'
    })
    $runtimeUnlockFallbackQuiescentAdmissionGrantedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_admission_granted' -and
        (Get-FileTransferEventField -Event $_ -Name 'source' -Default '') -eq 'fallback_survival_resume' -and
        (Get-FileTransferEventField -Event $_ -Name 'admission_reason' -Default '') -eq 'fallback_quiescent'
    })
    $runtimeUnlockFallbackProofReplayPendingAfterPairedYieldEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_admission_denied' -and
        (Get-FileTransferEventField -Event $_ -Name 'admission_reason' -Default '') -eq 'fallback_proof_replay_pending' -and
        (Get-FileTransferEventInt64Field -Event $_ -Name 'paired_fallback_yield_granted' -Default 0) -eq 1
    })
    $runtimeUnlockTunaProbeSidecarFailureEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_sidecar_failure'
    })
    $runtimeUnlockTunaProbeSidecarFreshTransactionScheduledEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_sidecar_closed_fresh_transaction_scheduled'
    })
    $runtimeUnlockTunaProbeSidecarFreshTransactionStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_tuna_probe_sidecar_closed_fresh_transaction_started'
    })
    $runtimeUnlockListenerRearmAfterProbeCloseFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'session_recovery_contract_listener_rearm_failed' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'runtime_unlock_tuna_listener_rearm_after_probe_close_failed'
    })
    $regularV4TerminalBeforeActivationEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_tuna_gui_regular_v4_terminal_before_tuna_activation_commit'
    })
    $runtimeUnlockPreCommitRouteEpochProofEvents = @($events | Where-Object {
        ($_.EventName -eq 'filetransfer_live_route_epoch_started' -or $_.EventName -eq 'filetransfer_live_route_epoch_recovered') -and
        (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v4' -and
        (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -eq '4' -and
        (Get-FileTransferEventField -Event $_ -Name 'handoff_kind' -Default '') -eq 'normal_to_tuna_activation' -and
        (Get-FileTransferEventField -Event $_ -Name 'target_transport' -Default '') -eq 'tuna' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -in @(
            'precommit_probe_ack',
            'precommit_probe_commit_confirmed',
            'precommit_probe_commit_confirmed_reply',
            'precommit_probe_commit_confirmed_reply_sent')
    })
    $runtimeUnlockPreCommitRouteEpochRecoveredEvents = @($runtimeUnlockPreCommitRouteEpochProofEvents | Where-Object {
        $_.EventName -eq 'filetransfer_live_route_epoch_recovered'
    })
    $runtimeUnlockPreCommitProbeFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_precommit_probe_failed'
    })
    $runtimeUnlockPairedCommitStartedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_paired_commit_started'
    })
    $runtimeUnlockPairedCommitConfirmedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_paired_commit_confirmed' -or
        $_.EventName -eq 'runtime_unlock_precommit_probe_paired_commit_confirmed'
    })
    $runtimeUnlockPairedCommitAbortedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_paired_commit_aborted'
    })
    $runtimeUnlockTransactionCommitPendingEvents = @($events | Where-Object {
        ($_.EventName -like 'runtime_unlock_transaction_*') -and
        (Get-FileTransferEventField -Event $_ -Name 'route_commit_pending' -Default '0') -eq '1'
    })
    $runtimeUnlockTransactionCommittedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_transaction_route_committed' -or
        $_.EventName -eq 'filetransfer_runtime_unlock_route_commit_accepted'
    })
    $runtimeUnlockTransactionFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_transaction_failed' -or
        $_.EventName -eq 'runtime_unlock_transaction_retired' -or
        $_.EventName -eq 'filetransfer_runtime_unlock_route_commit_rejected'
    })
    $runtimeUnlockBrokenStateTransactionFailedEvents = @($events | Where-Object {
        $_.EventName -eq 'runtime_unlock_transaction_failed' -and
        (Get-FileTransferEventField -Event $_ -Name 'failure_reason' -Default '') -like 'runtime_unlock_broken_state_retry_exhausted*'
    })
    $runtimeUnlockTransactionLocalOnlyRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_route_commit_rejected' -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'transaction_proof_missing' -or
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'peer_visible_proof_missing'
        )
    })
    $runtimeUnlockProbeMissingRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_route_commit_rejected' -and
        (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'runtime_unlock_probe_missing'
    })
    $runtimeUnlockProbeInvalidRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_runtime_unlock_route_commit_rejected' -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'runtime_unlock_probe_failed' -or
            (Get-FileTransferEventField -Event $_ -Name 'rejection_reason' -Default '') -eq 'runtime_unlock_probe_target_invalid'
        )
    })
    $regularV4FrontierRebindEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_receive_recovery_frontier_rebind'
    })
    $regularV4FrontierRebindRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_frontier_rebind_rejected'
    })
    $regularV4FrontierStallMissingRangeEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_v4_frontier_stall_missing_range_due'
    })
    $regularV4RepairRequestedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_v4_repair_requested'
    })
    $regularV4FrontierRepairAuthorityGrantedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_frontier_repair_authority_granted'
    })
    $regularV4FrontierRepairAuthorityRejectedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_frontier_repair_authority_rejected'
    })
    $regularV4FrontierRepairDispatchedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_frontier_repair_dispatched'
    })
    $regularV4FrontierRepairProgressObservedEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_frontier_repair_progress_observed'
    })
    $regularV4FrontierRepairStalledEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_regular_v4_frontier_repair_stalled'
    })
    $regularV4RepairCachePressureEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_sender_repair_cache_pressure_entered'
    })
    $regularV4RepairSkippedNotYetSentEvents = @($events | Where-Object {
        $_.EventName -eq 'filetransfer_sender_repair_chunk_skipped' -and
        (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'not_yet_sent'
    })
    $sessionLivenessTimeoutEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout' })
    $peerDisconnectedTerminalEvents = @($events | Where-Object {
        ($_.EventName -eq 'file_transfer_outbound_terminal' -or
         $_.EventName -eq 'file_transfer_inbound_terminal' -or
         $_.EventName -eq 'transfer_terminal') -and
        (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '') -eq 'peer_disconnected'
    })
    $cleanRegularV4TerminalEvents = @($events | Where-Object {
        ($_.EventName -eq 'file_transfer_outbound_terminal' -or
         $_.EventName -eq 'file_transfer_inbound_terminal' -or
         $_.EventName -eq 'transfer_terminal') -and
        (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'regular_nkn_v4_fast' -and
        (
            (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -in @('', '(none)', 'none') -or
            (Get-FileTransferEventField -Event $_ -Name 'state' -Default '') -eq 'Completed'
        )
    })
    $fileTunaV6Events = @($events | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v6'
    })
    $fallbackTailProof = Get-FileTransferFallbackTailReconciliationProof -Summary $Summary
    $controlPlaneProof = Get-FileTransferControlPlaneIsolationProof -Summary $Summary
    $staleRejectedActionEffectProof = Get-FileTransferStaleRejectedActionEffectProof -Summary $Summary
    $fallbackRuntimeUnlockSeparationProof = Get-FileTransferFallbackRuntimeUnlockSeparationProof -Summary $Summary
    $publicationAuthorityFieldCoverageProof = Get-FileTransferPublicationAuthorityFieldCoverageProof -Summary $Summary
    $fileTunaV4DataPathProof = Get-FileTransferFileTunaV4DataPathProof -Summary $Summary

    function Test-RuntimeUnlockRetryDispatchMatch {
        param(
            [Parameter(Mandatory = $true)]$Release,
            [Parameter(Mandatory = $true)]$Candidate
        )

        $releaseSession = Get-FileTransferEventField -Event $Release -Name 'session_id' -Default ''
        $candidateSession = Get-FileTransferEventField -Event $Candidate -Name 'session_id' -Default ''
        if ([string]::IsNullOrWhiteSpace($releaseSession) -or
            [string]::IsNullOrWhiteSpace($candidateSession) -or
            $releaseSession -ne $candidateSession) {
            return $false
        }

        $releaseTransfer = Get-FileTransferEventField -Event $Release -Name 'transfer_id' -Default ''
        $candidateTransfer = Get-FileTransferEventField -Event $Candidate -Name 'transfer_id' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($releaseTransfer) -and
            $releaseTransfer -ne '(none)' -and
            -not [string]::IsNullOrWhiteSpace($candidateTransfer) -and
            $candidateTransfer -ne '(none)' -and
            $releaseTransfer -ne $candidateTransfer) {
            return $false
        }

        $releaseContract = Get-FileTransferEventField -Event $Release -Name 'contract_generation' -Default ''
        $candidateContract = Get-FileTransferEventField -Event $Candidate -Name 'contract_generation' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($releaseContract) -and
            -not [string]::IsNullOrWhiteSpace($candidateContract) -and
            $releaseContract -ne $candidateContract) {
            return $false
        }

        return $true
    }

    function Test-RuntimeUnlockPreCommitProbeTupleMatch {
        param(
            [Parameter(Mandatory = $true)]$Sent,
            [Parameter(Mandatory = $true)]$Candidate
        )

        foreach ($identityField in @('session_id', 'transfer_id')) {
            $sentValue = Get-FileTransferEventField -Event $Sent -Name $identityField -Default ''
            $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $identityField -Default ''
            if (-not [string]::IsNullOrWhiteSpace($sentValue) -and
                $sentValue -ne '(none)' -and
                -not [string]::IsNullOrWhiteSpace($candidateValue) -and
                $candidateValue -ne '(none)' -and
                $sentValue -ne $candidateValue) {
                return $false
            }
        }

        $matchedDiscriminator = $false
        foreach ($probeField in @('transaction_generation', 'offer_generation', 'tuna_path_lease_generation', 'probe_id')) {
            $sentValue = Get-FileTransferEventField -Event $Sent -Name $probeField -Default ''
            $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $probeField -Default ''
            if ([string]::IsNullOrWhiteSpace($sentValue) -or
                $sentValue -eq '(none)' -or
                [string]::IsNullOrWhiteSpace($candidateValue) -or
                $candidateValue -eq '(none)') {
                continue
            }

            if ($sentValue -ne $candidateValue) {
                return $false
            }

            $matchedDiscriminator = $true
        }

        return $matchedDiscriminator
    }

    $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount = 0
    $regularV4RecoverySettlementCandidates = @()
    if ($runtimeUnlockRetryBlockedByRegularV4RecoveryEvents.Count -gt 0) {
        $regularV4RecoverySettlementCandidates = @($runtimeUnlockRetryBlockedByRegularV4RecoveryEvents)
    }
    else {
        $regularV4RecoverySettlementCandidates = @($runtimeUnlockRetryHeldByRegularV4RecoveryEvents)
    }

    if ($regularV4RecoverySettlementCandidates.Count -eq 0 -and
        $runtimeUnlockRetryRegularV4RecoverySettlementMissingEvents.Count -gt 0) {
        $regularV4RecoverySettlementCandidates = @($runtimeUnlockRetryRegularV4RecoverySettlementMissingEvents)
    }

    foreach ($blockedRetry in $regularV4RecoverySettlementCandidates) {
        $blockedSequence = [int64]$blockedRetry.Sequence
        $matchingRelease = @($runtimeUnlockRetryReleasedEvents | Where-Object {
            [int64]$_.Sequence -gt $blockedSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $blockedRetry -Candidate $_)
        })
        $matchingRegularV4Rejection = @($runtimeUnlockRetryRejectedAfterRegularV4RecoveryEvents | Where-Object {
            [int64]$_.Sequence -gt $blockedSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $blockedRetry -Candidate $_)
        })
        $matchingContractFailure = @($events | Where-Object {
            [int64]$_.Sequence -gt $blockedSequence -and
            $_.EventName -eq 'session_recovery_contract_failed' -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $blockedRetry -Candidate $_)
        })

        if ($matchingRelease.Count -eq 0 -and
            $matchingRegularV4Rejection.Count -eq 0 -and
            $matchingContractFailure.Count -eq 0) {
            $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount++
        }
    }

    $runtimeUnlockRetryDispatchMissingCount = 0
    $runtimeUnlockRetryDispatchReleaseCount = $runtimeUnlockRetryReleasedEvents.Count
    foreach ($release in $runtimeUnlockRetryReleasedEvents) {
        $releaseSequence = [int64]$release.Sequence
        $matchingGrant = @($runtimeUnlockRetryDispatchAuthorityGrantedEvents | Where-Object {
            [int64]$_.Sequence -gt $releaseSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $release -Candidate $_)
        })
        $matchingObserved = @($runtimeUnlockRetryDispatchObservedEvents | Where-Object {
            [int64]$_.Sequence -gt $releaseSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $release -Candidate $_)
        })
        $matchingRejected = @($runtimeUnlockRetryDispatchAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $releaseSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $release -Candidate $_)
        })
        $matchingFailed = @($events | Where-Object {
            [int64]$_.Sequence -gt $releaseSequence -and
            $_.EventName -eq 'session_recovery_contract_failed' -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $release -Candidate $_)
        })

        if (($matchingGrant.Count -gt 0 -and $matchingObserved.Count -gt 0) -or
            $matchingRejected.Count -gt 0 -or
            $matchingFailed.Count -gt 0) {
            continue
        }

        $runtimeUnlockRetryDispatchMissingCount++
    }

    foreach ($grant in @($runtimeUnlockRetryDispatchAuthorityGrantedEvents | Where-Object {
        (Get-FileTransferEventField -Event $_ -Name 'queued_behind_active_negotiation' -Default '0') -eq '1'
    })) {
        $grantSequence = [int64]$grant.Sequence
        $matchingObserved = @($runtimeUnlockRetryDispatchObservedEvents | Where-Object {
            [int64]$_.Sequence -gt $grantSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $grant -Candidate $_)
        })
        $matchingRejected = @($runtimeUnlockRetryDispatchAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $grantSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $grant -Candidate $_)
        })
        $matchingFailed = @($events | Where-Object {
            [int64]$_.Sequence -gt $grantSequence -and
            $_.EventName -eq 'session_recovery_contract_failed' -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $grant -Candidate $_)
        })

        if ($matchingObserved.Count -eq 0 -and
            $matchingRejected.Count -eq 0 -and
            $matchingFailed.Count -eq 0) {
            $runtimeUnlockRetryDispatchMissingCount++
        }
    }

    $runtimeUnlockRetryMissingAuthorityAfterReleaseCount = 0
    foreach ($release in $runtimeUnlockRetryReleasedEvents) {
        $releaseSequence = [int64]$release.Sequence
        $matchingMissingAuthority = @($events | Where-Object {
            [int64]$_.Sequence -gt $releaseSequence -and
            (Test-RuntimeUnlockRetryDispatchMatch -Release $release -Candidate $_) -and
            $_.EventName -notin @(
                'runtime_unlock_retry_dispatch_authority_rejected',
                'session_recovery_contract_failed') -and
            (
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'missing_runtime_unlock_retry_authority' -or
                (Get-FileTransferEventField -Event $_ -Name 'blocker_reason' -Default '') -eq 'missing_runtime_unlock_retry_authority' -or
                (Get-FileTransferEventField -Event $_ -Name 'authority_failure_reason' -Default '') -eq 'missing_runtime_unlock_retry_authority' -or
                (Get-FileTransferEventField -Event $_ -Name 'decision_reason' -Default '') -eq 'missing_runtime_unlock_retry_authority'
            )
        })

        foreach ($missingAuthority in $matchingMissingAuthority) {
            $missingSequence = [int64]$missingAuthority.Sequence
            $matchingRejectedOrFailed = @($events | Where-Object {
                [int64]$_.Sequence -gt $missingSequence -and
                (Test-RuntimeUnlockRetryDispatchMatch -Release $release -Candidate $_) -and
                (
                    $_.EventName -eq 'runtime_unlock_retry_dispatch_authority_rejected' -or
                    $_.EventName -eq 'session_recovery_contract_failed'
                )
            })

            if ($matchingRejectedOrFailed.Count -eq 0) {
                $runtimeUnlockRetryMissingAuthorityAfterReleaseCount++
            }
        }
    }

    $runtimeUnlockRetryDispatchProofVerdict = if ($runtimeUnlockRetryDispatchMissingCount -gt 0 -or
        $runtimeUnlockRetryMissingAuthorityAfterReleaseCount -gt 0) {
        'fail'
    } elseif ($runtimeUnlockRetryDispatchReleaseCount -gt 0) {
        'pass'
    } else {
        'none'
    }

    $runtimeUnlockPreCommitProbeAckMissingCount = 0
    foreach ($sent in $runtimeUnlockPreCommitProbeSentEvents) {
        $sentSequence = [int64]$sent.Sequence
        $matchingProbeDelivery = @(
            $runtimeUnlockPreCommitProbeDeliveryResultEvents |
                Where-Object {
                    (Get-FileTransferEventField -Event $_ -Name 'delivery_result' -Default '') -eq 'sent' -and
                    (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'tuna' -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $sent -Candidate $_)
                }
        )
        $matchingProbeReceiveOrAck = @(
            @($runtimeUnlockTunaProbeReceivedEvents + $runtimeUnlockTunaProbeAckSentEvents + $runtimeUnlockTunaProbeAckReceivedEvents + $runtimeUnlockPreCommitProbeAckedEvents + $runtimeUnlockTransactionCommittedEvents) |
                Where-Object {
                    [int64]$_.Sequence -gt $sentSequence -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $sent -Candidate $_)
                }
        )
        $matchingProbeFailure = @($runtimeUnlockPreCommitProbeFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $sentSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $sent -Candidate $_)
        })

        if ($matchingProbeDelivery.Count -eq 0 -and
            $matchingProbeReceiveOrAck.Count -eq 0 -and
            $matchingProbeFailure.Count -eq 0) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        }
    }

    foreach ($sent in $runtimeUnlockPreCommitProbeSentEvents) {
        $sentSequence = [int64]$sent.Sequence
        $matchingAckOrCommit = @(
            @($runtimeUnlockPreCommitProbeAckedEvents + $runtimeUnlockTunaProbeAckReceivedEvents + $runtimeUnlockTransactionCommittedEvents) |
                Where-Object {
                    [int64]$_.Sequence -gt $sentSequence -and
                    (($_.EventName -ne 'runtime_unlock_tuna_probe_ack_received') -or
                        (Get-FileTransferEventField -Event $_ -Name 'accepted' -Default '1') -ne '0') -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $sent -Candidate $_)
                }
        )
        $matchingFailure = @($runtimeUnlockPreCommitProbeFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $sentSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $sent -Candidate $_)
        })
        $matchingAckAuthorityRejected = @($runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $sentSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $sent -Candidate $_)
        })

        if ($matchingFailure.Count -eq 0 -or $matchingAckAuthorityRejected.Count -gt 0) {
            continue
        }

        $firstFailureSequence = (@($matchingFailure | Sort-Object Sequence | Select-Object -First 1))[0].Sequence
        $firstAckOrCommitSequence = if ($matchingAckOrCommit.Count -gt 0) {
            (@($matchingAckOrCommit | Sort-Object Sequence | Select-Object -First 1))[0].Sequence
        } else {
            $null
        }

        if ($null -eq $firstAckOrCommitSequence -or [int64]$firstFailureSequence -lt [int64]$firstAckOrCommitSequence) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        }
    }

    foreach ($received in $runtimeUnlockTunaProbeReceivedEvents) {
        $receivedSequence = [int64]$received.Sequence
        $matchingAckAuthorityRejected = @($runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $receivedSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $received -Candidate $_)
        })

        if ($matchingAckAuthorityRejected.Count -eq 0) {
            continue
        }

        $matchingAckOrCommit = @(
            @($runtimeUnlockTunaProbeAckSentEvents + $runtimeUnlockPreCommitProbeAckDeliveryResultEvents + $runtimeUnlockTunaProbeAckReceivedEvents + $runtimeUnlockPreCommitProbeAckedEvents + $runtimeUnlockTransactionCommittedEvents) |
                Where-Object {
                    [int64]$_.Sequence -gt $receivedSequence -and
                    (($_.EventName -ne 'runtime_unlock_tuna_probe_ack_received') -or
                        (Get-FileTransferEventField -Event $_ -Name 'accepted' -Default '1') -ne '0') -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $received -Candidate $_)
                }
        )
        $firstRejectionSequence = (@($matchingAckAuthorityRejected | Sort-Object Sequence | Select-Object -First 1))[0].Sequence
        $firstAckOrCommitSequence = if ($matchingAckOrCommit.Count -gt 0) {
            (@($matchingAckOrCommit | Sort-Object Sequence | Select-Object -First 1))[0].Sequence
        } else {
            $null
        }

        if ($null -eq $firstAckOrCommitSequence -or [int64]$firstRejectionSequence -lt [int64]$firstAckOrCommitSequence) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        }
    }

    foreach ($ackSent in $runtimeUnlockTunaProbeAckSentEvents) {
        $ackSentSequence = [int64]$ackSent.Sequence
        $matchingAckDelivery = @(
            $runtimeUnlockPreCommitProbeAckDeliveryResultEvents |
                Where-Object {
                    (Get-FileTransferEventField -Event $_ -Name 'delivery_result' -Default '') -eq 'sent' -and
                    (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'tuna' -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackSent -Candidate $_)
                }
        )
        $matchingAckReceive = @(
            @($runtimeUnlockTunaProbeAckReceivedEvents + $runtimeUnlockPreCommitProbeAckedEvents + $runtimeUnlockTransactionCommittedEvents) |
                Where-Object {
                    [int64]$_.Sequence -gt $ackSentSequence -and
                    (($_.EventName -ne 'runtime_unlock_tuna_probe_ack_received') -or
                        (Get-FileTransferEventField -Event $_ -Name 'accepted' -Default '1') -ne '0') -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackSent -Candidate $_)
                }
        )
        $matchingAckAuthorityRejected = @($runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackSentSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackSent -Candidate $_)
        })
        $matchingAckFailure = @($runtimeUnlockPreCommitProbeFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackSentSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackSent -Candidate $_)
        })
        $matchingTransactionFailure = @($runtimeUnlockTransactionFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackSentSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackSent -Candidate $_)
        })

        if ($matchingAckDelivery.Count -eq 0 -and $matchingAckReceive.Count -eq 0) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        } elseif ($matchingAckDelivery.Count -gt 0 -and
            $matchingAckReceive.Count -eq 0 -and
            $matchingAckAuthorityRejected.Count -eq 0 -and
            $matchingAckFailure.Count -eq 0 -and
            $matchingTransactionFailure.Count -eq 0) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        }
    }

    foreach ($ackDelivery in $runtimeUnlockPreCommitProbeAckDeliveryResultEvents) {
        $ackDeliverySequence = [int64]$ackDelivery.Sequence
        if ((Get-FileTransferEventField -Event $ackDelivery -Name 'delivery_result' -Default '') -ne 'sent' -or
            (Get-FileTransferEventField -Event $ackDelivery -Name 'actual_transport' -Default '') -ne 'tuna') {
            continue
        }

        $matchingAckSent = @($runtimeUnlockTunaProbeAckSentEvents | Where-Object {
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackDelivery -Candidate $_)
        })
        if ($matchingAckSent.Count -gt 0) {
            continue
        }

        $matchingAckReceive = @(
            @($runtimeUnlockTunaProbeAckReceivedEvents + $runtimeUnlockPreCommitProbeAckedEvents + $runtimeUnlockTransactionCommittedEvents) |
                Where-Object {
                    [int64]$_.Sequence -gt $ackDeliverySequence -and
                    (($_.EventName -ne 'runtime_unlock_tuna_probe_ack_received') -or
                        (Get-FileTransferEventField -Event $_ -Name 'accepted' -Default '1') -ne '0') -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackDelivery -Candidate $_)
                }
        )
        $matchingAckAuthorityRejected = @($runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackDeliverySequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackDelivery -Candidate $_)
        })
        $matchingAckFailure = @($runtimeUnlockPreCommitProbeFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackDeliverySequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackDelivery -Candidate $_)
        })
        $matchingTransactionFailure = @($runtimeUnlockTransactionFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackDeliverySequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackDelivery -Candidate $_)
        })

        if ($matchingAckReceive.Count -eq 0 -and
            $matchingAckAuthorityRejected.Count -eq 0 -and
            $matchingAckFailure.Count -eq 0 -and
            $matchingTransactionFailure.Count -eq 0) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        }
    }

    foreach ($ackReceive in $runtimeUnlockPreCommitProbeAckReceiveResultEvents) {
        $ackReceiveSequence = [int64]$ackReceive.Sequence
        if ((Get-FileTransferEventField -Event $ackReceive -Name 'delivery_result' -Default '') -ne 'received' -or
            (Get-FileTransferEventField -Event $ackReceive -Name 'actual_transport' -Default '') -ne 'tuna') {
            continue
        }

        $matchingAckObserved = @(
            @($runtimeUnlockTunaProbeAckReceivedEvents + $runtimeUnlockPreCommitProbeAckedEvents + $runtimeUnlockTransactionCommittedEvents) |
                Where-Object {
                    [int64]$_.Sequence -gt $ackReceiveSequence -and
                    (($_.EventName -ne 'runtime_unlock_tuna_probe_ack_received') -or
                        (Get-FileTransferEventField -Event $_ -Name 'accepted' -Default '1') -ne '0') -and
                    (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackReceive -Candidate $_)
                }
        )
        $matchingAckDispatchRejected = @($runtimeUnlockPreCommitProbeAckReceiveDispatchRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackReceiveSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackReceive -Candidate $_)
        })
        $matchingAckAuthorityRejected = @($runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackReceiveSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackReceive -Candidate $_)
        })
        $matchingAckFailure = @($runtimeUnlockPreCommitProbeFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackReceiveSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackReceive -Candidate $_)
        })
        $matchingTransactionFailure = @($runtimeUnlockTransactionFailedEvents | Where-Object {
            [int64]$_.Sequence -gt $ackReceiveSequence -and
            (Test-RuntimeUnlockPreCommitProbeTupleMatch -Sent $ackReceive -Candidate $_)
        })

        if ($matchingAckObserved.Count -eq 0 -and
            $matchingAckDispatchRejected.Count -eq 0 -and
            $matchingAckAuthorityRejected.Count -eq 0 -and
            $matchingAckFailure.Count -eq 0 -and
            $matchingTransactionFailure.Count -eq 0) {
            $runtimeUnlockPreCommitProbeAckMissingCount++
        }
    }

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
    $hasPostTunaFallbackEvidence = $routeChanges -contains 'post_tuna_fallback_v6'
    if (-not $hasPostTunaFallbackEvidence) {
        $hasPostTunaFallbackEvidence = @($events | Where-Object {
            (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6'
        }).Count -gt 0
    }
    $hasRegularOnlyRouteEvidence =
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast' -and
        -not $hasPostTunaFallbackEvidence
    $regularV4RepairLivenessAuthorityMissing =
        $hasRegularOnlyRouteEvidence -and
        $regularV4FrontierStallMissingRangeEvents.Count -gt 0 -and
        $regularV4RepairRequestedEvents.Count -gt 0 -and
        $regularV4FrontierRepairAuthorityGrantedEvents.Count -eq 0 -and
        ($sessionLivenessTimeoutEvents.Count -gt 0 -or $peerDisconnectedTerminalEvents.Count -gt 0)
    $regularV4FrontierRepairStalled =
        $hasRegularOnlyRouteEvidence -and
        (
            $regularV4FrontierRepairStalledEvents.Count -gt 0 -or
            (
                $regularV4FrontierRepairDispatchedEvents.Count -gt 0 -and
                $regularV4FrontierRepairProgressObservedEvents.Count -eq 0 -and
                ($sessionLivenessTimeoutEvents.Count -gt 0 -or $peerDisconnectedTerminalEvents.Count -gt 0)
            )
        )
    $runtimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedEvents = @(
        foreach ($pending in $runtimeUnlockFallbackProofReplayPendingAfterPairedYieldEvents) {
            $pendingSequence = [int64]$pending.Sequence
            $pendingTransferId = Get-FileTransferEventField -Event $pending -Name 'transfer_id' -Default ''
            $pendingSessionId = Get-FileTransferEventField -Event $pending -Name 'session_id' -Default ''
            $pendingDirection = Get-FileTransferEventField -Event $pending -Name 'direction' -Default ''
            $settled = @(
                @(
                    $runtimeUnlockFallbackQuiescentAdmissionGrantedEvents +
                    $runtimeUnlockPreCommitProbeStartedEvents +
                    $runtimeUnlockPreCommitProbePendingRejectedEvents +
                    $runtimeUnlockPreCommitProbePendingHeldEvents +
                    $runtimeUnlockTransactionCommittedEvents
                ) | Where-Object {
                    if ([int64]$_.Sequence -le $pendingSequence) {
                        return $false
                    }

                    $candidateTransferId = Get-FileTransferEventField -Event $_ -Name 'transfer_id' -Default ''
                    if (-not [string]::IsNullOrWhiteSpace($pendingTransferId) -and
                        -not [string]::Equals($candidateTransferId, $pendingTransferId, [System.StringComparison]::Ordinal)) {
                        return $false
                    }

                    $candidateSessionId = Get-FileTransferEventField -Event $_ -Name 'session_id' -Default ''
                    if (-not [string]::IsNullOrWhiteSpace($pendingSessionId) -and
                        -not [string]::IsNullOrWhiteSpace($candidateSessionId) -and
                        -not [string]::Equals($candidateSessionId, $pendingSessionId, [System.StringComparison]::Ordinal)) {
                        return $false
                    }

                    $candidateDirection = Get-FileTransferEventField -Event $_ -Name 'direction' -Default ''
                    if (-not [string]::IsNullOrWhiteSpace($pendingDirection) -and
                        -not [string]::IsNullOrWhiteSpace($candidateDirection) -and
                        -not [string]::Equals($candidateDirection, $pendingDirection, [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $false
                    }

                    return $true
                }
            )
            if ($settled.Count -eq 0) {
                $pending
            }
        }
    )
    $runtimeUnlockPostFallbackResumeBlockedEvents = @(
        foreach ($blocked in @($runtimeUnlockPreCommitProbeWaitingForTransportProofEvents + $runtimeUnlockPreCommitProbePendingHeldEvents)) {
            $blockedSequence = [int64]$blocked.Sequence
            $matchingAdmission = @(
                $runtimeUnlockFallbackQuiescentAdmissionGrantedEvents | Where-Object {
                    [int64]$_.Sequence -le $blockedSequence -and
                    (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $blocked -Effect $_)
                }
            )
            if ($matchingAdmission.Count -eq 0) {
                continue
            }

            $settled = @(
                @(
                    $runtimeUnlockPreCommitProbeStartedEvents +
                    $runtimeUnlockPreCommitProbeAckedEvents +
                    $runtimeUnlockPreCommitProbePendingRejectedEvents +
                    $runtimeUnlockPreCommitProbeFailedEvents +
                    $runtimeUnlockTransactionCommittedEvents
                ) | Where-Object {
                    Test-FileTransferRuntimeUnlockResumeSettlementMatches -Blocked $blocked -Settlement $_
                }
            )

            if ($settled.Count -eq 0) {
                $blocked
            }
        }
    )
    $postFallbackRuntimeUnlockResumeBlocked =
        $hasPostTunaFallbackEvidence -and
        (
            $runtimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedEvents.Count -gt 0 -or
            $runtimeUnlockPostFallbackResumeBlockedEvents.Count -gt 0
        )

    $runtimeUnlockHasPreCommitAckProof =
        $runtimeUnlockProbeAckedEvents.Count -gt 0 -or
        $runtimeUnlockTunaProbeAckReceivedEvents.Count -gt 0 -or
        $runtimeUnlockPreCommitProbeAckedEvents.Count -gt 0 -or
        $runtimeUnlockPreCommitRouteEpochRecoveredEvents.Count -gt 0

    $class = '(none)'
    if ($publicationAuthorityFieldCoverageProof.Verdict -eq 'fail') {
        $class = 'publication_authority_missing'
    }
    elseif ($fileTunaV4DataPathProof.Verdict -eq 'fail') {
        $class = 'file_tuna_data_path_mismatch'
    }
    elseif ($runtimeUnlockRetryDispatchProofVerdict -eq 'fail') {
        $class = 'runtime_unlock_retry_dispatch_missing'
    }
    elseif ($runtimeUnlockRetryRegularV4RecoverySettlementMissingCount -gt 0) {
        $class = 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
    }
    elseif ($regularV4FrontierRepairStalled) {
        $class = 'regular_v4_frontier_repair_stalled'
    }
    elseif ($regularV4RepairLivenessAuthorityMissing) {
        $class = 'regular_v4_repair_liveness_authority_missing'
    }
    elseif ($staleRejectedActionEffectProof.Verdict -eq 'fail') {
        $class = 'stale_rejected_action_effect'
    }
    elseif ($fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount -gt 0) {
        $class = 'stale_rejected_action_effect'
    }
    elseif ($fileTunaV6Events.Count -gt 0) {
        $class = 'active_file_tuna_v6_evidence'
    }
    elseif ($fallbackTailProof.OwnerGenerationMismatchCount -gt 0 -and
        $hasPostTunaFallbackEvidence) {
        $class = 'fallback_tail_owner_generation_mismatch'
    }
    elseif ($fallbackTailProof.FrontierRepairVerdict -eq 'fail' -and
        $hasPostTunaFallbackEvidence) {
        $class = 'fallback_frontier_repair_stalled'
    }
    elseif ($fallbackTailProof.LocalYieldBlockedCount -gt 0 -and
        $fallbackTailProof.PairedYieldVerdict -eq 'fail' -and
        $hasPostTunaFallbackEvidence) {
        $class = 'fallback_tail_local_yield_missing'
    }
    elseif ($fallbackTailProof.PairedYieldVerdict -eq 'fail' -and
        $fallbackTailProof.RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount -gt 0 -and
        $hasPostTunaFallbackEvidence) {
        $class = 'runtime_unlock_probe_rejected_by_inbound_fallback_owner'
    }
    elseif ($fallbackTailProof.PairedYieldVerdict -eq 'fail' -and
        $fallbackTailProof.QuiescentYieldGrantedCount -gt 0 -and
        $hasPostTunaFallbackEvidence) {
        $class = 'paired_fallback_tail_yield_missing'
    }
    elseif ($fallbackTailProof.QuiescenceVerdict -eq 'fail' -and
        $hasPostTunaFallbackEvidence) {
        $class = 'fallback_tail_quiescence_missing'
    }
    elseif ($fallbackRuntimeUnlockSeparationProof.Verdict -eq 'fail' -and
        $hasPostTunaFallbackEvidence) {
        $class = if ($fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount -gt 0) {
            'stale_rejected_action_effect'
        }
        else {
            'runtime_fallback_separation_violation'
        }
    }
    elseif ($fallbackTailProof.Verdict -eq 'fail') {
        $class = 'fallback_tail_reconciliation'
    }
    elseif ($postFallbackRuntimeUnlockResumeBlocked) {
        $class = if ($runtimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedEvents.Count -gt 0) {
            'fallback_proof_replay_pending_after_paired_yield'
        } else {
            'post_fallback_runtime_unlock_resume_blocked'
        }
    }
    elseif (($controlPlaneProof.StaleReplayTreadmillCount -gt $controlPlaneProof.StaleGenerationTreadmillScoreLimit -or
            $controlPlaneProof.CheckpointOwnerViolationCount -gt 0 -or
            $controlPlaneProof.ControlPlaneNonCurrentLegGenerationSendCount -gt 0) -and
        $hasPostTunaFallbackEvidence) {
        $class = 'fallback_control_plane_stale_generation_treadmill'
    }
    elseif ($hasPostTunaFallbackEvidence -and
        (
            $postTunaFallbackBulkActiveSuppressedDuringPressureEvents.Count -gt 0 -or
            (
                $postTunaFallbackControlPlanePressureMarkedEvents.Count -gt 0 -and
                $postTunaFallbackControlPlanePressureAllowedEvents.Count -eq 0 -and
                ($sessionLivenessTimeoutEvents.Count -gt 0 -or $peerDisconnectedTerminalEvents.Count -gt 0)
            ) -or
            (
                $postTunaFallbackLegacyControlLaneStarvationEvents.Count -gt 32 -and
                @($events | Where-Object {
                    $_.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed' -and
                    (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -in @('bulk_receive_active', 'filetransfer_bulk_receive_active')
                }).Count -gt 0
            )
        )) {
        $class = 'fallback_control_lane_starvation_under_bulk_receive'
    }
    elseif ($controlPlaneProof.Verdict -eq 'fail' -and
        $hasPostTunaFallbackEvidence -and
        ($sessionLivenessTimeoutEvents.Count -gt 0 -or $peerDisconnectedTerminalEvents.Count -gt 0)) {
        $class = 'fallback_control_plane_starvation'
    }
    elseif ($runtimeUnlockAnswerRejectedTunaPathLeaseUnavailableEvents.Count -gt 0 -or
        $runtimeUnlockTunaPathLeaseRouteCommitRejectedEvents.Count -gt 0) {
        $class = 'tuna_path_lease_unavailable_at_answer'
    }
    elseif ($runtimeUnlockPreCommitProbeAckMissingCount -gt 0) {
        $class = 'runtime_unlock_precommit_probe_ack_missing'
    }
    elseif ($runtimeUnlockTransactionCommittedEvents.Count -gt 0 -and
        -not $runtimeUnlockHasPreCommitAckProof) {
        $class = 'runtime_unlock_route_commit_before_probe'
    }
    elseif ($runtimeUnlockProbeStartedEvents.Count -gt 0 -and
        $runtimeUnlockPreCommitProbeStartedEvents.Count -eq 0 -and
        $runtimeUnlockPreCommitProbeProtocolMismatchEvents.Count -gt 0 -and
        -not $runtimeUnlockHasPreCommitAckProof) {
        $class = 'runtime_unlock_precommit_probe_protocol_mismatch'
    }
    elseif ($runtimeUnlockTransactionPeerProofEvents.Count -gt 0 -and
        $runtimeUnlockPreCommitProbeStartedEvents.Count -gt 0 -and
        $runtimeUnlockTunaProbeSentEvents.Count -gt 0 -and
        $runtimeUnlockTunaProbeReceivedEvents.Count -eq 0 -and
        $runtimeUnlockTunaProbeAckReceivedEvents.Count -eq 0 -and
        $runtimeUnlockTunaProbeSidecarFailureEvents.Count -gt 0 -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast' -and
        $runtimeUnlockTransactionCommittedEvents.Count -eq 0 -and
        ($cleanRegularV4TerminalEvents.Count -gt 0 -or
         $regularV4TerminalBeforeActivationEvents.Count -gt 0)) {
        $class = 'runtime_unlock_tuna_probe_sidecar_closed_regular_v4_terminal'
    }
    elseif ($runtimeUnlockListenerRearmAfterProbeCloseFailedEvents.Count -gt 0 -and
        $runtimeUnlockTunaProbeSidecarFailureEvents.Count -gt 0) {
        $class = 'runtime_unlock_tuna_listener_unavailable_after_probe_close'
    }
    elseif ($runtimeUnlockTransactionPeerProofEvents.Count -gt 0 -and
        $runtimeUnlockPreCommitProbeStartedEvents.Count -gt 0 -and
        $runtimeUnlockTunaProbeSentEvents.Count -gt 0 -and
        $runtimeUnlockTunaProbeReceivedEvents.Count -eq 0 -and
        $runtimeUnlockTunaProbeAckReceivedEvents.Count -eq 0 -and
        ($runtimeUnlockTunaProbeSidecarFailureEvents.Count -gt 0 -or
         $runtimeUnlockPreCommitProbeFailedEvents.Count -gt 0 -or
         $runtimeUnlockProbeFailedEvents.Count -gt 0)) {
        $class = 'runtime_unlock_tuna_probe_path_not_established'
    }
    elseif ($runtimeUnlockTransactionPeerProofEvents.Count -gt 0 -and
        -not $runtimeUnlockHasPreCommitAckProof -and
        ($runtimeUnlockProbeMissingRejectedEvents.Count -gt 0 -or
         $sessionLivenessTimeoutEvents.Count -gt 0 -or
         $peerDisconnectedTerminalEvents.Count -gt 0)) {
        $class = 'runtime_unlock_offer_answer_without_probe'
    }
    elseif ($fallbackRuntimeUnlockSeparationProof.Verdict -eq 'fail' -and
        $routeChanges -contains 'post_tuna_fallback_v6') {
        $class = if ($fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount -gt 0) {
            'stale_rejected_action_effect'
        }
        else {
            'runtime_fallback_separation_violation'
        }
    }
    elseif ($runtimeUnlockProbeInvalidRejectedEvents.Count -gt 0 -or
        $runtimeUnlockProbeFailedEvents.Count -gt 0) {
        $class = 'runtime_unlock_probe_failed'
    }
    elseif ($runtimeUnlockRegularV4PeerVisibleWindowDeliveryStartedEvents.Count -gt 0 -and
        $runtimeUnlockRegularV4ActivationWindowPeerVisibleEvents.Count -eq 0 -and
        ($runtimeUnlockRegularV4PeerVisibleWindowDeliveryFailedEvents.Count -gt 0 -or
         $runtimeUnlockRegularV4ActivationWindowFailedEvents.Count -gt 0) -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast' -and
        $runtimeUnlockTransactionCommittedEvents.Count -eq 0) {
        $class = 'runtime_unlock_regular_v4_peer_visible_window_delivery_failed'
    }
    elseif ($runtimeUnlockRegularV4ActivationWindowStartedEvents.Count -gt 0 -and
        $runtimeUnlockTransactionObservedSendEvents.Count -gt 0 -and
        $runtimeUnlockRegularV4ActivationWindowPeerVisibleEvents.Count -eq 0 -and
        ($runtimeUnlockRegularV4ActivationWindowFailedEvents.Count -gt 0 -or
         $runtimeUnlockRegularV4PeerVisibleWindowLateProofIgnoredEvents.Count -gt 0) -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast' -and
        $runtimeUnlockTransactionCommittedEvents.Count -eq 0) {
        $class = 'runtime_unlock_peer_visible_window_race'
    }
    elseif (($runtimeUnlockRegularV4ActivationWindowFailedEvents.Count -gt 0 -or
            $runtimeUnlockDispatchDeferredForRegularV4RecoveryEvents.Count -gt 0 -or
            (
                $runtimeUnlockBrokenStateRetrySuppressedEvents.Count -gt 0 -and
                $regularV4FrontierStallMissingRangeEvents.Count -gt 0
            )) -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast' -and
        $runtimeUnlockTransactionCommittedEvents.Count -eq 0 -and
        -not $runtimeUnlockHasPreCommitAckProof -and
        ($sessionLivenessTimeoutEvents.Count -gt 0 -or
         $peerDisconnectedTerminalEvents.Count -gt 0 -or
         $runtimeUnlockTransactionFailedEvents.Count -gt 0)) {
        $class = 'runtime_unlock_regular_v4_recovery_arbitration'
    }
    elseif ($runtimeUnlockBrokenStateRetrySuppressedEvents.Count -gt 0 -or
        $runtimeUnlockBrokenStateTransactionFailedEvents.Count -gt 0 -or
        ($runtimeUnlockBrokenStateFreshRetryConsumedEvents.Count -gt 0 -and
         $runtimeUnlockOfferNotObservedEvents.Count -gt 1 -and
         $runtimeUnlockTransactionPeerProofEvents.Count -eq 0)) {
        $class = 'runtime_unlock_broken_state_retry_churn'
    }
    elseif ($runtimeUnlockTransactionObservedSendEvents.Count -gt 0 -and
        $runtimeUnlockTransactionPeerProofEvents.Count -eq 0 -and
        ($runtimeUnlockTransactionLocalOnlyRejectedEvents.Count -gt 0 -or $sessionLivenessTimeoutEvents.Count -gt 0)) {
        $class = 'runtime_unlock_transaction_local_only'
    }
    elseif ($runtimeUnlockTransactionCommitPendingEvents.Count -gt $runtimeUnlockTransactionCommittedEvents.Count -and
        ($sessionLivenessTimeoutEvents.Count -gt 0 -or $runtimeUnlockTransactionFailedEvents.Count -gt 0)) {
        $class = 'runtime_unlock_transaction_commit_missing'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0 -and
        $sessionRecoveryContractRetryDispatchedEvents.Count -gt 0 -and
        $sessionRecoveryContractRetryAuthorityObservedEvents.Count -gt 0 -and
        $sessionLivenessTimeoutEvents.Count -eq 0 -and
        $peerDisconnectedTerminalEvents.Count -eq 0) {
        $class = '(none)'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0 -and
        $runtimeUnlockOfferPeerResponseTimeoutEvents.Count -gt 0 -and
        $sessionRecoveryContractRetryDispatchedEvents.Count -gt 0 -and
        $sessionRecoveryContractRetryAuthorityObservedEvents.Count -gt 0 -and
        $runtimeUnlockCutThroughPeerReceivedEvents.Count -eq 0 -and
        $sessionLivenessTimeoutEvents.Count -gt 0 -and
        $peerDisconnectedTerminalEvents.Count -gt 0 -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast') {
        $class = 'runtime_unlock_peer_response_not_received_under_regular_v4_recovery'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0 -and
        $sessionRecoveryContractRetryDispatchedEvents.Count -gt 0 -and
        ($runtimeUnlockOfferRejectedWithoutObservationEvents.Count -gt 0 -or $runtimeUnlockReceiveRecoveryBlockedOfferEvents.Count -gt 0) -and
        $sessionLivenessTimeoutEvents.Count -gt 0 -and
        $peerDisconnectedTerminalEvents.Count -gt 0 -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast') {
        $class = 'runtime_unlock_offer_observation_blocked_by_receive_recovery'
    }
    elseif ($runtimeUnlockDispatchDeferredForRegularV4RecoveryEvents.Count -gt 0 -and
        $sessionLivenessTimeoutEvents.Count -gt 0 -and
        $peerDisconnectedTerminalEvents.Count -gt 0 -and
        $routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast') {
        $class = 'runtime_unlock_dispatch_deferred_by_regular_v4_receive_recovery'
    }
    elseif ($runtimeUnlockOfferNotObservedEvents.Count -gt 0 -and
        $listenerRearmRequiredEvents.Count -gt 0 -and
        $listenerRearmCompletedEvents.Count -eq 0 -and
        ($listenerRearmFailedEvents.Count -gt 0 -or $listenerReadyUnavailableContradictionEvents.Count -gt 0) -and
        ($sessionLivenessTimeoutEvents.Count -gt 0 -or $peerDisconnectedTerminalEvents.Count -gt 0)) {
        $class = 'tuna_listener_rearm_coordination'
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

    $regularV4EfficiencyPressureClass = if ($routeChanges.Count -eq 1 -and
        $routeChanges[0] -eq 'regular_nkn_v4_fast' -and
        (
            $regularV4FrontierRebindEvents.Count -gt 3 -or
            ($regularV4FrontierRebindEvents.Count -gt 0 -and $regularV4RepairSkippedNotYetSentEvents.Count -gt 0) -or
            (
                $regularV4FrontierRebindEvents.Count -gt 0 -and
                $regularV4FrontierStallMissingRangeEvents.Count -gt 0 -and
                ($regularV4RepairRequestedEvents.Count -gt 0 -or $regularV4RepairCachePressureEvents.Count -gt 0)
            )
        )) {
        'regular_v4_recovery_rewind_treadmill'
    }
    else {
        '(none)'
    }

    $runtimeUnlockHasMakeBeforeBreakEvidence =
        $runtimeUnlockTransactionStartedEvents.Count -gt 0 -or
        $runtimeUnlockPreCommitRouteEpochRecoveredEvents.Count -gt 0
    $runtimeUnlockHasMakeBeforeBreakInvalidEvidence =
        $runtimeUnlockProbeInvalidRejectedEvents.Count -gt 0 -or
        $runtimeUnlockProbeMissingRejectedEvents.Count -gt 0 -or
        $runtimeUnlockProbeFailedEvents.Count -gt 0 -or
        $runtimeUnlockPreCommitProbeFailedEvents.Count -gt 0
    $runtimeUnlockProbeCommitVerdict = if ($runtimeUnlockPreCommitProbeStartedEvents.Count -eq 0 -and
        $runtimeUnlockPairedCommitStartedEvents.Count -eq 0) {
        'none'
    } elseif ($runtimeUnlockPreCommitProbeAckMissingCount -gt 0) {
        'fail'
    } elseif ($runtimeUnlockPairedCommitConfirmedEvents.Count -gt 0 -and
        $runtimeUnlockPreCommitRouteEpochRecoveredEvents.Count -gt 0 -and
        $runtimeUnlockPairedCommitAbortedEvents.Count -eq 0) {
        'pass'
    } else {
        'fail'
    }

    $runtimeUnlockRegularV4RecoveryArbitrationVerdict = if ($runtimeUnlockRegularV4ActivationWindowStartedEvents.Count -eq 0 -and
        $runtimeUnlockDispatchDeferredForRegularV4RecoveryEvents.Count -eq 0) {
        'none'
    } elseif ($runtimeUnlockRegularV4ActivationWindowPeerVisibleEvents.Count -gt 0 -and
        $runtimeUnlockRegularV4ActivationWindowFailedEvents.Count -eq 0) {
        'pass'
    } else {
        'fail'
    }

    [pscustomobject]@{
        Class = $class
        RuntimeUnlockOfferNotObservedCount = $runtimeUnlockOfferNotObservedEvents.Count
        RuntimeUnlockRetryScheduledCount = $runtimeUnlockRetryScheduledEvents.Count
        RuntimeUnlockRetryQueuedBehindActiveNegotiationCount = $runtimeUnlockRetryQueuedBehindActiveNegotiationEvents.Count
        RuntimeUnlockRetryDispatchedCount = $sessionRecoveryContractRetryDispatchedEvents.Count
        RuntimeUnlockRetryAuthorityGrantedCount = $sessionRecoveryContractRetryAuthorityGrantedEvents.Count
        RuntimeUnlockRetryAuthorityObservedCount = $sessionRecoveryContractRetryAuthorityObservedEvents.Count
        RuntimeUnlockRetryAuthorityFailedCount = $sessionRecoveryContractRetryAuthorityFailedEvents.Count
        RuntimeUnlockRetryDispatchProofVerdict = $runtimeUnlockRetryDispatchProofVerdict
        RuntimeUnlockRetryReleaseCount = $runtimeUnlockRetryDispatchReleaseCount
        RuntimeUnlockRetryDispatchAuthorityGrantedCount = $runtimeUnlockRetryDispatchAuthorityGrantedEvents.Count
        RuntimeUnlockRetryDispatchAuthorityRejectedCount = $runtimeUnlockRetryDispatchAuthorityRejectedEvents.Count
        RuntimeUnlockRetryDispatchObservedCount = $runtimeUnlockRetryDispatchObservedEvents.Count
        RuntimeUnlockRetryDispatchMissingCount = $runtimeUnlockRetryDispatchMissingCount
        RuntimeUnlockRetryMissingAuthorityAfterReleaseCount = $runtimeUnlockRetryMissingAuthorityAfterReleaseCount
        RuntimeUnlockRetryBlockedByRegularV4RecoveryCount = $runtimeUnlockRetryBlockedByRegularV4RecoveryEvents.Count
        RuntimeUnlockRetryHeldByRegularV4RecoveryCount = $runtimeUnlockRetryHeldByRegularV4RecoveryEvents.Count
        RuntimeUnlockRetryRejectedAfterRegularV4RecoveryCount = $runtimeUnlockRetryRejectedAfterRegularV4RecoveryEvents.Count
        RuntimeUnlockRetryRegularV4RecoverySettlementMissingCount = $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount
        RuntimeUnlockBrokenStateFreshRetryConsumedCount = $runtimeUnlockBrokenStateFreshRetryConsumedEvents.Count
        RuntimeUnlockBrokenStateRetrySuppressedCount = $runtimeUnlockBrokenStateRetrySuppressedEvents.Count
        RuntimeUnlockBrokenStateTransactionFailedCount = $runtimeUnlockBrokenStateTransactionFailedEvents.Count
        RuntimeUnlockCutThroughAttemptCount = $runtimeUnlockCutThroughStartedEvents.Count
        RuntimeUnlockCutThroughPeerReceivedCount = $runtimeUnlockCutThroughPeerReceivedEvents.Count
        RuntimeUnlockCutThroughTimeoutCount = $runtimeUnlockCutThroughFailedEvents.Count
        RuntimeUnlockCutThroughVerdict = if ($runtimeUnlockCutThroughStartedEvents.Count -eq 0) {
            'none'
        } elseif ($runtimeUnlockCutThroughPeerReceivedEvents.Count -gt 0 -and $runtimeUnlockCutThroughFailedEvents.Count -eq 0) {
            'pass'
        } else {
            'fail'
        }
        RuntimeUnlockTransactionStartedCount = $runtimeUnlockTransactionStartedEvents.Count
        RuntimeUnlockTransactionObservedSendCount = $runtimeUnlockTransactionObservedSendEvents.Count
        RuntimeUnlockTransactionPeerProofCount = $runtimeUnlockTransactionPeerProofEvents.Count
        RuntimeUnlockProbeStartedCount = $runtimeUnlockProbeStartedEvents.Count
        RuntimeUnlockProbeAckedCount = $runtimeUnlockProbeAckedEvents.Count
        RuntimeUnlockProbeFailedCount = $runtimeUnlockProbeFailedEvents.Count
        RuntimeUnlockPreCommitProbeStartedCount = $runtimeUnlockPreCommitProbeStartedEvents.Count
        RuntimeUnlockPreCommitProbeSentCount = $runtimeUnlockPreCommitProbeSentEvents.Count
        RuntimeUnlockPreCommitProbeWaitingForTransportProofCount = $runtimeUnlockPreCommitProbeWaitingForTransportProofEvents.Count
        RuntimeUnlockPreCommitProbePendingHeldCount = $runtimeUnlockPreCommitProbePendingHeldEvents.Count
        RuntimeUnlockPreCommitProbeDeliveryResultCount = $runtimeUnlockPreCommitProbeDeliveryResultEvents.Count
        RuntimeUnlockPreCommitProbeAckedCount = ($runtimeUnlockPreCommitProbeAckedEvents.Count + $runtimeUnlockPreCommitRouteEpochRecoveredEvents.Count)
        RuntimeUnlockPreCommitProbeAckMissingCount = $runtimeUnlockPreCommitProbeAckMissingCount
        RuntimeUnlockPreCommitProbeAckAuthorityRejectedCount = $runtimeUnlockPreCommitProbeAckAuthorityRejectedEvents.Count
        RuntimeUnlockPreCommitProbeFailedCount = $runtimeUnlockPreCommitProbeFailedEvents.Count
        RuntimeUnlockPreCommitProbeProtocolMismatchCount = $runtimeUnlockPreCommitProbeProtocolMismatchEvents.Count
        RuntimeUnlockTunaProbeSentCount = $runtimeUnlockTunaProbeSentEvents.Count
        RuntimeUnlockTunaProbeReceivedCount = $runtimeUnlockTunaProbeReceivedEvents.Count
        RuntimeUnlockTunaProbeAckSentCount = $runtimeUnlockTunaProbeAckSentEvents.Count
        RuntimeUnlockPreCommitProbeAckDeliveryResultCount = $runtimeUnlockPreCommitProbeAckDeliveryResultEvents.Count
        RuntimeUnlockPreCommitProbeAckReceiveResultCount = $runtimeUnlockPreCommitProbeAckReceiveResultEvents.Count
        RuntimeUnlockPreCommitProbeAckReceiveQueuedCount = $runtimeUnlockPreCommitProbeAckReceiveQueuedEvents.Count
        RuntimeUnlockPreCommitProbeAckReceiveDispatchedCount = $runtimeUnlockPreCommitProbeAckReceiveDispatchedEvents.Count
        RuntimeUnlockPreCommitProbeAckReceiveDispatchRejectedCount = $runtimeUnlockPreCommitProbeAckReceiveDispatchRejectedEvents.Count
        RuntimeUnlockTunaProbeAckReceivedCount = $runtimeUnlockTunaProbeAckReceivedEvents.Count
        RuntimeUnlockFallbackQuiescentAdmissionGrantedCount = $runtimeUnlockFallbackQuiescentAdmissionGrantedEvents.Count
        RuntimeUnlockFallbackProofReplayPendingAfterPairedYieldCount = $runtimeUnlockFallbackProofReplayPendingAfterPairedYieldEvents.Count
        RuntimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedCount = $runtimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedEvents.Count
        PostFallbackRuntimeUnlockResumeBlockedCount = if ($postFallbackRuntimeUnlockResumeBlocked) { 1 } else { 0 }
        RuntimeUnlockTunaProbeSidecarFailureCount = $runtimeUnlockTunaProbeSidecarFailureEvents.Count
        RuntimeUnlockTunaProbeSidecarFreshTransactionScheduledCount = $runtimeUnlockTunaProbeSidecarFreshTransactionScheduledEvents.Count
        RuntimeUnlockTunaProbeSidecarFreshTransactionStartedCount = $runtimeUnlockTunaProbeSidecarFreshTransactionStartedEvents.Count
        RuntimeUnlockListenerRearmAfterProbeCloseFailedCount = $runtimeUnlockListenerRearmAfterProbeCloseFailedEvents.Count
        RegularV4TerminalBeforeTunaActivationCommitCount = $regularV4TerminalBeforeActivationEvents.Count
        RuntimeUnlockPairedCommitStartedCount = $runtimeUnlockPairedCommitStartedEvents.Count
        RuntimeUnlockPairedCommitConfirmedCount = $runtimeUnlockPairedCommitConfirmedEvents.Count
        RuntimeUnlockPairedCommitAbortedCount = $runtimeUnlockPairedCommitAbortedEvents.Count
        RuntimeUnlockProbeCommitVerdict = $runtimeUnlockProbeCommitVerdict
        RuntimeUnlockMakeBeforeBreakVerdict = if (-not $runtimeUnlockHasMakeBeforeBreakEvidence) {
            'none'
    } elseif ($runtimeUnlockTransactionCommittedEvents.Count -gt 0 -and
        $runtimeUnlockTransactionPeerProofEvents.Count -gt 0 -and
        $runtimeUnlockHasPreCommitAckProof -and
        $runtimeUnlockProbeInvalidRejectedEvents.Count -eq 0) {
            'pass'
        } elseif ($runtimeUnlockPreCommitRouteEpochRecoveredEvents.Count -gt 0 -and
            -not $runtimeUnlockHasMakeBeforeBreakInvalidEvidence) {
            'pass'
        } elseif ($runtimeUnlockTransactionPeerProofEvents.Count -gt 0 -or
            $runtimeUnlockProbeMissingRejectedEvents.Count -gt 0 -or
            $runtimeUnlockProbeFailedEvents.Count -gt 0) {
            'fail'
        } else {
            'fail'
        }
        RuntimeUnlockTransactionCommitPendingCount = $runtimeUnlockTransactionCommitPendingEvents.Count
        RuntimeUnlockTransactionCommittedCount = $runtimeUnlockTransactionCommittedEvents.Count
        RuntimeUnlockTransactionFailedCount = $runtimeUnlockTransactionFailedEvents.Count
        RuntimeUnlockTransactionLocalOnlyRejectedCount = $runtimeUnlockTransactionLocalOnlyRejectedEvents.Count
        RuntimeUnlockTunaPathLeaseStartedCount = $tunaPathLeaseStartedEvents.Count
        RuntimeUnlockTunaPathLeaseReadyCount = $tunaPathLeaseReadyEvents.Count
        RuntimeUnlockTunaPathLeaseFailedCount = $tunaPathLeaseFailedEvents.Count
        RuntimeUnlockTunaPathLeaseRetiredCount = $tunaPathLeaseRetiredEvents.Count
        RuntimeUnlockTunaPathLeaseStaleListenerIgnoredCount = $tunaPathLeaseStaleListenerIgnoredEvents.Count
        RuntimeUnlockAnswerRejectedTunaPathLeaseUnavailableCount = $runtimeUnlockAnswerRejectedTunaPathLeaseUnavailableEvents.Count
        RegularV4EfficiencyPressureClass = $regularV4EfficiencyPressureClass
        RegularV4FrontierRebindCount = $regularV4FrontierRebindEvents.Count
        RegularV4FrontierRebindRejectedCount = $regularV4FrontierRebindRejectedEvents.Count
        RegularV4FrontierStallMissingRangeCount = $regularV4FrontierStallMissingRangeEvents.Count
        RegularV4RepairRequestedCount = $regularV4RepairRequestedEvents.Count
        RegularV4FrontierRepairAuthorityGrantedCount = $regularV4FrontierRepairAuthorityGrantedEvents.Count
        RegularV4FrontierRepairAuthorityRejectedCount = $regularV4FrontierRepairAuthorityRejectedEvents.Count
        RegularV4FrontierRepairDispatchedCount = $regularV4FrontierRepairDispatchedEvents.Count
        RegularV4FrontierRepairProgressObservedCount = $regularV4FrontierRepairProgressObservedEvents.Count
        RegularV4FrontierRepairStalledCount = $regularV4FrontierRepairStalledEvents.Count
        RegularV4FrontierRepairVerdict = if ($regularV4FrontierRepairStalled -or $regularV4RepairLivenessAuthorityMissing) {
            'fail'
        } elseif ($regularV4FrontierRepairProgressObservedEvents.Count -gt 0 -or $regularV4FrontierRepairAuthorityRejectedEvents.Count -gt 0) {
            'pass'
        } else {
            'none'
        }
        RegularV4RepairCachePressureCount = $regularV4RepairCachePressureEvents.Count
    RuntimeUnlockTransactionProofVerdict = if (-not $runtimeUnlockHasMakeBeforeBreakEvidence) {
        'none'
    } elseif ($runtimeUnlockTransactionCommittedEvents.Count -gt 0 -and
        $runtimeUnlockTransactionPeerProofEvents.Count -gt 0 -and
        $runtimeUnlockHasPreCommitAckProof -and
        $runtimeUnlockTransactionCommitPendingEvents.Count -le $runtimeUnlockTransactionCommittedEvents.Count) {
            'pass'
        } elseif ($runtimeUnlockPreCommitRouteEpochRecoveredEvents.Count -gt 0 -and
            -not $runtimeUnlockHasMakeBeforeBreakInvalidEvidence) {
            'pass'
        } else {
            'fail'
        }
        ListenerReadyUnavailableContradictionCount = $listenerReadyUnavailableContradictionEvents.Count
        ListenerRearmRequiredCount = $listenerRearmRequiredEvents.Count
        ListenerRearmCompletedCount = $listenerRearmCompletedEvents.Count
        ListenerRearmFailedCount = $listenerRearmFailedEvents.Count
        RuntimeUnlockOfferDispatchedAfterListenerRearmCount = $runtimeUnlockOfferDispatchedAfterListenerRearmEvents.Count
        RuntimeUnlockDispatchDeferredForRegularV4RecoveryCount = $runtimeUnlockDispatchDeferredForRegularV4RecoveryEvents.Count
        RuntimeUnlockRegularV4ActivationWindowStartedCount = $runtimeUnlockRegularV4ActivationWindowStartedEvents.Count
        RuntimeUnlockRegularV4ActivationWindowPeerVisibleCount = $runtimeUnlockRegularV4ActivationWindowPeerVisibleEvents.Count
        RuntimeUnlockRegularV4ActivationWindowFailedCount = $runtimeUnlockRegularV4ActivationWindowFailedEvents.Count
        RuntimeUnlockRegularV4PeerVisibleWindowStartedCount = $runtimeUnlockRegularV4ActivationWindowStartedEvents.Count
        RuntimeUnlockRegularV4PeerVisibleWindowDeliveryStartedCount = $runtimeUnlockRegularV4PeerVisibleWindowDeliveryStartedEvents.Count
        RuntimeUnlockRegularV4PeerVisibleWindowProofCount = $runtimeUnlockRegularV4ActivationWindowPeerVisibleEvents.Count
        RuntimeUnlockRegularV4PeerVisibleWindowDeliveryFailedCount = $runtimeUnlockRegularV4PeerVisibleWindowDeliveryFailedEvents.Count
        RuntimeUnlockRegularV4PeerVisibleWindowLateProofIgnoredCount = $runtimeUnlockRegularV4PeerVisibleWindowLateProofIgnoredEvents.Count
        RuntimeUnlockRegularV4PeerVisibleWindowFailedCount = $runtimeUnlockRegularV4ActivationWindowFailedEvents.Count
        RegularV4RepairSpamCoalescedCount = $regularV4RepairSpamCoalescedEvents.Count
        PostTunaFallbackControlPlanePressureMarkedCount = $postTunaFallbackControlPlanePressureMarkedEvents.Count
        PostTunaFallbackControlPlanePressureClearedCount = $postTunaFallbackControlPlanePressureClearedEvents.Count
        PostTunaFallbackControlPlanePressureAllowedRecoveryCount = $postTunaFallbackControlPlanePressureAllowedEvents.Count
        PostTunaFallbackControlPlanePressureBudgetSuppressedCount = $postTunaFallbackControlPlanePressureBudgetSuppressedEvents.Count
        PostTunaFallbackBulkActiveSuppressedDuringPressureCount = $postTunaFallbackBulkActiveSuppressedDuringPressureEvents.Count
        PostTunaFallbackLegacyControlLaneStarvationCount = $postTunaFallbackLegacyControlLaneStarvationEvents.Count
        RuntimeUnlockRegularV4RecoveryArbitrationVerdict = $runtimeUnlockRegularV4RecoveryArbitrationVerdict
        RuntimeUnlockOfferObservationBlockedCount = $runtimeUnlockOfferRejectedWithoutObservationEvents.Count + $runtimeUnlockReceiveRecoveryBlockedOfferEvents.Count
        SessionLivenessTimeoutAfterRuntimeUnlockCount = if ($runtimeUnlockOfferNotObservedEvents.Count -gt 0) { $sessionLivenessTimeoutEvents.Count } else { 0 }
        FallbackTailReconciliationRequestedCount = $fallbackTailProof.RequestedCount
        FallbackTailReconciliationAcceptedCount = $fallbackTailProof.AcceptedCount
        FallbackTailNormalZeroCreditStateRefreshCount = $fallbackTailProof.NormalZeroCreditTailStateRefreshCount
        FallbackTailQuiescenceVerdict = $fallbackTailProof.QuiescenceVerdict
        FallbackTailProgressForcedCount = $fallbackTailProof.ProgressForcedCount
        FallbackTailProgressCoalescedCount = $fallbackTailProof.ProgressCoalescedCount
        FallbackTailTerminalProofConsumedCount = $fallbackTailProof.TerminalProofConsumedCount
        FallbackTailQuiescentYieldGrantedCount = $fallbackTailProof.QuiescentYieldGrantedCount
        FallbackTailOutboundYieldGrantedCount = $fallbackTailProof.OutboundQuiescentYieldGrantedCount
        FallbackTailInboundYieldGrantedCount = $fallbackTailProof.InboundQuiescentYieldGrantedCount
        FallbackTailYieldProofSentCount = $fallbackTailProof.YieldProofSentCount
        FallbackTailYieldProofReceivedCount = $fallbackTailProof.YieldProofReceivedCount
        FallbackTailPairedYieldGrantedCount = $fallbackTailProof.PairedYieldGrantedCount
        FallbackTailOwnerGenerationMismatchCount = $fallbackTailProof.OwnerGenerationMismatchCount
        FallbackTailLocalYieldBlockedCount = $fallbackTailProof.LocalYieldBlockedCount
        FallbackTailStaleYieldProofIgnoredCount = $fallbackTailProof.StaleYieldProofIgnoredCount
        FallbackTailPairedYieldVerdict = $fallbackTailProof.PairedYieldVerdict
        FallbackFrontierRepairAuthorityGrantedCount = $fallbackTailProof.FrontierRepairAuthorityGrantedCount
        FallbackFrontierRepairAuthorityRejectedCount = $fallbackTailProof.FrontierRepairAuthorityRejectedCount
        FallbackFrontierRepairDispatchedCount = $fallbackTailProof.FrontierRepairDispatchedCount
        FallbackFrontierRepairStalledCount = $fallbackTailProof.FrontierRepairStalledCount
        FallbackFrontierRepairProgressObservedCount = $fallbackTailProof.FrontierRepairProgressObservedCount
        FallbackFrontierRepairVerdict = $fallbackTailProof.FrontierRepairVerdict
        RuntimeUnlockProbeRejectedByFallbackOwnerCount = $fallbackTailProof.RuntimeUnlockProbeRejectedByFallbackOwnerCount
        RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount = $fallbackTailProof.RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount
        FallbackTailQuiescenceTimeoutCount = $fallbackTailProof.QuiescenceTimeoutCount
        ControlPlaneIsolationVerdict = $controlPlaneProof.Verdict
        ControlPlanePeerVisibleCount = $controlPlaneProof.PeerVisibleCount
        ControlPlaneLocalOnlyRejectedCount = $controlPlaneProof.LocalOnlyRejectedCount
        ControlPlaneCheckpointExchangeCount = $controlPlaneProof.CheckpointExchangeCount
        ControlPlaneDeliveryFailureCount = $controlPlaneProof.DeliveryFailureCount
        ControlPlaneLivenessTimeoutDuringValidExchangeCount = $controlPlaneProof.LivenessTimeoutDuringValidExchangeCount
        ControlPlaneRetiredStaleSendCount = $controlPlaneProof.RetiredStaleSendCount
        FallbackCheckpointExchangeRetiredCount = $controlPlaneProof.CheckpointExchangeRetiredCount
        FallbackCheckpointOwnerViolationCount = $controlPlaneProof.CheckpointOwnerViolationCount
        FallbackStaleDeferredReplayDiscardedCount = $controlPlaneProof.StaleDeferredReplayDiscardedCount
        FallbackCheckpointNotPendingReissueSuppressedCount = $controlPlaneProof.CheckpointNotPendingReissueSuppressedCount
        FallbackStaleProofIgnoredCount = $controlPlaneProof.StaleProofIgnoredCount
        FallbackStaleProofReplaySuppressedCount = $controlPlaneProof.StaleProofReplaySuppressedCount
        FallbackControlPlaneStaleReplayTreadmillCount = $controlPlaneProof.StaleReplayTreadmillCount
        FallbackStaleGenerationTreadmillScore = $controlPlaneProof.StaleGenerationTreadmillScore
        FallbackStaleGenerationTreadmillScoreLimit = $controlPlaneProof.StaleGenerationTreadmillScoreLimit
        FallbackCheckpointIdsByLegGeneration = $controlPlaneProof.CheckpointIdsByLegGeneration
        FallbackCheckpointIdCountMaxPerLeg = $controlPlaneProof.CheckpointIdCountMaxPerLeg
        FallbackControlPlaneNonCurrentLegGenerationSendCount = $controlPlaneProof.ControlPlaneNonCurrentLegGenerationSendCount
        StaleRejectedActionEffectIsolationVerdict = $staleRejectedActionEffectProof.Verdict
        StaleRejectedActionCount = $staleRejectedActionEffectProof.RejectedActionCount
        StaleRejectedActionForbiddenEffectCount = $staleRejectedActionEffectProof.ForbiddenEffectCount
        StaleRejectedFallbackSendEffectCount = $staleRejectedActionEffectProof.FallbackSendForbiddenEffectCount
        StaleRejectedFallbackCheckpointEffectCount = $staleRejectedActionEffectProof.FallbackCheckpointForbiddenEffectCount
        StaleRejectedFallbackReissueEffectCount = $staleRejectedActionEffectProof.FallbackReissueForbiddenEffectCount
        StaleRejectedFallbackTailYieldEffectCount = $staleRejectedActionEffectProof.FallbackTailYieldForbiddenEffectCount
        StaleRejectedFallbackPressureEffectCount = $staleRejectedActionEffectProof.FallbackPressureForbiddenEffectCount
        StaleRejectedUnknownActionCount = $staleRejectedActionEffectProof.UnknownActionCount
        FallbackRuntimeUnlockSeparationVerdict = $fallbackRuntimeUnlockSeparationProof.Verdict
        FallbackRuntimeUnlockWaitingCount = $fallbackRuntimeUnlockSeparationProof.WaitingForFallbackSurvivalCount
        RuntimeUnlockActivationWindowGrantedCount = $fallbackRuntimeUnlockSeparationProof.ActivationWindowGrantedCount
        RuntimeUnlockProbeFailedFallbackResumedCount = $fallbackRuntimeUnlockSeparationProof.ProbeFailedFallbackResumedCount
        RuntimeUnlockProbeStartedWithoutActivationWindowCount = $fallbackRuntimeUnlockSeparationProof.ProbeStartedWithoutActivationWindowCount
        RuntimeUnlockRegularNknProbeRejectedCount = $fallbackRuntimeUnlockSeparationProof.RegularNknProbeRejectedCount
        RuntimeUnlockFallbackUnavailableMarkCount = $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableMarkCount
        RuntimeUnlockFallbackUnavailableWithoutDenialCount = $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount
    }
}

function New-FileTransferInvariantLedgerObject {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$GateResult,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    $routeConsistency = $null
    if ($Summary.PSObject.Properties.Name -contains 'RouteConsistency') {
        $routeConsistency = $Summary.RouteConsistency
    }

    $routeSelectedEvents = @()
    $routeFindings = @()
    if ($null -ne $routeConsistency) {
        $routeSelectedEvents = @($routeConsistency.RouteSelectedEvents | Sort-Object Sequence)
        $routeFindings = @($routeConsistency.Findings)
    }

    $selectedRouteSequence = @(
        $routeSelectedEvents |
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

    $latestRouteEvent = if ($routeSelectedEvents.Count -gt 0) { $routeSelectedEvents[-1] } else { $null }
    $finalRoute = if ($null -ne $latestRouteEvent) {
        Get-FileTransferInvariantText -Value (Get-FileTransferEventField -Event $latestRouteEvent -Name 'route' -Default '') -Default 'unknown'
    }
    elseif ($selectedRouteChanges.Count -gt 0) {
        Get-FileTransferInvariantText -Value $selectedRouteChanges[$selectedRouteChanges.Count - 1] -Default 'unknown'
    }
    else {
        'unknown'
    }
    $protocolVersion = if ($null -ne $latestRouteEvent) {
        Get-FileTransferInvariantText -Value (Get-FileTransferEventField -Event $latestRouteEvent -Name 'protocol_version' -Default '') -Default 'unknown'
    }
    else {
        'unknown'
    }
    $runtimeProfile = if ($null -ne $latestRouteEvent) {
        Get-FileTransferInvariantText -Value (Get-FileTransferEventField -Event $latestRouteEvent -Name 'runtime_profile' -Default '') -Default 'unknown'
    }
    else {
        'unknown'
    }

    $liveRouteProof = Get-FileTransferLiveRouteEpochProof -TransferEvents $Summary.TransferEvents -Mode $LiveRouteProofMode
    $fallbackAuthorityProof = Get-FileTransferFallbackLegAuthorityProof -TransferEvents $Summary.TransferEvents
    $bridgeLivenessProof = Get-FileTransferBridgeLivenessIntegrationProof -Summary $Summary
    $controlPlaneProof = Get-FileTransferControlPlaneIsolationProof -Summary $Summary
    $staleRejectedActionEffectProof = Get-FileTransferStaleRejectedActionEffectProof -Summary $Summary
    $fallbackRuntimeUnlockSeparationProof = Get-FileTransferFallbackRuntimeUnlockSeparationProof -Summary $Summary
    $publicationAuthorityFieldCoverageProof = Get-FileTransferPublicationAuthorityFieldCoverageProof -Summary $Summary
    $fileTunaV4DataPathProof = Get-FileTransferFileTunaV4DataPathProof -Summary $Summary
    $recoveryClassification = Get-FileTransferRecoveryFailureClassification -Summary $Summary

    $shaOk = Get-FileTransferInvariantShaOk -TerminalEvents @($Summary.TerminalEvents) -GateVerdict $GateResult.Verdict
    $terminalWinner = Get-FileTransferInvariantTerminalWinner -TerminalEvents @($Summary.TerminalEvents) -ShaOk $shaOk
    $lateFrameProof = Get-FileTransferInvariantLateFrameClassification -Summary $Summary -TerminalWinner $terminalWinner -ShaOk $shaOk
    $lateFrameClassification = $lateFrameProof.Classification
    $routeLaw = Get-FileTransferInvariantRouteLawResult -Summary $Summary
    $terminalLifecycle = Get-FileTransferInvariantTerminalLifecycleResult -Summary $Summary -LateFrameClassification $lateFrameClassification
    $controlPriority = Get-FileTransferInvariantControlPriorityResult -Summary $Summary
    $runtimeOwner = Get-FileTransferInvariantRuntimeOwnerResult -Summary $Summary
    $combinedInvariant = Get-FileTransferInvariantCombinedGateResult -RouteLaw $routeLaw -TerminalLifecycle $terminalLifecycle -ControlPriority $controlPriority -RuntimeOwner $runtimeOwner -StaleRejectedActionEffect $staleRejectedActionEffectProof -FallbackRuntimeUnlockSeparation $fallbackRuntimeUnlockSeparationProof -PublicationAuthorityFieldCoverage $publicationAuthorityFieldCoverageProof -FileTunaV4DataPathProof $fileTunaV4DataPathProof

    $maxLiveEpoch = -1L
    foreach ($event in @($Summary.TransferEvents)) {
        $epoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default -1
        if ($epoch -gt $maxLiveEpoch) {
            $maxLiveEpoch = $epoch
        }
    }
    $routeEpochOwnerGeneration = if ($maxLiveEpoch -ge 0) { [string]$maxLiveEpoch } else { 'unknown' }

    $goodput = '0'
    $throughputEvents = @(Get-FileTransferEventsForSummary -Summary $Summary -Names @('filetransfer_v4_throughput_summary'))
    $maxUsefulPayloadBps = Get-FileTransferMaxField -Events $throughputEvents -FieldName 'useful_payload_bytes_per_second'
    if ($maxUsefulPayloadBps -gt 0) {
        $goodput = $maxUsefulPayloadBps.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $sourceArtifactDir = $ArtifactDir
    if ($Summary.PSObject.Properties.Name -contains 'LogFiles' -and $Summary.LogFiles.Count -gt 0) {
        $firstLogFile = [string]$Summary.LogFiles[0]
        if (-not [string]::IsNullOrWhiteSpace($firstLogFile)) {
            $sourceArtifactDir = Split-Path -Parent $firstLogFile
        }
    }

    $runtimeFamily = Get-FileTransferInvariantRuntimeFamily -Route $finalRoute -RuntimeProfile $runtimeProfile -ProtocolVersion $protocolVersion
    $observedFrameFamily = Get-FileTransferInvariantObservedFrameFamily -Summary $Summary
    $derivedFrameFamily = Get-FileTransferInvariantFrameFamily -Route $finalRoute -ProtocolVersion $protocolVersion
    $frameFamily = if ($observedFrameFamily -ne 'unknown') { $observedFrameFamily } else { $derivedFrameFamily }
    $fallbackConsumed = if (($selectedRouteChanges -contains 'post_tuna_fallback_v6') -and
        ($terminalWinner -eq 'completed' -or $fallbackAuthorityProof.CompletedCount -gt 0)) { '1' } else { '0' }
    $staleRouteWorkerActivityCount = [string]([int]$routeFindings.Count + [int]$controlPlaneProof.ControlPlaneNonCurrentLegGenerationSendCount)
    [pscustomobject][ordered]@{
        transfer_id = Get-FileTransferInvariantText -Value $Summary.TransferId -Default 'unknown'
        scenario_name = Get-FileTransferInvariantText -Value (Split-Path -Leaf $sourceArtifactDir) -Default 'unknown'
        source_artifact_dir = $sourceArtifactDir
        final_route = $finalRoute
        protocol_version = $protocolVersion
        runtime_family = $runtimeFamily
        frame_family = $frameFamily
        selected_route_changes = if ($selectedRouteChanges.Count -gt 0) { $selectedRouteChanges.ToArray() -join ',' } else { '(none)' }
        live_epoch_route_changes = if ($liveRouteProof.RouteChanges.Count -gt 0) { @($liveRouteProof.RouteChanges) -join ',' } else { '(none)' }
        route_epoch_owner_generation = $routeEpochOwnerGeneration
        current_runtime_owner = $runtimeFamily
        fallback_owner_state = Get-FileTransferInvariantFallbackOwnerState -FinalRoute $finalRoute -FallbackAuthorityProof $fallbackAuthorityProof
        runtime_unlock_transaction_owner_state = Get-FileTransferInvariantRuntimeUnlockOwnerState -RecoveryClassification $recoveryClassification
        terminal_winner = $terminalWinner
        sender_terminal_state = Get-FileTransferInvariantLatestTerminalState -Events @($Summary.OutboundTerminalEvents)
        receiver_terminal_state = Get-FileTransferInvariantLatestTerminalState -Events @($Summary.InboundTerminalEvents)
        sha_ok = $shaOk
        late_frame_classification = $lateFrameClassification
        stale_route_mismatch_count = [string]$routeFindings.Count
        fallback_consumed = $fallbackConsumed
        stale_route_worker_activity_count = $staleRouteWorkerActivityCount
        control_plane_peer_visible_proof_count = [string]$controlPlaneProof.PeerVisibleCount
        runtime_fallback_separation_verdict = $fallbackRuntimeUnlockSeparationProof.Verdict
        runtime_fallback_arbitration_verdict = $fallbackRuntimeUnlockSeparationProof.Verdict
        runtime_unlock_fallback_unavailable_without_denial_count = [string]$fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount
        control_plane_isolation_verdict = $controlPlaneProof.Verdict
        bridge_liveness_integration_verdict = $bridgeLivenessProof.Verdict
        release_route_law_verdict = $routeLaw.Verdict
        terminal_lifecycle_invariant_verdict = $terminalLifecycle.Verdict
        control_priority_invariant_verdict = $controlPriority.Verdict
        runtime_owner_invariant_verdict = $runtimeOwner.Verdict
        runtime_owner_generation = [string]$runtimeOwner.RuntimeOwnerGeneration
        terminal_generation = [string]$runtimeOwner.TerminalGeneration
        runtime_owner_stale_update_rejected_count = [string]$runtimeOwner.StaleUpdateRejectedCount
        runtime_owner_forbidden_stale_effect_count = [string]$runtimeOwner.ForbiddenStaleEffectCount
        runtime_owner_worker_retired_count = [string]$runtimeOwner.WorkerRetiredCount
        stale_rejected_action_effect_isolation_verdict = $staleRejectedActionEffectProof.Verdict
        stale_rejected_action_count = [string]$staleRejectedActionEffectProof.RejectedActionCount
        stale_rejected_action_forbidden_effect_count = [string]$staleRejectedActionEffectProof.ForbiddenEffectCount
        stale_rejected_liveness_effect_count = [string]$staleRejectedActionEffectProof.LivenessForbiddenEffectCount
        stale_rejected_repair_effect_count = [string]$staleRejectedActionEffectProof.RepairForbiddenEffectCount
        stale_rejected_progress_effect_count = [string]$staleRejectedActionEffectProof.ProgressForbiddenEffectCount
        stale_rejected_checkpoint_effect_count = [string]$staleRejectedActionEffectProof.CheckpointForbiddenEffectCount
        stale_rejected_bridge_recovery_effect_count = [string]$staleRejectedActionEffectProof.BridgeRecoveryForbiddenEffectCount
        stale_rejected_control_plane_effect_count = [string]$staleRejectedActionEffectProof.ControlPlaneForbiddenEffectCount
        stale_rejected_route_publication_effect_count = [string]$staleRejectedActionEffectProof.RoutePublicationForbiddenEffectCount
        stale_rejected_fallback_send_effect_count = [string]$staleRejectedActionEffectProof.FallbackSendForbiddenEffectCount
        stale_rejected_fallback_checkpoint_effect_count = [string]$staleRejectedActionEffectProof.FallbackCheckpointForbiddenEffectCount
        stale_rejected_fallback_reissue_effect_count = [string]$staleRejectedActionEffectProof.FallbackReissueForbiddenEffectCount
        stale_rejected_fallback_tail_yield_effect_count = [string]$staleRejectedActionEffectProof.FallbackTailYieldForbiddenEffectCount
        stale_rejected_fallback_pressure_effect_count = [string]$staleRejectedActionEffectProof.FallbackPressureForbiddenEffectCount
        stale_rejected_unknown_action_count = [string]$staleRejectedActionEffectProof.UnknownActionCount
        publication_authority_field_coverage_verdict = $publicationAuthorityFieldCoverageProof.Verdict
        publication_authority_checked_event_count = [string]$publicationAuthorityFieldCoverageProof.CheckedEventCount
        publication_authority_missing_field_count = [string]$publicationAuthorityFieldCoverageProof.MissingFieldCount
        file_tuna_v4_data_path_proof_verdict = $fileTunaV4DataPathProof.Verdict
        file_tuna_v4_intended_send_count = [string]$fileTunaV4DataPathProof.IntendedSendCount
        file_tuna_v4_tuna_send_result_count = [string]$fileTunaV4DataPathProof.ActualTunaSendCount
        file_tuna_v4_regular_nkn_send_result_count = [string]$fileTunaV4DataPathProof.ActualRegularNknSendCount
        file_tuna_v4_data_path_mismatch_count = [string]$fileTunaV4DataPathProof.MismatchCount
        file_tuna_v4_data_path_missing_actual_proof_count = [string]$fileTunaV4DataPathProof.MissingActualProofCount
        file_tuna_v4_peer_visible_tuna_payload_bytes = [string]$fileTunaV4DataPathProof.PeerVisiblePayloadBytes
        invariant_failure_class = $combinedInvariant.FailureClass
        goodput_bytes_per_second = $goodput
        recovery_failure_class = $recoveryClassification.Class
    }
}

function New-FileTransferInvariantLedgerLines {
    param([Parameter(Mandatory = $true)]$Ledger)

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($property in @($Ledger.PSObject.Properties)) {
        $value = if ($null -eq $property.Value -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { 'unknown' } else { [string]$property.Value }
        $lines.Add(("{0}={1}" -f $property.Name, $value)) | Out-Null
    }

    return $lines.ToArray()
}

function Write-FileTransferInvariantLedgerArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$GateResult,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    $ledger = New-FileTransferInvariantLedgerObject -ArtifactDir $ArtifactDir -Summary $Summary -GateResult $GateResult -LiveRouteProofMode $LiveRouteProofMode
    $ledgerLines = New-FileTransferInvariantLedgerLines -Ledger $ledger
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'phase0-invariant-ledger.txt' -Lines $ledgerLines | Out-Null
    Write-FileTransferArtifact -ArtifactDir $ArtifactDir -FileName 'filetransfer-invariant-summary.txt' -Lines $ledgerLines | Out-Null
    $ledger | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'phase0-invariant-ledger.json') -Encoding UTF8
    $ledger | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-invariant-summary.json') -Encoding UTF8
    return $ledger
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
            elseif ($text -eq 'recovered regular NKN V4 bridge queue clear overlapped the completed transfer') {
                'recovered_regular_v4_bridge_clear'
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
    $fallbackAuthorityProof = Get-FileTransferFallbackLegAuthorityProof -TransferEvents $Summary.TransferEvents
    $fallbackTailProof = Get-FileTransferFallbackTailReconciliationProof -Summary $Summary
    $bridgeLivenessProof = Get-FileTransferBridgeLivenessIntegrationProof -Summary $Summary
    $controlPlaneProof = Get-FileTransferControlPlaneIsolationProof -Summary $Summary
    $staleRejectedActionEffectProof = Get-FileTransferStaleRejectedActionEffectProof -Summary $Summary
    $fallbackRuntimeUnlockSeparationProof = Get-FileTransferFallbackRuntimeUnlockSeparationProof -Summary $Summary
    $publicationAuthorityFieldCoverageProof = Get-FileTransferPublicationAuthorityFieldCoverageProof -Summary $Summary
    $fileTunaV4DataPathProof = Get-FileTransferFileTunaV4DataPathProof -Summary $Summary
    $invariantLedger = New-FileTransferInvariantLedgerObject -ArtifactDir $ArtifactDir -Summary $Summary -GateResult $GateResult -LiveRouteProofMode $LiveRouteProofMode
    $releaseRouteLaw = Get-FileTransferInvariantRouteLawResult -Summary $Summary
    $terminalLifecycleInvariant = Get-FileTransferInvariantTerminalLifecycleResult -Summary $Summary -LateFrameClassification $invariantLedger.late_frame_classification
    $controlPriorityInvariant = Get-FileTransferInvariantControlPriorityResult -Summary $Summary
    $runtimeOwnerInvariant = Get-FileTransferInvariantRuntimeOwnerResult -Summary $Summary
    $combinedInvariant = Get-FileTransferInvariantCombinedGateResult -RouteLaw $releaseRouteLaw -TerminalLifecycle $terminalLifecycleInvariant -ControlPriority $controlPriorityInvariant -RuntimeOwner $runtimeOwnerInvariant -StaleRejectedActionEffect $staleRejectedActionEffectProof -FallbackRuntimeUnlockSeparation $fallbackRuntimeUnlockSeparationProof -PublicationAuthorityFieldCoverage $publicationAuthorityFieldCoverageProof -FileTunaV4DataPathProof $fileTunaV4DataPathProof

    $effectiveVerdict = $GateResult.Verdict
    $effectiveGateStatus = $GateResult.GateStatus
    $effectiveNextArtifact = $GateResult.NextArtifact
    $effectiveHardFailures = New-Object System.Collections.Generic.List[string]
    foreach ($failure in @($GateResult.HardFailures)) {
        $effectiveHardFailures.Add([string]$failure) | Out-Null
    }
    [object[]]$effectiveEvidenceEvents = @($GateResult.EvidenceEvents)
    $baseVerdictText = if ($null -eq $GateResult.Verdict) { '' } else { [string]$GateResult.Verdict }
    $invariantMayOwnAcceptanceFailure = $combinedInvariant.Verdict -eq 'fail' -and -not $baseVerdictText.StartsWith('INCONCLUSIVE', [System.StringComparison]::OrdinalIgnoreCase)
    if ($invariantMayOwnAcceptanceFailure -and $GateResult.GateStatus -ne 'fail') {
        $effectiveVerdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
        $effectiveGateStatus = 'fail'
        $effectiveNextArtifact = 'filetransfer-invariant-summary.txt'
        foreach ($finding in @($combinedInvariant.Findings)) {
            $effectiveHardFailures.Add(("invariant: {0}" -f $finding)) | Out-Null
        }
        $effectiveEvidenceEvents = @($combinedInvariant.EvidenceEvents + $effectiveEvidenceEvents | Select-Object -First 30)
    }

    if ($recoveryClassification.Class -eq 'runtime_unlock_retry_dispatch_missing' -and
        $effectiveGateStatus -ne 'fail') {
        $effectiveVerdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
        $effectiveGateStatus = 'fail'
        $effectiveNextArtifact = 'filetransfer-operator-verdict.txt'
        $effectiveHardFailures.Add('recovery: runtime unlock retry release missing dispatch proof') | Out-Null
    }

    $verdictLines = @(
        ("verdict={0}" -f $effectiveVerdict),
        ("gate_status={0}" -f $effectiveGateStatus),
        ("transfer_id={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.TransferId)) { '(none)' } else { $Summary.TransferId }))),
        ("next_artifact={0}" -f $effectiveNextArtifact),
        ("acceptance_failure_class={0}" -f ($(if ($invariantMayOwnAcceptanceFailure) { $combinedInvariant.FailureClass } else { 'none' }))),
        ("release_route_law_verdict={0}" -f $releaseRouteLaw.Verdict),
        ("terminal_lifecycle_invariant_verdict={0}" -f $terminalLifecycleInvariant.Verdict),
        ("control_priority_invariant_verdict={0}" -f $controlPriorityInvariant.Verdict),
        ("runtime_owner_invariant_verdict={0}" -f $runtimeOwnerInvariant.Verdict),
        ("runtime_owner_generation={0}" -f $runtimeOwnerInvariant.RuntimeOwnerGeneration),
        ("terminal_generation={0}" -f $runtimeOwnerInvariant.TerminalGeneration),
        ("runtime_owner_stale_update_rejected_count={0}" -f $runtimeOwnerInvariant.StaleUpdateRejectedCount),
        ("runtime_owner_forbidden_stale_effect_count={0}" -f $runtimeOwnerInvariant.ForbiddenStaleEffectCount),
        ("runtime_owner_worker_retired_count={0}" -f $runtimeOwnerInvariant.WorkerRetiredCount),
        ("stale_rejected_action_effect_isolation_verdict={0}" -f $staleRejectedActionEffectProof.Verdict),
        ("stale_rejected_action_count={0}" -f $staleRejectedActionEffectProof.RejectedActionCount),
        ("stale_rejected_action_forbidden_effect_count={0}" -f $staleRejectedActionEffectProof.ForbiddenEffectCount),
        ("stale_rejected_liveness_effect_count={0}" -f $staleRejectedActionEffectProof.LivenessForbiddenEffectCount),
        ("stale_rejected_repair_effect_count={0}" -f $staleRejectedActionEffectProof.RepairForbiddenEffectCount),
        ("stale_rejected_progress_effect_count={0}" -f $staleRejectedActionEffectProof.ProgressForbiddenEffectCount),
        ("stale_rejected_checkpoint_effect_count={0}" -f $staleRejectedActionEffectProof.CheckpointForbiddenEffectCount),
        ("stale_rejected_bridge_recovery_effect_count={0}" -f $staleRejectedActionEffectProof.BridgeRecoveryForbiddenEffectCount),
        ("stale_rejected_control_plane_effect_count={0}" -f $staleRejectedActionEffectProof.ControlPlaneForbiddenEffectCount),
        ("stale_rejected_route_publication_effect_count={0}" -f $staleRejectedActionEffectProof.RoutePublicationForbiddenEffectCount),
        ("stale_rejected_fallback_send_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackSendForbiddenEffectCount),
        ("stale_rejected_fallback_checkpoint_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackCheckpointForbiddenEffectCount),
        ("stale_rejected_fallback_reissue_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackReissueForbiddenEffectCount),
        ("stale_rejected_fallback_tail_yield_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackTailYieldForbiddenEffectCount),
        ("stale_rejected_fallback_pressure_effect_count={0}" -f $staleRejectedActionEffectProof.FallbackPressureForbiddenEffectCount),
        ("stale_rejected_unknown_action_count={0}" -f $staleRejectedActionEffectProof.UnknownActionCount),
        ("publication_authority_field_coverage_verdict={0}" -f $publicationAuthorityFieldCoverageProof.Verdict),
        ("publication_authority_checked_event_count={0}" -f $publicationAuthorityFieldCoverageProof.CheckedEventCount),
        ("publication_authority_missing_field_count={0}" -f $publicationAuthorityFieldCoverageProof.MissingFieldCount),
        ("file_tuna_v4_data_path_proof_verdict={0}" -f $fileTunaV4DataPathProof.Verdict),
        ("file_tuna_v4_intended_send_count={0}" -f $fileTunaV4DataPathProof.IntendedSendCount),
        ("file_tuna_v4_tuna_send_result_count={0}" -f $fileTunaV4DataPathProof.ActualTunaSendCount),
        ("file_tuna_v4_regular_nkn_send_result_count={0}" -f $fileTunaV4DataPathProof.ActualRegularNknSendCount),
        ("file_tuna_v4_data_path_mismatch_count={0}" -f $fileTunaV4DataPathProof.MismatchCount),
        ("file_tuna_v4_data_path_missing_actual_proof_count={0}" -f $fileTunaV4DataPathProof.MissingActualProofCount),
        ("file_tuna_v4_peer_visible_tuna_payload_bytes={0}" -f $fileTunaV4DataPathProof.PeerVisiblePayloadBytes),
        ("invariant_failure_class={0}" -f $combinedInvariant.FailureClass),
        ("fallback_consumed={0}" -f $invariantLedger.fallback_consumed),
        ("stale_route_worker_activity_count={0}" -f $invariantLedger.stale_route_worker_activity_count),
        ("control_plane_peer_visible_proof_count={0}" -f $invariantLedger.control_plane_peer_visible_proof_count),
        ("late_frame_classification={0}" -f $invariantLedger.late_frame_classification),
        ("recovery_failure_class={0}" -f $recoveryClassification.Class),
        ("runtime_unlock_offer_not_observed_count={0}" -f $recoveryClassification.RuntimeUnlockOfferNotObservedCount),
        ("runtime_unlock_retry_scheduled_count={0}" -f $recoveryClassification.RuntimeUnlockRetryScheduledCount),
        ("runtime_unlock_retry_queued_behind_active_negotiation_count={0}" -f $recoveryClassification.RuntimeUnlockRetryQueuedBehindActiveNegotiationCount),
        ("runtime_unlock_retry_dispatched_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchedCount),
        ("runtime_unlock_retry_authority_granted_count={0}" -f $recoveryClassification.RuntimeUnlockRetryAuthorityGrantedCount),
        ("runtime_unlock_retry_authority_observed_count={0}" -f $recoveryClassification.RuntimeUnlockRetryAuthorityObservedCount),
        ("runtime_unlock_retry_authority_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRetryAuthorityFailedCount),
        ("runtime_unlock_retry_dispatch_proof_verdict={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchProofVerdict),
        ("runtime_unlock_retry_release_count={0}" -f $recoveryClassification.RuntimeUnlockRetryReleaseCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchAuthorityGrantedCount),
        ("runtime_unlock_retry_dispatch_authority_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchAuthorityRejectedCount),
        ("runtime_unlock_retry_dispatch_observed_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchObservedCount),
        ("runtime_unlock_retry_dispatch_missing_count={0}" -f $recoveryClassification.RuntimeUnlockRetryDispatchMissingCount),
        ("runtime_unlock_retry_missing_authority_after_release_count={0}" -f $recoveryClassification.RuntimeUnlockRetryMissingAuthorityAfterReleaseCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_rejected_after_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockRetryRejectedAfterRegularV4RecoveryCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $recoveryClassification.RuntimeUnlockRetryRegularV4RecoverySettlementMissingCount),
        ("runtime_unlock_broken_state_fresh_retry_consumed_count={0}" -f $recoveryClassification.RuntimeUnlockBrokenStateFreshRetryConsumedCount),
        ("runtime_unlock_broken_state_retry_suppressed_count={0}" -f $recoveryClassification.RuntimeUnlockBrokenStateRetrySuppressedCount),
        ("runtime_unlock_broken_state_transaction_failed_count={0}" -f $recoveryClassification.RuntimeUnlockBrokenStateTransactionFailedCount),
        ("runtime_unlock_cutthrough_attempt_count={0}" -f $recoveryClassification.RuntimeUnlockCutThroughAttemptCount),
        ("runtime_unlock_cutthrough_peer_received_count={0}" -f $recoveryClassification.RuntimeUnlockCutThroughPeerReceivedCount),
        ("runtime_unlock_cutthrough_timeout_count={0}" -f $recoveryClassification.RuntimeUnlockCutThroughTimeoutCount),
        ("runtime_unlock_cutthrough_verdict={0}" -f $recoveryClassification.RuntimeUnlockCutThroughVerdict),
        ("runtime_unlock_transaction_started_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionStartedCount),
        ("runtime_unlock_transaction_observed_send_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionObservedSendCount),
        ("runtime_unlock_transaction_peer_proof_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionPeerProofCount),
        ("runtime_unlock_probe_started_count={0}" -f $recoveryClassification.RuntimeUnlockProbeStartedCount),
        ("runtime_unlock_probe_acked_count={0}" -f $recoveryClassification.RuntimeUnlockProbeAckedCount),
        ("runtime_unlock_probe_failed_count={0}" -f $recoveryClassification.RuntimeUnlockProbeFailedCount),
        ("runtime_unlock_precommit_probe_started_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeStartedCount),
        ("runtime_unlock_precommit_probe_sent_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeSentCount),
        ("runtime_unlock_precommit_probe_waiting_for_transport_proof_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeWaitingForTransportProofCount),
        ("runtime_unlock_precommit_probe_pending_held_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbePendingHeldCount),
        ("runtime_unlock_precommit_probe_acked_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckedCount),
        ("runtime_unlock_precommit_probe_ack_missing_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckMissingCount),
        ("runtime_unlock_precommit_probe_ack_authority_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckAuthorityRejectedCount),
        ("runtime_unlock_precommit_probe_failed_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeFailedCount),
        ("runtime_unlock_precommit_probe_protocol_mismatch_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeProtocolMismatchCount),
        ("runtime_unlock_precommit_probe_delivery_result_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeDeliveryResultCount),
        ("runtime_unlock_tuna_probe_sent_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSentCount),
        ("runtime_unlock_tuna_probe_received_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeReceivedCount),
        ("runtime_unlock_tuna_probe_ack_sent_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeAckSentCount),
        ("runtime_unlock_precommit_probe_ack_delivery_result_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckDeliveryResultCount),
        ("runtime_unlock_precommit_probe_ack_receive_result_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveResultCount),
        ("runtime_unlock_precommit_probe_ack_receive_queued_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveQueuedCount),
        ("runtime_unlock_precommit_probe_ack_receive_dispatched_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveDispatchedCount),
        ("runtime_unlock_precommit_probe_ack_receive_dispatch_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockPreCommitProbeAckReceiveDispatchRejectedCount),
        ("runtime_unlock_tuna_probe_ack_received_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeAckReceivedCount),
        ("runtime_unlock_fallback_quiescent_admission_granted_count={0}" -f $recoveryClassification.RuntimeUnlockFallbackQuiescentAdmissionGrantedCount),
        ("runtime_unlock_fallback_proof_replay_pending_after_paired_yield_count={0}" -f $recoveryClassification.RuntimeUnlockFallbackProofReplayPendingAfterPairedYieldCount),
        ("runtime_unlock_fallback_proof_replay_pending_after_paired_yield_unresolved_count={0}" -f $recoveryClassification.RuntimeUnlockFallbackProofReplayPendingAfterPairedYieldUnresolvedCount),
        ("post_fallback_runtime_unlock_resume_blocked_count={0}" -f $recoveryClassification.PostFallbackRuntimeUnlockResumeBlockedCount),
        ("runtime_unlock_tuna_probe_sidecar_failure_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSidecarFailureCount),
        ("runtime_unlock_tuna_probe_sidecar_fresh_transaction_scheduled_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSidecarFreshTransactionScheduledCount),
        ("runtime_unlock_tuna_probe_sidecar_fresh_transaction_started_count={0}" -f $recoveryClassification.RuntimeUnlockTunaProbeSidecarFreshTransactionStartedCount),
        ("runtime_unlock_tuna_listener_rearm_after_probe_close_failed_count={0}" -f $recoveryClassification.RuntimeUnlockListenerRearmAfterProbeCloseFailedCount),
        ("regular_v4_terminal_before_tuna_activation_commit_count={0}" -f $recoveryClassification.RegularV4TerminalBeforeTunaActivationCommitCount),
        ("runtime_unlock_paired_commit_started_count={0}" -f $recoveryClassification.RuntimeUnlockPairedCommitStartedCount),
        ("runtime_unlock_paired_commit_confirmed_count={0}" -f $recoveryClassification.RuntimeUnlockPairedCommitConfirmedCount),
        ("runtime_unlock_paired_commit_aborted_count={0}" -f $recoveryClassification.RuntimeUnlockPairedCommitAbortedCount),
        ("runtime_unlock_probe_commit_verdict={0}" -f $recoveryClassification.RuntimeUnlockProbeCommitVerdict),
        ("runtime_unlock_make_before_break_verdict={0}" -f $recoveryClassification.RuntimeUnlockMakeBeforeBreakVerdict),
        ("fallback_runtime_unlock_separation_verdict={0}" -f $fallbackRuntimeUnlockSeparationProof.Verdict),
        ("runtime_fallback_separation_verdict={0}" -f $fallbackRuntimeUnlockSeparationProof.Verdict),
        ("runtime_fallback_arbitration_verdict={0}" -f $fallbackRuntimeUnlockSeparationProof.Verdict),
        ("fallback_runtime_unlock_waiting_count={0}" -f $fallbackRuntimeUnlockSeparationProof.WaitingForFallbackSurvivalCount),
        ("runtime_unlock_activation_window_granted_count={0}" -f $fallbackRuntimeUnlockSeparationProof.ActivationWindowGrantedCount),
        ("runtime_unlock_probe_failed_fallback_resumed_count={0}" -f $fallbackRuntimeUnlockSeparationProof.ProbeFailedFallbackResumedCount),
        ("runtime_unlock_probe_started_without_activation_window_count={0}" -f $fallbackRuntimeUnlockSeparationProof.ProbeStartedWithoutActivationWindowCount),
        ("runtime_unlock_regular_nkn_probe_rejected_count={0}" -f $fallbackRuntimeUnlockSeparationProof.RegularNknProbeRejectedCount),
        ("runtime_unlock_fallback_unavailable_mark_count={0}" -f $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableMarkCount),
        ("runtime_unlock_fallback_unavailable_suppressed_count={0}" -f $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableSuppressedCount),
        ("runtime_unlock_fallback_unavailable_without_denial_count={0}" -f $fallbackRuntimeUnlockSeparationProof.FallbackUnavailableWithoutCoordinatorDenialCount),
        ("runtime_unlock_transaction_commit_pending_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionCommitPendingCount),
        ("runtime_unlock_transaction_committed_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionCommittedCount),
        ("runtime_unlock_transaction_failed_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionFailedCount),
        ("runtime_unlock_transaction_local_only_rejected_count={0}" -f $recoveryClassification.RuntimeUnlockTransactionLocalOnlyRejectedCount),
        ("runtime_unlock_transaction_proof_verdict={0}" -f $recoveryClassification.RuntimeUnlockTransactionProofVerdict),
        ("runtime_unlock_tuna_path_lease_started_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseStartedCount),
        ("runtime_unlock_tuna_path_lease_ready_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseReadyCount),
        ("runtime_unlock_tuna_path_lease_failed_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseFailedCount),
        ("runtime_unlock_tuna_path_lease_retired_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseRetiredCount),
        ("runtime_unlock_tuna_path_lease_stale_listener_ignored_count={0}" -f $recoveryClassification.RuntimeUnlockTunaPathLeaseStaleListenerIgnoredCount),
        ("runtime_unlock_answer_rejected_tuna_path_lease_unavailable_count={0}" -f $recoveryClassification.RuntimeUnlockAnswerRejectedTunaPathLeaseUnavailableCount),
        ("regular_v4_efficiency_pressure_class={0}" -f $recoveryClassification.RegularV4EfficiencyPressureClass),
        ("regular_v4_frontier_rebind_count={0}" -f $recoveryClassification.RegularV4FrontierRebindCount),
        ("regular_v4_frontier_rebind_rejected_count={0}" -f $recoveryClassification.RegularV4FrontierRebindRejectedCount),
        ("regular_v4_frontier_stall_missing_range_count={0}" -f $recoveryClassification.RegularV4FrontierStallMissingRangeCount),
        ("regular_v4_repair_requested_count={0}" -f $recoveryClassification.RegularV4RepairRequestedCount),
        ("regular_v4_frontier_repair_authority_granted_count={0}" -f $recoveryClassification.RegularV4FrontierRepairAuthorityGrantedCount),
        ("regular_v4_frontier_repair_authority_rejected_count={0}" -f $recoveryClassification.RegularV4FrontierRepairAuthorityRejectedCount),
        ("regular_v4_frontier_repair_dispatched_count={0}" -f $recoveryClassification.RegularV4FrontierRepairDispatchedCount),
        ("regular_v4_frontier_repair_progress_observed_count={0}" -f $recoveryClassification.RegularV4FrontierRepairProgressObservedCount),
        ("regular_v4_frontier_repair_stalled_count={0}" -f $recoveryClassification.RegularV4FrontierRepairStalledCount),
        ("regular_v4_frontier_repair_verdict={0}" -f $recoveryClassification.RegularV4FrontierRepairVerdict),
        ("regular_v4_repair_cache_pressure_count={0}" -f $recoveryClassification.RegularV4RepairCachePressureCount),
        ("listener_ready_unavailable_contradiction_count={0}" -f $recoveryClassification.ListenerReadyUnavailableContradictionCount),
        ("listener_rearm_required_count={0}" -f $recoveryClassification.ListenerRearmRequiredCount),
        ("listener_rearm_completed_count={0}" -f $recoveryClassification.ListenerRearmCompletedCount),
        ("listener_rearm_failed_count={0}" -f $recoveryClassification.ListenerRearmFailedCount),
        ("runtime_unlock_offer_dispatched_after_listener_rearm_count={0}" -f $recoveryClassification.RuntimeUnlockOfferDispatchedAfterListenerRearmCount),
        ("runtime_unlock_dispatch_deferred_for_regular_v4_recovery_count={0}" -f $recoveryClassification.RuntimeUnlockDispatchDeferredForRegularV4RecoveryCount),
        ("runtime_unlock_regular_v4_activation_window_started_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4ActivationWindowStartedCount),
        ("runtime_unlock_regular_v4_activation_window_peer_visible_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4ActivationWindowPeerVisibleCount),
        ("runtime_unlock_regular_v4_activation_window_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4ActivationWindowFailedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_started_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowStartedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_delivery_started_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowDeliveryStartedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_proof_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowProofCount),
        ("runtime_unlock_regular_v4_peer_visible_window_delivery_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowDeliveryFailedCount),
        ("runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowLateProofIgnoredCount),
        ("runtime_unlock_regular_v4_peer_visible_window_failed_count={0}" -f $recoveryClassification.RuntimeUnlockRegularV4PeerVisibleWindowFailedCount),
        ("regular_v4_repair_spam_coalesced_count={0}" -f $recoveryClassification.RegularV4RepairSpamCoalescedCount),
        ("post_tuna_fallback_control_plane_pressure_marked_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureMarkedCount),
        ("post_tuna_fallback_control_plane_pressure_cleared_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureClearedCount),
        ("post_tuna_fallback_control_plane_pressure_allowed_recovery_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureAllowedRecoveryCount),
        ("post_tuna_fallback_control_plane_pressure_budget_suppressed_count={0}" -f $recoveryClassification.PostTunaFallbackControlPlanePressureBudgetSuppressedCount),
        ("post_tuna_fallback_bulk_active_suppressed_during_pressure_count={0}" -f $recoveryClassification.PostTunaFallbackBulkActiveSuppressedDuringPressureCount),
        ("post_tuna_fallback_legacy_control_lane_starvation_count={0}" -f $recoveryClassification.PostTunaFallbackLegacyControlLaneStarvationCount),
        ("runtime_unlock_regular_v4_recovery_arbitration_verdict={0}" -f $recoveryClassification.RuntimeUnlockRegularV4RecoveryArbitrationVerdict),
        ("runtime_unlock_offer_observation_blocked_count={0}" -f $recoveryClassification.RuntimeUnlockOfferObservationBlockedCount),
        ("session_liveness_timeout_after_runtime_unlock_count={0}" -f $recoveryClassification.SessionLivenessTimeoutAfterRuntimeUnlockCount),
        ("classification_fallback_tail_reconciliation_requested_count={0}" -f $recoveryClassification.FallbackTailReconciliationRequestedCount),
        ("classification_fallback_tail_reconciliation_accepted_count={0}" -f $recoveryClassification.FallbackTailReconciliationAcceptedCount),
        ("classification_fallback_tail_normal_zero_credit_state_refresh_count={0}" -f $recoveryClassification.FallbackTailNormalZeroCreditStateRefreshCount),
        ("classification_fallback_tail_local_yield_blocked_count={0}" -f $recoveryClassification.FallbackTailLocalYieldBlockedCount),
        ("classification_fallback_frontier_repair_authority_granted_count={0}" -f $recoveryClassification.FallbackFrontierRepairAuthorityGrantedCount),
        ("classification_fallback_frontier_repair_authority_rejected_count={0}" -f $recoveryClassification.FallbackFrontierRepairAuthorityRejectedCount),
        ("classification_fallback_frontier_repair_dispatched_count={0}" -f $recoveryClassification.FallbackFrontierRepairDispatchedCount),
        ("classification_fallback_frontier_repair_stalled_count={0}" -f $recoveryClassification.FallbackFrontierRepairStalledCount),
        ("classification_fallback_frontier_repair_progress_observed_count={0}" -f $recoveryClassification.FallbackFrontierRepairProgressObservedCount),
        ("classification_fallback_frontier_repair_verdict={0}" -f $recoveryClassification.FallbackFrontierRepairVerdict),
        ("classification_control_plane_isolation_verdict={0}" -f $recoveryClassification.ControlPlaneIsolationVerdict),
        ("classification_control_plane_peer_visible_count={0}" -f $recoveryClassification.ControlPlanePeerVisibleCount),
        ("classification_control_plane_local_only_rejected_count={0}" -f $recoveryClassification.ControlPlaneLocalOnlyRejectedCount),
        ("classification_control_plane_checkpoint_exchange_count={0}" -f $recoveryClassification.ControlPlaneCheckpointExchangeCount),
        ("classification_control_plane_delivery_failure_count={0}" -f $recoveryClassification.ControlPlaneDeliveryFailureCount),
        ("classification_control_plane_liveness_timeout_during_valid_exchange_count={0}" -f $recoveryClassification.ControlPlaneLivenessTimeoutDuringValidExchangeCount),
        ("classification_control_plane_stale_send_retired_count={0}" -f $recoveryClassification.ControlPlaneRetiredStaleSendCount),
        ("classification_fallback_checkpoint_exchange_retired_count={0}" -f $recoveryClassification.FallbackCheckpointExchangeRetiredCount),
        ("classification_fallback_checkpoint_owner_violation_count={0}" -f $recoveryClassification.FallbackCheckpointOwnerViolationCount),
        ("classification_fallback_stale_deferred_replay_discarded_count={0}" -f $recoveryClassification.FallbackStaleDeferredReplayDiscardedCount),
        ("classification_fallback_checkpoint_not_pending_reissue_suppressed_count={0}" -f $recoveryClassification.FallbackCheckpointNotPendingReissueSuppressedCount),
        ("classification_fallback_stale_proof_ignored_count={0}" -f $recoveryClassification.FallbackStaleProofIgnoredCount),
        ("classification_fallback_stale_proof_replay_suppressed_count={0}" -f $recoveryClassification.FallbackStaleProofReplaySuppressedCount),
        ("classification_fallback_control_plane_stale_replay_treadmill_count={0}" -f $recoveryClassification.FallbackControlPlaneStaleReplayTreadmillCount),
        ("classification_fallback_stale_generation_treadmill_score={0}" -f $recoveryClassification.FallbackStaleGenerationTreadmillScore),
        ("classification_fallback_stale_generation_treadmill_score_limit={0}" -f $recoveryClassification.FallbackStaleGenerationTreadmillScoreLimit),
        ("classification_fallback_checkpoint_ids_by_leg_generation={0}" -f $recoveryClassification.FallbackCheckpointIdsByLegGeneration),
        ("classification_fallback_checkpoint_id_count_max_per_leg={0}" -f $recoveryClassification.FallbackCheckpointIdCountMaxPerLeg),
        ("classification_fallback_control_plane_non_current_leg_generation_send_count={0}" -f $recoveryClassification.FallbackControlPlaneNonCurrentLegGenerationSendCount),
        ("classification_stale_rejected_action_effect_isolation_verdict={0}" -f $recoveryClassification.StaleRejectedActionEffectIsolationVerdict),
        ("classification_stale_rejected_action_count={0}" -f $recoveryClassification.StaleRejectedActionCount),
        ("classification_stale_rejected_action_forbidden_effect_count={0}" -f $recoveryClassification.StaleRejectedActionForbiddenEffectCount),
        ("classification_stale_rejected_fallback_send_effect_count={0}" -f $recoveryClassification.StaleRejectedFallbackSendEffectCount),
        ("classification_stale_rejected_fallback_checkpoint_effect_count={0}" -f $recoveryClassification.StaleRejectedFallbackCheckpointEffectCount),
        ("classification_stale_rejected_fallback_reissue_effect_count={0}" -f $recoveryClassification.StaleRejectedFallbackReissueEffectCount),
        ("classification_stale_rejected_fallback_tail_yield_effect_count={0}" -f $recoveryClassification.StaleRejectedFallbackTailYieldEffectCount),
        ("classification_stale_rejected_fallback_pressure_effect_count={0}" -f $recoveryClassification.StaleRejectedFallbackPressureEffectCount),
        ("classification_stale_rejected_unknown_action_count={0}" -f $recoveryClassification.StaleRejectedUnknownActionCount),
        ("observed_start_utc={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.FirstTimestamp)) { '(unknown)' } else { $Summary.FirstTimestamp }))),
        ("observed_end_utc={0}" -f ($(if ([string]::IsNullOrWhiteSpace($Summary.LastTimestamp)) { '(unknown)' } else { $Summary.LastTimestamp }))),
        ("analyzed_files={0}" -f $analyzedFiles),
        ("hard_failure_count={0}" -f $effectiveHardFailures.Count),
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
        ("fallback_leg_authority_proof_verdict={0}" -f $fallbackAuthorityProof.Verdict),
        ("clean_fallback_leg_proof_verdict={0}" -f $fallbackAuthorityProof.Verdict),
        ("fallback_leg_authority_started_count={0}" -f $fallbackAuthorityProof.StartedCount),
        ("fallback_leg_authority_checkpoint_accepted_count={0}" -f $fallbackAuthorityProof.CheckpointAcceptedCount),
        ("fallback_leg_authority_bridge_recovery_requested_count={0}" -f $fallbackAuthorityProof.BridgeRecoveryRequestedCount),
        ("fallback_leg_authority_bridge_recovery_escalated_count={0}" -f $fallbackAuthorityProof.BridgeRecoveryEscalatedCount),
        ("fallback_leg_authority_completed_count={0}" -f $fallbackAuthorityProof.CompletedCount),
        ("fallback_leg_authority_continuous_receiver_state_proof_count={0}" -f $fallbackAuthorityProof.ContinuousReceiverStateProofCount),
        ("fallback_leg_authority_stale_proof_ignored_count={0}" -f $fallbackAuthorityProof.StaleProofIgnoredCount),
        ("fallback_leg_authority_metadata_missing_count={0}" -f $fallbackAuthorityProof.MetadataMissingCount),
        ("fallback_tail_reconciliation_verdict={0}" -f $fallbackTailProof.Verdict),
        ("fallback_tail_quiescence_verdict={0}" -f $fallbackTailProof.QuiescenceVerdict),
        ("fallback_tail_reconciliation_requested_count={0}" -f $fallbackTailProof.RequestedCount),
        ("fallback_tail_reconciliation_accepted_count={0}" -f $fallbackTailProof.AcceptedCount),
        ("fallback_tail_progress_forced_count={0}" -f $fallbackTailProof.ProgressForcedCount),
        ("fallback_tail_progress_coalesced_count={0}" -f $fallbackTailProof.ProgressCoalescedCount),
        ("fallback_tail_terminal_proof_consumed_count={0}" -f $fallbackTailProof.TerminalProofConsumedCount),
        ("fallback_tail_quiescent_yield_granted_count={0}" -f $fallbackTailProof.QuiescentYieldGrantedCount),
        ("fallback_tail_outbound_yield_granted_count={0}" -f $fallbackTailProof.OutboundQuiescentYieldGrantedCount),
        ("fallback_tail_inbound_yield_granted_count={0}" -f $fallbackTailProof.InboundQuiescentYieldGrantedCount),
        ("fallback_tail_yield_proof_sent_count={0}" -f $fallbackTailProof.YieldProofSentCount),
        ("fallback_tail_yield_proof_received_count={0}" -f $fallbackTailProof.YieldProofReceivedCount),
        ("fallback_tail_paired_yield_granted_count={0}" -f $fallbackTailProof.PairedYieldGrantedCount),
        ("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailProof.OwnerGenerationMismatchCount),
        ("fallback_tail_local_yield_blocked_count={0}" -f $fallbackTailProof.LocalYieldBlockedCount),
        ("fallback_tail_stale_yield_proof_ignored_count={0}" -f $fallbackTailProof.StaleYieldProofIgnoredCount),
        ("fallback_tail_paired_yield_verdict={0}" -f $fallbackTailProof.PairedYieldVerdict),
        ("fallback_frontier_repair_authority_granted_count={0}" -f $fallbackTailProof.FrontierRepairAuthorityGrantedCount),
        ("fallback_frontier_repair_authority_rejected_count={0}" -f $fallbackTailProof.FrontierRepairAuthorityRejectedCount),
        ("fallback_frontier_repair_dispatched_count={0}" -f $fallbackTailProof.FrontierRepairDispatchedCount),
        ("fallback_frontier_repair_stalled_count={0}" -f $fallbackTailProof.FrontierRepairStalledCount),
        ("fallback_frontier_repair_progress_observed_count={0}" -f $fallbackTailProof.FrontierRepairProgressObservedCount),
        ("fallback_frontier_repair_verdict={0}" -f $fallbackTailProof.FrontierRepairVerdict),
        ("runtime_unlock_probe_rejected_by_fallback_owner_count={0}" -f $fallbackTailProof.RuntimeUnlockProbeRejectedByFallbackOwnerCount),
        ("runtime_unlock_probe_rejected_by_inbound_fallback_owner_count={0}" -f $fallbackTailProof.RuntimeUnlockProbeRejectedByInboundFallbackOwnerCount),
        ("fallback_tail_quiescence_timeout_count={0}" -f $fallbackTailProof.QuiescenceTimeoutCount),
        ("fallback_tail_zero_credit_breaker_count={0}" -f $fallbackTailProof.ZeroCreditBreakerCount),
        ("fallback_tail_stale_frontier_retired_count={0}" -f $fallbackTailProof.StaleFrontierRetiredCount),
        ("fallback_tail_state_refresh_send_slot_retired_count={0}" -f $fallbackTailProof.SendSlotRetiredCount),
        ("fallback_tail_receive_recovery_requested_count={0}" -f $fallbackTailProof.ReceiveRecoveryRequestedCount),
        ("fallback_tail_normal_zero_credit_state_refresh_count={0}" -f $fallbackTailProof.NormalZeroCreditTailStateRefreshCount),
        ("fallback_tail_reconciliation_metadata_missing_count={0}" -f $fallbackTailProof.MetadataMissingCount),
        ("bridge_liveness_integration_verdict={0}" -f $bridgeLivenessProof.Verdict),
        ("session_liveness_deferred_for_current_recovery_count={0}" -f $bridgeLivenessProof.CurrentRecoveryDeferralCount),
        ("session_liveness_deferred_for_bridge_recovery_count={0}" -f $bridgeLivenessProof.BridgeRecoveryDeferralCount),
        ("session_liveness_timeout_during_valid_recovery_count={0}" -f $bridgeLivenessProof.TimeoutDuringValidRecoveryCount),
        ("bridge_recovery_receive_resumed_count={0}" -f $bridgeLivenessProof.ReceiveResumedCount),
        ("bridge_recovery_exhausted_without_proof_count={0}" -f $bridgeLivenessProof.RecoveryExhaustedWithoutProofCount),
        ("fallback_leg_authority_liveness_deferral_count={0}" -f $bridgeLivenessProof.FallbackLegAuthorityLivenessDeferralCount),
        ("bridge_liveness_stale_deferral_count={0}" -f $bridgeLivenessProof.StaleDeferralCount),
        ("bridge_exhausted_sibling_deferral_suppressed_count={0}" -f $bridgeLivenessProof.SiblingDeferralSuppressedCount),
        ("bridge_exhausted_sibling_deferral_expired_count={0}" -f $bridgeLivenessProof.SiblingDeferralExpiredCount),
        ("bridge_exhausted_terminal_during_valid_deferral_count={0}" -f $bridgeLivenessProof.TerminalDuringValidSiblingDeferralCount),
        ("control_plane_isolation_verdict={0}" -f $controlPlaneProof.Verdict),
        ("control_plane_peer_visible_count={0}" -f $controlPlaneProof.PeerVisibleCount),
        ("control_plane_local_only_rejected_count={0}" -f $controlPlaneProof.LocalOnlyRejectedCount),
        ("control_plane_checkpoint_exchange_count={0}" -f $controlPlaneProof.CheckpointExchangeCount),
        ("control_plane_continuous_receiver_state_proof_count={0}" -f $controlPlaneProof.ContinuousReceiverStateProofCount),
        ("control_plane_delivery_failure_count={0}" -f $controlPlaneProof.DeliveryFailureCount),
        ("control_plane_delivery_failure_recovered_count={0}" -f $controlPlaneProof.RecoveredDeliveryFailureCount),
        ("control_plane_liveness_timeout_during_valid_exchange_count={0}" -f $controlPlaneProof.LivenessTimeoutDuringValidExchangeCount),
        ("control_plane_stale_send_retired_count={0}" -f $controlPlaneProof.RetiredStaleSendCount),
        ("fallback_checkpoint_exchange_retired_count={0}" -f $controlPlaneProof.CheckpointExchangeRetiredCount),
        ("fallback_checkpoint_owner_violation_count={0}" -f $controlPlaneProof.CheckpointOwnerViolationCount),
        ("fallback_stale_deferred_replay_discarded_count={0}" -f $controlPlaneProof.StaleDeferredReplayDiscardedCount),
        ("fallback_checkpoint_not_pending_reissue_suppressed_count={0}" -f $controlPlaneProof.CheckpointNotPendingReissueSuppressedCount),
        ("fallback_stale_proof_ignored_count={0}" -f $controlPlaneProof.StaleProofIgnoredCount),
        ("fallback_stale_proof_replay_suppressed_count={0}" -f $controlPlaneProof.StaleProofReplaySuppressedCount),
        ("fallback_control_plane_stale_replay_treadmill_count={0}" -f $controlPlaneProof.StaleReplayTreadmillCount),
        ("fallback_stale_generation_treadmill_score={0}" -f $controlPlaneProof.StaleGenerationTreadmillScore),
        ("fallback_stale_generation_treadmill_score_limit={0}" -f $controlPlaneProof.StaleGenerationTreadmillScoreLimit),
        ("fallback_checkpoint_ids_by_leg_generation={0}" -f $controlPlaneProof.CheckpointIdsByLegGeneration),
        ("fallback_checkpoint_id_count_max_per_leg={0}" -f $controlPlaneProof.CheckpointIdCountMaxPerLeg),
        ("fallback_control_plane_non_current_leg_generation_send_count={0}" -f $controlPlaneProof.ControlPlaneNonCurrentLegGenerationSendCount),
        ("live_route_epoch_proof_mode={0}" -f $LiveRouteProofMode),
        ("live_route_epoch_proof_verdict={0}" -f $liveRouteProof.Verdict),
        ("live_route_epoch_metadata_missing_count={0}" -f $liveRouteProof.MetadataMissingCount),
        ("live_route_epoch_transport_only_count={0}" -f $liveRouteProof.TransportOnlyCount),
        '',
        'hard_failures:'
    ) + ($(if ($effectiveHardFailures.Count -gt 0) { @($effectiveHardFailures.ToArray()) } else { @('(none)') })) + @(
        '',
        'warnings:'
    ) + ($(if ($GateResult.Warnings.Count -gt 0) { @($GateResult.Warnings) } else { @('(none)') })) + @(
        '',
        'top_evidence:'
    ) + (Get-FileTransferArtifactEvidenceLines -Events $effectiveEvidenceEvents -Limit 30)

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
    Write-FileTransferInvariantLedgerArtifacts -ArtifactDir $ArtifactDir -Summary $Summary -GateResult $GateResult -LiveRouteProofMode $LiveRouteProofMode | Out-Null
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
