param(
    [string]$ExePath = '',
    [int]$SmokeCycles = 5,
    [string]$WorkingDirectory = '',
    [string]$ArtifactPath = 'artifacts/beta-hardening/offline-smoke.txt'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'BetaHardening.Common.ps1')

function Add-Line {
    param([System.Collections.Generic.List[string]]$Lines, [string]$Text)
    [void]$Lines.Add($Text)
}

function Assert-NoSuspiciousNetworkPatterns {
    param(
        [string]$Text,
        [string]$Label,
        [System.Collections.Generic.List[string]]$Lines
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        Add-Line -Lines $Lines -Text ("{0}: no text captured" -f $Label)
        return
    }

    $patterns = @(
        'npm\s+ci',
        'npm\s+install',
        'registry\.npmjs\.org',
        'nodejs\.org',
        'make-fetch-happen',
        'npm-registry-fetch',
        'https?://[^\s]*(npmjs|nodejs)',
        'BridgeSupervisor'
    )

    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($pattern in $patterns) {
        if ([regex]::IsMatch($Text, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            [void]$hits.Add($pattern)
        }
    }

    if ($hits.Count -gt 0) {
        Add-Line -Lines $Lines -Text ("{0}: suspicious_patterns={1}" -f $Label, ($hits -join ', '))
        throw "Offline smoke detected unexpected network/fetch pattern(s) in $Label."
    }

    Add-Line -Lines $Lines -Text ("{0}: PASS (no suspicious network/fetch patterns)" -f $Label)
}

$repoRoot = Get-BetaHardeningRepoRoot
$artifactAbs = Resolve-BetaHardeningPath -RepoRoot $repoRoot -Path $ArtifactPath
$lines = New-Object System.Collections.Generic.List[string]
$exitCode = 1

try {
    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        $ExePath = Resolve-DefaultPortableExe -RepoRoot $repoRoot
    }
    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        throw 'nLink executable not found. Build portable output first or pass -ExePath.'
    }
    $ExePath = (Resolve-Path $ExePath).Path

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $WorkingDirectory = $repoRoot
    }

    Add-Line -Lines $lines -Text 'Beta Hardening - Offline Smoke'
    Add-Line -Lines $lines -Text ("utc_started={0}" -f ([DateTimeOffset]::UtcNow.ToString('o')))
    Add-Line -Lines $lines -Text ("exe_path={0}" -f $ExePath)
    Add-Line -Lines $lines -Text ("working_directory={0}" -f $WorkingDirectory)
    Add-Line -Lines $lines -Text ("smoke_cycles={0}" -f $SmokeCycles)
    Add-Line -Lines $lines -Text 'offline_mode=local-only DEVLOCAL transport + proxy blackhole (no NIC toggling)'

    $logSnapshot = Get-LogLineSnapshot
    Add-Line -Lines $lines -Text ("log_snapshot_before: path={0}; exists={1}; lines={2}" -f $logSnapshot.Path, $logSnapshot.Exists, $logSnapshot.LineCount)

    $offlineEnv = @{
        'NLINK_TRANSPORT' = 'DEVLOCAL'
        'HTTP_PROXY' = 'http://127.0.0.1:9'
        'HTTPS_PROXY' = 'http://127.0.0.1:9'
        'ALL_PROXY' = 'http://127.0.0.1:9'
        'NO_PROXY' = '*'
        'NPM_CONFIG_REGISTRY' = 'http://127.0.0.1:9/'
        'NPM_CONFIG_FETCH_RETRIES' = '0'
        'NPM_CONFIG_FETCH_TIMEOUT' = '1000'
        'NPM_CONFIG_AUDIT' = 'false'
    }

    $smoke = Invoke-NLinkDevLocalSmoke -ExePath $ExePath -Cycles $SmokeCycles -WorkingDirectory $WorkingDirectory -TimeoutSeconds 180 -EnvironmentOverrides $offlineEnv
    Add-Line -Lines $lines -Text ("smoke_exit_code={0}" -f $smoke.ExitCode)
    Add-Line -Lines $lines -Text ("smoke_timed_out={0}" -f $smoke.TimedOut)
    Add-Line -Lines $lines -Text ("smoke_duration_ms={0}" -f $smoke.DurationMs)

    if ($smoke.TimedOut) {
        throw 'Offline smoke timed out.'
    }
    if ([int]$smoke.ExitCode -ne 0) {
        throw ("Offline smoke failed with exit code {0}." -f $smoke.ExitCode)
    }

    $stdoutFirst = (($smoke.StdOut -split "`r?`n") | Select-Object -First 3) -join ' | '
    $stderrFirst = (($smoke.StdErr -split "`r?`n") | Select-Object -First 3) -join ' | '
    Add-Line -Lines $lines -Text ("stdout_head={0}" -f $stdoutFirst)
    Add-Line -Lines $lines -Text ("stderr_head={0}" -f $stderrFirst)

    $newLogLines = @(Get-NewLogLines -Snapshot $logSnapshot)
    Add-Line -Lines $lines -Text ("new_log_line_count={0}" -f $newLogLines.Count)

    $combinedLogText = ($newLogLines -join [Environment]::NewLine)
    $combinedOutputText = (([string]$smoke.StdOut) + [Environment]::NewLine + ([string]$smoke.StdErr))

    Assert-NoSuspiciousNetworkPatterns -Text $combinedOutputText -Label 'cli_output' -Lines $lines
    Assert-NoSuspiciousNetworkPatterns -Text $combinedLogText -Label 'new_log_lines' -Lines $lines

    Add-Line -Lines $lines -Text 'RESULT: PASS'
    $exitCode = 0
}
catch {
    Add-Line -Lines $lines -Text 'RESULT: FAIL'
    Add-Line -Lines $lines -Text ("error={0}" -f $_.Exception.Message)
    Add-Line -Lines $lines -Text ("error_type={0}" -f $_.Exception.GetType().FullName)
    $exitCode = 1
}
finally {
    Add-Line -Lines $lines -Text ("utc_finished={0}" -f ([DateTimeOffset]::UtcNow.ToString('o')))
    Write-BetaHardeningArtifact -Path $artifactAbs -Lines @($lines)
    Write-Host ("[beta-hardening] offline smoke report: {0}" -f $artifactAbs)
}

exit $exitCode
