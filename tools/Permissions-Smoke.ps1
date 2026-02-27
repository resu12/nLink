param(
    [string]$InstallerExePath = '',
    [string]$PortableZipPath = '',
    [string]$PortableExePath = '',
    [int]$SmokeCycles = 3,
    [string]$ArtifactPath = 'artifacts/beta-hardening/permissions-smoke.txt',
    [switch]$KeepScratch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'BetaHardening.Common.ps1')

function Add-Line {
    param([System.Collections.Generic.List[string]]$Lines, [string]$Text)
    [void]$Lines.Add($Text)
}

function Assert-ProcessSuccess {
    param([Parameter(Mandatory = $true)]$Result, [Parameter(Mandatory = $true)][string]$Label)
    if ($Result.TimedOut) { throw "$Label timed out." }
    if ([int]$Result.ExitCode -ne 0) { throw "$Label failed with exit code $($Result.ExitCode)." }
}

function Assert-ProcessFailedGracefullyForWritePath {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Label,
        [System.Collections.Generic.List[string]]$Lines
    )

    if ($Result.TimedOut) {
        throw "$Label timed out."
    }
    if ($null -eq $Result.ExitCode -or [int]$Result.ExitCode -eq 0) {
        throw "$Label was expected to fail on non-writable path, but exited 0."
    }

    $combined = ([string]$Result.StdOut) + [Environment]::NewLine + ([string]$Result.StdErr)
    $patterns = @('UnauthorizedAccess', 'Access is denied', 'access to the path', 'permission', 'denied')
    foreach ($pattern in $patterns) {
        if ([regex]::IsMatch($combined, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            Add-Line -Lines $Lines -Text ("{0}: graceful failure pattern matched '{1}'" -f $Label, $pattern)
            return
        }
    }

    Add-Line -Lines $Lines -Text ("{0}: output did not match canonical access-denied tokens; stderr_head={1}" -f $Label, ((($Result.StdErr -split "`r?`n") | Select-Object -First 3) -join ' | ')))
    throw "$Label failed, but not with a recognizable graceful non-writable-path error message."
}

function Invoke-Icacls {
    param([string[]]$Args)
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    return (Invoke-ProcessCapture -FilePath $icacls -ArgumentList $Args -TimeoutSeconds 120)
}

function Set-TempDirReadOnlyForCurrentUser {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [System.Collections.Generic.List[string]]$Lines
    )

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $denyArgs = @($Path, '/deny', ("{0}:(OI)(CI)W" -f $identity), '/t', '/c', '/q')
    $result = Invoke-Icacls -Args $denyArgs
    if ($result.TimedOut -or [int]$result.ExitCode -ne 0) {
        Add-Line -Lines $Lines -Text ("icacls_deny_exit={0}" -f $result.ExitCode)
        Add-Line -Lines $Lines -Text ("icacls_deny_stderr={0}" -f ((($result.StdErr -split "`r?`n") | Select-Object -First 3) -join ' | ')))
        throw 'Failed to apply read-only ACL to portable temp directory.'
    }
    Add-Line -Lines $Lines -Text ("portable_readonly_acl: applied deny-write for {0}" -f $identity)

    return [pscustomobject]@{ Path = $Path; Identity = $identity }
}

function Clear-TempDirReadOnlyForCurrentUser {
    param(
        [Parameter(Mandatory = $true)]$AclState,
        [System.Collections.Generic.List[string]]$Lines
    )

    $removeArgs = @([string]$AclState.Path, '/remove:d', [string]$AclState.Identity, '/t', '/c', '/q')
    $result = Invoke-Icacls -Args $removeArgs
    Add-Line -Lines $Lines -Text ("portable_readonly_acl_remove_exit={0}" -f $result.ExitCode)
}

