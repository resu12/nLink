param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CanonicalPortableOutDir = "artifacts/portable/nLink/win-x64",
    [string]$PortableZipPath = "",
    [string]$HelperPortableOutDir = "artifacts/portable/helper/win-x64",
    [string]$BridgeBundleDir = "artifacts/bridge/win-x64",
    [string]$InstallerOutDir = "artifacts/installer",
    [string]$ReleasesRootDir = "artifacts/releases",
    [string]$AppVersion = ""
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

function Copy-BridgeBundleToStaging {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [Parameter(Mandatory = $true)][string]$PublishOutDir
    )

    $destination = Join-Path $PublishOutDir (Join-Path "bridge" $Runtime)
    $bridgeRoot = Join-Path $PublishOutDir "bridge"
    if (Test-Path $bridgeRoot) {
        try {
            Invoke-WithRetry -OperationName "remove helper bridge staging" -Action {
                Remove-Item -Recurse -Force $bridgeRoot
            }
        }
        catch {
            if (Test-Path $destination) {
                try {
                    Assert-BridgeBundleRuntime -BridgeDir $destination
                    Write-Warning "[nLink] Reusing existing helper bridge staging due file lock during cleanup: $destination"
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
    Write-Host "[nLink] Copied bridge runtime into helper staging: $destination" -ForegroundColor Green
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
New-Item -ItemType Directory -Force -Path $helperPortableOutAbs | Out-Null
New-Item -ItemType Directory -Force -Path $installerOutAbs | Out-Null
New-Item -ItemType Directory -Force -Path $releasesRootAbs | Out-Null

Write-Host "[nLink] Building canonical portable app output + ZIP..." -ForegroundColor Cyan
& $portableScriptPath `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -CanonicalOutDir $CanonicalPortableOutDir `
    -ZipOutPath $PortableZipPath `
    -Version $resolvedVersion `
    -HelperAliasOutDir $HelperPortableOutDir `
    -CopyHelperAlias
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Assert-BridgeBundleRuntime -BridgeDir $bridgeBundleAbs
Copy-BridgeBundleToStaging -BridgeDir $bridgeBundleAbs -PublishOutDir $helperPortableOutAbs

# Safe size reduction in installer staging only (leave bin/obj untouched).
Remove-StagedDebugFiles -RootDir $helperPortableOutAbs
Assert-InstallerStagePayload -StageDir $helperPortableOutAbs -Runtime $Runtime
& $verifyPackageManifestPath -StageDir $helperPortableOutAbs -ManifestPath $packageManifestPath

$isccPath = Resolve-IsccPath
if (-not $isccPath) {
    Write-Warning "Inno Setup compiler (ISCC.exe) was not found."
    Write-Warning "Portable ZIP is ready at: $portableZipAbs"
    Write-Warning "Helper staging folder is ready at: $helperPortableOutAbs"
    Write-Warning "Install Inno Setup 6 and re-run this script to build the installer."
    exit 1
}

Write-Host "[nLink] Building installer with Inno Setup..." -ForegroundColor Cyan
& $isccPath "/DSourceDir=$helperPortableOutAbs" "/DOutDir=$installerOutAbs" "/DAppVersion=$AppVersion" $issPath

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# Packaging validation step: inspect staging folder used for installer packaging.
Assert-BridgeBundleRuntime -BridgeDir (Join-Path (Join-Path $helperPortableOutAbs "bridge") $Runtime)
$installerExeAbs = Join-Path $installerOutAbs ("nLink-Setup-win-x64-{0}.exe" -f $resolvedVersion)
$releasePublish = Publish-ReleaseAssets `
    -RepoRoot $repoRoot `
    -Version $resolvedVersion `
    -ReleasesRootDir $ReleasesRootDir `
    -AssetPaths @($portableZipAbs, $installerExeAbs)

Write-Host "[nLink] Canonical portable folder: $canonicalPortableOutAbs" -ForegroundColor Green
Write-Host "[nLink] Portable ZIP: $portableZipAbs" -ForegroundColor Green
Write-Host "[nLink] Installer output folder: $installerOutAbs" -ForegroundColor Green
Write-Host "[nLink] Release version: $resolvedVersion" -ForegroundColor Green
Write-Host "[nLink] Release assets folder: $($releasePublish.ReleaseDir)" -ForegroundColor Green
Write-Host "[nLink] SHA256SUMS: $($releasePublish.ChecksumsPath)" -ForegroundColor Green
foreach ($asset in @($releasePublish.Assets)) {
    if (-not [string]::IsNullOrWhiteSpace($asset)) {
        Write-Host "[nLink] Release asset: $asset" -ForegroundColor Green
    }
}

$bridgeRootAbs = Join-Path $helperPortableOutAbs "bridge"
$bridgeRidAbs = Join-Path $bridgeRootAbs $Runtime
$nodeModulesAbs = Join-Path $bridgeRidAbs "node_modules"

$totalSizeBytes = Get-DirectorySizeBytes -RootDir $helperPortableOutAbs
$bridgeSizeBytes = Get-DirectorySizeBytes -RootDir $bridgeRootAbs
$nodeModulesSizeBytes = Get-DirectorySizeBytes -RootDir $nodeModulesAbs

Write-Host "[nLink] Size summary (installer staging):" -ForegroundColor Cyan
Write-Host ("  total output size: {0} ({1} bytes)" -f (Format-Size $totalSizeBytes), $totalSizeBytes)
Write-Host ("  bridge folder size: {0} ({1} bytes)" -f (Format-Size $bridgeSizeBytes), $bridgeSizeBytes)
Write-Host ("  bridge/{0}/node_modules size: {1} ({2} bytes)" -f $Runtime, (Format-Size $nodeModulesSizeBytes), $nodeModulesSizeBytes)
