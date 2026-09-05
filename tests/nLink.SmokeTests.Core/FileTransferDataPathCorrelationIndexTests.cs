namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "DataPathCorrelationIndex")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferDataPathCorrelationIndexTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task DataPathIndex_EmptySingletonAndUnknownPayloadPreserveCompleteIndex()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $empty = Compare-Index @()
            if ($empty.OrderedEvents.Count -ne 0 -or $empty.Statistics.build_visit_count -ne 0) { throw 'empty index changed' }
            $unknown = Event 1 'unknown_event_with_payload'
            $index = Compare-Index @($unknown)
            $key = 'session|transfer|chunk|10|12|512|2'
            Assert-References @($unknown) @($index.PayloadEvents[$key].ToArray()) 'unknown payload'
            if ($index.TerminalEventsByScope.Count -ne 0 -or $index.RejectedReceiveEventsByKey.Count -ne 0 -or
                $index.ReceiverTransitionsByWriterScope.Count -ne 0) { throw 'unknown event gained a special bucket' }
            foreach ($event in @(
                [pscustomobject]@{Sequence=2; LineNumber=2; EventName='no_fields'},
                [pscustomobject]@{Sequence=3; LineNumber=3; EventName='null_fields'; Fields=$null},
                [pscustomobject]@{Sequence=4; LineNumber=4; EventName='non_dictionary_fields'; Fields='text'})) {
                $index = Compare-Index @($event)
                if ($index.PayloadEvents.Count -ne 0) { throw 'missing fields created payload identity' }
            }
            """);
    }

    [Fact]
    public async Task DataPathIndex_MixedNamesKeepTerminalTransitionAndRejectedMembership()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @()
            $sequence = 0
            foreach ($name in @('file_transfer_outbound_terminal', 'file_transfer_inbound_terminal',
                'transfer_terminal', 'filetransfer_runtime_terminalized', 'filetransfer_route_transitioned',
                'filetransfer_data_path_receive_result', 'filetransfer_tuna_data_path_mismatch',
                'filetransfer_data_frame_ignored', 'filetransfer_data_path_send_result',
                'filetransfer_tuna_data_path_peer_visible', 'unknown_event_with_payload')) {
                $events += Event (++$sequence) $name
            }
            $index = Compare-Index $events
            Assert-References @($events[0..3]) @($index.TerminalEventsByScope['session|transfer'].ToArray()) 'all terminal names'
            Assert-References @($events[4]) @($index.ReceiverTransitionsByWriterScope['writer|session|transfer'].ToArray()) 'transition'
            $rejectedKey = 'writer|session|transfer|chunk|file|file_tuna_v4|4|regular_nkn|tuna|10|12'
            Assert-References @($events[5..7]) @($index.RejectedReceiveEventsByKey[$rejectedKey].ToArray()) 'three rejected names'
            Assert-References $events @($index.PayloadEvents['session|transfer|chunk|10|12|512|2'].ToArray()) 'all names payload'
            $events += @(
                (Event 20 'FILE_TRANSFER_OUTBOUND_TERMINAL'),
                (Event 21 'FILETRANSFER_ROUTE_TRANSITIONED'),
                (Event 22 'FILETRANSFER_DATA_PATH_RECEIVE_RESULT'),
                (Event 23 'filetransfer_route_transitioned' @{log_writer_id=''}),
                (Event 24 'transfer_terminal' @{session_id=''}))
            $null = Compare-Index $events
            """);
    }

    [Fact]
    public async Task DataPathIndex_CaseBlankMalformedAndNonKeyFieldsMatchBaseline()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @((Event 1 'filetransfer_data_path_receive_result'))
            $sequence = 1
            foreach ($field in @('session_id','transfer_id','frame_type','log_writer_id','lane','route',
                'protocol_version','actual_transport','target_transport')) {
                foreach ($value in @('', '   ', $null, 'OTHER')) {
                    $events += Event (++$sequence) 'filetransfer_data_path_receive_result' @{$field=$value}
                }
            }
            foreach ($field in @('raw_payload_bytes','batch_chunk_count')) {
                foreach ($value in @('bad', '9223372036854775808', '-1', '0', '', ' 2 ', '02', $null)) {
                    $events += Event (++$sequence) 'filetransfer_data_path_receive_result' @{$field=$value}
                }
            }
            $upper = Event (++$sequence) 'filetransfer_data_path_receive_result' @{
                session_id='SESSION'; transfer_id='TRANSFER'; frame_type='CHUNK'; log_writer_id='WRITER';
                lane='FILE'; route='FILE_TUNA_V4'; actual_transport='REGULAR_NKN'; target_transport='TUNA'
            }
            $nonKey = Event (++$sequence) 'filetransfer_data_path_receive_result' @{
                direction='inbound'; protocol_version='4'; live_route_epoch='99'; runtime_generation='99';
                terminal_generation='99'; leg_generation='99'; route_publication_generation='99';
                route_commit_generation='99'; delivery_transport='different'
            }
            $nonKey.TransferId = 'ignored-property-transfer'
            $padded = Event (++$sequence) 'filetransfer_data_path_receive_result' @{session_id=' SESSION '}
            $events += @($upper, $nonKey, $padded)
            foreach ($upperKeys in @($false, $true)) {
                $dictionaryEvent = Event (++$sequence) 'filetransfer_data_path_receive_result'
                $dictionary = New-Object System.Collections.Hashtable ([System.StringComparer]::Ordinal)
                foreach ($key in $dictionaryEvent.Fields.Keys) {
                    $dictionaryKey = if ($upperKeys) { $key.ToUpperInvariant() } else { $key }
                    $dictionary.Add($dictionaryKey, $dictionaryEvent.Fields[$key])
                }
                if (-not $upperKeys) { $dictionary.Add('SESSION_ID', 'ignored-uppercase-value') }
                $dictionaryEvent.Fields = $dictionary
                $events += $dictionaryEvent
            }
            $missingFrame = Event (++$sequence) 'filetransfer_data_path_receive_result'
            $missingFrame.Fields.Remove('frame_type')
            $events += $missingFrame
            $index = Compare-Index $events
            if (-not $index.PayloadEvents.ContainsKey(' session |transfer|chunk|10|12|512|2')) { throw 'scope whitespace was trimmed' }
            $key = Get-FileTransferFileTunaV4RejectedReceiveCorrelationKey -Event $events[0]
            if ((Get-FileTransferFileTunaV4RejectedReceiveCorrelationKey -Event $upper) -cne $key -or
                (Get-FileTransferFileTunaV4RejectedReceiveCorrelationKey -Event $nonKey) -cne $key) { throw 'case or non-key fixture changed' }
            """);
    }

    [Fact]
    public async Task DataPathIndex_RangeStartEndAndCountFallbacksRemainExact()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $variants = @(
                @{},
                @{first_start_chunk_index='50'; chunk_index='70-71'; last_end_chunk_exclusive='12'},
                @{start_chunk_index='-1'; first_start_chunk_index='10'},
                @{start_chunk_index='bad'; first_start_chunk_index='010'},
                @{start_chunk_index=''; first_start_chunk_index=''; chunk_index='10-11'; last_end_chunk_exclusive=''},
                @{start_chunk_index='-1'; first_start_chunk_index='-1'; chunk_index='10'; last_end_chunk_exclusive=''},
                @{start_chunk_index='bad'; first_start_chunk_index='bad'; chunk_index='10'; last_end_chunk_exclusive='bad'},
                @{last_end_chunk_exclusive='0'; chunk_index='10-15'; batch_chunk_count='2'},
                @{last_end_chunk_exclusive=''; chunk_index=''; batch_chunk_count='2'},
                @{last_end_chunk_exclusive='-1'; chunk_index=''; batch_chunk_count='0'; sent_chunk_count='3'},
                @{last_end_chunk_exclusive='bad'; chunk_index=''; batch_chunk_count='bad'; sent_chunk_count='0'; frame_chunk_count='4'},
                @{last_end_chunk_exclusive=''; chunk_index=''; batch_chunk_count='0'; sent_chunk_count='bad'; frame_chunk_count='0'},
                @{start_chunk_index='9223372036854775808'; first_start_chunk_index='10'},
                @{last_end_chunk_exclusive='9223372036854775808'; chunk_index='10-11'},
                @{start_chunk_index=''; first_start_chunk_index=''; chunk_index='bad'},
                @{start_chunk_index=''; first_start_chunk_index=''; chunk_index=' 10 '},
                @{start_chunk_index=''; first_start_chunk_index=''; chunk_index='-1'},
                @{start_chunk_index=''; first_start_chunk_index=''; chunk_index='12-10'; last_end_chunk_exclusive=''},
                @{last_end_chunk_exclusive='10'},
                @{start_chunk_index=' 010 '; last_end_chunk_exclusive=' 012 '; raw_payload_bytes='0512'; batch_chunk_count='02'},
                @{start_chunk_index='-9223372036854775808'; first_start_chunk_index='10'; last_end_chunk_exclusive='-9223372036854775808'},
                @{start_chunk_index='0'; last_end_chunk_exclusive=''; batch_chunk_count='9223372036854775807'},
                @{start_chunk_index='10'; last_end_chunk_exclusive=''; batch_chunk_count='9223372036854775807'},
                @{start_chunk_index=''; first_start_chunk_index=''; chunk_index='10-9223372036854775807'; last_end_chunk_exclusive=''},
                @{start_chunk_index='9223372036854775807'; last_end_chunk_exclusive=''; chunk_index=''; batch_chunk_count='2'},
                @{raw_payload_bytes='9223372036854775807'},
                @{start_chunk_index='+10'; last_end_chunk_exclusive='+12'})
            $events = @()
            $sequence = 0
            foreach ($fields in $variants) { $events += Event (++$sequence) 'filetransfer_data_path_receive_result' $fields }
            $index = Compare-Index $events
            $sentFallback = $events[9]; $frameFallback = $events[10]
            if ((Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $sentFallback) -ne 13 -or
                (Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $frameFallback) -ne 14) { throw 'count fallback fixture changed' }
            foreach ($event in @($sentFallback,$frameFallback)) {
                if (@(Get-FileTransferFileTunaV4DataPathIndexedRejectedReceiveEvents -Index $index -Event $event).Count -ne 1 -or
                    @(Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents -Index $index -Event $event).Count -ne 0) {
                    throw 'rejected-only range fallback membership changed'
                }
            }
            """);
    }

    [Fact]
    public async Task DataPathIndex_DuplicatesOrderTiesScopeAbaAndInvocationIsolation()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Event 10 'filetransfer_data_path_receive_result' @{log_writer_sequence='30'}
            $b = Event 10 'filetransfer_tuna_data_path_mismatch' @{log_writer_sequence='10'}
            $c = Event 10 'filetransfer_data_frame_ignored' @{log_writer_sequence='10'}
            $a.LineNumber = 3; $b.LineNumber = 1; $c.LineNumber = 1
            $routeA = Event 20 'filetransfer_route_transitioned'
            $routeB = Event 30 'filetransfer_route_transitioned' @{session_id='session-b'; transfer_id='transfer-b'; log_writer_id='writer-b'}
            $routeAgain = Event 40 'filetransfer_route_transitioned' @{live_route_epoch='3'}
            $terminal = Event 50 'transfer_terminal'
            $events = @($terminal, $c, $routeB, $a, $routeAgain, $b, $a, $routeA, $c)
            $first = Compare-Index $events
            [array]::Reverse($events)
            $null = Compare-Index $events
            $oldKeys = @($first.PayloadEvents.Keys)
            foreach ($event in @($a,$routeA,$terminal)) {
                $event.Fields.session_id='new-session'; $event.Fields.transfer_id='new-transfer'; $event.Fields.log_writer_id='new-writer'
            }
            $second = Compare-Index $events
            if ($first.PayloadEvents.ContainsKey('new-session|new-transfer|chunk|10|12|512|2') -or
                -not $second.PayloadEvents.ContainsKey('new-session|new-transfer|chunk|10|12|512|2')) { throw 'scope mutation leaked between invocations' }
            Assert-Text $oldKeys @($first.PayloadEvents.Keys) 'previous invocation keys'
            foreach ($event in @($a,$routeA,$terminal)) {
                $event.Fields.session_id='session'; $event.Fields.transfer_id='transfer'; $event.Fields.log_writer_id='writer'
            }
            $third = Compare-Index $events
            if ($third.PayloadEvents.ContainsKey('new-session|new-transfer|chunk|10|12|512|2')) { throw 'ABA scope retained stale bucket' }
            foreach ($property in @('PayloadEvents','TerminalEventsByScope','ReceiverTransitionsByWriterScope','RejectedReceiveEventsByKey')) {
                if ([object]::ReferenceEquals($first.$property, $third.$property)) { throw "shared invocation dictionary: $property" }
                foreach ($key in $third.$property.Keys) {
                    if ($first.$property.ContainsKey($key) -and [object]::ReferenceEquals($first.$property[$key], $third.$property[$key])) {
                        throw "shared invocation bucket: $property/$key"
                    }
                }
            }
            if ([object]::ReferenceEquals($first.Statistics, $third.Statistics)) { throw 'shared invocation statistics' }
            $null = Compare-Index @((Event 1 'fresh_unknown_event'))
            """);
    }

    [Fact]
    public async Task DataPathIndex_MixedBuildBoundsFieldExtractionAgainstFrozenBaseline()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @()
            $sequence = 0
            for ($i=0; $i -lt 12; $i++) {
                foreach ($name in @('filetransfer_data_path_send_result','filetransfer_data_path_receive_result',
                    'filetransfer_tuna_data_path_mismatch','filetransfer_data_frame_ignored','filetransfer_route_transitioned',
                    'transfer_terminal','unknown_event_with_payload')) {
                    $events += Event (++$sequence) $name @{start_chunk_index=[string]$i; last_end_chunk_exclusive=[string]($i+2)}
                }
                $events += [pscustomobject]@{Sequence=(++$sequence); LineNumber=$sequence; EventName='unrelated'; Fields=@{}}
            }
            $null = Compare-Index $events
            if ($script:ActualFieldReads -ge $script:BaselineFieldReads -or $script:ActualIntegerReads -gt $script:BaselineIntegerReads) {
                throw "build extraction was not bounded below baseline: fields=$script:ActualFieldReads/$script:BaselineFieldReads integers=$script:ActualIntegerReads/$script:BaselineIntegerReads"
            }
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body + "\nWrite-Output 'data-path-index-ok'", [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("data-path-index-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/DataPathIndexBaseline.ps1')
        $script:ReadField = (Get-Command Get-FileTransferEventField).ScriptBlock
        $script:ReadInteger = (Get-Command Get-FileTransferEventInt64Field).ScriptBlock
        $script:FieldReads = 0L; $script:IntegerReads = 0L
        function Get-FileTransferEventField($Event, [string]$Name, [string]$Default='') {
            $script:FieldReads++
            & $script:ReadField -Event $Event -Name $Name -Default $Default
        }
        function Get-FileTransferEventInt64Field($Event, [string]$Name, [long]$Default=0) {
            $script:IntegerReads++
            & $script:ReadInteger -Event $Event -Name $Name -Default $Default
        }
        function Event([long]$Sequence, [string]$Name, [hashtable]$Overrides=@{}) {
            $fields = @{
                session_id='session'; transfer_id='transfer'; frame_type='chunk'; log_writer_id='writer';
                log_writer_sequence=[string]$Sequence; lane='file'; route='file_tuna_v4'; protocol_version='4';
                actual_transport='regular_nkn'; target_transport='tuna'; start_chunk_index='10';
                last_end_chunk_exclusive='12'; raw_payload_bytes='512'; batch_chunk_count='2'
            }
            foreach ($key in $Overrides.Keys) { $fields[$key] = $Overrides[$key] }
            [pscustomobject]@{Sequence=$Sequence; LineNumber=$Sequence; EventName=$Name; Fields=$fields; TransferId=$fields.transfer_id}
        }
        function Assert-Text($Expected, $Actual, [string]$Label) {
            if ((ConvertTo-Json -InputObject $Expected -Depth 12 -Compress) -cne
                (ConvertTo-Json -InputObject $Actual -Depth 12 -Compress)) { throw "value changed: $Label" }
        }
        function Assert-References([AllowEmptyCollection()][object[]]$Expected, [AllowEmptyCollection()][object[]]$Actual, [string]$Label) {
            if ($Expected.Count -ne $Actual.Count) { throw "event count changed: $Label/$($Expected.Count)/$($Actual.Count)" }
            for ($i=0; $i -lt $Expected.Count; $i++) {
                if (-not [object]::ReferenceEquals($Expected[$i], $Actual[$i])) { throw "event reference/order changed: $Label/$i" }
            }
        }
        function Compare-Index([AllowEmptyCollection()][object[]]$Events) {
            $before = ConvertTo-Json -InputObject $Events -Depth 12 -Compress
            $fieldReferences = @($Events | ForEach-Object {
                $property = $_.PSObject.Properties['Fields']
                [pscustomobject]@{Event=$_; Fields=if ($null -eq $property) {$null} else {$property.Value}}
            })
            $script:FieldReads = 0L; $script:IntegerReads = 0L
            $expected = Invoke-DataPathIndexBaseline -Events $Events
            $script:BaselineFieldReads = $script:FieldReads; $script:BaselineIntegerReads = $script:IntegerReads
            $script:FieldReads = 0L; $script:IntegerReads = 0L
            $actual = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $Events
            $script:ActualFieldReads = $script:FieldReads; $script:ActualIntegerReads = $script:IntegerReads
            Assert-Text @($expected.PSObject.Properties.Name) @($actual.PSObject.Properties.Name) 'index schema'
            Assert-References $expected.OrderedEvents $actual.OrderedEvents 'ordered events'
            Assert-Text $expected.Statistics $actual.Statistics 'initial statistics'
            if ($actual.Statistics.build_visit_count -ne $Events.Count -or $actual.Statistics.bounded_lookup_visit_count -ne 0) {
                throw 'initial visit counts changed'
            }
            foreach ($property in @('PayloadEvents','TerminalEventsByScope','ReceiverTransitionsByWriterScope','RejectedReceiveEventsByKey')) {
                if ($expected.$property.GetType() -ne $actual.$property.GetType()) { throw "dictionary type changed: $property" }
                [string[]]$expectedKeys = @($expected.$property.Keys)
                [string[]]$actualKeys = @($actual.$property.Keys)
                [array]::Sort($expectedKeys, [System.StringComparer]::Ordinal)
                [array]::Sort($actualKeys, [System.StringComparer]::Ordinal)
                Assert-Text $expectedKeys $actualKeys "$property exact keys"
                foreach ($key in $expectedKeys) {
                    Assert-References @($expected.$property[$key].ToArray()) @($actual.$property[$key].ToArray()) "$property/$key"
                }
            }
            $queries = @($Events) + @(
                (Event 100000 'filetransfer_data_path_receive_result' @{session_id='missing'; log_writer_id='missing'}),
                (Event 100001 'filetransfer_data_path_receive_result' @{session_id=''; transfer_id=''; start_chunk_index='-1'}),
                (Event 100002 'filetransfer_data_path_receive_result' @{session_id='SESSION'; transfer_id='TRANSFER'; log_writer_id='WRITER'}))
            foreach ($query in $queries) {
                foreach ($lookup in @('PayloadEvents','TerminalEvents','ReceiverTransitions','RejectedReceiveEvents')) {
                    $command = "Get-FileTransferFileTunaV4DataPathIndexed$lookup"
                    $arguments = @{Event=$query}
                    $writers = if ($lookup -eq 'ReceiverTransitions') { @('writer', 'WRITER', 'missing', ' ') } else { @('unused') }
                    foreach ($writer in $writers) {
                        if ($lookup -eq 'ReceiverTransitions') { $arguments.WriterId = $writer }
                        $beforeVisits = $actual.Statistics.bounded_lookup_visit_count
                        $expectedResult = @(& $command -Index $expected @arguments)
                        $actualResult = @(& $command -Index $actual @arguments)
                        Assert-References $expectedResult $actualResult "$lookup query/$writer"
                        if ($actual.Statistics.bounded_lookup_visit_count -ne $beforeVisits + $actualResult.Count) {
                            throw "lookup counter changed: $lookup"
                        }
                        Assert-Text $expected.Statistics $actual.Statistics "$lookup statistics"
                    }
                }
            }
            if ((ConvertTo-Json -InputObject $Events -Depth 12 -Compress) -cne $before) { throw 'input event mutated' }
            foreach ($entry in $fieldReferences) {
                $property = $entry.Event.PSObject.Properties['Fields']
                if ($null -ne $property -and -not [object]::ReferenceEquals($entry.Fields, $property.Value)) { throw 'input fields replaced' }
            }
            return $actual
        }
        """;
}
