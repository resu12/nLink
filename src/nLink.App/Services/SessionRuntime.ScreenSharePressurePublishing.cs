using System;
using System.Globalization;
using System.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    void IHelperRemoteScreenSharePressurePublishTarget.PublishHelperRemoteScreenSharePressureState(bool timerDriven)
    {
        MaybeSendScreenSharePressureStateCore(timerDriven);
    }

    private void MaybeSendScreenSharePressureStateCore()
    {
        MaybeSendScreenSharePressureStateCore(timerDriven: false);
    }

    private void MaybeSendScreenSharePressureStateCore(bool timerDriven)
    {
        var reduceFpsMinimumHold = TimeSpan.FromSeconds(3);
        var catchUpOnlyMinimumHold = TimeSpan.FromSeconds(5);
        if (disposed ||
            role != SessionRuntimeRole.Helper ||
            state != SessionRuntimeState.Connected ||
            transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        var nowUtc = nowProvider();
        var helperPressureSnapshot = GetHelperRemoteScreenSharePressureSnapshot();
        var currentViewerStaleDropCount = Math.Max(0L, helperPressureSnapshot.ViewerStaleDropCount);
        var viewerStaleDropDelta = Math.Max(0L, currentViewerStaleDropCount - lastObservedRemoteScreenShareStaleDrops);
        var transportBackpressureProbe = transport as IScreenShareTransportBackpressureProbe;
        var transportRecentDropCount = Math.Max(0, transportBackpressureProbe?.ScreenShareTransportRecentDropCount ?? 0);
        var recentHealthIssueCount = transportBackpressureProbe?.ScreenShareTransportRecentHealthIssueCount ?? 0;
        var severeHealthDegradation = transportBackpressureProbe?.IsScreenShareTransportHealthSeverelyDegraded == true;
        var laneQueueDepth = Math.Max(0, transportBackpressureProbe?.ScreenShareTransportQueueDepth ?? 0);
        var hasTransportQueuePressure =
            transportBackpressureProbe?.IsScreenShareTransportCongested == true ||
            transportBackpressureProbe?.IsScreenShareTransportSeverelyCongested == true ||
            laneQueueDepth > 0 ||
            transportRecentDropCount > 0;
        var previousMode = lastSentScreenSharePressureMode;
        var currentEpochWarmupActive = helperPressureSnapshot.CurrentEpochWarmupActive;
        var currentEpochApplyCount = Math.Max(0, helperPressureSnapshot.CurrentEpochApplyCount);
        var currentEpochNeedMoreInputCount = Math.Max(0L, helperPressureSnapshot.CurrentEpochNeedMoreInputCount);
        var currentEpochStaleDropCount = Math.Max(0L, helperPressureSnapshot.CurrentEpochStaleDropCount);
        var lastVisibleApplyFrameId = helperPressureSnapshot.LastVisibleApplyFrameId;
        var visibleHeadFrameId = helperPressureSnapshot.VisibleHeadFrameId;
        var visibleRecoveryFloorFrameId = helperPressureSnapshot.VisibleRecoveryFloorFrameId;
        var framesAppliedSinceLastGap = Math.Max(0L, helperPressureSnapshot.FramesAppliedSinceLastGap);
        var stableVisibleHeadFrameId = helperPressureSnapshot.StableVisibleHeadFrameId;
        var currentEpochGapCount = Math.Max(0L, helperPressureSnapshot.CurrentEpochGapCount);
        var currentEpochRecoveryKeyframeApplyCount = Math.Max(0L, helperPressureSnapshot.CurrentEpochRecoveryKeyframeApplyCount);
        var currentEpochResyncCount = Math.Max(0L, helperPressureSnapshot.CurrentEpochResyncCount);
        var currentEpochRecoveryActive = helperPressureSnapshot.CurrentEpochRecoveryActive;
        var currentEpochPostRecoveryStabilizationActive = helperPressureSnapshot.CurrentEpochPostRecoveryStabilizationActive;
        var helperSessionPhase = helperPressureSnapshot.HelperSessionPhase;
        var helperRecoveryMechanism = helperPressureSnapshot.HelperRecoveryMechanism;
        var helperBaselineEstablished = helperPressureSnapshot.HelperBaselineEstablished;
        var currentEpochProgressProven = helperPressureSnapshot.CurrentEpochProgressProven;
        var currentEpochProgressProofSource = string.IsNullOrWhiteSpace(helperPressureSnapshot.CurrentEpochProgressProofSource)
            ? "none"
            : helperPressureSnapshot.CurrentEpochProgressProofSource;
        var currentEpochProvenHeadFrameId = helperPressureSnapshot.CurrentEpochProvenHeadFrameId;
        var timeSinceLastVisibleApplyMs = Math.Max(-1L, helperPressureSnapshot.TimeSinceLastVisibleApplyMs);
        var baselineEstablished = helperPressureSnapshot.BaselineEstablished;
        var baselineCaptureToRenderMs = Math.Max(-1L, helperPressureSnapshot.BaselineCaptureToRenderMs);
        var ageExcessMs = Math.Max(-1L, helperPressureSnapshot.AgeExcessMs);
        var progressStallMs = Math.Max(-1L, helperPressureSnapshot.ProgressStallMs);
        var baselineReseedInProgress = helperPressureSnapshot.BaselineReseedInProgress;
        var postRecoveryAgeGraceActive = helperPressureSnapshot.PostRecoveryAgeGraceActive;
        var steadyVisibleProgressActive = helperPressureSnapshot.SteadyVisibleProgressActive;
        var derivedPostRecoveryHealthyActive = helperPressureSnapshot.DerivedPostRecoveryHealthyActive;
        var steadyVisibleProgressActivationFrameId = helperPressureSnapshot.SteadyVisibleProgressActivationFrameId;
        long previousSentVisibleHeadFrameId;
        long previousSentVisibleApplyFrameId;
        long previousSentAppliedHeadFrameId;
        long previousSentStableVisibleHeadFrameId;
        bool previousSentSteadyVisibleProgressActive;
        lock (helperRemoteScreenSharePressureGate)
        {
            previousSentVisibleHeadFrameId = helperRemoteLastSentVisibleHeadFrameId;
            previousSentVisibleApplyFrameId = helperRemoteLastSentVisibleApplyFrameId;
            previousSentAppliedHeadFrameId = helperRemoteLastSentAppliedHeadFrameId;
            previousSentStableVisibleHeadFrameId = helperRemoteLastSentStableVisibleHeadFrameId;
            previousSentSteadyVisibleProgressActive = helperRemoteLastSentSteadyVisibleProgressActive;
        }

        var currentProofHeadFrameId = GetLatestHelperVisibleProofHeadFrameId(
            currentEpochProvenHeadFrameId >= 0 ? currentEpochProvenHeadFrameId : visibleHeadFrameId,
            lastVisibleApplyFrameId);
        var previousSentProofHeadFrameId = GetLatestHelperVisibleProofHeadFrameId(
            previousSentVisibleHeadFrameId,
            previousSentVisibleApplyFrameId);
        var helperVisibleFactsAdvancedAgainstLastSend =
            (visibleHeadFrameId >= 0 && visibleHeadFrameId > previousSentVisibleHeadFrameId) ||
            (lastVisibleApplyFrameId >= 0 && lastVisibleApplyFrameId > previousSentVisibleApplyFrameId) ||
            (steadyVisibleProgressActive && !previousSentSteadyVisibleProgressActive);
        var senderStillNeedsHelperProof = currentEpochRecoveryActive;
        var bypassPressureSendThrottleForVisibleProgress =
            helperVisibleFactsAdvancedAgainstLastSend &&
            senderStillNeedsHelperProof;
        var helperHealthyProofKeepaliveActive =
            derivedPostRecoveryHealthyActive &&
            currentProofHeadFrameId >= 0 &&
            !currentEpochRecoveryActive &&
            !senderStillNeedsHelperProof;
        var helperHealthyProofKeepaliveHeadAdvanced =
            helperHealthyProofKeepaliveActive &&
            currentProofHeadFrameId > previousSentProofHeadFrameId;
        var helperHealthyProofKeepaliveExpired =
            helperHealthyProofKeepaliveActive &&
            (lastSentScreenSharePressureUtc == default ||
             nowUtc - lastSentScreenSharePressureUtc >= HelperRemoteScreenShareProofKeepaliveInterval);
        var bypassPressureSendThrottleForHealthyProofKeepalive =
            helperHealthyProofKeepaliveActive &&
            (helperHealthyProofKeepaliveHeadAdvanced || helperHealthyProofKeepaliveExpired);
        var effectiveProofRefreshBypass =
            bypassPressureSendThrottleForVisibleProgress ||
            bypassPressureSendThrottleForHealthyProofKeepalive;
        if (timerDriven &&
            !bypassPressureSendThrottleForHealthyProofKeepalive)
        {
            return;
        }

        lastObservedRemoteScreenShareStaleDrops = currentViewerStaleDropCount;
        var effectiveStaleDropDelta = currentEpochWarmupActive
            ? transportRecentDropCount
            : Math.Max(viewerStaleDropDelta, transportRecentDropCount);
        var hasAppliedFrameSample = helperPressureSnapshot.HasAppliedFrame && !currentEpochWarmupActive;
        var hasApplyCadenceSample =
            hasAppliedFrameSample &&
            currentEpochApplyCount >= HelperRemoteScreenShareCadencePressureMinimumApplies &&
            helperPressureSnapshot.LastApplyCadenceMs >= 0;
        var appliedFrameAgeMs = hasAppliedFrameSample
            ? Math.Max(0L, helperPressureSnapshot.LastAppliedFrameAgeMs)
            : 0L;
        var lastApplyCadenceMs = hasApplyCadenceSample
            ? Math.Max(0L, helperPressureSnapshot.LastApplyCadenceMs)
            : -1L;
        var averageApplyCadenceMs = hasApplyCadenceSample
            ? Math.Max(0d, helperPressureSnapshot.AverageApplyCadenceMs)
            : 0d;
        var visibleProgressWindowMs = (long)HelperRemoteScreenSharePostRecoveryVisibleProgressWindow.TotalMilliseconds;
        var hasOngoingVisibleProgress =
            lastVisibleApplyFrameId >= 0 &&
            progressStallMs >= 0 &&
            progressStallMs <= visibleProgressWindowMs;
        var effectiveCurrentFrameAgeMs =
            appliedFrameAgeMs > 0
                ? Math.Max(0L, appliedFrameAgeMs + Math.Max(0L, progressStallMs))
                : appliedFrameAgeMs;
        var trustedVisibleStableProgress =
            currentEpochProgressProven &&
            steadyVisibleProgressActive &&
            stableVisibleHeadFrameId >= 0 &&
            framesAppliedSinceLastGap >= 4 &&
            helperPressureSnapshot.AppliedHeadFrameId >= 0 &&
            hasAppliedFrameSample &&
            hasOngoingVisibleProgress &&
            progressStallMs >= 0 &&
            progressStallMs <= visibleProgressWindowMs &&
            !currentEpochWarmupActive &&
            !currentEpochRecoveryActive &&
            helperRecoveryMechanism == HelperRemoteRecoveryMechanism.None &&
            currentEpochStaleDropCount == 0 &&
            effectiveStaleDropDelta == 0 &&
            transportRecentDropCount == 0 &&
            !hasTransportQueuePressure &&
            recentHealthIssueCount <= 0 &&
            !severeHealthDegradation;

        lock (helperRemoteScreenSharePressureGate)
        {
            helperRemoteConsecutiveStaleDropWindows = effectiveStaleDropDelta > 0
                ? helperRemoteConsecutiveStaleDropWindows + 1
                : 0;
        }

        var consecutiveStaleDropWindows = Math.Max(0, Volatile.Read(ref helperRemoteConsecutiveStaleDropWindows));
        var shouldEvaluateAgeAndCadencePressure =
            baselineEstablished &&
            helperPressureSnapshot.HasAppliedFrame &&
            !currentEpochWarmupActive &&
            !currentEpochRecoveryActive;
        var rawReduceAgePressure =
            shouldEvaluateAgeAndCadencePressure &&
            ageExcessMs >= HelperRemoteScreenShareAgeExcessReduceThresholdMs;
        var rawSevereAgePressure =
            shouldEvaluateAgeAndCadencePressure &&
            ageExcessMs >= HelperRemoteScreenShareAgeExcessCatchUpThresholdMs;
        // Ordinary age/cadence pressure should step down gradually instead of
        // forcing an immediate catch-up epoch transition from one bad sample.
        var rawCatchUpAgePressure = false;
        var hasHealthyCadence =
            !hasApplyCadenceSample ||
            hasOngoingVisibleProgress;
        long cadenceStallElapsedMs = 0;
        var cadenceStallEligible =
            helperPressureSnapshot.HasAppliedFrame &&
            !currentEpochWarmupActive &&
            !currentEpochRecoveryActive &&
            !baselineReseedInProgress &&
            progressStallMs > visibleProgressWindowMs;
        lock (helperRemoteScreenSharePressureGate)
        {
            if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
            {
                if (cadenceStallEligible)
                {
                    if (helperRemoteCurrentPressureEpochCadenceStallStartedUtc == default)
                    {
                        helperRemoteCurrentPressureEpochCadenceStallStartedUtc = nowUtc;
                        helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
                        helperRemoteCurrentPressureEpochCadenceStallWindowCount++;
                        FreezeHelperRemotePressureBaselineUntilNextApply_NoLock(dueToStall: true);
                    }

                    cadenceStallElapsedMs = Math.Max(
                        0L,
                        (long)(nowUtc - helperRemoteCurrentPressureEpochCadenceStallStartedUtc).TotalMilliseconds);
                }
                else
                {
                    helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
                    helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
                }
            }
        }

        var rawReduceCadencePressure =
            cadenceStallEligible &&
            cadenceStallElapsedMs > (long)HelperRemoteScreenShareCadenceStallTriggerWindow.TotalMilliseconds;
        var rawCatchUpCadencePressure = false;

        int agePressureConsecutiveCount;
        int cadencePressureConsecutiveCount;
        long catchUpSuppressedDueToProgressCount;
        long previousEvaluatedAppliedHeadFrameId;
        long previousEvaluatedStableVisibleHeadFrameId;
        bool appliedHeadAdvancedSinceLastEvaluation;
        bool stableVisibleHeadAdvancedSinceLastEvaluation;
        bool headAdvancedSinceLastEvaluation;
        var suppressStandaloneHighFrameAgeDueToHeadAdvance = false;
        bool cadenceStallTriggered;
        lock (helperRemoteScreenSharePressureGate)
        {
            previousEvaluatedAppliedHeadFrameId = helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId;
            previousEvaluatedStableVisibleHeadFrameId = helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId;
            appliedHeadAdvancedSinceLastEvaluation =
                helperPressureSnapshot.AppliedHeadFrameId >= 0 &&
                helperPressureSnapshot.AppliedHeadFrameId > previousEvaluatedAppliedHeadFrameId;
            stableVisibleHeadAdvancedSinceLastEvaluation =
                stableVisibleHeadFrameId >= 0 &&
                stableVisibleHeadFrameId > previousEvaluatedStableVisibleHeadFrameId;
            headAdvancedSinceLastEvaluation =
                appliedHeadAdvancedSinceLastEvaluation ||
                stableVisibleHeadAdvancedSinceLastEvaluation;

            if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
            {
                helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId = Math.Max(
                    helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId,
                    helperPressureSnapshot.AppliedHeadFrameId);
                helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId = Math.Max(
                    helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId,
                    stableVisibleHeadFrameId);
            }

            cadenceStallTriggered = helperRemoteCurrentPressureEpochCadenceStallTriggered;
        }

        var trustedVisibleApplyProgress =
            !trustedVisibleStableProgress &&
            (currentEpochProgressProven ||
             derivedPostRecoveryHealthyActive ||
             steadyVisibleProgressActive) &&
            framesAppliedSinceLastGap >= 2 &&
            helperPressureSnapshot.AppliedHeadFrameId >= 0 &&
            hasAppliedFrameSample &&
            hasOngoingVisibleProgress &&
            progressStallMs >= 0 &&
            progressStallMs <= visibleProgressWindowMs &&
            !currentEpochWarmupActive &&
            !currentEpochRecoveryActive &&
            helperRecoveryMechanism == HelperRemoteRecoveryMechanism.None &&
            currentEpochStaleDropCount == 0 &&
            effectiveStaleDropDelta == 0 &&
            transportRecentDropCount == 0 &&
            !hasTransportQueuePressure &&
            recentHealthIssueCount <= 0 &&
            !severeHealthDegradation;
        var trustedVisibleProgress =
            trustedVisibleStableProgress ||
            trustedVisibleApplyProgress;

        suppressStandaloneHighFrameAgeDueToHeadAdvance =
            rawReduceAgePressure &&
            currentEpochProgressProven &&
            headAdvancedSinceLastEvaluation &&
            progressStallMs >= 0 &&
            progressStallMs <= visibleProgressWindowMs &&
            !currentEpochRecoveryActive &&
            helperRecoveryMechanism == HelperRemoteRecoveryMechanism.None;
        var suppressStandaloneHighFrameAgeDueToTrustedVisibleProgress =
            rawReduceAgePressure &&
            trustedVisibleProgress;
        var suppressSlowApplyCadenceDueToTrustedVisibleProgress =
            rawReduceCadencePressure &&
            trustedVisibleProgress &&
            !cadenceStallTriggered &&
            !rawCatchUpAgePressure &&
            !rawCatchUpCadencePressure;
        var suppressAgeOnlyHighFrameAgeDueToVisibleProgress =
            rawReduceAgePressure &&
            !rawReduceCadencePressure &&
            !rawCatchUpAgePressure &&
            !rawCatchUpCadencePressure &&
            (suppressStandaloneHighFrameAgeDueToHeadAdvance ||
             suppressStandaloneHighFrameAgeDueToTrustedVisibleProgress ||
             postRecoveryAgeGraceActive);

        lock (helperRemoteScreenSharePressureGate)
        {
            if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
            {
                if (suppressStandaloneHighFrameAgeDueToHeadAdvance ||
                    suppressStandaloneHighFrameAgeDueToTrustedVisibleProgress ||
                    suppressSlowApplyCadenceDueToTrustedVisibleProgress ||
                    postRecoveryAgeGraceActive)
                {
                    if (rawReduceAgePressure || rawReduceCadencePressure || rawCatchUpAgePressure || rawCatchUpCadencePressure)
                    {
                        helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount++;
                        if (currentEpochPostRecoveryStabilizationActive)
                        {
                            helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks++;
                        }
                    }

                    helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
                    helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
                    if (suppressStandaloneHighFrameAgeDueToHeadAdvance && rawReduceAgePressure)
                    {
                        helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount++;
                    }

                    if (postRecoveryAgeGraceActive && rawReduceAgePressure)
                    {
                        helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount++;
                        if (currentEpochPostRecoveryStabilizationActive)
                        {
                            helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks++;
                        }
                    }
                }
                else
                {
                    if (rawReduceCadencePressure &&
                        !helperRemoteCurrentPressureEpochCadenceStallTriggered)
                    {
                        helperRemoteCurrentPressureEpochCadenceStallTriggered = true;
                        helperRemoteCurrentPressureEpochCadenceStallTriggerCount++;
                    }

                    helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = rawReduceAgePressure
                        ? helperRemoteCurrentPressureEpochAgePressureConsecutiveCount + 1
                        : 0;
                    helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
                }
            }

            if (suppressStandaloneHighFrameAgeDueToTrustedVisibleProgress)
            {
                MaybeBeginHelperRemotePressureBaselineReseedAfterTrustedVisibleProgress_NoLock(
                    nowUtc,
                    helperPressureSnapshot.CurrentEpoch);
            }

            agePressureConsecutiveCount = helperRemoteCurrentPressureEpochAgePressureConsecutiveCount;
            cadencePressureConsecutiveCount = helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount;
            catchUpSuppressedDueToProgressCount = helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount;
        }

        if (suppressAgeOnlyHighFrameAgeDueToVisibleProgress ||
            suppressSlowApplyCadenceDueToTrustedVisibleProgress)
        {
            hasHealthyCadence = true;
        }

        var stableHeadAdvancedSinceSteadyProgressActivation =
            steadyVisibleProgressActive &&
            stableVisibleHeadFrameId >= 0 &&
            steadyVisibleProgressActivationFrameId >= 0
                ? stableVisibleHeadFrameId - steadyVisibleProgressActivationFrameId
                : -1L;
        var appliedHeadFrameId = helperPressureSnapshot.AppliedHeadFrameId;
        var visibleApplyBeyondLastRecoveryFrame = true;
        var hasAppliedHeadVisibleProgress =
            appliedHeadFrameId >= 0 &&
            hasOngoingVisibleProgress &&
            !currentEpochRecoveryActive &&
            progressStallMs >= 0 &&
            progressStallMs <= visibleProgressWindowMs;
        var allowStandaloneHighFrameAgePressureForVisibleProgress =
            rawReduceAgePressure &&
            !postRecoveryAgeGraceActive &&
            !suppressStandaloneHighFrameAgeDueToHeadAdvance &&
            !suppressStandaloneHighFrameAgeDueToTrustedVisibleProgress &&
            (progressStallMs > visibleProgressWindowMs ||
             currentEpochRecoveryActive ||
             rawCatchUpAgePressure ||
             (agePressureConsecutiveCount >= 2 &&
              visibleApplyBeyondLastRecoveryFrame &&
              (hasAppliedHeadVisibleProgress || steadyVisibleProgressActive) &&
              rawSevereAgePressure &&
              (stableHeadAdvancedSinceSteadyProgressActivation >= 4 ||
               !steadyVisibleProgressActive)));
        if (hasAppliedHeadVisibleProgress &&
            rawReduceAgePressure &&
            !allowStandaloneHighFrameAgePressureForVisibleProgress)
        {
            lock (helperRemoteScreenSharePressureGate)
            {
                if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
                {
                    helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount++;
                    if (currentEpochPostRecoveryStabilizationActive)
                    {
                        helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks++;
                    }
                }
            }
        }

        var hasReduceAgePressure =
            (hasAppliedHeadVisibleProgress || steadyVisibleProgressActive)
                ? allowStandaloneHighFrameAgePressureForVisibleProgress
                : agePressureConsecutiveCount >= HelperRemoteScreenSharePressureConsecutiveThreshold;
        var hasCatchUpAgePressure = rawCatchUpAgePressure;
        var hasReduceCadencePressure =
            rawReduceCadencePressure &&
            !suppressSlowApplyCadenceDueToTrustedVisibleProgress;
        var hasCatchUpCadencePressure = rawCatchUpCadencePressure;
        var hasReduceStalePressure = consecutiveStaleDropWindows >= 2;
        var hasCatchUpStalePressure = effectiveStaleDropDelta >= 2;
        var hasAppliedFramePressure =
            hasReduceAgePressure ||
            hasCatchUpAgePressure ||
            hasReduceCadencePressure ||
            hasCatchUpCadencePressure;
        if (hasAppliedFramePressure)
        {
            hasHealthyCadence = false;
            healthyScreenSharePressureIntervals = 0;
        }

        var hasStalePressure = hasReduceStalePressure || hasCatchUpStalePressure;
        var hasBridgeHealth = recentHealthIssueCount > 0 || severeHealthDegradation;
        var bridgeHealthQuarantineActive = false;
        var hasActionableBridgeHealth = false;
        lock (helperRemoteScreenSharePressureGate)
        {
            if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
            {
                if (!hasBridgeHealth)
                {
                    helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
                }
                else
                {
                    var quarantineDeadlineUtc =
                        helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc == default
                            ? default
                            : helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc + HelperRemoteScreenShareBridgeHealthQuarantineWindow;
                    bridgeHealthQuarantineActive =
                        helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc == default ||
                        !helperPressureSnapshot.CurrentEpochFirstApplySeen ||
                        (quarantineDeadlineUtc != default && nowUtc < quarantineDeadlineUtc);

                    if (bridgeHealthQuarantineActive)
                    {
                        helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount++;
                        helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount++;
                        helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
                    }
                    else if (severeHealthDegradation)
                    {
                        hasActionableBridgeHealth = true;
                        helperRemoteCurrentPressureEpochBridgeHealthActionableCount++;
                        helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
                    }
                    else if (hasTransportQueuePressure)
                    {
                        helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount++;
                        if (helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount >= 2)
                        {
                            hasActionableBridgeHealth = true;
                            helperRemoteCurrentPressureEpochBridgeHealthActionableCount++;
                        }
                        else
                        {
                            helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount++;
                        }
                    }
                    else
                    {
                        helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount++;
                        helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
                    }

                    if (hasActionableBridgeHealth &&
                        !severeHealthDegradation &&
                        !hasTransportQueuePressure)
                    {
                        helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount++;
                    }
                }
            }
        }

        var healthKind = FormatScreenSharePressureHealthKind(hasBridgeHealth, hasActionableBridgeHealth);
        var previousReduceFpsReasonIsSoftAgeOrCadence =
            string.Equals(lastSentScreenSharePressureReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) ||
            string.Equals(lastSentScreenSharePressureReason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal);
        var clearPreviousSoftPressureWithTrustedVisibleProgress =
            trustedVisibleProgress &&
            !hasAppliedFramePressure &&
            !hasStalePressure &&
            !hasBridgeHealth &&
            !hasActionableBridgeHealth &&
            !hasTransportQueuePressure &&
            previousMode == ScreenSharePressureMode.ReduceFps &&
            previousReduceFpsReasonIsSoftAgeOrCadence;
        var clearPreviousContinuityLossWithHealthyProof =
            trustedVisibleProgress &&
            !hasAppliedFramePressure &&
            !hasStalePressure &&
            !hasBridgeHealth &&
            !hasActionableBridgeHealth &&
            !hasTransportQueuePressure &&
            !currentEpochRecoveryActive &&
            previousMode == ScreenSharePressureMode.Normal &&
            string.Equals(lastSentScreenSharePressureReason, ScreenSharePressureProtocol.PressureReasonContinuityLoss, StringComparison.Ordinal);

        ScreenSharePressureMode nextMode;
        string nextReason;
        ScreenSharePressureSampleSource sampleSource;
        if (currentEpochRecoveryActive)
        {
            healthyScreenSharePressureIntervals = 0;
            var recoveryElapsed = helperPressureSnapshot.CurrentEpochRecoveryStartedUtc == default
                ? TimeSpan.Zero
                : nowUtc - helperPressureSnapshot.CurrentEpochRecoveryStartedUtc;
            if (recoveryElapsed < HelperRemoteScreenShareRecoveryOnlyWindow)
            {
                nextMode = ScreenSharePressureMode.Normal;
                nextReason = ScreenSharePressureProtocol.PressureReasonContinuityLoss;
                sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
            }
            else
            {
                if (helperPressureSnapshot.CurrentEpochRecoveryTimeoutSent)
                {
                    return;
                }

                lock (helperRemoteScreenSharePressureGate)
                {
                    if (helperRemoteContinuityRecoveryActive &&
                        helperRemoteContinuityRecoveryEpoch == helperPressureSnapshot.CurrentEpoch)
                    {
                        helperRemoteContinuityRecoveryTimeoutSent = true;
                    }
                }

                nextMode = ScreenSharePressureMode.CatchUpOnly;
                nextReason = ScreenSharePressureProtocol.PressureReasonContinuityLoss;
                sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
            }
        }
        else if (severeHealthDegradation && hasActionableBridgeHealth)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.CatchUpOnly;
            nextReason = ScreenSharePressureProtocol.PressureReasonBridgeHealth;
            sampleSource = ScreenSharePressureSampleSource.BridgeHealth;
        }
        else if (hasCatchUpCadencePressure)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.CatchUpOnly;
            nextReason = ScreenSharePressureProtocol.PressureReasonSlowApplyCadence;
            sampleSource = ScreenSharePressureSampleSource.ApplyCadence;
        }
        else if (hasCatchUpAgePressure)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.CatchUpOnly;
            nextReason = ScreenSharePressureProtocol.PressureReasonHighFrameAge;
            sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
        }
        else if (hasCatchUpStalePressure)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.CatchUpOnly;
            nextReason = ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops;
            sampleSource = ScreenSharePressureSampleSource.StaleDropOnly;
        }
        else if (recentHealthIssueCount > 0 && hasActionableBridgeHealth)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.ReduceFps;
            nextReason = ScreenSharePressureProtocol.PressureReasonBridgeHealth;
            sampleSource = ScreenSharePressureSampleSource.BridgeHealth;
        }
        else if (hasReduceCadencePressure)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.ReduceFps;
            nextReason = ScreenSharePressureProtocol.PressureReasonSlowApplyCadence;
            sampleSource = ScreenSharePressureSampleSource.ApplyCadence;
        }
        else if (hasReduceAgePressure)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.ReduceFps;
            nextReason = ScreenSharePressureProtocol.PressureReasonHighFrameAge;
            sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
        }
        else if (hasReduceStalePressure)
        {
            healthyScreenSharePressureIntervals = 0;
            nextMode = ScreenSharePressureMode.ReduceFps;
            nextReason = ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops;
            sampleSource = ScreenSharePressureSampleSource.StaleDropOnly;
        }
        else
        {
            if ((!hasAppliedFrameSample || currentEpochWarmupActive) &&
                effectiveStaleDropDelta == 0 &&
                !hasBridgeHealth &&
                !effectiveProofRefreshBypass)
            {
                return;
            }

            if (currentEpochWarmupActive &&
                !helperPressureSnapshot.CurrentEpochFirstApplySeen &&
                !effectiveProofRefreshBypass)
            {
                return;
            }

            if (baselineReseedInProgress &&
                progressStallMs > visibleProgressWindowMs &&
                !effectiveProofRefreshBypass)
            {
                return;
            }

            if (!hasHealthyCadence &&
                !effectiveProofRefreshBypass)
            {
                return;
            }

            if (clearPreviousSoftPressureWithTrustedVisibleProgress)
            {
                healthyScreenSharePressureIntervals = Math.Max(healthyScreenSharePressureIntervals, 4);
                nextMode = ScreenSharePressureMode.Normal;
                nextReason = ScreenSharePressureProtocol.PressureReasonHealthy;
                sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
            }
            else if (clearPreviousContinuityLossWithHealthyProof ||
                     bypassPressureSendThrottleForHealthyProofKeepalive)
            {
                healthyScreenSharePressureIntervals = Math.Max(healthyScreenSharePressureIntervals, 4);
                nextMode = ScreenSharePressureMode.Normal;
                nextReason = ScreenSharePressureProtocol.PressureReasonHealthy;
                sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
            }
            else if (effectiveProofRefreshBypass)
            {
                nextMode = lastSentScreenSharePressureMode;
                nextReason = string.IsNullOrWhiteSpace(lastSentScreenSharePressureReason)
                    ? ScreenSharePressureProtocol.PressureReasonHealthy
                    : lastSentScreenSharePressureReason;
                sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
            }
            else
            {
                healthyScreenSharePressureIntervals++;
                if (healthyScreenSharePressureIntervals >= 4)
                {
                    nextMode = ScreenSharePressureMode.Normal;
                    nextReason = ScreenSharePressureProtocol.PressureReasonHealthy;
                    sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
                }
                else
                {
                    return;
                }
            }
        }

        var holdPreservedPreviousMode = false;
        if (bridgeHealthQuarantineActive &&
            string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal))
        {
            nextMode = ScreenSharePressureMode.Normal;
            nextReason = ScreenSharePressureProtocol.PressureReasonHealthy;
            sampleSource = ScreenSharePressureSampleSource.AppliedFrameAge;
        }

        var ignorePreviousBridgeHealthHold =
            !hasActionableBridgeHealth &&
            string.Equals(lastSentScreenSharePressureReason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal);
        var ignorePreviousSoftPressureHold =
            clearPreviousSoftPressureWithTrustedVisibleProgress &&
            nextMode == ScreenSharePressureMode.Normal &&
            string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonHealthy, StringComparison.Ordinal);
        if (nextMode != previousMode &&
            lastSentScreenSharePressureModeEnteredUtc != default)
        {
            var heldFor = nowUtc - lastSentScreenSharePressureModeEnteredUtc;
            if (previousMode == ScreenSharePressureMode.CatchUpOnly &&
                !ignorePreviousBridgeHealthHold &&
                !string.Equals(lastSentScreenSharePressureReason, ScreenSharePressureProtocol.PressureReasonContinuityLoss, StringComparison.Ordinal) &&
                heldFor < catchUpOnlyMinimumHold)
            {
                nextMode = ScreenSharePressureMode.CatchUpOnly;
                nextReason = lastSentScreenSharePressureReason;
                holdPreservedPreviousMode = true;
            }
            else if (previousMode == ScreenSharePressureMode.ReduceFps &&
                     !ignorePreviousBridgeHealthHold &&
                     !ignorePreviousSoftPressureHold &&
                     heldFor < reduceFpsMinimumHold &&
                     nextMode == ScreenSharePressureMode.Normal)
            {
                nextMode = ScreenSharePressureMode.ReduceFps;
                nextReason = lastSentScreenSharePressureReason;
                sampleSource = lastSentScreenSharePressureReason == ScreenSharePressureProtocol.PressureReasonBridgeHealth
                    ? ScreenSharePressureSampleSource.BridgeHealth
                    : ScreenSharePressureSampleSource.AppliedFrameAge;
                holdPreservedPreviousMode = true;
            }
        }

        var nextIsHealthyPressure =
            nextMode == ScreenSharePressureMode.Normal &&
            string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonHealthy, StringComparison.Ordinal);
        var suppressNonHealthyClearDueToCurrentEpochProgress =
            string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) &&
            currentEpochProgressProven &&
            headAdvancedSinceLastEvaluation &&
            progressStallMs >= 0 &&
            progressStallMs <= visibleProgressWindowMs &&
            !currentEpochRecoveryActive &&
            helperRecoveryMechanism == HelperRemoteRecoveryMechanism.None;
        var preserveSteadyVisibleProgressForAgeOnlyPressure =
            suppressNonHealthyClearDueToCurrentEpochProgress ||
            (trustedVisibleProgress &&
             !hasStalePressure &&
             !hasBridgeHealth &&
             !hasActionableBridgeHealth &&
             !hasTransportQueuePressure &&
             (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) ||
              string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal)));

        if (!nextIsHealthyPressure)
        {
            if (!preserveSteadyVisibleProgressForAgeOnlyPressure)
            {
                var keepHealthyLatchAcrossNonHealthyPressure = false;
                if (!keepHealthyLatchAcrossNonHealthyPressure)
                {
                    lock (helperRemoteScreenSharePressureGate)
                    {
                        if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
                        {
                            ClearHelperRemoteSteadyVisibleProgressState_NoLock("non_healthy_pressure");
                        }
                    }

                    steadyVisibleProgressActive = false;
                    stableVisibleHeadFrameId = -1;
                    framesAppliedSinceLastGap = 0;
                }
                else
                {
                    currentEpochWarmupActive = false;
                    baselineEstablished = true;
                    framesAppliedSinceLastGap = Math.Max(1L, framesAppliedSinceLastGap);
                }
            }
            else
            {
                lock (helperRemoteScreenSharePressureGate)
                {
                    if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
                    {
                        helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount++;
                    }
                }
            }
        }

        var effectiveObservedFrameAgeMs = currentEpochRecoveryActive ? 0L : effectiveCurrentFrameAgeMs;
        var materialAgeChange = Math.Abs(effectiveObservedFrameAgeMs - lastSentScreenSharePressureAgeMs) >= 250;
        long helperFirstVisibleApplyToSenderFactSendMs = -1;
        if (holdPreservedPreviousMode &&
            nextMode == lastSentScreenSharePressureMode &&
            string.Equals(nextReason, lastSentScreenSharePressureReason, StringComparison.Ordinal) &&
            effectiveStaleDropDelta == 0 &&
            !effectiveProofRefreshBypass &&
            !clearPreviousSoftPressureWithTrustedVisibleProgress &&
            !clearPreviousContinuityLossWithHealthyProof)
        {
            return;
        }

        if (nextMode == lastSentScreenSharePressureMode &&
            string.Equals(nextReason, lastSentScreenSharePressureReason, StringComparison.Ordinal) &&
            effectiveStaleDropDelta == 0 &&
            !materialAgeChange &&
            !effectiveProofRefreshBypass &&
            !clearPreviousSoftPressureWithTrustedVisibleProgress &&
            !clearPreviousContinuityLossWithHealthyProof)
        {
            return;
        }

        if (lastSentScreenSharePressureUtc != default &&
            nowUtc - lastSentScreenSharePressureUtc < TimeSpan.FromSeconds(1) &&
            !effectiveProofRefreshBypass &&
            !clearPreviousSoftPressureWithTrustedVisibleProgress &&
            !clearPreviousContinuityLossWithHealthyProof)
        {
            return;
        }

        var message = new ScreenSharePressureStateV1
        {
            SessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId,
            Mode = nextMode,
            Reason = nextReason,
            ObservedFrameAgeMs = effectiveObservedFrameAgeMs,
            RecentStaleFrameDrops = effectiveStaleDropDelta,
            SentAtUtcMs = nowUtc.ToUnixTimeMilliseconds(),
            CurrentEpochWarmupActive = currentEpochWarmupActive,
            CurrentEpochApplyCount = currentEpochApplyCount,
            CurrentEpochNeedMoreInputCount = currentEpochNeedMoreInputCount,
            LastVisibleApplyFrameId = lastVisibleApplyFrameId >= 0 ? lastVisibleApplyFrameId : null,
            VisibleHeadFrameId = visibleHeadFrameId >= 0 ? visibleHeadFrameId : null,
            VisibleRecoveryFloorFrameId = visibleRecoveryFloorFrameId >= 0 ? visibleRecoveryFloorFrameId : null,
            AppliedHeadFrameId = helperPressureSnapshot.AppliedHeadFrameId >= 0 ? helperPressureSnapshot.AppliedHeadFrameId : null,
            SteadyVisibleProgressActive = steadyVisibleProgressActive,
            StableVisibleHeadFrameId = stableVisibleHeadFrameId >= 0 ? stableVisibleHeadFrameId : null,
            FramesAppliedSinceLastGap = framesAppliedSinceLastGap,
            CurrentEpochRecoveryKeyframeApplyCount = currentEpochRecoveryKeyframeApplyCount,
        };

        lastSentScreenSharePressureMode = nextMode;
        lastSentScreenSharePressureReason = nextReason;
        lastSentScreenSharePressureAgeMs = effectiveObservedFrameAgeMs;
        lastSentScreenSharePressureStaleDrops = effectiveStaleDropDelta;
        lastSentScreenSharePressureUtc = nowUtc;
        if (nextMode != previousMode || lastSentScreenSharePressureModeEnteredUtc == default)
        {
            lastSentScreenSharePressureModeEnteredUtc = nowUtc;
        }

        lock (helperRemoteScreenSharePressureGate)
        {
            helperRemoteLastAppliedHeadAdvancedSincePressureEvaluation = appliedHeadAdvancedSinceLastEvaluation;
            helperRemoteLastStableVisibleHeadAdvancedSincePressureEvaluation = stableVisibleHeadAdvancedSinceLastEvaluation;
            helperRemoteLastHealthyStateEstablishedBy = currentEpochProgressProven
                ? currentEpochProgressProofSource
                : "none";
            if (bypassPressureSendThrottleForVisibleProgress)
            {
                helperRemotePressureSendBypassedForVisibleProgressCount++;
                if (helperRemoteCurrentPressureEpochFirstVisibleApplyUtc != default &&
                    nowUtc >= helperRemoteCurrentPressureEpochFirstVisibleApplyUtc)
                {
                    helperRemoteLastFirstVisibleApplyToPressureSendMs = Math.Max(
                        0L,
                        (long)(nowUtc - helperRemoteCurrentPressureEpochFirstVisibleApplyUtc).TotalMilliseconds);
                    helperFirstVisibleApplyToSenderFactSendMs = helperRemoteLastFirstVisibleApplyToPressureSendMs;
                }
            }

            if (bypassPressureSendThrottleForHealthyProofKeepalive)
            {
                if (helperRemoteProofKeepaliveSendCount < long.MaxValue)
                {
                    helperRemoteProofKeepaliveSendCount++;
                }

                if (timerDriven &&
                    helperRemoteProofKeepaliveTimerDrivenSendCount < long.MaxValue)
                {
                    helperRemoteProofKeepaliveTimerDrivenSendCount++;
                }

                helperRemoteLastProofKeepaliveHeadFrameId = currentProofHeadFrameId;
                helperRemoteLastProofKeepaliveSentUtc = nowUtc;
            }

            helperRemoteLastSentSteadyProgressEpoch = helperPressureSnapshot.CurrentEpoch;
            helperRemoteLastSentSteadyVisibleProgressActive = steadyVisibleProgressActive;
            helperRemoteLastSentVisibleHeadFrameId = visibleHeadFrameId;
            helperRemoteLastSentStableVisibleHeadFrameId = stableVisibleHeadFrameId;
            helperRemoteLastSentFramesAppliedSinceLastGap = framesAppliedSinceLastGap;
            helperRemoteLastSentVisibleApplyFrameId = lastVisibleApplyFrameId;
            helperRemoteLastSentAppliedHeadFrameId = helperPressureSnapshot.AppliedHeadFrameId;
            if (helperRemoteCurrentPressureEpoch == helperPressureSnapshot.CurrentEpoch)
            {
                if (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonContinuityLoss, StringComparison.Ordinal))
                {
                    if (!derivedPostRecoveryHealthyActive)
                    {
                        helperRemoteCurrentPressureEpochContinuityLossTicks++;
                    }
                }

                if (currentEpochWarmupActive && !derivedPostRecoveryHealthyActive)
                {
                    helperRemoteCurrentPressureEpochWarmupTicks++;
                }

                if (lastVisibleApplyFrameId < 0)
                {
                    helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks++;
                }

                if (helperRemoteCurrentPressureEpochVisibleAppliesBeforePressureReenabled < 0 &&
                    currentEpochRecoveryKeyframeApplyCount > 0 &&
                    (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) ||
                     string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal)))
                {
                    helperRemoteCurrentPressureEpochVisibleAppliesBeforePressureReenabled = Math.Max(
                        0L,
                        helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount > 0
                            ? helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount
                            : framesAppliedSinceLastGap);
                }

                if (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal))
                {
                    helperRemoteCurrentPressureEpochSlowApplyCadenceTicks++;
                }
                else if (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal))
                {
                    helperRemoteCurrentPressureEpochHighFrameAgeTicks++;
                    helperRemoteCurrentPressureEpochActionableHighFrameAgeCount++;
                }
                else if (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops, StringComparison.Ordinal))
                {
                    helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks++;
                }
                else if (string.Equals(nextReason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal))
                {
                    helperRemoteCurrentPressureEpochBridgeHealthTicks++;
                }
            }
        }

        var loggedSampleSource =
            sampleSource == ScreenSharePressureSampleSource.BridgeHealth && !hasActionableBridgeHealth
                ? ScreenSharePressureSampleSource.AppliedFrameAge
                : sampleSource;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_pressure_state_sent; mode={nextMode}; reason={nextReason}; age_ms={effectiveObservedFrameAgeMs}; recent_stale_drops={effectiveStaleDropDelta}; recent_health_issues={recentHealthIssueCount}; health_kind={healthKind}; session_id={message.SessionId}; sample_source={FormatScreenSharePressureSampleSource(loggedSampleSource)}; current_epoch={helperPressureSnapshot.CurrentEpoch}; current_epoch_warmup_active={(currentEpochWarmupActive ? 1 : 0)}; current_epoch_apply_count={currentEpochApplyCount}; current_epoch_need_more_input_count={currentEpochNeedMoreInputCount}; current_epoch_stale_drops={currentEpochStaleDropCount}; current_epoch_recovery_active={(currentEpochRecoveryActive ? 1 : 0)}; helper_session_phase={ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(helperSessionPhase)}; helper_recovery_mechanism={ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(helperRecoveryMechanism)}; helper_baseline_established={(helperBaselineEstablished ? 1 : 0)}; current_epoch_progress_proven={(currentEpochProgressProven ? 1 : 0)}; current_epoch_progress_proof_source={FormatPressureTextValue(currentEpochProgressProofSource)}; current_epoch_proven_head_frame_id={FormatFrameIdForPressureLog(currentEpochProvenHeadFrameId)}; last_visible_apply_frame_id={FormatFrameIdForPressureLog(lastVisibleApplyFrameId)}; visible_head_frame_id={FormatFrameIdForPressureLog(visibleHeadFrameId)}; visible_recovery_floor_frame_id={FormatFrameIdForPressureLog(visibleRecoveryFloorFrameId)}; applied_head_frame_id={FormatFrameIdForPressureLog(helperPressureSnapshot.AppliedHeadFrameId)}; frames_applied_since_last_gap={framesAppliedSinceLastGap}; stable_visible_head_frame_id={FormatFrameIdForPressureLog(stableVisibleHeadFrameId)}; derived_post_recovery_healthy_active={(derivedPostRecoveryHealthyActive ? 1 : 0)}; derived_post_recovery_healthy_source={FormatPressureTextValue(helperPressureSnapshot.DerivedPostRecoveryHealthySource)}; derived_post_recovery_proof_frame_id={FormatFrameIdForPressureLog(helperPressureSnapshot.DerivedPostRecoveryProofFrameId)}; steady_visible_progress_active={(steadyVisibleProgressActive ? 1 : 0)}; steady_visible_progress_activation_frame_id={FormatFrameIdForPressureLog(helperPressureSnapshot.SteadyVisibleProgressActivationFrameId)}; applied_head_advanced_since_last_evaluation={(appliedHeadAdvancedSinceLastEvaluation ? 1 : 0)}; stable_visible_head_advanced_since_last_evaluation={(stableVisibleHeadAdvancedSinceLastEvaluation ? 1 : 0)}; helper_healthy_state_established_by={FormatPressureTextValue(currentEpochProgressProven ? currentEpochProgressProofSource : "none")}; non_healthy_clear_suppressed_due_to_progress_count={helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount}; last_sent_visible_head_frame_id={FormatFrameIdForPressureLog(visibleHeadFrameId)}; last_sent_stable_visible_head_frame_id={FormatFrameIdForPressureLog(stableVisibleHeadFrameId)}; pressure_send_bypassed_for_visible_progress_count={helperRemotePressureSendBypassedForVisibleProgressCount}; helper_proof_keepalive_send_count={helperRemoteProofKeepaliveSendCount}; helper_proof_keepalive_timer_driven_send_count={helperRemoteProofKeepaliveTimerDrivenSendCount}; helper_proof_keepalive_last_head_frame_id={FormatFrameIdForPressureLog(helperRemoteLastProofKeepaliveHeadFrameId)}; helper_proof_keepalive_last_send_age_ms={(helperRemoteLastProofKeepaliveSentUtc == default ? "(none)" : Math.Max(0L, (long)(nowUtc - helperRemoteLastProofKeepaliveSentUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture))}; helper_first_visible_apply_to_sender_fact_send_ms={(helperFirstVisibleApplyToSenderFactSendMs >= 0 ? helperFirstVisibleApplyToSenderFactSendMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; steady_visible_progress_cleared_count={helperPressureSnapshot.SteadyVisibleProgressClearedCount}; steady_visible_progress_cleared_reason={FormatPressureTextValue(helperPressureSnapshot.SteadyVisibleProgressClearedReason)}; recovery_lock_active={(currentEpochRecoveryActive ? 1 : 0)}; current_epoch_gap_count={currentEpochGapCount}; current_epoch_recovery_keyframe_apply_count={currentEpochRecoveryKeyframeApplyCount}; current_epoch_resync_count={currentEpochResyncCount}; post_recovery_age_grace_active={(helperPressureSnapshot.PostRecoveryAgeGraceActive ? 1 : 0)}; post_recovery_age_grace_suppressed_count={helperPressureSnapshot.PostRecoveryAgeGraceSuppressedCount}; baseline_established={(baselineEstablished ? 1 : 0)}; baseline_capture_to_render_ms={baselineCaptureToRenderMs}; age_excess_ms={ageExcessMs}; progress_stall_ms={progressStallMs}; baseline_reseed_in_progress={(baselineReseedInProgress ? 1 : 0)}; age_pressure_consecutive_count={agePressureConsecutiveCount}; cadence_pressure_consecutive_count={cadencePressureConsecutiveCount}; catch_up_suppressed_due_to_progress_count={catchUpSuppressedDueToProgressCount}; high_frame_age_suppressed_due_to_visible_progress_count={helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount}; high_frame_age_suppressed_due_to_head_advance_count={helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount}; actionable_high_frame_age_count={helperRemoteCurrentPressureEpochActionableHighFrameAgeCount}; time_since_last_visible_apply_ms={(timeSinceLastVisibleApplyMs >= 0 ? timeSinceLastVisibleApplyMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; time_spent_in_helper_warmup_ms={helperPressureSnapshot.TimeSpentInHelperWarmupMs}; last_apply_cadence_ms={(lastApplyCadenceMs >= 0 ? lastApplyCadenceMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; avg_apply_cadence_ms={(hasApplyCadenceSample ? averageApplyCadenceMs.ToString("F1", CultureInfo.InvariantCulture) : "(none)")}");

        lock (helperRemoteScreenSharePressureGate)
        {
            LogHelperRemoteScreenSharePressureSummary_NoLock("periodic");
        }

        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await screenShareTransport.SendScreenSharePressureStateAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogRemoteControlInfo("screenshare_pressure_state_send_failed", ex.GetType().Name, null, null);
                }
            },
            countAsTransportTask: false);
    }

    private void MaybeBeginHelperRemotePressureBaselineReseedAfterTrustedVisibleProgress_NoLock(
        DateTimeOffset nowUtc,
        long currentEpoch)
    {
        if (helperRemoteCurrentPressureEpoch != currentEpoch ||
            !helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending ||
            helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply ||
            helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies > 0)
        {
            return;
        }

        BeginHelperRemotePressureBaselineReseedAfterStall_NoLock(nowUtc);
    }

}
