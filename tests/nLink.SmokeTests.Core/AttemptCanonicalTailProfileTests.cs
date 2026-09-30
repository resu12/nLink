using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.Configuration;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class AttemptCanonicalTailProfileTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string? previous = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
    public AttemptCanonicalTailProfileTests() => Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "3");
    public void Dispose() => Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, previous);
    private static AttemptCanonicalTailProfile Profile() => new(268435456, 21504, 12484);
    private static byte[] Bytes(int index, int length = 256) => Enumerable.Repeat((byte)(index + 1), length).ToArray();
    private static AttemptOfflineFrameHint Hint(int index, byte[] bytes) => new("sender",
        new("session", "transfer", false, AttemptCanonicalTailProfile.FirstChunk + index, 1, 0,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
    private static AttemptOfflineFrameSchedule Schedule(CancellationToken ct = default) =>
        new("sender", "session", "transfer", ct, latePublication: true, canonicalTail: true);

    [Fact]
    public void GeometryIsFixedIncludingShortFinalChunkAndNonterminalWrite()
    {
        AttemptCanonicalTailProfile.RequireGeometry(268435456, 21504, 12484);
        Assert.Equal(12476, AttemptCanonicalTailProfile.FirstChunk);
        Assert.Equal(12473, AttemptCanonicalTailProfile.PrearmChunk);
        Assert.Equal(268219392, AttemptCanonicalTailProfile.PrearmOffset);
        Assert.Equal(268283904, (long)AttemptCanonicalTailProfile.FirstChunk * AttemptCanonicalTailProfile.ChunkBytes);
        Assert.Equal(1024, AttemptCanonicalTailProfile.FileBytes - (long)(AttemptCanonicalTailProfile.ChunkCount - 1) * AttemptCanonicalTailProfile.ChunkBytes);
        Assert.Equal(268369920, AttemptCanonicalTailProfile.CommittedAfterWrite);
        Assert.Equal(65536, AttemptCanonicalTailProfile.FileBytes - AttemptCanonicalTailProfile.CommittedAfterWrite);
    }

    [Theory]
    [InlineData(268435455L, 21504, 12484)] [InlineData(268435456L, 21503, 12484)]
    [InlineData(268435456L, 21504, 12483)] [InlineData(172032L, 21504, 8)]
    public void OtherGeometryIsRejectedWithoutArming(long length, int chunkBytes, int chunks)
    {
        Assert.Throws<IOException>(() => AttemptCanonicalTailProfile.RequireGeometry(length, chunkBytes, chunks));
        Assert.Equal("3", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
    }

    [Theory]
    [InlineData(null, false)] [InlineData(null, true)] [InlineData("3", false)] [InlineData("3", true)]
    public void ExactPriorProcessSettingIsRestoredOnce(string? setting, bool arm)
    {
        if (!OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, setting);
        var developer = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
        using var profile = Profile();
        Assert.Equal(setting, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        if (arm) profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
        Assert.Equal(arm ? "1" : setting, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        profile.Dispose();
        Assert.Equal(setting, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        Assert.Equal(developer, Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar));
        Assert.Throws<ObjectDisposedException>(profile.Check);
        Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "2");
        profile.Dispose(); // An old owner must not overwrite a subsequent setting.
        Assert.Equal("2", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
    }

    [Theory]
    [InlineData("1")] [InlineData("2")] [InlineData("4")] [InlineData("03")] [InlineData("bad")]
    public void NondefaultBatchSettingIsNotOverwritten(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, value);
        Assert.Throws<IOException>(() => Profile());
        Assert.Equal(value, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompetingOwnerOrChangedEnvironmentCannotResumeTheProfile(bool arm)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        Assert.Throws<IOException>(() => Profile());
        if (arm) profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
        Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "2");
        Assert.Throws<IOException>(profile.Check);
        Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, arm ? "1" : "3");
        Assert.Throws<IOException>(profile.Check);
        profile.Dispose();
        Assert.Equal("3", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        using var successor = Profile();
        Assert.Throws<ObjectDisposedException>(profile.RequireArmed);
        successor.Check();
    }

    [Fact]
    public async Task OnlyActualNonemptyReadsArmAndPartialReadResultsRemainExact()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        var start = AttemptCanonicalTailProfile.PrearmOffset - 8;
        var data = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        using var stream = new OffsetReadStream(data, start, 3);
        var observed = new List<(long Offset, byte[] Bytes)>();
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(stream, Task.CompletedTask,
            (offset, bytes) => observed.Add((offset, bytes)), profile);
        Assert.True(read.CanRead); Assert.True(read.CanSeek); Assert.False(read.CanWrite);
        Assert.Equal(268435456, read.Length);
        Assert.Equal(AttemptCanonicalTailProfile.PrearmOffset, read.Seek(AttemptCanonicalTailProfile.PrearmOffset, SeekOrigin.Begin));
        Assert.Throws<IOException>(profile.RequireArmed); // Seeking is not a read.
        Assert.Equal(0, await read.ReadAsync(Memory<byte>.Empty));
        Assert.Throws<IOException>(profile.RequireArmed);
        stream.Reads.Clear();
        read.Position = start;
        var buffer = new byte[8];
        Assert.Equal(3, await read.ReadAsync(buffer));
        Assert.Equal(data[..3], buffer[..3]);
        Assert.Equal("3", Assert.Single(stream.Reads).Setting);
        Assert.Throws<IOException>(profile.RequireArmed);
        read.Position = AttemptCanonicalTailProfile.PrearmOffset;
        Assert.Equal(3, await read.ReadAsync(buffer));
        Assert.Equal(data[8..11], buffer[..3]);
        profile.RequireArmed();
        Assert.Equal("1", stream.Reads[^1].Setting);
        Assert.Equal(AttemptCanonicalTailProfile.PrearmOffset + 3, read.Position);
        Assert.Equal(3, await read.ReadAsync(buffer));
        Assert.Equal(data[11..14], buffer[..3]);
        Assert.Equal(3, observed.Count);
        Assert.Equal(start, observed[0].Offset);
        Assert.Equal(data[..3], observed[0].Bytes);
        Assert.Equal(AttemptCanonicalTailProfile.PrearmOffset, observed[1].Offset);
        Assert.Equal(data[8..11], observed[1].Bytes);
        read.Seek(-6, SeekOrigin.Current);
        Assert.Equal(3, await read.ReadAsync(buffer));
        Assert.Equal(data[8..11], buffer[..3]); // No extra read or changed seek result.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeReleaseOrReadDoesNotArmOrRead(bool releaseReady)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        using var ct = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (releaseReady) release.SetResult();
        using var stream = new OffsetReadStream(Bytes(0), AttemptCanonicalTailProfile.PrearmOffset, 3);
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(stream, release.Task, (_, _) => Assert.Fail("Cancelled read"), profile);
        if (releaseReady) ct.Cancel();
        var pending = read.ReadAsync(new byte[8], ct.Token).AsTask();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(stream.Reads);
        Assert.Throws<IOException>(profile.RequireArmed);
        profile.Dispose();
        Assert.Equal("3", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EndOfFileAndRetiredReadersDoNotArm(bool retired)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        using var stream = new OffsetReadStream(Bytes(0), AttemptCanonicalTailProfile.PrearmOffset, 3);
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(stream, Task.CompletedTask, (_, _) => Assert.Fail("No data"), profile);
        if (retired)
        {
            stream.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => read.ReadAsync(new byte[8]).AsTask());
            Assert.Empty(stream.Reads);
        }
        else
        {
            read.Seek(0, SeekOrigin.End);
            Assert.Equal(0, await read.ReadAsync(new byte[8]));
            Assert.Equal("3", Assert.Single(stream.Reads).Setting);
        }
        Assert.Throws<IOException>(profile.RequireArmed);
    }

    [Fact]
    public void ArmingIsOneTransitionAndFailureFinallyRestoresTheOriginalSetting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = Console.Out;
        using var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            Assert.Throws<IOException>((Action)(() =>
            {
                using var profile = Profile();
                profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
                profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset + 3, CancellationToken.None);
                profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset - 3, CancellationToken.None);
                profile.RequireArmed();
                throw new IOException("test_failure_after_arming");
            }));
        }
        finally { Console.SetOut(output); }
        Assert.Equal("3", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        Assert.Single(capture.ToString().Split(Environment.NewLine).Where(line => line.StartsWith("carrier-canonical-tail=armed;", StringComparison.Ordinal)));
        using var successor = Profile();
        successor.Check();
    }

    [Fact]
    public async Task RetiredProfileCannotSupplyTailDescriptors()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
        profile.BindPrefix(Task.CompletedTask, () => { });
        await profile.BeforeTailReadAsync(AttemptCanonicalTailProfile.PrefixBytes, CancellationToken.None, () => { });
        var descriptors = new AttemptOfflineFrameDescriptors("sender", "session", "transfer", profile);
        var bytes = Bytes(0);
        descriptors.Observe(Hint(0, bytes).Frame);
        profile.Dispose();
        Assert.Throws<ObjectDisposedException>(() => descriptors.Consume(0, NknBridgeChannel.Bulk, bytes));
        Assert.Throws<ObjectDisposedException>(descriptors.VerifyComplete);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task OldThreeSegmentBatchReachingPrearmEndsBeforeTail(int chunksBeforeArm)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        var first = AttemptCanonicalTailProfile.PrearmChunk - chunksBeforeArm;
        var start = (long)first * AttemptCanonicalTailProfile.ChunkBytes;
        using var stream = new OffsetReadStream(new byte[3 * AttemptCanonicalTailProfile.ChunkBytes], start, int.MaxValue);
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(stream, Task.CompletedTask, (_, _) => { }, profile);
        var capturedBatchLimit = int.Parse(Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable)!);
        var buffer = new byte[AttemptCanonicalTailProfile.ChunkBytes];
        for (var i = 0; i < capturedBatchLimit; i++) Assert.Equal(buffer.Length, await read.ReadAsync(buffer));
        profile.RequireArmed();
        Assert.Equal(3, stream.Reads.Count);
        Assert.True(first + capturedBatchLimit <= AttemptCanonicalTailProfile.FirstChunk);
        Assert.Equal("1", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        Assert.All(stream.Reads.Take(chunksBeforeArm), row => Assert.Equal("3", row.Setting));
        Assert.All(stream.Reads.Skip(chunksBeforeArm), row => Assert.Equal("1", row.Setting));
    }

    [Fact]
    public async Task PrefixAndRepairPassWithoutSlotsThenEightExactTailFramesAreRequired()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        var descriptors = new AttemptOfflineFrameDescriptors("sender", "session", "transfer", profile);
        var prefix = Bytes(9);
        descriptors.Observe(Hint(-3, prefix).Frame with { ChunkCount = 3 });
        Assert.Null(descriptors.Consume(0, NknBridgeChannel.Bulk, prefix));
        var hints = (Array)descriptors.GetType().GetField("hints", Private)!.GetValue(descriptors)!;
        Assert.Equal(8, hints.Length); Assert.All(hints.Cast<object?>(), Assert.Null);
        Assert.Throws<IOException>(descriptors.VerifyComplete);
        profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
        profile.BindPrefix(Task.CompletedTask, () => { });
        await profile.BeforeTailReadAsync(AttemptCanonicalTailProfile.PrefixBytes, CancellationToken.None, () => { });
        for (var i = 0; i < 8; i++)
        {
            var bytes = Bytes(i);
            descriptors.Observe(Hint(i, bytes).Frame);
            var encoded = descriptors.Consume(0, NknBridgeChannel.Bulk, bytes);
            Assert.Equal(Hint(i, bytes), AttemptOfflineFrameHint.Decode(encoded!));
            if (i < 7) Assert.Throws<IOException>(descriptors.VerifyComplete);
        }
        descriptors.VerifyComplete();
        var repair = Bytes(10);
        descriptors.Observe(Hint(0, repair).Frame with { IsRepair = true });
        Assert.Null(descriptors.Consume(0, NknBridgeChannel.Bulk, repair));
        Assert.Null(descriptors.Consume(0, NknBridgeChannel.Control, Bytes(11)));
        Assert.Throws<IOException>(() => descriptors.Consume(0, NknBridgeChannel.Bulk, Bytes(1)));
    }

    [Theory]
    [InlineData("cache_bypass")] [InlineData("crossing")] [InlineData("tail_batch")]
    [InlineData("duplicate")] [InlineData("hash_replay")] [InlineData("foreign")]
    [InlineData("epoch")] [InlineData("past_end")]
    public async Task MissingArmingOrAmbiguousTailGeometryIsLatchedWithoutThrowingIntoObserver(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        var descriptors = new AttemptOfflineFrameDescriptors("sender", "session", "transfer", profile);
        if (fault != "cache_bypass")
        {
            profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
            profile.BindPrefix(Task.CompletedTask, () => { });
            await profile.BeforeTailReadAsync(AttemptCanonicalTailProfile.PrefixBytes, CancellationToken.None, () => { });
        }
        var frame = Hint(0, Bytes(0)).Frame;
        if (fault is "duplicate" or "hash_replay") descriptors.Observe(frame);
        frame = fault switch
        {
            "crossing" => frame with { StartChunkIndex = AttemptCanonicalTailProfile.FirstChunk - 1, ChunkCount = 3 },
            "tail_batch" => frame with { ChunkCount = 3 },
            "hash_replay" => frame with { StartChunkIndex = AttemptCanonicalTailProfile.FirstChunk + 1 },
            "foreign" => frame with { TransferId = "foreign" },
            "epoch" => frame with { TransportEpoch = 1 },
            "past_end" => frame with { StartChunkIndex = AttemptCanonicalTailProfile.ChunkCount },
            _ => frame
        };
        descriptors.Observe(frame);
        Assert.Throws<IOException>(descriptors.Check);
        Assert.Throws<IOException>(() => descriptors.Consume(0, NknBridgeChannel.Bulk, Bytes(0)));
        Assert.Throws<IOException>(descriptors.VerifyComplete);
    }

    [Fact]
    public void TailScheduleUsesRelativeReleaseOrderWithoutChangingBytesOrOriginalIndices()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var schedule = Schedule();
        Assert.False(schedule.AnyOrdinarySeen);
        var delivered = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var bytes = Bytes(i); var original = (byte[])bytes.Clone(); var chunk = i;
            schedule.Receive(Hint(i, bytes), bytes, value => { Assert.Equal(original, value); delivered.Add(chunk); });
            Array.Fill(bytes, (byte)99);
            Assert.True(schedule.AnyOrdinarySeen);
        }
        Assert.True(schedule.AllOrdinarySeen);
        Assert.Equal(new[] { 3, 5, 6, 7 }, delivered);
        Assert.Equal(3, schedule.ReadBudget().Count);
        Assert.InRange(schedule.PendingBytes, 3 * 256 + 1, 3 * (256 + 1024));
        schedule.Release(2); schedule.Release(1); schedule.Release(4);
        Assert.Equal(new[] { 3, 5, 6, 7, 2, 1, 4 }, delivered);
        Assert.Equal(0, schedule.PendingBytes);
        Assert.Throws<IOException>(() => schedule.Release(4));
    }

    [Theory]
    [InlineData("prefix")] [InlineData("past_end")] [InlineData("hash")]
    [InlineData("length")] [InlineData("sender")] [InlineData("session")]
    [InlineData("transfer")] [InlineData("epoch")] [InlineData("count")] [InlineData("repair")]
    public void InvalidTailHintsCannotDeliver(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var schedule = Schedule(); var bytes = Bytes(1); var hint = Hint(1, bytes);
        hint = fault switch
        {
            "prefix" => Hint(-1, bytes),
            "past_end" => Hint(8, bytes),
            "hash" => hint with { Frame = hint.Frame with { EnvelopeSha256 = "wrong" } },
            "length" => hint with { Frame = hint.Frame with { EnvelopeLength = 1 } },
            "sender" => hint with { Sender = "foreign" },
            "session" => hint with { Frame = hint.Frame with { SessionId = "foreign" } },
            "transfer" => hint with { Frame = hint.Frame with { TransferId = "foreign" } },
            "epoch" => hint with { Frame = hint.Frame with { TransportEpoch = 1 } },
            "count" => hint with { Frame = hint.Frame with { ChunkCount = 3 } },
            _ => hint with { Frame = hint.Frame with { IsRepair = true } }
        };
        Assert.Throws<IOException>(() => schedule.Receive(hint, bytes, _ => Assert.Fail("Invalid delivery")));
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData("capacity")] [InlineData("duplicate")] [InlineData("cancel")]
    [InlineData("dispose")] [InlineData("expiry")]
    public void TailScheduleKeepsOriginalCapacityExpiryAndCleanup(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ct = new CancellationTokenSource(); using var schedule = Schedule(ct.Token);
        var bytes = Bytes(1, fault == "capacity" ? 524288 : 256);
        schedule.Receive(Hint(1, bytes), bytes, _ => Assert.Fail("Held frame delivered"));
        var budget = schedule.ReadBudget();
        var held = (IDictionary)schedule.GetType().GetField("held", Private)!.GetValue(schedule)!;
        var started = (long)held[1]!.GetType().GetProperty("Started")!.GetValue(held[1])!;
        Assert.Equal(started + 4000, budget.OldestExpiresAtTick);
        Assert.Equal(budget.OldestExpiresAtTick, schedule.ReadBudget().OldestExpiresAtTick);
        switch (fault)
        {
            case "capacity":
                bytes = Bytes(2, 524288);
                Assert.Throws<IOException>(() => schedule.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Capacity")));
                break;
            case "duplicate":
                Assert.Throws<IOException>(() => schedule.Receive(Hint(1, bytes), bytes, _ => Assert.Fail("Duplicate")));
                break;
            case "cancel": ct.Cancel(); break;
            case "dispose": schedule.Dispose(); break;
            case "expiry":
                lock (schedule.GetType().GetField("gate", Private)!.GetValue(schedule)!)
                {
                    ((Timer)schedule.GetType().GetField("expiry", Private)!.GetValue(schedule)!).Dispose();
                    schedule.GetType().GetMethod("Expire", Private)!.Invoke(schedule, [budget.OldestExpiresAtTick]);
                }
                break;
        }
        Assert.Equal(0, schedule.PendingBytes);
        if (fault == "dispose") Assert.Throws<ObjectDisposedException>(schedule.Check);
        else Assert.Throws<IOException>(schedule.Check);
    }

    [Fact]
    public void SmallProfileDoesNotAdmitTailAndTailRequiresLatePolicy()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidOperationException>(() => new AttemptOfflineFrameSchedule("sender", "session", "transfer",
            CancellationToken.None, canonicalTail: true));
        using var small = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication: true);
        var bytes = Bytes(0);
        Assert.Throws<IOException>(() => small.Receive(Hint(0, bytes), bytes, _ => Assert.Fail("Tail admitted by small profile")));
    }

    // A tiny deterministic backing store with full-size *positions*, not a
    // canonical/native source or evidence of a full-size transfer.
    private sealed class OffsetReadStream(byte[] bytes, long start, int maxRead) : MemoryStream(bytes, false)
    {
        internal readonly List<(long Offset, string? Setting)> Reads = [];
        public override long Length => AttemptCanonicalTailProfile.FileBytes;
        public override long Position { get => start + base.Position; set => base.Position = value - start; }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = offset + (origin == SeekOrigin.Current ? Position : origin == SeekOrigin.End ? Length : 0);
            return Position;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add((Position, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable)));
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], ct);
        }
    }
}
