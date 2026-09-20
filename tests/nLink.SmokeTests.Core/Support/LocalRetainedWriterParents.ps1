param([string]$RepoRoot,[string]$Directory,[switch]$FrontierReports)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
function Parse([string]$Path) {
    $t=$null; $e=$null; $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$t,[ref]$e)
    if($e.Count) { throw "Parse failed: $Path" }; return $ast
}
function Definition($Ast,[string]$Name) {
    $found=@($Ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($found.Count -ne 1) { throw "Function ambiguous: $Name" }; return $found[0]
}
$route=Parse (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1')
$wrapper=Parse (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess')) {
    Invoke-Expression (Definition $route $name).Extent.Text
}
foreach($name in @('Read-TunaGuiReportValue','Write-TunaGuiRetainedAnalysisTimeoutArtifacts',
    'Test-TunaGuiRetainedAnalysisCompletedProofArtifacts','Stop-TunaGuiRetainedAnalysisProcess')) {
    Invoke-Expression (Definition $wrapper $name).Extent.Text
}
foreach($pair in @(@($route,'Invoke-RouteAcceptanceRetainedAnalysis'),@($wrapper,'Invoke-TunaGuiRetainedAnalysis'))) {
    $definition=Definition $pair[0] $pair[1]; $text=$definition.Extent.Text
    $deadline=@($definition.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$analysisTimeoutSeconds'},$true))
    if($deadline.Count -ne 1 -or $deadline[0].Right.Extent.Text -cne '1800' -or $text -cnotmatch 'while \(-not \$process.WaitForExit\(5000\)\)') { throw 'Original retained watchdog changed' }
    $text=$text.Replace($deadline[0].Extent.Text,'$analysisTimeoutSeconds = 5').Replace('while (-not $process.WaitForExit(5000))','while (-not $process.WaitForExit(25))')
    $text=$text.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
    $loader="Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'"
    if($text.Split([string[]]@($loader),[StringSplitOptions]::None).Length -ne 2) { throw 'Source loader seam changed' }
    $text=$text.Replace($loader,"Join-Path `$script:moduleRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
    Invoke-Expression $text
}
$script:moduleRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'; $RouteMode='live-regular-activation-cycle'
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'No new authority evaluation allowed' }
$originalLocation=Get-Location; $rows=[Collections.Generic.List[object]]::new()
$primaryFiles=@('filetransfer-operator-verdict.txt','stability-gates-summary.txt','filetransfer-route-consistency-summary.txt',
    'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
    'filetransfer-coordinator-snapshot-parity-summary.json','filetransfer-retained-compact-summary.json','filetransfer-retained-analysis-stage-diagnostics.json')
try {
    Set-Location -LiteralPath $Directory
    foreach($owner in @('Wrapper','RouteAcceptance')) {
        $baseline=$null
        foreach($mode in @('baseline','owned','forged','mutated','nonzero','proof-fail','timeout')) {
            $completion=$null
            $null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $Directory ($owner+'-'+$mode)) -AcceptedCompletion ([ref]$completion)
            if($null -eq $completion) { throw 'Actual GUI completion missing' }
            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$completion.AcceptanceRunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$completion.ScenarioRunId
            $output=$completion.ArtifactRoot; $log=$completion.Outputs[3].Path; $candidate=$completion
            if($mode -ceq 'forged') { $candidate=$completion | ConvertTo-Json -Depth 8 | ConvertFrom-Json }
            if($mode -ceq 'mutated') { [IO.File]::AppendAllText($log,"`nevent=test_milestone",[Text.UTF8Encoding]::new($false)) }
            $optionalMode=if($mode -ceq 'baseline') {'none'} elseif($mode -ceq 'nonzero') {'nonzero'} elseif($mode -ceq 'timeout') {'stall'} else {'completed'}
            Write-FileTransferEvidenceAtomicJson (Join-Path $output 'case.json') @{proofPass=$mode -cne 'proof-fail';optionalMode=$optionalMode;sourceRequest=$true;writerBinding=$true;repoRoot=$RepoRoot}
            Write-FileTransferEvidenceAtomicJson (Join-Path $output 'filetransfer-tuna-gui-summary.json') @{completed=$true;integrityOk=$true;inboundTerminalState='Completed';outboundTerminalState='Completed'}
            $args=@{CaptureLocalSourceOwnership=$mode -cne 'baseline';LocalWriterContext=$candidate}; $failure=''
            if($FrontierReports) {$args.CaptureFrontierReports=$true}
            try {
                if($owner -ceq 'Wrapper') { Invoke-TunaGuiRetainedAnalysis -RepoRoot $Directory -AnalysisDir $output -LogPath $log -LiveRouteProofMode RegularActivationCycle @args }
                else { Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $output -LiveRouteProofMode RegularActivationCycle @args }
            } catch { $failure=$_.Exception.Message }
            $receipt=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-retained-analysis-attempt-receipt.json')) | ConvertFrom-Json
            $source=[IO.File]::ReadAllText((Join-Path $output 'source-request-control.json')) | ConvertFrom-Json
            $expected=if($mode -ceq 'nonzero') {'failed'} elseif($mode -ceq 'proof-fail') {'proof_incomplete'} elseif($mode -ceq 'timeout') {'timed_out'} else {'completed'}
            $context=$mode -cin @('owned','nonzero','proof-fail','timeout')
            if($receipt.status -cne $expected -or $receipt.owner -cne $owner -or -not $receipt.analyzerInputUnchanged -or
                (($expected -ceq 'completed') -ne [string]::IsNullOrEmpty($failure)) -or $source.attached -ne ($mode -cne 'baseline') -or
                $source.writerContext -ne $context -or $source.writerVerified -ne $context -or
                ($context -and ($source.guiRequest -cne $completion.RequestId.ToString('D') -or $source.requestId.Replace('-','') -cne $receipt.attemptId -or
                    $source.owner -cne $owner -or $source.writerLines -ne 3))) { throw "Actual retained parent drift: $owner/$mode : $failure" }
            if($mode -ceq 'timeout' -and (-not $receipt.timedOut -or $receipt.exitCodeAvailable -or $receipt.completedProofArtifacts)) { throw 'Timeout receipt changed' }
            if($mode -cne 'timeout' -and (-not $receipt.exitCodeAvailable -or $receipt.exitCode -ne $(if($mode -ceq 'nonzero') {9} else {0}))) { throw 'Original exit result changed' }
            $hashes=@(foreach($file in $primaryFiles) { Get-FileTransferEvidenceSha256 (Join-Path $output $file) })
            if($mode -ceq 'baseline') { $baseline=$hashes }
            elseif($mode -cnotin @('proof-fail','timeout') -and ($hashes -join '|') -cne ($baseline -join '|')) { throw 'Primary report bytes changed' }
            foreach($file in @($log,$completion.Outputs[1].Path)) { $released=[IO.File]::OpenWrite($file); $released.Dispose() }
            if(Test-Path -LiteralPath (Join-Path $output 'late-optional.json')) { throw 'Timed-out worker survived' }
            $rows.Add(@{Owner=$owner;Mode=$mode;Status=$receipt.status;WriterVerifiedBeforeReports=$context;PublicationCompletion=$false})
        }
    }
} finally { Set-Location -LiteralPath $originalLocation.Path }
'retained-writer-binding='+(@{Cases=$rows.ToArray();ProductionWatchdogSeconds=1800;TestWatchdogSeconds=5;AuthorityEvaluations=0;LiveExport=$false} | ConvertTo-Json -Depth 5 -Compress)
