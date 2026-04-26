param(
    [Parameter(Mandatory = $true)][string]$OldInstallerPath,
    [Parameter(Mandatory = $true)][string]$NewInstallerPath,
    [string]$InstallDir = "",
    [string]$UserDataRoot = "",
    [switch]$SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$PathValue)
    return (Resolve-Path $PathValue).Path
}

function Get-UserDataRoot {
    param([string]$ConfiguredRoot)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredRoot)) {
        return $ConfiguredRoot
    }

    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    return Join-Path $localAppData "nLink"
}

function Invoke-Installer {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$TargetDir
    )

    $process = Start-Process -FilePath $InstallerPath -ArgumentList @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            "/DIR=$TargetDir"
        ) -Wait -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Installer failed with exit code $($process.ExitCode): $InstallerPath"
    }
}

function Invoke-Uninstaller {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $uninstaller = Get-ChildItem -Path $TargetDir -Filter "unins*.exe" -File | Select-Object -First 1
    if (-not $uninstaller) {
        throw "Uninstaller not found in install directory: $TargetDir"
    }

    $process = Start-Process -FilePath $uninstaller.FullName -ArgumentList @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART'
        ) -Wait -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($process.ExitCode): $($uninstaller.FullName)"
    }
}

function Assert-InstalledLayout {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $requiredPaths = @(
        'nLink.exe',
        'appsettings.json',
        'bridge\win-x64\index.js',
        'bridge\win-x64\node.exe',
        'bridge\win-x64\package.json',
        'bridge\win-x64\bridge-manifest.json'
    )

    foreach ($relativePath in $requiredPaths) {
        $fullPath = Join-Path $TargetDir $relativePath
        if (-not (Test-Path $fullPath)) {
            throw "Installed payload is missing required file: $fullPath"
        }
    }

    $bridgeDir = Join-Path $TargetDir 'bridge\win-x64'
    $manifestPath = Join-Path $bridgeDir 'bridge-manifest.json'
    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.runtime -ne 'win-x64') {
        throw "Installed bridge manifest runtime mismatch: $($manifest.runtime)"
    }

    $bridgeScriptPath = Join-Path $bridgeDir 'index.js'
    $actualHash = (Get-FileHash -Path $bridgeScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$manifest.bridgeScriptSha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'Installed bridge script hash does not match bridge manifest.'
    }
}

function Invoke-SelfTest {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $appExe = Join-Path $TargetDir 'nLink.exe'
    $process = Start-Process -FilePath $appExe -ArgumentList @('--self-test') -WorkingDirectory $TargetDir -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installed app self-test failed with exit code $($process.ExitCode): $appExe"
    }
}

function Start-BridgeNodeLock {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $nodeExe = Join-Path $TargetDir 'bridge\win-x64\node.exe'
    if (-not (Test-Path $nodeExe)) {
        throw "Bundled node.exe not found: $nodeExe"
    }

    $process = Start-Process -FilePath $nodeExe -ArgumentList @(
            '-e',
            'setInterval(() => {}, 1000 * 60);'
        ) -WorkingDirectory (Split-Path -Parent $nodeExe) -WindowStyle Hidden -PassThru

    Start-Sleep -Milliseconds 750
    if ($process.HasExited) {
        throw "Failed to keep bundled node.exe running for installer lock validation: $nodeExe"
    }

    return $process
}

function Stop-TestProcess {
    param($Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            $Process.Kill(entireProcessTree: $true)
            $Process.WaitForExit()
        }
    }
    catch {
    }
}

function Get-UserDataSnapshot {
    param([Parameter(Mandatory = $true)][string]$RootDir)

    $snapshot = [ordered]@{
        IdentityFiles = @()
        ReliabilityLogExists = $false
        LogsDirectoryExists = $false
        HangArtifactsDirectoryExists = $false
    }

    if (-not (Test-Path $RootDir)) {
        return [pscustomobject]$snapshot
    }

    $identityFiles = @(Get-ChildItem -Path $RootDir -Filter 'identity*.json' -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $snapshot.IdentityFiles = $identityFiles
    $snapshot.ReliabilityLogExists = Test-Path (Join-Path $RootDir 'reliability.jsonl')
    $snapshot.LogsDirectoryExists = Test-Path (Join-Path $RootDir 'logs')
    $snapshot.HangArtifactsDirectoryExists = Test-Path (Join-Path $RootDir 'artifacts\hang')
    return [pscustomobject]$snapshot
}

function Assert-UserDataPreserved {
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )

    foreach ($identityPath in @($Before.IdentityFiles)) {
        if (-not ($After.IdentityFiles -contains $identityPath)) {
            throw "User identity file was not preserved across upgrade/uninstall: $identityPath"
        }
    }

    if ($Before.ReliabilityLogExists -and -not $After.ReliabilityLogExists) {
        throw "User reliability log was not preserved across upgrade/uninstall."
    }

    if ($Before.LogsDirectoryExists -and -not $After.LogsDirectoryExists) {
        throw "User logs directory was not preserved across upgrade/uninstall."
    }

    if ($Before.HangArtifactsDirectoryExists -and -not $After.HangArtifactsDirectoryExists) {
        throw "User hang artifacts directory was not preserved across upgrade/uninstall."
    }
}

