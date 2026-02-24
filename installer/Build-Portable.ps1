param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CanonicalOutDir = "artifacts/portable/nLink/win-x64",
    [string]$ZipOutPath = "",
    [string]$ReleasesRootDir = "artifacts/releases",
    [string]$BridgeBundleDir = "artifacts/bridge/win-x64",
    [string]$HelperAliasOutDir = "artifacts/portable/helper/win-x64",
    [string]$HelpeeAliasOutDir = "artifacts/portable/helpee/win-x64",
    [string]$Version = "",
    [switch]$SkipBridgeBundle,
    [switch]$CopyHelperAlias,
    [switch]$CopyHelpeeAlias
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ReleaseVersion {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$OverrideVersion = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($OverrideVersion)) {
        return $OverrideVersion.Trim()
    }

    $versionPath = Join-Path $RepoRoot "VERSION"
    if (-not (Test-Path $versionPath)) {
        throw "VERSION file not found at '$versionPath'."
    }

    $value = (Get-Content $versionPath -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "VERSION file is empty: '$versionPath'."
    }

    return $value
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [int]$MaxAttempts = 5,
        [int]$DelayMilliseconds = 400,
        [string]$OperationName = "operation"
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            & $Action
            return
        }
        catch {
            if ($attempt -ge $MaxAttempts) {
                throw
            }

            Write-Warning "[nLink] Retrying $OperationName ($attempt/$MaxAttempts) after file lock: $($_.Exception.Message)"
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Copy-DirectoryClean {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationDir
    )

    if (Test-Path $DestinationDir) {
        Invoke-WithRetry -OperationName "remove alias folder" -Action {
            Remove-Item -Recurse -Force $DestinationDir
        }
    }

    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $SourceDir "*") $DestinationDir
}

function Remove-StagedDebugFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir
    )

    if (-not (Test-Path $RootDir)) {
        return
    }

    Get-ChildItem -Path $RootDir -Recurse -File -Include *.pdb,*.xml -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Get-DirectorySizeBytes {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir
    )

    if (-not (Test-Path $RootDir)) {
        return [int64]0
    }

    $sum = 0L
    foreach ($file in Get-ChildItem -Path $RootDir -Recurse -File -ErrorAction SilentlyContinue) {
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

function Publish-ReleaseAssets {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$ReleasesRootDir,
        [Parameter(Mandatory = $true)][string[]]$AssetPaths
    )

    $releaseDir = Join-Path (Join-Path $RepoRoot $ReleasesRootDir) $Version
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

    $copiedAssets = @()
    foreach ($assetPath in $AssetPaths) {
        if ([string]::IsNullOrWhiteSpace($assetPath) -or -not (Test-Path $assetPath)) {
            continue
        }

        $destPath = Join-Path $releaseDir (Split-Path -Leaf $assetPath)
        Copy-Item -Force $assetPath $destPath
        $copiedAssets += $destPath
    }

    $checksumsPath = Join-Path $releaseDir "SHA256SUMS.txt"
    $checksumTargets = @(Get-ChildItem -Path $releaseDir -File | Where-Object {
            $_.Name -ne "SHA256SUMS.txt" -and
            ($_.Name -like "nLink-Portable-*.zip" -or $_.Name -like "nLink-Setup-*.exe")
        } | Sort-Object Name)

    $lines = @()
    foreach ($file in $checksumTargets) {
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$hash  $($file.Name)"
    }

    Set-Content -Path $checksumsPath -Value $lines -Encoding ascii

    return [pscustomobject]@{
        ReleaseDir = $releaseDir
        ChecksumsPath = $checksumsPath
        Assets = $copiedAssets
    }
}

function Assert-BridgeBundleRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeDir
    )

    if (-not (Test-Path $BridgeDir)) {
        throw "Bridge runtime not found. Run the bridge bundle build step first."
    }

    $items = @(Get-ChildItem -Path $BridgeDir -Force -ErrorAction SilentlyContinue)
    if (-not $items -or $items.Count -eq 0) {
        throw "Bridge runtime not found. Run the bridge bundle build step first."
    }

    $indexJs = Join-Path $BridgeDir "index.js"
    $bridgeExe = Join-Path $BridgeDir "nkn-bridge.exe"
    if (-not (Test-Path $indexJs) -and -not (Test-Path $bridgeExe)) {
        throw "Bridge runtime not found. Run the bridge bundle build step first."
    }

    if (Test-Path $indexJs) {
        $nodeExe = Join-Path $BridgeDir "node.exe"
        if (-not (Test-Path $nodeExe)) {
            throw "Bridge runtime not found. Run the bridge bundle build step first."
        }
    }
}

