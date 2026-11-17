namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "BootstrapWriterMapping")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferBootstrapWriterMappingTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task BootstrapWriterMapping_EmptySingletonAndInheritedProofPreserveResults()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $result = Compare-Projection @() @() @()
            if ($result.Verdict -ne 'pass' -or $result.CandidateRecords.Count -ne 0) { throw 'empty projection changed' }
            $event = Event 'one' 'log_writer_id=writer; log_writer_sequence=1'
            $result = Compare-Projection @($event) @((Candidate 'writer' 1)) @()
            Assert-References @($event) @($result.CandidateEvents) 'singleton'
            $inherited = Record (Event 'snapshot' 'opaque snapshot line') $true
            $snapshot = Snapshot @($inherited)
            $result = Compare-Projection @() @() @() $snapshot
            Assert-References @($inherited) @($result.CandidateRecords) 'inherited record'
            Assert-References @($inherited.Event) @($result.RecoveredCandidateEvents) 'inherited event'
            Assert-References @($snapshot.AuthoritativeRecords) @($result.AuthoritativeRecords) 'authority records'
            """);
    }

    [Fact]
    public async Task BootstrapWriterMapping_RawCaseWhitespaceAndFieldsRemainDistinct()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $event = Event 'raw' "prefix=irrelevant;`tLOG_WRITER_ID=  WriTeR  ;`n log_writer_SEQUENCE=  42  ;payload=secret"
            $parsed = Candidate 'parsed-writer' 999
            $result = Compare-Projection @($event) @((Candidate 'writer' 42), $parsed) @()
            Assert-References @($event) @($result.CandidateEvents) 'raw identity not parsed fields'
            if ($result.RawCount -ne 2 -or $result.UnresolvedCount -ne 2) { throw 'unbound candidate counts changed' }
            $unicodeWriter = 'WR' + [char]0x00c9 + 'TER'
            $space = [char]0x00a0
            $unicode = Event 'unicode' "prefix=x;$space`nLOG_WRITER_ID=$space$unicodeWriter$space; log_writer_sequence=43"
            $result = Compare-Projection @($unicode) @((Candidate ($unicodeWriter.ToLowerInvariant()) 43)) @()
            Assert-References @($unicode) @($result.CandidateEvents) 'Unicode writer and whitespace'
            $sentinel = Event 'sentinel' 'log_writer_id=(none);log_writer_sequence=(none)'
            $result = Compare-Projection @($sentinel) @((Candidate '(none)' '(none)')) @()
            Assert-References @($sentinel) @($result.CandidateEvents) 'nonblank sentinel key remains literal'
            $null = Compare-Projection @((Event 'null' $null),(Event 'empty' ''),(Event 'blank' '   ')) @((Candidate 'writer' 42)) @()
            $missing = [pscustomobject]@{Id='missing'; Fields=@{log_writer_id='writer';log_writer_sequence='42'}}
            Assert-MissingRawFailure @($missing)
            """);
    }

    [Fact]
    public async Task BootstrapWriterMapping_MalformedDelimitersAndRepeatedFieldsKeepFirstMatch()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $invalid = @(
                (Event 'space-before-name' ' log_writer_id=writer; log_writer_sequence=1'),
                (Event 'space-before-equals' 'log_writer_id =writer; log_writer_sequence=1'),
                (Event 'comma' 'prefix=x,log_writer_id=writer; log_writer_sequence=1'),
                (Event 'blank-first-writer' 'log_writer_id= ; log_writer_id=writer;log_writer_sequence=1'),
                (Event 'blank-first-sequence' 'log_writer_id=writer;log_writer_sequence= ;log_writer_sequence=1'))
            $result = Compare-Projection $invalid @((Candidate 'writer' 1)) @()
            if ($result.CandidateEvents.Count -ne 0) { throw 'malformed delimiter or later repeated field admitted' }
            $event = Event 'first' 'LOG_WRITER_ID=First;log_writer_id=second;log_writer_sequence=7;LOG_WRITER_SEQUENCE=8'
            $result = Compare-Projection @($event) @((Candidate 'first' 7),(Candidate 'second' 8)) @()
            Assert-References @($event) @($result.CandidateEvents) 'first field wins'
            """);
    }

    [Fact]
    public async Task BootstrapWriterMapping_RawSequenceKeysAreNotNumericNormalized()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @(
                (Event 'leading-zero' 'log_writer_id=w;log_writer_sequence=007'),
                (Event 'signed' 'log_writer_id=w;log_writer_sequence=+7'),
                (Event 'word' 'log_writer_id=w;log_writer_sequence=Next'),
                (Event 'negative' 'log_writer_id=w;log_writer_sequence=-1'),
                (Event 'positive-one' 'log_writer_id=w;log_writer_sequence=+1'),
                (Event 'decimal' 'log_writer_id=w;log_writer_sequence=1.0'),
                (Event 'large' 'log_writer_id=w;log_writer_sequence=9223372036854775808'))
            $candidates = @((Candidate 'w' 7),(Candidate 'w' '007'),(Candidate 'w' '+7'),
                (Candidate 'W' 'next'),(Candidate 'w' -1),(Candidate 'w' '+1'),(Candidate 'w' '1.0'),
                (Candidate 'w' '9223372036854775808'))
            $result = Compare-Projection $events $candidates @()
            Assert-References $events @($result.CandidateEvents) 'opaque sequence key'
            """);
    }

    [Fact]
    public async Task BootstrapWriterMapping_DuplicateWriterSequenceUsesLastInputNotChronology()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $first = Event 'first' 'log_writer_id=WRITER;log_writer_sequence=9'
            $last = Event 'last' 'log_writer_id=writer;log_writer_sequence=9'
            $first.Sequence = 900
            $last.Sequence = 1
            $candidates = @((Candidate 'writer' 9),(Candidate 'WRITER' 9 $true))
            $result = Compare-Projection @($first,$last) $candidates @()
            Assert-References @($last,$last) @($result.CandidateEvents) 'last input wins'
            $result = Compare-Projection @($last,$first) $candidates @()
            Assert-References @($first,$first) @($result.CandidateEvents) 'reordered last input wins'
            """);
    }

    [Fact]
    public async Task BootstrapWriterMapping_MixedConsumersPreserveQuartetsOrderAndReferences()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Event 'a' 'log_writer_id=w;log_writer_sequence=1'
            $b = Event 'b' 'log_writer_id=w;log_writer_sequence=2'
            $c = Event 'c' 'log_writer_id=w;log_writer_sequence=3'
            $inheritedRecovered = Record (Event 'inherited-recovered' '') $true
            $inheritedUnresolved = Record (Event 'inherited-unresolved' '') $false
            $snapshot = Snapshot @($inheritedRecovered,$inheritedUnresolved)
            $direction = @((Candidate 'w' 2 $true),(Candidate 'w' 1))
            $peerFirst = @((Candidate 'w' 3),(Candidate 'w' 2 $true))
            $result = Compare-Projection @($a,$b,$c) $direction $peerFirst $snapshot
            Assert-References @($inheritedRecovered.Event,$inheritedUnresolved.Event,$b,$a,$c,$b) @($result.CandidateEvents) 'consumer concatenation'
            Assert-References @($inheritedRecovered.Event,$b,$b) @($result.RecoveredCandidateEvents) 'recovered'
            Assert-References @($inheritedUnresolved.Event,$a,$c) @($result.UnresolvedCandidateEvents) 'unresolved'
            if ($result.RawCount -ne 6 -or $result.DistinctCount -ne 6 -or $result.RecoveredCount -ne 3 -or
                $result.UnresolvedCount -ne 3 -or $result.Verdict -ne 'fail') { throw 'mixed quartet changed' }
            """);
    }

    [Fact]
    public async Task BootstrapWriterMapping_LongPayloadAndInvocationAbaDoNotLeakMappings()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $payload = 'token9SecretLong1234567890' * 5000
            $a = Event 'a' "payload=$payload; log_writer_id=w; log_writer_sequence=1;tail=$payload"
            $b = Event 'b' 'log_writer_id=w;log_writer_sequence=1'
            $candidate = Candidate 'w' 1
            $result = Compare-Projection @($a) @($candidate) @()
            Assert-References @($a) @($result.CandidateEvents) 'first A'
            $result = Compare-Projection @($b) @($candidate) @()
            Assert-References @($b) @($result.CandidateEvents) 'replacement B'
            $result = Compare-Projection @() @($candidate) @()
            if ($result.CandidateEvents.Count -ne 0) { throw 'mapping escaped its invocation' }
            $result = Compare-Projection @($a) @($candidate) @()
            Assert-References @($a) @($result.CandidateEvents) 'fresh A'
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body + "\nWrite-Output 'bootstrap-mapping-ok'", [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("bootstrap-mapping-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferAuthorityMatching.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferFallbackV6BootstrapDirectionCorrelation.ps1')
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapDirectionProjectionBaseline.ps1')
        $script:OriginalField = (Get-Command Get-FileTransferAuthorityLogField).ScriptBlock
        function Get-FileTransferAuthorityLogField([AllowNull()][string]$Line, [string]$Name) {
            $script:FieldCalls++
            & $script:OriginalField -Line $Line -Name $Name
        }
        function Get-FileTransferFallbackV6BootstrapDirectionCorrelation([AllowEmptyCollection()][string[]]$Lines) {
            $script:DirectionLines = $Lines
            return $script:Direction
        }
        function Get-FileTransferFallbackV6BootstrapPeerFirstCorrelation([AllowEmptyCollection()][string[]]$Lines) {
            $script:PeerLines = $Lines
            return $script:PeerFirst
        }
        function Event([string]$Id, [AllowNull()]$RawLine) {
            [pscustomobject]@{Id=$Id;RawLine=$RawLine;Sequence=999;LineNumber=999;LogWriterId='parsed-writer';
                Fields=@{log_writer_id='parsed-writer';log_writer_sequence='999'}}
        }
        function Candidate([string]$Writer, $Sequence, [bool]$Recovered=$false) {
            [pscustomobject]@{Writer=$Writer;WriterSequence=$Sequence;Class='bootstrap-class';Reason='exact-reason';Variant='exact-variant';Recovered=$Recovered}
        }
        function Record($Event, [bool]$Recovered) {
            [pscustomobject]@{Event=$Event;Class='snapshot-class';Reason='snapshot-reason';Variant='snapshot-variant';Recovered=$Recovered}
        }
        function Snapshot([AllowEmptyCollection()][object[]]$Records) {
            [object[]]$recovered = @($Records | Where-Object { $_.Recovered })
            [object[]]$unresolved = @($Records | Where-Object { -not $_.Recovered })
            [pscustomobject]@{RawCount=$Records.Count;DistinctCount=$Records.Count;RecoveredCount=$recovered.Count;
                UnresolvedCount=$unresolved.Count;CandidateRecords=$Records;RecoveredCandidateRecords=$recovered;
                UnresolvedCandidateRecords=$unresolved;AuthoritativeRecords=@([pscustomobject]@{Id='immutable-authority';Hash='exact-hash'})}
        }
        function Correlation([AllowEmptyCollection()][object[]]$Candidates) {
            [pscustomobject]@{RawCount=$Candidates.Count;DistinctCount=$Candidates.Count;
                RecoveredCount=@($Candidates | Where-Object { $_.Recovered }).Count;
                UnresolvedCount=@($Candidates | Where-Object { -not $_.Recovered }).Count;CandidateRecords=$Candidates}
        }
        function Json($Value) { ConvertTo-Json -InputObject $Value -Depth 15 -Compress }
        function Assert-References([AllowEmptyCollection()][object[]]$Expected, [AllowEmptyCollection()][object[]]$Actual, [string]$Label) {
            if ($Expected.Count -ne $Actual.Count) { throw "$Label count changed: $($Expected.Count) != $($Actual.Count)" }
            for ($i=0; $i -lt $Expected.Count; $i++) {
                if (-not [object]::ReferenceEquals($Expected[$i],$Actual[$i])) { throw "$Label identity/order changed at $i" }
            }
        }
        function Compare-Projection([AllowEmptyCollection()][object[]]$Events,
                [AllowEmptyCollection()][object[]]$DirectionCandidates,
                [AllowEmptyCollection()][object[]]$PeerCandidates, $SnapshotProof=$null) {
            if ($null -eq $SnapshotProof) { $SnapshotProof = Snapshot @() }
            $script:Direction = Correlation $DirectionCandidates
            $script:PeerFirst = Correlation $PeerCandidates
            $before = Json @($Events,$DirectionCandidates,$PeerCandidates,$SnapshotProof)
            [string[]]$rawLines = @($Events | ForEach-Object { [string]$_.RawLine })
            $script:FieldCalls = 0
            $expected = Invoke-BootstrapDirectionProjectionBaseline -Events $Events -SnapshotProof $SnapshotProof
            if ($script:FieldCalls -ne (2 * $Events.Count)) { throw 'baseline field-call count changed' }
            if ((Json $script:DirectionLines) -cne (Json $rawLines) -or (Json $script:PeerLines) -cne (Json $rawLines)) { throw 'baseline consumer input mismatch' }
            $script:FieldCalls = 0
            $actual = Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof -Events $Events -SnapshotProof $SnapshotProof
            if ($script:FieldCalls -ne 0) { throw 'optimized mapping repeated generic field extraction' }
            if ((Json $script:DirectionLines) -cne (Json $rawLines) -or (Json $script:PeerLines) -cne (Json $rawLines)) { throw 'consumer raw lines changed' }
            if ((Json $expected) -cne (Json $actual)) { throw 'complete projection JSON changed' }
            foreach ($name in @('CandidateEvents','RecoveredCandidateEvents','UnresolvedCandidateEvents','AuthoritativeRecords')) {
                Assert-References @($expected.$name) @($actual.$name) $name
            }
            foreach ($name in @('CandidateRecords','RecoveredCandidateRecords','UnresolvedCandidateRecords')) {
                for ($i=0; $i -lt $expected.$name.Count; $i++) {
                    if (-not [object]::ReferenceEquals($expected.$name[$i].Event,$actual.$name[$i].Event)) { throw "$name event reference changed" }
                }
            }
            if ((Json @($Events,$DirectionCandidates,$PeerCandidates,$SnapshotProof)) -cne $before) { throw 'source events, candidates, or snapshot mutated' }
            return $actual
        }
        function Assert-MissingRawFailure([object[]]$Events) {
            $snapshot = Snapshot @()
            $baselineError = $null
            $actualError = $null
            try { $null = Invoke-BootstrapDirectionProjectionBaseline -Events $Events -SnapshotProof $snapshot } catch { $baselineError = $_.Exception.Message }
            try { $null = Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof -Events $Events -SnapshotProof $snapshot } catch { $actualError = $_.Exception.Message }
            if ($null -eq $baselineError -or $actualError -cne $baselineError) { throw 'missing RawLine failure semantics changed' }
        }
        """;
}