function Get-ProcessesUnderInstallDir {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $normalizedDir = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\') + '\'
    return @(
        Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object {
                $_.ExecutablePath -and
                ([System.IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($normalizedDir, [StringComparison]::OrdinalIgnoreCase)
            }
    )
}

function Assert-NoProcessesUnderInstallDir {
    param([Parameter(Mandatory = $true)][string]$TargetDir)

    $remaining = @(Get-ProcessesUnderInstallDir -TargetDir $TargetDir)
    if ($remaining.Count -gt 0) {
        $details = @($remaining | ForEach-Object { "{0} (pid={1})" -f $_.ExecutablePath, $_.ProcessId }) -join ', '
        throw "Processes are still running from install directory '$TargetDir': $details"
    }
}

$resolvedOldInstaller = Resolve-FullPath -PathValue $OldInstallerPath
$resolvedNewInstaller = Resolve-FullPath -PathValue $NewInstallerPath

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:TEMP "nlink-upgrade-validation"
}

$resolvedInstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$resolvedUserDataRoot = [System.IO.Path]::GetFullPath((Get-UserDataRoot -ConfiguredRoot $UserDataRoot))

if (Test-Path $resolvedInstallDir) {
    $existingUninstaller = Get-ChildItem -Path $resolvedInstallDir -Filter "unins*.exe" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existingUninstaller) {
        Invoke-Uninstaller -TargetDir $resolvedInstallDir
    }

    if (Test-Path $resolvedInstallDir) {
        Remove-Item -Recurse -Force $resolvedInstallDir
    }
}

$baselineUserData = Get-UserDataSnapshot -RootDir $resolvedUserDataRoot

Write-Host "[nLink] Installing baseline build: $resolvedOldInstaller" -ForegroundColor Cyan
Invoke-Installer -InstallerPath $resolvedOldInstaller -TargetDir $resolvedInstallDir
Assert-InstalledLayout -TargetDir $resolvedInstallDir
if (-not $SkipSelfTest) {
    Invoke-SelfTest -TargetDir $resolvedInstallDir
}

Write-Host "[nLink] Create or confirm minimal user state now, then press Enter to continue with the upgrade." -ForegroundColor Yellow
Read-Host | Out-Null

$beforeUpgradeUserData = Get-UserDataSnapshot -RootDir $resolvedUserDataRoot

Write-Host "[nLink] Upgrading in place to: $resolvedNewInstaller" -ForegroundColor Cyan
$upgradeLockProcess = $null
try {
    $upgradeLockProcess = Start-BridgeNodeLock -TargetDir $resolvedInstallDir
    Invoke-Installer -InstallerPath $resolvedNewInstaller -TargetDir $resolvedInstallDir
}
finally {
    Stop-TestProcess -Process $upgradeLockProcess
}
Assert-InstalledLayout -TargetDir $resolvedInstallDir
if (-not $SkipSelfTest) {
    Invoke-SelfTest -TargetDir $resolvedInstallDir
}

$afterUpgradeUserData = Get-UserDataSnapshot -RootDir $resolvedUserDataRoot
Assert-UserDataPreserved -Before $beforeUpgradeUserData -After $afterUpgradeUserData

Write-Host "[nLink] Uninstalling upgraded build..." -ForegroundColor Cyan
$uninstallLockProcess = $null
try {
    $uninstallLockProcess = Start-BridgeNodeLock -TargetDir $resolvedInstallDir
    Invoke-Uninstaller -TargetDir $resolvedInstallDir
}
finally {
    Stop-TestProcess -Process $uninstallLockProcess
}
Assert-NoProcessesUnderInstallDir -TargetDir $resolvedInstallDir

$afterUninstallUserData = Get-UserDataSnapshot -RootDir $resolvedUserDataRoot
Assert-UserDataPreserved -Before $beforeUpgradeUserData -After $afterUninstallUserData

if (Test-Path (Join-Path $resolvedInstallDir 'nLink.exe')) {
    throw "Installed app executable still exists after uninstall: $(Join-Path $resolvedInstallDir 'nLink.exe')"
}

Write-Host "[nLink] Upgrade/uninstall validation passed." -ForegroundColor Green
Write-Host "[nLink] Install directory: $resolvedInstallDir" -ForegroundColor Green
Write-Host "[nLink] User data root preserved: $resolvedUserDataRoot" -ForegroundColor Green
Write-Host "[nLink] Checked user data paths: identity*.json, reliability.jsonl, logs, artifacts\\hang" -ForegroundColor Green
