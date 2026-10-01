using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockResponderPeerProbe")]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    public async Task JoinedFallbackRetry_InstalledServiceAuthorityDrivesRealOfferProbeAndAck()
        => await RunJoinedFallbackRetryAsync(withStalledRepair: false);

    [Fact]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockResponderPeerProbe")]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    public async Task JoinedFallbackRetry_StalledRepairAndOldSourceReplayRemainUnresolvedAcrossPeerHandoff()
        => await RunJoinedFallbackRetryAsync(withStalledRepair: true);

    [Fact]
    [Trait("OpsSuite", "SupersededSourceAccounting")]
    public Task JoinedFallbackRetry_StalledRepairWithDelayedReceiverSnapshots()
        => RunJoinedFallbackRetryAsync(withStalledRepair: true, delayReceiverSnapshots: true);

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RecentProgressSupersession")]
    public Task JoinedFallbackRetry_RecentProgressIsSupersededBeforeGraceDeadline()
        => RunJoinedFallbackRetryAsync(withStalledRepair: true, withRecentProgress: true);

    [Theory]
    [InlineData("sidecar_tuna_stream_eof")]
    [InlineData("listener_terminal_tuna_stream_eof")]
    [Trait("FileTransferInfraSuite", "PostFallbackFailureHandoff")]
    public Task JoinedFallbackRetry_SidecarEofBeforeRemoteClosedRetiresExactInstalledPairOnce(string reason)
        => RunJoinedFallbackRetryAsync(withStalledRepair: false, sidecarFailureReason: reason);

    [Theory]
    [InlineData("sidecar_tuna_stream_eof")]
    [InlineData("listener_terminal_tuna_stream_eof")]
    [Trait("FileTransferInfraSuite", "PostFallbackRetryInstallRetirement")]
    public Task JoinedFallbackRetry_EofAllowsNextIndependentInstall(string reason)
        => RunJoinedFallbackRetryAsync(false, reason, continueAfterEof: true);

    [Theory]
    [InlineData("sidecar_tuna_stream_eof")]
    [InlineData("listener_terminal_tuna_stream_eof")]
    [Trait("FileTransferInfraSuite", "PostFallbackRetryInstallRetirement")]
    public Task JoinedFallbackRetry_RejectedRetirementKeepsOldPairConsumed(string reason)
        => RunJoinedFallbackRetryAsync(false, reason, "retirement_stage_generation_conflict");

    [Theory]
    [InlineData("data_session_disposed")]
    [InlineData("data_session_replaced")]
    [InlineData("service_disposed")]
    [InlineData("raw_transfer_conflict")]
    [InlineData("raw_lease_transfer_conflict")]
    [InlineData("retired_lease")]
    [InlineData("superseded_launch")]
    [Trait("FileTransferInfraSuite", "PostFallbackFailureHandoff")]
    public Task JoinedFallbackRetry_ListenerEofRejectsStaleProbeBinding(string control)
        => RunJoinedFallbackRetryAsync(false, "listener_terminal_tuna_stream_eof", control);

    private async Task RunJoinedFallbackRetryAsync(bool withStalledRepair, string? sidecarFailureReason = null,
        string? failureControl = null, bool continueAfterEof = false, bool withRecentProgress = false,
        bool delayReceiverSnapshots = false)
    {
        // Initial fallback contexts are an explicit fixture baseline. There is
        // no file-I/O/live acceptance claim. After the baseline, all authority,
        // messages and proof receipts are produced by the two real Services/NKN
        // transports. The test forwards physical bytes and pumps probes; the
        // repair variant then uses the real sender receive supervisor.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var senderProbePumpLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var releaseReceiverHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? releaseReceiverSnapshots = null;
        var releasePayload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offerQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerAckSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailedProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousPriority = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSendBarrier = SessionFileTransferService.OutboundRegularV4PhysicalSendPreReservationBarrierForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        object? outbound = null, inbound = null;
        NknSignalingTransport? hostTransport = null, helperTransport = null;
        SessionFileTransferService? senderService = null, receiverService = null;
        Exception? scenarioFailure = null;
        Task[] pumps = [];
        try
        {
            var hostClient = new FakeNknClient("host.joined-retry-continuation.address");
            var helperClient = new FakeNknClient("helper.joined-retry-continuation.address");
            var hostLane = new RetryableTunaAccelerationSession(true, 0, supportedLanes: NknAccelerationLaneKind.File);
            var helperLane = new RetryableTunaAccelerationSession(false, 0, supportedLanes: NknAccelerationLaneKind.File);
            var forwarded = new ConcurrentQueue<(string Sender, NknBridgeChannel Lane, byte[] Bytes)>();
            var offers = new ConcurrentQueue<Envelope>();
            var answers = new ConcurrentQueue<Envelope>();
            var answerAcks = new ConcurrentQueue<Envelope>();
            hostLane.ForwardBytesForTests = (lane, bytes, ct) => Forward("host", helperLane, lane, bytes, ct);
            helperLane.ForwardBytesForTests = (lane, bytes, ct) => Forward("helper", hostLane, lane, bytes, ct);
            hostClient.BeforeSendAsync = async (_, bytes, ct) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope)) { return; }
                if (envelope.Type == MsgType.FileTransferDataFrame) { await releaseInitialData.Task.WaitAsync(ct); }
                if (envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    offers.Enqueue(envelope);
                    if (offerQueued.TrySetResult()) { await releaseOffer.Task.WaitAsync(ct); }
                }
                if (envelope.Type == MsgType.TransportAccelerationAnswerAck)
                {
                    answerAcks.Enqueue(envelope);
                    answerAckSent.TrySetResult();
                }
            };
            helperClient.BeforeSendAsync = async (_, bytes, ct) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope)) { return; }
                if (envelope.Type == MsgType.FileTransferDataFrame) { await releaseInitialData.Task.WaitAsync(ct); }
                if (envelope.Type == MsgType.TransportAccelerationAnswer)
                {
                    answers.Enqueue(envelope);
                    answerSent.TrySetResult();
                }
            };
            var options = NknTransportOptions.Load();
            var host = hostTransport = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-joined-retry", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            var helper = helperTransport = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-joined-retry", helperClient.Address), NknTunaAccelerationOptions.Disabled, helperLane);
            var sessionId = await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            const string transferId = "transfer_joined_retry_continuation";
            // Register the real protocol transfer on both transports before
            // attaching Services to the seeded, already-open fallback baseline.
            var fileOffer = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fileAccept = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fileOpen = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            helper.FileTransferOfferReceived += (_, args) => fileOffer.TrySetResult(args.Message);
            host.FileTransferAcceptReceived += (_, args) => fileAccept.TrySetResult(args.Message);
            helper.FileTransferSessionOpenReceived += (_, args) => fileOpen.TrySetResult(args.Message);
            await host.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = transferId + ".bin",
                FileSizeBytes = 256L * 21 * 1024, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = FileTransferRouteResolver.PostTunaFallbackV6Token,
            }, timeout.Token);
            Assert.Equal(transferId, (await fileOffer.Task.WaitAsync(timeout.Token)).TransferId);
            await helper.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            }, timeout.Token);
            Assert.Equal(transferId, (await fileAccept.Task.WaitAsync(timeout.Token)).TransferId);
            await host.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 8,
            }, timeout.Token);
            Assert.Equal(transferId, (await fileOpen.Task.WaitAsync(timeout.Token)).TransferId);
            var sender = senderService = new SessionFileTransferService();
            var receiver = receiverService = new SessionFileTransferService();
            if (withStalledRepair)
            {
                // Delay real availability work, not the proof or its result.
                // The sender may commit while the receiver still owns fallback.
                receiver.InboundDispatchBeforeWorkAsyncForTests = (_, operation) =>
                    operation == "data_session_availability" ? releaseReceiverHandoff.Task : Task.CompletedTask;
            }
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            var senderData = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
            var receiverData = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
            outbound = await SessionFileTransferV4SenderTests.CreateJoinedNknFallbackContextForTestAsync(
                sender, senderData, sessionId, transferId, FileTransferDirection.Outbound, withStalledRepair);
            inbound = await SessionFileTransferV4SenderTests.CreateJoinedNknFallbackContextForTestAsync(
                receiver, receiverData, sessionId, transferId, FileTransferDirection.Inbound, withStalledRepair, withRecentProgress);
            var senderInitial = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            var receiverInitial = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(receiver, inbound);
            var initialPublications = new[]
            {
                (Service: sender, Context: outbound, Direction: FileTransferDirection.Outbound,
                    Publication: Assert.IsType<RoutePublicationToken>(senderInitial.CurrentRoutePublicationToken), State: senderInitial, Data: senderData),
                (Service: receiver, Context: inbound, Direction: FileTransferDirection.Inbound,
                    Publication: Assert.IsType<RoutePublicationToken>(receiverInitial.CurrentRoutePublicationToken), State: receiverInitial, Data: receiverData),
            };
            var logStart = GetOperationalLogLength();
            var senderYield = SessionFileTransferV4SenderTests.IssueJoinedNknYieldForTest(sender, outbound, FileTransferDirection.Outbound);
            var receiverYield = SessionFileTransferV4SenderTests.IssueJoinedNknYieldForTest(receiver, inbound, FileTransferDirection.Inbound);
            // Both local issuances precede peer delivery in this fixture. Once
            // released, original frames take the normal peer/service path.
            releaseInitialData.TrySetResult();
            var atReceiver = await receiverData.ReceiveWithMetadataAsync(timeout.Token);
            var atSender = await senderData.ReceiveWithMetadataAsync(timeout.Token);
            var senderFrame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(atReceiver.Frame);
            var receiverFrame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(atSender.Frame);
            Assert.Equal(FileTransferTransportKind.RegularNkn, atReceiver.TransportKind);
            Assert.Equal(FileTransferTransportKind.RegularNkn, atSender.TransportKind);
            Assert.Equal(senderYield.AuthorityId.ToString(), senderFrame.AuthorityId);
            Assert.Equal(receiverYield.AuthorityId.ToString(), receiverFrame.AuthorityId);
            Assert.NotEqual(senderYield.AuthorityId, receiverYield.AuthorityId);
            Assert.Equal(senderYield.PairKey, receiverYield.PairKey);

            // The receiver sees the actual sender yield first. Neither side is
            // given an independently constructed peer proof or retry contract.
            SessionFileTransferV4SenderTests.ApplyJoinedNknYieldForTest(receiver, inbound, FileTransferDirection.Inbound, senderFrame);
            SessionFileTransferV4SenderTests.ApplyJoinedNknYieldForTest(sender, outbound, FileTransferDirection.Outbound, receiverFrame);
            await WaitUntilAsync(() => HasJoinedFallbackPair(sender, outbound) && HasJoinedFallbackPair(receiver, inbound),
                TimeSpan.FromSeconds(3));
            foreach (var (service, context, local, peer) in new[] { (sender, outbound, senderYield, receiverYield), (receiver, inbound, receiverYield, senderYield) })
            {
                var paired = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context)
                    .PersistentState.FallbackTailYield.CurrentOperation!.Value.PairedRelease!.Value;
                Assert.Equal(local.AuthorityId, paired.LocalProof.Decision.AuthorityId);
                Assert.Equal(peer.AuthorityId, paired.PeerProof.Proof.AuthorityId);
            }
            await offerQueued.Task.WaitAsync(timeout.Token);
            var installed = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound)
                .PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt!.Value;
            Assert.True(installed.IsInstalled);
            Assert.Equal(senderYield, installed.Request.FallbackPairedTailYieldEvidence!.Value.LocalDecision);
            Assert.Equal(receiverYield.AuthorityId, installed.Request.FallbackPairedTailYieldEvidence.Value.PeerAuthorityId);
            Assert.Equal(0, installed.Request.RetiredOfferGeneration);
            RuntimeUnlockRetryDispatchContract contract;
            lock (GetPrivateField(host, "accelerationGate")!)
            {
                var shadow = GetPrivateField(host, "runtimeUnlockRetryDispatchInstallShadow")!;
                Assert.Equal(installed.InstallReceipt, shadow.GetType().GetProperty("LastCommittedReceipt")!.GetValue(shadow));
                var retry = GetPrivateField(host, "runtimeUnlockRecoveryRetryState")!;
                contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(retry.GetType().GetProperty("DispatchContract")!.GetValue(retry));
            }
            Assert.Equal(installed.Authority, contract.Authority);
            Assert.True(contract.OfferGeneration > 0);
            Assert.False(string.IsNullOrWhiteSpace(contract.OfferNonce));
            var installedOfferGeneration = contract.OfferGeneration;
            var installedOfferNonce = contract.OfferNonce;
            var offerEnvelope = ExactMessage(offers);
            var secureOffer = DecodeControl(helper, host, offerEnvelope, "transport_acceleration_offer");
            var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var offer = JsonSerializer.Deserialize<TransportAccelerationOfferPayload>(secureOffer.Plaintext, json)!;
            Assert.Equal("fallback_paired_tail_yield", offer.Trigger);
            Assert.Equal((sessionId, installedOfferNonce), (offer.SessionId, offer.Nonce));
            Assert.True(offer.PayerDecisionId > 0);
            Assert.Equal(installedOfferNonce, secureOffer.Metadata.RequestId);

            var probes = new ConcurrentQueue<(string Recipient, FileTransferReceivedDataFrame Delivery)>();
            var acks = new ConcurrentQueue<(string Recipient, FileTransferReceivedDataFrame Delivery)>();
            var senderProbeScopes = new ConcurrentQueue<RuntimeUnlockRouteCommitSnapshot>();
            ((IFileTransferRuntimeUnlockAuthorityFrameReceiver)senderData).RuntimeUnlockAuthorityFrameReceived += (_, args) => acks.Enqueue(("host", args.ReceivedFrame));
            ((IFileTransferRuntimeUnlockAuthorityFrameReceiver)receiverData).RuntimeUnlockAuthorityFrameReceived += (_, args) => acks.Enqueue(("helper", args.ReceivedFrame));
            pumps = [Pump(sender, senderData, FileTransferDirection.Outbound), Pump(receiver, receiverData, FileTransferDirection.Inbound)];
            releaseOffer.TrySetResult();
            releaseInitialData.TrySetResult();
            await Task.WhenAll(answerSent.Task, answerAckSent.Task).WaitAsync(timeout.Token);
            var answerEnvelope = ExactMessage(answers);
            var ackEnvelope = ExactMessage(answerAcks);
            var secureAnswer = DecodeControl(host, helper, answerEnvelope, "transport_acceleration_answer");
            var secureAck = DecodeControl(helper, host, ackEnvelope, "transport_acceleration_answer_ack");
            var answer = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(secureAnswer.Plaintext, json)!;
            var answerAck = JsonSerializer.Deserialize<TransportAccelerationAnswerAckPayload>(secureAck.Plaintext, json)!;
            Assert.True(answer.Accepted);
            Assert.True(answerAck.Accepted);
            Assert.Equal((sessionId, installedOfferNonce, offer.PayerDecisionId), (answer.SessionId, answer.Nonce, answer.PayerDecisionId));
            Assert.Equal((sessionId, installedOfferNonce, offer.PayerDecisionId), (answerAck.SessionId, answerAck.Nonce, answerAck.PayerDecisionId));
            Assert.Equal(installedOfferNonce, secureAnswer.Metadata.RequestId);
            Assert.Equal(installedOfferNonce, secureAck.Metadata.RequestId);
            Assert.NotEqual(offerEnvelope.MessageId, answerEnvelope.MessageId);
            Assert.NotEqual(answerEnvelope.MessageId, ackEnvelope.MessageId);
            if (sidecarFailureReason is not null)
            {
                // Hold the physically delivered probe before the receiver can ACK.
                // Authority and dispatch settlement still come from the real peers.
                await probeHeld.Task.WaitAsync(timeout.Token);
                if (failureControl is "data_session_disposed" or "data_session_replaced" or "service_disposed")
                {
                    senderProbePumpLifetime.Cancel();
                    try { await pumps[0]; } catch (OperationCanceledException) { }
                }
                var eof = await AssertJoinedFallbackEofHandoffAsync(sender, outbound, host, hostLane, senderData,
                    installed.InstallReceipt!, sidecarFailureReason, failureControl, timeout.Token);
                if (continueAfterEof)
                {
                    await helperLane.StopAsync(sidecarFailureReason, timeout.Token);
                    releaseFailedProbe.TrySetResult();
                    await AssertJoinedIndependentInstallAfterEofAsync(sender, outbound, host,
                        installed.InstallReceipt!.Value, offers, logStart, eof!);
                }
                Assert.Empty(acks);
                return;
            }
            RuntimeUnlockTransactionSnapshot? acknowledged = null;
            var lastProbePredicate = "not observed";
            try
            {
                await WaitUntilAsync(() =>
                {
                    foreach (var pump in pumps) { if (pump.IsFaulted) { Assert.Fail(pump.Exception!.ToString()); } }
                    RuntimeUnlockTransactionSnapshot current;
                    lock (GetPrivateField(host, "accelerationGate")!)
                    {
                        current = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(host, "runtimeUnlockTransactionState"));
                    }
                    var scopeMatches = senderProbeScopes.Any(scope => scope.SessionId == sessionId && scope.TransferId == transferId &&
                        scope.TransactionGeneration == current.TransactionGeneration && scope.OfferGeneration == current.OfferGeneration &&
                        scope.TunaPathLeaseGeneration == current.TunaPathLeaseGeneration &&
                        scope.TunaPathLeaseListenerRunId == current.TunaPathLeaseListenerRunId && scope.PathProbeId == current.PathProbeId);
                    var rawScopeMatches = current.SessionId == sessionId &&
                        (string.IsNullOrWhiteSpace(current.TransferId) || current.TransferId == transferId);
                    var probeReceived = probes.Any(value => value.Recipient == "helper" &&
                        value.Delivery.Frame is FileTransferRuntimeUnlockPreCommitProbeFrame probe && probe.ProbeId == current.PathProbeId);
                    var ackReceived = acks.Any(value => value.Recipient == "host" &&
                        value.Delivery.Frame is FileTransferRuntimeUnlockPreCommitProbeAckFrame ack && ack.ProbeId == current.PathProbeId);
                    var diagnosticObserved = ReadOperationalLogTail(logStart).Contains("event=runtime_unlock_precommit_probe_acked;", StringComparison.Ordinal);
                    lastProbePredicate = $"state={current.State}; probeState={current.PathProbeState}; transaction={current.TransactionGeneration}; offer={current.OfferGeneration}; lease={current.TunaPathLeaseGeneration}; rawTransferAbsent={string.IsNullOrWhiteSpace(current.TransferId)}; rawScopeMatches={rawScopeMatches}; providerScopes={senderProbeScopes.Count}; providerScopeMatches={scopeMatches}; probeReceived={probeReceived}; ackReceived={ackReceived}; diagnosticObserved={diagnosticObserved}";
                    if (current.HasTunaPathProbeProof && rawScopeMatches && scopeMatches &&
                        current.OfferGeneration == installedOfferGeneration && probeReceived && ackReceived)
                    {
                        acknowledged = current;
                    }
                    return acknowledged is not null && diagnosticObserved;
                }, TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Joined probe acknowledgement predicate: {lastProbePredicate}", error);
            }
            var acknowledgedState = Assert.IsType<RuntimeUnlockTransactionSnapshot>(acknowledged);
            var probeDelivery = Assert.Single(probes.Where(value => value.Recipient == "helper" &&
                value.Delivery.Frame is FileTransferRuntimeUnlockPreCommitProbeFrame candidate && candidate.ProbeId == acknowledgedState.PathProbeId)).Delivery;
            var probe = Assert.IsType<FileTransferRuntimeUnlockPreCommitProbeFrame>(probeDelivery.Frame);
            var matchingAcks = acks.Where(value => value.Recipient == "host" && value.Delivery.Frame is FileTransferRuntimeUnlockPreCommitProbeAckFrame ack &&
                ack.ProbeId == probe.ProbeId && ack.TransactionGeneration == probe.TransactionGeneration &&
                ack.OfferGeneration == probe.OfferGeneration && ack.TunaPathLeaseGeneration == probe.TunaPathLeaseGeneration).Select(value => value.Delivery).ToArray();
            Assert.NotEmpty(matchingAcks);
            Assert.Equal((sessionId, transferId, installedOfferGeneration, acknowledgedState.TransactionGeneration, acknowledgedState.TunaPathLeaseGeneration),
                (probe.SessionId, probe.TransferId, probe.OfferGeneration, probe.TransactionGeneration, probe.TunaPathLeaseGeneration));
            Assert.True(acknowledgedState.PathProbeAckedUtcMs > 0);
            Assert.Equal(FileTransferTransportKind.Tuna, probeDelivery.TransportKind);
            Assert.NotEqual(default, probeDelivery.ReceivedUtc);
            Assert.All(matchingAcks, value => Assert.Equal(FileTransferTransportKind.Tuna, value.TransportKind));
            var decodedForwarded = forwarded.Select(value => (value.Sender, value.Lane, Frame: DecodeData(value))).ToArray();
            Assert.Contains(decodedForwarded, value => value.Sender == "host" && value.Lane == NknBridgeChannel.Bulk && Equals(value.Frame, probe));
            Assert.All(matchingAcks, delivery =>
            {
                var ack = Assert.IsType<FileTransferRuntimeUnlockPreCommitProbeAckFrame>(delivery.Frame);
                Assert.True(ack.Accepted);
                Assert.NotEqual(default, delivery.ReceivedUtc);
                Assert.Contains(decodedForwarded, value => value.Sender == "helper" && value.Lane == NknBridgeChannel.Bulk && Equals(value.Frame, ack));
            });
            object[]? repairLineage = null;
            object? recentWake = null;
            if (withRecentProgress)
            {
                await WaitUntilAsync(() => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound)
                    .PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                    { State: FileTransferRouteTransitionState.Committed,
                      DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied }, TimeSpan.FromSeconds(5));
                SessionFileTransferV4SenderTests.AssertJoinedReceiverStillFallbackForTest(receiver, inbound,
                    initialPublications[1].Publication);
                recentWake = await SessionFileTransferV4SenderTests.CreateJoinedRecentProgressForTestAsync(receiver, inbound);
                releaseReceiverHandoff.TrySetResult();
            }
            else if (withStalledRepair)
            {
                await WaitUntilAsync(() => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound)
                    .PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                    { State: FileTransferRouteTransitionState.Committed,
                      DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied }, TimeSpan.FromSeconds(5));
                SessionFileTransferV4SenderTests.AssertJoinedReceiverStillFallbackForTest(receiver, inbound,
                    initialPublications[1].Publication);

                // There must be only one reader: replace the probe-only pump
                // with the real committed sender's receive supervisor.
                senderProbePumpLifetime.Cancel();
                try { await pumps[0]; } catch (OperationCanceledException) { }
                // Hold physical payload work while the real receive supervisor
                // remains live. The barrier issues no authority or completion.
                SessionFileTransferService.OutboundRegularV4PhysicalSendPreReservationBarrierForTests = (id, _) =>
                    id == transferId ? releasePayload.Task : Task.CompletedTask;
                pumps[0] = SessionFileTransferV4SenderTests.StartJoinedSparseReceiveForTest(sender, outbound);
                var requests = new ConcurrentQueue<(FileTransferFrontierRequestFrameV6 Frame, NknBridgeChannel Lane)>();
                helperClient.BeforeSendCoreAsync = (_, bytes, lane, _) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame &&
                        DecodeData(("helper", lane, bytes)) is FileTransferFrontierRequestFrameV6 request)
                    {
                        requests.Enqueue((request, lane));
                    }
                    return Task.CompletedTask;
                };
                repairLineage = await SessionFileTransferV4SenderTests.AssertJoinedStaleFrontierRequestsForTestAsync(
                    sender, outbound, receiver, inbound, senderData, () => requests.ToArray(),
                    (frame, lane) => senderData.GetType().GetMethod("Deliver")!.Invoke(senderData,
                        [frame, lane, FileTransferTransportKind.RegularNkn,
                            lane == NknBridgeChannel.Bulk ? helperClient.BulkAddress : helper.LocalPeerAddress, null]));
                if (delayReceiverSnapshots)
                {
                    releaseReceiverSnapshots = await SessionFileTransferV4SenderTests.HoldJoinedSnapshotDrainForTestAsync(receiver, inbound);
                }
                releaseReceiverHandoff.TrySetResult();
            }
            var lastRoutePredicate = "not observed";
            try
            {
                await WaitUntilAsync(() =>
                {
                    foreach (var pump in pumps) { if (pump.IsFaulted) { Assert.Fail(pump.Exception!.ToString()); } }
                    var completed = 0;
                    var statuses = new List<string>();
                    foreach (var initial in initialPublications)
                    {
                        var current = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(initial.Service, initial.Context);
                        var routes = current.PersistentState.FileTransferRouteTransition;
                        statuses.Add($"{initial.Direction}: route={current.RouteSelection.Route}; publication={current.CurrentRoutePublicationToken?.PublicationGeneration}; current={routes.CurrentOperation?.State}; terminal={routes.LastTerminalOperation?.State}; disposition={routes.LastTerminalOperation?.DispositionReceipt?.Status}");
                        if (routes.CurrentOperation is not null || routes.LastTerminalOperation is not
                            { State: FileTransferRouteTransitionState.Committed,
                              DispositionReceipt: { Status: FileTransferRouteTransitionDispositionApplyStatus.Applied } disposition,
                              RoutePublicationToken: { } publication, RouteCommitToken: { } commit, FinalProof: { } finalProof } terminal)
                        {
                            continue;
                        }
                        Assert.Equal((sessionId, transferId, initial.Direction),
                            (terminal.Authority.SessionId, terminal.Authority.TransferId, terminal.Authority.Direction));
                        Assert.Equal(FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4, terminal.Authority.Kind);
                        Assert.Equal(initial.Publication, terminal.Authority.SourceRoutePublication);
                        Assert.Equal(initial.Publication.RuntimeOwner, terminal.Authority.SourceRuntimeOwner);
                        Assert.Equal((FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4, 2),
                            (publication.Route, publication.ProtocolVersion, publication.PublicationGeneration));
                        Assert.Equal(publication, current.CurrentRoutePublicationToken);
                        Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(current, publication, out var publicationReason), publicationReason);
                        Assert.Equal(commit, current.CurrentRouteCommit);
                        Assert.Equal(terminal.Authority, disposition.Command.Authority);
                        Assert.Equal(terminal.DispositionCommand, disposition.Command);
                        Assert.Equal(FileTransferRouteTransitionDispositionKind.Committed, disposition.Command.Kind);
                        Assert.Equal(publication, disposition.Command.RoutePublicationToken);
                        Assert.Equal(commit, disposition.Command.RouteCommitToken);
                        Assert.Equal(finalProof, disposition.Command.FinalProof);
                        Assert.Equal((sessionId, transferId, probe.ProbeId, RuntimeUnlockPathProbeState.Acked, FileTransferTransportKind.Tuna),
                            (finalProof.SessionId, finalProof.TransferId, finalProof.PathProbeId, finalProof.PathProbeState, finalProof.PathProbeTransport));
                        Assert.Equal(probe.ProbeId, commit.SamePathTunaProbeAckId);
                        Assert.Equal((commit.TransactionGeneration, commit.OfferGeneration, commit.TunaPathLeaseGeneration),
                            (finalProof.TransactionGeneration, finalProof.OfferGeneration, finalProof.TunaPathLeaseGeneration));
                        if (initial.Direction == FileTransferDirection.Outbound)
                        {
                            Assert.Equal((probe.TransactionGeneration, probe.OfferGeneration, probe.TunaPathLeaseGeneration),
                                (finalProof.TransactionGeneration, finalProof.OfferGeneration, finalProof.TunaPathLeaseGeneration));
                        }
                        completed++;
                    }
                    lastRoutePredicate = string.Join("; ", statuses);
                    return completed == initialPublications.Length;
                }, TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Joined route disposition predicate: {lastRoutePredicate}", error);
            }
            if (repairLineage is not null)
            {
                releaseReceiverSnapshots?.Invoke();
                releaseReceiverSnapshots = null;
                SessionFileTransferV4SenderTests.AssertJoinedRepairHandoffForTest(receiver, inbound, repairLineage,
                    ReadOperationalLogTail(logStart));
                foreach (var initial in initialPublications)
                {
                    SessionFileTransferV4SenderTests.AssertJoinedTargetHandoffContinuityForTest(
                        initial.Service, initial.Context, initial.State, initial.Data);
                }
            }
            if (recentWake is not null)
            {
                await SessionFileTransferV4SenderTests.AssertJoinedRecentProgressSupersededForTestAsync(
                    receiver, inbound, recentWake, logStart);
            }

            FileTransferDataFrame? DecodeData((string Sender, NknBridgeChannel Lane, byte[] Bytes) captured)
            {
                Assert.True(EnvelopeCodec.TryDeserialize(captured.Bytes, out var envelope));
                if (envelope.Type != MsgType.FileTransferDataFrame) { return null; }
                var recipient = captured.Sender == "host" ? helper : host;
                var senderTransport = captured.Sender == "host" ? host : helper;
                var senderClient = captured.Sender == "host" ? hostClient : helperClient;
                var expectedSender = captured.Lane == NknBridgeChannel.Bulk ? senderClient.BulkAddress : senderTransport.LocalPeerAddress;
                var secure = SessionSecureEnvelopeCodec.Decrypt(
                    Assert.IsType<byte[]>(GetPrivateField(recipient, "fileTransferSessionSharedKey")), envelope.Payload,
                    new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame",
                        new SessionId(sessionId), new PeerAddress(expectedSender), transferId));
                Assert.True(FileTransferDataFrameCodec.TryDeserialize(secure.Plaintext, out var frame));
                Assert.Equal((sessionId, transferId), (frame!.SessionId, frame.TransferId));
                return frame;
            }

            async Task<bool> Forward(string source, RetryableTunaAccelerationSession target,
                NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (!target.IsAvailable) { return false; }
                forwarded.Enqueue((source, lane, bytes.AsSpan().ToArray()));
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                target.ReceivePeerBytesForTests(lane, bytes);
                return true;
            }
            async Task Pump(SessionFileTransferService service, IFileTransferDataSession data, FileTransferDirection direction)
            {
                var token = direction == FileTransferDirection.Outbound ? senderProbePumpLifetime.Token : timeout.Token;
                while (!token.IsCancellationRequested)
                {
                    var received = await data.ReceiveWithMetadataAsync(token);
                    if (continueAfterEof && received.Frame is FileTransferFallbackTailYieldFrameV6 nextYield)
                    {
                        SessionFileTransferV4SenderTests.ApplyJoinedNknYieldForTest(service,
                            direction == FileTransferDirection.Outbound ? outbound! : inbound!, direction, nextYield);
                        continue;
                    }
                    if (received.Frame is not FileTransferRuntimeUnlockPreCommitProbeFrame)
                    {
                        // Post-proof state/commit traffic is outside this bounded
                        // probe lane; it is neither turned into proof nor replayed.
                        continue;
                    }
                    if (direction == FileTransferDirection.Inbound)
                    {
                        // Read the real sender provider before this original
                        // probe is handled and its ACK can commit the route.
                        Assert.True(((IRuntimeUnlockRouteCommitProofProvider)host).TryGetRuntimeUnlockRouteCommitProof(
                            sessionId, transferId, out var scope), "The sender provider did not expose the received probe's exact scope.");
                        var probe = Assert.IsType<FileTransferRuntimeUnlockPreCommitProbeFrame>(received.Frame);
                        Assert.Equal((sessionId, transferId, probe.TransactionGeneration, probe.OfferGeneration, probe.TunaPathLeaseGeneration, probe.ProbeId),
                            (scope.SessionId, scope.TransferId, scope.TransactionGeneration, scope.OfferGeneration, scope.TunaPathLeaseGeneration, scope.PathProbeId));
                        Assert.True(scope.TunaPathLeaseRequired && scope.TunaPathLeaseCurrent);
                        senderProbeScopes.Enqueue(scope);
                        if (sidecarFailureReason is not null)
                        {
                            if (probeHeld.TrySetResult() && continueAfterEof)
                            {
                                await releaseFailedProbe.Task.WaitAsync(token);
                                continue; // The old physical probe never becomes a proof after EOF.
                            }
                            await Task.Delay(Timeout.Infinite, token);
                        }
                    }
                    probes.Enqueue((direction == FileTransferDirection.Outbound ? "host" : "helper", received));
                    var method = typeof(SessionFileTransferService).GetMethod("TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Assert.True(await Assert.IsAssignableFrom<Task<bool>>(method.Invoke(service,
                        [sessionId, transferId, direction, received.Frame, received.TransportKind, data])));
                }
            }
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            releaseReceiverSnapshots?.Invoke();
            releaseOffer.TrySetResult();
            releaseReceiverHandoff.TrySetResult();
            if (outbound is not null) { SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(outbound); }
            if (inbound is not null) { SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(inbound); }
            // Retire owned workers before releasing test barriers. Canceling
            // their blocked physical send first would simulate a live failure.
            releasePayload.TrySetResult();
            timeout.Cancel();
            try { await Task.WhenAll(pumps); }
            catch (OperationCanceledException) { }
            catch (Exception) when (scenarioFailure is not null) { }
            finally
            {
                senderService?.Dispose();
                receiverService?.Dispose();
                hostTransport?.Dispose();
                helperTransport?.Dispose();
                NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriority;
                SessionFileTransferService.OutboundRegularV4PhysicalSendPreReservationBarrierForTests = previousSendBarrier;
            }
        }

        static Envelope ExactMessage(ConcurrentQueue<Envelope> messages)
        {
            var copies = Assert.Single(messages.GroupBy(envelope => envelope.MessageId)).ToArray();
            Assert.All(copies, copy => Assert.Equal(copies[0].Payload, copy.Payload));
            return copies[0];
        }

        static SessionSecureEnvelopePayload DecodeControl(NknSignalingTransport recipient, NknSignalingTransport sender,
            Envelope envelope, string type)
            => SessionSecureEnvelopeCodec.Decrypt(
                Assert.IsType<byte[]>(GetPrivateField(recipient, "controlSessionSharedKey")), envelope.Payload,
                new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.RemoteControl, type,
                    sender.CurrentSessionSecurityState.SessionId, new PeerAddress(sender.LocalPeerAddress)));
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task<Action> HoldJoinedSnapshotDrainForTestAsync(SessionFileTransferService service, object context)
    {
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var scheduled = emitter.GetType().GetField("drainScheduled", flags)!;
        await WaitUntilAsync(() =>
        {
            lock (GetServiceGate(service))
            {
                if ((int)scheduled.GetValue(emitter)! != 0) { return false; }
                // Hold only writer scheduling. Envelopes and effects still come
                // from the real Coordinator and remain in their original queue.
                scheduled.SetValue(emitter, 1);
                return true;
            }
        }, timeoutMs: 3000);
        return () => emitter.GetType().GetMethod("Drain", flags)!.Invoke(emitter, null);
    }

    internal static async Task<object> CreateJoinedNknFallbackContextForTestAsync(SessionFileTransferService service,
        IFileTransferDataSession data, string sessionId, string transferId, FileTransferDirection direction,
        bool withStalledRepair = false, bool withRecentProgress = false)
    {
        object context;
        if (direction == FileTransferDirection.Outbound)
        {
            context = withStalledRepair
                ? CreateSparsePostTunaContextWithSourceFactory(transferId,
                    _ => Task.FromResult<Stream>(new MemoryStream(new byte[256 * 21 * 1024], writable: false)),
                    remoteNextExpectedChunkIndex: 100, chunksAcceptedForTransport: 256,
                    remoteGrantedUntilExclusive: 256, chunkCount: 256, startCoordinatorSnapshotStream: false)
                : CreatePostTunaFallbackV6OutboundContext(transferId, startCoordinatorSnapshotStream: false);
            SetPrivateProperty(context, "SessionId", sessionId);
            SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 255);
        }
        else
        {
            var type = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
            var route = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
            context = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [new FileTransferOfferV2 { SessionId = sessionId, TransferId = transferId, FileName = transferId + ".bin",
                    FileSizeBytes = 256L * 21 * 1024, PreferredDataProtocolVersion = 6, FileTransferRoute = route.TelemetryToken }, route], null)!;
            SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
            SetPrivateProperty(context, "ChunkSizeBytes", 21 * 1024);
            SetPrivateProperty(context, "ChunkCount", 256);
            SetPrivateProperty(context, "PullSessionActive", true);
            SetPrivateProperty(context, "PullManifestReceived", true);
            SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
            SetPrivateProperty(context, "NextChunkIndex", 100);
            SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 255);
            SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 255);
            if (withStalledRepair)
            {
                var mode = typeof(SessionFileTransferService).GetNestedType("V6ReceiveDestinationMode", BindingFlags.NonPublic)!;
                SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(mode, "SparseSeekable"));
            }
            if (withRecentProgress)
            {
                var written = new System.Collections.BitArray(256);
                for (var index = 0; index < 100; index++) { written[index] = true; }
                written[253] = true;
                SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 253);
                SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 253);
                SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
                SetPrivateProperty(context, "V4CreditUntilChunkIndexExclusive", 256);
                SetPrivateProperty(context, "V6SparseAcceptWindowEndExclusive", 256);
                SetPrivateProperty(context, "V6ReceiverTransportEpoch", 3L);
            }
        }
        // Drain any constructor/setup stream before establishing the one initial
        // fixture source. No post-start state, authority or proof is authored.
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        var emitterType = emitter.GetType();
        emitterType.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter,
            [GetPrivateProperty<string>(context, "CoordinatorInstanceId"), sessionId, transferId, direction]);
        await WaitUntilAsync(() => (int)emitterType.GetField("drainScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(emitter)! == 0, timeoutMs: 3000);
        var id = Guid.NewGuid().ToString("N");
        context.GetType().GetField("<CoordinatorInstanceId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, id);
        context.GetType().GetField("<CoordinatorSnapshotEmitter>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, Activator.CreateInstance(emitterType, nonPublic: true));
        SetPrivateProperty(context, "CoordinatorPersistentState", new FileTransferCoordinatorPersistentState { CoordinatorInstanceId = id });
        (GetPrivatePropertyValue(context, "DataSession") as IFileTransferDataSession)?.Dispose();
        SetPrivateProperty(context, "DataSession", data);
        SetPrivateProperty(context, "DataSessionGeneration", ((IFileTransferDataSessionGenerationProvider)data).Generation);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        TrySetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", false);
        var policy = typeof(SessionFileTransferService).GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "BridgeRecoveryPolicy", Enum.Parse(policy, "PostTunaFallbackStrictRecovery"));
        // Only the initial fallback route/data/progress baseline is declared.
        // Runtime ownership and publication are born in the emitted history.
        SetPrivateProperty(context, "CurrentRuntimeOwner", null);
        SetPrivateProperty(context, "LastRuntimeOwnerGeneration", 0);
        SetPrivateProperty(context, "CurrentRoutePublicationToken", null);
        SetPrivateProperty(context, "LastRoutePublicationGeneration", 0);
        SetPrivateProperty(context, "CurrentTransferLeg", null);
        SetPrivateProperty(context, "LastTransferLegGeneration", 0);
        GetPrivateProperty<List<FileTransferLeg>>(context, "TransferLegHistory").Clear();
        var routeSelection = GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var initialEpoch = FileTransferCoordinator.StartLiveRouteEpoch(0, routeSelection,
            FileTransferTransportHandoffKind.None, FileTransferTransportKind.Unknown, "joined_nkn_initial_fallback_baseline");
        SetPrivateProperty(context, "CurrentLiveRouteEpoch", initialEpoch);
        SetPrivateProperty(context, "LastLiveRouteEpochId", initialEpoch.EpochId);
        typeof(SessionFileTransferService).GetField(direction == FileTransferDirection.Outbound ? "outboundTransfer" : "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        typeof(SessionFileTransferService).GetMethod("SubscribeDataSessionEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [data]);
        StartCoordinatorSnapshotStreamForTest(context);
        var state = CreateCoordinatorStateForTest(context);
        Assert.Null(state.CurrentRuntimeOwner);
        Assert.Null(state.CurrentLeg);
        Assert.Null(state.CurrentRoutePublicationToken);
        Assert.Equal(0, state.PersistentState.SnapshotStateGeneration);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(state), state.PersistentState.SnapshotStateFingerprint);
        var publishHint = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryPublishRouteHintIfAuthorizedLocked" && method.GetParameters()[0].ParameterType == context.GetType());
        lock (GetServiceGate(service))
        {
            var start = typeof(SessionFileTransferService).GetMethod($"Start{direction}TransferLegLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object?[] startArgs = direction == FileTransferDirection.Outbound
                ? [context, routeSelection, "joined_nkn_initial_source", FileTransferLegState.Active, true, false, 3L, 1, null, false, null, null, null, false]
                : [context, routeSelection, "joined_nkn_initial_source", FileTransferLegState.Active, true, false, 3L, 1, null, null];
            var leg = Assert.IsType<FileTransferLeg>(start.Invoke(service, startArgs));
            var started = CreateCoordinatorStateForTest(context);
            Assert.Equal(1, started.PersistentState.SnapshotStateGeneration);
            Assert.Equal(1, started.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            Assert.Equal((1, 3L, 1), (leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration));
            Assert.Null(started.CurrentRoutePublicationToken);
            var publish = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "TryPublishRouteSelectedIfAuthorizedLocked" &&
                    method.GetParameters()[0].ParameterType == context.GetType() && method.GetParameters()[4].ParameterType.IsByRef);
            var transport = Assert.IsAssignableFrom<IFileTransferSignalingTransport>(typeof(SessionFileTransferService)
                .GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
            object?[] publicationArgs = [context, FileTransferRouteResolverInput.FromTransport(transport),
                "joined_nkn_initial_source", "joined_nkn_initial_source", null, null];
            Assert.True(Assert.IsType<bool>(publish.Invoke(service, publicationArgs)));
            var publication = Assert.IsType<RoutePublicationToken>(publicationArgs[4]);
            var published = CreateCoordinatorStateForTest(context);
            Assert.Equal(2, published.PersistentState.SnapshotStateGeneration);
            Assert.Equal((FileTransferRoute.PostTunaFallbackV6, FileTransferProtocol.ProtocolVersionV6, 1),
                (publication.Route, publication.ProtocolVersion, publication.PublicationGeneration));
            Assert.Equal(FileTransferCoordinatorActionKind.RoutePublication, publication.ActionKind);
            Assert.Equal(started.CurrentRuntimeOwner, publication.RuntimeOwner);
            Assert.Equal(publication, published.CurrentRoutePublicationToken);
            var checkpoint = Assert.IsType<FileTransferRouteTransitionDurableCheckpoint>(typeof(SessionFileTransferService)
                .GetMethod($"Create{direction}FileTransferRouteTransitionCheckpoint", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [context, published.CurrentRuntimeOwner, publication, leg]));
            Assert.False(checkpoint.IsDefault);
            Assert.True(Assert.IsType<bool>(publishHint.Invoke(service, [context, "joined_nkn_initial_source", "joined_nkn_initial_source",
                "joined_nkn_initial_source", null, null, null, publication])));
        }
        return context;
    }

    internal static FallbackTailYieldDecision IssueJoinedNknYieldForTest(SessionFileTransferService service, object context, FileTransferDirection direction)
    {
        lock (GetServiceGate(service))
        {
            var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
            object?[] args = [context, leg, leg.TransportEpochId, 100, 255, "joined_nkn_tail_yield", null];
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod($"TryMark{direction}FallbackTailQuiescentWithCoordinatorLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)));
            var decision = Assert.IsType<FallbackTailYieldDecision>(args[^1]);
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod($"Record{direction}FallbackTailLocalYieldLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, leg, decision, 100, 255, "joined_nkn_tail_yield"])));
            // Complete the normal Service yield path. Cleanup, not this test,
            // owns both retiring the fallback worker and emitting its effect.
            typeof(SessionFileTransferService).GetMethod($"Clear{direction}FallbackTailRecoveryOwnershipAfterYieldLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context, leg, "joined_nkn_tail_yield"]);
            Assert.Null(GetPrivatePropertyValue(context, "V6TransportHandoff"));
            Assert.False(GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
            Assert.False(GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending"));
            Assert.True(GetPrivateProperty<long>(context, "LastRecoveredV6TransportHandoffEpoch") >= leg.TransportEpochId);
            return decision;
        }
    }

    internal static void ApplyJoinedNknYieldForTest(SessionFileTransferService service, object context,
        FileTransferDirection direction, FileTransferFallbackTailYieldFrameV6 frame)
        => typeof(SessionFileTransferService).GetMethod($"Apply{direction}FallbackTailYieldProof", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, frame]);

    internal static FileTransferCoordinatorState CaptureJoinedNknStateForTest(SessionFileTransferService service, object context)
    {
        lock (GetServiceGate(service)) { return CreateCoordinatorStateForTest(context); }
    }
    internal static void CancelJoinedNknContextForTest(object context) => TryCancelContextLifetime(context);
}
