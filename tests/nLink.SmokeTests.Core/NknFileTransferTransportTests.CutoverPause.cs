using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData("ingress")]
    [InlineData("queued")]
    [InlineData("control")]
    [InlineData("reset")]
    [InlineData("terminal")]
    [InlineData("rollback")]
    [InlineData("disposed_session")]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_CutoverPauseHoldsPayloadUntilCommittedPublicationIsForwarded(string mode)
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
                FileName = "cutover.bin", FileSizeBytes = 2, PreferredDataProtocolVersion = 4 }, ct);
            await offered.Task.WaitAsync(ct);
            await receiver.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = session,
                TransferId = transfer, AcceptedDataProtocolVersion = 4 }, ct);
            await accepted.Task.WaitAsync(ct);
            await sender.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = session,
                TransferId = transfer, ProtocolVersion = 4, SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, ct);
            await opened.Task.WaitAsync(ct);
            var inbound = await receiver.OpenFileTransferDataSessionAsync(session, transfer, ct);
            // The existing Coordinator fixture authors data-session generation
            // 17. Reach that generation through real open/dispose operations.
            while (Assert.IsType<long>(SecurityProperty(inbound, "Generation")) < 17)
            {
                inbound.Dispose();
                inbound = await receiver.OpenFileTransferDataSessionAsync(session, transfer, ct);
            }
            using var inboundLifetime = inbound;
            var history = new List<FileTransferCoordinatorTransitionEnvelope>();
            FileTransferCoordinatorState? committed = null;
            var tokens = FileTransferCoordinatorTests.CreateDataPathIngressRouteCycle(session, transfer,
                FileTransferDirection.Inbound, history.Add, initializeDataSession: true,
                captureFallbackState: state => committed = state, stopBeforeFallbackYield: true);
            var operation = committed!.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
            var disposition = operation.DispositionCommand!.Value;
            var bridge = (IFileTransferRouteTransitionBridge)receiver;
            FileTransferDataFrame frame = mode == "control"
                ? new FileTransferStateFrameV4 { SessionId = session, TransferId = transfer,
                    TransferPaused = true, TransferPauseReason = "ui_pause" }
                : PublicationUseFrame(session);
            var encrypted = EncryptWithSecurityEvidence(sender, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
            var envelope = new Envelope(1, "cutover-route", "cutover-route", MsgType.FileTransferDataFrame, encrypted.Bytes, 0, null);
            var args = new object?[] { senderClient.ConnectedBulkAddress, NknBridgeChannel.Bulk, envelope, null };
            Publish(0);
            if (mode == "queued")
                Assert.True((bool)SecurityMethod("TryPrepareFileTransferDataFrameDispatch").Invoke(receiver, args)!);
            Publish(1);
            var binding = bridge.BindFileTransferRouteTransitionPause(new(operation.Authority));
            Assert.True(binding.Status == FileTransferRouteTransitionPauseBindStatus.Installed, binding.Reason);
            var queueCount = inbound.GetType().GetField("queuedFrameCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferStates"));
            var before = states[transfer];
            var start = $"event=cutover_pause_started; marker={Guid.NewGuid():D}";
            var end = $"event=cutover_pause_ended; marker={Guid.NewGuid():D}";
            LocalOperationalLog.Info("CutoverPauseTest", start);
            if (mode != "queued")
                Assert.True((bool)SecurityMethod("TryPrepareFileTransferDataFrameDispatch").Invoke(receiver, args)!);
            Assert.IsType<Action>(SecurityProperty(args[3]!, "Dispatch"))();
            if (mode == "control")
            {
                Assert.True(Assert.IsType<FileTransferStateFrameV4>(await inbound.ReceiveAsync(ct)).TransferPaused);
            }
            else
            {
                Assert.Equal(0, queueCount.GetValue(inbound));
                Assert.Equal(before, states[transfer]);
                Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Rejected,
                    bridge.ApplyFileTransferRouteTransitionDisposition(disposition with { RoutePublicationToken = null }).Status);
                Assert.Equal(0, queueCount.GetValue(inbound));
                var shadow = GetPrivateField(receiver, "fileTransferRouteTransitionExecutionShadow")!;
                var completion = Assert.IsType<TaskCompletionSource<bool>>(SecurityProperty(
                    SecurityProperty(shadow, "Current")!, "PayloadCutoverCompletion"));
                Assert.False(completion.Task.IsCompleted);
            }

            // The Coordinator has already committed, but its actual transport
            // forwarder is held. Neither ingress nor queued payload may use the
            // superseded publication while this forwarder is outstanding.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwarding = Task.Run(async () => { await release.Task; if (mode != "rollback") Publish(2); }, ct);
            if (mode == "reset") SecurityMethod("ResetInboundFileTransferDispatchQueue").Invoke(receiver, null);
            if (mode == "disposed_session") inbound.Dispose();
            release.SetResult();
            await forwarding;
            var final = mode is "terminal" or "rollback" ? disposition with
            {
                Kind = mode == "terminal" ? FileTransferRouteTransitionDispositionKind.TerminalInvalidated
                    : FileTransferRouteTransitionDispositionKind.RolledBack,
                RoutePublicationToken = null,
                RouteCommitToken = null,
            } : disposition;
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
                bridge.ApplyFileTransferRouteTransitionDisposition(final).Status);
            if (mode is "ingress" or "queued")
                Assert.Equal(((FileTransferChunkBatchFrameV4)frame).DataSegments[0],
                    Assert.IsType<FileTransferChunkBatchFrameV4>(await inbound.ReceiveAsync(ct)).DataSegments[0]);
            else if (mode is "reset" or "terminal" or "rollback" or "disposed_session")
            {
                await Task.Delay(50, ct);
                Assert.Equal(0, queueCount.GetValue(inbound));
                Assert.Equal(before, states[transfer]);
            }
            LocalOperationalLog.Info("CutoverPauseTest", end);
            Assert.True(LocalOperationalLog.TryReadRetainedSlice(start, end, out var log));
            var rows = DataPathProvenanceRows(log, DataPathEnvelopeFingerprint(envelope.MessageId));
            if (mode != "rollback")
                Assert.DoesNotContain(rows, row => row.Contains("rejected_before_dispatch", StringComparison.Ordinal));
            if (mode is "ingress" or "queued")
            {
                Assert.NotEmpty(rows);
                Assert.All(rows, row => AssertDataPathPublication(row, tokens[2]));
            }
            else if (mode == "rollback")
            {
                Assert.Equal(3, rows.Length);
                Assert.All(rows, row =>
                {
                    AssertDataPathPublication(row, tokens[1]);
                    Assert.Contains("receive_disposition=rejected_before_dispatch", row, StringComparison.Ordinal);
                    Assert.Contains("authority_effect=none", row, StringComparison.Ordinal);
                });
            }
            else if (mode is "reset" or "terminal" or "disposed_session") Assert.Empty(rows);

            void Publish(int index)
            {
                var token = tokens[index];
                var fingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(token);
                var raw = history.First(item => item.Aggregate.Operations.Any(item =>
                    item.OperationRole == "route_publication" && item.ReceiptFingerprint == fingerprint));
                var projected = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(raw);
                var publication = Assert.Single(projected.Aggregate.Operations, item =>
                    item.OperationRole == "route_publication" && item.RouteRuntime!.Boundary.LiveRouteEpoch == token.LiveRouteEpoch);
                PublishPublicationUse(receiver, (token, new(projected.CoordinatorInstanceId, projected.SessionId,
                    projected.TransferId, projected.TransitionId, projected.Aggregate.AfterStateGeneration,
                    projected.Aggregate.AfterStateGeneration, publication.OperationKey, publication.ReceiptFingerprint)));
            }
        });
}
