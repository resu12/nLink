param(
    [string]$Runtime = "win-x64",
    [string]$OutDir = "artifacts/bridge/win-x64",
    [switch]$UseSystemNode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$PinnedNodeVersion = "24.13.1"
$PinnedNodeWinX64ArchiveSha256 = "fba577c4bb87df04d54dd87bbdaa5a2272f1f99a2acbf9152e1a91b8b5f0b279"

function Get-BridgeRuntimeConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$NodeVersion
    )

    switch ($Runtime) {
        "win-x64" {
            $archiveBase = "node-v$NodeVersion-win-x64"
            return [pscustomobject]@{
                Runtime = $Runtime
                NodeExeName = "node.exe"
                NpmExeName = "npm.cmd"
                ArchiveFileName = "$archiveBase.zip"
                DownloadUrl = "https://nodejs.org/dist/v$NodeVersion/$archiveBase.zip"
                ExtractedFolderName = $archiveBase
                ArchiveSha256 = $PinnedNodeWinX64ArchiveSha256
            }
        }
        default {
            throw "Only win-x64 bridge bundling is currently supported."
        }
    }
}

function Resolve-SystemNodePath {
    $candidate = (Get-Command node.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    if ($candidate -and (Test-Path $candidate)) {
        return (Resolve-Path $candidate).Path
    }

    foreach ($path in @(
        "C:\Program Files\nodejs\node.exe",
        "C:\Program Files (x86)\nodejs\node.exe"
    )) {
        if (Test-Path $path) {
            return (Resolve-Path $path).Path
        }
    }

    return $null
}

function Resolve-SystemNpmPath {
    $candidate = (Get-Command npm.cmd -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    if ($candidate -and (Test-Path $candidate)) {
        return (Resolve-Path $candidate).Path
    }

    foreach ($path in @(
        "C:\Program Files\nodejs\npm.cmd",
        "C:\Program Files (x86)\nodejs\npm.cmd"
    )) {
        if (Test-Path $path) {
            return (Resolve-Path $path).Path
        }
    }

    return $null
}

function Ensure-PortableNodeToolchain {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$RuntimeConfig
    )

    $bootstrapRoot = Join-Path $RepoRoot ("artifacts\toolcache\node\" + $RuntimeConfig.Runtime)
    $archivePath = Join-Path $bootstrapRoot $RuntimeConfig.ArchiveFileName
    $extractDir = Join-Path $bootstrapRoot $RuntimeConfig.ExtractedFolderName
    $nodePath = Join-Path $extractDir $RuntimeConfig.NodeExeName
    $npmPath = Join-Path $extractDir $RuntimeConfig.NpmExeName
    $legacyArchivePath = Join-Path $RepoRoot ("tools\node\" + $RuntimeConfig.Runtime + "\" + $RuntimeConfig.ArchiveFileName)

    if ((Test-Path $nodePath) -and (Test-Path $npmPath)) {
        if (Test-Path $archivePath) {
            $cachedArchiveHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($cachedArchiveHash -ne $RuntimeConfig.ArchiveSha256) {
                throw "Pinned Node.js archive hash mismatch for $archivePath. Expected $($RuntimeConfig.ArchiveSha256), got $cachedArchiveHash."
            }
        }

        return [pscustomobject]@{
            NodePath = (Resolve-Path $nodePath).Path
            NpmPath = (Resolve-Path $npmPath).Path
            Source = "portable-cache"
            NodeArchiveSha256 = $RuntimeConfig.ArchiveSha256
        }
    }

    New-Item -ItemType Directory -Force -Path $bootstrapRoot | Out-Null

    if (-not (Test-Path $archivePath)) {
        if (Test-Path $legacyArchivePath) {
            Write-Host "[nLink] Migrating pinned Node.js archive from legacy local cache into ignored toolcache..." -ForegroundColor Cyan
            $legacyArchiveHash = (Get-FileHash -Path $legacyArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($legacyArchiveHash -ne $RuntimeConfig.ArchiveSha256) {
                throw "Pinned Node.js legacy archive hash mismatch for $legacyArchivePath. Expected $($RuntimeConfig.ArchiveSha256), got $legacyArchiveHash."
            }

            Copy-Item -Force $legacyArchivePath $archivePath
        }
        else {
            Write-Host "[nLink] Downloading pinned Node.js runtime $PinnedNodeVersion for $($RuntimeConfig.Runtime)..." -ForegroundColor Cyan
            Invoke-WebRequest -Uri $RuntimeConfig.DownloadUrl -OutFile $archivePath
        }
    }
    else {
        Write-Host "[nLink] Using cached Node.js archive: $archivePath" -ForegroundColor Cyan
    }

    $archiveHash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveHash -ne $RuntimeConfig.ArchiveSha256) {
        throw "Pinned Node.js archive hash mismatch for $archivePath. Expected $($RuntimeConfig.ArchiveSha256), got $archiveHash."
    }

    Write-Host "[nLink] Extracting Node.js runtime..." -ForegroundColor Cyan
    Expand-Archive -Path $archivePath -DestinationPath $bootstrapRoot -Force

    if (-not (Test-Path $nodePath) -or -not (Test-Path $npmPath)) {
        throw "Portable Node bootstrap failed: extracted node.exe/npm.cmd not found in $extractDir"
    }

    return [pscustomobject]@{
        NodePath = (Resolve-Path $nodePath).Path
        NpmPath = (Resolve-Path $npmPath).Path
        Source = "portable-bootstrap"
        NodeArchiveSha256 = $RuntimeConfig.ArchiveSha256
    }
}

function Resolve-OrBootstrapNodeToolchain {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$RuntimeConfig
    )

    if ($UseSystemNode) {
        $systemNode = Resolve-SystemNodePath
        $systemNpm = Resolve-SystemNpmPath
        if ($systemNode -and $systemNpm) {
            return [pscustomobject]@{
                NodePath = $systemNode
                NpmPath = $systemNpm
                Source = "system"
                NodeArchiveSha256 = "(system)"
            }
        }

        throw "-UseSystemNode was specified, but node.exe and npm.cmd were not both found."
    }

    return Ensure-PortableNodeToolchain -RepoRoot $RepoRoot -RuntimeConfig $RuntimeConfig
}

function Assert-BridgeBundleOutput {
    param(
        [Parameter(Mandatory = $true)][string]$OutDir
    )

    $indexJs = Join-Path $OutDir "index.js"
    $nodeExe = Join-Path $OutDir "node.exe"
    $packageJsonPath = Join-Path $OutDir "package.json"
    $packageLockPath = Join-Path $OutDir "package-lock.json"
    $manifestPath = Join-Path $OutDir "bridge-manifest.json"
    $dependenciesPath = Join-Path $OutDir "bridge-dependencies.json"
    if (-not (Test-Path $indexJs) -or
        -not (Test-Path $nodeExe) -or
        -not (Test-Path $packageJsonPath) -or
        -not (Test-Path $packageLockPath) -or
        -not (Test-Path $manifestPath) -or
        -not (Test-Path $dependenciesPath)) {
        throw "Bridge bundle validation failed: missing node.exe, index.js, package.json, package-lock.json, bridge-manifest.json, or bridge-dependencies.json in $OutDir"
    }

    $nodeModules = Join-Path $OutDir "node_modules"
    if (Test-Path $nodeModules) {
        throw "Bridge bundle validation failed: node_modules must not be shipped in $OutDir"
    }
}

function Get-NLinkAppVersion {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $versionPath = Join-Path $RepoRoot "VERSION"
    if (-not (Test-Path $versionPath)) {
        throw "VERSION file not found at repo root: $versionPath"
    }

    $version = (Get-Content -Path $versionPath -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "VERSION file is empty: $versionPath"
    }

    return $version
}

function Write-BridgeBundleManifest {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$BridgeSourceDir,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$OutDir,
        [Parameter(Mandatory = $true)][string]$NodePath,
        [Parameter(Mandatory = $true)][string]$NodeArchiveSha256,
        [Parameter(Mandatory = $true)][string]$PackageLockSha256,
        [Parameter(Mandatory = $true)][string]$NpmVersion,
        [Parameter(Mandatory = $true)][string]$NccVersion,
        [Parameter(Mandatory = $true)][string]$BridgePackageVersion
    )

    $scriptPath = Join-Path $OutDir "index.js"
    if (-not (Test-Path $scriptPath)) {
        throw "Bridge manifest generation failed: missing bundled script at $scriptPath"
    }

    $appVersion = Get-NLinkAppVersion -RepoRoot $RepoRoot
    $scriptSha256 = (Get-FileHash -Path $scriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $nodeVersion = (& $NodePath --version | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($nodeVersion)) {
        throw "Bridge manifest generation failed: could not resolve node version from $NodePath"
    }

    $manifest = [ordered]@{
        manifestVersion = 1
        appVersion = $appVersion
        runtime = $Runtime
        buildTimestampUtc = [DateTimeOffset]::UtcNow.ToString("O")
        bridgeScriptSha256 = $scriptSha256
        nodeVersion = $nodeVersion
        nodeArchiveSha256 = $NodeArchiveSha256
        packageLockSha256 = $PackageLockSha256
        npmVersion = $NpmVersion
        nccVersion = $NccVersion
        bridgePackageVersion = $BridgePackageVersion
        nodeModulesShipped = $false
        dependencyEvidenceFile = "bridge-dependencies.json"
        capabilities = [ordered]@{
            ownerPidWatchdog = $true
            killOnCloseJob = $true
        }
    }

    $manifestPath = Join-Path $OutDir "bridge-manifest.json"
    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))
}

