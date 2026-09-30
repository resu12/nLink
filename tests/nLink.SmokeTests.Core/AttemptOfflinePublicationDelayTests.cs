using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

public sealed class AttemptOfflinePublicationDelayTests
{
    [Theory]
    [InlineData("source")] [InlineData("peer")] [InlineData("frontier")] [InlineData("epoch")]
    [InlineData("range")] [InlineData("paused")] [InlineData("terminal")] [InlineData("transport_epoch")]
    [InlineData("route")] [InlineData("protocol")] [InlineData("bulk_absent")] [InlineData("bulk_destination")]
    [InlineData("attempts")] [InlineData("ignore_cancel")] [InlineData("queue_only")] [InlineData("timeout")]
    [InlineData("oversize_envelope")] [InlineData("oversize_descriptor")] [InlineData("same_id")]
    [InlineData("same_hash")] [InlineData("hash")] [InlineData("aggregate_budget")] [InlineData("second_publication")]
    public async Task OfflinePublicationDelay_RejectsUnsupportedBinding(string change)
    {
        var item = Metadata();
        await using var delay = NewDelay(item, 500, 2000, change == "aggregate_budget" ? 1048576 : 0);
        var changed = change switch
        {
            "source" => item with { Source = "other" },
            "peer" => item with { ControlDestination = "other" },
            "frontier" => item with { State = item.State with { ContiguousCommittedChunkIndex = 5 } },
            "epoch" => item with { State = item.State with { Epoch = 2 } },
            "range" => item with { State = item.State with { MissingRanges = [new() { StartChunkIndex = 6, ChunkCount = 1 }] } },
            "paused" => item with { State = item.State with { TransferPaused = true } },
            "terminal" => item with { State = item.State with { TerminalReady = true } },
            "transport_epoch" => item with { TransportEpoch = 3 },
            "route" => item with { Route = FileTransferRouteResolver.FileTunaV4Token },
            "protocol" => item with { Protocol = 4 },
            "bulk_absent" => item with { Bulk = null },
            "bulk_destination" => item with { BulkDestination = null },
            "attempts" => item with { PeerCopyAttempts = 2 },
            "ignore_cancel" => item with { IgnoreCallerCancellation = true },
            "queue_only" => item with { PeerVisibleRequired = false },
            "timeout" => item with { CopyTimeout = TimeSpan.FromMilliseconds(2500) },
            "oversize_envelope" => item with { Control = item.Control with { Length = 16385 } },
            "oversize_descriptor" => item with { Control = item.Control with { MessageId = new string('x', 1024) } },
            "same_id" => item with { Bulk = item.Bulk! with { MessageId = item.Control.MessageId } },
            "same_hash" => item with { Bulk = item.Bulk! with { Sha256 = item.Control.Sha256 } },
            "hash" => item with { Control = item.Control with { Sha256 = new string('z', 64) } },
            _ => item,
        };
        if (change == "second_publication") delay.Bind(item);
        Assert.Throws<InvalidOperationException>(() => delay.Bind(changed));
        Assert.True(delay.Lifetime.IsCancellationRequested); Assert.NotNull(delay.Failure);
        Assert.Equal(0, delay.Pending);
    }

    [Fact]
    public async Task OfflinePublicationDelay_ReservesAllBuffersWithinExistingAggregateBudget()
    {
        var item = Metadata();
        const int remaining = 1048576 - 65536 - 3 * 100 - 2 * 100;
        await using var fits = NewDelay(item, 500, 1500, remaining);
        Assert.Same(fits, fits.Bind(item));
        await using var exceeds = NewDelay(item, 500, 1500, remaining + 1);
        Assert.Throws<InvalidOperationException>(() => exceeds.Bind(item));
    }

