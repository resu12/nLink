namespace NLink.Core.FileTransfer;

internal enum FallbackHandoffReleaseReason
{
    DeadlineExpired,
    DispatchFinished,
    RepairCapacityReached,
}

internal static partial class FileTransferCoordinator
{
    internal static long CapFallbackHandoffDeadline(RuntimeUnlockRetryDispatchAuthority authority, long deadlineUtcMs)
        => authority.FallbackHandoffDeadlineUtcMs > 0
            ? Math.Min(deadlineUtcMs, authority.FallbackHandoffDeadlineUtcMs)
            : deadlineUtcMs;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static bool IsFallbackHandoffReservationCurrent(
        FileTransferCoordinatorState state,
        in FileTransferFallbackTailYieldRetryDispatchRecheckCommand command)
        => state.PersistentState.FallbackTailYield.HandoffReservation == command &&
           IsFallbackHandoffSourceCurrent(state, command);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static bool IsFallbackHandoffSourceCurrent(
        FileTransferCoordinatorState state,
        in FileTransferFallbackTailYieldRetryDispatchRecheckCommand command)
        => !command.IsDefault && !state.IsTerminal &&
           state.PersistentState.FallbackTailYield.CurrentOperation is { PairedRelease: { } release } operation &&
           release == command.Hold.Release &&
           IsFallbackTailYieldScopeCurrent(state, GetEffectiveFallbackTailYieldScope(operation), out _) &&
           IsFallbackFrontierRepairAuthorityCurrentForRetryRecheck(
               state, command.Hold.BlockingRepair, command.Hold.DataSessionIdentity,
               command.Hold.DataSessionGeneration);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static bool IsFallbackHandoffCallbackCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        in FileTransferFallbackTailYieldRetryDispatchRecheckCommand? command,
        long observedUtcMs)
    {
        var pending = state.PersistentState.FallbackTailYield.HandoffReservation;
        if (command is null)
        {
            return pending is null || !IsFallbackHandoffReservationCurrent(state, pending.Value);
        }

        return command is { } exact && exact.Request == request &&
               IsFallbackHandoffReservationCurrent(state, exact) &&
               IsFallbackTailYieldRetryDispatchRecheckCommandCurrent(state, exact, out _) &&
               observedUtcMs >= exact.Observation.ObservedUtcMs && observedUtcMs < exact.DeadlineUtcMs;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static FileTransferFallbackTailYieldTransitionDecision ReleaseFallbackHandoffReservation(
        FileTransferCoordinatorState state,
        in FileTransferFallbackTailYieldRetryDispatchRecheckCommand command,
        FallbackHandoffReleaseReason reason,
        long observedUtcMs)
    {
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        var eligible = IsFallbackHandoffReservationCurrent(state, command) &&
            state.PersistentState.FallbackTailYield.CurrentReservation is null &&
            lifecycle.PendingAttempt is null &&
            reason switch
            {
                FallbackHandoffReleaseReason.DeadlineExpired => observedUtcMs >= command.DeadlineUtcMs,
                FallbackHandoffReleaseReason.DispatchFinished =>
                    lifecycle.InstalledAttempt?.Request == command.Request ||
                    lifecycle.LastTerminalAttempt?.Request == command.Request,
                FallbackHandoffReleaseReason.RepairCapacityReached =>
                    true,
                _ => false,
            };
        var decision = eligible
            ? FallbackTailYieldTransition(
                ApplyFallbackTailYieldSubstate(state, state.PersistentState.FallbackTailYield with
                {
                    HandoffReservation = null,
                    LastReleasedHandoffReservation = command,
                    PendingRetryDispatchRecheckCommand = null,
                }),
                FileTransferFallbackTailYieldTransitionStatus.Accepted,
                $"fallback_handoff_{reason}")
            : FallbackTailYieldTransitionRejected(state, "fallback_handoff_release_not_current");
        return FinalizeFallbackTailYieldSnapshot(state, decision,
            nameof(ReleaseFallbackHandoffReservation), new { command.RecheckGeneration, reason, observedUtcMs },
            command.Hold.Release.ReleaseAuthorityId);
    }
}
