param(
    [string]$Runtime = "win-x64",
    [string]$OutDir = "artifacts/bridge/win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$PinnedNodeVersion = "24.13.1"

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

    $bootstrapRoot = Join-Path $RepoRoot ("tools\node\" + $RuntimeConfig.Runtime)
    $archivePath = Join-Path $bootstrapRoot $RuntimeConfig.ArchiveFileName
    $extractDir = Join-Path $bootstrapRoot $RuntimeConfig.ExtractedFolderName
    $nodePath = Join-Path $extractDir $RuntimeConfig.NodeExeName
    $npmPath = Join-Path $extractDir $RuntimeConfig.NpmExeName

    if ((Test-Path $nodePath) -and (Test-Path $npmPath)) {
        return [pscustomobject]@{
            NodePath = (Resolve-Path $nodePath).Path
            NpmPath = (Resolve-Path $npmPath).Path
            Source = "portable-cache"
        }
    }

    New-Item -ItemType Directory -Force -Path $bootstrapRoot | Out-Null

    if (-not (Test-Path $archivePath)) {
        Write-Host "[nLink] Downloading pinned Node.js runtime $PinnedNodeVersion for $($RuntimeConfig.Runtime)..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $RuntimeConfig.DownloadUrl -OutFile $archivePath
    }
    else {
        Write-Host "[nLink] Using cached Node.js archive: $archivePath" -ForegroundColor Cyan
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
    }
}

function Resolve-OrBootstrapNodeToolchain {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$RuntimeConfig
    )

    $systemNode = Resolve-SystemNodePath
    $systemNpm = Resolve-SystemNpmPath
    if ($systemNode -and $systemNpm) {
        return [pscustomobject]@{
            NodePath = $systemNode
            NpmPath = $systemNpm
            Source = "system"
        }
    }

    return Ensure-PortableNodeToolchain -RepoRoot $RepoRoot -RuntimeConfig $RuntimeConfig
}

function Assert-BridgeBundleOutput {
    param(
        [Parameter(Mandatory = $true)][string]$OutDir
    )

    $indexJs = Join-Path $OutDir "index.js"
    $nodeExe = Join-Path $OutDir "node.exe"
    $manifestPath = Join-Path $OutDir "bridge-manifest.json"
    if (-not (Test-Path $indexJs) -or -not (Test-Path $nodeExe) -or -not (Test-Path $manifestPath)) {
        throw "Bridge bundle validation failed: missing node.exe, index.js, or bridge-manifest.json in $OutDir"
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
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$OutDir,
        [Parameter(Mandatory = $true)][string]$NodePath
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
        capabilities = [ordered]@{
            ownerPidWatchdog = $true
            killOnCloseJob = $true
        }
    }

    $manifestPath = Join-Path $OutDir "bridge-manifest.json"
    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))
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
$lockExistedBefore = Test-Path $lockPath

Push-Location $bridgeSource
try {
    $nodeDir = Split-Path -Parent $toolchain.NodePath
    $originalPath = $env:PATH
    $env:PATH = "$nodeDir;$originalPath"

    if ($lockExistedBefore) {
        Write-Host "[nLink] Installing NKN bridge dependencies with npm ci..." -ForegroundColor Cyan
        & $toolchain.NpmPath ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed for tools/nkn-bridge"
        }
    }
    else {
        Write-Host "[nLink] Installing NKN bridge dependencies with npm install (no lockfile present)..." -ForegroundColor Cyan
        & $toolchain.NpmPath install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed for tools/nkn-bridge"
        }
    }
}
finally {
    if ($null -ne $originalPath) {
        $env:PATH = $originalPath
    }
    Pop-Location
}

if (-not (Test-Path $lockPath)) {
    throw "tools/nkn-bridge/package-lock.json is required after install. Generate and commit it."
}

if (-not $lockExistedBefore) {
    Write-Warning "tools/nkn-bridge/package-lock.json was created during this run. Commit it to keep bridge bundling deterministic."
}

if (Test-Path $outAbs) {
    Remove-Item -Recurse -Force $outAbs
}
New-Item -ItemType Directory -Force -Path $outAbs | Out-Null

$bundleOutDir = Bundle-BridgeScript -BridgeSourceDir $bridgeSource -NodePath $toolchain.NodePath
$bundleEntryScript = Join-Path $bundleOutDir "index.js"

Copy-Item -Force $toolchain.NodePath (Join-Path $outAbs "node.exe")
Copy-Item -Force $bundleEntryScript (Join-Path $outAbs "index.js")
Copy-Item -Force (Join-Path $bridgeSource "package.json") (Join-Path $outAbs "package.json")
Write-BridgeBundleManifest -RepoRoot $repoRoot -Runtime $Runtime -OutDir $outAbs -NodePath (Join-Path $outAbs "node.exe")

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
