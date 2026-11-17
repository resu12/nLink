using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("disabled", false)]
    [InlineData("missing", false)]
    [InlineData("clone", false)]
    [InlineData("pre-pump", false)]
    [InlineData("wrong-publication", false)]
    [InlineData("inbound", false)]
    [InlineData("v6", false)]
    [InlineData("wrong-target", false)]
    [InlineData("no-owner", false)]
    [InlineData("wrong-wire-scope", false)]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public async Task NknTransport_ContentCaptureRequiresExactProducerBindings(string change, bool expected)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var owner = ContentOwner(sender, session);
            var source = FileTransferPublicationUseCaptureTests.CreateCycle(session, "security-transfer", FileTransferDirection.Outbound)[0];
            var authority = FileTransferPublicationUseCaptureTests.CreateAuthority(source.Token);
            authority = change switch
            {
                "inbound" => authority with { Direction = FileTransferDirection.Inbound },
                "v6" => authority with { ProtocolVersion = 6 },
                "wrong-target" => authority with { TargetProtocolVersion = 6 },
                _ => authority,
            };
            if (change != "missing")
            {
                FileTransferPublicationUseEvidence.CaptureSend(authority,
                    change == "wrong-publication" ? source.Evidence with { SessionId = "wrong" } : source.Evidence,
                    change == "pre-pump" ? FileTransferPublicationUseKind.V4PrePumpRequest : FileTransferPublicationUseKind.V4PhysicalSubmission);
            }
            if (change == "clone") { authority = authority with { }; }
            PublishPublicationUse(sender, source); // A cache entry cannot supply the missing association.
            using var trace = FileTransferDiagnosticLogPolicy.OverrideTraceLoggingForTests(change != "disabled");
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            var frame = PublicationUseFrame(session);
            var wire = change == "wrong-wire-scope" ? frame with { SessionId = "wrong" } : frame;
            var start = GetOperationalLogLength();
            await ContentRawSend(sender, change == "no-owner" ? null : owner, authority, wire, frame, ct);
            var row = ContentSendRow(start);
            Assert.Equal(expected, row.Contains("content_evidence_profile=", StringComparison.Ordinal));
            Assert.Equal(expected, GetPrivateField(sender, "fileTransferContentEvidenceSlot") is not null);
            if (expected)
            {
                Assert.Equal("captured", SecurityEvidenceValue(row, "content_evidence_state"));
                Assert.Equal("1", SecurityEvidenceValue(row, "content_segment_count"));
                Assert.Matches("^ftcc1-[a-f0-9]{64}$", SecurityEvidenceValue(row, "content_segment_0_commitment"));
            }
        });

    [Theory]
    [InlineData("reset")]
    [InlineData("same-key")]
    [InlineData("remove")]
    [InlineData("dispose")]
    [InlineData("transport-dispose")]
    [InlineData("terminal")]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public async Task NknTransport_ContentCaptureRetiresAndZerosOriginalScopeAcrossAwait(string change)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var owner = ContentOwner(sender, session);
            var authority = ContentAuthority(session);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.BeforeSendAsync = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            var start = GetOperationalLogLength();
            var send = ContentRawSend(sender, owner, authority, PublicationUseFrame(session), null, ct);
            await entered.Task.WaitAsync(ct);
            var slot = GetPrivateField(sender, "fileTransferContentEvidenceSlot")!;
            var scope = (FileTransferContentEvidenceScope)SecurityProperty(slot, "Scope")!;
            var secret = (byte[])scope.GetType().GetField("secret", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
            Assert.Contains(secret, value => value != 0);
            try
            {
                if (change == "reset") { SecurityMethod("ResetControlSecureState").Invoke(sender, null); }
                if (change == "same-key") { ReinstallContentSecurity(sender, session, approval); }
                if (change == "remove") { SecurityMethod("RemoveFileTransferDataSession").Invoke(sender, [owner, false, "test"]); }
                if (change == "dispose") { owner.Dispose(); }
                if (change == "transport-dispose") { sender.Dispose(); }
                if (change == "terminal") { AcceptContentTerminal(sender); }
                Assert.Null(GetPrivateField(sender, "fileTransferContentEvidenceSlot"));
                Assert.All(secret, value => Assert.Equal(0, value));
            }
            finally { release.TrySetResult(); }
            if (change == "transport-dispose")
            {
                try { await send.WaitAsync(ct); } catch (ObjectDisposedException) { }
            }
            else { await send.WaitAsync(ct); }
            var rows = ReadOperationalLogTail(start).Split('\n').Where(line => line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal)).ToArray();
            Assert.All(rows, row =>
            {
                Assert.Equal("retired", SecurityEvidenceValue(row, "content_evidence_state"));
                Assert.Equal("0", SecurityEvidenceValue(row, "content_segment_count"));
                Assert.DoesNotContain("content_segment_0_commitment=", row, StringComparison.Ordinal);
            });
            if (change != "transport-dispose") { Assert.Single(rows); }
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public async Task NknTransport_ContentCaptureOldOwnerCannotRetireOrReplaceNewOwner()
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var oldOwner = ContentOwner(sender, session);
            using var replacement = ContentOwner(sender, session);
            var authority = ContentAuthority(session);
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            await ContentRawSend(sender, oldOwner, authority, PublicationUseFrame(session), null, ct);
            var oldSlot = GetPrivateField(sender, "fileTransferContentEvidenceSlot");
            var completionOwner = SecurityMethod("CaptureTerminalContentOwner").Invoke(sender, ["security-transfer"]);
            await ContentRawSend(sender, replacement, authority, PublicationUseFrame(session), null, ct);
            Assert.Same(oldSlot, GetPrivateField(sender, "fileTransferContentEvidenceSlot")); // Occupied owner is not replaced.
            oldOwner.Dispose();
            await ContentRawSend(sender, replacement, authority, PublicationUseFrame(session), null, ct);
            var newSlot = GetPrivateField(sender, "fileTransferContentEvidenceSlot");
            Assert.NotNull(newSlot); Assert.NotSame(oldSlot, newSlot);
            SecurityMethod("RemoveFileTransferDataSession").Invoke(sender, [oldOwner, false, "late"]);
            AcceptContentTerminal(sender, completionOwner);
            Assert.Same(newSlot, GetPrivateField(sender, "fileTransferContentEvidenceSlot"));
            // A delayed old sender also cannot create or borrow this scope.
            var start = GetOperationalLogLength();
            await ContentRawSend(sender, oldOwner, authority, PublicationUseFrame(session), null, ct);
            Assert.DoesNotContain("content_evidence_profile=", ContentSendRow(start), StringComparison.Ordinal);
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public async Task NknTransport_ContentCaptureNewInstallationUsesFreshScopeAndRejectsOldContext()
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var owner = ContentOwner(sender, session);
            var authority = ContentAuthority(session);
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            var wire = FileTransferDataFrameCodec.SerializeLegacyV4(PublicationUseFrame(session));
            var oldEvidence = EncryptWithSecurityEvidence(sender, wire).Evidence;
            var firstStart = GetOperationalLogLength();
            await ContentRawSend(sender, owner, authority, PublicationUseFrame(session), null, ct);
            var firstRow = ContentSendRow(firstStart);
            var oldSlot = GetPrivateField(sender, "fileTransferContentEvidenceSlot");
            ReinstallContentSecurity(sender, session, approval);
            var capture = SecurityMethod("CaptureFileTransferContentEvidence").Invoke(sender,
                [wire, owner, authority, FileTransferPublicationUseEvidence.GetSend(authority), oldEvidence]);
            Assert.Null(capture); Assert.Null(GetPrivateField(sender, "fileTransferContentEvidenceSlot"));
            var secondStart = GetOperationalLogLength();
            await ContentRawSend(sender, owner, authority, PublicationUseFrame(session), null, ct);
            var secondRow = ContentSendRow(secondStart);
            Assert.NotNull(GetPrivateField(sender, "fileTransferContentEvidenceSlot"));
            Assert.NotSame(oldSlot, GetPrivateField(sender, "fileTransferContentEvidenceSlot"));
            Assert.NotEqual(SecurityEvidenceValue(firstRow, "content_scope_id"), SecurityEvidenceValue(secondRow, "content_scope_id"));
            Assert.NotEqual(SecurityEvidenceValue(firstRow, "content_segment_0_commitment"), SecurityEvidenceValue(secondRow, "content_segment_0_commitment"));
        });

    [Theory]
    [InlineData("estimate")]
    [InlineData("control")]
    [InlineData("v6")]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public async Task NknTransport_ContentCaptureExcludesEstimatesAndNonV4Payloads(string change)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var owner = ContentOwner(sender, session);
            var authority = ContentAuthority(session);
            var bytes = change switch
            {
                "control" => FileTransferDataFrameCodec.SerializeLegacyV4(new FileTransferCancelFrameV4
                    { SessionId = session, TransferId = "security-transfer" }),
                "v6" => FileTransferDataFrameCodec.Serialize(new FileTransferChunkBatchFrameV6
                    { SessionId = session, TransferId = "security-transfer", StartChunkIndex = 0, ChunkCount = 1,
                        DataSegments = [new byte[] { 17, 34 }] }),
                _ => FileTransferDataFrameCodec.SerializeLegacyV4(PublicationUseFrame(session)),
            };
            var start = GetOperationalLogLength();
            if (change == "estimate")
            {
                SecurityMethod("CreateSecureFileTransferPayloadForBudgetEstimate").Invoke(sender,
                    [MsgType.FileTransferDataFrame, "security-transfer", bytes, true]);
            }
            else
            {
                var evidence = EncryptWithSecurityEvidence(sender, bytes).Evidence;
                Assert.Null(SecurityMethod("CaptureFileTransferContentEvidence").Invoke(sender,
                    [bytes, owner, authority, FileTransferPublicationUseEvidence.GetSend(authority), evidence]));
            }
            Assert.Null(GetPrivateField(sender, "fileTransferContentEvidenceSlot"));
            Assert.DoesNotContain("content_evidence_profile=", ReadOperationalLogTail(start), StringComparison.Ordinal);
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData("mutate-bytes")]
    [InlineData("conflicting-row")]
    [InlineData("failed")]
    [InlineData("canceled")]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public async Task NknTransport_ContentCaptureIsWireDerivedAndDoesNotRecordFailedSends(string change)
        => await WithSecurityCaptureSession(async (sender, receiver, client, approval, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            using var owner = ContentOwner(sender, session);
            var authority = ContentAuthority(session);
            var frame = PublicationUseFrame(session);
            var start = GetOperationalLogLength();
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            client.BeforeSendAsync = (_, _, _) =>
            {
                if (change == "failed") { throw new IOException("test send failure"); }
                if (change == "canceled") { throw new OperationCanceledException(ct); }
                if (change == "mutate-bytes") { frame.DataSegments[0][0] ^= 1; }
                return Task.CompletedTask;
            };
            var send = ContentRawSend(sender, owner, authority, frame,
                change == "conflicting-row" ? frame with { StartChunkIndex = 99 } : frame, ct);
            if (change is "failed" or "canceled")
            {
                await Assert.ThrowsAnyAsync<Exception>(() => send);
                Assert.DoesNotContain("content_evidence_profile=", ReadOperationalLogTail(start), StringComparison.Ordinal);
                return;
            }
            await send;
            var row = ContentSendRow(start);
            Assert.Equal(change == "mutate-bytes", row.Contains("content_evidence_profile=", StringComparison.Ordinal));
            if (change == "mutate-bytes")
            {
                var slot = GetPrivateField(sender, "fileTransferContentEvidenceSlot")!;
                var scope = (FileTransferContentEvidenceScope)SecurityProperty(slot, "Scope")!;
                frame.DataSegments[0][0] ^= 1;
                Assert.True(scope.TryCapture(FileTransferDataFrameCodec.SerializeLegacyV4(frame), out var original));
                Assert.Equal(original.Segments[0].Commitment, SecurityEvidenceValue(row, "content_segment_0_commitment"));
            }
        });

    private static IFileTransferDataSession ContentOwner(NknSignalingTransport sender, string session)
        => (IFileTransferDataSession)Activator.CreateInstance(typeof(NknSignalingTransport)
            .GetNestedType("TransportFileTransferDataSession", BindingFlags.NonPublic)!,
            [sender, session, "security-transfer", 1L])!;

    private static FileTransferDataPathSendAuthority ContentAuthority(string session)
    {
        var source = FileTransferPublicationUseCaptureTests.CreateCycle(session, "security-transfer", FileTransferDirection.Outbound)[0];
        var authority = FileTransferPublicationUseCaptureTests.CreateAuthority(source.Token);
        FileTransferPublicationUseEvidence.CaptureSend(authority, source.Evidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
        return authority;
    }

    private static Task ContentRawSend(NknSignalingTransport sender, IFileTransferDataSession? owner,
        FileTransferDataPathSendAuthority authority, FileTransferChunkBatchFrameV4 wire,
        FileTransferChunkBatchFrameV4? rowFrame, CancellationToken ct)
    {
        var method = SecurityMethod("SendFileTransferEnvelopeRawAsync");
        var parameters = method.GetParameters();
        var args = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        args[0] = MsgType.FileTransferDataFrame; args[1] = "security-transfer";
        args[2] = FileTransferDataFrameCodec.SerializeLegacyV4(wire); args[3] = true;
        args[4] = wire.Type; args[5] = ct; args[6] = wire.DataSegments.Sum(segment => segment.Length);
        args[7] = wire.DataSegments.Count; args[8] = wire.BatchProfile; args[9] = true;
        args[Array.FindIndex(parameters, p => p.Name == "dataPathSendAuthority")] = authority;
        args[Array.FindIndex(parameters, p => p.Name == "dataFrame")] = rowFrame ?? wire;
        args[Array.FindIndex(parameters, p => p.Name == "originatingDataSession")] = owner;
        return (Task)method.Invoke(sender, args)!;
    }

    private static string ContentSendRow(int start)
        => Assert.Single(ReadOperationalLogTail(start).Split('\n'),
            line => line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal));

    private static void ReinstallContentSecurity(NknSignalingTransport sender, string session, byte[] approval)
        => SecurityMethod("InstallControlSessionSharedKey").Invoke(sender,
            [SecurityMethod("GetControlSessionSharedKeyOrThrow").Invoke(sender, null), session, approval]);

    private static void AcceptContentTerminal(NknSignalingTransport sender, object? capturedOwner = null)
    {
        var stateType = typeof(NknSignalingTransport).GetNestedType("FileTransferTransportState", BindingFlags.NonPublic)!;
        var phaseType = typeof(NknSignalingTransport).GetNestedType("FileTransferTransportPhase", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, [true, Enum.Parse(phaseType, "Transferring"), false]);
        ((IDictionary)GetPrivateField(sender, "fileTransferStates")!)["security-transfer"] = state;
        var args = new object?[] { MsgType.FileTransferCancel, "security-transfer", true, true, null, capturedOwner };
        Assert.True((bool)SecurityMethod("TryValidateAndTrackFileTransferMessage").Invoke(sender, args)!);
    }
}
