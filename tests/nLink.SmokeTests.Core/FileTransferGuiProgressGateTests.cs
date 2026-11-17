namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiProgressGateTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", false)]
    [InlineData("pwsh", true)]
    public async Task ProgressGate_TerminalGenerationFieldsDoNotTriggerTerminalParsing(string host, bool earlyFallback)
    {
        var script = Fixture() + $$"""
            $lines = [Collections.Generic.List[string]]::new()
            foreach ($eventName in @('filetransfer_route_transition_physical_send_lease_retired',
                'filetransfer_outbound_physical_send_lease_registered',
                'filetransfer_outbound_physical_send_lease_retired',
                'filetransfer_route_transition_physical_send_lease_registered')) {
                for ($i = 0; $i -lt 1041; $i++) {
                    $lines.Add("event=$eventName; source_terminal_generation=0; target_terminal_generation=0; state=Completed; error_code=(none)")
                }
            }
            $lines.Add($peer)
            $gate = '{{(earlyFallback ? "Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault" : "Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault")}}'
            $r = Invoke-Gate -Gate $gate -Snapshots @([pscustomobject]@{ Lines = $lines.ToArray() })
            if ($r.Error) { throw $r.Error }
            if ($r.Result.Kind -ne 'peer_visible_payload' -or $r.Visits -ne 4165 -or
                $r.Polls -ne 1 -or $r.Result.AcceleratedPayloadBytes -ne 524288) { throw 'gate accounting changed' }
            if ($r.Parses -ne 2) { throw "unrelated terminal-generation fields still parsed: $($r.Parses)" }
            'terminal-generation-fast-rejection-ok'
            """;
        await AssertScriptAsync(script, "terminal-generation-fast-rejection-ok", host);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalPredicate_PreservesSupportedEventsAndParserSemantics(string host)
    {
        var script = Fixture() + """
            $script:Visits = 0
            foreach ($eventName in @('file_transfer_inbound_terminal', 'file_transfer_outbound_terminal', 'transfer_terminal')) {
                $direction = if ($eventName -eq 'file_transfer_inbound_terminal') { 'inbound' } else { 'outbound' }
                $line = "event=$eventName; direction=$direction; transfer_id=t1; reason=Transfer complete.; error_code=(none)"
                foreach ($positive in @($line, $line.ToUpperInvariant().Replace('EVENT=', 'EVENT = '), ('event=noise; ' + $line))) {
                    $script:Parses = 0
                    $r = Get-TunaGuiCompletedTerminalFromLogLine -Line $positive
                    if ($null -eq $r -or $r.Direction -ne $direction -or $r.TransferId -ine 't1' -or $script:Parses -ne 1) {
                        throw 'supported terminal event changed'
                    }
                }
                foreach ($negative in @(($line + '; event=noise'), ($line.Replace($eventName, ($eventName + '_suffix'))),
                    "event=noise; detail=$eventName; direction=$direction; transfer_id=t1; state=Completed",
                    ($line + '; state=Failed'), ($line + '; error_code=io_error'))) {
                    if ($null -ne (Get-TunaGuiCompletedTerminalFromLogLine -Line $negative)) { throw 'nonterminal admitted' }
                }
            }
            foreach ($negative in @($null, '', ' ', 'event=noise; source_terminal_generation=0')) {
                $script:Parses = 0
                if ($null -ne (Get-TunaGuiCompletedTerminalFromLogLine -Line $negative) -or $script:Parses -ne 0) {
                    throw 'irrelevant line parsed'
                }
            }
            'terminal-parser-semantics-ok'
            """;
        await AssertScriptAsync(script, "terminal-parser-semantics-ok", host);
    }

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", false)]
    [InlineData("pwsh", true)]
    public async Task ProgressGate_NoiseAvoidsParsingWithoutSkippingRecords(string host, bool earlyFallback)
    {
        var script = Fixture() + $$"""
            $noise = 'event=unrelated; ' + ((1..35 | ForEach-Object { 'field' + $_ + '=' + ('x' * 24) }) -join '; ')
            $lines = [Collections.Generic.List[string]]::new()
            for ($i = 0; $i -lt 18000; $i++) { $lines.Add($noise) }
            $lines.Add($peer)
            $snapshots = @([pscustomobject]@{ Lines = $lines.ToArray() })
            $gate = '{{(earlyFallback ? "Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault" : "Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault")}}'
            $before = Invoke-Gate -Gate $gate -Snapshots $snapshots -Baseline
            $after = Invoke-Gate -Gate $gate -Snapshots $snapshots
            Assert-GateParity $before $after
            if ($after.Error) { throw $after.Error }
            if ($after.Result.Kind -ne 'peer_visible_payload' -or $after.Polls -ne 1 -or
                $after.Visits -ne 18001 -or $after.Result.AcceleratedPayloadBytes -ne 524288) { throw 'scan/accounting changed' }
            if ($before.Parses -ne 18002 -or $after.Parses -ne 2) {
                throw "irrelevant records parsed: baseline=$($before.Parses) current=$($after.Parses)"
            }
            'noise-work-count-ok'
            """;
        await AssertScriptAsync(script, "noise-work-count-ok", host);
    }

    [Theory]
    [InlineData("null", -1)]
    [InlineData("empty", -1)]
    [InlineData("blank", -1)]
    [InlineData("noise", -1)]
    [InlineData("valid", 524288)]
    [InlineData("uppercase-spaced-prefix", 524288)]
    [InlineData("duplicate-valid-last", 524288)]
    [InlineData("duplicate-invalid-last", -1)]
    [InlineData("name-in-value", -1)]
    [InlineData("event-suffix", -1)]
    [InlineData("route", -1)]
    [InlineData("target_route", -1)]
    [InlineData("target_transport", -1)]
    [InlineData("actual_transport", -1)]
    [InlineData("protocol_version", -1)]
    [InlineData("target_protocol_version", -1)]
    [InlineData("route_publication_generation", -1)]
    [InlineData("runtime_generation", -1)]
    [InlineData("terminal_generation", -1)]
    [InlineData("leg_generation", -1)]
    [InlineData("zero-publication", -1)]
    [InlineData("negative-payload", 0)]
    [InlineData("small-frame", 16383)]
    public async Task PayloadPredicate_PreservesParserAndEligibility(string shape, long expectedBytes)
    {
        var script = Fixture() + $$"""
            $line = $peer
            switch ('{{shape}}') {
                'null' { $line = $null }
                'empty' { $line = '' }
                'blank' { $line = '   ' }
                'noise' { $line = 'event=unrelated' }
                'uppercase-spaced-prefix' { $line = '[2026-09-22 08:41:39Z] [INFO] [Test] ' + $peer.ToUpperInvariant().Replace('EVENT=', 'EVENT = ') }
                'duplicate-valid-last' { $line = 'event=noise; ' + $peer }
                'duplicate-invalid-last' { $line += '; event=noise' }
                'name-in-value' { $line = 'event=noise; detail=filetransfer_tuna_data_path_peer_visible' }
                'event-suffix' { $line = $peer.Replace('peer_visible;', 'peer_visible_suffix;') }
                { $_ -in @('route', 'target_route', 'target_transport', 'actual_transport', 'protocol_version', 'target_protocol_version') } { $line += "; $_=wrong" }
                { $_ -in @('route_publication_generation', 'runtime_generation', 'terminal_generation', 'leg_generation') } { $line = $peer -replace ('; ' + $_ + '=\d+'), '' }
                'zero-publication' { $line += '; route_publication_generation=0' }
                'negative-payload' { $line += '; payload_bytes=-1' }
                'small-frame' { $line += '; payload_bytes=16383' }
            }
            foreach ($definition in @($baselinePredicate, $currentPredicate)) {
                . ([scriptblock]::Create($definition))
                $script:Parses = 0
                $actual = Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes -Line $line
                if ($actual -ne {{expectedBytes}}) { throw "payload acceptance changed: $actual" }
            }
            'predicate-parity-ok'
            """;
        await AssertScriptAsync(script, "predicate-parity-ok");
    }

    [Theory]
    [InlineData("peer", "peer_visible_payload")]
    [InlineData("committed", "progress")]
    [InlineData("terminal-before", "completed_before_fault")]
    [InlineData("terminal-after", "completed_before_fault")]
    [InlineData("terminal-mismatch", "peer_visible_payload")]
    [InlineData("minimum-elapsed", "peer_visible_payload")]
    [InlineData("append", "peer_visible_payload")]
    [InlineData("multiple-frames", "peer_visible_payload")]
    [InlineData("small-frame", "error")]
    [InlineData("source-failure", "error")]
    [InlineData("early-fallback", "early_fallback")]
    public async Task ProgressGate_PreservesAccountingThresholdsAndTerminalOrder(string shape, string expectedKind)
    {
        var script = Fixture() + $$"""
            $lines = @($peer); $initialMs = 5000; $failure = $false; $snapshots = @()
            switch ('{{shape}}') {
                'committed' { $lines = @('event=filetransfer_v4_state_sent; bytes_committed=33554432') }
                'terminal-before' { $lines = @($inbound, $outbound, $peer) }
                'terminal-after' { $lines = @($peer, 'event=noise', $inbound, $outbound) }
                'terminal-mismatch' { $lines = @($peer, $inbound, $outbound.Replace('transfer_id=t1', 'transfer_id=t2')) }
                'minimum-elapsed' { $initialMs = 0 }
                'append' { $snapshots = @([pscustomobject]@{ Lines = @('event=noise') }, [pscustomobject]@{ Lines = @('event=noise', $peer) }) }
                'multiple-frames' { $lines = @($peer.Replace('payload_bytes=524288', 'payload_bytes=262144'), $peer.Replace('payload_bytes=524288', 'payload_bytes=262144')) }
                'small-frame' { $lines = @($peer.Replace('payload_bytes=524288', 'payload_bytes=16383')) }
                'source-failure' { $failure = $true }
                'early-fallback' { $lines = @('event=filetransfer_live_route_epoch_started; route=post_tuna_fallback_v6; protocol_version=6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn', $peer) }
            }
            if ($snapshots.Count -eq 0) { $snapshots = @([pscustomobject]@{ Lines = $lines }) }
            foreach ($gate in @('Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault', 'Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault')) {
                if ('{{shape}}' -eq 'early-fallback' -and $gate -eq 'Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault') { continue }
                $options = @{ Gate = $gate; Snapshots = $snapshots; InitialMs = $initialMs; SourceFailure = $failure }
                $before = Invoke-Gate @options -Baseline
                $after = Invoke-Gate @options
                Assert-GateParity $before $after
                $kind = if ($null -eq $after.Result) { 'error' } else { $after.Result.Kind }
                if ($kind -ne '{{expectedKind}}') { throw "unexpected outcome: $kind / $($after.Error)" }
                if ('{{shape}}' -eq 'minimum-elapsed' -and ($after.Polls -ne 6 -or $after.Visits -ne 1 -or $after.Result.ElapsedMs -ne 5000)) { throw 'minimum wait or suffix cursor changed' }
                if ('{{shape}}' -eq 'append' -and ($after.Polls -ne 2 -or $after.Visits -ne 2)) { throw 'append/cursor changed' }
                if ('{{shape}}' -eq 'multiple-frames' -and $after.Result.AcceleratedPayloadBytes -ne 524288) { throw 'frame sum changed' }
                if ('{{shape}}' -eq 'terminal-after' -and $after.Visits -ne 4) { throw 'terminal suffix skipped' }
                if ('{{shape}}' -eq 'small-frame' -and $after.Error -notlike 'Timed out waiting*') { throw 'timeout changed' }
                if ('{{shape}}' -eq 'source-failure' -and $after.Error -ne 'synthetic_cursor_failure') { throw 'source error swallowed' }
            }
            'gate-parity-ok'
            """;
        await AssertScriptAsync(script, "gate-parity-ok");
    }

    [Fact]
    public async Task SharedPredicate_PreservesAcceleratedWaitAndTerminalSummary()
    {
        var script = Fixture() + """
            $script:RawLines = @('event=noise', $peer, ($peer + '; actual_transport=regular_nkn'), $peer, $inbound, $outbound)
            $summaries = @()
            foreach ($definition in @($baselinePredicate, $currentPredicate)) {
                . ([scriptblock]::Create($definition))
                $script:Snapshots = @([pscustomobject]@{ Lines = $script:RawLines }); $script:SourceFailure = $false
                $script:Clock = [pscustomobject]@{ ElapsedMilliseconds = 0 }; $script:Polls = 0; $script:Parses = 0
                $script:Sleeps = [Collections.Generic.List[int]]::new()
                $line = Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark 97 -MinimumTotalPayloadBytes 1048576 -TimeoutMs 7000
                if ($line -cne $peer) { throw 'accelerated payload selection changed' }
                $script:GuiSmokeTerminalEvidenceGeneration = 0L
                $script:GuiSmokeAppLogLedger = [pscustomobject]@{StorageMode='RollingMemory';Lines=$script:RawLines}
                $tracker = New-TunaGuiTerminalEvidenceTracker -Bookmark 0
                [void](Update-TunaGuiTerminalEvidenceTracker -Tracker $tracker)
                $summary = Get-TunaGuiEvidenceSummary -Bookmark 0 -EvidenceTracker $tracker
                if ($tracker.LineVisitCount -ne 6 -or $tracker.Cursor -ne 6 -or
                    -not $summary.terminalPairCompleted -or -not $summary.peerVisibleTunaPayloadObserved -or
                    $summary.tunaAcceleratedFileFrameCount -ne 2 -or $summary.tunaAcceleratedFilePayloadBytes -ne 1048576) { throw 'summary counts or cursor changed' }
                $summaries += ($summary | ConvertTo-Json -Depth 5 -Compress)
            }
            if ($summaries[0] -cne $summaries[1]) { throw 'summary parity changed' }
            'shared-callers-ok'
            """;
        await AssertScriptAsync(script, "shared-callers-ok");
    }

    private static async Task AssertScriptAsync(string script, string marker, string host = "powershell")
    {
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>(), host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(marker, result.Stdout, StringComparison.Ordinal);
    }

    private static string Fixture()
    {
        var root = FindRepoRoot().Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{root}}/tools/GuiSmoke-Windows.ps1', [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw ($errors | Out-String) }
            foreach ($name in @('ConvertFrom-GuiSmokeSemicolonFields', 'Get-GuiSmokeFieldValue', 'ConvertTo-GuiSmokeInt',
                'Get-GuiSmokeInt64FieldValue', 'Test-GuiSmokeFieldEquals', 'Get-GuiSmokeFileTransferTerminalDirection',
                'Set-GuiSmokeFileTransferTerminalDefaults', 'Test-TunaGuiPeerVisibleFileTunaV4DataPathLine',
                'Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes', 'Get-TunaGuiCompletedTerminalFromLogLine',
                'Add-TunaGuiCompletedTerminalToTracker', 'New-TunaGuiLiveSwitchOffProgressGateResult',
                'Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault', 'Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault',
                'Wait-TunaGuiAcceleratedFilePayloadBeforeFault', 'Assert-TunaGuiTerminalEvidenceLine',
                'New-TunaGuiTerminalEvidenceTracker', 'Update-TunaGuiTerminalEvidenceTracker', 'Get-TunaGuiEvidenceSummary',
                'Test-TunaGuiAuthorizedFileTunaV4PublicationLine')) {
                $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true)
                if ($null -eq $node) { throw "missing function $name" }
                # Preserve every production check; only elapsed time and snapshots are synthetic.
                $definition = $node.Extent.Text.Replace('[System.Diagnostics.Stopwatch]::StartNew()', '$script:Clock')
                . ([scriptblock]::Create($definition))
                if ($name -eq 'Test-TunaGuiPeerVisibleFileTunaV4DataPathLine') {
                    $currentPredicate = $definition
                    $guard = $node.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                        $n.Clauses[0].Item1.Extent.Text.Contains(".IndexOf('filetransfer_tuna_data_path_peer_visible',") }, $true)
                    $baselinePredicate = $definition
                    if ($null -ne $guard) { $baselinePredicate = $definition.Replace($guard.Extent.Text, '') }
                }
            }
            $parser = (Get-Command ConvertFrom-GuiSmokeSemicolonFields).ScriptBlock
            function ConvertFrom-GuiSmokeSemicolonFields { param([string]$Message) $script:Parses++; & $parser -Message $Message }
            $originalTerminalParser = (Get-Command Get-TunaGuiCompletedTerminalFromLogLine).ScriptBlock
            function Get-TunaGuiCompletedTerminalFromLogLine { param([string]$Line) $script:Visits++; & $originalTerminalParser -Line $Line }
            function Start-Sleep { param([int]$Milliseconds) $script:Sleeps.Add($Milliseconds); $script:Clock.ElapsedMilliseconds += 1000L }
            function Get-AppLogLinesAfterBookmark {
                param([int]$Bookmark)
                if ($Bookmark -ne 97) { throw 'bookmark changed' }
                $script:Polls++
                if ($script:SourceFailure) { throw 'synthetic_cursor_failure' }
                return $script:Snapshots[[Math]::Min($script:Polls - 1, $script:Snapshots.Count - 1)].Lines
            }
            function Get-GuiSmokeLogLedgerProgressSnapshotAfterBookmark {
                param($State, [int]$Bookmark)
                Get-AppLogLinesAfterBookmark -Bookmark $Bookmark
            }
            function Invoke-Gate {
                param([string]$Gate, [object[]]$Snapshots, [long]$InitialMs = 5000, [bool]$SourceFailure = $false, [switch]$Baseline)
                $definition = if ($Baseline) { $baselinePredicate } else { $currentPredicate }
                . ([scriptblock]::Create($definition))
                $script:Snapshots = $Snapshots; $script:SourceFailure = $SourceFailure
                $script:Parses = 0; $script:Visits = 0; $script:Polls = 0
                $script:Sleeps = [Collections.Generic.List[int]]::new()
                $script:Clock = [pscustomobject]@{ ElapsedMilliseconds = $InitialMs }
                $result = $null; $errorText = ''
                try {
                    $result = & $Gate -Bookmark 97 -TimeoutMs 7000 -MinimumElapsedMs 5000 -MinimumCommittedBytes 33554432 -MinimumPeerVisiblePayloadBytes 524288 -MinimumFramePayloadBytes 16384 -PollIntervalMs 25
                    $result.Line = $result.Line -replace '^\[[^\]]+\]', '[fixed-report-time]'
                }
                catch { $errorText = $_.Exception.Message }
                [pscustomobject]@{ Result = $result; Error = $errorText; Polls = $script:Polls; Visits = $script:Visits; Sleeps = $script:Sleeps -join ','; Parses = $script:Parses }
            }
            function Assert-GateParity {
                param($Before, $After)
                $left = $Before | Select-Object Result,Error,Polls,Visits,Sleeps | ConvertTo-Json -Depth 8 -Compress
                $right = $After | Select-Object Result,Error,Polls,Visits,Sleeps | ConvertTo-Json -Depth 8 -Compress
                if ($left -cne $right) { throw "gate parity changed: $left / $right" }
            }
            $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
            function Assert-GuiSmokeScenarioDeadline {}
            function Sync-GuiSmokeAppLogLedger {}
            function Assert-GuiSmokeLogLedgerHealthy {}
            function Get-GuiSmokeLogLedgerLineCount { return $script:RawLines.Count }
            function Get-GuiSmokeLogLedgerBookmark { return $script:RawLines.Count }
            $peer = 'event=filetransfer_tuna_data_path_peer_visible; route=file_tuna_v4; target_route=file_tuna_v4; target_transport=tuna; actual_transport=tuna; protocol_version=4; target_protocol_version=4; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; payload_bytes=524288'
            $inbound = 'event=file_transfer_inbound_terminal; transfer_id=t1; state=Completed; error_code=(none)'
            $outbound = 'event=transfer_terminal; direction=outbound; transfer_id=t1; reason=Transfer complete.; error_code=(none)'

            """;
    }
}