function Copy-BridgeBundleToPortable {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [Parameter(Mandatory = $true)][string]$PortableOutDir,
        [Parameter(Mandatory = $true)][string]$Runtime
    )

    $bridgeRoot = Join-Path $PortableOutDir "bridge"
    $destination = Join-Path $bridgeRoot $Runtime

    if (Test-Path $bridgeRoot) {
        try {
            Invoke-WithRetry -OperationName "remove bundled bridge folder" -Action {
                Remove-Item -Recurse -Force $bridgeRoot
            }
        }
        catch {
            if (Test-Path $destination) {
                try {
                    Assert-BridgeBundleRuntime -BridgeDir $destination
                    Write-Warning "[nLink] Reusing existing bundled bridge folder due file lock during cleanup: $destination"
                    return
                }
                catch {
                    throw
                }
            }

            throw
        }
    }

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -Recurse -Force (Join-Path $BridgeDir "*") $destination
    Assert-BridgeBundleRuntime -BridgeDir $destination
    Write-Host "[nLink] Bundled bridge runtime into canonical portable: $destination" -ForegroundColor Green
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "src\nLink.App\nLink.App.csproj"
$resolvedVersion = Get-ReleaseVersion -RepoRoot $repoRoot -OverrideVersion $Version
if ([string]::IsNullOrWhiteSpace($ZipOutPath)) {
    $ZipOutPath = "artifacts/portable/nLink-Portable-$Runtime-$resolvedVersion.zip"
}
$canonicalOutAbs = Join-Path $repoRoot $CanonicalOutDir
$zipOutAbs = Join-Path $repoRoot $ZipOutPath
$bridgeBundleAbs = Join-Path $repoRoot $BridgeBundleDir
$releasesRootAbs = Join-Path $repoRoot $ReleasesRootDir
$helperAliasAbs = Join-Path $repoRoot $HelperAliasOutDir
$helpeeAliasAbs = Join-Path $repoRoot $HelpeeAliasOutDir

New-Item -ItemType Directory -Force -Path $canonicalOutAbs | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipOutAbs) | Out-Null
New-Item -ItemType Directory -Force -Path $releasesRootAbs | Out-Null

Write-Host "[nLink] Publishing canonical portable app folder..." -ForegroundColor Cyan
dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $canonicalOutAbs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not $SkipBridgeBundle) {
    Assert-BridgeBundleRuntime -BridgeDir $bridgeBundleAbs
    Copy-BridgeBundleToPortable -BridgeDir $bridgeBundleAbs -PortableOutDir $canonicalOutAbs -Runtime $Runtime
}

# Safe size reduction in final artifact only (keep bin/obj untouched).
Remove-StagedDebugFiles -RootDir $canonicalOutAbs

if (Test-Path $zipOutAbs) {
    Invoke-WithRetry -OperationName "remove portable zip" -Action {
        Remove-Item -Force $zipOutAbs
    }
}

Write-Host "[nLink] Creating portable ZIP..." -ForegroundColor Cyan
Invoke-WithRetry -OperationName "create portable zip" -DelayMilliseconds 700 -Action {
    Compress-Archive -Path (Join-Path $canonicalOutAbs "*") -DestinationPath $zipOutAbs -CompressionLevel Optimal
}

if ($CopyHelperAlias) {
    Copy-DirectoryClean -SourceDir $canonicalOutAbs -DestinationDir $helperAliasAbs
    Write-Host "[nLink] Copied helper alias from canonical portable: $helperAliasAbs" -ForegroundColor Green
}

if ($CopyHelpeeAlias) {
    Copy-DirectoryClean -SourceDir $canonicalOutAbs -DestinationDir $helpeeAliasAbs
    Write-Host "[nLink] Copied helpee alias from canonical portable: $helpeeAliasAbs" -ForegroundColor Green
}

Write-Host "[nLink] Canonical portable folder: $canonicalOutAbs" -ForegroundColor Green
Write-Host "[nLink] Portable ZIP: $zipOutAbs" -ForegroundColor Green
Write-Host "[nLink] Release version: $resolvedVersion" -ForegroundColor Green

$releasePublish = Publish-ReleaseAssets `
    -RepoRoot $repoRoot `
    -Version $resolvedVersion `
    -ReleasesRootDir $ReleasesRootDir `
    -AssetPaths @($zipOutAbs)

Write-Host "[nLink] Release assets folder: $($releasePublish.ReleaseDir)" -ForegroundColor Green
Write-Host "[nLink] SHA256SUMS: $($releasePublish.ChecksumsPath)" -ForegroundColor Green

$bridgeRootAbs = Join-Path $canonicalOutAbs "bridge"
$bridgeRidAbs = Join-Path $bridgeRootAbs $Runtime
$nodeModulesAbs = Join-Path $bridgeRidAbs "node_modules"

$totalSizeBytes = Get-DirectorySizeBytes -RootDir $canonicalOutAbs
$bridgeSizeBytes = Get-DirectorySizeBytes -RootDir $bridgeRootAbs
$nodeModulesSizeBytes = Get-DirectorySizeBytes -RootDir $nodeModulesAbs

Write-Host "[nLink] Size summary:" -ForegroundColor Cyan
Write-Host ("  total output size: {0} ({1} bytes)" -f (Format-Size $totalSizeBytes), $totalSizeBytes)
Write-Host ("  bridge folder size: {0} ({1} bytes)" -f (Format-Size $bridgeSizeBytes), $bridgeSizeBytes)
Write-Host ("  bridge/{0}/node_modules size: {1} ({2} bytes)" -f $Runtime, (Format-Size $nodeModulesSizeBytes), $nodeModulesSizeBytes)