function Write-BridgeDependencyEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeSourceDir,
        [Parameter(Mandatory = $true)][string]$OutDir,
        [Parameter(Mandatory = $true)][string]$NodePath,
        [Parameter(Mandatory = $true)][string]$NodeVersion,
        [Parameter(Mandatory = $true)][string]$NpmVersion,
        [Parameter(Mandatory = $true)][string]$NccVersion,
        [Parameter(Mandatory = $true)][string]$NodeArchiveSha256,
        [Parameter(Mandatory = $true)][string]$PackageLockSha256
    )

    $packageJsonPath = Join-Path $BridgeSourceDir "package.json"
    $lockPath = Join-Path $BridgeSourceDir "package-lock.json"
    $evidencePath = Join-Path $OutDir "bridge-dependencies.json"
    $evidenceScriptPath = Join-Path $OutDir "bridge-dependencies.generate.js"
    $evidenceScript = @'
const fs = require("fs");

const [
  packageJsonPath,
  lockPath,
  evidencePath,
  nodeVersion,
  npmVersion,
  nccVersion,
  nodeArchiveSha256,
  packageLockSha256
] = process.argv.slice(2);

const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
const lockJson = JSON.parse(fs.readFileSync(lockPath, "utf8"));
const lockedPackages = Object.entries(lockJson.packages || {})
  .filter(([name]) => name.startsWith("node_modules/"))
  .map(([name, pkg]) => ({
    name: name.slice("node_modules/".length),
    version: String(pkg.version || ""),
    resolved: String(pkg.resolved || ""),
    integrity: String(pkg.integrity || ""),
    dev: Boolean(pkg.dev),
    deprecated: String(pkg.deprecated || "")
  }));

