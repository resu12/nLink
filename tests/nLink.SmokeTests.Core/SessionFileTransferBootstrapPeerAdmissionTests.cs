using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false, "current")] [InlineData(true, "current")]
    [InlineData(false, "local-send")] [InlineData(true, "local-send")]
    [InlineData(false, "wrong-fingerprint")] [InlineData(true, "wrong-fingerprint")]
    [InlineData(false, "replaced-frame")] [InlineData(true, "replaced-frame")]
    [InlineData(false, "provider")] [InlineData(true, "provider")]
    [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "checkpoint")] [InlineData(true, "checkpoint")]
    [InlineData(false, "wrong-source")] [InlineData(true, "wrong-source")]
    [InlineData(false, "missing-wake")] [InlineData(true, "missing-wake")]
    [InlineData(false, "other-bridge")] [InlineData(true, "other-bridge")]
    [InlineData(false, "wrong-session")] [InlineData(true, "wrong-session")]
    public async Task BootstrapPeerAdmission_ServiceRequiresExactStagedWakeBeforeHold(bool inbound, string variant)
    {
        const string transferId = "bootstrap_peer_admission";
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        using var otherBridge = new BootstrapPhaseTestBridge(session);
        object context;
        if (inbound)
        {
            var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
            context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
                beforeActivation: current => PinBootstrapServiceAgreement(current, issuer));
        }
        else
        {
            service.AttachTransport(bridge);
            context = CreateRegularNknV4OutboundContext(transferId, remoteNextExpectedChunkIndex: 4, chunksAcceptedForTransport: 4,
                remoteGrantedUntilExclusive: 4, lastGrantReceivedUtc: DateTimeOffset.UtcNow, seedRuntimeOwner: false, chunkCount: 128);
            SetPrivateProperty(context, "DataSession", session);
            SetPrivateProperty(context, "DataSessionGeneration", 17L);
            SetPrivateProperty(context, "V4LastStateEpoch", 3);
            SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 6);
            AdoptSourceFailureTunaForTest(service, context);
        }
        var state = CreateCoordinatorStateForTest(context);
        var peerIssuer = BootstrapPeerIssuerForTest(state);
        var peerId = $"fbc1:{peerIssuer}:371:{new string('b', 64)}";
        FileTransferFallbackBootstrapFrameV6 frame = inbound
            ? new FileTransferFallbackBootstrapRequestFrameV6 { SessionId = session.SessionId, TransferId = transferId,
                TransportEpoch = state.CurrentLeg!.TransportEpochId + 1, SenderRequestId = peerId }
            : new FileTransferFallbackBootstrapStateFrameV6 { SessionId = session.SessionId, TransferId = transferId,
                TransportEpoch = state.CurrentLeg!.TransportEpochId + 1, ReceiverChallengeId = peerId, Epoch = 3,
                ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 6, CreditUntilChunkIndexExclusive = 4,
                BytesCommitted = 4096, TransferPaused = true, TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode };
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapWakeFrameCurrent(state, frame));
        var bind = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method =>
            method.Name == "TryCreateFallbackV6ProofBootstrapPeerFrameBinding" && method.GetParameters()[0].ParameterType == context.GetType());
        object?[] args = [context, frame, state.Direction, null];
        Assert.True(Assert.IsType<bool>(bind.Invoke(null, args)));
        var binding = Assert.IsType<FileTransferFallbackV6ProofBootstrapPeerFrameBinding>(args[3]);
        SetPrivateProperty(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence", frame);
        SetPrivateProperty(context, "PendingFallbackV6ProofBootstrapPeerFrameBinding", binding);
        var evaluation = Assert.IsType<FileTransferFallbackV6ProofBootstrapEvaluation>(typeof(SessionFileTransferService)
            .GetMethod("CreateBootstrapCorrelationWakeEvaluation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [state, binding]));
        if (variant == "local-send") { evaluation = new(state.SessionId, state.Direction, 1, 1, "tuna_unavailable_before_send", "file_transfer_v4_state_frame_sent", 10000); }
        if (variant == "wrong-fingerprint") { evaluation = evaluation with { TriggerFrameFingerprint = new string('c', 64) }; }
        if (variant == "wrong-source") { evaluation = evaluation with { Source = "tuna_unavailable_before_send" }; }
        if (variant == "wrong-session") { evaluation = evaluation with { SessionId = "other-session" }; }
        if (variant == "missing-wake")
        {
            SetPrivateProperty(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence", null);
            SetPrivateProperty(context, "PendingFallbackV6ProofBootstrapPeerFrameBinding", null);
        }
        if (variant == "replaced-frame")
        {
            var replacement = inbound ? frame with { SenderRequestId = peerId.Replace(new string('b', 64), new string('c', 64)) }
                : frame with { ReceiverChallengeId = peerId.Replace(new string('b', 64), new string('c', 64)) };
            SetPrivateProperty(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence", replacement);
        }
        if (variant == "provider") { session.Generation++; }
        if (variant == "session") { SetPrivateProperty(context, "DataSessionGeneration", state.CurrentDataSessionGeneration + 1); }
        if (variant == "checkpoint") { state.CurrentLeg!.CheckpointGeneration++; }
        var before = CreateCoordinatorStateForTest(context);
        var paused = GetPrivatePropertyValue(context, "PullTransportPaused");
        try
        {
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, variant == "other-bridge" ? otherBridge : bridge, evaluation);
            var after = CreateCoordinatorStateForTest(context);
            if (variant == "current")
            {
                var execution = Assert.Single(bridge.Executions);
                Assert.Equal(inbound ? FileTransferBootstrapPhase.Response : FileTransferBootstrapPhase.Solicit, execution.Command.Phase);
                Assert.Equal("correlated_bootstrap_peer_frame", execution.Command.Bootstrap.Observation.Source);
                Assert.Equal(frame, BootstrapPeerHoldForTest(after)!.Command.PeerWake);
            }
            else
            {
                Assert.Empty(bridge.Executions);
                AssertBootstrapPeerStateUnchangedForTest(before, after);
                Assert.Null(GetPrivatePropertyValue(context, "BootstrapSourceHoldExecution"));
                Assert.Equal(paused, GetPrivatePropertyValue(context, "PullTransportPaused"));
                Assert.Empty(session.SentFrames);
            }
            Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
            Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
            Assert.Empty(otherBridge.Executions);
            if (variant == "local-send")
            {
                // The rejected transport observation must not poison a later
                // real received-frame wake or be relabeled as its authority.
                Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod("TryHandleBootstrapCorrelationFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [state.Direction, session, new FileTransferReceivedDataFrame(frame, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)])));
                var phase = Assert.Single(bridge.Executions);
                Assert.Equal("correlated_bootstrap_peer_frame", phase.Command.Bootstrap.Observation.Source);
                Assert.Equal(frame, BootstrapPeerHoldForTest(CreateCoordinatorStateForTest(context))!.Command.PeerWake);
            }
        }
        finally
        {
            foreach (var phase in bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(phase.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "modeled_cleanup_no_submission");
                phase.Delivery.TrySetResult(held); phase.Settlement.TrySetResult(held);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string BootstrapPeerIssuerForTest(FileTransferCoordinatorState state)
    {
        var agreement = state.PersistentState.FileTransferRouteTransition.BootstrapAgreement!;
        return (state.Direction == FileTransferDirection.Inbound ? agreement.Offer.FallbackBootstrapIssuerId : agreement.Accept!.FallbackBootstrapIssuerId)!;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferBootstrapSourceHoldOperation? BootstrapPeerHoldForTest(FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertBootstrapPeerStateUnchangedForTest(FileTransferCoordinatorState before, FileTransferCoordinatorState after)
        => Assert.Equal(before.PersistentState.FileTransferRouteTransition, after.PersistentState.FileTransferRouteTransition);
}
