using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, "ready")]
    [InlineData(true, "ready")]
    [InlineData(false, "runtime_no_transfer")]
    [InlineData(true, "runtime_no_transfer")]
    [InlineData(false, "ack_held")]
    [InlineData(true, "ack_held")]
    [InlineData(false, "answer_held")]
    [InlineData(true, "answer_held")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "consumed")]
    [InlineData(true, "consumed")]
    [InlineData(false, "wrong_direction")]
    [InlineData(true, "wrong_direction")]
    [Trait("FileTransferInfraSuite", "InitialTunaReadiness")]
    public async Task InitialTunaReadiness_PairedServicesRequireLocalEvidenceAtOfferAdmission(
        bool helperPays, string control)
    {
        var previousPriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var heldMessage = new TaskCompletionSource<(string Destination, byte[] Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var identity = Guid.NewGuid().ToString("N");
            var hostClient = new FakeNknClient($"host.initial-readiness.{identity}.address");
            var helperClient = new FakeNknClient($"helper.initial-readiness.{identity}.address");
            var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0, supportedLanes: NknAccelerationLaneKind.File);
            var helperLane = new RetryableTunaAccelerationSession(helperPays, 0, supportedLanes: NknAccelerationLaneKind.File);
            var payerClient = helperPays ? helperClient : hostClient;
            var runtimeNegotiation = control is "answer_held" or "runtime_no_transfer";
            var heldClient = runtimeNegotiation ? (helperPays ? hostClient : helperClient) : payerClient;
            var heldType = runtimeNegotiation ? MsgType.TransportAccelerationAnswer : MsgType.TransportAccelerationAnswerAck;
            heldClient.ShouldDeliverSendAsync = (destination, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) &&
                    envelope.Type == heldType && !releaseDelivery.Task.IsCompleted)
                {
                    // Hold network delivery, not the sender's control queue:
                    // unrelated file-offer/decline messages must still flow.
                    heldMessage.TrySetResult((destination, bytes.ToArray()));
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            };
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-initial-readiness", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-initial-readiness", helperClient.Address), NknTunaAccelerationOptions.Disabled, helperLane);
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            var transferId = $"initial_readiness_{helperPays}_{control}";
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            var offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            helper.FileTransferOfferReceived += (_, args) => offerReceived.TrySetResult(args.Message);
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            var logStart = GetOperationalLogLength();

            // Real offer/answer/ACK messages, with no transfer, route
            // publication, readiness snapshot or runtime transaction preseeded.
            if (runtimeNegotiation)
            {
                // Keep the original transfer-scoped answer guard regression.
                // Public runtime_unlock now rejects this late/unknown preparation
                // attachment; its selection is tested in SessionPreparationRequest.
                InvokePrivateMethod(helperPays ? helper : host, "ScheduleAccelerationNegotiationIfEligible",
                    "runtime_unlock", null, false, true, 0L);
            }
            else await (helperPays ? helper : host).RequestAccelerationNegotiationAsync("session_security_state_ready", cts.Token);
            var captured = await heldMessage.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            Assert.Equal(!helperPays, hostLane.IsLocalPaidListenerActive);
            Assert.Equal(helperPays, helperLane.IsLocalPaidListenerActive);
            Assert.Contains(ReadOperationalLogTail(logStart).Split('\n'), line =>
                line.Contains("event=runtime_unlock_transaction_answer_received;", StringComparison.Ordinal) &&
                line.Contains("peer_visible_proof=1;", StringComparison.Ordinal) &&
                line.Contains("answer_received=1;", StringComparison.Ordinal) &&
                line.Contains("tuna_path_lease_current=1;", StringComparison.Ordinal) &&
                line.Contains("tuna_path_lease_state=listenerready;", StringComparison.Ordinal));
            if (runtimeNegotiation)
            {
                Assert.Contains("reason=transport_acceleration_answer_pending;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            }

            if (control is not ("ack_held" or "answer_held"))
            {
                releaseDelivery.TrySetResult();
                await heldClient.SendAsync(captured.Destination, captured.Bytes, cts.Token);
                if (control == "runtime_no_transfer")
                {
                    await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                        line.Contains("event=runtime_unlock_transaction_peer_received;", StringComparison.Ordinal) &&
                        line.Contains("reason=transport_acceleration_answer;", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
                    var payer = helperPays ? helper : host;
                    var boundary = GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary");
                    Assert.NotNull(boundary);
                    var sourceScope = ContextValue(boundary!, "InitialActivationExpectedScope");
                    Assert.NotNull(sourceScope);
                    Assert.Null(ContextValue(sourceScope!, "SourcePublicationAuthority"));
                    // A session-only unlock has no existing Coordinator source
                    // publication. Preserve this guard; do not invent one to
                    // make the pre-transfer GUI readiness condition pass.
                    Assert.False(Assert.IsType<bool>(InvokePrivateMethod(payer,
                        "IsRuntimeUnlockInitialActivationAnswerBoundarySourceCurrent", boundary)));
                    Assert.False(Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                        GetPrivateField(payer, "runtimeUnlockTransactionState")).AnswerReceived);
                    Assert.Null(GetPrivateField(payer, "runtimeUnlockRetiredAnswerBarrier"));
                }
                else
                {
                    await WaitUntilAsync(() => GetPrivateField(host, "initialTunaReadinessState") is not null &&
                        GetPrivateField(helper, "initialTunaReadinessState") is not null, TimeSpan.FromSeconds(3));
                    var snapshot = GetPrivateField(helper, "initialTunaReadinessState")!;
                    switch (control)
                    {
                        case "missing":
                            SetPrivateField(helper, "initialTunaReadinessState", null);
                            break;
                        case "expired":
                            SetPrivateFieldDynamic(snapshot, "<CapturedUtc>k__BackingField", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(6));
                            break;
                        case "consumed":
                            Assert.True(((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(
                                sessionId, "prior-transfer", FileTransferDirection.Inbound, out _));
                            break;
                        case "wrong_direction":
                            SetPrivateFieldDynamic(snapshot, "<Direction>k__BackingField", FileTransferDirection.Outbound);
                            break;
                    }
                }
            }

            // Capture the production providers with the exact transfer/direction
            // the services will use. A successful call consumes the snapshot for
            // this transfer, so it is not a general-purpose readiness poll.
            var senderReady = ((IFileTransferInitialTunaReadinessProvider)host).TryGetInitialTunaReadinessEvidence(
                sessionId, transferId, FileTransferDirection.Outbound, out var senderEvidence);
            var receiverReady = ((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(
                sessionId, transferId, FileTransferDirection.Inbound, out var receiverEvidence);
            Assert.Equal(control switch { "ack_held" => !helperPays, "answer_held" or "runtime_no_transfer" => helperPays, _ => true }, senderReady);
            Assert.Equal(control == "ready" || (control == "ack_held" && helperPays) ||
                (runtimeNegotiation && !helperPays), receiverReady);
            Assert.Equal(senderReady, senderEvidence is not null);
            Assert.Equal(receiverReady, receiverEvidence is not null);
            Assert.Null(sender.Snapshot.Outbound);
            Assert.Null(receiver.Snapshot.Inbound);
            if (senderEvidence is not null)
            {
                Assert.Equal((sessionId, transferId, FileTransferDirection.Outbound),
                    (senderEvidence.SessionId, senderEvidence.TransferId, senderEvidence.Direction));
            }
            if (receiverEvidence is not null)
            {
                Assert.Equal((sessionId, transferId, FileTransferDirection.Inbound),
                    (receiverEvidence.SessionId, receiverEvidence.TransferId, receiverEvidence.Direction));
            }

            await sender.TryStartSendAsync(new("initial-readiness.bin", 8, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[8], writable: false)), cts.Token);
            var offer = await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var expectedRoute = senderReady ? FileTransferRoute.FileTunaV4 : FileTransferRoute.RegularNknV4Fast;
            var expectedToken = senderReady ? FileTransferRouteResolver.FileTunaV4Token : FileTransferRouteResolver.RegularNknV4FastToken;
            Assert.Equal(expectedToken, offer.FileTransferRoute);
            Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
            if (senderReady && !receiverReady)
            {
                await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Declined,
                    TimeSpan.FromSeconds(3));
                Assert.Null(receiver.Snapshot.Inbound);
                var log = ReadOperationalLogTail(logStart);
                Assert.Contains(log.Split('\n'), line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("direction=inbound;", StringComparison.Ordinal) &&
                    line.Contains("initial_tuna_readiness_missing", StringComparison.Ordinal));
                Assert.DoesNotContain(log.Split('\n'), line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("event=filetransfer_route_selected; direction=inbound;", StringComparison.Ordinal));
                Assert.DoesNotContain(log.Split('\n'), line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("event=filetransfer_runtime_started;", StringComparison.Ordinal));
            }
            else
            {
                await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                    TimeSpan.FromSeconds(3));
                AssertPublication(sender, "outboundTransfer", FileTransferDirection.Outbound);
                AssertPublication(receiver, "inboundTransfer", FileTransferDirection.Inbound);
            }

            void AssertPublication(SessionFileTransferService service, string contextField, FileTransferDirection direction)
            {
                lock (GetPrivateField(service, "gate")!)
                {
                    var context = GetPrivateField(service, contextField)!;
                    var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                    var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                    Assert.Equal((sessionId, transferId, direction), (publication.SessionId, publication.TransferId, publication.Direction));
                    Assert.Equal(expectedRoute, publication.Route);
                    Assert.Equal(FileTransferProtocol.ProtocolVersionV4, publication.ProtocolVersion);
                    Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _));
                }
            }
        }
        finally
        {
            releaseDelivery.TrySetResult();
            cts.Cancel();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriorityDelay;
        }
    }
}
