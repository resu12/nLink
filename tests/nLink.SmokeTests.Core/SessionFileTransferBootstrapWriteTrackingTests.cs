using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks.Sources;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BootstrapWriteTracking_StaleIngressCannotBeRelabeledOrFailReplacement(bool malformed)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var ingress = fixture.Ingress();
        fixture.ReplaceSession();
        var before = fixture.Progress();
        var batch = fixture.Batch();
        if (malformed) { batch = batch with { ChunkCount = 2 }; }
        await fixture.WriteAsync(batch, ingress);
        Assert.Equal(before, fixture.Progress());
        Assert.Equal(0, fixture.Stream.Writes);
        Assert.Empty(fixture.Operations());
        Assert.Empty(fixture.Replacement.SentFrames);
        Assert.False(GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
        await fixture.WriteAsync(fixture.Batch(), ingress: null);
        Assert.Equal(0, fixture.Stream.Writes);
    }

    [Theory]
    [InlineData("session")] [InlineData("provider")] [InlineData("service")]
    [InlineData("stream")] [InlineData("layout")] [InlineData("pause")]
    [InlineData("agreement")]
    public async Task BootstrapWriteTracking_QueuedBatchRechecksBeforeStartingIo(string replacement)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var semaphore = GetPrivateProperty<SemaphoreSlim>(fixture.Context, "ReceiverSparseWriteGate");
        await semaphore.WaitAsync();
        var before = fixture.Progress();
        var writing = fixture.WriteAsync();
        var operation = Assert.Single(fixture.Operations());
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        Assert.Equal(0, fixture.Stream.Writes);
        try { fixture.Invalidate(replacement); }
        finally { semaphore.Release(); }
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(before, fixture.Progress());
        Assert.Equal(0, fixture.Stream.Writes);
        Assert.Equal(0, fixture.OtherStream.Writes);
        Assert.Empty(fixture.Operations());
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Empty(fixture.Session.SentFrames.OfType<FileTransferStateFrameV4>());
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Theory]
    [InlineData("session")] [InlineData("stream")] [InlineData("layout")] [InlineData("pause")]
    public async Task BootstrapWriteTracking_PartiallyStartedBatchDoesNotStartMoreIoOrCommit(string replacement)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        var before = fixture.Progress();
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
        Assert.Equal(before, fixture.Progress());
        Assert.Empty(fixture.Operations());
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task BootstrapWriteTracking_PhysicalCompletionDoesNotBypassCommitBinding()
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SessionFileTransferService.BootstrapWriteBeforeCommitForTests;
        SessionFileTransferService.BootstrapWriteBeforeCommitForTests = _ => { entered.TrySetResult(); return release.Task; };
        Task? writing = null;
        var before = fixture.Progress();
        try
        {
            writing = fixture.WriteAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var operation = Assert.Single(fixture.Operations());
            Assert.Equal(1, fixture.Stream.Writes);
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            fixture.ReplaceSession();
            release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(before, fixture.Progress());
            Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            if (writing is not null) { await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
            SessionFileTransferService.BootstrapWriteBeforeCommitForTests = previous;
        }
    }

    [Fact]
    public async Task BootstrapWriteTracking_EmptyChunkSetsDoNotSettleUnfinishedLocalTail()
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SessionFileTransferService.BootstrapWriteBeforeFinalizationForTests;
        SessionFileTransferService.BootstrapWriteBeforeFinalizationForTests = _ => { entered.TrySetResult(); return release.Task; };
        Task? writing = null;
        try
        {
            writing = fixture.WriteAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var operation = Assert.Single(fixture.Operations());
            Assert.Empty(GetPrivateProperty<HashSet<int>>(fixture.Context, "ReceiverSparseChunksPendingWrite"));
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge,
                LocalStopEvaluationForTest(fixture.Context, fixture.Session));
            Assert.Empty(fixture.Bridge.Executions);
            Assert.NotNull(GetPrivatePropertyValue(fixture.Context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
            // Invalidate the owned wake so cleanup cannot start a phase in this prerequisite-only test.
            fixture.ReplaceSession();
            release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
            Assert.Empty(fixture.Bridge.Executions);
        }
        finally
        {
            release.TrySetResult();
            if (writing is not null) { await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
            SessionFileTransferService.BootstrapWriteBeforeFinalizationForTests = previous;
        }
    }

    [Fact]
    public async Task BootstrapWriteTracking_OldCompletionCannotRetireSameIndexSuccessorAfterLayoutReset()
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        var oldWriting = fixture.WriteAsync();
        var oldOperation = Assert.Single(fixture.Operations());
        var index = GetPrivateProperty<int>(fixture.Context, "NextChunkIndex");
        fixture.ResetWrittenMap();
        fixture.OtherStream.BlockWrite = true;
        SetPrivateProperty(fixture.Context, "WriteStream", fixture.OtherStream);
        var newWriting = fixture.WriteAsync();
        var newOperation = fixture.Operations().Single(op => !ReferenceEquals(op, oldOperation));
        var owners = (IDictionary)GetPrivatePropertyValue(fixture.Context, "BootstrapPendingWriteOwners")!;
        Assert.Same(newOperation, owners[index]);
        Assert.Equal(2, fixture.Operations().Length);
        try
        {
            fixture.Stream.CompleteWrite();
            await oldWriting.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.OtherStream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(BootstrapFeedbackSettledForTest(oldOperation).IsCompletedSuccessfully);
            Assert.False(BootstrapFeedbackSettledForTest(newOperation).IsCompleted);
            Assert.Same(newOperation, owners[index]);
            Assert.Contains(index, GetPrivateProperty<HashSet<int>>(fixture.Context, "ReceiverSparseChunksPendingWrite"));
            Assert.Equal((long)GetPrivateProperty<int>(fixture.Context, "ChunkSizeBytes"), GetPrivateProperty<long>(fixture.Context, "BufferedBytes"));
            Assert.Equal(0, GetPrivateProperty<long>(fixture.Context, "BytesTransferred"));
            Assert.Equal(1, fixture.Stream.Writes);
            Assert.Equal(1, fixture.OtherStream.Writes);
            fixture.OtherStream.CompleteWrite();
            await newWriting.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(index + 1, GetPrivateProperty<int>(fixture.Context, "NextChunkIndex"));
            Assert.Empty(fixture.Operations());
            Assert.Empty(owners);
            Assert.Equal(0, GetPrivateProperty<long>(fixture.Context, "BufferedBytes"));
        }
        finally
        {
            fixture.Stream.CompleteWrite(); fixture.OtherStream.CompleteWrite();
            await Task.WhenAll(oldWriting, newWriting).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BootstrapWriteTracking_CancellationOrTerminalCannotManufacturePhysicalSettlement(bool terminal)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        var before = fixture.Progress();
        var writing = fixture.WriteAsync(fixture.Batch(count: 2));
        var operation = Assert.Single(fixture.Operations());
        if (terminal)
        {
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("TransitionInboundToTerminalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Service, [fixture.Context, FileTransferTransferState.Canceled, null, "test terminal",
                    false, null, null, CancellationToken.None]));
        }
        else { GetPrivateProperty<CancellationTokenSource>(fixture.Context, "LifetimeCts").Cancel(); }
        Assert.False(writing.IsCompleted);
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        Assert.Single(fixture.Operations());
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Stream.Writes);
        Assert.Equal(1, fixture.Stream.Completion.GetResultCalls);
        Assert.Equal(before, fixture.Progress());
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Empty(fixture.Operations());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task BootstrapWriteTracking_CurrentFailureFailsButStaleFailureCannot(bool synchronous, bool stale)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: !synchronous);
        var semaphore = GetPrivateProperty<SemaphoreSlim>(fixture.Context, "ReceiverSparseWriteGate");
        if (synchronous) { await semaphore.WaitAsync(); fixture.Stream.ThrowAtStart = true; }
        var writing = fixture.WriteAsync();
        var operation = Assert.Single(fixture.Operations());
        if (stale) { fixture.ReplaceSession(); }
        if (synchronous) { semaphore.Release(); }
        else { fixture.Stream.CompleteWrite(new IOException("modeled physical write failure")); }
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(!stale, GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
        if (!stale) { Assert.Equal(FileTransferTransferState.Failed, GetPrivateProperty<FileTransferTransferState>(fixture.Context, "State")); }
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Empty(fixture.Operations());
        Assert.Equal(1, semaphore.CurrentCount);
        Assert.Empty(fixture.Replacement.SentFrames);
    }

    [Fact]
    public async Task BootstrapWriteTracking_SameTransferReplacementContextDoesNotInheritCleanupOrWake()
    {
        using var old = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        using var successor = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        var writing = old.WriteAsync();
        var operation = Assert.Single(old.Operations());
        var successorWriting = successor.WriteAsync();
        var successorOperation = Assert.Single(successor.Operations());
        var before = successor.Progress();
        typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(old.Service, successor.Context);
        old.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(before, successor.Progress());
        Assert.Same(successorOperation, Assert.Single(successor.Operations()));
        Assert.False(BootstrapFeedbackSettledForTest(successorOperation).IsCompleted);
        Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        Assert.Empty(successor.Bridge.Executions);
        successor.Stream.CompleteWrite();
        await successorWriting.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task BootstrapWriteTracking_DestinationFinalizationTailCannotCompleteReplacement()
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var size = GetPrivateProperty<int>(fixture.Context, "ChunkSizeBytes");
        var count = GetPrivateProperty<int>(fixture.Context, "ChunkCount");
        var fileSize = GetPrivateProperty<long>(fixture.Context, "FileSizeBytes");
        SetPrivateProperty(fixture.Context, "NextChunkIndex", count - 1);
        SetPrivateProperty(fixture.Context, "BytesTransferred", fileSize - size);
        var written = new BitArray(count, true);
        written[count - 1] = false;
        SetPrivateProperty(fixture.Context, "ReceiverSparseChunksWritten", written);
        fixture.Stream.SetLength(fileSize);
        SetPrivateProperty(fixture.Context, "Sha256Base64", Convert.ToBase64String(SHA256.HashData(new byte[fileSize])));
        SetPrivateProperty(fixture.Context, "Hash", IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var destination = new FileTransferReceiveDestination(fixture.Stream,
            _ => { entered.TrySetResult(); return release.Task; }, finalPath: "modeled-final-path");
        SetPrivateProperty(fixture.Context, "WriteDestination", destination);
        var writing = fixture.WriteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var operation = Assert.Single(fixture.Operations());
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            fixture.ReplaceSession();
            release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(destination.IsFinalized); // Already-started physical work is not rolled back.
            Assert.Null(GetPrivatePropertyValue(fixture.Context, "SavedFilePath"));
            Assert.False(GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
            Assert.Empty(fixture.Replacement.SentFrames);
            Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
        }
        finally { release.TrySetResult(); await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private sealed class BootstrapWriteFixture : IDisposable
    {
        public SessionFileTransferService Service { get; } = new();
        public InitialBootstrapRecordingDataSession Session { get; } = new("session_bootstrap_write_tracking", "bootstrap_write_tracking");
        public InitialBootstrapRecordingDataSession Replacement { get; } = new("session_bootstrap_write_tracking", "bootstrap_write_tracking");
        public BootstrapPhaseTestBridge Bridge { get; private set; } = null!;
        public object Context { get; private set; } = null!;
        public BootstrapControlledWriteStream Stream { get; } = new();
        public BootstrapControlledWriteStream OtherStream { get; } = new();

        public static async Task<BootstrapWriteFixture> CreateAsync(bool blockWrite = false)
        {
            var fixture = new BootstrapWriteFixture();
            fixture.Bridge = new(fixture.Session);
            var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", fixture.Session.SessionId, fixture.Session.TransferId, FileTransferDirection.Outbound);
            fixture.Context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(fixture.Service, fixture.Bridge, fixture.Session,
                beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
            SetPrivateProperty(fixture.Context, "WriteStream", fixture.Stream);
            SetPrivateProperty(fixture.Context, "PullManifestReceived", true);
            SetPrivateProperty(fixture.Context, "ReceiverSparseWriteActive", true);
            fixture.Stream.BlockWrite = blockWrite;
            return fixture;
        }

        public object? Ingress() => typeof(SessionFileTransferService)
            .GetMethod("CaptureInboundBootstrapWriteIngress", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Context, Session]);
        public FileTransferChunkBatchFrameV4 Batch(int count = 1) => new()
        {
            SessionId = Session.SessionId, TransferId = Session.TransferId,
            StartChunkIndex = GetPrivateProperty<int>(Context, "NextChunkIndex"), ChunkCount = count,
            DataSegments = Enumerable.Range(0, count).Select(_ => new byte[GetPrivateProperty<int>(Context, "ChunkSizeBytes")]).ToArray(),
        };
        public Task WriteAsync() => WriteAsync(Batch(), Ingress());
        public Task WriteAsync(FileTransferChunkBatchFrameV4 batch) => WriteAsync(batch, Ingress());
        public Task WriteAsync(FileTransferChunkBatchFrameV4 batch, object? ingress) => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("HandleInboundV4ChunkBatchFromReceiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Service, [Context, batch, FileTransferTransportKind.Tuna, ingress]));
        public object[] Operations() => ((IDictionary)GetPrivatePropertyValue(Context, "ActiveBootstrapWriteOperations")!).Values.Cast<object>().ToArray();
        public (int Frontier, long Bytes, int Epoch) Progress() => (GetPrivateProperty<int>(Context, "NextChunkIndex"),
            GetPrivateProperty<long>(Context, "BytesTransferred"), GetPrivateProperty<int>(Context, "V4StateEpoch"));
        public void ReplaceSession() { SetPrivateProperty(Context, "DataSession", Replacement); }
        public void ResetWrittenMap()
        {
            var written = (BitArray)GetPrivateProperty<BitArray>(Context, "ReceiverSparseChunksWritten").Clone();
            SetPrivateProperty(Context, "ReceiverSparseChunksWritten", written);
            GetPrivateProperty<HashSet<int>>(Context, "ReceiverSparseChunksPendingWrite").Clear();
            ((IDictionary)GetPrivatePropertyValue(Context, "BootstrapPendingWriteOwners")!).Clear();
            SetPrivateProperty(Context, "BufferedBytes", 0L);
        }
        public void Invalidate(string kind)
        {
            switch (kind)
            {
                case "session": ReplaceSession(); break;
                case "provider": Session.Generation++; break;
                case "service": SetPrivateProperty(Context, "DataSessionGeneration", 2L); break;
                case "stream": SetPrivateProperty(Context, "WriteStream", OtherStream); break;
                case "layout": ResetWrittenMap(); break;
                case "pause": SetPrivateProperty(Context, "PullTransportPaused", true); SetPrivateProperty(Context, "PullTransportPausedSinceUtc", DateTimeOffset.UtcNow); break;
                case "agreement":
                    var state = GetPrivateProperty<FileTransferCoordinatorPersistentState>(Context, "CoordinatorPersistentState");
                    SetPrivateProperty(Context, "CoordinatorPersistentState", state with { FileTransferRouteTransition = state.FileTransferRouteTransition with { BootstrapAgreement = null } });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        public void Dispose() { Stream.CompleteWrite(); OtherStream.CompleteWrite(); Service.Dispose(); Bridge.Dispose(); Session.Dispose(); Replacement.Dispose(); Stream.Dispose(); OtherStream.Dispose(); }
    }

    private sealed class BootstrapControlledWriteStream : MemoryStream
    {
        public bool BlockWrite { get; set; }
        public bool ThrowAtStart { get; set; }
        public int Writes { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BootstrapOneShotWrite Completion { get; } = new();
        private ReadOnlyMemory<byte> pending;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (ThrowAtStart) { throw new IOException("modeled synchronous start failure"); }
            if (BlockWrite && Writes == 1)
            {
                pending = buffer;
                Started.TrySetResult();
                return new(Completion, 0); // Ignores cancellation deliberately; actual settlement is independently controlled.
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
        public void CompleteWrite(Exception? error = null)
        {
            if (!Started.Task.IsCompleted || Completion.Completed) { return; }
            if (error is null)
            {
                try { Write(pending.Span); }
                catch (Exception ex) { error = ex; }
            }
            Completion.Complete(error);
        }
    }

    private sealed class BootstrapOneShotWrite : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> source = new() { RunContinuationsAsynchronously = true };
        public bool Completed { get; private set; }
        public int GetResultCalls { get; private set; }
        public void Complete(Exception? error)
        {
            Completed = true;
            if (error is null) { source.SetResult(true); } else { source.SetException(error); }
        }
        public void GetResult(short token) { Assert.Equal(0, GetResultCalls++); source.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => source.OnCompleted(continuation, state, token, flags);
    }
}
