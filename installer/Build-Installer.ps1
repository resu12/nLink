param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CanonicalPortableOutDir = "artifacts/portable/nLink/win-x64",
    [string]$PortableZipPath = "",
    [string]$HelperPortableOutDir = "artifacts/portable/helper/win-x64",
    [string]$BridgeBundleDir = "artifacts/bridge/win-x64",
    [string]$InstallerOutDir = "artifacts/installer",
    [string]$ReleasesRootDir = "artifacts/releases",
    [string]$AppVersion = "",
    [ValidateSet("DownloadSize", "InstalledSize")]
    [string]$OptimizeFor = "DownloadSize",
    [switch]$CopyHelperAlias,
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

function Resolve-IsccPath {
    $registryInstallLocations = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    ) | ForEach-Object {
        Get-ItemProperty $_ -ErrorAction SilentlyContinue |
            Where-Object {
                $_.PSObject.Properties.Name -contains "DisplayName" -and
                $_.DisplayName -like "Inno Setup*"
            } |
            ForEach-Object { $_.InstallLocation }
    } | Where-Object { $_ }

    $candidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ }

    foreach ($installLocation in $registryInstallLocations | Select-Object -Unique) {
        $candidates += (Join-Path $installLocation "ISCC.exe")
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
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

function Assert-InstallerStagePayload {
    param(
        [Parameter(Mandatory = $true)][string]$StageDir,
        [Parameter(Mandatory = $true)][string]$Runtime
    )

    $appExe = Join-Path $StageDir "nLink.exe"
    $appSettings = Join-Path $StageDir "appsettings.json"
    if (-not (Test-Path $appExe)) {
        throw "Installer staging app executable not found: $appExe"
    }

    if (-not (Test-Path $appSettings)) {
        throw "Installer staging appsettings.json not found: $appSettings"
    }

    Assert-BridgeBundleRuntime -BridgeDir (Join-Path (Join-Path $StageDir "bridge") $Runtime)
    Assert-NoDebugOnlyPayload -StageDir $StageDir
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

function Write-PackageSizeSummary {
    param(
        [Parameter(Mandatory = $true)][string]$SummaryPath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$InstallerStageDir,
        [Parameter(Mandatory = $true)][string]$PortableZipPath,
        [Parameter(Mandatory = $true)][string]$InstallerExePath,
        [Parameter(Mandatory = $true)][string]$ReleasesRootDir,
        [Parameter(Mandatory = $true)][string]$OptimizeFor,
        [Parameter(Mandatory = $true)][bool]$SingleFileCompression,
        [bool]$LocalOnly
    )

    $previousSummary = @{}
    if (Test-Path $SummaryPath) {
        foreach ($line in Get-Content -Path $SummaryPath -ErrorAction SilentlyContinue) {
            $separatorIndex = $line.IndexOf('=')
            if ($separatorIndex -le 0) {
                continue
            }

            $key = $line.Substring(0, $separatorIndex)
            $value = $line.Substring($separatorIndex + 1)
            $previousSummary[$key] = $value
        }
    }

    $bridgeRoot = Join-Path $InstallerStageDir "bridge"
    $ffmpegRoot = Join-Path $InstallerStageDir "ffmpeg"
    $appExe = Join-Path $InstallerStageDir "nLink.exe"
    $releaseDir = Join-Path $ReleasesRootDir $Version

    $stageSize = Get-PathSizeBytes -Path $InstallerStageDir
    $installerSize = Get-PathSizeBytes -Path $InstallerExePath
    $portableZipSize = Get-PathSizeBytes -Path $PortableZipPath
    $appExeSize = Get-PathSizeBytes -Path $appExe
    $bridgeSize = Get-PathSizeBytes -Path $bridgeRoot
    $ffmpegSize = Get-PathSizeBytes -Path $ffmpegRoot
    $releaseSize = if ($LocalOnly) { [int64]0 } else { Get-PathSizeBytes -Path $releaseDir }
    $releaseDirText = if ($LocalOnly) { "(skipped)" } else { $releaseDir }
    $localOnlyValue = if ($LocalOnly) { 1 } else { 0 }
    $singleFileCompressionValue = if ($SingleFileCompression) { 1 } else { 0 }

    $lines = @(
        "generated_utc=$([DateTimeOffset]::UtcNow.ToString('u'))",
        "version=$Version",
        "runtime=$Runtime",
        "local_only=$localOnlyValue",
        "optimize_for=$OptimizeFor",
        "single_file_compression=$singleFileCompressionValue",
        "installer_stage_dir=$InstallerStageDir",
        "installer_stage_size_bytes=$stageSize",
        "installer_stage_size=$((Format-Size $stageSize))",
        "installer_exe=$InstallerExePath",
        "installer_exe_size_bytes=$installerSize",
        "installer_exe_size=$((Format-Size $installerSize))",
        "portable_zip=$PortableZipPath",
        "portable_zip_size_bytes=$portableZipSize",
        "portable_zip_size=$((Format-Size $portableZipSize))",
        "app_exe_size_bytes=$appExeSize",
        "app_exe_size=$((Format-Size $appExeSize))",
        "bridge_size_bytes=$bridgeSize",
        "bridge_size=$((Format-Size $bridgeSize))",
        "ffmpeg_size_bytes=$ffmpegSize",
        "ffmpeg_size=$((Format-Size $ffmpegSize))",
        "release_assets_dir=$releaseDirText",
        "release_assets_size_bytes=$releaseSize",
        "release_assets_size=$((Format-Size $releaseSize))"
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SummaryPath) | Out-Null
    Set-Content -Path $SummaryPath -Value $lines -Encoding ascii

    Write-Host "[nLink] Package size summary:" -ForegroundColor Cyan
    Write-Host ("  optimize for: {0}" -f $OptimizeFor)
    Write-Host ("  single-file compression: {0}" -f $singleFileCompressionValue)
    Write-Host ("  installer staging: {0} ({1} bytes)" -f (Format-Size $stageSize), $stageSize)
    Write-Host ("  app executable: {0} ({1} bytes)" -f (Format-Size $appExeSize), $appExeSize)
    Write-Host ("  bridge folder: {0} ({1} bytes)" -f (Format-Size $bridgeSize), $bridgeSize)
    Write-Host ("  ffmpeg folder: {0} ({1} bytes)" -f (Format-Size $ffmpegSize), $ffmpegSize)
    Write-Host ("  portable ZIP: {0} ({1} bytes)" -f (Format-Size $portableZipSize), $portableZipSize)
    Write-Host ("  installer EXE: {0} ({1} bytes)" -f (Format-Size $installerSize), $installerSize)
    Write-Host ("  release assets: {0} ({1} bytes)" -f (Format-Size $releaseSize), $releaseSize)
    if ($previousSummary.Count -gt 0) {
        Write-Host "[nLink] Previous package comparison:" -ForegroundColor Cyan
        $comparisonKeys = @(
            @{ Label = "installer EXE"; Key = "installer_exe_size_bytes"; NewBytes = $installerSize },
            @{ Label = "portable ZIP"; Key = "portable_zip_size_bytes"; NewBytes = $portableZipSize },
            @{ Label = "installer staging"; Key = "installer_stage_size_bytes"; NewBytes = $stageSize },
            @{ Label = "app executable"; Key = "app_exe_size_bytes"; NewBytes = $appExeSize }
        )

        foreach ($entry in $comparisonKeys) {
            if (-not $previousSummary.ContainsKey($entry.Key)) {
                continue
            }

            $oldBytes = 0L
            if (-not [int64]::TryParse([string]$previousSummary[$entry.Key], [ref]$oldBytes)) {
                continue
            }

            $deltaBytes = [int64]$entry.NewBytes - $oldBytes
            $sign = if ($deltaBytes -ge 0) { "+" } else { "-" }
            Write-Host ("  {0}: {1} -> {2} ({3}{4})" -f $entry.Label, (Format-Size $oldBytes), (Format-Size ([int64]$entry.NewBytes)), $sign, (Format-Size ([Math]::Abs($deltaBytes))))
        }
    }

    Write-Host "[nLink] Package size summary file: $SummaryPath" -ForegroundColor Green
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

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedVersion = Get-ReleaseVersion -RepoRoot $repoRoot -OverrideVersion $AppVersion
$AppVersion = $resolvedVersion
if ([string]::IsNullOrWhiteSpace($PortableZipPath)) {
    $PortableZipPath = "artifacts/portable/nLink-Portable-$Runtime-$resolvedVersion.zip"
}
$canonicalPortableOutAbs = Join-Path $repoRoot $CanonicalPortableOutDir
$portableZipAbs = Join-Path $repoRoot $PortableZipPath
$helperPortableOutAbs = Join-Path $repoRoot $HelperPortableOutDir
$bridgeBundleAbs = Join-Path $repoRoot $BridgeBundleDir
$installerOutAbs = Join-Path $repoRoot $InstallerOutDir
$releasesRootAbs = Join-Path $repoRoot $ReleasesRootDir
$issPath = Join-Path $PSScriptRoot "nLink.iss"
$portableScriptPath = Join-Path $PSScriptRoot "Build-Portable.ps1"
$verifyPackageManifestPath = Join-Path $repoRoot "build\verify-package-manifest.ps1"
$packageManifestPath = Join-Path $repoRoot ("installer\package-manifest.{0}.txt" -f $Runtime)

New-Item -ItemType Directory -Force -Path $canonicalPortableOutAbs | Out-Null
New-Item -ItemType Directory -Force -Path $installerOutAbs | Out-Null
if ($CopyHelperAlias) {
    New-Item -ItemType Directory -Force -Path $helperPortableOutAbs | Out-Null
}
if (-not $LocalOnly) {
    New-Item -ItemType Directory -Force -Path $releasesRootAbs | Out-Null
}

$singleFileCompressionEnabled = [string]::Equals($OptimizeFor, "InstalledSize", [System.StringComparison]::OrdinalIgnoreCase)

Write-Host "[nLink] Building canonical portable app output + ZIP..." -ForegroundColor Cyan
& $portableScriptPath `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -CanonicalOutDir $CanonicalPortableOutDir `
    -ZipOutPath $PortableZipPath `
    -Version $resolvedVersion `
    -HelperAliasOutDir $HelperPortableOutDir `
    -OptimizeFor $OptimizeFor `
    -CopyHelperAlias:$CopyHelperAlias `
    -LocalOnly:$LocalOnly
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Assert-BridgeBundleRuntime -BridgeDir $bridgeBundleAbs

$installerStageAbs = $canonicalPortableOutAbs
Assert-InstallerStagePayload -StageDir $installerStageAbs -Runtime $Runtime
& $verifyPackageManifestPath -StageDir $installerStageAbs -ManifestPath $packageManifestPath

$isccPath = Resolve-IsccPath
if (-not $isccPath) {
    Write-Warning "Inno Setup compiler (ISCC.exe) was not found."
    Write-Warning "Portable ZIP is ready at: $portableZipAbs"
    Write-Warning "Installer staging folder is ready at: $installerStageAbs"
    Write-Warning "Install Inno Setup 6 and re-run this script to build the installer."
    exit 1
}

Write-Host "[nLink] Building installer with Inno Setup..." -ForegroundColor Cyan
& $isccPath "/DSourceDir=$installerStageAbs" "/DOutDir=$installerOutAbs" "/DAppVersion=$AppVersion" $issPath

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# Packaging validation step: inspect staging folder used for installer packaging.
Assert-BridgeBundleRuntime -BridgeDir (Join-Path (Join-Path $installerStageAbs "bridge") $Runtime)
$installerExeAbs = Join-Path $installerOutAbs ("nLink-Setup-win-x64-{0}.exe" -f $resolvedVersion)
if ($LocalOnly) {
    $releasePublish = $null
    Write-Host "[nLink] LocalOnly: skipped release asset copy." -ForegroundColor Yellow
}
else {
    $releasePublish = Publish-ReleaseAssets `
        -RepoRoot $repoRoot `
        -Version $resolvedVersion `
        -ReleasesRootDir $ReleasesRootDir `
        -AssetPaths @($portableZipAbs, $installerExeAbs)
}

Write-Host "[nLink] Canonical portable folder: $canonicalPortableOutAbs" -ForegroundColor Green
Write-Host "[nLink] Portable ZIP: $portableZipAbs" -ForegroundColor Green
Write-Host "[nLink] Installer output folder: $installerOutAbs" -ForegroundColor Green
Write-Host "[nLink] Release version: $resolvedVersion" -ForegroundColor Green
Write-Host "[nLink] OptimizeFor: $OptimizeFor" -ForegroundColor Green
if ($releasePublish) {
    Write-Host "[nLink] Release assets folder: $($releasePublish.ReleaseDir)" -ForegroundColor Green
    Write-Host "[nLink] SHA256SUMS: $($releasePublish.ChecksumsPath)" -ForegroundColor Green
    foreach ($asset in @($releasePublish.Assets)) {
        if (-not [string]::IsNullOrWhiteSpace($asset)) {
            Write-Host "[nLink] Release asset: $asset" -ForegroundColor Green
        }
    }
}

$bridgeRootAbs = Join-Path $installerStageAbs "bridge"
$bridgeRidAbs = Join-Path $bridgeRootAbs $Runtime
$nodeModulesAbs = Join-Path $bridgeRidAbs "node_modules"

$totalSizeBytes = Get-DirectorySizeBytes -RootDir $installerStageAbs
$bridgeSizeBytes = Get-DirectorySizeBytes -RootDir $bridgeRootAbs
$nodeModulesSizeBytes = Get-DirectorySizeBytes -RootDir $nodeModulesAbs

Write-Host "[nLink] Size summary (installer staging):" -ForegroundColor Cyan
Write-Host ("  optimize for: {0}" -f $OptimizeFor)
Write-Host ("  single-file compression: {0}" -f ($(if ($singleFileCompressionEnabled) { 1 } else { 0 })))
Write-Host ("  total output size: {0} ({1} bytes)" -f (Format-Size $totalSizeBytes), $totalSizeBytes)
Write-Host ("  bridge folder size: {0} ({1} bytes)" -f (Format-Size $bridgeSizeBytes), $bridgeSizeBytes)
Write-Host ("  bridge/{0}/node_modules size: {1} ({2} bytes)" -f $Runtime, (Format-Size $nodeModulesSizeBytes), $nodeModulesSizeBytes)

$packageSizeSummaryPath = Join-Path $installerOutAbs "package-size-summary.txt"
Write-PackageSizeSummary `
    -SummaryPath $packageSizeSummaryPath `
    -Version $resolvedVersion `
    -Runtime $Runtime `
    -InstallerStageDir $installerStageAbs `
    -PortableZipPath $portableZipAbs `
    -InstallerExePath $installerExeAbs `
    -ReleasesRootDir $releasesRootAbs `
    -OptimizeFor $OptimizeFor `
    -SingleFileCompression:$singleFileCompressionEnabled `
    -LocalOnly:$LocalOnly
