param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Primary orchestration did not parse' }
$fn=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Write-FileTransferCompactRetainedAnalysisArtifacts'},$true))[0]
$seams=@($fn.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Clauses[0].Item1.Extent.Text -ceq '$localFailureWitnessEnabled' -and
    @($n.FindAll({param($c) $c -is [Management.Automation.Language.CommandAst] -and
        $c.GetCommandName() -ceq 'Resolve-FileTransferPrimaryPublicationWitness'},$true)).Count -eq 1},$true))
if($seams.Count -ne 1) { throw 'Actual first-failure custody seam changed' }
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'An extra evaluation is forbidden' }
function Resolve-FileTransferPrimaryFailureWitness { param($Index,$Failure) return [pscustomobject]@{Marker='witness'} }
function New-FileTransferPrimaryPublicationCompletion { param($Custody,$Evaluations,$Context,$Prepared) return $null }
function Resolve-FileTransferPrimaryPublicationWitness {
    param($Context,$Index,$Failure,$Witness)
    $script:preparations++
    foreach($name in @('localPrimaryCustody','localCompletedEvaluations')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) { throw 'Private capability leaked to resolver' }
    }
    if($script:mode -ceq 'resolver-throws') { throw 'injected resolver failure' }
    return [pscustomobject]@{Marker='prepared diagnostic only'}
}
$rows=[Collections.Generic.List[object]]::new()
foreach($script:mode in @('valid','pre-omitted','pre-throws','resolver-throws','post-omitted','post-throws')) {
    $script:ownershipChecks=0; $script:preparations=0
    $localFailureWitnessEnabled=$true; $preparedLocalFailureWitness=$null
    $Index=[pscustomobject]@{}; $firstCompactProofChainFailure=[pscustomobject]@{}
    $private:localPrimaryInputs=[pscustomobject]@{Associated=$true;FullLines=[string[]]@('full');CompactLines=[string[]]@('compact');FullView='full';CompactView='compact'}
    $private:localCompletedEvaluations=[pscustomobject]@{Marker='test double only'}
    $private:localPublicationWitness=$null
    $private:localPrimaryCustody=[pscustomobject]@{}
    $private:localPrimaryCustody | Add-Member ScriptMethod Owns {
        param($Pair,$Context,$FullLines,$CompactLines,$FullView,$CompactView)
        $script:ownershipChecks++
        if(($script:mode -ceq 'pre-throws' -and $script:ownershipChecks -eq 1) -or
            ($script:mode -ceq 'post-throws' -and $script:ownershipChecks -eq 2)) { throw 'injected ownership failure' }
        if(($script:mode -ceq 'pre-omitted' -and $script:ownershipChecks -eq 1) -or
            ($script:mode -ceq 'post-omitted' -and $script:ownershipChecks -eq 2)) { return $false }
        return $true
    }
    Invoke-Expression $seams[0].Extent.Text
    $expected=$script:mode -ceq 'valid'
    if(($null -ne $private:localPublicationWitness) -ne $expected) { throw "Prepared candidate survived rejected custody: $script:mode" }
    $expectedPreparations=if($script:mode -like 'pre-*') { 0 } else { 1 }
    $expectedChecks=if($script:mode -like 'pre-*' -or $script:mode -ceq 'resolver-throws') { 1 } else { 2 }
    if($script:preparations -ne $expectedPreparations -or $script:ownershipChecks -ne $expectedChecks) { throw 'Actual seam call count changed' }
    $rows.Add(@{Mode=$script:mode;Prepared=$expected;OwnershipChecks=$script:ownershipChecks;Preparations=$script:preparations})
}
'primary-custody-seam='+(@{Cases=$rows.ToArray();AuthorityEvaluations=0;PublicationCompletionIssued=$false} | ConvertTo-Json -Depth 4 -Compress)
