param([string]$RepoRoot,[string]$OutputRoot,[ValidateSet('stall','recent_progress')][string]$Kind='stall')
$propertyPrefix=if($Kind -ceq 'stall'){'fileTransferFallbackFrontierStallGraceRecheckMissing'}else{'fileTransferFallbackFrontierRecentProgressGraceRecheckMissing'}
$reportPrefix="filetransfer_fallback_frontier_${Kind}_grace_recheck_missing_"
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/StallGraceAccounting.ps1')
$tokens=$null;$errors=$null
$path=Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'acceptance script parse errors' }
# Load actual definitions and their scalar/list initial state, never dispatch
# the acceptance runner, GUI, preflight, build or network actions.
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) { Invoke-Expression $statement.Extent.Text }
}
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) { break }
    if ($statement -is [Management.Automation.Language.AssignmentStatementAst]) { Invoke-Expression $statement.Extent.Text }
}
$script:RouteAcceptanceExecutionMode='fake'
$GoodputRegressionTolerancePercent=10D
function Check([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function New-Result([string]$Name,[int]$Version) {
    $dir=Join-Path $OutputRoot $Name
    [void][IO.Directory]::CreateDirectory($dir)
    $r=New-RouteAcceptanceRunResult -Name $Name -ArtifactDir $dir -ExpectedRoute 'file_tuna_v4' -ExpectedProtocol 4
    foreach($family in @('Stall','RecentProgress')) {
        $r.('fileTransferFallbackFrontier'+$family+'GraceRecheckMissingAccountingVersion')=2
        $r.('fileTransferFallbackFrontier'+$family+'GraceRecheckMissingAccountingAvailability')='valid'
    }
    $r.($propertyPrefix+'AccountingVersion')=$Version
    $r.($propertyPrefix+'AccountingAvailability')='valid'
    $r.($propertyPrefix+'RawCount')=1
    $r.($propertyPrefix+'DistinctCount')=1
    $r.($propertyPrefix+'RecoveredCount')=0
    $r.($propertyPrefix+'SupersededCount')=if ($Version -eq 2) {1} else {0}
    $r.($propertyPrefix+'UnresolvedCount')=if ($Version -eq 1) {1} else {0}
    $r.($propertyPrefix+'Verdict')=if ($Version -eq 2) {'pass'} else {'fail'}
    return $r
}
foreach ($case in @('import-v2','import-legacy','import-missing')) {
    $r=New-Result $case 1
    $p=$reportPrefix
    $reportLines=@('verdict=pass','hard_failure_count=0','warning_count=0',
        ($p+'raw_count=1'),($p+'distinct_count=1'),($p+'recovered_count=0'))
    if ($case -ceq 'import-legacy') { $reportLines+=@(($p+'unresolved_count=1'),($p+'verdict=fail')) }
    else {
        $reportLines+=@(($p+'accounting_version=2'),($p+'unresolved_count=0'),($p+'verdict=pass'))
        if ($case -ceq 'import-v2') { $reportLines+=($p+'superseded_count=1') }
    }
    [IO.File]::WriteAllLines((Join-Path $r.artifactDir 'filetransfer-operator-verdict.txt'),$reportLines)
    Assert-Phase4OperatorVerdict -Result $r -RequireWarningFree $false
    if ($case -ceq 'import-v2') {
        Check ($r.($propertyPrefix+'AccountingVersion') -eq 2 -and
            $r.($propertyPrefix+'SupersededCount') -eq 1) 'v2 import lost fields'
    } elseif ($case -ceq 'import-legacy') {
        Check ($r.($propertyPrefix+'AccountingVersion') -eq 1 -and
            $r.($propertyPrefix+'SupersededCount') -eq 0) 'legacy import upgraded'
    } else {
        Check ($r.($propertyPrefix+'AccountingVersion') -eq 0 -and
            @($r.failures | Where-Object { $_ -like '*counter_missing:Superseded*' }).Count -eq 1) 'malformed import accepted'
    }
}
foreach($case in @('missing-report','empty-accounting','bad-version','bad-equation','negative','legacy-superseded')) {
    $r=New-Result $case 2
    $report=[ordered]@{verdict='pass';hard_failure_count='0';warning_count='0'}
    if($case -cnotin @('missing-report','empty-accounting')) {
        foreach($pair in @('accounting_version=2','raw_count=1','distinct_count=1','recovered_count=0','superseded_count=1','unresolved_count=0','verdict=pass')) {
            $parts=$pair -split '=',2;$report[$reportPrefix+$parts[0]]=$parts[1]
        }
        switch($case) {
            'bad-version' {$report[$reportPrefix+'accounting_version']='9'}
            'bad-equation' {$report[$reportPrefix+'distinct_count']='2'}
            'negative' {$report[$reportPrefix+'raw_count']='-1'}
            'legacy-superseded' {$report.Remove($reportPrefix+'accounting_version')}
        }
    }
    if($case -cne 'missing-report') {
        [IO.File]::WriteAllLines((Join-Path $r.artifactDir 'filetransfer-operator-verdict.txt'),[string[]]@($report.Keys | ForEach-Object {"$_=$($report[$_])"}))
    }
    Assert-Phase4OperatorVerdict -Result $r -RequireWarningFree $false
    $wanted=if($case -cin @('missing-report','empty-accounting')){'unavailable'}else{'invalid'}
    Check ($r.($propertyPrefix+'AccountingAvailability') -ceq $wanted -and
        $r.($propertyPrefix+'AccountingVersion') -eq 0 -and $r.($propertyPrefix+'SupersededCount') -eq 0) "$case stale or malformed accounting accepted"
}
foreach ($case in @('v2','mixed','target-failure','unavailable','invalid','partial')) {
    $runRoot=Join-Path $OutputRoot $case
    [void][IO.Directory]::CreateDirectory($runRoot)
    $script:RunResults.Clear()
    $r=New-Result 'superseded' 2
    if ($case -ceq 'target-failure') { Add-RouteAcceptanceFailure $r 'independent target integrity failure' }
    $script:RunResults.Add($r)
    if ($case -ceq 'mixed') { $script:RunResults.Add((New-Result 'legacy' 1)) }
    if ($case -cin @('unavailable','invalid')) {
        $bad=New-Result 'excluded' 2
        $bad.($propertyPrefix+'AccountingAvailability')=$case
        $bad.($propertyPrefix+'SupersededCount')=999
        $script:RunResults.Add($bad)
    }
    $expected=$script:RunResults.Count+$(if($case -ceq 'partial'){1}else{0})
    $verdict=Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath 'unit-test-no-baseline' -AcceptancePhase phase5 -ExpectedRunCount $expected
    $report=Read-RouteAcceptanceKeyValueArtifact (Join-Path $runRoot 'route-acceptance-summary.txt')
    $json=Get-Content (Join-Path $runRoot 'phase4-ab-acceptance-summary.json') -Raw | ConvertFrom-Json
    $p=$reportPrefix
    $expectedVersion=if ($case -cin @('mixed','unavailable','invalid','partial')) {'0'} else {'2'}
    Check ($report[$p+'accounting_version'] -ceq $expectedVersion -and $report[$p+'superseded_count'] -ceq '1') "$case root fields"
    Check ($report['superseded.'+$p+'accounting_version'] -ceq '2' -and $report['superseded.'+$p+'superseded_count'] -ceq '1') "$case per-input fields"
    Check ($json.($propertyPrefix+'AccountingVersion') -eq [int]$expectedVersion -and
        $json.($propertyPrefix+'SupersededCount') -eq 1) "$case JSON fields"
    if ($case -cne 'v2') { Check ($verdict -ceq 'FAIL') "$case incorrectly accepted" }
    if ($case -ceq 'mixed') { Check (@($json.failures | Where-Object { $_ -like '*mixed or unsupported policy versions*' }).Count -eq 1) 'mixed versions not rejected explicitly' }
    if ($case -ceq 'target-failure') { Check (@($json.failures | Where-Object { $_ -like '*independent target integrity failure*' }).Count -eq 1) 'target failure waived' }
}
Write-Output "${Kind}-grace-accounting-reports-ok"