    [Theory]
    [InlineData("frontier")] [InlineData("range_count")] [InlineData("derived_metadata")]
    [InlineData("paused")] [InlineData("terminal")] [InlineData("release_expired")] [InlineData("completion_reserve")]
    public void OfflinePublicationDelay_RejectsUnavailableArm(string fault)
    {
        var state = (FileTransferReceiverStateFrameV6)Metadata().State;
        state = fault switch
        {
            "frontier" => state with { ContiguousCommittedChunkIndex = 1 },
            "range_count" => state with { MissingRanges = Enumerable.Repeat(new FileTransferRangeV4(), 9).ToArray() },
            "derived_metadata" => state with { RecoveryMode = new string('x', 4096) },
            "paused" => state with { TransferPaused = true },
            "terminal" => state with { TerminalReady = true },
            _ => state,
        };
        var now = Stopwatch.GetTimestamp();
        Assert.Throws<InvalidOperationException>(() => new AttemptOfflinePublicationDelay(state, "source", "peer",
            fault == "release_expired" ? now - 1 : now + Stopwatch.Frequency / 2,
            fault == "completion_reserve" ? now + Stopwatch.Frequency / 2 : now + Stopwatch.Frequency * 2, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData("duplicate_role")] [InlineData("unknown_role")] [InlineData("timeout_role_mismatch")]
    public async Task OfflinePublicationDelay_RoleFailureCancelsAlreadyPendingWait(string fault)
    {
        var item = Metadata();
        await using var delay = NewDelay(item, 500, 1500);
        delay.Bind(item);
        var pending = RunRole(delay, "control_queue", () => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => delay.BeforeCopyAsync(
            fault == "unknown_role" ? "unknown" : fault == "duplicate_role" ? "control_queue" : "control_copy", null, delay.Lifetime));
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.Equal(0, delay.Pending); Assert.NotNull(delay.Failure);
    }

    [Fact]
    public async Task OfflinePublicationDelay_TimeReleaseDoesNotNeedArrivalsOrImplyCompletion()
    {
        var item = Metadata();
        await using var delay = NewDelay(item, 80, 1000);
        delay.Bind(item);
        // A single role releases. There is deliberately no all-arrivals barrier.
        await RunRole(delay, "control_queue", () => Task.CompletedTask);
        Assert.Equal(0, delay.Pending);
        Assert.Throws<InvalidOperationException>(delay.VerifyComplete);
        Assert.NotNull(delay.Failure);
    }

    [Theory]
    [InlineData("early")] [InlineData("wrong_peer")] [InlineData("wrong_source")] [InlineData("tuna")]
    [InlineData("lane")] [InlineData("length")] [InlineData("completion_only")]
    [InlineData("duplicate_attempt")] [InlineData("duplicate_completion")] [InlineData("third_control")]
    public async Task OfflinePublicationDelay_RejectsPhysicalWriteMismatch(string fault)
    {
        var item = Metadata();
        await using var delay = NewDelay(item, fault == "early" ? 500 : 50, 1200);
        delay.Bind(item);
        if (fault != "early") await RunRole(delay, "control_queue", () => Task.CompletedTask);
        var write = new AttemptPipeWriteObservation(Guid.NewGuid(), item.Source, item.ControlDestination,
            0, NknBridgeChannel.Control, item.Control.Length, item.Control.Sha256, false, Stopwatch.GetTimestamp());
        var changed = fault switch
        {
            "wrong_peer" => write with { Peer = "other" },
            "wrong_source" => write with { Source = "other" },
            "tuna" => write with { Kind = 1 },
            "lane" => write with { Lane = NknBridgeChannel.Bulk },
            "length" => write with { Length = write.Length + 1 },
            "completion_only" or "duplicate_completion" => write with { Completed = true },
            _ => write,
        };
        if (fault is "duplicate_attempt" or "duplicate_completion" or "third_control") delay.ObserveWrite(write);
        if (fault == "duplicate_completion") delay.ObserveWrite(changed);
        if (fault == "third_control")
        {
            delay.ObserveWrite(write with { WriteId = Guid.NewGuid() });
            changed = write with { WriteId = Guid.NewGuid() };
        }
        Assert.Throws<InvalidOperationException>(() => delay.ObserveWrite(changed));
        Assert.True(delay.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task OfflinePublicationDelay_DisposalDrainsPostReleaseWorkAndRejectsLateWrite()
    {
        var item = Metadata();
        await using var delay = NewDelay(item, 50, 1000);
        delay.Bind(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var role = RunRole(delay, "control_queue", async () =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, delay.Lifetime); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, delay.Pending);
        await delay.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => role);
        Assert.Equal(0, delay.Pending);
        Assert.Throws<InvalidOperationException>(() => delay.ObserveWrite(new(Guid.NewGuid(), item.Source,
            item.ControlDestination, 0, NknBridgeChannel.Control, item.Control.Length, item.Control.Sha256, false, Stopwatch.GetTimestamp())));
    }

    [Fact]
    public async Task OfflinePublicationDelay_RejectsInsufficientOriginalCopyBudget()
    {
        var item = Metadata();
        await using var delay = NewDelay(item, 500, 1500);
        delay.Bind(item);
        await Assert.ThrowsAsync<InvalidOperationException>(() => delay.BeforeCopyAsync("control_copy",
            Stopwatch.GetTimestamp() - Stopwatch.Frequency / 2, CancellationToken.None));
        delay.CopyFinished("control_copy", false);
        Assert.True(delay.Lifetime.IsCancellationRequested); Assert.Equal(0, delay.Pending);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OfflinePublicationDelay_PipeCompletionFollowsOriginalPayloadFlushAndPeerCheck(bool hinted)
    {
        using var stream = new PublicationWriteStream();
        var observations = new List<AttemptPipeWriteObservation>();
        var bytes = new byte[] { 1, 2, 3, 4 };
        byte[]? hint = hinted ? [5, 6] : null;
        var checkedPeer = false;
        var write = AttemptPipeFrameWriter.WriteAsync(stream, "source", "peer", 0, NknBridgeChannel.Bulk,
            bytes, hint, () => checkedPeer = true, item =>
            {
                if (item.Completed) Assert.True(checkedPeer);
                observations.Add(item);
            }, CancellationToken.None);
        await stream.AtFlush.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(write.IsCompleted); Assert.False(checkedPeer); Assert.False(Assert.Single(observations).Completed);
        stream.ReleaseFlush.SetResult(); await write;
        Assert.Equal(2, observations.Count); Assert.True(observations[1].Completed);
        Assert.Equal(observations[0].WriteId, observations[1].WriteId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), observations[1].Sha256);
        var wire = stream.ToArray();
        Assert.Equal(hinted ? 2 : 0, wire[0]); Assert.Equal((byte)NknBridgeChannel.Bulk, wire[1]);
        Assert.Equal(bytes.Length, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(2, 4)));
        if (hinted) { Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(6, 4))); Assert.Equal(hint, wire[10..12]); }
        Assert.Equal(bytes, wire[(hinted ? 12 : 6)..]);
    }

