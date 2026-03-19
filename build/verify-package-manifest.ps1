param(
    [Parameter(Mandatory = $true)][string]$StageDir,
    [string]$ManifestPath = "installer/package-manifest.win-x64.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-NormalizedRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $relative = [System.IO.Path]::GetRelativePath($RootDir, $Path)
    return $relative.Replace('\', '/')
}

function Assert-BridgeSupportsBulkChannel {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeScriptPath
    )

    $content = Get-Content -Path $BridgeScriptPath -Raw
    $requiredMarkers = @(
        'bulkClient',
        'bulkAddress',
        'SUPPORTED_CHANNELS',
        "'bulk'"
    )

    foreach ($marker in $requiredMarkers) {
        if ($content -notlike "*$marker*") {
            throw "Packaged bridge script does not advertise/implement bulk channel support: missing marker '$marker' in $BridgeScriptPath"
        }
    }
}

$resolvedStageDir = (Resolve-Path $StageDir).Path
$resolvedManifestPath = (Resolve-Path $ManifestPath).Path

$manifestEntries = @(
    Get-Content -Path $resolvedManifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
)

if ($manifestEntries.Count -eq 0) {
    throw "Package manifest is empty: $resolvedManifestPath"
}

$duplicateEntries = @($manifestEntries | Group-Object | Where-Object Count -gt 1)
if ($duplicateEntries.Count -gt 0) {
    $duplicates = @($duplicateEntries | ForEach-Object Name) -join ', '
    throw "Package manifest contains duplicate entries: $duplicates"
}

$expectedBridgeRuntimes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($entry in $manifestEntries) {
    $expectsDirectory = $entry.EndsWith('/')
    $normalizedEntry = $entry.TrimEnd('/')
    $stagePath = Join-Path $resolvedStageDir ($normalizedEntry -replace '/', '\')

    if ($expectsDirectory) {
        if (-not (Test-Path -Path $stagePath -PathType Container)) {
            throw "Required package directory is missing: $entry"
        }
    }
    else {
        if (-not (Test-Path -Path $stagePath -PathType Leaf)) {
            throw "Required package file is missing: $entry"
        }
    }

    if ($normalizedEntry -like 'bridge/*') {
        $segments = $normalizedEntry.Split('/')
        if ($segments.Length -ge 2) {
            [void]$expectedBridgeRuntimes.Add($segments[1])
        }
    }
}

$forbiddenFiles = @(
    'Avalonia.Diagnostics.dll',
    'nLink.runtimeconfig.dev.json'
)

foreach ($fileName in $forbiddenFiles) {
    $matches = @(Get-ChildItem -Path $resolvedStageDir -Recurse -File -Filter $fileName -ErrorAction SilentlyContinue)
    if ($matches.Count -gt 0) {
        $paths = @($matches | ForEach-Object { Get-NormalizedRelativePath -RootDir $resolvedStageDir -Path $_.FullName }) -join ', '
        throw "Package staging contains forbidden file '$fileName': $paths"
    }
}

$symbolLikeFiles = @(Get-ChildItem -Path $resolvedStageDir -Recurse -File -Include *.pdb,*.xml -ErrorAction SilentlyContinue)
if ($symbolLikeFiles.Count -gt 0) {
    $paths = @($symbolLikeFiles | ForEach-Object { Get-NormalizedRelativePath -RootDir $resolvedStageDir -Path $_.FullName }) -join ', '
    throw "Package staging contains debug-only files: $paths"
}

$bridgeRoot = Join-Path $resolvedStageDir 'bridge'
if (Test-Path -Path $bridgeRoot -PathType Container) {
    $actualBridgeRuntimes = @(
        Get-ChildItem -Path $bridgeRoot -Directory -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name |
            Sort-Object -Unique
    )

    $unexpectedBridgeRuntimes = @(
        $actualBridgeRuntimes |
            Where-Object { -not $expectedBridgeRuntimes.Contains($_) }
    )

    if ($unexpectedBridgeRuntimes.Count -gt 0) {
        throw "Package staging contains unexpected bridge runtime directories: $($unexpectedBridgeRuntimes -join ', ')"
    }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $bridgeSourcePath = Join-Path $repoRoot "tools\nkn-bridge\index.js"
    $expectedBridgeHash = if (Test-Path $bridgeSourcePath) { (Get-FileHash -Path $bridgeSourcePath -Algorithm SHA256).Hash } else { $null }

    foreach ($runtime in $actualBridgeRuntimes) {
        $stagedBridgePath = Join-Path $bridgeRoot (Join-Path $runtime 'index.js')
        if (-not (Test-Path -Path $stagedBridgePath -PathType Leaf)) {
            continue
        }

        Assert-BridgeSupportsBulkChannel -BridgeScriptPath $stagedBridgePath

        if ($expectedBridgeHash) {
            $stagedHash = (Get-FileHash -Path $stagedBridgePath -Algorithm SHA256).Hash
            if ($stagedHash -ne $expectedBridgeHash) {
                throw "Packaged bridge script does not match repo source for runtime '$runtime'."
            }
        }
    }
}

Write-Host "[nLink] Package manifest verified: $resolvedManifestPath"
Write-Host "[nLink] Stage directory verified: $resolvedStageDir"
Write-Host "[nLink] Required entries: $($manifestEntries.Count)"
