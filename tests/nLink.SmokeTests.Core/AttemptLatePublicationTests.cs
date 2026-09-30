using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;
using NLink.Tests;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;

namespace NLink.SmokeTests;

public sealed class AttemptLatePublicationTests
{
    private static Stamp Stall() => new(Environment.ProcessId, 1, Guid.NewGuid(), Guid.NewGuid(), 1,
        Stopwatch.Frequency, DateTimeOffset.UtcNow.UtcTicks, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp());
    private static AttemptOfflineFrameSchedule.HoldBudget Hold(Stamp s) => new(s.CounterAfter, 3000, 1000, 65536, 2);
    private static AttemptLatePublicationWindow Window(Stamp s) => AttemptLatePublicationWindow.Create(s,
        s.UtcTicks + TimeSpan.TicksPerMillisecond * 750, Hold(s), s.CounterAfter);

    [Theory]
    [InlineData("frequency")] [InlineData("process")] [InlineData("creation")] [InlineData("observation")]
    [InlineData("clock")] [InlineData("bracket")] [InlineData("deadline")] [InlineData("hold_count")]
    [InlineData("hold_bytes")] [InlineData("hold_reserve")] [InlineData("late_arm")] [InlineData("future_hold")]
    public void LateWindow_RejectsUnavailableOriginalBudget(string fault)
    {
        var s = Stall(); var h = Hold(s); var now = s.CounterAfter;
        var due = s.UtcTicks + TimeSpan.TicksPerMillisecond * 750;
        switch (fault)
        {
            case "frequency": s = s with { Frequency = 1 }; break;
            case "process": s = s with { ProcessId = -1 }; break;
            case "creation": s = s with { ProcessCreationFileTime = 0 }; break;
            case "observation": s = s with { ObservationId = Guid.Empty }; break;
            case "clock": s = s with { ClockInstanceId = Guid.Empty }; break;
            case "bracket": s = s with { CounterBefore = s.CounterAfter + 1 }; break;
            case "deadline": due++; break;
            case "hold_count": h = h with { Count = 1 }; break;
            case "hold_bytes": h = h with { Bytes = 1048577 }; break;
            case "hold_reserve": h = h with { OldestExpiresAtTick = 2000 }; break;
            case "late_arm": now += Stopwatch.Frequency / 4; break;
            case "future_hold": h = h with { CapturedCounter = now + 1 }; break;
        }
        var error = Assert.Throws<IOException>(() => AttemptLatePublicationWindow.Create(s, due, h, now));
        if (fault is not ("hold_reserve" or "late_arm"))
            Assert.Equal("late_publication_clock_or_hold_shape", error.Message);
    }

