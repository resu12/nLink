using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Channels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_ReceiveLoopAdmitsReplayAfterCheckpointRollover()
    {
        var frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>();
        var reads = 0;
        using var session = new InitialBootstrapRecordingDataSession("session_fallback_repair_write", "fallback_repair_write")
        {
            ReceiveWithMetadataForTest = ct => { Interlocked.Increment(ref reads); return frames.Reader.ReadAsync(ct); },
        };
        using var transport = new InitialBootstrapCheckpointTransport(session);
        using var fixture = new FallbackRepairWriteFixture(transport: transport, session: session);
        var source = CreateCoordinatorStateForTest(fixture.Context);
        var loop = fixture.StartReceiveLoop();
        try
        {
            Assert.Equal(1, Volatile.Read(ref reads));
            var request = new FileTransferFrontierRequestFrameV6
            {
                SessionId = session.SessionId, TransferId = session.TransferId,
                TransportEpoch = source.CurrentLeg!.TransportEpochId + 1,
                RepairRequestId = "v6-regular-nkn-state-refresh:rollover",
                Priority = "state_refresh", RecoveryMode = "regular_nkn_state_refresh",
                MissingRanges = [new() { StartChunkIndex = fixture.Frontier, ChunkCount = 1 }],
            };
            await frames.Writer.WriteAsync(new(request, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow));
            await WaitUntilAsync(() => Volatile.Read(ref reads) == 2, timeoutMs: 3000);
            var current = CreateCoordinatorStateForTest(fixture.Context);
            Assert.Equal(source.CurrentRuntimeOwner!.Value.RuntimeGeneration + 1, current.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            Assert.Equal(source.CurrentRoutePublicationToken!.Value.PublicationGeneration + 1, current.CurrentRoutePublicationToken!.Value.PublicationGeneration);
            Assert.Equal(request.TransportEpoch, current.CurrentLeg!.TransportEpochId);
            Assert.Null(current.CurrentFallbackCheckpointLease);
            Assert.NotNull(current.PersistentState.CheckpointRepairContinuityOrigin);
            Assert.Same(session, GetPrivatePropertyValue(fixture.Context, "DataSession"));
            await frames.Writer.WriteAsync(new(fixture.Batch(), FileTransferTransportKind.RegularNkn, "bulk", DateTimeOffset.UtcNow));
            await WaitUntilAsync(() => Volatile.Read(ref reads) == 3, timeoutMs: 3000);
            Assert.Equal(101, fixture.Frontier);
            Assert.Equal(1, fixture.Stream.Writes);
            Assert.Empty(fixture.Operations());
            Assert.Equal(current.CurrentRuntimeOwner, CreateCoordinatorStateForTest(fixture.Context).CurrentRuntimeOwner);
            Assert.Equal(current.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(fixture.Context).CurrentRoutePublicationToken);
        }
        finally
        {
            TryCancelContextLifetime(fixture.Context);
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData("session")] [InlineData("provider")] [InlineData("service")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_ReceiveLoopCannotRebindReplacedReader(string replacement)
    {
        var frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>();
        var reads = 0;
        using var session = new InitialBootstrapRecordingDataSession("session_fallback_repair_write", "fallback_repair_write")
        {
            ReceiveWithMetadataForTest = ct => { Interlocked.Increment(ref reads); return frames.Reader.ReadAsync(ct); },
        };
        using var fixture = new FallbackRepairWriteFixture(session: session);
        var loop = fixture.StartReceiveLoop();
        try
        {
            Assert.Equal(1, Volatile.Read(ref reads));
            fixture.Invalidate(replacement);
            var source = CreateCoordinatorStateForTest(fixture.Context);
            for (var index = 0; index < 2; index++)
            {
                await frames.Writer.WriteAsync(new(fixture.Batch(), FileTransferTransportKind.RegularNkn, "bulk", DateTimeOffset.UtcNow));
                await WaitUntilAsync(() => Volatile.Read(ref reads) == index + 2, timeoutMs: 3000);
            }
            Assert.Equal(100, fixture.Frontier);
            Assert.Equal(0, fixture.Stream.Writes);
            Assert.Empty(fixture.Operations());
            Assert.Empty(fixture.Replacement.SentFrames);
            Assert.Equal(source.CurrentRuntimeOwner, CreateCoordinatorStateForTest(fixture.Context).CurrentRuntimeOwner);
            Assert.Equal(source.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(fixture.Context).CurrentRoutePublicationToken);
        }
        finally
        {
            TryCancelContextLifetime(fixture.Context);
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData("leg")] [InlineData("checkpoint")] [InlineData("publication")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_ReceiveLoopCannotRebindPendingReceive(string replacement)
    {
        var frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>();
        var reads = 0;
        using var session = new InitialBootstrapRecordingDataSession("session_fallback_repair_write", "fallback_repair_write")
        {
            ReceiveWithMetadataForTest = ct => { Interlocked.Increment(ref reads); return frames.Reader.ReadAsync(ct); },
        };
        using var fixture = new FallbackRepairWriteFixture(session: session);
        var loop = fixture.StartReceiveLoop();
        try
        {
            Assert.Equal(1, Volatile.Read(ref reads));
            fixture.Invalidate(replacement);
            var source = CreateCoordinatorStateForTest(fixture.Context);
            await frames.Writer.WriteAsync(new(fixture.Batch(), FileTransferTransportKind.RegularNkn, "bulk", DateTimeOffset.UtcNow));
            await WaitUntilAsync(() => Volatile.Read(ref reads) == 2, timeoutMs: 3000);
            Assert.Equal(100, fixture.Frontier);
            Assert.Equal(0, fixture.Stream.Writes);
            Assert.Empty(fixture.Operations());
            Assert.Equal(source.CurrentRuntimeOwner, CreateCoordinatorStateForTest(fixture.Context).CurrentRuntimeOwner);
            Assert.Equal(source.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(fixture.Context).CurrentRoutePublicationToken);
        }
        finally
        {
            TryCancelContextLifetime(fixture.Context);
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_CurrentWriteRetainsEofAndRemainingSparseGaps()
    {
        using var fixture = new FallbackRepairWriteFixture();
        var written = fixture.Written;
        var before = CreateCoordinatorStateForTest(fixture.Context);
        await fixture.WriteAsync(fixture.Batch(count: 2));
        Assert.Equal(2, fixture.Stream.Writes);
        Assert.Equal(102, fixture.Frontier);
        Assert.False(written[102]); Assert.True(written[255]);
        Assert.Equal(2L * GetPrivateProperty<int>(fixture.Context, "ChunkSizeBytes"),
            GetPrivateProperty<long>(fixture.Context, "BytesTransferred"));
        Assert.Same(written, fixture.Written);
        Assert.Empty(fixture.Operations());
        Assert.Empty(GetPrivateProperty<HashSet<int>>(fixture.Context, "ReceiverSparseChunksPendingWrite"));
        Assert.Equal(before.CurrentRuntimeOwner, CreateCoordinatorStateForTest(fixture.Context).CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(fixture.Context).CurrentRoutePublicationToken);
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).RepairHandoff);
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).CurrentOperation);
        Assert.False(GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_StaleIngressCannotWriteOrFailReplacement(bool malformed)
    {
        using var fixture = new FallbackRepairWriteFixture();
        var ingress = fixture.Ingress();
        fixture.Invalidate("session");
        var batch = fixture.Batch();
        if (malformed) { batch = batch with { ChunkCount = 2 }; }
        await fixture.WriteAsync(batch, ingress);
        Assert.Equal(0, fixture.Stream.Writes);
        Assert.Equal(100, fixture.Frontier);
        Assert.Empty(fixture.Operations());
        Assert.False(GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
        Assert.Empty(fixture.Replacement.SentFrames);
        await fixture.WriteAsync(fixture.Batch(), null);
        Assert.Equal(0, fixture.Stream.Writes);
    }

    [Theory]
    [InlineData("session")] [InlineData("provider")] [InlineData("service")]
    [InlineData("stream")] [InlineData("layout")] [InlineData("pause")]
    [InlineData("leg")] [InlineData("checkpoint")] [InlineData("publication")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_QueuedWriteRechecksExactSource(string replacement)
    {
        using var fixture = new FallbackRepairWriteFixture();
        var semaphore = GetPrivateProperty<SemaphoreSlim>(fixture.Context, "ReceiverSparseWriteGate");
        await semaphore.WaitAsync();
        var writing = fixture.WriteAsync();
        var operation = Assert.Single(fixture.Operations());
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        try { fixture.Invalidate(replacement); }
        finally { semaphore.Release(); }
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, fixture.Stream.Writes);
        Assert.Equal(0, fixture.OtherStream.Writes);
        Assert.Equal(100, fixture.Frontier);
        Assert.False(fixture.Written[100]);
        Assert.Empty(fixture.Operations());
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Equal(1, semaphore.CurrentCount);
        Assert.Empty(fixture.Replacement.SentFrames);
    }

    [Theory]
    [InlineData("session")] [InlineData("stream")] [InlineData("layout")]
    [InlineData("leg")] [InlineData("checkpoint")] [InlineData("publication")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_StartedWriteSettlesWithoutCommittingReplacement(string replacement)
    {
        using var fixture = new FallbackRepairWriteFixture(blockWrite: true);
        var writing = fixture.WriteAsync(fixture.Batch(count: 2));
        var operation = Assert.Single(fixture.Operations());
        Assert.Equal(1, fixture.Stream.Writes);
        fixture.Invalidate(replacement);
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Stream.Writes);
        Assert.Equal(1, fixture.Stream.Completion.GetResultCalls);
        Assert.Equal(0, fixture.OtherStream.Writes);
        Assert.Equal(100, fixture.Frontier);
        Assert.False(fixture.Written[100]);
        Assert.False(fixture.Written[101]);
        Assert.Empty(fixture.Operations());
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_PhysicalCompletionStillRequiresCurrentCommitSource()
    {
        using var fixture = new FallbackRepairWriteFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = SessionFileTransferService.BootstrapWriteBeforeCommitForTests;
        SessionFileTransferService.BootstrapWriteBeforeCommitForTests = _ => { entered.TrySetResult(); return release.Task; };
        Task? writing = null;
        try
        {
            writing = fixture.WriteAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var operation = Assert.Single(fixture.Operations());
            Assert.Equal(1, fixture.Stream.Writes);
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            fixture.Invalidate("publication");
            release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(100, fixture.Frontier);
            Assert.False(fixture.Written[100]);
            Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            if (writing is not null) { await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
            SessionFileTransferService.BootstrapWriteBeforeCommitForTests = prior;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_OnlyCurrentWriteFailureCanFailTransfer(bool stale)
    {
        using var fixture = new FallbackRepairWriteFixture(blockWrite: true);
        var writing = fixture.WriteAsync();
        var operation = Assert.Single(fixture.Operations());
        if (stale) { fixture.Invalidate("session"); }
        fixture.Stream.CompleteWrite(new IOException("modeled write failure"));
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(!stale, GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Empty(fixture.Operations());
        Assert.Empty(fixture.Replacement.SentFrames);
    }

    [Theory]
    [InlineData("session")] [InlineData("stream")] [InlineData("layout")] [InlineData("publication")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_LateFeedbackCannotAffectReplacement(string replacement)
    {
        using var transport = new DeferredControlPlaneCompletionLoopbackTransport("session_fallback_repair_write");
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var writing = fixture.WriteAsync();
        try
        {
            await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(transport.LastValidationAccepted);
            Assert.Equal(101, fixture.Frontier);
            var operation = Assert.Single(fixture.Operations());
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            fixture.Invalidate(replacement);
            var before = CreateCoordinatorStateForTest(fixture.Context);
            var logStart = GetOperationalLogLength();
            transport.ReleaseCompletion.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            var after = CreateCoordinatorStateForTest(fixture.Context);
            Assert.Equal(before.PersistentState, after.PersistentState);
            Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
            Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
            Assert.Equal(101, fixture.Frontier);
            Assert.False(GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
            Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
            Assert.Empty(fixture.Operations());
            var log = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=filetransfer_v6_receiver_state_sent;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_dispatched;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_control_plane_pressure_marked;", log, StringComparison.Ordinal);
        }
        finally { transport.ReleaseCompletion.TrySetResult(); await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_CancellationDoesNotManufacturePhysicalSettlement()
    {
        using var fixture = new FallbackRepairWriteFixture(blockWrite: true);
        var writing = fixture.WriteAsync(fixture.Batch(count: 2));
        var operation = Assert.Single(fixture.Operations());
        GetPrivateProperty<CancellationTokenSource>(fixture.Context, "LifetimeCts").Cancel();
        Assert.False(writing.IsCompleted);
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Stream.Writes);
        Assert.Equal(1, fixture.Stream.Completion.GetResultCalls);
        Assert.Equal(100, fixture.Frontier);
        Assert.False(fixture.Written[100]);
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Empty(fixture.Operations());
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairWriteTracking")]
    public async Task FallbackRepairWriteTracking_FinalizationKeepsFallbackV6FeedbackAndIntegrity()
    {
        using var transport = new LoopbackFileTransferTransport("session_fallback_repair_write");
        using var peer = new LoopbackFileTransferTransport("session_fallback_repair_write");
        transport.Connect(peer);
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var size = GetPrivateProperty<long>(fixture.Context, "FileSizeBytes");
        var chunkSize = GetPrivateProperty<int>(fixture.Context, "ChunkSizeBytes");
        fixture.Written.SetAll(false);
        fixture.Stream.SetLength(size);
        SetPrivateProperty(fixture.Context, "NextChunkIndex", 255);
        SetPrivateProperty(fixture.Context, "BytesTransferred", size - chunkSize);
        SetPrivateProperty(fixture.Context, "Sha256Base64", Convert.ToBase64String(SHA256.HashData(new byte[size])));
        SetPrivateProperty(fixture.Context, "Hash", IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        await fixture.WriteAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(GetPrivateProperty<FileTransferTransferState>(fixture.Context, "State") == FileTransferTransferState.Completed,
            GetPrivatePropertyValue(fixture.Context, "StatusMessage")?.ToString());
        Assert.Equal(256, fixture.Frontier);
        Assert.Empty(fixture.Operations());
        Assert.DoesNotContain(fixture.Session.SentFrames, frame => frame.GetType() == typeof(FileTransferStateFrameV4));
        Assert.Contains(fixture.Session.SentFrames.OfType<FileTransferReceiverStateFrameV6>(), frame => frame.TerminalReady);
    }

    private sealed class FallbackRepairWriteFixture : IDisposable
    {
        public SessionFileTransferService Service { get; } = new();
        public InitialBootstrapRecordingDataSession Session { get; }
        public InitialBootstrapRecordingDataSession Replacement { get; } = new("session_fallback_repair_write", "fallback_repair_write");
        public BootstrapControlledWriteStream Stream { get; } = new();
        public BootstrapControlledWriteStream OtherStream { get; } = new();
        public object Context { get; }
        public int Frontier => GetPrivateProperty<int>(Context, "NextChunkIndex");
        public BitArray Written => GetPrivateProperty<BitArray>(Context, "ReceiverSparseChunksWritten");

        public FallbackRepairWriteFixture(bool blockWrite = false, IFileTransferSignalingTransport? transport = null,
            InitialBootstrapRecordingDataSession? session = null)
        {
            Session = session ?? new("session_fallback_repair_write", "fallback_repair_write");
            if (transport is not null) { Service.AttachTransport(transport); }
            Context = CreatePostTunaFallbackV6InboundContext(Session.TransferId, Session.SessionId, Session);
            typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Service, Context);
            var written = new BitArray(256);
            for (var i = 0; i < 100; i++) { written[i] = true; }
            written[255] = true; // EOF has arrived; the earlier sparse gap remains.
            SetPrivateProperty(Context, "ReceiverSparseChunksWritten", written);
            SetPrivateProperty(Context, "WriteStream", Stream);
            var mode = Context.GetType().GetProperty("V6DestinationMode")!;
            mode.SetValue(Context, Enum.Parse(mode.PropertyType, "SparseSeekable"));
            SetPrivateProperty(Context, "V6SparseAcceptWindowEndExclusive", 256);
            SetPrivateProperty(Context, "V6ReceiverTransportEpoch", GetCurrentContextLeg(Context).TransportEpochId);
            Stream.BlockWrite = blockWrite;
            Assert.NotNull(FileTransferCoordinator.CaptureInboundFallbackRepairWriteScope(CreateCoordinatorStateForTest(Context)));
        }

        public object? Ingress() => typeof(SessionFileTransferService)
            .GetMethod("CaptureInboundBootstrapWriteIngress", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Context, Session]);
        public Task StartReceiveLoop() => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("RunInboundRegularNknV4Async", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Service, [Context, new FileTransferSessionOpenV2
            {
                SessionId = Session.SessionId, TransferId = Session.TransferId,
                ProtocolVersion = 6, ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 1,
            }]));
        public FileTransferChunkBatchFrameV6 Batch(int count = 1) => new()
        {
            SessionId = Session.SessionId, TransferId = Session.TransferId, StartChunkIndex = Frontier, ChunkCount = count,
            TransportEpoch = GetCurrentContextLeg(Context).TransportEpochId,
            DataSegments = Enumerable.Range(0, count).Select(_ => new byte[GetPrivateProperty<int>(Context, "ChunkSizeBytes")]).ToArray(),
        };
        public Task WriteAsync() => WriteAsync(Batch(), Ingress());
        public Task WriteAsync(FileTransferChunkBatchFrameV6 batch) => WriteAsync(batch, Ingress());
        public Task WriteAsync(FileTransferChunkBatchFrameV6 batch, object? ingress) => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("HandleInboundV6ChunkBatchFromReceiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Service, [Context, batch, FileTransferTransportKind.RegularNkn, ingress]));
        public object[] Operations() => ((IDictionary)GetPrivatePropertyValue(Context, "ActiveBootstrapWriteOperations")!).Values.Cast<object>().ToArray();
        public void Invalidate(string kind)
        {
            switch (kind)
            {
                case "session": SetPrivateProperty(Context, "DataSession", Replacement); break;
                case "provider": Session.Generation++; break;
                case "service": SetPrivateProperty(Context, "DataSessionGeneration", 2L); break;
                case "stream": SetPrivateProperty(Context, "WriteStream", OtherStream); break;
                case "layout": SetPrivateProperty(Context, "ReceiverSparseChunksWritten", new BitArray(Written)); break;
                case "pause": SetPrivateProperty(Context, "PullTransportPaused", true); SetPrivateProperty(Context, "PullTransportPausedSinceUtc", DateTimeOffset.UtcNow); break;
                case "leg": GetCurrentContextLeg(Context).TransportEpochId++; break;
                case "checkpoint": GetCurrentContextLeg(Context).CheckpointGeneration++; break;
                case "publication": SetPrivateProperty(Context, "CurrentRoutePublicationToken", null); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        public void Dispose()
        {
            Stream.CompleteWrite(); OtherStream.CompleteWrite(); Service.Dispose();
            Session.Dispose(); Replacement.Dispose(); Stream.Dispose(); OtherStream.Dispose();
        }
    }
}