const evidence = {
  evidenceVersion: 1,
  generatedUtc: new Date().toISOString(),
  packageName: String(packageJson.name || ""),
  packageVersion: String(packageJson.version || ""),
  nodeVersion,
  nodeArchiveSha256,
  npmVersion,
  nccVersion,
  packageLockSha256,
  nodeModulesShipped: false,
  dependencies: packageJson.dependencies || {},
  devDependencies: packageJson.devDependencies || {},
  lockedPackageCount: lockedPackages.length,
  lockedPackages
};

fs.writeFileSync(evidencePath, JSON.stringify(evidence, null, 2), "utf8");
'@

    [System.IO.File]::WriteAllText($evidenceScriptPath, $evidenceScript, [System.Text.UTF8Encoding]::new($false))
    try {
        & $NodePath $evidenceScriptPath $packageJsonPath $lockPath $evidencePath $NodeVersion $NpmVersion $NccVersion $NodeArchiveSha256 $PackageLockSha256
        if ($LASTEXITCODE -ne 0) {
            throw "Bridge dependency evidence generation failed."
        }
    }
    finally {
        if (Test-Path $evidenceScriptPath) {
            Remove-Item -Force $evidenceScriptPath
        }
    }
}

function Assert-BridgeBundleSupportsBulkChannel {
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
            throw "Bridge bundle validation failed: missing required bulk-channel marker '$marker' in $BridgeScriptPath"
        }
    }
}

