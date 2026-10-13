param([AllowNull()]$PreparedSource=$null)
$retainedOwner=if($primaryCase.StartsWith('parent-Wrapper-')) {'Wrapper'} else {'RouteAcceptance'}
$retainedCase=$primaryCase.Substring(('parent-'+$retainedOwner+'-').Length)
$retainedPrelaunch=$retainedCase.StartsWith('start-')
$retainedLegacy=$retainedCase -ceq 'preparation-budget'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
function Read-ParentAst([string]$Path) {
    $t=$null; $e=$null; $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$t,[ref]$e)
    if($e.Count) {throw ('Parent parse failed: '+$Path)}; return $ast
}
function Read-ParentDefinition($Ast,[string]$Name) {
    $found=@($Ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($found.Count -ne 1) {throw ('Ambiguous parent function: '+$Name)}; return $found[0]
}
$routeAst=Read-ParentAst (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1')
$wrapperAst=Read-ParentAst (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess')) {
    Invoke-Expression (Read-ParentDefinition $routeAst $name).Extent.Text
}
foreach($name in @('Read-TunaGuiReportValue','Write-TunaGuiRetainedAnalysisTimeoutArtifacts',
    'Test-TunaGuiRetainedAnalysisCompletedProofArtifacts','Stop-TunaGuiRetainedAnalysisProcess')) {
    Invoke-Expression (Read-ParentDefinition $wrapperAst $name).Extent.Text
}
foreach($pair in @(@($routeAst,'Invoke-RouteAcceptanceRetainedAnalysis'),@($wrapperAst,'Invoke-TunaGuiRetainedAnalysis'))) {
    $definition=Read-ParentDefinition $pair[0] $pair[1]; $text=$definition.Extent.Text
    $deadline=@($definition.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$analysisTimeoutSeconds'},$true))
    $expectedWatchdog=if($pair[1] -ceq 'Invoke-RouteAcceptanceRetainedAnalysis') {'3600'} else {'1800'}
    if($deadline.Count -ne 1 -or $deadline[0].Right.Extent.Text -cne $expectedWatchdog -or $text -cnotmatch 'while \(-not \$process.WaitForExit\(5000\)\)' -or
        $text -cmatch 'TryAdoptWhenOfferedAsync') {throw 'Original watchdog changed or short launch-time offer wait introduced'}
    if($retainedCase -ceq 'timeout') {$text=$text.Replace($deadline[0].Extent.Text,'$analysisTimeoutSeconds = 5')}
    $text=$text.Replace('$startInfo.FileName = ''powershell''','$startInfo.FileName = (Get-Process -Id $PID).Path')
    $text=$text.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
    $loader="Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'"
    if($text.Split([string[]]@($loader),[StringSplitOptions]::None).Length -ne 2) {throw 'Original source loader seam changed'}
    $text=$text.Replace($loader,"Join-Path `$script:retainedRepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
    # Inject only the failing resource before the unchanged original launch.
    $launchSeam='$process.StartInfo = $startInfo'
    if($text.Split([string[]]@($launchSeam),[StringSplitOptions]::None).Length -ne 2) {throw 'Original launch seam changed'}
    $text=$text.Replace($launchSeam,$launchSeam+"`nInitialize-RetainedParentFailure -Process `$process -Preparation `$private:frontierPreparation")
    Invoke-Expression $text
}
$script:retainedRepoRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'; $RouteMode='live-regular-activation-cycle'
$script:retainedSource=$completion.Outputs[3].Path; $script:retainedHashes=0; $script:retainedCounts=0; $script:retainedWrites=0
$script:retainedHash=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock
$script:retainedCount=(Get-Command Get-FileTransferEvidenceLineCount).ScriptBlock
$script:retainedReceipt=(Get-Command Write-FileTransferRetainedAnalysisAttemptReceipt).ScriptBlock
$script:retainedAtomic=(Get-Command Write-FileTransferEvidenceAtomicJson).ScriptBlock
$script:retainedRoot=$null; $script:retainedTerminal=$null; $script:retainedReturned=$null
$script:retainedPreparation=$null; $script:retainedLaunchRoot=$null; $script:retainedPins=@(); $script:retainedForeign=$null
$script:retainedLaunchEntries=0; $script:retainedCaptureCollision=''
function Initialize-RetainedParentFailure {
    param($Process,$Preparation)
    $script:retainedLaunchEntries++
    $script:retainedPreparation=$Preparation
    if($retainedLegacy) {if($null -ne $Preparation) {throw 'Expired preparation unexpectedly succeeded'}; return}
    if($null -eq $Preparation) {throw 'Native parent preparation unavailable'}
    if($null -ne $PreparedSource -and -not [object]::ReferenceEquals($Preparation,$PreparedSource)) {throw 'Prepared wrapper source was replaced'}
    if($null -ne $PreparedSource -and ($Preparation.TryClaimForAnalysis('Wrapper',$completion.AcceptanceRunId,
        $completion.ScenarioRunId,$script:retainedSource,$completion) -or -not $Preparation.IsCurrent)) {throw 'Original prepared claim repeated or retired before launch'}
    $script:retainedLaunchRoot=$Preparation.Request
    $pins=$Preparation.Inputs.GetType().GetField('pins',$parentFlags).GetValue($Preparation.Inputs)
    $script:retainedPins=@($pins.GetType().GetField('Handles',$parentFlags).GetValue($pins))
    if($retainedCase.StartsWith('start-collision-')) {
        $script:retainedForeign=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
            $Preparation.Request.Name+'.frontier-start-v1.'+$retainedCase.Substring('start-collision-'.Length))
    }
    if($retainedCase -ceq 'start-failure') {$Process.StartInfo.FileName=Join-Path $Directory 'missing-retained-worker.exe'}
    if($retainedCase -ceq 'start-stale-inputs') {$Preparation.Inputs.Dispose()}
    if($retainedCase -ceq 'capture-failure') {
        $script:retainedCaptureCollision=Join-Path $Directory ('frontier-primary-output-'+$Preparation.Request.RequestId.ToString('N')+'-stdout.log')
        [IO.File]::WriteAllText($script:retainedCaptureCollision,'preserve-capture-collision')
    }
    if($retainedCase -ceq 'receiver-failure') {
        $script:retainedForeign=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($Preparation.Request.Name+'.frontier-primary-v1.files-v1.diagnostic-v1',160)
    }
}
function Get-FileTransferEvidenceSha256 {
    param([string]$Path,$ReadLease=$null,$OwnedSource=$null)
    if($Path -ceq $script:retainedSource) {
        $script:retainedHashes++
        if(-not $retainedLegacy -and $null -eq $OwnedSource) {throw 'Parent reopened native source for hash'}
    }
    & $script:retainedHash -Path $Path -ReadLease $ReadLease -OwnedSource $OwnedSource
}
function Get-FileTransferEvidenceLineCount {
    param([string]$Path,$ReadLease=$null,$OwnedSource=$null)
    if($Path -ceq $script:retainedSource) {
        $script:retainedCounts++
        if(-not $retainedLegacy -and $null -eq $OwnedSource) {throw 'Parent reopened native source for count'}
    }
    & $script:retainedCount -Path $Path -ReadLease $ReadLease -OwnedSource $OwnedSource
}
function Write-FileTransferEvidenceAtomicJson {
    param([string]$Path,$Value)
    if([IO.Path]::GetFileName($Path) -ceq 'filetransfer-retained-analysis-attempt-receipt.json') {
        $script:retainedWrites++
        foreach($name in @('PreparedFrontierSource','FrontierParentCustody','frontierPreparation','frontierTerminal','frontierSource','localTerminalReceipt')) {
            if($null -ne (Get-Variable -Name $name -ErrorAction SilentlyContinue)) {throw ('Private parent custody leaked: '+$name)}
        }
        if($retainedCase -ceq 'receipt-write-failure') {throw 'injected original receipt write failure'}
    }
    & $script:retainedAtomic -Path $Path -Value $Value
}
function Write-FileTransferRetainedAnalysisAttemptReceipt {
    param($ArtifactDir,$Owner,$AttemptId,$AcceptanceRunId,$ScenarioRunId,$LogPath,$InputSha256Before,$InputLineCountBefore,
        $LiveRouteProofMode,$CompactRequested,$ProcessId,$StartedUtc,$EndedUtc,$TimedOut,$ExitCodeAvailable,$ExitCode,
        $CompletedProofArtifacts,$LocalPublicationCustody=$null,$FrontierTerminalCustody=$null,$FrontierSourceRequest=$null)
    $script:retainedRoot=$FrontierSourceRequest; $script:retainedTerminal=$FrontierTerminalCustody
    $script:retainedReturned=& $script:retainedReceipt @PSBoundParameters
    return $script:retainedReturned
}
function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Parent introduced authority evaluation'}
$previousLocation=Get-Location; $retainedFailure=''; $retainedException=$null; $retainedOutput=@(); $retainedWatch=[Diagnostics.Stopwatch]::StartNew()
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $Directory 'tools'))
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParentWorker.ps1') -Destination (Join-Path $Directory 'tools/FileTransfer-Ops.ps1')
    Write-FileTransferEvidenceAtomicJson (Join-Path $Directory 'parent-case.json') @{repoRoot=$RepoRoot;scenario=$retainedCase}
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$completion.AcceptanceRunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$completion.ScenarioRunId
    $diagnosticPath=Join-Path $Directory 'filetransfer-frontier-terminal-diagnostic.json'
    if($retainedCase -ceq 'persistence-collision') {Set-Content -LiteralPath $diagnosticPath -Value 'preserve-existing' -Encoding UTF8}
    Set-Location -LiteralPath $Directory
    $parentArgs=@{CaptureLocalSourceOwnership=$true;CaptureFrontierReports=$true;LocalWriterContext=$completion}
    if($null -ne $PreparedSource) {
        $parentArgs.PreparedFrontierSource=$PreparedSource
        foreach($rejected in @('copy','wrong-type','wrong-completion','wrong-run','wrong-path','no-opt-in','two-sources')) {
            $unowned=$parentArgs.Clone(); $testPath=$script:retainedSource
            switch($rejected) {
                'copy' {$unowned.PreparedFrontierSource=$parentClone.Invoke($PreparedSource,$null)}
                'wrong-type' {$unowned.PreparedFrontierSource=[pscustomobject]@{Request=$PreparedSource.Request}}
                'wrong-completion' {$unowned.LocalWriterContext=$parentClone.Invoke($completion,$null)}
                'wrong-run' {$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='foreign-run'}
                'wrong-path' {$testPath=Join-Path $Directory 'foreign.log'}
                'no-opt-in' {$unowned.CaptureFrontierReports=$false}
                'two-sources' {$unowned.FrontierParentCustody=[pscustomobject]@{}}
            }
            $guardFailure=''
            try {$null=Invoke-TunaGuiRetainedAnalysis -RepoRoot $Directory -AnalysisDir $Directory -LogPath $testPath -LiveRouteProofMode RegularActivationCycle @unowned}
            catch {$guardFailure=$_.Exception.Message}
            finally {$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$completion.AcceptanceRunId}
            if($guardFailure -cne 'Prepared frontier source is not available for this retained analysis.' -or
                $script:retainedLaunchEntries -or $script:retainedHashes -or $script:retainedCounts -or $script:retainedWrites -or
                -not $PreparedSource.IsCurrent) {throw ('Prepared-source rejection changed ownership or launched analysis: '+$rejected+' / '+$guardFailure)}
        }
        foreach($bad in @(@('RouteAcceptance',$completion.AcceptanceRunId,$completion.ScenarioRunId),@('Wrapper',$completion.AcceptanceRunId,'foreign-scenario'))) {
            if($PreparedSource.TryClaimForAnalysis($bad[0],$bad[1],$bad[2],$script:retainedSource,$completion) -or -not $PreparedSource.IsCurrent) {throw 'Foreign owner/scope consumed preparation'}
        }
    } else {$parentArgs.FrontierParentCustody=$custody}
    if($retainedCase -ceq 'valid') {
        if([IO.File]::Exists($script:retainedSource)) {throw 'Native parent fixture unexpectedly has a legacy analyzer file'}
        $foreignOwner=$parentArgs.Clone(); $foreignOwner.FrontierParentCustody=[pscustomobject]@{}
        $foreignCompletion=$parentArgs.Clone(); $foreignCompletion.LocalWriterContext=$parentClone.Invoke($completion,$null)
        foreach($unowned in @(@{},$foreignOwner,$foreignCompletion)) {
            $missingFailure=''
            try {
                if($retainedOwner -ceq 'Wrapper') {$null=Invoke-TunaGuiRetainedAnalysis -RepoRoot $Directory -AnalysisDir $Directory -LogPath $script:retainedSource -LiveRouteProofMode RegularActivationCycle @unowned}
                else {$null=Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $Directory -LiveRouteProofMode RegularActivationCycle @unowned}
            } catch {$missingFailure=$_.Exception.Message}
            $expectedMissing=if($retainedOwner -ceq 'Wrapper') {"Retained log slice was not available: $script:retainedSource"} else {"Retained log slice missing: $script:retainedSource"}
            if($missingFailure -cne $expectedMissing -or $script:retainedLaunchEntries -or $script:retainedHashes -or
                $script:retainedCounts -or $script:retainedWrites -or -not $custody.IsCurrent) {throw 'Missing ordinary source bypassed preflight or consumed original custody'}
        }
        'frontier-retained-source-guards=3'
    }
    if($retainedLegacy) {
        $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    try {
        if($retainedOwner -ceq 'Wrapper') {$retainedOutput=@(Invoke-TunaGuiRetainedAnalysis -RepoRoot $Directory -AnalysisDir $Directory -LogPath $script:retainedSource -LiveRouteProofMode RegularActivationCycle @parentArgs)}
        else {$retainedOutput=@(Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $Directory -LiveRouteProofMode RegularActivationCycle @parentArgs)}
    } catch {$retainedFailure=$_.Exception.Message; $retainedException=$_.Exception}
    $retainedWatch.Stop()
    if($script:retainedLaunchEntries -ne 1 -or @($script:retainedPins | Where-Object {-not $_.IsClosed}).Count -or
        ($null -ne $script:retainedPreparation -and ($script:retainedPreparation.IsCurrent -or $null -ne $script:retainedPreparation.Request)) -or
        ($null -ne $script:retainedLaunchRoot -and $null -ne $script:retainedLaunchRoot.TryOpenTerminalSource($script:retainedSource))) {throw "Original parent launch/cleanup changed: $retainedFailure"}
    if($retainedPrelaunch) {
        $expectedFailure=if($retainedCase.StartsWith('start-collision-')) {'frontier_start_signal_unavailable'} else {'frontier_start_unavailable_or_repeated'}
        $rejected=$retainedFailure -match $expectedFailure
        if($retainedCase -ceq 'start-failure') {
            $inner=$retainedException; while($null -ne $inner -and $null -ne $inner.InnerException) {$inner=$inner.InnerException}
            $rejected=$inner -is [ComponentModel.Win32Exception] -and $inner.NativeErrorCode -eq 2
        }
        if(-not $rejected -or $retainedOutput.Count -or $script:retainedWrites -or $script:retainedHashes -or $script:retainedCounts -or
            $null -ne $script:retainedRoot -or $null -ne $script:retainedReturned) {throw ('Prelaunch rejection hidden or receipt manufactured: '+$retainedFailure)}
        foreach($file in @('primary-pid.txt','filetransfer-retained-analysis.stdout.txt','filetransfer-retained-analysis.stderr.txt',
            'filetransfer-retained-analysis-diagnostics.json','filetransfer-retained-analysis-attempt-receipt.json','filetransfer-frontier-terminal-diagnostic.json')) {
            if(Test-Path -LiteralPath (Join-Path $Directory $file)) {throw ('Rejected launch created worker/report artifact: '+$file)}
        }
        if($retainedCase.StartsWith('start-collision-') -and $script:retainedForeign.WaitOne(0)) {throw 'Readiness rejection signalled foreign event'}
        # The original parent must release its source even when no receipt runs.
        $released=[IO.File]::OpenRead($script:retainedSource); $released.Dispose()
        'frontier-retained-parent='+$retainedOwner+'/'+$retainedCase; return
    }
    $expectedPasses=if($retainedLegacy) {2} else {1}
    if(($retainedLegacy -ne ($null -eq $script:retainedRoot)) -or $script:retainedWrites -ne 1 -or $script:retainedHashes -ne $expectedPasses -or $script:retainedCounts -ne $expectedPasses -or
        $retainedOutput.Count) {
        throw ("Original parent source/receipt/cleanup changed: $retainedFailure : $script:retainedWrites/$script:retainedHashes/$script:retainedCounts")
    }
    $receiptPath=Join-Path $Directory 'filetransfer-retained-analysis-attempt-receipt.json'
    if($retainedCase -ceq 'receipt-write-failure') {
        if($retainedFailure -cne 'injected original receipt write failure' -or (Test-Path -LiteralPath $receiptPath) -or
            (Test-Path -LiteralPath $diagnosticPath)) {throw ('Receipt failure was hidden or published diagnostics: '+$retainedFailure)}
    } else {
        $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        # Preserve the original validators' different report-completion rules.
        # RouteAcceptance's completed receipt is not a route-correctness verdict.
        $expectedStatus=if($retainedCase -ceq 'timeout') {'timed_out'} elseif($retainedCase -ceq 'report-failure') {'failed'} elseif($retainedOwner -ceq 'RouteAcceptance') {'completed'} else {'proof_incomplete'}
        if($receipt.status -cne $expectedStatus -or $receipt.owner -cne $retainedOwner -or -not $receipt.analyzerInputUnchanged -or
            $receipt.analyzerInputSha256Before -cne $completion.Outputs[3].Sha256 -or $receipt.analyzerInputLineCountBefore -ne $completion.Outputs[3].LineCount -or
            (-not $retainedLegacy -and $receipt.attemptId -cne $script:retainedRoot.RequestId.ToString('N')) -or
            ([string]::IsNullOrEmpty($retainedFailure) -ne ($expectedStatus -ceq 'completed'))) {throw ('Original receipt/status changed: '+$retainedFailure+' / '+$receipt.status)}
        if($retainedCase -ceq 'timeout') {
            if(-not $receipt.timedOut -or $receipt.exitCodeAvailable -or $receipt.completedProofArtifacts -or $retainedFailure -notmatch 'timed out after 5s' -or
                $null -ne (Get-Process -Id $receipt.processId -ErrorAction SilentlyContinue)) {throw 'Original timeout watchdog/process cleanup changed'}
        } else {
            if(-not $receipt.exitCodeAvailable -or $receipt.exitCode -ne $(if($retainedCase -ceq 'report-failure') {7} else {0})) {throw 'Original worker exit code changed'}
        }
    }
    if($retainedCase -cne 'timeout') {
        $stdout=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis.stdout.txt') -Raw
        $stderr=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis.stderr.txt') -Raw
        if($stdout -cnotmatch 'original parent analyzer evaluations=3 nativeReads=1' -or $stderr.TrimEnd() -cne 'original parent analyzer stderr') {throw 'Original parent reports were lost'}
    }
    # Prepared wrapper fixtures exercise genuine GUI source ownership, not the
    # synthetic unresolved grace failure used by the diagnostic fixture.
    $expectedDiagnostic=$retainedCase -cin @('valid','delayed-offer')
    if($retainedCase -ceq 'persistence-collision') {
        if((Get-Content -LiteralPath $diagnosticPath -Raw).Trim() -cne 'preserve-existing') {throw 'Preexisting diagnostic overwritten'}
    } elseif((Test-Path -LiteralPath $diagnosticPath) -ne $expectedDiagnostic) {
        $reason=if($null -ne $script:retainedTerminal) {$script:retainedTerminal.OmissionReason} else {'no-terminal'}
        throw ('Parent diagnostic presence changed: '+$retainedCase+' / '+$reason)
    }
    if($expectedDiagnostic) {
        $diagnostic=Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
        if(-not $diagnostic.diagnosticOnly -or $diagnostic.liveProofEligible -or $diagnostic.nativeWriterCustodyEstablished -or $diagnostic.publicationCompletionIssued -or
            $diagnostic.status -cne $receipt.status -or $diagnostic.attemptId -cne $receipt.attemptId -or $diagnostic.owner -cne $retainedOwner -or
            $diagnostic.stdoutReportSha256 -cne $receipt.stdout.sha256 -or $diagnostic.stderrReportSha256 -cne $receipt.stderr.sha256 -or
            $diagnostic.witnessCount -lt 1 -or $diagnostic.witnessCount -gt 64) {throw 'Diagnostic lost exact terminal binding or acquired authority'}
        if($retainedCase -ceq 'delayed-offer' -and $retainedWatch.ElapsedMilliseconds -lt 22000) {throw 'Long-analysis scheduling was not exercised'}
    }
    if($retainedLegacy -and ($custody.IsCurrent -or @($custodyPins | Where-Object {-not $_.IsClosed}).Count)) {throw 'Rejected preparation retained source custody'}
    if($retainedCase -ceq 'capture-failure' -and [IO.File]::ReadAllText($script:retainedCaptureCollision) -cne 'preserve-capture-collision') {throw 'Capture collision overwritten'}
    if($retainedCase -ceq 'receiver-failure') {
        $foreignView=$script:retainedForeign.CreateViewAccessor()
        try {for($i=0;$i -lt 160;$i++) {if($foreignView.ReadByte($i) -ne 0) {throw 'Receiver collision altered foreign mapping'}}} finally {$foreignView.Dispose()}
    }
    if($null -ne $PreparedSource) {
        $replayFailure=''
        try {$null=Invoke-TunaGuiRetainedAnalysis -RepoRoot $Directory -AnalysisDir $Directory -LogPath $script:retainedSource -LiveRouteProofMode RegularActivationCycle @parentArgs}
        catch {$replayFailure=$_.Exception.Message}
        if($replayFailure -cne 'Prepared frontier source is not available for this retained analysis.' -or $script:retainedLaunchEntries -ne 1 -or
            $script:retainedHashes -ne 1 -or $script:retainedCounts -ne 1 -or $script:retainedWrites -ne 1) {throw 'Prepared-source replay launched another primary'}
        'frontier-prepared-source-guards=11'
    }
    'frontier-retained-parent='+$retainedOwner+'/'+$retainedCase
} finally {
    if($null -ne $script:retainedForeign) {$script:retainedForeign.Dispose()}
    Set-Location -LiteralPath $previousLocation.Path
}
