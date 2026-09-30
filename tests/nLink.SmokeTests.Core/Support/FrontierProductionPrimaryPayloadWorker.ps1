param([string]$Mode,[string]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$config=Get-Content -LiteralPath (Join-Path $ArtifactDir 'parent-case.json') -Raw | ConvertFrom-Json
$RepoRoot=$config.repoRoot
$script:productionPrimaryChecked=$false;$script:productionPrimaryClosed=$false;$script:productionPrimaryError=$null
$script:productionTransfer=$null;$script:productionWorkerHandles=@();$script:productionBoundObserved=$false
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$text=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParentWorker.ps1'))
$text=Replace-AttemptSeam $text ". (Join-Path `$RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')" ". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') `$RepoRoot"
$text=Replace-AttemptSeam $text ". (Join-Path `$RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')" @'
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
$definition=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.Ast.Parent.Extent.Text
$definition=$definition.Replace('$PSScriptRoot',("'"+(Join-Path $RepoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($definition,[ref]$tokens,[ref]$errors)
$gate=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Extent.Text.StartsWith('if ($null -ne $private:nativePrimaryPayloads -and $null -ne $firstCompactProofChainFailure -and')},$true))
if($errors.Count -or $gate.Count -ne 1 -or $gate[0].Extent.Text -notmatch 'TryBindTransferPair') {throw 'Production primary diagnostic gate changed'}
$before="try { . (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionPrimaryPayloadProbe.ps1') -Stage Before } catch { `$script:productionPrimaryError=`$_; throw }"
$after="try { . (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionPrimaryPayloadProbe.ps1') -Stage After } catch { `$script:productionPrimaryError=`$_; throw }"
$definition=Replace-AttemptSeam $definition $gate[0].Extent.Text ($before+"`n"+$gate[0].Extent.Text+"`n"+$after)
$resolve='$private:nativeWitness = Resolve-FileTransferNativeFrontierWitness'
$bound="try { . (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionPrimaryPayloadProbe.ps1') -Stage Bound } catch { `$script:productionPrimaryError=`$_; throw }"
$definition=Replace-AttemptSeam $definition $resolve ($bound+"`n"+$resolve)
$close='try { $private:nativePrimaryCustody.Dispose() } catch { }'
$definition=Replace-AttemptSeam $definition $close ($close+"`nif (`$null -eq `$script:productionPrimaryError) { . (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionPrimaryPayloadProbe.ps1') -Stage Closed }")
Invoke-Expression $definition
'@
& ([scriptblock]::Create($text)) -Mode $Mode -LogPath $LogPath -ArtifactDir $ArtifactDir -TailMinutes $TailMinutes -LiveRouteProofMode $LiveRouteProofMode
if($null -ne $script:productionPrimaryError) {throw $script:productionPrimaryError}
if(-not $script:productionPrimaryChecked -or -not $script:productionPrimaryClosed) {throw 'Production primary diagnostic calls or cleanup omitted'}
Write-Host 'attempt-payload-primary-checked=production-primary'