function Get-DirectorySizeBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path $Path)) {
        return [int64]0
    }

    $files = Get-ChildItem -Path $Path -Recurse -File -Force -ErrorAction SilentlyContinue
    if ($null -eq $files) {
        return [int64]0
    }

    $sum = [int64]0
    foreach ($file in @($files)) {
        $sum += [int64]$file.Length
    }

    return $sum
}

function Format-Bytes {
    param(
        [Parameter(Mandatory = $true)][int64]$Bytes
    )

    if ($Bytes -ge 1GB) { return ("{0:N1} GB ({1} bytes)" -f ($Bytes / 1GB), $Bytes) }
    if ($Bytes -ge 1MB) { return ("{0:N1} MB ({1} bytes)" -f ($Bytes / 1MB), $Bytes) }
    if ($Bytes -ge 1KB) { return ("{0:N1} KB ({1} bytes)" -f ($Bytes / 1KB), $Bytes) }
    return ("{0} bytes" -f $Bytes)
}

function Resolve-BridgeBundlerCliPath {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeSourceDir
    )

    $nccCli = Join-Path $BridgeSourceDir "node_modules\@vercel\ncc\dist\ncc\cli.js"
    if (-not (Test-Path $nccCli)) {
        throw "Bridge bundler CLI not found after install: $nccCli"
    }

    return $nccCli
}

function Invoke-BridgeNpmCi {
    param(
        [Parameter(Mandatory = $true)][string]$NpmPath
    )

    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        & $NpmPath ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -eq 0) {
            return
        }

        if ($attempt -ge $maxAttempts) {
            throw "npm ci failed for tools/nkn-bridge"
        }

        Write-Warning ("npm ci failed for tools/nkn-bridge on attempt {0}; retrying after transient file-lock cleanup." -f $attempt)
        Start-Sleep -Seconds 2
    }
}

function Bundle-BridgeScript {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeSourceDir,
        [Parameter(Mandatory = $true)][string]$NodePath
    )

    $bundleDir = Join-Path $BridgeSourceDir ".nlink-bundle"
    if (Test-Path $bundleDir) {
        Remove-Item -Recurse -Force $bundleDir
    }

    $nccCli = Resolve-BridgeBundlerCliPath -BridgeSourceDir $BridgeSourceDir
    $entrySource = Join-Path $BridgeSourceDir "index.js"
    & $NodePath $nccCli build $entrySource "--out" $bundleDir | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "ncc bundle failed for tools/nkn-bridge"
    }

    $entryScript = Join-Path $bundleDir "index.js"
    if (-not (Test-Path $entryScript)) {
        throw "ncc bundle did not produce index.js at $entryScript"
    }

    return $bundleDir
}

