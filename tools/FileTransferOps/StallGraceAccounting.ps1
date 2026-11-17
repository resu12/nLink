Set-StrictMode -Version Latest

# Report compatibility is independent of the Coordinator evidence catalog.
function ConvertFrom-FileTransferStallGraceAccountingReport {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Report)
    ConvertFrom-FileTransferGraceAccountingReport -Report $Report -Kind stall
}

function ConvertFrom-FileTransferRecentProgressGraceAccountingReport {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Report)
    ConvertFrom-FileTransferGraceAccountingReport -Report $Report -Kind recent_progress
}

function ConvertFrom-FileTransferGraceAccountingReport {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Report,
        [ValidateSet('stall','recent_progress')][string]$Kind)
    $prefix = "filetransfer_fallback_frontier_${Kind}_grace_recheck_missing_"
    $errorPrefix = "${Kind}_grace"
    $version = 1
    if ($Report.Contains($prefix + 'accounting_version')) {
        if ([string]$Report[$prefix + 'accounting_version'] -cnotin @('1','2')) {
            throw "${errorPrefix}_accounting_version_unsupported"
        }
        $version = [int]$Report[$prefix + 'accounting_version']
    }
    $result = [ordered]@{ AccountingVersion = $version }
    foreach ($name in @('Raw','Distinct','Recovered','Superseded','Unresolved')) {
        $key = $prefix + $name.ToLowerInvariant() + '_count'
        $value = 0
        if ($Report.Contains($key)) {
            $text = [string]$Report[$key]
            if ($text -cnotmatch '^[0-9]+$' -or -not [int]::TryParse($text, [ref]$value)) {
                throw ("${errorPrefix}_accounting_counter_invalid:" + $name)
            }
        }
        elseif ($version -eq 2) { throw ("${errorPrefix}_accounting_counter_missing:" + $name) }
        $result[$name + 'Count'] = $value
    }
    if ($version -eq 1 -and $result.SupersededCount -ne 0) { throw "${errorPrefix}_legacy_superseded_count_invalid" }
    if ($version -eq 2 -and [long]$result.DistinctCount -ne
        ([long]$result.RecoveredCount + [long]$result.SupersededCount + [long]$result.UnresolvedCount)) {
        throw "${errorPrefix}_accounting_equation_invalid"
    }
    return [pscustomobject]$result
}
