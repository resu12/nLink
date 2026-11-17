using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("send_failed")]
    [InlineData("send_cancelled")]
    [InlineData("session_replaced")]
    [InlineData("receipt_mutated")]
    [InlineData("wrong_probe")]
    [InlineData("wrong_transport")]
    [InlineData("semantic_mutation")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockResponderPeerProbe")]
    public async Task RuntimeUnlockResponderPeerProbe_EarlyConfirmationWaitsForRegistration(string outcome)
    {
        using var service = new SessionFileTransferService();
        var transferId = "early_confirmation_" + outcome;
        var sessionId = "session_" + transferId;
        const string probeId = "runtime-unlock-precommit:1:1:peer";
        using var transport = new PeerPreCommitAnswerAcceptanceTransport(
            sessionId, mutateDecisionReceipt: outcome == "receipt_mutated")
        {
            RuntimeUnlockRouteCommitSnapshot = new RuntimeUnlockRouteCommitSnapshot(
                sessionId, transferId, TransactionGeneration: 1, OfferGeneration: 6,
                PeerVisibleProof: true, PeerReceived: true, AnswerReceived: true,
                TargetRoute: FileTransferRoute.FileTunaV4,
                ProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
                TargetTransport: FileTransferTransportKind.Tuna,
                TransactionState: RuntimeUnlockTransactionState.AnswerReceived.ToString(),
                Reason: "test_early_confirmation",
                PathProbeId: "runtime-unlock-precommit:1:6:local",
                PathProbeState: RuntimeUnlockPathProbeState.Started.ToString(),
                PathProbeTransport: FileTransferTransportKind.Tuna,
                TunaPathLeaseRequired: true, TunaPathLeaseGeneration: 1,
                TunaPathLeaseState: RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
                TunaPathLeaseListenerRunId: "peer-offer-dialer-early-confirmation",
                TunaPathLeaseCurrent: true),
        };
        service.AttachTransport(transport);
        var context = CreateRegularNknV4InboundContext(transferId);
        SeedInitialRegularV4RuntimeScopeForTest(context, "test_early_confirmation");
        typeof(SessionFileTransferService).GetField("inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var handler = typeof(SessionFileTransferService).GetMethod(
            "TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var replacement = new RecordingDataSession(sessionId, transferId);
        Task<bool>? earlyConfirmation = null;
        FileTransferRuntimeUnlockPreCommitProbeAckFrame? confirmation = null;
        bool? completedInsideSend = null;
        bool? acceptanceVisibleInsideSend = null;
        bool? pendingVisibleInsideSend = null;
        RecordingDataSession? dataSession = null;
        using var session = dataSession = new RecordingDataSession(sessionId, transferId, (frame, _) =>
        {
            if (frame is not FileTransferRuntimeUnlockPreCommitProbeAckFrame { Reason: "precommit_probe_received" } ack)
                return;

            confirmation = ack with
            {
                Reason = "precommit_probe_commit_confirmed",
                ProbeId = outcome == "wrong_probe" ? "different-probe" : ack.ProbeId,
                Accepted = outcome != "semantic_mutation",
            };
            // Re-enter the actual Core handler before the ACK send returns. No sleeps or scheduler luck.
            earlyConfirmation = Dispatch(confirmation,
                outcome == "wrong_transport" ? FileTransferTransportKind.RegularNkn : FileTransferTransportKind.Tuna);
            completedInsideSend = earlyConfirmation.IsCompleted;
            acceptanceVisibleInsideSend = transport.AnswerAcceptanceReceipt is not null;
            pendingVisibleInsideSend = GetPrivatePropertyValue(context, "RuntimeUnlockPendingPeerPreCommitProbe") is not null;
            if (outcome == "session_replaced")
            {
                SetPrivateProperty(context, "DataSession", replacement);
                SetPrivateProperty(context, "DataSessionGeneration", 2L);
            }
            if (outcome == "send_failed")
                throw new IOException("test ACK send failure");
            if (outcome == "send_cancelled")
                throw new OperationCanceledException("test ACK send cancellation");
        });
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        var probe = new FileTransferRuntimeUnlockPreCommitProbeFrame
        {
            SessionId = sessionId, TransferId = transferId,
            TransactionGeneration = 1, OfferGeneration = 1, TunaPathLeaseGeneration = 2,
            ProbeId = probeId, TargetRoute = FileTransferRouteResolver.FileTunaV4Token,
            TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV4, TargetTransport = "tuna",
            HandoffKind = "normal_to_tuna_activation",
            SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var stateBefore = CreateCoordinatorStateForTest(context);
        var proofBefore = transport.RuntimeUnlockRouteCommitSnapshot;
        var logStart = GetOperationalLogLength();
        var probeTask = Dispatch(probe, FileTransferTransportKind.Tuna);
        if (outcome == "send_cancelled")
            await Assert.ThrowsAsync<OperationCanceledException>(() => probeTask);
        else
            Assert.True(await probeTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(earlyConfirmation);
        Assert.True(await earlyConfirmation.WaitAsync(TimeSpan.FromSeconds(5)));
        var invalidFrame = outcome is "wrong_probe" or "wrong_transport" or "semantic_mutation";
        Assert.Equal(invalidFrame, completedInsideSend);
        Assert.False(acceptanceVisibleInsideSend);
        Assert.False(pendingVisibleInsideSend);
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockPeerPreCommitProbeRegistration"));
        if (outcome is "send_failed" or "send_cancelled" or "session_replaced")
        {
            Assert.Null(transport.AnswerAcceptanceReceipt);
            Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockPendingPeerPreCommitProbe"));
        }

        if (outcome == "success")
        {
            Assert.Equal("acked:" + probeId + ":precommit_probe_commit_confirmed",
                Assert.Single(transport.RuntimeUnlockProbeEvents));
            var scope = Assert.Single(transport.RuntimeUnlockProbeResultScopes);
            var current = CreateCoordinatorStateForTest(context);
            Assert.Equal(current.CurrentRuntimeOwner, scope.RuntimeOwner);
            Assert.Equal(current.CurrentRoutePublicationToken, scope.RoutePublication);
            Assert.Equal(current.CurrentRuntimeUnlockAdmission, scope.Admission);
            var pending = GetPrivatePropertyValue(context, "RuntimeUnlockPendingPeerPreCommitProbe")!;
            Assert.Equal(confirmation, GetPrivatePropertyValue(pending, "CompletedCommitConfirmation"));
            Assert.DoesNotContain("runtime_unlock_precommit_probe_ack_peer_acceptance_not_current",
                ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            // A later duplicate remains idempotent after the registration wait.
            Assert.True(await Dispatch(confirmation!, FileTransferTransportKind.Tuna));
            Assert.Single(transport.RuntimeUnlockProbeEvents);
        }
        else
        {
            Assert.Empty(transport.RuntimeUnlockProbeEvents);
            Assert.Empty(transport.RuntimeUnlockProbeResultScopes);
            Assert.Equal(proofBefore, transport.RuntimeUnlockRouteCommitSnapshot);
            Assert.Equal(stateBefore.CurrentRuntimeOwner, CreateCoordinatorStateForTest(context).CurrentRuntimeOwner);
            Assert.Equal(stateBefore.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(context).CurrentRoutePublicationToken);
        }
        Assert.Empty(transport.RuntimeUnlockRouteCommitResults);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").TelemetryToken);

        Task<bool> Dispatch(FileTransferDataFrame frame, FileTransferTransportKind kind)
            => (Task<bool>)handler.Invoke(service,
                [sessionId, transferId, FileTransferDirection.Inbound, frame, kind, dataSession])!;
    }
}
