Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-BetaHardeningRepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Resolve-BetaHardeningPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $RepoRoot $Path)
}

function Ensure-ParentDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}

function Write-BetaHardeningArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Lines
    )

    Ensure-ParentDirectory -Path $Path
    Set-Content -Path $Path -Value ($Lines -join [Environment]::NewLine) -Encoding UTF8
}

function Get-CurrentVersionFromRepo {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $versionFile = Join-Path $RepoRoot 'VERSION'
    if (-not (Test-Path $versionFile)) {
        return $null
    }

    $value = (Get-Content $versionFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return $value
}

function Resolve-DefaultPortableExe {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $candidate = Join-Path $RepoRoot 'artifacts\portable\nLink\win-x64\nLink.exe'
    if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }

    return $null
}

function Resolve-DefaultPortableZip {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $version = Get-CurrentVersionFromRepo -RepoRoot $RepoRoot
    if (-not [string]::IsNullOrWhiteSpace($version)) {
        $candidate = Join-Path $RepoRoot ("artifacts\\portable\\nLink-Portable-win-x64-{0}.zip" -f $version)
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }

    $latest = Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts\portable') -File -Filter 'nLink-Portable-win-x64-*.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -ne $latest) { return $latest.FullName }

    return $null
}

function Resolve-DefaultCurrentInstallerExe {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $version = Get-CurrentVersionFromRepo -RepoRoot $RepoRoot
    if (-not [string]::IsNullOrWhiteSpace($version)) {
        $candidate = Join-Path $RepoRoot ("artifacts\\installer\\nLink-Setup-win-x64-{0}.exe" -f $version)
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }

        $candidate = Join-Path $RepoRoot ("artifacts\\releases\\{0}\\nLink-Setup-win-x64-{0}.exe" -f $version)
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }

    $latest = Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts') -Recurse -File -Filter 'nLink-Setup-win-x64-*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -ne $latest) { return $latest.FullName }

    return $null
}

function Test-IsAdministrator {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch {
        return $false
    }
}

function Invoke-ProcessCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = '',
        [int]$TimeoutSeconds = 120
    )

    $tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nlink-beta-hardening-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
    $stdoutPath = Join-Path $tmpRoot 'stdout.txt'
    $stderrPath = Join-Path $tmpRoot 'stderr.txt'

    try {
        $start = Get-Date
        $psi = @{
            FilePath = $FilePath
            ArgumentList = $ArgumentList
            PassThru = $true
            Wait = $false
            RedirectStandardOutput = $stdoutPath
            RedirectStandardError = $stderrPath
            WindowStyle = 'Hidden'
        }
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            $psi.WorkingDirectory = $WorkingDirectory
        }

        $proc = Start-Process @psi
        $timedOut = $false
        if ($TimeoutSeconds -gt 0) {
            if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true
                try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
                try { $proc.WaitForExit(5000) | Out-Null } catch {}
            }
        }
        else {
            $proc.WaitForExit()
        }
        $end = Get-Date

        $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue } else { '' }
        $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue } else { '' }

        return [pscustomobject]@{
            FilePath = $FilePath
            Arguments = @($ArgumentList)
            WorkingDirectory = $WorkingDirectory
            ExitCode = if ($timedOut) { $null } else { $proc.ExitCode }
            TimedOut = $timedOut
            StartedAt = $start
            EndedAt = $end
            DurationMs = [int][math]::Round(($end - $start).TotalMilliseconds)
            StdOut = [string]$stdout
            StdErr = [string]$stderr
            StdOutPath = $stdoutPath
            StdErrPath = $stderrPath
        }
    }
    finally {
        # Leave temp files on disk for troubleshooting while script is running; caller may inspect paths.
    }
}

function Invoke-WithTemporaryEnvironment {
    param(
        [hashtable]$Variables,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $saved = @{}
    if ($null -eq $Variables) {
        $Variables = @{}
    }

    foreach ($key in $Variables.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable([string]$key, 'Process')
        [Environment]::SetEnvironmentVariable([string]$key, [string]$Variables[$key], 'Process')
    }

    try {
        return & $Action
    }
    finally {
        foreach ($key in $Variables.Keys) {
            $previous = $saved[[string]$key]
            [Environment]::SetEnvironmentVariable([string]$key, $previous, 'Process')
        }
    }
}

function Invoke-NLinkDevLocalSmoke {
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [int]$Cycles = 5,
        [string]$WorkingDirectory = '',
        [int]$TimeoutSeconds = 180,
        [hashtable]$EnvironmentOverrides = $null
    )

    if (-not (Test-Path $ExePath)) {
        throw "nLink executable not found: $ExePath"
    }

    $args = @(
        '--bench',
        '--cycles', [string]$Cycles,
        '--delay-ms', '0',
        '--transport', 'devlocal',
        '--bridge-reuse-mode', 'persession',
        '--reliability-gate'
    )

    $envVars = @{
        'NLINK_TRANSPORT' = 'DEVLOCAL'
    }
    if ($null -ne $EnvironmentOverrides) {
        foreach ($k in $EnvironmentOverrides.Keys) {
            $envVars[[string]$k] = [string]$EnvironmentOverrides[$k]
        }
    }

    return Invoke-WithTemporaryEnvironment -Variables $envVars -Action {
        Invoke-ProcessCapture -FilePath $ExePath -ArgumentList $args -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds
    }
}

function Get-NLinkLogsDirectory {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    return (Join-Path $localAppData 'nLink\logs')
}

function Get-NLinkLogFilePath {
    return (Join-Path (Get-NLinkLogsDirectory) 'nlink.log')
}

