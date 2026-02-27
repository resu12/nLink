param(
    [string]$OldInstallerPath = '',
    [string]$CurrentInstallerPath = '',
    [string]$InstallDir = '',
    [int]$SmokeCycles = 5,
    [string]$ArtifactPath = 'artifacts/beta-hardening/installer-upgrade-rollback.txt',
    [switch]$AllowExistingInstallImpact,
    [switch]$KeepInstalledForInspection
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'BetaHardening.Common.ps1')

function Add-ReportLine {
    param([System.Collections.Generic.List[string]]$Lines, [string]$Text)
    [void]$Lines.Add($Text)
}

function Resolve-OldInstallerAuto {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$CurrentInstaller
    )

    $currentName = if ([string]::IsNullOrWhiteSpace($CurrentInstaller)) { '' } else { [System.IO.Path]::GetFileName($CurrentInstaller) }
    $candidates = @(Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts') -Recurse -File -Filter 'nLink-Setup-win-x64-*.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $currentName } |
        Sort-Object LastWriteTimeUtc -Descending)

    if ($candidates.Count -eq 0) {
        return $null
    }

    return $candidates[0].FullName
}

function Get-InstalledNLinkUninstallEntries {
    $roots = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    $entries = @()
    foreach ($root in $roots) {
        try {
            $entries += @(Get-ItemProperty $root -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.PSObject.Properties.Name -contains 'DisplayName' -and
                    [string]$_.DisplayName -eq 'nLink'
                } |
                Select-Object DisplayName, InstallLocation, UninstallString, PSPath)
        }
        catch {
        }
    }

    return @($entries)
}

function Assert-ExitCodeZero {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$StepName
    )

    if ($Result.TimedOut) {
        throw "$StepName timed out after $($Result.DurationMs) ms."
    }
    if ($null -eq $Result.ExitCode -or [int]$Result.ExitCode -ne 0) {
        throw "$StepName failed with exit code $($Result.ExitCode)."
    }
}

function Assert-SettingsPersistenceBehavior {
    param(
        [Parameter(Mandatory = $true)]$BaselineAfterOldSmoke,
        [Parameter(Mandatory = $true)]$AfterUpgrade,
        [Parameter(Mandatory = $true)]$AfterRollback,
        [System.Collections.Generic.List[string]]$Lines
    )

    $baselineSettings = @($BaselineAfterOldSmoke.SettingsLikeFiles)
    if ($baselineSettings.Count -eq 0) {
        Add-ReportLine -Lines $Lines -Text 'settings_persistence: no settings-like files detected under %LOCALAPPDATA%\nLink (current behavior); verified stable absence through upgrade/rollback.'
        return
    }

    foreach ($relative in $baselineSettings) {
        if (@($AfterUpgrade.SettingsLikeFiles) -notcontains $relative) {
            throw "Settings persistence check failed after upgrade: missing '$relative'."
        }
        if (@($AfterRollback.SettingsLikeFiles) -notcontains $relative) {
            throw "Settings persistence check failed after rollback reinstall: missing '$relative'."
        }
    }

    Add-ReportLine -Lines $Lines -Text ("settings_persistence: preserved {0} settings-like file(s) across upgrade and rollback reinstall." -f $baselineSettings.Count)
}

function Assert-NoOrphanNodeForInstallDir {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Label = 'node_orphan_check'
    )

    $snapshot = Get-NodeProcessSnapshot
    $matches = @(Find-NLinkNodeProcesses -Snapshot $snapshot -PathHints @($InstallDir, 'nLink\\bridge', 'nkn-bridge'))
    if ($matches.Count -gt 0) {
        foreach ($p in $matches) {
            Add-ReportLine -Lines $Lines -Text ("[{0}] orphan_node pid={1}; exe={2}; cmd={3}" -f $Label, $p.ProcessId, $p.ExecutablePath, $p.CommandLine)
        }
        throw "Orphan node.exe process(es) detected after uninstall."
    }

    Add-ReportLine -Lines $Lines -Text ("[{0}] PASS (no nLink-related node.exe processes)" -f $Label)
}

