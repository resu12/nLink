param(
    [Parameter(Mandatory = $true)][string]$StageDir,
    [string]$ManifestPath = "installer/package-manifest.win-x64.txt",
    [string]$ExpectedAppVersion = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExpectedAppVersion)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $versionPath = Join-Path $repoRoot "VERSION"
    if (Test-Path -Path $versionPath -PathType Leaf) {
        $ExpectedAppVersion = (Get-Content -Path $versionPath -Raw).Trim()
    }
}

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

function Get-Sha256FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-BridgeManifestMatchesBundle {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [string]$ExpectedAppVersion = ""
    )

    $manifestPath = Join-Path $BridgeDir 'bridge-manifest.json'
    if (-not (Test-Path -Path $manifestPath -PathType Leaf)) {
        throw "Packaged bridge manifest is missing for runtime '$Runtime': $manifestPath"
    }

    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.runtime -ne $Runtime) {
        throw "Packaged bridge manifest runtime mismatch: expected '$Runtime', got '$($manifest.runtime)'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedAppVersion)) {
        $actualAppVersion = [string]$manifest.appVersion
        if ($actualAppVersion -ne $ExpectedAppVersion) {
            throw "Packaged bridge manifest appVersion mismatch: expected '$ExpectedAppVersion', got '$actualAppVersion'. Rebuild the bridge bundle after changing VERSION: $manifestPath"
        }
    }

    if ([string]::IsNullOrWhiteSpace($manifest.bridgeScriptSha256)) {
        throw "Packaged bridge manifest is missing bridgeScriptSha256: $manifestPath"
    }

    if ($manifest.nodeModulesShipped -ne $false) {
        throw "Packaged bridge manifest must declare nodeModulesShipped=false: $manifestPath"
    }

    $stagedBridgePath = Join-Path $BridgeDir 'index.js'
    $actualHash = Get-Sha256FileHash -Path $stagedBridgePath
    $expectedHash = ([string]$manifest.bridgeScriptSha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Packaged bridge script hash does not match bridge manifest for runtime '$Runtime'."
    }

    $packageLockPath = Join-Path $BridgeDir 'package-lock.json'
    if (-not (Test-Path -Path $packageLockPath -PathType Leaf)) {
        throw "Packaged bridge package-lock.json is missing for runtime '$Runtime': $packageLockPath"
    }

    if ([string]::IsNullOrWhiteSpace($manifest.packageLockSha256)) {
        throw "Packaged bridge manifest is missing packageLockSha256: $manifestPath"
    }

    $actualLockHash = Get-Sha256FileHash -Path $packageLockPath
    $expectedLockHash = ([string]$manifest.packageLockSha256).ToLowerInvariant()
    if ($actualLockHash -ne $expectedLockHash) {
        throw "Packaged bridge package-lock hash does not match bridge manifest for runtime '$Runtime'."
    }

    $dependencyEvidencePath = Join-Path $BridgeDir 'bridge-dependencies.json'
    if (-not (Test-Path -Path $dependencyEvidencePath -PathType Leaf)) {
        throw "Packaged bridge dependency evidence is missing for runtime '$Runtime': $dependencyEvidencePath"
    }

    $dependencyEvidence = Get-Content -Path $dependencyEvidencePath -Raw | ConvertFrom-Json
    if ($dependencyEvidence.nodeModulesShipped -ne $false) {
        throw "Packaged bridge dependency evidence must declare nodeModulesShipped=false: $dependencyEvidencePath"
    }
}

