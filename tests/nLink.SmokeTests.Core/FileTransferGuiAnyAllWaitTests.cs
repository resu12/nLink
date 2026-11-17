namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiAnyAllWaitTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", false)]
    [InlineData("pwsh", true)]
    public async Task AnyAllWait_AppendOnlyReadsFullSnapshotWithoutMaterialization(string host, bool matchLast)
    {
        var script = Fixture(true) + $$"""
            $lines = [Collections.Generic.List[string]]::new()
            $match = 'event=filetransfer_route_selected; route=post_tuna_fallback_v6; protocol_version=6'
            if (-not {{(matchLast ? "$true" : "$false")}}) { $lines.Add($match) }
            for ($i=0; $i -lt 18000; $i++) { $lines.Add('event=payload; data=' + ('x'*600)) }
            if ({{(matchLast ? "$true" : "$false")}}) { $lines.Add($match) }
            $r = Invoke-Case -Lines $lines.ToArray() -Sets @(
                @('event=filetransfer_route_selected','route=post_tuna_fallback_v6','protocol_version=6'),
                @('event=filetransfer_protocol_negotiated','route=post_tuna_fallback_v6','protocol_version=6'))
            if ($r.Error -or $r.Result -cne $match -or $r.Polls -ne 1) { throw ('route result changed: ' + ($r | ConvertTo-Json -Depth 4)) }
            if ($r.Materialized -ne 0) { throw "full-slice materialization remains: $($r.Materialized)" }
            if ($r.ReadIndices.Count -ne 18001 -or $r.ReadIndices[0] -ne 0 -or $r.ReadIndices[18000] -ne 18000) {
                throw 'indexed tail was not fully read'
            }
            'full-snapshot-no-materialization-ok'
            """;
        await AssertScript(script, host, "full-snapshot-no-materialization-ok");
    }

    public static IEnumerable<object[]> MatchingCases()
    {
        foreach (var host in new[] { "powershell", "pwsh" })
        foreach (var file in new[] { false, true })
        foreach (var shape in new[] { "line-first", "flat-and", "nested-and", "literal", "case", "null-needle",
                     "empty-set", "numeric", "empty-first", "empty-outer", "subrange", "no-match" })
            yield return new object[] { host, file, shape };
    }

    [Theory]
    [MemberData(nameof(MatchingCases))]
    public async Task AnyAllWait_PreservesMatchingAndNormalization(string host, bool file, string shape)
    {
        var script = Fixture(file) + $$"""
            $options=@{Lines=@('MATCH'); Sets=@('MATCH')}; $expected='MATCH'; $reject=$false
            switch ('{{shape}}') {
                'line-first' { $options.Lines=@('second event','first event'); $options.Sets=@(@('first','event'),@('second','event')); $expected='second event' }
                'flat-and' { $options.Lines=@('MATCH','route','MATCH route'); $options.Sets=@('MATCH','route'); $expected='MATCH route' }
                'nested-and' { $options.Lines=@('MATCH','MATCH route'); $options.Sets=@(,@('MATCH','route')); $expected='MATCH route' }
                'literal' { $options.Lines=@('plain','[route]'); $options.Sets=@('[route]'); $expected='[route]' }
                'case' { $options.Lines=@('mAtCh'); $expected='mAtCh' }
                'null-needle' { $options.Lines=@('noise'); $options.Sets=@(,@($null)); $expected='noise' }
                'empty-set' { $options.Lines=@('noise'); $options.Sets=@(,@()); $expected='noise' }
                'numeric' { $options.Lines=@('value=42'); $options.Sets=@(42); $expected='value=42' }
                'empty-first' { $options.Lines=@('','MATCH'); $options.Sets=@(,''); $reject=$true }
                'empty-outer' { $options.Sets=@(); $reject=$true }
                'subrange' { $options.Lines=@('MATCH old','MATCH new'); $options.Bookmark=1; $expected='MATCH new' }
                'no-match' { $options.Lines=@('noise'); $reject=$true }
            }
            $r=Invoke-Case @options
            if ($reject) {
                if (-not $r.Error -or $null -ne $r.Result) { throw 'expected rejection' }
                if ('{{shape}}' -ne 'empty-outer' -and ($r.Polls -ne 2 -or $r.Error -cne 'Timed out waiting for app log evidence: synthetic')) { throw 'retry semantics changed' }
            }
            elseif ($r.Error -or $r.Result -cne $expected -or $r.Polls -ne 1) { throw ($r | ConvertTo-Json -Depth 4) }
            'matching-ok'
            """;
        await AssertScript(script, host, "matching-ok");
    }

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", false)]
    [InlineData("pwsh", true)]
    public async Task AnyAllWait_PreservesBookmarksPollingAndCancellation(string host, bool file)
    {
        var script = Fixture(file) + """
            foreach ($bookmark in @(-1,2)) {
                $r=Invoke-Case -Lines @('MATCH') -Bookmark $bookmark
                if ($r.FailureCode -cne 'gui_smoke_log_cursor_invalid_bookmark' -or
                    $r.FailureDetail -cne "bookmark=$bookmark; line_count=1" -or $r.Polls -ne 2 -or -not $r.Error) { throw 'bookmark diagnostics changed' }
            }
            foreach ($empty in @($false,$true)) {
                $r=if($empty){Invoke-Case -Lines @()}else{Invoke-Case -Lines @('MATCH') -Bookmark 1}
                if ($r.FailureCode -or $r.Polls -ne 2 -or -not $r.Error) { throw 'empty range changed' }
            }
            $r=Invoke-Case -Lines @('noise') -Append 'MATCH appended'
            if ($r.Result -cne 'MATCH appended' -or $r.Polls -ne 2 -or $r.Sleeps -cne '500') { throw 'next-poll append lost' }
            $r=Invoke-Case -Lines @('MATCH') -Fault 'transient'
            if ($r.Result -cne 'MATCH' -or $r.Polls -ne 2) { throw 'transient retry changed' }
            foreach ($fault in @('cancel-before','cancel-after','sync-cancel')) {
                $r=Invoke-Case -Lines @('MATCH') -Fault $fault
                if ($r.Error -notlike 'synthetic*' -or $r.ErrorType -cne 'System.OperationCanceledException' -or $r.Sleeps) { throw 'cancellation swallowed' }
            }
            $r=Invoke-Case -Lines @('MATCH') -Fault 'timeout-during-condition'
            if ($r.Error -or $r.Result -cne 'MATCH' -or $r.Deadlines -ne 2) { throw 'local timeout policy changed' }
            $r=Invoke-Case -Lines @('MATCH') -TimeoutMs 0
            if (-not $r.Error -or $r.Polls -ne 0 -or $r.Deadlines -ne 0) { throw 'zero timeout polled' }
            $r=Invoke-Case -Lines @('MATCH') -Fault 'unhealthy'
            if ($r.FailureCode -cne 'synthetic_existing_failure' -or $r.Result -or -not $r.Error) { throw 'unhealthy ledger admitted' }
            'boundaries-ok'
            """;
        await AssertScript(script, host, "boundaries-ok");
    }

    [Theory]
    [InlineData("powershell", "truncate")]
    [InlineData("powershell", "decoder")]
    [InlineData("pwsh", "truncate")]
    [InlineData("pwsh", "decoder")]
    public async Task AnyAllWait_MatchingPrefixCannotHideUnreadableTail(string host, string fault)
    {
        var script = Fixture(true) + $$"""
            $r=Invoke-Case -Lines @('MATCH',('x'*131072),'tail') -Fault '{{fault}}' -Direct
            if ($r.Result -or -not $r.Error) { throw 'bad tail returned matching prefix' }
            if ('{{fault}}' -eq 'truncate') {
                if ($r.FailureCode -cne 'gui_smoke_log_cursor_gap' -or $r.Error -notlike '*line_index=1; reason=append_only_range_short_read*') { throw 'short-read diagnostic changed' }
            }
            elseif ($r.ErrorTypes -notcontains 'System.Text.DecoderFallbackException') { throw ('strict decoder lost: ' + $r.Error) }
            'tail-rejected-handle-disposed-ok'
            """;
        await AssertScript(script, host, "tail-rejected-handle-disposed-ok");
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task AnyAllWait_AppendOnlyKeepsIndexedHorizonAndPartialLineBoundary(string host)
    {
        var script = Fixture(true) + """
            $r=Invoke-Case -Lines @('noise') -Fault 'append-after-horizon'
            if ($r.Result -cne 'MATCH late' -or $r.Polls -ne 2 -or $r.Horizons -cne '1,2') { throw 'snapshot horizon widened' }
            $r=Invoke-Case -Lines @('noise') -Fault 'partial-line'
            if ($r.Result -or -not $r.Error -or $r.Horizons -cne '1,1') { throw 'unindexed partial line admitted' }
            'horizon-ok'
            """;
        await AssertScript(script, host, "horizon-ok");
    }

    private static async Task AssertScript(string script, string host, string marker)
    {
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>(), host);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(marker, result.Stdout, StringComparison.Ordinal);
    }

    private static string Fixture(bool file)
    {
        var root = FindRepoRoot().Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference='Stop'
            Set-StrictMode -Version Latest
            . '{{root}}/tools/GuiSmokeLogLedger.ps1'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{root}}/tools/GuiSmoke-Windows.ps1',[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'parse errors'}
            foreach($name in @('Wait-Until','Get-AppLogLinesAfterBookmark','Wait-AppLogContainsAnyAllAfterBookmark')){
                $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
                if($null -eq $node){throw "missing $name"}
                . ([scriptblock]::Create($node.Extent.Text.Replace('[System.Diagnostics.Stopwatch]::StartNew()','$script:Clock')))
            }
            $lookup=Get-Command Find-GuiSmokeLogLedgerAnyAllAfterBookmark -ErrorAction SilentlyContinue
            if($lookup){
                # Instrument the production loop only in this process; no production test hooks.
                $body=$lookup.ScriptBlock.ToString().Replace('$line = $reader.ReadLine()', '$line = $reader.ReadLine(); $script:ReadIndices.Add($lineIndex)')
                Set-Item Function:Find-GuiSmokeLogLedgerAnyAllAfterBookmark ([scriptblock]::Create($body))
            }
            $rangeVisitor=(Get-Command Invoke-GuiSmokeLogLedgerRangeVisitor).ScriptBlock
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State,[int]$StartIndex,[int]$EndIndex,[scriptblock]$Visitor)
                $script:Materialized += $EndIndex-$StartIndex
                & $rangeVisitor -State $State -StartIndex $StartIndex -EndIndex $EndIndex -Visitor $Visitor
            }
            $script:OriginalLineCount=(Get-Command Get-GuiSmokeLogLedgerLineCount).ScriptBlock
            function Get-GuiSmokeLogLedgerLineCount {
                param($State)
                $count=& $script:OriginalLineCount -State $State
                if($script:ObserveHorizon){
                    $script:Horizons.Add($count)
                    if($script:Fault -eq 'append-after-horizon' -and $script:Polls -eq 1){[IO.File]::AppendAllText($script:Path,"MATCH late`n",$utf8)}
                    $script:ObserveHorizon=$false
                }
                return $count
            }
            function Assert-GuiSmokeScenarioDeadline {
                $script:Deadlines++
                if($script:Fault -eq 'cancel-before' -or ($script:Fault -eq 'cancel-after' -and $script:Deadlines -eq 2)){throw [OperationCanceledException]::new('synthetic deadline')}
            }
            function Start-Sleep {param([int]$Milliseconds) $script:Sleeps.Add($Milliseconds); $script:Clock.ElapsedMilliseconds += $Milliseconds}
            function Sync-GuiSmokeAppLogLedger {
                $script:Polls++
                if($script:Fault -eq 'sync-cancel'){throw [OperationCanceledException]::new('synthetic sync cancellation')}
                if($script:Fault -eq 'transient' -and $script:Polls -eq 1){throw 'synthetic transient'}
                if($script:Fault -eq 'timeout-during-condition'){$script:Clock.ElapsedMilliseconds=5000}
                if($script:Polls -eq 2 -and $script:Append){
                    if({{(file ? "$true" : "$false")}}){[IO.File]::AppendAllText($script:Path,$script:Append+"`n",$utf8)}
                    else{$script:GuiSmokeAppLogLedger.Lines.Add($script:Append)}
                }
                if({{(file ? "$true" : "$false")}}){Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger}
                if($script:Polls -eq 1){
                    switch($script:Fault){
                        'truncate' {[IO.File]::WriteAllText($script:Path,"MATCH`n",$utf8)}
                        'decoder' {$bytes=[IO.File]::ReadAllBytes($script:Path); $bytes[$bytes.Length-2]=255; [IO.File]::WriteAllBytes($script:Path,$bytes)}
                        'unhealthy' {Set-GuiSmokeLogLedgerFailure -State $script:GuiSmokeAppLogLedger -Code 'synthetic_existing_failure' -Detail 'preserve'}
                    }
                }
                $script:ObserveHorizon=$true
            }
            $utf8=[Text.UTF8Encoding]::new($false)
            function Invoke-Case {
                param([string[]]$Lines,[object[]]$Sets=@('MATCH'),[int]$Bookmark=0,[string]$Fault='',
                    [string]$Append='',[int]$TimeoutMs=1000,[switch]$Direct)
                $script:Fault=$Fault; $script:Append=$Append; $script:Polls=0; $script:Deadlines=0; $script:Materialized=0
                $script:ObserveHorizon=$false; $script:Clock=[pscustomobject]@{ElapsedMilliseconds=0}
                $script:ReadIndices=[Collections.Generic.List[int]]::new(); $script:Horizons=[Collections.Generic.List[int]]::new()
                $script:Sleeps=[Collections.Generic.List[int]]::new()
                $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
                $script:Path=Join-Path $PSScriptRoot ([guid]::NewGuid().ToString('N')+'.log')
                if({{(file ? "$true" : "$false")}}){
                    Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $script:Path -MaximumBytes 67108864
                    [IO.File]::WriteAllLines($script:Path,$Lines,$utf8)
                    if($Fault -eq 'partial-line'){[IO.File]::AppendAllText($script:Path,'MATCH partial',$utf8)}
                    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger
                }
                else{foreach($line in $Lines){$script:GuiSmokeAppLogLedger.Lines.Add($line)} }
                $value=$null; $errorText=''; $errorType=''; $errorTypes=[Collections.Generic.List[string]]::new()
                try{
                    if($Direct){
                        Sync-GuiSmokeAppLogLedger
                        $value=Find-GuiSmokeLogLedgerAnyAllAfterBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $Bookmark -NormalizedNeedleSets @(,$Sets)
                    }
                    else{$value=Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $Bookmark -NeedleSets $Sets -TimeoutMs $TimeoutMs -Description 'synthetic'}
                }
                catch{
                    $errorText=$_.Exception.Message; $errorType=$_.Exception.GetType().FullName; $exception=$_.Exception
                    while($exception){$errorTypes.Add($exception.GetType().FullName); $exception=$exception.InnerException}
                }
                if({{(file ? "$true" : "$false")}}){
                    # Exclusive open proves reader/stream disposal after success, timeout and failure.
                    $exclusive=[IO.File]::Open($script:Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
                    $exclusive.Dispose()
                }
                [pscustomobject]@{Result=$value; Error=$errorText; ErrorType=$errorType; ErrorTypes=$errorTypes.ToArray();
                    Polls=$script:Polls; Deadlines=$script:Deadlines; Sleeps=($script:Sleeps -join ',');
                    FailureCode=$script:GuiSmokeAppLogLedger.FailureCode; FailureDetail=$script:GuiSmokeAppLogLedger.FailureDetail;
                    Materialized=$script:Materialized; ReadIndices=$script:ReadIndices.ToArray(); Horizons=($script:Horizons -join ',')}
            }

            """;
    }
}
