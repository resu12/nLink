using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private void OnReceiverBarrierBindingRequested(object? sender, InitialActivationReceiverBarrierBindingEventArgs args)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(transport, sender) || inboundTransfer is not { IsTerminal: false } context ||
                !ReferenceEquals(context.DataSession, args.DataSession))
            {
                return;
            }
            var state = CreateCoordinatorStateLocked(context);
            var source = FileTransferCoordinator.CreateInitialActivationReceiverBarrierSource(state, args.DataSession);
            if (source is null)
            {
                return;
            }
            var current = state.PersistentState.InitialActivationReceiverBarrier.Current;
            if (args.Authority is null)
            {
                if (current is not null && current.Authority.PauseGeneration < args.PauseGeneration)
                {
                    AdoptReceiverBarrierDecisionLocked(context, FileTransferCoordinator.InvalidateInitialActivationReceiverBarrier(
                        state, current.Authority, "receiver_barrier_pause_replaced", args.PauseGeneration));
                    state = CreateCoordinatorStateLocked(context);
                }
                args.Decision = FileTransferCoordinator.BindInitialActivationReceiverBarrier(
                    state, source, args.PauseGeneration, args.Negotiation);
            }
            else if (current?.Authority == args.Authority && state.CurrentRuntimeUnlockAdmission is { } admission)
            {
                args.Decision = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(
                    state, args.Authority, source, admission, args.Negotiation);
            }
            if (args.Decision is { } decision)
            {
                AdoptReceiverBarrierDecisionLocked(context, decision);
                if (args.Authority is null && decision.Authority is { } bound && decision.Status is
                    (InitialActivationReceiverBarrierDecisionStatus.Accepted or InitialActivationReceiverBarrierDecisionStatus.Idempotent) &&
                    CreateCoordinatorStateLocked(context).CurrentRuntimeUnlockAdmission is { } admission)
                {
                    args.Decision = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(
                        CreateCoordinatorStateLocked(context), bound, source, admission, args.Negotiation);
                    AdoptReceiverBarrierDecisionLocked(context, args.Decision);
                }
            }
        }
    }

    private static void AttachInitialReceiverBarrierAdmissionLocked(InboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        if (state.PersistentState.InitialActivationReceiverBarrier.Current is not { } barrier ||
            state.CurrentRuntimeUnlockAdmission is not { } admission || context.DataSession is not { } dataSession ||
            FileTransferCoordinator.CreateInitialActivationReceiverBarrierSource(state, dataSession) is not { } source)
        {
            return;
        }
        var decision = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(
            state, barrier.Authority, source, admission, barrier.Negotiation);
        AdoptReceiverBarrierDecisionLocked(context, decision);
    }

    private void OnReceiverBarrierFailureObserved(object? sender, InitialActivationReceiverBarrierFailureEventArgs args)
    {
        if (sender is not IInitialActivationReceiverBarrierBridge bridge || !ReferenceEquals(transport, sender) ||
            !bridge.IsReceiverBarrierFailureCurrent(args))
        {
            return;
        }
        InboundTransferContext context;
        InitialActivationReceiverBarrierCloseCommand command;
        lock (gate)
        {
            if (disposed || inboundTransfer is not { IsTerminal: false } current ||
                !ReferenceEquals(current.DataSession, args.Authority.Source.DataSession) ||
                !current.PullTransportPaused || !IsTunaActivationNegotiationTransportPauseReason(current.PullTransportPauseReason))
            {
                return;
            }
            context = current;
            var state = CreateCoordinatorStateLocked(context);
            if (state.PersistentState.InitialActivationReceiverBarrier.Current?.Negotiation != args.RetainedNegotiation)
            {
                return;
            }
            var source = FileTransferCoordinator.CreateInitialActivationReceiverBarrierSource(state, context.DataSession!);
            if (source is null)
            {
                return;
            }
            args.Decision = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(
                state, args.Authority, source, args.Authority.PauseGeneration, args.Negotiation, args.Origin);
            AdoptReceiverBarrierDecisionLocked(context, args.Decision);
            if (args.Decision.Command is not { } prepared || args.Decision.Status is not
                (InitialActivationReceiverBarrierDecisionStatus.Accepted or InitialActivationReceiverBarrierDecisionStatus.Idempotent))
            {
                return;
            }
            command = prepared;
        }

        // Transport and lane lifetime checks never run under the service gate.
        var receipt = bridge.ApplyReceiverBarrierClose(command);
        var released = false;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(transport, bridge) || !ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal || !ReferenceEquals(context.DataSession, command.Authority.Source.DataSession))
            {
                return;
            }
            var state = CreateCoordinatorStateLocked(context);
            var source = FileTransferCoordinator.CreateInitialActivationReceiverBarrierSource(state, context.DataSession!);
            if (source is null)
            {
                return;
            }
            command.Authority.ExecutionFence.TryFinalize(() =>
            {
                args.Decision = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(state, source, receipt);
                AdoptReceiverBarrierDecisionLocked(context, args.Decision);
                if (args.Decision.Status == InitialActivationReceiverBarrierDecisionStatus.Accepted)
                {
                    released = TryResumeInboundTransportLocked(context, "initial_activation_receiver_barrier_closed",
                        false, FileTransferTransportHandoffKind.None, FileTransferTransportKind.RegularNkn);
                }
            });
        }
        if (released)
        {
            ScheduleInboundV4TransportPauseControlRetry(context, paused: false,
                "initial_activation_receiver_barrier_closed", localTunaActivationBarrier: true);
            RunHardPriorityInboundLifecycle("initial_receiver_barrier_closed", async () =>
            {
                // Build a fresh state from current buffered progress; no cached frame or repair authority.
                await SendInboundV4StateAsync(context, "initial_activation_receiver_barrier_closed",
                    terminalReady: false, forceSend: true).ConfigureAwait(false);
            });
        }
        LocalOperationalLog.Info("FileTransferService",
            $"event=filetransfer_initial_activation_receiver_barrier_close_decided; direction=inbound; authority_id={args.Authority.AuthorityId}; barrier_generation={args.Authority.BarrierGeneration}; pause_generation={args.Authority.PauseGeneration}; decision_status={args.Decision?.Status}; decision_reason={FormatProtocolLogValue(args.Decision?.Reason)}; authority_effect=none");
    }

    private static void AdoptReceiverBarrierDecisionLocked(InboundTransferContext context, InitialActivationReceiverBarrierDecision decision)
    {
        AdoptCoordinatorAuthoredEnvelopeLocked(context.CoordinatorInstanceId, context.SessionId, context.TransferId,
            FileTransferDirection.Inbound, decision.State, decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState, context.CoordinatorSnapshotEmitter);
        if (decision.Status != InitialActivationReceiverBarrierDecisionStatus.Accepted ||
            decision.Receipt is not { Status: InitialActivationReceiverBarrierCloseApplyStatus.Applied } ||
            decision.SnapshotEnvelope is not { } envelope)
        {
            return;
        }
        var matches = envelope.Aggregate.Operations.Where(operation => operation.ReceiverBarrier is not null &&
            operation.OperationRole == "receiver_barrier_terminal" && operation.AfterState == "Closed" &&
            operation.ReceiptValidated).ToArray();
        if (matches.Length != 1)
        {
            return;
        }
        var operation = matches.Single();
        context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(context.CoordinatorInstanceId, context.SessionId,
            context.TransferId, FileTransferDirection.Inbound, envelope.Aggregate.AfterStateGeneration,
            operation.Family, operation.AuthorityId, operation.OperationGeneration, operation.ReceiptIdentity,
            operation.ReceiptGeneration, operation.ReceiptStatus, operation.ReceiptFingerprint,
            "receiver_barrier_close_applied", expectedOperationKey: operation.OperationKey);
    }
}
