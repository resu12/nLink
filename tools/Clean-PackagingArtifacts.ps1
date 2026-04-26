param(
    [string]$RepoRoot = "",
    [int]$KeepReleaseVersions = 3,
    [switch]$IncludeSoak,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ReleaseVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $versionPath = Join-Path $Root "VERSION"
    if (-not (Test-Path $versionPath)) {
        return ""
    }

    return (Get-Content -Path $versionPath -Raw).Trim()
}

function Get-PathSizeBytes {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return [int64]0
    }

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer) {
        return [int64]$item.Length
    }

    $sum = 0L
    foreach ($file in Get-ChildItem -LiteralPath $item.FullName -Recurse -File -ErrorAction SilentlyContinue) {
        $sum += [int64]$file.Length
    }

    return $sum
}

function Format-Size {
    param([int64]$Bytes)

    if ($Bytes -lt 1KB) { return "$Bytes B" }
    if ($Bytes -lt 1MB) { return ("{0:N1} KB" -f ($Bytes / 1KB)) }
    if ($Bytes -lt 1GB) { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    return ("{0:N2} GB" -f ($Bytes / 1GB))
}

function Resolve-ExistingPathOrParent {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path $Path) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path $parent)) {
        return $null
    }

    $parentResolved = (Resolve-Path -LiteralPath $parent).Path
    return Join-Path $parentResolved (Split-Path -Leaf $Path)
}

function Test-IsUnderDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $rootWithSlash = $Root.TrimEnd('\') + '\'
    return $Candidate.StartsWith($rootWithSlash, [StringComparison]::OrdinalIgnoreCase)
}

function Add-CleanupCandidate {
    param(
        [System.Collections.Generic.List[object]]$Candidates,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ArtifactsRoot,
        [Parameter(Mandatory = $true)][string]$Group,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    if (-not (Test-Path $Path)) {
        return
    }

    $resolved = Resolve-ExistingPathOrParent -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved) -or -not (Test-IsUnderDirectory -Candidate $resolved -Root $ArtifactsRoot)) {
        throw "Refusing to add cleanup candidate outside artifacts: $Path"
    }

    [void]$Candidates.Add([pscustomobject]@{
        Path = $resolved
        Group = $Group
        Reason = $Reason
        SizeBytes = Get-PathSizeBytes -Path $resolved
    })
}

function Test-IsCurrentVersionAsset {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($Version)) {
        return $false
    }

    return $FileName.IndexOf("-$Version.", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

$artifactsRoot = Join-Path $RepoRoot "artifacts"
if (-not (Test-Path $artifactsRoot)) {
    Write-Host "[nLink] No artifacts folder found: $artifactsRoot"
    exit 0
}

$artifactsRoot = (Resolve-Path -LiteralPath $artifactsRoot).Path
$currentVersion = Get-ReleaseVersion -Root $RepoRoot
$candidates = [System.Collections.Generic.List[object]]::new()

foreach ($relativePath in @(
    "publish",
    "size-probes",
    "portable-installer-temp",
    "tmp-installer-build",
    "portable-installer",
    "portable-installer-sizecheck",
    "portable-sizecheck",
    "installer-sizecheck",
    "test-results",
    "portable\helper",
    "portable\helpee"
)) {
    Add-CleanupCandidate `
        -Candidates $candidates `
        -Path (Join-Path $artifactsRoot $relativePath) `
        -ArtifactsRoot $artifactsRoot `
        -Group (($relativePath -split '\\')[0]) `
        -Reason "generated packaging artifact"
}

$portableRoot = Join-Path $artifactsRoot "portable"
if (Test-Path $portableRoot) {
    foreach ($zip in Get-ChildItem -LiteralPath $portableRoot -File -Filter "nLink-Portable-*.zip" -ErrorAction SilentlyContinue) {
        if (-not (Test-IsCurrentVersionAsset -FileName $zip.Name -Version $currentVersion)) {
            Add-CleanupCandidate -Candidates $candidates -Path $zip.FullName -ArtifactsRoot $artifactsRoot -Group "portable" -Reason "old portable ZIP"
        }
    }
}

$installerRoot = Join-Path $artifactsRoot "installer"
if (Test-Path $installerRoot) {
    foreach ($setup in Get-ChildItem -LiteralPath $installerRoot -File -Filter "nLink-Setup-*.exe" -ErrorAction SilentlyContinue) {
        if (-not (Test-IsCurrentVersionAsset -FileName $setup.Name -Version $currentVersion)) {
            Add-CleanupCandidate -Candidates $candidates -Path $setup.FullName -ArtifactsRoot $artifactsRoot -Group "installer" -Reason "old installer EXE"
        }
    }
}

$releasesRoot = Join-Path $artifactsRoot "releases"
if (Test-Path $releasesRoot) {
    $releaseDirs = @(Get-ChildItem -LiteralPath $releasesRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    $keepNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if (-not [string]::IsNullOrWhiteSpace($currentVersion)) {
        [void]$keepNames.Add($currentVersion)
    }

    foreach ($dir in $releaseDirs | Where-Object { -not $keepNames.Contains($_.Name) } | Select-Object -First ([Math]::Max(0, $KeepReleaseVersions - 1))) {
        [void]$keepNames.Add($dir.Name)
    }

    foreach ($dir in $releaseDirs) {
        if (-not $keepNames.Contains($dir.Name)) {
            Add-CleanupCandidate -Candidates $candidates -Path $dir.FullName -ArtifactsRoot $artifactsRoot -Group "releases" -Reason "old release folder"
        }
    }
}

if ($IncludeSoak) {
    Add-CleanupCandidate -Candidates $candidates -Path (Join-Path $artifactsRoot "soak") -ArtifactsRoot $artifactsRoot -Group "soak" -Reason "explicit IncludeSoak"
}

$totalBytes = ($candidates | Measure-Object SizeBytes -Sum).Sum
if ($null -eq $totalBytes) {
    $totalBytes = 0
}

$modeText = if ($Apply) { "APPLY" } else { "DRY-RUN" }
Write-Host ("[nLink] Packaging cleanup mode: {0}" -f $modeText) -ForegroundColor Cyan
Write-Host ("[nLink] Reclaimable size: {0} ({1} bytes)" -f (Format-Size ([int64]$totalBytes)), $totalBytes)

foreach ($group in $candidates | Group-Object Group | Sort-Object Name) {
    $groupBytes = ($group.Group | Measure-Object SizeBytes -Sum).Sum
    if ($null -eq $groupBytes) {
        $groupBytes = 0
    }

    Write-Host ("  {0}: {1} ({2} bytes)" -f $group.Name, (Format-Size ([int64]$groupBytes)), $groupBytes)
}

foreach ($candidate in $candidates | Sort-Object Group, Path) {
    Write-Host ("  [{0}] {1} - {2} ({3})" -f $candidate.Group, $candidate.Path, $candidate.Reason, (Format-Size $candidate.SizeBytes))
}

if (-not $Apply) {
    Write-Host "[nLink] Dry run only. Re-run with -Apply to delete these generated artifacts." -ForegroundColor Yellow
    exit 0
}

foreach ($candidate in $candidates) {
    if (-not (Test-Path $candidate.Path)) {
        continue
    }

    if (-not (Test-IsUnderDirectory -Candidate $candidate.Path -Root $artifactsRoot)) {
        throw "Refusing to remove path outside artifacts: $($candidate.Path)"
    }

    Remove-Item -LiteralPath $candidate.Path -Recurse -Force
}

Write-Host "[nLink] Packaging cleanup complete." -ForegroundColor Green
