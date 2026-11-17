using System.Reflection;
using System.Threading.Channels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task RetainedCheckpointResponse_DuplicateRefreshCannotRetireItsPendingCompletion(bool replacePublication)
    {
        const string transferId = "retained_checkpoint_response_overlap";
        var frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>();
        var firstResponseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseCount = 0;
        var receiveCount = 0;
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId)
        {
            ReceiveWithMetadataForTest = ct =>
            {
                if (Interlocked.Increment(ref receiveCount) == 2) { duplicateProcessed.TrySetResult(); }
                return frames.Reader.ReadAsync(ct);
            },
            BeforeSendAsyncForTest = async (frame, ct) =>
            {
                if (frame is FileTransferReceiverStateFrameV6 { RecoveryMode: "regular_nkn_state_refresh" } &&
                    Interlocked.Increment(ref responseCount) == 1)
                {
                    firstResponseStarted.TrySetResult();
                    await releaseFirstResponse.Task.WaitAsync(ct);
                }
            },
        };
        using var bridge = new RetainedCheckpointTestBridge(session);
        using var stream = new MemoryStream();
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
            beforeActivation: value => PinBootstrapServiceAgreement(value, issuer));
        SetPrivateProperty(context, "WriteStream", stream);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
        var solicit = new FileTransferFallbackBootstrapRequestFrameV6
        {
            SessionId = session.SessionId, TransferId = transferId, TransportEpoch = 1,
            SenderRequestId = $"fbc1:{issuer}:371:{new string('b', 64)}",
        };
        bool ReceiveBootstrap(FileTransferDataFrame frame) => Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryHandleBootstrapCorrelationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [FileTransferDirection.Inbound, session, new FileTransferReceivedDataFrame(frame, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)]));
        Task? loop = null;
        var logStart = GetOperationalLogLength();
        try
        {
            Assert.True(ReceiveBootstrap(solicit));
            await WaitUntilAsync(() => bridge.Executions.Count == 1, timeoutMs: 3000);
            var phase = Assert.Single(bridge.Executions);
            var response = Assert.IsType<FileTransferFallbackBootstrapStateFrameV6>(phase.Command.Frame);
            phase.Delivery.TrySetResult(ServiceBootstrapReceipt(phase.Command, settled: false));
            Assert.True(ReceiveBootstrap(solicit with { ReceiverChallengeId = response.ReceiverChallengeId }));
            phase.Settlement.TrySetResult(ServiceBootstrapReceipt(phase.Command, settled: true));
            await firstResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(FallbackRepairFeedbackOperationsForTest(context));

            var pending = CreateCoordinatorStateForTest(context);
            var duplicate = new FileTransferFrontierRequestFrameV6
            {
                SessionId = session.SessionId, TransferId = transferId,
                TransportEpoch = pending.CurrentLeg!.TransportEpochId,
                RepairRequestId = pending.CurrentFallbackCheckpointLease!.Value.CheckpointRequestId,
                Priority = "state_refresh", RecoveryMode = "regular_nkn_state_refresh", MissingRanges = [],
            };
            Assert.NotNull(CaptureServiceBootstrapTerminal(pending).RetainedFrameApplyCommand);
            Assert.Null(CaptureServiceBootstrapTerminal(pending).RetainedFrameApplyReceipt);
            var dispatch = typeof(SessionFileTransferService).GetMethod(
                "GetInboundFileTransferRouteTransitionRetainedFrameReceiveDispatchDecision", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var unrelated in new[]
            {
                duplicate with { SessionId = "other_session" }, duplicate with { TransferId = "other_transfer" },
                duplicate with { RepairRequestId = duplicate.RepairRequestId + ":other" },
                duplicate with { TransportEpoch = duplicate.TransportEpoch + 1 },
                duplicate with { RecoveryMode = "frontier_stall", RepairRequestId = "other_frontier_request" },
            })
            {
                Assert.Equal("None", dispatch.Invoke(service, [context, unrelated])!.ToString());
            }
            var runtimeKind = Enum.Parse(typeof(SessionFileTransferService).GetNestedType(
                "FileTransferSparseCreditRuntimeKind", BindingFlags.NonPublic)!, "PrimaryRegularNknV4");
            loop = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "RunInboundSparseCreditReceiveLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [context, new FileTransferSessionOpenV2 { SessionId = session.SessionId, TransferId = transferId,
                    ProtocolVersion = 4, FileTransferRoute = "file_tuna_v4", ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 1 }, runtimeKind]));
            await frames.Writer.WriteAsync(new(duplicate, FileTransferTransportKind.RegularNkn, "bulk", DateTimeOffset.UtcNow));
            await duplicateProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var publication = pending.CurrentRoutePublicationToken!.Value;
            if (replacePublication)
            {
                SetPrivateProperty(context, "CurrentRoutePublicationToken", publication with
                {
                    PublicationGeneration = publication.PublicationGeneration + 1,
                });
                Assert.Equal("None", dispatch.Invoke(service, [context, duplicate])!.ToString());
            }
            var beforeCompletion = CreateCoordinatorStateForTest(context);
            releaseFirstResponse.TrySetResult();
            await WaitUntilAsync(() => CaptureServiceBootstrapTerminal(CreateCoordinatorStateForTest(context)).DispositionReceipt is not null ||
                ReadOperationalLogTail(logStart).Contains("event=filetransfer_route_transition_retained_frame_rejected;", StringComparison.Ordinal), timeoutMs: 5000);
            var terminal = CaptureServiceBootstrapTerminal(CreateCoordinatorStateForTest(context));
            if (replacePublication)
            {
                Assert.Null(terminal.RetainedFrameApplyReceipt);
                Assert.Null(terminal.DispositionReceipt);
                var afterCompletion = CreateCoordinatorStateForTest(context);
                Assert.Equal(beforeCompletion.CurrentFallbackCheckpointLease, afterCompletion.CurrentFallbackCheckpointLease);
                Assert.Equal(beforeCompletion.CurrentRuntimeOwner, afterCompletion.CurrentRuntimeOwner);
                Assert.Equal(beforeCompletion.CurrentRoutePublicationToken, afterCompletion.CurrentRoutePublicationToken);
                Assert.Contains("decision_reason=filetransfer_route_transition_retained_frame_response_failed",
                    ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            }
            else
            {
                Assert.True(terminal.RetainedFrameApplyReceipt?.Status == FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                    string.Join(Environment.NewLine, ReadOperationalLogTail(logStart).Split('\n')
                        .Where(line => !line.Contains("snapshot_payload=", StringComparison.Ordinal))));
                Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, terminal.DispositionReceipt!.Value.Status);
                Assert.Null(CreateCoordinatorStateForTest(context).CurrentFallbackCheckpointLease);
                Assert.Equal("None", dispatch.Invoke(service, [context, duplicate])!.ToString());
                Assert.Contains("decision_reason=retained_frame_duplicate_coalesced", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            }
            Assert.Equal(1, responseCount);
        }
        finally
        {
            releaseFirstResponse.TrySetResult();
            GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Cancel();
            if (loop is not null) { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private sealed class RetainedCheckpointTestBridge(InitialBootstrapRecordingDataSession session)
        : InitialBootstrapCheckpointTransport(session), IFileTransferRouteTransitionBridge,
            IFileTransferControlPlanePhysicalSendSubmissionTransport
    {
        public List<BootstrapServiceExecution> Executions { get; } = [];
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => true;
        FileTransferBootstrapPhaseExecution IFileTransferRouteTransitionBridge.ExecuteFallbackBootstrapPhase(
            FileTransferBootstrapPhaseCommand command, IFileTransferDataSession dataSession, Func<bool> current, CancellationToken ct)
        {
            Assert.True(current());
            var execution = new BootstrapServiceExecution(command, new(TaskCreationOptions.RunContinuationsAsynchronously),
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            Executions.Add(execution);
            return new(execution.Delivery.Task, execution.Settlement.Task);
        }

        public FileTransferControlPlanePhysicalSendSubmission SubmitFileTransferControlPlanePhysicalSend(
            FileTransferControlPlaneDeliveryRequest request, CancellationToken ct)
        {
            var sending = SendFileTransferControlPlaneFrameAsync(request, ct);
            return new(sending, sending);
        }
    }
}
