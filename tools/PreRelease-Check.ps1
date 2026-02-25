param(
    [switch]$RunGuiSmoke,
    [string]$GuiScenarios = "A",
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "[PreRelease] $Name" -ForegroundColor Cyan
    & $Action
}

function Assert-PathExists {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path $Path)) {
        throw "$Description not found: $Path"
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$versionPath = Join-Path $repoRoot "VERSION"
Assert-PathExists -Path $versionPath -Description "VERSION file"
$version = (Get-Content $versionPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "VERSION file is empty: $versionPath"
}

Write-Host "[PreRelease] Version: $version" -ForegroundColor Green

$guiSmokeArtifactRoot = Join-Path $repoRoot "artifacts\gui-smoke"
$guiSmokeArtifactDirs = @()

Push-Location $repoRoot
try {
    Invoke-Step -Name "Smoke tests (Category=Smoke)" -Action {
        dotnet test -c Release --filter Category=Smoke
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($RunGuiSmoke) {
        $oldGuiSmoke = $env:NLINK_RUN_GUI_SMOKE
        $oldGuiScenarios = $env:NLINK_GUI_SMOKE_SCENARIOS
        try {
            $env:NLINK_RUN_GUI_SMOKE = "1"
            $env:NLINK_GUI_SMOKE_SCENARIOS = $GuiScenarios
            Invoke-Step -Name "Optional GUI smoke tests (Category=GuiSmoke)" -Action {
                dotnet test -c Release --filter Category=GuiSmoke
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            }

            if (Test-Path $guiSmokeArtifactRoot) {
                $guiSmokeArtifactDirs = @(
                    Get-ChildItem -Path $guiSmokeArtifactRoot -Directory -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTimeUtc -Descending
                )
                if ($guiSmokeArtifactDirs.Count -gt 0) {
                    Write-Host "[PreRelease] GUI smoke artifacts:" -ForegroundColor Green
                    foreach ($dir in $guiSmokeArtifactDirs) {
                        Write-Host ("  {0}" -f $dir.FullName)
                    }
                }
                else {
                    Write-Host "[PreRelease] GUI smoke artifacts: none created (no failures or no dumps)." -ForegroundColor DarkGray
                }
            }
        }
        finally {
            if ($null -eq $oldGuiSmoke) {
                Remove-Item Env:NLINK_RUN_GUI_SMOKE -ErrorAction SilentlyContinue
            }
            else {
                $env:NLINK_RUN_GUI_SMOKE = $oldGuiSmoke
            }

            if ($null -eq $oldGuiScenarios) {
                Remove-Item Env:NLINK_GUI_SMOKE_SCENARIOS -ErrorAction SilentlyContinue
            }
            else {
                $env:NLINK_GUI_SMOKE_SCENARIOS = $oldGuiScenarios
            }
        }
    }

    Invoke-Step -Name "Build bridge bundle" -Action {
        & powershell -ExecutionPolicy Bypass -File ".\installer\Build-BridgeBundle.ps1" -Runtime $Runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Invoke-Step -Name "Build portable ZIP" -Action {
        & powershell -ExecutionPolicy Bypass -File ".\installer\Build-Portable.ps1" -Runtime $Runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Invoke-Step -Name "Build installer" -Action {
        & powershell -ExecutionPolicy Bypass -File ".\installer\Build-Installer.ps1" -Runtime $Runtime
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}

$releaseDir = Join-Path (Join-Path $repoRoot "artifacts\releases") $version
$portableZip = Join-Path $releaseDir ("nLink-Portable-{0}-{1}.zip" -f $Runtime, $version)
$installerExe = Join-Path $releaseDir ("nLink-Setup-{0}-{1}.exe" -f $Runtime, $version)

Assert-PathExists -Path $releaseDir -Description "Release folder"
Assert-PathExists -Path $portableZip -Description "Portable ZIP"
Assert-PathExists -Path $installerExe -Description "Installer EXE"

$portableStage = Join-Path $repoRoot ("artifacts\portable\nLink\win-x64")
$helperStage = Join-Path $repoRoot ("artifacts\portable\helper\win-x64")

$portableBridgeRid = Join-Path $portableStage (Join-Path "bridge" $Runtime)
$helperBridgeRid = Join-Path $helperStage (Join-Path "bridge" $Runtime)

Assert-PathExists -Path (Join-Path $portableBridgeRid "index.js") -Description "Portable bridge index.js"
Assert-PathExists -Path (Join-Path $portableBridgeRid "node.exe") -Description "Portable bridge node.exe"
Assert-PathExists -Path (Join-Path $portableBridgeRid "node_modules") -Description "Portable bridge node_modules"

Assert-PathExists -Path (Join-Path $helperBridgeRid "index.js") -Description "Helper staging bridge index.js"
Assert-PathExists -Path (Join-Path $helperBridgeRid "node.exe") -Description "Helper staging bridge node.exe"
Assert-PathExists -Path (Join-Path $helperBridgeRid "node_modules") -Description "Helper staging bridge node_modules"

$portableZipAbs = (Resolve-Path $portableZip).Path
$installerExeAbs = (Resolve-Path $installerExe).Path
$releaseDirAbs = (Resolve-Path $releaseDir).Path
$portableBridgeRidAbs = (Resolve-Path $portableBridgeRid).Path
$helperBridgeRidAbs = (Resolve-Path $helperBridgeRid).Path

Write-Host "" 
Write-Host "[PreRelease] Final checklist summary" -ForegroundColor Green
Write-Host ("  Version: {0}" -f $version)
Write-Host ("  Runtime: {0}" -f $Runtime)
Write-Host ("  Smoke tests: PASS")
Write-Host ("  GUI smoke: {0}" -f ($(if ($RunGuiSmoke) { "PASS (scenarios: $GuiScenarios)" } else { "SKIPPED" })))
Write-Host ("  Bridge runtime verified in portable stage: {0}" -f $portableBridgeRidAbs)
Write-Host ("  Bridge runtime verified in helper stage: {0}" -f $helperBridgeRidAbs)
Write-Host ""
Write-Host "[PreRelease] Final upload assets:" -ForegroundColor Green
Write-Host ("  Portable ZIP: {0}" -f $portableZipAbs)
Write-Host ("  Installer EXE: {0}" -f $installerExeAbs)
Write-Host ("  Release folder: {0}" -f $releaseDirAbs)
if ($guiSmokeArtifactDirs.Count -gt 0) {
    Write-Host ("  GUI smoke artifacts root: {0}" -f (Resolve-Path $guiSmokeArtifactRoot).Path)
}
Write-Host ""
Write-Host "[PreRelease] READY TO RELEASE" -ForegroundColor Green
