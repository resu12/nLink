using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "YieldEvidence")]
    public void YieldEvidence_MissingSnapshotAndProjectionFailureCannotInventAnAssociation()
    {
        var frame = YieldEvidenceFrame("missing-snapshot");
        var evidence = new FileTransferFallbackTailYieldEvidence(frame, new("control", () => ("bound", "")));
        var log = typeof(SessionFileTransferService).GetMethod("LogYieldReceiptAssociation", BindingFlags.Static | BindingFlags.NonPublic)!;
        var start = GetOperationalLogLength();
        // Negative formatter control, not a source of positive receipt evidence.
        log.Invoke(null, [frame, evidence, null, true, "local_proof", "Accepted"]);
        var row = Assert.Single(ReadOperationalLogTail(start).Split('\n'), line =>
            line.Contains("yield_evidence_role=receipt_association;", StringComparison.Ordinal));
        Assert.Contains("yield_evidence_status=unbound;", row, StringComparison.Ordinal);
        Assert.Contains("yield_receipt_decision=Accepted;", row, StringComparison.Ordinal);
        Assert.Contains("yield_receipt_envelope=missing;", row, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt_identity=", row, StringComparison.Ordinal);
        Assert.DoesNotContain("transition_id=", row, StringComparison.Ordinal);
        var broken = new FileTransferFallbackTailYieldEvidence(frame, new("control", () => throw new InvalidOperationException("diagnostic_failure")));
        log.Invoke(null, [frame, broken, null, true, "local_proof", "Accepted"]);
        broken.WriteCopy("send_copy", broken.First);
        Assert.Single(ReadOperationalLogTail(start).Split('\n'), line =>
            line.Contains("yield_evidence_role=receipt_association;", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "YieldEvidence")]
    public void YieldEvidence_CanonicalReceiptProjectionPreservesOriginalHashAndNormalization()
    {
        var frame = YieldEvidenceFrame("session-yield") with
        {
            TransferId = "transfer-yield", OwnerKey = "owner", PairKey = "pair",
        };
        const string hash = "56f9dfa75664e6ed10145e6fcedeef07ee5fcb9aef70226b91b6f730a8c5b443";
        Assert.Equal(hash, SessionFileTransferService.CreateFallbackTailYieldFrameFingerprint(frame));
        Assert.Equal(hash, SessionFileTransferService.CreateFallbackTailYieldFrameFingerprint(frame with { RepairRequestId = "  " }));
        Assert.NotEqual(hash, SessionFileTransferService.CreateFallbackTailYieldFrameFingerprint(frame with { RepairRequestId = " checkpoint " }));
        var proof = $"session-yield:transfer-yield:outbound:{frame.AuthorityId}:1:{hash}";
        Assert.Equal(proof, SessionFileTransferService.CreateFallbackTailYieldProofIdentity(frame));
        var evidence = new FileTransferFallbackTailYieldEvidence(frame, new("control", () => ("bound", "")));
        Assert.Equal(SessionFileTransferService.ProjectYieldEvidenceToken(proof), evidence.ProofIdentity);
        Assert.Equal(SessionFileTransferService.ProjectYieldEvidenceToken(hash), evidence.FrameFingerprint);
    }

    [Theory]
    [InlineData("control-only")]
    [InlineData("bulk-only")]
    [InlineData("both")]
    [InlineData("queue-only")]
    [InlineData("all-failed")]
    [InlineData("preflight-rejected")]
    [InlineData("cancelled")]
    [InlineData("stale")]
    [InlineData("retired")]
    [Trait("FileTransferInfraSuite", "YieldEvidence")]
    public async Task YieldEvidence_ControlPlaneCapturesOriginalCopiesWithoutChangingOutcomes(string mode)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var data = await sender.OpenFileTransferDataSessionAsync(session, "security-transfer", ct);
            if (mode == "control-only") { SetPrivateField(sender, "remoteBulkEndpoint", null); }
            var packets = new ConcurrentQueue<(NknBridgeChannel Lane, Envelope Envelope)>();
            var current = true;
            client.BeforeSendCoreAsync = (_, bytes, lane, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope) || envelope.Type != MsgType.FileTransferDataFrame)
                    return Task.CompletedTask;
                packets.Enqueue((lane, envelope));
                if (mode == "stale") { current = false; }
                var queuedCopy = new System.Diagnostics.StackTrace().GetFrames().Any(stack =>
                    stack.GetMethod()?.DeclaringType?.FullName?.Contains("ControlOutboundQueue", StringComparison.Ordinal) == true);
                if (mode == "all-failed" || mode == "queue-only" && !queuedCopy || mode == "bulk-only" && lane == NknBridgeChannel.Control)
                    throw new InvalidOperationException("test_copy_failure");
                return Task.CompletedTask;
            };
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            using var cancelled = new CancellationTokenSource();
            if (mode == "cancelled") { cancelled.Cancel(); }
            var calls = 0;
            var frame = YieldEvidenceFrame(session);
            var request = new FileTransferControlPlaneDeliveryRequest(FileTransferControlPlaneKind.FallbackTailYield, frame, "yield_evidence_test")
            {
                PreTransportDeliveryValidator = () => mode == "preflight-rejected" ? ++calls == 1 : current,
                CopyTimeout = TimeSpan.FromMilliseconds(250),
            };
            var sequence = sender.NextOutboundFileTransferSecureSequence;
            var start = GetOperationalLogLength();
            var result = await sender.SendFileTransferControlPlaneFrameAsync(request,
                mode == "cancelled" ? cancelled.Token : ct);
            var evidence = Assert.IsType<FileTransferFallbackTailYieldEvidence>(result.YieldEvidence);
            Assert.Matches("^ftye1-[0-9a-f]{32}$", evidence.Id);
            Assert.Equal(sequence + (mode == "control-only" ? 1 : 2), sender.NextOutboundFileTransferSecureSequence);
            Assert.Equal("control", evidence.First.Name);
            Assert.Equal(mode != "control-only", evidence.Second is not null);
            Assert.DoesNotContain("YieldEvidence", JsonSerializer.Serialize(result), StringComparison.Ordinal);
            Assert.Equal(result.ControlAck || result.ControlCopy || result.BulkCopy, result.PeerVisibleAny);
            Assert.Equal(result.PeerVisibleAny, result.AcceptedAny);
            if (mode is "control-only" or "both" or "retired") { Assert.True(result.ControlCopy); }
            if (mode is "bulk-only" or "both" or "retired") { Assert.True(result.BulkCopy); }
            if (mode is "bulk-only" or "queue-only" or "all-failed" or "preflight-rejected" or "cancelled") { Assert.False(result.ControlCopy); }
            if (mode is "queue-only" or "all-failed" or "preflight-rejected" or "cancelled") { Assert.False(result.PeerVisibleAny); }
            if (mode == "queue-only") { Assert.True(result.ControlQueue); }
            if (mode == "preflight-rejected") { Assert.False(result.AcceptedAny); Assert.Empty(packets); }
            var rows = ReadOperationalLogTail(start).Split('\n').Where(row =>
                row.Contains("yield_evidence_role=send_copy;", StringComparison.Ordinal)).ToArray();
            Assert.Equal(mode == "control-only" ? 1 : 2, rows.Length);
            foreach (var row in rows)
            {
                Assert.Contains($"yield_evidence_id={evidence.Id};", row, StringComparison.Ordinal);
                Assert.Contains("authority_effect=none", row, StringComparison.Ordinal);
                Assert.DoesNotContain("coordinator_instance_id=", row, StringComparison.Ordinal);
                var bulk = SecurityEvidenceValue(row, "yield_evidence_copy") == "bulk";
                Assert.Equal(bulk ? result.BulkCopy : result.ControlCopy,
                    SecurityEvidenceValue(row, bulk ? "bulk_copy" : "control_copy") == "1");
                var copies = packets.Where(packet => (packet.Lane == NknBridgeChannel.Bulk) == bulk).ToArray();
                if (copies.Length > 0)
                {
                    Assert.Single(copies.Select(packet => packet.Envelope.MessageId).Distinct());
                    Assert.Equal(SecurityFingerprint("payload", "ftpay1-", copies[0].Envelope.Payload),
                        SecurityEvidenceValue(row, "security_payload_fingerprint"));
                }
            }
            if (evidence.Second is { } second)
                Assert.NotEqual(SecurityEvidenceValue(evidence.First.ReadCapture().Suffix, "security_payload_fingerprint"),
                    SecurityEvidenceValue(second.ReadCapture().Suffix, "security_payload_fingerprint"));
            if (mode == "retired")
            {
                var old = evidence.First.ReadCapture().Suffix;
                var key = SecurityMethod("GetControlSessionSharedKeyOrThrow").Invoke(sender, null);
                SecurityMethod("SetControlSessionSharedKey").Invoke(sender, [key]);
                Assert.Equal("retired", evidence.Status);
                Assert.Equal(SecurityEvidenceValue(old, "security_installation_id"),
                    SecurityEvidenceValue(evidence.First.ReadCapture().Suffix, "security_installation_id"));
                Assert.True(result.AcceptedAny);
            }
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "YieldEvidence")]
    public async Task YieldEvidence_AuthenticatedDispatchCarriesSameReferenceThroughQueueAndEvent()
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            var offerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferOfferReceived += (_, _) => offerReceived.TrySetResult();
            sender.FileTransferAcceptReceived += (_, _) => acceptReceived.TrySetResult();
            await sender.SendFileTransferOfferAsync(new()
            {
                SessionId = session, TransferId = "security-transfer", FileName = "yield.bin", FileSizeBytes = 1024,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            }, ct);
            await offerReceived.Task.WaitAsync(ct);
            await receiver.SendFileTransferAcceptAsync(new()
            {
                SessionId = session, TransferId = "security-transfer", AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            }, ct);
            await acceptReceived.Task.WaitAsync(ct);
            using var sendData = await sender.OpenFileTransferDataSessionAsync(session, "security-transfer", ct);
            using var receiveData = await receiver.OpenFileTransferDataSessionAsync(session, "security-transfer", ct);
            SetPrivateField(sender, "remoteBulkEndpoint", null);
            var delivered = new TaskCompletionSource<FileTransferFallbackTailYieldProofReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferFallbackTailYieldProofReceived += (_, args) => delivered.TrySetResult(args);
            var result = await sender.SendFileTransferControlPlaneFrameAsync(new(
                FileTransferControlPlaneKind.FallbackTailYield, YieldEvidenceFrame(session), "yield_ingress_test"), ct);
            var queued = await receiveData.ReceiveWithMetadataAsync(ct);
            var raised = await delivered.Task.WaitAsync(ct);
            Assert.Same(raised.YieldEvidence, queued.YieldEvidence);
            var evidence = Assert.IsType<FileTransferFallbackTailYieldEvidence>(queued.YieldEvidence);
            Assert.Equal("bound", evidence.Status);
            Assert.Equal(result.YieldEvidence!.ProofIdentity, evidence.ProofIdentity);
            Assert.Equal(SecurityEvidenceValue(result.YieldEvidence.First.ReadCapture().Suffix, "security_payload_fingerprint"),
                SecurityEvidenceValue(evidence.First.ReadCapture().Suffix, "security_payload_fingerprint"));
            Assert.NotEqual(result.YieldEvidence.Id, evidence.Id);
            SecurityMethod("ResetControlSecureState").Invoke(receiver, null);
            Assert.Equal("retired", evidence.Status);
        });

    [Theory]
    [InlineData(false, true, "bound")]
    [InlineData(false, false, "bound")]
    [InlineData(true, true, "bound")]
    [InlineData(true, false, "bound")]
    [InlineData(false, true, "legacy")]
    [InlineData(false, false, "retired")]
    [InlineData(false, true, "rejected")]
    [InlineData(false, false, "stale")]
    [Trait("FileTransferRuntimeSuite", "YieldEvidence")]
    public async Task YieldEvidence_ServiceOwnedSendAndReceiptsUseOriginalCapture(bool inbound, bool eventFirst, string mode)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            var offer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferOfferReceived += (_, _) => offer.TrySetResult();
            sender.FileTransferAcceptReceived += (_, _) => accept.TrySetResult();
            await sender.SendFileTransferOfferAsync(new()
            {
                SessionId = session, TransferId = "security-transfer", FileName = "yield.bin", FileSizeBytes = 256L * 21 * 1024,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            }, ct);
            await offer.Task.WaitAsync(ct);
            await receiver.SendFileTransferAcceptAsync(new()
            {
                SessionId = session, TransferId = "security-transfer", AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            }, ct);
            await accept.Task.WaitAsync(ct);
            using var sendData = await sender.OpenFileTransferDataSessionAsync(session, "security-transfer", ct);
            using var receiveData = await receiver.OpenFileTransferDataSessionAsync(session, "security-transfer", ct);
            SetPrivateField(sender, "remoteBulkEndpoint", null);
            var delivered = new TaskCompletionSource<FileTransferFallbackTailYieldProofReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferFallbackTailYieldProofReceived += (_, args) => delivered.TrySetResult(args);
            await SessionFileTransferV4SenderTests.WithServiceYieldEvidenceSenderAsync(sender, sendData, inbound, async () =>
            {
                var queued = await receiveData.ReceiveWithMetadataAsync(ct);
                var raised = await delivered.Task.WaitAsync(ct);
                Assert.Same(queued.YieldEvidence, raised.YieldEvidence);
                Assert.NotNull(queued.YieldEvidence);
                if (mode == "retired") { SecurityMethod("ResetControlSecureState").Invoke(receiver, null); }
                await SessionFileTransferV4SenderTests.CheckServiceYieldEvidenceReceiptsAsync(queued, raised, eventFirst, mode);
            });
        });

    [Fact]
    [Trait("FileTransferRuntimeSuite", "YieldEvidence")]
    public Task YieldEvidence_CoordinatorRejectedReceiptCannotBorrowAcceptedAssociation()
        => YieldEvidence_ServiceOwnedSendAndReceiptsUseOriginalCapture(false, true, "coordinator-rejected");

    [Theory]
    [InlineData("reset")]
    [InlineData("same-key")]
    [Trait("FileTransferInfraSuite", "YieldEvidence")]
    public async Task YieldEvidence_ControlPlaneRetirementDuringSendKeepsCapturedInstallation(string retirement)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var data = await sender.OpenFileTransferDataSessionAsync(session, "security-transfer", ct);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ShouldDeliverSendAsync = async (_, bytes, token) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return false;
                }
                return true;
            };
            var original = GetPrivateField(sender, "fileTransferSecurityContext")!;
            var send = sender.SendFileTransferControlPlaneFrameAsync(new(FileTransferControlPlaneKind.FallbackTailYield,
                YieldEvidenceFrame(session), "yield_retirement_test"), ct);
            await entered.Task.WaitAsync(ct);
            try
            {
                if (retirement == "reset") { SecurityMethod("ResetControlSecureState").Invoke(sender, null); }
                else
                {
                    var key = SecurityMethod("GetControlSessionSharedKeyOrThrow").Invoke(sender, null);
                    SecurityMethod("SetControlSessionSharedKey").Invoke(sender, [key]);
                }
            }
            finally { release.TrySetResult(); }
            var result = await send;
            Assert.True(result.PeerVisibleAny);
            var evidence = Assert.IsType<FileTransferFallbackTailYieldEvidence>(result.YieldEvidence);
            Assert.Equal("retired", evidence.Status);
            Assert.Equal(SecurityProperty(original, "InstallationId"),
                SecurityEvidenceValue(evidence.First.ReadCapture().Suffix, "security_installation_id"));
        });

    private static FileTransferFallbackTailYieldFrameV6 YieldEvidenceFrame(string session) => new()
    {
        SessionId = session, TransferId = "security-transfer", Direction = "outbound", TransportEpoch = 7,
        OwnerKey = $"{session}|security-transfer|post_tuna_fallback_v6|6|4|5|2|7|(none)",
        PairKey = $"{session}|security-transfer|post_tuna_fallback_v6|6|4|7|(none)",
        YieldGeneration = 1, AuthoritySchema = FileTransferAuthoritySchema.Current,
        AuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567", LiveRouteEpoch = 4,
        FallbackLegGeneration = 5, BridgeRecoveryGeneration = 2, CommittedChunkIndex = 43,
        HighestObservedChunkIndex = 42, SentUnixTimeMilliseconds = 1234,
    };
}