function Resolve-PortableRootAndExe {
    param(
        [Parameter(Mandatory = $true)][string]$ScratchRoot,
        [string]$PortableZipPath,
        [string]$PortableExePath,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $portableStage = Join-Path $ScratchRoot 'portable-stage'
    New-Item -ItemType Directory -Force -Path $portableStage | Out-Null

    if ([string]::IsNullOrWhiteSpace($PortableZipPath)) {
        $PortableZipPath = Resolve-DefaultPortableZip -RepoRoot $RepoRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($PortableZipPath)) {
        $zipAbs = (Resolve-Path $PortableZipPath).Path
        Expand-Archive -Path $zipAbs -DestinationPath $portableStage -Force
    }
    elseif (-not [string]::IsNullOrWhiteSpace($PortableExePath)) {
        $exeAbs = (Resolve-Path $PortableExePath).Path
        $sourceRoot = Split-Path -Parent $exeAbs
        Copy-Item -Recurse -Force (Join-Path $sourceRoot '*') $portableStage
    }
    else {
        throw 'Portable ZIP/EXE not found. Build portable artifact first or pass -PortableZipPath/-PortableExePath.'
    }

    $exe = Get-ChildItem -Path $portableStage -Recurse -File -Filter 'nLink.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $exe) {
        throw 'Could not locate nLink.exe in extracted portable payload.'
    }

    return [pscustomobject]@{
        PortableRoot = $exe.Directory.FullName
        ExePath = $exe.FullName
    }
}

function Assert-NoNLinkNodeForPathHints {
    param([string[]]$PathHints, [System.Collections.Generic.List[string]]$Lines, [string]$Label)
    $snapshot = Get-NodeProcessSnapshot
    $matches = @(Find-NLinkNodeProcesses -Snapshot $snapshot -PathHints $PathHints)
    if ($matches.Count -gt 0) {
        foreach ($m in $matches) {
            Add-Line -Lines $Lines -Text ("{0}: orphan_node pid={1}; exe={2}; cmd={3}" -f $Label, $m.ProcessId, $m.ExecutablePath, $m.CommandLine)
        }
        throw "$Label failed: nLink-related node.exe process still running."
    }
    Add-Line -Lines $Lines -Text ("{0}: PASS" -f $Label)
}

$repoRoot = Get-BetaHardeningRepoRoot
$artifactAbs = Resolve-BetaHardeningPath -RepoRoot $repoRoot -Path $ArtifactPath
$lines = New-Object System.Collections.Generic.List[string]
$exitCode = 1
$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nlink-permissions-smoke-' + [Guid]::NewGuid().ToString('N'))
$aclState = $null
$portableInfo = $null
$programFilesInstallDir = Join-Path $env:ProgramFiles 'nLink-BetaHardening-Permissions'