    [Theory]
    [InlineData("header")] [InlineData("hint_length")] [InlineData("hint")] [InlineData("payload")]
    [InlineData("flush")] [InlineData("peer")] [InlineData("attempt_observer")] [InlineData("completion_observer")]
    public async Task OfflinePublicationDelay_PipeFailureCannotManufactureCompletion(string fault)
    {
        using var stream = new PublicationWriteStream { Fault = fault };
        stream.ReleaseFlush.SetResult();
        var observations = new List<AttemptPipeWriteObservation>();
        await Assert.ThrowsAsync<IOException>(() => AttemptPipeFrameWriter.WriteAsync(stream, "source", "peer", 0,
            NknBridgeChannel.Bulk, [1, 2, 3], [4, 5],
            () => { if (fault == "peer") throw new IOException("peer_retired"); }, item =>
            {
                if (fault == (item.Completed ? "completion_observer" : "attempt_observer")) throw new IOException("observer");
                observations.Add(item);
            }, CancellationToken.None));
        Assert.DoesNotContain(observations, value => value.Completed);
    }

    private static async Task RunRole(AttemptOfflinePublicationDelay delay, string role, Func<Task> send)
    {
        var success = false;
        try { await delay.BeforeCopyAsync(role, role == "control_queue" ? null : Stopwatch.GetTimestamp(), delay.Lifetime); await send(); success = true; }
        finally { delay.CopyFinished(role, success); }
    }
    private static AttemptOfflinePublicationDelay NewDelay(OfflinePublicationMetadata item, int release, int expiry, int retained = 0)
    {
        var now = Stopwatch.GetTimestamp();
        return new((FileTransferReceiverStateFrameV6)item.State, item.Source, item.ControlDestination,
            now + Stopwatch.Frequency * release / 1000, now + Stopwatch.Frequency * expiry / 1000, retained, CancellationToken.None);
    }
    private static OfflinePublicationMetadata Metadata() => new(new FileTransferReceiverStateFrameV6
    {
        SessionId = "session", TransferId = "transfer", Epoch = 1, TransportEpoch = 2,
        ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 7,
        CreditUntilChunkIndexExclusive = 8, BytesCommitted = 4 * 21504,
        MissingRanges = [new() { StartChunkIndex = 4, ChunkCount = 1 }],
    }, "source", "peer", "peer-bulk", new("control", 100, new string('A', 64)), new("bulk", 100, new string('B', 64)),
        FileTransferDirection.Inbound, FileTransferRouteResolver.PostTunaFallbackV6Token, 6, 2, 1, 1, 1, true, false, 1, TimeSpan.FromMilliseconds(750));

    private sealed class PublicationWriteStream : MemoryStream
    {
        private int writeCount;
        internal string? Fault;
        internal readonly TaskCompletionSource AtFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var stage = ++writeCount switch { 1 => "header", 2 => "hint_length", 3 => "hint", _ => "payload" };
            if (Fault == stage) throw new IOException(stage);
            return base.WriteAsync(buffer, ct);
        }
        public override async Task FlushAsync(CancellationToken ct)
        {
            AtFlush.TrySetResult(); await ReleaseFlush.Task.WaitAsync(ct);
            if (Fault == "flush") throw new IOException("flush");
            await base.FlushAsync(ct);
        }
    }
}
