function Stop-NLinkProcesses {
    param([string]$ResolvedExePath = "")

    $targets = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like 'nLink*' -or $_.ProcessName -eq 'dotnet'
    }

    if ($targets) {
        Write-Host "Stopping lingering nLink/dotnet processes before NKN soak..." -ForegroundColor DarkYellow
        $targets | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 750
    }

    Stop-BridgeNodeProcesses -ResolvedExePath $ResolvedExePath
}

function Stop-BridgeNodeProcesses {
    param([string]$ResolvedExePath = "")

    if ([string]::IsNullOrWhiteSpace($ResolvedExePath)) {
        return
    }

    $appDir = Split-Path -Parent $ResolvedExePath
    if ([string]::IsNullOrWhiteSpace($appDir)) {
        return
    }

    $expectedNodePath = Join-Path $appDir 'bridge\win-x64\node.exe'
    $expectedScriptPath = Join-Path $appDir 'bridge\win-x64\index.js'
    if (-not (Test-Path $expectedNodePath)) {
        return
    }

    $expectedNodePath = [System.IO.Path]::GetFullPath($expectedNodePath)
    $expectedScriptPath = [System.IO.Path]::GetFullPath($expectedScriptPath)

    $targets = @(
        Get-CimInstance Win32_Process -Filter "Name = 'node.exe' OR Name = 'node'" -ErrorAction SilentlyContinue |
            Where-Object {
                $exeMatches = $false
                $cmdMatches = $false

                if ($_.ExecutablePath) {
                    try {
                        $exeMatches = [string]::Equals(
                            [System.IO.Path]::GetFullPath($_.ExecutablePath),
                            $expectedNodePath,
                            [System.StringComparison]::OrdinalIgnoreCase)
                    }
                    catch {}
                }

                if ($_.CommandLine) {
                    try {
                        $cmdMatches = $_.CommandLine.IndexOf($expectedScriptPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                    }
                    catch {}
                }

                return $exeMatches -or $cmdMatches
            }
    )

    if ($targets.Count -eq 0) {
        return
    }

    Write-Host "Stopping lingering bridge node.exe processes for selected app..." -ForegroundColor DarkYellow
    foreach ($target in $targets) {
        try { Stop-Process -Id $target.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    Start-Sleep -Milliseconds 500
}

function Resolve-RepoRoot {
    $current = Split-Path -Parent $PSScriptRoot
    while ($null -ne $current -and $current -ne "") {
        if ((Test-Path (Join-Path $current "nLink.sln")) -and
            (Test-Path (Join-Path $current "VERSION"))) {
            return $current
        }

        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }

        $current = $parent
    }

    throw "Could not locate repo root from $PSScriptRoot."
}

function Resolve-ExePath {
    param(
        [string]$RepoRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidate = $RequestedPath
        if (-not [System.IO.Path]::IsPathRooted($candidate)) {
            $candidate = Join-Path $RepoRoot $candidate
        }

        if (Test-Path $candidate) {
            return $candidate
        }

        $alternateCandidate = $candidate -replace '(?i)(^|\\)link\.exe$', '${1}nLink.exe'
        if ($alternateCandidate -ne $candidate -and (Test-Path $alternateCandidate)) {
            return $alternateCandidate
        }

        return $candidate
    }

    $candidates = @(
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nlink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\Link.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\win-x64\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\win-x64\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Debug\net8.0\win-x64\Link.exe"),
        (Join-Path $RepoRoot "artifacts\portable\nLink\win-x64\nnLink.exe"),
        (Join-Path $RepoRoot "artifacts\portable\nLink\win-x64\nLink.exe"),
        (Join-Path $RepoRoot "artifacts\portable\nLink\win-x64\Link.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\Link.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\win-x64\nnLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\win-x64\nLink.exe"),
        (Join-Path $RepoRoot "src\nLink.App\bin\Release\net8.0\win-x64\Link.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $candidates[0]
}

function Build-LocalExeIfNeeded {
    param(
        [string]$RepoRoot,
        [string]$ResolvedExePath,
        [bool]$ForceBuild
    )

    if (-not $ForceBuild -and (Test-Path $ResolvedExePath)) {
        return
    }

    Write-Host "Building nLink executable for NKN soak..." -ForegroundColor Cyan
    dotnet build (Join-Path $RepoRoot "src\nLink.App\nLink.App.csproj") | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $ResolvedExePath)) {
        $alternateResolvedExePath = $ResolvedExePath -replace '(?i)(^|\\)link\.exe$', '${1}nLink.exe'
        if ($alternateResolvedExePath -ne $ResolvedExePath -and (Test-Path $alternateResolvedExePath)) {
            return
        }

        throw "Build completed but executable was not found at $ResolvedExePath."
    }
}

function Test-IsPathWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\') + '\'
    $normalizedCandidate = [System.IO.Path]::GetFullPath($CandidatePath)
    return $normalizedCandidate.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-BridgeRuntimePresent {
    param([Parameter(Mandatory = $true)][string]$AppDir)

    $bridgeDir = Join-Path $AppDir 'bridge\win-x64'
    return (Test-Path (Join-Path $bridgeDir 'index.js')) -and
           (Test-Path (Join-Path $bridgeDir 'node.exe')) -and
           (Test-Path (Join-Path $bridgeDir 'bridge-manifest.json'))
}

function Resolve-NLinkRepoRootForPath {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][string]$FallbackRepoRoot
    )

    $current = [System.IO.Path]::GetFullPath($CandidatePath)
    if (Test-Path -LiteralPath $current -PathType Leaf) {
        $current = Split-Path -Parent $current
    }

    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $hasVersion = Test-Path -LiteralPath (Join-Path $current 'VERSION') -PathType Leaf
        $hasBridgeBuilder = Test-Path -LiteralPath (Join-Path $current 'installer\Build-BridgeBundle.ps1') -PathType Leaf
        $hasBridgeSource = Test-Path -LiteralPath (Join-Path $current 'tools\nkn-bridge\index.js') -PathType Leaf
        if ($hasVersion -and $hasBridgeBuilder -and $hasBridgeSource) {
            return $current
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::Equals($parent, $current, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $current = $parent
    }

    return [System.IO.Path]::GetFullPath($FallbackRepoRoot)
}

function Ensure-NknBridgeRuntimeForExe {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath
    )

    $appDir = Split-Path -Parent $ResolvedExePath
    if ([string]::IsNullOrWhiteSpace($appDir) -or -not (Test-Path $appDir)) {
        throw "Could not resolve app directory for executable: $ResolvedExePath"
    }

    $buildRepoRoot = Resolve-NLinkRepoRootForPath -CandidatePath $appDir -FallbackRepoRoot $RepoRoot
    $repoLocalBuild = Test-IsPathWithinRoot -RootPath $buildRepoRoot -CandidatePath $appDir
    if (-not $repoLocalBuild) {
        if (Test-BridgeRuntimePresent -AppDir $appDir) {
            return
        }

        throw "Selected executable is missing bridge\\win-x64 (index.js, node.exe, bridge-manifest.json): $ResolvedExePath"
    }

    $bridgeBundleDir = Join-Path $buildRepoRoot 'artifacts\bridge\win-x64'
    $bridgeBundleScript = Join-Path $buildRepoRoot 'installer\Build-BridgeBundle.ps1'
    $bridgeSourceScript = Join-Path $buildRepoRoot 'tools\nkn-bridge\index.js'
    $artifactIndexPath = Join-Path $bridgeBundleDir 'index.js'
    $artifactNodePath = Join-Path $bridgeBundleDir 'node.exe'
    $artifactManifestPath = Join-Path $bridgeBundleDir 'bridge-manifest.json'

    $needsBundleBuild =
        -not (Test-Path $artifactIndexPath) -or
        -not (Test-Path $artifactNodePath) -or
        -not (Test-Path $artifactManifestPath)

    if (-not $needsBundleBuild -and (Test-Path $bridgeSourceScript)) {
        $sourceWriteUtc = (Get-Item $bridgeSourceScript).LastWriteTimeUtc
        $artifactWriteUtc = (Get-Item $artifactIndexPath).LastWriteTimeUtc
        if ($artifactWriteUtc -lt $sourceWriteUtc) {
            $needsBundleBuild = $true
        }
    }

    if (-not (Test-Path $bridgeBundleScript)) {
        throw "Bridge bundle script not found: $bridgeBundleScript"
    }

    if ($needsBundleBuild) {
        Write-Host "Building bridge bundle for repo-local soak target..." -ForegroundColor Cyan
        & powershell -ExecutionPolicy Bypass -File $bridgeBundleScript -Runtime 'win-x64'
        if ($LASTEXITCODE -ne 0) {
            throw "Build-BridgeBundle.ps1 failed with exit code $LASTEXITCODE."
        }
    }

    $targetBridgeDir = Join-Path $appDir 'bridge\win-x64'
    New-Item -ItemType Directory -Force -Path $targetBridgeDir | Out-Null
    Copy-Item -Path (Join-Path $bridgeBundleDir '*') -Destination $targetBridgeDir -Recurse -Force

    if (-not (Test-BridgeRuntimePresent -AppDir $appDir)) {
        throw "Failed to stage bridge runtime into $targetBridgeDir"
    }

    Write-Host "Staged bridge runtime into repo-local soak target: $targetBridgeDir (source root: $buildRepoRoot)" -ForegroundColor DarkCyan
}