function Test-BridgeBundleHealth {
    param(
        [Parameter(Mandatory = $true)][string]$NodePath,
        [Parameter(Mandatory = $true)][string]$BridgeScriptPath
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $NodePath
    $psi.Arguments = '"' + $BridgeScriptPath + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path -Parent $BridgeScriptPath

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true

    if (-not $proc.Start()) {
        throw "Bridge health check failed: could not start node bridge."
    }

    $proc.StandardInput.AutoFlush = $true

    $stderrPump = [System.Threading.Tasks.Task]::Run([Action]{
        try {
            while (-not $proc.HasExited) {
                $line = $proc.StandardError.ReadLine()
                if ($null -eq $line) { break }
            }
        }
        catch {
            # ignore stderr read errors
        }
    })

    try {
        $proc.StandardInput.WriteLine('{"id":"1","cmd":"hello","protocol":2,"appVersion":"bundle-check"}')
        $helloTask = $proc.StandardOutput.ReadLineAsync()
        if (-not $helloTask.Wait(5000)) {
            throw "Bridge health check failed: timed out waiting for hello_ok."
        }
        $helloLine = $helloTask.Result
        if ([string]::IsNullOrWhiteSpace($helloLine)) {
            throw "Bridge health check failed: empty hello response."
        }
        $helloJson = $helloLine | ConvertFrom-Json
        if ($helloJson.event -ne "hello_ok") {
            throw "Bridge health check failed: expected hello_ok, got $($helloJson.event)."
        }

        $proc.StandardInput.WriteLine('{"id":"2","cmd":"ping"}')
        $pongTask = $proc.StandardOutput.ReadLineAsync()
        if (-not $pongTask.Wait(2000)) {
            throw "Bridge health check failed: timed out waiting for pong."
        }
        $pongLine = $pongTask.Result
        if ([string]::IsNullOrWhiteSpace($pongLine)) {
            throw "Bridge health check failed: empty pong response."
        }
        $pongJson = $pongLine | ConvertFrom-Json
        $pongKind = $null
        if ($pongJson.PSObject.Properties.Name -contains 'event') {
            $pongKind = [string]$pongJson.event
        }
        elseif ($pongJson.PSObject.Properties.Name -contains 'type') {
            $pongKind = [string]$pongJson.type
        }

        if ($pongKind -ne "pong") {
            throw "Bridge health check failed: expected pong, got $pongKind."
        }

        $proc.StandardInput.WriteLine('{"id":"3","cmd":"shutdown"}')
        if (-not $proc.WaitForExit(4000)) {
            throw "Bridge health check failed: shutdown timed out."
        }
    }
    finally {
        try {
            if (-not $proc.HasExited) {
                $proc.Kill()
                [void]$proc.WaitForExit(2000)
            }
        }
        catch {
            # Best-effort cleanup.
        }

        try { $proc.Dispose() } catch {}
    }
}

$runtimeConfig = Get-BridgeRuntimeConfig -Runtime $Runtime -NodeVersion $PinnedNodeVersion

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$bridgeSource = Join-Path $repoRoot "tools\nkn-bridge"
$outAbs = Join-Path $repoRoot $OutDir

if (-not (Test-Path $bridgeSource)) {
    throw "Bridge source folder not found: $bridgeSource"
}

$toolchain = Resolve-OrBootstrapNodeToolchain -RepoRoot $repoRoot -RuntimeConfig $runtimeConfig
Write-Host "[nLink] Using Node toolchain source: $($toolchain.Source)" -ForegroundColor Cyan

$lockPath = Join-Path $bridgeSource "package-lock.json"
if (-not (Test-Path $lockPath)) {
    throw "tools/nkn-bridge/package-lock.json is required for deterministic bridge bundling."
}

$packageJsonPath = Join-Path $bridgeSource "package.json"
if (-not (Test-Path $packageJsonPath)) {
    throw "tools/nkn-bridge/package.json is required for bridge bundling."
}

$packageLockSha256 = (Get-FileHash -Path $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$bridgePackageJson = Get-Content -Path $packageJsonPath -Raw | ConvertFrom-Json
$bridgePackageVersion = [string]$bridgePackageJson.version
$nccVersion = [string]$bridgePackageJson.devDependencies.PSObject.Properties["@vercel/ncc"].Value

Push-Location $bridgeSource
try {
    $nodeDir = Split-Path -Parent $toolchain.NodePath
    $originalPath = $env:PATH
    $env:PATH = "$nodeDir;$originalPath"

    Write-Host "[nLink] Installing NKN bridge dependencies with locked npm ci..." -ForegroundColor Cyan
    Invoke-BridgeNpmCi -NpmPath $toolchain.NpmPath
}
finally {
    if ($null -ne $originalPath) {
        $env:PATH = $originalPath
    }
    Pop-Location
}

if (Test-Path $outAbs) {
    Remove-Item -Recurse -Force $outAbs
}
New-Item -ItemType Directory -Force -Path $outAbs | Out-Null

$bundleOutDir = Bundle-BridgeScript -BridgeSourceDir $bridgeSource -NodePath $toolchain.NodePath
$bundleEntryScript = Join-Path $bundleOutDir "index.js"
$nodeVersion = (& $toolchain.NodePath --version | Select-Object -First 1).Trim()
$npmVersion = (& $toolchain.NpmPath --version | Select-Object -First 1).Trim()

Copy-Item -Force $toolchain.NodePath (Join-Path $outAbs "node.exe")
Copy-Item -Force $bundleEntryScript (Join-Path $outAbs "index.js")
Copy-Item -Force $packageJsonPath (Join-Path $outAbs "package.json")
Copy-Item -Force $lockPath (Join-Path $outAbs "package-lock.json")
Write-BridgeDependencyEvidence `
    -BridgeSourceDir $bridgeSource `
    -OutDir $outAbs `
    -NodePath $toolchain.NodePath `
    -NodeVersion $nodeVersion `
    -NpmVersion $npmVersion `
    -NccVersion $nccVersion `
    -NodeArchiveSha256 $toolchain.NodeArchiveSha256 `
    -PackageLockSha256 $packageLockSha256
Write-BridgeBundleManifest `
    -RepoRoot $repoRoot `
    -BridgeSourceDir $bridgeSource `
    -Runtime $Runtime `
    -OutDir $outAbs `
    -NodePath (Join-Path $outAbs "node.exe") `
    -NodeArchiveSha256 $toolchain.NodeArchiveSha256 `
    -PackageLockSha256 $packageLockSha256 `
    -NpmVersion $npmVersion `
    -NccVersion $nccVersion `
    -BridgePackageVersion $bridgePackageVersion

Assert-BridgeBundleOutput -OutDir $outAbs

$bundleEntrySize = (Get-Item (Join-Path $outAbs "index.js")).Length
$totalSize = Get-DirectorySizeBytes -Path $outAbs

Assert-BridgeBundleOutput -OutDir $outAbs
Assert-BridgeBundleSupportsBulkChannel -BridgeScriptPath (Join-Path $outAbs "index.js")
Test-BridgeBundleHealth -NodePath (Join-Path $outAbs "node.exe") -BridgeScriptPath (Join-Path $outAbs "index.js")

Write-Host "[nLink] Bridge bundle output: $outAbs" -ForegroundColor Green
Write-Host "[nLink] Bundled bridge entry size: $(Format-Bytes -Bytes $bundleEntrySize)" -ForegroundColor Cyan
Write-Host "[nLink] Bridge bundle total size: $(Format-Bytes -Bytes $totalSize)" -ForegroundColor Cyan
Write-Host "[nLink] Bridge hello/ping health check: PASS" -ForegroundColor Green
