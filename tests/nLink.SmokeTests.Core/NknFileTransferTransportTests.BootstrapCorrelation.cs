using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CorrelatedBootstrap_CoordinatorSelectionSuppressesLegacyReceiveSideEffects(int kind)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.legacy");
            var helperClient = new FakeNknClient("helper.bootstrap.legacy");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-bootstrap-legacy", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bootstrap-legacy", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await OpenBootstrapTransportTransferAsync(host, helper, sessionId, cts.Token);
            var recipient = kind == 1 ? helper : host;
            var sender = kind == 1 ? host : helper;
            var senderClient = kind == 1 ? hostClient : helperClient;
            var bridge = (IFileTransferRouteTransitionBridge)recipient;
            bridge.ObserveFallbackBootstrapAgreement(FileTransferCoordinatorTests.CreateCorrelatedBootstrapAgreementFixture(sessionId,
                "bootstrap-transfer", kind == 1 ? FileTransferDirection.Outbound : FileTransferDirection.Inbound));
            var session = await recipient.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            var states = Assert.IsAssignableFrom<System.Collections.IDictionary>(GetPrivateField(recipient, "fileTransferStates"));
            var priorState = states["bootstrap-transfer"];
            FileTransferDataFrame frame = kind switch
            {
                0 => new FileTransferFrontierRequestFrameV6
                {
                    SessionId = sessionId, TransferId = "bootstrap-transfer", TransportEpoch = 1,
                    Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority, RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
                    RequestCorrelationId = FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(1),
                },
                1 => new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId, TransferId = "bootstrap-transfer", TransportEpoch = 1, Epoch = 1,
                    DurableReceivedHighestChunkIndex = -1, TransferPaused = true,
                    TransferPauseReason = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
                    Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority, RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
                },
                _ => new FileTransferTransportEpochFrameV6
                {
                    SessionId = sessionId, TransferId = "bootstrap-transfer", TransportEpoch = 1,
                    RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
                },
            };
            var sequence = GetNextFileTransferSecureSequence(sender);
            InjectSecureFileTransferDataFrame(recipient, sender, senderClient, frame, NknBridgeChannel.Control, sequence++);
            Assert.Equal(frame.Type, (await session.ReceiveAsync(cts.Token)).Type);
            Assert.Equal(priorState, states["bootstrap-transfer"]);
            InvokePrivateMethod(recipient, "RemoveFileTransferDataSession", session, true, "test_resume_required");
            var logStart = GetOperationalLogLength();
            InjectSecureFileTransferDataFrame(recipient, sender, senderClient, frame, NknBridgeChannel.Bulk, sequence++);
            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("reason=bootstrap_current_data_session_required", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
            Assert.Empty(Assert.IsAssignableFrom<System.Collections.IDictionary>(GetPrivateField(recipient, "fileTransferDataSessions")));
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CorrelatedBootstrap_SerializedReceiveKeepsBothCopiesAsEvidenceOnly(int kind)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.receive");
            var helperClient = new FakeNknClient("helper.bootstrap.receive");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-bootstrap-receive", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bootstrap-receive", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await OpenBootstrapTransportTransferAsync(host, helper, sessionId, cts.Token);
            var recipient = kind == 1 ? helper : host;
            var sender = kind == 1 ? host : helper;
            var senderClient = kind == 1 ? hostClient : helperClient;
            var session = await recipient.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            var states = Assert.IsAssignableFrom<System.Collections.IDictionary>(GetPrivateField(recipient, "fileTransferStates"));
            var priorState = states["bootstrap-transfer"];
            var frame = BootstrapPhysicalFrame(kind, sessionId);
            var evaluationCount = 0;
            ((IFileTransferRouteTransitionBridge)recipient).FallbackV6ProofBootstrapEvaluationRequested += (_, _) => evaluationCount++;
            var sequence = GetNextFileTransferSecureSequence(sender);
            foreach (var lane in new[] { NknBridgeChannel.Control, NknBridgeChannel.Bulk })
            {
                InjectSecureFileTransferDataFrame(recipient, sender, senderClient, frame, lane, sequence++);
                var received = await session.ReceiveWithMetadataAsync(cts.Token);
                Assert.Equal(frame.GetType(), received.Frame.GetType());
                Assert.Equal(FileTransferDataFrameCodec.Serialize(frame), FileTransferDataFrameCodec.Serialize(received.Frame));
                Assert.Equal(lane == NknBridgeChannel.Control ? "control" : "bulk", received.Lane);
                Assert.Equal(FileTransferTransportKind.RegularNkn, received.TransportKind);
                Assert.Equal(priorState, states["bootstrap-transfer"]);
                Assert.Equal(0, evaluationCount);
            }
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CorrelatedBootstrap_SerializedReceiveCannotReopenSuppressedSession(int kind)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.suppressed");
            var helperClient = new FakeNknClient("helper.bootstrap.suppressed");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-bootstrap-suppressed", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bootstrap-suppressed", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await OpenBootstrapTransportTransferAsync(host, helper, sessionId, cts.Token);
            var recipient = kind == 1 ? helper : host;
            var sender = kind == 1 ? host : helper;
            var senderClient = kind == 1 ? hostClient : helperClient;
            var session = await recipient.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            InvokePrivateMethod(recipient, "RemoveFileTransferDataSession", session, true, "test_resume_required");
            var start = GetOperationalLogLength();
            InjectSecureFileTransferDataFrame(recipient, sender, senderClient, BootstrapPhysicalFrame(kind, sessionId),
                NknBridgeChannel.Control, GetNextFileTransferSecureSequence(sender));
            await WaitUntilAsync(() => ReadOperationalLogTail(start).Contains("reason=bootstrap_current_data_session_required", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
            Assert.Empty(Assert.IsAssignableFrom<System.Collections.IDictionary>(GetPrivateField(recipient, "fileTransferDataSessions")));
            Assert.Contains("bootstrap-transfer", Assert.IsAssignableFrom<ISet<string>>(GetPrivateField(recipient, "fileTransferDataSessionRemoteOpenSuppressed")));
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    private static async Task OpenBootstrapTransportTransferAsync(NknSignalingTransport receiver, NknSignalingTransport sender,
        string sessionId, CancellationToken ct)
    {
        var offer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileTransferOfferReceived += (_, _) => offer.TrySetResult();
        sender.FileTransferAcceptReceived += (_, _) => accept.TrySetResult();
        receiver.FileTransferSessionOpenReceived += (_, _) => open.TrySetResult();
        await sender.SendFileTransferOfferAsync(new FileTransferOfferV2
        {
            SessionId = sessionId, TransferId = "bootstrap-transfer", FileName = "bootstrap.bin", FileSizeBytes = 2048,
            PreferredDataProtocolVersion = 4, FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = new string('a', 64),
        }, ct);
        await offer.Task.WaitAsync(ct);
        await receiver.SendFileTransferAcceptAsync(new FileTransferAcceptV1
        {
            SessionId = sessionId, TransferId = "bootstrap-transfer", AcceptedDataProtocolVersion = 4,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = new string('c', 64),
        }, ct);
        await accept.Task.WaitAsync(ct);
        await sender.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
        {
            SessionId = sessionId, TransferId = "bootstrap-transfer", ProtocolVersion = 4,
            SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = new string('a', 64),
        }, ct);
        await open.Task.WaitAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrelatedBootstrap_ExecutorJoinsAndCaches_AndReleasesOnlyAfterSettlement(bool failSecondary)
    {
        FakeNknClient.ResetNetwork();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.executor");
            var helperClient = new FakeNknClient("helper.bootstrap.executor");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-bootstrap-executor", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bootstrap-executor", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            helperClient.BeforeSendCoreAsync = async (_, _, lane, _) =>
            {
                Interlocked.Increment(ref physicalCount);
                if (lane != NknBridgeChannel.Bulk) { return; }
                await release.Task;
                if (failSecondary) { throw new IOException("modeled_secondary_failure"); }
            };
            var fixture = FileTransferCoordinatorTests.CreateCorrelatedBootstrapTransportFixture(sessionId, "bootstrap-transfer");
            var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(helper);
            bool Current() => FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(fixture.State, fixture.Command);
            var execution = bridge.ExecuteFallbackBootstrapPhase(fixture.Command, dataSession, Current, cts.Token);
            Assert.Same(execution, bridge.ExecuteFallbackBootstrapPhase(fixture.Command, dataSession, Current, cts.Token));
            var delivered = await execution.Delivery.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferBootstrapPhaseReceiptStatus.Delivered, delivered.Status);
            Assert.Equal(4, delivered.Copies.Length);
            Assert.Equal(2, delivered.Copies.Count(copy => copy.Outcome == FileTransferBootstrapCopyOutcome.Pending));
            Assert.False(execution.Settlement.IsCompleted);
            var conflict = bridge.ExecuteFallbackBootstrapPhase(fixture.Command with { Identity = "different-command" }, dataSession, Current, cts.Token);
            Assert.Equal(FileTransferBootstrapPhaseReceiptStatus.Failed, (await conflict.Delivery).Status);
            Assert.Equal(4, physicalCount);
            release.TrySetResult();
            var settled = await execution.Settlement.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(settled.Copies, copy => Assert.NotEqual(FileTransferBootstrapCopyOutcome.Pending, copy.Outcome));
            Assert.Equal(FileTransferBootstrapPhaseReceiptStatus.Settled, settled.Status);
            Assert.Same(execution, bridge.ExecuteFallbackBootstrapPhase(fixture.Command, dataSession, Current, cts.Token));
            Assert.Equal(4, physicalCount);
            fixture.State = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(fixture.State, delivered).State;
            fixture.State = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(fixture.State, settled).State;
            Assert.True(fixture.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.IsReady);
        }
        finally
        {
            release.TrySetResult();
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    public async Task CorrelatedBootstrap_ExecutorEpochOnlyThenInvalidationNeverSubmitsProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.executor.stale");
            var helperClient = new FakeNknClient("helper.bootstrap.executor.stale");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-stale", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-stale", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            var checks = 0;
            var fixture = FileTransferCoordinatorTests.CreateCorrelatedBootstrapTransportFixture(sessionId, "bootstrap-transfer");
            var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(helper);
            var execution = bridge.ExecuteFallbackBootstrapPhase(fixture.Command, dataSession,
                () => Interlocked.Increment(ref checks) == 1, cts.Token);
            var failed = await execution.Delivery.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferBootstrapPhaseReceiptStatus.Failed, failed.Status);
            Assert.Equal(2, failed.Copies.Length);
            Assert.All(failed.Copies, copy => Assert.Equal(fixture.Command.EpochFrameIdentity, copy.FrameIdentity));
            await execution.Settlement;
            Assert.Same(execution, bridge.ExecuteFallbackBootstrapPhase(fixture.Command, dataSession, () => true, cts.Token));
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CorrelatedBootstrap_PhysicalFeedbackWaitsForEveryCopy(int kind, bool failSecondary)
    {
        FakeNknClient.ResetNetwork();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.correlation.physical");
            var helperClient = new FakeNknClient("helper.bootstrap.correlation.physical");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-bootstrap", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bootstrap", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            helperClient.BeforeSendCoreAsync = async (_, _, lane, _) =>
            {
                if (lane != NknBridgeChannel.Bulk) { return; }
                entered.TrySetResult();
                await release.Task;
                if (failSecondary) { throw new IOException("modeled_secondary_failure"); }
            };
            using var sendCts = new CancellationTokenSource();
            var submission = Assert.IsAssignableFrom<IFileTransferPhysicalSendSubmissionSession>(dataSession)
                .SubmitPhysicalSend(BootstrapPhysicalFrame(kind, sessionId), null, sendCts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await submission.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(submission.BootstrapCopies);
            var delivered = submission.BootstrapCopies!();
            Assert.Equal(2, delivered.Length);
            Assert.Contains(delivered, copy => copy.Lane == "control" && copy.Outcome == FileTransferBootstrapCopyOutcome.Succeeded);
            Assert.Contains(delivered, copy => copy.Lane == "bulk" && copy.Outcome == FileTransferBootstrapCopyOutcome.Pending);
            sendCts.Cancel();
            Assert.False(submission.PhysicalCompletionTask.IsCompleted);
            release.TrySetResult();
            await submission.PhysicalCompletionTask.WaitAsync(TimeSpan.FromSeconds(2));
            var settled = submission.BootstrapCopies();
            Assert.Equal(2, settled.Length);
            Assert.All(settled, copy => Assert.NotEqual(FileTransferBootstrapCopyOutcome.Pending, copy.Outcome));
            Assert.Contains(settled, copy => copy.Lane == "control" && copy.Outcome == FileTransferBootstrapCopyOutcome.Succeeded);
            Assert.True(submission.DeliveryTask.IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CorrelatedBootstrap_DisabledRedundancyRetainsSingleControlCopy(int kind)
    {
        var prior = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY");
        using var unsafeMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY", "0");
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.correlation.single");
            var helperClient = new FakeNknClient("helper.bootstrap.correlation.single");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-bootstrap-single", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bootstrap-single", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "bootstrap-transfer", cts.Token);
            var submission = Assert.IsAssignableFrom<IFileTransferPhysicalSendSubmissionSession>(dataSession)
                .SubmitPhysicalSend(BootstrapPhysicalFrame(kind, sessionId), null, cts.Token);
            await submission.DeliveryTask;
            await submission.PhysicalCompletionTask;
            var copy = Assert.Single(submission.BootstrapCopies!());
            Assert.Equal("control", copy.Lane);
            Assert.Equal(FileTransferBootstrapCopyOutcome.Succeeded, copy.Outcome);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY", prior);
            FakeNknClient.ResetNetwork();
        }
    }

    private static FileTransferDataFrame BootstrapPhysicalFrame(int kind, string sessionId)
    {
        var id = "fbc1:" + new string('a', 64) + ":1:" + new string('b', 64);
        return kind switch
        {
            0 => new FileTransferFallbackBootstrapRequestFrameV6
            {
                SessionId = sessionId, TransferId = "bootstrap-transfer", TransportEpoch = 1, SenderRequestId = id,
            },
            1 => new FileTransferFallbackBootstrapStateFrameV6
            {
                SessionId = sessionId, TransferId = "bootstrap-transfer", TransportEpoch = 1, ReceiverChallengeId = id,
                Epoch = 1, ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1, CreditUntilChunkIndexExclusive = 0,
                TransferPaused = true, TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode,
            },
            _ => new FileTransferTransportEpochFrameV6
            {
                SessionId = sessionId, TransferId = "bootstrap-transfer", TransportEpoch = 1,
                RecoveryMode = FileTransferBootstrapCorrelation.RecoveryMode,
            },
        };
    }
}
