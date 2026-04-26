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
    [ValidateSet("DownloadSize", "InstalledSize")]
    [string]$OptimizeFor = "DownloadSize",
    [switch]$SkipBridgeBundle,
    [switch]$CopyHelperAlias,
    [switch]$CopyHelpeeAlias,
    [switch]$LocalOnly
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
    Invoke-WithRetry -OperationName "copy alias folder" -DelayMilliseconds 700 -Action {
        Copy-Item -Recurse -Force (Join-Path $SourceDir "*") $DestinationDir
    }
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

function Get-PathSizeBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path $Path)) {
        return [int64]0
    }

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer) {
        return [int64]$item.Length
    }

    return Get-DirectorySizeBytes -RootDir $item.FullName
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

function Assert-NoDebugOnlyPayload {
    param(
        [Parameter(Mandatory = $true)][string]$StageDir
    )

    $forbiddenFiles = @(
        "Avalonia.Diagnostics.dll",
        "nLink.runtimeconfig.dev.json"
    )

    foreach ($fileName in $forbiddenFiles) {
        $matches = @(Get-ChildItem -Path $StageDir -Recurse -File -Filter $fileName -ErrorAction SilentlyContinue)
        if ($matches.Count -gt 0) {
            $paths = @($matches | ForEach-Object { $_.FullName }) -join ", "
            throw "Release staging contains debug-only dependency '$fileName': $paths"
        }
    }

    $symbolLikeFiles = @(Get-ChildItem -Path $StageDir -Recurse -File -Include *.pdb,*.xml -ErrorAction SilentlyContinue)
    if ($symbolLikeFiles.Count -gt 0) {
        $paths = @($symbolLikeFiles | ForEach-Object { $_.FullName }) -join ", "
        throw "Release staging contains debug-only files: $paths"
    }
}

function Assert-PortableStagePayload {
    param(
        [Parameter(Mandatory = $true)][string]$StageDir,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [bool]$RequireBridge = $true
    )

    $appExe = Join-Path $StageDir "nLink.exe"
    $appSettings = Join-Path $StageDir "appsettings.json"
    if (-not (Test-Path $appExe)) {
        throw "Portable app executable not found: $appExe"
    }

    if (-not (Test-Path $appSettings)) {
        throw "Portable appsettings.json not found: $appSettings"
    }

    if ($RequireBridge) {
        Assert-BridgeBundleRuntime -BridgeDir (Join-Path (Join-Path $StageDir "bridge") $Runtime)
    }

    Assert-NoDebugOnlyPayload -StageDir $StageDir
}

function Ensure-PortableConfigFile {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$StageDir
    )

    $stageAppSettings = Join-Path $StageDir "appsettings.json"
    if (Test-Path $stageAppSettings) {
        return
    }

    $sourceAppSettings = Join-Path $RepoRoot "src\nLink.App\appsettings.json"
    if (-not (Test-Path $sourceAppSettings)) {
        throw "Source appsettings.json not found: $sourceAppSettings"
    }

    Copy-Item -Force $sourceAppSettings $stageAppSettings
    Write-Host "[nLink] Staged appsettings.json into portable output: $stageAppSettings" -ForegroundColor Green
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
        Invoke-WithRetry -OperationName "remove bundled bridge folder" -Action {
            Remove-Item -Recurse -Force $bridgeRoot
        }
    }

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Invoke-WithRetry -OperationName "copy bundled bridge folder" -DelayMilliseconds 700 -Action {
        Copy-Item -Recurse -Force (Join-Path $BridgeDir "*") $destination
    }
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
$verifyPackageManifestPath = Join-Path $repoRoot "build\verify-package-manifest.ps1"
$packageManifestPath = Join-Path $repoRoot ("installer\package-manifest.{0}.txt" -f $Runtime)

New-Item -ItemType Directory -Force -Path $canonicalOutAbs | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipOutAbs) | Out-Null
if (-not $LocalOnly) {
    New-Item -ItemType Directory -Force -Path $releasesRootAbs | Out-Null
}

$singleFileCompressionEnabled = [string]::Equals($OptimizeFor, "InstalledSize", [System.StringComparison]::OrdinalIgnoreCase)
$singleFileCompressionValue = if ($singleFileCompressionEnabled) { "true" } else { "false" }

if (Test-Path $canonicalOutAbs) {
    Invoke-WithRetry -OperationName "clean canonical portable folder" -Action {
        Remove-Item -Recurse -Force $canonicalOutAbs
    }
}

New-Item -ItemType Directory -Force -Path $canonicalOutAbs | Out-Null

