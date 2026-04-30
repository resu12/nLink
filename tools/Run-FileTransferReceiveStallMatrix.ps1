param(
    [string]$ArtifactRoot = "",
    [string]$PayloadSize = "16MiB",
    [int]$TimeoutSeconds = 180,
    [int]$CycleTimeoutSeconds = 120,
    [int]$ProgressTimeoutSeconds = 30,
    [string]$ExePath = "",
    [switch]$Build,
    [switch]$IncludeUnsafePackedMixed,
    [switch]$FailOnFirstFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $current = $PSScriptRoot
    while ($null -ne $current -and $current -ne "") {
        if ((Test-Path (Join-Path $current "nLink.sln")) -and
            (Test-Path (Join-Path $current "VERSION"))) {
            return $current
        }

        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }

        $current = $parent
    }

    throw "Could not locate repo root from $PSScriptRoot."
}

function Set-EnvValue {
    param([string]$Name, [string]$Value)
    $previous = [Environment]::GetEnvironmentVariable($Name, "Process")
    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
    return [pscustomobject]@{ Name = $Name; Previous = $previous }
}

function Restore-EnvValues {
    param([object[]]$Restore)
    foreach ($entry in $Restore) {
        [Environment]::SetEnvironmentVariable([string]$entry.Name, $entry.Previous, "Process")
    }
}

function Join-ProcessArgumentList {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    return (($Arguments | ForEach-Object {
                $value = [string]$_
                if ($value.IndexOfAny([char[]]@(' ', "`t", '"')) -ge 0) {
                    '"' + ($value -replace '"', '\"') + '"'
                }
                else {
                    $value
                }
            }) -join ' ')
}

function Invoke-MatrixCase {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Case,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $artifactDir = Join-Path $Root ([string]$Case.Name)
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    $restore = @()
    try {
        foreach ($key in @($Case.Env.Keys)) {
            $restore += Set-EnvValue -Name ([string]$key) -Value ([string]$Case.Env[$key])
        }

        $arguments = @(
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            (Join-Path $RepoRoot "tools\FileTransfer-Ops.ps1"),
            "-Mode",
            ([string]$Case.Mode),
            "-PayloadSizes",
            $PayloadSize,
            "-Cycles",
            "1",
            "-PayloadEfficiencyProfile",
            ([string]$Case.PayloadEfficiencyProfile),
            "-ArtifactDir",
            $artifactDir,
            "-CycleTimeoutSeconds",
            ([string]$CycleTimeoutSeconds),
            "-ProgressTimeoutSeconds",
            ([string]$ProgressTimeoutSeconds),
            "-TimeoutSeconds",
            ([string]$TimeoutSeconds),
            "-FailOnGate"
        )

        if ($Build -and [string]$Case.Name -eq "nkn-fast-current") {
            $arguments += "-Build"
        }

        if (-not [string]::IsNullOrWhiteSpace($ExePath)) {
            $arguments += @("-ExePath", $ExePath)
        }

        Write-Host ("[ReceiveStallMatrix] Running {0} -> {1}" -f $Case.Name, $artifactDir) -ForegroundColor Cyan
        $stdoutPath = Join-Path $artifactDir "receive-stall-matrix-case-stdout.log"
        $stderrPath = Join-Path $artifactDir "receive-stall-matrix-case-stderr.log"
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

        $process = Start-Process `
            -FilePath "powershell" `
            -ArgumentList (Join-ProcessArgumentList -Arguments $arguments) `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -NoNewWindow `
            -Wait `
            -PassThru

        if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
            Get-Content -LiteralPath $stdoutPath | ForEach-Object { Write-Host $_ }
        }

        if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
            Get-Content -LiteralPath $stderrPath | ForEach-Object { Write-Host $_ }
        }

        $exitCode = [int]$process.ExitCode
        [pscustomobject]@{
            name = [string]$Case.Name
            mode = [string]$Case.Mode
            payload_efficiency_profile = [string]$Case.PayloadEfficiencyProfile
            artifact_dir = $artifactDir
            exit_code = $exitCode
        }
    }
    finally {
        Restore-EnvValues -Restore $restore
    }
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repoRoot "artifacts\filetransfer-soak\receive-stall"
} elseif (-not [System.IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repoRoot $ArtifactRoot
}

New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null
$cases = New-Object System.Collections.Generic.List[hashtable]
$cases.Add(@{ Name = "nkn-fast-current"; Mode = "NknFast"; PayloadEfficiencyProfile = "Current"; Env = @{} }) | Out-Null
$cases.Add(@{ Name = "nkn-mixed-current"; Mode = "NknMixed"; PayloadEfficiencyProfile = "Current"; Env = @{} }) | Out-Null
$cases.Add(@{ Name = "nkn-mixed-current-bulk-serial"; Mode = "NknMixed"; PayloadEfficiencyProfile = "Current"; Env = @{ "NLINK_NKN_BULK_SEND_CONCURRENCY" = "1" } }) | Out-Null
$cases.Add(@{ Name = "nkn-mixed-current-no-control-bulk-redundancy"; Mode = "NknMixed"; PayloadEfficiencyProfile = "Current"; Env = @{ "NLINK_FILETRANSFER_V3_CONTROL_BULK_REDUNDANCY" = "0" } }) | Out-Null

if ($IncludeUnsafePackedMixed) {
    Write-Warning "Including unsafe packed mixed payload case. Use only for controlled receive-stall reproduction."
    $cases.Add(@{
            Name = "nkn-mixed-packed3x21kib-unsafe"
            Mode = "NknMixed"
            PayloadEfficiencyProfile = "Packed3x21KiB"
            Env = @{ "NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE" = "1" }
        }) | Out-Null
}

$cases = $cases.ToArray()

$results = New-Object System.Collections.Generic.List[object]
foreach ($case in $cases) {
    $result = Invoke-MatrixCase -Case $case -RepoRoot $repoRoot -Root $ArtifactRoot
    $results.Add($result) | Out-Null
    if ($result.exit_code -ne 0 -and $FailOnFirstFailure) {
        break
    }
}

$summaryPath = Join-Path $ArtifactRoot "receive-stall-matrix-summary.json"
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host ("[ReceiveStallMatrix] Summary: {0}" -f $summaryPath) -ForegroundColor Green

$failedResults = @($results | Where-Object { $_.exit_code -ne 0 })
if ($failedResults.Count -gt 0) {
    exit 1
}

exit 0
