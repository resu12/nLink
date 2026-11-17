using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackStartupCheckpoint")]
    public async Task FallbackV6RuntimeStart_BindsStartupSuccessorButRejectsReplacementDuringOpen(bool replaceDuringOpen)
    {
        var transferId = "v6_start_publication_" + replaceDuringOpen;
        var sessionId = "session_" + transferId;
        using var transport = new RuntimeStartOpenBarrierTransport(sessionId)
            { IsPostTunaFileFallbackActiveForRouteSelection = true };
        using var peer = new LoopbackFileTransferTransport(sessionId)
            { IsPostTunaFileFallbackActiveForRouteSelection = true };
        transport.Connect(peer);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(transport);
        receiver.AttachTransport(peer);
        using var destination = new NonDisposingMemoryStream();
        var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheckpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferReceiverStateFrameV6 { RecoveryMode: "regular_nkn_state_refresh" })
            {
                checkpoint.TrySetResult();
                await releaseCheckpoint.Task.WaitAsync(ct);
            }
            return false;
        };
        var logStart = GetOperationalLogLength();
        try
        {
            await sender.TryStartSendAsync(new("startup.bin", 2048, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[2048], writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId,
                (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await transport.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var context = typeof(SessionFileTransferService).GetField("outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
            var opening = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
            if (replaceDuringOpen)
            {
                lock (GetServiceGate(sender))
                    ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }
            transport.ReleaseOpen.TrySetResult();
            if (replaceDuringOpen)
            {
                await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                    line.Contains("source=v6_sender_start;") &&
                    line.Contains("reason=runtime_start_data_session_opened;") &&
                    line.Contains("rejection_reason=route_publication_generation_mismatch;")));
                Assert.Empty(SparseRuntimeStartLines(logStart, transferId));
                Assert.Empty(transport.SentSessionOpens);
                Assert.Empty(transport.SentDataFrames);
                Assert.Null(GetPrivatePropertyValue(context, "DataSession"));
                return;
            }

            await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
            Assert.True(current.PublicationGeneration > opening.PublicationGeneration);
            var started = Assert.Single(SparseRuntimeStartLines(logStart, transferId));
            Assert.Contains($"route_publication_generation={current.PublicationGeneration};", started);
            Assert.Contains($"runtime_generation={current.RuntimeGeneration};", started);
            Assert.Contains($"leg_generation={current.LegGeneration};", started);
            Assert.False(GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").CanSendData);
            Assert.Empty(transport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());
            releaseCheckpoint.TrySetResult();
            await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
                receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 8000);
            Assert.Equal(new byte[2048], destination.ToArray());
        }
        finally
        {
            transport.ReleaseOpen.TrySetResult();
            releaseCheckpoint.TrySetResult();
        }
    }

    private sealed class RuntimeStartOpenBarrierTransport(string sessionId)
        : LoopbackFileTransferTransport(sessionId), IFileTransferSignalingTransport
    {
        public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IFileTransferDataSession> IFileTransferSignalingTransport.OpenFileTransferDataSessionAsync(
            string session, string transfer, CancellationToken ct)
        {
            var dataSession = await base.OpenFileTransferDataSessionAsync(session, transfer, ct);
            OpenEntered.TrySetResult();
            await ReleaseOpen.Task.WaitAsync(ct);
            return dataSession;
        }
    }
}
