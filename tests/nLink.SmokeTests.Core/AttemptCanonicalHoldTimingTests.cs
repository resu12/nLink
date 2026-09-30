using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class AttemptCanonicalHoldTimingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Prefix = "carrier-canonical-hold-timing=";
    private static object Field(AttemptOfflineFrameSchedule schedule, string name) =>
        typeof(AttemptOfflineFrameSchedule).GetField(name, Private)!.GetValue(schedule)!;
    private static AttemptOfflineFrameSchedule Schedule(CancellationToken ct = default)
    {
        var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", ct,
            latePublication: true, canonicalTail: true);
        // Exact-boundary tests use the existing expiry path, without wall-clock sleeps.
        lock (Field(schedule, "gate")) { ((Timer)Field(schedule, "expiry")).Dispose(); }
        return schedule;
    }
    private static void Receive(AttemptOfflineFrameSchedule schedule, int index, Action<byte[]> deliver)
    {
        var bytes = Enumerable.Repeat((byte)index, 256).ToArray();
        var chunk = index + (schedule.IsCanonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0);
        schedule.Receive(new("sender", new("session", "transfer", false, chunk, 1, 0,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))), bytes, deliver);
    }
    private static object Held(AttemptOfflineFrameSchedule schedule, int index) => ((IDictionary)Field(schedule, "held"))[index]!;
    private static long Started(AttemptOfflineFrameSchedule schedule, int index) =>
        (long)Held(schedule, index).GetType().GetProperty("Started")!.GetValue(Held(schedule, index))!;
    private static void Expire(AttemptOfflineFrameSchedule schedule, long tick)
    {
        lock (Field(schedule, "gate"))
            typeof(AttemptOfflineFrameSchedule).GetMethod("Expire", Private)!.Invoke(schedule, [tick]);
    }
    private static JsonElement[] Read(AttemptOfflineFrameSchedule schedule)
    {
        using var output = new StringWriter();
        schedule.WriteHoldTimings(output);
        return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                Assert.StartsWith(Prefix, line);
                Assert.True(line.Length < 512);
                using var json = JsonDocument.Parse(line[Prefix.Length..]);
                return json.RootElement.Clone();
            }).ToArray();
    }
    private static long Number(JsonElement value, string name) => value.GetProperty(name).GetInt64();

    [Fact]
    public void FiveFixedSlotsRetainOriginalStartsReadyAndFirstExpiryAfterDisposal()
    {
        using var schedule = Schedule();
        var starts = new Dictionary<int, long>();
        foreach (var index in new[] { 1, 2, 4 })
        {
            Receive(schedule, index, _ => Assert.Fail("Held frame delivered"));
            starts.Add(index, Started(schedule, index));
        }
        var bytes = schedule.PendingBytes;
        schedule.CaptureTailReadyTiming();
        var before = Environment.TickCount64;
        schedule.CaptureTailReadyTiming();
        var deadline = starts.Values.Min() + 4000;
        Expire(schedule, deadline);
        var error = Assert.Throws<IOException>(schedule.Check).InnerException;
        Assert.Equal("offline_schedule_hold_expired", Assert.IsType<TimeoutException>(error).Message);
        Expire(schedule, deadline + 100);
        schedule.CaptureTailReadyTiming(); // The first ready snapshot must survive a later failed/empty state.
        schedule.Dispose();
        var rows = Read(schedule);
        Assert.Equal(5, rows.Length);
        for (var slot = 0; slot < 3; slot++)
        {
            var index = new[] { 1, 2, 4 }[slot];
            Assert.Equal("held", rows[slot].GetProperty("Boundary").GetString());
            Assert.Equal(AttemptCanonicalTailProfile.FirstChunk + index, Number(rows[slot], "ChunkIndex"));
            Assert.Equal(starts[index], Number(rows[slot], "StartedTick"));
            Assert.Equal(starts[index] + 4000, Number(rows[slot], "ExpiresAtTick"));
            Assert.Equal(slot + 1, Number(rows[slot], "HeldCount"));
            Assert.True(Number(rows[slot], "UtcTicks") > 0);
        }
        Assert.Equal("exact-tail-ready-observed", rows[3].GetProperty("Boundary").GetString());
        Assert.True(Number(rows[3], "TickMilliseconds") <= before);
        Assert.Equal(deadline, Number(rows[3], "ExpiresAtTick"));
        Assert.Equal(bytes, Number(rows[3], "HeldBytes"));
        Assert.Equal("expired", rows[4].GetProperty("Boundary").GetString());
        Assert.Equal(deadline, Number(rows[4], "TickMilliseconds"));
        Assert.Equal(deadline, Number(rows[4], "ExpiresAtTick"));
        Assert.Equal(3, Number(rows[4], "HeldCount"));
        Assert.Equal(bytes, Number(rows[4], "HeldBytes"));
        Assert.Equal(0, schedule.PendingBytes);
        Assert.Same(error, Assert.Throws<IOException>(schedule.Check).InnerException);
        schedule.CaptureTailReadyTiming();
        Assert.Empty(Read(schedule));
    }

    [Fact]
    public void PassiveReadAndOutputDoNotExpireAnOverdueFrameOrRenewItsClock()
    {
        using var schedule = Schedule();
        Receive(schedule, 2, _ => Assert.Fail("Held frame delivered"));
        var frame = Held(schedule, 2);
        var start = Environment.TickCount64 - 4001;
        frame.GetType().GetProperty("Started")!.SetValue(frame, start);
        schedule.CaptureTailReadyTiming();
        var rows = Read(schedule);
        Assert.Equal(2, rows.Length);
        Assert.Equal(start + 4000, Number(rows[1], "ExpiresAtTick"));
        Assert.True(Number(rows[1], "TickMilliseconds") > Number(rows[1], "ExpiresAtTick"));
        Assert.False(rows[1].GetProperty("Failed").GetBoolean());
        Assert.True(schedule.PendingBytes > 0);
        Assert.Equal(start, Started(schedule, 2));
        Assert.IsType<TimeoutException>(Assert.Throws<IOException>(schedule.Check).InnerException);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public void DiagnosticCaptureLeavesExactExpiryBoundaryUnchanged(int offset)
    {
        using var schedule = Schedule();
        Receive(schedule, 2, _ => Assert.Fail("Held frame delivered"));
        var deadline = Started(schedule, 2) + 4000;
        schedule.CaptureTailReadyTiming();
        Expire(schedule, deadline + offset);
        var rows = Read(schedule);
        Assert.Equal(offset < 0 ? 2 : 3, rows.Length);
        if (offset < 0)
        {
            schedule.Check();
            Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
        }
        else
        {
            Assert.Equal(deadline + offset, Number(rows[^1], "TickMilliseconds"));
            Assert.IsType<TimeoutException>(Assert.Throws<IOException>(schedule.Check).InnerException);
            Assert.Equal(0, schedule.PendingBytes);
        }
    }

    [Fact]
    public void ReadyCaptureDoesNotChangeDropPassReleaseOrderOrDeadline()
    {
        using var schedule = Schedule();
        var delivered = new List<int>();
        for (var index = 0; index < 8; index++)
        {
            var chunk = index;
            Receive(schedule, index, _ => delivered.Add(chunk));
        }
        var deadline = schedule.ReadBudget().OldestExpiresAtTick;
        schedule.CaptureTailReadyTiming();
        Assert.Equal(new[] { 3, 5, 6, 7 }, delivered);
        schedule.Release(2);
        Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
        schedule.Release(1); schedule.Release(4);
        Assert.Equal(new[] { 3, 5, 6, 7, 2, 1, 4 }, delivered);
        Assert.Equal(4, Read(schedule).Length);
        Assert.Equal(0, schedule.PendingBytes);
    }

    private sealed class FailingWriter(object gate) : StringWriter
    {
        internal int Attempts;
        internal bool LockHeld;
        public override void WriteLine(string? value)
        {
            Attempts++;
            LockHeld |= Monitor.IsEntered(gate);
            throw new IOException("diagnostic_output_failed");
        }
    }

    [Fact]
    public void OutputFailureOutsideLockIsSingleAttemptAndPreservesOriginalFailure()
    {
        using var schedule = Schedule();
        Receive(schedule, 2, _ => Assert.Fail("Held frame delivered"));
        Expire(schedule, Started(schedule, 2) + 4000);
        var original = Assert.Throws<IOException>(schedule.Check).InnerException;
        using var output = new FailingWriter(Field(schedule, "gate"));
        schedule.WriteHoldTimings(output);
        schedule.WriteHoldTimings(output);
        Assert.Equal(1, output.Attempts);
        Assert.False(output.LockHeld);
        Assert.Empty(Read(schedule));
        Assert.Same(original, Assert.Throws<IOException>(schedule.Check).InnerException);
    }

    [Fact]
    public void BrokenDiagnosticStorageCannotReplaceExpiryOrReleaseFrames()
    {
        using var schedule = Schedule();
        var field = typeof(AttemptOfflineFrameSchedule).GetField("holdTimings", Private)!;
        field.SetValue(schedule, Array.CreateInstance(field.FieldType.GetElementType()!, 0));
        Receive(schedule, 2, _ => Assert.Fail("Held frame delivered"));
        var deadline = Started(schedule, 2) + 4000;
        schedule.CaptureTailReadyTiming();
        Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
        Expire(schedule, deadline);
        Assert.Equal("offline_schedule_hold_expired", Assert.IsType<TimeoutException>(
            Assert.Throws<IOException>(schedule.Check).InnerException).Message);
        Assert.Empty(Read(schedule));
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CancelledOrDisposedSnapshotIsPassiveAndDoesNotInventExpiry(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var schedule = Schedule(cancellation.Token);
        Receive(schedule, 2, _ => Assert.Fail("Held frame delivered"));
        if (cancel) cancellation.Cancel(); else schedule.Dispose();
        schedule.CaptureTailReadyTiming();
        var rows = Read(schedule);
        Assert.Equal(2, rows.Length);
        Assert.Equal(0, Number(rows[1], "HeldCount"));
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("ExpiresAtTick").ValueKind);
        Assert.Equal(cancel, rows[1].GetProperty("Failed").GetBoolean());
        Assert.Equal(!cancel, rows[1].GetProperty("Disposed").GetBoolean());
        if (cancel) Assert.IsType<OperationCanceledException>(Assert.Throws<IOException>(schedule.Check).InnerException);
        else Assert.Throws<ObjectDisposedException>(schedule.Check);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NoncanonicalProfilesDoNotCaptureOrWriteTiming(bool latePublication)
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication);
        Receive(schedule, 2, _ => Assert.Fail("Held frame delivered"));
        var deadline = schedule.ReadBudget().OldestExpiresAtTick;
        schedule.CaptureTailReadyTiming();
        Assert.Empty(Read(schedule));
        Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
    }
}
