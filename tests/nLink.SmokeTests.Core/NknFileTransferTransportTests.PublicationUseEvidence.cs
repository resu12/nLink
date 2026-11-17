using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData("replacement", true)]
    [InlineData("reset", true)]
    [InlineData("clone", false)]
    [InlineData("missing", false)]
    [InlineData("wrong-scope", false)]
    [InlineData("late-capture", false)]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_PublicationUseSendKeepsOperationCaptureAcrossAwait(string change, bool expected)
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            var cycle = FileTransferPublicationUseCaptureTests.CreateCycle(session, "security-transfer", FileTransferDirection.Outbound);
            var authority = FileTransferPublicationUseCaptureTests.CreateAuthority(cycle[0].Token);
            var original = cycle[0].Evidence;
            if (change is not ("missing" or "late-capture"))
            {
                FileTransferPublicationUseEvidence.CaptureSend(authority,
                    change == "wrong-scope" ? original with { SessionId = DataPathScopeFingerprint("wrong-session") } : original,
                    FileTransferPublicationUseKind.V4PhysicalSubmission);
            }
            if (change == "clone") { authority = authority with { }; }
            PublishPublicationUse(sender, cycle[0]); // Missing explicit evidence must not borrow this cache entry.
            var entered = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            senderClient.BeforeSendAsync = async (_, bytes, token) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                {
                    entered.TrySetResult(envelope);
                    await release.Task.WaitAsync(token);
                }
            };
            senderClient.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            var frame = PublicationUseFrame(session);
            var method = SecurityMethod("SendFileTransferEnvelopeRawAsync");
            var parameters = method.GetParameters();
            var args = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            args[0] = MsgType.FileTransferDataFrame; args[1] = frame.TransferId;
            args[2] = FileTransferDataFrameCodec.SerializeLegacyV4(frame); args[3] = true;
            args[4] = frame.Type; args[5] = ct; args[6] = 2; args[7] = 1;
            args[8] = frame.BatchProfile; args[9] = true;
            args[Array.FindIndex(parameters, p => p.Name == "dataPathSendAuthority")] = authority;
            args[Array.FindIndex(parameters, p => p.Name == "dataFrame")] = frame;
            var logStart = GetOperationalLogLength();
            var send = Assert.IsAssignableFrom<Task>(method.Invoke(sender, args));
            var envelope = await entered.Task.WaitAsync(ct);
            try
            {
                PublishPublicationUse(sender, cycle[3]);
                FileTransferPublicationUseEvidence.CaptureSend(authority, cycle[3].Evidence,
                    FileTransferPublicationUseKind.V4PhysicalSubmission);
                if (change == "reset") { SecurityMethod("ResetControlSecureState").Invoke(sender, null); }
            }
            finally { release.TrySetResult(); }
            await send.WaitAsync(ct);
            var row = Assert.Single(ReadOperationalLogTail(logStart).Split('\n'),
                line => line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal));
            Assert.Equal(expected, row.Contains("publication_use_kind=", StringComparison.Ordinal));
            if (expected)
            {
                Assert.Contains(original.FormatLogSuffix(), row, StringComparison.Ordinal);
                Assert.Contains("publication_use_kind=v4_physical_submission", row, StringComparison.Ordinal);
                Assert.DoesNotContain(cycle[3].Evidence.BoundaryId, row, StringComparison.Ordinal);
            }
            Assert.Equal(SecurityFingerprint("payload", "ftpay1-", envelope.Payload),
                SecurityEvidenceValue(row, "security_payload_fingerprint"));
            Assert.Equal(change == "reset" ? "retired" : "captured", SecurityEvidenceValue(row, "security_context_status"));
        });

    [Theory]
    [InlineData("exact", true)]
    [InlineData("stale-notification", true)]
    [InlineData("missing-replacement", false)]
    [InlineData("wrong-session", false)]
    [InlineData("wrong-transfer", false)]
    [InlineData("missing-scope", false)]
    [InlineData("outbound-only", false)]
    [InlineData("cleanup", false)]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_PublicationUseGuardCapturesOnlyExactInboundAssociation(string change, bool expected)
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            var direction = change == "outbound-only" ? FileTransferDirection.Outbound : FileTransferDirection.Inbound;
            var cycle = FileTransferPublicationUseCaptureTests.CreateCycle(session, "security-transfer", direction);
            var current = cycle[3];
            PublishPublicationUse(receiver, current);
            if (change == "stale-notification") { PublishPublicationUse(receiver, cycle[1]); }
            if (change == "missing-replacement") { PublishPublicationUse(receiver, current, omitEvidence: true); }
            if (change is "wrong-session" or "wrong-transfer" or "missing-scope")
            {
                PublishPublicationUse(receiver, (current.Token, change switch
                {
                    "wrong-session" => current.Evidence with { SessionId = DataPathScopeFingerprint("wrong") },
                    "missing-scope" => current.Evidence with { SessionId = "(none)", TransferId = "(none)" },
                    _ => current.Evidence with { TransferId = DataPathScopeFingerprint("wrong") },
                }));
            }
            if (change == "cleanup")
            {
                SecurityMethod("RemoveFileTransferRouteHintsForTransferUnsafe").Invoke(receiver, ["security-transfer", session]);
                Assert.Empty(Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferPublicationCaptures")));
            }
            var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferStates"));
            var before = states.Count;
            var frame = PublicationUseFrame(session);
            var encrypted = EncryptWithSecurityEvidence(sender, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
            var envelope = new Envelope(1, "publication-use-test", "publication-guard", MsgType.FileTransferDataFrame, encrypted.Bytes, 0, null);
            var logStart = GetOperationalLogLength();
            var args = new object?[] { senderClient.ConnectedBulkAddress, NknBridgeChannel.Bulk, envelope, null };
            Assert.False((bool)SecurityMethod("TryPrepareFileTransferDataFrameDispatch").Invoke(receiver, args)!);
            Assert.Equal(before, states.Count);
            var rows = DataPathProvenanceRows(ReadOperationalLogTail(logStart), DataPathEnvelopeFingerprint(envelope.MessageId));
            Assert.Equal(change is "outbound-only" or "cleanup" ? 0 : 3, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Contains("receive_disposition=rejected_before_dispatch", row, StringComparison.Ordinal);
                Assert.Contains("authority_effect=none", row, StringComparison.Ordinal);
                Assert.Equal(expected, row.Contains("publication_use_kind=", StringComparison.Ordinal));
                if (expected)
                {
                    Assert.Contains(current.Evidence.FormatLogSuffix(), row, StringComparison.Ordinal);
                    Assert.Contains("publication_use_kind=receive_guard", row, StringComparison.Ordinal);
                }
            });
            await Task.CompletedTask;
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_PublicationUseSnapshotIsDetachedFromLaterPublicationAndAuthorityCopies()
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            var cycle = FileTransferPublicationUseCaptureTests.CreateCycle(session, "security-transfer", FileTransferDirection.Inbound);
            PublishPublicationUse(receiver, cycle[1]);
            var args = new object?[] { session, "security-transfer", FileTransferDirection.Inbound, null, null };
            Assert.True((bool)SecurityMethod("TryGetFileTransferDataPathRouteAuthorityWithEvidence").Invoke(receiver, args)!);
            var captured = Assert.IsType<FileTransferRoutePublicationEvidence>(args[4]);
            PublishPublicationUse(receiver, cycle[3]);
            Assert.Equal(cycle[1].Evidence, captured);
            Assert.True((bool)SecurityMethod("TryGetFileTransferDataPathRouteAuthorityWithEvidence").Invoke(receiver, args)!);
            Assert.Equal(cycle[3].Evidence, args[4]);

            // An equal-valued replacement in the authority map is not the captured object.
            var map = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferRoutePublicationAuthorities"));
            var key = Assert.Single(map.Keys.Cast<object>());
            map[key] = ((FileTransferRoutePublicationAuthority)map[key]!) with { };
            Assert.True((bool)SecurityMethod("TryGetFileTransferDataPathRouteAuthorityWithEvidence").Invoke(receiver, args)!);
            Assert.Null(args[4]); // Route lookup result is unchanged; evidence alone is unbound.
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData("filetransfer_data_path_send_result")]
    [InlineData("filetransfer_data_path_receive_result")]
    [InlineData("filetransfer_tuna_data_path_peer_visible")]
    [InlineData("filetransfer_tuna_data_path_mismatch")]
    [InlineData("filetransfer_data_frame_ignored")]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public void NknTransport_PublicationUseRedactionPreservesProjectedScopesNotSecrets(string eventName)
    {
        var session = "sess_" + new string('a', 64);
        var current = FileTransferPublicationUseCaptureTests.CreateCycle(session, new string('b', 64), FileTransferDirection.Inbound)[3];
        var use = new FileTransferPublicationUseEvidence(current.Evidence, FileTransferPublicationUseKind.ReceiveGuard);
        var input = $"event={eventName}; session_id={session}; transfer_id={current.Token.TransferId}" +
            use.FormatLogSuffix() + $"; source={new string('c', 64)}; sharedKey={new string('d', 64)}";
        var output = SensitiveDataRedactor.Redact(input);
        Assert.Contains("session_id=sess_[redacted]", output, StringComparison.Ordinal);
        Assert.Contains(use.FormatLogSuffix(), output, StringComparison.Ordinal);
        Assert.DoesNotContain(session, output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', 64), output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('d', 64), output, StringComparison.Ordinal);
        var malformed = input.Replace(current.Evidence.CoordinatorId, "fte1-" + new string('e', 64), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('e', 64), SensitiveDataRedactor.Redact(malformed), StringComparison.Ordinal);
        Assert.Equal(output, SensitiveDataRedactor.Redact(output));
    }

    private static FileTransferChunkBatchFrameV4 PublicationUseFrame(string session)
        => new() { SessionId = session, TransferId = "security-transfer", StartChunkIndex = 0, ChunkCount = 1,
            DataSegments = [new byte[] { 17, 34 }], BatchProfile = "v4_default_21k" };

    [Fact]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_PublicationUseActualDispatchKeepsItsOwnPublicationAndPayload()
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            const string transfer = "security-transfer";
            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
            sender.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
            receiver.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            await sender.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = session, TransferId = transfer,
                FileName = "publication.bin", FileSizeBytes = 2, PreferredDataProtocolVersion = 4 }, ct);
            await offered.Task.WaitAsync(ct);
            await receiver.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = session,
                TransferId = transfer, AcceptedDataProtocolVersion = 4 }, ct);
            await accepted.Task.WaitAsync(ct);
            await sender.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = session,
                TransferId = transfer, ProtocolVersion = 4, SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, ct);
            await opened.Task.WaitAsync(ct);
            using var inbound = await receiver.OpenFileTransferDataSessionAsync(session, transfer, ct);
            using var outbound = await sender.OpenFileTransferDataSessionAsync(session, transfer, ct);
            var source = FileTransferPublicationUseCaptureTests.CreateCycle(session, transfer, FileTransferDirection.Outbound)[0];
            var target = FileTransferPublicationUseCaptureTests.CreateCycle(session, transfer, FileTransferDirection.Inbound)[0];
            PublishPublicationUse(sender, source);
            PublishPublicationUse(receiver, target);
            var authority = FileTransferPublicationUseCaptureTests.CreateAuthority(source.Token);
            FileTransferPublicationUseEvidence.CaptureSend(authority, source.Evidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
            var logStart = GetOperationalLogLength();
            await ((IFileTransferDataPathAuthoritySession)outbound).SendAsync(PublicationUseFrame(session), authority, ct);
            var received = await inbound.ReceiveAsync(ct);
            Assert.Equal(transfer, received.TransferId);
            var rows = ReadOperationalLogTail(logStart).Split('\n');
            var sent = Assert.Single(rows, row => row.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal));
            var dispatch = Assert.Single(rows, row => row.Contains("event=filetransfer_data_path_receive_result;", StringComparison.Ordinal));
            Assert.Contains("publication_use_kind=v4_physical_submission", sent, StringComparison.Ordinal);
            Assert.Contains(source.Evidence.FormatLogSuffix(), sent, StringComparison.Ordinal);
            Assert.Contains("receive_disposition=dispatch_entered", dispatch, StringComparison.Ordinal);
            Assert.Contains("publication_use_kind=receive_dispatch", dispatch, StringComparison.Ordinal);
            Assert.Contains(target.Evidence.FormatLogSuffix(), dispatch, StringComparison.Ordinal);
            Assert.NotEqual(source.Evidence.CoordinatorId, target.Evidence.CoordinatorId);
            Assert.Equal(SecurityEvidenceValue(sent, "security_payload_fingerprint"), SecurityEvidenceValue(dispatch, "security_payload_fingerprint"));
            Assert.Equal(SecurityEvidenceValue(sent, "security_sequence"), SecurityEvidenceValue(dispatch, "security_sequence"));
        });

    private static void PublishPublicationUse(NknSignalingTransport transport,
        (RoutePublicationToken Token, FileTransferRoutePublicationEvidence Evidence) item, bool omitEvidence = false)
        => ((IFileTransferRouteHintObserver)transport).ObserveFileTransferRouteHint(
            new FileTransferRouteHintNotification(item.Token.SessionId, item.Token.TransferId, item.Token.RouteToken,
                item.Token.ProtocolVersion, "publication_use_test",
                PublicationAuthority: FileTransferCoordinator.CreateRoutePublicationAuthority(item.Token))
            { PublicationEvidence = omitEvidence ? null : item.Evidence });
}
