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
    [switch]$SkipTunaSidecarBundle,
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
        $hash = Get-Sha256FileHash -Path $file.FullName
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
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [string]$ExpectedAppVersion = ""
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

        foreach ($requiredFile in @("package.json", "package-lock.json", "bridge-manifest.json", "bridge-dependencies.json")) {
            if (-not (Test-Path (Join-Path $BridgeDir $requiredFile))) {
                throw "Bridge runtime not found. Run the bridge bundle build step first."
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($ExpectedAppVersion)) {
            $manifestPath = Join-Path $BridgeDir "bridge-manifest.json"
            $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
            $actualAppVersion = [string]$manifest.appVersion
            if ($actualAppVersion -ne $ExpectedAppVersion) {
                throw "Bridge runtime version mismatch: expected bridge-manifest.json appVersion '$ExpectedAppVersion', got '$actualAppVersion'. Rebuild the bridge bundle after changing VERSION: $manifestPath"
            }
        }

        if (Test-Path (Join-Path $BridgeDir "node_modules")) {
            throw "Bridge runtime must not ship node_modules. Rebuild the bridge bundle."
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
        [string]$ExpectedAppVersion = "",
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
        Assert-BridgeBundleRuntime -BridgeDir (Join-Path (Join-Path $StageDir "bridge") $Runtime) -ExpectedAppVersion $ExpectedAppVersion
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

function Build-TunaSidecarToPortable {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$PortableOutDir,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if ($Runtime -ne "win-x64") {
        throw "Tuna sidecar packaging currently supports only win-x64, got '$Runtime'."
    }

    $sidecarSourceDir = Join-Path $RepoRoot "tools\nkn-tuna-sidecar"
    if (-not (Test-Path (Join-Path $sidecarSourceDir "go.mod"))) {
        throw "Tuna sidecar source not found: $sidecarSourceDir"
    }

    $goCommand = Get-Command go -ErrorAction SilentlyContinue
    if (-not $goCommand) {
        throw "Go toolchain not found. Install Go; Tuna sidecar packaging is required for release and installer builds."
    }

    $destinationDir = Join-Path (Join-Path $PortableOutDir "tuna") $Runtime
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    $destinationExe = Join-Path $destinationDir "nlink-tuna-sidecar.exe"

    Write-Host "[nLink] Building Tuna sidecar verifier: $destinationExe" -ForegroundColor Cyan
    Push-Location $sidecarSourceDir
    try {
        $previousGoos = $env:GOOS
        $previousGoarch = $env:GOARCH
        $env:GOOS = "windows"
        $env:GOARCH = "amd64"
        $ldflags = "-s -w -X main.sidecarVersion=$Version"
        & go build -ldflags $ldflags -o $destinationExe .
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
    finally {
        $env:GOOS = $previousGoos
        $env:GOARCH = $previousGoarch
        Pop-Location
    }

    if (-not (Test-Path $destinationExe)) {
        throw "Tuna sidecar build did not produce expected executable: $destinationExe"
    }

    $versionLines = @(& $destinationExe version --jsonl)
    if ($LASTEXITCODE -ne 0) {
        throw "Tuna sidecar version probe failed for '$destinationExe'."
    }

    $versionEvent = $null
    foreach ($line in $versionLines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $candidate = $line | ConvertFrom-Json
        if ([string]$candidate.event -eq "sidecar_version") {
            $versionEvent = $candidate
            break
        }
    }

    if ($null -eq $versionEvent) {
        throw "Tuna sidecar version probe did not emit sidecar_version JSONL."
    }

    if ([string]$versionEvent.sidecarVersion -ne $Version) {
        throw "Tuna sidecar version mismatch: expected '$Version', got '$($versionEvent.sidecarVersion)'."
    }

    if ([string]$versionEvent.runtime -ne $Runtime) {
        throw "Tuna sidecar runtime mismatch: expected '$Runtime', got '$($versionEvent.runtime)'."
    }

    if ([int]$versionEvent.appProtocolVersion -ne 1 -or [int]$versionEvent.frameProtocolVersion -ne 1) {
        throw "Tuna sidecar protocol mismatch: app='$($versionEvent.appProtocolVersion)', frame='$($versionEvent.frameProtocolVersion)'."
    }

    $manifestPath = Join-Path $destinationDir "tuna-sidecar-manifest.json"
    $manifest = [ordered]@{
        manifestVersion      = 1
        appVersion           = $Version
        sidecarVersion       = [string]$versionEvent.sidecarVersion
        runtime              = $Runtime
        appProtocolVersion   = [int]$versionEvent.appProtocolVersion
        frameProtocolVersion = [int]$versionEvent.frameProtocolVersion
        sidecarExeSha256     = Get-Sha256FileHash -Path $destinationExe
        buildTimestampUtc    = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

    Write-Host "[nLink] Bundled Tuna sidecar verifier: $destinationExe" -ForegroundColor Green
    Write-Host "[nLink] Bundled Tuna sidecar manifest: $manifestPath" -ForegroundColor Green
}

function Copy-BridgeBundleToPortable {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [Parameter(Mandatory = $true)][string]$PortableOutDir,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [string]$ExpectedAppVersion = ""
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
    Assert-BridgeBundleRuntime -BridgeDir $destination -ExpectedAppVersion $ExpectedAppVersion
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
$bridgeBundleScriptPath = Join-Path $PSScriptRoot "Build-BridgeBundle.ps1"
$verifyPackageManifestPath = Join-Path $repoRoot "build\verify-package-manifest.ps1"
$packageManifestPath = Join-Path $repoRoot ("installer\package-manifest.{0}.txt" -f $Runtime)

New-Item -ItemType Directory -Force -Path $canonicalOutAbs | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipOutAbs) | Out-Null
if (-not $SkipBridgeBundle -and -not (Test-Path $bridgeBundleScriptPath)) {
    throw "Bridge bundle build script not found: $bridgeBundleScriptPath"
}
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
    Write-Host "[nLink] Building bundled NKN bridge runtime..." -ForegroundColor Cyan
    & $bridgeBundleScriptPath -Runtime $Runtime -OutDir $BridgeBundleDir
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Assert-BridgeBundleRuntime -BridgeDir $bridgeBundleAbs -ExpectedAppVersion $resolvedVersion
    Copy-BridgeBundleToPortable -BridgeDir $bridgeBundleAbs -PortableOutDir $canonicalOutAbs -Runtime $Runtime -ExpectedAppVersion $resolvedVersion
}

if (-not $SkipTunaSidecarBundle) {
    Build-TunaSidecarToPortable -RepoRoot $repoRoot -PortableOutDir $canonicalOutAbs -Runtime $Runtime -Version $resolvedVersion
}

Ensure-PortableConfigFile -RepoRoot $repoRoot -StageDir $canonicalOutAbs

# Safe size reduction in final artifact only (keep bin/obj untouched).
Remove-StagedDebugFiles -RootDir $canonicalOutAbs
Assert-PortableStagePayload -StageDir $canonicalOutAbs -Runtime $Runtime -ExpectedAppVersion $resolvedVersion -RequireBridge:(-not $SkipBridgeBundle)
& $verifyPackageManifestPath -StageDir $canonicalOutAbs -ManifestPath $packageManifestPath -ExpectedAppVersion $resolvedVersion

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
$bridgeScriptAbs = Join-Path $bridgeRidAbs "index.js"
$bridgeNodeAbs = Join-Path $bridgeRidAbs "node.exe"

$totalSizeBytes = Get-DirectorySizeBytes -RootDir $canonicalOutAbs
$bridgeSizeBytes = Get-DirectorySizeBytes -RootDir $bridgeRootAbs
$bridgeScriptSizeBytes = Get-PathSizeBytes -Path $bridgeScriptAbs
$bridgeNodeSizeBytes = Get-PathSizeBytes -Path $bridgeNodeAbs
$appExeSizeBytes = Get-PathSizeBytes -Path (Join-Path $canonicalOutAbs "nLink.exe")
$ffmpegSizeBytes = Get-PathSizeBytes -Path (Join-Path $canonicalOutAbs "ffmpeg")
$portableZipSizeBytes = Get-PathSizeBytes -Path $zipOutAbs

Write-Host "[nLink] Size summary:" -ForegroundColor Cyan
Write-Host ("  optimize for: {0}" -f $OptimizeFor)
Write-Host ("  single-file compression: {0}" -f ($(if ($singleFileCompressionEnabled) { 1 } else { 0 })))
Write-Host ("  total output size: {0} ({1} bytes)" -f (Format-Size $totalSizeBytes), $totalSizeBytes)
Write-Host ("  app executable size: {0} ({1} bytes)" -f (Format-Size $appExeSizeBytes), $appExeSizeBytes)
Write-Host ("  bridge folder size: {0} ({1} bytes)" -f (Format-Size $bridgeSizeBytes), $bridgeSizeBytes)
Write-Host ("  bridge/{0}/index.js size: {1} ({2} bytes)" -f $Runtime, (Format-Size $bridgeScriptSizeBytes), $bridgeScriptSizeBytes)
Write-Host ("  bridge/{0}/node.exe size: {1} ({2} bytes)" -f $Runtime, (Format-Size $bridgeNodeSizeBytes), $bridgeNodeSizeBytes)
Write-Host ("  ffmpeg folder size: {0} ({1} bytes)" -f (Format-Size $ffmpegSizeBytes), $ffmpegSizeBytes)
Write-Host ("  portable ZIP size: {0} ({1} bytes)" -f (Format-Size $portableZipSizeBytes), $portableZipSizeBytes)
Write-Host ("  bridge/{0}/node_modules shipped: 0" -f $Runtime)
