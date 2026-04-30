param(
    [string]$NodePath = "",
    [string]$BridgePath = "",
    [string]$ArtifactDir = "",
    [int]$DurationSeconds = 60,
    [int]$IntervalMs = 1000,
    [int]$PayloadBytes = 1024,
    [int]$MediaPayloadBytes = 0,
    [int]$BulkPayloadBytes = 0,
    [int]$BulkSendConcurrency = 0,
    [int]$BulkBurstFrames = 1,
    [switch]$BulkOnly,
    [switch]$OneWayBulk,
    [switch]$IgnoreStdinBackpressure
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

function Resolve-FirstExistingPath {
    param([string[]]$Candidates)
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return ""
}

$repoRoot = Resolve-RepoRoot
$rid = "win-x64"
if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $NodePath = Resolve-FirstExistingPath @(
        (Join-Path $repoRoot "artifacts\bridge\$rid\node.exe"),
        (Join-Path $repoRoot "tools\node\$rid\node-v24.13.1-win-x64\node.exe"),
        (Join-Path $repoRoot "src\nLink.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\bridge\$rid\node.exe")
    )
}

if ([string]::IsNullOrWhiteSpace($BridgePath)) {
    $BridgePath = Resolve-FirstExistingPath @(
        (Join-Path $repoRoot "tools\nkn-bridge\index.js"),
        (Join-Path $repoRoot "artifacts\bridge\$rid\index.js"),
        (Join-Path $repoRoot "src\nLink.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\bridge\$rid\index.js")
    )
}

if ([string]::IsNullOrWhiteSpace($NodePath)) {
    throw "Node runtime not found. Build the bridge bundle or pass -NodePath."
}

if ([string]::IsNullOrWhiteSpace($BridgePath)) {
    throw "NKN bridge index.js not found. Build the bridge bundle or pass -BridgePath."
}

if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArtifactDir = Join-Path $repoRoot "artifacts\nkn-bridge-receive-probe\$stamp"
} elseif (-not [System.IO.Path]::IsPathRooted($ArtifactDir)) {
    $ArtifactDir = Join-Path $repoRoot $ArtifactDir
}

New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
$probeScript = Join-Path $repoRoot "tools\nkn-bridge-receive-probe.js"
if (-not (Test-Path -LiteralPath $probeScript -PathType Leaf)) {
    throw "Probe script not found: $probeScript"
}

$arguments = @(
    $probeScript,
    "--node",
    $NodePath,
    "--bridge",
    $BridgePath,
    "--artifact-dir",
    $ArtifactDir,
    "--duration-seconds",
    ([string]$DurationSeconds),
    "--interval-ms",
    ([string]$IntervalMs),
    "--payload-bytes",
    ([string]$PayloadBytes)
)

if ($MediaPayloadBytes -gt 0) {
    $arguments += @("--media-payload-bytes", ([string]$MediaPayloadBytes))
}

if ($BulkPayloadBytes -gt 0) {
    $arguments += @("--bulk-payload-bytes", ([string]$BulkPayloadBytes))
}

if ($BulkSendConcurrency -gt 0) {
    $arguments += @("--bulk-send-concurrency", ([string]$BulkSendConcurrency))
}

if ($BulkBurstFrames -gt 1) {
    $arguments += @("--bulk-burst-frames", ([string]$BulkBurstFrames))
}

if ($BulkOnly) {
    $arguments += "--bulk-only"
}

if ($OneWayBulk) {
    $arguments += "--one-way-bulk"
}

if ($IgnoreStdinBackpressure) {
    $arguments += "--ignore-stdin-backpressure"
}

Write-Host ("[NknBridgeReceiveProbe] Node: {0}" -f $NodePath) -ForegroundColor Cyan
Write-Host ("[NknBridgeReceiveProbe] Bridge: {0}" -f $BridgePath) -ForegroundColor Cyan
Write-Host ("[NknBridgeReceiveProbe] Artifacts: {0}" -f $ArtifactDir) -ForegroundColor Cyan

& $NodePath @arguments
exit $LASTEXITCODE
