namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiTimingDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task GuiTimingDiagnostics_HarnessDelayDoesNotChangeSelectedEvidenceWindow()
    {
        var script = DiagnosticFixture() + """
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $first = Get-GuiSmokeRouteToTerminalTimingDiagnostic @arguments
            $firstElapsed = $watch.Elapsed.TotalMilliseconds
            Start-Sleep -Milliseconds 40
            $arguments.NotAfterUtc = $arguments.NotAfterUtc.AddSeconds(10)
            $second = Get-GuiSmokeRouteToTerminalTimingDiagnostic @arguments
            $secondElapsed = $watch.Elapsed.TotalMilliseconds
            if ($secondElapsed -le $firstElapsed) { throw 'harness delay was not observed' }
            foreach ($result in @($first, $second)) {
                if ($result.status -ne 'available' -or $result.durationMs -ne 2000 -or
                    $result.payloadBytesPerSecond -ne 500000 -or -not $result.reportingOnly) {
                    throw ($result | ConvertTo-Json -Compress)
                }
            }
            $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace(
                'event=file_transfer_outbound_terminal', 'event=transfer_terminal; direction=outbound')
            $generic = Get-GuiSmokeRouteToTerminalTimingDiagnostic @arguments
            if ($generic.status -ne 'available' -or $generic.durationMs -ne 2000) { throw 'generic terminal timing changed' }
            Write-Output 'fixed-evidence-window-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("fixed-evidence-window-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-route", "missing_selected_evidence")]
    [InlineData("missing-terminal", "missing_selected_evidence")]
    [InlineData("duplicate-ticks", "ambiguous_fields")]
    [InlineData("duplicate-identity", "ambiguous_fields")]
    [InlineData("different-writer", "identity_mismatch")]
    [InlineData("different-session", "identity_mismatch")]
    [InlineData("different-transfer", "identity_mismatch")]
    [InlineData("wrong-selected-transfer", "selected_transfer_mismatch")]
    [InlineData("redacted-transfer", "missing_or_redacted_identity")]
    [InlineData("redacted-session", "missing_or_redacted_identity")]
    [InlineData("missing-identity", "missing_or_redacted_identity")]
    [InlineData("missing-ticks", "missing_or_invalid_writer_timing")]
    [InlineData("malformed-ticks", "missing_or_invalid_writer_timing")]
    [InlineData("negative-ticks", "missing_or_invalid_writer_timing")]
    [InlineData("negative-duration", "missing_or_invalid_writer_timing")]
    [InlineData("zero-duration", "missing_or_invalid_writer_timing")]
    [InlineData("reversed-sequence", "missing_or_invalid_writer_timing")]
    [InlineData("stale", "outside_measurement_window")]
    [InlineData("future", "outside_measurement_window")]
    [InlineData("unsupported-route", "unsupported_selected_evidence")]
    [InlineData("inbound-route", "unsupported_selected_evidence")]
    [InlineData("failed-terminal", "unsupported_selected_evidence")]
    [InlineData("invalid-payload", "invalid_payload_size")]
    public async Task GuiTimingDiagnostics_UnsafeEvidenceCannotManufactureRate(string mutation, string expectedReason)
    {
        var script = DiagnosticFixture() + $$"""
            switch ('{{mutation}}') {
                'missing-route' { $arguments.RouteLine = '' }
                'missing-terminal' { $arguments.OutboundTerminalLine = '' }
                'duplicate-ticks' { $arguments.RouteLine += '; log_writer_attempt_utc_ticks=123' }
                'duplicate-identity' { $arguments.RouteLine += '; transfer_id=t' }
                'different-writer' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('log_writer_id=w', 'log_writer_id=other') }
                'different-session' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('session_id=s', 'session_id=other') }
                'different-transfer' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('transfer_id=t', 'transfer_id=other') }
                'wrong-selected-transfer' { $arguments.ExpectedTransferId = 'other' }
                'redacted-transfer' {
                    $arguments.RouteLine = $arguments.RouteLine.Replace('transfer_id=t', 'transfer_id=[redacted]')
                    $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('transfer_id=t', 'transfer_id=[redacted]')
                    $arguments.ExpectedTransferId = '[redacted]'
                }
                'redacted-session' {
                    $arguments.RouteLine = $arguments.RouteLine.Replace('session_id=s', 'session_id=sess_[redacted]')
                    $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('session_id=s', 'session_id=sess_[redacted]')
                }
                'missing-identity' { $arguments.RouteLine = $arguments.RouteLine.Replace('session_id=s;', '') }
                'missing-ticks' { $arguments.RouteLine = $arguments.RouteLine -replace '; log_writer_attempt_utc_ticks=\d+', '' }
                'malformed-ticks' { $arguments.RouteLine = $arguments.RouteLine -replace 'log_writer_attempt_utc_ticks=\d+', 'log_writer_attempt_utc_ticks=invalid' }
                'negative-ticks' { $arguments.RouteLine = $arguments.RouteLine -replace 'log_writer_attempt_utc_ticks=\d+', 'log_writer_attempt_utc_ticks=-1' }
                'negative-duration' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace([string]$endTicks, [string]($startTicks - 1)) }
                'zero-duration' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace([string]$endTicks, [string]$startTicks) }
                'reversed-sequence' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('log_writer_sequence=20', 'log_writer_sequence=9') }
                'stale' { $arguments.NotBeforeUtc = $origin.AddSeconds(2) }
                'future' { $arguments.NotAfterUtc = $origin.AddSeconds(2) }
                'unsupported-route' { $arguments.RouteLine = $arguments.RouteLine.Replace('filetransfer_route_selected', 'filetransfer_runtime_started') }
                'inbound-route' { $arguments.RouteLine = $arguments.RouteLine.Replace('direction=outbound', 'direction=inbound') }
                'failed-terminal' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('state=Completed', 'state=Failed') }
                'invalid-payload' { $arguments.PayloadBytes = 0L }
            }
            $result = Get-GuiSmokeRouteToTerminalTimingDiagnostic @arguments
            if ($result.status -ne 'unavailable' -or $result.reason -ne '{{expectedReason}}' -or
                $null -ne $result.durationMs -or $null -ne $result.payloadBytesPerSecond) {
                throw ($result | ConvertTo-Json -Compress)
            }
            Write-Output 'unsafe-evidence-unavailable'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("unsafe-evidence-unavailable", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiTimingDiagnostics_SummarySeparatesHarnessObservationFromEvidenceDuration()
    {
        var guiPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1");
        var script = DiagnosticFixture() + $$"""
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                '{{guiPath.Replace("'", "''", StringComparison.Ordinal)}}', [ref]$tokens, [ref]$errors)
            $cycle = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Invoke-FileTransferTunaHandoffFallbackCycle'
            }, $true)
            $timingAst = $cycle.Find({ param($node)
                if ($node -is [System.Management.Automation.Language.HashtableAst]) {
                    foreach ($pair in $node.KeyValuePairs) {
                        if ($pair.Item1.Extent.Text -eq 'measurementStart') { return $true }
                    }
                }
                return $false
            }, $true)
            if ($null -eq $timingAst) { throw 'production timing summary not found' }
            $projection = [scriptblock]::Create($timingAst.Extent.Text)
            $setupTimingWatch = [pscustomobject]@{ Elapsed = [TimeSpan]::FromMilliseconds(300) }
            $sw = [pscustomobject]@{ Elapsed = [TimeSpan]::FromMilliseconds(5000) }
            $timingSendAcceptObservedElapsedMs = 200D
            $timingRouteObservedElapsedMs = 2000D
            $timingTerminalWaitStartedElapsedMs = 3500D
            $timingSelectedRouteLine = $arguments.RouteLine
            $terminal = [pscustomobject]@{ OutboundLine = $arguments.OutboundTerminalLine; TransferId = 't' }
            $PayloadSizeBytes = $arguments.PayloadBytes
            $cycleStartedUtc = $arguments.NotBeforeUtc
            $timingTerminalObservedUtc = $arguments.NotAfterUtc
            $first = & $projection
            $sw.Elapsed = [TimeSpan]::FromMilliseconds(10000)
            $timingTerminalObservedUtc = $timingTerminalObservedUtc.AddSeconds(5)
            $second = & $projection
            if ($first.terminalObservedElapsedMs -ne 5000 -or $second.terminalObservedElapsedMs -ne 10000 -or
                $first.terminalObservationWaitMs -ne 1500 -or $second.terminalObservationWaitMs -ne 6500) {
                throw 'harness observations did not include additional delay'
            }
            foreach ($result in @($first, $second)) {
                if ($result.setupBeforeMeasurementMs -ne 300 -or $result.sendAcceptObservedElapsedMs -ne 200 -or
                    $result.selectedRouteObservedElapsedMs -ne 2000 -or $result.routeToTerminal.durationMs -ne 2000 -or
                    $result.routeToTerminal.payloadBytesPerSecond -ne 500000) { throw ($result | ConvertTo-Json) }
            }
            $timingRouteObservedElapsedMs = $null
            $timingSelectedRouteLine = ''
            $missing = & $projection
            if ($null -ne $missing.selectedRouteObservedElapsedMs -or $null -ne $missing.routeToTerminal.payloadBytesPerSecond) {
                throw 'missing route observation was manufactured'
            }
            Write-Output 'summary-timing-boundaries-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("summary-timing-boundaries-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiTimingDiagnostics_WiringPreservesMetricAndDoesNotRescanOrEvaluateAuthority()
    {
        var root = FindRepoRoot();
        var guiText = File.ReadAllText(Path.Combine(root, "tools", "GuiSmoke-Windows.ps1"));
        var helperPath = Path.Combine(root, "tools", "FileTransferOps", "GuiTimingDiagnostics.ps1");
        var helperText = File.ReadAllText(helperPath);
        var cycle = ExtractPowerShellFunctionBody(guiText, "Invoke-FileTransferTunaHandoffFallbackCycle", "Wait-FileTransferTerminalOrProgressBeforeAction");
        Assert.Contains("$goodputBytesPerSecond = if ($sw.Elapsed.TotalSeconds -gt 0) { [Math]::Round($PayloadSizeBytes / $sw.Elapsed.TotalSeconds, 3) } else { 0D }", cycle, StringComparison.Ordinal);
        Assert.Contains("durationMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 3)", cycle, StringComparison.Ordinal);
        Assert.Contains("goodputMeasurementScope = 'gui_end_to_end'", cycle, StringComparison.Ordinal);
        Assert.Contains("-RouteLine $timingSelectedRouteLine -OutboundTerminalLine $terminal.OutboundLine", cycle, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cycle, @"\$timingSelectedRouteLine = \$routeLine").Count);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(cycle, "Get-GuiSmokeRouteToTerminalTimingDiagnostic").Count);
        Assert.DoesNotContain("Get-AppLog", helperText, StringComparison.Ordinal);
        Assert.DoesNotContain("Coordinator", helperText, StringComparison.Ordinal);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            foreach ($path in @('{{helperPath.Replace("'", "''", StringComparison.Ordinal)}}', '{{Path.Combine(root, "tools", "GuiSmoke-Windows.ps1").Replace("'", "''", StringComparison.Ordinal)}}')) {
                $tokens = $null
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) { throw ($errors | Out-String) }
            }
            Write-Output 'timing-syntax-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
    }

    private static string DiagnosticFixture()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "FileTransferOps", "GuiTimingDiagnostics.ps1");
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . '{{helperPath.Replace("'", "''", StringComparison.Ordinal)}}'
            $origin = [datetime]::Parse('2026-09-22T06:14:00Z').ToUniversalTime()
            $startTicks = $origin.AddSeconds(1).Ticks
            $endTicks = $origin.AddSeconds(3).Ticks
            $arguments = @{
                RouteLine = "[2026-09-22 06:14:01Z] [INFO] [FileTransferService] event=filetransfer_route_selected; direction=outbound; transfer_id=t; session_id=s; log_writer_id=w; log_writer_sequence=10; log_writer_attempt_utc_ticks=$startTicks"
                OutboundTerminalLine = "[2026-09-22 06:14:03Z] [INFO] [Session] event=file_transfer_outbound_terminal; state=Completed; error_code=(none); transfer_id=t; session_id=s; log_writer_id=w; log_writer_sequence=20; log_writer_attempt_utc_ticks=$endTicks"
                ExpectedTransferId = 't'
                PayloadBytes = 1000000L
                NotBeforeUtc = $origin
                NotAfterUtc = $origin.AddSeconds(5)
            }

            """;
    }
}
