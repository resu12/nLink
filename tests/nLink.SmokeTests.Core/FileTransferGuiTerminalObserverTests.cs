namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiTerminalObserverTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Modes()
    {
        foreach (var host in new[] { "powershell", "pwsh" })
        foreach (var file in new[] { false, true })
            yield return new object[] { host, file };
    }

    [Fact]
    public void TerminalObserver_ClassifiesSummaryInlineWithoutPerRecordHelperDispatch()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var body = ExtractPowerShellFunctionBody(script, "Update-TunaGuiTerminalEvidenceTracker", "Get-TopLevelWindowElementsByProcessId");
        Assert.DoesNotContain("Test-TunaGuiEvidenceSummaryRelevantLine", script, StringComparison.Ordinal);
        Assert.Contains("if ([string]::IsNullOrEmpty($line))", body, StringComparison.Ordinal);
        Assert.Contains("Assert-TunaGuiTerminalEvidenceLine -Line $line", body, StringComparison.Ordinal);
        Assert.Contains("$summaryRelevant = if ($eventName -in @(", body, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TerminalObserver_PreservesSummaryClassificationAndOrdering(string host, bool file)
    {
        await Check(Fixture(file) + """
            $names = @(
                'transfer_terminal', 'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal',
                'tuna_acceleration_negotiated', 'runtime_unlock_transaction_answer_received', 'filetransfer_route_selected',
                'filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied',
                'filetransfer_tuna_gui_live_switch_off_completed_before_fault', 'filetransfer_tuna_gui_live_switch_off_fault_injected',
                'filetransfer_tuna_gui_fault_window', 'filetransfer_v6_epoch_started', 'filetransfer_v6_epoch_recovered',
                'filetransfer_v6_epoch_observed', 'filetransfer_v6_epoch_waiting', 'tuna_fallback_started',
                'tuna_fallback_filetransfer_rebind_requested', 'tuna_acceleration_user_stop_filetransfer_fallback_forced',
                'filetransfer_post_tuna_recovery_started', 'filetransfer_post_tuna_fallback_nkn_proved',
                'filetransfer_live_v4_fallback_nkn_proved', 'filetransfer_post_tuna_fallback_cleanup_completed',
                'filetransfer_live_v4_fallback_cleanup_completed', 'tuna_disable_handoff_completed',
                'tuna_fallback_nkn_frame_sent', 'tuna_fallback_nkn_frame_received', 'filetransfer_v6_sender_started',
                'filetransfer_lifecycle_priority_sent', 'filetransfer_lifecycle_priority_received', 'filetransfer_v6_heartbeat_timeout',
                'filetransfer_tuna_data_path_peer_visible', 'filetransfer_binary_frame_sent', 'filetransfer_binary_frame_received',
                'filetransfer_v6_sparse_write_committed', 'filetransfer_v6_contiguous_write_committed')
            $excluded = @('filetransfer_tuna_data_path_peer_visible', 'filetransfer_binary_frame_sent',
                'filetransfer_binary_frame_received', 'filetransfer_v6_sparse_write_committed', 'filetransfer_v6_contiguous_write_committed')
            $lines = [Collections.Generic.List[string]]::new()
            $expected = [Collections.Generic.List[string]]::new()
            foreach ($name in $names) {
                foreach ($line in @(('event='+$name), ('EVENT='+$name.ToUpperInvariant()), ('event= '+$name+' ; tag=padded'))) {
                    $lines.Add($line)
                    if ($name -notin $excluded) { $expected.Add($line) }
                }
                $lines.Add('event='+$name+'_not_exact')
            }
            foreach ($line in @('note=peer_disconnected', 'note=PEER_DISCONNECTED', 'transport_state_changed; to=Failed',
                'prefixTRANSPORT_STATE_CHANGEDsuffix; to=fAiLeDlater', 'event=; note=peer_disconnected',
                'note=event=filetransfer_route_selected; event=noise', 'event=filetransfer_route_selected; event=noise')) {
                $lines.Add($line); $expected.Add($line)
            }
            foreach ($line in @('transport_state_changed; to=Ready', 'to=Failed', 'event=noise; event=filetransfer_route_selected',
                'event=peer_disconnected; event=filetransfer_binary_frame_sent', '   ', ('unicode='+[char]0x03bb))) {
                $lines.Add($line)
                if ($line -like 'event=peer_disconnected;*') { $expected.Add($line) }
            }
            $failed = $outbound.Replace('Transfer complete.', 'Transfer failed.')
            foreach ($line in @($inbound, $outbound, $failed)) { $lines.Add($line); $expected.Add($line) }
            $culture = [Threading.Thread]::CurrentThread.CurrentCulture
            try {
                foreach ($name in @('en-US','tr-TR')) {
                    [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($name)
                    $r = Invoke-Case -Lines $lines.ToArray()
                    $t = $r.Tracker
                    if ($r.Error -or $t.Cursor -ne $lines.Count -or $t.LineVisitCount -ne $lines.Count -or
                        ($t.SummaryLines -join "`n") -cne ($expected -join "`n")) { throw 'summary classification/order changed' }
                    if ($t.ByTransfer.Count -ne 1 -or $t.ByTransfer['t']['OutboundLine'] -cne $failed -or
                        $t.ByTransfer['t']['Outbound']['state'] -ne 'Failed' -or
                        $t.ByTransfer['t']['OutboundLineIndex'] -ne $lines.Count-1) { throw 'terminal replacement changed' }
                    if ($script:IsFile -and ($r.ReadIndices.Count -ne $lines.Count -or
                        $r.ReadIndices[$lines.Count-1] -ne $lines.Count-1)) { throw 'classification skipped physical reads' }
                }
            } finally { [Threading.Thread]::CurrentThread.CurrentCulture = $culture }
            'observer-ok'
            """, host);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TerminalObserver_EmptyTailPreservesBindingFailureAndPartialState(string host, bool file)
    {
        await Check(Fixture(file) + """
            function Test-OriginalLineBinding {
                param([Parameter(Mandatory=$true)][string]$Line, [Parameter(Mandatory=$true)][AllowEmptyString()][string]$EventName)
            }
            $expectedError=''; $expectedTypes=[Collections.Generic.List[string]]::new()
            try { Test-OriginalLineBinding -Line '' -EventName '' } catch {
                $expectedError=$_.Exception.Message
                $e=$_.Exception; while($e) { $expectedTypes.Add($e.GetType().FullName); $e=$e.InnerException }
            }
            $r=Invoke-Case -Lines @($inbound,$outbound,'','event=filetransfer_binary_frame_sent; raw_chunk_bytes=99')
            $t=$r.Tracker
            if (-not $expectedError -or $r.Error -cne $expectedError -or
                ($r.ErrorTypes -join '|') -cne ($expectedTypes -join '|')) { throw 'empty-line binding failure changed' }
            if ($t.Cursor -ne 2 -or $t.LineVisitCount -ne 2 -or $t.ParsedEventCount -ne 2 -or $t.SummaryLines.Count -ne 2 -or
                $t.SenderChunkBytes -ne 0 -or $t.ProjectionElapsedTicks -ne 0 -or
                $t.ByTransfer['t']['OutboundLine'] -cne $outbound) { throw 'empty-tail partial state changed' }
            if ($script:IsFile -and ($r.ReadIndices -join ',') -cne '0,1,2') { throw 'read past rejected empty tail' }
            'observer-ok'
            """, host);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TerminalObserver_ReadsWholeHorizonWithoutCallbacksOrReplay(string host, bool file)
    {
        await Check(Fixture(file) + """
            $lines = [Collections.Generic.List[string]]::new()
            $lines.Add($inbound); $lines.Add($outbound)
            for ($i=0; $i -lt 8193; $i++) { $lines.Add('event=noise; data=' + ('x'*600)) }
            $r = Invoke-Case -Lines $lines.ToArray()
            if ($r.Error -or $r.Tracker.Cursor -ne 8195 -or $r.Tracker.LineVisitCount -ne 8195 -or
                $r.Tracker.ParsedEventCount -ne 2 -or $r.Tracker.MaxBatchLineCount -ne 4096) { throw 'horizon/accounting changed' }
            if ($r.Callbacks -ne 0) { throw "terminal callbacks remain: $($r.Callbacks)" }
            if ($script:IsFile -and ($r.ReadIndices.Count -ne 8195 -or $r.ReadIndices[8194] -ne 8194)) { throw 'physical tail skipped' }
            if ($r.Tracker.ByTransfer['t']['OutboundLine'] -cne $outbound) { throw 'selected terminal changed' }
            'observer-ok'
            """, host);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TerminalObserver_PreservesSemanticsBoundariesAndAppends(string host, bool file)
    {
        await Check(Fixture(file) + """
            $lines = @('event=noise', $peer, $peer.Replace('protocol_version=4','protocol_version=6'),
                'event=filetransfer_binary_frame_sent; frame_type=chunk_data; raw_chunk_bytes=10; raw_chunk_bytes=17',
                'event=filetransfer_binary_frame_received; frame_type=chunk_batch; raw_chunk_bytes=11',
                'event=filetransfer_v6_sparse_write_committed; written_bytes=12',
                'event=filetransfer_v4_state_received; contiguous_committed_chunk_index=9; durable_received_highest_chunk_index=11',
                $inbound.Replace('role=helper','role=wrong'), $inbound, $outbound,
                'note=peer_disconnected', 'transport_state_changed; to=Failed', ('unicode=' + [char]0x03bb))
            $r = Invoke-Case -Lines $lines
            $t = $r.Tracker
            if ($r.Error -or $t.LineVisitCount -ne 13 -or $t.ParsedEventCount -ne 9 -or $t.ByTransfer.Count -ne 1 -or
                $t.SenderChunkBytes -ne 17 -or $t.ReceiverChunkBytes -ne 23 -or $t.ProgressEventCount -ne 3 -or
                $t.TunaAcceleratedFileFrameCount -ne 1 -or $t.TunaAcceleratedFilePayloadBytes -ne 524288 -or
                $t.MaxReceiverNextChunkIndex.Value -ne 9 -or $t.MaxReceiverHighestChunkIndex.Value -ne 11) { throw 'semantic accounting changed' }
            if ($t.ByTransfer['t']['InboundLineIndex'] -ne 8 -or $t.ByTransfer['t']['OutboundLineIndex'] -ne 9 -or
                $t.ByTransfer['t']['Outbound']['state'] -ne 'Completed' -or $t.SummaryLines.Count -ne 5 -or
                $t.SummaryLines[4] -cne 'transport_state_changed; to=Failed') { throw 'terminal/summary ordering changed' }
            $r = Invoke-Case -Lines @($inbound,$outbound) -Bookmark 1
            if ($r.Error -or $null -ne $r.Tracker.ByTransfer['t']['Inbound'] -or $r.Tracker.LineVisitCount -ne 1) { throw 'bookmark widened' }
            $r = Invoke-Case -Lines @($inbound,$outbound) -EndBookmark 1
            if ($r.Error -or $null -ne $r.Tracker.ByTransfer['t']['Outbound'] -or $r.Tracker.Cursor -ne 1) { throw 'horizon widened' }
            $r = Invoke-Case -Lines @($inbound) -Append $outbound
            if ($r.Error -or $r.Tracker.LineVisitCount -ne 2 -or $r.Tracker.ByTransfer['t']['OutboundLine'] -cne $outbound) { throw 'append lost/replayed' }
            $r = Invoke-Case -Lines @()
            if ($r.Error -or $r.Tracker.Cursor -ne 0 -or $r.Callbacks -ne 0) { throw 'empty ledger changed' }
            $r = Invoke-Case -Lines @($inbound,$outbound) -Fault 'late-append'
            if ($r.Error -or $r.Tracker.Cursor -ne 2 -or $r.Tracker.SenderChunkBytes -ne 0) { throw 'late append crossed captured horizon' }
            'observer-ok'
            """, host);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TerminalObserver_PreservesFailuresPartialStateAndDisposal(string host, bool file)
    {
        await Check(Fixture(file) + """
            foreach ($options in @(@{Bookmark=-1}, @{EndBookmark=99}, @{Bookmark=2;EndBookmark=1})) {
                $r = Invoke-Case -Lines @($inbound,$outbound) @options
                if (-not $r.Error -or $r.Tracker.LineVisitCount -ne 0) { throw 'invalid cursor accepted' }
            }
            foreach ($fault in @('sync','cancel','unhealthy','batch-cancel')) {
                $lines = [Collections.Generic.List[string]]::new()
                for ($i=0;$i -lt 4097;$i++) { $lines.Add('event=noise') }
                $r = Invoke-Case -Lines $lines.ToArray() -Fault $fault
                if (-not $r.Error) { throw "failure swallowed: $fault" }
                $expected = if ($fault -eq 'batch-cancel') {4096} else {0}
                if ($r.Tracker.LineVisitCount -ne $expected) { throw "failure cursor changed: $fault" }
                if ($fault -like '*cancel' -and $r.ErrorTypes -notcontains 'System.OperationCanceledException') { throw 'cancellation type lost' }
            }
            $r = Invoke-Case -Lines @($inbound,$outbound,'')
            if (-not $r.Error -or $r.Tracker.Cursor -ne 2) { throw 'empty tail error skipped' }
            if ($script:IsFile) {
                foreach ($fault in @('truncate','decoder')) {
                    $r = Invoke-Case -Lines @($inbound,$outbound,('event=noise; data=' + ('x'*70000))) -Fault $fault
                    if (-not $r.Error) { throw 'corrupt tail accepted after terminals' }
                    if ($fault -eq 'truncate' -and ($r.FailureCode -ne 'gui_smoke_log_cursor_gap' -or $r.Tracker.Cursor -ne 2)) { throw 'partial read state changed' }
                }
            }
            'observer-ok'
            """, host);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalObserver_TimesSynchronizationAndProjectionExclusively(string host)
    {
        await Check(Fixture(true) + """
            $unit=[long]([Diagnostics.Stopwatch]::Frequency/1000)
            $script:FakeTimes=[Collections.Generic.Queue[long]]::new()
            foreach($ms in @(0,2,2,5,5,7,7,10)){$script:FakeTimes.Enqueue($ms*$unit)}
            $r=Invoke-Case -Lines @('event=noise')
            if($r.Error -or $r.Tracker.SynchronizationElapsedTicks -ne 4*$unit -or
                $r.Tracker.ProjectionElapsedTicks -ne 6*$unit -or $r.Tracker.LineVisitCount -ne 1 -or $script:FakeTimes.Count -ne 0){throw 'update timing overlaps or replays'}
            'observer-ok'
            """, host);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalWait_ReportsExclusiveTimesAndPreservesFailures(string host)
    {
        var root = FindRepoRoot().Replace("'", "''", StringComparison.Ordinal);
        await Check(Fixture(false) + $$"""
            . '{{root}}/tools/FileTransferOps/GuiTimingDiagnostics.ps1'
            foreach($name in @('Assert-TunaGuiTerminalEvidenceTrackerScope','Wait-FileTransferTerminalPairAfterBookmark')){
                $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
                . ([scriptblock]::Create($node.Extent.Text.Replace('[System.Diagnostics.Stopwatch]::StartNew()','$script:Clock').Replace('[Diagnostics.Stopwatch]::GetTimestamp()','$script:Now')))
            }
            $unit=[long]([Diagnostics.Stopwatch]::Frequency/1000)
            $script:Clock=[pscustomobject]@{}
            $script:Clock | Add-Member ScriptProperty ElapsedTicks {$script:Now}
            $script:Clock | Add-Member ScriptProperty ElapsedMilliseconds {[long]($script:Now/$unit)}
            function Assert-GuiSmokeScenarioDeadline {}
            function Update-TunaGuiTerminalEvidenceTracker {
                param($Tracker)
                if($script:Failure -eq 'update'){throw [OperationCanceledException]::new('synthetic_update_cancel')}
                $script:Polls++
                $Tracker.SynchronizationElapsedTicks+=2*$unit; $Tracker.ProjectionElapsedTicks+=3*$unit
                $Tracker.LineVisitCount+=2; $Tracker.ParsedEventCount++
                if($script:Failure -notin @('quiet-grace','tail-grace')){$Tracker.ProgressEventCount++}
                if($script:Failure -eq 'tail-grace'){$Tracker.MaxReceiverHighestChunkIndex.Value=0}
                $script:Now+=5*$unit
                $terminalPoll=if($script:Failure -eq 'tail-grace'){24}elseif($script:Failure -eq 'quiet-grace'){3}else{2}
                if($script:Polls -eq $terminalPoll){
                    $state=if($script:Failure -eq 'failed-terminal'){'Failed'}else{'Completed'}
                    $Tracker.ByTransfer['t']=@{TransferId='t';Inbound=@{state=$state;error_code='(none)';saved_path='(none)'};
                        Outbound=@{state=$state;error_code='(none)'};OutboundLine='selected-terminal';ResolvedSavedPath=''}
                }
                if($script:Failure -eq 'invalid-timing'){$Tracker.SynchronizationElapsedTicks+=1000*$unit}
            }
            function Start-Sleep {param([int]$Milliseconds) if($Milliseconds -ne 500){throw 'poll changed'};$script:Now+=503*$unit}
            function Resolve-FileTransferLiveReceivedFilePath {
                if($script:Failure -eq 'path'){throw 'synthetic_path_failure'}
                $script:Now+=7*$unit
                return 'synthetic-resolved-path'
            }
            foreach($failure in @('','quiet-grace','tail-grace','invalid-timing','failed-terminal','update','path','timeout','scope')){
                $script:Failure=$failure;$script:Now=0L;$script:Polls=0
                $tracker=New-TunaGuiTerminalEvidenceTracker -Bookmark 0 -ExpectedFileName 'p' -ExpectedSizeBytes 10
                $tracker.SynchronizationElapsedTicks=100*$unit;$tracker.ProjectionElapsedTicks=200*$unit
                $tracker.LineVisitCount=30;$tracker.ParsedEventCount=20
                $result=$null;$errorText=''
                try{
                    $timeout=if($failure -eq 'timeout'){1}else{20000}
                    $progressTimeout=if($failure -in @('quiet-grace','tail-grace')){1}else{10000}
                    $bookmark=if($failure -eq 'scope'){1}else{0}
                    $result=Wait-FileTransferTerminalPairAfterBookmark -Bookmark $bookmark -TimeoutMs $timeout -ProgressTimeoutMs $progressTimeout -ExpectedFileName 'p' -ExpectedSizeBytes 10 -EvidenceTracker $tracker
                }catch{$errorText=$_.Exception.Message}
                if($failure -in @('update','path','timeout','scope')){
                    if($result -or -not $errorText){throw 'failure became success'}
                    continue
                }
                if($errorText -or $result.OutboundLine -cne 'selected-terminal' -or $result.ResolvedSavedPath -cne 'synthetic-resolved-path'){throw 'terminal result changed'}
                $d=$result.TimingDiagnostics
                if($failure -eq 'failed-terminal' -and $result.Inbound.state -ne 'Failed'){throw 'diagnostic terminalized failure'}
                if($failure -eq 'invalid-timing'){
                    if($d.status -ne 'unavailable' -or $null -ne $d.elapsedMs){throw 'invalid timing manufactured duration'}
                }else{
                    $polls=if($failure -eq 'tail-grace'){24}elseif($failure -eq 'quiet-grace'){3}else{2}
                    $sleeps=($polls-1)*503
                    if(-not $d.reportingOnly -or $d.status -ne 'available' -or $d.elapsedMs -ne ($polls*5+$sleeps+7) -or
                        $d.synchronizationMs -ne $polls*2 -or $d.projectionMs -ne $polls*3 -or $d.pathResolutionMs -ne 7 -or
                        $d.pollSleepMs -ne $sleeps -or $d.otherMs -ne 0 -or $d.pollCount -ne $polls -or
                        $d.lineVisitCount -ne $polls*2 -or $d.parsedEventCount -ne $polls){throw ($d|ConvertTo-Json -Compress)}
                }
            }
            $bad=Get-GuiSmokeTerminalWaitTimingDiagnostic -ElapsedTicks 10 -ProjectionTicks -1
            if($bad.status -ne 'unavailable' -or $null -ne $bad.projectionMs){throw 'negative timing accepted'}
            $residual=Get-GuiSmokeTerminalWaitTimingDiagnostic -ElapsedTicks (20*$unit) -ProjectionTicks (10*$unit)
            if($residual.otherMs -ne 10 -or $residual.elapsedMs -ne 20){throw 'residual bookkeeping lost'}
            'observer-ok'
            """, host);
    }

    private static async Task Check(string script, string host)
    {
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>(), host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("observer-ok", result.Stdout, StringComparison.Ordinal);
    }

    private static string Fixture(bool file)
    {
        var root = FindRepoRoot().Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference='Stop'
            Set-StrictMode -Version Latest
            . '{{root}}/tools/GuiSmokeLogLedger.ps1'
            $script:IsFile={{(file ? "$true" : "$false")}}
            $script:GuiSmokeTerminalEvidenceGeneration=0L
            $script:FakeTimes=$null
            function Get-ProbeTimestamp {
                if($null -ne $script:FakeTimes){return $script:FakeTimes.Dequeue()}
                return [Diagnostics.Stopwatch]::GetTimestamp()
            }
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{root}}/tools/GuiSmoke-Windows.ps1',[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'source parse errors'}
            foreach($name in @('ConvertFrom-GuiSmokeSemicolonFields','Get-GuiSmokeFieldValue','ConvertTo-GuiSmokeInt',
                'Get-GuiSmokeInt64FieldValue','Test-GuiSmokeFieldEquals','Get-GuiSmokeFileTransferTerminalDirection',
                'Set-GuiSmokeFileTransferTerminalDefaults','Test-TunaGuiPeerVisibleFileTunaV4DataPathLine',
                'Get-FileTransferLiveProgressScore','Assert-TunaGuiTerminalEvidenceLine',
                'New-TunaGuiTerminalEvidenceTracker','Update-TunaGuiTerminalEvidenceTracker')) {
                $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
                . ([scriptblock]::Create($node.Extent.Text.Replace('$reader.ReadLine()', '$script:Reads.Add($lineIndex); $reader.ReadLine()').Replace('[Diagnostics.Stopwatch]::GetTimestamp()', '(Get-ProbeTimestamp)')))
            }
            $originalVisitor=(Get-Command Invoke-GuiSmokeLogLedgerRangeVisitor).ScriptBlock
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State,[int]$StartIndex,[int]$EndIndex,[scriptblock]$Visitor)
                $script:Callbacks += $EndIndex-$StartIndex
                & $originalVisitor -State $State -StartIndex $StartIndex -EndIndex $EndIndex -Visitor $Visitor
            }
            function Assert-GuiSmokeScenarioDeadline {
                if($script:Fault -eq 'cancel' -or ($script:Fault -eq 'batch-cancel' -and $script:Tracker.Cursor -ge 4096)) {throw [OperationCanceledException]::new('synthetic_deadline')}
            }
            function Sync-GuiSmokeAppLogLedger {if($script:Fault -eq 'sync'){throw 'synthetic_sync_failure'} }
            $originalCount=(Get-Command Get-GuiSmokeLogLedgerLineCount).ScriptBlock
            function Get-GuiSmokeLogLedgerLineCount {
                param($State)
                $count=& $originalCount -State $State
                if($script:Fault -eq 'late-append' -and -not $script:Appended) {
                    $script:Appended=$true
                    $line='event=filetransfer_binary_frame_sent; frame_type=chunk_data; raw_chunk_bytes=99'
                    if($script:IsFile){[IO.File]::AppendAllText($script:Path,$line+"`n",$utf8)}else{$State.Lines.Add($line)}
                }
                return $count
            }
            $utf8=[Text.UTF8Encoding]::new($false)
            $peer='event=filetransfer_tuna_data_path_peer_visible; route=file_tuna_v4; target_route=file_tuna_v4; target_transport=tuna; actual_transport=tuna; protocol_version=4; target_protocol_version=4; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; payload_bytes=524288'
            $inbound='event=file_transfer_inbound_terminal; transfer_id=t; direction=inbound; role=helper; state=Completed; error_code=(none)'
            $outbound='event=transfer_terminal; transfer_id=t; direction=outbound; role=helpee; reason=Transfer complete.; error_code=(none)'
            function Invoke-Case {
                param([string[]]$Lines,[int]$Bookmark=0,[int]$EndBookmark=-1,[string]$Fault='',[string]$Append='')
                $script:Fault=''; $script:Appended=$false; $script:Callbacks=0L; $script:Reads=[Collections.Generic.List[int]]::new()
                $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
                $script:Path=Join-Path $PSScriptRoot ([guid]::NewGuid().ToString('N')+'.log')
                if($script:IsFile){
                    Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $script:Path -MaximumBytes 67108864
                    [IO.File]::WriteAllLines($script:Path,$Lines,$utf8)
                    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger
                    if($Fault -eq 'truncate'){[IO.File]::WriteAllLines($script:Path,@($inbound,$outbound),$utf8)}
                    if($Fault -eq 'decoder'){$bytes=[IO.File]::ReadAllBytes($script:Path);$bytes[$bytes.Length-2]=255;[IO.File]::WriteAllBytes($script:Path,$bytes)}
                }else{foreach($line in $Lines){$script:GuiSmokeAppLogLedger.Lines.Add($line)} }
                $script:Fault=$Fault
                if($Fault -eq 'unhealthy'){Set-GuiSmokeLogLedgerFailure -State $script:GuiSmokeAppLogLedger -Code 'synthetic_health' -Detail 'fixture'}
                $script:Tracker=New-TunaGuiTerminalEvidenceTracker -Bookmark $Bookmark -ExpectedInboundRole helper -ExpectedOutboundRole helpee
                $errorText='';$errorTypes=[Collections.Generic.List[string]]::new()
                try{
                    [void](Update-TunaGuiTerminalEvidenceTracker -Tracker $script:Tracker -EndBookmark $EndBookmark)
                    if($Fault -ne 'late-append'){[void](Update-TunaGuiTerminalEvidenceTracker -Tracker $script:Tracker -EndBookmark $EndBookmark)}
                    if($Append){
                        if($script:IsFile){[IO.File]::AppendAllText($script:Path,$Append+"`n",$utf8);Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger}
                        else{$script:GuiSmokeAppLogLedger.Lines.Add($Append)}
                        [void](Update-TunaGuiTerminalEvidenceTracker -Tracker $script:Tracker)
                    }
                }catch{$errorText=$_.Exception.Message;$e=$_.Exception;while($e){$errorTypes.Add($e.GetType().FullName);$e=$e.InnerException} }
                if($script:IsFile){$exclusive=[IO.File]::Open($script:Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$exclusive.Dispose()}
                [pscustomobject]@{Tracker=$script:Tracker;Error=$errorText;ErrorTypes=$errorTypes.ToArray();Callbacks=$script:Callbacks;
                    ReadIndices=$script:Reads.ToArray();FailureCode=$script:GuiSmokeAppLogLedger.FailureCode}
            }

            """;
    }
}
