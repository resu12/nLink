namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiRejectedIngressScriptTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuiSmokeFileTunaDataPath_InitialOnlySelectionContract(bool conflictingDuplicateOnly)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var guiPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1");
        var guiText = await File.ReadAllTextAsync(guiPath);
        var catalogStart = guiText.IndexOf("    $earlyPredicateEventNames = @{", StringComparison.Ordinal);
        var catalogEnd = guiText.IndexOf("    function Test-TunaGuiLogLineHasEvent", catalogStart, StringComparison.Ordinal);
        var indexStart = guiText.IndexOf("    $earlyEvidenceLineVisitCount = 0L", StringComparison.Ordinal);
        var indexEnd = guiText.IndexOf("    $responderPeerProbeFastPath =", indexStart, StringComparison.Ordinal);
        Assert.True(catalogStart >= 0 && catalogEnd > catalogStart && indexStart >= 0 && indexEnd > indexStart);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $tokens = $null; $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{guiPath.Replace("'", "''", StringComparison.Ordinal)}}', [ref]$tokens, [ref]$parseErrors)
            if (@($parseErrors).Count -gt 0) { throw 'GUI script must parse before extracting the bounded contract.' }
            foreach ($name in @('Get-TunaGuiLogLineFieldValue', 'Get-TunaGuiLogLineEventNameFast', 'Get-TunaGuiFileTunaV4DataPathMismatchFailure')) {
                $node = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))[0]
                Invoke-Expression $node.Extent.Text
            }
            function Select-DataPathLines {
                param([AllowEmptyCollection()][string[]]$evidenceLines)
                {{guiText[catalogStart..catalogEnd]}}
                {{guiText[indexStart..indexEnd]}}
                return $earlyPredicateLines.FileTunaDataPath.ToArray()
            }
            {{guiText[catalogStart..catalogEnd]}}
            foreach ($required in @('filetransfer_data_path_send_result', 'filetransfer_data_path_receive_result',
                    'filetransfer_tuna_data_path_mismatch', 'filetransfer_data_frame_ignored', 'filetransfer_route_transitioned',
                    'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal', 'transfer_terminal', 'filetransfer_runtime_terminalized')) {
                if ($required -notin $earlyPredicateEventNames.FileTunaDataPath) { throw "Missing exact GUI selector event: $required" }
            }
            foreach ($alias in @('filetransfer_data_path_transport_mismatch', 'filetransfer_data_path_frame_ignored')) {
                if ($alias -in $earlyPredicateEventNames.FileTunaDataPath) { throw "Unexpected translated GUI selector event: $alias" }
            }
            if (@(Select-DataPathLines -evidenceLines @()).Count -ne 0) { throw 'Empty selector changed cardinality.' }
            $singleton = @(Select-DataPathLines -evidenceLines @('unrelated text', 'event=filetransfer_data_path_receive_result; marker=singleton'))
            if ($singleton.Count -ne 1 -or $singleton[0] -cne 'event=filetransfer_data_path_receive_result; marker=singleton; gui_evidence_sequence=2') {
                throw 'Singleton selector changed the original line or evidence sequence.'
            }

            $payload = 'session_id=sess; transfer_id=transfer; frame_type=filetransfer.chunk_batch.v4; lane=bulk; start_chunk_index=939; last_end_chunk_exclusive=942; raw_payload_bytes=64512; batch_chunk_count=3'
            $scope = 'live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; route_publication_generation=2; route_commit_generation=1'
            $source = "event=filetransfer_data_path_send_result; $payload; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; actual_transport=regular_nkn; delivery_transport=regular_nkn; delivery_result=sent; target_transport=regular_nkn; log_writer_id=sender; log_writer_sequence=1"
            $transition = "event=filetransfer_route_transitioned; session_id=sess; transfer_id=transfer; direction=inbound; route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; $scope; log_writer_id=helper; log_writer_sequence=2"
            $receive = "event=filetransfer_data_path_receive_result; $payload; direction=inbound; route=file_tuna_v4; protocol_version=4; actual_transport=regular_nkn; delivery_transport=regular_nkn; target_transport=tuna; $scope; log_writer_id=helper; log_writer_sequence=3"
            $mismatch = "event=filetransfer_tuna_data_path_mismatch; $payload; direction=inbound; route=file_tuna_v4; protocol_version=4; actual_transport=regular_nkn; delivery_transport=regular_nkn; target_transport=tuna; proof_direction=receive; mismatch_reason=file_tuna_v4_resolved_to_regular_nkn; $scope; log_writer_id=helper; log_writer_sequence=4"
            $ignore = 'event=filetransfer_data_frame_ignored; session_id=sess; transfer_id=transfer; frame_type=filetransfer.chunk_batch.v4; lane=bulk; chunk_index=939-941; route=file_tuna_v4; protocol_version=4; actual_transport=regular_nkn; target_transport=tuna; reason=file_tuna_v4_payload_requires_tuna; log_writer_id=helper; log_writer_sequence=17'
            $terminal = 'event=file_transfer_inbound_terminal; session_id=sess; transfer_id=transfer; terminal_state=completed; log_writer_id=helper; log_writer_sequence=18'
            $replacement = $transition.Replace('route=file_tuna_v4', 'route=post_tuna_fallback_v6').Replace('protocol_version=4', 'protocol_version=6').Replace('log_writer_sequence=2', 'log_writer_sequence=30')
            $base = @('event=tuna_dialer_sidecar_stderr; marker=prefix', $source, $transition, $receive, $mismatch)
            $base += @(for ($index=0; $index -lt 12; $index++) { "event=tuna_dialer_sidecar_stderr; marker=$index; log_writer_id=helper; log_writer_sequence=$($index+5)" })
            $base += @($ignore, $terminal)
            $positive = @('initial', 'no_telemetry', 'exact_duplicate', 'unrelated_transfer_route', 'unrelated_writer_route', 'unrelated_transfer_terminal', 'unrelated_writer_terminal')
            $cases = @($positive) + @('missing_source', 'source_after_transition', 'missing_transition', 'missing_mismatch', 'missing_ignore',
                'wrong_source_writer', 'wrong_source_session', 'wrong_source_transfer', 'wrong_source_bytes',
                'wrong_mismatch_writer', 'wrong_mismatch_session', 'wrong_mismatch_transfer', 'wrong_mismatch_range',
                'wrong_mismatch_bytes', 'wrong_mismatch_publication', 'wrong_mismatch_runtime', 'wrong_ignore_writer',
                'wrong_ignore_range', 'duplicate_mismatch', 'duplicate_ignore', 'second_cycle', 'second_cycle_with_receipt',
                'missing_predecessor', 'unknown_predecessor', 'wrong_transition_protocol', 'wrong_transition_route',
                'wrong_transition_direction', 'wrong_transition_publication', 'wrong_transition_runtime',
                'route_replacement', 'same_scope_replacement', 'route_aba', 'terminal_before_transition',
                'source_terminal_before_transition', 'terminal_before_receive', 'terminal_inside_chain', 'source_terminal_inside_chain', 'terminal_aba')
            if ({{(conflictingDuplicateOnly ? "$true" : "$false")}}) { $cases = @('conflicting_duplicate') }
            $results = [Collections.Generic.List[object]]::new()
            foreach ($case in $cases) {
                $lines = [Collections.Generic.List[string]]::new()
                foreach ($original in $base) {
                    $line = $original
                    if (($case -in @('missing_source', 'source_after_transition') -and $line -ceq $source) -or
                        ($case -ceq 'missing_transition' -and $line -ceq $transition) -or
                        ($case -ceq 'missing_mismatch' -and $line -ceq $mismatch) -or
                        ($case -ceq 'missing_ignore' -and $line -ceq $ignore) -or
                        ($case -ceq 'no_telemetry' -and $line -match 'event=tuna_dialer_sidecar_stderr')) { continue }
                    if ($line -ceq $source) {
                        switch ($case) {
                            'wrong_source_writer' { $line = $line.Replace('log_writer_id=sender', 'log_writer_id=helper') }
                            'wrong_source_session' { $line = $line.Replace('session_id=sess', 'session_id=foreign') }
                            'wrong_source_transfer' { $line = $line.Replace('transfer_id=transfer', 'transfer_id=foreign') }
                            'wrong_source_bytes' { $line = $line.Replace('raw_payload_bytes=64512', 'raw_payload_bytes=64511') }
                        }
                    }
                    if ($line -ceq $transition) {
                        if ($case -in @('terminal_before_transition', 'source_terminal_before_transition')) {
                            $poison = $terminal.Replace('log_writer_sequence=18', 'log_writer_sequence=20')
                            if ($case -ceq 'source_terminal_before_transition') { $poison = $poison.Replace('log_writer_id=helper', 'log_writer_id=sender') }
                            $lines.Add($poison)
                        }
                        switch ($case) {
                            { $_ -in @('second_cycle', 'second_cycle_with_receipt') } { $line = $line.Replace('previous_route=regular_nkn_v4_fast', 'previous_route=post_tuna_fallback_v6') }
                            'missing_predecessor' { $line = $line.Replace('previous_route=regular_nkn_v4_fast; ', '') }
                            'unknown_predecessor' { $line = $line.Replace('previous_route=regular_nkn_v4_fast', 'previous_route=unknown') }
                            'wrong_transition_protocol' { $line = $line.Replace('protocol_version=4', 'protocol_version=6') }
                            'wrong_transition_route' { $line = $line.Replace('; route=file_tuna_v4;', '; route=post_tuna_fallback_v6;') }
                            'wrong_transition_direction' { $line = $line.Replace('direction=inbound', 'direction=outbound') }
                            'wrong_transition_publication' { $line = $line.Replace('route_publication_generation=2', 'route_publication_generation=3') }
                            'wrong_transition_runtime' { $line = $line.Replace('runtime_generation=2', 'runtime_generation=3') }
                        }
                    }
                    if ($line -ceq $receive) {
                        switch ($case) {
                            'route_replacement' { $lines.Add($replacement) }
                            'same_scope_replacement' { $lines.Add($transition.Replace('previous_route=regular_nkn_v4_fast', 'previous_route=unknown').Replace('log_writer_sequence=2', 'log_writer_sequence=30')) }
                            'route_aba' { $lines.Add($replacement); $lines.Add($transition.Replace('log_writer_sequence=2', 'log_writer_sequence=31')) }
                            'terminal_before_receive' { $lines.Add($terminal.Replace('log_writer_sequence=18', 'log_writer_sequence=20')) }
                            'terminal_aba' { $lines.Add($terminal.Replace('log_writer_sequence=18', 'log_writer_sequence=20')); $lines.Add($transition.Replace('log_writer_sequence=2', 'log_writer_sequence=31')) }
                            'unrelated_transfer_route' { $lines.Add($replacement.Replace('transfer_id=transfer', 'transfer_id=foreign')) }
                            'unrelated_writer_route' { $lines.Add($replacement.Replace('log_writer_id=helper', 'log_writer_id=foreign')) }
                            'unrelated_transfer_terminal' { $lines.Add($terminal.Replace('transfer_id=transfer', 'transfer_id=foreign')) }
                            'unrelated_writer_terminal' { $lines.Add($terminal.Replace('log_writer_id=helper', 'log_writer_id=foreign')) }
                            'second_cycle_with_receipt' {
                                $lines.Add($source.Replace('route=regular_nkn_v4_fast', 'route=file_tuna_v4').Replace('transport=regular_nkn', 'transport=tuna').Replace('log_writer_sequence=1', 'log_writer_sequence=50'))
                                $lines.Add($receive.Replace('actual_transport=regular_nkn', 'actual_transport=tuna').Replace('delivery_transport=regular_nkn', 'delivery_transport=tuna').Replace('log_writer_sequence=3', 'log_writer_sequence=50'))
                            }
                        }
                    }
                    if ($line -ceq $mismatch) {
                        switch ($case) {
                            'wrong_mismatch_writer' { $line = $line.Replace('log_writer_id=helper', 'log_writer_id=foreign') }
                            'wrong_mismatch_session' { $line = $line.Replace('session_id=sess', 'session_id=foreign') }
                            'wrong_mismatch_transfer' { $line = $line.Replace('transfer_id=transfer', 'transfer_id=foreign') }
                            'wrong_mismatch_range' { $line = $line.Replace('start_chunk_index=939', 'start_chunk_index=942').Replace('last_end_chunk_exclusive=942', 'last_end_chunk_exclusive=945') }
                            'wrong_mismatch_bytes' { $line = $line.Replace('raw_payload_bytes=64512', 'raw_payload_bytes=64511') }
                            'wrong_mismatch_publication' { $line = $line.Replace('route_publication_generation=2', 'route_publication_generation=3') }
                            'wrong_mismatch_runtime' { $line = $line.Replace('runtime_generation=2', 'runtime_generation=3') }
                        }
                    }
                    if ($line -ceq $ignore) {
                        if ($case -ceq 'terminal_inside_chain') { $lines.Add($terminal.Replace('log_writer_sequence=18', 'log_writer_sequence=20')) }
                        if ($case -ceq 'source_terminal_inside_chain') { $lines.Add($terminal.Replace('log_writer_id=helper', 'log_writer_id=sender').Replace('log_writer_sequence=18', 'log_writer_sequence=20')) }
                        if ($case -ceq 'wrong_ignore_writer') { $line = $line.Replace('log_writer_id=helper', 'log_writer_id=foreign') }
                        if ($case -ceq 'wrong_ignore_range') { $line = $line.Replace('chunk_index=939-941', 'chunk_index=942-944') }
                    }
                    $lines.Add($line)
                    if ($original -ceq $transition -and $case -ceq 'source_after_transition') { $lines.Add($source) }
                    if ($original -ceq $mismatch) {
                        if ($case -ceq 'duplicate_mismatch') { $lines.Add($mismatch.Replace('log_writer_sequence=4', 'log_writer_sequence=19')) }
                        if ($case -ceq 'exact_duplicate') { $lines.Add($mismatch) }
                        if ($case -ceq 'conflicting_duplicate') { $lines.Add($mismatch.Replace('raw_payload_bytes=64512', 'raw_payload_bytes=64511')) }
                    }
                    if ($original -ceq $ignore -and $case -ceq 'duplicate_ignore') { $lines.Add($ignore.Replace('log_writer_sequence=17', 'log_writer_sequence=19')) }
                }
                $selected = @(Select-DataPathLines -evidenceLines $lines.ToArray())
                $unfiltered = @(for ($index=0; $index -lt $lines.Count; $index++) { "$($lines[$index]); gui_evidence_sequence=$($index+1)" })
                $originalCandidate = @($unfiltered | Where-Object { $_ -match 'event=filetransfer_data_path_receive_result;' -and $_ -match 'actual_transport=regular_nkn;' })[0]
                $expectedSequence = [long](Get-TunaGuiLogLineFieldValue -Line $originalCandidate -Name 'gui_evidence_sequence')
                $expectedRecovered = if ($case -in $positive) { 1 } else { 0 }
                $first = $null
                foreach ($inputLines in @($selected, $unfiltered)) {
                    $result = Get-TunaGuiFileTunaV4DataPathMismatchFailure -Lines $inputLines
                    if ($result.RawCount -ne 1 -or $result.DistinctCount -ne 1 -or $result.RecoveredCount -ne $expectedRecovered -or
                        $result.UnresolvedCount -ne (1-$expectedRecovered)) { throw "${case}: GUI quartet changed: $($result | ConvertTo-Json -Compress)" }
                    if ($expectedRecovered -eq 0 -and ($result.Line -cne $originalCandidate -or $result.FirstSequence -ne $expectedSequence)) {
                        throw "${case}: original unresolved candidate location changed"
                    }
                    $normalized = [pscustomobject]@{ Case=$case; Raw=$result.RawCount; Distinct=$result.DistinctCount;
                        Recovered=$result.RecoveredCount; Unresolved=$result.UnresolvedCount; FirstSequence=$result.FirstSequence; Line=$result.Line } | ConvertTo-Json -Compress
                    if ($null -ne $first -and $first -cne $normalized) { throw "${case}: selected/unfiltered GUI reducer results differ" }
                    $first = $normalized
                }
                $results.Add($first)
            }
            Write-Output ('gui-initial-only-contract=' + ($results.ToArray() | ConvertTo-Json -Compress))
            """;
        string? firstHost = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [], host);
            Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("gui-initial-only-contract=", StringComparison.Ordinal)));
            if (firstHost is not null) { Assert.Equal(firstHost, normalized); }
            firstHost = normalized;
        }
    }
}
