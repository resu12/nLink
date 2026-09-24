namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Check received-frame custody before a hold can pause work or start a drain.
    // Legacy transport observations are not promoted by a separately staged wake.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool IsBootstrapCorrelationInitiationCurrentLocked(IFileTransferRouteTransitionBridge bridge,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        FileTransferCoordinatorState state;
        IFileTransferDataSession? session;
        FileTransferDataFrame? frame;
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding? binding;
        if (evaluation.Direction == FileTransferDirection.Outbound && outboundTransfer is { } outbound)
        {
            state = CreateCoordinatorStateLocked(outbound); session = outbound.DataSession;
            frame = outbound.PendingFallbackV6ProofBootstrapPeerFrameEvidence;
            binding = outbound.PendingFallbackV6ProofBootstrapPeerFrameBinding;
        }
        else if (evaluation.Direction == FileTransferDirection.Inbound && inboundTransfer is { } inbound)
        {
            state = CreateCoordinatorStateLocked(inbound); session = inbound.DataSession;
            frame = inbound.PendingFallbackV6ProofBootstrapPeerFrameEvidence;
            binding = inbound.PendingFallbackV6ProofBootstrapPeerFrameBinding;
        }
        else { return true; }
        if (!UsesBootstrapCorrelation(state)) { return true; }
        if (!ReferenceEquals(transport, bridge) || !bridge.SupportsFallbackBootstrapCorrelation) { return false; }
        if (frame is null && binding is null)
        {
            return evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
                GetCurrentBootstrapLocalStopStateLocked(evaluation) is not null;
        }
        if (frame is not FileTransferFallbackBootstrapFrameV6 || binding is null || session is null ||
            GetBootstrapCorrelationStateLocked(evaluation.Direction, session, frame, out var current) is null ||
            binding.FrameIdentity != current.FrameIdentity || !MatchesFallbackV6ProofBootstrapPeerFrameBinding(binding, current)) { return false; }
        if (evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable)
        {
            return GetCurrentBootstrapLocalStopStateLocked(evaluation) is not null;
        }
        var observation = CreateFallbackV6ProofBootstrapObservation(evaluation, state.TransferId, current.DurableCheckpoint);
        return FileTransferCoordinator.IsFallbackBootstrapInitiationCurrent(state, observation) &&
            MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(current, evaluation);
    }

    private static bool IsLocalBootstrapStopShapeValid(FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
        => evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
           evaluation.LocalStop is { Scope.Publication: { } publication, Scope.Source: { }, DataSession: { }, ProviderGeneration: > 0 } &&
           evaluation.SessionId == publication.SessionId && evaluation.Direction == publication.Direction &&
           evaluation.Source == "runtime_disabled" && evaluation.ProofKind == "local_runtime_disabled" &&
           evaluation.ObservationGeneration > 0 && evaluation.FallbackEpoch > 0 && evaluation.ObservedUtcMilliseconds > 0 &&
           evaluation.TriggerTransferId is null && evaluation.TriggerTransportEpoch == 0 &&
           evaluation.TriggerFrameFingerprint is null && evaluation.TriggerDataSession is null &&
           evaluation.TriggerDataSessionGeneration == 0 && evaluation.TriggerSourceScope is null && evaluation.TriggerBootstrapGeneration == 0;

    private FileTransferCoordinatorState? GetCurrentBootstrapLocalStopStateLocked(FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        if (!IsLocalBootstrapStopShapeValid(evaluation)) { return null; }
        var stop = evaluation.LocalStop!;
        FileTransferCoordinatorState? state = null;
        if (evaluation.Direction == FileTransferDirection.Outbound && outboundTransfer is { IsTerminal: false, UserPaused: false } outbound &&
            MatchesBootstrapLocalStopDataSession(evaluation, outbound.TransferId, outbound.DataSession))
        {
            state = CreateCoordinatorStateLocked(outbound);
        }
        else if (evaluation.Direction == FileTransferDirection.Inbound && inboundTransfer is { IsTerminal: false, UserPaused: false } inbound &&
            MatchesBootstrapLocalStopDataSession(evaluation, inbound.TransferId, inbound.DataSession))
        {
            state = CreateCoordinatorStateLocked(inbound);
        }
        return state is not null && state.CurrentDataSessionGeneration > 0 && !string.IsNullOrEmpty(state.CurrentDataSessionIdentity) &&
            FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(state, stop.Scope) ? state : null;
    }

    private static bool MatchesBootstrapLocalStopDataSession(FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        string transferId, IFileTransferDataSession? session)
        => IsLocalBootstrapStopShapeValid(evaluation) && evaluation.LocalStop is { } stop &&
           stop.Scope.Publication.TransferId == transferId && ReferenceEquals(stop.DataSession, session) &&
           session is IFileTransferDataSessionGenerationProvider provider && stop.ProviderGeneration == provider.Generation;

    private FileTransferFallbackV6ProofBootstrapObservation CreateFallbackV6ProofBootstrapObservation(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation, string transferId,
        FileTransferRouteTransitionDurableCheckpoint checkpoint)
    {
        var state = evaluation.LocalStop is null ? null : GetCurrentBootstrapLocalStopStateLocked(evaluation);
        return new(evaluation.SessionId, transferId, evaluation.Direction, evaluation.ObservationGeneration,
            evaluation.FallbackEpoch, evaluation.Source, evaluation.ProofKind, evaluation.ObservedUtcMilliseconds, checkpoint)
        {
            InitiationKind = evaluation.InitiationKind,
            LocalStop = state is null ? null : new(evaluation.LocalStop!.Scope, state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration),
        };
    }
}
