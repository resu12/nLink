using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed record DeferredFallbackHandoffRepair(
        FileTransferFallbackTailYieldRetryDispatchRecheckCommand Reservation,
        FileTransferFrontierRequestFrameV6 Request,
        FileTransferTransportKind Transport,
        bool RetainedSparse);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool TryDeferFallbackHandoffRepairLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferTransportKind receivedTransport,
        bool retainedSparse)
    {
        var state = CreateCoordinatorStateLocked(context);
        if (state.PersistentState.FallbackTailYield.HandoffReservation is not { } reservation ||
            !FileTransferCoordinator.IsFallbackHandoffReservationCurrent(state, reservation))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pending = context.DeferredFallbackHandoffRepairs;
        pending.RemoveAll(item => item.Reservation != reservation);
        if (now < reservation.DeadlineUtcMs && pending.Any(item => item.RetainedSparse == retainedSparse &&
                item.Transport == receivedTransport && item.Request == (request with { MissingRanges = item.Request.MissingRanges }) &&
                item.Request.MissingRanges.SequenceEqual(request.MissingRanges)))
        {
            return true;
        }
        if (now >= reservation.DeadlineUtcMs || pending.Count >= FallbackFrontierRepairAliasLimit)
        {
            // Exhausting the existing bounded repair capacity ends the opportunity;
            // it must not discard this request or grow an unbounded side queue.
            ReleaseFallbackHandoffLocked(context, reservation,
                now >= reservation.DeadlineUtcMs
                    ? FallbackHandoffReleaseReason.DeadlineExpired
                    : FallbackHandoffReleaseReason.RepairCapacityReached, now);
            return false;
        }

        // Store immutable wire evidence, not a repair authority. Every entry is
        // revalidated on replay against the coordinator-approved source scope.
        var copy = request with
        {
            MissingRanges = request.MissingRanges.Select(range => range with { }).ToArray(),
        };
        pending.Add(new(reservation, copy, receivedTransport, retainedSparse));
        LocalOperationalLog.Info("FileTransferService",
            $"event=filetransfer_fallback_handoff_repair_deferred; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}recheck_generation={reservation.RecheckGeneration}; deferred_count={pending.Count}; decision_reason=coordinator_handoff_reserved");
        return true;
    }

    private void ScheduleFallbackHandoffExpiry(
        OutboundTransferContext context,
        FileTransferFallbackTailYieldRetryDispatchRecheckCommand reservation)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,
                reservation.DeadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
                .ConfigureAwait(false);
            lock (gate)
            {
                ReleaseFallbackHandoffLocked(context, reservation,
                    FallbackHandoffReleaseReason.DeadlineExpired,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ReleaseFallbackHandoffLocked(
        OutboundTransferContext context,
        FileTransferFallbackTailYieldRetryDispatchRecheckCommand reservation,
        FallbackHandoffReleaseReason reason,
        long observedUtcMs)
    {
        if (!ReferenceEquals(outboundTransfer, context))
        {
            return;
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.IsFallbackHandoffReservationCurrent(state, reservation))
        {
            // Scope replacement/terminal state never authorizes replay into the new scope.
            context.DeferredFallbackHandoffRepairs.RemoveAll(item => item.Reservation == reservation);
            return;
        }

        if (reason is FallbackHandoffReleaseReason.DeadlineExpired or FallbackHandoffReleaseReason.RepairCapacityReached &&
            (reason != FallbackHandoffReleaseReason.DeadlineExpired || observedUtcMs >= reservation.DeadlineUtcMs) &&
            FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state).PendingAttempt is { } attempt &&
            attempt.Request == reservation.Request)
        {
            var invalidation = FileTransferCoordinator.InvalidateRuntimeUnlockRetryDispatchPendingAttempt(
                state, attempt.Authority.AuthorityId, "fallback_handoff_abandoned");
            ApplyRuntimeUnlockRetryLifecycleStateLocked(context, invalidation.State, invalidation.SnapshotEnvelope);
            state = CreateCoordinatorStateLocked(context);
        }

        var release = FileTransferCoordinator.ReleaseFallbackHandoffReservation(
            state, reservation, reason, observedUtcMs);
        _ = AdoptCoordinatorStateLocked(context, release);
        if (!release.Accepted)
        {
            return;
        }

        var deferred = context.DeferredFallbackHandoffRepairs
            .Where(item => item.Reservation == reservation).ToArray();
        context.DeferredFallbackHandoffRepairs.RemoveAll(item => item.Reservation == reservation);
        LocalOperationalLog.Info("FileTransferService",
            $"event=filetransfer_fallback_handoff_released; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}recheck_generation={reservation.RecheckGeneration}; deferred_count={deferred.Length}; decision_reason={reason}");
        foreach (var item in deferred)
        {
            if (!FileTransferCoordinator.IsFallbackHandoffSourceCurrent(CreateCoordinatorStateLocked(context), reservation))
            {
                break;
            }
            if (item.RetainedSparse)
            {
                _ = ApplyOutboundV6RepairRequestCore(context, item.Request, item.Transport);
            }
            else
            {
                _ = ApplyOutboundAuthorizedFallbackFrontierRequest(context, item.Request, item.Transport);
            }
        }
        if (deferred.Length > 0)
        {
            context.SignalSparseSenderPump();
        }
    }
}
