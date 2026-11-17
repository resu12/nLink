param([string]$RepoRoot, [string]$OutputRoot, [ValidateSet('Imports','Summary','Overrides','Compatibility')][string]$Lane)
$ErrorActionPreference = 'Stop'
if ($Lane -eq 'Compatibility') {
    & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceAccountingReports.ps1') -RepoRoot $RepoRoot -OutputRoot $OutputRoot
    Write-Output "accounting-availability-$Lane-ok"
    exit
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/StallGraceAccounting.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'acceptance script parse errors' }
# Load definitions and scalar/list defaults, never the runner or an analyzer.
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) { Invoke-Expression $statement.Extent.Text }
}
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) { break }
    if ($statement -is [Management.Automation.Language.AssignmentStatementAst]) { Invoke-Expression $statement.Extent.Text }
}
$script:RouteAcceptanceExecutionMode = 'fake'
$GoodputRegressionTolerancePercent = 10D
$p = 'filetransfer_fallback_frontier_stall_grace_recheck_missing_'
$f = 'fileTransferFallbackFrontierStallGraceRecheckMissing'
$header = @('verdict=PASS', 'hard_failure_count=0', 'warning_count=0')
$v2 = @(($p+'accounting_version=2'), ($p+'raw_count=3'), ($p+'distinct_count=3'),
    ($p+'recovered_count=1'), ($p+'superseded_count=1'), ($p+'unresolved_count=1'), ($p+'verdict=fail'))
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function New-Result([string]$Name) {
    $dir = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    New-RouteAcceptanceRunResult -Name $Name -ArtifactDir $dir -ExpectedRoute 'file_tuna_v4' -ExpectedProtocol 4
}
function Import-Report([string]$Name, $Lines, $Result = $null) {
    if ($null -eq $Result) { $Result = New-Result $Name }
    $path = Join-Path $Result.artifactDir 'filetransfer-operator-verdict.txt'
    if ($null -ne $Lines) { [IO.File]::WriteAllLines($path, [string[]]$Lines) }
    elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    Assert-Phase4OperatorVerdict -Result $Result -RequireWarningFree $false
    return $Result
}
function Check-Row($Row, [string]$Availability, [int]$Version, [string]$Counts = '0/0/0/0/0') {
    Check ($Row.PSObject.Properties.Name -contains ($f+'AccountingAvailability')) 'missing explicit accounting availability'
    Check ($Row.($f+'AccountingAvailability') -ceq $Availability) "wrong availability for $($Row.name)"
    Check ($Row.($f+'AccountingVersion') -eq $Version) "wrong policy version for $($Row.name)"
    $actual = @('Raw','Distinct','Recovered','Superseded','Unresolved') | ForEach-Object { $Row.($f+$_+'Count') }
    Check (($actual -join '/') -ceq $Counts) "wrong counters for $($Row.name): $($actual -join '/')"
}
if ($Lane -eq 'Imports') {
    Check-Row (New-Result 'skipped') unavailable 0
    $cases = @(
        @{ Name='absent'; Lines=$null; State='unavailable' },
        @{ Name='empty'; Lines=@(); State='invalid' },
        @{ Name='unparseable'; Lines=@('not a report'); State='invalid' },
        @{ Name='unrelated'; Lines=@('other=value'); State='invalid' },
        @{ Name='header-only'; Lines=$header; State='unavailable' },
        @{ Name='bad-envelope'; Lines=@('verdict=PASS','hard_failure_count=NaN'); State='invalid' },
        @{ Name='unknown-family-key'; Lines=$header+@($p+'typo_count=0'); State='unavailable' },
        @{ Name='timeout'; Lines=@('verdict=INCONCLUSIVE','hard_failure_count=1','retained_analysis_completed=0','semantic_failure_domain=unavailable'); State='unavailable' },
        @{ Name='contradiction-completed'; Lines=$header+$v2+@('retained_analysis_completed=0'); State='invalid' },
        @{ Name='contradiction-domain'; Lines=$header+$v2+@('semantic_failure_domain=unavailable'); State='invalid' }
    )
    foreach ($case in $cases) {
        $row = Import-Report $case.Name $case.Lines
        Check-Row $row $case.State 0
        if ($case.State -eq 'invalid') {
            Check (@($row.failures | Where-Object { $_ -like 'invalid stall-grace accounting:*' }).Count -eq 1) "missing invalid reason: $($case.Name)"
        }
        if ($case.Name -eq 'absent') { Check ($row.failures -contains 'missing artifact: filetransfer-operator-verdict.txt') 'missing-file failure erased' }
    }
    Check-Row (Import-Report 'valid-v2' ($header+$v2)) valid 2 '3/3/1/1/1'
    $zero = @($v2 | ForEach-Object { $_ -replace 'count=[0-9]+$', 'count=0' -replace 'verdict=fail$', 'verdict=none' })
    Check-Row (Import-Report 'valid-zero' ($header+$zero)) valid 2
    $legacy = @(($p+'distinct_count=1'),($p+'unresolved_count=1'),($p+'verdict=fail'))
    Check-Row (Import-Report 'sparse-legacy' ($header+$legacy)) valid 1 '0/1/0/0/1'
    foreach ($case in @('version','missing','negative','overflow','nonnumeric','equation','unresolved-verdict','legacy-superseded')) {
        $lines = switch ($case) {
            'version' { $v2 -replace 'accounting_version=2$', 'accounting_version=3' }
            'missing' { $v2 | Where-Object { $_ -notlike '*superseded_count=*' } }
            'negative' { $v2 -replace 'raw_count=3$', 'raw_count=-1' }
            'overflow' { $v2 -replace 'raw_count=3$', 'raw_count=2147483648' }
            'nonnumeric' { $v2 -replace 'raw_count=3$', 'raw_count=NaN' }
            'equation' { $v2 -replace 'distinct_count=3$', 'distinct_count=2' }
            'unresolved-verdict' { $v2 -replace 'verdict=fail$', 'verdict=pass' }
            'legacy-superseded' { $v2 | Where-Object { $_ -notlike '*accounting_version=*' } }
        }
        $row = Import-Report "invalid-$case" ($header+$lines)
        Check-Row $row invalid 0
        Check ($row.($f+'Verdict') -ceq 'fail' -and @($row.failures | Where-Object { $_ -like 'invalid stall-grace accounting:*' }).Count -eq 1) "missing parser failure: $case"
    }
    foreach ($target in @('absent','invalid','header')) {
        $row = Import-Report "repeat-$target" ($header+$v2)
        $row.finalRoute = 'file_tuna_v4'
        Add-RouteAcceptanceFailure $row 'independent failure must survive'
        $next = if ($target -eq 'invalid') { $header+@($p+'accounting_version=3') } elseif ($target -eq 'header') { $header } else { $null }
        $row = Import-Report "repeat-$target" $next $row
        Check-Row $row $(if ($target -eq 'invalid') { 'invalid' } else { 'unavailable' }) 0
        Check ($row.finalRoute -ceq 'file_tuna_v4' -and $row.failures -contains 'independent failure must survive') 'reset changed unrelated evidence'
    }
}
elseif ($Lane -eq 'Summary') {
    function New-ManualRow([string]$Name, [string]$Kind) {
        $row = New-Result $Name
        # This lane mutates only stall accounting; the independent recent
        # family has a complete empty report, not an unavailable result.
        $row.fileTransferFallbackFrontierRecentProgressGraceRecheckMissingAccountingVersion = 2
        $row.fileTransferFallbackFrontierRecentProgressGraceRecheckMissingAccountingAvailability = 'valid'
        if ($Kind -eq 'fatal') { return $row }
        $state = if ($Kind -in @('v1','v2','unsupported','missing-version','nonnumeric-version')) { 'valid' } elseif ($Kind -eq 'unknown') { 'unknown' } else { $Kind }
        $row | Add-Member -NotePropertyName ($f+'AccountingAvailability') -NotePropertyValue $state -Force
        $row.($f+'AccountingVersion') = if ($Kind -eq 'v1') { 1 } elseif ($Kind -eq 'unsupported') { 3 } else { 2 }
        foreach ($name in @('Raw','Distinct','Recovered')) { $row.($f+$name+'Count') = if ($Kind -in @('v1','v2')) { 1 } else { 999 } }
        $row.($f+'Verdict') = 'pass'
        if ($Kind -eq 'missing-property') { $row.PSObject.Properties.Remove($f+'AccountingAvailability') }
        if ($Kind -eq 'missing-version') { $row.PSObject.Properties.Remove($f+'AccountingVersion') }
        if ($Kind -eq 'nonnumeric-version') { $row.($f+'AccountingVersion') = 'NaN' }
        return $row
    }
    $cases = @(
        @{ Name='v1'; Kinds=@('v1'); State='complete'; Version=1; Counts='1/0/0' },
        @{ Name='v2'; Kinds=@('v2','v2'); State='complete'; Version=2; Counts='2/0/0' },
        @{ Name='partial'; Kinds=@('v2','unavailable'); State='partial'; Version=0; Counts='1/1/0' },
        @{ Name='all-unavailable'; Kinds=@('unavailable'); State='unavailable'; Version=0; Counts='0/1/0' },
        @{ Name='invalid'; Kinds=@('v2','invalid'); State='invalid'; Version=0; Counts='1/0/1' },
        @{ Name='all-invalid'; Kinds=@('invalid'); State='invalid'; Version=0; Counts='0/0/1' },
        @{ Name='mixed'; Kinds=@('v1','v2'); State='invalid'; Version=0; Counts='2/0/0' },
        @{ Name='mixed-partial'; Kinds=@('v1','v2','unavailable'); State='invalid'; Version=0; Counts='2/1/0' },
        @{ Name='unsupported'; Kinds=@('unsupported'); State='invalid'; Version=0; Counts='0/0/1' },
        @{ Name='missing-version'; Kinds=@('missing-version'); State='invalid'; Version=0; Counts='0/0/1' },
        @{ Name='nonnumeric-version'; Kinds=@('nonnumeric-version'); State='invalid'; Version=0; Counts='0/0/1' },
        @{ Name='unknown'; Kinds=@('unknown'); State='invalid'; Version=0; Counts='0/0/1' },
        @{ Name='old-dto'; Kinds=@('missing-property'); State='unavailable'; Version=0; Counts='0/1/0' },
        @{ Name='missing-scenario'; Kinds=@('v2'); State='partial'; Version=0; Counts='1/0/0' },
        @{ Name='fatal'; Kinds=@('fatal'); State='unavailable'; Version=0; Counts='0/1/0' },
        @{ Name='correctness'; Kinds=@('v2'); State='complete'; Version=2; Counts='1/0/0' },
        @{ Name='performance'; Kinds=@('v2'); State='complete'; Version=2; Counts='1/0/0' },
        @{ Name='wide-unavailable'; Kinds=@('unavailable'); State='unavailable'; Version=0; Counts='0/1/0' }
    )
    foreach ($phase in @('phase4','phase5')) { foreach ($case in $cases) {
        $script:RunResults.Clear()
        $i = 0
        foreach ($kind in $case.Kinds) {
            $row = New-ManualRow ($case.Name+'-'+(++$i)) $kind
            if ($case.Name -eq 'correctness') { Add-RouteAcceptanceFailure $row 'independent integrity failure' }
            if ($case.Name -eq 'performance') { Add-RouteAcceptanceFailure $row 'goodput regression exceeded' }
            if ($case.Name -eq 'wide-unavailable') { foreach ($n in 1..140) { Add-RouteAcceptanceFailure $row "independent failure $n" } }
            $script:RunResults.Add($row)
        }
        $expected = if ($case.Name -eq 'missing-scenario') { $script:RunResults.Count+1 } else { $script:RunResults.Count }
        $dir = Join-Path $OutputRoot ($phase+'-'+$case.Name)
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        try { $verdict = Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $dir -BaselinePath 'unit-test-no-baseline' -AcceptancePhase $phase -ExpectedRunCount $expected }
        catch { throw "$phase/$($case.Name): $($_.Exception.Message); $($_.ScriptStackTrace)" }
        $json = Get-Content -LiteralPath (Join-Path $dir 'route-acceptance-summary.json') -Raw | ConvertFrom-Json
        $text = Read-RouteAcceptanceKeyValueArtifact (Join-Path $dir 'route-acceptance-summary.txt')
        Check ($json.PSObject.Properties.Name -contains ($f+'AccountingAvailability')) 'summary missing explicit accounting availability'
        $counts = $case.Counts -split '/'
        $scope = if ($case.State -eq 'complete') { 'complete' } elseif ([int]$counts[0] -gt 0) { 'partial' } else { 'none' }
        $values = @{
            accounting_availability=$case.State; accounting_version=$case.Version; accounting_counter_scope=$scope
            accounting_valid_run_count=[int]$counts[0]; accounting_unavailable_run_count=[int]$counts[1]; accounting_invalid_run_count=[int]$counts[2]
            raw_count=[int]$counts[0]; distinct_count=[int]$counts[0]; recovered_count=[int]$counts[0]; superseded_count=0; unresolved_count=0
        }
        foreach ($key in $values.Keys) {
            $suffix = (($key -split '_' | ForEach-Object { $_.Substring(0,1).ToUpperInvariant()+$_.Substring(1) }) -join '')
            Check ([string]$json.($f+$suffix) -ceq [string]$values[$key] -and [string]$text[$p+$key] -ceq [string]$values[$key]) "$phase/$($case.Name) lost $key"
        }
        foreach ($run in $json.runs) {
            $state = $run.($f+'AccountingAvailability')
            Check ([string]$text[$run.name+'.'+$p+'accounting_availability'] -ceq $state) 'per-run text/JSON state mismatch'
            if ($state -ne 'valid') {
                Check-Row $run $state 0
                Check (@($json.correctnessFailures | Where-Object { $_ -like '*stall-grace accounting*' -and $_.Contains($run.name) }).Count -gt 0) 'unavailable/invalid identity lost'
            }
        }
        $shouldFail = $case.State -ne 'complete' -or $case.Name -eq 'correctness' -or $case.Name -eq 'wide-unavailable' -or ($phase -eq 'phase4' -and $case.Name -eq 'performance')
        Check ($verdict -ceq $(if ($shouldFail) { 'FAIL' } else { 'PASS' })) "$phase/$($case.Name) changed acceptance"
        Check ($json.performanceVerdict -ceq $(if ($case.Name -eq 'performance') { 'FAIL' } else { 'PASS' })) 'availability reclassified as performance'
        $mixed = @($json.failures | Where-Object { $_ -like '*mixed or unsupported policy versions*' }).Count
        Check ($mixed -eq $(if ($case.Name -in @('mixed','mixed-partial')) { 1 } else { 0 })) 'unavailable row treated as a policy version'
        if ($case.Name -eq 'correctness') { Check (@($json.correctnessFailures | Where-Object { $_ -like '*independent integrity failure*' }).Count -eq 1) 'independent failure erased' }
    } }
    # The existing full writer fails on unrelated Measure-Object sums for zero
    # rows. Exercise only the real accounting block for that input; actual fatal
    # result rendering is covered above, without changing the wider writer.
    $summary = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Write-Phase4RouteAcceptanceSummaryFiles' }, $true)
    $body = $summary.Extent.Text
    $start = $body.IndexOf('$stallValidRuns =', [StringComparison]::Ordinal)
    $end = $body.IndexOf("if (`$AcceptancePhase -eq 'phase5' -and", $start, [StringComparison]::Ordinal)
    Check ($start -ge 0 -and $end -gt $start) 'accounting block not found'
    $runDtos = @(); $ExpectedRunCount = 1; $failureLines = @()
    Invoke-Expression $body.Substring($start, $end-$start)
    Check ($stallAccountingAvailability -ceq 'unavailable' -and $stallAccountingCounterScope -ceq 'none' -and
        $fileTransferFallbackFrontierStallGraceRecheckMissingAccountingVersion -eq 0 -and
        @($failureLines | Where-Object { $_ -like 'incomplete stall-grace accounting aggregation:*' }).Count -eq 1) 'empty accounting became complete'
}
elseif ($Lane -eq 'Overrides') {
    $fake = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Write-RouteAcceptanceFakePhase4Run' }, $true)
    $blocks = @($fake.Body.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and
        ($_.Extent.Text.StartsWith('if ($terminalBeforeFallbackSwitchOff -and') -or $_.Extent.Text.Contains("-Suffix 'COMBINED_ROUTE_ANALYSIS_CONTAMINATED'"))
    })
    Check ($blocks.Count -eq 2) 'expected two fake header overrides'
    function Test-RouteAcceptanceScenarioEnvEnabled { param($ScenarioName, $Suffix) return $true }
    $terminalBeforeFallbackSwitchOff = $true
    foreach ($block in $blocks) { foreach ($hasOriginal in @($true,$false)) {
        $combined = $block.Extent.Text.Contains('COMBINED_ROUTE_ANALYSIS_CONTAMINATED')
        $Scenario = [pscustomobject]@{ Name = $(if ($combined) { 'second-transfer-after-reactivation' } else { 'regular-v4-live-activation-off-on-off-256mb' }) }
        $scenarioName = $Scenario.Name
        $row = New-Result ($scenarioName+'-'+$hasOriginal)
        $ArtifactDir = $row.artifactDir
        $operatorPath = Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt'
        if ($hasOriginal) { [IO.File]::WriteAllLines($operatorPath, [string[]]($header+$v2)) }
        Invoke-Expression $block.Extent.Text
        $lines = @(Get-Content -LiteralPath $operatorPath)
        $family = @($lines | Where-Object { $_.StartsWith($p, [StringComparison]::Ordinal) })
        Check (($family -join '|') -ceq $(if ($hasOriginal) { $v2 -join '|' } else { '' })) 'fake override discarded or invented accounting'
        Check ($lines -contains $(if ($combined) { 'verdict=FAIL' } else { 'verdict=PASS' })) 'fake intended verdict changed'
        Assert-Phase4OperatorVerdict -Result $row -RequireWarningFree $false
        if ($hasOriginal) { Check-Row $row valid 2 '3/3/1/1/1' } else { Check-Row $row unavailable 0 }
        if ($combined) { Check (@($row.failures | Where-Object { $_ -like '*operator hard failures observed: 3*' }).Count -eq 1) 'fake correctness failure erased' }
    } }
}
Write-Output "accounting-availability-$Lane-ok"
