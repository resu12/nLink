using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairHandoffRuntime_CapturedLastGapRestoresAndFinalizesWithTerminalFeedback()
    {
        using var transport = new LoopbackFileTransferTransport("session_fallback_repair_write");
        using var peer = new LoopbackFileTransferTransport("session_fallback_repair_write");
        transport.Connect(peer);
        using var fixture = CreateRuntimeRepairHandoffFixture(blockWrite: true, transport: transport);
        var size = GetPrivateProperty<long>(fixture.Context, "FileSizeBytes");
        var chunkSize = GetPrivateProperty<int>(fixture.Context, "ChunkSizeBytes");
        for (var index = 100; index < 254; index++) { fixture.Written[index] = true; }
        SetPrivateProperty(fixture.Context, "NextChunkIndex", 254);
        SetPrivateProperty(fixture.Context, "BytesTransferred", 254L * chunkSize);
        SetPrivateProperty(fixture.Context, "Sha256Base64", Convert.ToBase64String(SHA256.HashData(new byte[size])));
        SetPrivateProperty(fixture.Context, "Hash", IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        fixture.Stream.SetLength(size);
        var writing = fixture.WriteAsync();
        ApplyRuntimeRepairHandoffPeer(fixture);
        Assert.Equal(FileTransferFallbackRepairHandoffState.Captured, GetRuntimeRepairHandoff(fixture)!.State);
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => GetPrivateProperty<FileTransferTransferState>(fixture.Context, "State") == FileTransferTransferState.Completed, 3000);
        Assert.Equal(256, fixture.Frontier);
        Assert.Equal(FileTransferFallbackRepairHandoffState.Restored, GetRuntimeRepairHandoff(fixture)!.State);
        Assert.Empty(GetRuntimeRepairHandoff(fixture)!.DrainReceipt!.MissingRanges);
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).CurrentOperation);
        Assert.Contains(fixture.Session.SentFrames.OfType<FileTransferReceiverStateFrameV6>(), frame => frame.TerminalReady);
        Assert.DoesNotContain(fixture.Session.SentFrames, frame => frame.GetType() == typeof(FileTransferStateFrameV4));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairHandoffRuntime_CapturedWriteDrainsUnderOwnedPauseAndRetainsExactGaps()
    {
        using var fixture = CreateRuntimeRepairHandoffFixture(blockWrite: true);
        var writing = fixture.WriteAsync(fixture.Batch(count: 2));
        var operation = Assert.Single(fixture.Operations());
        ApplyRuntimeRepairHandoffPeer(fixture);
        var captured = GetRuntimeRepairHandoff(fixture)!;
        Assert.Equal(FileTransferFallbackRepairHandoffState.Captured, captured.State);
        Assert.Single(captured.Capture!.InboundWrites);
        Assert.True(GetPrivateProperty<bool>(fixture.Context, "PullTransportPaused"));
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).CurrentOperation);
        await fixture.WriteAsync(fixture.Batch(count: 1));
        Assert.Single(fixture.Operations());
        Assert.Equal(1, fixture.Stream.Writes);
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => GetRuntimeRepairHandoff(fixture)?.State == FileTransferFallbackRepairHandoffState.Drained, 3000);
        var drained = GetRuntimeRepairHandoff(fixture)!;
        Assert.Equal(102, fixture.Frontier);
        Assert.Equal(255, drained.DrainReceipt!.Checkpoint.DurableHighestChunkIndex);
        Assert.Equal(new[] { new FileTransferRepairHandoffRange(102, 153) }, drained.DrainReceipt.MissingRanges);
        Assert.True(fixture.Written[255]);
        Assert.Empty(fixture.Operations());
        Assert.NotNull(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).CurrentOperation);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, CreateCoordinatorStateForTest(fixture.Context).RouteSelection.Route);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairHandoffRuntime_FeedbackDeliveryDoesNotDrainPhysicalOrLocalWork()
    {
        var transport = new PhysicalFallbackFeedbackTransport("session_fallback_repair_write");
        using var fixture = CreateRuntimeRepairHandoffFixture(transport: transport);
        var send = SendFallbackRepairStateForTest(fixture);
        await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        ApplyRuntimeRepairHandoffPeer(fixture);
        Assert.Equal(FileTransferFallbackRepairHandoffState.Captured, GetRuntimeRepairHandoff(fixture)!.State);
        Assert.Single(GetRuntimeRepairHandoff(fixture)!.Capture!.InboundFeedback);
        transport.ReleaseDelivery.TrySetResult();
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        Assert.Equal(FileTransferFallbackRepairHandoffState.Captured, GetRuntimeRepairHandoff(fixture)!.State);
        Assert.False(await SendFallbackRepairStateForTest(fixture));
        transport.ReleasePhysical.TrySetResult();
        await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => GetRuntimeRepairHandoff(fixture)?.State == FileTransferFallbackRepairHandoffState.Drained, 3000);
        Assert.Equal(new[] { new FileTransferRepairHandoffRange(100, 155) }, GetRuntimeRepairHandoff(fixture)!.DrainReceipt!.MissingRanges);
    }

    [Theory]
    [InlineData("stream")] [InlineData("layout")] [InlineData("session")] [InlineData("provider")]
    [InlineData("service")] [InlineData("pause")] [InlineData("leg")] [InlineData("checkpoint")] [InlineData("publication")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairHandoffRuntime_StaleCapturedWriteCannotDrainYieldOrRestore(string mutation)
    {
        using var fixture = CreateRuntimeRepairHandoffFixture(blockWrite: true);
        var writing = fixture.WriteAsync();
        var operation = Assert.Single(fixture.Operations());
        ApplyRuntimeRepairHandoffPeer(fixture);
        fixture.Invalidate(mutation);
        var before = GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState");
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(InvokeRuntimeRepairHandoffRestore(fixture));
        Assert.Equal(before, GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState"));
        Assert.Equal(FileTransferFallbackRepairHandoffState.Captured, GetRuntimeRepairHandoff(fixture)!.State);
        Assert.Equal(100, fixture.Frontier);
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).CurrentOperation);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairHandoffRuntime_ClosedNegotiationRestoresSameStreamBitmapAndGaps()
    {
        using var fixture = CreateRuntimeRepairHandoffFixture();
        var written = fixture.Written;
        ApplyRuntimeRepairHandoffPeer(fixture);
        await WaitUntilAsync(() => GetRuntimeRepairHandoff(fixture)?.State == FileTransferFallbackRepairHandoffState.Drained, 3000);
        var receipt = GetRuntimeRepairHandoff(fixture)!.DrainReceipt!;
        Assert.True(InvokeRuntimeRepairHandoffRestore(fixture));
        Assert.Equal(FileTransferFallbackRepairHandoffState.Restored, GetRuntimeRepairHandoff(fixture)!.State);
        Assert.Equal(receipt.Checkpoint, GetRuntimeRepairHandoff(fixture)!.RestoreCommand!.Checkpoint);
        Assert.Equal(receipt.MissingRanges, GetRuntimeRepairHandoff(fixture)!.RestoreCommand!.MissingRanges);
        Assert.Same(written, fixture.Written);
        Assert.Same(fixture.Stream, GetPrivatePropertyValue(fixture.Context, "WriteStream"));
        Assert.False(GetPrivateProperty<bool>(fixture.Context, "PullTransportPaused"));
        Assert.Null(GetPrivatePropertyValue(fixture.Context, "FallbackRepairHandoffExecution"));
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(fixture.Context).CurrentOperation);
        Assert.Equal(100, fixture.Frontier);
        Assert.False(InvokeRuntimeRepairHandoffRestore(fixture));
        await fixture.WriteAsync();
        Assert.Equal(101, fixture.Frontier);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairHandoffRuntime_OrdinaryTransportResumeCannotReleasePause()
    {
        using var fixture = CreateRuntimeRepairHandoffFixture();
        ApplyRuntimeRepairHandoffPeer(fixture);
        await WaitUntilAsync(() => GetRuntimeRepairHandoff(fixture)?.State == FileTransferFallbackRepairHandoffState.Drained, 3000);
        var before = GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState");
        var result = typeof(SessionFileTransferService).GetMethod("TryResumeInboundTransportLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, "transport_ready", false, FileTransferTransportHandoffKind.None, FileTransferTransportKind.RegularNkn]);
        Assert.False(Assert.IsType<bool>(result));
        Assert.True(GetPrivateProperty<bool>(fixture.Context, "PullTransportPaused"));
        Assert.Equal(before, GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState"));
    }

    private static FallbackRepairWriteFixture CreateRuntimeRepairHandoffFixture(bool blockWrite = false,
        IFileTransferSignalingTransport? transport = null)
    {
        var fixture = new FallbackRepairWriteFixture(blockWrite, transport);
        GetCurrentContextLeg(fixture.Context).CheckpointGeneration = 1;
        SetPrivateProperty(fixture.Context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(fixture.Context, "V6TransportHandoff", new TransportHandoffEpoch
        {
            EpochId = GetCurrentContextLeg(fixture.Context).TransportEpochId,
            Direction = FileTransferDirection.Inbound,
            State = V6TransportHandoffState.FrontierRepairOnly,
        });
        return fixture;
    }

    private static FileTransferFallbackRepairHandoffOperation? GetRuntimeRepairHandoff(FallbackRepairWriteFixture fixture)
        => GetCoordinatorFallbackTailYieldSubstate(fixture.Context).RepairHandoff;

    private static bool InvokeRuntimeRepairHandoffRestore(FallbackRepairWriteFixture fixture)
    {
        lock (typeof(SessionFileTransferService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Service)!)
        {
            return Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryRestoreInboundFallbackRepairHandoffLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, null]));
        }
    }

    private static void ApplyRuntimeRepairHandoffPeer(FallbackRepairWriteFixture fixture)
    {
        var leg = GetCurrentContextLeg(fixture.Context);
        var peer = new FileTransferFallbackTailYieldFrameV6
        {
            AuthoritySchema = FileTransferAuthoritySchema.Current,
            SessionId = fixture.Session.SessionId, TransferId = fixture.Session.TransferId, Direction = "outbound",
            TransportEpoch = leg.TransportEpochId, LiveRouteEpoch = leg.LiveRouteEpochId,
            FallbackLegGeneration = leg.Generation, BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            OwnerKey = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(fixture.Session.SessionId, fixture.Session.TransferId,
                leg.RouteSelection.TelemetryToken, leg.ProtocolVersion, leg.LiveRouteEpochId, leg.Generation,
                leg.BridgeRecoveryGeneration, leg.TransportEpochId, "(none)"),
            PairKey = FileTransferCoordinator.BuildFallbackTailYieldPairKey(fixture.Session.SessionId, fixture.Session.TransferId,
                leg.RouteSelection.TelemetryToken, leg.ProtocolVersion, leg.LiveRouteEpochId, leg.TransportEpochId, "(none)"),
            YieldGeneration = 1, AuthorityId = CreatePeerFallbackTailYieldAuthorityId(fixture.Session.TransferId),
            CommittedChunkIndex = 100, HighestObservedChunkIndex = 255, RecoveryMode = "fallback_tail_yield",
        };
        typeof(SessionFileTransferService).GetMethod("ApplyInboundFallbackTailYieldProof", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, peer]);
        Assert.NotNull(GetRuntimeRepairHandoff(fixture));
    }
}
