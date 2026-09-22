namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiRouteWaitScanTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RouteWait_ProductionWidthNoisePreservesBoundaryCursorAndAppend(bool appendOnly, bool rotate)
    {
        var script = Fixture(appendOnly) + $$"""
            $lines = [Collections.Generic.List[string]]::new()
            $lines.Add('event=old')
            $lines.Add('exact boundary without an event field')
            $width = '; telemetry=' + ('x' * 600)
            for ($i = 0; $i -lt 20000; $i++) { $lines.Add("event=unrelated_telemetry; index=$i$width") }
            $direct = 'event=filetransfer_live_route_epoch_started; route=file_tuna_v4; protocol_version=4; handoff_kind=NormalToTunaActivation; target_transport=tuna; live_route_epoch=2'
            $options = @{ Bookmark = 2; FallbackBookmark = 0; AfterLine = $lines[1] }
            $onPoll = {
                if ($script:PollCount -eq 2) {
                    if ({{(rotate ? "$true" : "$false")}}) {
                        [IO.File]::Move($script:SourcePath, $script:RotatedPath)
                        [IO.File]::WriteAllText($script:SourcePath, $direct + [Environment]::NewLine, $utf8)
                    }
                    else { [IO.File]::AppendAllText($script:SourcePath, $direct + [Environment]::NewLine, $utf8) }
                }
            }
            $baseline = Invoke-ScanCase -Lines $lines.ToArray() -Options $options -OnPoll $onPoll -Baseline
            $candidate = Invoke-ScanCase -Lines $lines.ToArray() -Options $options -OnPoll $onPoll
            Assert-ScanParity $baseline $candidate
            if ($candidate.Result -cne $direct -or $candidate.Polls -ne 2 -or
                $candidate.Ranges -cne '2:20002|0:20002|20002:20003|20002:20003' -or
                $candidate.VisitCount -ne 40004) {
                throw "boundary/cursor/append changed: result=$($candidate.Result) error=$($candidate.Error) polls=$($candidate.Polls) visits=$($candidate.VisitCount) ranges=$($candidate.Ranges) parsed=$($candidate.ParseCount)"
            }
            if ($baseline.ParseCount -ne 20001 -or $candidate.ParseCount -ne 1) {
                throw "unrelated lines still parsed: baseline=$($baseline.ParseCount) candidate=$($candidate.ParseCount)"
            }
            'production-width-scan-parity-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("production-width-scan-parity-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("receive")]
    [InlineData("peer-visible")]
    [InlineData("fallback")]
    [InlineData("fallback-projection")]
    [InlineData("recovered")]
    [InlineData("whitespace")]
    [InlineData("duplicate-event")]
    [InlineData("overwritten-event")]
    [InlineData("direct-preferred")]
    [InlineData("direct-prefix")]
    [InlineData("required-ignores-direct")]
    [InlineData("wrong-scope")]
    [InlineData("missing-frame")]
    [InlineData("stale-epoch")]
    [InlineData("wrong-exact-epoch")]
    [InlineData("authoritative-after-line")]
    public async Task RouteWait_PrefilterPreservesDirectAndCoordinatorProjectionSemantics(string shape)
    {
        var script = Fixture(appendOnly: true) + $$"""
            $route = 'file_tuna_v4'
            $protocol = 4
            $handoff = 'NormalToTunaActivation'
            $transport = 'tuna'
            if ('{{shape}}' -in @('fallback', 'fallback-projection', 'missing-frame')) {
                $route = 'post_tuna_fallback_v6'; $protocol = 6; $handoff = 'tuna_to_normal_fallback'; $transport = 'regular_nkn'
            }
            $scope = 'session_id=s1; transfer_id=t1; direction=outbound'
            $target = "$scope; route=$route; protocol_version=$protocol; runtime_generation=2; terminal_generation=0; leg_generation=2; route_publication_generation=2; route_commit_generation=1"
            $commit = "event=filetransfer_route_transition_committed; $scope; authority_id=a1; transition_generation=1; decision_status=Committed; projected_target_route=$route; target_protocol_version=$protocol; target_transport=$transport; handoff_kind=$handoff; target_runtime_generation=2; target_terminal_generation=0; target_leg_generation=2; target_route_publication_generation=2; target_route_commit_generation=1"
            $publication = "event=filetransfer_route_hint_authorized_publication_accepted; $target; live_route_epoch=2"
            $disposition = "event=filetransfer_route_transition_disposition_applied; $scope; authority_id=a1; transition_generation=1; disposition=committed"
            $frame = "event=filetransfer_route_transition_retained_frame_applied; $scope; authority_id=a1; transition_generation=1"
            $payloadEvent = switch ('{{shape}}') {
                'receive' { 'filetransfer_data_path_receive_result' }
                'peer-visible' { 'filetransfer_tuna_data_path_peer_visible' }
                default { 'filetransfer_data_path_send_result' }
            }
            $payload = "event=$payloadEvent; $target"
            if ('{{shape}}' -eq 'wrong-scope') { $payload = $payload.Replace('transfer_id=t1', 'transfer_id=other') }
            if ('{{shape}}' -eq 'missing-frame') { $frame = 'event=unrelated_frame' }
            $direct = "event=filetransfer_live_route_epoch_started; route=$route; protocol_version=$protocol; handoff_kind=$handoff; target_transport=$transport; live_route_epoch=2"
            $options = @{ Route = $route; ProtocolVersion = $protocol; HandoffKind = $handoff; TargetTransport = $transport }
            if ('{{shape}}' -notin @('direct-preferred', 'direct-prefix', 'fallback-projection', 'recovered')) { $options.RequireAuthoritativeProjection = $true }
            if ('{{shape}}' -eq 'stale-epoch') { $options.AfterLiveRouteEpoch = 2 }
            if ('{{shape}}' -eq 'wrong-exact-epoch') { $options.LiveRouteEpoch = 3 }
            if ('{{shape}}' -eq 'authoritative-after-line') { $options.AfterLine = 'gui_authoritative_projection=1' }
            $lines = [Collections.Generic.List[string]]::new()
            foreach ($record in @($commit, $publication, $disposition, $frame, $payload)) {
                $lines.Add('event=unrelated_telemetry; detail=noise')
                $line = $record
                if ('{{shape}}' -eq 'whitespace') { $line = $line.Replace('event=', 'EVENT = ') }
                if ('{{shape}}' -eq 'duplicate-event') { $line = 'event=unrelated; ' + $line }
                if ('{{shape}}' -eq 'overwritten-event') { $line += '; event=unrelated' }
                $lines.Add('[2026-09-22 07:28:00Z] [INFO] [FileTransferService] ' + $line)
            }
            if ('{{shape}}' -eq 'direct-prefix') { $direct = $direct.Replace('event=filetransfer_live_route_epoch_started;', 'event=filetransfer_live_route_epoch_started_suffix;') }
            if ('{{shape}}' -in @('direct-preferred', 'direct-prefix', 'required-ignores-direct')) { $lines.Insert(0, $direct) }
            if ('{{shape}}' -eq 'fallback-projection') { $lines.Add('event=unrelated_tail') }
            if ('{{shape}}' -eq 'recovered') {
                $options.EventName = 'filetransfer_live_route_epoch_recovered'
                $options.AfterLine = $direct
                $lines.Insert(0, $direct)
                $direct = $direct.Replace('filetransfer_live_route_epoch_started', 'filetransfer_live_route_epoch_recovered')
                $lines.Add($direct)
            }
            $baseline = Invoke-ScanCase -Lines $lines.ToArray() -Options $options -Baseline
            $candidate = Invoke-ScanCase -Lines $lines.ToArray() -Options $options
            Assert-ScanParity $baseline $candidate
            if ('{{shape}}' -in @('wrong-scope', 'missing-frame', 'stale-epoch', 'wrong-exact-epoch', 'overwritten-event')) {
                if ($candidate.Error -notlike 'Timed out waiting*' -or $null -ne $candidate.Result) { throw 'incomplete projection accepted' }
            }
            elseif ('{{shape}}' -in @('direct-preferred', 'direct-prefix', 'recovered')) {
                if ($candidate.Result -cne $direct) { throw 'direct selection precedence changed' }
            }
            elseif ($candidate.Result -notlike '*gui_authoritative_projection=1*') { throw 'complete projection lost' }
            'projection-scan-parity-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("projection-scan-parity-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task RouteWait_UnrelatedLinesRetainTimeoutAndCancellationChecks(bool appendOnly, string failure)
    {
        var script = Fixture(appendOnly) + $$"""
            $lines = @('event=noise', 'event=noise', 'event=noise',
                'event=filetransfer_live_route_epoch_started; route=file_tuna_v4; protocol_version=4; handoff_kind=NormalToTunaActivation; target_transport=tuna; live_route_epoch=2')
            $script:InjectedFailure = '{{failure}}'
            $baseline = Invoke-ScanCase -Lines $lines -Options @{} -Baseline
            $candidate = Invoke-ScanCase -Lines $lines -Options @{}
            Assert-ScanParity $baseline $candidate
            if ($candidate.VisitCount -ne 3 -or $null -ne $candidate.Result) { throw 'wait escaped failure on unrelated line' }
            if ('{{failure}}' -eq 'timeout' -and $candidate.Error -notlike 'Timed out waiting*') { throw 'timeout changed' }
            if ('{{failure}}' -eq 'cancel' -and $candidate.Error -ne 'injected cancellation') { throw 'cancellation changed' }
            'failure-scan-parity-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("failure-scan-parity-ok", result.Stdout, StringComparison.Ordinal);
    }

    private static string Fixture(bool appendOnly)
    {
        var root = FindRepoRoot().Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . '{{root}}/tools/GuiSmokeLogLedger.ps1'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{root}}/tools/GuiSmoke-Windows.ps1', [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) { throw ($errors | Out-String) }
            foreach ($name in @('ConvertFrom-GuiSmokeSemicolonFields', 'Get-GuiSmokeFieldValue', 'Test-GuiSmokeFieldEquals', 'ConvertTo-GuiSmokeInt', 'Wait-TunaGuiLiveRouteEpochEvidence')) {
                $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true)
                if ($null -eq $node) { throw "missing function $name" }
                # Deterministic elapsed time; all production budget checks remain in the extracted wait.
                $definition = $node.Extent.Text.Replace('[System.Diagnostics.Stopwatch]::StartNew()', '$script:Clock')
                . ([scriptblock]::Create($definition))
                if ($name -eq 'Wait-TunaGuiLiveRouteEpochEvidence') {
                    $guard = $node.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                        $n.Clauses[0].Item1.Extent.Text.Contains('$projectionEventPattern.IsMatch($line)') }, $true)
                    $baseline = $definition.Replace('function Wait-TunaGuiLiveRouteEpochEvidence', 'function Wait-UnfilteredRouteEvidence')
                    if ($null -ne $guard) { $baseline = $baseline.Replace($guard.Extent.Text, '') }
                    . ([scriptblock]::Create($baseline))
                }
            }
            $originalParser = (Get-Command ConvertFrom-GuiSmokeSemicolonFields).ScriptBlock
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $script:ParseCount++
                & $originalParser -Message $Message
            }
            $originalVisitor = (Get-Command Invoke-GuiSmokeLogLedgerRangeVisitor).ScriptBlock
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                $script:Ranges.Add("${StartIndex}:$EndIndex")
                $innerVisitor = $Visitor
                & $originalVisitor -State $State -StartIndex $StartIndex -EndIndex $EndIndex -Visitor {
                    param([string]$line, [int]$lineIndex)
                    $script:Visited.Add($lineIndex)
                    if ($script:Visited.Count -eq 3) {
                        if ($script:InjectedFailure -eq 'timeout') { $script:Clock.ElapsedMilliseconds = 2000 }
                        if ($script:InjectedFailure -eq 'cancel') { throw [OperationCanceledException]::new('injected cancellation') }
                    }
                    & $innerVisitor $line $lineIndex
                }
            }
            function Assert-GuiSmokeScenarioDeadline {}
            function Start-Sleep { param([int]$Milliseconds) $script:Clock.ElapsedMilliseconds += $Milliseconds }
            function Sync-GuiSmokeAppLogLedger {
                $paths = @($script:SourcePath)
                if ([IO.File]::Exists($script:RotatedPath)) { $paths = @($script:RotatedPath, $script:SourcePath) }
                Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths $paths
            }
            $utf8 = [Text.UTF8Encoding]::new($false)
            $script:InjectedFailure = ''
            function Invoke-ScanCase {
                param([string[]]$Lines, [hashtable]$Options, [scriptblock]$OnPoll = $null, [switch]$Baseline)
                $id = [guid]::NewGuid().ToString('N')
                $script:SourcePath = Join-Path $PSScriptRoot "$id.log"
                $script:RotatedPath = Join-Path $PSScriptRoot "$id.1.log"
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                if ({{(appendOnly ? "$true" : "$false")}}) {
                    Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $script:SourcePath -MaximumBytes 67108864
                }
                [IO.File]::WriteAllLines($script:SourcePath, $Lines, $utf8)
                $script:ParseCount = 0
                $script:PollCount = 0
                $script:Clock = [pscustomobject]@{ ElapsedMilliseconds = 0 }
                $script:Visited = [Collections.Generic.List[int]]::new()
                $script:Ranges = [Collections.Generic.List[string]]::new()
                $arguments = @{ Bookmark = 0; Route = 'file_tuna_v4'; ProtocolVersion = 4; HandoffKind = 'NormalToTunaActivation'
                    TargetTransport = 'tuna'; EventName = 'filetransfer_live_route_epoch_started'; Description = 'scan-parity'; TimeoutMs = 2000 }
                foreach ($entry in $Options.GetEnumerator()) { $arguments[$entry.Key] = $entry.Value }
                $pollAction = $OnPoll
                $arguments.OnPoll = { $script:PollCount++; if ($null -ne $pollAction) { & $pollAction } }
                $result = $null; $errorText = ''
                try {
                    if ($Baseline) { $result = Wait-UnfilteredRouteEvidence @arguments }
                    else { $result = Wait-TunaGuiLiveRouteEpochEvidence @arguments }
                }
                catch { $errorText = $_.Exception.Message }
                [pscustomobject]@{ Result = $result; Error = $errorText; ParseCount = $script:ParseCount
                    Polls = $script:PollCount; VisitCount = $script:Visited.Count; Visited = $script:Visited -join ','; Ranges = $script:Ranges -join '|' }
            }
            function Assert-ScanParity {
                param($Baseline, $Candidate)
                foreach ($name in @('Result', 'Error', 'Polls', 'Visited', 'Ranges')) {
                    if ($Baseline.$name -cne $Candidate.$name) { throw "scan parity failed for $name" }
                }
            }

            """;
    }
}
