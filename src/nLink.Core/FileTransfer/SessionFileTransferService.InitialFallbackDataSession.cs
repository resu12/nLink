namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static InitialFallbackDataSessionDecision BindInitialFallbackDataSession(
        FileTransferCoordinatorState state, IFileTransferDataSession session)
        => FileTransferCoordinator.BindInitialFallbackDataSession(state, new(
            state.PersistentState.CoordinatorInstanceId!, session.SessionId, session.TransferId, state.Direction,
            state.CurrentRuntimeOwner ?? default, state.CurrentRoutePublicationToken ?? default,
            CreateReceiveRecoveryDataSessionIdentity(state.TransferId, session, 1), 1));

    private bool AttachInitialFallbackDataSessionLocked(OutboundTransferContext context, IFileTransferDataSession session)
    {
        var decision = BindInitialFallbackDataSession(CreateCoordinatorStateLocked(context), session);
        if (decision.Binding is not { } binding) { return false; }
        AdoptCoordinatorAuthoredEnvelopeLocked(context.CoordinatorInstanceId, context.SessionId, context.TransferId,
            FileTransferDirection.Outbound, decision.State, decision.SnapshotEnvelope, adopted =>
            {
                context.DataSession = session;
                context.DataSessionGeneration = binding.DataSessionGeneration;
                context.CoordinatorPersistentState = adopted.PersistentState;
            }, context.CoordinatorSnapshotEmitter);
        SubscribeDataSessionEvents(session);
        return true;
    }

    private bool AttachInitialFallbackDataSessionLocked(InboundTransferContext context, IFileTransferDataSession session)
    {
        var decision = BindInitialFallbackDataSession(CreateCoordinatorStateLocked(context), session);
        if (decision.Binding is not { } binding) { return false; }
        AdoptCoordinatorAuthoredEnvelopeLocked(context.CoordinatorInstanceId, context.SessionId, context.TransferId,
            FileTransferDirection.Inbound, decision.State, decision.SnapshotEnvelope, adopted =>
            {
                context.DataSession = session;
                context.DataSessionGeneration = binding.DataSessionGeneration;
                context.CoordinatorPersistentState = adopted.PersistentState;
            }, context.CoordinatorSnapshotEmitter);
        ReconcileInboundRegularV4FrontierRepairProjectionLocked(context, "initial_fallback_data_session_bound");
        SubscribeDataSessionEvents(session);
        return true;
    }
}
