namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierGuiTerminalIntegrityTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalIntegrity_PreservesCompletedWhenHashUnavailable(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
            $field=@($ast.EndBlock.Statements | Where-Object {
                $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Get-GuiSmokeFieldValue'})
            Invoke-Expression $field[0].Extent.Text
            foreach($name in @('Get-FileSha256Hex','Get-FileTransferTunaTerminalIntegrity')) {
                Invoke-Expression (Get-AttemptFunction $ast $name)
            }
            $terminal=@{state='Completed';error_code='(none)'}
            $path=Join-Path $RepoRoot ('artifacts/terminal-integrity-'+[guid]::NewGuid().ToString('N')+'.bin')
            [IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3))
            $expected=Get-FileSha256Hex -Path $path
            $result=Get-FileTransferTunaTerminalIntegrity -Inbound $terminal -Outbound $terminal -Path $path -PayloadSizeBytes 3 -ExpectedHash $expected
            if(-not $result.Completed -or -not $result.IntegrityOk -or $result.SavedSize -ne 3 -or $result.ActualHash -cne $expected -or $result.MeasurementError) {throw 'Ordinary hash changed'}
            $hold=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            try {
                $result=Get-FileTransferTunaTerminalIntegrity -Inbound $terminal -Outbound $terminal -Path $path -PayloadSizeBytes 3 -ExpectedHash $expected
                if(-not $result.Completed -or $result.IntegrityOk -or $result.InboundState -cne 'Completed' -or
                    $result.OutboundState -cne 'Completed' -or -not $result.MeasurementError -or $result.ActualHash -cne '(none)') {throw 'Hash failure erased terminal facts or passed integrity'}
            } finally {$hold.Dispose()}
            $terminalIntegrity=$result
            $completed=$result.Completed; $integrityOk=$result.IntegrityOk
            $inboundState=$result.InboundState; $outboundState=$result.OutboundState
            $liveSwitchOffTerminalCompletedBeforeFault=$false
            $cycle=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'},$true))[0]
            $summaryAssignment=@($cycle.Body.EndBlock.Statements | Where-Object {
                $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$summary'})[0]
            $table=@($summaryAssignment.FindAll({param($n) $n -is [Management.Automation.Language.HashtableAst]},$true))[0]
            $summary=@{}
            foreach($pair in $table.KeyValuePairs) {
                $key=$pair.Item1.Value
                if($key -cin @('completed','integrityOk','inboundState','outboundState','failurePhase','failureReason','integrityMeasurementError')) {
                    $summary[$key]=Invoke-Expression $pair.Item2.Extent.Text
                }
            }
            if(-not $summary.completed -or $summary.integrityOk -or $summary.inboundState -cne 'Completed' -or
                $summary.outboundState -cne 'Completed' -or $summary.failurePhase -cne 'terminal_integrity_measurement' -or
                -not $summary.integrityMeasurementError) {throw 'Production summary lost terminal measurement failure'}
            $wrapper=Read-AttemptAst (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
            $proof=@($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and
                $n.Left.Extent.Text -ceq '$currentRuntimeProof'},$true))[0]
            $artifactFinalized=$cursorPass=$writerPass=$retainedAnalysisCompleted=$analysisReceiptValid=$GuiExitCodeAvailable=$identityMatches=$true
            $sourceDirty=$false; $GuiExitCode=1; $summaryValid=$false
            Invoke-Expression $proof.Extent.Text
            if($currentRuntimeProof) {throw 'Failed GUI result granted runtime proof'}
            'terminal-integrity-reporting-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("terminal-integrity-reporting-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "wrong-file")]
    [InlineData("pwsh", "dispose")]
    public async Task TerminalIntegrity_MeasuresOriginalHeldDestination(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "terminal-integrity-fix-20260930",
            host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        script = script.Replace("$ErrorActionPreference='Stop';",
            "$env:NLINK_TEST_TERMINAL_MEASUREMENT='" + fault + "'; $ErrorActionPreference='Stop';", StringComparison.Ordinal);
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "writer-source-read-cross-process-gui-valid"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("terminal-held-measurement=" + fault,
            await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log")));
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "cancel")]
    [InlineData("pwsh", "deadline")]
    [InlineData("pwsh", "reject")]
    [InlineData("pwsh", "dispose")]
    public Task TerminalIntegrity_UsesArmedDestinationWithinDeadline(string host, string scenario) =>
        new FrontierGuiDestinationLifetimeTests().ProductionGui_ArmsAndJoinsOriginalDestinationLifetime(host, scenario);
}
