using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SecurityEvidenceContract")]
    public async Task NknTransport_KeyReinstallationCanRepeatAuthenticatedMetadataForDifferentPayloads(
        bool replaceKey)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var receiverClient = new FakeNknClient("receiver.security-evidence.address");
            var senderClient = new FakeNknClient("sender.security-evidence.address");
            using var receiver = new NknSignalingTransport(receiverClient, options,
                new NknIdentity("security-evidence-receiver", receiverClient.Address));
            using var sender = new NknSignalingTransport(senderClient, options,
                new NknIdentity("security-evidence-sender", senderClient.Address));
            await ApproveNknSessionAsync(receiver, sender, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(NknSignalingTransport);
            var getControlKey = type.GetMethod("GetControlSessionSharedKeyOrThrow", flags)!;
            var installKey = type.GetMethod("SetControlSessionSharedKey", flags)!;
            var getFileKey = type.GetMethod("GetFileTransferSessionSharedKeyOrThrow", flags)!;
            var encrypt = type.GetMethod("CreateSecureFileTransferPayload", flags)!;
            var controlKey = Assert.IsType<byte[]>(getControlKey.Invoke(sender, null));
            var approvedContext = GetPrivateField(sender, "fileTransferSecurityContext")!;

            // Deliberately exercise the actual install/reset boundary. This is
            // an evidence-identity counterexample, not a normal reconnect trace.
            installKey.Invoke(sender, [controlKey]);
            var firstContext = GetPrivateField(sender, "fileTransferSecurityContext")!;
            Assert.True((bool)approvedContext.GetType().GetField("Retired")!.GetValue(approvedContext)!);
            Assert.Equal("(none)", SecurityProperty(firstContext, "EstablishmentFingerprint"));
            var firstKey = Assert.IsType<byte[]>(getFileKey.Invoke(sender, null));
            byte[] firstPlaintext = [17, 34];
            var firstWire = Assert.IsType<byte[]>(encrypt.Invoke(sender,
                [MsgType.FileTransferDataFrame, "transfer_security_evidence", firstPlaintext, true]));
            var first = SessionSecureEnvelopeCodec.Decrypt(firstKey, firstWire);

            var replacement = replaceKey ? RandomNumberGenerator.GetBytes(32) : controlKey;
            installKey.Invoke(sender, [replacement]);
            var secondContext = GetPrivateField(sender, "fileTransferSecurityContext")!;
            Assert.NotEqual(SecurityProperty(firstContext, "InstallationId"), SecurityProperty(secondContext, "InstallationId"));
            Assert.Equal((long)SecurityProperty(firstContext, "Generation")! + 1, SecurityProperty(secondContext, "Generation"));
            Assert.True((bool)firstContext.GetType().GetField("Retired")!.GetValue(firstContext)!);
            var secondKey = Assert.IsType<byte[]>(getFileKey.Invoke(sender, null));
            byte[] secondPlaintext = [51, 68];
            var secondWire = Assert.IsType<byte[]>(encrypt.Invoke(sender,
                [MsgType.FileTransferDataFrame, "transfer_security_evidence", secondPlaintext, true]));
            var second = SessionSecureEnvelopeCodec.Decrypt(secondKey, secondWire);

            Assert.Equal(1, first.Metadata.Sequence);
            Assert.Equal(first.Metadata, second.Metadata);
            Assert.Equal(firstPlaintext, first.Plaintext);
            Assert.Equal(secondPlaintext, second.Plaintext);
            Assert.False(firstWire.AsSpan().SequenceEqual(secondWire));
            if (replaceKey)
            {
                Assert.ThrowsAny<CryptographicException>(() =>
                    SessionSecureEnvelopeCodec.Decrypt(firstKey, secondWire));
            }
            else
            {
                Assert.Equal(firstKey, secondKey);
            }

            // Equal codec metadata produces equal canonical AAD and therefore
            // cannot alone identify a payload across an unbound installation.
            // No frame was dispatched and no positive classifier is enabled.
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "SecurityEvidenceContract")]
    public async Task NknTransport_SecurityCaptureBindsExactApprovalAndPayload_NotOuterIdOrEstimate()
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var senderContext = GetPrivateField(sender, "fileTransferSecurityContext")!;
            var receiverContext = GetPrivateField(receiver, "fileTransferSecurityContext")!;
            var expectedApproval = SecurityFingerprint("establishment", "ftest1-", approvalBytes);
            Assert.Equal(expectedApproval, SecurityProperty(senderContext, "EstablishmentFingerprint"));
            Assert.Equal(expectedApproval, SecurityProperty(receiverContext, "EstablishmentFingerprint"));
            Assert.NotEqual(SecurityProperty(senderContext, "TransportId"), SecurityProperty(receiverContext, "TransportId"));
            Assert.NotEqual(SecurityProperty(senderContext, "InstallationId"), SecurityProperty(receiverContext, "InstallationId"));

            var logStart = GetOperationalLogLength();
            var sequenceBeforeEstimate = sender.NextOutboundFileTransferSecureSequence;
            SecurityMethod("CreateSecureFileTransferPayloadForBudgetEstimate").Invoke(sender,
                [MsgType.FileTransferDataFrame, "security-transfer", new byte[] { 17, 34 }, true]);
            Assert.Equal(sequenceBeforeEstimate, sender.NextOutboundFileTransferSecureSequence);
            Assert.DoesNotContain("security_payload_fingerprint=", ReadOperationalLogTail(logStart), StringComparison.Ordinal);

            var first = EncryptWithSecurityEvidence(sender, [17, 34]);
            var second = EncryptWithSecurityEvidence(sender, [51, 68]);
            Assert.Equal(sequenceBeforeEstimate + 1, SecurityProperty(first.Evidence, "Sequence"));
            Assert.NotEqual(SecurityProperty(first.Evidence, "PayloadFingerprint"), SecurityProperty(second.Evidence, "PayloadFingerprint"));
            var envelope = new Envelope(1, "unused-private-decode-context", "reused-outer-id",
                MsgType.FileTransferDataFrame, first.Bytes, 0, null);
            foreach (var payload in new[] { first, second })
            {
                var args = new object?[] { senderClient.ConnectedBulkAddress, envelope with { Payload = payload.Bytes },
                    MsgType.FileTransferDataFrame, null, null, NknBridgeChannel.Bulk, true };
                Assert.True((bool)SecurityMethod("TryDecryptFileTransferPayloadWithEvidence").Invoke(receiver, args)!);
                Assert.NotNull(args[4]);
                var receiveEvidence = args[4]!;
                Assert.IsType(payload.Evidence.GetType(), receiveEvidence);
                Assert.Equal(SecurityFingerprint("payload", "ftpay1-", payload.Bytes), SecurityProperty(receiveEvidence, "PayloadFingerprint"));
                Assert.Equal(SecurityProperty(payload.Evidence, "Sequence"), SecurityProperty(receiveEvidence, "Sequence"));
                Assert.Equal(SecurityProperty(payload.Evidence, "PayloadFingerprint"), SecurityProperty(receiveEvidence, "PayloadFingerprint"));
                Assert.Contains("security_context_status=captured", FormatSecurityEvidence(receiveEvidence), StringComparison.Ordinal);
            }
            // Private decoding deliberately bypasses outer deduplication; no frame is dispatched.
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData("reset")]
    [InlineData("same-key")]
    [InlineData("different-key")]
    [Trait("FileTransferInfraSuite", "SecurityEvidenceContract")]
    public async Task NknTransport_SecurityCaptureKeepsRetiredContextAcrossAsyncSend(string retirement)
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var entered = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            senderClient.ShouldDeliverSendAsync = async (_, bytes, token) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.FileTransferDataFrame)
                {
                    entered.TrySetResult(env);
                    await release.Task.WaitAsync(token);
                    return false;
                }
                return true;
            };
            var oldContext = GetPrivateField(sender, "fileTransferSecurityContext")!;
            var method = SecurityMethod("SendFileTransferEnvelopeRawAsync");
            var args = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            args[0] = MsgType.FileTransferDataFrame;
            args[1] = "security-transfer";
            args[2] = new byte[] { 17, 34 };
            args[3] = true;
            args[4] = "security-capture-test";
            args[5] = ct;
            args[9] = true; // Existing regular transport only; this test grants no route authority.
            var logStart = GetOperationalLogLength();
            var send = Assert.IsAssignableFrom<Task>(method.Invoke(sender, args));
            var envelope = await entered.Task.WaitAsync(ct);
            try
            {
                if (retirement == "reset")
                {
                    SecurityMethod("ResetControlSecureState").Invoke(sender, null);
                }
                else
                {
                    var key = retirement == "same-key"
                        ? (byte[])SecurityMethod("GetControlSessionSharedKeyOrThrow").Invoke(sender, null)!
                        : RandomNumberGenerator.GetBytes(32);
                    SecurityMethod("SetControlSessionSharedKey").Invoke(sender, [key]);
                }
            }
            finally
            {
                release.TrySetResult();
            }
            await send.WaitAsync(ct);
            var row = Assert.Single(ReadOperationalLogTail(logStart).Split('\n'),
                line => line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal));
            Assert.Equal(SecurityProperty(oldContext, "InstallationId"), SecurityEvidenceValue(row, "security_installation_id"));
            Assert.Equal("retired", SecurityEvidenceValue(row, "security_context_status"));
            Assert.Equal(SecurityFingerprint("payload", "ftpay1-", envelope.Payload), SecurityEvidenceValue(row, "security_payload_fingerprint"));
            Assert.Equal(SecurityFingerprint("establishment", "ftest1-", approvalBytes), SecurityEvidenceValue(row, "security_establishment_fingerprint"));
            Assert.Contains("event=filetransfer_security_context_retired;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        });

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-session")]
    [InlineData("wrong-peer")]
    [InlineData("replay")]
    [Trait("FileTransferInfraSuite", "SecurityEvidenceContract")]
    public async Task NknTransport_SecurityCaptureInvalidReceiveHasNoEvidence(string invalid)
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var encrypted = EncryptWithSecurityEvidence(sender, [17, 34]);
            var key = (byte[])SecurityMethod("GetFileTransferSessionSharedKeyOrThrow").Invoke(sender, null)!;
            var payload = SessionSecureEnvelopeCodec.Decrypt(key, encrypted.Bytes);
            var bytes = invalid switch
            {
                "malformed" => new byte[] { 1, 2, 3 },
                "wrong-session" => SessionSecureEnvelopeCodec.Encrypt(key,
                    payload.Metadata with { SessionId = new SessionId("different-security-session") }, payload.Plaintext),
                "wrong-peer" => SessionSecureEnvelopeCodec.Encrypt(key,
                    payload.Metadata with { SenderIdentity = new PeerAddress("different.peer.address") }, payload.Plaintext),
                _ => encrypted.Bytes,
            };
            var env = new Envelope(1, "unused-private-decode-context", "invalid-envelope", MsgType.FileTransferDataFrame, bytes, 0, null);
            var args = new object?[] { senderClient.ConnectedBulkAddress, env, MsgType.FileTransferDataFrame, null, null, NknBridgeChannel.Bulk, true };
            var decode = SecurityMethod("TryDecryptFileTransferPayloadWithEvidence");
            if (invalid == "replay")
            {
                Assert.True((bool)decode.Invoke(receiver, args)!);
                Assert.NotNull(args[4]);
            }
            Assert.False((bool)decode.Invoke(receiver, args)!);
            Assert.Null(args[4]);
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData("frame-session")]
    [InlineData("frame-transfer")]
    [InlineData("bridge-source")]
    [Trait("FileTransferInfraSuite", "SecurityEvidenceContract")]
    public async Task NknTransport_SecurityCaptureRejectedFrameValidationPublishesNoReference(string invalid)
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var frame = new FileTransferChunkBatchFrameV4
            {
                SessionId = invalid == "frame-session" ? "wrong-frame-session" : sender.CurrentSessionSecurityState.SessionId!.Value.Value,
                TransferId = invalid == "frame-transfer" ? "wrong-frame-transfer" : "security-transfer",
                StartChunkIndex = 0, ChunkCount = 1, DataSegments = [new byte[] { 17, 34 }],
                BatchProfile = "v4_default_21k",
            };
            var encrypted = EncryptWithSecurityEvidence(sender, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
            var env = new Envelope(1, "unused-private-prepare-context", "invalid-frame", MsgType.FileTransferDataFrame, encrypted.Bytes, 0, null);
            var logStart = GetOperationalLogLength();
            var args = new object?[] { invalid == "bridge-source" ? "wrong.bridge.address" : senderClient.ConnectedBulkAddress,
                NknBridgeChannel.Bulk, env, null };
            Assert.False((bool)SecurityMethod("TryPrepareFileTransferDataFrameDispatch").Invoke(receiver, args)!);
            var tail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=filetransfer_data_path_receive_result;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("security_payload_fingerprint=", tail, StringComparison.Ordinal);
            await Task.CompletedTask;
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "SecurityEvidenceContract")]
    public async Task NknTransport_SecurityCaptureResetAfterKeyCopyCannotBorrowNewContext()
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var args = new object?[] { null };
            var key = (byte[])SecurityMethod("GetFileTransferKeyWithSecurityEvidence").Invoke(sender, args)!;
            var context = args[0]!;
            SecurityMethod("ResetControlSecureState").Invoke(sender, null);
            var metadata = new SessionSecureEnvelopeMetadata(SessionSecureMessageFamily.FileTransfer,
                "file_transfer_data_frame", sender.CurrentSessionSecurityState.SessionId!.Value,
                new PeerAddress(senderClient.ConnectedBulkAddress), 1, "security-transfer");
            var bytes = SessionSecureEnvelopeCodec.Encrypt(key, metadata, new byte[] { 17, 34 });
            var evidence = SecurityMethod("CaptureFileTransferSecurityEvidence").Invoke(null, [context, metadata, bytes])!;
            Assert.Contains("security_context_status=retired", FormatSecurityEvidence(evidence), StringComparison.Ordinal);
            Assert.Equal(SecurityProperty(context, "InstallationId"), SecurityEvidenceValue(FormatSecurityEvidence(evidence), "security_installation_id"));
            Assert.Null(GetPrivateField(sender, "fileTransferSecurityContext"));
            await Task.CompletedTask;
        });

    private static MethodInfo SecurityMethod(string name)
        => typeof(NknSignalingTransport).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;

    private static object? SecurityProperty(object value, string name)
        => value.GetType().GetProperty(name)!.GetValue(value);

    private static string FormatSecurityEvidence(object evidence)
        => (string)SecurityMethod("FormatFileTransferSecurityEvidenceSuffix").Invoke(null, [evidence])!;

    private static string SecurityFingerprint(string kind, string prefix, byte[] bytes)
        => prefix + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"nLink/filetransfer/security-{kind}/v1\0").Concat(bytes).ToArray())).ToLowerInvariant();

    private static string SecurityEvidenceValue(string line, string field)
        => Regex.Match(line, @"(?:^|; )" + Regex.Escape(field) + @"=([^;\r\n]+)").Groups[1].Value;

    private static (byte[] Bytes, object Evidence) EncryptWithSecurityEvidence(NknSignalingTransport sender, byte[] plaintext)
    {
        var args = new object?[] { MsgType.FileTransferDataFrame, "security-transfer", plaintext, true, null, true };
        var bytes = (byte[])SecurityMethod("CreateSecureFileTransferPayloadWithEvidence").Invoke(sender, args)!;
        Assert.NotNull(args[4]);
        return (bytes, args[4]!);
    }

    private static async Task WithSecurityCaptureSession(
        Func<NknSignalingTransport, NknSignalingTransport, FakeNknClient, byte[], CancellationToken, Task> check)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            // Independent cases must not share the process-wide invite throttle key.
            var caseId = Guid.NewGuid().ToString("N");
            var receiverClient = new FakeNknClient($"receiver.security-capture-{caseId}.address");
            var senderClient = new FakeNknClient($"sender.security-capture-{caseId}.address");
            byte[]? approvalBytes = null;
            receiverClient.BeforeSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.Approve)
                {
                    using var document = JsonDocument.Parse(env.Payload);
                    approvalBytes = Convert.FromBase64String(document.RootElement.GetProperty("secureEnvelopeBase64").GetString()!);
                }
                return Task.CompletedTask;
            };
            using var receiver = new NknSignalingTransport(receiverClient, options,
                new NknIdentity("security-capture-receiver", receiverClient.Address));
            using var sender = new NknSignalingTransport(senderClient, options,
                new NknIdentity("security-capture-sender", senderClient.Address));
            await ApproveNknSessionAsync(receiver, sender, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            Assert.NotNull(approvalBytes);
            await check(sender, receiver, senderClient, approvalBytes, cts.Token);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }
}
