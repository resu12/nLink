namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferSecondTransferTimingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task SecondTransfer_GoodputExcludesReadinessButPreservesProofAndFailureChecks(string host)
    {
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'GUI harness syntax errors'}
            $assignments=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst]},$true))
            $clock=@($assignments|Where-Object {$_.Left.Extent.Text -ceq '$secondSw'})
            $stamp=@($assignments|Where-Object {$_.Left.Extent.Text -ceq '$secondStartedUtc'})
            $send=@($assignments|Where-Object {$_.Left.Extent.Text -ceq '$secondSendButton'})
            if($clock.Count -ne 1 -or $stamp.Count -ne 1 -or $send.Count -ne 1){throw 'Timing source ambiguous'}
            $outer=$send[0].Parent
            while($outer -isnot [Management.Automation.Language.TryStatementAst]){$outer=$outer.Parent;if($null -eq $outer){throw 'Transfer try block missing'}}
            $body=[Collections.Generic.List[string]]::new()
            foreach($statement in $outer.Body.Statements){
                $body.Add($statement.Extent.Text)
                if($statement.Extent.Text -ceq '$secondSw.Stop()'){break}
            }
            if($body[$body.Count-1] -cne '$secondSw.Stop()'){throw 'Transfer timer stop missing'}
            $cleanup=$outer.CatchClauses[0].Body.Statements[1].Extent.Text
            if($cleanup -notmatch 'secondSw.IsRunning' -or $cleanup -notmatch 'secondSw.Stop'){throw 'Failure timer cleanup missing'}
            $run=[scriptblock]::Create($stamp[0].Extent.Text+"`n"+$clock[0].Extent.Text+"`ntry {`n"+($body -join "`n")+"`n} catch {`n"+$cleanup+"`nthrow`n}")
            function CheckClock([bool]$Running,[string]$Stage){
                if($secondSw.IsRunning -ne $Running){throw "clock_state_mismatch:$Stage"}
                $script:stages.Add($Stage)
            }
            function Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer {
                param($Bookmark,$TimeoutMs)
                CheckClock $false 'diagnostic'
                if($Bookmark -ne 23 -or $TimeoutMs -ne 60000){throw 'Diagnostic readiness arguments changed'}
                $script:proofStamp=$secondStartedUtc
                if($script:case -in @('optional_timeout','required_timeout')){throw 'synthetic diagnostic timeout'}
                'diagnostic-only proof'
            }
            function Wait-TunaGuiSecondTransferReadinessOrThrow {
                param($Window,$Bookmark,$TimeoutMs,$RouteMode,$SenderRole)
                CheckClock $false 'ready'
                if($Bookmark -ne 23 -or $TimeoutMs -ne 60000){throw 'Send readiness arguments changed'}
                if($script:case -eq 'readiness_failure'){throw 'synthetic readiness failure'}
                'send'
            }
            function Click-Element($Element){CheckClock $true $Element;if($script:case -eq 'send_failure' -and $Element -eq 'send'){throw 'synthetic send failure'}}
            function Wait-TunaGuiFileTransferAcceptOrThrow { param($Window,$Bookmark,$TimeoutMs) CheckClock $true 'accept';'accept-click' }
            function Wait-TunaGuiFileTunaV4PublicationAfterBookmark {
                param($Bookmark,$TimeoutMs,$Description,$ActionKind,$RequireZeroRouteCommitGeneration)
                CheckClock $true 'publication'
                if($ActionKind -cne 'RoutePublication' -or -not $RequireZeroRouteCommitGeneration -or $TimeoutMs -ne 90000){throw 'Publication proof weakened'}
                'coordinator publication'
            }
            function Wait-FileTransferTerminalPairAfterBookmark {
                param($Bookmark,$TimeoutMs,$ProgressTimeoutMs,$ExpectedFileName,$ExpectedSizeBytes,$ExpectedInboundRole,$ExpectedOutboundRole,$NotBeforeUtc,$ArtifactDir)
                CheckClock $true 'terminal'
                if($NotBeforeUtc -ne $script:proofStamp -or $Bookmark -ne 23 -or $ExpectedSizeBytes -ne 16777216 -or $TimeoutMs -ne 900000 -or $ProgressTimeoutMs -ne 180000){throw 'Terminal proof boundary changed'}
                if($script:case -eq 'terminal_failure'){throw 'synthetic terminal failure'}
                'paired terminal'
            }
            function Set-Content {param([Parameter(ValueFromPipeline=$true)]$Value,$LiteralPath,$Encoding) process {}}
            function Write-Host {param($Object,$ForegroundColor)}
            foreach($script:case in @('ready','optional_timeout','required_timeout','readiness_failure','send_failure','terminal_failure')){
                $script:stages=[Collections.Generic.List[string]]::new();$script:proofStamp=$null
                $secondBookmark=23;$StartupTimeoutMs=90000;$TimeoutMs=900000;$ProgressTimeoutMs=180000
                $secondTransferRequiresFreshDiagnosticReadiness=$script:case -eq 'required_timeout'
                $observedEvidenceLines=[Collections.Generic.List[string]]::new();$summary=@{}
                $RouteMode='live-reactivation-second-transfer';$firstTransferTerminalBeforeLiveReactivationTransferId='fixture'
                $ArtifactDir='unused';$senderWindow='sender';$receiverWindow='receiver'
                $expectedOutboundRole='helpee';$expectedInboundRole='helper';$AutopickPath='fixture.bin';$secondPayloadBytes=16777216
                $failure=''
                try{. $run}catch{$failure=$_.Exception.Message}
                $expectedFailure=switch($script:case){'readiness_failure'{'synthetic readiness failure'} 'send_failure'{'synthetic send failure'} 'terminal_failure'{'synthetic terminal failure'} default{''}}
                if($failure -cne $expectedFailure){throw "Timing regression $script:case : $failure"}
                if($secondSw.IsRunning){throw 'Timer left running'}
                if($script:case -eq 'readiness_failure' -and $secondSw.ElapsedTicks -ne 0){throw 'Readiness failure was timed as transfer'}
                $expectedStages=switch($script:case){'readiness_failure'{'diagnostic,ready'} 'send_failure'{'diagnostic,ready,send'} default{'diagnostic,ready,send,accept,accept-click,publication,terminal'}}
                if(($script:stages -join ',') -cne $expectedStages){throw 'Readiness or proof step skipped'}
                $observed=$script:case -notin @('optional_timeout','required_timeout')
                if($summary.secondTransferDiagnosticReadiness.observed -ne $observed -or $summary.secondTransferDiagnosticReadiness.required -ne $secondTransferRequiresFreshDiagnosticReadiness){throw 'Readiness reporting changed'}
                "pass $script:case"
            }
            "host=$($PSVersionTable.PSVersion)"
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("pass terminal_failure", result.Stdout, StringComparison.Ordinal);
    }
}
