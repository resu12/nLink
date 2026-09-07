using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferRuntimeSuite", "ReceiveRecoveryOperation")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferReceiverRecoverySubmissionTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task RegularV4Receiver_EarlyProofDuringRecoveryDoesNotMakeSubmissionBarrierFatal()
    {
        const string sessionId = "session_receiver_recovery_submission";
        const string transferId = "transfer_receiver_recovery_submission";
        using var sender = new LoopbackFileTransferTransport(sessionId);
        using var bridge = new LoopbackFileTransferTransport(sessionId);
        sender.Connect(bridge);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(bridge);
        using var destination = new MemoryStream();
        await StartReceiverAsync(sender, receiver, destination, sessionId, transferId);
        var context = GetInboundContext(receiver);

        Assert.True(RequestRecovery(receiver, sessionId, transferId));
        await WaitUntilAsync(() => GetRecovery(context).CurrentOperation?.ExecutionState ==
            FileTransferReceiveRecoveryExecutionState.Running);
        var operation = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            GetRecovery(context).CurrentOperation);
        var command = Assert.Single(bridge.BridgeRecoveryExecutionCommands);
        Assert.Equal(operation.Authority.ExecutionCommand, command);

        // A queued receive can prove reachability before the replacement bridge is
        // ready. It is evidence, not permission to submit through the barrier.
        bridge.RaiseReceiveRecoveryProof(command, "file_transfer_data_frame:bulk:1");
        await WaitUntilAsync(() => GetRecovery(context).CurrentOperation?.ReceiveProof is not null);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Running,
            GetRecovery(context).CurrentOperation!.Value.ExecutionState);
        Assert.Equal(FileTransferReceiveRecoveryClosureState.None,
            GetRecovery(context).CurrentOperation!.Value.ClosureState);

        var rejectedSubmissions = 0;
        bridge.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6)
            {
                Interlocked.Increment(ref rejectedSubmissions);
                throw SubmissionBarrierUnavailable();
            }

            return Task.FromResult(false);
        };
        var sentCount = GetProperty<int>(context, "PullV4StateSentCountTotal");
        await SendReceiverStateAsync(receiver, context);

        Assert.True(Volatile.Read(ref rejectedSubmissions) > 0);
        Assert.False(receiver.Snapshot.Inbound!.IsTerminal);
        Assert.Equal(sentCount, GetProperty<int>(context, "PullV4StateSentCountTotal"));
        Assert.False(GetProperty<bool>(context, "PullTransportPaused"));
        Assert.Empty(bridge.SentErrors);
        Assert.Empty(bridge.InvalidatedBridgeRecoveryExecutionCommands);
        Assert.Single(bridge.BridgeRecoveryExecutionCommands);
        Assert.Equal(operation.Authority, GetRecovery(context).CurrentOperation!.Value.Authority);

        bridge.OutboundDataFrameDeliveryOverrideAsync = null;
        bridge.CompleteBridgeRecoveryExecution(command,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "replacement_bridge_ready");
        await WaitUntilAsync(() => GetRecovery(context).CurrentOperation is
        {
            ExecutionState: FileTransferReceiveRecoveryExecutionState.Completed,
            ClosureState: FileTransferReceiveRecoveryClosureState.Adopted,
        });
        // The existing receiver worker must regenerate feedback without the test
        // forcing a replay or granting a new recovery operation.
        await WaitUntilAsync(() => GetProperty<int>(context, "PullV4StateSentCountTotal") > sentCount,
            timeoutMs: 5000);
        Assert.False(receiver.Snapshot.Inbound!.IsTerminal);
        Assert.Empty(bridge.SentErrors);
    }

    [Theory]
    [InlineData("missing_operation")]
    [InlineData("stale_data_session")]
    [InlineData("cancelled_operation")]
    [InlineData("terminal_ready")]
    [InlineData("barrier_inactive")]
    public async Task RegularV4Receiver_SubmissionRejectionCannotCreateOrReplaceRecoveryAuthority(string invalidScope)
    {
        var sessionId = $"session_receiver_recovery_{invalidScope}";
        var transferId = $"transfer_receiver_recovery_{invalidScope}";
        using var sender = new LoopbackFileTransferTransport(sessionId);
        using var bridge = new LoopbackFileTransferTransport(sessionId);
        sender.Connect(bridge);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(bridge);
        using var destination = new MemoryStream();
        await StartReceiverAsync(sender, receiver, destination, sessionId, transferId);
        var context = GetInboundContext(receiver);
        var dataSession = GetProperty<IFileTransferDataSession>(context, "DataSession");
        if (invalidScope != "missing_operation")
        {
            Assert.True(RequestRecovery(receiver, sessionId, transferId));
            await WaitUntilAsync(() => GetRecovery(context).CurrentOperation?.ExecutionState ==
                FileTransferReceiveRecoveryExecutionState.Running);
        }

        if (invalidScope == "stale_data_session")
        {
            // Keep the issued authority intact but replace one live scope field.
            context.GetType().GetProperty("DataSessionGeneration")!.SetValue(context,
                GetProperty<long>(context, "DataSessionGeneration") + 1);
        }
        else if (invalidScope == "cancelled_operation")
        {
            await receiver.CancelTransferAsync(transferId, "test_cancelled_recovery", CancellationToken.None);
        }

        var recoveryBefore = GetRecovery(context);
        var executionsBefore = bridge.BridgeRecoveryExecutionCommands.Count;
        var rejected = Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
            "TryDeferInboundRegularNknV4ReceiverStateSendFailureForRecovery",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receiver,
            [context, dataSession, new FileTransferStateFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                TerminalReady = invalidScope == "terminal_ready",
            }, "chunk_batch_committed", SubmissionBarrierUnavailable(invalidScope != "barrier_inactive")]));

        Assert.False(rejected);
        Assert.False(GetProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal(recoveryBefore, GetRecovery(context));
        Assert.Equal(executionsBefore, bridge.BridgeRecoveryExecutionCommands.Count);
    }

    private static async Task StartReceiverAsync(
        LoopbackFileTransferTransport sender,
        SessionFileTransferService receiver,
        Stream destination,
        string sessionId,
        string transferId)
    {
        const string fileName = "receiver-recovery-submission.bin";
        const int fileSize = 128;
        const string route = FileTransferRouteResolver.RegularNknV4FastToken;
        await sender.SendFileTransferOfferAsync(new FileTransferOfferV2
        {
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            FileSizeBytes = fileSize,
            PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute = route,
        }, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State ==
            FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId,
            (_, _) => Task.FromResult(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State ==
            FileTransferTransferState.AwaitingMetadata);
        await sender.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
        {
            SessionId = sessionId,
            TransferId = transferId,
            ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute = route,
            SessionRole = FileTransferProtocol.SessionRoleSender,
            ChunkSizeBytes = 4,
            InitialPipelineDepth = 1,
        }, CancellationToken.None);
        await WaitUntilAsync(() => GetProperty<object?>(GetInboundContext(receiver), "DataSession") is not null);
        var senderSession = await sender.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);
        await senderSession.SendAsync(new FileTransferManifestFrameV4
        {
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            FileSizeBytes = fileSize,
            ChunkSizeBytes = 4,
            ChunkCount = fileSize / 4,
            Sha256Base64 = Convert.ToBase64String(SHA256.HashData(new byte[fileSize])),
        }, CancellationToken.None);
        await WaitUntilAsync(() => GetProperty<bool>(GetInboundContext(receiver), "PullManifestReceived") &&
            GetProperty<int>(GetInboundContext(receiver), "PullV4StateSentCountTotal") > 0);
    }

    private static bool RequestRecovery(SessionFileTransferService service, string sessionId, string transferId)
        => Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
            "TryRequestFileTransferReceiveRecovery", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(FileTransferReceiveRecoveryRequest)], modifiers: null)!
            .Invoke(service, [new FileTransferReceiveRecoveryRequest(
                sessionId, transferId, FileTransferDirection.Inbound, "control_receive_stalled")]));

    private static Task<bool> SendReceiverStateAsync(SessionFileTransferService service, object context)
        => Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService).GetMethod(
            "SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "chunk_batch_committed", false, false, false, true]));

    private static FileTransferDataSessionSubmissionUnavailableException SubmissionBarrierUnavailable(
        bool recoveryBarrierActive = true)
        => new(FileTransferDataSessionSubmissionUnavailableReason.SubmissionEpochNotReady,
            "pre_wait", "control", 0, 2, 0, 0, recoveryBarrierActive,
            "NKN bridge process is not ready for payload submission.");

    private static object GetInboundContext(SessionFileTransferService service)
        => typeof(SessionFileTransferService).GetField("inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static T GetProperty<T>(object instance, string name)
        => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private static FileTransferReceiveRecoverySubstate GetRecovery(object context)
        => GetProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState")
            .FileTransferReceiveRecovery;
}
