param([string]$Primary,[string]$GuiReader,[string]$SourceProbe)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TerminalPreVerdictInventory.ps1')
function Replace-One([string]$Text,[string]$Needle,[string]$Replacement) {
    if($Text.Split([string[]]@($Needle),[StringSplitOptions]::None).Length -ne 2) {throw 'pre_verdict_unique_seam'}
    return $Text.Replace($Needle,$Replacement)
}
$Primary=$Primary.Replace("`r`n","`n"); $GuiReader=$GuiReader.Replace("`r`n","`n")
$start='    $verdict = ''PASS'''+"`n"+'    $gateStatus = ''pass'''+"`n"+'    $acceptanceFailureClass = ''none'''
$end='    $recoveryFailureClass = if ($null -ne $firstCompactProofChainFailure) {'
if($Primary.IndexOf($start) -lt 0 -or $Primary.IndexOf($end) -le $Primary.IndexOf($start)) {throw 'pre_verdict_gate_block'}
$gateBlock=$Primary.Substring($Primary.IndexOf($start),$Primary.IndexOf($end)-$Primary.IndexOf($start))
# Static coverage only: neither this inspection nor the observer evaluates authority.
$countStart=$gateBlock.IndexOf('    if ($runtimeUnlockRetryDispatchInstallProof.')
$countEnd=$gateBlock.IndexOf('    elseif ($compactProofChainCandidates.Count -gt 0)')
if($countStart -lt 0 -or $countEnd -le $countStart) {throw 'pre_verdict_count_seam'}
$actual=@([regex]::Matches($gateBlock.Substring($countStart,$countEnd-$countStart),'\$([A-Za-z0-9_]+\.[A-Za-z0-9_]+)(?= -gt 0)') | ForEach-Object {$_.Groups[1].Value})
if(@(Compare-Object $script:TerminalIntegrityCountFactors $actual).Count -ne 0 -or $actual.Count -ne 42) {throw 'pre_verdict_predicate_inventory_changed'}
$init='$private:localPrimaryInputs = $null'
$Primary=Replace-One $Primary $init ('$private:terminalPreSource = $null'+"`n    "+$init)
$SourceProbe=$SourceProbe.Replace('-PrimaryVerdict $verdict -PrimaryGateStatus $gateStatus',"-PrimaryVerdict '' -PrimaryGateStatus ''")+' -DeferOutput'
$counts=$script:TerminalIntegrityCountFactors | ForEach-Object {"        '$_'="+'$'+$_}
$frame=@'
$private:terminalPreFrame=[ordered]@{
    GuiCompleted=$guiCompleted;GuiIntegrityOk=$guiIntegrityOk;HardFailureCount=$hardFailureCount
    ActiveFileTunaV6Count=$activeFileTunaV6Count;RouteConsistency=$routeConsistency;LiveRouteProof=$compactLiveRouteProof
    LiveRouteProofMode=$LiveRouteProofMode;PreTerminalLivenessTimeoutCount=$preTerminalLivenessTimeoutCount
    WarningCap=$compactWarningCap;WarningKinds=$warningKinds;Candidates=$compactProofChainCandidates
    CandidateArray=$compactProofChainCandidateArray;FirstFailure=$firstCompactProofChainFailure
    Mismatch=$fileTunaV4DataPathMismatchProof;Snapshot=$snapshotEvaluation;Parity=$snapshotParity
    FallbackTailAuthoritative=$snapshotFallbackTailYieldAuthoritative;ReceiveRecoverySelected=$snapshotReceiveRecoveryEvidenceSelected
    Counts=[ordered]@{
__COUNTS__
    }
}
'@
$frame=$frame.Replace('__COUNTS__',($counts -join "`n"))
$path=(Join-Path $PSScriptRoot 'TerminalPreVerdictProbe.ps1').Replace("'","''")
$call="& '$path' -Context `$private:localPrimaryInputs -Custody `$private:localPrimaryCustody -Evaluations `$private:localCompletedEvaluations -Frame `$private:terminalPreFrame -Gui `$gui -ReadStatus `$script:terminalGuiReadStatus -ArtifactDir `$ArtifactDir -EvaluationCount `$script:livePrimaryEvaluations"
$before=@'
try {
    # A selected failure may still need the original one-shot writer at the
    # later publication-witness seam. This bounded shadow must not steal it.
    if($null -ne $firstCompactProofChainFailure) {throw 'integrity_shadow_selected_failure_writer_reserved'}
    $private:terminalPreSource = __SOURCE__
    __FRAME__
    $private:terminalPreSource.PreVerdictIntegrity = __CALL__
    $private:terminalPreSource.WorkMilliseconds=$private:localPrimaryInputs.Work.ElapsedMilliseconds
} catch {
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'terminal-source-probe-error.txt'),[string]$_ + "`n" + $_.ScriptStackTrace)
} finally { $private:terminalPreFrame=$null }
'@
$before=$before.Replace('__SOURCE__',$SourceProbe).Replace('__FRAME__',$frame).Replace('__CALL__',$call)
$Primary=Replace-One $Primary $start ($before+"`n"+$start)
$cleanup='$private:localPublicationWitness = $null'+"`n"+'        $private:localCompletedEvaluations = $null'
$flush=@'
if($null -ne $private:terminalPreSource) {
    $private:terminalPreSource.PrimaryVerdict=[string](Get-Variable verdict -Scope Local -ValueOnly -ErrorAction SilentlyContinue)
    $private:terminalPreSource.PrimaryGateStatus=[string](Get-Variable gateStatus -Scope Local -ValueOnly -ErrorAction SilentlyContinue)
    try {
        [IO.File]::WriteAllText((Join-Path $ArtifactDir 'terminal-source-primary-observation.json'),
            ($private:terminalPreSource | ConvertTo-Json -Depth 12 -Compress))
    } catch {
        [IO.File]::WriteAllText((Join-Path $ArtifactDir 'terminal-source-probe-error.txt'),[string]$_)
    } finally {$private:terminalPreSource=$null}
}
'@
$Primary=Replace-One $Primary $cleanup ($flush+"`n        "+$cleanup)
if(-not $Primary.Contains($gateBlock)) {throw 'pre_verdict_original_gate_changed'}
$absent=@'
if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $null
    }
'@
$GuiReader=Replace-One $GuiReader $absent ($absent.Replace('return $null',"`$script:terminalGuiReadStatus='absent'; return `$null"))
$read='return Get-Content -LiteralPath $summaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop'
$GuiReader=Replace-One $GuiReader $read ('$private:terminalGuiValue = '+$read.Substring(7)+"`n        `$script:terminalGuiReadStatus='read'; return `$private:terminalGuiValue")
$catch="catch {`n        return `$null`n    }"
$GuiReader=Replace-One $GuiReader $catch ("catch {`n        `$script:terminalGuiReadStatus='unreadable'; return `$null`n    }")
$readerStart='$summaryPath = Join-Path $ArtifactDir ''filetransfer-tuna-gui-summary.json'''
$GuiReader=Replace-One $GuiReader $readerStart ("`$script:terminalGuiReadStatus='unknown'`n    "+$readerStart)
[pscustomobject]@{Primary=$Primary;GuiReader=$GuiReader}
