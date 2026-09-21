using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("missing_peer")]
    [InlineData("missing_snapshot_observation")]
    [InlineData("wrong_publication")]
    [InlineData("wrong_data_session_generation")]
    [InlineData("wrong_transition_mode")]
    [Trait("FileTransferRuntimeSuite", "InboundPublicationTransition")]
    public async Task SecondTunaPublication_ServiceRequiresPairedYieldBeforeResponderCommit(string control)
        => await CreateSecondTunaPublicationEvidenceFixtureAsync(control);

    internal static async Task<string> CreateSecondTunaPublicationEvidenceFixtureAsync(
        string control = "none", bool replayPostCommitCallbacks = true,
        string? sessionId = null, string? suppliedTransferId = null,
        Func<IReadOnlyList<FileTransferRouteHintNotification>, Task<FileTransferFallbackTailYieldFrameV6>>? receivePeerYield = null,
        Func<RoutePublicationToken, IReadOnlyList<FileTransferRouteHintNotification>, Task>? afterSecondTuna = null,
        Func<FileTransferFallbackTailYieldFrameV6, Task>? sendLocalYield = null,
        Func<IReadOnlyList<FileTransferRouteHintNotification>, string?, Task>? prepareFinalFallbackReceive = null,
        Func<CancellationToken, ValueTask<FileTransferReceivedDataFrame>>? receiveData = null)
    {
        var transferId = suppliedTransferId ?? "second_tuna_service_publication_" + control;
        const string reason = "second_tuna_service_fixture";
        using var receiver = new SessionFileTransferService();
        using var receiverData = new InitialBootstrapRecordingDataSession(sessionId ?? "session_" + transferId, transferId)
        {
            ReceiveWithMetadataForTest = receiveData,
        };
        using var receiverBridge = new InitialBootstrapCheckpointTransport(receiverData);
        var destinationPath = prepareFinalFallbackReceive is null ? null : Path.Combine(
            Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!, "durable-receiver.bin");
        using var destination = destinationPath is null ? null : new FileStream(destinationPath,
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        destination?.SetLength(256L * 21 * 1024);
        var logStart = GetOperationalLogLength();
        var inbound = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(
            receiver, receiverBridge, receiverData, receiverBarrier: true,
            beforeActivation: receivePeerYield is null ? null : context =>
            {
                // Match the normal service context wiring: committed tokens
                // pass through its production publication forwarder.
                var forward = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(method => method.Name == "ForwardCoordinatorIssuedRoutePublicationAuthorityLocked" &&
                        method.GetParameters()[0].ParameterType.Name == "InboundTransferContext");
                SetPrivateProperty(context, "RoutePublicationAuthorityForwarder",
                    new Action<RoutePublicationToken, string>((token, source) => forward.Invoke(receiver, [context, token, source])));
                if (destination is not null)
                {
                    SetPrivateProperty(context, "WriteStream", destination);
                    SetPrivateProperty(context, "BytesTransferred", 100L * 21 * 1024);
                    SetPrivateProperty(context, "ReceiverSparseBytesWritten", 100L * 21 * 1024);
                    SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(
                        GetPrivatePropertyValue(context, "V6DestinationMode")!.GetType(), "SparseSeekable"));
                }
            });
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(inbound, receiverData);
        AssertInitialPeerFirstBootstrapFrameReceived(receiver, inbound, frame);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(receiver, receiverBridge, evaluation);
        await WaitUntilAsync(() => GetPrivatePropertyValue(inbound, "FileTransferRouteTransitionExecution") is null, 5000);
        var fallbackEpoch = GetPrivateProperty<LiveRouteEpoch>(inbound, "CurrentLiveRouteEpoch");
        var fallbackLeg = GetPrivateProperty<FileTransferLeg>(inbound, "CurrentTransferLeg");
        Assert.Equal(2, fallbackEpoch.EpochId);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, fallbackEpoch.RouteSelection.Route);

        // Standalone service controls use the existing seeded peer baseline;
        // the two-writer integration supplies its frame from the remote host.
        using var sender = receivePeerYield is null ? new SessionFileTransferService() : null;
        using var senderData = new InitialBootstrapRecordingDataSession(receiverData.SessionId, transferId);
        using var senderBridge = new InitialBootstrapCheckpointTransport(senderData);
        object? outbound = null;
        try
        {
            FileTransferFallbackTailYieldFrameV6 peerFrame;
            if (sender is not null)
            {
                sender.AttachTransport(senderBridge);
                outbound = CreatePostTunaFallbackV6OutboundContext(transferId, startCoordinatorSnapshotStream: false);
                SetPrivateProperty(outbound, "DataSession", senderData);
                SetPrivateProperty(outbound, "PullPostTunaRecoveryActive", false);
                SetPrivateProperty(outbound, "PullTransportFrontierOnlyRepairActive", false);
                SetPrivateProperty(outbound, "CurrentLiveRouteEpoch", FileTransferCoordinator.StartLiveRouteEpoch(
                    1, fallbackEpoch.RouteSelection, FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.RegularNkn, reason));
                var peerLeg = GetPrivateProperty<FileTransferLeg>(outbound, "CurrentTransferLeg");
                SetPrivateProperty(peerLeg, "LiveRouteEpochId", 2);
                peerLeg.TransportEpochId = fallbackLeg.TransportEpochId;
                peerLeg.BridgeRecoveryGeneration = fallbackLeg.BridgeRecoveryGeneration;
                SeedCurrentRuntimeOwnerForTest(outbound, FileTransferDirection.Outbound, reason, startCoordinatorSnapshotStream: false);
                StartCoordinatorSnapshotStreamForTest(outbound);
                typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(sender, outbound);
                lock (GetServiceGate(sender))
                {
                    object?[] args = [outbound, peerLeg, peerLeg.TransportEpochId, 100, 99, reason, null];
                    Assert.True(Invoke(sender, "TryMarkOutboundFallbackTailQuiescentWithCoordinatorLocked", args));
                    var yield = Assert.IsType<FallbackTailYieldDecision>(args[^1]);
                    Assert.True(Invoke(sender, "RecordOutboundFallbackTailLocalYieldLocked",
                        outbound, peerLeg, yield, 100, 99, reason));
                }
                await WaitUntilAsync(() => senderData.SentFrames.OfType<FileTransferFallbackTailYieldFrameV6>().Any(), 5000);
                peerFrame = Assert.Single(senderData.SentFrames.OfType<FileTransferFallbackTailYieldFrameV6>());
                var peerProof = GetCoordinatorFallbackTailYieldSubstate(outbound).CurrentOperation!.Value.LocalProof;
                Assert.NotNull(peerProof);
                Assert.Equal(peerProof.Value.Decision.AuthorityId.ToString(), peerFrame.AuthorityId);
            }
            else
            {
                // The integration receives the original encoded peer frame from
                // the other test host. No local replacement peer is constructed.
                peerFrame = await receivePeerYield!(receiverBridge.ObservedRouteHints.ToArray());
            }
            if (control == "missing_peer")
            {
                Assert.Null(GetCoordinatorFallbackTailYieldSubstate(inbound).CurrentOperation?.PairedRelease);
                AssertSecondTunaCallbackPreservesRuntimeAuthority(receiver, inbound, receiverBridge, receiverData, () =>
                {
                    object?[] args = [inbound, receiverBridge.RuntimeUnlockRouteCommitSnapshot, reason, null];
                    Assert.False(Invoke(receiver, "TryPrepareInboundPostTunaFallbackToTunaRouteTransitionLocked", args));
                });
                Assert.Equal(2, TransitionRows().Length);
                return string.Empty;
            }
            Assert.True(FileTransferDataFrameCodec.TryDeserialize(FileTransferDataFrameCodec.Serialize(peerFrame), out var parsed));
            typeof(SessionFileTransferService).GetMethod("ApplyInboundFallbackTailYieldProof", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(receiver, [inbound, Assert.IsType<FileTransferFallbackTailYieldFrameV6>(parsed)]);
            await WaitUntilAsync(() => GetCoordinatorFallbackTailYieldSubstate(inbound).CurrentOperation?.PairedRelease is not null, 5000);
            var paired = GetCoordinatorFallbackTailYieldSubstate(inbound).CurrentOperation!.Value.PairedRelease!.Value;
            Assert.Equal(peerFrame.AuthorityId, paired.PeerProof.Proof.AuthorityId.ToString());
            if (sendLocalYield is not null)
            {
                await WaitUntilAsync(() => receiverData.SentFrames.OfType<FileTransferFallbackTailYieldFrameV6>().Any(), 5000);
                await sendLocalYield(Assert.Single(receiverData.SentFrames.OfType<FileTransferFallbackTailYieldFrameV6>()));
                // Unwind the inline transport-send continuation before entering
                // the deep Coordinator Snapshot fixture on the test-host stack.
                await Task.Yield();
            }
            await WaitUntilAsync(() => CreateCoordinatorStateForTest(inbound).CurrentRuntimeUnlockAdmission is
                { Route: FileTransferRoute.PostTunaFallbackV6 }, 5000);

            var execution = PrepareSecondTuna();
            if (execution is null)
            {
                Assert.Equal(2, TransitionRows().Length);
                return string.Empty;
            }
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "RecheckInboundFileTransferRouteTransitionDrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(receiver, [inbound, execution]));
            AssertSecondTunaCommitted(inbound);
            var transitions = TransitionRows();
            Assert.Equal(3, transitions.Length);
            Assert.Contains("previous_route=post_tuna_fallback_v6; new_route=file_tuna_v4;", transitions[^1], StringComparison.Ordinal);
            if (afterSecondTuna is not null)
            {
                var beforeIngress = SecondTunaStateFingerprint(inbound);
                var beforeFrames = receiverData.SentFrames.Count;
                var beforeHints = receiverBridge.ObservedRouteHints.Count;
                var beforeBytes = GetPrivateProperty<long>(inbound, "BytesTransferred");
                var beforeFrontier = GetPrivateProperty<int>(inbound, "NextChunkIndex");
                var beforeFile = destinationPath is null ? null : HashDurableFixtureFile(destinationPath);
                await afterSecondTuna(GetPrivateProperty<RoutePublicationToken>(inbound, "CurrentRoutePublicationToken"),
                    receiverBridge.ObservedRouteHints.ToArray());
                Assert.Equal(beforeIngress, SecondTunaStateFingerprint(inbound));
                Assert.Equal(beforeFrames, receiverData.SentFrames.Count);
                Assert.Equal(beforeHints, receiverBridge.ObservedRouteHints.Count);
                Assert.Equal(transitions, TransitionRows());
                Assert.Equal(beforeBytes, GetPrivateProperty<long>(inbound, "BytesTransferred"));
                Assert.Equal(beforeFrontier, GetPrivateProperty<int>(inbound, "NextChunkIndex"));
                if (beforeFile is not null) { Assert.Equal(beforeFile, HashDurableFixtureFile(destinationPath!)); }
            }
            if (replayPostCommitCallbacks)
            {
                var snapshot = receiverBridge.RuntimeUnlockRouteCommitSnapshot!;
                AssertSecondTunaCallbackPreservesRuntimeAuthority(receiver, inbound, receiverBridge, receiverData, () =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        Assert.False(Invoke(receiver,
                            "TryHandleInboundRuntimeUnlockPostFallbackResponderPeerProbeRouteTransitionHandoffLocked",
                            inbound, receiverData, snapshot, snapshot.ResponderPeerProbeRouteTransitionObservation, reason));
                    }
                });
                var beforeLateDrain = SecondTunaStateFingerprint(inbound);
                var lateStart = GetOperationalLogLength();
                for (var i = 0; i < 2; i++)
                {
                    await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                        "RecheckInboundFileTransferRouteTransitionDrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(receiver, [inbound, execution]));
                }
                Assert.Equal(beforeLateDrain, SecondTunaStateFingerprint(inbound));
                Assert.Equal(transitions, TransitionRows());
                Assert.DoesNotContain("effect_kind=", ReadOperationalLogTail(lateStart), StringComparison.Ordinal);
            }

            if (prepareFinalFallbackReceive is not null)
            {
                await ContinueSecondTunaToDurableFallbackAsync(receiver, inbound, receiverBridge, receiverData,
                    destination!, destinationPath!, prepareFinalFallbackReceive, execution);
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            object? PrepareSecondTuna()
            {
                lock (GetServiceGate(receiver))
                {
                    receiverBridge.SeedRuntimeUnlockRouteCommitProofForTests(transferId, reason,
                        transactionGeneration: 13, offerGeneration: 15, leaseGeneration: 17, launchGeneration: 19);
                    var snapshot = CreateInitialResponderRouteCommitSnapshotForInboundTest(inbound,
                        receiverBridge.RuntimeUnlockRouteCommitSnapshot!);
                    var observation = snapshot.ResponderPeerProbeRouteTransitionObservation!;
                    var scope = observation.PeerReceiveScope with
                    {
                        ResponderTransitionMode = RuntimeUnlockResponderPeerProbeTransitionMode.PostFallbackV6Paired,
                    };
                    scope = control switch
                    {
                        "wrong_publication" => scope with { RoutePublication = scope.RoutePublication with
                            { PublicationGeneration = scope.RoutePublication.PublicationGeneration + 1 } },
                        "wrong_data_session_generation" => scope with { DataSessionGeneration = scope.DataSessionGeneration + 1 },
                        "wrong_transition_mode" => scope with
                            { ResponderTransitionMode = RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4 },
                        _ => scope,
                    };
                    observation = observation with { PeerReceiveScope = scope };
                    snapshot = snapshot with { ResponderPeerProbeRouteTransitionObservation =
                        control == "missing_snapshot_observation" ? null : observation };
                    receiverBridge.RuntimeUnlockRouteCommitSnapshot = snapshot;
                    bool Handle() => Invoke(receiver,
                        "TryHandleInboundRuntimeUnlockPostFallbackResponderPeerProbeRouteTransitionHandoffLocked",
                        inbound, receiverData, snapshot, observation, reason);
                    if (control != "none")
                    {
                        AssertSecondTunaCallbackPreservesRuntimeAuthority(receiver, inbound, receiverBridge, receiverData,
                            () => Assert.False(Handle()));
                        return null;
                    }
                    Assert.True(Handle());
                    StabilizeFileTransferRouteTransitionDrainForTest(inbound);
                    var execution = GetPrivatePropertyValue(inbound, "FileTransferRouteTransitionExecution")!;
                    Assert.NotNull(execution);
                    var prepared = CreateCoordinatorStateForTest(inbound).PersistentState.FileTransferRouteTransition.CurrentOperation!.Value;
                    var activation = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(prepared.Authority.ProofBoundary);
                    Assert.Equal(paired, activation.FallbackTailPairedReleaseReceipt);
                    Assert.NotNull(activation.ResponderPeerProbeHandoffAuthority);
                    Assert.Equal(FileTransferRoute.PostTunaFallbackV6, GetPrivateProperty<RoutePublicationToken>(inbound, "CurrentRoutePublicationToken").Route);
                    return execution;
                }
            }
        }
        finally
        {
            if (outbound is not null) { TryCancelContextLifetime(outbound); }
            TryCancelContextLifetime(inbound);
        }
        receiver.Dispose();
        var identity = GetPrivateProperty<string>(inbound, "CoordinatorInstanceId");
        var evidenceIdentity = "fte1-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
            line.Contains("event=filetransfer_coordinator_snapshot_stream_completed;", StringComparison.Ordinal) &&
            line.Contains("coordinator_instance_id=" + evidenceIdentity + ";", StringComparison.Ordinal)), 5000);
        // Preserve original receiver records. This is a scoped service Snapshot
        // input, not a two-writer payload capture or a peer-lineage validator.
        return string.Join('\n', ReadOperationalLogTail(logStart).Split('\n').Where(line =>
            line.Contains("coordinator_instance_id=" + evidenceIdentity + ";", StringComparison.Ordinal)));

        static bool Invoke(SessionFileTransferService service, string name, params object?[] args)
            => Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, args));

        string[] TransitionRows() => ReadOperationalLogTail(logStart).Split('\n').Where(line =>
            line.Contains("event=filetransfer_route_transitioned; direction=inbound;", StringComparison.Ordinal) &&
            line.Contains("transfer_id=" + transferId + ";", StringComparison.Ordinal)).ToArray();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string SecondTunaStateFingerprint(object inbound)
        => FileTransferCoordinator.CalculateSnapshotStateFingerprint(CreateCoordinatorStateForTest(inbound));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertSecondTunaCallbackPreservesRuntimeAuthority(SessionFileTransferService service, object inbound,
        InitialBootstrapCheckpointTransport bridge, InitialBootstrapRecordingDataSession data, Action callback)
    {
        lock (GetServiceGate(service))
        {
            var before = SecondTunaStateFingerprint(inbound);
            var beforePersistent = GetPrivateProperty<FileTransferCoordinatorPersistentState>(inbound, "CoordinatorPersistentState");
            var beforePublication = GetPrivateProperty<RoutePublicationToken>(inbound, "CurrentRoutePublicationToken");
            var beforeFrames = data.SentFrames.Count;
            var beforeHints = bridge.ObservedRouteHints.Count;
            var logStart = GetOperationalLogLength();
            callback();
            Assert.Equal(before, SecondTunaStateFingerprint(inbound));
            Assert.Equal(beforePersistent, GetPrivateProperty<FileTransferCoordinatorPersistentState>(inbound, "CoordinatorPersistentState"));
            Assert.Equal(beforePublication, GetPrivateProperty<RoutePublicationToken>(inbound, "CurrentRoutePublicationToken"));
            Assert.Equal(beforeFrames, data.SentFrames.Count);
            Assert.Equal(beforeHints, bridge.ObservedRouteHints.Count);
            Assert.Null(GetPrivatePropertyValue(inbound, "FileTransferRouteTransitionExecution"));
            var log = ReadOperationalLogTail(logStart);
            // Rejected handoffs retain a diagnostic Snapshot observation of the
            // existing handoff. It grants no new runtime or publication effect.
            Assert.All(log.Split('\n').Where(line => line.Contains("event=filetransfer_coordinator_authority_effect;", StringComparison.Ordinal)), line =>
            {
                Assert.Contains("family=responder_probe_handoff;", line, StringComparison.Ordinal);
                Assert.Contains("effect_kind=convergence_observed;", line, StringComparison.Ordinal);
            });
            Assert.DoesNotContain("event=filetransfer_route_transitioned;", log, StringComparison.Ordinal);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertSecondTunaCommitted(object inbound)
    {
        var committed = CreateCoordinatorStateForTest(inbound);
        Assert.Equal(FileTransferRoute.FileTunaV4, committed.RouteSelection.Route);
        Assert.Equal(3, committed.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(4, committed.CurrentRoutePublicationToken!.Value.ProtocolVersion);
        var operation = committed.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        Assert.Equal(FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4, operation.Authority.Kind);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, operation.DispositionReceipt!.Value.Status);
        Assert.Equal(committed.CurrentRoutePublicationToken, operation.DispositionCommand!.Value.RoutePublicationToken);
        Assert.Null(GetPrivatePropertyValue(inbound, "FileTransferRouteTransitionExecution"));
    }
}