$publishArgs = @(
    "publish",
    $projectPath,
    "-c",
    $Configuration,
    "-r",
    $Runtime,
    "--self-contained",
    "true",
    "/p:NLinkVersion=$resolvedVersion",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:EnableCompressionInSingleFile=$singleFileCompressionValue"
)

$publishArgs += @("-o", $canonicalOutAbs)

Write-Host "[nLink] Publishing canonical portable app folder..." -ForegroundColor Cyan
Write-Host "[nLink] Package optimization: $OptimizeFor." -ForegroundColor Cyan
Write-Host ("[nLink] Single-file compression: {0}." -f ($(if ($singleFileCompressionEnabled) { "enabled" } else { "disabled" }))) -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not $SkipBridgeBundle) {
    Assert-BridgeBundleRuntime -BridgeDir $bridgeBundleAbs
    Copy-BridgeBundleToPortable -BridgeDir $bridgeBundleAbs -PortableOutDir $canonicalOutAbs -Runtime $Runtime
}

Ensure-PortableConfigFile -RepoRoot $repoRoot -StageDir $canonicalOutAbs

# Safe size reduction in final artifact only (keep bin/obj untouched).
Remove-StagedDebugFiles -RootDir $canonicalOutAbs
Assert-PortableStagePayload -StageDir $canonicalOutAbs -Runtime $Runtime -RequireBridge:(-not $SkipBridgeBundle)
& $verifyPackageManifestPath -StageDir $canonicalOutAbs -ManifestPath $packageManifestPath

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
Write-Host "[nLink] OptimizeFor: $OptimizeFor" -ForegroundColor Green

if ($LocalOnly) {
    Write-Host "[nLink] LocalOnly: skipped release asset copy." -ForegroundColor Yellow
}
else {
    $releasePublish = Publish-ReleaseAssets `
        -RepoRoot $repoRoot `
        -Version $resolvedVersion `
        -ReleasesRootDir $ReleasesRootDir `
        -AssetPaths @($zipOutAbs)

    Write-Host "[nLink] Release assets folder: $($releasePublish.ReleaseDir)" -ForegroundColor Green
    Write-Host "[nLink] SHA256SUMS: $($releasePublish.ChecksumsPath)" -ForegroundColor Green
    foreach ($asset in @($releasePublish.Assets)) {
        if (-not [string]::IsNullOrWhiteSpace($asset)) {
            Write-Host "[nLink] Release asset: $asset" -ForegroundColor Green
        }
    }
}

$bridgeRootAbs = Join-Path $canonicalOutAbs "bridge"
$bridgeRidAbs = Join-Path $bridgeRootAbs $Runtime
$nodeModulesAbs = Join-Path $bridgeRidAbs "node_modules"

$totalSizeBytes = Get-DirectorySizeBytes -RootDir $canonicalOutAbs
$bridgeSizeBytes = Get-DirectorySizeBytes -RootDir $bridgeRootAbs
$nodeModulesSizeBytes = Get-DirectorySizeBytes -RootDir $nodeModulesAbs
$appExeSizeBytes = Get-PathSizeBytes -Path (Join-Path $canonicalOutAbs "nLink.exe")
$ffmpegSizeBytes = Get-PathSizeBytes -Path (Join-Path $canonicalOutAbs "ffmpeg")
$portableZipSizeBytes = Get-PathSizeBytes -Path $zipOutAbs

Write-Host "[nLink] Size summary:" -ForegroundColor Cyan
Write-Host ("  optimize for: {0}" -f $OptimizeFor)
Write-Host ("  single-file compression: {0}" -f ($(if ($singleFileCompressionEnabled) { 1 } else { 0 })))
Write-Host ("  total output size: {0} ({1} bytes)" -f (Format-Size $totalSizeBytes), $totalSizeBytes)
Write-Host ("  app executable size: {0} ({1} bytes)" -f (Format-Size $appExeSizeBytes), $appExeSizeBytes)
Write-Host ("  bridge folder size: {0} ({1} bytes)" -f (Format-Size $bridgeSizeBytes), $bridgeSizeBytes)
Write-Host ("  ffmpeg folder size: {0} ({1} bytes)" -f (Format-Size $ffmpegSizeBytes), $ffmpegSizeBytes)
Write-Host ("  portable ZIP size: {0} ({1} bytes)" -f (Format-Size $portableZipSizeBytes), $portableZipSizeBytes)
Write-Host ("  bridge/{0}/node_modules size: {1} ({2} bytes)" -f $Runtime, (Format-Size $nodeModulesSizeBytes), $nodeModulesSizeBytes)
