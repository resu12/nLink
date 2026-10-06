using System.Diagnostics;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;
using NLink.Tests;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("RequiredConfiguration", "Debug")]
public sealed class AttemptCanonicalTailPublicationTests
{
    private static FileTransferReceiverStateFrameV6 Frame(bool canonical = true) => new()
    {
        SessionId = "s", TransferId = "t", TransportEpoch = 2, Epoch = 7,
        ContiguousCommittedChunkIndex = canonical ? 12480 : 4,
        DurableReceivedHighestChunkIndex = canonical ? 12483 : 7,
        BytesCommitted = canonical ? 268369920 : 86016,
        MissingRanges = [new() { StartChunkIndex = canonical ? 12480 : 4, ChunkCount = 1 }]
    };
    private static OfflinePublicationMetadata Metadata() => new(Frame(), "source", "peer", "bulk.peer",
        new("control", 100, new string('A', 64)), new("bulk", 100, new string('B', 64)),
        FileTransferDirection.Inbound, FileTransferRouteResolver.PostTunaFallbackV6Token,
        6, 2, 3, 4, 5, true, false, 1, TimeSpan.FromMilliseconds(750));
    private static Stamp Stall() => new(Environment.ProcessId, 1, Guid.NewGuid(), Guid.NewGuid(), 1,
        Stopwatch.Frequency, DateTimeOffset.UtcNow.UtcTicks, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp());
    private static AttemptLatePublicationWindow Window(Stamp s) => AttemptLatePublicationWindow.Create(s,
        s.UtcTicks + TimeSpan.TicksPerMillisecond * 750, new(s.CounterAfter, 3000, 1000, 65536, 2), s.CounterAfter);
    private static AttemptLatePublicationOwner Owner(AttemptLatePublicationWindow w, CancellationToken ct = default) =>
        new(w, new("s", "t", "source", "peer", 2, 3, 4, 5), ct, canonicalTail: true);

