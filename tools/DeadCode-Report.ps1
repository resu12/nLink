param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Parse-WarningLine {
    param([Parameter(Mandatory = $true)][string]$Line)

    $regex = '^(?<file>[A-Za-z]:\\.+?)\((?<line>\d+),(?<col>\d+)\):\s+warning\s+(?<code>[A-Z0-9]+):\s+(?<message>.+?)\s+\[(?<project>.+?)\]$'
    $m = [regex]::Match($Line, $regex)
    if (-not $m.Success) {
        return $null
    }

    [pscustomobject]@{
        File    = $m.Groups['file'].Value
        Line    = [int]$m.Groups['line'].Value
        Column  = [int]$m.Groups['col'].Value
        Code    = $m.Groups['code'].Value
        Message = $m.Groups['message'].Value.Trim()
        Project = $m.Groups['project'].Value
        RawLine = $Line
    }
}

function Get-ScriptReferences {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ScriptRelativePath
    )

    $searchRoots = @(
        (Join-Path $RepoRoot "README.md"),
        (Join-Path $RepoRoot "docs"),
        (Join-Path $RepoRoot ".github"),
        (Join-Path $RepoRoot "tools"),
        (Join-Path $RepoRoot "installer")
    ) | Where-Object { Test-Path $_ }

    $needle = [System.IO.Path]::GetFileName($ScriptRelativePath)
    $refs = @()
    foreach ($root in $searchRoots) {
        $items = if (Test-Path $root -PathType Leaf) { @($root) } else { Get-ChildItem -Path $root -Recurse -File }
        foreach ($item in $items) {
            $itemPath = if ($item -is [string]) { $item } else { $item.FullName }
            if ($itemPath -ieq (Join-Path $RepoRoot $ScriptRelativePath)) {
                continue
            }

            try {
                $matches = Select-String -Path $itemPath -SimpleMatch -Pattern $needle -ErrorAction Stop
                foreach ($match in $matches) {
                    $refs += [pscustomobject]@{
                        File = $itemPath
                        Line = $match.LineNumber
                        Text = $match.Line.Trim()
                    }
                }
            }
            catch {
                # Non-text files can fail; ignore.
            }
        }
    }

    return $refs
}

function To-RepoRelative {
    param([string]$RepoRoot, [string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($RepoRoot.Length).TrimStart('\','/')
    }
    return $Path
}

$repoRoot = Resolve-RepoRoot
$outDir = Join-Path $repoRoot "artifacts\deadcode"
Ensure-Directory -Path $outDir
$buildLog = Join-Path $outDir "build-output.log"
$reportPath = Join-Path $outDir "report.md"

Push-Location $repoRoot
try {
    Write-Host "[nLink] Running build for dead-code warning evidence..." -ForegroundColor Cyan
    & dotnet build .\nLink.sln -c $Configuration 2>&1 | Tee-Object -FilePath $buildLog | Out-Null
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

$buildLines = Get-Content $buildLog -ErrorAction SilentlyContinue
$warnings = @()
foreach ($line in $buildLines) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }
    $parsed = Parse-WarningLine -Line $line
    if ($null -ne $parsed) {
        $warnings += $parsed
    }
}

$deadCodeLikeCodes = @('IDE0005', 'CS0067', 'CS0169', 'CS0414', 'CS0649')
$safeCodes = @('IDE0005', 'CS0067', 'CS0169', 'CS0414')
$mediumCodes = @('CS0649')

$topWarnings = @($warnings |
    Group-Object Code |
    Sort-Object @{ Expression = 'Count'; Descending = $true }, @{ Expression = 'Name'; Descending = $false } |
    Select-Object -First 10)

$filesWithWarnings = @($warnings |
    Group-Object File |
    Sort-Object @{ Expression = 'Count'; Descending = $true }, @{ Expression = 'Name'; Descending = $false } |
    Select-Object -First 10)

$safeCandidates = @()
$mediumCandidates = @()

foreach ($w in ($warnings | Where-Object { $deadCodeLikeCodes -contains $_.Code })) {
    $candidate = [pscustomobject]@{
        File    = (To-RepoRelative -RepoRoot $repoRoot -Path $w.File)
        Line    = $w.Line
        Code    = $w.Code
        Message = $w.Message
        Evidence = "compiler warning"
    }

    if ($safeCodes -contains $w.Code) {
        $safeCandidates += $candidate
    }
    elseif ($mediumCodes -contains $w.Code) {
        $mediumCandidates += $candidate
    }
}

# Script reference scan for "possibly unused scripts not referenced by docs/scripts".
$scriptCandidates = @()
$toolScripts = Get-ChildItem -Path (Join-Path $repoRoot "tools") -File -Filter *.ps1 -ErrorAction SilentlyContinue
foreach ($script in $toolScripts) {
    $relScript = To-RepoRelative -RepoRoot $repoRoot -Path $script.FullName
    $refs = Get-ScriptReferences -RepoRoot $repoRoot -ScriptRelativePath $relScript
    $refsArray = @($refs)
    if ($refsArray.Count -eq 0) {
        $scriptCandidates += [pscustomobject]@{
            File     = $relScript
            Line     = ''
            Code     = 'SCRIPT-UNREFERENCED'
            Message  = 'PowerShell script not referenced by README/docs/workflows/scripts (manual review).'
            Evidence = 'text reference scan'
        }
    }
}

$safeCandidates += $scriptCandidates
$safeCandidates = @($safeCandidates | Sort-Object File, Line, Code)
$mediumCandidates = @($mediumCandidates | Sort-Object File, Line, Code)

# High-risk areas are not "safe delete" candidates; report as protected/manual-review surfaces.
$highRiskEvidence = @()
$highRiskDirs = @(
    "src\nLink.App\Views",
    "src\nLink.App\Assets",
    "src\nLink.App\App.axaml",
    "tools\nkn-bridge"
) | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }

