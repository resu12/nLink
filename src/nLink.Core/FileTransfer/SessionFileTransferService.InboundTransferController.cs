using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed class InboundTransferController
    {
        private readonly SessionFileTransferService owner;

        public InboundTransferController(SessionFileTransferService owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public async Task SendWindowUpdateAsync(InboundTransferContext context, WindowUpdateTrigger trigger, CancellationToken ct)
        {
            bool useV3GrantWindow = false;
            bool forceV3Grant = false;
            lock (owner.gate)
            {
                if (ReferenceEquals(owner.inboundTransfer, context) &&
                    !context.IsTerminal &&
                    context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3)
                {
                    useV3GrantWindow = true;
                    forceV3Grant = trigger is WindowUpdateTrigger.Startup or WindowUpdateTrigger.StartupResend or WindowUpdateTrigger.SteadyStateResend;
                }
            }

            if (useV3GrantWindow)
            {
                await owner.SendInboundGrantWindowV3Async(context, forceV3Grant).ConfigureAwait(false);
                return;
            }

            try
            {
                FileTransferWindowUpdateV1? message = null;
                string? triggerReason = null;
                string? phase = null;
                var highestBufferedChunkIndex = -1;
                var creditFrontier = 0;
                lock (owner.gate)
                {
                    WindowUpdateTrigger currentTrigger = default;
                    if (!ReferenceEquals(owner.inboundTransfer, context) ||
                        context.IsTerminal ||
                        context.State is not FileTransferTransferState.Receiving and not FileTransferTransferState.Verifying)
                    {
                        return;
                    }

                    if ((trigger == WindowUpdateTrigger.LowWatermark ||
                         trigger == WindowUpdateTrigger.BufferedFrontier ||
                         trigger == WindowUpdateTrigger.GapProgressAck) &&
                        !TryGetWindowUpdateRefreshTriggerLocked(context, out currentTrigger))
                    {
                        return;
                    }

                    if ((trigger == WindowUpdateTrigger.LowWatermark ||
                         trigger == WindowUpdateTrigger.BufferedFrontier ||
                         trigger == WindowUpdateTrigger.GapProgressAck) &&
                        currentTrigger != trigger)
                    {
                        return;
                    }

                    UpdateInboundDegradedRepairModeLocked(context);
                    UpdateInboundBulkHealthLocked(context);
                    highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
                    creditFrontier = GetCreditFrontierLocked(context, highestBufferedChunkIndex);
                    var grantedUntilExclusive = GetTargetGrantedUntilExclusiveLocked(context, trigger, creditFrontier);
                    message = new FileTransferWindowUpdateV1
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        NextExpectedChunkIndex = context.NextChunkIndex,
                        GrantedUntilChunkIndexExclusive = grantedUntilExclusive,
                        BytesReceived = context.BytesTransferred,
                    };
                    context.LastAdvertisedGrantedUntilExclusive = grantedUntilExclusive;
                    context.LastAdvertisedNextChunkIndex = context.NextChunkIndex;
                    context.LastAdvertisedCreditFrontier = creditFrontier;
                    context.LastWindowUpdateSentUtc = DateTimeOffset.UtcNow;
                    if (trigger is WindowUpdateTrigger.StartupResend or WindowUpdateTrigger.SteadyStateResend)
                    {
                        context.LastForcedWindowUpdateSentUtc = context.LastWindowUpdateSentUtc;
                    }

                    if (trigger is not WindowUpdateTrigger.Startup and
                        not WindowUpdateTrigger.StartupResend and
                        not WindowUpdateTrigger.GapProgressAck)
                    {
                        context.SteadyStateWindowAdvertised = true;
                    }

                    if (trigger == WindowUpdateTrigger.GapProgressAck)
                    {
                        context.RecentGapProgressAckSentUtc.Enqueue(context.LastWindowUpdateSentUtc.Value);
                    }

                    triggerReason = trigger switch
                    {
                        WindowUpdateTrigger.Startup => "startup",
                        WindowUpdateTrigger.StartupResend => "startup_resend",
                        WindowUpdateTrigger.GapProgressAck => "gap_progress_ack",
                        WindowUpdateTrigger.BufferedFrontier => "buffered_frontier",
                        WindowUpdateTrigger.SteadyStateResend => "watchdog_forced",
                        _ => "low_watermark",
                    };
                    phase = trigger is WindowUpdateTrigger.Startup or WindowUpdateTrigger.StartupResend
                        ? "startup"
                        : "steady_state";
                }

                var currentTransport = owner.GetTransportOrThrow();
                await currentTransport.SendFileTransferWindowUpdateAsync(message!, ct).ConfigureAwait(false);
                SessionFileTransferService.LogWindowUpdateSent(message!, phase!, triggerReason!, highestBufferedChunkIndex, creditFrontier);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SessionFileTransferService.Warn($"window_update send failed: {ex.Message}");
            }
        }

        public async Task RunInboundWindowRefreshWatchdogAsync(InboundTransferContext context)
        {
            try
            {
                while (!context.LifetimeCts.IsCancellationRequested)
                {
                    await Task.Delay(WindowUpdateWatchdogDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                    WindowUpdateTrigger? trigger = null;
                    lock (owner.gate)
                    {
                        if (ReferenceEquals(owner.inboundTransfer, context) &&
                            !context.IsTerminal &&
                            context.State == FileTransferTransferState.Receiving)
                        {
                            UpdateInboundBulkHealthLocked(context);
                            if (TryGetWindowUpdateRefreshTriggerLocked(context, out var refreshTrigger))
                            {
                                trigger = refreshTrigger;
                            }
                            else if (TryGetWatchdogWindowUpdateTriggerLocked(context, out var watchdogTrigger))
                            {
                                trigger = watchdogTrigger;
                            }
                        }
                    }

                    if (trigger is null)
                    {
                        lock (owner.gate)
                        {
                            if (!ReferenceEquals(owner.inboundTransfer, context) ||
                                context.IsTerminal ||
                                context.State != FileTransferTransferState.Receiving)
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    await MaybeSendPressureStateAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
                    await SendWindowUpdateAsync(context, trigger.Value, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task RunInboundGapRecoveryWatchdogAsync(InboundTransferContext context)
        {
            try
            {
                while (!context.LifetimeCts.IsCancellationRequested)
                {
                    await Task.Delay(MissingRangeWatchdogDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                    var shouldSendMissingRange = false;
                    lock (owner.gate)
                    {
                        if (ReferenceEquals(owner.inboundTransfer, context) &&
                            !context.IsTerminal &&
                            context.State == FileTransferTransferState.Receiving)
                        {
                            UpdateInboundBulkHealthLocked(context);
                            shouldSendMissingRange = ShouldRequestMissingRangeLocked(context);
                        }
                    }

                    if (!shouldSendMissingRange)
                    {
                        lock (owner.gate)
                        {
                            if (!ReferenceEquals(owner.inboundTransfer, context) ||
                                context.IsTerminal ||
                                context.State != FileTransferTransferState.Receiving)
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    await SendMissingRangeAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task SendMissingRangeAsync(InboundTransferContext context, CancellationToken ct)
        {
            try
            {
                FileTransferMissingRangeV1? message = null;
                int nextExpectedChunkIndex;
                int highestBufferedChunkIndex;
                bool shouldLogDeferredGapExtension = false;
                int deferredGapGrantedUntilExclusive = 0;
                lock (owner.gate)
                {
                    if (!ReferenceEquals(owner.inboundTransfer, context) ||
                        context.IsTerminal ||
                        context.State != FileTransferTransferState.Receiving)
                    {
                        return;
                    }

                    if (!TryBuildMissingRangeLocked(context, out message))
                    {
                        return;
                    }

                    nextExpectedChunkIndex = context.NextChunkIndex;
                    highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
                    if (highestBufferedChunkIndex >= nextExpectedChunkIndex &&
                        ShouldLogGapDeferredLocked(context))
                    {
                        shouldLogDeferredGapExtension = true;
                        deferredGapGrantedUntilExclusive = context.LastAdvertisedGrantedUntilExclusive;
                    }
                }

                var currentTransport = owner.GetTransportOrThrow();
                await currentTransport.SendFileTransferMissingRangeAsync(message!, ct).ConfigureAwait(false);
                if (shouldLogDeferredGapExtension)
                {
                    SessionFileTransferService.LogWindowExtensionDeferredDueToGap(
                        context.TransferId,
                        context.SessionId,
                        nextExpectedChunkIndex,
                        highestBufferedChunkIndex,
                        deferredGapGrantedUntilExclusive);
                }

                SessionFileTransferService.LogMissingRangeSent(message!, nextExpectedChunkIndex, highestBufferedChunkIndex);
                await MaybeSendPressureStateAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SessionFileTransferService.Warn($"missing_range send failed: {ex.Message}");
            }
        }

        public bool TryGetWindowUpdateRefreshTriggerLocked(InboundTransferContext context, out WindowUpdateTrigger trigger)
        {
            trigger = default;

            var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
            var creditFrontier = GetCreditFrontierLocked(context, highestBufferedChunkIndex);
            var rawTargetGrantedUntilExclusive = GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier);
            var shouldDeferGrantExtension = ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, rawTargetGrantedUntilExclusive);
            if (shouldDeferGrantExtension)
            {
                if (context.LastAdvertisedGrantedUntilExclusive > 0 &&
                    context.NextChunkIndex > context.LastAdvertisedNextChunkIndex)
                {
                    trigger = WindowUpdateTrigger.GapProgressAck;
                    return true;
                }

                if (ShouldLogGapDeferredLocked(context))
                {
                    SessionFileTransferService.LogWindowExtensionDeferredDueToGap(
                        context.TransferId,
                        context.SessionId,
                        context.NextChunkIndex,
                        highestBufferedChunkIndex,
                        context.LastAdvertisedGrantedUntilExclusive);
                }

                return false;
            }

            if (!context.StartupPhaseCompleted)
            {
                return false;
            }

            var targetGrantedUntilExclusive = GetTargetGrantedUntilExclusiveLocked(context, WindowUpdateTrigger.BufferedFrontier, creditFrontier);
            if (!context.SteadyStateWindowAdvertised &&
                targetGrantedUntilExclusive > context.LastAdvertisedGrantedUntilExclusive)
            {
                trigger = context.BulkFallbackModeActive
                    ? WindowUpdateTrigger.LowWatermark
                    : WindowUpdateTrigger.BufferedFrontier;
                return true;
            }

            var hasDelta =
                context.NextChunkIndex > context.LastAdvertisedNextChunkIndex ||
                (!context.BulkFallbackModeActive && creditFrontier > context.LastAdvertisedCreditFrontier) ||
                targetGrantedUntilExclusive > context.LastAdvertisedGrantedUntilExclusive;
            if (!hasDelta)
            {
                return false;
            }

            var remainingContiguousRunway = context.LastAdvertisedGrantedUntilExclusive - context.NextChunkIndex;
            var lowWatermarkChunks = GetEffectiveLowWatermarkChunksLocked(context);
            if (remainingContiguousRunway <= lowWatermarkChunks)
            {
                trigger = WindowUpdateTrigger.LowWatermark;
                return true;
            }

            if (context.BulkFallbackModeActive)
            {
                return false;
            }

            var remainingFrontierRunway = context.LastAdvertisedGrantedUntilExclusive - creditFrontier;
            if (creditFrontier > context.NextChunkIndex &&
                remainingFrontierRunway <= lowWatermarkChunks)
            {
                trigger = WindowUpdateTrigger.BufferedFrontier;
                return true;
            }

            return false;
        }

        public bool TryGetWatchdogWindowUpdateTriggerLocked(InboundTransferContext context, out WindowUpdateTrigger trigger)
        {
            trigger = default;
            if (context.LastWindowUpdateSentUtc is null)
            {
                return false;
            }

            if (TryGetWindowUpdateRefreshTriggerLocked(context, out _))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var forcedResendMinIntervalMs =
                context.LocalPressureMode == FileTransferPressureMode.CatchUpOnly &&
                context.LastAdvertisedNextChunkIndex == context.NextChunkIndex &&
                context.LastAdvertisedGrantedUntilExclusive > 0
                    ? PressuredWatchdogResendMinIntervalMs
                    : WindowUpdateWatchdogDelayMs;
            if (context.LastForcedWindowUpdateSentUtc is not null &&
                now - context.LastForcedWindowUpdateSentUtc.Value < TimeSpan.FromMilliseconds(forcedResendMinIntervalMs))
            {
                return false;
            }

            if (!context.StartupPhaseCompleted)
            {
                if (now - context.LastWindowUpdateSentUtc.Value < TimeSpan.FromMilliseconds(WindowUpdateWatchdogDelayMs))
                {
                    return false;
                }

                trigger = WindowUpdateTrigger.StartupResend;
                return true;
            }

            var lastActivityUtc = MaxDateTimeOffset(
                context.LastContiguousProgressUtc,
                context.LastBufferedFrontierAdvanceUtc,
                context.LastWindowUpdateSentUtc);
            if (now - lastActivityUtc < TimeSpan.FromMilliseconds(WindowUpdateWatchdogDelayMs))
            {
                return false;
            }

            trigger = WindowUpdateTrigger.SteadyStateResend;
            return true;
        }

        public void UpdateOldestGapTrackingLocked(InboundTransferContext context)
        {
            if (context.PendingChunks.Count == 0)
            {
                context.OldestGapStartChunkIndex = null;
                context.OldestGapFirstSeenUtc = null;
                context.OutstandingMissingRange = null;
                context.LastMissingRangeHighestBufferedChunkIndex = -1;
                context.GapFreeSinceUtc ??= DateTimeOffset.UtcNow;
                return;
            }

            var firstBufferedChunkIndex = context.PendingChunks.Keys.Min();
            if (firstBufferedChunkIndex <= context.NextChunkIndex)
            {
                context.OldestGapStartChunkIndex = null;
                context.OldestGapFirstSeenUtc = null;
                context.OutstandingMissingRange = null;
                context.LastMissingRangeHighestBufferedChunkIndex = -1;
                context.GapFreeSinceUtc ??= DateTimeOffset.UtcNow;
                return;
            }

            if (context.OldestGapStartChunkIndex == context.NextChunkIndex)
            {
                return;
            }

            context.OldestGapStartChunkIndex = context.NextChunkIndex;
            context.OldestGapFirstSeenUtc = DateTimeOffset.UtcNow;
            context.OutstandingMissingRange = null;
            context.LastMissingRangeSentUtc = null;
            context.LastMissingRangeHighestBufferedChunkIndex = -1;
            context.GapFreeSinceUtc = null;
        }

        public void UpdateInboundDegradedRepairModeLocked(InboundTransferContext context)
        {
            var now = DateTimeOffset.UtcNow;
            while (context.RecentMissingRangeSentUtc.Count > 0 &&
                   now - context.RecentMissingRangeSentUtc.Peek() > TimeSpan.FromMilliseconds(DegradedRepairMissingRangeBurstWindowMs))
            {
                context.RecentMissingRangeSentUtc.Dequeue();
            }

            if (context.OldestGapStartChunkIndex is null)
            {
                context.GapFreeSinceUtc ??= now;
                if (context.DegradedRepairModeActive &&
                    now - context.GapFreeSinceUtc.Value >= TimeSpan.FromMilliseconds(DegradedRepairRecoveryHoldMs))
                {
                    context.DegradedRepairModeActive = false;
                    context.RecentMissingRangeSentUtc.Clear();
                }

                return;
            }

            context.GapFreeSinceUtc = null;
            if (context.DegradedRepairModeActive)
            {
                return;
            }

            var persistentGap =
                context.OldestGapFirstSeenUtc is not null &&
                now - context.OldestGapFirstSeenUtc.Value >= TimeSpan.FromMilliseconds(DegradedRepairGapPersistenceMs);
            var burstMissingRanges = context.RecentMissingRangeSentUtc.Count >= DegradedRepairMissingRangeBurstThreshold;
            if (persistentGap || burstMissingRanges)
            {
                context.DegradedRepairModeActive = true;
            }
        }

        public void RecordInboundUsefulBulkProgressLocked(InboundTransferContext context, DateTimeOffset now, bool clearGapState)
        {
            context.LastUsefulBulkProgressUtc = now;
            context.BulkDispatchedChunksSinceLastUsefulProgress = 0;
            context.ObsoleteChunksArrivedSinceLastUsefulProgress = 0;
            context.ConsecutiveBulkUnhealthyDetections = 0;
            if (clearGapState)
            {
                context.BulkUnhealthyDetected = false;
            }
        }

        public void RecordInboundContiguousProgressLocked(InboundTransferContext context, DateTimeOffset now, int contiguousProgressChunkCount)
        {
            for (var index = 0; index < contiguousProgressChunkCount; index++)
            {
                context.RecentContiguousProgressChunkUtc.Enqueue(now);
            }
        }

        public void UpdateInboundBulkHealthLocked(InboundTransferContext context)
        {
            var now = DateTimeOffset.UtcNow;
            while (context.RecentMissingRangeSentUtc.Count > 0 &&
                   now - context.RecentMissingRangeSentUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
            {
                context.RecentMissingRangeSentUtc.Dequeue();
            }

            while (context.RecentGapProgressAckSentUtc.Count > 0 &&
                   now - context.RecentGapProgressAckSentUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
            {
                context.RecentGapProgressAckSentUtc.Dequeue();
            }

            while (context.RecentContiguousProgressChunkUtc.Count > 0 &&
                   now - context.RecentContiguousProgressChunkUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
            {
                context.RecentContiguousProgressChunkUtc.Dequeue();
            }

            while (context.RecentObsoleteChunkArrivalUtc.Count > 0 &&
                   now - context.RecentObsoleteChunkArrivalUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
            {
                context.RecentObsoleteChunkArrivalUtc.Dequeue();
            }

            var lastUsefulProgressUtc = MaxDateTimeOffset(context.LastUsefulBulkProgressUtc, context.LastContiguousProgressUtc, context.LastBufferedFrontierAdvanceUtc);
            var recentBulkDispatch =
                context.LastBulkDispatchedChunkUtc is not null &&
                now - context.LastBulkDispatchedChunkUtc.Value <= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs);
            var noUsefulProgress = now - lastUsefulProgressUtc >= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs);
            var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
            var stagnantBufferedFrontier =
                highestBufferedChunkIndex <= context.NextChunkIndex ||
                context.LastBufferedFrontierAdvanceUtc is null ||
                now - context.LastBufferedFrontierAdvanceUtc.Value >= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs);
            var repeatedGapSignals =
                context.OldestGapStartChunkIndex is not null ||
                context.RecentMissingRangeSentUtc.Count > 0 ||
                context.RecentGapProgressAckSentUtc.Count > 0;
            var recentContiguousProgressChunks = context.RecentContiguousProgressChunkUtc.Count;
            var sustainedRecoveryChurn =
                context.RecentMissingRangeSentUtc.Count >= BulkUnhealthyDetectionConfirmations ||
                context.RecentGapProgressAckSentUtc.Count >= BulkUnhealthyDetectionConfirmations;
            var repairChurnUnhealthy =
                context.OldestGapStartChunkIndex is not null &&
                context.RecentMissingRangeSentUtc.Count >= RepairChurnMissingRangeThreshold &&
                recentContiguousProgressChunks <= RepairChurnMaxHealthyProgressChunks;
            var unhealthy =
                repeatedGapSignals &&
                (
                    (
                        noUsefulProgress &&
                        stagnantBufferedFrontier &&
                        (
                            (recentBulkDispatch && context.BulkDispatchedChunksSinceLastUsefulProgress > 0) ||
                            sustainedRecoveryChurn
                        )
                    ) ||
                    repairChurnUnhealthy
                );

            if (unhealthy)
            {
                context.ConsecutiveBulkUnhealthyDetections++;
                if (context.LastBulkUnhealthyLogUtc is null ||
                    now - context.LastBulkUnhealthyLogUtc.Value >= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs))
                {
                    context.LastBulkUnhealthyLogUtc = now;
                    context.BulkUnhealthyDetected = true;
                    SessionFileTransferService.LogBulkUnhealthyDetected(
                        context.TransferId,
                        context.SessionId,
                        context.NextChunkIndex,
                        highestBufferedChunkIndex,
                        context.LastAdvertisedGrantedUntilExclusive,
                        context.BulkDispatchedChunksSinceLastUsefulProgress,
                        context.ObsoleteChunksArrivedSinceLastUsefulProgress,
                        context.RecentObsoleteChunkArrivalUtc.Count,
                        context.RecentMissingRangeSentUtc.Count,
                        context.RecentGapProgressAckSentUtc.Count,
                        recentContiguousProgressChunks);
                }

                if (!context.BulkFallbackModeActive &&
                    context.ConsecutiveBulkUnhealthyDetections >= BulkUnhealthyDetectionConfirmations)
                {
                    context.BulkFallbackModeActive = true;
                    SessionFileTransferService.LogBulkFallbackEntered(
                        context.TransferId,
                        context.SessionId,
                        context.NextChunkIndex,
                        highestBufferedChunkIndex,
                        context.LastAdvertisedGrantedUntilExclusive);
                }

                return;
            }

            context.ConsecutiveBulkUnhealthyDetections = 0;
            if (context.BulkUnhealthyDetected)
            {
                context.BulkUnhealthyDetected = false;
                SessionFileTransferService.LogBulkHealthyResumed(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    highestBufferedChunkIndex,
                    context.LastAdvertisedGrantedUntilExclusive);
            }

            if (context.BulkFallbackModeActive &&
                context.OldestGapStartChunkIndex is null &&
                context.GapFreeSinceUtc is not null &&
                now - context.GapFreeSinceUtc.Value >= TimeSpan.FromMilliseconds(BulkFallbackRecoveryHoldMs) &&
                context.RecentMissingRangeSentUtc.Count == 0)
            {
                context.BulkFallbackModeActive = false;
                SessionFileTransferService.LogBulkFallbackExited(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    highestBufferedChunkIndex,
                    context.LastAdvertisedGrantedUntilExclusive);
            }
        }

        public async Task MaybeSendPressureStateAsync(InboundTransferContext context, CancellationToken ct)
        {
            try
            {
                FileTransferPressureStateV1? message = null;
                bool pressureStateChanged = false;
                SessionFileTransferSnapshot? snapshot = null;

                lock (owner.gate)
                {
                    if (!ReferenceEquals(owner.inboundTransfer, context) ||
                        context.IsTerminal ||
                        context.State is not FileTransferTransferState.Receiving and not FileTransferTransferState.Verifying)
                    {
                        return;
                    }

                    UpdateInboundBulkHealthLocked(context);
                    pressureStateChanged = TryTransitionInboundPressureStateLocked(context, out message);
                    if (pressureStateChanged)
                    {
                        snapshot = owner.CreateSnapshotLocked();
                    }
                }

                if (message is null)
                {
                    return;
                }

                var currentTransport = owner.GetTransportOrThrow();
                await currentTransport.SendFileTransferPressureStateAsync(message, ct).ConfigureAwait(false);
                SessionFileTransferService.LogPressureStateSent(message);
                lock (owner.gate)
                {
                    if (ReferenceEquals(owner.inboundTransfer, context) && !context.IsTerminal)
                    {
                        context.LastPressureStateSentUtc = DateTimeOffset.UtcNow;
                        context.LastPressureStateSentMode = context.LocalPressureMode;
                        context.LastPressureStateSentReason = context.LocalPressureReason;
                        context.LastPressureStateSentSuggestedSendAheadChunks = context.LocalPressureSuggestedSendAheadChunks;
                        context.LastPressureStateSentReceiverNextExpectedChunkIndex = context.LocalPressureReceiverNextExpectedChunkIndex;
                        context.LastPressureStateSentProfileName = owner.ResolveInboundV3ProfileName(context);
                    }
                }

                if (pressureStateChanged && snapshot is not null)
                {
                    owner.RaiseTransferChanged(snapshot);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SessionFileTransferService.Warn($"pressure_state send failed: {ex.Message}");
            }
        }

        public bool TryTransitionInboundPressureStateLocked(InboundTransferContext context, out FileTransferPressureStateV1? message)
        {
            message = null;
            var now = DateTimeOffset.UtcNow;
            var obsoleteChunkCountRecent = context.RecentObsoleteChunkArrivalUtc.Count;
            var missingRangeCountRecent = context.RecentMissingRangeSentUtc.Count;
            var obsoleteChunkArrivalRatio = context.BulkDispatchedChunksSinceLastUsefulProgress <= 0
                ? 0D
                : (double)context.ObsoleteChunksArrivedSinceLastUsefulProgress / context.BulkDispatchedChunksSinceLastUsefulProgress;
            var mediaProtection = owner.sessionScreenShareActive && owner.sessionScreenShareDegraded;
            var staleBulkBacklog =
                obsoleteChunkCountRecent >= PressureStateObsoleteChunkThreshold &&
                obsoleteChunkArrivalRatio >= PressureStateObsoleteChunkRatioThreshold;
            var noContiguousProgress =
                context.LastContiguousProgressUtc is null ||
                now - context.LastContiguousProgressUtc.Value >= TimeSpan.FromMilliseconds(PressureStateNoProgressWindowMs);
            var stalledBulkBacklog =
                context.BulkDispatchedChunksSinceLastUsefulProgress >= PressureStateBulkDispatchThreshold &&
                noContiguousProgress;
            var catchUpRequested = mediaProtection || staleBulkBacklog || stalledBulkBacklog;
            if (context.LocalPressureMode == FileTransferPressureMode.CatchUpOnly && !catchUpRequested)
            {
                var cleanForRecovery =
                    context.OldestGapStartChunkIndex is null &&
                    obsoleteChunkCountRecent == 0 &&
                    context.BulkDispatchedChunksSinceLastUsefulProgress <= 1 &&
                    !mediaProtection;
                if (!cleanForRecovery)
                {
                    context.PressureRecoverySinceUtc = null;
                    catchUpRequested = true;
                }
                else
                {
                    context.PressureRecoverySinceUtc ??= now;
                    catchUpRequested = now - context.PressureRecoverySinceUtc.Value < TimeSpan.FromMilliseconds(PressureStateRecoveryHoldMs);
                }
            }
            else if (catchUpRequested)
            {
                context.PressureRecoverySinceUtc = null;
            }
            else
            {
                context.PressureRecoverySinceUtc = null;
            }

            var desiredMode = catchUpRequested ? FileTransferPressureMode.CatchUpOnly : FileTransferPressureMode.Normal;
            var desiredReason = catchUpRequested
                ? mediaProtection
                    ? FileTransferPressureReason.MediaProtection
                    : context.OldestGapStartChunkIndex is not null
                        ? FileTransferPressureReason.GapRepair
                        : FileTransferPressureReason.BulkBacklog
                : FileTransferPressureReason.BulkBacklog;
            var desiredSuggestedSendAheadChunks = desiredMode == FileTransferPressureMode.CatchUpOnly
                ? owner.sessionScreenShareActive
                    ? PressureStateCatchUpOnlySuggestedSendAheadWhileScreenshareChunks
                    : PressureStateCatchUpOnlySuggestedSendAheadChunks
                : PressureStateNormalSuggestedSendAheadChunks;
            var desiredReceiverNextExpectedChunkIndex = context.NextChunkIndex;
            if (context.LocalPressureMode == desiredMode &&
                context.LocalPressureSuggestedSendAheadChunks == desiredSuggestedSendAheadChunks &&
                context.LocalPressureReceiverNextExpectedChunkIndex == desiredReceiverNextExpectedChunkIndex)
            {
                context.LocalPressureReason = desiredReason;
                return false;
            }

            var previousMode = context.LocalPressureMode;
            context.LocalPressureMode = desiredMode;
            context.LocalPressureReason = desiredReason;
            context.LocalPressureSuggestedSendAheadChunks = desiredSuggestedSendAheadChunks;
            context.LocalPressureReceiverNextExpectedChunkIndex = desiredReceiverNextExpectedChunkIndex;
            context.LocalPressureRevision++;
            if (desiredMode == FileTransferPressureMode.CatchUpOnly)
            {
                context.PressureRecoverySinceUtc = null;
            }

            if (previousMode != desiredMode)
            {
                if (desiredMode == FileTransferPressureMode.CatchUpOnly)
                {
                    SessionFileTransferService.LogBulkCatchUpOnlyEntered(
                        context.TransferId,
                        context.SessionId,
                        context.NextChunkIndex,
                        GetCurrentHighestBufferedChunkIndexLocked(context),
                        obsoleteChunkCountRecent,
                        obsoleteChunkArrivalRatio,
                        missingRangeCountRecent,
                        context.RecentContiguousProgressChunkUtc.Count,
                        desiredReason);
                }
                else
                {
                    SessionFileTransferService.LogBulkCatchUpOnlyExited(
                        context.TransferId,
                        context.SessionId,
                        context.NextChunkIndex,
                        GetCurrentHighestBufferedChunkIndexLocked(context),
                        desiredSuggestedSendAheadChunks);
                }
            }

            if (ShouldSuppressV3PressureStateLocked(
                    context,
                    desiredMode,
                    desiredReason,
                    desiredSuggestedSendAheadChunks,
                    desiredReceiverNextExpectedChunkIndex,
                    now))
            {
                return false;
            }

            message = new FileTransferPressureStateV1
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Revision = context.LocalPressureRevision,
                Mode = SessionFileTransferService.FormatPressureMode(desiredMode),
                SuggestedSendAheadChunks = desiredSuggestedSendAheadChunks,
                ReceiverNextExpectedChunkIndex = desiredReceiverNextExpectedChunkIndex,
                Reason = SessionFileTransferService.FormatPressureReason(desiredReason),
            };
            return true;
        }

        private bool ShouldSuppressV3PressureStateLocked(
            InboundTransferContext context,
            FileTransferPressureMode desiredMode,
            FileTransferPressureReason desiredReason,
            int desiredSuggestedSendAheadChunks,
            int desiredReceiverNextExpectedChunkIndex,
            DateTimeOffset now)
        {
            var currentProfileName = owner.ResolveInboundV3ProfileName(context);
            if (context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3 ||
                desiredMode != FileTransferPressureMode.Normal ||
                desiredReason != FileTransferPressureReason.BulkBacklog ||
                context.LastPressureStateSentUtc is null ||
                context.LastPressureStateSentMode != desiredMode ||
                context.LastPressureStateSentReason != desiredReason ||
                context.LastPressureStateSentSuggestedSendAheadChunks != desiredSuggestedSendAheadChunks ||
                !string.Equals(context.LastPressureStateSentProfileName, currentProfileName, StringComparison.Ordinal))
            {
                return false;
            }

            var recentEnough =
                now - context.LastPressureStateSentUtc.Value < TimeSpan.FromMilliseconds(PullV3PressureStateSuppressionMs);
            var progressDeltaChunks =
                Math.Abs(desiredReceiverNextExpectedChunkIndex - context.LastPressureStateSentReceiverNextExpectedChunkIndex);
            var suppressionThreshold = currentProfileName switch
            {
                "degraded" => PullV3PressureStateDegradedProgressDeltaChunks,
                "balanced_screenshare" => PullV3PressureStateBalancedProgressDeltaChunks,
                _ => PullV3PressureStateHealthyProgressDeltaChunks,
            };
            return recentEnough && progressDeltaChunks < suppressionThreshold;
        }

        public void RecordMissingRangeSentLocked(InboundTransferContext context)
        {
            var now = DateTimeOffset.UtcNow;
            context.RecentMissingRangeSentUtc.Enqueue(now);
            while (context.RecentMissingRangeSentUtc.Count > 0 &&
                   now - context.RecentMissingRangeSentUtc.Peek() > TimeSpan.FromMilliseconds(Math.Max(DegradedRepairMissingRangeBurstWindowMs, RepairChurnDetectionWindowMs)))
            {
                context.RecentMissingRangeSentUtc.Dequeue();
            }

            UpdateInboundDegradedRepairModeLocked(context);
        }

        public void RefreshHighestBufferedChunkIndexLocked(InboundTransferContext context)
        {
            context.HighestBufferedChunkIndex = context.PendingChunks.Count == 0
                ? context.NextChunkIndex - 1
                : Math.Max(context.NextChunkIndex - 1, context.PendingChunks.Keys.Max());
        }

        public int GetCreditFrontierLocked(InboundTransferContext context, int highestBufferedChunkIndex)
        {
            if (context.BulkFallbackModeActive)
            {
                return context.NextChunkIndex;
            }

            var bufferedExclusive = Math.Max(context.NextChunkIndex, highestBufferedChunkIndex + 1);
            return Math.Min(bufferedExclusive, context.NextChunkIndex + GetEffectiveGrantChunksLocked(context));
        }

        public int GetRawTargetGrantedUntilExclusiveLocked(InboundTransferContext context, int creditFrontier)
        {
            if (!context.StartupPhaseCompleted)
            {
                return Math.Min(context.ChunkCount, context.NextChunkIndex + GetEffectiveStartupGrantChunksLocked());
            }

            var effectiveGrantChunks = GetEffectiveGrantChunksLocked(context);
            return Math.Min(context.ChunkCount, creditFrontier + effectiveGrantChunks);
        }

        public int GetTargetGrantedUntilExclusiveLocked(InboundTransferContext context, WindowUpdateTrigger trigger, int creditFrontier)
        {
            if (trigger is WindowUpdateTrigger.Startup or WindowUpdateTrigger.StartupResend || !context.StartupPhaseCompleted)
            {
                return Math.Min(context.ChunkCount, context.NextChunkIndex + GetEffectiveStartupGrantChunksLocked());
            }

            if (trigger == WindowUpdateTrigger.GapProgressAck)
            {
                return context.LastAdvertisedGrantedUntilExclusive;
            }

            var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
            if (ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier)) &&
                context.LastAdvertisedGrantedUntilExclusive > 0)
            {
                return context.LastAdvertisedGrantedUntilExclusive;
            }

            if (context.BulkFallbackModeActive)
            {
                var fallbackCap = Math.Min(context.ChunkCount, context.NextChunkIndex + BulkFallbackGrantChunks);
                return Math.Max(context.LastAdvertisedGrantedUntilExclusive, fallbackCap);
            }

            if (context.DegradedRepairModeActive)
            {
                var degradedCap = Math.Min(context.ChunkCount, context.NextChunkIndex + DegradedRepairGrantChunks);
                return Math.Max(context.LastAdvertisedGrantedUntilExclusive, degradedCap);
            }

            return GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier);
        }

        public DateTimeOffset MaxDateTimeOffset(params DateTimeOffset?[] values)
        {
            DateTimeOffset? max = null;
            foreach (var value in values)
            {
                if (value is null)
                {
                    continue;
                }

                if (max is null || value.Value > max.Value)
                {
                    max = value.Value;
                }
            }

            return max ?? DateTimeOffset.UtcNow;
        }

        public int GetCurrentHighestBufferedChunkIndexLocked(InboundTransferContext context)
        {
            return context.PendingChunks.Count == 0
                ? context.NextChunkIndex - 1
                : Math.Max(context.NextChunkIndex - 1, context.HighestBufferedChunkIndex);
        }

        public int GetEffectiveGrantChunksLocked(InboundTransferContext context)
            => context.BulkFallbackModeActive
                ? BulkFallbackGrantChunks
                : context.DegradedRepairModeActive
                    ? Math.Min(owner.flowControlPolicy.GrantChunks, DegradedRepairGrantChunks)
                    : owner.flowControlPolicy.GrantChunks;

        public int GetEffectiveStartupGrantChunksLocked()
            => owner.sessionScreenShareActive
                ? Math.Min(owner.flowControlPolicy.StartupGrantChunks, ScreenShareActiveStartupGrantCapChunks)
                : owner.flowControlPolicy.StartupGrantChunks;

        public int GetEffectiveLowWatermarkChunksLocked(InboundTransferContext context)
            => context.BulkFallbackModeActive
                ? BulkFallbackLowWatermarkChunks
                : context.DegradedRepairModeActive
                    ? DegradedRepairLowWatermarkChunks
                    : owner.flowControlPolicy.LowWatermarkChunks;

        public bool ShouldRequestMissingRangeLocked(InboundTransferContext context)
        {
            if (context.PendingChunks.Count == 0 ||
                context.OldestGapStartChunkIndex is null ||
                context.OldestGapFirstSeenUtc is null)
            {
                return false;
            }

            if (context.OutstandingMissingRange is not null &&
                context.LastMissingRangeSentUtc is not null)
            {
                var now = DateTimeOffset.UtcNow;
                var sinceLastMissingRange = now - context.LastMissingRangeSentUtc.Value;
                if (sinceLastMissingRange < TimeSpan.FromMilliseconds(MissingRangeCooldownMs))
                {
                    return false;
                }

                if (sinceLastMissingRange < TimeSpan.FromMilliseconds(RepeatedMissingRangeMinIntervalMs))
                {
                    var gapStartChunkIndex = context.OldestGapStartChunkIndex.Value;
                    var firstBufferedChunkIndex = context.PendingChunks.Keys.Min();
                    var nextRange = new MissingRange(
                        gapStartChunkIndex,
                        Math.Min(firstBufferedChunkIndex, gapStartChunkIndex + MissingRangeMaxChunks));
                    var narrowedRange =
                        nextRange.StartChunkIndex >= context.OutstandingMissingRange.Value.StartChunkIndex &&
                        nextRange.EndChunkIndexExclusive <= context.OutstandingMissingRange.Value.EndChunkIndexExclusive &&
                        (nextRange.StartChunkIndex > context.OutstandingMissingRange.Value.StartChunkIndex ||
                         nextRange.EndChunkIndexExclusive < context.OutstandingMissingRange.Value.EndChunkIndexExclusive);
                    var highestBufferedAdvanced = GetCurrentHighestBufferedChunkIndexLocked(context) > context.LastMissingRangeHighestBufferedChunkIndex;
                    if (!narrowedRange && !highestBufferedAdvanced)
                    {
                        return false;
                    }
                }
            }

            return DateTimeOffset.UtcNow - context.OldestGapFirstSeenUtc.Value >= TimeSpan.FromMilliseconds(MissingRangeTriggerDelayMs);
        }

        public bool TryBuildMissingRangeLocked(InboundTransferContext context, out FileTransferMissingRangeV1 message)
        {
            message = default!;
            if (context.PendingChunks.Count == 0 || context.OldestGapStartChunkIndex is null)
            {
                return false;
            }

            var firstBufferedChunkIndex = context.PendingChunks.Keys.Min();
            var gapStartChunkIndex = context.OldestGapStartChunkIndex.Value;
            if (firstBufferedChunkIndex <= gapStartChunkIndex)
            {
                return false;
            }

            var endChunkIndexExclusive = Math.Min(firstBufferedChunkIndex, gapStartChunkIndex + MissingRangeMaxChunks);
            if (endChunkIndexExclusive <= gapStartChunkIndex)
            {
                return false;
            }

            var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
            var range = new MissingRange(gapStartChunkIndex, endChunkIndexExclusive);
            var narrowedRange =
                context.OutstandingMissingRange is not null &&
                range.StartChunkIndex >= context.OutstandingMissingRange.Value.StartChunkIndex &&
                range.EndChunkIndexExclusive <= context.OutstandingMissingRange.Value.EndChunkIndexExclusive &&
                (range.StartChunkIndex > context.OutstandingMissingRange.Value.StartChunkIndex ||
                 range.EndChunkIndexExclusive < context.OutstandingMissingRange.Value.EndChunkIndexExclusive);
            var highestBufferedAdvanced = highestBufferedChunkIndex > context.LastMissingRangeHighestBufferedChunkIndex;
            if (context.OutstandingMissingRange == range &&
                !highestBufferedAdvanced &&
                context.LastMissingRangeSentUtc is not null &&
                DateTimeOffset.UtcNow - context.LastMissingRangeSentUtc.Value < TimeSpan.FromMilliseconds(RepeatedMissingRangeMinIntervalMs))
            {
                return false;
            }

            if (context.OutstandingMissingRange is not null &&
                !narrowedRange &&
                !highestBufferedAdvanced &&
                context.LastMissingRangeSentUtc is not null &&
                DateTimeOffset.UtcNow - context.LastMissingRangeSentUtc.Value < TimeSpan.FromMilliseconds(RepeatedMissingRangeMinIntervalMs))
            {
                return false;
            }

            context.OutstandingMissingRange = range;
            context.LastMissingRangeSentUtc = DateTimeOffset.UtcNow;
            context.LastMissingRangeHighestBufferedChunkIndex = highestBufferedChunkIndex;
            RecordMissingRangeSentLocked(context);
            message = new FileTransferMissingRangeV1
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                StartChunkIndex = range.StartChunkIndex,
                EndChunkIndexExclusive = range.EndChunkIndexExclusive,
            };
            return true;
        }

        public bool ShouldDeferGrantExtensionDueToGapLocked(InboundTransferContext context, int highestBufferedChunkIndex, int targetGrantedUntilExclusive)
        {
            if (context.OldestGapStartChunkIndex is null ||
                highestBufferedChunkIndex < context.NextChunkIndex)
            {
                return false;
            }

            return targetGrantedUntilExclusive > context.LastAdvertisedGrantedUntilExclusive ||
                   highestBufferedChunkIndex > context.LastAdvertisedCreditFrontier;
        }

        public bool ShouldLogGapDeferredLocked(InboundTransferContext context)
        {
            var now = DateTimeOffset.UtcNow;
            if (context.LastGapExtensionDeferredLogUtc is not null &&
                now - context.LastGapExtensionDeferredLogUtc.Value < TimeSpan.FromMilliseconds(MissingRangeCooldownMs))
            {
                return false;
            }

            context.LastGapExtensionDeferredLogUtc = now;
            return true;
        }
    }
}
