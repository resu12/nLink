using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockResponderPeerProbe")]
    public async Task PostFallbackPairedRetryOffer_CheckpointAcceptanceRefreshesExactPublishedScope()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var receiverClient = new FakeNknClient("receiver.checkpoint-paired-binding.address");
            var peerClient = new FakeNknClient("peer.checkpoint-paired-binding.address");
            var lane = new RetryableTunaAccelerationSession(canListen: false,
                failedDialAttemptsBeforeSuccess: 0, supportedLanes: NknAccelerationLaneKind.File);
            using var transport = new NknSignalingTransport(receiverClient, NknTransportOptions.Load(),
                new NknIdentity("receiver-checkpoint-paired-binding", receiverClient.Address),
                NknTunaAccelerationOptions.Disabled, lane);
            using var peer = new NknSignalingTransport(peerClient, NknTransportOptions.Load(),
                new NknIdentity("peer-checkpoint-paired-binding", peerClient.Address));
            var sessionId = await ApproveNknSessionAsync(peer, transport, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
                suppressAutomaticAccelerationNegotiation: true);
            const string transferId = "post_fallback_checkpoint_paired_binding";
            using var dataSession = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            using var peerDataSession = await peer.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var dataGeneration = Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(dataSession).Generation;
            var source = FileTransferCoordinatorTests.CreatePostFallbackCheckpointBindingSource(
                sessionId, transferId, dataGeneration);
            var initialLeg = Assert.IsType<FileTransferLeg>(source.State.CurrentLeg);
            const string checkpointRequest = "v6-regular-nkn-state-refresh:checkpoint-binding:2:1";
            var publication = source.State.CurrentRoutePublicationToken!.Value;
            var owner = source.State.CurrentRuntimeOwner;

            using var service = new SessionFileTransferService();
            service.AttachTransport(transport);
            var contextType = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
            var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [new FileTransferOfferV2
                {
                    SessionId = sessionId, TransferId = transferId, FileName = "paired-binding.bin",
                    FileSizeBytes = 128 * 1024,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    FileTransferRoute = FileTransferRouteResolver.PostTunaFallbackV6Token,
                }, source.State.RouteSelection], culture: null)!;
            void Set(string name, object? value) => contextType.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(context, value);
            object? Get(string name) => contextType.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(context);
            Set("DataSession", dataSession);
            Set("DataSessionGeneration", dataGeneration);
            Set("State", FileTransferTransferState.Receiving);
            Set("ChunkSizeBytes", 1024);
            Set("ChunkCount", 128);
            Set("NextChunkIndex", 8);
            // This positive binding fixture has no outstanding frontier gap.
            Set("PullHighestReceivedChunkIndex", 7);
            Set("PullSessionActive", true);
            Set("PullManifestReceived", true);
            Set("ReceiverSparseWriteActive", true);
            typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, context);
            var serviceGate = GetPrivateField(service, "gate")!;
            FileTransferCoordinatorState State() => Assert.IsType<FileTransferCoordinatorState>(
                typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Single(method => method.Name == "CreateCoordinatorStateLocked" &&
                        method.GetParameters()[0].ParameterType == contextType).Invoke(null, [context]));
            lock (serviceGate)
            {
                var apply = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                        method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == contextType);
                // Explicit seeded setup from real Coordinator route decisions;
                // this fixture does not emit or claim the prior route history.
                var snapshotMetadata = State().PersistentState;
                apply.Invoke(null, [context, source, false, false]);
                Set("CoordinatorPersistentState", source.State.PersistentState with
                {
                    CoordinatorInstanceId = snapshotMetadata.CoordinatorInstanceId,
                    SnapshotStateGeneration = snapshotMetadata.SnapshotStateGeneration,
                    SnapshotStateFingerprint = snapshotMetadata.SnapshotStateFingerprint,
                });
                var checkpoint = FileTransferCoordinatorTests.RequestPostFallbackCheckpointBindingSource(State(), checkpointRequest);
                Assert.NotNull(checkpoint.SnapshotEnvelope);
                Assert.Null(checkpoint.MutationRejectedReason);
                Assert.Equal(publication, checkpoint.State.CurrentRoutePublicationToken);
                Assert.Equal(owner, checkpoint.State.CurrentRuntimeOwner);
                Assert.Equal(1, checkpoint.State.CurrentLeg!.CheckpointGeneration);
                Assert.Equal(checkpointRequest, checkpoint.State.CurrentLeg.CheckpointRequestId);
                apply.Invoke(null, [context, checkpoint, true, true]);
                var publish = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(method => method.Name == "TryPublishRouteHintIfAuthorizedLocked" &&
                        method.GetParameters()[0].ParameterType == contextType);
                Assert.True(Assert.IsType<bool>(publish.Invoke(service,
                    [context, "checkpoint_binding_fixture", "checkpoint_pending", "coordinator_route_publication_successor",
                        null, null, null, publication])));
            }

            var bridge = Assert.IsAssignableFrom<IRuntimeUnlockRetryDispatchAuthorityBridge>(transport);
            var observations = new List<RuntimeUnlockPostFallbackPairedRetryOfferBindingRequestedEventArgs>();
            // Attached after the real service handler: observe, never supply, its decision.
            bridge.RuntimeUnlockPostFallbackPairedRetryOfferBindingRequested += (_, args) => observations.Add(args);
            var offer = CreateOfferPayload(sessionId, "checkpointpaired0011223344556677",
                supportedLanes: ["file"], payerDecisionId: 91);
            offer.Trigger = "fallback_paired_tail_yield";
            offer.SenderRole = "helpee";
            bool Capture(string messageId)
            {
                object?[] args = [peerClient.Address, offer, messageId, null];
                return Assert.IsType<bool>(typeof(NknSignalingTransport).GetMethod(
                    "TryCaptureInboundPostFallbackPairedRetryOfferScope", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(transport, args));
            }
            Assert.False(Capture("checkpoint-pending-offer"));
            var oldRequest = Assert.Single(observations).Request;
            Assert.Equal(checkpointRequest, oldRequest.CheckpointRequestId);
            observations.Clear();
            var acceptanceLogStart = GetOperationalLogLength();

            lock (serviceGate)
            {
                typeof(SessionFileTransferService).GetMethod("MarkInboundFallbackCheckpointAcceptedLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [context, checkpointRequest, initialLeg.TransportEpochId, "regular_nkn_state_refresh"]);
                var accepted = State();
                Assert.Null(accepted.CurrentLeg!.CheckpointRequestId);
                Assert.Equal(1, accepted.CurrentLeg.CheckpointGeneration);
                Assert.Equal(publication, accepted.CurrentRoutePublicationToken);
                Assert.Equal(owner, accepted.CurrentRuntimeOwner);
                var acceptedFingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(accepted);
                typeof(SessionFileTransferService).GetMethod("MarkInboundFallbackCheckpointAcceptedLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [context, checkpointRequest, initialLeg.TransportEpochId, "regular_nkn_state_refresh"]);
                Assert.Equal(acceptedFingerprint, FileTransferCoordinator.CalculateSnapshotFingerprint(State()));
                var stale = new RuntimeUnlockPostFallbackPairedRetryOfferBindingRequestedEventArgs(oldRequest);
                typeof(SessionFileTransferService).GetMethod("OnRuntimeUnlockPostFallbackPairedRetryOfferBindingRequested",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [transport, stale]);
                Assert.Equal(RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Stale, stale.Decision.Status);
                Assert.Equal("paired_retry_offer_binding_source_not_current", stale.Decision.Reason);
                Assert.True(stale.Decision.Receipt.IsDefault);
                Assert.Equal(acceptedFingerprint, FileTransferCoordinator.CalculateSnapshotFingerprint(State()));
                // This is the peer-driven local-yield path, not the separate
                // receiver-state quiescent path that already publishes a hint.
                Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                    "TryMarkInboundFallbackTailLocalYieldFromCurrentLegLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, "peer_fallback_tail_yield", false, null])));
                var local = State().PersistentState.FallbackTailYield.CurrentOperation!.Value;
                var leg = Assert.IsType<FileTransferLeg>(Get("CurrentTransferLeg"));
                var peerYield = new FileTransferFallbackTailYieldFrameV6
                {
                    AuthoritySchema = FileTransferAuthoritySchema.Current,
                    SessionId = sessionId, TransferId = transferId, Direction = "outbound",
                    TransportEpoch = leg.TransportEpochId, LiveRouteEpoch = leg.LiveRouteEpochId,
                    FallbackLegGeneration = leg.Generation, BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
                    OwnerKey = local.Decision.OwnerKey, PairKey = local.Decision.PairKey,
                    YieldGeneration = 1,
                    AuthorityId = $"fta{FileTransferAuthoritySchema.Current}-fallback_tail_yield-" +
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transferId)).AsSpan(0, 12)).ToLowerInvariant(),
                    CommittedChunkIndex = leg.ProvenCommittedChunkIndex,
                    HighestObservedChunkIndex = leg.ProvenHighestObservedChunkIndex,
                    RecoveryMode = "fallback_tail_yield",
                };
                typeof(SessionFileTransferService).GetMethod("ApplyInboundFallbackTailYieldProof",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, peerYield]);
            }
            await WaitUntilAsync(() =>
            {
                lock (serviceGate)
                {
                    return State().PersistentState.FallbackTailYield.CurrentOperation?.PairingState ==
                        FileTransferFallbackTailYieldPairingState.Paired;
                }
            }, TimeSpan.FromSeconds(3));
            lock (serviceGate)
            {
                var paired = State();
                Assert.Equal(FileTransferFallbackTailYieldPairingState.Paired,
                    paired.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
                Assert.NotNull(paired.CurrentRuntimeUnlockAdmission);
                Assert.Equal("fallback_quiescent", paired.CurrentRuntimeUnlockAdmission!.Value.Reason);
                Assert.Equal(publication, paired.CurrentRoutePublicationToken);
                Assert.Equal(owner, paired.CurrentRuntimeOwner);
                Assert.Null(paired.CurrentLeg!.CheckpointRequestId);
                Assert.Equal(1, paired.CurrentLeg.CheckpointGeneration);
            }

            var captured = Capture("checkpoint-accepted-paired-offer");
            var observed = Assert.Single(observations);
            // Without the scope refresh, the transport submits the retired ID
            // and the real service correctly returns source-not-current.
            Assert.Equal("(none)", observed.Request.CheckpointRequestId);
            Assert.Equal(RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Accepted, observed.Decision.Status);
            Assert.True(captured);
            Assert.False(observed.Decision.Receipt.IsDefault);
            Assert.Equal(publication, State().CurrentRoutePublicationToken);
            Assert.Equal(owner, State().CurrentRuntimeOwner);

            var request = observed.Request;
            var authority = request.PublicationAuthority;
            RuntimeUnlockPostFallbackPairedRetryOfferBindingRequest[] staleRequests =
            [
                request with { CheckpointRequestId = checkpointRequest },
                request with { TransportEpoch = request.TransportEpoch + 1 },
                request with { BridgeRecoveryGeneration = request.BridgeRecoveryGeneration + 1 },
                request with { DataSessionIdentity = "replaced-data-session" },
                request with { DataSessionGeneration = request.DataSessionGeneration + 1 },
                request with { PublicationAuthority = authority with { RoutePublicationGeneration = authority.RoutePublicationGeneration + 1 } },
                request with { PublicationAuthority = authority with { RuntimeGeneration = authority.RuntimeGeneration + 1 } },
                request with { PublicationAuthority = authority with { TerminalGeneration = authority.TerminalGeneration + 1 } },
                request with { PublicationAuthority = authority with { LegGeneration = authority.LegGeneration + 1 } },
                request with { PublicationAuthority = authority with { LiveRouteEpoch = authority.LiveRouteEpoch + 1 } },
                request with { PublicationAuthority = authority with { RouteToken = FileTransferRouteResolver.FileTunaV4Token } },
                request with { PublicationAuthority = authority with { ProtocolVersion = FileTransferProtocol.ProtocolVersionV4 } },
            ];
            lock (serviceGate)
            {
                var fingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(State());
                foreach (var staleRequest in staleRequests)
                {
                    var rejected = new RuntimeUnlockPostFallbackPairedRetryOfferBindingRequestedEventArgs(staleRequest);
                    typeof(SessionFileTransferService).GetMethod("OnRuntimeUnlockPostFallbackPairedRetryOfferBindingRequested",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [transport, rejected]);
                    Assert.False(rejected.Decision.Accepted);
                    Assert.True(rejected.Decision.Receipt.IsDefault);
                    Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotFingerprint(State()));
                }
            }

            observations.Clear();
            var beforeTransaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                GetPrivateField(transport, "runtimeUnlockTransactionState"));
            var logStart = GetOperationalLogLength();
            var handleOffer = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(transport,
                "HandleTransportAccelerationOfferAsync", peerClient.Address, offer,
                "checkpoint-accepted-real-paired-offer", cts.Token));
            await handleOffer.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            Assert.Contains(observations, item => item.Decision.Accepted &&
                item.Request.CheckpointRequestId == "(none)" &&
                item.Decision.Receipt.AcceptanceScope == observed.Decision.Receipt.AcceptanceScope);
            var transaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                GetPrivateField(transport, "runtimeUnlockTransactionState"));
            Assert.True(transaction.TransactionGeneration > beforeTransaction.TransactionGeneration);
            Assert.True(transaction.OfferGeneration > beforeTransaction.OfferGeneration);
            Assert.True(transaction.AnswerReceived);
            Assert.False(transaction.IsTerminal);
            Assert.True(transaction.TunaPathLeaseCurrent);
            Assert.True(transaction.TunaPathLeaseGeneration > beforeTransaction.TunaPathLeaseGeneration);
            Assert.Contains("event=tuna_acceleration_answer_ack_pending;", ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);
            var pendingGeneration = Assert.IsType<long>(GetPrivateField(transport, "pendingAccelerationAnswerAckGeneration"));
            var authorityMode = GetPrivateField(transport, "pendingAccelerationAnswerAckAuthorityMode");
            Assert.Equal("InitialActivation", authorityMode?.ToString());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ack = new TransportAccelerationAnswerAckPayload
            {
                SessionId = sessionId, Accepted = true, SupportedLanes = ["file"],
                SentAtUnixMs = now, ExpiresAtUnixMs = now + 60_000, Nonce = offer.Nonce,
                SidecarProtocolVersion = 1, PayerDecisionId = offer.PayerDecisionId,
            };
            object?[] ackArguments =
            [
                ack, "checkpoint-paired-answer-ack", new string('a', 64), new string('b', 64),
                NknAccelerationLaneKind.File, authorityMode, pendingGeneration, null, null,
            ];
            lock (GetPrivateField(transport, "accelerationGate")!)
            {
                Assert.True(Assert.IsType<bool>(typeof(NknSignalingTransport).GetMethod(
                    "TryRecordRuntimeUnlockResponderSemanticAnswerAckUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(transport, ackArguments)));
            }
            Assert.NotNull(ackArguments[7]);
            Assert.Equal("semantic_answer_ack_retained_waiting_for_peer_probe", ackArguments[8]);
            Assert.NotNull(GetPrivateField(transport, "runtimeUnlockResponderAnswerProbeConvergence"));
            // The real callback and ACK do not themselves grant the second Tuna route.
            Assert.Equal(publication, State().CurrentRoutePublicationToken);
            Assert.Equal(owner, State().CurrentRuntimeOwner);
            Assert.DoesNotContain("effect_kind=route_publication_forwarded;",
                ReadOperationalLogTail(acceptanceLogStart), StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorDecision CreatePostFallbackCheckpointBindingSource(
        string sessionId, string transferId, long dataSessionGeneration)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var empty = EmptyState(regular, FileTransferDirection.Inbound) with
        {
            SessionId = sessionId, TransferId = transferId,
        };
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(
            FileTransferCoordinatorEventKind.StartTransfer, regular,
            state: FileTransferLegState.Active, canSendData: true), empty);
        var tuna = ActivateInitialResponderTunaForFallbackFixture(started.State).State with
        {
            CurrentDataSessionIdentity = $"{transferId}:{dataSessionGeneration}",
            CurrentDataSessionGeneration = dataSessionGeneration,
        };
        var fallback = FallBack(tuna, FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6),
            committedChunk: 8, transportEpoch: 1);
        Assert.Equal(2, fallback.State.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(3, fallback.State.CurrentRoutePublicationToken!.Value.PublicationGeneration);
        return fallback;
    }

    internal static FileTransferCoordinatorDecision RequestPostFallbackCheckpointBindingSource(
        FileTransferCoordinatorState state, string checkpointRequestId)
        => RequestFallbackCheckpoint(state, state.RouteSelection, checkpointRequestId, transportEpoch: 1);
}