function Assert-TunaSidecarManifestMatchesBundle {
    param(
        [Parameter(Mandatory = $true)][string]$TunaDir,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [string]$ExpectedAppVersion = ""
    )

    $sidecarPath = Join-Path $TunaDir 'nlink-tuna-sidecar.exe'
    if (-not (Test-Path -Path $sidecarPath -PathType Leaf)) {
        throw "Packaged Tuna sidecar executable is missing for runtime '$Runtime': $sidecarPath"
    }

    $manifestPath = Join-Path $TunaDir 'tuna-sidecar-manifest.json'
    if (-not (Test-Path -Path $manifestPath -PathType Leaf)) {
        throw "Packaged Tuna sidecar manifest is missing for runtime '$Runtime': $manifestPath"
    }

    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.manifestVersion -ne 1) {
        throw "Packaged Tuna sidecar manifestVersion mismatch: expected '1', got '$($manifest.manifestVersion)'."
    }

    if ($manifest.runtime -ne $Runtime) {
        throw "Packaged Tuna sidecar manifest runtime mismatch: expected '$Runtime', got '$($manifest.runtime)'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedAppVersion)) {
        $actualAppVersion = [string]$manifest.appVersion
        if ($actualAppVersion -ne $ExpectedAppVersion) {
            throw "Packaged Tuna sidecar manifest appVersion mismatch: expected '$ExpectedAppVersion', got '$actualAppVersion'. Rebuild the Tuna sidecar after changing VERSION: $manifestPath"
        }

        $actualSidecarVersion = [string]$manifest.sidecarVersion
        if ($actualSidecarVersion -ne $ExpectedAppVersion) {
            throw "Packaged Tuna sidecar version mismatch: expected '$ExpectedAppVersion', got '$actualSidecarVersion'. Rebuild the Tuna sidecar after changing VERSION: $manifestPath"
        }
    }

    if ($manifest.appProtocolVersion -ne 1) {
        throw "Packaged Tuna sidecar app protocol mismatch: expected '1', got '$($manifest.appProtocolVersion)'."
    }

    if ($manifest.frameProtocolVersion -ne 1) {
        throw "Packaged Tuna sidecar frame protocol mismatch: expected '1', got '$($manifest.frameProtocolVersion)'."
    }

    if ([string]::IsNullOrWhiteSpace($manifest.sidecarExeSha256)) {
        throw "Packaged Tuna sidecar manifest is missing sidecarExeSha256: $manifestPath"
    }

    $actualHash = Get-Sha256FileHash -Path $sidecarPath
    $expectedHash = ([string]$manifest.sidecarExeSha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Packaged Tuna sidecar hash does not match Tuna sidecar manifest for runtime '$Runtime'."
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
$expectedTunaRuntimes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

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

    if ($normalizedEntry -like 'tuna/*') {
        $segments = $normalizedEntry.Split('/')
        if ($segments.Length -ge 2) {
            [void]$expectedTunaRuntimes.Add($segments[1])
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

    foreach ($runtime in $actualBridgeRuntimes) {
        $bridgeRuntimeDir = Join-Path $bridgeRoot $runtime
        $stagedBridgePath = Join-Path $bridgeRuntimeDir 'index.js'
        if (-not (Test-Path -Path $stagedBridgePath -PathType Leaf)) {
            continue
        }

        Assert-BridgeSupportsBulkChannel -BridgeScriptPath $stagedBridgePath
        Assert-BridgeManifestMatchesBundle -BridgeDir $bridgeRuntimeDir -Runtime $runtime -ExpectedAppVersion $ExpectedAppVersion

        $nodeModulesPath = Join-Path $bridgeRuntimeDir 'node_modules'
        if (Test-Path -Path $nodeModulesPath) {
            throw "Package staging must not include bridge node_modules: $nodeModulesPath"
        }
    }
}

$tunaRoot = Join-Path $resolvedStageDir 'tuna'
if (Test-Path -Path $tunaRoot -PathType Container) {
    $actualTunaRuntimes = @(
        Get-ChildItem -Path $tunaRoot -Directory -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name |
            Sort-Object -Unique
    )

    $unexpectedTunaRuntimes = @(
        $actualTunaRuntimes |
            Where-Object { -not $expectedTunaRuntimes.Contains($_) }
    )

    if ($unexpectedTunaRuntimes.Count -gt 0) {
        throw "Package staging contains unexpected Tuna runtime directories: $($unexpectedTunaRuntimes -join ', ')"
    }

    foreach ($runtime in $actualTunaRuntimes) {
        Assert-TunaSidecarManifestMatchesBundle -TunaDir (Join-Path $tunaRoot $runtime) -Runtime $runtime -ExpectedAppVersion $ExpectedAppVersion
    }
}

Write-Host "[nLink] Package manifest verified: $resolvedManifestPath"
Write-Host "[nLink] Stage directory verified: $resolvedStageDir"
Write-Host "[nLink] Required entries: $($manifestEntries.Count)"
