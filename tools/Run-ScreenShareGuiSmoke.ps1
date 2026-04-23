param(
    [string]$ExePath = "",
    [string[]]$Scenarios = @(
        "screenshare_button_visibility",
        "screenshare_viewer_toggle",
        "screenshare_chat_coexistence",
        "screenshare_stop_pending_approval"
    ),
    [ValidateSet("AUTO", "DEVLOCAL", "NKN")]
    [string]$Transport = "AUTO",
    [switch]$Build,
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-NLinkProcesses {
    param([string]$ResolvedExePath = "")

    $targets = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like 'nLink*' -or $_.ProcessName -eq 'dotnet'
    }

    if ($targets) {
        Write-Host "Stopping lingering nLink/dotnet processes before GUI smoke..." -ForegroundColor DarkYellow
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

    Write-Host "Building nLink debug executable..." -ForegroundColor Cyan
    dotnet build (Join-Path $RepoRoot "src\nLink.App\nLink.App.csproj") | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path $ResolvedExePath)) {
        throw "Build completed but executable was not found at $ResolvedExePath."
    }
}

$repoRoot = Resolve-RepoRoot
$guiSmokeScript = Join-Path $repoRoot "tools\GuiSmoke-Windows.ps1"
if (-not (Test-Path $guiSmokeScript)) {
    throw "GUI smoke harness not found: $guiSmokeScript"
}

$resolvedExePath = Resolve-ExePath -RepoRoot $repoRoot -RequestedPath $ExePath
Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
Build-LocalExeIfNeeded -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath -ForceBuild:$Build.IsPresent

$selectedScenarios = @($Scenarios | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
if ($selectedScenarios.Count -eq 0) {
    throw "At least one GUI smoke scenario is required."
}

$previousScenarioEnv = $env:NLINK_GUI_SMOKE_SCENARIOS
$previousTransportEnv = $env:NLINK_TRANSPORT

try {
    $env:NLINK_GUI_SMOKE_SCENARIOS = ($selectedScenarios -join ",")
    if ($Transport -eq "AUTO") {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $Transport
    }

    Write-Host "Running GUI screenshare smoke..." -ForegroundColor Cyan
    Write-Host "  ExePath: $resolvedExePath"
    Write-Host "  Scenarios: $($selectedScenarios -join ', ')"
    Write-Host "  Transport: $Transport"

    & powershell -ExecutionPolicy Bypass -File $guiSmokeScript -ExePath $resolvedExePath -TimeoutSeconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) {
        throw "GUI smoke harness exited with code $LASTEXITCODE."
    }
}
finally {
    Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
    $env:NLINK_GUI_SMOKE_SCENARIOS = $previousScenarioEnv
    if ($null -eq $previousTransportEnv) {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $previousTransportEnv
    }
}