try {
    New-Item -ItemType Directory -Force -Path $scratchRoot | Out-Null

    Add-Line -Lines $lines -Text 'Beta Hardening - Permissions Smoke'
    Add-Line -Lines $lines -Text ("utc_started={0}" -f ([DateTimeOffset]::UtcNow.ToString('o')))
    Add-Line -Lines $lines -Text ("repo_root={0}" -f $repoRoot)
    Add-Line -Lines $lines -Text ("is_admin={0}" -f (Test-IsAdministrator))
    Add-Line -Lines $lines -Text ("scratch_root={0}" -f $scratchRoot)
    Add-Line -Lines $lines -Text ("smoke_cycles={0}" -f $SmokeCycles)

    if ([string]::IsNullOrWhiteSpace($InstallerExePath)) {
        $InstallerExePath = Resolve-DefaultCurrentInstallerExe -RepoRoot $repoRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($InstallerExePath)) {
        $InstallerExePath = (Resolve-Path $InstallerExePath).Path
    }

    Add-Line -Lines $lines -Text ("installer_exe={0}" -f $(if ([string]::IsNullOrWhiteSpace($InstallerExePath)) { '(missing)' } else { $InstallerExePath }))

    if (-not [string]::IsNullOrWhiteSpace($InstallerExePath)) {
        $installerLog = Join-Path $repoRoot 'artifacts\beta-hardening\permissions-installer-programfiles.log'
        if (Test-Path $programFilesInstallDir) {
            try { Remove-Item -Recurse -Force $programFilesInstallDir -ErrorAction SilentlyContinue } catch {}
        }

        $installAttempt = Invoke-InnoSilentInstall -SetupExe $InstallerExePath -InstallDir $programFilesInstallDir -InstallerLogPath $installerLog
        Add-Line -Lines $lines -Text ("program_files_install_attempt_exit={0}" -f $installAttempt.ExitCode)
        Add-Line -Lines $lines -Text ("program_files_install_attempt_timed_out={0}" -f $installAttempt.TimedOut)

        if (-not $installAttempt.TimedOut -and [int]$installAttempt.ExitCode -eq 0 -and (Test-Path (Join-Path $programFilesInstallDir 'nLink.exe'))) {
            Add-Line -Lines $lines -Text 'program_files_install: PASS (install succeeded)'
            $pfSmoke = Invoke-NLinkDevLocalSmoke -ExePath (Join-Path $programFilesInstallDir 'nLink.exe') -Cycles $SmokeCycles -WorkingDirectory $repoRoot -TimeoutSeconds 180
            Assert-ProcessSuccess -Result $pfSmoke -Label 'Program Files installed app DevLocal smoke'
            Add-Line -Lines $lines -Text ("program_files_smoke: PASS; duration_ms={0}" -f $pfSmoke.DurationMs)

            $pfUninstall = Invoke-InnoSilentUninstall -InstallDir $programFilesInstallDir -InstallerLogPath (Join-Path $repoRoot 'artifacts\beta-hardening\permissions-installer-programfiles-uninstall.log')
            Assert-ProcessSuccess -Result $pfUninstall -Label 'Program Files uninstall'
            Add-Line -Lines $lines -Text 'program_files_uninstall: PASS'
            Assert-NoNLinkNodeForPathHints -PathHints @($programFilesInstallDir, 'nLink\\bridge', 'nkn-bridge') -Lines $lines -Label 'program_files_node_orphan_check'
        }
        else {
            $combinedInstallerText = ([string]$installAttempt.StdOut) + [Environment]::NewLine + ([string]$installAttempt.StdErr)
            if (Test-Path $installerLog) {
                try {
                    $combinedInstallerText += [Environment]::NewLine + (Get-Content $installerLog -Raw -ErrorAction SilentlyContinue)
                }
                catch {}
            }

            $gracefulPatterns = @('Access is denied', 'privilege', 'permission', 'cannot create', 'error opening file for writing', 'requires')
            $matched = $null
            foreach ($p in $gracefulPatterns) {
                if ([regex]::IsMatch($combinedInstallerText, $p, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                    $matched = $p
                    break
                }
            }

            if ($null -eq $matched) {
                throw 'Program Files installer attempt failed, but no recognizable graceful permissions/elevation error was found.'
            }

            Add-Line -Lines $lines -Text ("program_files_install: PASS (graceful denial); matched='{0}'" -f $matched)
        }
    }
    else {
        Add-Line -Lines $lines -Text 'program_files_install: SKIP (installer EXE not found)'
    }

    $portableInfo = Resolve-PortableRootAndExe -ScratchRoot $scratchRoot -PortableZipPath $PortableZipPath -PortableExePath $PortableExePath -RepoRoot $repoRoot
    Add-Line -Lines $lines -Text ("portable_root={0}" -f $portableInfo.PortableRoot)
    Add-Line -Lines $lines -Text ("portable_exe={0}" -f $portableInfo.ExePath)

    $aclState = Set-TempDirReadOnlyForCurrentUser -Path $portableInfo.PortableRoot -Lines $lines

    $logBefore = Get-LogLineSnapshot
    Add-Line -Lines $lines -Text ("log_snapshot_before: path={0}; exists={1}; lines={2}" -f $logBefore.Path, $logBefore.Exists, $logBefore.LineCount)

    $nonWritableRun = Invoke-NLinkDevLocalSmoke -ExePath $portableInfo.ExePath -Cycles 1 -WorkingDirectory $portableInfo.PortableRoot -TimeoutSeconds 120
    Add-Line -Lines $lines -Text ("portable_nonwritable_cwd_exit={0}" -f $nonWritableRun.ExitCode)
    Assert-ProcessFailedGracefullyForWritePath -Result $nonWritableRun -Label 'portable_nonwritable_cwd' -Lines $lines

    $writableCwd = Join-Path $repoRoot 'artifacts\beta-hardening\permissions-portable-work'
    New-Item -ItemType Directory -Force -Path $writableCwd | Out-Null
    $readOnlyExeDirRun = Invoke-NLinkDevLocalSmoke -ExePath $portableInfo.ExePath -Cycles $SmokeCycles -WorkingDirectory $writableCwd -TimeoutSeconds 180
    Assert-ProcessSuccess -Result $readOnlyExeDirRun -Label 'portable_readonly_exe_dir_writable_cwd'
    Add-Line -Lines $lines -Text ("portable_readonly_exe_dir_writable_cwd: PASS; duration_ms={0}" -f $readOnlyExeDirRun.DurationMs)

    $newLogLines = @(Get-NewLogLines -Snapshot $logBefore)
    $logPath = Get-NLinkLogFilePath
    if (-not (Test-Path $logPath)) {
        throw 'Expected operational log file in user-writable location was not found.'
    }
    if ($logPath.IndexOf((Join-Path $env:LOCALAPPDATA 'nLink\logs'), [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw ("Operational log path is not under user-writable LocalAppData: {0}" -f $logPath)
    }
    Add-Line -Lines $lines -Text ("user_log_path={0}" -f $logPath)
    Add-Line -Lines $lines -Text ("new_log_line_count={0}" -f $newLogLines.Count)
    Add-Line -Lines $lines -Text 'logs_user_writable_location: PASS'

    Assert-NoNLinkNodeForPathHints -PathHints @($portableInfo.PortableRoot, 'nLink\\bridge', 'nkn-bridge') -Lines $lines -Label 'portable_node_orphan_check'

    Add-Line -Lines $lines -Text 'RESULT: PASS'
    $exitCode = 0
}
catch {
    Add-Line -Lines $lines -Text 'RESULT: FAIL'
    Add-Line -Lines $lines -Text ("error={0}" -f $_.Exception.Message)
    Add-Line -Lines $lines -Text ("error_type={0}" -f $_.Exception.GetType().FullName)
    $exitCode = 1
}
finally {
    if ($null -ne $aclState) {
        try {
            Clear-TempDirReadOnlyForCurrentUser -AclState $aclState -Lines $lines
        }
        catch {
            Add-Line -Lines $lines -Text ("acl_restore_error={0}" -f $_.Exception.Message)
        }
    }

    if (-not $KeepScratch.IsPresent) {
        try {
            if (Test-Path $scratchRoot) {
                Remove-Item -Recurse -Force $scratchRoot -ErrorAction SilentlyContinue
            }
        }
        catch {
            Add-Line -Lines $lines -Text ("scratch_cleanup_error={0}" -f $_.Exception.Message)
        }
    }

    try {
        if (Test-Path (Join-Path $programFilesInstallDir 'unins000.exe')) {
            $cleanupPf = Invoke-InnoSilentUninstall -InstallDir $programFilesInstallDir -InstallerLogPath (Join-Path $repoRoot 'artifacts\beta-hardening\permissions-installer-cleanup.log')
            Add-Line -Lines $lines -Text ("cleanup_program_files_uninstall_exit={0}" -f $cleanupPf.ExitCode)
        }
    }
    catch {
        Add-Line -Lines $lines -Text ("cleanup_program_files_uninstall_error={0}" -f $_.Exception.Message)
    }

    Add-Line -Lines $lines -Text ("utc_finished={0}" -f ([DateTimeOffset]::UtcNow.ToString('o')))
    Write-BetaHardeningArtifact -Path $artifactAbs -Lines @($lines)
    Write-Host ("[beta-hardening] permissions smoke report: {0}" -f $artifactAbs)
}

exit $exitCode
