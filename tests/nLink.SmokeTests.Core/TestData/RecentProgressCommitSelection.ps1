param([string]$RepoRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Check([bool]$Condition,[string]$Reason) { if (-not $Condition) { throw $Reason } }
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 16 -Compress) }
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/FileTransferRecentProgressSupersession.ps1'),[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'Consumer parse failure'
foreach ($name in @('F','N')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    Check ($definition.Count -eq 1) "Missing production function:$name"
    Invoke-Expression $definition[0].Extent.Text
}
$assignments=@{}
foreach ($name in @('boundaries','boundary','typed')) {
    $assignment=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and
        $n.Left.Extent.Text -ceq ('$'+$name)},$true))
    Check ($assignment.Count -eq 1) "Missing production assignment:$name"
    $assignments[$name]=$assignment[0]
}
# Execute the actual selection and uniqueness guard, without invoking or
# replacing any authority evaluation. These are isolated predicate inputs,
# not producer evidence and not positive supersession or delivery proof.
$lookup=$ast.Extent.Text.Substring($assignments.boundaries.Extent.StartOffset,
    $assignments.boundary.Extent.EndOffset-$assignments.boundaries.Extent.StartOffset)
$typedLookup=$assignments.typed.Extent.Text
$Episode=[pscustomobject]@{Writer='writer'}
$repair=[pscustomobject]@{Boundary=[pscustomobject]@{coordinatorInstanceId='coordinator';sessionId='session';transferId='transfer'}}
$commit=[pscustomobject]@{Fields=@{transition_generation='3'}}
$id='fte1-'+('a'*24)
$target=[pscustomobject]@{family='route_transition';operationRole='terminal';authorityId=$id;operationGeneration=3;
    beforeState='None';afterState='Committed';stateApplied=$true;receiptValidated=$true;receiptStatus='Committed';
    routeTransition=[pscustomobject]@{targetRoute='file_tuna_v4'}}
$old=Copy-Value $target
$old.authorityId='fte1-'+('b'*24);$old.operationGeneration=2
$old.beforeState='Committed';$old.afterState='None';$old.receiptStatus='Applied';$old.routeTransition.targetRoute='post_tuna_fallback_v6'
$current=[pscustomobject]@{writer='writer';coordinatorInstanceId='coordinator';sessionId='session';transferId='transfer';
    direction='inbound';decisionKind='CommitFileTransferRouteTransition';decisionStatus='Committed';operations=@($old,$target)}
$retirement=Copy-Value $current
$retirement.operations=@((Copy-Value $target))
$retirement.operations[0].beforeState='Committed';$retirement.operations[0].afterState='None'
$retirement.operations[0].receiptStatus='Applied'
function Verify([string]$Case,[object[]]$InputBoundaries,[bool]$Expected) {
    $SnapshotEvidence=[pscustomobject]@{Boundaries=$InputBoundaries}
    $accepted=$false;$reason=''
    try { Invoke-Expression $lookup; Invoke-Expression $typedLookup; $accepted=$true }
    catch { $reason=$_.Exception.Message }
    Check ($accepted -eq $Expected) "$Case expected $Expected, got $accepted ($reason)"
    if ($accepted) { Check ($typed.targetRoute -ceq 'file_tuna_v4') "$Case selected another terminal payload" }
    else { Check ($reason -ceq 'recent_supersession_commit_unbound') "$Case failed outside the selection guard:$reason" }
    Write-Output "pass $Case"
}
Verify 'commit-with-later-retirement' @($current,$retirement) $true
Verify 'reversed-boundaries' @($retirement,$current) $true
$reordered=Copy-Value $current;$reordered.operations=@($reordered.operations[1],$reordered.operations[0])
Verify 'reordered-operations' @($reordered,$retirement) $true
Verify 'retirement-only' @($retirement) $false
Verify 'duplicate-commit-boundary' @($current,(Copy-Value $current)) $false
$duplicate=Copy-Value $current;$duplicate.operations+=Copy-Value $target
Verify 'duplicate-commit-operation' @($duplicate) $false
foreach ($case in @('authority','generation','before-state','after-state','state-not-applied','receipt-invalid',
        'receipt-rejected','decision-rejected','foreign-coordinator','foreign-session','foreign-transfer','foreign-writer','foreign-direction')) {
    $b=Copy-Value $current;$o=$b.operations[1]
    switch ($case) {
        'authority' {$o.authorityId='foreign'}
        'generation' {$o.operationGeneration=4}
        'before-state' {$o.beforeState='Committed'}
        'after-state' {$o.afterState='None'}
        'state-not-applied' {$o.stateApplied=$false}
        'receipt-invalid' {$o.receiptValidated=$false}
        'receipt-rejected' {$o.receiptStatus='Rejected'}
        'decision-rejected' {$b.decisionStatus='Rejected'}
        'foreign-coordinator' {$b.coordinatorInstanceId='foreign'}
        'foreign-session' {$b.sessionId='foreign'}
        'foreign-transfer' {$b.transferId='foreign'}
        'foreign-writer' {$b.writer='foreign'}
        'foreign-direction' {$b.direction='outbound'}
    }
    Verify $case @($b,$retirement) $false
}
Write-Output 'recent-progress-commit-selection-ok:19'