function Assert-StableInnoAppId {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [System.Collections.Generic.List[string]]$Lines
    )

    $issPath = Join-Path $RepoRoot 'installer\nLink.iss'
    if (-not (Test-Path $issPath)) {
        throw "Inno Setup script not found: $issPath"
    }

    $appIdLine = (Select-String -Path $issPath -Pattern '^AppId=' | Select-Object -First 1)
    if ($null -eq $appIdLine) {
        throw 'AppId line not found in installer\nLink.iss.'
    }

    $expected = 'AppId={{9D5C9C2D-7D66-4E6E-8A5A-20F64C2F31A7}'
    if ([string]$appIdLine.Line -ne $expected) {
        throw "Unexpected AppId. Expected '$expected' but found '$($appIdLine.Line)'."
    }

    Add-ReportLine -Lines $Lines -Text ("installer_appid: stable ({0})" -f $expected)
}

$repoRoot = Get-BetaHardeningRepoRoot
$artifactAbs = Resolve-BetaHardeningPath -RepoRoot $repoRoot -Path $ArtifactPath
$reportLines = New-Object System.Collections.Generic.List[string]
$exitCode = 1
$cleanupAttempted = $false

try {
    if ([string]::IsNullOrWhiteSpace($InstallDir)) {
        $InstallDir = Join-Path $env:LOCALAPPDATA 'nLink-BetaHardening\UpgradeRollback'
    }

    Add-ReportLine -Lines $reportLines -Text 'Beta Hardening - Installer Upgrade/Rollback Test'
    Add-ReportLine -Lines $reportLines -Text ("utc_started={0}" -f ([DateTimeOffset]::UtcNow.ToString('o')))
    Add-ReportLine -Lines $reportLines -Text ("repo_root={0}" -f $repoRoot)
    Add-ReportLine -Lines $reportLines -Text ("install_dir={0}" -f $InstallDir)
    Add-ReportLine -Lines $reportLines -Text ("smoke_cycles={0}" -f $SmokeCycles)

    Assert-StableInnoAppId -RepoRoot $repoRoot -Lines $reportLines

    if ([string]::IsNullOrWhiteSpace($CurrentInstallerPath)) {
        $CurrentInstallerPath = Resolve-DefaultCurrentInstallerExe -RepoRoot $repoRoot
    }
    if ([string]::IsNullOrWhiteSpace($CurrentInstallerPath)) {
        throw 'Current installer EXE not found. Build installer first or pass -CurrentInstallerPath.'
    }
    if ([string]::IsNullOrWhiteSpace($OldInstallerPath)) {
        $OldInstallerPath = Resolve-OldInstallerAuto -RepoRoot $repoRoot -CurrentInstaller $CurrentInstallerPath
    }
    if ([string]::IsNullOrWhiteSpace($OldInstallerPath)) {
        throw 'Old installer EXE not found. Pass -OldInstallerPath.'
    }

    $CurrentInstallerPath = (Resolve-Path $CurrentInstallerPath).Path
    $OldInstallerPath = (Resolve-Path $OldInstallerPath).Path

    Add-ReportLine -Lines $reportLines -Text ("old_installer={0}" -f $OldInstallerPath)
    Add-ReportLine -Lines $reportLines -Text ("current_installer={0}" -f $CurrentInstallerPath)

    $existingEntries = @(Get-InstalledNLinkUninstallEntries)
    foreach ($entry in $existingEntries) {
        Add-ReportLine -Lines $reportLines -Text ("existing_uninstall_entry: install_location={0}; uninstall={1}" -f ([string]$entry.InstallLocation), ([string]$entry.UninstallString))
    }

    if ($existingEntries.Count -gt 0 -and -not $AllowExistingInstallImpact.IsPresent) {
        throw 'Detected existing nLink uninstall entry. Re-run with -AllowExistingInstallImpact only in an isolated test environment.'
    }

    $baselineNodeSnapshot = Get-NodeProcessSnapshot
    $baselineUserState = Get-NLinkUserStateSnapshot
    foreach ($line in @(Format-UserStateSnapshotLines -Label 'baseline_before_install' -Snapshot $baselineUserState)) {
        Add-ReportLine -Lines $reportLines -Text $line
    }

    if (Test-Path $InstallDir) {
        Add-ReportLine -Lines $reportLines -Text 'preclean: existing test install dir found; attempting removal.'
        try { Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue } catch {}
    }

    $stepLogDir = Join-Path $repoRoot 'artifacts\beta-hardening\installer-logs'
    New-Item -ItemType Directory -Force -Path $stepLogDir | Out-Null

    $oldInstallResult = Invoke-InnoSilentInstall -SetupExe $OldInstallerPath -InstallDir $InstallDir -InstallerLogPath (Join-Path $stepLogDir 'install-old.log')
    Assert-ExitCodeZero -Result $oldInstallResult -StepName 'Install old version'
    Add-ReportLine -Lines $reportLines -Text ("install_old: PASS; exit_code=0; duration_ms={0}" -f $oldInstallResult.DurationMs)

    $installedExe = Join-Path $InstallDir 'nLink.exe'
    if (-not (Test-Path $installedExe)) {
        throw "Installed EXE not found after old install: $installedExe"
    }

    $oldSmokeLogSnapshot = Get-LogLineSnapshot
    $oldSmoke = Invoke-NLinkDevLocalSmoke -ExePath $installedExe -Cycles $SmokeCycles -WorkingDirectory $repoRoot -TimeoutSeconds 180
    Assert-ExitCodeZero -Result $oldSmoke -StepName 'Old version DevLocal smoke'
    Add-ReportLine -Lines $reportLines -Text ("smoke_old: PASS; exit_code=0; duration_ms={0}" -f $oldSmoke.DurationMs)
    Add-ReportLine -Lines $reportLines -Text ("smoke_old_stdout_first_line={0}" -f (($oldSmoke.StdOut -split "`r?`n" | Select-Object -First 1)))
    $stateAfterOldSmoke = Get-NLinkUserStateSnapshot
    foreach ($line in @(Format-UserStateSnapshotLines -Label 'after_old_smoke' -Snapshot $stateAfterOldSmoke)) {
        Add-ReportLine -Lines $reportLines -Text $line
    }

    $upgradeInstallResult = Invoke-InnoSilentInstall -SetupExe $CurrentInstallerPath -InstallDir $InstallDir -InstallerLogPath (Join-Path $stepLogDir 'install-current-over-old.log')
    Assert-ExitCodeZero -Result $upgradeInstallResult -StepName 'Install current version over old'
    Add-ReportLine -Lines $reportLines -Text ("install_upgrade: PASS; exit_code=0; duration_ms={0}" -f $upgradeInstallResult.DurationMs)

    if (-not (Test-Path $installedExe)) {
        throw "Installed EXE not found after upgrade install: $installedExe"
    }

    $upgradeSmoke = Invoke-NLinkDevLocalSmoke -ExePath $installedExe -Cycles $SmokeCycles -WorkingDirectory $repoRoot -TimeoutSeconds 180
    Assert-ExitCodeZero -Result $upgradeSmoke -StepName 'Upgraded version DevLocal smoke'
    Add-ReportLine -Lines $reportLines -Text ("smoke_upgrade: PASS; exit_code=0; duration_ms={0}" -f $upgradeSmoke.DurationMs)
    Add-ReportLine -Lines $reportLines -Text ("smoke_upgrade_stdout_first_line={0}" -f (($upgradeSmoke.StdOut -split "`r?`n" | Select-Object -First 1)))
    $stateAfterUpgrade = Get-NLinkUserStateSnapshot
    foreach ($line in @(Format-UserStateSnapshotLines -Label 'after_upgrade_smoke' -Snapshot $stateAfterUpgrade)) {
        Add-ReportLine -Lines $reportLines -Text $line
    }

    $uninstallCurrentResult = Invoke-InnoSilentUninstall -InstallDir $InstallDir -InstallerLogPath (Join-Path $stepLogDir 'uninstall-current.log')
    Assert-ExitCodeZero -Result $uninstallCurrentResult -StepName 'Uninstall current version'
    Add-ReportLine -Lines $reportLines -Text ("uninstall_current: PASS; exit_code=0; duration_ms={0}" -f $uninstallCurrentResult.DurationMs)
    Assert-NoOrphanNodeForInstallDir -InstallDir $InstallDir -Lines $reportLines -Label 'post_uninstall_current_node_check'

    $rollbackInstallResult = Invoke-InnoSilentInstall -SetupExe $OldInstallerPath -InstallDir $InstallDir -InstallerLogPath (Join-Path $stepLogDir 'reinstall-old.log')
    Assert-ExitCodeZero -Result $rollbackInstallResult -StepName 'Reinstall old version'
    Add-ReportLine -Lines $reportLines -Text ("reinstall_old: PASS; exit_code=0; duration_ms={0}" -f $rollbackInstallResult.DurationMs)

    if (-not (Test-Path $installedExe)) {
        throw "Installed EXE not found after rollback reinstall: $installedExe"
    }

    $rollbackSmoke = Invoke-NLinkDevLocalSmoke -ExePath $installedExe -Cycles $SmokeCycles -WorkingDirectory $repoRoot -TimeoutSeconds 180
    Assert-ExitCodeZero -Result $rollbackSmoke -StepName 'Rollback old version DevLocal smoke'
    Add-ReportLine -Lines $reportLines -Text ("smoke_rollback_old: PASS; exit_code=0; duration_ms={0}" -f $rollbackSmoke.DurationMs)
    Add-ReportLine -Lines $reportLines -Text ("smoke_rollback_stdout_first_line={0}" -f (($rollbackSmoke.StdOut -split "`r?`n" | Select-Object -First 1)))
    $stateAfterRollback = Get-NLinkUserStateSnapshot
    foreach ($line in @(Format-UserStateSnapshotLines -Label 'after_rollback_smoke' -Snapshot $stateAfterRollback)) {
        Add-ReportLine -Lines $reportLines -Text $line
    }

    Assert-SettingsPersistenceBehavior -BaselineAfterOldSmoke $stateAfterOldSmoke -AfterUpgrade $stateAfterUpgrade -AfterRollback $stateAfterRollback -Lines $reportLines

    if (-not $KeepInstalledForInspection.IsPresent) {
        $finalUninstallResult = Invoke-InnoSilentUninstall -InstallDir $InstallDir -InstallerLogPath (Join-Path $stepLogDir 'uninstall-old-final.log')
        Assert-ExitCodeZero -Result $finalUninstallResult -StepName 'Final uninstall old version'
        Add-ReportLine -Lines $reportLines -Text ("uninstall_old_final: PASS; exit_code=0; duration_ms={0}" -f $finalUninstallResult.DurationMs)
        Assert-NoOrphanNodeForInstallDir -InstallDir $InstallDir -Lines $reportLines -Label 'post_uninstall_final_node_check'
    }
    else {
        Add-ReportLine -Lines $reportLines -Text 'final_uninstall: SKIP (KeepInstalledForInspection set)'
    }

    $finalNodeSnapshot = Get-NodeProcessSnapshot
    $newNodePids = @($finalNodeSnapshot | Where-Object { @($baselineNodeSnapshot.ProcessId) -notcontains $_.ProcessId })
    Add-ReportLine -Lines $reportLines -Text ("node_processes_before={0}; after={1}; new_after={2}" -f $baselineNodeSnapshot.Count, $finalNodeSnapshot.Count, $newNodePids.Count)

    Add-ReportLine -Lines $reportLines -Text 'RESULT: PASS'
    $exitCode = 0
}
catch {
    Add-ReportLine -Lines $reportLines -Text ("RESULT: FAIL")
    Add-ReportLine -Lines $reportLines -Text ("error={0}" -f $_.Exception.Message)
    Add-ReportLine -Lines $reportLines -Text ("error_type={0}" -f $_.Exception.GetType().FullName)
    $exitCode = 1
}
finally {
    if (-not $KeepInstalledForInspection.IsPresent) {
        try {
            if (Test-Path (Join-Path $InstallDir 'unins000.exe')) {
                $cleanupAttempted = $true
                $cleanupResult = Invoke-InnoSilentUninstall -InstallDir $InstallDir -InstallerLogPath (Join-Path $repoRoot 'artifacts\beta-hardening\installer-logs\cleanup-finally.log')
                Add-ReportLine -Lines $reportLines -Text ("cleanup_finally_uninstall_exit={0}" -f $cleanupResult.ExitCode)
            }
        }
        catch {
            Add-ReportLine -Lines $reportLines -Text ("cleanup_finally_uninstall_error={0}" -f $_.Exception.Message)
        }
    }

    try {
        if (Test-Path $InstallDir) {
            Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
        }
    }
    catch {
        Add-ReportLine -Lines $reportLines -Text ("cleanup_install_dir_error={0}" -f $_.Exception.Message)
    }

    Add-ReportLine -Lines $reportLines -Text ("cleanup_attempted={0}" -f $cleanupAttempted)
    Add-ReportLine -Lines $reportLines -Text ("utc_finished={0}" -f ([DateTimeOffset]::UtcNow.ToString('o')))

    Write-BetaHardeningArtifact -Path $artifactAbs -Lines @($reportLines)
    Write-Host ("[beta-hardening] installer upgrade/rollback report: {0}" -f $artifactAbs)
}

exit $exitCode
