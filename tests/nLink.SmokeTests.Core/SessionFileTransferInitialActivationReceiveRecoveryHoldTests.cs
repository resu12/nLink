using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class
    SessionFileTransferInitialActivationReceiveRecoveryHoldTests :
    SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData("reset_session_state")]
    [InlineData("transport_detached")]
    [InlineData("transfer_terminal")]
    [InlineData("service_disposed")]
    public async Task
        RuntimeUnlockInitialActivationReceiveRecoveryHold_InvalidationDirectiveReachesCapturedBridgeAfterTeardown(
            string teardown)
    {
        var sessionId = $"session_initial_activation_hold_{teardown}";
        var transferId = $"transfer_initial_activation_hold_{teardown}";
        using var bridge =
            new InitialActivationHoldLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        bridge.Connect(peer);

        using var service = new SessionFileTransferService();
        service.AttachTransport(bridge);
        var started = await service.TryStartSendAsync(
            new FileTransferSendDescriptor(
                "initial-activation-hold.bin",
                FileSizeBytes: 1,
                transferId,
                ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(
                new MemoryStream([0x2a], writable: false)),
            CancellationToken.None);
        Assert.NotNull(started);

        var context = GetOutboundContext(service);
        var operation = await SeedCurrentReceiveRecoveryOperationAsync(
            service,
            bridge,
            context);
        var request = CreateInitialActivationHoldRequest(operation);
        var held = bridge.RaiseHoldRequest(request);
        Assert.Equal(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            held.Status);
        Assert.Equal(request, held.Request);
        Assert.True(held.HoldGeneration > 0);
        Assert.Equal(operation.Authority, held.ReceiveRecoveryOperation?.Authority);

        PauseInitialActivationHoldDirectiveWorker(service);
        switch (teardown)
        {
            case "reset_session_state":
                service.ResetSessionState();
                Assert.Null(service.Snapshot.Outbound);
                break;
            case "transport_detached":
                service.DetachTransport();
                Assert.Null(GetAttachedTransport(service));
                break;
            case "transfer_terminal":
                var terminal = await service.CancelTransferAsync(
                    transferId,
                    "test_initial_activation_hold_terminal",
                    CancellationToken.None);
                Assert.NotNull(terminal);
                Assert.True(terminal.IsTerminal);
                break;
            case "service_disposed":
                service.Dispose();
                Assert.Null(GetAttachedTransport(service));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(teardown),
                    teardown,
                    "Unknown teardown.");
        }

        Assert.Empty(bridge.ObservedHoldDecisions);
        FlushInitialActivationHoldDirectiveWorker(service);

        var invalidated = Assert.Single(bridge.ObservedHoldDecisions);
        Assert.Equal(request, invalidated.Request);
        Assert.Equal(held.HoldGeneration, invalidated.HoldGeneration);
        Assert.Equal(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Invalidated,
            invalidated.Status);
        Assert.Equal(
            teardown == "transfer_terminal"
                ? "terminal_state"
                : "filetransfer_receive_recovery_operation_invalidated",
            invalidated.Reason);
        Assert.Equal(
            FileTransferReceiveRecoveryExecutionState.Invalidated,
            invalidated.ReceiveRecoveryOperation?.ExecutionState);
        Assert.Equal(
            operation.Authority,
            invalidated.ReceiveRecoveryOperation?.Authority);
    }

    [Fact]
    public async Task
        RuntimeUnlockInitialActivationReceiveRecoveryHold_QueuedReleaseIsDroppedAfterTerminalAndExactInvalidationSurvives()
    {
        const string sessionId =
            "session_initial_activation_hold_stale_release";
        const string transferId =
            "transfer_initial_activation_hold_stale_release";
        using var bridge =
            new InitialActivationHoldLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        bridge.Connect(peer);

        using var service = new SessionFileTransferService();
        service.AttachTransport(bridge);
        var started = await service.TryStartSendAsync(
            new FileTransferSendDescriptor(
                "initial-activation-hold-stale-release.bin",
                FileSizeBytes: 1,
                transferId,
                ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(
                new MemoryStream([0x2a], writable: false)),
            CancellationToken.None);
        Assert.NotNull(started);

        var context = GetOutboundContext(service);
        var operation = await SeedCurrentReceiveRecoveryOperationAsync(
            service,
            bridge,
            context);
        var request = CreateInitialActivationHoldRequest(operation);
        var held = bridge.RaiseHoldRequest(request);
        Assert.Equal(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            held.Status);

        PauseInitialActivationHoldDirectiveWorker(service);
        _ = bridge.CompleteBridgeRecoveryExecution(
            operation.Authority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "test_initial_activation_hold_completed");
        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context).CurrentOperation is
                {
                    ExecutionState:
                        FileTransferReceiveRecoveryExecutionState.Completed,
                    ClosureState:
                        FileTransferReceiveRecoveryClosureState.Retained,
                });

        bridge.RaiseReceiveRecoveryProof(
            operation.Authority.ExecutionCommand,
            "test_initial_activation_hold_receive_proof",
            "test_initial_activation_hold_receive_proof");
        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context)
                    .LastInitialActivationHold is
                {
                    Status:
                        RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                            .Released,
                });

        var terminal = await service.CancelTransferAsync(
            transferId,
            "test_initial_activation_hold_terminal",
            CancellationToken.None);
        Assert.NotNull(terminal);
        Assert.True(terminal.IsTerminal);
        Assert.Empty(bridge.ObservedHoldDecisions);

        FlushInitialActivationHoldDirectiveWorker(service);

        var invalidated = Assert.Single(bridge.ObservedHoldDecisions);
        Assert.Equal(request, invalidated.Request);
        Assert.Equal(held.HoldGeneration, invalidated.HoldGeneration);
        Assert.Equal(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Invalidated,
            invalidated.Status);
        Assert.Equal("terminal_state", invalidated.Reason);
        Assert.Equal(
            FileTransferReceiveRecoveryExecutionState.Invalidated,
            invalidated.ReceiveRecoveryOperation?.ExecutionState);
    }

    [Fact]
    public async Task
        RuntimeUnlockInitialActivationReceiveRecoveryHold_ExactSuccessorRebindAndReleaseReachBridgeInCoordinatorOrder()
    {
        const string sessionId =
            "session_initial_activation_hold_successor_release";
        const string transferId =
            "transfer_initial_activation_hold_successor_release";
        using var bridge =
            new InitialActivationHoldLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        bridge.Connect(peer);

        using var service = new SessionFileTransferService();
        service.AttachTransport(bridge);
        var started = await service.TryStartSendAsync(
            new FileTransferSendDescriptor(
                "initial-activation-hold-successor-release.bin",
                FileSizeBytes: 1,
                transferId,
                ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(
                new MemoryStream([0x2a], writable: false)),
            CancellationToken.None);
        Assert.NotNull(started);

        var context = GetOutboundContext(service);
        var predecessor = await SeedCurrentReceiveRecoveryOperationAsync(
            service,
            bridge,
            context);
        var request = CreateInitialActivationHoldRequest(predecessor);
        var held = bridge.RaiseHoldRequest(request);
        Assert.Equal(
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held,
            held.Status);
        PauseInitialActivationHoldDirectiveWorker(service);
        _ = bridge.CompleteBridgeRecoveryExecution(
            predecessor.Authority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "test_initial_activation_hold_predecessor_completed");
        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context).CurrentOperation is
                {
                    ExecutionState:
                        FileTransferReceiveRecoveryExecutionState.Completed,
                    ClosureState:
                        FileTransferReceiveRecoveryClosureState.Retained,
                });

        var observedUtcMs = Math.Max(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            GetReceiveRecoverySubstate(context).CurrentOperation!.Value
                .ExecutionReceiptObservedUtcMs + 1);
        bridge.RaiseBridgeRecoveryEvaluation(
            new FileTransferBridgeRecoveryEvaluationRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                FileTransferBridgeRecoveryEvaluationKind.PingTimeout,
                "bridge_ping_timeout",
                "bridge_ping_timeout",
                ObservationGeneration: 1,
                ObservedUtcMs: observedUtcMs,
                ConsecutiveObservations: 1));
        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context).CurrentOperation is
                    { } successor &&
                successor.Authority.OperationGeneration ==
                    predecessor.Authority.OperationGeneration + 1);
        var reboundOperation =
            Assert.IsType<FileTransferReceiveRecoveryOperation>(
                GetReceiveRecoverySubstate(context).CurrentOperation);
        Assert.Equal(
            predecessor.Authority.AuthorityId,
            reboundOperation.Authority.PredecessorAuthorityId);

        _ = bridge.CompleteBridgeRecoveryExecution(
            reboundOperation.Authority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "test_initial_activation_hold_successor_completed");
        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context).CurrentOperation is
                {
                    ExecutionState:
                        FileTransferReceiveRecoveryExecutionState.Completed,
                    ClosureState:
                        FileTransferReceiveRecoveryClosureState.Retained,
                } successor &&
                successor.Authority ==
                    reboundOperation.Authority);
        bridge.RaiseReceiveRecoveryProof(
            reboundOperation.Authority.ExecutionCommand,
            "test_initial_activation_hold_successor_proof",
            "test_initial_activation_hold_successor_proof");
        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context).LastInitialActivationHold
                    is
                {
                    Status:
                        RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                            .Released,
                });

        Assert.Empty(bridge.ObservedHoldDecisions);
        FlushInitialActivationHoldDirectiveWorker(service);

        var delivered = bridge.ObservedHoldDecisions.ToArray();
        var reboundIndex = Array.FindIndex(
            delivered,
            decision =>
                decision.Status ==
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Held &&
                decision.ReceiveRecoveryOperation?.Authority ==
                    reboundOperation.Authority &&
                decision.PreviousReceiveRecoveryAuthority ==
                    predecessor.Authority);
        var releaseIndex = Array.FindIndex(
            delivered,
            decision =>
                decision.Status ==
                    RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus
                        .Released &&
                decision.ReceiveRecoveryOperation?.Authority ==
                    reboundOperation.Authority);
        Assert.True(reboundIndex >= 0);
        Assert.True(releaseIndex > reboundIndex);
        Assert.All(
            delivered,
            decision =>
            {
                Assert.Equal(request, decision.Request);
                Assert.Equal(held.HoldGeneration, decision.HoldGeneration);
            });
    }

    private static async Task<FileTransferReceiveRecoveryOperation>
        SeedCurrentReceiveRecoveryOperationAsync(
            SessionFileTransferService service,
            InitialActivationHoldLoopbackTransport bridge,
            object context)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var dataSession = await bridge.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        SetProperty(context, "DataSession", dataSession);
        SetProperty(context, "DataSessionGeneration", 1L);

        var tryRequest = Assert.IsAssignableFrom<MethodInfo>(
            typeof(SessionFileTransferService).GetMethod(
                "TryRequestFileTransferReceiveRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(FileTransferReceiveRecoveryRequest)],
                modifiers: null));
        var requested = Assert.IsType<bool>(
            tryRequest.Invoke(
                service,
                [
                    new FileTransferReceiveRecoveryRequest(
                        sessionId,
                        transferId,
                        FileTransferDirection.Outbound,
                        "test_initial_activation_hold_teardown"),
                ]));
        Assert.True(requested);

        await WaitUntilAsync(
            () =>
                GetReceiveRecoverySubstate(context).CurrentOperation is
                    {
                        ExecutionState:
                            FileTransferReceiveRecoveryExecutionState.Issued or
                            FileTransferReceiveRecoveryExecutionState.Running,
                    });
        return Assert.IsType<FileTransferReceiveRecoveryOperation>(
            GetReceiveRecoverySubstate(context).CurrentOperation);
    }

    private static
        RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest
        CreateInitialActivationHoldRequest(
            FileTransferReceiveRecoveryOperation operation)
    {
        var authority = operation.Authority;
        var observation = authority.Observation;
        var publicationAuthority =
            FileTransferCoordinator.CreateRoutePublicationAuthority(
                observation.RoutePublication,
                "test_initial_activation_hold_teardown");
        Assert.NotNull(publicationAuthority);
        return new RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest(
            observation.SessionId,
            observation.TransferId,
            observation.Direction,
            OfferNonce: "initial-activation-hold-offer",
            PayerDecisionId: 1,
            OfferGeneration: 1,
            TransactionGeneration: 1,
            TunaPathLeaseGeneration: 1,
            TunaPathLeaseListenerRunId:
                "initial-activation-hold-listener",
            ControlEnvelopeFingerprint:
                "initial-activation-hold-envelope-fingerprint",
            PeerVisibleDispatchObserved: false,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldBoundary
                .OfferPreSend)
        {
            ExpectedScope =
                new RuntimeUnlockInitialActivationReceiveRecoveryExpectedScope(
                    publicationAuthority,
                    observation.TransportEpoch,
                    observation.BridgeRecoveryGeneration,
                    observation.CheckpointRequestId,
                    observation.DataSessionIdentity,
                    observation.DataSessionGeneration,
                    authority.ExecutionCommand),
        };
    }

    private static void PauseInitialActivationHoldDirectiveWorker(
        SessionFileTransferService service)
    {
        var ordering = GetInitialActivationHoldDirectiveOrdering(service);
        var orderingType = ordering.GetType();
        var gate = Assert.IsType<object>(
            orderingType.GetProperty("Gate")?.GetValue(ordering));
        var workerScheduled = Assert.IsAssignableFrom<PropertyInfo>(
            orderingType.GetProperty("WorkerScheduled"));
        lock (gate)
        {
            workerScheduled.SetValue(ordering, true);
        }
    }

    private static void FlushInitialActivationHoldDirectiveWorker(
        SessionFileTransferService service)
    {
        var flush = Assert.IsAssignableFrom<MethodInfo>(
            typeof(SessionFileTransferService).GetMethod(
                "FlushRuntimeUnlockInitialActivationReceiveRecoveryHoldDirectives",
                BindingFlags.Instance | BindingFlags.NonPublic));
        flush.Invoke(service, parameters: null);
    }

    private static object GetInitialActivationHoldDirectiveOrdering(
        SessionFileTransferService service)
    {
        var field = Assert.IsAssignableFrom<FieldInfo>(
            typeof(SessionFileTransferService).GetField(
                "runtimeUnlockInitialActivationHoldDirectiveOrdering",
                BindingFlags.Instance | BindingFlags.NonPublic));
        return Assert.IsAssignableFrom<object>(field.GetValue(service));
    }

    private static IFileTransferSignalingTransport? GetAttachedTransport(
        SessionFileTransferService service)
    {
        var field = Assert.IsAssignableFrom<FieldInfo>(
            typeof(SessionFileTransferService).GetField(
                "transport",
                BindingFlags.Instance | BindingFlags.NonPublic));
        return field.GetValue(service) as IFileTransferSignalingTransport;
    }

    private static object GetOutboundContext(
        SessionFileTransferService service)
    {
        var field = Assert.IsAssignableFrom<FieldInfo>(
            typeof(SessionFileTransferService).GetField(
                "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic));
        return Assert.IsAssignableFrom<object>(field.GetValue(service));
    }

    private static FileTransferReceiveRecoverySubstate
        GetReceiveRecoverySubstate(object context)
        => GetRequiredProperty<FileTransferCoordinatorPersistentState>(
                context,
                "CoordinatorPersistentState")
            .FileTransferReceiveRecovery;

    private static T GetRequiredProperty<T>(object context, string name)
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(
            context.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public));
        return Assert.IsType<T>(property.GetValue(context));
    }

    private static void SetProperty(
        object context,
        string name,
        object? value)
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(
            context.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public));
        property.SetValue(context, value);
    }

    private sealed class InitialActivationHoldLoopbackTransport(
        string sessionId) :
        LoopbackFileTransferTransport(sessionId),
        IRuntimeUnlockInitialActivationReceiveRecoveryHoldBridge
    {
        private event EventHandler<
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequestedEventArgs>?
            holdRequested;
        private event EventHandler<
            RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequestedEventArgs>?
            holdInvalidationRequested;

        internal ConcurrentQueue<
            RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision>
            ObservedHoldDecisions
        {
            get;
        } = [];

        event EventHandler<
            RuntimeUnlockInitialActivationReceiveRecoveryHoldRequestedEventArgs>?
            IRuntimeUnlockInitialActivationReceiveRecoveryHoldBridge
                .RuntimeUnlockInitialActivationReceiveRecoveryHoldRequested
        {
            add => holdRequested += value;
            remove => holdRequested -= value;
        }

        event EventHandler<
            RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequestedEventArgs>?
            IRuntimeUnlockInitialActivationReceiveRecoveryHoldBridge
                .RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequested
        {
            add => holdInvalidationRequested += value;
            remove => holdInvalidationRequested -= value;
        }

        internal RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision
            RaiseHoldRequest(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest
                    request)
        {
            var args =
                new RuntimeUnlockInitialActivationReceiveRecoveryHoldRequestedEventArgs(
                    request);
            holdRequested?.Invoke(this, args);
            return Assert.IsType<
                RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision>(
                args.Decision);
        }

        bool IRuntimeUnlockInitialActivationReceiveRecoveryHoldBridge
            .ObserveRuntimeUnlockInitialActivationReceiveRecoveryHoldDecision(
                RuntimeUnlockInitialActivationReceiveRecoveryHoldDecision
                    decision)
        {
            ObservedHoldDecisions.Enqueue(decision);
            return true;
        }
    }
}
