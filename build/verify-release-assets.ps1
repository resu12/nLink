param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Runtime = "win-x64",
    [string]$RepoRoot = "",
    [string]$ReleasesRoot = "artifacts/releases"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$releasesRootAbs = Join-Path $RepoRoot $ReleasesRoot
$releaseDir = Join-Path $releasesRootAbs $Version

if (-not (Test-Path $releaseDir)) {
    throw "Release assets directory not found: $releaseDir"
}

$expectedFiles = @(
    "nLink-Portable-$Runtime-$Version.zip",
    "nLink-Setup-win-x64-$Version.exe",
    "SHA256SUMS.txt"
)

$actualFiles = @(Get-ChildItem -Path $releaseDir -File | Select-Object -ExpandProperty Name | Sort-Object)
$expectedSorted = @($expectedFiles | Sort-Object)

if (@($actualFiles).Count -ne @($expectedSorted).Count) {
    throw "Release assets mismatch in '$releaseDir'. Expected: $($expectedSorted -join ', '). Actual: $($actualFiles -join ', ')"
}

for ($i = 0; $i -lt $expectedSorted.Count; $i++) {
    if ($actualFiles[$i] -ne $expectedSorted[$i]) {
        throw "Release assets mismatch in '$releaseDir'. Expected: $($expectedSorted -join ', '). Actual: $($actualFiles -join ', ')"
    }
}

foreach ($fileName in $expectedFiles) {
    $filePath = Join-Path $releaseDir $fileName
    if (-not (Test-Path $filePath)) {
        throw "Expected release asset not found: $filePath"
    }

    $fileInfo = Get-Item $filePath
    if ($fileInfo.Length -le 0) {
        throw "Release asset is empty: $filePath"
    }
}

$checksumsPath = Join-Path $releaseDir "SHA256SUMS.txt"
$checksums = Get-Content $checksumsPath -Raw
foreach ($requiredName in @(
        "nLink-Portable-$Runtime-$Version.zip",
        "nLink-Setup-win-x64-$Version.exe"
    )) {
    if ($checksums -notmatch [regex]::Escape($requiredName)) {
        throw "SHA256SUMS.txt is missing an entry for '$requiredName'."
    }
}

Write-Host "[nLink] Release assets verified: $releaseDir"
Write-Host "[nLink] Verified files: $($expectedFiles -join ', ')"
