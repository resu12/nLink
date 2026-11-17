namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiProgressSnapshotTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", false)]
    [InlineData("pwsh", true)]
    public async Task ProgressSnapshot_AppendOnlyRemovesCallbacksWithoutSkippingLines(string host, bool early)
    {
        var script = Fixture(true, early) + """
            $lines = [Collections.Generic.List[string]]::new()
            $lines.Add($peer)
            for ($i = 0; $i -lt 18000; $i++) { $lines.Add('event=noise; data=' + ('x'*600)) }
            $r = Invoke-Case -Lines $lines.ToArray()
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload' -or $r.Polls -ne 1) { throw 'gate result changed' }
            if ($r.Callbacks -ne 0) { throw "snapshot callbacks remain: $($r.Callbacks)" }
            if ($r.ReadIndices.Count -ne 18001 -or $r.ReadIndices[0] -ne 0 -or $r.ReadIndices[18000] -ne 18000) { throw 'indexed tail skipped' }
            if ($r.Result.AcceleratedPayloadBytes -ne 524288 -or $r.Result.ElapsedMs -ne 5000) { throw 'accounting or minimum elapsed changed' }
            'full-snapshot-no-callbacks-ok'
            """;
        await AssertScript(script, host, "full-snapshot-no-callbacks-ok");
    }

    public static IEnumerable<object[]> GateModes()
    {
        foreach (var host in new[] { "powershell", "pwsh" })
        foreach (var file in new[] { false, true })
        foreach (var early in new[] { false, true })
            yield return new object[] { host, file, early };
    }

    [Theory]
    [MemberData(nameof(GateModes))]
    public async Task ProgressSnapshot_PreservesDecisionsAccountingAndPolling(string host, bool file, bool early)
    {
        var script = Fixture(file, early) + """
            foreach ($lines in @(@($inbound,$outbound,$peer), @($peer,'event=noise',$inbound,$outbound))) {
                $r = Invoke-Case -Lines $lines
                if ($r.Error -or $r.Result.Kind -ne 'completed_before_fault') { throw 'terminal pair lost precedence' }
            }
            $r = Invoke-Case -Lines @($peer,$inbound,$outbound.Replace('transfer_id=t1','transfer_id=t2'))
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload') { throw 'mismatched pair terminalized' }
            $r = Invoke-Case -Lines @('event=filetransfer_v4_state_sent; bytes_committed=33554432',$peer)
            if ($r.Error -or $r.Result.Kind -ne 'progress' -or $r.Result.CommittedBytes -ne 33554432) { throw 'committed precedence changed' }
            $half = $peer.Replace('payload_bytes=524288','payload_bytes=262144')
            $r = Invoke-Case -Lines @($half,$half) -InitialMs 0
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload' -or $r.Result.AcceleratedPayloadBytes -ne 524288 -or
                $r.Polls -ne 6 -or $r.Sleeps -cne '25,25,25,25,25' -or $r.Result.ElapsedMs -ne 5000) { throw 'frames double counted or minimum wait changed' }
            if ($script:IsFile -and $r.ReadIndices.Count -ne 12) { throw 'original bookmark snapshot not re-read' }
            $r = Invoke-Case -Lines @('event=noise') -Append $peer
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload' -or $r.Polls -ne 2) { throw 'append lost' }
            $r = Invoke-Case -Lines @($peer.Replace('payload_bytes=524288','payload_bytes=16383'))
            if ($r.Result -or $r.Error -notlike 'Timed out waiting*') { throw 'small frame admitted' }
            if ($script:IsEarly) {
                $r = Invoke-Case -Lines @($inbound,$outbound,$earlyLine,$peer)
                if ($r.Error -or $r.Result.Kind -ne 'early_fallback' -or $r.Result.AcceleratedPayloadBytes -ne 0) { throw 'early-fallback ordering changed' }
            }
            if (-not $script:IsFile) {
                $r = Invoke-Case -Lines @('noise1','noise2','noise3',$half) -ShrinkTo $half -InitialMs 0
                if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload' -or $r.Result.AcceleratedPayloadBytes -ne 524288) { throw 'rolling-memory shrink compatibility changed' }
            }
            'decisions-accounting-polling-ok'
            """;
        await AssertScript(script, host, "decisions-accounting-polling-ok");
    }

    [Theory]
    [MemberData(nameof(GateModes))]
    public async Task ProgressSnapshot_PreservesBoundariesAndFailurePropagation(string host, bool file, bool early)
    {
        var script = Fixture(file, early) + """
            foreach ($bookmark in @(-1,2)) {
                $r = Invoke-Case -Lines @($peer) -Bookmark $bookmark
                if (-not $r.Error -or $r.Result -or $r.Polls -ne 1 -or $r.Sleeps -or
                    $r.FailureCode -cne 'gui_smoke_log_cursor_invalid_bookmark' -or
                    $r.FailureDetail -cne "bookmark=$bookmark; line_count=1") { throw 'invalid bookmark not preserved' }
            }
            foreach ($options in @(@{Lines=@()}, @{Lines=@($peer); Bookmark=1})) {
                $r = Invoke-Case @options
                if ($r.Result -or $r.Error -notlike 'Timed out waiting*' -or $r.Polls -ne 3 -or $r.FailureCode) { throw 'empty snapshot changed' }
            }
            $r = Invoke-Case -Lines @($inbound,$outbound,$peer) -Bookmark 2
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload') { throw 'bookmark scope widened' }
            foreach ($fault in @('sync-cancel','sync-failure','unhealthy')) {
                $r = Invoke-Case -Lines @($peer) -Fault $fault
                if ($r.Result -or -not $r.Error -or $r.Polls -ne 1 -or $r.Sleeps) { throw 'source failure swallowed' }
                if ($fault -eq 'sync-cancel' -and $r.ErrorTypes -notcontains 'System.OperationCanceledException') { throw 'cancellation changed' }
                if ($fault -eq 'sync-failure' -and $r.Error -cne 'synthetic_sync_failure') { throw 'sync error changed' }
                if ($fault -eq 'unhealthy' -and $r.FailureCode -cne 'synthetic_existing_failure') { throw 'ledger health ignored' }
            }
            $r = Invoke-Case -Lines @($peer) -TimeoutMs 0
            if ($r.Result -or $r.Error -notlike 'Timed out waiting*' -or $r.Polls -ne 0) { throw 'zero timeout polled' }
            $r = Invoke-Case -Lines @('',('unicode=' + [char]0x03bb),'tail') -Direct
            if ($r.Error -or $r.Result.Count -ne 3 -or $r.Result[0] -cne '' -or $r.Result[1] -cne ('unicode=' + [char]0x03bb) -or $r.Result[2] -cne 'tail') { throw 'snapshot strings/order changed' }
            'boundaries-failures-ok'
            """;
        await AssertScript(script, host, "boundaries-failures-ok");
    }

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", false)]
    [InlineData("pwsh", true)]
    public async Task ProgressSnapshot_RejectsUnreadableTailAndKeepsIndexedHorizon(string host, bool early)
    {
        var script = Fixture(true, early) + """
            foreach ($fault in @('truncate','decoder')) {
                $prefix = if ($script:IsEarly) { $earlyLine } else { $peer }
                $r = Invoke-Case -Lines @($prefix,('x'*131072),'tail') -Fault $fault
                if ($r.Result -or -not $r.Error -or $r.Polls -ne 1 -or $r.Sleeps) { throw 'unreadable tail admitted progress/early fallback' }
                if ($fault -eq 'truncate' -and ($r.FailureCode -cne 'gui_smoke_log_cursor_gap' -or $r.Error -notlike '*line_index=1; reason=append_only_range_short_read*')) { throw 'short-read diagnostics changed' }
                if ($fault -eq 'decoder' -and $r.ErrorTypes -notcontains 'System.Text.DecoderFallbackException') { throw 'strict decoder lost' }
            }
            $r = Invoke-Case -Lines @('event=noise') -Fault 'append-after-horizon'
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload' -or $r.Polls -ne 2 -or $r.Horizons -cne '1,2') { throw 'horizon widened' }
            $r = Invoke-Case -Lines @('event=noise') -Fault 'partial-line'
            if ($r.Result -or $r.Error -notlike 'Timed out waiting*' -or $r.Horizons -cne '1,1,1') { throw 'unindexed partial line admitted' }
            $r = Invoke-Case -Lines @(('unicode=' + [char]0x03bb),$peer) -Bookmark 1 -Fault 'bom'
            if ($r.Error -or $r.Result.Kind -ne 'peer_visible_payload' -or $r.ReadIndices.Count -ne 1 -or $r.ReadIndices[0] -ne 1) { throw 'BOM/non-ASCII offset changed' }
            'tail-horizon-disposal-ok'
            """;
        await AssertScript(script, host, "tail-horizon-disposal-ok");
    }

    private static async Task AssertScript(string script, string host, string marker)
    {
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>(), host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(marker, result.Stdout, StringComparison.Ordinal);
    }

    private static string Fixture(bool file, bool early)
    {
        var root = FindRepoRoot().Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference='Stop'
            Set-StrictMode -Version Latest
            . '{{root}}/tools/GuiSmokeLogLedger.ps1'
            $script:IsFile={{(file ? "$true" : "$false")}}; $script:IsEarly={{(early ? "$true" : "$false")}}
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{root}}/tools/GuiSmoke-Windows.ps1',[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'parse errors'}
            foreach($name in @('Get-AppLogLinesAfterBookmark','ConvertFrom-GuiSmokeSemicolonFields','Get-GuiSmokeFieldValue',
                'ConvertTo-GuiSmokeInt','Get-GuiSmokeInt64FieldValue','Test-GuiSmokeFieldEquals','Get-GuiSmokeFileTransferTerminalDirection',
                'Set-GuiSmokeFileTransferTerminalDefaults','Test-TunaGuiPeerVisibleFileTunaV4DataPathLine','Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes',
                'Get-TunaGuiCompletedTerminalFromLogLine','Add-TunaGuiCompletedTerminalToTracker','New-TunaGuiLiveSwitchOffProgressGateResult',
                'Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault','Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault')){
                $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
                if($null -eq $node){throw "missing $name"}
                . ([scriptblock]::Create($node.Extent.Text.Replace('[System.Diagnostics.Stopwatch]::StartNew()','$script:Clock')))
            }
            $lookup=Get-Command Get-GuiSmokeLogLedgerProgressSnapshotAfterBookmark -ErrorAction SilentlyContinue
            if($lookup){
                # Observe every actual read without adding production test hooks.
                $body=$lookup.ScriptBlock.ToString().Replace('$line = $reader.ReadLine()', '$line = $reader.ReadLine(); $script:ReadIndices.Add($lineIndex)')
                Set-Item Function:Get-GuiSmokeLogLedgerProgressSnapshotAfterBookmark ([scriptblock]::Create($body))
            }
            $script:OriginalRangeVisitor=(Get-Command Invoke-GuiSmokeLogLedgerRangeVisitor).ScriptBlock
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State,[int]$StartIndex,[int]$EndIndex,[scriptblock]$Visitor)
                $script:Callbacks += $EndIndex-$StartIndex
                & $script:OriginalRangeVisitor -State $State -StartIndex $StartIndex -EndIndex $EndIndex -Visitor $Visitor
            }
            $script:OriginalLineCount=(Get-Command Get-GuiSmokeLogLedgerLineCount).ScriptBlock
            function Get-GuiSmokeLogLedgerLineCount {
                param($State)
                $count=& $script:OriginalLineCount -State $State
                if($script:ObserveHorizon){
                    $script:Horizons.Add($count); $script:ObserveHorizon=$false
                    if($script:Fault -eq 'append-after-horizon' -and $script:Polls -eq 1){[IO.File]::AppendAllText($script:Path,$peer+"`n",$utf8)}
                }
                return $count
            }
            function Start-Sleep {param([int]$Milliseconds) $script:Sleeps.Add($Milliseconds); $script:Clock.ElapsedMilliseconds += 1000L}
            function Sync-GuiSmokeAppLogLedger {
                $script:Polls++
                if($script:Fault -eq 'sync-cancel'){throw [OperationCanceledException]::new('synthetic sync cancellation')}
                if($script:Fault -eq 'sync-failure'){throw 'synthetic_sync_failure'}
                if($script:Polls -eq 2 -and $script:Append){
                    if($script:IsFile){[IO.File]::AppendAllText($script:Path,$script:Append+"`n",$utf8)}
                    else{$script:GuiSmokeAppLogLedger.Lines.Add($script:Append)}
                }
                if($script:Polls -eq 2 -and $script:ShrinkTo){$script:GuiSmokeAppLogLedger.Lines.Clear(); $script:GuiSmokeAppLogLedger.Lines.Add($script:ShrinkTo)}
                if($script:IsFile){Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger}
                if($script:Polls -eq 1){
                    switch($script:Fault){
                        'truncate' {[IO.File]::WriteAllText($script:Path,$script:FirstLine+"`n",$utf8)}
                        'decoder' {$bytes=[IO.File]::ReadAllBytes($script:Path); $bytes[$bytes.Length-2]=255; [IO.File]::WriteAllBytes($script:Path,$bytes)}
                        'unhealthy' {Set-GuiSmokeLogLedgerFailure -State $script:GuiSmokeAppLogLedger -Code 'synthetic_existing_failure' -Detail 'preserve'}
                    }
                }
                $script:ObserveHorizon=$true
            }
            $utf8=[Text.UTF8Encoding]::new($false)
            $peer='event=filetransfer_tuna_data_path_peer_visible; route=file_tuna_v4; target_route=file_tuna_v4; target_transport=tuna; actual_transport=tuna; protocol_version=4; target_protocol_version=4; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; payload_bytes=524288'
            $inbound='event=file_transfer_inbound_terminal; transfer_id=t1; state=Completed; error_code=(none)'
            $outbound='event=transfer_terminal; direction=outbound; transfer_id=t1; reason=Transfer complete.; error_code=(none)'
            $earlyLine='event=filetransfer_live_route_epoch_started; route=post_tuna_fallback_v6; protocol_version=6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn'
            function Invoke-Case {
                param([string[]]$Lines,[int]$Bookmark=0,[long]$InitialMs=5000,[int]$TimeoutMs=8000,
                    [string]$Append='',[string]$ShrinkTo='',[string]$Fault='',[switch]$Direct)
                $script:Fault=$Fault; $script:Append=$Append; $script:ShrinkTo=$ShrinkTo; $script:Polls=0; $script:Callbacks=0L
                $script:ObserveHorizon=$false; $script:Clock=[pscustomobject]@{ElapsedMilliseconds=$InitialMs}
                $script:ReadIndices=[Collections.Generic.List[int]]::new(); $script:Horizons=[Collections.Generic.List[int]]::new()
                $script:Sleeps=[Collections.Generic.List[int]]::new(); $script:FirstLine=if($Lines.Count){$Lines[0]}else{''}
                $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
                $script:Path=Join-Path $PSScriptRoot ([guid]::NewGuid().ToString('N')+'.log')
                if($script:IsFile){
                    Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $script:Path -MaximumBytes 67108864
                    $encoding=if($Fault -eq 'bom'){[Text.UTF8Encoding]::new($true)}else{$utf8}
                    [IO.File]::WriteAllLines($script:Path,$Lines,$encoding)
                    if($Fault -eq 'partial-line'){[IO.File]::AppendAllText($script:Path,$peer,$utf8)}
                    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger
                }
                else{foreach($line in $Lines){$script:GuiSmokeAppLogLedger.Lines.Add($line)} }
                $value=$null; $errorText=''; $errorTypes=[Collections.Generic.List[string]]::new()
                try{
                    if($Direct){Sync-GuiSmokeAppLogLedger; $value=@(Get-GuiSmokeLogLedgerProgressSnapshotAfterBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $Bookmark)}
                    else{
                        $gate=if($script:IsEarly){'Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault'}else{'Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault'}
                        $value=& $gate -Bookmark $Bookmark -TimeoutMs $TimeoutMs -MinimumElapsedMs 5000 -MinimumCommittedBytes 33554432 -MinimumPeerVisiblePayloadBytes 524288 -MinimumFramePayloadBytes 16384 -PollIntervalMs 25
                    }
                }
                catch{$errorText=$_.Exception.Message; $exception=$_.Exception; while($exception){$errorTypes.Add($exception.GetType().FullName); $exception=$exception.InnerException} }
                if($script:IsFile){
                    # Exclusive open verifies disposal after success, timeout and corrupt-tail failure.
                    $exclusive=[IO.File]::Open($script:Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $exclusive.Dispose()
                }
                [pscustomobject]@{Result=$value; Error=$errorText; ErrorTypes=$errorTypes.ToArray(); Polls=$script:Polls; Sleeps=($script:Sleeps -join ',');
                    FailureCode=$script:GuiSmokeAppLogLedger.FailureCode; FailureDetail=$script:GuiSmokeAppLogLedger.FailureDetail;
                    Callbacks=$script:Callbacks; ReadIndices=$script:ReadIndices.ToArray(); Horizons=($script:Horizons -join ',')}
            }

            """;
    }
}
