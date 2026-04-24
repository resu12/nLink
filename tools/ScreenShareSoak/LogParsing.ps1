function Get-PercentileValue {
    param(
        [Parameter(Mandatory = $true)][int[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $rank = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [int]$sorted[$rank]
}

function Get-StructuredLogFieldPairs {
    param([string]$Line)

    $pairs = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $pairs
    }

    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Line, '(?<key>[A-Za-z0-9_\[\]-]+)=(?<value>[^;]*)')) {
        $pairs.Add([pscustomobject]@{
            Key = [string]$match.Groups['key'].Value
            Value = [string]$match.Groups['value'].Value.Trim()
        })
    }

    return $pairs
}

function Get-StructuredLogFieldValue {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key
    )

    if ($null -eq $Pairs -or [string]::IsNullOrWhiteSpace($Key)) {
        return $null
    }

    foreach ($pair in $Pairs) {
        if ([string]::Equals([string]$pair.Key, $Key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [string]$pair.Value
        }
    }

    return $null
}

function Get-StructuredLogFieldValueAfter {
    param(
        [System.Collections.IList]$Pairs,
        [string]$AfterKey,
        [int]$Offset = 1
    )

    if ($null -eq $Pairs -or [string]::IsNullOrWhiteSpace($AfterKey) -or $Offset -lt 1) {
        return $null
    }

    for ($i = 0; $i -lt $Pairs.Count; $i++) {
        if ([string]::Equals([string]$Pairs[$i].Key, $AfterKey, [System.StringComparison]::OrdinalIgnoreCase)) {
            $targetIndex = $i + $Offset
            if ($targetIndex -lt $Pairs.Count) {
                return [string]$Pairs[$targetIndex].Value
            }

            break
        }
    }

    return $null
}

function Get-StructuredLogIntField {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key,
        [int]$DefaultValue = -1,
        [string]$FallbackAfterKey = '',
        [int]$FallbackOffset = 1
    )

    $value = Get-StructuredLogFieldValue -Pairs $Pairs -Key $Key
    if ([string]::IsNullOrWhiteSpace($value) -and -not [string]::IsNullOrWhiteSpace($FallbackAfterKey)) {
        $value = Get-StructuredLogFieldValueAfter -Pairs $Pairs -AfterKey $FallbackAfterKey -Offset $FallbackOffset
    }

    if (-not [string]::IsNullOrWhiteSpace($value) -and $value -match '^-?[0-9]+$') {
        return [int]$value
    }

    return $DefaultValue
}

function Get-StructuredLogFloatField {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key,
        [double]$DefaultValue = -1,
        [string]$FallbackAfterKey = '',
        [int]$FallbackOffset = 1
    )

    $value = Get-StructuredLogFieldValue -Pairs $Pairs -Key $Key
    if ([string]::IsNullOrWhiteSpace($value) -and -not [string]::IsNullOrWhiteSpace($FallbackAfterKey)) {
        $value = Get-StructuredLogFieldValueAfter -Pairs $Pairs -AfterKey $FallbackAfterKey -Offset $FallbackOffset
    }

    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $parsed = 0.0
        if ([double]::TryParse($value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $parsed
        }
    }

    return $DefaultValue
}

function Get-StructuredLogStringField {
    param(
        [System.Collections.IList]$Pairs,
        [string]$Key,
        [string]$DefaultValue = '',
        [string]$FallbackAfterKey = '',
        [int]$FallbackOffset = 1
    )

    $value = Get-StructuredLogFieldValue -Pairs $Pairs -Key $Key
    if ([string]::IsNullOrWhiteSpace($value) -and -not [string]::IsNullOrWhiteSpace($FallbackAfterKey)) {
        $value = Get-StructuredLogFieldValueAfter -Pairs $Pairs -AfterKey $FallbackAfterKey -Offset $FallbackOffset
    }

    if ($null -eq $value) {
        return $DefaultValue
    }

    return [string]$value
}

function Read-KeyValueSummaryFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path $Path)) {
        return $values
    }

    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -le 0) {
            continue
        }

        $key = $line.Substring(0, $separatorIndex).Trim()
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        $values[$key] = $line.Substring($separatorIndex + 1).Trim()
    }

    return $values
}

function Get-SummaryNumberValue {
    param(
        [hashtable]$Values,
        [string[]]$Keys
    )

    if ($null -eq $Values -or $null -eq $Keys) {
        return $null
    }

    foreach ($key in $Keys) {
        if ([string]::IsNullOrWhiteSpace($key) -or -not $Values.ContainsKey($key)) {
            continue
        }

        $rawValue = [string]$Values[$key]
        if ([string]::IsNullOrWhiteSpace($rawValue) -or
            [string]::Equals($rawValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $parsed = 0.0
        if ([double]::TryParse($rawValue, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $parsed
        }
    }

    return $null
}