    [Theory]
    [InlineData(-1, -1)] [InlineData(-1, 0)] [InlineData(-1, 1)]
    [InlineData(0, -1)] [InlineData(0, 0)] [InlineData(0, 1)]
    [InlineData(1, -1)] [InlineData(1, 0)] [InlineData(1, 1)]
    public void LateWindow_ReportsExactBudgetAtEqualityBoundaries(int armOffset, int holdSlack)
    {
        // Immutable synthetic scheduling facts only, not a native receipt or
        // another transfer. Offsets are individual QPC ticks, not milliseconds.
        var f = Stopwatch.Frequency;
        var s = Stall() with { CounterBefore = f * 100, CounterAfter = f * 100 };
        var chunk = s.CounterAfter + f / 4;
        var copies = s.CounterAfter + f * 4 / 5;
        var h = Hold(s) with { CapturedCounter = copies + f / 5 - f + holdSlack, OldestExpiresAtTick = 2000 };
        var now = chunk + armOffset;
        var due = s.UtcTicks + TimeSpan.TicksPerMillisecond * 750;
        var holdEnd = copies + f / 5 + holdSlack;
        var expires = Math.Min(s.CounterBefore + f * 5 / 4, holdEnd - f / 10);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            if (armOffset < 0 && holdSlack > 0)
            {
                var window = AttemptLatePublicationWindow.Create(s, due, h, now);
                Assert.Equal(new AttemptLatePublicationWindow(s, chunk, copies, expires, h.Bytes), window);
                return;
            }
            var error = Assert.Throws<IOException>(() => AttemptLatePublicationWindow.Create(s, due, h, now));
            const string prefix = "late_publication_original_budget_unavailable; budget_json=";
            Assert.StartsWith(prefix, error.Message);
            Assert.DoesNotContain('\n', error.Message);
            Assert.DoesNotContain('\r', error.Message);
            Assert.True(error.Message.Length < 2048);
            using var json = JsonDocument.Parse(error.Message[prefix.Length..]);
            var d = json.RootElement;
            Assert.Equal(1, d.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(s, d.GetProperty("Stall").Deserialize<Stamp>());
            Assert.Equal(h, d.GetProperty("Hold").Deserialize<AttemptOfflineFrameSchedule.HoldBudget>());
            Assert.Equal(due, d.GetProperty("DeadlineUtcTicks").GetInt64());
            Assert.Equal(now, d.GetProperty("NowCounter").GetInt64());
            Assert.Equal(holdEnd, d.GetProperty("HoldEndCounter").GetInt64());
            Assert.Equal(chunk, d.GetProperty("ChunkReleaseCounter").GetInt64());
            Assert.Equal(copies, d.GetProperty("CopyReleaseCounter").GetInt64());
            Assert.Equal(expires, d.GetProperty("ExpiresCounter").GetInt64());
            Assert.Equal(-armOffset, d.GetProperty("ArmMarginCounter").GetInt64());
            Assert.Equal(holdSlack, d.GetProperty("HoldReserveMarginCounter").GetInt64());
            Assert.Equal(expires - (copies + f / 10), d.GetProperty("ExpiryMarginCounter").GetInt64());
            Assert.Equal(armOffset >= 0, d.GetProperty("LateArm").GetBoolean());
            Assert.Equal(holdSlack <= 0, d.GetProperty("HoldReserveUnavailable").GetBoolean());
            Assert.Equal(copies + f / 10 >= expires, d.GetProperty("ExpiryAllowanceUnavailable").GetBoolean());
            // The original parent logs nested Exception.ToString(), not Data.
            var outer = new IOException("offline_schedule_unavailable; stage=native-arm-and-original-hold-budget", error);
            Assert.Contains(error.Message, outer.ToString());
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData(3000)] [InlineData(4000)]
    public void LateWindow_SyntheticFixedHoldBudgetDoesNotExtendPublicationDeadlines(int milliseconds)
    {
        // Synthetic admission arithmetic, not a replay or a native receipt.
        // At S+4 ms the oldest frame is already 2328 ms old.
        var f = Stopwatch.Frequency;
        var s = Stall() with { CounterBefore = f * 100, CounterAfter = f * 100 };
        var h = Hold(s) with { CapturedCounter = s.CounterAfter + f * 4 / 1000,
            CapturedTick = 2328, OldestExpiresAtTick = milliseconds };
        var due = s.UtcTicks + TimeSpan.TicksPerMillisecond * 750;
        if (milliseconds == 3000)
        {
            var error = Assert.Throws<IOException>(() => AttemptLatePublicationWindow.Create(s, due, h, h.CapturedCounter));
            Assert.StartsWith("late_publication_original_budget_unavailable; budget_json=", error.Message);
        }
        else
        {
            var window = AttemptLatePublicationWindow.Create(s, due, h, h.CapturedCounter);
            Assert.Equal(s.CounterAfter + f / 4, window.ChunkRelease);
            Assert.Equal(s.CounterAfter + f * 4 / 5, window.CopyRelease);
            Assert.Equal(s.CounterBefore + f * 5 / 4, window.Expires);
        }
    }

    [Theory]
    [InlineData("valid")] [InlineData("deadline")] [InlineData("qpc_deadline")] [InlineData("process")]
    [InlineData("clock")] [InlineData("sequence")] [InlineData("before_release")] [InlineData("utc_backwards")]
    public void LateWindow_ChecksOriginalWriteWithoutEvaluatingAuthority(string fault)
    {
        var s = Stall(); var w = Window(s);
        var write = s with { CaptureSequence = 2, UtcTicks = s.UtcTicks + TimeSpan.TicksPerMillisecond * 300,
            CounterBefore = s.CounterAfter + Stopwatch.Frequency * 3 / 10, CounterAfter = s.CounterAfter + Stopwatch.Frequency * 3 / 10 + 1 };
        write = fault switch
        {
            "deadline" => write with { UtcTicks = s.UtcTicks + TimeSpan.TicksPerMillisecond * 750 },
            "qpc_deadline" => write with { CounterAfter = s.CounterBefore + Stopwatch.Frequency * 3 / 4 },
            "process" => write with { ProcessCreationFileTime = 2 },
            "clock" => write with { ClockInstanceId = Guid.NewGuid() },
            "sequence" => write with { CaptureSequence = 1 },
            "before_release" => write with { CounterBefore = w.ChunkRelease - 1 },
            "utc_backwards" => write with { UtcTicks = s.UtcTicks - 1 },
            _ => write
        };
        if (fault == "valid") w.CheckWrite(write);
        else Assert.Throws<IOException>(() => w.CheckWrite(write));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HoldBudget_ReadAndReleaseDoNotRenewOriginalDeadline(bool latePublication)
    {
        using var schedule = new AttemptOfflineFrameSchedule("source", "s", "t", CancellationToken.None, latePublication);
        foreach (var i in new[] { 1, 2, 4 })
        {
            var bytes = new byte[] { (byte)i };
            schedule.Receive(new("source", new("s", "t", false, i, 1, 0, 1, Convert.ToHexString(SHA256.HashData(bytes)))), bytes, _ => { });
        }
        var first = schedule.ReadBudget(); await Task.Delay(30);
        var second = schedule.ReadBudget(); schedule.Release(2); var remaining = schedule.ReadBudget();
        Assert.Equal(first.OldestExpiresAtTick, second.OldestExpiresAtTick);
        Assert.Equal(first.OldestExpiresAtTick, remaining.OldestExpiresAtTick);
        Assert.True(second.CapturedTick >= first.CapturedTick); Assert.Equal(2, remaining.Count);
        Assert.True(remaining.Bytes < first.Bytes);
        schedule.Release(1); schedule.Release(4);
        Assert.Throws<IOException>(() => schedule.ReadBudget());
    }

    private static OfflinePublicationMetadata Metadata() => new(new FileTransferReceiverStateFrameV6
    {
        SessionId = "s", TransferId = "t", TransportEpoch = 2, Epoch = 7,
        ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 7, BytesCommitted = 86016,
        MissingRanges = [new() { StartChunkIndex = 4, ChunkCount = 1 }]
    }, "source", "peer", "bulk.peer", new("control", 100, new string('A', 64)), new("bulk", 100, new string('B', 64)),
        FileTransferDirection.Inbound, FileTransferRouteResolver.PostTunaFallbackV6Token, 6, 2, 3, 4, 5, true, false, 1, TimeSpan.FromMilliseconds(750));
    private static AttemptLatePublicationOwner Owner(AttemptLatePublicationWindow w, CancellationToken ct = default) =>
        new(w, new("s", "t", "source", "peer", 2, 3, 4, 5), ct);

    [Theory]
    [InlineData("session")] [InlineData("transfer")] [InlineData("source")] [InlineData("epoch")]
    [InlineData("route_epoch")] [InlineData("leg")] [InlineData("bridge")] [InlineData("direction")]
    [InlineData("competing")]
    public async Task LateOwner_RejectsChangedSourceOrCompetingPublication(string fault)
    {
        await using var owner = Owner(Window(Stall())); var item = Metadata();
        var hook = owner.Registration();
        item = fault switch
        {
            "session" => item with { State = item.State with { SessionId = "other" } },
            "transfer" => item with { State = item.State with { TransferId = "other" } },
            "source" => item with { Source = "other" },
            "epoch" => item with { TransportEpoch = 3 },
            "route_epoch" => item with { LiveRouteEpoch = 4 },
            "leg" => item with { LegGeneration = 5 },
            "bridge" => item with { BridgeRecoveryGeneration = 6 },
            "direction" => item with { Direction = FileTransferDirection.Outbound },
            _ => item
        };
        if (fault == "competing") hook.Bind(item);
        Assert.Throws<IOException>(() => hook.Bind(item));
        Assert.True(owner.Lifetime.IsCancellationRequested);
    }

    [Theory]
    [InlineData("valid")] [InlineData("missing_native")] [InlineData("changed_frame")]
    [InlineData("cancel")] [InlineData("source_retired")]
    public async Task LateOwner_NeedsNativeCheckAndDrainsOwnedRoles(string fault)
    {
        var s = Stall(); var w = Window(s);
        // Only this deterministic owner fixture compresses the wait. Production
        // integration uses Create's unchanged S+250/S+800 original deadlines.
        w = w with { ChunkRelease = s.CounterBefore, CopyRelease = s.CounterAfter + Stopwatch.Frequency / 5,
            Expires = s.CounterAfter + Stopwatch.Frequency };
        using var cancellation = new CancellationTokenSource();
        await using var owner = Owner(w, cancellation.Token); var item = Metadata();
        Assert.Same(owner, owner.Registration().Bind(item));
        if (fault is "valid" or "changed_frame")
        {
            var frame = (FileTransferReceiverStateFrameV6)item.State;
            if (fault == "changed_frame") frame = frame with { Epoch = 8 };
            var write = s with { CaptureSequence = 2, CounterBefore = s.CounterAfter, CounterAfter = s.CounterAfter + 1 };
            if (fault == "valid") Assert.True(owner.CheckNativeWrite(write, frame));
            else { Assert.Throws<IOException>(() => owner.CheckNativeWrite(write, frame)); owner.Fail("native_frame_rejected"); }
        }
        var success = false;
        async Task Role()
        {
            try { await owner.BeforeCopyAsync("control_queue", null, owner.Lifetime); success = true; }
            finally { owner.CopyFinished("control_queue", success); }
        }
        var pending = Role();
        if (fault == "cancel") cancellation.Cancel();
        if (fault == "source_retired") owner.Fail("source_retired");
        if (fault == "valid") await pending; else await Assert.ThrowsAnyAsync<Exception>(() => pending);
        // A timer/one successful role is never physical completion.
        Assert.ThrowsAny<Exception>(owner.VerifyComplete);
        await Task.WhenAll(owner.DisposeAsync().AsTask(), owner.DisposeAsync().AsTask());
        Assert.True(owner.Lifetime.IsCancellationRequested);
        Assert.Null(typeof(AttemptLatePublicationOwner).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
        Assert.Throws<IOException>(() => owner.Registration().Bind(item));
    }

    [Fact]
    public async Task LateOwner_OriginalAbsoluteExpiryAppliesBeforeBinding()
    {
        var window = Window(Stall());
        await using var owner = Owner(window with { Expires = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20 });
        await Task.Delay(100);
        Assert.Throws<IOException>(() => owner.Registration().Bind(Metadata()));
        Assert.True(owner.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task LateOwner_VerifiesAllOriginalWriteCallbacksAfterNativeCheck()
    {
        var s = Stall(); var w = Window(s) with { ChunkRelease = s.CounterBefore, CopyRelease = s.CounterAfter + Stopwatch.Frequency / 5 };
        await using var owner = Owner(w);
        var control = Enumerable.Repeat((byte)1, 100).ToArray(); var bulk = Enumerable.Repeat((byte)2, 100).ToArray();
        var item = Metadata() with { Control = new("control", 100, Convert.ToHexString(SHA256.HashData(control))),
            Bulk = new("bulk", 100, Convert.ToHexString(SHA256.HashData(bulk))) };
        owner.Registration().Bind(item);
        Assert.True(owner.CheckNativeWrite(s with { CaptureSequence = 2, CounterBefore = s.CounterAfter, CounterAfter = s.CounterAfter + 1 },
            (FileTransferReceiverStateFrameV6)item.State));
        async Task Copy(string role, bool bulkLane)
        {
            var success = false;
            try
            {
                await owner.BeforeCopyAsync(role, role == "control_queue" ? null : Stopwatch.GetTimestamp(), owner.Lifetime);
                using var stream = new MemoryStream();
                await AttemptPipeFrameWriter.WriteAsync(stream, "source", "peer", 0,
                    bulkLane ? NknBridgeChannel.Bulk : NknBridgeChannel.Control, bulkLane ? bulk : control,
                    null, () => { }, owner.ObserveWrite, owner.Lifetime);
                success = true;
            }
            finally { owner.CopyFinished(role, success); }
        }
        await Task.WhenAll(Copy("control_queue", false), Copy("control_copy", false), Copy("bulk_copy", true));
        Assert.True(owner.CopiesFinished); owner.VerifyComplete();
        await owner.DisposeAsync();
        Assert.Throws<IOException>(() => owner.CheckNativeWrite(s, (FileTransferReceiverStateFrameV6)item.State));
    }
}
