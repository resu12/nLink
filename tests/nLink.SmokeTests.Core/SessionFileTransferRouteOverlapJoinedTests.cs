using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    public async Task JoinedFallbackYield_LoopbackHarnessStopsAtMissingRetryAuthorityBridge()
        => await RunJoinedFallbackYieldAsync(withRetryBridge: false);

    [Fact]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockRetryDispatch")]
    public async Task JoinedFallbackYield_ExactQueuedRequestInstallsOnlySenderRetryAuthority()
        => await RunJoinedFallbackYieldAsync(withRetryBridge: true);

    [Fact]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockRetryDispatch")]
    public async Task JoinedFallbackYield_MismatchedInstallResponseCannotInstallRetryAuthority()
        => await RunJoinedFallbackYieldAsync(withRetryBridge: true, RetryBridgeFault.MismatchedInstallReceipt);

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockRetryDispatch")]
    public async Task JoinedFallbackYield_RejectedConfirmationCompensatesCurrentInstallWithoutReusingRelease(FileTransferDirection retryDirection)
        => await RunJoinedFallbackYieldAsync(withRetryBridge: true, RetryBridgeFault.ConfirmationRejected, retryDirection);

    private enum RetryBridgeFault { None, MismatchedInstallReceipt, ConfirmationRejected }

    private async Task RunJoinedFallbackYieldAsync(bool withRetryBridge, RetryBridgeFault fault = RetryBridgeFault.None,
        FileTransferDirection retryDirection = FileTransferDirection.Outbound)
    {
        var transferId = withRetryBridge
            ? "transfer_joined_fallback_yield_retry_" + fault + "_" + retryDirection
            : "transfer_joined_fallback_yield_harness_boundary";
        var sessionId = "session_" + transferId;
        using PeerVisibleControlPlaneLoopbackTransport senderTransport = withRetryBridge && retryDirection == FileTransferDirection.Outbound
            ? new QueuedRetryAuthorityLoopbackTransport(sessionId)
            : new PeerVisibleControlPlaneLoopbackTransport(sessionId);
        using PeerVisibleControlPlaneLoopbackTransport receiverTransport = withRetryBridge && retryDirection == FileTransferDirection.Inbound
            ? new QueuedRetryAuthorityLoopbackTransport(sessionId)
            : new PeerVisibleControlPlaneLoopbackTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var outbound = CreatePostTunaFallbackV6OutboundContext(transferId,
            startCoordinatorSnapshotStream: false);
        var inbound = CreatePostTunaFallbackV6InboundContext(transferId);
        var senderData = await senderTransport.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);
        var receiverData = await receiverTransport.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);

        // Only the initial, quiescent fallback contexts are seeded. All local
        // yield decisions, emitted frames and opposite-peer proof receipts below
        // come from the service/Coordinator. This is a harness prerequisite test,
        // not a joined sender-commit/old-request/supersession positive.
        foreach (var (context, data) in new[] { (outbound, senderData), (inbound, receiverData) })
        {
            SetPrivateProperty(context, "DataSession", data);
            SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
            TrySetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", false);
        }
        SetPrivateProperty(inbound, "V4LastStateDurableHighestChunkIndex", 255);
        typeof(SessionFileTransferService).GetField("outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, outbound);
        typeof(SessionFileTransferService).GetField("inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(receiver, inbound);

        // The inbound constructor starts an empty-context stream. Close/drain
        // that setup stream, then describe the seeded source with a fresh real
        // emitter, as in the checkpoint-continuity evidence fixture. No yield,
        // pairing or retry decision has occurred yet.
        var setupEmitter = GetPrivatePropertyValue(inbound, "CoordinatorSnapshotEmitter")!;
        var emitterType = setupEmitter.GetType();
        emitterType.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(setupEmitter,
            [GetPrivateProperty<string>(inbound, "CoordinatorInstanceId"), sessionId, transferId, FileTransferDirection.Inbound]);
        await WaitUntilAsync(() => (int)emitterType.GetField("drainScheduled",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(setupEmitter)! == 0, timeoutMs: 3000);
        var coordinatorId = Guid.NewGuid().ToString("N");
        inbound.GetType().GetField("<CoordinatorInstanceId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inbound, coordinatorId);
        inbound.GetType().GetField("<CoordinatorSnapshotEmitter>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inbound, Activator.CreateInstance(emitterType, nonPublic: true));
        SetPrivateProperty(inbound, "CoordinatorPersistentState", new FileTransferCoordinatorPersistentState
        {
            CoordinatorInstanceId = coordinatorId,
        });
        StartCoordinatorSnapshotStreamForTest(outbound);
        StartCoordinatorSnapshotStreamForTest(inbound);
        var senderSource = CreateCoordinatorStateForTest(outbound);
        var receiverSource = CreateCoordinatorStateForTest(inbound);
        foreach (var source in new[] { senderSource, receiverSource })
        {
            Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(source),
                source.PersistentState.SnapshotStateFingerprint);
        }
        var retryService = retryDirection == FileTransferDirection.Outbound ? sender : receiver;
        var retryContext = retryDirection == FileTransferDirection.Outbound ? outbound : inbound;
        var retrySource = retryDirection == FileTransferDirection.Outbound ? senderSource : receiverSource;
        var retryData = retryDirection == FileTransferDirection.Outbound ? senderData : receiverData;
        var retryBridge = (retryDirection == FileTransferDirection.Outbound ? senderTransport : receiverTransport)
            as QueuedRetryAuthorityLoopbackTransport;
        retryBridge?.BindCoreContext(retryService, retryContext);
        if (retryBridge is not null)
        {
            retryBridge.ReturnMismatchedInstallReceipt = fault == RetryBridgeFault.MismatchedInstallReceipt;
            retryBridge.RejectConfirmation = fault == RetryBridgeFault.ConfirmationRejected;
        }
        Assert.Equal(withRetryBridge && retryDirection == FileTransferDirection.Outbound,
            senderTransport is IRuntimeUnlockRetryDispatchAuthorityBridge);
        Assert.Equal(withRetryBridge && retryDirection == FileTransferDirection.Inbound,
            receiverTransport is IRuntimeUnlockRetryDispatchAuthorityBridge);
        var logStart = GetOperationalLogLength();
        try
        {
            var senderYield = IssueAndQueueYield(sender, outbound, "Outbound");
            var receiverYield = IssueAndQueueYield(receiver, inbound, "Inbound");
            Assert.NotEqual(senderYield.AuthorityId, receiverYield.AuthorityId);
            Assert.Equal(senderYield.PairKey, receiverYield.PairKey);

            // Consume actual production control-plane sends from the connected
            // loopback queues. No fabricated peer authority or accepted receipt
            // supplies either side of this pairing.
            using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var atReceiver = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(
                await receiverData.ReceiveAsync(receiveTimeout.Token));
            var atSender = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(
                await senderData.ReceiveAsync(receiveTimeout.Token));
            Assert.True(senderTransport.LastValidationAccepted);
            Assert.True(receiverTransport.LastValidationAccepted);
            Assert.Equal(senderYield.AuthorityId.ToString(), atReceiver.AuthorityId);
            Assert.Equal(receiverYield.AuthorityId.ToString(), atSender.AuthorityId);
            Assert.Equal("outbound", atReceiver.Direction);
            Assert.Equal("inbound", atSender.Direction);
            ApplyPeerYield(receiver, inbound, "Inbound", atReceiver);
            ApplyPeerYield(sender, outbound, "Outbound", atSender);
            await WaitUntilAsync(() => HasPairedFallbackTailYieldInAggregate(outbound) &&
                HasPairedFallbackTailYieldInAggregate(inbound), timeoutMs: 5000);

            AssertPairAndUnchangedSource(sender, outbound, senderSource, senderYield, receiverYield, senderData);
            AssertPairAndUnchangedSource(receiver, inbound, receiverSource, receiverYield, senderYield, receiverData);
            if (retryBridge is not null)
            {
                var localYield = retryDirection == FileTransferDirection.Outbound ? senderYield : receiverYield;
                var peerYield = retryDirection == FileTransferDirection.Outbound ? receiverYield : senderYield;
                var request = Assert.IsType<RuntimeUnlockRetryDispatchAuthorityRequest>(retryBridge.CapturedRequest);
                Assert.Equal(retryDirection, request.Direction);
                Assert.Equal(localYield, request.FallbackPairedTailYieldEvidence!.Value.LocalDecision);
                Assert.Equal(peerYield.AuthorityId, request.FallbackPairedTailYieldEvidence.Value.PeerAuthorityId);
                Assert.True(retryBridge.QueueRuntimeUnlockRetryDispatchAuthorityRequest(request));
                Assert.False(retryBridge.QueueRuntimeUnlockRetryDispatchAuthorityRequest(request with { TransferId = "foreign-transfer" }));
                Assert.False(retryBridge.QueueRuntimeUnlockRetryDispatchAuthorityRequest(request with
                {
                    DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
                }));
                Assert.Equal(request, retryBridge.CapturedRequest);
                lock (GetServiceGate(retryService))
                {
                    Assert.Throws<InvalidOperationException>(() => retryBridge.DrainQueuedAuthorityRequest());
                }
                Assert.Null(retryBridge.LastCommittedReceipt);
                Assert.True(retryBridge.DrainQueuedAuthorityRequest());
                Assert.False(retryBridge.LastStageObservedUnderCoreGate);
                Assert.True(retryBridge.LastCommitObservedUnderCoreGate);
                if (fault == RetryBridgeFault.ConfirmationRejected)
                {
                    Assert.True(retryBridge.LastConfirmationObservedUnderCoreGate);
                    Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, retryBridge.LastDecision!.Value.Status);
                    Assert.Equal("retry_dispatch_install_confirmation_rejected", retryBridge.LastDecision.Value.Reason);
                    Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Installed, retryBridge.LastInstallResult!.Value.Status);
                    Assert.Null(retryBridge.LastCommittedReceipt);
                    Assert.Null(retryBridge.InfraCurrentContract);
                    Assert.Equal(0, retryBridge.InstallCount);
                    Assert.False(retryBridge.DrainQueuedAuthorityRequest());
                    var compensated = CreateCoordinatorStateForTest(retryContext);
                    Assert.Null(compensated.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
                    Assert.Null(compensated.PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt);
                    Assert.Null(compensated.PersistentState.RuntimeUnlockRetryLifecycle.CurrentInstalledAuthority);
                    var terminal = Assert.IsType<RuntimeUnlockRetryLifecycleAttemptSnapshot>(
                        compensated.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt);
                    Assert.Equal(RuntimeUnlockRetryLifecycleAttemptState.Invalidated, terminal.State);
                    Assert.Equal(retryBridge.LastInstallResult.Value.Receipt, terminal.InstallReceipt);
                    Assert.Equal(request, terminal.Request);
                    Assert.Equal(terminal.InstallReceipt, compensated.PersistentState.FallbackTailYield.LastConsumingInstallReceipt);
                    Assert.NotNull(compensated.PersistentState.FallbackTailYield.LastConsumedReservation);
                    Assert.Null(compensated.PersistentState.FallbackTailYield.CurrentReservation);
                    Assert.Equal(FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
                        compensated.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
                    Assert.Equal(retryBridge.LastConfirmationCoordinatorGeneration + 1,
                        compensated.PersistentState.SnapshotStateGeneration);
                    var emitter = GetPrivatePropertyValue(retryContext, "CoordinatorSnapshotEmitter")!;
                    Assert.Equal(compensated.PersistentState.SnapshotStateGeneration,
                        emitterType.GetField("finalGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(emitter));
                    var storedEvidenceHash = emitterType.GetMethod("EvidenceToken", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [compensated.PersistentState.SnapshotStateFingerprint]);
                    Assert.Equal(storedEvidenceHash,
                        emitterType.GetField("finalHash", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(emitter));
                    // The graph fingerprint and chained stream fingerprint are
                    // different domains after the initial snapshot.
                    Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(retryBridge.LastDecision.Value.State),
                        FileTransferCoordinator.CalculateSnapshotStateFingerprint(compensated));
                    AssertPairAndUnchangedSource(retryService, retryContext, retrySource, localYield, peerYield, retryData);
                    Assert.True(retryBridge.QueueRuntimeUnlockRetryDispatchAuthorityRequest(request));
                    Assert.False(retryBridge.DrainQueuedAuthorityRequest());
                    Assert.Equal(compensated.PersistentState, CreateCoordinatorStateForTest(retryContext).PersistentState);
                }
                else if (fault != RetryBridgeFault.None)
                {
                    Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, retryBridge.LastDecision!.Value.Status);
                    Assert.Equal("retry_dispatch_install_commit_rejected", retryBridge.LastDecision.Value.Reason);
                    Assert.Null(retryBridge.LastCommittedReceipt);
                    Assert.Null(retryBridge.InfraCurrentContract);
                    Assert.Equal(0, retryBridge.InstallCount);
                    Assert.False(retryBridge.DrainQueuedAuthorityRequest());
                    var rejected = CreateCoordinatorStateForTest(outbound);
                    Assert.Null(rejected.PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt);
                    Assert.Null(rejected.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
                    Assert.Null(rejected.PersistentState.RuntimeUnlockRetryLifecycle.CurrentInstalledAuthority);
                    Assert.Null(rejected.PersistentState.FallbackTailYield.LastConsumingInstallReceipt);
                    Assert.Equal(senderSource.CurrentRuntimeOwner, rejected.CurrentRuntimeOwner);
                    Assert.Equal(senderSource.CurrentRoutePublicationToken, rejected.CurrentRoutePublicationToken);
                    Assert.Equal(senderSource.CurrentDataSessionIdentity, rejected.CurrentDataSessionIdentity);
                    Assert.Equal(senderSource.CurrentDataSessionGeneration, rejected.CurrentDataSessionGeneration);
                    Assert.Equal(FileTransferRoute.PostTunaFallbackV6, rejected.RouteSelection.Route);
                    Assert.Null(rejected.PersistentState.FileTransferRouteTransition.CurrentOperation);
                    Assert.Equal(100, rejected.CurrentLeg!.ProvenCommittedChunkIndex);
                    Assert.Equal(255, rejected.CurrentLeg.ProvenHighestObservedChunkIndex);
                    AssertPairAndUnchangedSource(receiver, inbound, receiverSource, receiverYield, senderYield, receiverData);
                }
                else
                {
                    Assert.True(retryBridge.LastConfirmationObservedUnderCoreGate);
                    Assert.True(retryBridge.LastDecision!.Value.Granted, retryBridge.LastDecision.Value.Reason);
                    var receipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(retryBridge.LastCommittedReceipt);
                    var state = CreateCoordinatorStateForTest(outbound);
                    var installed = state.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt!.Value;
                    Assert.Equal(request, installed.Request);
                    Assert.Equal(receipt, installed.InstallReceipt);
                    Assert.Equal(receipt.Authority, installed.Authority);
                    Assert.Equal(RuntimeUnlockRetryLifecycleAttemptState.Installed, installed.State);
                    Assert.Equal(receipt, state.PersistentState.FallbackTailYield.LastConsumingInstallReceipt);
                    Assert.NotNull(state.PersistentState.FallbackTailYield.LastConsumedReservation);
                    Assert.Null(state.PersistentState.FallbackTailYield.CurrentReservation);
                    Assert.Null(state.PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt);
                    Assert.Equal(1, retryBridge.InstallCount);
                    Assert.Same(retryBridge.LastDecision.Value.Contract, retryBridge.InfraCurrentContract);
                    Assert.True(retryBridge.QueueRuntimeUnlockRetryDispatchAuthorityRequest(request));
                    Assert.False(retryBridge.DrainQueuedAuthorityRequest());
                    Assert.Equal(receipt, retryBridge.LastCommittedReceipt);
                    Assert.Equal(1, retryBridge.InstallCount);
                    Assert.Equal(state.PersistentState, CreateCoordinatorStateForTest(outbound).PersistentState);
                    Assert.Equal(senderSource.CurrentRuntimeOwner, state.CurrentRuntimeOwner);
                    Assert.Equal(senderSource.CurrentRoutePublicationToken, state.CurrentRoutePublicationToken);
                    Assert.Equal(FileTransferRoute.PostTunaFallbackV6, state.RouteSelection.Route);
                    Assert.Null(state.PersistentState.FileTransferRouteTransition.CurrentOperation);
                    AssertPairAndUnchangedSource(receiver, inbound, receiverSource, receiverYield, senderYield, receiverData);
                }
            }
            var log = ReadOperationalLogTail(logStart);
            foreach (var direction in new[] { "outbound", "inbound" })
            {
                Assert.Contains(log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), line =>
                    line.Contains("event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested;", StringComparison.Ordinal) &&
                    line.Contains($"direction={direction};", StringComparison.Ordinal) &&
                    line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("source_route=post_tuna_fallback_v6; source_protocol_version=6;", StringComparison.Ordinal) &&
                    line.Contains(withRetryBridge && direction == retryDirection.ToString().ToLowerInvariant()
                        ? "decision_status=Queued; decision_reason=fallback_paired_tail_yield_request_queued"
                        : "decision_status=Rejected; decision_reason=fallback_paired_tail_yield_request_queue_rejected", StringComparison.Ordinal));
            }
            Assert.DoesNotContain("event=filetransfer_route_transition_committed;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_progress_observed;", log, StringComparison.Ordinal);
        }
        finally
        {
            TryCancelContextLifetime(outbound);
            TryCancelContextLifetime(inbound);
        }

        static FallbackTailYieldDecision IssueAndQueueYield(SessionFileTransferService service, object context, string direction)
        {
            lock (GetServiceGate(service))
            {
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                object?[] args = [context, leg, leg.TransportEpochId, 100, 255, "joined_harness_tail_yield", null];
                Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                    $"TryMark{direction}FallbackTailQuiescentWithCoordinatorLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)));
                var decision = Assert.IsType<FallbackTailYieldDecision>(args[^1]);
                Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                    $"Record{direction}FallbackTailLocalYieldLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, leg, decision, 100, 255, "joined_harness_tail_yield"])));
                return decision;
            }
        }

        static void ApplyPeerYield(SessionFileTransferService service, object context, string direction,
            FileTransferFallbackTailYieldFrameV6 frame)
        {
            typeof(SessionFileTransferService).GetMethod($"Apply{direction}FallbackTailYieldProof",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, frame]);
        }

        static void AssertPairAndUnchangedSource(SessionFileTransferService service, object context,
            FileTransferCoordinatorState before, FallbackTailYieldDecision local, FallbackTailYieldDecision peer,
            IFileTransferDataSession data)
        {
            lock (GetServiceGate(service))
            {
                var after = CreateCoordinatorStateForTest(context);
                var operation = Assert.IsType<FileTransferFallbackTailYieldOperation>(
                    after.PersistentState.FallbackTailYield.CurrentOperation);
                var release = Assert.IsType<FileTransferFallbackTailPairedReleaseReceipt>(operation.PairedRelease);
                Assert.Equal(local.AuthorityId, release.LocalProof.Decision.AuthorityId);
                Assert.Equal(peer.AuthorityId, release.PeerProof.Proof.AuthorityId);
                Assert.Equal(peer.YieldGeneration, release.PeerProof.Proof.YieldGeneration);
                Assert.Equal(local.PairKey, release.PeerProof.Proof.PairKey);
                Assert.Equal(before.RouteSelection, after.RouteSelection);
                Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
                Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
                Assert.Equal(before.CurrentRouteCommit, after.CurrentRouteCommit);
                var oldEpoch = Assert.IsType<LiveRouteEpoch>(before.CurrentLiveRouteEpoch);
                var currentEpoch = Assert.IsType<LiveRouteEpoch>(after.CurrentLiveRouteEpoch);
                Assert.Equal((oldEpoch.EpochId, oldEpoch.RouteSelection, oldEpoch.HandoffKind,
                        oldEpoch.TargetTransport, oldEpoch.Reason, oldEpoch.State, oldEpoch.RoutePublicationToken),
                    (currentEpoch.EpochId, currentEpoch.RouteSelection, currentEpoch.HandoffKind,
                        currentEpoch.TargetTransport, currentEpoch.Reason, currentEpoch.State, currentEpoch.RoutePublicationToken));
                Assert.Equal(before.CurrentDataSessionIdentity, after.CurrentDataSessionIdentity);
                Assert.Equal(before.CurrentDataSessionGeneration, after.CurrentDataSessionGeneration);
                Assert.Same(data, GetPrivatePropertyValue(context, "DataSession"));
                Assert.Equal(100, after.CurrentLeg!.ProvenCommittedChunkIndex);
                Assert.Equal(255, after.CurrentLeg.ProvenHighestObservedChunkIndex);
                Assert.Null(after.PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt);
                Assert.Null(after.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
                Assert.Null(after.PersistentState.RuntimeUnlockRetryLifecycle.CurrentInstalledAuthority);
                Assert.Null(after.PersistentState.FileTransferRouteTransition.CurrentOperation);
                Assert.Null(after.PersistentState.FileTransferRouteTransition.LastTerminalOperation);
            }
        }
    }
}