    [Theory]
    [InlineData(null, false)] [InlineData(false, false)] [InlineData(true, true)]
    [InlineData(null, true)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task GeometryRequiresTheExplicitMatchingProfile(bool? canonicalFlag, bool canonicalFrame)
    {
        if (canonicalFlag == true && !OperatingSystem.IsWindows()) return;
        var frame = Frame(canonicalFrame); var now = Stopwatch.GetTimestamp();
        AttemptOfflinePublicationDelay Delay() => canonicalFlag is { } flag
            ? new(frame, "source", "peer", now + Stopwatch.Frequency, now + 2 * Stopwatch.Frequency, 0, CancellationToken.None, flag)
            : new(frame, "source", "peer", now + Stopwatch.Frequency, now + 2 * Stopwatch.Frequency, 0, CancellationToken.None);
        if ((canonicalFlag == true) == canonicalFrame)
        {
            await using var delay = Delay();
            Assert.Same(delay, delay.Registration().Bind(Metadata() with { State = frame }));
        }
        else Assert.Equal("publication_arm_unavailable", Assert.Throws<InvalidOperationException>(Delay).Message);
    }

    [Theory]
    [InlineData("frontier")] [InlineData("highest")] [InlineData("bytes")]
    [InlineData("terminal")] [InlineData("paused")] [InlineData("ranges")]
    [InlineData("state_capacity")] [InlineData("source_capacity")] [InlineData("retained_bytes")]
    [InlineData("release")] [InlineData("reserve")]
    public void CanonicalAdmissionPreservesExactShapeCapacityAndTiming(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var frame = Frame(); var source = "source"; var retained = 0;
        var now = Stopwatch.GetTimestamp(); var release = now + Stopwatch.Frequency; var expires = now + 2 * Stopwatch.Frequency;
        switch (fault)
        {
            case "frontier": frame = frame with { ContiguousCommittedChunkIndex = 12481 }; break;
            case "highest": frame = frame with { DurableReceivedHighestChunkIndex = 12482 }; break;
            case "bytes": frame = frame with { BytesCommitted = 268369921 }; break;
            case "terminal": frame = frame with { TerminalReady = true }; break;
            case "paused": frame = frame with { TransferPaused = true }; break;
            case "ranges": frame = frame with { MissingRanges = Enumerable.Repeat(new FileTransferRangeV4 { StartChunkIndex = 12480, ChunkCount = 1 }, 9).ToArray() }; break;
            case "state_capacity": frame = frame with { SessionId = new string('s', 4097) }; break;
            case "source_capacity": source = new string('s', 4097); break;
            case "retained_bytes": retained = -1; break;
            case "release": release = now; break;
            case "reserve": expires = release + Stopwatch.Frequency / 10; break;
        }
        Assert.Equal("publication_arm_unavailable", Assert.Throws<InvalidOperationException>(() =>
            new AttemptOfflinePublicationDelay(frame, source, "peer", release, expires, retained, CancellationToken.None, canonicalTail: true)).Message);
    }

    [Theory]
    [InlineData("session")] [InlineData("transfer")] [InlineData("source")] [InlineData("epoch")]
    [InlineData("route_epoch")] [InlineData("leg")] [InlineData("bridge")] [InlineData("direction")] [InlineData("competing")]
    public async Task CanonicalOwnerStillRejectsForeignOrCompetingPublication(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owner = Owner(Window(Stall())); var item = Metadata(); var registration = owner.Registration();
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
        if (fault == "competing") registration.Bind(item);
        Assert.Equal("late_publication_source_or_competing_state", Assert.Throws<IOException>(() => registration.Bind(item)).Message);
        Assert.True(owner.Lifetime.IsCancellationRequested);
    }

    [Theory]
    [InlineData("clock")] [InlineData("process")] [InlineData("sequence")]
    [InlineData("before_release")] [InlineData("utc_deadline")] [InlineData("qpc_deadline")]
    [InlineData("frame")] [InlineData("ranges")]
    public async Task CanonicalOwnerStillRequiresTheOriginalNativeWrite(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = Stall(); var w = Window(s); var frame = Frame();
        await using var owner = Owner(w); owner.Registration().Bind(Metadata());
        var write = s with { CaptureSequence = 2, UtcTicks = s.UtcTicks + TimeSpan.TicksPerMillisecond * 250,
            CounterBefore = w.ChunkRelease, CounterAfter = w.ChunkRelease + 1 };
        switch (fault)
        {
            case "clock": write = write with { ClockInstanceId = Guid.NewGuid() }; break;
            case "process": write = write with { ProcessCreationFileTime = 2 }; break;
            case "sequence": write = write with { CaptureSequence = 1 }; break;
            case "before_release": write = write with { CounterBefore = w.ChunkRelease - 1 }; break;
            case "utc_deadline": write = write with { UtcTicks = s.UtcTicks + TimeSpan.TicksPerMillisecond * 750 }; break;
            case "qpc_deadline": write = write with { CounterAfter = s.CounterBefore + Stopwatch.Frequency * 3 / 4 }; break;
            case "frame": frame = frame with { Epoch = 8 }; break;
            case "ranges": frame = frame with { MissingRanges = [new() { StartChunkIndex = 12481, ChunkCount = 1 }] }; break;
        }
        Assert.Throws<IOException>(() => owner.CheckNativeWrite(write, frame));
        Assert.Equal("late_publication_no_native_write", Assert.Throws<IOException>(owner.VerifyComplete).Message);
    }

    [Theory]
    [InlineData("cancel")] [InlineData("retired")] [InlineData("missing_native")]
    public async Task CanonicalOwnerCancelsAndDrainsWithoutManufacturingCompletion(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = Stall();
        // Compressed unit fixture only; no native receipt or integration timing is replaced.
        var w = Window(s) with { CopyRelease = s.CounterAfter + Stopwatch.Frequency / 5 };
        using var cancellation = new CancellationTokenSource();
        await using var owner = Owner(w, cancellation.Token); owner.Registration().Bind(Metadata());
        async Task Copy()
        {
            var success = false;
            try { await owner.BeforeCopyAsync("control_queue", null, owner.Lifetime); success = true; }
            finally { owner.CopyFinished("control_queue", success); }
        }
        var pending = Copy();
        if (fault == "cancel") cancellation.Cancel();
        if (fault == "retired") owner.Fail("source_retired");
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.ThrowsAny<Exception>(owner.VerifyComplete);
        await owner.DisposeAsync();
        Assert.True(owner.Lifetime.IsCancellationRequested);
        Assert.Throws<IOException>(() => owner.CopiesFinished);
        Assert.Throws<IOException>(() => owner.Registration());
    }

    [Fact]
    public async Task CanonicalOwnerRequiresAllThreeOriginalPhysicalWriteCallbacks()
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = Stall();
        // This in-memory helper test proves copy accounting, not native custody or transfer completion.
        var w = Window(s) with { ChunkRelease = s.CounterBefore, CopyRelease = s.CounterAfter + Stopwatch.Frequency / 5 };
        await using var owner = Owner(w);
        var control = Enumerable.Repeat((byte)1, 100).ToArray(); var bulk = Enumerable.Repeat((byte)2, 100).ToArray();
        var item = Metadata() with { Control = new("control", 100, Convert.ToHexString(SHA256.HashData(control))),
            Bulk = new("bulk", 100, Convert.ToHexString(SHA256.HashData(bulk))) };
        owner.Registration().Bind(item);
        Assert.True(owner.CheckNativeWrite(s with { CaptureSequence = 2, CounterBefore = s.CounterAfter, CounterAfter = s.CounterAfter + 1 }, Frame()));
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
        owner.VerifyComplete(); Assert.True(owner.CopiesFinished);
        await owner.DisposeAsync();
        Assert.Throws<IOException>(() => owner.CheckNativeWrite(s, Frame()));
    }
}