function Get-LogLineSnapshot {
    param([string]$Path = $(Get-NLinkLogFilePath))

    if (-not (Test-Path $Path)) {
        return [pscustomobject]@{ Path = $Path; Exists = $false; LineCount = 0 }
    }

    $count = 0
    try {
        $count = @(Get-Content $Path -ErrorAction SilentlyContinue).Count
    }
    catch {
        $count = 0
    }

    return [pscustomobject]@{ Path = $Path; Exists = $true; LineCount = $count }
}

function Get-NewLogLines {
    param([Parameter(Mandatory = $true)]$Snapshot)

    $path = [string]$Snapshot.Path
    if (-not (Test-Path $path)) {
        return @()
    }

    $all = @(Get-Content $path -ErrorAction SilentlyContinue)
    $start = 0
    try {
        $start = [int]$Snapshot.LineCount
    }
    catch {
        $start = 0
    }
    if ($start -lt 0) { $start = 0 }
    if ($start -ge $all.Count) { return @() }
    return @($all[$start..($all.Count - 1)])
}

function Get-NLinkUserStateSnapshot {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $root = Join-Path $localAppData 'nLink'
    $settingsLike = @()
    if (Test-Path $root) {
        $settingsLike = @(Get-ChildItem -Path $root -Recurse -File -Filter '*settings*.json' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName.Substring($root.Length).TrimStart('\\') } |
            Sort-Object -Unique)
    }

    $logsDir = Join-Path $root 'logs'
    $logFiles = @()
    if (Test-Path $logsDir) {
        $logFiles = @(Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue |
            Sort-Object Name |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Length = [int64]$_.Length; LastWriteTimeUtc = $_.LastWriteTimeUtc } })
    }

    $reliabilityPath = Join-Path $root 'reliability.jsonl'

    return [pscustomobject]@{
        Root = $root
        RootExists = (Test-Path $root)
        SettingsLikeFiles = $settingsLike
        LogsDir = $logsDir
        LogsDirExists = (Test-Path $logsDir)
        LogFiles = $logFiles
        ReliabilityPath = $reliabilityPath
        ReliabilityExists = (Test-Path $reliabilityPath)
    }
}

function Format-UserStateSnapshotLines {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)]$Snapshot
    )

    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add(("[{0}] root={1}; exists={2}" -f $Label, $Snapshot.Root, $Snapshot.RootExists))
    [void]$lines.Add(("[{0}] logs_dir={1}; exists={2}; log_file_count={3}" -f $Label, $Snapshot.LogsDir, $Snapshot.LogsDirExists, @($Snapshot.LogFiles).Count))
    [void]$lines.Add(("[{0}] reliability_jsonl={1}; exists={2}" -f $Label, $Snapshot.ReliabilityPath, $Snapshot.ReliabilityExists))

    if (@($Snapshot.SettingsLikeFiles).Count -eq 0) {
        [void]$lines.Add(("[{0}] settings_like_files=(none)" -f $Label))
    }
    else {
        foreach ($relative in @($Snapshot.SettingsLikeFiles)) {
            [void]$lines.Add(("[{0}] settings_like_file={1}" -f $Label, $relative))
        }
    }

    return @($lines)
}

function Get-NodeProcessSnapshot {
    $items = @()
    try {
        $items = @(Get-CimInstance Win32_Process -Filter "Name = 'node.exe' OR Name = 'node'" -ErrorAction SilentlyContinue |
            ForEach-Object {
                [pscustomobject]@{
                    ProcessId = [int]$_.ProcessId
                    Name = [string]$_.Name
                    ExecutablePath = [string]$_.ExecutablePath
                    CommandLine = [string]$_.CommandLine
                }
            })
    }
    catch {
        $items = @()
    }

    return @($items)
}

function Find-NLinkNodeProcesses {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [string[]]$PathHints = @()
    )

    $normalizedHints = @($PathHints | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { [string]$_ })
    if ($normalizedHints.Count -eq 0) {
        $normalizedHints = @('nLink', 'nkn-bridge')
    }

    return @($Snapshot | Where-Object {
        $text = (([string]$_.ExecutablePath) + ' ' + ([string]$_.CommandLine))
        foreach ($hint in $normalizedHints) {
            if ($text.IndexOf($hint, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
        return $false
    })
}

function Invoke-InnoSilentInstall {
    param(
        [Parameter(Mandatory = $true)][string]$SetupExe,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$InstallerLogPath = ''
    )

    if (-not (Test-Path $SetupExe)) {
        throw "Installer EXE not found: $SetupExe"
    }

    $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', ('/DIR={0}' -f $InstallDir))
    if (-not [string]::IsNullOrWhiteSpace($InstallerLogPath)) {
        Ensure-ParentDirectory -Path $InstallerLogPath
        $args += ('/LOG={0}' -f $InstallerLogPath)
    }

    return (Invoke-ProcessCapture -FilePath $SetupExe -ArgumentList $args -TimeoutSeconds 600)
}

function Invoke-InnoSilentUninstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$InstallerLogPath = ''
    )

    $uninstaller = Join-Path $InstallDir 'unins000.exe'
    if (-not (Test-Path $uninstaller)) {
        throw "Uninstaller not found: $uninstaller"
    }

    $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    if (-not [string]::IsNullOrWhiteSpace($InstallerLogPath)) {
        Ensure-ParentDirectory -Path $InstallerLogPath
        $args += ('/LOG={0}' -f $InstallerLogPath)
    }

    return (Invoke-ProcessCapture -FilePath $uninstaller -ArgumentList $args -TimeoutSeconds 600)
}
