using System.Text.Json;
using NLink.Tests;
using Boundary = NLink.Tests.AttemptDeferredWriteTiming.Boundary;

namespace NLink.SmokeTests;

public sealed class AttemptDeferredWriteTimingTests
{
    private const string Prefix = "carrier-canonical-deferred-write-timing=";
    private const long Epoch = 639262694860000000;
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    private static string Line(long ticks = Epoch + 700, long sequence = 42, bool canonical = true) =>
        $"event=filetransfer_v6_receiver_state_deferred; reason=frontier_stalled; next_chunk_index={(canonical ? 12477 : 1)}; highest_received_chunk_index={(canonical ? 12483 : 7)}; log_writer_sequence={sequence}; log_writer_attempt_utc_ticks={ticks};";
    private static JsonElement Read(AttemptDeferredWriteTiming timing)
    {
        using var output = new StringWriter();
        timing.Write(output);
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(Prefix, line);
        Assert.True(line.Length < 3000);
        using var json = JsonDocument.Parse(line[Prefix.Length..]);
        return json.RootElement.Clone();
    }
    private static long Number(JsonElement row, string field) => row.GetProperty(field).GetInt64();

    [Fact]
    public void FixedBoundariesKeepFirstSamplesAndOriginalDeadlineValues()
    {
        var tick = 1L;
        var timing = new AttemptDeferredWriteTiming(() => new(tick++, Epoch + tick));
        foreach (var boundary in Enum.GetValues<Boundary>()) timing.Capture(boundary);
        foreach (var boundary in Enum.GetValues<Boundary>()) timing.Capture(boundary);
        timing.Grace(false, Utc(Epoch), Utc(Epoch + 1000));
        timing.Grace(false, Utc(Epoch + 100), Utc(Epoch + 2000));
        timing.Grace(true, Utc(Epoch + 1001), Utc(Epoch + 1000));
        timing.Grace(true, Utc(Epoch + 2000), Utc(Epoch + 3000));
        var row = Read(timing);
        foreach (var boundary in Enum.GetValues<Boundary>())
            Assert.Equal((long)boundary + 1, Number(row.GetProperty(boundary.ToString()), "Counter"));
        Assert.Equal(Epoch + 1000, Number(row, "BeforeReleaseDueUtcTicks"));
        Assert.Equal(Epoch + 1000, Number(row, "RecheckDueUtcTicks"));
        Assert.Equal(Epoch + 1001, Number(row, "RecheckNowUtcTicks"));
        Assert.Equal(0, Number(row, "ReadCount"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("MatchedAt").ValueKind);
        using var again = new StringWriter();
        timing.Capture(Boundary.CursorStart); timing.Write(again);
        Assert.Empty(again.ToString());
    }

    [Fact]
    public void PollingRetainsAggregateCostsAndTheFirstActualMatchWithoutLogText()
    {
        var tick = 0L;
        var timing = new AttemptDeferredWriteTiming(() => new(tick, Epoch + tick));
        Assert.False(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => { tick = 20; return "unrelated secret text"; }, timing));
        tick = 30;
        Assert.True(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => { tick = 80; return Line() + "\n" + Line(Epoch + 900, 99); }, timing));
        var row = Read(timing);
        Assert.Equal(2, Number(row, "ReadCount")); Assert.Equal(2, Number(row, "CompletedReads"));
        Assert.Equal(70, Number(row, "TotalReadTicks")); Assert.Equal(50, Number(row, "MaxReadTicks"));
        Assert.Equal(0, Number(row.GetProperty("FirstReadStart"), "Counter"));
        Assert.Equal(30, Number(row.GetProperty("LastReadStart"), "Counter"));
        Assert.Equal(80, Number(row.GetProperty("LastReadEnd"), "Counter"));
        Assert.Equal(80, Number(row.GetProperty("LastPollEnd"), "Counter"));
        Assert.Equal(Epoch + 700, Number(row, "MatchedWriterUtcTicks"));
        Assert.Equal(42, Number(row, "MatchedWriterSequence"));
        Assert.DoesNotContain("secret", row.GetRawText());
        Assert.DoesNotContain("event=", row.GetRawText());
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, false)] [InlineData(1, false)]
    [InlineData(-1, true)] [InlineData(0, true)] [InlineData(1, true)]
    public void ExactGraceBoundaryRemainsStrictWithOrWithoutDiagnostics(int offset, bool enabled)
    {
        var due = Utc(Epoch + 1000);
        var timing = enabled ? new AttemptDeferredWriteTiming() : null;
        void Check() => AttemptOfflineLatePublicationProgress.RequireDeferredWithinRecentGrace(due.AddTicks(offset), due, timing);
        if (offset < 0) Check();
        else Assert.Equal("offline_schedule_deferred_outside_recent_grace", Assert.Throws<IOException>(Check).Message);
        if (timing is not null)
        {
            var row = Read(timing);
            Assert.Equal(due.UtcTicks, Number(row, "RecheckDueUtcTicks"));
            Assert.Equal(due.UtcTicks + offset, Number(row, "RecheckNowUtcTicks"));
        }
    }

    [Theory]
    [InlineData(700, 800, 900, false)] // Timely event, read and gate check.
    [InlineData(700, 1100, 1100, true)] // Timely writer attempt; observation finishes late.
    [InlineData(700, 800, 1100, true)] // Timely read; gate acquisition/check finishes late.
    [InlineData(1000, 1100, 1100, true)] // Writer attempt itself is at the deadline.
    public void ScalarTimelineDistinguishesLateObservationFromLateWriterWithoutGrantingAcceptance(
        int writer, int readEnd, int guard, bool rejected)
    {
        var now = 100L;
        var timing = new AttemptDeferredWriteTiming(() => new(now, Epoch + now));
        timing.Grace(false, Utc(Epoch + now), Utc(Epoch + 1000));
        Assert.True(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true,
            () => { now = readEnd; return Line(Epoch + writer); }, timing));
        timing.Capture(Boundary.RecheckRequested);
        now = guard; timing.Capture(Boundary.RecheckGateEntered);
        void Check() => AttemptOfflineLatePublicationProgress.RequireDeferredWithinRecentGrace(Utc(Epoch + guard), Utc(Epoch + 1000), timing);
        if (rejected) Assert.Throws<IOException>(Check); else Check();
        var row = Read(timing);
        Assert.Equal(Epoch + writer, Number(row, "MatchedWriterUtcTicks"));
        Assert.Equal(Epoch + readEnd, Number(row.GetProperty("LastReadEnd"), "UtcTicks"));
        Assert.Equal(Epoch + guard, Number(row.GetProperty("RecheckGateEntered"), "UtcTicks"));
        Assert.Equal(Epoch + 1000, Number(row, "RecheckDueUtcTicks"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReadUsesTheOriginalGeometryPredicateAndDoesNotRequireDiagnosticMetadata(bool canonical)
    {
        var timing = new AttemptDeferredWriteTiming();
        var calls = 0;
        Assert.False(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(canonical, () => { calls++; return Line(canonical: !canonical); }, timing));
        var line = Line(canonical: canonical).Split("; log_writer_sequence=")[0] + ";";
        Assert.True(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(canonical, () => { calls++; return line; }, timing));
        Assert.Equal(2, calls);
        var row = Read(timing);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("MatchedWriterUtcTicks").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("MatchedWriterSequence").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("MatchedAt").ValueKind);
    }

    [Theory]
    [InlineData("negative")] [InlineData("overflow")] [InlineData("duplicate")] [InlineData("text")]
    public void MalformedMetadataIsUnavailableAndCannotChangeTheOriginalMatch(string fault)
    {
        var line = Line();
        line = fault switch
        {
            "negative" => line.Replace($"={Epoch + 700};", "=-1;"),
            "overflow" => line.Replace($"={Epoch + 700};", "=99999999999999999999;"),
            "duplicate" => line + $" log_writer_attempt_utc_ticks={Epoch + 800};",
            _ => line.Replace($"={Epoch + 700};", "=not-a-clock;")
        };
        var timing = new AttemptDeferredWriteTiming();
        Assert.True(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => line, timing));
        Assert.Equal(JsonValueKind.Null, Read(timing).GetProperty("MatchedWriterUtcTicks").ValueKind);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalReadExceptionOrCancellationPropagatesOnce(bool cancelled)
    {
        Exception original = cancelled ? new OperationCanceledException() : new IOException("original read failure");
        var timing = new AttemptDeferredWriteTiming(); var calls = 0;
        var observed = Record.Exception(() => AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => { calls++; throw original; }, timing));
        Assert.Same(original, observed); Assert.Equal(1, calls);
        var row = Read(timing);
        Assert.Equal(1, Number(row, "ReadCount")); Assert.Equal(0, Number(row, "CompletedReads"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("LastReadEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("MatchedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("LastPollEnd").ValueKind);
    }

    [Fact]
    public void BrokenClockDoesNotReplaceMatchingOrGraceRejection()
    {
        var timing = new AttemptDeferredWriteTiming(() => throw new IOException("broken diagnostic clock"));
        timing.Capture(Boundary.CursorStart);
        Assert.True(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => Line(), timing));
        Assert.Equal("offline_schedule_deferred_outside_recent_grace", Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.RequireDeferredWithinRecentGrace(Utc(Epoch + 1000), Utc(Epoch + 1000), timing)).Message);
        var row = Read(timing);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("LastReadStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("MatchedAt").ValueKind);
        Assert.Equal(1, Number(row, "CompletedReads"));
        Assert.Equal(Line().Length, Number(row, "LastTailCharacters"));
    }

    [Fact]
    public void FailedReadStartSampleCannotReuseAnEarlierPollStart()
    {
        var call = 0;
        var timing = new AttemptDeferredWriteTiming(() => ++call == 4
            ? throw new IOException("second start unavailable") : new(call, Epoch + call));
        Assert.False(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => "", timing));
        Assert.False(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => "", timing));
        var row = Read(timing);
        Assert.Equal(2, Number(row, "ReadCount")); Assert.Equal(2, Number(row, "CompletedReads"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("LastReadStart").ValueKind);
        Assert.Equal(1, Number(row, "TotalReadTicks"));
    }

    [Fact]
    public void ManyPollsRemainOneBoundedRecordAndCannotProduceAMatch()
    {
        var tick = 0L;
        var timing = new AttemptDeferredWriteTiming(() => new(++tick, Epoch + tick));
        for (var i = 0; i < 10000; i++)
            Assert.False(AttemptOfflineLatePublicationProgress.ReadDeferredEvent(true, () => "", timing));
        var row = Read(timing);
        Assert.Equal(10000, Number(row, "ReadCount")); Assert.Equal(10000, Number(row, "CompletedReads"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("MatchedAt").ValueKind);
    }

    private sealed class FailingWriter(object gate) : StringWriter
    {
        internal int Attempts;
        internal bool LockHeld;
        public override void WriteLine(string? value)
        {
            Attempts++;
            LockHeld |= Monitor.IsEntered(gate);
            throw new IOException("output failed");
        }
    }

    [Fact]
    public void OutputIsDeferredSingleAttemptAndCannotMaskOriginalFailure()
    {
        var gate = new object(); var timing = new AttemptDeferredWriteTiming();
        IOException original;
        lock (gate)
        {
            timing.Capture(Boundary.RecheckGateEntered);
            original = Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.RequireDeferredWithinRecentGrace(Utc(Epoch + 1000), Utc(Epoch + 1000), timing));
        }
        using var output = new FailingWriter(gate);
        timing.Write(output); timing.Write(output);
        Assert.Equal(1, output.Attempts);
        Assert.False(output.LockHeld);
        Assert.Equal("offline_schedule_deferred_outside_recent_grace", original.Message);
        using var again = new StringWriter(); timing.Write(again);
        Assert.Empty(again.ToString());
    }
}
