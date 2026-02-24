Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$maxBytes = 100MB

function Format-SizeMiB {
    param([long]$Bytes)
    return "{0:N1} MiB" -f ($Bytes / 1MB)
}

try {
    $insideRepo = (& git rev-parse --is-inside-work-tree 2>$null).Trim()
}
catch {
    Write-Warning "Git is not available or this folder is not a git repository."
    exit 1
}

if ($insideRepo -ne "true") {
    Write-Warning "This folder is not a git repository."
    exit 1
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    Write-Warning "Could not determine git repository root."
    exit 1
}

$trackedFiles = & git ls-files
$offenders = @()

foreach ($relativePath in $trackedFiles) {
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        continue
    }

    $fullPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $fullPath -PathType Leaf)) {
        continue
    }

    try {
        $file = Get-Item -LiteralPath $fullPath
    }
    catch {
        continue
    }

    if ($file.Length -gt $maxBytes) {
        $offenders += [pscustomobject]@{
            Path = $relativePath
            Bytes = [long]$file.Length
        }
    }
}

if ($offenders.Count -eq 0) {
    Write-Host "[nLink] Repo size preflight OK: no tracked files over 100 MiB." -ForegroundColor Green
    exit 0
}

Write-Warning "Tracked file(s) over 100 MiB detected. GitHub/GitHub Desktop may reject the push."
Write-Host "Offending files:" -ForegroundColor Yellow

$offenders |
    Sort-Object Bytes -Descending |
    ForEach-Object {
        Write-Host ("- {0} ({1}, {2} bytes)" -f $_.Path, (Format-SizeMiB $_.Bytes), $_.Bytes)
    }

Write-Host ""
Write-Host "This script does not change files. Remove/untrack large files before pushing." -ForegroundColor Yellow
exit 0