foreach ($path in $highRiskDirs) {
    if (Test-Path $path -PathType Container) {
        $count = (Get-ChildItem -Path $path -Recurse -File | Measure-Object).Count
        $highRiskEvidence += [pscustomobject]@{
            Area = (To-RepoRelative -RepoRoot $repoRoot -Path $path)
            Why  = "Framework/string/reflection/protocol loaded; not safe for dead-code deletion by warning-only scan."
            Count = $count
        }
    }
    else {
        $highRiskEvidence += [pscustomobject]@{
            Area = (To-RepoRelative -RepoRoot $repoRoot -Path $path)
            Why  = "Framework entry/resource file; not safe for dead-code deletion by warning-only scan."
            Count = 1
        }
    }
}

$generatedAt = [DateTimeOffset]::UtcNow.ToString("u")
$buildStatus = if ($buildExitCode -eq 0) { "PASS" } else { "FAIL" }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Dead Code Candidate Report")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Generated: $generatedAt")
[void]$sb.AppendLine("Configuration: $Configuration")
[void]$sb.AppendLine("Build status (evidence run): **$buildStatus**")
[void]$sb.AppendLine()
[void]$sb.AppendLine("This report is **non-destructive**. It collects warning evidence and conservative candidates only.")
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Evidence")
[void]$sb.AppendLine()
[void]$sb.AppendLine('- Build log: `artifacts/deadcode/build-output.log`')
[void]$sb.AppendLine("- Total parsed warnings: **$($warnings.Count)**")
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Top Warning Codes")
[void]$sb.AppendLine()
if ($topWarnings.Count -eq 0) {
    [void]$sb.AppendLine("_No warnings parsed._")
}
else {
    [void]$sb.AppendLine("| Warning | Count |")
    [void]$sb.AppendLine("|---|---:|")
    foreach ($g in $topWarnings) {
        [void]$sb.AppendLine(('| `{0}` | {1} |' -f $g.Name, $g.Count))
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Files With Most Warnings")
[void]$sb.AppendLine()
if ($filesWithWarnings.Count -eq 0) {
    [void]$sb.AppendLine("_No warnings parsed._")
}
else {
    [void]$sb.AppendLine("| File | Warning Count |")
    [void]$sb.AppendLine("|---|---:|")
    foreach ($g in $filesWithWarnings) {
        [void]$sb.AppendLine(('| `{0}` | {1} |' -f (To-RepoRelative -RepoRoot $repoRoot -Path $g.Name), $g.Count))
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Candidate Dead Code (Risk Grouped)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("### SAFE")
[void]$sb.AppendLine()
if ($safeCandidates.Count -eq 0) {
    [void]$sb.AppendLine("_No SAFE candidates found from current warning evidence / script scan._")
}
else {
    foreach ($c in $safeCandidates) {
        $loc = if ([string]::IsNullOrWhiteSpace([string]$c.Line)) { "" } else { ":$($c.Line)" }
        [void]$sb.AppendLine(('- `{0}{1}` - `{2}` - {3} _(evidence: {4})_' -f $c.File, $loc, $c.Code, $c.Message, $c.Evidence))
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("### MEDIUM")
[void]$sb.AppendLine()
if ($mediumCandidates.Count -eq 0) {
    [void]$sb.AppendLine("_No MEDIUM-risk candidates found from current compiler/analyzer warnings._")
}
else {
    foreach ($c in $mediumCandidates) {
        $loc = if ([string]::IsNullOrWhiteSpace([string]$c.Line)) { "" } else { ":$($c.Line)" }
        [void]$sb.AppendLine(('- `{0}{1}` - `{2}` - {3} _(manual review required)_' -f $c.File, $loc, $c.Code, $c.Message))
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("### HIGH")
[void]$sb.AppendLine()
[void]$sb.AppendLine("High-risk areas are not auto-marked as removable. These are **manual-review only** surfaces because they are commonly referenced by framework conventions, reflection, or string keys.")
[void]$sb.AppendLine()
foreach ($h in $highRiskEvidence) {
    [void]$sb.AppendLine(('- `{0}` ({1} file(s)) - {2}' -f $h.Area, $h.Count, $h.Why))
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Notes")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- SAFE candidates are based on compiler/analyzer evidence and a simple script-reference scan.")
[void]$sb.AppendLine("- MEDIUM/HIGH items require human review before deletion.")
[void]$sb.AppendLine("- This report does **not** delete or modify source files.")

[System.IO.File]::WriteAllText($reportPath, $sb.ToString(), [System.Text.Encoding]::UTF8)

Write-Host "[nLink] Dead-code report written to: $reportPath" -ForegroundColor Green
if ($buildExitCode -ne 0) {
    Write-Warning "[nLink] Build used for evidence failed. Report still generated from captured output."
    exit $buildExitCode
}

