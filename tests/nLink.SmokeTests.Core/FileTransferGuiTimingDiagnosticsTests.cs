namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiTimingDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("canonical", "selected_evidence_matched")]
    [InlineData("redacted", "missing_or_redacted_identity")]
    [InlineData("different-writer", "identity_mismatch")]
    [InlineData("duplicate-bytes", "ambiguous_fields")]
    [InlineData("failed-state", "unsupported_selected_evidence")]
    [InlineData("canceled-state", "unsupported_selected_evidence")]
    [InlineData("blank-state", "unsupported_selected_evidence")]
    [InlineData("inbound", "unsupported_selected_evidence")]
    [InlineData("missing-direction", "unsupported_selected_evidence")]
    [InlineData("wrong-event", "unsupported_selected_evidence")]
    [InlineData("incomplete-reason", "unsupported_selected_evidence")]
    [InlineData("missing-reason", "unsupported_selected_evidence")]
    [InlineData("error", "unsupported_selected_evidence")]
    [InlineData("missing-error", "unsupported_selected_evidence")]
    [InlineData("partial-bytes", "unsupported_selected_evidence")]
    [InlineData("wrong-size", "unsupported_selected_evidence")]
    [InlineData("missing-size", "unsupported_selected_evidence")]
    [InlineData("malformed-bytes", "unsupported_selected_evidence")]
    [InlineData("overflow-bytes", "unsupported_selected_evidence")]
    [InlineData("partial-chunks", "unsupported_selected_evidence")]
    [InlineData("missing-chunks", "unsupported_selected_evidence")]
    [InlineData("zero-chunks", "unsupported_selected_evidence")]
    [InlineData("negative-chunks", "unsupported_selected_evidence")]
    public async Task GuiTimingDiagnostics_ServiceTerminalWithoutStateRequiresCanonicalCompletion(string mutation, string expectedReason)
    {
        var script = DiagnosticFixture() + $$"""
            $arguments.OutboundTerminalLine = "[2026-09-22 06:14:03Z] [INFO] [FileTransferService] event=transfer_terminal; direction=outbound; transfer_id=t; session_id=s; file_name_len=33; file_size_bytes=1000000; bytes_transferred=1000000; chunks_transferred=10; chunk_count=10; error_code=(none); reason=Transfer complete.; saved_path=(none); route=post_tuna_fallback_v6; protocol_version=6; log_writer_id=w; log_writer_sequence=20; log_writer_attempt_utc_ticks=$endTicks"
            switch ('{{mutation}}') {
                'canonical' {}
                'redacted' {
                    $arguments.RouteLine = $arguments.RouteLine.Replace('transfer_id=t', 'transfer_id=[redacted]').Replace('session_id=s', 'session_id=sess_[redacted]')
                    $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('transfer_id=t', 'transfer_id=[redacted]').Replace('session_id=s', 'session_id=sess_[redacted]')
                    $arguments.ExpectedTransferId = '[redacted]'
                }
                'different-writer' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('log_writer_id=w', 'log_writer_id=other') }
                'duplicate-bytes' { $arguments.OutboundTerminalLine += '; bytes_transferred=1000000' }
                'failed-state' { $arguments.OutboundTerminalLine += '; state=Failed' }
                'canceled-state' { $arguments.OutboundTerminalLine += '; state=Canceled' }
                'blank-state' { $arguments.OutboundTerminalLine += '; state=' }
                'inbound' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('direction=outbound', 'direction=inbound') }
                'missing-direction' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('direction=outbound;', '') }
                'wrong-event' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('event=transfer_terminal', 'event=file_transfer_outbound_terminal') }
                'incomplete-reason' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('reason=Transfer complete.', 'reason=Transfer incomplete.') }
                'missing-reason' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('reason=Transfer complete.;', '') }
                'error' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('error_code=(none)', 'error_code=failed') }
                'missing-error' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('error_code=(none);', '') }
                'partial-bytes' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('bytes_transferred=1000000', 'bytes_transferred=999999') }
                'wrong-size' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('file_size_bytes=1000000', 'file_size_bytes=999999') }
                'missing-size' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('file_size_bytes=1000000;', '') }
                'malformed-bytes' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('bytes_transferred=1000000', 'bytes_transferred=invalid') }
                'overflow-bytes' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('bytes_transferred=1000000', 'bytes_transferred=9223372036854775808') }
                'partial-chunks' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('chunks_transferred=10', 'chunks_transferred=9') }
                'missing-chunks' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('chunk_count=10;', '') }
                'zero-chunks' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('chunks_transferred=10', 'chunks_transferred=0').Replace('chunk_count=10', 'chunk_count=0') }
                'negative-chunks' { $arguments.OutboundTerminalLine = $arguments.OutboundTerminalLine.Replace('chunks_transferred=10', 'chunks_transferred=-1').Replace('chunk_count=10', 'chunk_count=-1') }
                default { throw 'unknown mutation' }
            }
            $result = Get-GuiSmokeRouteToTerminalTimingDiagnostic @arguments
            if ($result.reason -ne '{{expectedReason}}') { throw ($result | ConvertTo-Json -Compress) }
            if ('{{mutation}}' -eq 'canonical') {
                if ($result.status -ne 'available' -or $result.durationMs -ne 2000 -or $result.payloadBytesPerSecond -ne 500000) {
                    throw ($result | ConvertTo-Json -Compress)
                }
            }
            elseif ($result.status -ne 'unavailable' -or $null -ne $result.durationMs -or $null -ne $result.payloadBytesPerSecond) {
                throw 'unsafe service terminal manufactured timing'
            }
            Write-Output 'service-terminal-shape-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("service-terminal-shape-ok", result.Stdout, StringComparison.Ordinal);
    }

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
            $timingFallbackStartedWaitEnteredMs = 2100D
            $timingFallbackStartedWaitReturnedMs = 2201D
            $timingFallbackRouteWaitEnteredMs = 2210D
            $timingFallbackRouteWaitReturnedMs = 2421D
            $timingFallbackRecoveredWaitEnteredMs = 2430D
            $timingFallbackRecoveredWaitReturnedMs = 2737D
            $timingSelectedRouteLine = $arguments.RouteLine
            $observerTiming = [ordered]@{reportingOnly=$true;status='available';projectionMs=123}
            $terminal = [pscustomobject]@{ OutboundLine = $arguments.OutboundTerminalLine; TransferId = 't'; TimingDiagnostics = $observerTiming }
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
                if (-not [object]::ReferenceEquals($result.terminalObserver,$observerTiming)) { throw 'observer timing was recomputed or replaced' }
                if ($result.setupBeforeMeasurementMs -ne 300 -or $result.sendAcceptObservedElapsedMs -ne 200 -or
                    $result.selectedRouteObservedElapsedMs -ne 2000 -or $result.routeToTerminal.durationMs -ne 2000 -or
                    $result.routeToTerminal.payloadBytesPerSecond -ne 500000) { throw ($result | ConvertTo-Json) }
                $waits = $result.switchOffFallbackWaits
                if ($waits.started.enteredElapsedMs -ne 2100 -or $waits.started.returnedElapsedMs -ne 2201 -or $waits.started.durationMs -ne 101 -or
                    $waits.route.enteredElapsedMs -ne 2210 -or $waits.route.returnedElapsedMs -ne 2421 -or $waits.route.durationMs -ne 211 -or
                    $waits.recovered.enteredElapsedMs -ne 2430 -or $waits.recovered.returnedElapsedMs -ne 2737 -or $waits.recovered.durationMs -ne 307) {
                    throw 'fallback attribution changed with unrelated terminal delay'
                }
            }
            $timingRouteObservedElapsedMs = $null
            $timingSelectedRouteLine = ''
            $timingFallbackStartedWaitEnteredMs = $null
            $timingFallbackStartedWaitReturnedMs = $null
            $timingFallbackRouteWaitEnteredMs = $null
            $timingFallbackRouteWaitReturnedMs = $null
            $timingFallbackRecoveredWaitEnteredMs = $null
            $timingFallbackRecoveredWaitReturnedMs = $null
            $missing = & $projection
            if ($null -ne $missing.selectedRouteObservedElapsedMs -or $null -ne $missing.routeToTerminal.payloadBytesPerSecond) {
                throw 'missing route observation was manufactured'
            }
            if ($null -ne $missing.switchOffFallbackWaits.started -or $null -ne $missing.switchOffFallbackWaits.route -or
                $null -ne $missing.switchOffFallbackWaits.recovered) { throw 'unexecuted waits manufactured timing' }
            Write-Output 'summary-timing-boundaries-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("summary-timing-boundaries-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none", false, 3)]
    [InlineData("started", false, 1)]
    [InlineData("started", true, 1)]
    [InlineData("route", false, 2)]
    [InlineData("route", true, 2)]
    [InlineData("recovered", false, 3)]
    [InlineData("recovered", true, 3)]
    public async Task GuiTimingDiagnostics_SwitchOffWaitBoundariesPreserveResultsAndFailures(string failureStage, bool canceled, int expectedCalls)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var cycle = ExtractPowerShellFunctionBody(guiText, "Invoke-FileTransferTunaHandoffFallbackCycle", "Wait-FileTransferTerminalOrProgressBeforeAction");
        var start = cycle.IndexOf("$timingFallbackStartedWaitEnteredMs = $sw.Elapsed.TotalMilliseconds", StringComparison.Ordinal);
        const string endMarker = "$fallbackEpochRecoveredObserved = $true";
        Assert.True(start >= 0);
        var end = cycle.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start);
        var waitBlock = cycle[start..(end + endMarker.Length)];
        var initializers = System.Text.RegularExpressions.Regex.Matches(cycle, @"\$timingFallback\w+ = \$null");
        Assert.Equal(6, initializers.Count);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            {{string.Join(Environment.NewLine, initializers.Select(match => match.Value))}}
            $sw = [pscustomobject]@{ Elapsed = [TimeSpan]::FromMilliseconds(1000) }
            $bookmark = 852
            $observedEvidenceLines = New-Object 'System.Collections.Generic.List[string]'
            $liveRouteEpochObservations = New-Object 'System.Collections.Generic.List[string]'
            $waitCalls = New-Object 'System.Collections.Generic.List[string]'
            $fallbackEpochStartedObserved = $false
            $fallbackEpochRecoveredObserved = $false
            $expectedException = if ({{(canceled ? "$true" : "$false")}}) {
                [OperationCanceledException]::new('original cancellation')
            } else { [TimeoutException]::new('original timeout') }
            function Invoke-FakeWait {
                param([string]$Name, [int]$Delay)
                $waitCalls.Add($Name)
                $sw.Elapsed = $sw.Elapsed.Add([TimeSpan]::FromMilliseconds($Delay))
                if ($Name -eq '{{failureStage}}') { throw $expectedException }
                return "selected-$Name"
            }
            function Wait-TunaGuiLiveRouteEpochStarted {
                param($Bookmark, $Route, $ProtocolVersion, $HandoffKind, $TargetTransport, $Description)
                if ($Bookmark -ne 852 -or $Route -ne 'post_tuna_fallback_v6' -or $ProtocolVersion -ne 6 -or
                    $HandoffKind -ne 'tuna_to_normal_fallback' -or $TargetTransport -ne 'regular_nkn') { throw 'start scope changed' }
                Invoke-FakeWait started 101
            }
            function Wait-AppLogContainsAnyAllAfterBookmark {
                param($Bookmark, $NeedleSets, $TimeoutMs, $Description)
                if ($Bookmark -ne 852 -or $TimeoutMs -ne 90000 -or $NeedleSets.Count -ne 2 -or
                    ($NeedleSets[0] -join '|') -ne 'event=filetransfer_route_selected|route=post_tuna_fallback_v6|protocol_version=6' -or
                    ($NeedleSets[1] -join '|') -ne 'event=filetransfer_protocol_negotiated|route=post_tuna_fallback_v6|protocol_version=6') { throw 'route scope changed' }
                Invoke-FakeWait route 211
            }
            function Wait-TunaGuiLiveRouteEpochRecovered {
                param($Bookmark, $Route, $ProtocolVersion, $HandoffKind, $TargetTransport, $Description, $AfterLine)
                if ($Bookmark -ne 852 -or $Route -ne 'post_tuna_fallback_v6' -or $ProtocolVersion -ne 6 -or
                    $HandoffKind -ne 'tuna_to_normal_fallback' -or $TargetTransport -ne 'regular_nkn' -or
                    $AfterLine -cne 'selected-started') { throw 'recovery scope changed' }
                Invoke-FakeWait recovered 307
            }
            function Add-TunaGuiLiveRouteEpochObservation {
                param($Observations, $Action, $Line)
                $Observations.Add("$Action|$Line")
            }
            $waitBlock = {
                {{waitBlock}}
            }
            $caught = $null
            try { $output = @(. $waitBlock) } catch { $caught = $_.Exception }
            if ($waitCalls.Count -ne {{expectedCalls}}) { throw 'wait call count changed' }
            if ('{{failureStage}}' -eq 'none') {
                if ($null -ne $caught) { throw $caught }
                if ($output.Count -ne 0 -or ($observedEvidenceLines -join '|') -cne 'selected-started|selected-route|selected-recovered' -or
                    ($liveRouteEpochObservations -join ',') -cne 'off_started|selected-started,off_recovered|selected-recovered' -or
                    -not $fallbackEpochStartedObserved -or -not $fallbackEpochRecoveredObserved) { throw 'selected evidence changed' }
                if ($timingFallbackStartedWaitEnteredMs -ne 1000 -or $timingFallbackStartedWaitReturnedMs -ne 1101 -or
                    $timingFallbackRouteWaitEnteredMs -ne 1101 -or $timingFallbackRouteWaitReturnedMs -ne 1312 -or
                    $timingFallbackRecoveredWaitEnteredMs -ne 1312 -or $timingFallbackRecoveredWaitReturnedMs -ne 1619) { throw 'wait boundaries moved' }
            }
            else {
                if (-not [object]::ReferenceEquals($caught, $expectedException)) { throw 'original exception did not propagate' }
                $returned = Get-Variable -Name 'timingFallback{{(failureStage == "started" ? "Started" : failureStage == "route" ? "Route" : "Recovered")}}WaitReturnedMs' -ValueOnly
                if ($null -ne $returned -or $fallbackEpochRecoveredObserved) { throw 'failed wait was marked completed' }
            }
            Write-Output 'switch-off-wait-boundaries-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("switch-off-wait-boundaries-ok", result.Stdout, StringComparison.Ordinal);
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
