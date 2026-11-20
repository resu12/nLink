using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(true, "first-fails")]
    [InlineData(true, "second-fails")]
    [InlineData(true, "replace-at-writer")]
    [InlineData(true, "overlap-zero")]
    [InlineData(true, "overlap-one")]
    [InlineData(true, "overlap-revoked")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockResponderPeerProbe")]
    public async Task PostFallbackCommitConfirmation_RepliesToPeerOfferOneOnlyForBoundLocalOfferSeven(bool commitFirst, string fault)
    {
        const string file = "paired_confirmation";
        var session = $"session_{file}";
        const string probeId = "runtime-unlock-precommit:1:1:peer";
        const string listener = "peer-offer-dialer-paired-confirmation";
        using var service = new SessionFileTransferService();
        var atWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlap = fault.StartsWith("overlap-", StringComparison.Ordinal);
        var attempts = 0;
        using var peer = new RecordingDataSession(session, file, allowReplyAtWriter: async attempt =>
        {
            Interlocked.Increment(ref attempts);
            if ((fault == "replace-at-writer" || overlap) && attempt == 1)
            {
                atWriter.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return !((fault is "first-fails" or "overlap-zero") && attempt == 1 ||
                     (fault is "second-fails" or "overlap-one") && attempt == 2);
        });
        using var transport = new PeerPreCommitAnswerAcceptanceTransport(session);
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6InboundContext(file, session, peer);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 99); // Frontier is chunk 100.
        SeedPairedFallbackTailYieldForTest(context, GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg"));
        SeedRuntimeUnlockAdmissionForTest(context, FileTransferDirection.Inbound, "paired_confirmation");
        var type = typeof(SessionFileTransferService);
        type.GetField("inboundTransfer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, context);
        var handler = type.GetMethod("TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task<bool> Receive(FileTransferDataFrame frame) => (Task<bool>)handler.Invoke(service,
            [session, file, FileTransferDirection.Inbound, frame, FileTransferTransportKind.Tuna, peer])!;
        var proof = new RuntimeUnlockRouteCommitSnapshot(session, file, 1, 7, true, true, true,
            FileTransferRoute.FileTunaV4, 4, FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna, "AnswerReceived", "paired_confirmation",
            PathProbeId: probeId, PathProbeState: "Started", PathProbeTransport: FileTransferTransportKind.Tuna,
            TunaPathLeaseRequired: true, TunaPathLeaseGeneration: 3, TunaPathLeaseState: "ListenerReady",
            TunaPathLeaseListenerRunId: listener, TunaPathLeaseCurrent: true, LaunchGeneration: 1);
        transport.RuntimeUnlockRouteCommitSnapshot = proof;
        Assert.True(await Receive(new FileTransferRuntimeUnlockPreCommitProbeFrame
        {
            SessionId = session, TransferId = file, TransactionGeneration = 1, OfferGeneration = 1,
            TunaPathLeaseGeneration = 2, ProbeId = probeId, TargetRoute = "file_tuna_v4",
            TargetProtocolVersion = 4, TargetTransport = "tuna", HandoffKind = "normal_to_tuna_activation",
            SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }));
        var ack = Assert.Single(peer.SentFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeAckFrame>());
        var confirmation = ack with { Reason = "precommit_probe_commit_confirmed" };
        var receipt = Assert.IsType<RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt>(transport.AnswerAcceptanceReceipt);
        var observation = new RuntimeUnlockResponderPeerProbeRouteTransitionObservation(session, file, 1, 7, 1, 1, 1,
            "file", "SemanticAnswerAck", "answer", new string('a', 64), receipt.ProbeFrameFingerprint,
            3, listener, probeId, RuntimeUnlockPathProbeState.Acked, FileTransferTransportKind.Tuna,
            receipt.ProbeAckSentUtcMs, FileTransferRoute.FileTunaV4, 4,
            FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna, receipt.ReceiveScope);
        async Task CommitLocal()
        {
            var converged = proof with { TransactionState = "RouteCommitPending", PathProbeState = "Acked",
                PathProbeAckedUtcMs = observation.ProbeAckedUtcMs, ResponderPeerProbeRouteTransitionObservation = observation };
            transport.RuntimeUnlockRouteCommitSnapshot = converged;
            await (Task)type.GetMethod("HandleDataSessionAvailabilityChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [peer, new FileTransferDataSessionAvailabilityChangedEventArgs(true, "paired_confirmation", true,
                    FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna, converged)])!;
            await WaitUntilAsync(() => GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route == FileTransferRoute.FileTunaV4, 5000);
            Assert.Equal(7, GetPrivateProperty<RouteCommitToken>(context, "RuntimeUnlockRouteCommitToken").OfferGeneration);
        }
        FileTransferRuntimeUnlockPreCommitProbeAckFrame[] Replies() => peer.SentFrames
            .OfType<FileTransferRuntimeUnlockPreCommitProbeAckFrame>().Where(f => f.Reason == "precommit_probe_commit_confirmed_reply").ToArray();
        if (commitFirst) await CommitLocal();
        Assert.True(await Receive(confirmation with { OfferGeneration = 7 }));
        Assert.Empty(Replies());
        var matching = Receive(confirmation);
        if (overlap)
        {
            try
            {
                await atWriter.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await Receive(confirmation with { SentUnixTimeMilliseconds = confirmation.SentUnixTimeMilliseconds + 1 }));
                Assert.True(await Receive(confirmation));
                Assert.Equal(1, Volatile.Read(ref attempts));
                Assert.Empty(Replies());
                if (fault == "overlap-revoked")
                    lock (GetServiceGate(service)) SetPrivateProperty(context, "CurrentRoutePublicationToken", null);
            }
            finally { release.TrySetResult(); }
        }
        if (fault == "replace-at-writer")
        {
            await atWriter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Same channel, but the original coordinator publication is no longer current.
            SetPrivateProperty(context, "CurrentRoutePublicationToken", null);
            release.TrySetResult();
            Assert.True(await matching);
            Assert.Empty(Replies());
            Assert.Empty(transport.RuntimeUnlockRouteCommitResults);
            Assert.True(await Receive(confirmation));
            Assert.Empty(Replies());
            return;
        }
        Assert.True(await matching);
        if (fault == "overlap-revoked")
        {
            Assert.Empty(Replies());
            Assert.Equal(1, attempts);
            Assert.Empty(transport.RuntimeUnlockRouteCommitResults);
            return;
        }
        if (overlap) Assert.Equal(fault == "overlap-zero" ? 3 : 2, attempts);
        if (fault == "first-fails")
        {
            Assert.Empty(Replies());
            Assert.Empty(transport.RuntimeUnlockRouteCommitResults);
            Assert.True(await Receive(confirmation with { SentUnixTimeMilliseconds = confirmation.SentUnixTimeMilliseconds + 1 }));
        }
        if (!commitFirst)
        {
            Assert.Empty(Replies());
            await CommitLocal();
        }
        var expectedCopies = fault is "second-fails" or "overlap-one" ? 1 : 2;
        await WaitUntilAsync(() => Replies().Length >= expectedCopies, 5000);
        await WaitUntilAsync(() => !transport.RuntimeUnlockRouteCommitResults.IsEmpty);
        Assert.Equal("accepted:1:7:precommit_probe_commit_confirmed_reply_sent",
            Assert.Single(transport.RuntimeUnlockRouteCommitResults));
        Assert.All(Replies(), reply =>
        {
            Assert.Equal(1, reply.OfferGeneration);
            Assert.Equal(confirmation.TransactionGeneration, reply.TransactionGeneration);
            Assert.Equal(confirmation.TunaPathLeaseGeneration, reply.TunaPathLeaseGeneration);
            Assert.Equal(probeId, reply.ProbeId);
            Assert.Equal(file, reply.TransferId);
        });
        Assert.True(await Receive(confirmation with { OfferGeneration = 2 }));
        Assert.True(await Receive(confirmation));
        Assert.Equal(expectedCopies, Replies().Length);
    }
}
