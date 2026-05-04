using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    private const int HelperRemoteScreenShareMinimumWarmupApplies = 3;
    private const int HelperRemoteScreenShareCadencePressureMinimumApplies = 4;
    private const long HelperRemoteScreenShareHealthyApplyCadenceThresholdMs = 300;
    private const long HelperRemoteScreenShareHealthyApplyCadenceBurstThresholdMs = 400;
    private const long HelperRemoteScreenShareReduceApplyCadenceThresholdMs = 350;
    private const long HelperRemoteScreenShareReduceApplyCadenceBurstThresholdMs = 500;
    private const long HelperRemoteScreenShareCatchUpApplyCadenceThresholdMs = 700;
    private const long HelperRemoteScreenShareCatchUpApplyCadenceBurstThresholdMs = 900;
    private const int HelperRemoteScreenShareBaselineEstablishVisibleApplies = 8;
    private const int HelperRemoteScreenSharePressureConsecutiveThreshold = 3;
    private const long HelperRemoteScreenShareAgeExcessReduceThresholdMs = 250;
    private const long HelperRemoteScreenShareAgeExcessCatchUpThresholdMs = 900;
    private const long HelperRemoteScreenShareCadencePressureThresholdMs = 500;
    private const double HelperRemoteScreenShareBaselineEwmaAlpha = 0.25d;
    private const long HelperRemoteScreenShareBaselineReseedEligibleAgeThresholdMs = 900;
    private static readonly TimeSpan HelperRemoteScreenShareEpochWarmupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HelperRemoteScreenShareRecoveryOnlyWindow = TimeSpan.FromSeconds(3);
    private const int HelperRemoteScreenSharePostRecoveryMinimumApplies = 3;
    private const int HelperRemoteScreenShareBaselineReseedVisibleApplies = 3;
    private static readonly TimeSpan HelperRemoteScreenShareBaselineReseedTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan HelperRemoteScreenSharePostRecoveryStabilizationWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelperRemoteScreenSharePostRecoveryVisibleProgressWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HelperRemoteScreenSharePostRecoveryAgeGraceWindow = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan HelperRemoteScreenSharePostRecoveryHealthyLatchStallTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan HelperRemoteScreenSharePressureReevaluationInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HelperRemoteScreenShareProofKeepaliveInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HelperRemoteScreenShareRecoveryReceiptRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HelperRemoteScreenShareCadenceStallTriggerWindow = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan HelperRemoteScreenShareBridgeHealthQuarantineWindow = TimeSpan.FromMilliseconds(1500);
    private long helperRemoteLastReportedAppliedFrameEpoch = -1;
    private long helperRemoteLastReportedAppliedFrameId = -1;
    private HelperRemoteSessionSnapshot helperRemoteLastReportedSessionSnapshot = default;
    private DateTimeOffset helperRemoteLastReportedSessionSnapshotUtc;
    private bool helperRemoteLastAppliedHeadAdvancedSincePressureEvaluation;
    private bool helperRemoteLastStableVisibleHeadAdvancedSincePressureEvaluation;
    private string helperRemoteLastHealthyStateEstablishedBy = "none";
    private long helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount;

    private enum ScreenSharePressureSampleSource
    {
        AppliedFrameAge = 0,
        ApplyCadence = 1,
        StaleDropOnly = 2,
        BridgeHealth = 3,
    }

    private readonly record struct HelperRemoteScreenSharePressureSnapshot(
        bool HasAppliedFrame,
        long LastAppliedFrameAgeMs,
        int RecentAppliedHighFrameCount,
        int ConsecutiveVeryHighAppliedFrames,
        long LastApplyCadenceMs,
        double AverageApplyCadenceMs,
        long ViewerStaleDropCount,
        long ViewerSoftStaleDropCount,
        long CurrentEpoch,
        bool CurrentEpochFirstApplySeen,
        bool CurrentEpochWarmupActive,
        int CurrentEpochApplyCount,
        long CurrentEpochNeedMoreInputCount,
        long CurrentEpochStaleDropCount,
        long CurrentEpochSoftStaleDropCount,
        long LastVisibleApplyFrameId,
        long VisibleHeadFrameId,
        long VisibleRecoveryFloorFrameId,
        long AppliedHeadFrameId,
        long FramesAppliedSinceLastGap,
        long StableVisibleHeadFrameId,
        long CurrentEpochGapCount,
        long CurrentEpochRecoveryKeyframeApplyCount,
        long CurrentEpochResyncCount,
        bool CurrentEpochRecoveryActive,
        DateTimeOffset CurrentEpochRecoveryStartedUtc,
        bool CurrentEpochRecoveryTimeoutSent,
        bool CurrentEpochPostRecoveryStabilizationActive,
        bool CurrentEpochPostRecoveryHealthySignalSent,
        HelperRemoteSessionPhase HelperSessionPhase,
        HelperRemoteRecoveryMechanism HelperRecoveryMechanism,
        bool HelperBaselineEstablished,
        bool CurrentEpochProgressProven,
        string CurrentEpochProgressProofSource,
        long CurrentEpochProvenHeadFrameId,
        long TimeSinceLastVisibleApplyMs,
        bool BaselineEstablished,
        long BaselineCaptureToRenderMs,
        long AgeExcessMs,
        long ProgressStallMs,
        bool BaselineReseedInProgress,
        int AgePressureConsecutiveCount,
        int CadencePressureConsecutiveCount,
        long CatchUpSuppressedDueToProgressCount,
        long BaselineFrozenDueToStallCount,
        long BaselineReseedAfterRecoveryCount,
        long CadenceStallWindowCount,
        long CadenceStallTriggerCount,
        bool DerivedPostRecoveryHealthyActive,
        string DerivedPostRecoveryHealthySource,
        long DerivedPostRecoveryProofFrameId,
        bool SteadyVisibleProgressActive,
        long SteadyVisibleProgressActivationFrameId,
        long LastSentVisibleHeadFrameId,
        long LastSentStableVisibleHeadFrameId,
        long PressureSendBypassedForVisibleProgressCount,
        long ProofKeepaliveSendCount,
        long ProofKeepaliveTimerDrivenSendCount,
        long ProofKeepaliveLastHeadFrameId,
        long ProofKeepaliveLastSendAgeMs,
        long SteadyVisibleProgressClearedCount,
        string SteadyVisibleProgressClearedReason,
        long PostRecoveryHealthyLatchCount,
        long PostRecoveryHealthyLatchClearCount,
        string PostRecoveryHealthyLatchClearReason,
        bool PostRecoveryAgeGraceActive,
        long PostRecoveryAgeGraceSuppressedCount,
        long BridgeHealthAdvisoryCount,
        long BridgeHealthActionableCount,
        long BridgeHealthQuarantineSuppressedCount,
        long BridgeHealthActionableWithoutQueueOrDropCount,
        long TimeSpentInHelperWarmupMs,
        long VisibleAppliesDuringSettleCount,
        long PostRecoverySettleWindowCount,
        long PostRecoverySettleWindowSuccessCount,
        long PostRecoverySettleWindowTimeoutCount,
        long VisibleAppliesBeforePressureReenabled,
        bool AppliedHeadAdvancedSinceLastEvaluation,
        bool StableVisibleHeadAdvancedSinceLastEvaluation,
        string HelperHealthyStateEstablishedBy,
        long NonHealthyClearSuppressedDueToProgressCount);

    internal sealed record HelperRemoteScreenSharePressureDiagnosticsSnapshot(
        long StreamEpoch,
        long LastVisibleApplyFrameId,
        long VisibleHeadFrameId,
        long VisibleRecoveryFloorFrameId,
        long AppliedHeadFrameId,
        long FramesAppliedSinceLastGap,
        long StableVisibleHeadFrameId,
        long CurrentEpochGapCount,
        long CurrentEpochRecoveryKeyframeApplyCount,
        long CurrentEpochResyncCount,
        bool RecoveryWindowActive,
        bool RecoveryWindowProgressed,
        bool RecoveryWindowSucceeded,
        long RecoveryWindowProgressedCount,
        long RecoveryWindowSuccessCount,
        long ActiveRecoveryWindowEpoch,
        long ActiveRecoveryWindowRecoveryFrameId,
        long RecoveryWindowContiguousFollowerApplyCount,
        long ContinuityLossTicks,
        long WarmupTicks,
        long BeforeFirstVisibleApplyTicks,
        long AfterVisibleRecoveryFrameTicks,
        long AfterVisibleRecoveryFrameSuppressedDueToSuccessCount,
        long SlowApplyCadenceTicks,
        long HighFrameAgeTicks,
        long HighFrameAgeSuppressedDueToVisibleProgressCount,
        long HighFrameAgeSuppressedDueToHeadAdvanceCount,
        long ActionableHighFrameAgeCount,
        long PostRecoveryHighFrameAgeSuppressedTicks,
        long RepeatedStaleDropsTicks,
        long BridgeHealthTicks,
        bool BaselineEstablished,
        long BaselineCaptureToRenderMs,
        long AgeExcessMs,
        long ProgressStallMs,
        bool BaselineReseedInProgress,
        int AgePressureConsecutiveCount,
        int CadencePressureConsecutiveCount,
        long CatchUpSuppressedDueToProgressCount,
        long BaselineFrozenDueToStallCount,
        long BaselineReseedAfterRecoveryCount,
        long CadenceStallWindowCount,
        long CadenceStallTriggerCount,
        bool DerivedPostRecoveryHealthyActive,
        string DerivedPostRecoveryHealthySource,
        long DerivedPostRecoveryProofFrameId,
        bool SteadyVisibleProgressActive,
        long SteadyVisibleProgressActivationFrameId,
        long LastSentVisibleHeadFrameId,
        long LastSentStableVisibleHeadFrameId,
        long PressureSendBypassedForVisibleProgressCount,
        long ProofKeepaliveSendCount,
        long ProofKeepaliveTimerDrivenSendCount,
        long ProofKeepaliveLastHeadFrameId,
        long ProofKeepaliveLastSendAgeMs,
        long SteadyVisibleProgressClearedCount,
        string SteadyVisibleProgressClearedReason,
        long PostRecoveryHealthyLatchCount,
        long PostRecoveryHealthyLatchClearCount,
        string PostRecoveryHealthyLatchClearReason,
        bool PostRecoveryAgeGraceActive,
        long PostRecoveryAgeGraceSuppressedCount,
        long BridgeHealthAdvisoryCount,
        long BridgeHealthActionableCount,
        long BridgeHealthQuarantineSuppressedCount,
        long BridgeHealthActionableWithoutQueueOrDropCount,
        HelperRemoteSessionPhase HelperSessionPhase,
        HelperRemoteRecoveryMechanism HelperRecoveryMechanism,
        bool HelperBaselineEstablished,
        bool CurrentEpochProgressProven,
        string CurrentEpochProgressProofSource,
        long CurrentEpochProvenHeadFrameId,
        bool AppliedHeadAdvancedSinceLastEvaluation,
        bool StableVisibleHeadAdvancedSinceLastEvaluation,
        string HelperHealthyStateEstablishedBy,
        long NonHealthyClearSuppressedDueToProgressCount,
        long TimeSpentInHelperWarmupMs,
        long VisibleAppliesDuringSettleCount,
        long PostRecoverySettleWindowCount,
        long PostRecoverySettleWindowSuccessCount,
        long PostRecoverySettleWindowTimeoutCount,
        long VisibleAppliesBeforePressureReenabled,
        string DominantPressureBlocker);

    private enum HelperRemoteRecoveryWindowStatus
    {
        Unknown = 0,
        Started,
        FollowerApplied,
        Succeeded,
        Aborted,
    }

    private readonly record struct HelperRemoteRecoveryReceiptPublicationCandidate(
        long StreamEpoch,
        long OwnerFrameId,
        long VisibleRecoveryFrameId,
        long VisibleHeadFrameId,
        string ReceiptKind,
        ScreenShareRecoveryReceiptV1 Message);

    internal Task<bool> DispatchRemoteControlHelperEventAsync(
        RemoteControlReducerEventKind eventKind,
        string? reason = null,
        CancellationToken uiCt = default)
    {
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "ui_event" : reason.Trim();
        return eventKind switch
        {
            RemoteControlReducerEventKind.HelperRequestClicked => RequestRemoteControlAsync(uiCt),
            RemoteControlReducerEventKind.HelperStopClicked => StopRemoteControlAsync(effectiveReason, uiCt),
            _ => Task.FromResult(false),
        };
    }

    private void ApplyRemoteControlReducerTransition(
        in RemoteControlReducerEvent evt,
        bool notifyStateChanged = true)
    {
        var reducerReason = evt.Reason;
        var eventUnixMs = nowProvider().ToUnixTimeMilliseconds();
        var reducerEvent = evt.OccurredAtUnixMs.HasValue
            ? evt
            : evt with { OccurredAtUnixMs = eventUnixMs };

        var transition = RemoteControlReducerWiring.Reduce(remoteControlSessionState, reducerEvent);
        var previousState = transition.PreviousState;
        remoteControlSessionState = transition.NextState;
        var requestIdChanged = !string.Equals(
            previousState.CurrentControlRequestId,
            transition.NextState.CurrentControlRequestId,
            StringComparison.Ordinal);
        var controlStopped = previousState.ControlState != ControlState.Off &&
                             transition.NextState.ControlState == ControlState.Off;

        if (requestIdChanged)
        {
            ResetRemoteControlRequestScopedTracking("request_id_changed");
        }

        UpdateRemoteControlStatusHint(reducerEvent.Reason, transition.NextState.ControlState);

        RemoteControlReducerWiring.ExecuteSideEffects(
            transition,
            effect => ExecuteRemoteControlReducerSideEffect(effect, reducerReason));

        if (previousState.ControlState != ControlState.Active &&
            transition.NextState.ControlState == ControlState.Active)
        {
            Interlocked.Exchange(ref remoteControlStopInputSuppressionLatched, 0);
            if (role == SessionRuntimeRole.Helpee)
            {
                MarkForceNextMoveInjectionLog("control_became_active");
            }
        }

        if (controlStopped && role == SessionRuntimeRole.Helpee)
        {
            ClearRemoteControlAppliedInputState("control_stopped");
        }

        if (role != SessionRuntimeRole.Helpee || transition.NextState.ControlState != ControlState.Active)
        {
            ClearRemoteControlAdminRestartWarning("control_not_active");
        }

        SyncTransportScreenShareCursorCaptureForRemoteControl(reducerEvent.Reason);

        if (!notifyStateChanged)
        {
            SyncFileTransferFlowControlMode();
            return;
        }

        if (!previousState.Equals(transition.NextState))
        {
            LogRemoteControlTransition(previousState, transition.NextState, reducerEvent.Reason);
            NotifyRemoteControlStateChanged();
        }
        else
        {
            SyncFileTransferFlowControlMode();
        }
    }

    private void ExecuteRemoteControlReducerSideEffect(RemoteControlSideEffect effect, string defaultReason)
    {
        var reason = string.IsNullOrWhiteSpace(effect.Reason) ? defaultReason : effect.Reason!;
        switch (effect.Kind)
        {
            case RemoteControlSideEffectKind.ScheduleTimeout:
            case RemoteControlSideEffectKind.StartTimer:
            {
                var requestId = effect.RequestId ?? remoteControlSessionState.CurrentControlRequestId;
                if (!effect.TimeoutKind.HasValue)
                {
                    break;
                }

                var timeoutKind = effect.TimeoutKind.Value;
                var deadlineUnixMs = effect.DeadlineUnixMs.GetValueOrDefault(
                    nowProvider().ToUnixTimeMilliseconds() + ResolveRemoteControlTimeoutMs(timeoutKind, effect.TimeoutMs));
                ScheduleRemoteControlTimeout(timeoutKind, requestId, deadlineUnixMs, reason);
                break;
            }
            case RemoteControlSideEffectKind.CancelTimeouts:
            case RemoteControlSideEffectKind.StopTimer:
                CancelRemoteControlRequestTimeout();
                CancelRemoteControlConsentTimeout();
                CancelRemoteControlDeniedCooldown();
                break;
            case RemoteControlSideEffectKind.FlushOutgoingMouseMoves:
                ClearQueuedRemoteControlMouseMoves("reducer:" + reason);
                break;
            case RemoteControlSideEffectKind.FlushInjectionQueue:
                ClearQueuedRemoteControlInjections("reducer:" + reason);
                break;
            case RemoteControlSideEffectKind.SetConsentPromptVisible:
                hasPendingRemoteControlConsentPrompt = effect.BoolValue == true;
                break;
            case RemoteControlSideEffectKind.Log:
                LogRemoteControlInfo(
                    "reducer",
                    reason,
                    effect.RequestId,
                    effect.PeerId);
                break;
            case RemoteControlSideEffectKind.SendControlRequest:
                // Keep request send synchronous in RequestRemoteControlAsync to preserve
                // deterministic command semantics for helper UI/actions.
                break;
            case RemoteControlSideEffectKind.SendControlResponse:
                if (Interlocked.CompareExchange(ref suppressNextReducerSendControlResponse, 0, 1) == 1)
                {
                    break;
                }

                ExecuteRemoteControlReducerSendSideEffect(effect, reason);
                break;
            case RemoteControlSideEffectKind.SendControlStart:
            case RemoteControlSideEffectKind.SendControlStop:
                ExecuteRemoteControlReducerSendSideEffect(effect, reason);
                break;
            case RemoteControlSideEffectKind.SetControlModeEnabled:
            case RemoteControlSideEffectKind.None:
            default:
                // No-op for runtime-level effects handled in page VMs.
                break;
        }
    }

    private void ExecuteRemoteControlReducerSendSideEffect(RemoteControlSideEffect effect, string reason)
    {
        RunCountedBackgroundTask(
            () => ExecuteRemoteControlReducerSendSideEffectAsync(effect, reason),
            countAsTransportTask: false);
    }

    private async Task ExecuteRemoteControlReducerSendSideEffectAsync(RemoteControlSideEffect effect, string reason)
    {
        IRemoteControlSignalingTransport? controlTransport = null;
        var requestId = effect.RequestId;
        var controllerPeerId = effect.PeerId;
        var consentToken = effect.ConsentToken;
        var stopEpochSnapshot = SnapshotRemoteControlStopPriorityEpoch();

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress)
            {
                return;
            }

            if (transport is not IRemoteControlSignalingTransport rt)
            {
                LogRemoteControlViolation("reducer_send_skipped", "transport_missing_control_channel", requestId, controllerPeerId);
                return;
            }

            controlTransport = rt;
            requestId = string.IsNullOrWhiteSpace(requestId) ? remoteControlSessionState.CurrentControlRequestId : requestId;
            controllerPeerId = string.IsNullOrWhiteSpace(controllerPeerId) ? remoteControlSessionState.ControllerPeerId : controllerPeerId;
            if (string.IsNullOrWhiteSpace(consentToken))
            {
                consentToken = remoteControlSessionState.ConsentToken;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        switch (effect.Kind)
        {
            case RemoteControlSideEffectKind.SendControlRequest:
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    LogRemoteControlViolation("request_send_skipped", "missing_request_id");
                    return;
                }

                if (!RequireRemoteControlAuxiliaryCapability(
                        "remote_control_request_send",
                        "request_send_skipped",
                        requestId,
                        controllerPeerId))
                {
                    await ApplyRemoteControlLocalStopFallbackAsync("request_capability_not_granted", requestId, controllerPeerId).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await controlTransport.SendControlRequestAsync(
                        new ControlRequestMessageV1
                        {
                            RequestId = requestId,
                            Caps = new[] { "mouse", "keyboard" },
                            Reason = reason,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    LogRemoteControlInfo("request_sent", "caps=mouse,keyboard", requestId, controllerPeerId);
                }
                catch (Exception ex)
                {
                    LogRemoteControlViolation("request_send_failed", ex.GetType().Name, requestId, controllerPeerId);
                    await ApplyRemoteControlLocalStopFallbackAsync("request_send_failed", requestId, controllerPeerId).ConfigureAwait(false);
                }

                return;

            case RemoteControlSideEffectKind.SendControlResponse:
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    LogRemoteControlViolation("response_send_skipped", "missing_request_id", requestId, controllerPeerId);
                    return;
                }

                var decision = effect.Decision switch
                {
                    RemoteControlReducerResponseDecision.Allow => "allow",
                    _ => "deny",
                };
                var response = string.Equals(decision, "allow", StringComparison.Ordinal)
                    ? new ControlResponseMessageV1
                    {
                        RequestId = requestId,
                        Decision = decision,
                        ConsentToken = consentToken,
                        TtlMs = RemoteControlConsentTokenTtlMs,
                    }
                    : new ControlResponseMessageV1
                    {
                        RequestId = requestId,
                        Decision = decision,
                        Reason = reason,
                    };

                try
                {
                    await controlTransport.SendControlResponseAsync(response, CancellationToken.None).ConfigureAwait(false);
                    LogRemoteControlInfo("consent_response_sent", response.Decision ?? "(none)", requestId, controllerPeerId);
                }
                catch (Exception ex)
                {
                    LogRemoteControlViolation("consent_response_send_failed", ex.GetType().Name, requestId, controllerPeerId);
                    if (string.Equals(decision, "allow", StringComparison.Ordinal))
                    {
                        await ApplyRemoteControlLocalStopFallbackAsync("consent_allow_send_failed", requestId, controllerPeerId).ConfigureAwait(false);
                    }
                }

                return;

            case RemoteControlSideEffectKind.SendControlStart:
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    LogRemoteControlViolation("start_send_skipped", "missing_request_id", requestId, controllerPeerId);
                    return;
                }

                if (HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
                {
                    LogRemoteControlInfo("start_send_skipped", "stop_priority", requestId, controllerPeerId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(consentToken))
                {
                    LogRemoteControlViolation("start_send_skipped", "missing_consent_token", requestId, controllerPeerId);
                    await ApplyRemoteControlLocalStopFallbackAsync("response_missing_token", requestId, controllerPeerId).ConfigureAwait(false);
                    return;
                }

                if (!RequireRemoteControlAuxiliaryCapability(
                        "remote_control_start_send",
                        "start_send_skipped",
                        requestId,
                        controllerPeerId))
                {
                    await ApplyRemoteControlLocalStopFallbackAsync("start_capability_not_granted", requestId, controllerPeerId).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await controlTransport.SendControlStartAsync(
                        new ControlStartMessageV1
                        {
                            RequestId = requestId,
                            ConsentToken = consentToken,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    LogRemoteControlInfo("start_sent", "allow", requestId, controllerPeerId);
                }
                catch (Exception ex)
                {
                    LogRemoteControlViolation("start_send_failed", ex.GetType().Name, requestId, controllerPeerId);
                    await ApplyRemoteControlLocalStopFallbackAsync("start_send_failed", requestId, controllerPeerId).ConfigureAwait(false);
                    return;
                }

                await lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
                    {
                        LogRemoteControlInfo("start_activate_skipped", "stop_priority", requestId, controllerPeerId);
                        return;
                    }

                    if (!disposed &&
                        role == SessionRuntimeRole.Helper &&
                        remoteControlSessionState.ControlState == ControlState.Requesting &&
                        string.Equals(remoteControlSessionState.CurrentControlRequestId, requestId, StringComparison.Ordinal))
                    {
                        ApplyRemoteControlReducerTransition(
                            new RemoteControlReducerEvent(
                                RemoteControlReducerEventKind.TransportControlStartReceived,
                                "start_sent_active",
                                RequestId: requestId,
                                PeerId: "local-helper"));
                    }
                }
                finally
                {
                    lifecycleGate.Release();
                }

                return;

            case RemoteControlSideEffectKind.SendControlStop:
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    return;
                }

                var stopReason = reason.StartsWith("local_stop:", StringComparison.Ordinal)
                    ? reason["local_stop:".Length..]
                    : reason;

                try
                {
                    await controlTransport.SendControlStopAsync(
                        new ControlStopMessageV1
                        {
                            RequestId = requestId,
                            Reason = stopReason,
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    LogRemoteControlInfo("stop_sent", stopReason, requestId, controllerPeerId);
                }
                catch (Exception ex)
                {
                    LogRemoteControlViolation("stop_send_failed", ex.GetType().Name, requestId, controllerPeerId);
                }

                return;
        }
    }

    private async Task ApplyRemoteControlLocalStopFallbackAsync(string reason, string? requestId, string? peerId)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            MarkRemoteControlStopPriority(reason, requestId, peerId);
            ApplyRemoteControlReducerTransition(
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.TransportControlStopReceived,
                    reason,
                    RequestId: requestId,
                    PeerId: peerId));
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task<bool> RequestRemoteControlAsync(CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.RemoteControlRequest, "remote_control_request"),
            ct => remoteControlActions.RequestAsync(ct),
            deniedValue: false,
            uiCt);
    }

    internal async Task<bool> RequestRemoteControlCoreAsync(CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IRemoteControlSignalingTransport? controlTransport;
        ControlRequestMessageV1? request = null;

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (role != SessionRuntimeRole.Helper ||
                state != SessionRuntimeState.Connected ||
                !SessionSupportsRemoteControl ||
                remoteControlSessionState.ControlState != ControlState.Off)
            {
                LogRemoteControlInfo("request_ignored", "invalid_state");
                return false;
            }

            if (transport is not IRemoteControlSignalingTransport nextControlTransport)
            {
                LogRemoteControlViolation("request_failed", "transport_missing_control_channel");
                return false;
            }

            var requestId = Guid.NewGuid().ToString("N");
            controlTransport = nextControlTransport;
            request = new ControlRequestMessageV1
            {
                RequestId = requestId,
                Caps = new[] { "mouse", "keyboard" },
                Reason = "helper_request",
            };

            hasPendingRemoteControlConsentPrompt = false;
            ClearPendingRemoteControlConsentToken();
            ApplyRemoteControlReducerTransition(
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.HelperRequestClicked,
                    "helper_request_started",
                    RequestId: requestId,
                    TimeoutKind: RemoteControlReducerTimeoutKind.Request,
                    TimeoutMs: (long)RemoteControlRequestTimeout.TotalMilliseconds));
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (controlTransport is null || request is null)
        {
            return false;
        }

        try
        {
            await controlTransport.SendControlRequestAsync(request, uiCt).ConfigureAwait(false);
            LogRemoteControlInfo("request_sent", "caps=mouse,keyboard", request.RequestId);
            return true;
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("request_send_failed", ex.GetType().Name, request.RequestId);
            await ApplyRemoteControlLocalStopFallbackAsync("request_send_failed", request.RequestId, null).ConfigureAwait(false);
            return false;
        }
    }

    public Task<bool> RespondToRemoteControlRequestAsync(bool allow, CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.RemoteControlRespond, "remote_control_respond"),
            ct => remoteControlActions.RespondAsync(allow, ct),
            deniedValue: false,
            uiCt);
    }

    internal async Task<bool> RespondToRemoteControlRequestCoreAsync(bool allow, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IRemoteControlSignalingTransport? controlTransport = null;
        ControlResponseMessageV1? response = null;
        string? controllerPeerId = null;

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (role != SessionRuntimeRole.Helpee ||
                state != SessionRuntimeState.Connected ||
                !hasPendingRemoteControlConsentPrompt ||
                remoteControlSessionState.ControlState != ControlState.Requesting ||
                string.IsNullOrWhiteSpace(remoteControlSessionState.CurrentControlRequestId))
            {
                LogRemoteControlInfo("consent_ignored", "invalid_state");
                return false;
            }

            if (transport is not IRemoteControlSignalingTransport nextControlTransport)
            {
                MarkRemoteControlStopPriority(
                    "consent_transport_missing",
                    remoteControlSessionState.CurrentControlRequestId,
                    remoteControlSessionState.ControllerPeerId);
                ApplyRemoteControlReducerTransition(
                    new RemoteControlReducerEvent(
                        RemoteControlReducerEventKind.TransportControlStopReceived,
                        "consent_transport_missing",
                        RequestId: remoteControlSessionState.CurrentControlRequestId,
                        PeerId: remoteControlSessionState.ControllerPeerId));
                LogRemoteControlViolation("consent_failed", "transport_missing_control_channel");
                return false;
            }

            controlTransport = nextControlTransport;
            var requestId = remoteControlSessionState.CurrentControlRequestId!;
            controllerPeerId = remoteControlSessionState.ControllerPeerId;
            if (allow)
            {
                    var controllerPeerIdValue = string.IsNullOrWhiteSpace(remoteControlSessionState.ControllerPeerId)
                        ? null
                        : remoteControlSessionState.ControllerPeerId;
                if (string.IsNullOrWhiteSpace(controllerPeerIdValue))
                {
                    response = new ControlResponseMessageV1
                    {
                        RequestId = requestId,
                        Decision = "deny",
                        Reason = "missing_controller_peer",
                    };
                    Interlocked.Exchange(ref suppressNextReducerSendControlResponse, 1);
                    try
                    {
                        ApplyRemoteControlReducerTransition(
                            new RemoteControlReducerEvent(
                                RemoteControlReducerEventKind.HelpeeConsentDenied,
                                "helpee_allow_rejected_missing_peer",
                                RequestId: requestId,
                                PeerId: controllerPeerIdValue,
                                TimeoutKind: RemoteControlReducerTimeoutKind.DeniedCooldown,
                                TimeoutMs: (long)RemoteControlDeniedCooldown.TotalMilliseconds));
                    }
                    finally
                    {
                        Interlocked.Exchange(ref suppressNextReducerSendControlResponse, 0);
                    }
                }
                else
                {
                    var consentToken = GenerateRemoteControlConsentToken();
                    pendingRemoteControlConsentToken = new PendingRemoteControlConsentToken(
                        requestId,
                        controllerPeerIdValue,
                        ComputeRemoteControlConsentTokenHash(requestId, controllerPeerIdValue, consentToken),
                        DateTimeOffset.UtcNow.AddMilliseconds(RemoteControlConsentTokenTtlMs));
                    response = new ControlResponseMessageV1
                    {
                        RequestId = requestId,
                        Decision = "allow",
                        ConsentToken = consentToken,
                        TtlMs = RemoteControlConsentTokenTtlMs,
                    };
                    LogRemoteControlInfo(
                        "token_issued",
                        "allow",
                        requestId,
                        controllerPeerIdValue,
                        tokenDecision: "issued");

                    Interlocked.Exchange(ref suppressNextReducerSendControlResponse, 1);
                    try
                    {
                        ApplyRemoteControlReducerTransition(
                            new RemoteControlReducerEvent(
                                RemoteControlReducerEventKind.HelpeeConsentAllowed,
                                "helpee_allowed_waiting_start",
                                RequestId: requestId,
                                PeerId: controllerPeerIdValue,
                                ConsentToken: consentToken,
                                TimeoutKind: RemoteControlReducerTimeoutKind.StartAwait,
                                TimeoutMs: (long)RemoteControlStartAwaitTimeout.TotalMilliseconds));
                    }
                    finally
                    {
                        Interlocked.Exchange(ref suppressNextReducerSendControlResponse, 0);
                    }
                }
            }
            else
            {
                ClearPendingRemoteControlConsentToken();
                response = new ControlResponseMessageV1
                {
                    RequestId = requestId,
                    Decision = "deny",
                    Reason = "helpee_denied",
                };
                Interlocked.Exchange(ref suppressNextReducerSendControlResponse, 1);
                try
                {
                    ApplyRemoteControlReducerTransition(
                        new RemoteControlReducerEvent(
                            RemoteControlReducerEventKind.HelpeeConsentDenied,
                            "helpee_denied",
                            RequestId: requestId,
                            PeerId: remoteControlSessionState.ControllerPeerId,
                            TimeoutKind: RemoteControlReducerTimeoutKind.DeniedCooldown,
                            TimeoutMs: (long)RemoteControlDeniedCooldown.TotalMilliseconds));
                }
                finally
                {
                    Interlocked.Exchange(ref suppressNextReducerSendControlResponse, 0);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (controlTransport is null || response is null)
        {
            return false;
        }

        try
        {
            await controlTransport.SendControlResponseAsync(response, uiCt).ConfigureAwait(false);
            LogRemoteControlInfo("consent_response_sent", response.Decision ?? "(none)", response.RequestId, controllerPeerId);
            return true;
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("consent_response_send_failed", ex.GetType().Name, response.RequestId, controllerPeerId);
            if (string.Equals(response.Decision, "allow", StringComparison.Ordinal))
            {
                await ApplyRemoteControlLocalStopFallbackAsync("consent_allow_send_failed", response.RequestId, controllerPeerId).ConfigureAwait(false);
            }

            return false;
        }

    }

    public Task<bool> StopRemoteControlAsync(string reason, CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.RemoteControlStop, "remote_control_stop"),
            ct => remoteControlActions.StopAsync(reason, ct),
            deniedValue: false,
            uiCt);
    }

    internal async Task<bool> StopRemoteControlCoreAsync(string reason, CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        reason = string.IsNullOrWhiteSpace(reason) ? "stopped" : reason.Trim();

        string? requestIdForLog = null;
        string? controllerPeerIdForLog = null;

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (remoteControlSessionState.ControlState == ControlState.Off)
            {
                return false;
            }

            requestIdForLog = remoteControlSessionState.CurrentControlRequestId;
            controllerPeerIdForLog = remoteControlSessionState.ControllerPeerId;

            MarkRemoteControlStopPriority("local_stop:" + reason, requestIdForLog, controllerPeerIdForLog);
            ApplyRemoteControlReducerTransition(
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.HelperStopClicked,
                    "local_stop:" + reason,
                    RequestId: requestIdForLog,
                    PeerId: controllerPeerIdForLog));
        }
        finally
        {
            lifecycleGate.Release();
        }

        return true;
    }

    public Task<bool> SendRemoteControlInputAsync(ControlInputMessageV1 message, CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.RemoteControlInputSend, "remote_control_input_send"),
            ct => remoteControlActions.SendInputAsync(message, ct),
            deniedValue: false,
            uiCt);
    }

    internal Task<bool> SendRemoteControlInputCoreAsync(ControlInputMessageV1 message, CancellationToken uiCt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsLowPriorityMouseMoveInput(message))
        {
            return SendRemoteControlInputImmediateAsync(message, uiCt);
        }

        lock (remoteControlMouseMoveQueueGate)
        {
            // Low-priority lane: keep only the newest mouse move and drop older pending ones.
            queuedRemoteControlMouseMove = message;
            if (remoteControlMouseMoveSenderActive)
            {
                return Task.FromResult(true);
            }

            remoteControlMouseMoveSenderActive = true;
        }

        RunCountedBackgroundTask(
            DrainQueuedRemoteControlMouseMovesAsync,
            countAsTransportTask: false);
        return Task.FromResult(true);
    }

    public Task<bool> SendRemoteControlStateSnapshotAsync(ControlStateSnapshotV1 snapshot, CancellationToken uiCt = default)
    {
        return privilegedCommandExecutor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.RemoteControlSnapshotSend, "remote_control_snapshot_send"),
            ct => remoteControlActions.SendStateSnapshotAsync(snapshot, ct),
            deniedValue: false,
            uiCt);
    }

    internal async Task<bool> SendRemoteControlStateSnapshotCoreAsync(ControlStateSnapshotV1 snapshot, CancellationToken uiCt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!FeatureFlags.RemoteControlStateSnapshotEnabled)
        {
            return false;
        }

        IRemoteControlSignalingTransport? controlTransport;
        ControlStateSnapshotV1 outboundSnapshot;
        string? requestIdForLog = null;
        string? controllerPeerIdForLog = null;

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (disposed ||
                resetInProgress ||
                role != SessionRuntimeRole.Helper ||
                state != SessionRuntimeState.Connected ||
                !SessionSupportsRemoteControl ||
                remoteControlSessionState.ControlState != ControlState.Active)
            {
                if (ShouldEmitRemoteControlRateLimitedLog("state_snapshot_send_ignored:invalid_runtime_state"))
                {
                    LogRemoteControlInfo("state_snapshot_send_ignored", "invalid_runtime_state");
                }
                return false;
            }

            if (transport is not IRemoteControlSignalingTransport nextControlTransport)
            {
                LogRemoteControlViolation("state_snapshot_send_failed", "transport_missing_control_channel");
                return false;
            }

            var requestId = remoteControlSessionState.CurrentControlRequestId;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                LogRemoteControlViolation("state_snapshot_send_failed", "missing_request_id");
                return false;
            }

            if (snapshot.Seq <= 0)
            {
                LogRemoteControlViolation("state_snapshot_send_failed", "invalid_seq", requestId);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.RequestId) &&
                !string.Equals(snapshot.RequestId, requestId, StringComparison.Ordinal))
            {
                LogRemoteControlViolation("state_snapshot_send_failed", "request_id_mismatch", snapshot.RequestId);
                return false;
            }

            requestIdForLog = requestId;
            controllerPeerIdForLog = remoteControlSessionState.ControllerPeerId;
            var tsUtcMs = snapshot.TsUtcMs > 0
                ? snapshot.TsUtcMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            outboundSnapshot = snapshot with
            {
                RequestId = requestId,
                TsUtcMs = tsUtcMs,
            };
            controlTransport = nextControlTransport;
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await controlTransport.SendControlStateSnapshotAsync(outboundSnapshot, uiCt).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("state_snapshot_send_failed", ex.GetType().Name, requestIdForLog, controllerPeerIdForLog);
            return false;
        }
    }

    private async Task<bool> SendRemoteControlInputImmediateAsync(ControlInputMessageV1 message, CancellationToken uiCt = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(disposed, this);

        IRemoteControlSignalingTransport? controlTransport;
        ControlInputMessageV1 outboundMessage;
        string? requestIdForLog = null;
        string? controllerPeerIdForLog = null;
        var kind = string.IsNullOrWhiteSpace(message.Kind) ? "mouse_move" : message.Kind.Trim();
        var requiresDisplayMapping = !string.Equals(kind, "key", StringComparison.Ordinal);

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (disposed ||
                resetInProgress ||
                role != SessionRuntimeRole.Helper ||
                state != SessionRuntimeState.Connected ||
                !RequireCapability(SessionCapability.RemoteControl) ||
                !SessionSupportsRemoteControl ||
                remoteControlSessionState.ControlState != ControlState.Active)
            {
                LogRemoteControlInfo("input_send_ignored", "invalid_runtime_state");
                return false;
            }

            if (transport is not IRemoteControlSignalingTransport nextControlTransport)
            {
                LogRemoteControlViolation("input_send_failed", "transport_missing_control_channel");
                return false;
            }

            var requestId = remoteControlSessionState.CurrentControlRequestId;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                LogRemoteControlViolation("input_send_failed", "missing_request_id");
                return false;
            }
            requestIdForLog = requestId;
            controllerPeerIdForLog = remoteControlSessionState.ControllerPeerId;

            ControlDisplayInfoMessageV1? peerDisplayInfo = null;
            if (requiresDisplayMapping && !IsUsableRemoteControlDisplayInfo(latestRemoteControlDisplayInfo))
            {
                if (ShouldEmitRemoteControlRateLimitedLog("input_send_ignored:mapping_unavailable"))
                {
                    LogRemoteControlInfo("input_send_ignored", "mapping_unavailable");
                }
                return false;
            }
            if (requiresDisplayMapping)
            {
                peerDisplayInfo = latestRemoteControlDisplayInfo!;
            }

            var outboundSeq = message.Seq > 0
                ? message.Seq
                : Interlocked.Increment(ref remoteControlInputSequence);
            var outboundTimestamp = message.TsUtcMs.GetValueOrDefault();
            if (outboundTimestamp <= 0)
            {
                outboundTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            outboundMessage = message with
            {
                RequestId = requestId,
                Kind = kind,
                DisplayId = requiresDisplayMapping ? peerDisplayInfo!.DisplayId : null,
                DisplayInfoRevision = requiresDisplayMapping ? peerDisplayInfo!.Revision : null,
                Seq = outboundSeq,
                TsUtcMs = outboundTimestamp,
            };

            var nowTicks = Stopwatch.GetTimestamp();
            var previousInputTick = Volatile.Read(ref helperRemoteControlLastInputSentTick);
            Volatile.Write(ref helperRemoteControlLastInputSentTick, nowTicks);
            if (FeatureFlags.RemoteControlAckEnabled)
            {
                var lastAckSeq = Volatile.Read(ref helperRemoteControlLastAckSeq);
                var lastAckAdvanceTick = Volatile.Read(ref helperRemoteControlLastAckAdvanceTick);
                var ackStale = lastAckAdvanceTick > 0
                    ? Stopwatch.GetElapsedTime(lastAckAdvanceTick, nowTicks) > RemoteControlAckStallWindow
                    : previousInputTick > 0 &&
                      Stopwatch.GetElapsedTime(previousInputTick, nowTicks) > RemoteControlAckStallWindow;
                var inputRecent = previousInputTick > 0 &&
                                  Stopwatch.GetElapsedTime(previousInputTick, nowTicks) < RemoteControlRecentInputWindow;
                if (ackStale && inputRecent && outboundSeq > lastAckSeq)
                {
                    Interlocked.Increment(ref helperRemoteControlAckStallDetectedCount);
                    // Do not send display-info probes from helper on ACK stall.
                    // Display-info is authoritative helpee->helper and sending it
                    // from helper can trigger conservative stop handling on helpee.
                    if (TryArmRemoteControlStallRecovery(nowTicks))
                    {
                        if (ShouldEmitRemoteControlRateLimitedLog("input_ack_stall_recovery_skipped", RemoteControlStallRecoveryMinInterval))
                        {
                            LogRemoteControlInfo(
                                "input_ack_stall_recovery_skipped",
                                "display_info_probe_disabled",
                                requestId,
                                remoteControlSessionState.ControllerPeerId);
                        }
                    }
                    PublishRemoteControlDebugDiagnostics();
                    if (ShouldEmitRemoteControlRateLimitedLog("input_ack_stall_detected"))
                    {
                        var ackAgeMs = lastAckAdvanceTick > 0
                            ? Stopwatch.GetElapsedTime(lastAckAdvanceTick, nowTicks).TotalMilliseconds
                            : Stopwatch.GetElapsedTime(previousInputTick, nowTicks).TotalMilliseconds;
                        LogRemoteControlInfo(
                            "input_ack_stall_detected",
                            $"out_seq={outboundSeq.ToString(CultureInfo.InvariantCulture)}; ack_seq={lastAckSeq.ToString(CultureInfo.InvariantCulture)}; ack_age_ms={ackAgeMs.ToString("F0", CultureInfo.InvariantCulture)}",
                            requestId,
                            remoteControlSessionState.ControllerPeerId);
                    }
                }
            }

            controlTransport = nextControlTransport;
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await controlTransport.SendControlInputAsync(outboundMessage, uiCt).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("input_send_failed", ex.GetType().Name, outboundMessage.RequestId);
            return false;
        }
    }

    private bool TryArmRemoteControlStallRecovery(long nowTicks)
    {
        while (true)
        {
            var previousTick = Volatile.Read(ref helperRemoteControlStallRecoveryLastTick);
            if (previousTick > 0 &&
                Stopwatch.GetElapsedTime(previousTick, nowTicks) < RemoteControlStallRecoveryMinInterval)
            {
                return false;
            }

            var observed = Interlocked.CompareExchange(
                ref helperRemoteControlStallRecoveryLastTick,
                nowTicks,
                previousTick);
            if (observed == previousTick)
            {
                return true;
            }
        }
    }

    private async Task SendRemoteControlStallRecoveryProbeAsync(
        IRemoteControlSignalingTransport? controlTransport,
        ControlDisplayInfoMessageV1 probe,
        string? requestId,
        string? controllerPeerId)
    {
        if (controlTransport is null)
        {
            return;
        }

        try
        {
            await controlTransport.SendControlDisplayInfoAsync(probe, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref helperRemoteControlStallRecoverySentCount);
            PublishRemoteControlDebugDiagnostics();
            if (ShouldEmitRemoteControlRateLimitedLog("input_ack_stall_recovery_sent", RemoteControlStallRecoveryMinInterval))
            {
                LogRemoteControlInfo(
                    "input_ack_stall_recovery_sent",
                    $"display_id={probe.DisplayId}; revision={probe.Revision.ToString(CultureInfo.InvariantCulture)}",
                    requestId,
                    controllerPeerId);
            }
        }
        catch (Exception ex)
        {
            if (ShouldEmitRemoteControlRateLimitedLog("input_ack_stall_recovery_send_failed", RemoteControlStallRecoveryMinInterval))
            {
                LogRemoteControlViolation("input_ack_stall_recovery_send_failed", ex.GetType().Name, requestId, controllerPeerId);
            }
        }
    }

    private async Task SendRemoteControlDisplayInfoProbeResponseAsync(
        IRemoteControlSignalingTransport? controlTransport,
        ControlDisplayInfoMessageV1 response,
        string? requestId,
        string? controllerPeerId)
    {
        if (controlTransport is null)
        {
            return;
        }

        if (!RequireRemoteControlAuxiliaryCapability(
                "remote_control_display_info_probe_send",
                "display_info_probe_response_skipped",
                requestId,
                controllerPeerId,
                rateLimitKey: "display_info_probe_response_skipped:capability_not_granted",
                rateLimitWindow: RemoteControlStallRecoveryMinInterval))
        {
            return;
        }

        try
        {
            await controlTransport.SendControlDisplayInfoAsync(response, CancellationToken.None).ConfigureAwait(false);
            if (ShouldEmitRemoteControlRateLimitedLog("display_info_probe_response_sent", RemoteControlStallRecoveryMinInterval))
            {
                LogRemoteControlInfo(
                    "display_info_probe_response_sent",
                    $"display_id={response.DisplayId}; revision={response.Revision.ToString(CultureInfo.InvariantCulture)}",
                    requestId,
                    controllerPeerId);
            }
        }
        catch (Exception ex)
        {
            if (ShouldEmitRemoteControlRateLimitedLog("display_info_probe_response_failed", RemoteControlStallRecoveryMinInterval))
            {
                LogRemoteControlViolation("display_info_probe_response_failed", ex.GetType().Name, requestId, controllerPeerId);
            }
        }
    }

    private async Task DrainQueuedRemoteControlMouseMovesAsync()
    {
        while (true)
        {
            ControlInputMessageV1? next;
            lock (remoteControlMouseMoveQueueGate)
            {
                next = queuedRemoteControlMouseMove;
                queuedRemoteControlMouseMove = null;
                if (next is null)
                {
                    remoteControlMouseMoveSenderActive = false;
                    return;
                }
            }

            await SendRemoteControlInputImmediateAsync(next, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private RemoteControlCoordinatorResult ApplyRemoteControlCoordinatorDisplayInfoChanged(
        ControlDisplayInfoMessageV1 message,
        string reason)
    {
        var transition = RemoteControlCoordinator.Apply(
            remoteControlSessionState,
            remoteControlCoordinatorDisplayInfoState,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.DisplayInfoChanged,
                reason,
                DisplayId: message.DisplayId,
                DisplayRevision: message.Revision,
                VirtualDesktopX: message.VirtualDesktopX,
                VirtualDesktopY: message.VirtualDesktopY,
                VirtualDesktopWidth: message.VirtualDesktopWidth,
                VirtualDesktopHeight: message.VirtualDesktopHeight,
                CaptureRegionX: message.CaptureRegionX,
                CaptureRegionY: message.CaptureRegionY,
                CaptureRegionWidth: message.CaptureRegionWidth,
                CaptureRegionHeight: message.CaptureRegionHeight,
                FrameWidth: message.FrameWidth,
                FrameHeight: message.FrameHeight));

        remoteControlSessionState = transition.NextState;
        remoteControlCoordinatorDisplayInfoState = transition.NextDisplayInfo;
        return transition;
    }

    private static RemoteControlDisplayInfoState CreateRemoteControlDisplayInfoState(ControlDisplayInfoMessageV1 message)
    {
        return new RemoteControlDisplayInfoState(
            DisplayId: string.IsNullOrWhiteSpace(message.DisplayId) ? null : message.DisplayId.Trim(),
            Revision: message.Revision,
            VirtualDesktopX: message.VirtualDesktopX,
            VirtualDesktopY: message.VirtualDesktopY,
            VirtualDesktopWidth: message.VirtualDesktopWidth,
            VirtualDesktopHeight: message.VirtualDesktopHeight,
            CaptureRegionX: message.CaptureRegionX,
            CaptureRegionY: message.CaptureRegionY,
            CaptureRegionWidth: message.CaptureRegionWidth,
            CaptureRegionHeight: message.CaptureRegionHeight,
            FrameWidth: message.FrameWidth,
            FrameHeight: message.FrameHeight);
    }

    private (string? RequestId, string? PeerId, string? StopReason) ApplyRemoteControlDisplayInfoCoordinatorSideEffects(
        in RemoteControlCoordinatorResult transition,
        string coordinatorReason,
        bool showScreenChangedHint)
    {
        var stopReason = transition.ControlStopReason;
        var stopRequestId = transition.PreviousState.CurrentControlRequestId;
        var stopPeerId = transition.PreviousState.ControllerPeerId;

        if ((transition.SideEffects & RemoteControlCoordinatorSideEffect.CancelInjectionQueue) != 0 &&
            role == SessionRuntimeRole.Helpee)
        {
            MarkRemoteControlStopPriority(
                coordinatorReason,
                stopRequestId,
                stopPeerId);
            ClearQueuedRemoteControlInjections("display_info_changed");
        }

        if ((transition.SideEffects & RemoteControlCoordinatorSideEffect.FlushLowLane) != 0)
        {
            ClearQueuedRemoteControlMouseMoves("display_info_changed");
        }

        if (showScreenChangedHint)
        {
            ShowHelperScreenChangedTransientStatus();
        }

        if ((transition.SideEffects & RemoteControlCoordinatorSideEffect.ClearedControlContext) != 0)
        {
            CancelRemoteControlRequestTimeout();
            CancelRemoteControlConsentTimeout();
            CancelRemoteControlDeniedCooldown();
            hasPendingRemoteControlConsentPrompt = false;
            ClearPendingRemoteControlConsentToken();
        }

        if (!transition.PreviousState.Equals(transition.NextState))
        {
            LogRemoteControlTransition(transition.PreviousState, transition.NextState, coordinatorReason);
            NotifyRemoteControlStateChanged();
        }

        if ((transition.SideEffects & RemoteControlCoordinatorSideEffect.SendControlStop) == 0 ||
            string.IsNullOrWhiteSpace(stopRequestId))
        {
            return (null, null, null);
        }

        return (stopRequestId, stopPeerId, string.IsNullOrWhiteSpace(stopReason) ? "DisplayChanged" : stopReason);
    }

    private async Task SendDirectRemoteControlStopAsync(
        IRemoteControlSignalingTransport? controlTransport,
        string requestId,
        string? controllerPeerId,
        string reason,
        CancellationToken ct)
    {
        if (controlTransport is null || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var wireReason = string.Equals(reason, "DisplayChanged", StringComparison.Ordinal)
            ? "screen_changed"
            : reason;

        try
        {
            await controlTransport.SendControlStopAsync(
                new ControlStopMessageV1
                {
                    RequestId = requestId,
                    Reason = wireReason,
                },
                ct).ConfigureAwait(false);
            LogRemoteControlInfo("stop_sent", wireReason, requestId, controllerPeerId);
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("stop_send_failed", ex.GetType().Name, requestId, controllerPeerId);
        }
    }

    private async Task SendRemoteControlDisplayInfoAsync(string sessionIdSnapshot, ControlDisplayInfoMessageV1 message, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdSnapshot);
        ArgumentNullException.ThrowIfNull(message);
        if (ct.IsCancellationRequested || disposed)
        {
            return;
        }

        IRemoteControlSignalingTransport? controlTransport;
        string? displayChangeStopRequestId = null;
        string? displayChangeStopPeerId = null;
        string? displayChangeStopReason = null;

        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed ||
                resetInProgress ||
                role != SessionRuntimeRole.Helpee ||
                state != SessionRuntimeState.Connected ||
                !FeatureFlags.EnableScreenShareTransport ||
                !FeatureFlags.EnableScreenShareCapture)
            {
                LogRemoteControlInfo("display_info_send_ignored", "invalid_runtime_state");
                return;
            }

            var currentSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
            if (string.IsNullOrWhiteSpace(currentSessionId) ||
                !string.Equals(currentSessionId, sessionIdSnapshot, StringComparison.Ordinal))
            {
                LogRemoteControlInfo(
                    "display_info_send_ignored",
                    $"session_mismatch; expected={currentSessionId ?? "(none)"}; captured={sessionIdSnapshot}",
                    controllerPeerId: remoteControlSessionState.ControllerPeerId);
                return;
            }

            if (!RequireRemoteControlAuxiliaryCapability(
                    "remote_control_display_info_send",
                    "display_info_send_ignored",
                    remoteControlSessionState.CurrentControlRequestId,
                    remoteControlSessionState.ControllerPeerId,
                    rateLimitKey: "display_info_send_ignored:capability_not_granted",
                    rateLimitWindow: RemoteControlStallRecoveryMinInterval))
            {
                return;
            }

            if (transport is not IRemoteControlSignalingTransport nextControlTransport)
            {
                LogRemoteControlViolation("display_info_send_failed", "transport_missing_control_channel");
                return;
            }

            var previous = latestRemoteControlDisplayInfo;
            var didMappingChange = IsUsableRemoteControlDisplayInfo(previous) &&
                                   HasDisplayInfoMappingChanged(previous!, message);
            if (didMappingChange)
            {
                LogRemoteControlInfo(
                    "display_info_changed_active",
                    $"prev={FormatControlDisplayInfoLogSummary(previous!)}; next={FormatControlDisplayInfoLogSummary(message)}",
                    requestId: remoteControlSessionState.CurrentControlRequestId,
                    controllerPeerId: remoteControlSessionState.ControllerPeerId);
            }

            if (didMappingChange || (previous is not null && remoteControlSessionState.ControlState != ControlState.Active))
            {
                var displayTransition = ApplyRemoteControlCoordinatorDisplayInfoChanged(
                    message,
                    didMappingChange ? "display_info_changed" : "display_info_updated");
                (displayChangeStopRequestId, displayChangeStopPeerId, displayChangeStopReason) =
                    ApplyRemoteControlDisplayInfoCoordinatorSideEffects(
                        displayTransition,
                        coordinatorReason: didMappingChange ? "display_info_changed" : "display_info_updated",
                        showScreenChangedHint: didMappingChange);
            }
            else
            {
                remoteControlCoordinatorDisplayInfoState = CreateRemoteControlDisplayInfoState(message);
            }

            latestRemoteControlDisplayInfo = message;
            if (didMappingChange)
            {
                MarkForceNextMoveInjectionLog("display_info_changed");
            }
            ClearRemoteControlRevisionMismatchCache();
            controlTransport = nextControlTransport;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (!string.IsNullOrWhiteSpace(displayChangeStopRequestId) &&
            !string.IsNullOrWhiteSpace(displayChangeStopReason))
        {
            await SendDirectRemoteControlStopAsync(
                    controlTransport,
                    displayChangeStopRequestId!,
                    displayChangeStopPeerId,
                    displayChangeStopReason!,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            await controlTransport.SendControlDisplayInfoAsync(message, ct).ConfigureAwait(false);
            LogRemoteControlInfo(
                "display_info_sent",
                FormatControlDisplayInfoLogSummary(message),
                controllerPeerId: remoteControlSessionState.ControllerPeerId);
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("display_info_send_failed", ex.GetType().Name);
        }
    }

    public Task DisconnectAsync(
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        LogResetRequest("disconnect", callerMember, callerFilePath, callerLineNumber);
        return ResetAsync(notifyRemoteSessionEnd: true);
    }

    public async Task FailAsync(string userStatusText)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            TransitionTo(TransportState.Failed, "fail_async");
            SetState(SessionRuntimeState.Failed, userStatusText ?? string.Empty);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task FailAsync(TransportFailure failure, string userStatusText)
    {
        ArgumentNullException.ThrowIfNull(failure);
        await FailAsync(userStatusText).ConfigureAwait(false);
        LogTransportFailure(failure, "fail_async");
    }

    public Task ResetAsync(
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        LogResetRequest("reset", callerMember, callerFilePath, callerLineNumber);
        // Reset is local lifecycle cleanup/restart and must not imply explicit peer termination.
        // Use DisconnectAsync for user-intended "End session".
        return ResetAsync(notifyRemoteSessionEnd: false);
    }

    private async Task ResetAsync(bool notifyRemoteSessionEnd)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetCoreAsync(notifyRemoteSessionEnd).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        StopHelperRemoteScreenSharePressureTimer();
        CancelRemoteControlScreenChangedStatus();
        chatService.MessageReceived -= OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged -= OnChatStateChanged;
        fileTransferService.TransferChanged -= OnFileTransferChanged;

        var disconnectCompleted = RunBoundedCleanup("disconnect", () => DisconnectAsync(), DisposeOperationTimeout);
        ClearQueuedRemoteControlMouseMoves();
        ClearQueuedRemoteControlInjections();

        disposed = true;
        if (!disconnectCompleted)
        {
            ForceDisposeLingeringTransportAfterDisconnectTimeout();
        }

        TransitionTo(TransportState.Disposed, "dispose");
        CancelCachedBridgeIdleTimeout();

        if (cachedBridgeTransport is not null)
        {
            var transportToDispose = cachedBridgeTransport;
            try
            {
                RunBoundedCleanup("cached_bridge_dispose", () =>
                {
                    transportToDispose.Dispose();
                    return Task.CompletedTask;
                }, DisposeOperationTimeout);
            }
            catch
            {
                // Best-effort shutdown.
            }
            finally
            {
                cachedBridgeTransport = null;
            }
        }

        chatService.Dispose();
        fileTransferService.Dispose();
        RunBoundedCleanup("transport_screenshare_dispose", () => transportScreenShareCoordinator.DisposeAsync().AsTask(), DisposeOperationTimeout);
        watchdogRetryPolicy.EventEmitted -= OnWatchdogRetryPolicyEvent;
        lifecycleGate.Dispose();
    }

    private void ForceDisposeLingeringTransportAfterDisconnectTimeout()
    {
        var lingeringSessionCts = sessionCts;
        var lingeringTransport = transport;
        sessionCts = null;
        transport = null;

        if (lingeringSessionCts is not null)
        {
            try
            {
                lingeringSessionCts.Cancel();
            }
            catch
            {
                // Best-effort teardown only.
            }

            try
            {
                lingeringSessionCts.Dispose();
            }
            catch
            {
                // Best-effort teardown only.
            }
        }

        if (lingeringTransport is null)
        {
            return;
        }

        try
        {
            UnwireTransport(lingeringTransport);
        }
        catch
        {
            // Best-effort teardown only.
        }

        try
        {
            chatService.DetachTransport();
            fileTransferService.DetachTransport();
        }
        catch
        {
            // Best-effort teardown only.
        }

        try
        {
            lingeringTransport.Dispose();
            LocalOperationalLog.Warn(
                "Session",
                "event=dispose_forced_transport_cleanup; reason=disconnect_timeout; transport_disposed=1");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=dispose_forced_transport_cleanup_failed; reason=disconnect_timeout; ex={ex.GetType().Name}");
        }
    }

    private static bool RunBoundedCleanup(string operationName, Func<Task> cleanup, TimeSpan timeout)
    {
        try
        {
            var cleanupTask = Task.Run(cleanup);
            var completed = Task.WhenAny(cleanupTask, Task.Delay(timeout)).GetAwaiter().GetResult();
            if (!ReferenceEquals(completed, cleanupTask))
            {
                LocalOperationalLog.Warn(
                    "Session",
                    $"event=dispose_timeout; operation={operationName}; timeout_ms={timeout.TotalMilliseconds:F0}");
                return false;
            }

            cleanupTask.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "Session",
                $"event=dispose_error; operation={operationName}; ex={ex.GetType().Name}");
            return false;
        }
    }

    private async Task RunHostAsync(ISignalingTransport hostTransport, CancellationToken ct)
    {
        try
        {
            if (hostTransport is IAddressHostSignalingTransport addressHostTransport)
            {
                await addressHostTransport.HostByAddressAsync(ct).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException("This transport does not support address-native hosting.");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during reset/navigation.
        }
        catch
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(transport, hostTransport) || disposed)
            {
                return;
            }

            var nknSnapshot = NknRuntimeDiagnostics.Snapshot();
            var lastError = nknSnapshot.LastError;
            var failure = TransportFailureMapper.FromSignals(
                lastError,
                lastDisconnectReason: nknSnapshot.LastDisconnectReason,
                fallbackMessage: "Connection lost.");

            if (ShouldQuietlyRecoverHelpeeHostStartFailure(failure) &&
                TryScheduleQuietHelpeeRehost("host_start_failed_rehost"))
            {
                LogTransportFailure(failure, "host_start_failed_recovering");
                return;
            }

            var message = failure.Category == TransportFailureCategory.BridgeStartFailure && UserErrorMapper.IsNknStartFailure(lastError)
                ? UserErrorMapper.NknStartFailedReinstall()
                : FailureCopyMap.For(failure.Category).Message;
            TransitionTo(TransportState.Failed, "host_start_failed");
            SetState(SessionRuntimeState.Disconnected, message);
            LogTransportFailure(failure, "host_start_failed");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleSynchronousStartFailure(Exception ex, string reason)
    {
        if (disposed || lastTransportFailure is not null || transportState == TransportState.Failed)
        {
            return;
        }

        var nknSnapshot = NknRuntimeDiagnostics.Snapshot();
        var failure = TransportFailureMapper.FromException(
            ex,
            nknSnapshot.LastError,
            nknSnapshot.LastDisconnectReason);
        var persistenceWarning = PersistenceDiagnostics.Snapshot().LastWarning;
        var message = !string.IsNullOrWhiteSpace(persistenceWarning) &&
                      !string.Equals(persistenceWarning, "(none)", StringComparison.Ordinal)
            ? persistenceWarning
            : failure.Category == TransportFailureCategory.BridgeStartFailure && UserErrorMapper.IsNknStartFailure(nknSnapshot.LastError)
                ? UserErrorMapper.NknStartFailedReinstall()
                : FailureCopyMap.For(failure.Category).Message;

        if (transportState != TransportState.Failed &&
            IsTransportTransitionAllowed(transportState, TransportState.Failed))
        {
            TransitionTo(TransportState.Failed, reason);
        }

        SetState(SessionRuntimeState.Disconnected, message);
        LogTransportFailure(failure, reason);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task ResetCoreAsync(bool notifyRemoteSessionEnd)
    {
        if (resetInProgress)
        {
            return;
        }

        resetInProgress = true;
        try
        {
            var oldCts = sessionCts;
            var oldTransport = transport;
            var oldRole = role;
            var oldState = state;
            var oldControlState = remoteControlSessionState.ControlState;
            var oldControlRequestId = remoteControlSessionState.CurrentControlRequestId;
            var hadActiveSession = oldCts is not null || oldTransport is not null || oldRole != SessionRuntimeRole.None;

            ClearHelperListenerBootstrapSnapshot(notifyRemoteSessionEnd ? "remote_session_end" : "reset_core");

            if (oldTransport is ILocalPeerAddressSignalingTransport localPeerTransport &&
                oldTransport.GetType().Name == "DevLocalTransport" &&
                !string.IsNullOrWhiteSpace(localPeerTransport.LocalPeerAddress))
            {
                preservedDevLocalPeerAddress = localPeerTransport.LocalPeerAddress.Trim();
            }

            if (hadActiveSession && transportState is not TransportState.Disposed)
            {
                TransitionTo(TransportState.Reconnecting, "reset");
            }

            if (notifyRemoteSessionEnd)
            {
                // Tell the peer the session is over before best-effort teardown work starts
                // competing for transport state or outbound bandwidth.
                await TrySendPendingIncomingHelpRequestCancellationAsync(oldTransport, "helper_closed").ConfigureAwait(false);
                await TrySendPendingOutboundHelpRequestCancellationAsync(oldTransport, "helpee_closed").ConfigureAwait(false);
                await TrySendRemoteSessionEndAsync(oldTransport, oldRole, oldState).ConfigureAwait(false);
                await TrySendRemoteControlStopAsync(oldTransport, oldControlState, oldControlRequestId, "session_end").ConfigureAwait(false);
                await StopTransportScreenShareAsync(
                    notifyRemoteStop: oldRole == SessionRuntimeRole.Helpee &&
                                      oldState == SessionRuntimeState.Connected &&
                                      oldTransport is NknSignalingTransport,
                    reason: "session_end",
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await TrySendRemoteControlStopAsync(oldTransport, oldControlState, oldControlRequestId, "reset").ConfigureAwait(false);
                await StopTransportScreenShareAsync(
                    notifyRemoteStop: oldRole == SessionRuntimeRole.Helpee &&
                                      oldState == SessionRuntimeState.Connected &&
                                      oldTransport is NknSignalingTransport,
                    reason: "reset",
                    CancellationToken.None).ConfigureAwait(false);
            }

            sessionCts = null;
            transport = null;
            pendingJoinRequest = null;
            role = SessionRuntimeRole.None;
            helperConnectOrigin = HelperConnectOrigin.None;
            helperShouldReturnToListenerWaiting = false;
            hostReady = false;
            currentHelperTargetAddress = null;
            currentHelperInviteToken = null;
            currentHelperInvite = null;
            ClearHelpRequestState();
            ResetSessionSecurityState();
            ClearRemoteControlDisplayInfo("reset_core", notifyStateChanged: false);
            if (remoteControlSessionState.ControlState != ControlState.Off)
            {
                MarkRemoteControlStopPriority(
                    "reset_core",
                    remoteControlSessionState.CurrentControlRequestId,
                    remoteControlSessionState.ControllerPeerId);
            }
            ResetRemoteControlState("reset_core");
            CancelWatchdog();

            if (oldTransport is not null)
            {
                UnwireTransport(oldTransport);
            }

            chatService.DetachTransport();
            fileTransferService.DetachTransport();
            fileTransferService.ResetSessionState();
            ClearActiveConnectAttempt();
            ClearActiveSession();

            if (oldCts is not null)
            {
                try
                {
                    oldCts.Cancel();
                }
                catch
                {
                    // Ignore.
                }
                oldCts.Dispose();
            }

            if (oldTransport is not null)
            {
                if (ShouldKeepBridgeAlive(oldTransport))
                {
                    if (oldTransport is NknSignalingTransport nknTransport)
                    {
                        try
                        {
                            await nknTransport.PrepareForReuseAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // If keepalive cleanup fails, fall back to normal disposal.
                            try
                            {
                                await Task.Run(oldTransport.Dispose).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Best-effort cleanup.
                            }

                            oldTransport = null;
                        }
                    }

                    if (oldTransport is not null)
                    {
                        CacheTransportForKeepAlive(oldTransport);
                    }
                }
                else
                {
                    try
                    {
                        await Task.Run(oldTransport.Dispose).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort cleanup.
                    }
                }
            }

            SetState(SessionRuntimeState.Idle, string.Empty);
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.ResetCompleted,
                oldRole,
                state,
                transportState,
                notifyRemoteSessionEnd ? "disconnect_complete" : "reset_complete"));
            if (!disposed)
            {
                TransitionTo(TransportState.Idle, "reset_complete");
            }
        }
        finally
        {
            resetInProgress = false;
        }
    }

    private ISignalingTransport AcquireTransportForNewSession(out bool reusedCachedBridge)
    {
        return transportLifecycle.AcquireTransportForNewSession(out reusedCachedBridge);
    }

    private void EmitSyntheticWarmBridgeLifecycle()
    {
        transportLifecycle.EmitSyntheticWarmBridgeLifecycle();
    }

    private bool ShouldKeepBridgeAlive(ISignalingTransport transportToRelease)
    {
        return transportLifecycle.ShouldKeepBridgeAlive(transportToRelease);
    }

    private void CacheTransportForKeepAlive(ISignalingTransport transportToCache)
    {
        transportLifecycle.CacheTransportForKeepAlive(transportToCache);
    }

    private void DiscardCachedBridgeTransport()
    {
        transportLifecycle.DiscardCachedBridgeTransport();
    }

    private void WireTransport(ISignalingTransport nextTransport)
    {
        transportLifecycle.WireTransport(nextTransport);
    }

    private void UnwireTransport(ISignalingTransport nextTransport)
    {
        transportLifecycle.UnwireTransport(nextTransport);
    }

    private bool IsFromCurrentTransport(object? sender)
    {
        return transportLifecycle.IsFromCurrentTransport(sender);
    }

    private bool IsKnownBridgeEventSender(object? sender)
    {
        return transportLifecycle.IsKnownBridgeEventSender(sender);
    }

    private void OnTransportSessionSecurityStateChanged(object? sender, TransportSessionSecurityStateChangedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        ApplyTransportSecurityState(e.State);
    }

    private void ApplyTransportSecurityState(SessionSecurityState transportState)
    {
        ArgumentNullException.ThrowIfNull(transportState);
        EnsureApprovalGrantActive();

        if (ShouldIgnoreStaleTransportSecurityDowngrade(transportState))
        {
            return;
        }

        var nextState = transportState;
        var previousGrant = currentSessionGrant;
        if (TryCreateGrantFromTransportState(transportState, out var grant))
        {
            currentSessionGrant = grant;
            nextState = nextState.WithApproval(grant);
            if (HasScreenShareCapability(previousGrant) && !HasScreenShareCapability(grant))
            {
                HandleScreenShareAuthorizationLost("screen_share_capability_removed");
            }
        }
        else
        {
            var hadGrant = currentSessionGrant is not null;
            currentSessionGrant = null;
            nextState = nextState.WithoutApproval();
            if (hadGrant)
            {
                HandleGrantInvalidated("security_context_changed");
            }
        }

        if (transportState.HandshakeState != SessionHandshakeState.Verified ||
            !transportState.InviteValidated ||
            transportState.SessionId is null ||
            transportState.HelperAddress is null)
        {
            pendingApprovalRequest = null;
        }

        SetSessionSecurityState(nextState);
        RefreshRemoteControlCapabilitiesFromTransport();
    }

    private bool ShouldIgnoreStaleTransportSecurityDowngrade(SessionSecurityState transportState)
    {
        if (currentSessionGrant is not SessionGrant currentGrant)
        {
            return false;
        }

        var downgradeCandidate =
            !transportState.InviteValidated ||
            transportState.HandshakeState != SessionHandshakeState.Verified ||
            !transportState.ApprovalGranted ||
            transportState.ApprovedCapabilities == CapabilityGrant.None ||
            transportState.SessionId is null ||
            transportState.HelperAddress is null;
        if (!downgradeCandidate)
        {
            return false;
        }

        if (transportState.SessionId == currentGrant.SessionId &&
            transportState.HelperAddress == currentGrant.HelperIdentity &&
            ShouldIgnoreLateHandshakeFailureForActiveGrant(transportState))
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=late_transport_security_downgrade_ignored; session_id={transportState.SessionId?.Value ?? "(none)"}; helper_identity={transportState.HelperAddress?.Value ?? "(none)"}; reason={transportState.HandshakeFailureReason ?? "(none)"}; active_session_id={currentGrant.SessionId.Value}; active_helper_identity={currentGrant.HelperIdentity.Value}");
            return true;
        }

        var mismatchedSession =
            transportState.SessionId is not SessionId transportSessionId ||
            transportSessionId != currentGrant.SessionId;
        var mismatchedHelper =
            transportState.HelperAddress is not PeerAddress transportHelperAddress ||
            transportHelperAddress != currentGrant.HelperIdentity;

        if (!mismatchedSession && !mismatchedHelper)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=stale_transport_security_downgrade_ignored; session_id={transportState.SessionId?.Value ?? "(none)"}; helper_identity={transportState.HelperAddress?.Value ?? "(none)"}; active_session_id={currentGrant.SessionId.Value}; active_helper_identity={currentGrant.HelperIdentity.Value}");
        return true;
    }

    private static bool ShouldIgnoreLateHandshakeFailureForActiveGrant(SessionSecurityState transportState)
    {
        if (transportState.ApprovalGranted ||
            transportState.HandshakeState is not (SessionHandshakeState.Failed or SessionHandshakeState.Expired))
        {
            return false;
        }

        return string.Equals(transportState.HandshakeFailureReason, "invite_revoked", StringComparison.Ordinal) ||
               string.Equals(transportState.HandshakeFailureReason, "invite_binding_mismatch", StringComparison.Ordinal) ||
               string.Equals(transportState.HandshakeFailureReason, "invite_helper_required", StringComparison.Ordinal) ||
               string.Equals(transportState.HandshakeFailureReason, "invite_helper_mismatch", StringComparison.Ordinal) ||
               string.Equals(transportState.HandshakeFailureReason, "handshake_start_timeout", StringComparison.Ordinal);
    }

    private void SetSessionSecurityState(SessionSecurityState nextState)
    {
        ArgumentNullException.ThrowIfNull(nextState);

        if (Equals(sessionSecurityState, nextState))
        {
            return;
        }

        sessionSecurityState = nextState;
        SessionSecurityStateChanged?.Invoke(this, EventArgs.Empty);
        RefreshSessionFlowProjection();
    }

    private void EnsureApprovalGrantActive()
    {
        if (currentSessionGrant is not SessionGrant grant)
        {
            return;
        }

        if (!grant.IsExpired(nowProvider()))
        {
            return;
        }

        currentSessionGrant = null;
        pendingApprovalRequest = null;
        SetSessionSecurityState(sessionSecurityState.WithApprovalExpired());
        HandleGrantInvalidated("approval_expired");
    }

    private bool TryCreateGrantFromTransportState(SessionSecurityState transportState, out SessionGrant grant)
    {
        grant = default!;
        if (!transportState.InviteValidated ||
            transportState.HandshakeState != SessionHandshakeState.Verified ||
            !transportState.ApprovalGranted ||
            transportState.ApprovedCapabilities == CapabilityGrant.None ||
            transportState.SessionId is not SessionId sessionId ||
            transportState.HelperAddress is not PeerAddress helperAddress ||
            transportState.ApprovalExpiresAt is not DateTimeOffset expiresAtUtc)
        {
            return false;
        }

        grant = new SessionGrant(helperAddress, transportState.ApprovedCapabilities, sessionId, expiresAtUtc);
        return grant.Permits(CapabilityGrant.None, sessionId, helperAddress, nowProvider());
    }

    private void HandleGrantInvalidated(string reason)
    {
        LogApprovalInvalidated(reason);
        EnsureRemoteControlStoppedForAuthorizationLoss(reason);
        allowTransportScreenShareAutoStart = false;
        RefreshRemoteControlCapabilitiesFromTransport();
        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await transportScreenShareCoordinator.HandleDisconnectedAsync().ConfigureAwait(false);
                }
                finally
                {
                    ForceCloseWindowsGraphicsCaptureLeases("transport_disconnected");
                }
            },
            countAsTransportTask: false);
        try
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void HandleScreenShareAuthorizationLost(string reason)
    {
        allowTransportScreenShareAutoStart = false;
        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await transportScreenShareCoordinator.HandleDisconnectedAsync().ConfigureAwait(false);
                }
                finally
                {
                    ForceCloseWindowsGraphicsCaptureLeases("remote_session_ended");
                }
            },
            countAsTransportTask: false);
        try
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }

        LogScreenShareRejected("screen_share_authorization_changed", "stream", reason, ResolveAuthorizedSessionIdForScreenShare());
    }

    private static bool HasScreenShareCapability(SessionGrant? grant)
    {
        if (grant is null)
        {
            return false;
        }

        return (grant.Capabilities & CapabilityGrant.ScreenShare) == CapabilityGrant.ScreenShare;
    }

    private string? ResolveAuthorizedSessionIdForScreenShare()
    {
        return currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value;
    }

    private bool TryValidateScreenSharePayload(ReadOnlySpan<byte> payload, string operation)
    {
        if (!TryParseScreenSharePayloadSession(payload, out var messageType, out var messageSessionId))
        {
            LogScreenShareRejected(operation, "payload", "payload_invalid", messageSessionId: null);
            return false;
        }

        return TryValidateScreenShareSession(messageSessionId, operation, messageType);
    }

    private bool TryValidateScreenShareSession(string? messageSessionId, string operation, string messageType)
    {
        var expectedSessionId = ResolveAuthorizedSessionIdForScreenShare();
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else
        {
            return true;
        }

        LogScreenShareRejected(operation, messageType, failureReason, normalizedMessageSessionId);
        return false;
    }

    private static bool TryParseScreenSharePayloadSession(
        ReadOnlySpan<byte> payload,
        out string messageType,
        out string? messageSessionId)
    {
        if (ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload, out var fragments, out _) &&
            fragments.Length > 0)
        {
            messageType = "frame";
            messageSessionId = fragments[0].SessionId;
            return true;
        }

        if (ScreenSharePayloadCodec.TryDeserializeStop(payload, out var stop))
        {
            messageType = "stop";
            messageSessionId = stop.SessionId;
            return true;
        }

        messageType = "payload";
        messageSessionId = null;
        return false;
    }

    private void InvalidateSessionSecurity(string reason)
    {
        currentSessionGrant = null;
        pendingApprovalRequest = null;
        SetSessionSecurityState(sessionSecurityState == SessionSecurityState.Empty
            ? SessionSecurityState.Empty
            : sessionSecurityState.Invalidate(reason));
    }

    private void ResetSessionSecurityState()
    {
        currentSessionGrant = null;
        pendingApprovalRequest = null;
        SetSessionSecurityState(SessionSecurityState.Empty);
    }

    private void OnIncomingJoinRequest(object? sender, IncomingJoinRequestEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            _ = e.RejectAsync();
            return;
        }

        if (disposed || resetInProgress)
        {
            _ = e.RejectAsync();
            return;
        }

        if (sender is not ISessionSecuritySignalingTransport)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=incoming_join_rejected; reason=security_transport_required; transport={GetTransportNameForLog(sender)}");
            _ = e.RejectAsync();
            return;
        }

        // The join request can arrive before the separate security-state event is processed.
        // Refresh from the authoritative transport snapshot before validating the request.
        ApplyTransportSecurityState(((ISessionSecuritySignalingTransport)sender).CurrentSessionSecurityState);

        if (sessionSecurityState.HandshakeState != SessionHandshakeState.Verified ||
            !sessionSecurityState.InviteValidated)
        {
            _ = e.RejectAsync();
            return;
        }

        if (pendingJoinRequest is not null)
        {
            _ = e.RejectAsync();
            return;
        }

        if (e.ApprovalRequest is null ||
            sessionSecurityState.SessionId is not SessionId sessionId ||
            sessionSecurityState.HelperAddress is not PeerAddress helperAddress ||
            e.ApprovalRequest.SessionId != sessionId ||
            e.ApprovalRequest.HelperIdentity != helperAddress ||
            e.ApprovalRequest.RequestedCapabilities == CapabilityGrant.None)
        {
            _ = e.RejectAsync();
            return;
        }

        pendingJoinRequest = e;
        pendingApprovalRequest = e.ApprovalRequest;
        RefreshRemoteControlCapabilitiesFromTransport();
        SessionTimeline.Record("IncomingJoinRequest");
        TransitionTo(TransportState.Handshake, "incoming_join_request");
        SetState(SessionRuntimeState.IncomingJoinRequest, "Helper on this PC wants to connect. Click Allow.");
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.InboundJoinRequestReceived,
            role,
            state,
            transportState,
            "incoming_join_request"));
        IncomingJoinRequestAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportApproved(object? sender, EventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        if (sender is not ISessionSecuritySignalingTransport securityTransport)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=transport_approval_rejected; reason=security_transport_required; transport={GetTransportNameForLog(sender)}");
            InvalidateSessionSecurity("security_transport_required");
            TransitionTo(TransportState.Failed, "security_transport_required");
            SetState(SessionRuntimeState.Failed, "Secure session validation failed.");
            QueueDetachFileTransferTransport();
            return;
        }

        ApplyTransportSecurityState(securityTransport.CurrentSessionSecurityState);
        EnsureApprovalGrantActive();
        if (currentSessionGrant is null ||
            sessionSecurityState.HandshakeState != SessionHandshakeState.Verified ||
            !sessionSecurityState.InviteValidated ||
            !sessionSecurityState.ApprovalGranted)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=transport_approval_rejected; reason=approval_grant_missing; session_id={sessionSecurityState.SessionId?.Value ?? "(none)"}; helper_identity={sessionSecurityState.HelperAddress?.Value ?? "(none)"}");
            InvalidateSessionSecurity("approval_grant_missing");
            TransitionTo(TransportState.Failed, "approval_grant_missing");
            SetState(SessionRuntimeState.Failed, "Secure session validation failed.");
            QueueDetachFileTransferTransport();
            return;
        }

        RefreshRemoteControlCapabilitiesFromTransport();
        SessionTimeline.Record("Approved");
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.TransportApproved,
            role,
            state,
            transportState,
            "transport_approved"));
        TransitionTo(TransportState.Connected, "transport_approved");
        SetState(SessionRuntimeState.Connected, "Connected");
        Approved?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportRejected(object? sender, EventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        var rejectionReason = sessionSecurityState.HandshakeFailureReason;
        RefreshRemoteControlCapabilitiesFromTransport();
        if (remoteControlSessionState.ControlState != ControlState.Off)
        {
            MarkRemoteControlStopPriority(
                "transport_rejected",
                remoteControlSessionState.CurrentControlRequestId,
                remoteControlSessionState.ControllerPeerId);
        }
        ApplyRemoteControlReducerTransition(
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.SystemDisconnect,
                "transport_rejected",
                RequestId: remoteControlSessionState.CurrentControlRequestId,
                PeerId: remoteControlSessionState.ControllerPeerId));
        allowTransportScreenShareAutoStart = false;
        RunCountedBackgroundTask(
            () => transportScreenShareCoordinator.HandleDisconnectedAsync(),
            countAsTransportTask: false);
        pendingJoinRequest = null;
        InvalidateSessionSecurity("transport_rejected");
        if (role == SessionRuntimeRole.Helpee &&
            state == SessionRuntimeState.IncomingJoinRequest)
        {
            SessionTimeline.Record("SessionEndReceived", "remote_end");
            SessionTimeline.Record("Disconnected", "remote_end");
            TransitionTo(TransportState.Failed, "transport_rejected_remote_end");
            SetState(SessionRuntimeState.Failed, "The helper ended the session.");
            QueueDetachFileTransferTransport();
            RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
            Disconnected?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (role == SessionRuntimeRole.Helper &&
            string.Equals(rejectionReason, "approval_timeout", StringComparison.Ordinal))
        {
            const string approvalTimeoutReason = "approval_timeout";
            var failure = TransportFailureMapper.CreateTimeout(approvalTimeoutReason);
            var shouldReturnToListenerWaiting = ShouldReturnHelperListenerToWaitingForCurrentAttempt();
            SessionTimeline.Record("Rejected", approvalTimeoutReason);
            if (shouldReturnToListenerWaiting)
            {
                BeginHelperListenerWaitingRecovery(
                    "transport_rejected",
                    UserErrorMapper.HelperApprovalTimeout(),
                    failure);
                TryScheduleQuietHelperListenerRestart("helper_transport_rejected_approval_timeout");
            }
            else
            {
                TransitionTo(TransportState.Failed, approvalTimeoutReason);
                SetState(SessionRuntimeState.Failed, UserErrorMapper.HelperApprovalTimeout());
                LogTransportFailure(failure, "transport_rejected");
                PublishSessionFlowEvent(new SessionFlowEvent(
                    SessionFlowEventKind.TransportRejected,
                    role,
                    state,
                    transportState,
                    approvalTimeoutReason));
                QueueDetachFileTransferTransport();
            }
            Rejected?.Invoke(this, EventArgs.Empty);
            return;
        }

        SessionTimeline.Record("Rejected");
        var shouldReturnHelperListenerToWaiting =
            role == SessionRuntimeRole.Helper &&
            ShouldReturnHelperListenerToWaitingForCurrentAttempt();
        if (shouldReturnHelperListenerToWaiting)
        {
            BeginHelperListenerWaitingRecovery(
                "transport_rejected",
                UserErrorMapper.HelperRejected());
            TryScheduleQuietHelperListenerRestart("helper_transport_rejected");
        }
        else
        {
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.TransportRejected,
                role,
                state,
                transportState,
                rejectionReason ?? "transport_rejected"));
            TransitionTo(TransportState.Failed, "transport_rejected");
            SetState(SessionRuntimeState.Rejected, "Permission was declined.");
            QueueDetachFileTransferTransport();
        }
        Rejected?.Invoke(this, EventArgs.Empty);
    }

    private void OnTransportDisconnected(object? sender, EventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        if (remoteControlSessionState.ControlState != ControlState.Off)
        {
            MarkRemoteControlStopPriority(
                "transport_disconnected",
                remoteControlSessionState.CurrentControlRequestId,
                remoteControlSessionState.ControllerPeerId);
        }
        ApplyRemoteControlReducerTransition(
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.SystemDisconnect,
                "transport_disconnected",
                RequestId: remoteControlSessionState.CurrentControlRequestId,
                PeerId: remoteControlSessionState.ControllerPeerId));
        allowTransportScreenShareAutoStart = false;
        RunCountedBackgroundTask(
            () => transportScreenShareCoordinator.HandleDisconnectedAsync(),
            countAsTransportTask: false);
        NotifyLocalScreenShareStoppedForTeardown("transport_disconnected", sender);

        if (disposed || resetInProgress || remoteSessionEndHandling || sessionCts?.IsCancellationRequested == true)
        {
            return;
        }

        // A transport-level disconnect often follows an explicit SessionEnd envelope.
        // In that case the user-facing state/message was already handled in OnRemoteSessionEnded.
        // Do not clear the remote-end marker or overwrite the helper UI with a generic error.
        if (lastDisconnectWasRemoteEnd)
        {
            InvalidateSessionSecurity("remote_session_end");
            QueueDetachFileTransferTransport();
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.RemoteEndReceived,
                role,
                state,
                transportState,
                "remote_session_end"));
            Disconnected?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (role == SessionRuntimeRole.Helpee &&
            state == SessionRuntimeState.Waiting &&
            HasPendingOutboundHelpRequest)
        {
            TryCompletePendingOutboundHelpRequestAsUnavailable(
                reason: "helper_closed",
                trigger: "transport_disconnected_pending_help_request");
            QueueDetachFileTransferTransport();
            TryScheduleQuietHelpeeRehost("transport_disconnected_pending_help_request_rehost");
            return;
        }

        // Helpee idle hosting should recover quietly if the underlying transport/bridge drops.
        // Do not surface this as "Connection lost." while simply waiting for a helper.
        if (role == SessionRuntimeRole.Helpee &&
            state == SessionRuntimeState.Waiting)
        {
            QueueDetachFileTransferTransport();
            TryScheduleQuietHelpeeRehost("transport_disconnected_rehost");
            return;
        }

        // Helper idle-listening should recover quietly too. A dropped listener transport/bridge
        // is not a user-visible session failure when no help request is being handled yet.
        if (role == SessionRuntimeRole.Helper &&
            state == SessionRuntimeState.Waiting &&
            pendingIncomingHelpRequest is null)
        {
            QueueDetachFileTransferTransport();
            TryScheduleQuietHelperListenerRestart("transport_disconnected_relisten");
            return;
        }

        var alreadyFailedWithMappedStatus =
            state == SessionRuntimeState.Failed &&
            !string.IsNullOrWhiteSpace(StatusText);

        var shouldFail = state is SessionRuntimeState.Waiting
            or SessionRuntimeState.IncomingJoinRequest
            or SessionRuntimeState.Connecting
            or SessionRuntimeState.Connected;

        if (shouldFail || alreadyFailedWithMappedStatus)
        {
            lastDisconnectWasRemoteEnd = false;
            pendingJoinRequest = null;
            InvalidateSessionSecurity("transport_disconnected");
            SessionTimeline.Record("Disconnected", "connection_lost");
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            var failure = TransportFailureMapper.FromSignals(
                snapshot.LastError,
                lastDisconnectReason: snapshot.LastDisconnectReason,
                fallbackMessage: "Connection lost.");
            if (alreadyFailedWithMappedStatus)
            {
                if (lastTransportFailure is null)
                {
                    LogTransportFailure(failure, "transport_disconnected");
                }
                QueueDetachFileTransferTransport();
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Preserve an already-mapped failure StatusText here; the generic fallback is only
            // for unmapped failures and must not clobber smoke-tested copy expectations.
            var message = ShouldPreserveMappedFailureStatusText(StatusText)
                ? StatusText
                : "Connection lost.";
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.TransportDisconnected,
                role,
                state,
                transportState,
                "transport_disconnected"));
            TransitionTo(TransportState.Failed, "transport_disconnected");
            SetState(SessionRuntimeState.Failed, message);
            LogTransportFailure(failure, "transport_disconnected");
        }

        QueueDetachFileTransferTransport();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoteSessionEnded(object? sender, EventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        if (disposed || resetInProgress || remoteSessionEndHandling)
        {
            return;
        }

        if (remoteControlSessionState.ControlState != ControlState.Off)
        {
            MarkRemoteControlStopPriority(
                "remote_session_ended",
                remoteControlSessionState.CurrentControlRequestId,
                remoteControlSessionState.ControllerPeerId);
        }
        ApplyRemoteControlReducerTransition(
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.SystemDisconnect,
                "remote_session_ended",
                RequestId: remoteControlSessionState.CurrentControlRequestId,
                PeerId: remoteControlSessionState.ControllerPeerId));
        allowTransportScreenShareAutoStart = false;
        remoteSessionEndHandling = true;
        lastDisconnectWasRemoteEnd = true;
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.RemoteEndReceived,
            role,
            state,
            transportState,
            "remote_session_end"));
        QueueDetachFileTransferTransport();
        RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
        RunCountedBackgroundTask(
            () => transportScreenShareCoordinator.HandleDisconnectedAsync(),
            countAsTransportTask: false);
        NotifyLocalScreenShareStoppedForTeardown("remote_session_ended", sender);

        var message = role switch
        {
            SessionRuntimeRole.Helpee => "The helper ended the session.",
            SessionRuntimeRole.Helper => "The other person ended the session.",
            _ => "The session ended."
        };

        if (role == SessionRuntimeRole.Helper)
        {
            RunCountedBackgroundTask(async () =>
            {
                try
                {
                    SessionTimeline.Record("SessionEndReceived", "remote_end");
                    SessionTimeline.Record("Disconnected", "remote_end");
                    await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
                    // A just-closed helper session can leave the old NKN bridge in a stale state
                    // for passive hosting. Force a fresh listener transport before relistening.
                    DiscardCachedBridgeTransport();
                    await StartHelperListeningAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort. If quiet relisten fails, later transport failures can still surface UI state.
                }
                finally
                {
                    remoteSessionEndHandling = false;
                }
            });
            return;
        }

        RunCountedBackgroundTask(async () =>
        {
            try
            {
                SessionTimeline.Record("SessionEndReceived", "remote_end");
                SessionTimeline.Record("Disconnected", "remote_end");
                await FailAsync(message).ConfigureAwait(false);
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Best-effort. Transport disconnection will still update UI if needed.
            }
            finally
            {
                remoteSessionEndHandling = false;
            }
        });
    }

    private void OnTransportScreenShareFrameCompleted(object? sender, ScreenShareFrameCompletedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        if (remoteSessionEndHandling ||
            lastDisconnectWasRemoteEnd ||
            resetInProgress ||
            state is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected)
        {
            return;
        }

        if (!TryValidateScreenShareSession(e.SessionId, "screen_share_frame_dispatch", "frame"))
        {
            return;
        }

        if (!RequireCapability(SessionCapability.ScreenShare, "screen_share_frame_dispatch"))
        {
            return;
        }

        var suppressedUntilUtc = remoteScreenShareFramesSuppressedUntilUtc;
        var suppressCapturedThroughUtcMs = Interlocked.Read(ref remoteScreenShareSuppressFramesCapturedBeforeOrAtUtcMs);
        if (suppressCapturedThroughUtcMs > 0 &&
            e.CapturedTsUtcMs > 0 &&
            e.CapturedTsUtcMs <= suppressCapturedThroughUtcMs)
        {
            var nowTicks = Environment.TickCount64;
            if (nowTicks - Interlocked.Read(ref lastScreenShareStopSuppressedLogTick) >= 1000)
            {
                Interlocked.Exchange(ref lastScreenShareStopSuppressedLogTick, nowTicks);
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_frame_suppressed_pre_stop_capture; session_id={e.SessionId}; captured_ts_utc_ms={e.CapturedTsUtcMs}; suppress_captured_through_utc_ms={suppressCapturedThroughUtcMs}; control_state={remoteControlSessionState.ControlState}; role={role}");
            }
            return;
        }

        if (suppressedUntilUtc > DateTimeOffset.MinValue && nowProvider() < suppressedUntilUtc)
        {
            var nowTicks = Environment.TickCount64;
            if (nowTicks - Interlocked.Read(ref lastScreenShareStopSuppressedLogTick) >= 1000)
            {
                Interlocked.Exchange(ref lastScreenShareStopSuppressedLogTick, nowTicks);
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_frame_suppressed_after_stop; session_id={e.SessionId}; suppressed_until_utc={suppressedUntilUtc:O}; control_state={remoteControlSessionState.ControlState}; role={role}");
            }
            return;
        }

        if (suppressedUntilUtc > DateTimeOffset.MinValue || suppressCapturedThroughUtcMs > 0)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_resumed_after_stop; session_id={e.SessionId}; resumed_at_utc={nowProvider():O}; captured_ts_utc_ms={e.CapturedTsUtcMs}; control_state={remoteControlSessionState.ControlState}; role={role}");
        }

        remoteScreenShareFramesSuppressedUntilUtc = default;
        Interlocked.Exchange(ref remoteScreenShareSuppressFramesCapturedBeforeOrAtUtcMs, 0);
        lastScreenShareStopSuppressedLogTick = 0;
        CancelRemoteControlScreenShareStopGrace("screenshare_frame_resumed");
        screenShareControlHost.ObserveAcceptedFrame(e);
        try
        {
            TrackHelperRemoteScreenShareAcceptedFrameCore(e);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_dispatch_stage_failed; stage=track_helper_remote_frame; session_id={e.SessionId}; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; encoding={e.Encoding}; reason={ex.GetType().Name}; message={SanitizeDispatchExceptionMessage(ex.Message)}");
            throw;
        }

        try
        {
            SyncFileTransferFlowControlMode();
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_dispatch_stage_failed; stage=sync_filetransfer_flow_control; session_id={e.SessionId}; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; encoding={e.Encoding}; reason={ex.GetType().Name}; message={SanitizeDispatchExceptionMessage(ex.Message)}");
            throw;
        }

        try
        {
            screenShareControlHost.MaybeSendScreenSharePressureState();
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_dispatch_stage_failed; stage=send_pressure_state; session_id={e.SessionId}; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; encoding={e.Encoding}; reason={ex.GetType().Name}; message={SanitizeDispatchExceptionMessage(ex.Message)}");
            throw;
        }

        try
        {
            ScreenShareFrameCompleted?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_dispatch_stage_failed; stage=screen_share_frame_completed_subscriber; session_id={e.SessionId}; stream_epoch={e.StreamEpoch}; frame_id={e.FrameId}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; encoding={e.Encoding}; reason={ex.GetType().Name}; message={SanitizeDispatchExceptionMessage(ex.Message)}");
            throw;
        }
    }

    private void OnTransportScreenShareCursorStateReceived(object? sender, ScreenShareCursorStateReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender) ||
            remoteSessionEndHandling ||
            lastDisconnectWasRemoteEnd ||
            resetInProgress ||
            state is SessionRuntimeState.Failed or SessionRuntimeState.Disconnected)
        {
            return;
        }

        if (role != SessionRuntimeRole.Helper ||
            !FeatureFlags.EnableScreenShareTransport ||
            !TryValidateScreenShareSession(e.Message.SessionId, "screen_share_cursor_state_received", "cursor_state") ||
            !RequireCapability(SessionCapability.ScreenShare, "screen_share_cursor_state_received"))
        {
            return;
        }

        if (transport is not IScreenShareCursorOverlayCapabilityProvider cursorOverlayProvider ||
            !cursorOverlayProvider.SessionSupportsScreenShareCursorOverlay)
        {
            return;
        }

        ScreenShareCursorStateReceived?.Invoke(this, e);
    }

    private void TrackHelperRemoteScreenShareAcceptedFrame(ScreenShareFrameCompletedEventArgs e)
    {
        screenShareControlHost.ObserveAcceptedFrame(e);
    }

    private void TrackHelperRemoteScreenShareAcceptedFrameCore(ScreenShareFrameCompletedEventArgs e)
    {
        if (role != SessionRuntimeRole.Helper)
        {
            return;
        }

        _ = Interlocked.Increment(ref helperRemoteScreenShareAcceptedFrames);
        var streamEpoch = e.StreamEpoch;
        Interlocked.Exchange(ref helperRemoteScreenShareLastAcceptedEpoch, streamEpoch);
        if (e.StreamConfig is not null)
        {
            Interlocked.Exchange(ref helperRemoteScreenShareSawConfig, 1);
        }

        if (streamEpoch <= 0)
        {
            return;
        }

        var nowUtc = nowProvider();
        var epochChanged = false;
        lock (helperRemoteScreenSharePressureGate)
        {
            var suppressEpochAdvanceForNonVisibleRecoveryChurn =
                streamEpoch > 0 &&
                helperRemoteCurrentPressureEpoch > 0 &&
                helperRemoteCurrentPressureEpoch != streamEpoch &&
                helperRemoteContinuityRecoveryActive;

            if (!suppressEpochAdvanceForNonVisibleRecoveryChurn &&
                helperRemoteCurrentPressureEpoch != streamEpoch)
            {
                BeginHelperRemoteScreenSharePressureEpoch_NoLock(streamEpoch, nowUtc);
                epochChanged = true;
            }

            if (helperRemoteCurrentPressureEpoch == streamEpoch &&
                helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc == default)
            {
                helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc = nowUtc;
            }
        }

        if (epochChanged)
        {
            healthyScreenSharePressureIntervals = 0;
            lastObservedRemoteScreenShareStaleDrops = 0;
            lastSentScreenSharePressureAgeMs = 0;
            lastSentScreenSharePressureStaleDrops = 0;
            lastSentScreenSharePressureUtc = default;
        }
    }

    internal void ReportHelperRemoteScreenShareFrameApplied(long ageMs, long streamEpoch)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareFrameApplied(
            ageMs,
            streamEpoch,
            frameId: -1,
            visibleHeadFrameId: -1,
            stableVisibleHeadFrameId: -1,
            framesAppliedSinceLastGap: 0);
    }

    internal void ReportHelperRemoteScreenShareFrameApplied(long ageMs, long streamEpoch, long frameId)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareFrameApplied(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId: frameId,
            stableVisibleHeadFrameId: -1,
            framesAppliedSinceLastGap: 0);
    }

    internal void ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareFrameApplied(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap);
    }

    internal void ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot sessionSnapshot)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareFrameApplied(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            sessionSnapshot);
    }

    internal void ReportHelperRemoteScreenShareSessionSnapshot(HelperRemoteSessionSnapshot snapshot)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareSessionSnapshot(snapshot);
    }

    private void ReportHelperRemoteScreenShareFrameAppliedCore(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
    {
        ReportHelperRemoteScreenShareFrameAppliedCore(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            sessionSnapshot: null);
    }

    private void ReportHelperRemoteScreenShareFrameAppliedCore(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot? sessionSnapshot)
    {
        if (disposed || role != SessionRuntimeRole.Helper)
        {
            return;
        }

        var nowUtc = nowProvider();
        lock (helperRemoteScreenSharePressureGate)
        {
            if (frameId >= 0 &&
                helperRemoteLastReportedAppliedFrameEpoch == streamEpoch &&
                helperRemoteLastReportedAppliedFrameId == frameId)
            {
                return;
            }

            EnsureHelperRemoteScreenSharePressureEpoch_NoLock(streamEpoch, nowUtc);
            var normalizedAgeMs = Math.Max(0, ageMs);
            if (helperRemoteLastAppliedFrameUtc != default)
            {
                var cadenceMs = Math.Max(0L, (long)(nowUtc - helperRemoteLastAppliedFrameUtc).TotalMilliseconds);
                helperRemoteLastApplyCadenceMs = cadenceMs;
                helperRemoteTotalApplyCadenceMs += cadenceMs;
                helperRemoteApplyCadenceObserved++;
            }

            helperRemoteLastAppliedFrameUtc = nowUtc;
            helperRemoteLastAppliedFrameAgeMs = normalizedAgeMs;
            helperRemoteRecentAppliedFrameAgesMs[helperRemoteRecentAppliedFrameIndex] = normalizedAgeMs;
            helperRemoteRecentAppliedFrameIndex = (helperRemoteRecentAppliedFrameIndex + 1) % helperRemoteRecentAppliedFrameAgesMs.Length;
            if (helperRemoteRecentAppliedFrameCount < helperRemoteRecentAppliedFrameAgesMs.Length)
            {
                helperRemoteRecentAppliedFrameCount++;
            }

            helperRemoteConsecutiveVeryHighAppliedFrames = normalizedAgeMs >= 1200
                ? helperRemoteConsecutiveVeryHighAppliedFrames + 1
                : 0;
            if (!helperRemoteCurrentPressureEpochFirstApplySeen)
            {
                helperRemoteCurrentPressureEpochFirstVisibleApplyUtc = nowUtc;
            }

            helperRemoteCurrentPressureEpochFirstApplySeen = true;
            helperRemoteCurrentPressureEpochApplyCount++;
            if (frameId >= 0)
            {
                helperRemoteLastReportedAppliedFrameEpoch = streamEpoch;
                helperRemoteLastReportedAppliedFrameId = frameId;
                helperRemoteCurrentPressureEpochLastVisibleApplyFrameId = frameId;
            }

            if (!(helperRemoteContinuityRecoveryActive && helperRemoteContinuityRecoveryEpoch == streamEpoch))
            {
                helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
                helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
            }

            helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
            helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
            if (sessionSnapshot.HasValue)
            {
                UpdateHelperRemoteReportedSessionSnapshot_NoLock(sessionSnapshot.Value);
            }

            UpdateHelperRemotePressureBaselineForVisibleApply_NoLock(streamEpoch, normalizedAgeMs, frameId);
            UpdateHelperRemoteSteadyVisibleProgressStateForApply_NoLock(
                streamEpoch,
                frameId,
                visibleHeadFrameId,
                stableVisibleHeadFrameId,
                framesAppliedSinceLastGap,
                normalizedAgeMs,
                nowUtc);

            if (helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal > 0 &&
                frameId >= 0)
            {
                helperRemotePostRecoveryAgeGraceEpoch = streamEpoch;
                helperRemotePostRecoveryAgeGraceUntilUtc = nowUtc + HelperRemoteScreenSharePostRecoveryAgeGraceWindow;
                helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
            }
        }

        MaybeSendScreenSharePressureState();
        MaybePublishHelperRemoteRecoveryReceipt();
    }

    private void ReportHelperRemoteScreenShareSessionSnapshotCore(HelperRemoteSessionSnapshot snapshot)
    {
        if (disposed || role != SessionRuntimeRole.Helper)
        {
            return;
        }

        lock (helperRemoteScreenSharePressureGate)
        {
            UpdateHelperRemoteReportedSessionSnapshot_NoLock(snapshot);
        }
    }

    internal void ReportHelperRemoteScreenShareDecodeNeedsMoreInput(long streamEpoch)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareDecodeNeedsMoreInput(streamEpoch);
    }

    private void ReportHelperRemoteScreenShareDecodeNeedsMoreInputCore(long streamEpoch)
    {
        if (disposed || role != SessionRuntimeRole.Helper)
        {
            return;
        }

        var nowUtc = nowProvider();
        lock (helperRemoteScreenSharePressureGate)
        {
            EnsureHelperRemoteScreenSharePressureEpoch_NoLock(streamEpoch, nowUtc);
            helperRemoteCurrentPressureEpochNeedMoreInputCount++;
        }

        MaybeSendScreenSharePressureState();
    }

    internal void ReportHelperRemoteScreenShareContinuityLost(
        long streamEpoch,
        string reason,
        bool shouldRequestRecoveryKeyframe,
        long currentEpochNeedMoreInputCount,
        long expectedNextFrameId,
        long receivedFrameId,
        long lastCleanFrameId)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareContinuityLost(
            streamEpoch,
            reason,
            shouldRequestRecoveryKeyframe,
            currentEpochNeedMoreInputCount,
            expectedNextFrameId,
            receivedFrameId,
            lastCleanFrameId);
    }

    private void ReportHelperRemoteScreenShareContinuityLostCore(
        long streamEpoch,
        string reason,
        bool shouldRequestRecoveryKeyframe,
        long currentEpochNeedMoreInputCount,
        long expectedNextFrameId,
        long receivedFrameId,
        long lastCleanFrameId)
    {
        if (disposed || role != SessionRuntimeRole.Helper)
        {
            return;
        }

        var nowUtc = nowProvider();
        var helperPressureSnapshot = GetHelperRemoteScreenSharePressureSnapshot();
        var transportBackpressureProbe = transport as IScreenShareTransportBackpressureProbe;
        var transportRecentDropCount = Math.Max(0, transportBackpressureProbe?.ScreenShareTransportRecentDropCount ?? 0);
        var recentHealthIssueCount = transportBackpressureProbe?.ScreenShareTransportRecentHealthIssueCount ?? 0;
        var hasTransportQueuePressure =
            transportBackpressureProbe?.IsScreenShareTransportCongested == true ||
            transportBackpressureProbe?.IsScreenShareTransportSeverelyCongested == true ||
            Math.Max(0, transportBackpressureProbe?.ScreenShareTransportQueueDepth ?? 0) > 0 ||
            transportRecentDropCount > 0;
        var hasRealTransportOrBridgePressure =
            hasTransportQueuePressure ||
            recentHealthIssueCount > 0 ||
            transportBackpressureProbe?.IsScreenShareTransportHealthSeverelyDegraded == true;
        if (!hasRealTransportOrBridgePressure &&
            ShouldSuppressSatisfiedFloorContinuityLoss(
                helperPressureSnapshot,
                streamEpoch,
                expectedNextFrameId,
                receivedFrameId,
                lastCleanFrameId))
        {
            healthyScreenSharePressureIntervals = Math.Max(healthyScreenSharePressureIntervals, 4);
            MaybeSendScreenSharePressureState();
            return;
        }

        lock (helperRemoteScreenSharePressureGate)
        {
            EnsureHelperRemoteScreenSharePressureEpoch_NoLock(streamEpoch, nowUtc);
            var epochChanged = helperRemoteContinuityRecoveryEpoch != streamEpoch;
            helperRemoteContinuityRecoveryActive = true;
            helperRemotePostRecoveryHealthySignalSent = false;
            ResetHelperRemoteProgressAwarePressureState_NoLock();
            ClearHelperRemoteSteadyVisibleProgressState_NoLock("continuity_loss");
            ClearHelperRemoteActiveRecoveryReceiptOwner_NoLock();
            helperRemoteContinuityRecoveryEpoch = streamEpoch;
            if (epochChanged || helperRemoteContinuityRecoveryStartedUtc == default)
            {
                helperRemoteContinuityRecoveryStartedUtc = nowUtc;
                helperRemoteContinuityRecoveryTimeoutSent = false;
            }

            if (epochChanged)
            {
                helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal = 0;
            }

            helperRemoteCurrentPressureEpochNeedMoreInputCount = Math.Max(
                helperRemoteCurrentPressureEpochNeedMoreInputCount,
                Math.Max(0, currentEpochNeedMoreInputCount));
        }

        healthyScreenSharePressureIntervals = 0;
        MaybeSendScreenSharePressureState();

        if (!shouldRequestRecoveryKeyframe)
        {
            return;
        }

        RequestHelperRemoteRecoveryKeyframe(
            streamEpoch,
            reason,
            currentEpochNeedMoreInputCount,
            expectedNextFrameId,
            receivedFrameId,
            lastCleanFrameId);
    }

    private static bool ShouldSuppressSatisfiedFloorContinuityLoss(
        HelperRemoteScreenSharePressureSnapshot helperPressureSnapshot,
        long streamEpoch,
        long expectedNextFrameId,
        long receivedFrameId,
        long lastCleanFrameId)
    {
        if (streamEpoch <= 0 ||
            helperPressureSnapshot.CurrentEpoch != streamEpoch ||
            helperPressureSnapshot.CurrentEpochRecoveryActive ||
            GetActionableStaleDropCount(
                helperPressureSnapshot.CurrentEpochStaleDropCount,
                helperPressureSnapshot.CurrentEpochSoftStaleDropCount) > 0)
        {
            return false;
        }

        var visibleProgressWindowMs = (long)HelperRemoteScreenSharePostRecoveryVisibleProgressWindow.TotalMilliseconds;
        if (!helperPressureSnapshot.HasAppliedFrame ||
            helperPressureSnapshot.LastVisibleApplyFrameId < 0 ||
            helperPressureSnapshot.FramesAppliedSinceLastGap < 2 ||
            helperPressureSnapshot.ProgressStallMs < 0 ||
            helperPressureSnapshot.ProgressStallMs > visibleProgressWindowMs)
        {
            return false;
        }

        var visibleProofHead = GetLatestHelperVisibleProofHeadFrameId(
            helperPressureSnapshot.CurrentEpochProvenHeadFrameId >= 0
                ? helperPressureSnapshot.CurrentEpochProvenHeadFrameId
                : helperPressureSnapshot.VisibleHeadFrameId,
            helperPressureSnapshot.LastVisibleApplyFrameId);
        var reportedContinuityBoundary = Math.Max(
            expectedNextFrameId,
            Math.Max(receivedFrameId, lastCleanFrameId));
        var recoveryFloorSatisfied =
            helperPressureSnapshot.VisibleRecoveryFloorFrameId >= 0 &&
            visibleProofHead >= helperPressureSnapshot.VisibleRecoveryFloorFrameId &&
            (helperPressureSnapshot.CurrentEpochProgressProven ||
             helperPressureSnapshot.DerivedPostRecoveryHealthyActive) &&
            (string.Equals(helperPressureSnapshot.CurrentEpochProgressProofSource, "recovery_floor_plus_head", StringComparison.Ordinal) ||
             string.Equals(helperPressureSnapshot.DerivedPostRecoveryHealthySource, "recovery_floor_plus_head", StringComparison.Ordinal) ||
             visibleProofHead > helperPressureSnapshot.VisibleRecoveryFloorFrameId);

        return recoveryFloorSatisfied &&
               reportedContinuityBoundary >= 0 &&
               visibleProofHead >= reportedContinuityBoundary;
    }

    private static long GetActionableStaleDropCount(long totalStaleDropCount, long softStaleDropCount)
    {
        return Math.Max(0L, Math.Max(0L, totalStaleDropCount) - Math.Max(0L, softStaleDropCount));
    }

    internal void ReportHelperRemoteScreenShareRecoveryKeyframeApplied(long ageMs, long streamEpoch)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareRecoveryKeyframeApplied(ageMs, streamEpoch);
    }

    private void ReportHelperRemoteScreenShareRecoveryKeyframeAppliedCore(long ageMs, long streamEpoch)
    {
        if (disposed || role != SessionRuntimeRole.Helper)
        {
            return;
        }

        lock (helperRemoteScreenSharePressureGate)
        {
            if (!helperRemoteContinuityRecoveryActive || helperRemoteContinuityRecoveryEpoch != streamEpoch)
            {
                return;
            }

            ResetHelperRemoteScreenSharePressureAfterRecoveryKeyframe_NoLock(
                streamEpoch,
                nowProvider());
            helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal = 1;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_viewer_recovery_keyframe_applied; role=helper_remote; stream_epoch={streamEpoch}; recovery_active=0; age_ms={Math.Max(0, ageMs)}");
        healthyScreenSharePressureIntervals = 0;
        MaybeSendScreenSharePressureState();
        MaybePublishHelperRemoteRecoveryReceipt();
    }

    internal void ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason = null)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
            streamEpoch,
            recoveryFrameId,
            lastContiguousFrameId,
            contiguousFollowerApplyCount,
            status,
            abortReason);
    }

    private void ReportHelperRemoteScreenShareRecoveryWindowStateChangedCore(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason = null)
    {
        if (disposed || role != SessionRuntimeRole.Helper || streamEpoch <= 0 || recoveryFrameId < 0)
        {
            return;
        }

        var effectiveStatus = ParseHelperRemoteRecoveryWindowStatus(status);
        if (effectiveStatus == HelperRemoteRecoveryWindowStatus.Unknown)
        {
            return;
        }

        var shouldAttemptPublish = false;
        lock (helperRemoteScreenSharePressureGate)
        {
            switch (effectiveStatus)
            {
                case HelperRemoteRecoveryWindowStatus.Started:
                case HelperRemoteRecoveryWindowStatus.FollowerApplied:
                case HelperRemoteRecoveryWindowStatus.Succeeded:
                    UpdateHelperRemoteActiveRecoveryReceiptOwner_NoLock(streamEpoch, recoveryFrameId);
                    shouldAttemptPublish = true;
                    break;
                case HelperRemoteRecoveryWindowStatus.Aborted:
                    ClearHelperRemoteActiveRecoveryReceiptOwner_NoLock();
                    break;
            }
        }

        if (effectiveStatus != HelperRemoteRecoveryWindowStatus.Succeeded)
        {
            if (shouldAttemptPublish)
            {
                MaybePublishHelperRemoteRecoveryReceipt();
            }

            return;
        }

        var activeSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
        if (!string.IsNullOrWhiteSpace(activeSessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveRecoveryWindowSucceeded(
                activeSessionId,
                streamEpoch,
                recoveryFrameId,
                lastContiguousFrameId >= 0 ? lastContiguousFrameId : recoveryFrameId);
        }

        if (shouldAttemptPublish)
        {
            MaybePublishHelperRemoteRecoveryReceipt();
        }
    }

    private static HelperRemoteRecoveryWindowStatus ParseHelperRemoteRecoveryWindowStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return HelperRemoteRecoveryWindowStatus.Unknown;
        }

        return status.Trim() switch
        {
            "started" => HelperRemoteRecoveryWindowStatus.Started,
            "follower_applied" => HelperRemoteRecoveryWindowStatus.FollowerApplied,
            "succeeded" => HelperRemoteRecoveryWindowStatus.Succeeded,
            "aborted" => HelperRemoteRecoveryWindowStatus.Aborted,
            _ => HelperRemoteRecoveryWindowStatus.Unknown,
        };
    }

    private void ResetHelperRemotePublishedRecoveryReceiptState_NoLock()
    {
        helperRemotePublishedRecoveryReceiptEpoch = 0;
        helperRemotePublishedRecoveryReceiptOwnerFrameId = -1;
        helperRemotePublishedRecoveryReceiptVisibleRecoveryFrameId = -1;
        helperRemotePublishedRecoveryReceiptVisibleHeadFrameId = -1;
        helperRemotePublishedRecoveryReceiptKind = string.Empty;
        helperRemotePublishedRecoveryReceiptUtc = default;
        helperRemotePublishedRecoveryReceiptRetrySent = false;
        helperRemoteRecoveryReceiptRetryGeneration++;
    }

    private void ResetHelperRemoteRecoveryReceiptPublicationState_NoLock()
    {
        helperRemoteActiveRecoveryReceiptOwnerEpoch = 0;
        helperRemoteActiveRecoveryReceiptOwnerFrameId = -1;
        ResetHelperRemotePublishedRecoveryReceiptState_NoLock();
    }

    private void ClearHelperRemoteActiveRecoveryReceiptOwner_NoLock()
    {
        helperRemoteActiveRecoveryReceiptOwnerEpoch = 0;
        helperRemoteActiveRecoveryReceiptOwnerFrameId = -1;
        helperRemoteRecoveryReceiptRetryGeneration++;
    }

    private void UpdateHelperRemoteActiveRecoveryReceiptOwner_NoLock(long streamEpoch, long recoveryFrameId)
    {
        if (streamEpoch <= 0 || recoveryFrameId < 0)
        {
            ClearHelperRemoteActiveRecoveryReceiptOwner_NoLock();
            return;
        }

        if (helperRemoteActiveRecoveryReceiptOwnerEpoch == streamEpoch &&
            helperRemoteActiveRecoveryReceiptOwnerFrameId == recoveryFrameId)
        {
            return;
        }

        helperRemoteActiveRecoveryReceiptOwnerEpoch = streamEpoch;
        helperRemoteActiveRecoveryReceiptOwnerFrameId = recoveryFrameId;
        helperRemoteRecoveryReceiptRetryGeneration++;
    }

    private bool TryBuildHelperRemoteRecoveryReceiptCandidate_NoLock(
        out HelperRemoteRecoveryReceiptPublicationCandidate candidate)
    {
        candidate = default;

        var activeSessionId = GetHelperRemoteActiveSessionId_NoLock();
        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return false;
        }

        var streamEpoch = helperRemoteActiveRecoveryReceiptOwnerEpoch;
        var ownerFrameId = helperRemoteActiveRecoveryReceiptOwnerFrameId;
        if (streamEpoch <= 0 || ownerFrameId < 0)
        {
            return false;
        }

        var frameLossSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(activeSessionId);
        var epochDiagnostics = frameLossSnapshot.EpochDiagnostics.FirstOrDefault(epoch => epoch.StreamEpoch == streamEpoch);
        if (epochDiagnostics is null)
        {
            return false;
        }

        var visibleRecoveryFrameId = epochDiagnostics.VisibleRecoveryFloorFrameId;
        if (visibleRecoveryFrameId < ownerFrameId)
        {
            return false;
        }

        var visibleHeadFrameId = Math.Max(
            visibleRecoveryFrameId,
            Math.Max(
                epochDiagnostics.VisibleHeadFrameId,
                helperRemoteSteadyProgressEpoch == streamEpoch
                    ? helperRemoteSteadyProgressVisibleHeadFrameId
                    : -1L));
        var receiptKind = visibleRecoveryFrameId == ownerFrameId
            ? ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind
            : ScreenShareRecoveryReceiptCodec.VisibleProgressAfterRecoveryKeyframeReceiptKind;
        var message = new ScreenShareRecoveryReceiptV1
        {
            SessionId = activeSessionId,
            StreamEpoch = streamEpoch,
            OwnerFrameId = ownerFrameId,
            VisibleRecoveryFrameId = visibleRecoveryFrameId,
            VisibleHeadFrameId = visibleHeadFrameId,
            ReceiptKind = receiptKind,
        };

        candidate = new HelperRemoteRecoveryReceiptPublicationCandidate(
            streamEpoch,
            ownerFrameId,
            visibleRecoveryFrameId,
            visibleHeadFrameId,
            receiptKind,
            message);
        return true;
    }

    private void MaybePublishHelperRemoteRecoveryReceipt()
    {
        if (disposed ||
            role != SessionRuntimeRole.Helper ||
            state != SessionRuntimeState.Connected ||
            transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        HelperRemoteRecoveryReceiptPublicationCandidate candidate;
        long retryGeneration;
        lock (helperRemoteScreenSharePressureGate)
        {
            if (!TryBuildHelperRemoteRecoveryReceiptCandidate_NoLock(out candidate))
            {
                return;
            }

            var sameReceiptKey =
                helperRemotePublishedRecoveryReceiptEpoch == candidate.StreamEpoch &&
                helperRemotePublishedRecoveryReceiptOwnerFrameId == candidate.OwnerFrameId &&
                helperRemotePublishedRecoveryReceiptVisibleRecoveryFrameId == candidate.VisibleRecoveryFrameId &&
                string.Equals(
                    helperRemotePublishedRecoveryReceiptKind,
                    candidate.ReceiptKind,
                    StringComparison.Ordinal);
            if (sameReceiptKey)
            {
                return;
            }

            helperRemotePublishedRecoveryReceiptEpoch = candidate.StreamEpoch;
            helperRemotePublishedRecoveryReceiptOwnerFrameId = candidate.OwnerFrameId;
            helperRemotePublishedRecoveryReceiptVisibleRecoveryFrameId = candidate.VisibleRecoveryFrameId;
            helperRemotePublishedRecoveryReceiptVisibleHeadFrameId = candidate.VisibleHeadFrameId;
            helperRemotePublishedRecoveryReceiptKind = candidate.ReceiptKind;
            helperRemotePublishedRecoveryReceiptUtc = nowProvider();
            helperRemotePublishedRecoveryReceiptRetrySent = false;
            helperRemoteRecoveryReceiptRetryGeneration++;
            retryGeneration = helperRemoteRecoveryReceiptRetryGeneration;
        }

        QueueHelperRemoteRecoveryReceiptSend(screenShareTransport, candidate.Message, isRetry: false);
        ScheduleHelperRemoteRecoveryReceiptRetry(retryGeneration);
    }

    private void ScheduleHelperRemoteRecoveryReceiptRetry(long retryGeneration)
    {
        RunCountedBackgroundTask(
            async () =>
            {
                await Task.Delay(HelperRemoteScreenShareRecoveryReceiptRetryDelay, CancellationToken.None).ConfigureAwait(false);
                TrySendHelperRemoteRecoveryReceiptRetry(retryGeneration);
            },
            countAsTransportTask: false);
    }

    private void TrySendHelperRemoteRecoveryReceiptRetry(long retryGeneration)
    {
        if (disposed ||
            role != SessionRuntimeRole.Helper ||
            state != SessionRuntimeState.Connected ||
            transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        HelperRemoteRecoveryReceiptPublicationCandidate candidate;
        lock (helperRemoteScreenSharePressureGate)
        {
            if (retryGeneration != helperRemoteRecoveryReceiptRetryGeneration ||
                helperRemotePublishedRecoveryReceiptRetrySent ||
                !TryBuildHelperRemoteRecoveryReceiptCandidate_NoLock(out candidate))
            {
                return;
            }

            var sameReceiptKey =
                helperRemotePublishedRecoveryReceiptEpoch == candidate.StreamEpoch &&
                helperRemotePublishedRecoveryReceiptOwnerFrameId == candidate.OwnerFrameId &&
                helperRemotePublishedRecoveryReceiptVisibleRecoveryFrameId == candidate.VisibleRecoveryFrameId &&
                string.Equals(
                    helperRemotePublishedRecoveryReceiptKind,
                    candidate.ReceiptKind,
                    StringComparison.Ordinal);
            if (!sameReceiptKey)
            {
                return;
            }

            helperRemotePublishedRecoveryReceiptRetrySent = true;
            helperRemotePublishedRecoveryReceiptVisibleHeadFrameId = candidate.VisibleHeadFrameId;
            helperRemotePublishedRecoveryReceiptUtc = nowProvider();
        }

        QueueHelperRemoteRecoveryReceiptSend(screenShareTransport, candidate.Message, isRetry: true);
    }

    private void QueueHelperRemoteRecoveryReceiptSend(
        IScreenShareSignalingTransport screenShareTransport,
        ScreenShareRecoveryReceiptV1 message,
        bool isRetry)
    {
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_receipt_sent; role=helper_remote; session_id={message.SessionId}; stream_epoch={message.StreamEpoch}; owner_frame_id={message.OwnerFrameId}; visible_recovery_frame_id={message.VisibleRecoveryFrameId}; visible_head_frame_id={message.VisibleHeadFrameId}; receipt_kind={message.ReceiptKind}; retry={(isRetry ? 1 : 0)}");

        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await screenShareTransport.SendScreenShareRecoveryReceiptAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_recovery_receipt_send_failed; role=helper_remote; reason={ex.GetType().Name}; message={SanitizeDispatchExceptionMessage(ex.Message)}; stream_epoch={message.StreamEpoch}; owner_frame_id={message.OwnerFrameId}; visible_recovery_frame_id={message.VisibleRecoveryFrameId}; retry={(isRetry ? 1 : 0)}");
                }
            },
            countAsTransportTask: false);
    }

    private bool IsHelperRemoteCurrentEpochWarmupActive_NoLock(DateTimeOffset nowUtc, long currentEpoch)
    {
        if (IsHelperRemotePostRecoveryHealthyLatched_NoLock(currentEpoch))
        {
            return false;
        }

        return currentEpoch > 0 &&
               helperRemoteCurrentPressureEpochStartedUtc != default &&
               nowUtc - helperRemoteCurrentPressureEpochStartedUtc < HelperRemoteScreenShareEpochWarmupTimeout &&
               (!helperRemoteCurrentPressureEpochFirstApplySeen ||
                helperRemoteCurrentPressureEpochApplyCount < HelperRemoteScreenShareMinimumWarmupApplies);
    }

    private void UpdateHelperRemoteReportedSessionSnapshot_NoLock(HelperRemoteSessionSnapshot snapshot)
    {
        if (snapshot.CurrentEpoch > 0 &&
            helperRemoteLastReportedSessionSnapshot.CurrentEpoch > snapshot.CurrentEpoch)
        {
            return;
        }

        helperRemoteLastReportedSessionSnapshot = snapshot;
        helperRemoteLastReportedSessionSnapshotUtc = nowProvider();
    }

    private void ResetHelperRemoteSentVisibleProgressProof_NoLock()
    {
        helperRemoteLastSentSteadyProgressEpoch = 0;
        helperRemoteLastSentSteadyVisibleProgressActive = false;
        helperRemoteLastSentStableVisibleHeadFrameId = -1;
        helperRemoteLastSentVisibleHeadFrameId = -1;
        helperRemoteLastSentFramesAppliedSinceLastGap = 0;
        helperRemoteLastSentVisibleApplyFrameId = -1;
        helperRemoteLastSentAppliedHeadFrameId = -1;
        helperRemoteProofKeepaliveSendCount = 0;
        helperRemoteProofKeepaliveTimerDrivenSendCount = 0;
        helperRemoteLastProofKeepaliveHeadFrameId = -1;
        helperRemoteLastProofKeepaliveSentUtc = default;
        helperRemoteLastAppliedHeadAdvancedSincePressureEvaluation = false;
        helperRemoteLastStableVisibleHeadAdvancedSincePressureEvaluation = false;
        helperRemoteLastHealthyStateEstablishedBy = "none";
    }

    private static long GetLatestHelperAppliedProofHeadFrameId(
        long lastVisibleApplyFrameId,
        long appliedHeadFrameId,
        long stableVisibleHeadFrameId)
    {
        return Math.Max(
            Math.Max(lastVisibleApplyFrameId, appliedHeadFrameId),
            stableVisibleHeadFrameId);
    }

    private static long GetLatestHelperVisibleProofHeadFrameId(
        long visibleHeadFrameId,
        long lastVisibleApplyFrameId)
    {
        return visibleHeadFrameId >= 0
            ? visibleHeadFrameId
            : lastVisibleApplyFrameId;
    }

    private string? GetHelperRemoteActiveSessionId_NoLock()
    {
        return currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
    }

    private readonly record struct HelperRemoteAuthoritativeProgressState(
        HelperRemoteSessionSnapshot SessionSnapshot,
        bool SessionSnapshotPresent,
        long LastVisibleApplyFrameId,
        long VisibleHeadFrameId,
        long VisibleRecoveryFloorFrameId,
        long AppliedHeadFrameId,
        long StableVisibleHeadFrameId,
        long FramesAppliedSinceLastGap,
        bool CurrentEpochProgressProven,
        string CurrentEpochProgressProofSource,
        long CurrentEpochProvenHeadFrameId,
        bool SteadyVisibleProgressActive,
        long SteadyVisibleProgressActivationFrameId);

    private HelperRemoteAuthoritativeProgressState ResolveHelperRemoteAuthoritativeProgressState_NoLock(
        long currentEpoch,
        ScreenShareEpochDiagnosticsSnapshot? currentEpochDiagnostics)
    {
        var sessionSnapshotPresent =
            currentEpoch > 0 &&
            helperRemoteLastReportedSessionSnapshot.CurrentEpoch == currentEpoch;
        var sessionSnapshot = sessionSnapshotPresent
            ? helperRemoteLastReportedSessionSnapshot
            : default;
        var durableSteadyProgressStatePresent = helperRemoteSteadyProgressEpoch == currentEpoch;
        var lastVisibleApplyFrameId = Math.Max(
            Math.Max(
                currentEpochDiagnostics?.LastAppliedFrameId ?? helperRemoteCurrentPressureEpochLastVisibleApplyFrameId,
                sessionSnapshotPresent ? sessionSnapshot.AppliedHeadFrameId : -1L),
            durableSteadyProgressStatePresent ? helperRemoteSteadyProgressVisibleHeadFrameId : -1L);
        var visibleHeadFrameId = Math.Max(
            Math.Max(
                currentEpochDiagnostics?.VisibleHeadFrameId ?? -1L,
                sessionSnapshotPresent ? sessionSnapshot.VisibleHeadFrameId : -1L),
            durableSteadyProgressStatePresent ? helperRemoteSteadyProgressVisibleHeadFrameId : -1L);
        var appliedHeadFrameId = Math.Max(
            currentEpochDiagnostics?.AppliedHeadFrameId ?? -1L,
            Math.Max(lastVisibleApplyFrameId, sessionSnapshotPresent ? sessionSnapshot.AppliedHeadFrameId : -1L));
        var stableVisibleHeadFrameId = Math.Max(
            Math.Max(
                currentEpochDiagnostics?.StableVisibleHeadFrameId ?? -1L,
                sessionSnapshotPresent ? sessionSnapshot.StableVisibleHeadFrameId : -1L),
            durableSteadyProgressStatePresent ? helperRemoteSteadyProgressStableVisibleHeadFrameId : -1L);
        var visibleRecoveryFloorFrameId = Math.Max(
            currentEpochDiagnostics?.VisibleRecoveryFloorFrameId ?? -1L,
            sessionSnapshotPresent ? sessionSnapshot.VisibleRecoveryFloorFrameId : -1L);
        var fallbackDerivedPostRecoveryHealthy = ComputeDerivedPostRecoveryHealthyState(
            visibleRecoveryFloorFrameId,
            lastVisibleApplyFrameId,
            appliedHeadFrameId,
            stableVisibleHeadFrameId,
            Math.Max(
                durableSteadyProgressStatePresent ? helperRemoteSteadyProgressFramesAppliedSinceLastGap : 0L,
                currentEpochDiagnostics?.FramesAppliedSinceLastGap ?? 0L),
            helperRemoteCurrentPressureEpochApplyCount);
        var framesAppliedSinceLastGap = Math.Max(
            fallbackDerivedPostRecoveryHealthy.FramesAppliedSinceLastGap,
            sessionSnapshotPresent ? sessionSnapshot.FramesAppliedSinceLastGap : 0L);
        var currentEpochProgressProven = sessionSnapshotPresent
            ? sessionSnapshot.CurrentEpochProgressProven
            : fallbackDerivedPostRecoveryHealthy.Active;
        var currentEpochProgressProofSource = sessionSnapshotPresent && sessionSnapshot.CurrentEpochProgressProven
            ? sessionSnapshot.CurrentEpochProgressProofSource
            : fallbackDerivedPostRecoveryHealthy.Source;
        var currentEpochProvenHeadFrameId = sessionSnapshotPresent && sessionSnapshot.CurrentEpochProgressProven
            ? sessionSnapshot.ProvenHeadFrameId
            : fallbackDerivedPostRecoveryHealthy.ProofFrameId;
        var steadyVisibleProgressActive = sessionSnapshotPresent
            ? sessionSnapshot.SteadyVisibleProgressActive
            : fallbackDerivedPostRecoveryHealthy.Active;
        var steadyVisibleProgressActivationFrameId =
            durableSteadyProgressStatePresent && helperRemoteSteadyProgressActivationFrameId >= 0
                ? helperRemoteSteadyProgressActivationFrameId
                : currentEpochProvenHeadFrameId;
        return new HelperRemoteAuthoritativeProgressState(
            sessionSnapshot,
            sessionSnapshotPresent,
            lastVisibleApplyFrameId,
            visibleHeadFrameId,
            visibleRecoveryFloorFrameId,
            appliedHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            currentEpochProgressProven,
            currentEpochProgressProofSource,
            currentEpochProvenHeadFrameId,
            steadyVisibleProgressActive,
            steadyVisibleProgressActivationFrameId);
    }

    private static long ComputePostRecoveryHealthyFramesAppliedSinceLastGap(
        long visibleRecoveryFloorFrameId,
        long appliedHeadFrameId,
        long stableVisibleHeadFrameId,
        long currentFramesAppliedSinceLastGap,
        int currentEpochApplyCount)
    {
        var provenFramesAppliedSinceLastGap = Math.Max(0L, currentFramesAppliedSinceLastGap);
        var effectiveHeadFrameId = Math.Max(appliedHeadFrameId, stableVisibleHeadFrameId);
        if (visibleRecoveryFloorFrameId >= 0 && effectiveHeadFrameId >= visibleRecoveryFloorFrameId)
        {
            provenFramesAppliedSinceLastGap = Math.Max(
                provenFramesAppliedSinceLastGap,
                effectiveHeadFrameId - visibleRecoveryFloorFrameId + 1);
        }
        else if (stableVisibleHeadFrameId >= 0)
        {
            provenFramesAppliedSinceLastGap = Math.Max(
                provenFramesAppliedSinceLastGap,
                Math.Max(0, currentEpochApplyCount));
        }

        return provenFramesAppliedSinceLastGap;
    }

    private static (bool Active, string Source, long ProofFrameId, long FramesAppliedSinceLastGap)
        ComputeDerivedPostRecoveryHealthyState(
            long visibleRecoveryFloorFrameId,
            long lastVisibleApplyFrameId,
            long appliedHeadFrameId,
            long stableVisibleHeadFrameId,
            long currentFramesAppliedSinceLastGap,
            int currentEpochApplyCount)
    {
        var framesAppliedSinceLastGap = ComputePostRecoveryHealthyFramesAppliedSinceLastGap(
            visibleRecoveryFloorFrameId,
            appliedHeadFrameId,
            stableVisibleHeadFrameId,
            currentFramesAppliedSinceLastGap,
            currentEpochApplyCount);
        var proofFrameId = Math.Max(lastVisibleApplyFrameId, Math.Max(appliedHeadFrameId, stableVisibleHeadFrameId));
        if (visibleRecoveryFloorFrameId >= 0 && proofFrameId >= visibleRecoveryFloorFrameId + 1)
        {
            return (
                true,
                "recovery_floor_plus_head",
                proofFrameId,
                Math.Max(framesAppliedSinceLastGap, proofFrameId - visibleRecoveryFloorFrameId + 1));
        }

        if (visibleRecoveryFloorFrameId < 0 &&
            stableVisibleHeadFrameId >= 0 &&
            framesAppliedSinceLastGap >= 4)
        {
            return (
                true,
                "stable_visible_plus_applies",
                proofFrameId,
                framesAppliedSinceLastGap);
        }

        return (false, "none", -1L, framesAppliedSinceLastGap);
    }

    private void ResetHelperRemotePostRecoveryHealthyLatch_NoLock()
    {
        helperRemotePostRecoveryHealthyLastHeadAdvanceUtc = default;
    }

    private bool IsHelperRemotePostRecoveryHealthyLatched_NoLock(long streamEpoch)
    {
        return streamEpoch > 0 &&
               helperRemoteSteadyProgressEpoch == streamEpoch &&
               helperRemoteSteadyVisibleProgressActive;
    }

    private void MarkHelperRemotePostRecoveryHealthyLatched_NoLock(
        long streamEpoch,
        long activationFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        long normalizedAgeMs,
        DateTimeOffset nowUtc)
    {
        var alreadyLatched = IsHelperRemotePostRecoveryHealthyLatched_NoLock(streamEpoch);
        var reseedBaselineAfterStallRelatch =
            !alreadyLatched &&
            helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending;
        helperRemoteSteadyProgressEpoch = streamEpoch;
        helperRemoteSteadyVisibleProgressActive = true;
        helperRemoteSteadyProgressActivationFrameId = activationFrameId;
        helperRemoteSteadyProgressStableVisibleHeadFrameId = Math.Max(
            helperRemoteSteadyProgressStableVisibleHeadFrameId,
            stableVisibleHeadFrameId);
        helperRemoteSteadyProgressFramesAppliedSinceLastGap = Math.Max(
            helperRemoteSteadyProgressFramesAppliedSinceLastGap,
            Math.Max(1L, framesAppliedSinceLastGap));
        helperRemoteSteadyVisibleProgressClearedReason = string.Empty;
        helperRemotePostRecoveryHealthySignalSent = true;
        if (reseedBaselineAfterStallRelatch)
        {
            BeginHelperRemotePressureBaselineReseedAfterStall_NoLock(nowUtc);
        }
        else
        {
            helperRemoteCurrentPressureEpochBaselineEstablished = true;
            helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = Math.Max(0L, normalizedAgeMs);
            helperRemoteCurrentPressureEpochBaselineSampleCount = Math.Max(1L, helperRemoteCurrentPressureEpochBaselineSampleCount);
            helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
            helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies = 0;
            helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
            helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = default;
            helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
        }

        helperRemoteCurrentPressureEpochWarmupEndedUtc = nowUtc;
        helperRemoteCurrentPressureEpochContinuityLossTicks = 0;
        helperRemoteCurrentPressureEpochWarmupTicks = 0;
        helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks = 0;
        helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameTicks = 0;
        helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = 0;
        helperRemotePostRecoveryHealthyLastHeadAdvanceUtc = nowUtc;
        if (!alreadyLatched)
        {
            helperRemotePostRecoveryHealthyLatchCount++;
        }
    }

    private bool ShouldClearHelperRemotePostRecoveryHealthyLatchForStall_NoLock(DateTimeOffset nowUtc, long streamEpoch)
    {
        if (!IsHelperRemotePostRecoveryHealthyLatched_NoLock(streamEpoch) ||
            helperRemoteContinuityRecoveryActive && helperRemoteContinuityRecoveryEpoch == streamEpoch)
        {
            return false;
        }

        var lastHeadAdvanceUtc = helperRemotePostRecoveryHealthyLastHeadAdvanceUtc;
        if (helperRemoteLastAppliedFrameUtc == default || lastHeadAdvanceUtc == default)
        {
            return false;
        }

        var noApplyMs = nowUtc >= helperRemoteLastAppliedFrameUtc
            ? Math.Max(0L, (long)(nowUtc - helperRemoteLastAppliedFrameUtc).TotalMilliseconds)
            : 0L;
        var noHeadAdvanceMs = nowUtc >= lastHeadAdvanceUtc
            ? Math.Max(0L, (long)(nowUtc - lastHeadAdvanceUtc).TotalMilliseconds)
            : 0L;
        return noApplyMs >= (long)HelperRemoteScreenSharePostRecoveryHealthyLatchStallTimeout.TotalMilliseconds &&
               noHeadAdvanceMs >= (long)HelperRemoteScreenSharePostRecoveryHealthyLatchStallTimeout.TotalMilliseconds;
    }

    private void ClearHelperRemoteSteadyVisibleProgressState_NoLock(string reason)
    {
        var hadSteadyProgressState =
            helperRemoteSteadyProgressEpoch > 0 &&
            (helperRemoteSteadyVisibleProgressActive ||
             helperRemoteSteadyProgressActivationFrameId >= 0 ||
             helperRemoteSteadyProgressVisibleHeadFrameId >= 0 ||
             helperRemoteSteadyProgressStableVisibleHeadFrameId >= 0 ||
             helperRemoteSteadyProgressFramesAppliedSinceLastGap > 0);
        if (hadSteadyProgressState)
        {
            helperRemoteSteadyVisibleProgressClearedCount++;
            helperRemoteSteadyVisibleProgressClearedReason = string.IsNullOrWhiteSpace(reason)
                ? "unknown"
                : reason.Trim();
            helperRemotePostRecoveryHealthyLatchClearCount++;
            helperRemotePostRecoveryHealthyLatchClearReason = helperRemoteSteadyVisibleProgressClearedReason;
        }

        helperRemoteSteadyProgressEpoch = 0;
        helperRemoteSteadyVisibleProgressActive = false;
        helperRemoteSteadyProgressActivationFrameId = -1;
        helperRemoteSteadyProgressVisibleHeadFrameId = -1;
        helperRemoteSteadyProgressStableVisibleHeadFrameId = -1;
        helperRemoteSteadyProgressFramesAppliedSinceLastGap = 0;
        ResetHelperRemotePostRecoveryHealthyLatch_NoLock();
        ResetHelperRemoteSentVisibleProgressProof_NoLock();
    }

    private void UpdateHelperRemoteSteadyVisibleProgressStateForApply_NoLock(
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        long normalizedAgeMs,
        DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0 || helperRemoteCurrentPressureEpoch != streamEpoch)
        {
            return;
        }

        if (helperRemoteSteadyProgressEpoch != streamEpoch)
        {
            helperRemoteSteadyProgressEpoch = streamEpoch;
            helperRemoteSteadyVisibleProgressActive = false;
            helperRemoteSteadyProgressActivationFrameId = -1;
            helperRemoteSteadyProgressVisibleHeadFrameId = -1;
            helperRemoteSteadyProgressStableVisibleHeadFrameId = -1;
            helperRemoteSteadyProgressFramesAppliedSinceLastGap = 0;
        }

        var activeSessionId = GetHelperRemoteActiveSessionId_NoLock();
        var visibleRecoveryFloorFrameId =
            !string.IsNullOrWhiteSpace(activeSessionId)
                ? ScreenShareFrameLossAttributionRegistry.GetVisibleRecoveryFloorFrameId(activeSessionId, streamEpoch)
                : -1L;
        var previousVisibleHeadFrameId = helperRemoteSteadyProgressVisibleHeadFrameId;
        var previousStableVisibleHeadFrameId = helperRemoteSteadyProgressStableVisibleHeadFrameId;

        if (visibleHeadFrameId >= 0)
        {
            helperRemoteSteadyProgressVisibleHeadFrameId = Math.Max(
                helperRemoteSteadyProgressVisibleHeadFrameId,
                visibleHeadFrameId);
        }

        if (stableVisibleHeadFrameId >= 0)
        {
            helperRemoteSteadyProgressStableVisibleHeadFrameId = Math.Max(
                helperRemoteSteadyProgressStableVisibleHeadFrameId,
                stableVisibleHeadFrameId);
        }

        if (frameId > previousVisibleHeadFrameId ||
            helperRemoteSteadyProgressVisibleHeadFrameId > previousVisibleHeadFrameId ||
            helperRemoteSteadyProgressStableVisibleHeadFrameId > previousStableVisibleHeadFrameId)
        {
            helperRemotePostRecoveryHealthyLastHeadAdvanceUtc = nowUtc;
        }

        var appliedHeadFrameId = Math.Max(frameId, helperRemoteSteadyProgressVisibleHeadFrameId);
        helperRemoteSteadyProgressFramesAppliedSinceLastGap = Math.Max(
            helperRemoteSteadyProgressFramesAppliedSinceLastGap,
            ComputeDerivedPostRecoveryHealthyState(
                visibleRecoveryFloorFrameId,
                helperRemoteSteadyProgressVisibleHeadFrameId,
                appliedHeadFrameId,
                helperRemoteSteadyProgressStableVisibleHeadFrameId,
                Math.Max(0L, framesAppliedSinceLastGap),
                helperRemoteCurrentPressureEpochApplyCount).FramesAppliedSinceLastGap);

        if (helperRemoteSteadyVisibleProgressActive)
        {
            return;
        }

        var derivedPostRecoveryHealthy = ComputeDerivedPostRecoveryHealthyState(
            visibleRecoveryFloorFrameId,
            helperRemoteSteadyProgressVisibleHeadFrameId,
            appliedHeadFrameId,
            helperRemoteSteadyProgressStableVisibleHeadFrameId,
            helperRemoteSteadyProgressFramesAppliedSinceLastGap,
            helperRemoteCurrentPressureEpochApplyCount);
        if (!derivedPostRecoveryHealthy.Active)
        {
            return;
        }

        MarkHelperRemotePostRecoveryHealthyLatched_NoLock(
            streamEpoch,
            derivedPostRecoveryHealthy.ProofFrameId >= 0
                ? derivedPostRecoveryHealthy.ProofFrameId
                : (frameId >= 0
                    ? frameId
                    : Math.Max(helperRemoteSteadyProgressVisibleHeadFrameId, helperRemoteSteadyProgressStableVisibleHeadFrameId)),
            helperRemoteSteadyProgressStableVisibleHeadFrameId,
            derivedPostRecoveryHealthy.FramesAppliedSinceLastGap,
            normalizedAgeMs,
            nowUtc);
    }

    private void ResetHelperRemoteProgressAwarePressureState_NoLock()
    {
        helperRemoteCurrentPressureEpochBaselineEstablished = false;
        helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = 0d;
        helperRemoteCurrentPressureEpochBaselineSampleCount = 0;
        helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
        helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending = false;
        helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies = 0;
        helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
        helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = default;
        helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
        helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId = -1;
        helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId = -1;
        helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount = 0;
        helperRemoteCurrentPressureEpochActionableHighFrameAgeCount = 0;
        helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
        helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
        helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount = 0;
    }

    private void FreezeHelperRemotePressureBaselineUntilNextApply_NoLock(bool dueToStall)
    {
        helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = true;
        helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = default;
        helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
        if (dueToStall)
        {
            helperRemoteCurrentPressureEpochBaselineFrozenDueToStallCount++;
            helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending = true;
        }

        helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
    }

    private void BeginHelperRemotePressureBaselineReseed_NoLock(DateTimeOffset nowUtc, bool countAsAfterRecovery)
    {
        helperRemoteCurrentPressureEpochBaselineEstablished = false;
        helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = 0d;
        helperRemoteCurrentPressureEpochBaselineSampleCount = 0;
        helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
        helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending = false;
        helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies = HelperRemoteScreenShareBaselineReseedVisibleApplies;
        helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
        helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = nowUtc;
        helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = helperRemoteCurrentPressureEpochLastVisibleApplyFrameId;
        if (countAsAfterRecovery)
        {
            helperRemoteCurrentPressureEpochBaselineReseedAfterRecoveryCount++;
        }

        helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
        helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
    }

    private void BeginHelperRemotePressureBaselineReseedAfterRecovery_NoLock(DateTimeOffset nowUtc)
    {
        BeginHelperRemotePressureBaselineReseed_NoLock(nowUtc, countAsAfterRecovery: true);
    }

    private void BeginHelperRemotePressureBaselineReseedAfterStall_NoLock(DateTimeOffset nowUtc)
    {
        BeginHelperRemotePressureBaselineReseed_NoLock(nowUtc, countAsAfterRecovery: false);
    }

    private void UpdateHelperRemotePressureBaselineForVisibleApply_NoLock(long streamEpoch, long normalizedAgeMs, long frameId)
    {
        if (streamEpoch <= 0 || helperRemoteCurrentPressureEpoch != streamEpoch)
        {
            return;
        }

        if (helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply)
        {
            helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
            helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
            helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
            return;
        }

        if (helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies > 0)
        {
            helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
            helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
            var reseedExpired =
                helperRemoteCurrentPressureEpochBaselineReseedStartedUtc != default &&
                nowProvider() - helperRemoteCurrentPressureEpochBaselineReseedStartedUtc > HelperRemoteScreenShareBaselineReseedTimeout;
            var eligibleReseedApply =
                !reseedExpired &&
                frameId >= 0 &&
                frameId > helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId &&
                normalizedAgeMs <= HelperRemoteScreenShareBaselineReseedEligibleAgeThresholdMs &&
                helperRemoteLastAppliedFrameUtc != default &&
                nowProvider() - helperRemoteLastAppliedFrameUtc <= HelperRemoteScreenSharePostRecoveryVisibleProgressWindow;
            if (!eligibleReseedApply)
            {
                return;
            }

            helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs += normalizedAgeMs;
            helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = frameId;
            helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies--;
            if (helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies == 0)
            {
                helperRemoteCurrentPressureEpochBaselineEstablished = true;
                helperRemoteCurrentPressureEpochBaselineSampleCount = HelperRemoteScreenShareBaselineReseedVisibleApplies;
                helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs =
                    helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs / (double)HelperRemoteScreenShareBaselineReseedVisibleApplies;
                helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
                helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = default;
                helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
            }

            return;
        }

        var shouldEstablishBaseline =
            !helperRemoteContinuityRecoveryActive &&
            helperRemoteCurrentPressureEpochApplyCount >= HelperRemoteScreenShareBaselineEstablishVisibleApplies;
        if (!shouldEstablishBaseline)
        {
            return;
        }

        if (!helperRemoteCurrentPressureEpochBaselineEstablished)
        {
            helperRemoteCurrentPressureEpochBaselineEstablished = true;
            helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = normalizedAgeMs;
            helperRemoteCurrentPressureEpochBaselineSampleCount = 1;
            helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
            helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
            return;
        }

        helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs =
            (helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs * (1d - HelperRemoteScreenShareBaselineEwmaAlpha)) +
            (normalizedAgeMs * HelperRemoteScreenShareBaselineEwmaAlpha);
        helperRemoteCurrentPressureEpochBaselineSampleCount++;
    }

    internal void ReportHelperRemoteScreenShareStaleFrameDropped(
        long renderedAgeMs,
        long streamEpoch,
        bool referenceContinuityPreserved = false)
    {
        screenShareControlHost.ReportHelperRemoteScreenShareStaleFrameDropped(
            renderedAgeMs,
            streamEpoch,
            referenceContinuityPreserved);
    }

    private void ReportHelperRemoteScreenShareStaleFrameDroppedCore(
        long renderedAgeMs,
        long streamEpoch,
        bool referenceContinuityPreserved = false)
    {
        if (disposed || role != SessionRuntimeRole.Helper)
        {
            return;
        }

        var nowUtc = nowProvider();
        lock (helperRemoteScreenSharePressureGate)
        {
            EnsureHelperRemoteScreenSharePressureEpoch_NoLock(streamEpoch, nowUtc);
            helperRemoteViewerStaleDropCount++;
            helperRemoteCurrentPressureEpochStaleDropCount++;
            if (referenceContinuityPreserved)
            {
                helperRemoteViewerSoftStaleDropCount++;
                helperRemoteCurrentPressureEpochSoftStaleDropCount++;
            }
        }

        MaybeSendScreenSharePressureState();
    }

    private HelperRemoteScreenSharePressureSnapshot GetHelperRemoteScreenSharePressureSnapshot()
    {
        var nowUtc = nowProvider();
        lock (helperRemoteScreenSharePressureGate)
        {
            if (ShouldClearHelperRemotePostRecoveryHealthyLatchForStall_NoLock(nowUtc, helperRemoteCurrentPressureEpoch))
            {
                ClearHelperRemoteSteadyVisibleProgressState_NoLock("post_recovery_stall");
            }

            var recentHighAppliedFrameCount = 0;
            for (var i = 0; i < helperRemoteRecentAppliedFrameCount; i++)
            {
                if (helperRemoteRecentAppliedFrameAgesMs[i] >= 450)
                {
                    recentHighAppliedFrameCount++;
                }
            }

            var currentEpoch = helperRemoteCurrentPressureEpoch;
            var currentEpochWarmupActive = IsHelperRemoteCurrentEpochWarmupActive_NoLock(nowUtc, currentEpoch);
            if (currentEpochWarmupActive)
            {
                if (helperRemoteCurrentPressureEpochWarmupStartedUtc == default)
                {
                    helperRemoteCurrentPressureEpochWarmupStartedUtc = nowUtc;
                }

                helperRemoteCurrentPressureEpochWarmupEndedUtc = default;
            }
            else if (helperRemoteCurrentPressureEpochWarmupStartedUtc != default &&
                     helperRemoteCurrentPressureEpochWarmupEndedUtc == default)
            {
                helperRemoteCurrentPressureEpochWarmupEndedUtc = nowUtc;
            }

            var activeSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
            var frameLossSnapshot = string.IsNullOrWhiteSpace(activeSessionId)
                ? ScreenShareFrameLossSessionSnapshot.Empty
                : ScreenShareFrameLossAttributionRegistry.GetSnapshot(activeSessionId);
            ScreenShareEpochDiagnosticsSnapshot? currentEpochDiagnostics = null;
            if (currentEpoch > 0)
            {
                currentEpochDiagnostics = frameLossSnapshot.EpochDiagnostics.FirstOrDefault(epoch => epoch.StreamEpoch == currentEpoch);
            }

            var authoritativeProgressState = ResolveHelperRemoteAuthoritativeProgressState_NoLock(
                currentEpoch,
                currentEpochDiagnostics);
            var lastVisibleApplyFrameId = authoritativeProgressState.LastVisibleApplyFrameId;
            var visibleHeadFrameId = authoritativeProgressState.VisibleHeadFrameId;
            var appliedHeadFrameId = authoritativeProgressState.AppliedHeadFrameId;
            var stableVisibleHeadFrameId = authoritativeProgressState.StableVisibleHeadFrameId;
            var visibleRecoveryFloorFrameId = authoritativeProgressState.VisibleRecoveryFloorFrameId;
            var framesAppliedSinceLastGap = authoritativeProgressState.FramesAppliedSinceLastGap;
            var currentEpochGapCount = currentEpochDiagnostics?.GapCount ?? 0;
            var currentEpochRecoveryKeyframeApplyCount = currentEpochDiagnostics?.RecoveryKeyframeApplyCount ?? 0;
            currentEpochRecoveryKeyframeApplyCount = Math.Max(
                currentEpochRecoveryKeyframeApplyCount,
                helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal);
            var currentEpochResyncCount = currentEpochDiagnostics?.ResyncCount ?? 0;
            var timeSinceLastVisibleApplyMs =
                helperRemoteLastAppliedFrameUtc != default && nowUtc >= helperRemoteLastAppliedFrameUtc
                    ? Math.Max(0L, (long)(nowUtc - helperRemoteLastAppliedFrameUtc).TotalMilliseconds)
                    : -1L;
            var effectiveCurrentCaptureToRenderMs =
                helperRemoteLastAppliedFrameAgeMs >= 0
                    ? Math.Max(0L, helperRemoteLastAppliedFrameAgeMs + Math.Max(0L, timeSinceLastVisibleApplyMs))
                    : -1L;
            var steadyVisibleProgressActive = authoritativeProgressState.SteadyVisibleProgressActive;
            currentEpochWarmupActive = currentEpochWarmupActive && !steadyVisibleProgressActive;

            var warmupEndUtc = helperRemoteCurrentPressureEpochWarmupEndedUtc != default
                ? helperRemoteCurrentPressureEpochWarmupEndedUtc
                : nowUtc;
            var timeSpentInHelperWarmupMs =
                helperRemoteCurrentPressureEpochWarmupStartedUtc != default && warmupEndUtc >= helperRemoteCurrentPressureEpochWarmupStartedUtc
                    ? Math.Max(0L, (long)(warmupEndUtc - helperRemoteCurrentPressureEpochWarmupStartedUtc).TotalMilliseconds)
                    : 0L;
            var baselineEstablished = helperRemoteCurrentPressureEpochBaselineEstablished && helperRemoteCurrentPressureEpochBaselineSampleCount > 0;
            var baselineReseedInProgress = helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies > 0;
            var baselineCaptureToRenderMs = baselineEstablished
                ? Math.Max(0L, (long)Math.Round(helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs, MidpointRounding.AwayFromZero))
                : -1L;
            var ageExcessMs =
                baselineEstablished && effectiveCurrentCaptureToRenderMs >= 0
                    ? Math.Max(0L, effectiveCurrentCaptureToRenderMs - baselineCaptureToRenderMs)
                    : -1L;
            var postRecoveryAgeGraceActive =
                helperRemotePostRecoveryAgeGraceEpoch == currentEpoch &&
                helperRemotePostRecoveryAgeGraceUntilUtc != default &&
                nowUtc <= helperRemotePostRecoveryAgeGraceUntilUtc;
            var currentEpochRecoveryActive =
                (helperRemoteContinuityRecoveryActive && helperRemoteContinuityRecoveryEpoch == currentEpoch) ||
                (authoritativeProgressState.SessionSnapshotPresent && authoritativeProgressState.SessionSnapshot.RecoveryActive);

            return new HelperRemoteScreenSharePressureSnapshot(
                HasAppliedFrame: helperRemoteRecentAppliedFrameCount > 0,
                LastAppliedFrameAgeMs: helperRemoteLastAppliedFrameAgeMs,
                RecentAppliedHighFrameCount: recentHighAppliedFrameCount,
                ConsecutiveVeryHighAppliedFrames: helperRemoteConsecutiveVeryHighAppliedFrames,
                LastApplyCadenceMs: helperRemoteLastApplyCadenceMs,
                AverageApplyCadenceMs: helperRemoteApplyCadenceObserved > 0
                    ? (double)helperRemoteTotalApplyCadenceMs / helperRemoteApplyCadenceObserved
                    : 0d,
                ViewerStaleDropCount: helperRemoteViewerStaleDropCount,
                ViewerSoftStaleDropCount: helperRemoteViewerSoftStaleDropCount,
                CurrentEpoch: currentEpoch,
                CurrentEpochFirstApplySeen: helperRemoteCurrentPressureEpochFirstApplySeen,
                CurrentEpochWarmupActive: currentEpochWarmupActive,
                CurrentEpochApplyCount: helperRemoteCurrentPressureEpochApplyCount,
                CurrentEpochNeedMoreInputCount: helperRemoteCurrentPressureEpochNeedMoreInputCount,
                CurrentEpochStaleDropCount: helperRemoteCurrentPressureEpochStaleDropCount,
                CurrentEpochSoftStaleDropCount: helperRemoteCurrentPressureEpochSoftStaleDropCount,
                LastVisibleApplyFrameId: lastVisibleApplyFrameId,
                VisibleHeadFrameId: visibleHeadFrameId,
                VisibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
                AppliedHeadFrameId: appliedHeadFrameId,
                FramesAppliedSinceLastGap: framesAppliedSinceLastGap,
                StableVisibleHeadFrameId: stableVisibleHeadFrameId,
                CurrentEpochGapCount: currentEpochGapCount,
                CurrentEpochRecoveryKeyframeApplyCount: currentEpochRecoveryKeyframeApplyCount,
                CurrentEpochResyncCount: currentEpochResyncCount,
                CurrentEpochRecoveryActive: currentEpochRecoveryActive,
                CurrentEpochRecoveryStartedUtc: helperRemoteContinuityRecoveryStartedUtc,
                CurrentEpochRecoveryTimeoutSent: helperRemoteContinuityRecoveryTimeoutSent,
                CurrentEpochPostRecoveryStabilizationActive:
                    authoritativeProgressState.SessionSnapshotPresent &&
                    authoritativeProgressState.SessionSnapshot.PostRecoveryStabilizationActive,
                CurrentEpochPostRecoveryHealthySignalSent: helperRemotePostRecoveryHealthySignalSent,
                HelperSessionPhase:
                    authoritativeProgressState.SessionSnapshotPresent
                        ? authoritativeProgressState.SessionSnapshot.Phase
                        : (steadyVisibleProgressActive ? HelperRemoteSessionPhase.VisibleStable : HelperRemoteSessionPhase.NoVisibleBaseline),
                HelperRecoveryMechanism:
                    authoritativeProgressState.SessionSnapshotPresent
                        ? authoritativeProgressState.SessionSnapshot.RecoveryMechanism
                        : HelperRemoteRecoveryMechanism.None,
                HelperBaselineEstablished:
                    authoritativeProgressState.SessionSnapshotPresent
                        ? authoritativeProgressState.SessionSnapshot.BaselineEstablished
                        : visibleHeadFrameId >= 0 || appliedHeadFrameId >= 0 || stableVisibleHeadFrameId >= 0,
                CurrentEpochProgressProven: authoritativeProgressState.CurrentEpochProgressProven,
                CurrentEpochProgressProofSource: authoritativeProgressState.CurrentEpochProgressProofSource,
                CurrentEpochProvenHeadFrameId: authoritativeProgressState.CurrentEpochProvenHeadFrameId,
                TimeSinceLastVisibleApplyMs: timeSinceLastVisibleApplyMs,
                BaselineEstablished: baselineEstablished,
                BaselineCaptureToRenderMs: baselineCaptureToRenderMs,
                AgeExcessMs: ageExcessMs,
                ProgressStallMs: timeSinceLastVisibleApplyMs,
                BaselineReseedInProgress: baselineReseedInProgress,
                AgePressureConsecutiveCount: helperRemoteCurrentPressureEpochAgePressureConsecutiveCount,
                CadencePressureConsecutiveCount: helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount,
                CatchUpSuppressedDueToProgressCount: helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount,
                BaselineFrozenDueToStallCount: helperRemoteCurrentPressureEpochBaselineFrozenDueToStallCount,
                BaselineReseedAfterRecoveryCount: helperRemoteCurrentPressureEpochBaselineReseedAfterRecoveryCount,
                CadenceStallWindowCount: helperRemoteCurrentPressureEpochCadenceStallWindowCount,
                CadenceStallTriggerCount: helperRemoteCurrentPressureEpochCadenceStallTriggerCount,
                DerivedPostRecoveryHealthyActive: authoritativeProgressState.CurrentEpochProgressProven,
                DerivedPostRecoveryHealthySource: authoritativeProgressState.CurrentEpochProgressProofSource,
                DerivedPostRecoveryProofFrameId: authoritativeProgressState.CurrentEpochProvenHeadFrameId,
                SteadyVisibleProgressActive: steadyVisibleProgressActive,
                SteadyVisibleProgressActivationFrameId: authoritativeProgressState.SteadyVisibleProgressActivationFrameId,
                LastSentVisibleHeadFrameId:
                    helperRemoteLastSentSteadyProgressEpoch == currentEpoch
                        ? helperRemoteLastSentVisibleHeadFrameId
                        : -1L,
                LastSentStableVisibleHeadFrameId:
                    helperRemoteLastSentSteadyProgressEpoch == currentEpoch
                        ? helperRemoteLastSentStableVisibleHeadFrameId
                        : -1L,
                PressureSendBypassedForVisibleProgressCount: helperRemotePressureSendBypassedForVisibleProgressCount,
                ProofKeepaliveSendCount: helperRemoteProofKeepaliveSendCount,
                ProofKeepaliveTimerDrivenSendCount: helperRemoteProofKeepaliveTimerDrivenSendCount,
                ProofKeepaliveLastHeadFrameId: helperRemoteLastProofKeepaliveHeadFrameId,
                ProofKeepaliveLastSendAgeMs:
                    helperRemoteLastProofKeepaliveSentUtc == default
                        ? -1
                        : Math.Max(0L, (long)(nowProvider() - helperRemoteLastProofKeepaliveSentUtc).TotalMilliseconds),
                SteadyVisibleProgressClearedCount: helperRemoteSteadyVisibleProgressClearedCount,
                SteadyVisibleProgressClearedReason: helperRemoteSteadyVisibleProgressClearedReason,
                PostRecoveryHealthyLatchCount: helperRemotePostRecoveryHealthyLatchCount,
                PostRecoveryHealthyLatchClearCount: helperRemotePostRecoveryHealthyLatchClearCount,
                PostRecoveryHealthyLatchClearReason: helperRemotePostRecoveryHealthyLatchClearReason,
                PostRecoveryAgeGraceActive: postRecoveryAgeGraceActive,
                PostRecoveryAgeGraceSuppressedCount: helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount,
                BridgeHealthAdvisoryCount: helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount,
                BridgeHealthActionableCount: helperRemoteCurrentPressureEpochBridgeHealthActionableCount,
                BridgeHealthQuarantineSuppressedCount: helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount,
                BridgeHealthActionableWithoutQueueOrDropCount: helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount,
                TimeSpentInHelperWarmupMs: timeSpentInHelperWarmupMs,
                VisibleAppliesDuringSettleCount: 0,
                PostRecoverySettleWindowCount: 0,
                PostRecoverySettleWindowSuccessCount: 0,
                PostRecoverySettleWindowTimeoutCount: 0,
                VisibleAppliesBeforePressureReenabled: -1,
                AppliedHeadAdvancedSinceLastEvaluation: helperRemoteLastAppliedHeadAdvancedSincePressureEvaluation,
                StableVisibleHeadAdvancedSinceLastEvaluation: helperRemoteLastStableVisibleHeadAdvancedSincePressureEvaluation,
                HelperHealthyStateEstablishedBy: helperRemoteLastHealthyStateEstablishedBy,
                NonHealthyClearSuppressedDueToProgressCount: helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount);
        }
    }

    private void EnsureHelperRemoteScreenSharePressureEpoch_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0 || helperRemoteCurrentPressureEpoch == streamEpoch)
        {
            return;
        }

        BeginHelperRemoteScreenSharePressureEpoch_NoLock(streamEpoch, nowUtc);
    }

    private void BeginHelperRemoteScreenSharePressureEpoch_NoLock(long streamEpoch, DateTimeOffset nowUtc)
    {
        if (helperRemoteCurrentPressureEpoch > 0)
        {
            LogHelperRemoteScreenSharePressureSummary_NoLock("epoch_advanced");
        }

        if (helperRemoteActiveRecoveryReceiptOwnerEpoch != streamEpoch)
        {
            ClearHelperRemoteActiveRecoveryReceiptOwner_NoLock();
        }

        ResetHelperRemotePublishedRecoveryReceiptState_NoLock();
        ClearHelperRemoteSteadyVisibleProgressState_NoLock("epoch_change");

        Array.Clear(helperRemoteRecentAppliedFrameAgesMs, 0, helperRemoteRecentAppliedFrameAgesMs.Length);
        helperRemoteRecentAppliedFrameCount = 0;
        helperRemoteRecentAppliedFrameIndex = 0;
        helperRemoteLastAppliedFrameAgeMs = -1;
        helperRemoteLastAppliedFrameUtc = default;
        helperRemoteLastApplyCadenceMs = -1;
        helperRemoteApplyCadenceObserved = 0;
        helperRemoteTotalApplyCadenceMs = 0;
        helperRemoteViewerStaleDropCount = 0;
        helperRemoteViewerSoftStaleDropCount = 0;
        helperRemoteConsecutiveVeryHighAppliedFrames = 0;
        helperRemoteConsecutiveStaleDropWindows = 0;
        helperRemoteCurrentPressureEpoch = streamEpoch;
        helperRemoteCurrentPressureEpochStartedUtc = nowUtc;
            helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc = nowUtc;
            helperRemoteCurrentPressureEpochFirstApplySeen = false;
            helperRemoteCurrentPressureEpochFirstVisibleApplyUtc = default;
            helperRemoteCurrentPressureEpochApplyCount = 0;
        helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal = 0;
        helperRemoteCurrentPressureEpochNeedMoreInputCount = 0;
        helperRemoteCurrentPressureEpochStaleDropCount = 0;
        helperRemoteCurrentPressureEpochSoftStaleDropCount = 0;
        helperRemoteCurrentPressureEpochLastVisibleApplyFrameId = -1;
        helperRemoteCurrentPressureEpochContinuityLossTicks = 0;
        helperRemoteCurrentPressureEpochWarmupTicks = 0;
        helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks = 0;
        helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameTicks = 0;
        helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = 0;
        helperRemoteCurrentPressureEpochSlowApplyCadenceTicks = 0;
        helperRemoteCurrentPressureEpochHighFrameAgeTicks = 0;
        helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount = 0;
        helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks = 0;
        helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount = 0;
        helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks = 0;
        helperRemoteCurrentPressureEpochBridgeHealthTicks = 0;
        helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthActionableCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount = 0;
        helperRemoteCurrentPressureEpochVisibleAppliesBeforePressureReenabled = -1;
        helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount = 0;
        helperRemoteLastReportedAppliedFrameEpoch = -1;
        helperRemoteLastReportedAppliedFrameId = -1;
        helperRemoteLastReportedSessionSnapshot = default;
        helperRemoteLastReportedSessionSnapshotUtc = default;
        helperRemoteCurrentPressureEpochBaselineEstablished = false;
        helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = 0d;
        helperRemoteCurrentPressureEpochBaselineSampleCount = 0;
        helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
        helperRemoteCurrentPressureEpochBaselineFrozenDueToStallCount = 0;
        helperRemoteCurrentPressureEpochBaselineReseedAfterRecoveryCount = 0;
        helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending = false;
        helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies = 0;
        helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
        helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = default;
        helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
        helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount = 0;
        helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
        helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
        helperRemoteCurrentPressureEpochCadenceStallWindowCount = 0;
        helperRemoteCurrentPressureEpochCadenceStallTriggerCount = 0;
        helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount = 0;
        helperRemoteCurrentPressureEpochWarmupStartedUtc = nowUtc;
        helperRemoteCurrentPressureEpochWarmupEndedUtc = default;
        helperRemoteContinuityRecoveryActive = false;
        helperRemoteContinuityRecoveryEpoch = 0;
        helperRemoteContinuityRecoveryStartedUtc = default;
        helperRemoteContinuityRecoveryTimeoutSent = false;
        helperRemotePostRecoveryStabilizationActive = false;
        helperRemotePostRecoveryStabilizationStartedUtc = default;
        helperRemotePostRecoveryAgeGraceEpoch = 0;
        helperRemotePostRecoveryAgeGraceUntilUtc = default;
        helperRemotePostRecoveryHealthySignalSent = false;
        helperRemotePostRecoverySettleWindowTimedOut = false;
        helperRemotePostRecoverySettleWindowSucceeded = false;
        helperRemoteRecoveryWindowActive = false;
        helperRemoteRecoveryWindowProgressed = false;
        helperRemoteRecoveryWindowSucceeded = false;
        helperRemoteRecoveryWindowEpoch = 0;
        helperRemoteRecoveryWindowRecoveryFrameId = -1;
        helperRemoteRecoveryWindowLastContiguousFrameId = -1;
        helperRemoteRecoveryWindowContiguousFollowerApplyCount = 0;
        helperRemoteRecoveryWindowAbortReason = string.Empty;
        helperRemoteLastRecoveryKeyframeRequestUtc = default;
        helperRemoteLastRecoveryKeyframeRequestEpoch = 0;
        helperRemotePostRecoveryHealthyLatchCount = 0;
        helperRemotePostRecoveryHealthyLatchClearCount = 0;
        helperRemotePostRecoveryHealthyLatchClearReason = string.Empty;
        helperRemoteProofKeepaliveSendCount = 0;
        helperRemoteProofKeepaliveTimerDrivenSendCount = 0;
        helperRemoteLastProofKeepaliveHeadFrameId = -1;
        helperRemoteLastProofKeepaliveSentUtc = default;
        ResetHelperRemotePostRecoveryHealthyLatch_NoLock();
    }

    private void ResetHelperRemoteScreenSharePressureAfterRecoveryKeyframe_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc)
    {
        ClearHelperRemoteSteadyVisibleProgressState_NoLock("recovery_keyframe_applied");
        Array.Clear(helperRemoteRecentAppliedFrameAgesMs, 0, helperRemoteRecentAppliedFrameAgesMs.Length);
        helperRemoteRecentAppliedFrameCount = 0;
        helperRemoteRecentAppliedFrameIndex = 0;
        helperRemoteLastAppliedFrameAgeMs = -1;
        helperRemoteLastAppliedFrameUtc = default;
        helperRemoteLastApplyCadenceMs = -1;
        helperRemoteApplyCadenceObserved = 0;
        helperRemoteTotalApplyCadenceMs = 0;
        helperRemoteViewerStaleDropCount = 0;
        helperRemoteViewerSoftStaleDropCount = 0;
        helperRemoteConsecutiveVeryHighAppliedFrames = 0;
        helperRemoteConsecutiveStaleDropWindows = 0;
        helperRemoteCurrentPressureEpoch = streamEpoch;
        helperRemoteCurrentPressureEpochStartedUtc = nowUtc;
        if (helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc == default)
        {
            helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc = nowUtc;
        }
        helperRemoteCurrentPressureEpochFirstApplySeen = false;
        helperRemoteCurrentPressureEpochFirstVisibleApplyUtc = default;
        helperRemoteCurrentPressureEpochApplyCount = 0;
        helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal = 0;
        helperRemoteCurrentPressureEpochNeedMoreInputCount = 0;
        helperRemoteCurrentPressureEpochStaleDropCount = 0;
        helperRemoteCurrentPressureEpochSoftStaleDropCount = 0;
        helperRemoteCurrentPressureEpochLastVisibleApplyFrameId = -1;
        helperRemoteCurrentPressureEpochContinuityLossTicks = 0;
        helperRemoteCurrentPressureEpochWarmupTicks = 0;
        helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks = 0;
        helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameTicks = 0;
        helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = 0;
        helperRemoteCurrentPressureEpochSlowApplyCadenceTicks = 0;
        helperRemoteCurrentPressureEpochHighFrameAgeTicks = 0;
        helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount = 0;
        helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount = 0;
        helperRemoteCurrentPressureEpochActionableHighFrameAgeCount = 0;
        helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks = 0;
        helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount = 0;
        helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks = 0;
        helperRemoteCurrentPressureEpochBridgeHealthTicks = 0;
        helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthActionableCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount = 0;
        helperRemoteCurrentPressureEpochVisibleAppliesBeforePressureReenabled = -1;
        helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount = 0;
        helperRemoteLastReportedAppliedFrameEpoch = -1;
        helperRemoteLastReportedAppliedFrameId = -1;
        helperRemoteCurrentPressureEpochBaselineEstablished = false;
        helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = 0d;
        helperRemoteCurrentPressureEpochBaselineSampleCount = 0;
        helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
        helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending = false;
        helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies = 0;
        helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
        helperRemoteCurrentPressureEpochBaselineReseedStartedUtc = default;
        helperRemoteCurrentPressureEpochBaselineReseedMinimumFrameId = -1;
        helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
        helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount = 0;
        helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
        helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
        helperRemoteCurrentPressureEpochWarmupStartedUtc = nowUtc;
        helperRemoteCurrentPressureEpochWarmupEndedUtc = default;
        helperRemoteContinuityRecoveryActive = false;
        helperRemoteContinuityRecoveryEpoch = 0;
        helperRemoteContinuityRecoveryStartedUtc = default;
        helperRemoteContinuityRecoveryTimeoutSent = false;
        helperRemotePostRecoveryStabilizationActive = false;
        helperRemotePostRecoveryStabilizationStartedUtc = default;
        helperRemotePostRecoveryAgeGraceEpoch = 0;
        helperRemotePostRecoveryAgeGraceUntilUtc = default;
        helperRemotePostRecoveryHealthySignalSent = false;
        helperRemotePostRecoverySettleWindowTimedOut = false;
        helperRemotePostRecoverySettleWindowSucceeded = false;
        helperRemotePostRecoverySettleWindowCount = 0;
        helperRemotePostRecoverySettleWindowSuccessCount = 0;
        helperRemotePostRecoverySettleWindowTimeoutCount = 0;
        helperRemoteRecoveryWindowActive = false;
        helperRemoteRecoveryWindowProgressed = false;
        helperRemoteRecoveryWindowSucceeded = false;
        helperRemoteRecoveryWindowProgressedCount = 0;
        helperRemoteRecoveryWindowSuccessCount = 0;
        helperRemoteRecoveryWindowEpoch = 0;
        helperRemoteRecoveryWindowRecoveryFrameId = -1;
        helperRemoteRecoveryWindowLastContiguousFrameId = -1;
        helperRemoteRecoveryWindowContiguousFollowerApplyCount = 0;
        helperRemoteRecoveryWindowAbortReason = string.Empty;
        helperRemoteLastRecoveryKeyframeRequestUtc = default;
        helperRemoteLastRecoveryKeyframeRequestEpoch = 0;
        helperRemotePostRecoveryHealthyLatchCount = 0;
        helperRemotePostRecoveryHealthyLatchClearCount = 0;
        helperRemotePostRecoveryHealthyLatchClearReason = string.Empty;
        helperRemoteProofKeepaliveSendCount = 0;
        helperRemoteProofKeepaliveTimerDrivenSendCount = 0;
        helperRemoteLastProofKeepaliveHeadFrameId = -1;
        helperRemoteLastProofKeepaliveSentUtc = default;
        ResetHelperRemotePostRecoveryHealthyLatch_NoLock();
    }

    private void LogAndResetHelperRemoteScreenShareSummary(string reason)
    {
        if (role != SessionRuntimeRole.Helper)
        {
            return;
        }

        lock (helperRemoteScreenSharePressureGate)
        {
            LogHelperRemoteScreenSharePressureSummary_NoLock(reason);
        }

        var acceptedFrames = Interlocked.Exchange(ref helperRemoteScreenShareAcceptedFrames, 0);
        var lastAcceptedEpoch = Interlocked.Exchange(ref helperRemoteScreenShareLastAcceptedEpoch, 0);
        var sawConfig = Interlocked.Exchange(ref helperRemoteScreenShareSawConfig, 0);
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=helper_remote_screenshare_frame_summary; role=helper_remote; reason={reason}; accepted_frames={acceptedFrames}; last_accepted_epoch={lastAcceptedEpoch}; saw_frame_with_config={sawConfig}");
    }

    private void ResetHelperRemoteScreenSharePressureTracking()
    {
        lock (helperRemoteScreenSharePressureGate)
        {
            ResetHelperRemoteRecoveryReceiptPublicationState_NoLock();
            ClearHelperRemoteSteadyVisibleProgressState_NoLock("tracking_reset");
            Array.Clear(helperRemoteRecentAppliedFrameAgesMs, 0, helperRemoteRecentAppliedFrameAgesMs.Length);
            helperRemoteRecentAppliedFrameCount = 0;
            helperRemoteRecentAppliedFrameIndex = 0;
            helperRemoteLastAppliedFrameAgeMs = -1;
            helperRemoteLastAppliedFrameUtc = default;
            helperRemoteLastApplyCadenceMs = -1;
            helperRemoteApplyCadenceObserved = 0;
            helperRemoteTotalApplyCadenceMs = 0;
            helperRemoteViewerStaleDropCount = 0;
            helperRemoteViewerSoftStaleDropCount = 0;
            helperRemoteConsecutiveVeryHighAppliedFrames = 0;
            helperRemoteConsecutiveStaleDropWindows = 0;
            helperRemoteCurrentPressureEpoch = 0;
            helperRemoteCurrentPressureEpochStartedUtc = default;
            helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc = default;
            helperRemoteCurrentPressureEpochFirstApplySeen = false;
            helperRemoteCurrentPressureEpochFirstVisibleApplyUtc = default;
            helperRemoteCurrentPressureEpochApplyCount = 0;
            helperRemoteCurrentPressureEpochRecoveryKeyframeApplyCountLocal = 0;
            helperRemoteCurrentPressureEpochNeedMoreInputCount = 0;
            helperRemoteCurrentPressureEpochStaleDropCount = 0;
            helperRemoteCurrentPressureEpochSoftStaleDropCount = 0;
            helperRemoteCurrentPressureEpochLastVisibleApplyFrameId = -1;
            helperRemoteCurrentPressureEpochContinuityLossTicks = 0;
            helperRemoteCurrentPressureEpochWarmupTicks = 0;
            helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks = 0;
            helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameTicks = 0;
            helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameSuppressedDueToSuccessCount = 0;
            helperRemoteCurrentPressureEpochSlowApplyCadenceTicks = 0;
            helperRemoteCurrentPressureEpochHighFrameAgeTicks = 0;
            helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount = 0;
            helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount = 0;
            helperRemoteCurrentPressureEpochActionableHighFrameAgeCount = 0;
            helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks = 0;
            helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount = 0;
            helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks = 0;
            helperRemoteCurrentPressureEpochBridgeHealthTicks = 0;
            helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount = 0;
            helperRemoteCurrentPressureEpochBridgeHealthActionableCount = 0;
            helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount = 0;
            helperRemoteCurrentPressureEpochBridgeHealthCorrelationConsecutiveCount = 0;
            helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount = 0;
            helperRemoteCurrentPressureEpochVisibleAppliesBeforePressureReenabled = -1;
            helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount = 0;
            helperRemoteLastReportedAppliedFrameEpoch = -1;
            helperRemoteLastReportedAppliedFrameId = -1;
            helperRemoteCurrentPressureEpochBaselineEstablished = false;
            helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs = 0d;
            helperRemoteCurrentPressureEpochBaselineSampleCount = 0;
            helperRemoteCurrentPressureEpochBaselineFreezeUntilNextApply = false;
            helperRemoteCurrentPressureEpochBaselineFrozenDueToStallCount = 0;
            helperRemoteCurrentPressureEpochBaselineReseedAfterRecoveryCount = 0;
            helperRemoteCurrentPressureEpochBaselineReseedAfterStallPending = false;
            helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies = 0;
            helperRemoteCurrentPressureEpochBaselineReseedAccumulatedAgeMs = 0;
            helperRemoteCurrentPressureEpochAgePressureConsecutiveCount = 0;
            helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount = 0;
            helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount = 0;
            helperRemoteCurrentPressureEpochCadenceStallStartedUtc = default;
            helperRemoteCurrentPressureEpochCadenceStallTriggered = false;
            helperRemoteCurrentPressureEpochCadenceStallWindowCount = 0;
            helperRemoteCurrentPressureEpochCadenceStallTriggerCount = 0;
            helperRemoteCurrentPressureEpochWarmupStartedUtc = default;
            helperRemoteCurrentPressureEpochWarmupEndedUtc = default;
            helperRemoteContinuityRecoveryActive = false;
            helperRemoteContinuityRecoveryEpoch = 0;
            helperRemoteContinuityRecoveryStartedUtc = default;
            helperRemoteContinuityRecoveryTimeoutSent = false;
            helperRemotePostRecoveryStabilizationActive = false;
            helperRemotePostRecoveryStabilizationStartedUtc = default;
            helperRemotePostRecoveryAgeGraceEpoch = 0;
            helperRemotePostRecoveryAgeGraceUntilUtc = default;
            helperRemotePostRecoveryHealthySignalSent = false;
            helperRemotePostRecoverySettleWindowTimedOut = false;
            helperRemotePostRecoverySettleWindowSucceeded = false;
            helperRemoteRecoveryWindowActive = false;
            helperRemoteRecoveryWindowProgressed = false;
            helperRemoteRecoveryWindowSucceeded = false;
            helperRemoteRecoveryWindowEpoch = 0;
            helperRemoteRecoveryWindowRecoveryFrameId = -1;
            helperRemoteRecoveryWindowLastContiguousFrameId = -1;
            helperRemoteRecoveryWindowContiguousFollowerApplyCount = 0;
            helperRemoteRecoveryWindowAbortReason = string.Empty;
            helperRemoteLastRecoveryKeyframeRequestUtc = default;
            helperRemoteLastRecoveryKeyframeRequestEpoch = 0;
            helperRemotePostRecoveryHealthyLatchCount = 0;
            helperRemotePostRecoveryHealthyLatchClearCount = 0;
            helperRemotePostRecoveryHealthyLatchClearReason = string.Empty;
            helperRemoteProofKeepaliveSendCount = 0;
            helperRemoteProofKeepaliveTimerDrivenSendCount = 0;
            helperRemoteLastProofKeepaliveHeadFrameId = -1;
            helperRemoteLastProofKeepaliveSentUtc = default;
            ResetHelperRemotePostRecoveryHealthyLatch_NoLock();
        }
    }

    internal HelperRemoteScreenSharePressureDiagnosticsSnapshot GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests()
        => screenShareControlHost.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();

    private HelperRemoteScreenSharePressureDiagnosticsSnapshot GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTestsCore()
    {
        lock (helperRemoteScreenSharePressureGate)
        {
            return BuildHelperRemoteScreenSharePressureDiagnosticsSnapshot_NoLock();
        }
    }

    private void LogHelperRemoteScreenSharePressureSummary_NoLock(string reason)
    {
        var snapshot = BuildHelperRemoteScreenSharePressureDiagnosticsSnapshot_NoLock();
        if (snapshot.StreamEpoch <= 0)
        {
            return;
        }

        var activeSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_helper_pressure_epoch_summary; role=helper_remote; reason={reason}; session_id={(string.IsNullOrWhiteSpace(activeSessionId) ? "(none)" : activeSessionId)}; stream_epoch={snapshot.StreamEpoch}; last_visible_apply_frame_id={FormatFrameIdForPressureLog(snapshot.LastVisibleApplyFrameId)}; visible_head_frame_id={FormatFrameIdForPressureLog(snapshot.VisibleHeadFrameId)}; visible_recovery_floor_frame_id={FormatFrameIdForPressureLog(snapshot.VisibleRecoveryFloorFrameId)}; applied_head_frame_id={FormatFrameIdForPressureLog(snapshot.AppliedHeadFrameId)}; frames_applied_since_last_gap={snapshot.FramesAppliedSinceLastGap}; stable_visible_head_frame_id={FormatFrameIdForPressureLog(snapshot.StableVisibleHeadFrameId)}; helper_session_phase={ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(snapshot.HelperSessionPhase)}; helper_recovery_mechanism={ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(snapshot.HelperRecoveryMechanism)}; helper_baseline_established={(snapshot.HelperBaselineEstablished ? 1 : 0)}; current_epoch_progress_proven={(snapshot.CurrentEpochProgressProven ? 1 : 0)}; current_epoch_progress_proof_source={FormatPressureTextValue(snapshot.CurrentEpochProgressProofSource)}; current_epoch_proven_head_frame_id={FormatFrameIdForPressureLog(snapshot.CurrentEpochProvenHeadFrameId)}; derived_post_recovery_healthy_active={(snapshot.DerivedPostRecoveryHealthyActive ? 1 : 0)}; derived_post_recovery_healthy_source={FormatPressureTextValue(snapshot.DerivedPostRecoveryHealthySource)}; derived_post_recovery_proof_frame_id={FormatFrameIdForPressureLog(snapshot.DerivedPostRecoveryProofFrameId)}; steady_visible_progress_active={(snapshot.SteadyVisibleProgressActive ? 1 : 0)}; steady_visible_progress_activation_frame_id={FormatFrameIdForPressureLog(snapshot.SteadyVisibleProgressActivationFrameId)}; applied_head_advanced_since_last_evaluation={(snapshot.AppliedHeadAdvancedSinceLastEvaluation ? 1 : 0)}; stable_visible_head_advanced_since_last_evaluation={(snapshot.StableVisibleHeadAdvancedSinceLastEvaluation ? 1 : 0)}; helper_healthy_state_established_by={FormatPressureTextValue(snapshot.HelperHealthyStateEstablishedBy)}; non_healthy_clear_suppressed_due_to_progress_count={snapshot.NonHealthyClearSuppressedDueToProgressCount}; last_sent_visible_head_frame_id={FormatFrameIdForPressureLog(snapshot.LastSentVisibleHeadFrameId)}; last_sent_stable_visible_head_frame_id={FormatFrameIdForPressureLog(snapshot.LastSentStableVisibleHeadFrameId)}; pressure_send_bypassed_for_visible_progress_count={snapshot.PressureSendBypassedForVisibleProgressCount}; helper_proof_keepalive_send_count={snapshot.ProofKeepaliveSendCount}; helper_proof_keepalive_timer_driven_send_count={snapshot.ProofKeepaliveTimerDrivenSendCount}; helper_proof_keepalive_last_head_frame_id={FormatFrameIdForPressureLog(snapshot.ProofKeepaliveLastHeadFrameId)}; helper_proof_keepalive_last_send_age_ms={(snapshot.ProofKeepaliveLastSendAgeMs >= 0 ? snapshot.ProofKeepaliveLastSendAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; steady_visible_progress_cleared_count={snapshot.SteadyVisibleProgressClearedCount}; steady_visible_progress_cleared_reason={FormatPressureTextValue(snapshot.SteadyVisibleProgressClearedReason)}; post_recovery_healthy_latch_count={snapshot.PostRecoveryHealthyLatchCount}; post_recovery_healthy_latch_clear_count={snapshot.PostRecoveryHealthyLatchClearCount}; post_recovery_healthy_latch_clear_reason={FormatPressureTextValue(snapshot.PostRecoveryHealthyLatchClearReason)}; current_epoch_gap_count={snapshot.CurrentEpochGapCount}; current_epoch_recovery_keyframe_apply_count={snapshot.CurrentEpochRecoveryKeyframeApplyCount}; current_epoch_resync_count={snapshot.CurrentEpochResyncCount}; recovery_window_active={(snapshot.RecoveryWindowActive ? 1 : 0)}; recovery_window_progressed={(snapshot.RecoveryWindowProgressed ? 1 : 0)}; recovery_window_succeeded={(snapshot.RecoveryWindowSucceeded ? 1 : 0)}; recovery_window_progressed_count={snapshot.RecoveryWindowProgressedCount}; recovery_window_success_count={snapshot.RecoveryWindowSuccessCount}; active_recovery_window_epoch={FormatFrameIdForPressureLog(snapshot.ActiveRecoveryWindowEpoch)}; active_recovery_window_recovery_frame_id={FormatFrameIdForPressureLog(snapshot.ActiveRecoveryWindowRecoveryFrameId)}; recovery_window_contiguous_follower_apply_count={snapshot.RecoveryWindowContiguousFollowerApplyCount}; continuity_loss_ticks={snapshot.ContinuityLossTicks}; warmup_ticks={snapshot.WarmupTicks}; before_first_visible_apply_ticks={snapshot.BeforeFirstVisibleApplyTicks}; after_visible_recovery_frame_ticks={snapshot.AfterVisibleRecoveryFrameTicks}; after_visible_recovery_frame_suppressed_due_to_success_count={snapshot.AfterVisibleRecoveryFrameSuppressedDueToSuccessCount}; slow_apply_cadence_ticks={snapshot.SlowApplyCadenceTicks}; high_frame_age_ticks={snapshot.HighFrameAgeTicks}; high_frame_age_suppressed_due_to_visible_progress_count={snapshot.HighFrameAgeSuppressedDueToVisibleProgressCount}; high_frame_age_suppressed_due_to_head_advance_count={snapshot.HighFrameAgeSuppressedDueToHeadAdvanceCount}; actionable_high_frame_age_count={snapshot.ActionableHighFrameAgeCount}; post_recovery_age_grace_active={(snapshot.PostRecoveryAgeGraceActive ? 1 : 0)}; post_recovery_age_grace_suppressed_count={snapshot.PostRecoveryAgeGraceSuppressedCount}; post_recovery_high_frame_age_suppressed_ticks={snapshot.PostRecoveryHighFrameAgeSuppressedTicks}; repeated_stale_drops_ticks={snapshot.RepeatedStaleDropsTicks}; viewer_stale_drops={helperRemoteViewerStaleDropCount}; viewer_soft_stale_drops={helperRemoteViewerSoftStaleDropCount}; viewer_actionable_stale_drops={GetActionableStaleDropCount(helperRemoteViewerStaleDropCount, helperRemoteViewerSoftStaleDropCount)}; current_epoch_stale_drops={helperRemoteCurrentPressureEpochStaleDropCount}; current_epoch_soft_stale_drops={helperRemoteCurrentPressureEpochSoftStaleDropCount}; current_epoch_actionable_stale_drops={GetActionableStaleDropCount(helperRemoteCurrentPressureEpochStaleDropCount, helperRemoteCurrentPressureEpochSoftStaleDropCount)}; bridge_health_ticks={snapshot.BridgeHealthTicks}; bridge_health_advisory_count={snapshot.BridgeHealthAdvisoryCount}; bridge_health_actionable_count={snapshot.BridgeHealthActionableCount}; bridge_health_quarantine_suppressed_count={snapshot.BridgeHealthQuarantineSuppressedCount}; bridge_health_became_actionable_without_queue_or_drop_count={snapshot.BridgeHealthActionableWithoutQueueOrDropCount}; baseline_established={(snapshot.BaselineEstablished ? 1 : 0)}; baseline_capture_to_render_ms={snapshot.BaselineCaptureToRenderMs}; age_excess_ms={snapshot.AgeExcessMs}; progress_stall_ms={snapshot.ProgressStallMs}; baseline_reseed_in_progress={(snapshot.BaselineReseedInProgress ? 1 : 0)}; age_pressure_consecutive_count={snapshot.AgePressureConsecutiveCount}; cadence_pressure_consecutive_count={snapshot.CadencePressureConsecutiveCount}; catch_up_suppressed_due_to_progress_count={snapshot.CatchUpSuppressedDueToProgressCount}; baseline_frozen_due_to_stall_count={snapshot.BaselineFrozenDueToStallCount}; baseline_reseed_after_recovery_count={snapshot.BaselineReseedAfterRecoveryCount}; cadence_stall_window_count={snapshot.CadenceStallWindowCount}; cadence_stall_trigger_count={snapshot.CadenceStallTriggerCount}; time_spent_in_helper_warmup_ms={snapshot.TimeSpentInHelperWarmupMs}; visible_applies_during_settle_count={snapshot.VisibleAppliesDuringSettleCount}; post_recovery_settle_window_count={snapshot.PostRecoverySettleWindowCount}; post_recovery_settle_window_success_count={snapshot.PostRecoverySettleWindowSuccessCount}; post_recovery_settle_window_timeout_count={snapshot.PostRecoverySettleWindowTimeoutCount}; visible_applies_before_pressure_reenabled={snapshot.VisibleAppliesBeforePressureReenabled}; dominant_pressure_blocker={snapshot.DominantPressureBlocker}");
    }

    private HelperRemoteScreenSharePressureDiagnosticsSnapshot BuildHelperRemoteScreenSharePressureDiagnosticsSnapshot_NoLock()
    {
        var currentEpoch = helperRemoteCurrentPressureEpoch;
        if (currentEpoch <= 0)
        {
            return new HelperRemoteScreenSharePressureDiagnosticsSnapshot(
                StreamEpoch: 0,
                LastVisibleApplyFrameId: -1,
                VisibleHeadFrameId: -1,
                VisibleRecoveryFloorFrameId: -1,
                AppliedHeadFrameId: -1,
                FramesAppliedSinceLastGap: 0,
                StableVisibleHeadFrameId: -1,
                CurrentEpochGapCount: 0,
                CurrentEpochRecoveryKeyframeApplyCount: 0,
                CurrentEpochResyncCount: 0,
                RecoveryWindowActive: false,
                RecoveryWindowProgressed: false,
                RecoveryWindowSucceeded: false,
                RecoveryWindowProgressedCount: 0,
                RecoveryWindowSuccessCount: 0,
                ActiveRecoveryWindowEpoch: -1,
                ActiveRecoveryWindowRecoveryFrameId: -1,
                RecoveryWindowContiguousFollowerApplyCount: 0,
                ContinuityLossTicks: 0,
                WarmupTicks: 0,
                BeforeFirstVisibleApplyTicks: 0,
                AfterVisibleRecoveryFrameTicks: 0,
                AfterVisibleRecoveryFrameSuppressedDueToSuccessCount: 0,
                SlowApplyCadenceTicks: 0,
                HighFrameAgeTicks: 0,
                HighFrameAgeSuppressedDueToVisibleProgressCount: 0,
                HighFrameAgeSuppressedDueToHeadAdvanceCount: 0,
                ActionableHighFrameAgeCount: 0,
                PostRecoveryAgeGraceActive: false,
                PostRecoveryAgeGraceSuppressedCount: 0,
                PostRecoveryHighFrameAgeSuppressedTicks: 0,
                RepeatedStaleDropsTicks: 0,
                BridgeHealthTicks: 0,
                BridgeHealthAdvisoryCount: 0,
                BridgeHealthActionableCount: 0,
                BridgeHealthQuarantineSuppressedCount: 0,
                BridgeHealthActionableWithoutQueueOrDropCount: 0,
                HelperSessionPhase: HelperRemoteSessionPhase.NoVisibleBaseline,
                HelperRecoveryMechanism: HelperRemoteRecoveryMechanism.None,
                HelperBaselineEstablished: false,
                CurrentEpochProgressProven: false,
                CurrentEpochProgressProofSource: "none",
                CurrentEpochProvenHeadFrameId: -1,
                AppliedHeadAdvancedSinceLastEvaluation: false,
                StableVisibleHeadAdvancedSinceLastEvaluation: false,
                HelperHealthyStateEstablishedBy: "none",
                NonHealthyClearSuppressedDueToProgressCount: 0,
                BaselineEstablished: false,
                BaselineCaptureToRenderMs: -1,
                AgeExcessMs: -1,
                ProgressStallMs: -1,
                BaselineReseedInProgress: false,
                AgePressureConsecutiveCount: 0,
                CadencePressureConsecutiveCount: 0,
                CatchUpSuppressedDueToProgressCount: 0,
                BaselineFrozenDueToStallCount: 0,
                BaselineReseedAfterRecoveryCount: 0,
                CadenceStallWindowCount: 0,
                CadenceStallTriggerCount: 0,
                DerivedPostRecoveryHealthyActive: false,
                DerivedPostRecoveryHealthySource: "none",
                DerivedPostRecoveryProofFrameId: -1,
                SteadyVisibleProgressActive: false,
                SteadyVisibleProgressActivationFrameId: -1,
                LastSentVisibleHeadFrameId: -1,
                LastSentStableVisibleHeadFrameId: -1,
                PressureSendBypassedForVisibleProgressCount: helperRemotePressureSendBypassedForVisibleProgressCount,
                ProofKeepaliveSendCount: helperRemoteProofKeepaliveSendCount,
                ProofKeepaliveTimerDrivenSendCount: helperRemoteProofKeepaliveTimerDrivenSendCount,
                ProofKeepaliveLastHeadFrameId: helperRemoteLastProofKeepaliveHeadFrameId,
                ProofKeepaliveLastSendAgeMs:
                    helperRemoteLastProofKeepaliveSentUtc == default
                        ? -1
                        : Math.Max(0L, (long)(nowProvider() - helperRemoteLastProofKeepaliveSentUtc).TotalMilliseconds),
                SteadyVisibleProgressClearedCount: 0,
                SteadyVisibleProgressClearedReason: string.Empty,
                PostRecoveryHealthyLatchCount: 0,
                PostRecoveryHealthyLatchClearCount: 0,
                PostRecoveryHealthyLatchClearReason: string.Empty,
                TimeSpentInHelperWarmupMs: 0,
                VisibleAppliesDuringSettleCount: 0,
                PostRecoverySettleWindowCount: 0,
                PostRecoverySettleWindowSuccessCount: 0,
                PostRecoverySettleWindowTimeoutCount: 0,
                VisibleAppliesBeforePressureReenabled: -1,
                DominantPressureBlocker: "none");
        }

        var activeSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value ?? sessionId;
        var proofKeepaliveLastSendAgeMs =
            helperRemoteLastProofKeepaliveSentUtc == default
                ? -1
                : Math.Max(0L, (long)(nowProvider() - helperRemoteLastProofKeepaliveSentUtc).TotalMilliseconds);
        var frameLossSnapshot = string.IsNullOrWhiteSpace(activeSessionId)
            ? ScreenShareFrameLossSessionSnapshot.Empty
            : ScreenShareFrameLossAttributionRegistry.GetSnapshot(activeSessionId);
        var epochDiagnostics = frameLossSnapshot.EpochDiagnostics.FirstOrDefault(epoch => epoch.StreamEpoch == currentEpoch);
        var nowUtc = nowProvider();
        var warmupEndUtc = helperRemoteCurrentPressureEpochWarmupEndedUtc != default
            ? helperRemoteCurrentPressureEpochWarmupEndedUtc
            : nowUtc;
        var timeSpentInHelperWarmupMs =
            helperRemoteCurrentPressureEpochWarmupStartedUtc != default && warmupEndUtc >= helperRemoteCurrentPressureEpochWarmupStartedUtc
                ? Math.Max(0L, (long)(warmupEndUtc - helperRemoteCurrentPressureEpochWarmupStartedUtc).TotalMilliseconds)
                : 0L;
        var baselineEstablished = helperRemoteCurrentPressureEpochBaselineEstablished && helperRemoteCurrentPressureEpochBaselineSampleCount > 0;
        var baselineReseedInProgress = helperRemoteCurrentPressureEpochBaselineReseedRemainingVisibleApplies > 0;
        var baselineCaptureToRenderMs = baselineEstablished
            ? Math.Max(0L, (long)Math.Round(helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs, MidpointRounding.AwayFromZero))
            : -1L;
        var nowCaptureToRenderMs =
            helperRemoteLastAppliedFrameAgeMs >= 0 && helperRemoteLastAppliedFrameUtc != default && nowUtc >= helperRemoteLastAppliedFrameUtc
                ? Math.Max(0L, helperRemoteLastAppliedFrameAgeMs + (long)(nowUtc - helperRemoteLastAppliedFrameUtc).TotalMilliseconds)
                : helperRemoteLastAppliedFrameAgeMs;
        var ageExcessMs =
            baselineEstablished && nowCaptureToRenderMs >= 0
                ? Math.Max(0L, nowCaptureToRenderMs - baselineCaptureToRenderMs)
                : -1L;
        var progressStallMs =
            helperRemoteLastAppliedFrameUtc != default && nowUtc >= helperRemoteLastAppliedFrameUtc
                ? Math.Max(0L, (long)(nowUtc - helperRemoteLastAppliedFrameUtc).TotalMilliseconds)
                : -1L;
        var postRecoveryAgeGraceActive =
            helperRemotePostRecoveryAgeGraceEpoch == currentEpoch &&
            helperRemotePostRecoveryAgeGraceUntilUtc != default &&
            nowUtc <= helperRemotePostRecoveryAgeGraceUntilUtc;
        var currentEpochWarmupActive = IsHelperRemoteCurrentEpochWarmupActive_NoLock(nowUtc, currentEpoch);
        var authoritativeProgressState = ResolveHelperRemoteAuthoritativeProgressState_NoLock(
            currentEpoch,
            epochDiagnostics);
        var lastVisibleApplyFrameId = authoritativeProgressState.LastVisibleApplyFrameId;
        var visibleHeadFrameId = authoritativeProgressState.VisibleHeadFrameId;
        var visibleRecoveryFloorFrameId = authoritativeProgressState.VisibleRecoveryFloorFrameId;
        var appliedHeadFrameId = authoritativeProgressState.AppliedHeadFrameId;
        var stableVisibleHeadFrameId = authoritativeProgressState.StableVisibleHeadFrameId;
        var framesAppliedSinceLastGap = authoritativeProgressState.FramesAppliedSinceLastGap;
        currentEpochWarmupActive = currentEpochWarmupActive && !authoritativeProgressState.SteadyVisibleProgressActive;
        var currentEpochProgressProven = authoritativeProgressState.CurrentEpochProgressProven;
        var currentEpochProgressProofSource = authoritativeProgressState.CurrentEpochProgressProofSource;
        var currentEpochProvenHeadFrameId = authoritativeProgressState.CurrentEpochProvenHeadFrameId;
        var helperSessionPhase = authoritativeProgressState.SessionSnapshotPresent
            ? authoritativeProgressState.SessionSnapshot.Phase
            : (authoritativeProgressState.SteadyVisibleProgressActive
                ? HelperRemoteSessionPhase.VisibleStable
                : HelperRemoteSessionPhase.NoVisibleBaseline);
        var helperRecoveryMechanism = authoritativeProgressState.SessionSnapshotPresent
            ? authoritativeProgressState.SessionSnapshot.RecoveryMechanism
            : HelperRemoteRecoveryMechanism.None;
        var helperBaselineEstablished = authoritativeProgressState.SessionSnapshotPresent
            ? authoritativeProgressState.SessionSnapshot.BaselineEstablished
            : visibleHeadFrameId >= 0 || appliedHeadFrameId >= 0 || stableVisibleHeadFrameId >= 0;
        var derivedPostRecoveryHealthy = (
            Active: currentEpochProgressProven,
            Source: currentEpochProgressProofSource,
            ProofFrameId: currentEpochProvenHeadFrameId);

            return new HelperRemoteScreenSharePressureDiagnosticsSnapshot(
                StreamEpoch: currentEpoch,
                LastVisibleApplyFrameId: lastVisibleApplyFrameId,
                VisibleHeadFrameId: visibleHeadFrameId,
                VisibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
                AppliedHeadFrameId: appliedHeadFrameId,
                FramesAppliedSinceLastGap: framesAppliedSinceLastGap,
            StableVisibleHeadFrameId: stableVisibleHeadFrameId,
            CurrentEpochGapCount: epochDiagnostics?.GapCount ?? 0,
            CurrentEpochRecoveryKeyframeApplyCount: epochDiagnostics?.RecoveryKeyframeApplyCount ?? 0,
            CurrentEpochResyncCount: epochDiagnostics?.ResyncCount ?? 0,
            RecoveryWindowActive: false,
            RecoveryWindowProgressed: false,
            RecoveryWindowSucceeded: false,
            RecoveryWindowProgressedCount: 0,
            RecoveryWindowSuccessCount: 0,
            ActiveRecoveryWindowEpoch: -1,
            ActiveRecoveryWindowRecoveryFrameId: -1,
            RecoveryWindowContiguousFollowerApplyCount: 0,
            ContinuityLossTicks: helperRemoteCurrentPressureEpochContinuityLossTicks,
            WarmupTicks: helperRemoteCurrentPressureEpochWarmupTicks,
            BeforeFirstVisibleApplyTicks: helperRemoteCurrentPressureEpochBeforeFirstVisibleApplyTicks,
            AfterVisibleRecoveryFrameTicks: helperRemoteCurrentPressureEpochAfterVisibleRecoveryFrameTicks,
            AfterVisibleRecoveryFrameSuppressedDueToSuccessCount: 0,
            SlowApplyCadenceTicks: helperRemoteCurrentPressureEpochSlowApplyCadenceTicks,
            HighFrameAgeTicks: helperRemoteCurrentPressureEpochHighFrameAgeTicks,
            HighFrameAgeSuppressedDueToVisibleProgressCount: helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToVisibleProgressCount,
            HighFrameAgeSuppressedDueToHeadAdvanceCount: helperRemoteCurrentPressureEpochHighFrameAgeSuppressedDueToHeadAdvanceCount,
            ActionableHighFrameAgeCount: helperRemoteCurrentPressureEpochActionableHighFrameAgeCount,
            PostRecoveryAgeGraceActive: postRecoveryAgeGraceActive,
            PostRecoveryAgeGraceSuppressedCount: helperRemoteCurrentPressureEpochPostRecoveryAgeGraceSuppressedCount,
            PostRecoveryHighFrameAgeSuppressedTicks: helperRemoteCurrentPressureEpochPostRecoveryHighFrameAgeSuppressedTicks,
            RepeatedStaleDropsTicks: helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks,
            BridgeHealthTicks: helperRemoteCurrentPressureEpochBridgeHealthTicks,
            BridgeHealthAdvisoryCount: helperRemoteCurrentPressureEpochBridgeHealthAdvisoryCount,
            BridgeHealthActionableCount: helperRemoteCurrentPressureEpochBridgeHealthActionableCount,
            BridgeHealthQuarantineSuppressedCount: helperRemoteCurrentPressureEpochBridgeHealthQuarantineSuppressedCount,
            BridgeHealthActionableWithoutQueueOrDropCount: helperRemoteCurrentPressureEpochBridgeHealthActionableWithoutQueueOrDropCount,
            HelperSessionPhase: helperSessionPhase,
            HelperRecoveryMechanism: helperRecoveryMechanism,
            HelperBaselineEstablished: helperBaselineEstablished,
            CurrentEpochProgressProven: currentEpochProgressProven,
            CurrentEpochProgressProofSource: currentEpochProgressProofSource,
            CurrentEpochProvenHeadFrameId: currentEpochProvenHeadFrameId,
            AppliedHeadAdvancedSinceLastEvaluation: helperRemoteLastAppliedHeadAdvancedSincePressureEvaluation,
            StableVisibleHeadAdvancedSinceLastEvaluation: helperRemoteLastStableVisibleHeadAdvancedSincePressureEvaluation,
            HelperHealthyStateEstablishedBy: helperRemoteLastHealthyStateEstablishedBy,
            NonHealthyClearSuppressedDueToProgressCount: helperRemoteCurrentPressureEpochNonHealthyClearSuppressedDueToProgressCount,
            BaselineEstablished: baselineEstablished,
            BaselineCaptureToRenderMs: baselineCaptureToRenderMs,
            AgeExcessMs: ageExcessMs,
            ProgressStallMs: progressStallMs,
            BaselineReseedInProgress: baselineReseedInProgress,
            AgePressureConsecutiveCount: helperRemoteCurrentPressureEpochAgePressureConsecutiveCount,
            CadencePressureConsecutiveCount: helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount,
            CatchUpSuppressedDueToProgressCount: helperRemoteCurrentPressureEpochCatchUpSuppressedDueToProgressCount,
            BaselineFrozenDueToStallCount: helperRemoteCurrentPressureEpochBaselineFrozenDueToStallCount,
            BaselineReseedAfterRecoveryCount: helperRemoteCurrentPressureEpochBaselineReseedAfterRecoveryCount,
            CadenceStallWindowCount: helperRemoteCurrentPressureEpochCadenceStallWindowCount,
            CadenceStallTriggerCount: helperRemoteCurrentPressureEpochCadenceStallTriggerCount,
            DerivedPostRecoveryHealthyActive: derivedPostRecoveryHealthy.Active,
            DerivedPostRecoveryHealthySource: derivedPostRecoveryHealthy.Source,
            DerivedPostRecoveryProofFrameId: derivedPostRecoveryHealthy.ProofFrameId,
            SteadyVisibleProgressActive: authoritativeProgressState.SteadyVisibleProgressActive,
            SteadyVisibleProgressActivationFrameId: authoritativeProgressState.SteadyVisibleProgressActivationFrameId,
            LastSentVisibleHeadFrameId:
                helperRemoteLastSentSteadyProgressEpoch == currentEpoch
                    ? helperRemoteLastSentVisibleHeadFrameId
                    : -1L,
            LastSentStableVisibleHeadFrameId:
                helperRemoteLastSentSteadyProgressEpoch == currentEpoch
                    ? helperRemoteLastSentStableVisibleHeadFrameId
                    : -1L,
            PressureSendBypassedForVisibleProgressCount: helperRemotePressureSendBypassedForVisibleProgressCount,
            ProofKeepaliveSendCount: helperRemoteProofKeepaliveSendCount,
            ProofKeepaliveTimerDrivenSendCount: helperRemoteProofKeepaliveTimerDrivenSendCount,
            ProofKeepaliveLastHeadFrameId: helperRemoteLastProofKeepaliveHeadFrameId,
            ProofKeepaliveLastSendAgeMs: proofKeepaliveLastSendAgeMs,
            SteadyVisibleProgressClearedCount: helperRemoteSteadyVisibleProgressClearedCount,
            SteadyVisibleProgressClearedReason: helperRemoteSteadyVisibleProgressClearedReason,
            PostRecoveryHealthyLatchCount: helperRemotePostRecoveryHealthyLatchCount,
            PostRecoveryHealthyLatchClearCount: helperRemotePostRecoveryHealthyLatchClearCount,
            PostRecoveryHealthyLatchClearReason: helperRemotePostRecoveryHealthyLatchClearReason,
            TimeSpentInHelperWarmupMs: timeSpentInHelperWarmupMs,
            VisibleAppliesDuringSettleCount: 0,
            PostRecoverySettleWindowCount: 0,
            PostRecoverySettleWindowSuccessCount: 0,
            PostRecoverySettleWindowTimeoutCount: 0,
            VisibleAppliesBeforePressureReenabled: -1,
            DominantPressureBlocker: DetermineDominantPressureBlocker(
                helperRemoteCurrentPressureEpochContinuityLossTicks,
                helperRemoteCurrentPressureEpochWarmupTicks,
                helperRemoteCurrentPressureEpochSlowApplyCadenceTicks,
                helperRemoteCurrentPressureEpochHighFrameAgeTicks,
                helperRemoteCurrentPressureEpochRepeatedStaleDropsTicks,
                helperRemoteCurrentPressureEpochBridgeHealthTicks,
                authoritativeProgressState.CurrentEpochProgressProven));
    }

    private static string DetermineDominantPressureBlocker(
        long continuityLossTicks,
        long warmupTicks,
        long slowApplyCadenceTicks,
        long highFrameAgeTicks,
        long repeatedStaleDropsTicks,
        long bridgeHealthTicks,
        bool derivedPostRecoveryHealthyActive)
    {
        var candidates = new[]
        {
            (Name: "continuity_loss", Count: derivedPostRecoveryHealthyActive ? 0L : continuityLossTicks),
            (Name: "warmup", Count: derivedPostRecoveryHealthyActive ? 0L : warmupTicks),
            (Name: "slow_apply_cadence", Count: slowApplyCadenceTicks),
            (Name: "high_frame_age", Count: highFrameAgeTicks),
            (Name: "repeated_stale_drops", Count: repeatedStaleDropsTicks),
            (Name: "bridge_health", Count: bridgeHealthTicks),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.Count)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return best.Count > 0 ? best.Name : "none";
    }

    private void OnTransportScreenSharePressureStateReceived(object? sender, ScreenSharePressureStateReceivedEventArgs e)
    {
        screenShareControlHost.HandleTransportScreenSharePressureStateReceived(sender, e);
    }

    private void OnTransportScreenShareRecoveryReceiptReceived(object? sender, ScreenShareRecoveryReceiptReceivedEventArgs e)
    {
        screenShareControlHost.HandleTransportScreenShareRecoveryReceiptReceived(sender, e);
    }

    private void OnTransportScreenShareVideoKeyframeRequestReceived(object? sender, ScreenShareVideoKeyframeRequestReceivedEventArgs e)
    {
        screenShareControlHost.HandleTransportScreenShareVideoKeyframeRequestReceived(sender, e);
    }

    private void EnsureHelperRemoteScreenSharePressureTimerStarted()
    {
        screenShareControlHost.EnsureHelperRemoteScreenSharePressureTimerStarted();
    }

    private void StopHelperRemoteScreenSharePressureTimer()
    {
        screenShareControlHost.StopHelperRemoteScreenSharePressureTimer();
    }

    private void OnHelperRemoteScreenSharePressureTimerTick()
    {
        screenShareControlHost.OnHelperRemoteScreenSharePressureTimerTick();
    }

    private void MaybeSendScreenSharePressureState()
    {
        screenShareControlHost.MaybeSendScreenSharePressureState();
    }

    private void MaybeSendScreenSharePressureState(bool timerDriven)
    {
        screenShareControlHost.MaybeSendScreenSharePressureState(timerDriven);
    }

    private static string FormatScreenSharePressureSampleSource(ScreenSharePressureSampleSource source)
    {
        return source switch
        {
            ScreenSharePressureSampleSource.ApplyCadence => "apply_cadence",
            ScreenSharePressureSampleSource.StaleDropOnly => "stale_drop_only",
            ScreenSharePressureSampleSource.BridgeHealth => "bridge_health",
            _ => "applied_frame_age",
        };
    }

    private static string FormatScreenSharePressureHealthKind(bool hasBridgeHealth, bool hasActionableBridgeHealth)
    {
        if (hasActionableBridgeHealth)
        {
            return "actionable";
        }

        return hasBridgeHealth ? "advisory" : "none";
    }

    private static string FormatFrameIdForPressureLog(long frameId)
    {
        return frameId >= 0 ? frameId.ToString(CultureInfo.InvariantCulture) : "(none)";
    }

    private static string FormatPressureTextValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value.Trim();
    }

    private void OnTransportScreenShareStopped(object? sender, EventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        screenShareControlHost.NotifyRemoteScreenShareStopped("transport_screen_share_stopped", sender, localStop: false);
    }

    private void RequestHelperRemoteRecoveryKeyframe(
        long streamEpoch,
        string reason,
        long currentEpochNeedMoreInputCount,
        long expectedNextFrameId = -1,
        long receivedFrameId = -1,
        long lastCleanFrameId = -1)
    {
        if (disposed ||
            role != SessionRuntimeRole.Helper ||
            state != SessionRuntimeState.Connected ||
            transport is not IScreenShareSignalingTransport screenShareTransport)
        {
            return;
        }

        var sessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var nowUtc = nowProvider();
        lock (helperRemoteScreenSharePressureGate)
        {
            if (helperRemoteLastRecoveryKeyframeRequestEpoch == streamEpoch &&
                helperRemoteLastRecoveryKeyframeRequestUtc != default &&
                nowUtc - helperRemoteLastRecoveryKeyframeRequestUtc < TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            helperRemoteLastRecoveryKeyframeRequestEpoch = streamEpoch;
            helperRemoteLastRecoveryKeyframeRequestUtc = nowUtc;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_keyframe_requested; role=helper_remote; stream_epoch={streamEpoch}; reason={reason}; recovery_active=1; current_epoch_need_more_input_count={Math.Max(0, currentEpochNeedMoreInputCount)}; expected_next_frame_id={(expectedNextFrameId >= 0 ? expectedNextFrameId.ToString() : "(none)")}; received_frame_id={(receivedFrameId >= 0 ? receivedFrameId.ToString() : "(none)")}; last_clean_frame_id={(lastCleanFrameId >= 0 ? lastCleanFrameId.ToString() : "(none)")}");

        var message = new ScreenShareVideoKeyframeRequestV1
        {
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            Reason = reason,
        };

        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await screenShareTransport.SendScreenShareVideoKeyframeRequestAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogRemoteControlInfo("screenshare_recovery_keyframe_request_failed", ex.GetType().Name, null, null);
                }
            },
            countAsTransportTask: false);
    }

    private void NotifyLocalScreenShareStoppedForTeardown(string reason, object? sender)
    {
        screenShareControlHost.NotifyRemoteScreenShareStopped(reason, sender, localStop: true);
    }

    private static void ForceCloseWindowsGraphicsCaptureLeases(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var safeReason = SensitiveDataRedactor.Redact(
            string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim());
        try
        {
            WindowsGraphicsCaptureRawSource.ForceCloseAllScreenShareLeases(safeReason);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_wgc_force_close_all_failed; reason={safeReason}; ex={ex.GetType().Name}");
        }
    }

    private void NotifyRemoteScreenShareStoppedCore(string reason, object? sender, bool localStop)
    {
        var nowUtc = nowProvider();
        remoteScreenShareFramesSuppressedUntilUtc = nowUtc.Add(RemoteScreenShareStopFrameSuppressionWindow);
        Interlocked.Exchange(ref remoteScreenShareSuppressFramesCapturedBeforeOrAtUtcMs, nowUtc.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref lastScreenShareStopSuppressedLogTick, 0);
        screenShareControlHost.ResetRemoteScreenShareActivity();
        StopHelperRemoteScreenSharePressureTimer();
        LogAndResetHelperRemoteScreenShareSummary(reason);
        ResetHelperRemoteScreenSharePressureTracking();
        if (localStop)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_local_runtime; reason={reason}; suppressed_until_utc={remoteScreenShareFramesSuppressedUntilUtc:O}; control_state={remoteControlSessionState.ControlState}; role={role}; transport={GetTransportNameForLog(sender)}");
        }
        else
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_received_runtime; suppressed_until_utc={remoteScreenShareFramesSuppressedUntilUtc:O}; control_state={remoteControlSessionState.ControlState}; role={role}; transport={GetTransportNameForLog(sender)}");
        }
        ScheduleRemoteControlScreenShareStopGrace();
        SyncFileTransferFlowControlMode();
        transportScreenShareCoordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, localStop ? reason : "screenshare_stopped", 0, 0);
        try
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void OnRemoteControlRequestReceived(object? sender, RemoteControlRequestReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlRequestReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlResponseReceived(object? sender, RemoteControlResponseReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlResponseReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlStartReceived(object? sender, RemoteControlStartReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlStartReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlStopReceived(object? sender, RemoteControlStopReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        // Latch stop preemption immediately so in-flight input handlers can short-circuit.
        MarkRemoteControlStopPriority(
            "remote_stop_signal:" + (e.Message.Reason ?? "remote_stop"),
            e.Message.RequestId,
            e.PeerId);

        RunCountedBackgroundTask(
            () => HandleRemoteControlStopReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlInputReceived(object? sender, RemoteControlInputReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlInputReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlAckReceived(object? sender, RemoteControlAckReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlAckReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlStateSnapshotReceived(object? sender, RemoteControlStateSnapshotReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlStateSnapshotReceivedAsync(e),
            countAsTransportTask: false);
    }

    private void OnRemoteControlDisplayInfoReceived(object? sender, RemoteControlDisplayInfoReceivedEventArgs e)
    {
        if (!IsFromCurrentTransport(sender))
        {
            return;
        }

        RunCountedBackgroundTask(
            () => HandleRemoteControlDisplayInfoReceivedAsync(e),
            countAsTransportTask: false);
    }

    private async Task HandleRemoteControlAckReceivedAsync(RemoteControlAckReceivedEventArgs e)
    {
        var ackAdvanced = false;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed ||
                resetInProgress ||
                state != SessionRuntimeState.Connected ||
                role != SessionRuntimeRole.Helper ||
                remoteControlSessionState.ControlState != ControlState.Active)
            {
                return;
            }

            if (!RequireRemoteControlAuxiliaryCapability(
                    "remote_control_ack_receive",
                    "ack_ignored",
                    e.Ack.RequestId,
                    e.PeerId,
                    rateLimitKey: "ack_ignored:capability_not_granted"))
            {
                return;
            }

            var currentRequestId = remoteControlSessionState.CurrentControlRequestId;
            if (string.IsNullOrWhiteSpace(currentRequestId) ||
                !string.Equals(currentRequestId, e.Ack.RequestId, StringComparison.Ordinal))
            {
                return;
            }

            var previousAckSeq = Volatile.Read(ref helperRemoteControlLastAckSeq);
            if (e.Ack.AckSeq > previousAckSeq)
            {
                Volatile.Write(ref helperRemoteControlLastAckSeq, e.Ack.AckSeq);
                Volatile.Write(ref helperRemoteControlLastAckAdvanceTick, Stopwatch.GetTimestamp());
                ackAdvanced = true;
                PublishRemoteControlDebugDiagnostics();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (ackAdvanced && ShouldEmitRemoteControlRateLimitedLog("ack_received"))
        {
            LogRemoteControlInfo(
                "ack_received",
                $"ack_seq={e.Ack.AckSeq.ToString(CultureInfo.InvariantCulture)}",
                e.Ack.RequestId,
                e.PeerId);
        }
    }

    private async Task HandleRemoteControlStateSnapshotReceivedAsync(RemoteControlStateSnapshotReceivedEventArgs e)
    {
        if (!FeatureFlags.RemoteControlStateSnapshotEnabled)
        {
            return;
        }

        ControlStateSnapshotV1? acceptedSnapshot = null;
        string? peerId = null;
        long stopEpochSnapshot = 0;
        var normalizedPeerId = string.IsNullOrWhiteSpace(e.Source) ? null : e.Source.Trim();

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress || state != SessionRuntimeState.Connected)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:runtime_state"))
                {
                    return;
                }

                LogRemoteControlInfo("snapshot_ignored", "guard=runtime_state", e.Snapshot.RequestId, normalizedPeerId);
                return;
            }

            if (Volatile.Read(ref remoteControlStopInputSuppressionLatched) != 0)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:stop_preempt_latched"))
                {
                    return;
                }

                LogRemoteControlInfo("snapshot_ignored", "guard=stop_preempt_latched", e.Snapshot.RequestId, normalizedPeerId);
                return;
            }

            if (role != SessionRuntimeRole.Helpee)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:wrong_role"))
                {
                    return;
                }

                LogRemoteControlInfo("snapshot_ignored", $"guard=role; expected=Helpee; actual={role}", e.Snapshot.RequestId, normalizedPeerId);
                return;
            }

            if (!RequireCapability(SessionCapability.RemoteControl, "remote_control_snapshot_receive"))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                LogRemoteControlViolation("snapshot_ignored", "capability_not_granted", e.Snapshot.RequestId, normalizedPeerId);
                return;
            }

            if (remoteControlSessionState.ControlState != ControlState.Active)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:inactive_control_state"))
                {
                    return;
                }

                LogRemoteControlInfo(
                    "snapshot_ignored",
                    $"guard=control_state_active; actual={remoteControlSessionState.ControlState}",
                    e.Snapshot.RequestId,
                    normalizedPeerId);
                return;
            }

            if (!string.Equals(remoteControlSessionState.CurrentControlRequestId, e.Snapshot.RequestId, StringComparison.Ordinal))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:request_mismatch"))
                {
                    return;
                }

                LogRemoteControlViolation(
                    "snapshot_ignored",
                    $"guard=request_id_match; expected={remoteControlSessionState.CurrentControlRequestId ?? "(none)"}; incoming={e.Snapshot.RequestId ?? "(none)"}",
                    e.Snapshot.RequestId,
                    normalizedPeerId);
                return;
            }

            if (string.IsNullOrWhiteSpace(remoteControlSessionState.ControllerPeerId) ||
                !string.Equals(remoteControlSessionState.ControllerPeerId, normalizedPeerId, StringComparison.Ordinal))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:controller_mismatch"))
                {
                    return;
                }

                LogRemoteControlViolation(
                    "snapshot_ignored",
                    $"guard=controller_peer_match; expected={remoteControlSessionState.ControllerPeerId ?? "(none)"}; incoming={normalizedPeerId ?? "(none)"}",
                    e.Snapshot.RequestId,
                    normalizedPeerId);
                return;
            }

            if (e.Snapshot.Seq <= 0)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                LogRemoteControlViolation("snapshot_ignored", "missing_seq", e.Snapshot.RequestId, normalizedPeerId);
                return;
            }

            var previousReceivedSeq = Interlocked.Read(ref remoteControlSnapshotLastReceivedSeq);
            if (previousReceivedSeq > 0 &&
                e.Snapshot.Seq <= previousReceivedSeq)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                var duplicateSnapshot = e.Snapshot.Seq == previousReceivedSeq;
                var rateLimitKey = duplicateSnapshot
                    ? "snapshot_deduped:duplicate_seq"
                    : "snapshot_deduped:out_of_order_seq";
                if (ShouldEmitRemoteControlRateLimitedLog(rateLimitKey))
                {
                    LogRemoteControlRateLimitedInfo(
                        "snapshot_stale_dropped",
                        duplicateSnapshot
                            ? $"duplicate_or_replay_seq={e.Snapshot.Seq.ToString(CultureInfo.InvariantCulture)}; last_received={previousReceivedSeq.ToString(CultureInfo.InvariantCulture)}"
                            : $"out_of_order_seq={e.Snapshot.Seq.ToString(CultureInfo.InvariantCulture)}; last_received={previousReceivedSeq.ToString(CultureInfo.InvariantCulture)}",
                        e.Snapshot.RequestId,
                        normalizedPeerId);
                }

                return;
            }

            stopEpochSnapshot = SnapshotRemoteControlStopPriorityEpoch();
            acceptedSnapshot = e.Snapshot;
            peerId = normalizedPeerId;
            Interlocked.Increment(ref remoteControlSnapshotReceivedCount);
            Interlocked.Exchange(ref remoteControlSnapshotLastReceivedSeq, e.Snapshot.Seq);
            Interlocked.Exchange(ref remoteControlSnapshotLastReceivedModifiersMask, e.Snapshot.ModifiersMask);
            Interlocked.Exchange(ref remoteControlSnapshotLastReceivedMouseButtonsMask, e.Snapshot.MouseButtonsMask);
            var nowTicks = Stopwatch.GetTimestamp();
            var previousSnapshotTick = Interlocked.Read(ref remoteControlSnapshotLastReceivedTick);
            if (previousSnapshotTick <= 0 ||
                Stopwatch.GetElapsedTime(previousSnapshotTick, nowTicks) > RemoteControlSnapshotContinuousGapTolerance)
            {
                Interlocked.Exchange(ref remoteControlSnapshotContinuousStartTick, nowTicks);
            }
            else if (Interlocked.Read(ref remoteControlSnapshotContinuousStartTick) <= 0)
            {
                Interlocked.Exchange(ref remoteControlSnapshotContinuousStartTick, previousSnapshotTick);
            }

            Interlocked.Exchange(ref remoteControlSnapshotLastReceivedTick, nowTicks);
            PublishRemoteControlDebugDiagnostics();
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (acceptedSnapshot is null)
        {
            return;
        }

        await TryInjectRemoteControlStateSnapshotAsync(acceptedSnapshot, peerId, stopEpochSnapshot).ConfigureAwait(false);
    }

    private async Task HandleRemoteControlDisplayInfoReceivedAsync(RemoteControlDisplayInfoReceivedEventArgs e)
    {
        string? displayChangeStopRequestId = null;
        string? displayChangeStopPeerId = null;
        string? displayChangeStopReason = null;
        string? displayProbeResponseRequestId = null;
        string? displayProbeResponsePeerId = null;
        IRemoteControlSignalingTransport? controlTransport = null;
        ControlDisplayInfoMessageV1? displayProbeResponse = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress || state != SessionRuntimeState.Connected)
            {
                LogRemoteControlInfo("display_info_ignored", "invalid_runtime_state", controllerPeerId: e.PeerId);
                return;
            }

            if (role == SessionRuntimeRole.Helpee)
            {
                if (!RequireRemoteControlAuxiliaryCapability(
                        "remote_control_display_info_probe_receive",
                        "display_info_ignored",
                        remoteControlSessionState.CurrentControlRequestId,
                        e.PeerId,
                        rateLimitKey: "display_info_probe_ignored:capability_not_granted",
                        rateLimitWindow: RemoteControlStallRecoveryMinInterval))
                {
                    return;
                }

                if (!IsUsableRemoteControlDisplayInfo(e.Message))
                {
                    LogRemoteControlInfo("display_info_ignored", "invalid_probe", controllerPeerId: e.PeerId);
                    return;
                }

                var controllerPeerId = remoteControlSessionState.ControllerPeerId;
                if (remoteControlSessionState.ControlState == ControlState.Active &&
                    !string.IsNullOrWhiteSpace(controllerPeerId) &&
                    string.Equals(controllerPeerId, e.PeerId, StringComparison.Ordinal) &&
                    IsUsableRemoteControlDisplayInfo(latestRemoteControlDisplayInfo) &&
                    transport is IRemoteControlSignalingTransport helpeeControlTransport)
                {
                    displayProbeResponse = latestRemoteControlDisplayInfo! with
                    {
                        TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    displayProbeResponseRequestId = remoteControlSessionState.CurrentControlRequestId;
                    displayProbeResponsePeerId = controllerPeerId;
                    controlTransport = helpeeControlTransport;
                    if (ShouldEmitRemoteControlRateLimitedLog("display_info_probe_received", RemoteControlStallRecoveryMinInterval))
                    {
                        LogRemoteControlInfo(
                            "display_info_probe_received",
                            $"display_id={e.Message.DisplayId}; revision={e.Message.Revision.ToString(CultureInfo.InvariantCulture)}",
                            displayProbeResponseRequestId,
                            controllerPeerId);
                    }
                }
                else
                {
                    LogRemoteControlInfo("display_info_ignored", "wrong_role", controllerPeerId: e.PeerId);
                }

                return;
            }

            if (role != SessionRuntimeRole.Helper)
            {
                LogRemoteControlInfo("display_info_ignored", "wrong_role", controllerPeerId: e.PeerId);
                return;
            }

            if (!RequireRemoteControlAuxiliaryCapability(
                    "remote_control_display_info_receive",
                    "display_info_ignored",
                    remoteControlSessionState.CurrentControlRequestId,
                    e.PeerId,
                    rateLimitKey: "display_info_ignored:capability_not_granted",
                    rateLimitWindow: RemoteControlStallRecoveryMinInterval))
            {
                ClearRemoteControlDisplayInfo("capability_not_granted", notifyStateChanged: true);
                return;
            }

            if (!IsUsableRemoteControlDisplayInfo(e.Message))
            {
                LogRemoteControlInfo("display_info_ignored", "invalid_or_stale", controllerPeerId: e.PeerId);
                ClearRemoteControlDisplayInfo("invalid_or_stale", notifyStateChanged: true);
                return;
            }

            var previous = latestRemoteControlDisplayInfo;
            var mappingBecameAvailable = previous is null;
            if (previous is not null &&
                string.Equals(previous.DisplayId, e.Message.DisplayId, StringComparison.Ordinal) &&
                e.Message.Revision <= previous.Revision)
            {
                LogRemoteControlInfo(
                    "display_info_ignored",
                    $"stale_revision:{e.Message.Revision}<={previous.Revision}",
                    controllerPeerId: e.PeerId);
                return;
            }

            var didMappingChange = previous is not null && HasDisplayInfoMappingChanged(previous, e.Message);
            latestRemoteControlDisplayInfo = e.Message;
            LogRemoteControlInfo(
                "display_info_received",
                FormatControlDisplayInfoLogSummary(e.Message),
                controllerPeerId: e.PeerId);

            if (didMappingChange || (previous is not null && remoteControlSessionState.ControlState != ControlState.Active))
            {
                var displayTransition = ApplyRemoteControlCoordinatorDisplayInfoChanged(
                    e.Message,
                    didMappingChange ? "display_info_changed" : "display_info_updated");
                (displayChangeStopRequestId, displayChangeStopPeerId, displayChangeStopReason) =
                    ApplyRemoteControlDisplayInfoCoordinatorSideEffects(
                        displayTransition,
                        coordinatorReason: didMappingChange ? "display_info_changed" : "display_info_updated",
                        showScreenChangedHint: didMappingChange);
            }
            else
            {
                remoteControlCoordinatorDisplayInfoState = CreateRemoteControlDisplayInfoState(e.Message);
                if (mappingBecameAvailable && remoteControlSessionState.ControlState == ControlState.Active)
                {
                    NotifyRemoteControlStateChanged();
                }
            }

            if (didMappingChange)
            {
                LogRemoteControlInfo(
                    "display_info_changed",
                    $"display_id={e.Message.DisplayId}; revision={e.Message.Revision.ToString(CultureInfo.InvariantCulture)}",
                    controllerPeerId: e.PeerId);
            }

            if (!string.IsNullOrWhiteSpace(displayChangeStopRequestId))
            {
                controlTransport = transport as IRemoteControlSignalingTransport;
            }

            // TODO(v0.5.0-P7): extend display descriptors for helper target selection and
            // stricter multi-display mapping validation.
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (!string.IsNullOrWhiteSpace(displayChangeStopRequestId) &&
            !string.IsNullOrWhiteSpace(displayChangeStopReason))
        {
            await SendDirectRemoteControlStopAsync(
                    controlTransport,
                    displayChangeStopRequestId!,
                    displayChangeStopPeerId,
                    displayChangeStopReason!,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (displayProbeResponse is not null)
        {
            await SendRemoteControlDisplayInfoProbeResponseAsync(
                    controlTransport,
                    displayProbeResponse,
                    displayProbeResponseRequestId,
                    displayProbeResponsePeerId)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleRemoteControlRequestReceivedAsync(RemoteControlRequestReceivedEventArgs e)
    {
        var stopEpochSnapshot = SnapshotRemoteControlStopPriorityEpoch();
        RemoteControlSideEffect? autoDenySideEffect = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
            {
                LogRemoteControlInfo("request_ignored", "stop_priority", e.Message.RequestId, e.PeerId);
                return;
            }

            if (disposed || resetInProgress || state != SessionRuntimeState.Connected || role != SessionRuntimeRole.Helpee)
            {
                LogRemoteControlViolation("request_received", "invalid_runtime_state", e.Message.RequestId, e.PeerId);
                autoDenySideEffect = new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlResponse,
                    "not_connected",
                    RequestId: e.Message.RequestId,
                    PeerId: e.PeerId,
                    Decision: RemoteControlReducerResponseDecision.Deny);

                return;
            }

            if (!RequireCapability(SessionCapability.RemoteControl))
            {
                LogRemoteControlViolation("request_received", "capability_not_granted", e.Message.RequestId, e.PeerId);
                autoDenySideEffect = new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlResponse,
                    "capability_not_granted",
                    RequestId: e.Message.RequestId,
                    PeerId: e.PeerId,
                    Decision: RemoteControlReducerResponseDecision.Deny);

                return;
            }

            if (!SessionSupportsRemoteControl)
            {
                LogRemoteControlViolation("request_received", "capability_not_supported", e.Message.RequestId, e.PeerId);
                autoDenySideEffect = new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlResponse,
                    "unsupported",
                    RequestId: e.Message.RequestId,
                    PeerId: e.PeerId,
                    Decision: RemoteControlReducerResponseDecision.Deny);

                return;
            }

            if (remoteControlSessionState.ControlState is ControlState.Active or ControlState.Requesting)
            {
                LogRemoteControlViolation("request_received", "busy", e.Message.RequestId, e.PeerId);
            }

            ClearPendingRemoteControlConsentToken();
            ApplyRemoteControlReducerTransition(
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.TransportControlRequestReceived,
                    "request_received",
                    RequestId: e.Message.RequestId,
                    PeerId: e.PeerId,
                    TimeoutKind: RemoteControlReducerTimeoutKind.ConsentDecision,
                    TimeoutMs: (long)RemoteControlConsentDecisionTimeout.TotalMilliseconds));
            LogRemoteControlInfo(
                "request_received",
                string.IsNullOrWhiteSpace(e.Message.Reason) ? "incoming_request" : e.Message.Reason!,
                e.Message.RequestId,
                e.PeerId);
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (autoDenySideEffect.HasValue)
        {
            ExecuteRemoteControlReducerSideEffect(autoDenySideEffect.Value, autoDenySideEffect.Value.Reason ?? "auto_deny");
        }
    }

    private async Task HandleRemoteControlResponseReceivedAsync(RemoteControlResponseReceivedEventArgs e)
    {
        var stopEpochSnapshot = SnapshotRemoteControlStopPriorityEpoch();

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
            {
                LogRemoteControlInfo("response_ignored", "stop_priority", e.Message.RequestId, e.PeerId);
                return;
            }

            if (disposed || resetInProgress || role != SessionRuntimeRole.Helper)
            {
                LogRemoteControlViolation("response_received", "invalid_runtime_state", e.Message.RequestId, e.PeerId);
                return;
            }

            if (remoteControlSessionState.ControlState != ControlState.Requesting ||
                !string.Equals(remoteControlSessionState.CurrentControlRequestId, e.Message.RequestId, StringComparison.Ordinal))
            {
                LogRemoteControlInfo("response_ignored", "request_mismatch", e.Message.RequestId, e.PeerId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(remoteControlSessionState.ConsentToken))
            {
                LogRemoteControlInfo("response_ignored", "late_or_duplicate", e.Message.RequestId, e.PeerId);
                return;
            }

            var decision = e.Message.Decision ?? string.Empty;
            LogRemoteControlInfo(
                "response_received",
                string.IsNullOrWhiteSpace(decision) ? "(none)" : decision,
                e.Message.RequestId,
                e.PeerId);
            if (string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(decision, "denied", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRemoteControlReducerTransition(
                    new RemoteControlReducerEvent(
                        RemoteControlReducerEventKind.TransportControlResponseReceived,
                        "response_denied",
                        RequestId: e.Message.RequestId,
                        PeerId: e.PeerId,
                        Decision: RemoteControlReducerResponseDecision.Deny,
                        TimeoutKind: RemoteControlReducerTimeoutKind.DeniedCooldown,
                        TimeoutMs: (long)RemoteControlDeniedCooldown.TotalMilliseconds));
            }
            else
            {
                if (!string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase))
                {
                    LogRemoteControlViolation("response_received", "unknown_decision", e.Message.RequestId, e.PeerId);
                    MarkRemoteControlStopPriority("response_protocol_error", e.Message.RequestId, e.PeerId);
                    ApplyRemoteControlReducerTransition(
                        new RemoteControlReducerEvent(
                            RemoteControlReducerEventKind.TransportControlStopReceived,
                            "response_protocol_error",
                            RequestId: e.Message.RequestId,
                            PeerId: e.PeerId));
                        return;
                }

                if (!RequireRemoteControlAuxiliaryCapability(
                        "remote_control_response_receive",
                        "response_received",
                        e.Message.RequestId,
                        e.PeerId))
                {
                    MarkRemoteControlStopPriority("response_capability_not_granted", e.Message.RequestId, e.PeerId);
                    ApplyRemoteControlReducerTransition(
                        new RemoteControlReducerEvent(
                            RemoteControlReducerEventKind.TransportControlStopReceived,
                            "response_capability_not_granted",
                            RequestId: e.Message.RequestId,
                            PeerId: e.PeerId));
                    return;
                }

                if (transport is not IRemoteControlSignalingTransport)
                {
                    LogRemoteControlViolation("response_received", "transport_missing_control_channel", e.Message.RequestId, e.PeerId);
                    MarkRemoteControlStopPriority("response_transport_missing", e.Message.RequestId, e.PeerId);
                    ApplyRemoteControlReducerTransition(
                        new RemoteControlReducerEvent(
                            RemoteControlReducerEventKind.TransportControlStopReceived,
                            "response_transport_missing",
                            RequestId: e.Message.RequestId,
                            PeerId: e.PeerId));
                    return;
                }

                if (string.IsNullOrWhiteSpace(e.Message.ConsentToken))
                {
                    LogRemoteControlViolation("response_received", "missing_consent_token", e.Message.RequestId, e.PeerId);
                    MarkRemoteControlStopPriority("response_missing_token", e.Message.RequestId, e.PeerId);
                    ApplyRemoteControlReducerTransition(
                        new RemoteControlReducerEvent(
                            RemoteControlReducerEventKind.TransportControlStopReceived,
                            "response_missing_token",
                            RequestId: e.Message.RequestId,
                            PeerId: e.PeerId));
                    return;
                }

                ApplyRemoteControlReducerTransition(
                    new RemoteControlReducerEvent(
                        RemoteControlReducerEventKind.TransportControlResponseReceived,
                        "response_allowed_waiting_start",
                        RequestId: e.Message.RequestId,
                        PeerId: "local-helper",
                        ConsentToken: e.Message.ConsentToken,
                        Decision: RemoteControlReducerResponseDecision.Allow));
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task HandleRemoteControlStartReceivedAsync(RemoteControlStartReceivedEventArgs e)
    {
        var stopEpochSnapshot = SnapshotRemoteControlStopPriorityEpoch();
        ControlDisplayInfoMessageV1? displayInfoToResend = null;
        string? displayInfoSessionId = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
            {
                LogRemoteControlInfo("start_ignored", "stop_priority", e.Message.RequestId, e.PeerId);
                return;
            }

            if (disposed || resetInProgress || role != SessionRuntimeRole.Helpee)
            {
                LogRemoteControlViolation("start_received", "invalid_runtime_state", e.Message.RequestId, e.PeerId);
                return;
            }

            if (!RequireCapability(SessionCapability.RemoteControl))
            {
                LogRemoteControlViolation("start_received", "capability_not_granted", e.Message.RequestId, e.PeerId);
                return;
            }

            if (remoteControlSessionState.ControlState != ControlState.Requesting ||
                !string.Equals(remoteControlSessionState.CurrentControlRequestId, e.Message.RequestId, StringComparison.Ordinal))
            {
                LogRemoteControlViolation("start_received", "request_mismatch", e.Message.RequestId, e.PeerId);
                ClearPendingRemoteControlConsentToken();
                MarkRemoteControlStopPriority("start_request_mismatch", e.Message.RequestId, e.PeerId);
                ApplyRemoteControlReducerTransition(
                    new RemoteControlReducerEvent(
                        RemoteControlReducerEventKind.HelpeeUserStopClicked,
                        "protocol_error_request_mismatch",
                        RequestId: e.Message.RequestId,
                        PeerId: e.PeerId));
            }
            else if (!TryValidateRemoteControlStartToken(
                         e.Message.RequestId,
                         e.PeerId,
                         e.Message.ConsentToken,
                         out var tokenFailureReason))
            {
                LogRemoteControlViolation("start_received", tokenFailureReason, e.Message.RequestId, e.PeerId);
                ClearPendingRemoteControlConsentToken();
                MarkRemoteControlStopPriority("start_invalid_token:" + tokenFailureReason, e.Message.RequestId, e.PeerId);
                ApplyRemoteControlReducerTransition(
                    new RemoteControlReducerEvent(
                        RemoteControlReducerEventKind.HelpeeUserStopClicked,
                        "protocol_error_" + tokenFailureReason,
                        RequestId: e.Message.RequestId,
                        PeerId: e.PeerId));
            }
            else
            {
                if (pendingRemoteControlConsentToken is not null)
                {
                    pendingRemoteControlConsentToken.IsUsed = true;
                }

                ApplyRemoteControlReducerTransition(
                    new RemoteControlReducerEvent(
                        RemoteControlReducerEventKind.TransportControlStartReceived,
                        "start_received_active",
                        RequestId: e.Message.RequestId,
                        PeerId: remoteControlSessionState.ControllerPeerId ?? e.PeerId));
                LogRemoteControlInfo(
                    "start_received",
                    "active",
                    e.Message.RequestId,
                    remoteControlSessionState.ControllerPeerId ?? e.PeerId);
                if (IsUsableRemoteControlDisplayInfo(latestRemoteControlDisplayInfo))
                {
                    displayInfoToResend = latestRemoteControlDisplayInfo;
                    displayInfoSessionId = currentSessionGrant?.SessionId.Value ?? sessionSecurityState.SessionId?.Value;
                }
                ClearPendingRemoteControlConsentToken();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (!string.IsNullOrWhiteSpace(displayInfoSessionId) &&
            displayInfoToResend is not null)
        {
            await SendRemoteControlDisplayInfoAsync(
                displayInfoSessionId!,
                displayInfoToResend,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleRemoteControlStopReceivedAsync(RemoteControlStopReceivedEventArgs e)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress)
            {
                return;
            }

            if (remoteControlSessionState.ControlState == ControlState.Off)
            {
                return;
            }

            ApplyRemoteControlReducerTransition(
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.TransportControlStopReceived,
                    "stop_received:" + (e.Message.Reason ?? "remote_stop"),
                    RequestId: e.Message.RequestId,
                    PeerId: e.PeerId));
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task HandleRemoteControlInputReceivedAsync(RemoteControlInputReceivedEventArgs e)
    {
        ControlInputMessageV1? acceptedMessage = null;
        string? peerId = null;
        long stopEpochSnapshot = 0;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress || state != SessionRuntimeState.Connected)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("input_ignored:runtime_state"))
                {
                    return;
                }

                LogRemoteControlInfo(
                    "input_ignored",
                    $"guard=runtime_state; disposed={disposed}; reset={resetInProgress}; session_state={state}",
                    e.Message.RequestId,
                    e.PeerId);
                return;
            }

            if (Volatile.Read(ref remoteControlStopInputSuppressionLatched) != 0)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("input_ignored:stop_preempt_latched"))
                {
                    return;
                }

                LogRemoteControlInfo(
                    "input_ignored",
                    "guard=stop_preempt_latched",
                    e.Message.RequestId,
                    e.PeerId);
                return;
            }

            if (role != SessionRuntimeRole.Helpee)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("input_ignored:wrong_role"))
                {
                    return;
                }

                LogRemoteControlInfo(
                    "input_ignored",
                    $"guard=role; expected=Helpee; actual={role}",
                    e.Message.RequestId,
                    e.PeerId);
                return;
            }

            if (!RequireCapability(SessionCapability.RemoteControl, "remote_control_input_receive"))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                LogRemoteControlViolation("input_ignored", "capability_not_granted", e.Message.RequestId, e.PeerId);
                return;
            }

            if (remoteControlSessionState.ControlState != ControlState.Active)
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("input_ignored:inactive_control_state"))
                {
                    return;
                }

                LogRemoteControlInfo(
                    "input_ignored",
                    $"guard=control_state_active; actual={remoteControlSessionState.ControlState}",
                    e.Message.RequestId,
                    e.PeerId);
                return;
            }

            if (!string.Equals(remoteControlSessionState.CurrentControlRequestId, e.Message.RequestId, StringComparison.Ordinal))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("input_ignored:request_mismatch"))
                {
                    return;
                }

                LogRemoteControlViolation(
                    "input_ignored",
                    $"guard=request_id_match; expected={remoteControlSessionState.CurrentControlRequestId ?? "(none)"}; incoming={e.Message.RequestId ?? "(none)"}",
                    e.Message.RequestId,
                    e.PeerId);
                return;
            }

            if (string.IsNullOrWhiteSpace(remoteControlSessionState.ControllerPeerId) ||
                !string.Equals(remoteControlSessionState.ControllerPeerId, e.PeerId, StringComparison.Ordinal))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (!ShouldEmitRemoteControlRateLimitedLog("input_ignored:controller_mismatch"))
                {
                    return;
                }

                LogRemoteControlViolation(
                    "input_ignored",
                    $"guard=controller_peer_match; expected={remoteControlSessionState.ControllerPeerId ?? "(none)"}; incoming={e.PeerId ?? "(none)"}",
                    e.Message.RequestId,
                    e.PeerId);
                return;
            }

            stopEpochSnapshot = SnapshotRemoteControlStopPriorityEpoch();
            var isMouseMove = string.Equals(e.Message.Kind, "mouse_move", StringComparison.Ordinal);
            if (!isMouseMove || ShouldEmitRemoteControlRateLimitedLog("input_received:mouse_move"))
            {
                LogRemoteControlInfo("input_received", FormatControlInputLogSummary(e.Message), e.Message.RequestId, e.PeerId);
            }
            acceptedMessage = e.Message;
            peerId = e.PeerId;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (acceptedMessage is not null)
        {
            await TryInjectRemoteControlInputAsync(acceptedMessage, peerId, stopEpochSnapshot).ConfigureAwait(false);
            RemoteControlInputReceived?.Invoke(
                this,
                new SessionRuntimeRemoteControlInputReceivedEventArgs(acceptedMessage, peerId));
        }
    }

    private Task TryInjectRemoteControlInputAsync(
        ControlInputMessageV1 message,
        string? peerId,
        long stopEpochSnapshot)
    {
        if (!remoteInputInjector.IsSupported)
        {
            if (ShouldEmitRemoteControlRateLimitedLog("input_inject_ignored:injector_not_supported"))
            {
                LogRemoteControlInfo("input_inject_ignored", "injector_not_supported", message.RequestId, peerId);
            }
            return Task.CompletedTask;
        }

        EnqueueRemoteControlInjection(
            new RemoteControlInjectionWorkItem(
                Message: message,
                Snapshot: null,
                PeerId: peerId,
                StopEpochSnapshot: stopEpochSnapshot));
        return Task.CompletedTask;
    }

    private Task TryInjectRemoteControlStateSnapshotAsync(
        ControlStateSnapshotV1 snapshot,
        string? peerId,
        long stopEpochSnapshot)
    {
        if (!FeatureFlags.RemoteControlStateSnapshotEnabled)
        {
            return Task.CompletedTask;
        }

        if (!remoteInputInjector.IsSupported)
        {
            if (ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:injector_not_supported"))
            {
                LogRemoteControlInfo("snapshot_ignored", "injector_not_supported", snapshot.RequestId, peerId);
            }
            return Task.CompletedTask;
        }

        EnqueueRemoteControlInjection(
            new RemoteControlInjectionWorkItem(
                Message: null,
                Snapshot: snapshot,
                PeerId: peerId,
                StopEpochSnapshot: stopEpochSnapshot));
        return Task.CompletedTask;
    }

    private void EnqueueRemoteControlInjection(RemoteControlInjectionWorkItem workItem)
    {
        var isMouseMove = workItem.Message is not null && IsLowPriorityMouseMoveInput(workItem.Message);
        var isSnapshot = workItem.Snapshot is not null;
        var coalescedMouseMoveDrop = false;
        var coalescedSnapshotDrop = false;
        var queueOverflowDroppedMouseMove = false;
        var queueOverflowDroppedSnapshot = false;
        var queueOverflowDroppedCriticalInput = false;
        string droppedCriticalKind = "(none)";
        var shouldStartExecutor = false;

        lock (remoteControlInjectionQueueGate)
        {
            if (isMouseMove)
            {
                if (queuedRemoteControlInjectionMouseMoveNode is not null)
                {
                    // Keep latest pointer position and discard stale mouse move.
                    coalescedMouseMoveDrop = true;
                    queuedRemoteControlInjectionMouseMoveNode.Value = workItem;
                }
                else if (remoteControlInjectionQueue.Count >= RemoteControlInjectionQueueCapacity)
                {
                    queueOverflowDroppedMouseMove = true;
                }
                else
                {
                    queuedRemoteControlInjectionMouseMoveNode = remoteControlInjectionQueue.AddLast(workItem);
                }
            }
            else if (isSnapshot)
            {
                if (queuedRemoteControlInjectionSnapshotNode is not null)
                {
                    // Keep only the newest snapshot state while older queued snapshots are still pending.
                    coalescedSnapshotDrop = true;
                    queuedRemoteControlInjectionSnapshotNode.Value = workItem;
                }
                else
                {
                    if (remoteControlInjectionQueue.Count >= RemoteControlInjectionQueueCapacity)
                    {
                        if (queuedRemoteControlInjectionMouseMoveNode is not null)
                        {
                            remoteControlInjectionQueue.Remove(queuedRemoteControlInjectionMouseMoveNode);
                            queuedRemoteControlInjectionMouseMoveNode = null;
                            queueOverflowDroppedMouseMove = true;
                        }
                        else
                        {
                            queueOverflowDroppedSnapshot = true;
                        }
                    }

                    if (!queueOverflowDroppedSnapshot)
                    {
                        queuedRemoteControlInjectionSnapshotNode = remoteControlInjectionQueue.AddLast(workItem);
                    }
                }
            }
            else
            {
                if (remoteControlInjectionQueue.Count >= RemoteControlInjectionQueueCapacity)
                {
                    if (queuedRemoteControlInjectionMouseMoveNode is not null)
                    {
                        // Preserve button/key events by evicting pending low-priority mouse move first.
                        remoteControlInjectionQueue.Remove(queuedRemoteControlInjectionMouseMoveNode);
                        queuedRemoteControlInjectionMouseMoveNode = null;
                        queueOverflowDroppedMouseMove = true;
                    }
                    else if (queuedRemoteControlInjectionSnapshotNode is not null)
                    {
                        // Prefer evicting stale snapshot state before older key/button work.
                        remoteControlInjectionQueue.Remove(queuedRemoteControlInjectionSnapshotNode);
                        queuedRemoteControlInjectionSnapshotNode = null;
                        queueOverflowDroppedSnapshot = true;
                    }
                    else if (remoteControlInjectionQueue.First is not null)
                    {
                        droppedCriticalKind = DescribeRemoteControlInjectionWorkItemKind(remoteControlInjectionQueue.First.Value);
                        remoteControlInjectionQueue.RemoveFirst();
                        queueOverflowDroppedCriticalInput = true;
                    }
                }

                remoteControlInjectionQueue.AddLast(workItem);
            }

            if (remoteControlInjectionQueue.Count > 0 && !remoteControlInjectionExecutorActive)
            {
                remoteControlInjectionExecutorActive = true;
                shouldStartExecutor = true;
            }
        }

        if (coalescedMouseMoveDrop)
        {
            IncrementRemoteControlDebugQueueDropCount();
            PublishRemoteControlDebugDiagnostics();
        }

        if (coalescedSnapshotDrop)
        {
            IncrementRemoteControlDebugQueueDropCount();
            PublishRemoteControlDebugDiagnostics();
        }

        if (queueOverflowDroppedMouseMove)
        {
            IncrementRemoteControlDebugQueueDropCount();
            PublishRemoteControlDebugDiagnostics();
                LogRemoteControlInfo(
                    "input_inject_queue_drop",
                    "mouse_move_overflow",
                    GetRemoteControlInjectionWorkItemRequestId(workItem),
                    workItem.PeerId);
        }

        if (queueOverflowDroppedSnapshot)
        {
            IncrementRemoteControlDebugQueueDropCount();
            PublishRemoteControlDebugDiagnostics();
            LogRemoteControlInfo(
                "input_inject_queue_drop",
                "state_snapshot_overflow",
                GetRemoteControlInjectionWorkItemRequestId(workItem),
                workItem.PeerId);
        }

        if (queueOverflowDroppedCriticalInput)
        {
            IncrementRemoteControlDebugQueueDropCount();
            PublishRemoteControlDebugDiagnostics();
            LogRemoteControlViolation(
                "input_inject_queue_drop",
                "critical_overflow_dropped_oldest:" + droppedCriticalKind,
                GetRemoteControlInjectionWorkItemRequestId(workItem),
                workItem.PeerId);
        }

        if (shouldStartExecutor)
        {
            RunCountedBackgroundTask(
                DrainRemoteControlInjectionQueueAsync,
                countAsTransportTask: false);
        }
    }

    private Task DrainRemoteControlInjectionQueueAsync()
    {
        while (true)
        {
            RemoteControlInjectionWorkItem next;

            lock (remoteControlInjectionQueueGate)
            {
                if (remoteControlInjectionQueue.First is null)
                {
                    remoteControlInjectionExecutorActive = false;
                    PublishRemoteControlDebugDiagnostics();
                    return Task.CompletedTask;
                }

                var node = remoteControlInjectionQueue.First;
                remoteControlInjectionQueue.RemoveFirst();
                if (ReferenceEquals(node, queuedRemoteControlInjectionMouseMoveNode))
                {
                    queuedRemoteControlInjectionMouseMoveNode = null;
                }
                else if (ReferenceEquals(node, queuedRemoteControlInjectionSnapshotNode))
                {
                    queuedRemoteControlInjectionSnapshotNode = null;
                }

                next = node.Value;
            }

            PublishRemoteControlDebugDiagnostics();

            ProcessRemoteControlInjectionWorkItem(next);
        }
    }

    private void ProcessRemoteControlInjectionWorkItem(RemoteControlInjectionWorkItem workItem)
    {
        var snapshot = workItem.Snapshot;
        if (snapshot is not null)
        {
            try
            {
                if (!ApplyRemoteControlStateSnapshotCore(snapshot, workItem.PeerId, workItem.StopEpochSnapshot, out var reason))
                {
                    LogRemoteControlSnapshotSuppressed(reason, snapshot.RequestId, workItem.PeerId);
                    return;
                }

                if (ShouldEmitRemoteControlRateLimitedLog("snapshot_applied", TimeSpan.FromSeconds(1)))
                {
                    LogRemoteControlInfo(
                        "snapshot_applied",
                        $"seq={snapshot.Seq.ToString(CultureInfo.InvariantCulture)}; buttons_mask={snapshot.MouseButtonsMask.ToString(CultureInfo.InvariantCulture)}; modifiers_mask={snapshot.ModifiersMask.ToString(CultureInfo.InvariantCulture)}",
                        snapshot.RequestId,
                        workItem.PeerId);
                }
            }
            catch (Exception ex)
            {
                LogRemoteControlViolation("snapshot_apply_failed", ex.GetType().Name, snapshot.RequestId, workItem.PeerId);
            }

            return;
        }

        var message = workItem.Message;
        if (message is null)
        {
            return;
        }

        var peerId = workItem.PeerId;
        var seq = message.Seq;

        try
        {
            if (HasRemoteControlStopPriorityChanged(workItem.StopEpochSnapshot))
            {
                IncrementRemoteControlSuppressedInjectionCounter();
                if (ShouldEmitRemoteControlRateLimitedLog("input_inject_ignored:stop_priority"))
                {
                    LogRemoteControlInfo("input_inject_ignored", "stop_priority", message.RequestId, peerId);
                }
                return;
            }

            if (FeatureFlags.RemoteControlSeqGateEnabled)
            {
                if (seq <= 0)
                {
                    LogRemoteControlInjectionSuppressed("missing_seq", message.RequestId, peerId);
                    return;
                }

                var previousSeq = Volatile.Read(ref lastRemoteControlInjectedSeq);
                if (seq <= previousSeq)
                {
                    MaybeSendControlAck(message.RequestId, message.Kind, previousSeq);
                    if (ShouldEmitRemoteControlRateLimitedLog("input_deduped:duplicate_seq"))
                    {
                        LogRemoteControlRateLimitedInfo(
                            "input_stale_dropped",
                            $"duplicate_or_replay_seq={seq.ToString(CultureInfo.InvariantCulture)}; last={previousSeq.ToString(CultureInfo.InvariantCulture)}",
                            message.RequestId,
                            peerId);
                    }

                    return;
                }

                if (seq != previousSeq + 1)
                {
                    var isMouseMoveGap = string.Equals(message.Kind, "mouse_move", StringComparison.Ordinal) &&
                                         seq > previousSeq + 1;
                    if (isMouseMoveGap)
                    {
                        // MouseMove packets can be intentionally dropped by low-priority/latest-wins lanes.
                        // Allow gap recovery so seq-gating does not permanently stall remote control.
                        var recoveredPreviousSeq = seq - 1;
                        Volatile.Write(ref lastRemoteControlInjectedSeq, recoveredPreviousSeq);
                        if (ShouldEmitRemoteControlRateLimitedLog("input_deduped:mouse_move_gap_recovered"))
                        {
                            LogRemoteControlInfo(
                                "input_deduped",
                                $"mouse_move_gap_recovered_seq={seq.ToString(CultureInfo.InvariantCulture)}; old_last={previousSeq.ToString(CultureInfo.InvariantCulture)}; recovered_last={recoveredPreviousSeq.ToString(CultureInfo.InvariantCulture)}",
                                message.RequestId,
                                peerId);
                        }
                    }
                    else
                    {
                        MaybeSendControlAck(message.RequestId, message.Kind, previousSeq);
                        if (ShouldEmitRemoteControlRateLimitedLog("input_deduped:out_of_order_seq"))
                        {
                            LogRemoteControlRateLimitedInfo(
                                "input_stale_dropped",
                                $"out_of_order_seq={seq.ToString(CultureInfo.InvariantCulture)}; expected={(previousSeq + 1).ToString(CultureInfo.InvariantCulture)}; last={previousSeq.ToString(CultureInfo.InvariantCulture)}",
                                message.RequestId,
                                peerId);
                        }

                        return;
                    }
                }
            }

            if (!TryInjectRemoteControlInputCore(message, peerId, workItem.StopEpochSnapshot, out var reason))
            {
                LogRemoteControlInjectionSuppressed(reason, message.RequestId, peerId);
                if (string.Equals(reason, "display_id_mismatch", StringComparison.Ordinal))
                {
                    TriggerRemoteControlMismatchStop(message.RequestId, peerId);
                }

                return;
            }

            if (seq > 0)
            {
                Volatile.Write(ref lastRemoteControlInjectedSeq, seq);
                MaybeSendControlAck(message.RequestId, message.Kind, seq);
            }

            var isMouseMove = string.Equals(message.Kind, "mouse_move", StringComparison.Ordinal);
            if (!isMouseMove || ShouldEmitRemoteControlRateLimitedLog("input_injected:mouse_move"))
            {
                LogRemoteControlInfo("input_injected", FormatControlInputLogSummary(message), message.RequestId, peerId);
            }
        }
        catch (Exception ex)
        {
            LogRemoteControlViolation("input_inject_failed", ex.GetType().Name, message.RequestId, peerId);
        }
    }

    private bool ApplyRemoteControlStateSnapshotCore(
        ControlStateSnapshotV1 snapshot,
        string? controllerPeerId,
        long stopEpochSnapshot,
        out string reason)
    {
        reason = string.Empty;
        if (!FeatureFlags.RemoteControlStateSnapshotEnabled)
        {
            reason = "feature_disabled";
            return false;
        }

        if (Volatile.Read(ref remoteControlStopInputSuppressionLatched) != 0 ||
            HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
        {
            reason = "stop_priority";
            return false;
        }

        if (state != SessionRuntimeState.Connected)
        {
            reason = "guard_runtime_state";
            return false;
        }

        if (role != SessionRuntimeRole.Helpee)
        {
            reason = "guard_role";
            return false;
        }

        if (remoteControlSessionState.ControlState != ControlState.Active)
        {
            reason = "guard_control_state";
            return false;
        }

        if (!string.Equals(remoteControlSessionState.CurrentControlRequestId, snapshot.RequestId, StringComparison.Ordinal))
        {
            reason = "guard_request_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteControlSessionState.ControllerPeerId) ||
            !string.Equals(remoteControlSessionState.ControllerPeerId, controllerPeerId, StringComparison.Ordinal))
        {
            reason = "guard_controller_mismatch";
            return false;
        }

        if (!TryAuthorizeRemoteControlInjection(out reason))
        {
            return false;
        }

        if (snapshot.Seq <= 0)
        {
            reason = "missing_seq";
            return false;
        }

        var previousAppliedSeq = Interlocked.Read(ref remoteControlSnapshotLastAppliedSeq);
        if (previousAppliedSeq > 0 &&
            snapshot.Seq <= previousAppliedSeq)
        {
            reason = snapshot.Seq == previousAppliedSeq
                ? "duplicate_seq"
                : "out_of_order_seq";
            return false;
        }

        var desiredButtons = ((RemoteControlMouseButtonsMask)snapshot.MouseButtonsMask) & RemoteControlKnownMouseButtonsMask;
        var stuckButtons = remoteControlAppliedMouseButtonsMask & ~desiredButtons;
        var nowTicks = Stopwatch.GetTimestamp();
        var unstuckButtons = ReleaseStuckRemoteControlMouseButtons(stuckButtons, nowTicks);
        remoteControlAppliedMouseButtonsMask &= desiredButtons;
        var forceDownInjected = 0L;
        if (FeatureFlags.RemoteControlStateSnapshotForceDownEnabled &&
            IsSnapshotForceDownEligible(nowTicks))
        {
            forceDownInjected = TryForceDownButtonsFromSnapshot(desiredButtons, nowTicks);
        }

        var desiredModifiers = ((RemoteControlModifiersMask)snapshot.ModifiersMask) & RemoteControlKnownModifiersMask;
        var stuckModifiers = remoteControlAppliedModifiersMask & ~desiredModifiers;
        var unstuckModifiers = ReleaseStuckRemoteControlModifiers(stuckModifiers);
        remoteControlAppliedModifiersMask &= desiredModifiers;

        Interlocked.Increment(ref remoteControlSnapshotAppliedCount);
        if (unstuckButtons > 0)
        {
            Interlocked.Add(ref remoteControlSnapshotUnstuckButtonsCount, unstuckButtons);
        }
        if (unstuckModifiers > 0)
        {
            Interlocked.Add(ref remoteControlSnapshotUnstuckModifiersCount, unstuckModifiers);
        }
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedSeq, snapshot.Seq);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedModifiersMask, (int)remoteControlAppliedModifiersMask);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedMouseButtonsMask, (int)remoteControlAppliedMouseButtonsMask);
        if (forceDownInjected > 0 &&
            ShouldEmitRemoteControlRateLimitedLog("snapshot_force_down_applied", TimeSpan.FromSeconds(2)))
        {
            LogRemoteControlInfo(
                "snapshot_force_down_applied",
                $"count={forceDownInjected.ToString(CultureInfo.InvariantCulture)}; desired_buttons={(int)desiredButtons}; applied_buttons={(int)remoteControlAppliedMouseButtonsMask}",
                snapshot.RequestId,
                controllerPeerId);
        }

        PublishRemoteControlDebugDiagnostics();
        reason = unstuckButtons > 0 || unstuckModifiers > 0
            ? "unstuck_input_state"
            : forceDownInjected > 0
                ? "force_down_applied"
                : "snapshot_noop";
        return true;
    }

    private long ReleaseStuckRemoteControlMouseButtons(RemoteControlMouseButtonsMask buttonsToRelease, long nowTicks)
    {
        if (buttonsToRelease == RemoteControlMouseButtonsMask.None)
        {
            return 0;
        }

        long releasedCount = 0;
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.Left, RemoteMouseButton.Left);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.Right, RemoteMouseButton.Right);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.Middle, RemoteMouseButton.Middle);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.X1, RemoteMouseButton.X1);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.X2, RemoteMouseButton.X2);
        return releasedCount;

        void ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask mask, RemoteMouseButton button)
        {
            if ((buttonsToRelease & mask) == 0)
            {
                return;
            }

            remoteInputInjector.InjectMouseButton(button, RemoteButtonAction.Up);
            RecordSnapshotForcedButtonUp(mask, nowTicks);
            releasedCount++;
        }
    }

    private long ReleaseStuckRemoteControlModifiers(RemoteControlModifiersMask modifiersToRelease)
    {
        if (modifiersToRelease == RemoteControlModifiersMask.None)
        {
            return 0;
        }

        long releasedCount = 0;
        ReleaseModifierIfNeeded(RemoteControlModifiersMask.Shift, "Shift");
        ReleaseModifierIfNeeded(RemoteControlModifiersMask.Ctrl, "Ctrl");
        ReleaseModifierIfNeeded(RemoteControlModifiersMask.Alt, "Alt");
        if ((modifiersToRelease & (RemoteControlModifiersMask.Meta | RemoteControlModifiersMask.Win)) != 0)
        {
            remoteInputInjector.InjectKey(new RemoteKey("Meta"), RemoteKeyAction.Up, RemoteKeyModifiers.None);
            releasedCount++;
        }

        return releasedCount;

        void ReleaseModifierIfNeeded(RemoteControlModifiersMask mask, string key)
        {
            if ((modifiersToRelease & mask) == 0)
            {
                return;
            }

            remoteInputInjector.InjectKey(new RemoteKey(key), RemoteKeyAction.Up, RemoteKeyModifiers.None);
            releasedCount++;
        }
    }

    private bool IsSnapshotForceDownEligible(long nowTicks)
    {
        var continuousStartTick = Interlocked.Read(ref remoteControlSnapshotContinuousStartTick);
        if (continuousStartTick <= 0)
        {
            return false;
        }

        return Stopwatch.GetElapsedTime(continuousStartTick, nowTicks) >= RemoteControlSnapshotForceDownContinuousWindow;
    }

    private long TryForceDownButtonsFromSnapshot(RemoteControlMouseButtonsMask desiredButtons, long nowTicks)
    {
        long forcedDownCount = 0;
        TryForceDownButton(RemoteControlMouseButtonsMask.Left, RemoteMouseButton.Left);
        TryForceDownButton(RemoteControlMouseButtonsMask.Right, RemoteMouseButton.Right);
        TryForceDownButton(RemoteControlMouseButtonsMask.Middle, RemoteMouseButton.Middle);
        TryForceDownButton(RemoteControlMouseButtonsMask.X1, RemoteMouseButton.X1);
        TryForceDownButton(RemoteControlMouseButtonsMask.X2, RemoteMouseButton.X2);
        return forcedDownCount;

        void TryForceDownButton(RemoteControlMouseButtonsMask mask, RemoteMouseButton button)
        {
            if ((desiredButtons & mask) == 0)
            {
                return;
            }

            if ((remoteControlAppliedMouseButtonsMask & mask) != 0)
            {
                return;
            }

            if (!WasSnapshotForcedUpRecently(mask, nowTicks))
            {
                return;
            }

            remoteInputInjector.InjectMouseButton(button, RemoteButtonAction.Down);
            remoteControlAppliedMouseButtonsMask |= mask;
            forcedDownCount++;
        }
    }

    private void RecordSnapshotForcedButtonUp(RemoteControlMouseButtonsMask mask, long nowTicks)
    {
        switch (mask)
        {
            case RemoteControlMouseButtonsMask.Left:
                Interlocked.Exchange(ref remoteControlSnapshotForcedUpLeftTick, nowTicks);
                break;
            case RemoteControlMouseButtonsMask.Right:
                Interlocked.Exchange(ref remoteControlSnapshotForcedUpRightTick, nowTicks);
                break;
            case RemoteControlMouseButtonsMask.Middle:
                Interlocked.Exchange(ref remoteControlSnapshotForcedUpMiddleTick, nowTicks);
                break;
            case RemoteControlMouseButtonsMask.X1:
                Interlocked.Exchange(ref remoteControlSnapshotForcedUpX1Tick, nowTicks);
                break;
            case RemoteControlMouseButtonsMask.X2:
                Interlocked.Exchange(ref remoteControlSnapshotForcedUpX2Tick, nowTicks);
                break;
        }
    }

    private bool WasSnapshotForcedUpRecently(RemoteControlMouseButtonsMask mask, long nowTicks)
    {
        long tick = mask switch
        {
            RemoteControlMouseButtonsMask.Left => Interlocked.Read(ref remoteControlSnapshotForcedUpLeftTick),
            RemoteControlMouseButtonsMask.Right => Interlocked.Read(ref remoteControlSnapshotForcedUpRightTick),
            RemoteControlMouseButtonsMask.Middle => Interlocked.Read(ref remoteControlSnapshotForcedUpMiddleTick),
            RemoteControlMouseButtonsMask.X1 => Interlocked.Read(ref remoteControlSnapshotForcedUpX1Tick),
            RemoteControlMouseButtonsMask.X2 => Interlocked.Read(ref remoteControlSnapshotForcedUpX2Tick),
            _ => 0,
        };

        return tick > 0 && Stopwatch.GetElapsedTime(tick, nowTicks) <= RemoteControlSnapshotRecentForcedUpWindow;
    }

    private static RemoteControlMouseButtonsMask ToRemoteControlMouseButtonsMask(RemoteMouseButton button)
    {
        return button switch
        {
            RemoteMouseButton.Left => RemoteControlMouseButtonsMask.Left,
            RemoteMouseButton.Right => RemoteControlMouseButtonsMask.Right,
            RemoteMouseButton.Middle => RemoteControlMouseButtonsMask.Middle,
            RemoteMouseButton.X1 => RemoteControlMouseButtonsMask.X1,
            RemoteMouseButton.X2 => RemoteControlMouseButtonsMask.X2,
            _ => RemoteControlMouseButtonsMask.None,
        };
    }

    private void ApplyInjectedMouseButtonState(RemoteMouseButton button, RemoteButtonAction action)
    {
        var mask = ToRemoteControlMouseButtonsMask(button);
        if (mask == RemoteControlMouseButtonsMask.None)
        {
            return;
        }

        remoteControlAppliedMouseButtonsMask = action == RemoteButtonAction.Down
            ? remoteControlAppliedMouseButtonsMask | mask
            : remoteControlAppliedMouseButtonsMask & ~mask;
    }

    private static bool TryMapModifierMaskForInjectedKey(string? key, out RemoteControlModifiersMask mask)
    {
        mask = RemoteControlModifiersMask.None;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        switch (key.Trim())
        {
            case "LeftShift":
            case "RightShift":
            case "Shift":
                mask = RemoteControlModifiersMask.Shift;
                return true;
            case "LeftCtrl":
            case "RightCtrl":
            case "Ctrl":
                mask = RemoteControlModifiersMask.Ctrl;
                return true;
            case "LeftAlt":
            case "RightAlt":
            case "Alt":
                mask = RemoteControlModifiersMask.Alt;
                return true;
            case "Meta":
            case "LWin":
            case "RWin":
            case "LeftWindows":
            case "RightWindows":
                mask = RemoteControlModifiersMask.Meta | RemoteControlModifiersMask.Win;
                return true;
            default:
                return false;
        }
    }

    private void ApplyInjectedModifierState(string? key, RemoteKeyAction action)
    {
        if (!TryMapModifierMaskForInjectedKey(key, out var mask))
        {
            return;
        }

        remoteControlAppliedModifiersMask = action == RemoteKeyAction.Down
            ? remoteControlAppliedModifiersMask | mask
            : remoteControlAppliedModifiersMask & ~mask;
    }

    private void LogRemoteControlSnapshotSuppressed(string reason, string? requestId, string? controllerPeerId)
    {
        IncrementRemoteControlSuppressedInjectionCounter();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        if (!ShouldEmitRemoteControlRateLimitedLog("snapshot_ignored:" + normalizedReason))
        {
            return;
        }

        if (normalizedReason is "duplicate_seq" or "out_of_order_seq")
        {
            LogRemoteControlRateLimitedInfo(
                "snapshot_stale_dropped",
                normalizedReason,
                requestId,
                controllerPeerId);
        }

        if (normalizedReason is "guard_runtime_state" or "guard_role" or "guard_control_state" or "stop_priority" or "feature_disabled" or "duplicate_seq" or "out_of_order_seq")
        {
            LogRemoteControlInfo("snapshot_ignored", normalizedReason, requestId, controllerPeerId);
            return;
        }

        LogRemoteControlViolation("snapshot_ignored", normalizedReason, requestId, controllerPeerId);
    }

    private void MaybeSendControlAck(string? requestId, string? kind, long seq)
    {
        if (!FeatureFlags.RemoteControlAckEnabled || seq <= 0)
        {
            return;
        }

        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        if (normalizedRequestId is null)
        {
            return;
        }

        if (state != SessionRuntimeState.Connected ||
            role != SessionRuntimeRole.Helpee ||
            remoteControlSessionState.ControlState != ControlState.Active ||
            !string.Equals(remoteControlSessionState.CurrentControlRequestId, normalizedRequestId, StringComparison.Ordinal))
        {
            return;
        }

        var isMouseMove = string.Equals(kind?.Trim(), "mouse_move", StringComparison.Ordinal);
        var nowTicks = Stopwatch.GetTimestamp();
        var previousAckSeq = Volatile.Read(ref lastRemoteControlAckSentSeq);
        var shouldSend = !isMouseMove;
        if (isMouseMove)
        {
            var previousAckTick = Volatile.Read(ref lastRemoteControlAckSentTick);
            var sequenceAdvancedEnough = previousAckSeq <= 0 || seq - previousAckSeq >= RemoteControlAckMouseMoveMinSeqDelta;
            var timeWindowElapsed = previousAckTick <= 0 || Stopwatch.GetElapsedTime(previousAckTick, nowTicks) >= RemoteControlAckMouseMoveMinInterval;
            shouldSend = sequenceAdvancedEnough || timeWindowElapsed;
        }

        if (!shouldSend)
        {
            return;
        }

        if (transport is not IRemoteControlSignalingTransport controlTransport)
        {
            return;
        }

        Volatile.Write(ref lastRemoteControlAckSentSeq, seq);
        Volatile.Write(ref lastRemoteControlAckSentTick, nowTicks);
        Interlocked.Increment(ref remoteControlAckSentCount);
        PublishRemoteControlDebugDiagnostics();

        var ack = new ControlInputAckV1
        {
            RequestId = normalizedRequestId,
            AckSeq = seq,
            TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await controlTransport.SendControlAckAsync(ack, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (ShouldEmitRemoteControlRateLimitedLog("ack_send_failed"))
                    {
                        LogRemoteControlViolation("ack_send_failed", ex.GetType().Name, ack.RequestId);
                    }
                }
            },
            countAsTransportTask: false);
    }

    private bool TryInjectRemoteControlInputCore(
        ControlInputMessageV1 message,
        string? controllerPeerId,
        long stopEpochSnapshot,
        out string reason)
    {
        reason = string.Empty;
        if (Volatile.Read(ref remoteControlStopInputSuppressionLatched) != 0 ||
            HasRemoteControlStopPriorityChanged(stopEpochSnapshot))
        {
            reason = "stop_priority";
            return false;
        }

        if (state != SessionRuntimeState.Connected)
        {
            reason = "guard_runtime_state";
            return false;
        }

        if (role != SessionRuntimeRole.Helpee)
        {
            reason = "guard_role";
            return false;
        }

        if (remoteControlSessionState.ControlState != ControlState.Active)
        {
            reason = "guard_control_state";
            return false;
        }

        if (!string.Equals(remoteControlSessionState.CurrentControlRequestId, message.RequestId, StringComparison.Ordinal))
        {
            reason = "guard_request_mismatch";
            return false;
        }

            if (string.IsNullOrWhiteSpace(remoteControlSessionState.ControllerPeerId) ||
                !string.Equals(remoteControlSessionState.ControllerPeerId, controllerPeerId, StringComparison.Ordinal))
            {
                reason = "guard_controller_mismatch";
                return false;
            }

            if (!TryAuthorizeRemoteControlInjection(out reason))
            {
                return false;
            }

            if (TryRejectStaleDisplayInfoInput(message, out reason))
            {
                return false;
            }

            ProbeRemoteControlElevationBoundary(message, controllerPeerId);

            var kind = string.IsNullOrWhiteSpace(message.Kind)
                ? string.Empty
                : message.Kind.Trim();
        switch (kind)
        {
            case "mouse_move":
            {
                if (!TryMapNormalizedToVirtualDesktopPixels(message, controllerPeerId, out var x, out var y, out reason))
                {
                    return false;
                }

                LogRemoteInputMoveInjection(
                    message,
                    controllerPeerId,
                    message.Nx!.Value,
                    message.Ny!.Value,
                    x,
                    y,
                    isHighRateMove: true);
                remoteInputInjector.InjectMouseMoveAbsolute(x, y);
                return true;
            }
            case "mouse_button":
            {
                if (!TryParseRemoteButtonAction(message.Action, out var action))
                {
                    reason = "invalid_button_action";
                    return false;
                }

                if (!TryParseRemoteMouseButton(message.Button, out var button))
                {
                    reason = "invalid_button";
                    return false;
                }

                if (message.Nx.HasValue && message.Ny.HasValue)
                {
                    if (!TryMapNormalizedToVirtualDesktopPixels(message, controllerPeerId, out var x, out var y, out reason))
                    {
                        return false;
                    }

                    LogRemoteInputMoveInjection(
                        message,
                        controllerPeerId,
                        message.Nx.Value,
                        message.Ny.Value,
                        x,
                        y,
                        isHighRateMove: false);
                    remoteInputInjector.InjectMouseMoveAbsolute(x, y);
                }

                remoteInputInjector.InjectMouseButton(button, action);
                ApplyInjectedMouseButtonState(button, action);
                return true;
            }
            case "mouse_wheel":
            {
                var deltaX = NormalizeWheelDelta(message.DeltaX, horizontal: true);
                var deltaY = NormalizeWheelDelta(message.DeltaY, horizontal: false);
                if (deltaX == 0 && deltaY == 0)
                {
                    reason = "wheel_carry_only";
                    return true;
                }

                remoteInputInjector.InjectMouseWheel(deltaX, deltaY);
                return true;
            }
            case "key":
            {
                if (!TryParseRemoteKeyAction(message.Action, out var action))
                {
                    reason = "invalid_key_action";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(message.Key))
                {
                    reason = "missing_key";
                    return false;
                }

                var modifiers = BuildRemoteKeyModifiers(message);
                remoteInputInjector.InjectKey(
                    new RemoteKey(message.Key.Trim(), message.PhysicalKey?.Trim()),
                    action,
                    modifiers);
                ApplyInjectedModifierState(message.Key, action);
                return true;
            }
            default:
                reason = "unsupported_kind";
                return false;
        }
    }

    private bool TryAuthorizeRemoteControlInjection(out string reason)
    {
        EnsureApprovalGrantActive();
        var authorization = EvaluateCapabilityAuthorization(SessionCapability.RemoteControl);
        if (authorization.IsAuthorized)
        {
            reason = string.Empty;
            return true;
        }

        reason = MapAuthorizationFailureReason(authorization.Failure);
        return false;
    }

    private bool TryRejectStaleDisplayInfoInput(ControlInputMessageV1 message, out string reason)
    {
        reason = string.Empty;
        var kind = string.IsNullOrWhiteSpace(message.Kind) ? string.Empty : message.Kind.Trim();
        if (string.Equals(kind, "key", StringComparison.Ordinal))
        {
            return false;
        }

        var displayInfo = latestRemoteControlDisplayInfo;
        if (!IsUsableRemoteControlDisplayInfo(displayInfo))
        {
            return false;
        }

        var currentDisplayInfo = displayInfo!;
        var normalizedDisplayId = string.IsNullOrWhiteSpace(message.DisplayId) ? null : message.DisplayId.Trim();
        if (normalizedDisplayId is not null &&
            !string.Equals(normalizedDisplayId, currentDisplayInfo.DisplayId, StringComparison.Ordinal))
        {
            var incomingRevision = message.DisplayInfoRevision ?? 0;
            if (!hasRemoteControlRevisionMismatchCache ||
                !string.Equals(lastRemoteControlRevisionMismatchDisplayId, normalizedDisplayId, StringComparison.Ordinal) ||
                lastRemoteControlRevisionMismatchIncomingRevision != incomingRevision ||
                lastRemoteControlRevisionMismatchExpectedRevision != currentDisplayInfo.Revision)
            {
                hasRemoteControlRevisionMismatchCache = true;
                lastRemoteControlRevisionMismatchDisplayId = normalizedDisplayId;
                lastRemoteControlRevisionMismatchIncomingRevision = incomingRevision;
                lastRemoteControlRevisionMismatchExpectedRevision = currentDisplayInfo.Revision;
            }
            reason = "display_id_mismatch";
            return true;
        }

        var incomingRevisionForCurrentDisplay = message.DisplayInfoRevision ?? 0;
        if (incomingRevisionForCurrentDisplay <= 0)
        {
            reason = "display_revision_missing";
            return true;
        }

        if (incomingRevisionForCurrentDisplay < currentDisplayInfo.Revision)
        {
            reason = "display_revision_stale";
            return true;
        }

        if (incomingRevisionForCurrentDisplay != currentDisplayInfo.Revision)
        {
            reason = "display_revision_mismatch";
            return true;
        }

        ClearRemoteControlRevisionMismatchCache();
        return false;
    }

    private bool TryMapNormalizedToVirtualDesktopPixels(
        ControlInputMessageV1 message,
        string? controllerPeerId,
        out int xPx,
        out int yPx,
        out string reason)
    {
        xPx = 0;
        yPx = 0;
        reason = string.Empty;
        var nxValue = message.Nx;
        var nyValue = message.Ny;
        if (!nxValue.HasValue || !nyValue.HasValue ||
            double.IsNaN(nxValue.Value) || double.IsInfinity(nxValue.Value) ||
            double.IsNaN(nyValue.Value) || double.IsInfinity(nyValue.Value))
        {
            reason = "invalid_coordinates";
            return false;
        }

        var displayInfo = latestRemoteControlDisplayInfo;
        if (IsUsableRemoteControlDisplayInfo(displayInfo))
        {
            var currentDisplayInfo = displayInfo!;
            var normalizedDisplayId = string.IsNullOrWhiteSpace(message.DisplayId) ? null : message.DisplayId.Trim();
            if (normalizedDisplayId is null)
            {
                reason = "missing_display_id";
                return false;
            }

            if (!string.Equals(normalizedDisplayId, currentDisplayInfo.DisplayId, StringComparison.Ordinal))
            {
                reason = "display_id_mismatch";
                return false;
            }

            var clampedNx = Math.Clamp(nxValue.Value, 0d, 1d);
            var clampedNy = Math.Clamp(nyValue.Value, 0d, 1d);
            if (Math.Abs(clampedNx - nxValue.Value) > double.Epsilon ||
                Math.Abs(clampedNy - nyValue.Value) > double.Epsilon)
            {
                IncrementRemoteControlDebugMappingClampCount();
                PublishRemoteControlDebugDiagnostics();
            }

            var captureX = currentDisplayInfo.CaptureRegionX;
            var captureY = currentDisplayInfo.CaptureRegionY;
            var captureWidth = currentDisplayInfo.CaptureRegionWidth;
            var captureHeight = currentDisplayInfo.CaptureRegionHeight;
            if (captureWidth <= 0 || captureHeight <= 0)
            {
                reason = "capture_region_invalid";
                return false;
            }

            var mappedX = captureX + (int)Math.Round(clampedNx * (captureWidth - 1d), MidpointRounding.AwayFromZero);
            var mappedY = captureY + (int)Math.Round(clampedNy * (captureHeight - 1d), MidpointRounding.AwayFromZero);

            var maxX = captureX + captureWidth - 1;
            var maxY = captureY + captureHeight - 1;
            xPx = Math.Clamp(mappedX, captureX, maxX);
            yPx = Math.Clamp(mappedY, captureY, maxY);
            SetRemoteControlDebugLastMapped(nxValue.Value, nyValue.Value, xPx, yPx);
            PublishRemoteControlDebugDiagnostics();
            if (ShouldEmitRemoteControlRateLimitedLog(
                    "input_mapping_applied:" + currentDisplayInfo.DisplayId,
                    TimeSpan.FromSeconds(2)))
            {
                LogRemoteControlInfo(
                    "input_mapping_applied",
                    $"display_id={currentDisplayInfo.DisplayId}; revision={currentDisplayInfo.Revision.ToString(CultureInfo.InvariantCulture)}; nx={FormatOptionalCoordinate(nxValue)}; ny={FormatOptionalCoordinate(nyValue)}; clamped_nx={clampedNx.ToString("0.###", CultureInfo.InvariantCulture)}; clamped_ny={clampedNy.ToString("0.###", CultureInfo.InvariantCulture)}; capture_region={captureX},{captureY},{captureWidth}x{captureHeight}; mapped_px={xPx},{yPx}",
                    message.RequestId);
            }
            return true;
        }

        reason = "mapping_metadata_missing";
        if (ShouldEmitRemoteControlRateLimitedLog("input_mapping_missing", RemoteControlMoveInjectLogWindow))
        {
            var incomingDisplayId = string.IsNullOrWhiteSpace(message.DisplayId) ? "(none)" : message.DisplayId!.Trim();
            var incomingRevision = message.DisplayInfoRevision?.ToString(CultureInfo.InvariantCulture) ?? "(none)";
            var localDisplayId = latestRemoteControlDisplayInfo?.DisplayId ?? "(none)";
            var localRevision = latestRemoteControlDisplayInfo?.Revision.ToString(CultureInfo.InvariantCulture) ?? "(none)";
            LogRemoteControlViolation(
                "input_mapping_missing",
                $"incoming_display_id={incomingDisplayId}; incoming_revision={incomingRevision}; local_display_id={localDisplayId}; local_revision={localRevision}",
                message.RequestId,
                controllerPeerId);
        }

        return false;
    }

    private static bool TryParseRemoteButtonAction(string? action, out RemoteButtonAction parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        switch (action.Trim().ToLowerInvariant())
        {
            case "down":
                parsed = RemoteButtonAction.Down;
                return true;
            case "up":
                parsed = RemoteButtonAction.Up;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseRemoteMouseButton(string? button, out RemoteMouseButton parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(button))
        {
            return false;
        }

        switch (button.Trim().ToLowerInvariant())
        {
            case "left":
                parsed = RemoteMouseButton.Left;
                return true;
            case "right":
                parsed = RemoteMouseButton.Right;
                return true;
            case "middle":
                parsed = RemoteMouseButton.Middle;
                return true;
            case "x1":
                parsed = RemoteMouseButton.X1;
                return true;
            case "x2":
                parsed = RemoteMouseButton.X2;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseRemoteKeyAction(string? action, out RemoteKeyAction parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        switch (action.Trim().ToLowerInvariant())
        {
            case "down":
                parsed = RemoteKeyAction.Down;
                return true;
            case "up":
                parsed = RemoteKeyAction.Up;
                return true;
            default:
                return false;
        }
    }

    private int NormalizeWheelDelta(double? value, bool horizontal)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return 0;
        }

        lock (remoteControlWheelDeltaGate)
        {
            var carry = horizontal ? remoteControlWheelDeltaCarryX : remoteControlWheelDeltaCarryY;
            var accumulated = carry + value.Value;
            if (accumulated > int.MaxValue)
            {
                accumulated = int.MaxValue;
            }
            else if (accumulated < int.MinValue)
            {
                accumulated = int.MinValue;
            }

            var wholeDelta = (int)Math.Truncate(accumulated);
            var remainder = accumulated - wholeDelta;
            if (horizontal)
            {
                remoteControlWheelDeltaCarryX = remainder;
            }
            else
            {
                remoteControlWheelDeltaCarryY = remainder;
            }

            return wholeDelta;
        }
    }

    private static RemoteKeyModifiers BuildRemoteKeyModifiers(ControlInputMessageV1 message)
    {
        var modifiers = RemoteKeyModifiers.None;
        if (message.Shift == true)
        {
            modifiers |= RemoteKeyModifiers.Shift;
        }

        if (message.Ctrl == true)
        {
            modifiers |= RemoteKeyModifiers.Ctrl;
        }

        if (message.Alt == true)
        {
            modifiers |= RemoteKeyModifiers.Alt;
        }

        if (message.Meta == true)
        {
            modifiers |= RemoteKeyModifiers.Meta;
        }

        return modifiers;
    }

    private bool ShouldQuietlyRecoverHelpeeHostStartFailure(TransportFailure failure)
    {
        if (role != SessionRuntimeRole.Helpee)
        {
            return false;
        }

        // Host startup can fail while a previous disconnect/reset/rehost callback is still
        // settling UI state. Keep helpee recovery quiet unless a real interactive session is
        // in progress or approval is currently pending.
        if (state is SessionRuntimeState.Connected or SessionRuntimeState.IncomingJoinRequest)
        {
            return false;
        }

        return failure.IsTransient || failure.Category is
            TransportFailureCategory.BridgeUnresponsive or
            TransportFailureCategory.UnexpectedProcessExit or
            TransportFailureCategory.BridgeCrashed or
            TransportFailureCategory.UserCancelled;
    }

    private bool TryScheduleQuietHelpeeRehost(string reason)
    {
        if (Interlocked.Exchange(ref quietHelpeeRehostInProgress, 1) != 0)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "Session",
            $"event={reason}; role=Helpee; host_mode=address_native; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

        ActiveRuntimeCounters.IncWatchdogs();
        RunCountedBackgroundTask(async () =>
        {
            try
            {
                await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
                await StartHelpeeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // If quiet recovery fails, the normal start path / subsequent disconnects will surface UI state.
            }
            finally
            {
                Interlocked.Exchange(ref quietHelpeeRehostInProgress, 0);
            }
        });

        return true;
    }

    private void OnBridgeLifecycle(object? sender, BridgeLifecycleEvent e)
    {
        if (!IsKnownBridgeEventSender(sender))
        {
            return;
        }

        if (ReferenceEquals(sender, transport) &&
            e.Kind == BridgeLifecycleEventKind.Ready &&
            transportState == TransportState.BridgeStarting)
        {
            TransitionTo(TransportState.BridgeReady, "bridge_ready");
            if (!IsPassiveHelperListenerState() &&
                role is SessionRuntimeRole.Helper or SessionRuntimeRole.Helpee &&
                transport is NknSignalingTransport &&
                transportState == TransportState.BridgeReady)
            {
                TransitionTo(TransportState.Connecting, "bridge_ready");
            }
        }

        var eventName = e.Kind switch
        {
            BridgeLifecycleEventKind.Spawned => "bridge_spawned",
            BridgeLifecycleEventKind.Ready => "bridge_ready",
            BridgeLifecycleEventKind.Exited => "bridge_exited",
            _ => "bridge_unknown"
        };

        var startMode = e.StartMode?.ToString().ToLowerInvariant() ?? string.Empty;
        var exitReason = e.ExitReasonKind?.ToString().ToLowerInvariant() ?? (string.IsNullOrWhiteSpace(e.ExitReasonText) ? string.Empty : e.ExitReasonText);
        var transportKind = "NKN";
        var sessionIdForLog = GetSessionIdForLog();

        LocalOperationalLog.Info(
            "Session",
            $"event={eventName}; start_mode={(string.IsNullOrWhiteSpace(startMode) ? "(none)" : startMode)}; pid={(e.Pid?.ToString() ?? "(none)")}; ready_time_ms={(e.ReadyTimeMs?.ToString("F2") ?? "(none)")}; ping_rtt_ms={(e.PingRttMs?.ToString("F2") ?? "(none)")}; uptime_ms={(e.UptimeMs?.ToString("F2") ?? "(none)")}; exit_code={(e.ExitCode?.ToString() ?? "(none)")}; exit_reason={(string.IsNullOrWhiteSpace(exitReason) ? "(none)" : exitReason)}; attempt={connectAttempt}; transport={transportKind}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={sessionIdForLog}; scenario={GetScenarioForLog()}");

        if (e.Kind == BridgeLifecycleEventKind.Ready &&
            e.StartMode == BridgeStartMode.Cold &&
            e.ReadyTimeMs.HasValue)
        {
            NknRuntimeDiagnostics.RecordFirstColdStart(e.ReadyTimeMs.Value, DateTimeOffset.UtcNow);
        }

        telemetrySink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            eventName,
            startMode,
            e.Pid,
            e.ReadyTimeMs,
            e.PingRttMs,
            e.UptimeMs,
            e.ExitCode,
            exitReason,
            GetRunIdForLog(),
            GetScenarioForTelemetry(),
            GetBridgeReuseModeForTelemetry(),
            connectAttempt,
            transportKind,
            sessionIdForLog));
    }

    private void OnChatMessageReceived(object? sender, ChatMessageEventArgs e)
    {
        if (!RequireCapability(SessionCapability.Chat, "chat_receive"))
        {
            return;
        }

        ChatMessageReceived?.Invoke(this, e);
    }

    private void OnChatMessageReceivedBeforeApproved(object? sender, EventArgs e)
    {
        if (!RequireCapability(SessionCapability.Chat, "chat_receive_notice"))
        {
            return;
        }

        ChatMessageReceivedBeforeApproved?.Invoke(this, EventArgs.Empty);
    }

    private void OnChatStateChanged(object? sender, EventArgs e)
    {
        ChatStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFileTransferChanged(object? sender, SessionFileTransferSnapshotChangedEventArgs e)
    {
        var screenShareActive = IsSessionScreenShareActive();
        fileTransferHost.LogSnapshot(e.Snapshot);
        fileTransferService.SetSessionScreenShareDegraded(
            screenShareActive &&
            !string.Equals(transportScreenShareCoordinator.GetMetricsSnapshot().FreshnessMode, "normal", StringComparison.Ordinal));
        var mixedV4TransferActive = screenShareActive && fileTransferService.IsV4MixedScreenShareTransferActive;
        transportScreenShareCoordinator.SetFileTransferDegradedHint(
            screenShareActive && (fileTransferService.IsTransferDegraded || mixedV4TransferActive));
        transportScreenShareCoordinator.SetFileTransferCatchUpOnlyHint(
            screenShareActive && (fileTransferService.IsCatchUpOnlyPressureActive || mixedV4TransferActive));
        FileTransferChanged?.Invoke(this, e);
    }

    private void OnScreenShareSenderDegradedModeChanged(object? sender, ScreenShareSenderDegradedModeChangedEventArgs e)
    {
        try
        {
            fileTransferService.SetSessionScreenShareDegraded(IsSessionScreenShareActive() && e.IsActive);
            if (transport is IScreenShareTransportPolicyController policyController)
            {
                RunCountedBackgroundTask(
                    async () =>
                    {
                        try
                        {
                            await policyController.SetScreenShareTransportCatchUpOnlyAsync(
                                active: IsSessionScreenShareActive() && e.IsActive,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogRemoteControlInfo("screenshare_transport_policy_update_failed", ex.GetType().Name, null, null);
                        }
                    },
                    countAsTransportTask: false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AttachFileTransferTransport(ISignalingTransport nextTransport)
    {
        fileTransferHost.AttachTransport(nextTransport);
    }

    private void QueueDetachFileTransferTransport()
    {
        fileTransferHost.QueueDetachTransport();
    }

    private static FileTransferStoragePolicy CreateInboundFileTransferStoragePolicy(FileTransferIncomingOffer offer)
    {
        return new FileTransferStoragePolicy(GetDefaultInboundFileTransferRootDirectory());
    }

    internal static string GetDefaultInboundFileTransferRootDirectory()
    {
        if (OperatingSystem.IsWindows() &&
            TryGetWindowsKnownFolderPath(WindowsDownloadsKnownFolderId, out var downloadsPath))
        {
            return downloadsPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, "Downloads");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            localAppData,
            FileTransferAppDataDirectoryName,
            FileTransferTransfersDirectoryName,
            FileTransferIncomingDirectoryName);
    }

    private static bool TryGetWindowsKnownFolderPath(Guid folderId, out string path)
    {
        path = string.Empty;
        var nativePath = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out nativePath);
            if (hr != 0 || nativePath == IntPtr.Zero)
            {
                return false;
            }

            var value = Marshal.PtrToStringUni(nativePath);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            path = value;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (nativePath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(nativePath);
            }
        }
    }

    private static readonly Guid WindowsDownloadsKnownFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static string SanitizeFileTransferPathSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    internal static bool IsTransportTransitionAllowed(TransportState from, TransportState to)
    {
        if (from == to)
        {
            return true;
        }

        if (from == TransportState.Disposed)
        {
            return false;
        }

        if (to == TransportState.Disposed)
        {
            return true;
        }

        if (to == TransportState.Failed)
        {
            return true;
        }

        if (to == TransportState.Reconnecting)
        {
            return from is not TransportState.Idle;
        }

        return (from, to) switch
        {
            (TransportState.Idle, TransportState.TransportInitializing) => true,
            (TransportState.Idle, TransportState.Connected) => true,
            (TransportState.TransportInitializing, TransportState.BridgeStarting) => true,
            (TransportState.TransportInitializing, TransportState.Connecting) => true,
            (TransportState.BridgeStarting, TransportState.Handshake) => true,
            (TransportState.BridgeStarting, TransportState.Connected) => true,
            (TransportState.BridgeStarting, TransportState.BridgeReady) => true,
            (TransportState.BridgeReady, TransportState.Connecting) => true,
            (TransportState.Connecting, TransportState.Handshake) => true,
            (TransportState.Connecting, TransportState.Connected) => true,
            (TransportState.Handshake, TransportState.Connected) => true,
            (TransportState.Reconnecting, TransportState.Idle) => true,
            (TransportState.Failed, TransportState.Reconnecting) => true,
            (TransportState.Failed, TransportState.Idle) => true,
            (TransportState.Failed, TransportState.TransportInitializing) => true,
            (TransportState.Failed, TransportState.Connected) => true,
            _ => false
        };
    }

    [Conditional("DEBUG")]
    private static void ThrowInvalidTransportTransition(TransportState from, TransportState to, string reason)
    {
        throw new InvalidOperationException(
            $"Invalid transport transition: {from} -> {to} (reason={reason})");
    }

    private static string SanitizeDispatchExceptionMessage(string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "(none)"
            : message.Replace(';', ',').Trim();
    }

    private void TransitionTo(TransportState newState, string reason, Exception? ex = null)
    {
        transportLifecycle.TransitionTo(newState, reason, ex);
    }

    private void UpdateTransientStatusForTransportState(TransportState stateValue)
    {
        transportLifecycle.UpdateTransientStatusForTransportState(stateValue);
    }

    private void SetTransientStatus(bool isVisible, string text, bool canCancel)
    {
        transportLifecycle.SetTransientStatus(isVisible, text, canCancel);
    }

    private void ShowHelperScreenChangedTransientStatus()
    {
        if (state != SessionRuntimeState.Connected)
        {
            return;
        }

        var statusText = role switch
        {
            SessionRuntimeRole.Helper => RemoteControlScreenChangedStatusTextHelper,
            SessionRuntimeRole.Helpee => RemoteControlScreenChangedStatusTextHelpee,
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(statusText))
        {
            return;
        }

        CancelRemoteControlScreenChangedStatus();
        var cts = new CancellationTokenSource();
        remoteControlScreenChangedStatusCts = cts;
        SetTransientStatus(isVisible: true, text: statusText, canCancel: false);

        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await Task.Delay(RemoteControlScreenChangedStatusDuration, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (disposed || !ReferenceEquals(remoteControlScreenChangedStatusCts, cts))
                    {
                        return;
                    }

                    if (transientStatusVisible &&
                        string.Equals(transientStatusText, statusText, StringComparison.Ordinal))
                    {
                        SetTransientStatus(isVisible: false, text: string.Empty, canCancel: false);
                    }

                    remoteControlScreenChangedStatusCts = null;
                }
                finally
                {
                    lifecycleGate.Release();
                    try
                    {
                        cts.Dispose();
                    }
                    catch
                    {
                        // Best-effort.
                    }
                }
            },
            countAsTransportTask: false);
    }

    private void CancelRemoteControlScreenChangedStatus()
    {
        if (remoteControlScreenChangedStatusCts is null)
        {
            return;
        }

        var cts = remoteControlScreenChangedStatusCts;
        remoteControlScreenChangedStatusCts = null;
        try
        {
            cts.Cancel();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private void ScheduleRemoteControlScreenShareStopGrace()
    {
        CancelRemoteControlScreenShareStopGrace("screenshare_stopped_reschedule");
        RunCountedBackgroundTask(
            async () =>
            {
                string? requestId = null;
                string? controllerPeerId = null;
                var shouldSchedule = false;
                var shouldStopImmediately = false;

                await lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!disposed &&
                        role == SessionRuntimeRole.Helper &&
                        state == SessionRuntimeState.Connected &&
                        remoteControlSessionState.ControlState != ControlState.Off)
                    {
                        requestId = remoteControlSessionState.CurrentControlRequestId;
                        controllerPeerId = remoteControlSessionState.ControllerPeerId;
                        shouldSchedule = remoteControlSessionState.ControlState == ControlState.Active;
                        shouldStopImmediately = remoteControlSessionState.ControlState == ControlState.Requesting;
                    }
                }
                finally
                {
                    lifecycleGate.Release();
                }

                if (shouldStopImmediately)
                {
                    LogRemoteControlInfo(
                        "screenshare_stop_pending_request",
                        "stopping_remote_control_immediately",
                        requestId,
                        controllerPeerId);
                    await StopRemoteControlAsync("screenshare_stopped_pending_request", CancellationToken.None).ConfigureAwait(false);

                    await lifecycleGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        ClearRemoteControlDisplayInfo("screenshare_stopped", notifyStateChanged: true);
                    }
                    finally
                    {
                        lifecycleGate.Release();
                    }
                    return;
                }

                if (!shouldSchedule)
                {
                    await lifecycleGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        ClearRemoteControlDisplayInfo("screenshare_stopped", notifyStateChanged: true);
                    }
                    finally
                    {
                        lifecycleGate.Release();
                    }
                    return;
                }

                var cts = new CancellationTokenSource();
                remoteControlScreenShareStopGraceCts = cts;
                LogRemoteControlInfo(
                    "screenshare_stop_deferred",
                    $"grace_ms={RemoteControlScreenShareStopGracePeriod.TotalMilliseconds:F0}",
                    requestId,
                    controllerPeerId);

                try
                {
                    await Task.Delay(RemoteControlScreenShareStopGracePeriod, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool shouldStop;
                string? stopRequestId = null;
                string? stopControllerPeerId = null;

                await lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (disposed || !ReferenceEquals(remoteControlScreenShareStopGraceCts, cts))
                    {
                        return;
                    }

                    remoteControlScreenShareStopGraceCts = null;
                    shouldStop = role == SessionRuntimeRole.Helper &&
                                 state == SessionRuntimeState.Connected &&
                                 remoteControlSessionState.ControlState != ControlState.Off;
                    if (shouldStop)
                    {
                        stopRequestId = remoteControlSessionState.CurrentControlRequestId;
                        stopControllerPeerId = remoteControlSessionState.ControllerPeerId;
                    }
                }
                finally
                {
                    lifecycleGate.Release();
                }

                if (shouldStop)
                {
                    LogRemoteControlInfo(
                        "screenshare_stop_grace_elapsed",
                        "stopping_remote_control",
                        stopRequestId,
                        stopControllerPeerId);
                    await StopRemoteControlAsync("screenshare_stopped_grace_timeout", CancellationToken.None).ConfigureAwait(false);
                }

                await lifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ClearRemoteControlDisplayInfo("screenshare_stopped", notifyStateChanged: true);
                }
                finally
                {
                    lifecycleGate.Release();
                }
            },
            countAsTransportTask: false);
    }

    private void CancelRemoteControlScreenShareStopGrace(string reason)
    {
        var cts = remoteControlScreenShareStopGraceCts;
        if (cts is null)
        {
            return;
        }

        remoteControlScreenShareStopGraceCts = null;
        try
        {
            cts.Cancel();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }

        LogRemoteControlInfo("screenshare_stop_deferred_cancelled", reason);
    }

    private void RaiseTransientStatusChanged()
    {
        transportLifecycle.RaiseTransientStatusChanged();
    }

    internal bool TryTransitionTransportStateForTests(TransportState newState, string reason)
    {
        if (!IsTransportTransitionAllowed(transportState, newState))
        {
            return false;
        }

        TransitionTo(newState, reason);
        return true;
    }

    private void HandleTimingAfterStateChange(TransportState newState)
    {
        transportLifecycle.HandleTimingAfterStateChange(newState);
    }

    private void UpdateWatchdogForState(TransportState newState, string reason)
    {
        transportLifecycle.UpdateWatchdogForState(newState, reason);
    }

    private TimeSpan? GetWatchdogTimeout(TransportState state, string reason)
    {
        return transportLifecycle.GetWatchdogTimeout(state, reason);
    }

    private void CancelWatchdog()
    {
        transportLifecycle.CancelWatchdog();
    }

    private static async Task<bool> TryPingBridgeForExternalRecoveryAsync(
        ISignalingTransport? activeTransport,
        ISignalingTransport? cachedTransport,
        CancellationToken ct)
    {
        static async Task<bool> TryPingAsync(ISignalingTransport? candidate, CancellationToken token)
        {
            if (candidate is not NknSignalingTransport nknTransport)
            {
                return false;
            }

            try
            {
                return await nknTransport.TryPingBridgeHealthAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        if (await TryPingAsync(activeTransport, ct).ConfigureAwait(false))
        {
            return true;
        }

        if (!ReferenceEquals(activeTransport, cachedTransport))
        {
            return await TryPingAsync(cachedTransport, ct).ConfigureAwait(false);
        }

        return false;
    }

    private void StartCachedBridgeIdleTimeout()
    {
        transportLifecycle.StartCachedBridgeIdleTimeout();
    }

    private void CancelCachedBridgeIdleTimeout()
    {
        transportLifecycle.CancelCachedBridgeIdleTimeout();
    }

    private async Task HandleCachedBridgeIdleTimeoutAsync(long generation)
    {
        await transportLifecycle.HandleCachedBridgeIdleTimeoutAsync(generation).ConfigureAwait(false);
    }

    private async Task HandleWatchdogTimeoutAsync(
        TransportState expectedState,
        long generation,
        long expectedAttempt,
        TimeSpan timeout)
    {
        bool shouldAutoRetry = false;
        bool shouldReturnHelperListenerToWaiting = false;
        TransportFailure? watchdogFailure = null;
        string? watchdogTimeoutMessage = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || resetInProgress)
            {
                return;
            }

            if (generation != Volatile.Read(ref watchdogGeneration))
            {
                return;
            }

            if (transportState != expectedState || connectAttempt != expectedAttempt)
            {
                return;
            }

            // Helpee waiting for a helper is intentionally long-lived. If a connecting
            // watchdog was armed due to transition ordering/race, suppress the false
            // timeout instead of surfacing "Connection lost.".
            if (expectedState == TransportState.Connecting &&
                role == SessionRuntimeRole.Helpee &&
                state == SessionRuntimeState.Waiting)
            {
                return;
            }

            if (expectedState == TransportState.Connecting &&
                IsPassiveHelperListenerState())
            {
                return;
            }

            var failure = CreateWatchdogFailure(expectedState, timeout);
            watchdogFailure = failure;
            watchdogTimeoutMessage = GetWatchdogUserMessage(expectedState);
            LocalOperationalLog.Error(
                "Session",
                $"event=transport_watchdog_timeout; state={expectedState}; timeout_ms={timeout.TotalMilliseconds:F0}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

            pendingJoinRequest = null;
            shouldReturnHelperListenerToWaiting =
                expectedState == TransportState.Handshake &&
                role == SessionRuntimeRole.Helper &&
                ShouldReturnHelperListenerToWaitingForCurrentAttempt();

            if (shouldReturnHelperListenerToWaiting)
            {
                BeginHelperListenerWaitingRecovery(
                    "watchdog_timeout",
                    watchdogTimeoutMessage ?? UserErrorMapper.HelperApprovalTimeout(),
                    failure);
            }
            else
            {
                TransitionTo(TransportState.Failed, "watchdog_timeout");
                SetState(SessionRuntimeState.Failed, watchdogTimeoutMessage ?? GetWatchdogUserMessage(expectedState));
                LogTransportFailure(failure, "watchdog_timeout");
            }

            if (!shouldReturnHelperListenerToWaiting &&
                watchdogOptions.AutoRetryEnabled &&
                role != SessionRuntimeRole.None &&
                (role == SessionRuntimeRole.Helpee || currentHelperTargetAddress is not null))
            {
                shouldAutoRetry = true;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (shouldReturnHelperListenerToWaiting)
        {
            TryScheduleQuietHelperListenerRestart("helper_watchdog_timeout_return_to_listener_waiting");
            return;
        }

        if (shouldAutoRetry)
        {
            try
            {
                LocalOperationalLog.Info(
                    "Session",
                    $"event=transport_watchdog_retry_requested; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
                await watchdogRetryPolicy.ExecuteAsync(
                    async (_, retryCt) =>
                    {
                        retryCt.ThrowIfCancellationRequested();
                        await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
                    },
                    resetBetweenAttemptsAsync: null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; leave Failed state if reset fails.
            }
        }
    }

    private void OnWatchdogRetryPolicyEvent(object? sender, RetryEvent e)
    {
        var kind = e.Kind switch
        {
            RetryEventKind.AttemptStart => "retry_attempt_start",
            RetryEventKind.AttemptScheduled => "retry_attempt_scheduled",
            RetryEventKind.AttemptSuccess => "retry_attempt_success",
            RetryEventKind.FinalFail => "retry_attempt_final_fail",
            _ => "retry_attempt"
        };

        LocalOperationalLog.Info(
            "Session",
            $"event={kind}; retry_attempt={e.Attempt}; retry_max_attempts={e.MaxAttempts}; delay_ms={(e.Delay?.TotalMilliseconds.ToString("F0") ?? "(none)")}; reason={(string.IsNullOrWhiteSpace(e.Reason) ? "(none)" : e.Reason)}; ex={(string.IsNullOrWhiteSpace(e.ExceptionType) ? "(none)" : e.ExceptionType)}; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

        switch (e.Kind)
        {
            case RetryEventKind.AttemptStart:
                SetTransientStatus(true, $"Reconnecting… (attempt {e.Attempt})", canCancel: true);
                break;
            case RetryEventKind.AttemptScheduled:
                var nextSeconds = Math.Max(1, (int)Math.Ceiling((e.Delay ?? TimeSpan.Zero).TotalSeconds));
                SetTransientStatus(true, $"Reconnecting… (attempt {e.Attempt}, next retry in {nextSeconds}s)", canCancel: true);
                break;
            case RetryEventKind.AttemptSuccess:
                SetTransientStatus(true, "Reconnecting…", canCancel: true);
                break;
            case RetryEventKind.FinalFail:
                SetTransientStatus(false, string.Empty, canCancel: false);
                break;
        }
    }

    private TransportFailure CreateWatchdogFailure(TransportState timedOutState, TimeSpan timeout)
    {
        var timeoutMs = timeout.TotalMilliseconds.ToString("F0");
        return timedOutState switch
        {
            TransportState.BridgeStarting => TransportFailure.Create(
                TransportFailureCategory.BridgeStartFailure,
                $"Bridge start timed out after {timeoutMs} ms.",
                rawError: "bridge_start_timeout",
                isTransient: true),
            TransportState.Connecting => TransportFailure.Create(
                TransportFailureCategory.PeerUnreachable,
                $"Connect timed out after {timeoutMs} ms.",
                rawError: "connect_timeout",
                isTransient: true),
            TransportState.Handshake => TransportFailure.Create(
                TransportFailureCategory.HandshakeTimeout,
                $"Handshake timed out after {timeoutMs} ms.",
                rawError: "handshake_timeout",
                isTransient: true),
            TransportState.Reconnecting => TransportFailure.Create(
                TransportFailureCategory.Unknown,
                $"Reconnect timed out after {timeoutMs} ms.",
                rawError: "reconnect_timeout",
                isTransient: true),
            _ => TransportFailure.Create(
                TransportFailureCategory.Unknown,
                $"State watchdog timed out after {timeoutMs} ms.",
                rawError: "watchdog_timeout",
                isTransient: true),
        };
    }

    private string GetWatchdogUserMessage(TransportState timedOutState)
    {
        return timedOutState switch
        {
            TransportState.BridgeStarting => "Please reinstall.",
            TransportState.Handshake => "No response yet.",
            TransportState.Connecting when role == SessionRuntimeRole.Helper => "No response from target address.",
            _ => "Connection lost.",
        };
    }

    private void BeginConnectAttempt(SessionRuntimeRole nextRole, string connectTargetKey)
    {
        lastDisconnectWasRemoteEnd = false;
        allowTransportScreenShareAutoStart = true;
        if (remoteControlSessionState.ControlState != ControlState.Off)
        {
            MarkRemoteControlStopPriority(
                "begin_connect_attempt",
                remoteControlSessionState.CurrentControlRequestId,
                remoteControlSessionState.ControllerPeerId);
        }
        ResetRemoteControlState("begin_connect_attempt");
        MarkActiveSession();
        MarkActiveConnectAttempt();

        var key = $"{nextRole}:{connectTargetKey}";
        if (!string.Equals(attemptSessionKey, key, StringComparison.Ordinal))
        {
            attemptSessionKey = key;
            connectAttempt = 1;
            sessionId = Guid.NewGuid().ToString("N")[..8];
        }
        else
        {
            connectAttempt++;
        }

        lastTransportFailure = null;
        bridgeStartTiming = default;
        transportInitTiming = default;
        connectTiming = default;
        handshakeTiming = default;
        reconnectTiming = default;
    }

    private void HandleTimingBeforeStateChange(
        TransportState previous,
        TransportState next,
        string reason,
        Exception? ex)
    {
        transportLifecycle.HandleTimingBeforeStateChange(previous, next, reason, ex);
    }

    private void CompleteDurationMetric(
        string metricName,
        string eventName,
        TimingSpan timing,
        string reason,
        Exception? ex,
        bool failed)
    {
        transportLifecycle.CompleteDurationMetric(metricName, eventName, timing, reason, ex, failed);
    }

    private string GetCurrentTransportKind()
    {
        return transportLifecycle.GetCurrentTransportKind();
    }

    private void RefreshRemoteControlCapabilitiesFromTransport()
    {
        var localSupports = true;
        var remoteSupports = false;
        var grantAllowsRemoteControl = CanPerform(SessionCapability.RemoteControl);

        if (transport is IRemoteControlCapabilityProvider provider)
        {
            localSupports = provider.LocalSupportsRemoteControl && grantAllowsRemoteControl;
            remoteSupports = provider.RemoteSupportsRemoteControl && grantAllowsRemoteControl;
        }

        var transition = RemoteControlCoordinator.Apply(
            remoteControlSessionState,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.SyncCapabilities,
                Reason: "refresh_capabilities",
                SupportsRemoteControl: localSupports,
                PeerSupportsRemoteControl: remoteSupports));
        remoteControlSessionState = transition.NextState;

        if ((transition.SideEffects & RemoteControlCoordinatorSideEffect.CapabilitiesChanged) != 0)
        {
            NotifyRemoteControlStateChanged();
        }

        if (!remoteControlSessionState.SessionSupportsRemoteControl)
        {
            ClearRemoteControlDisplayInfo("capability_lost", notifyStateChanged: true);
        }

        if (!remoteControlSessionState.SessionSupportsRemoteControl &&
            remoteControlSessionState.ControlState != ControlState.Off)
        {
            EnsureRemoteControlStoppedForAuthorizationLoss("capability_lost");
        }

        SyncTransportScreenShareCursorCaptureForRemoteControl("refresh_capabilities");
    }

    private static void EnsureSessionSecurityTransport(ISignalingTransport signalingTransport)
    {
        ArgumentNullException.ThrowIfNull(signalingTransport);
        if (signalingTransport is ISessionSecuritySignalingTransport)
        {
            return;
        }

        throw new NotSupportedException("SessionRuntime requires a security-capable signaling transport.");
    }

    private static string GetTransportNameForLog(object? sender)
    {
        return sender?.GetType().Name ?? "(none)";
    }

    private void ResetRemoteControlState(string reason)
    {
        Interlocked.Increment(ref remoteControlStopPriorityEpoch);
        StopHelperRemoteScreenSharePressureTimer();
        CancelRemoteControlRequestTimeout();
        CancelRemoteControlConsentTimeout();
        CancelRemoteControlDeniedCooldown();
        CancelRemoteControlScreenChangedStatus();
        CancelRemoteControlScreenShareStopGrace("reset_remote_control_state");
        ClearQueuedRemoteControlMouseMoves("reset:" + reason);
        ClearQueuedRemoteControlInjections("reset:" + reason);
        Interlocked.Exchange(ref remoteControlInputSequence, 0);
        Interlocked.Exchange(ref lastRemoteControlInjectedSeq, 0);
        Interlocked.Exchange(ref lastRemoteControlAckSentSeq, 0);
        Interlocked.Exchange(ref lastRemoteControlAckSentTick, 0);
        Interlocked.Exchange(ref remoteControlAckSentCount, 0);
        Interlocked.Exchange(ref helperRemoteControlLastAckSeq, 0);
        Interlocked.Exchange(ref helperRemoteControlLastAckAdvanceTick, 0);
        Interlocked.Exchange(ref helperRemoteControlLastInputSentTick, 0);
        Interlocked.Exchange(ref helperRemoteControlAckStallDetectedCount, 0);
        Interlocked.Exchange(ref helperRemoteControlStallRecoveryLastTick, 0);
        Interlocked.Exchange(ref helperRemoteControlStallRecoverySentCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotReceivedCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotAppliedCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotUnstuckButtonsCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotUnstuckModifiersCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedSeq, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedModifiersMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedMouseButtonsMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedSeq, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedModifiersMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedMouseButtonsMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotContinuousStartTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpLeftTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpRightTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpMiddleTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpX1Tick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpX2Tick, 0);
        remoteScreenShareFramesSuppressedUntilUtc = default;
        Interlocked.Exchange(ref remoteScreenShareSuppressFramesCapturedBeforeOrAtUtcMs, 0);
        screenShareControlHost.ResetRemoteScreenShareActivity();
        lastSentScreenSharePressureMode = ScreenSharePressureMode.Normal;
        lastSentScreenSharePressureReason = ScreenSharePressureProtocol.PressureReasonHealthy;
        lastSentScreenSharePressureAgeMs = 0;
        lastSentScreenSharePressureStaleDrops = 0;
        lastSentScreenSharePressureUtc = default;
        lastSentScreenSharePressureModeEnteredUtc = default;
        lastObservedRemoteScreenShareStaleDrops = 0;
        healthyScreenSharePressureIntervals = 0;
        ResetHelperRemoteScreenSharePressureTracking();
        ResetRemoteControlWheelDeltaCarry();
        ResetRemoteControlDebugLastMapped();
        Interlocked.Exchange(ref remoteControlForceNextMoveInjectionLog, 0);
        ClearRemoteControlAppliedInputState("reset:" + reason);
        hasPendingRemoteControlConsentPrompt = false;
        ClearPendingRemoteControlConsentToken();
        ClearRemoteControlRevisionMismatchCache();
        remoteControlCoordinatorDisplayInfoState = RemoteControlDisplayInfoState.Empty;
        var transition = RemoteControlCoordinator.Apply(
            remoteControlSessionState,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.Reset,
                reason));
        remoteControlSessionState = transition.NextState;
        UpdateRemoteControlStatusHint(reason, remoteControlSessionState.ControlState);
        SyncTransportScreenShareCursorCaptureForRemoteControl(reason);

        LogRemoteControlTransition(transition.PreviousState, remoteControlSessionState, reason);
        NotifyRemoteControlStateChanged();
    }

    private void SyncTransportScreenShareCursorCaptureForRemoteControl(string? reason)
    {
        if (role != SessionRuntimeRole.Helpee)
        {
            return;
        }

        var passiveOverlayActive = ShouldUsePassiveScreenShareCursorOverlayForTransport();
        var shouldEnableCapturedCursor =
            remoteControlSessionState.ControlState != ControlState.Active &&
            !passiveOverlayActive;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "remote_control_state"
            : reason.Trim();
        var applied = transportScreenShareCoordinator.TrySetCapturedCursorEnabledForRemoteControl(
            shouldEnableCapturedCursor,
            normalizedReason);
        LocalOperationalLog.Info(
            "RemoteControl",
            $"event=screen_share_cursor_capture_sync; role={role}; state={remoteControlSessionState.ControlState}; passive_cursor_overlay_active={(passiveOverlayActive ? 1 : 0)}; captured_cursor_enabled={(shouldEnableCapturedCursor ? 1 : 0)}; applied={(applied ? 1 : 0)}; reason={normalizedReason}");
    }

    private bool ShouldUsePassiveScreenShareCursorOverlayForTransport()
    {
        return role == SessionRuntimeRole.Helpee &&
               state == SessionRuntimeState.Connected &&
               remoteControlSessionState.ControlState != ControlState.Active &&
               FeatureFlags.EnableScreenShareTransport &&
               FeatureFlags.EnableScreenShareCapture &&
               CanPerform(SessionCapability.ScreenShare) &&
               transport is IScreenShareCursorOverlayCapabilityProvider cursorOverlayProvider &&
               cursorOverlayProvider.SessionSupportsScreenShareCursorOverlay;
    }

    private void ResetRemoteControlRequestScopedTracking(string reason)
    {
        ClearQueuedRemoteControlMouseMoves("request_scope_reset:" + reason);
        ClearQueuedRemoteControlInjections("request_scope_reset:" + reason);
        Interlocked.Exchange(ref remoteControlInputSequence, 0);
        Interlocked.Exchange(ref lastRemoteControlInjectedSeq, 0);
        Interlocked.Exchange(ref lastRemoteControlAckSentSeq, 0);
        Interlocked.Exchange(ref lastRemoteControlAckSentTick, 0);
        Interlocked.Exchange(ref remoteControlAckSentCount, 0);
        Interlocked.Exchange(ref helperRemoteControlLastAckSeq, 0);
        Interlocked.Exchange(ref helperRemoteControlLastAckAdvanceTick, 0);
        Interlocked.Exchange(ref helperRemoteControlLastInputSentTick, 0);
        Interlocked.Exchange(ref helperRemoteControlAckStallDetectedCount, 0);
        Interlocked.Exchange(ref helperRemoteControlStallRecoveryLastTick, 0);
        Interlocked.Exchange(ref helperRemoteControlStallRecoverySentCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotReceivedCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotAppliedCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotUnstuckButtonsCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotUnstuckModifiersCount, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedSeq, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedModifiersMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedMouseButtonsMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedSeq, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedModifiersMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedMouseButtonsMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastReceivedTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotContinuousStartTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpLeftTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpRightTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpMiddleTick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpX1Tick, 0);
        Interlocked.Exchange(ref remoteControlSnapshotForcedUpX2Tick, 0);
        ClearRemoteControlAppliedInputState("request_scope_reset:" + reason);
        if (role == SessionRuntimeRole.Helper)
        {
            ClearRemoteControlDisplayInfo("request_scope_reset:" + reason, notifyStateChanged: false);
        }
        ResetRemoteControlWheelDeltaCarry();
        ResetRemoteControlDebugLastMapped();
        PublishRemoteControlDebugDiagnostics();
    }

    private void ClearRemoteControlAppliedInputState(string reason)
    {
        var releasedButtons = 0L;
        var releasedModifiers = 0L;
        if (role == SessionRuntimeRole.Helpee &&
            remoteInputInjector.IsSupported)
        {
            releasedButtons = ReleaseTrackedRemoteControlMouseButtons(remoteControlAppliedMouseButtonsMask);
            releasedModifiers = ReleaseStuckRemoteControlModifiers(remoteControlAppliedModifiersMask);
        }

        remoteControlAppliedMouseButtonsMask = RemoteControlMouseButtonsMask.None;
        remoteControlAppliedModifiersMask = RemoteControlModifiersMask.None;
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedSeq, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedModifiersMask, 0);
        Interlocked.Exchange(ref remoteControlSnapshotLastAppliedMouseButtonsMask, 0);
        if (ShouldEmitRemoteControlRateLimitedLog("applied_input_state_cleared", TimeSpan.FromSeconds(2)))
        {
            LogRemoteControlInfo(
                "applied_input_state_cleared",
                $"{reason}; released_buttons={releasedButtons.ToString(CultureInfo.InvariantCulture)}; released_modifiers={releasedModifiers.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private long ReleaseTrackedRemoteControlMouseButtons(RemoteControlMouseButtonsMask buttonsToRelease)
    {
        if (buttonsToRelease == RemoteControlMouseButtonsMask.None)
        {
            return 0;
        }

        long releasedCount = 0;
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.Left, RemoteMouseButton.Left);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.Right, RemoteMouseButton.Right);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.Middle, RemoteMouseButton.Middle);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.X1, RemoteMouseButton.X1);
        ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask.X2, RemoteMouseButton.X2);
        return releasedCount;

        void ReleaseMouseButtonIfNeeded(RemoteControlMouseButtonsMask mask, RemoteMouseButton button)
        {
            if ((buttonsToRelease & mask) == 0)
            {
                return;
            }

            remoteInputInjector.InjectMouseButton(button, RemoteButtonAction.Up);
            releasedCount++;
        }
    }

    private static string GenerateRemoteControlConsentToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static byte[] ComputeRemoteControlConsentTokenHash(string requestId, string controllerPeerId, string token)
    {
        var normalizedRequestId = requestId?.Trim() ?? string.Empty;
        var normalizedControllerPeerId = controllerPeerId?.Trim() ?? string.Empty;
        var normalizedToken = token?.Trim() ?? string.Empty;
        var material = string.Concat(
            normalizedRequestId,
            "\n",
            normalizedControllerPeerId,
            "\n",
            normalizedToken);
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    private bool TryValidateRemoteControlStartToken(
        string requestId,
        string? senderPeerId,
        string? consentToken,
        out string reason)
    {
        reason = string.Empty;
        var pending = pendingRemoteControlConsentToken;
        if (pending is null)
        {
            reason = "token_missing";
            LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "rejected");
            return false;
        }

        if (pending.IsUsed)
        {
            reason = "token_reused";
            LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "rejected");
            return false;
        }

        if (!string.Equals(pending.RequestId, requestId, StringComparison.Ordinal))
        {
            reason = "request_mismatch";
            LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "rejected");
            return false;
        }

        if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            reason = "token_expired";
            LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "expired");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pending.ControllerPeerId) &&
            !string.Equals(pending.ControllerPeerId, senderPeerId, StringComparison.Ordinal))
        {
            reason = "peer_mismatch";
            LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "rejected");
            return false;
        }

        if (string.IsNullOrWhiteSpace(consentToken))
        {
            reason = "token_missing";
            LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "rejected");
            return false;
        }

        var providedHash = ComputeRemoteControlConsentTokenHash(
            requestId,
            pending.ControllerPeerId,
            consentToken);

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(pending.TokenHash, providedHash))
            {
                reason = "token_mismatch";
                LogRemoteControlViolation("token_validate", reason, requestId, senderPeerId, tokenDecision: "rejected");
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(providedHash);
        }

        LogRemoteControlInfo("token_validate", "ok", requestId, senderPeerId, tokenDecision: "accepted");
        return true;
    }

    private void ClearPendingRemoteControlConsentToken()
    {
        if (pendingRemoteControlConsentToken is null)
        {
            return;
        }

        try
        {
            CryptographicOperations.ZeroMemory(pendingRemoteControlConsentToken.TokenHash);
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            pendingRemoteControlConsentToken = null;
        }
    }

    private void StartRemoteControlRequestTimeout(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var deadlineUnixMs = nowProvider()
            .Add(RemoteControlRequestTimeout)
            .ToUnixTimeMilliseconds();
        ScheduleRemoteControlTimeout(
            RemoteControlReducerTimeoutKind.Request,
            requestId,
            deadlineUnixMs,
            "helper_request_timeout");
    }

    private void StartRemoteControlConsentTimeout(string requestId, long timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var timeoutKind = role switch
        {
            SessionRuntimeRole.Helper => RemoteControlReducerTimeoutKind.Request,
            SessionRuntimeRole.Helpee when hasPendingRemoteControlConsentPrompt => RemoteControlReducerTimeoutKind.ConsentDecision,
            _ => RemoteControlReducerTimeoutKind.StartAwait,
        };

        var fallbackTimeoutMs = ResolveRemoteControlTimeoutMs(timeoutKind);
        var effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : fallbackTimeoutMs;
        var deadlineUnixMs = nowProvider()
            .AddMilliseconds(effectiveTimeoutMs)
            .ToUnixTimeMilliseconds();
        var timeoutReason = timeoutKind switch
        {
            RemoteControlReducerTimeoutKind.Request => "helper_request_timeout",
            RemoteControlReducerTimeoutKind.ConsentDecision => "helpee_consent_timeout",
            RemoteControlReducerTimeoutKind.StartAwait => "helpee_start_timeout",
            _ => "remote_control_timeout",
        };

        ScheduleRemoteControlTimeout(timeoutKind, requestId, deadlineUnixMs, timeoutReason);
    }

    private void StartRemoteControlDeniedCooldown(string? requestId)
    {
        var deadlineUnixMs = nowProvider()
            .Add(RemoteControlDeniedCooldown)
            .ToUnixTimeMilliseconds();
        ScheduleRemoteControlTimeout(
            RemoteControlReducerTimeoutKind.DeniedCooldown,
            requestId,
            deadlineUnixMs,
            "denied_cooldown_elapsed");
    }

    private void ScheduleRemoteControlTimeout(
        RemoteControlReducerTimeoutKind timeoutKind,
        string? requestId,
        long deadlineUnixMs,
        string reason)
    {
        CancellationTokenSource cts;
        switch (timeoutKind)
        {
            case RemoteControlReducerTimeoutKind.Request:
                CancelRemoteControlRequestTimeout();
                cts = new CancellationTokenSource();
                remoteControlRequestTimeoutCts = cts;
                break;
            case RemoteControlReducerTimeoutKind.ConsentDecision:
            case RemoteControlReducerTimeoutKind.StartAwait:
                CancelRemoteControlConsentTimeout();
                cts = new CancellationTokenSource();
                remoteControlConsentTimeoutCts = cts;
                break;
            case RemoteControlReducerTimeoutKind.DeniedCooldown:
                CancelRemoteControlDeniedCooldown();
                cts = new CancellationTokenSource();
                remoteControlDeniedCooldownCts = cts;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(timeoutKind), timeoutKind, "Unknown remote control timeout kind.");
        }

        RunCountedBackgroundTask(
            () => RunRemoteControlTimeoutAsync(timeoutKind, requestId, deadlineUnixMs, reason, cts.Token),
            countAsTransportTask: false);
    }

    private async Task RunRemoteControlTimeoutAsync(
        RemoteControlReducerTimeoutKind timeoutKind,
        string? requestId,
        long deadlineUnixMs,
        string reason,
        CancellationToken ct)
    {
        var delayMs = deadlineUnixMs - nowProvider().ToUnixTimeMilliseconds();
        if (delayMs > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        else if (ct.IsCancellationRequested)
        {
            return;
        }

        await ApplyRemoteControlTimeoutAsync(timeoutKind, requestId, reason).ConfigureAwait(false);
    }

    private async Task ApplyRemoteControlTimeoutAsync(
        RemoteControlReducerTimeoutKind timeoutKind,
        string? requestId,
        string reason)
    {
        RemoteControlSideEffect? timeoutDenySideEffect = null;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            var currentRequestId = remoteControlSessionState.CurrentControlRequestId;
            if (!string.IsNullOrWhiteSpace(requestId) &&
                !string.Equals(currentRequestId, requestId, StringComparison.Ordinal))
            {
                return;
            }

            if (timeoutKind == RemoteControlReducerTimeoutKind.ConsentDecision &&
                role == SessionRuntimeRole.Helpee &&
                hasPendingRemoteControlConsentPrompt &&
                !string.IsNullOrWhiteSpace(currentRequestId))
            {
                timeoutDenySideEffect = new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlResponse,
                    "consent_timeout",
                    RequestId: currentRequestId,
                    PeerId: remoteControlSessionState.ControllerPeerId,
                    Decision: RemoteControlReducerResponseDecision.Deny);
            }

            ApplyRemoteControlReducerTransition(
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.SystemTimeout,
                    reason,
                    RequestId: requestId ?? currentRequestId,
                    TimeoutKind: timeoutKind,
                    TimeoutMs: (long)RemoteControlDeniedCooldown.TotalMilliseconds));
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (timeoutDenySideEffect.HasValue)
        {
            ExecuteRemoteControlReducerSideEffect(timeoutDenySideEffect.Value, timeoutDenySideEffect.Value.Reason ?? "consent_timeout");
        }
    }

    private static long ResolveRemoteControlTimeoutMs(
        RemoteControlReducerTimeoutKind timeoutKind,
        long? timeoutMsOverride = null)
    {
        if (timeoutMsOverride.HasValue && timeoutMsOverride.Value > 0)
        {
            return timeoutMsOverride.Value;
        }

        return timeoutKind switch
        {
            RemoteControlReducerTimeoutKind.Request => (long)RemoteControlRequestTimeout.TotalMilliseconds,
            RemoteControlReducerTimeoutKind.ConsentDecision => (long)RemoteControlConsentDecisionTimeout.TotalMilliseconds,
            RemoteControlReducerTimeoutKind.StartAwait => (long)RemoteControlStartAwaitTimeout.TotalMilliseconds,
            RemoteControlReducerTimeoutKind.DeniedCooldown => (long)RemoteControlDeniedCooldown.TotalMilliseconds,
            _ => (long)RemoteControlConsentDecisionTimeout.TotalMilliseconds,
        };
    }

    private void CancelRemoteControlRequestTimeout()
    {
        if (remoteControlRequestTimeoutCts is null)
        {
            return;
        }

        try
        {
            remoteControlRequestTimeoutCts.Cancel();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            remoteControlRequestTimeoutCts.Dispose();
            remoteControlRequestTimeoutCts = null;
        }
    }

    private void CancelRemoteControlConsentTimeout()
    {
        if (remoteControlConsentTimeoutCts is null)
        {
            return;
        }

        try
        {
            remoteControlConsentTimeoutCts.Cancel();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            remoteControlConsentTimeoutCts.Dispose();
            remoteControlConsentTimeoutCts = null;
        }
    }

    private void CancelRemoteControlDeniedCooldown()
    {
        if (remoteControlDeniedCooldownCts is null)
        {
            return;
        }

        try
        {
            remoteControlDeniedCooldownCts.Cancel();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            remoteControlDeniedCooldownCts.Dispose();
            remoteControlDeniedCooldownCts = null;
        }
    }

    private void NotifyRemoteControlStateChanged()
    {
        SyncFileTransferFlowControlMode();
        PublishRemoteControlDebugDiagnostics();
        RemoteControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [Conditional("DEBUG")]
    private void PublishRemoteControlDebugDiagnostics()
    {
        if (role is not (SessionRuntimeRole.Helper or SessionRuntimeRole.Helpee))
        {
            return;
        }

        var diagnosticsRole = role == SessionRuntimeRole.Helper
            ? RemoteControlDiagnosticsRole.Helper
            : RemoteControlDiagnosticsRole.Helpee;

        RemoteControlRectPx? captureRegion = null;
        RemoteControlSizePx? frameSize = null;
        var displayInfo = latestRemoteControlDisplayInfo;
        if (displayInfo is not null)
        {
            if (displayInfo.CaptureRegionWidth > 0 && displayInfo.CaptureRegionHeight > 0)
            {
                captureRegion = new RemoteControlRectPx(
                    displayInfo.CaptureRegionX,
                    displayInfo.CaptureRegionY,
                    displayInfo.CaptureRegionWidth,
                    displayInfo.CaptureRegionHeight);
            }

            if (displayInfo.FrameWidth > 0 && displayInfo.FrameHeight > 0)
            {
                frameSize = new RemoteControlSizePx(displayInfo.FrameWidth, displayInfo.FrameHeight);
            }
        }

        RemoteControlDebugDiagnostics.SetCommon(
            diagnosticsRole,
            remoteControlSessionState.ControlState,
            displayInfo?.DisplayId,
            displayInfo?.Revision,
            captureRegion,
            frameSize);

        if (diagnosticsRole == RemoteControlDiagnosticsRole.Helper)
        {
            var lastAckSeq = Interlocked.Read(ref helperRemoteControlLastAckSeq);
            var lastAckAdvanceTick = Interlocked.Read(ref helperRemoteControlLastAckAdvanceTick);
            long? lastAckAgeMs = null;
            if (lastAckAdvanceTick > 0)
            {
                lastAckAgeMs = (long)Math.Max(
                    0d,
                    Stopwatch.GetElapsedTime(lastAckAdvanceTick, Stopwatch.GetTimestamp()).TotalMilliseconds);
            }

            RemoteControlDebugDiagnostics.SetHelperAckRuntime(
                lastAckSeq: lastAckSeq,
                lastAckAgeMs: lastAckAgeMs,
                stallDetectedCount: Interlocked.Read(ref helperRemoteControlAckStallDetectedCount),
                stallRecoverySentCount: Interlocked.Read(ref helperRemoteControlStallRecoverySentCount));
            return;
        }

        RemoteControlDebugDiagnostics.SetHelpeeRuntime(
            injectionQueueSize: RemoteControlInjectionQueueDepth,
            suppressedInjections: RemoteControlDebugInjectionSuppressedCount,
            queueFlushes: RemoteControlDebugQueueFlushCount,
            lastInjectedSeq: Interlocked.Read(ref lastRemoteControlInjectedSeq),
            lastAckSentSeq: Interlocked.Read(ref lastRemoteControlAckSentSeq),
            ackSentCount: Interlocked.Read(ref remoteControlAckSentCount),
            snapshotReceivedCount: Interlocked.Read(ref remoteControlSnapshotReceivedCount),
            snapshotAppliedCount: Interlocked.Read(ref remoteControlSnapshotAppliedCount),
            snapshotUnstuckButtonsCount: Interlocked.Read(ref remoteControlSnapshotUnstuckButtonsCount),
            snapshotUnstuckModifiersCount: Interlocked.Read(ref remoteControlSnapshotUnstuckModifiersCount));
        RemoteControlDebugDiagnostics.SetHelpeeSnapshotRuntime(
            lastReceivedSeq: Interlocked.Read(ref remoteControlSnapshotLastReceivedSeq),
            lastReceivedModifiersMask: Interlocked.CompareExchange(ref remoteControlSnapshotLastReceivedModifiersMask, 0, 0),
            lastReceivedMouseButtonsMask: Interlocked.CompareExchange(ref remoteControlSnapshotLastReceivedMouseButtonsMask, 0, 0),
            lastAppliedSeq: Interlocked.Read(ref remoteControlSnapshotLastAppliedSeq),
            lastAppliedModifiersMask: Interlocked.CompareExchange(ref remoteControlSnapshotLastAppliedModifiersMask, 0, 0),
            lastAppliedMouseButtonsMask: Interlocked.CompareExchange(ref remoteControlSnapshotLastAppliedMouseButtonsMask, 0, 0));
        RemoteControlDebugDiagnostics.SetHelpeeGuardrailCounters(
            outOfRangeClamps: RemoteControlDebugMappingClampCount,
            droppedMouseMoves: RemoteControlDebugQueueDropCount,
            suppressedInjections: RemoteControlDebugInjectionSuppressedCount,
            queueFlushes: RemoteControlDebugQueueFlushCount);

        if (Interlocked.Read(ref remoteControlDebugLastMappedVersion) > 0)
        {
            var nx = BitConverter.Int64BitsToDouble(Interlocked.Read(ref remoteControlDebugLastMappedNxBits));
            var ny = BitConverter.Int64BitsToDouble(Interlocked.Read(ref remoteControlDebugLastMappedNyBits));
            var px = Interlocked.CompareExchange(ref remoteControlDebugLastMappedPx, 0, 0);
            var py = Interlocked.CompareExchange(ref remoteControlDebugLastMappedPy, 0, 0);
            RemoteControlDebugDiagnostics.SetHelpeeLastMapped(nx, ny, px, py);
        }
    }

    private long SnapshotRemoteControlStopPriorityEpoch()
    {
        return Interlocked.Read(ref remoteControlStopPriorityEpoch);
    }

    private bool HasRemoteControlStopPriorityChanged(long snapshot)
    {
        return Interlocked.Read(ref remoteControlStopPriorityEpoch) != snapshot;
    }

    private void MarkRemoteControlStopPriority(string reason, string? requestId, string? controllerPeerId)
    {
        Interlocked.Exchange(ref remoteControlStopInputSuppressionLatched, 1);
        var epoch = Interlocked.Increment(ref remoteControlStopPriorityEpoch);
        LogRemoteControlInfo("stop_priority_latched", reason, requestId, controllerPeerId, tokenDecision: $"epoch:{epoch}");
    }

    private bool ShouldEmitRemoteControlRateLimitedLog(string key, TimeSpan? window = null)
    {
        var effectiveWindow = window ?? RemoteControlLogRateLimitWindow;
        var nowTicks = Environment.TickCount64;
        var windowTicks = (long)Math.Max(1d, effectiveWindow.TotalMilliseconds);

        lock (remoteControlLogRateLimitGate)
        {
            if (remoteControlLogRateLimitTicks.TryGetValue(key, out var lastTicks) &&
                nowTicks - lastTicks < windowTicks)
            {
                return false;
            }

            remoteControlLogRateLimitTicks[key] = nowTicks;
            return true;
        }
    }

    private void MarkForceNextMoveInjectionLog(string reason)
    {
        _ = reason;
        Interlocked.Exchange(ref remoteControlForceNextMoveInjectionLog, 1);
    }

    private bool ShouldEmitRemoteInputMoveLog(bool isHighRateMove)
    {
        if (Interlocked.Exchange(ref remoteControlForceNextMoveInjectionLog, 0) == 1)
        {
            return true;
        }

        if (!isHighRateMove)
        {
            return true;
        }

        return ShouldEmitRemoteControlRateLimitedLog("input_remote_move", RemoteControlMoveInjectLogWindow);
    }

    private void LogRemoteInputMoveInjection(
        ControlInputMessageV1 message,
        string? controllerPeerId,
        double nx,
        double ny,
        int px,
        int py,
        bool isHighRateMove)
    {
        if (!ShouldEmitRemoteInputMoveLog(isHighRateMove))
        {
            return;
        }

        var effectiveRequestId = string.IsNullOrWhiteSpace(message.RequestId)
            ? remoteControlSessionState.CurrentControlRequestId ?? "(none)"
            : message.RequestId.Trim();
        var effectiveControllerPeerId = string.IsNullOrWhiteSpace(controllerPeerId)
            ? remoteControlSessionState.ControllerPeerId ?? "(none)"
            : controllerPeerId.Trim();
        var displayInfo = latestRemoteControlDisplayInfo;
        var effectiveDisplayId = displayInfo?.DisplayId ??
                                 (string.IsNullOrWhiteSpace(message.DisplayId) ? "(none)" : message.DisplayId!.Trim());
        var effectiveDisplayRevision = displayInfo is not null
            ? displayInfo.Revision.ToString(CultureInfo.InvariantCulture)
            : message.DisplayInfoRevision?.ToString(CultureInfo.InvariantCulture) ?? "(none)";

        LocalOperationalLog.Info(
            "RemoteControl",
            $"RemoteInput: move (nx={nx.ToString("0.##", CultureInfo.InvariantCulture)} ny={ny.ToString("0.##", CultureInfo.InvariantCulture)}) -> (px={px} py={py}); request_id={effectiveRequestId}; controller_peer_id={effectiveControllerPeerId}; display_id={effectiveDisplayId}; revision={effectiveDisplayRevision}");
    }

    private void ProbeRemoteControlElevationBoundary(ControlInputMessageV1 message, string? controllerPeerId)
    {
        if (!OperatingSystem.IsWindows() || remoteControlProcessElevated)
        {
            ClearRemoteControlAdminRestartWarning("probe_not_required");
            return;
        }

        var nowTicks = Stopwatch.GetTimestamp();
        var nextProbeTick = Interlocked.Read(ref remoteControlElevationProbeNextTick);
        if (nextProbeTick > nowTicks)
        {
            return;
        }

        var probeIntervalTicks = (long)Math.Max(
            1d,
            RemoteControlElevationProbeInterval.TotalSeconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref remoteControlElevationProbeNextTick, nowTicks + probeIntervalTicks);

        if (!WindowsInputIntegrityProbe.TryIsForegroundWindowElevated(out var isForegroundElevated, out var foregroundPid, out var foregroundProcessName))
        {
            return;
        }

        if (!isForegroundElevated || foregroundPid == (uint)Environment.ProcessId)
        {
            ClearRemoteControlAdminRestartWarning("foreground_not_elevated");
            return;
        }

        var processLabel = string.IsNullOrWhiteSpace(foregroundProcessName)
            ? $"pid:{foregroundPid.ToString(CultureInfo.InvariantCulture)}"
            : foregroundProcessName.Trim();
        var warningText = $"Foreground app '{processLabel}' is running as administrator. Restart nLink as administrator on helpee to keep remote control working.";
        SetRemoteControlAdminRestartWarning(warningText, message.RequestId, controllerPeerId, processLabel, foregroundPid);
    }

    private void SetRemoteControlAdminRestartWarning(
        string warningText,
        string? requestId,
        string? controllerPeerId,
        string processLabel,
        uint processId)
    {
        if (string.IsNullOrWhiteSpace(warningText))
        {
            return;
        }

        var normalizedWarningText = warningText.Trim();
        var changed = false;
        if (Interlocked.CompareExchange(ref remoteControlElevationWarningVisible, 1, 1) == 0)
        {
            Interlocked.Exchange(ref remoteControlElevationWarningVisible, 1);
            changed = true;
        }

        if (!string.Equals(remoteControlElevationWarningText, normalizedWarningText, StringComparison.Ordinal))
        {
            remoteControlElevationWarningText = normalizedWarningText;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        if (ShouldEmitRemoteControlRateLimitedLog("input_elevation_boundary_detected", TimeSpan.FromSeconds(2)))
        {
            LogRemoteControlViolation(
                "input_elevation_boundary_detected",
                $"foreground_process={processLabel}; foreground_pid={processId.ToString(CultureInfo.InvariantCulture)}",
                requestId,
                controllerPeerId);
        }

        NotifyRemoteControlStateChanged();
    }

    private void ClearRemoteControlAdminRestartWarning(string reason)
    {
        var wasVisible = Interlocked.Exchange(ref remoteControlElevationWarningVisible, 0);
        if (wasVisible == 0 && string.IsNullOrWhiteSpace(remoteControlElevationWarningText))
        {
            return;
        }

        remoteControlElevationWarningText = string.Empty;
        if (ShouldEmitRemoteControlRateLimitedLog("input_elevation_boundary_cleared", TimeSpan.FromSeconds(3)))
        {
            LogRemoteControlInfo("input_elevation_boundary_cleared", reason);
        }

        NotifyRemoteControlStateChanged();
    }

    private void IncrementRemoteControlSuppressedInjectionCounter()
    {
        IncrementRemoteControlDebugInjectionSuppressedCount();
        PublishRemoteControlDebugDiagnostics();
    }

    [Conditional("DEBUG")]
    private void IncrementRemoteControlDebugQueueDropCount()
    {
        Interlocked.Increment(ref remoteControlDebugQueueDropCount);
    }

    [Conditional("DEBUG")]
    private void IncrementRemoteControlDebugMappingClampCount()
    {
        Interlocked.Increment(ref remoteControlDebugMappingClampCount);
    }

    [Conditional("DEBUG")]
    private void IncrementRemoteControlDebugInjectionSuppressedCount()
    {
        Interlocked.Increment(ref remoteControlDebugInjectionSuppressedCount);
    }

    [Conditional("DEBUG")]
    private void IncrementRemoteControlDebugQueueFlushCount()
    {
        Interlocked.Increment(ref remoteControlDebugQueueFlushCount);
    }

    [Conditional("DEBUG")]
    private void SetRemoteControlDebugLastMapped(double nx, double ny, int px, int py)
    {
        Interlocked.Exchange(ref remoteControlDebugLastMappedNxBits, BitConverter.DoubleToInt64Bits(nx));
        Interlocked.Exchange(ref remoteControlDebugLastMappedNyBits, BitConverter.DoubleToInt64Bits(ny));
        Interlocked.Exchange(ref remoteControlDebugLastMappedPx, px);
        Interlocked.Exchange(ref remoteControlDebugLastMappedPy, py);
        Interlocked.Increment(ref remoteControlDebugLastMappedVersion);
        RemoteControlDebugDiagnostics.SetHelpeeLastMapped(nx, ny, px, py);
    }

    [Conditional("DEBUG")]
    private void ResetRemoteControlDebugLastMapped()
    {
        Interlocked.Exchange(ref remoteControlDebugLastMappedNxBits, 0);
        Interlocked.Exchange(ref remoteControlDebugLastMappedNyBits, 0);
        Interlocked.Exchange(ref remoteControlDebugLastMappedPx, 0);
        Interlocked.Exchange(ref remoteControlDebugLastMappedPy, 0);
        Interlocked.Exchange(ref remoteControlDebugLastMappedVersion, 0);
    }

    private static string? GetRemoteControlInjectionSuppressedRateLimitKey(string normalizedReason)
    {
        return normalizedReason switch
        {
            "mapping_unavailable" => "input_inject_ignored:mapping_unavailable",
            "invalid_coordinates" => "input_inject_ignored:invalid_coordinates",
            "capture_region_invalid" => "input_inject_ignored:capture_region_invalid",
            "missing_display_id" => "input_inject_ignored:missing_display_id",
            "mapping_metadata_missing" => "input_inject_ignored:mapping_metadata_missing",
            "guard_runtime_state" => "input_inject_ignored:guard_runtime_state",
            "guard_role" => "input_inject_ignored:guard_role",
            "guard_control_state" => "input_inject_ignored:guard_control_state",
            "guard_request_mismatch" => "input_inject_ignored:guard_request_mismatch",
            "guard_controller_mismatch" => "input_inject_ignored:guard_controller_mismatch",
            "display_id_mismatch" => "input_inject_ignored:display_id_mismatch",
            "display_revision_stale" => "input_inject_ignored:display_revision_stale",
            "display_revision_mismatch" => "input_inject_ignored:display_revision_mismatch",
            "missing_seq" => "input_inject_ignored:missing_seq",
            "stop_priority" => "input_inject_ignored:stop_priority",
            "authorization_transport_required" => "input_inject_ignored:authorization_transport_required",
            "authorization_invite_not_validated" => "input_inject_ignored:authorization_invite_not_validated",
            "authorization_handshake_incomplete" => "input_inject_ignored:authorization_handshake_incomplete",
            "authorization_approval_missing" => "input_inject_ignored:authorization_approval_missing",
            "authorization_session_missing" => "input_inject_ignored:authorization_session_missing",
            "authorization_helper_identity_missing" => "input_inject_ignored:authorization_helper_identity_missing",
            "authorization_session_mismatch" => "input_inject_ignored:authorization_session_mismatch",
            "authorization_helper_identity_mismatch" => "input_inject_ignored:authorization_helper_identity_mismatch",
            "authorization_expired" => "input_inject_ignored:authorization_expired",
            "authorization_capability_missing" => "input_inject_ignored:authorization_capability_missing",
            _ => null,
        };
    }

    private void LogRemoteControlInjectionSuppressed(string reason, string? requestId, string? controllerPeerId)
    {
        IncrementRemoteControlSuppressedInjectionCounter();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        var rateLimitKey = GetRemoteControlInjectionSuppressedRateLimitKey(normalizedReason);
        if (rateLimitKey is not null)
        {
            if (!ShouldEmitRemoteControlRateLimitedLog(rateLimitKey))
            {
                return;
            }
        }

        if (normalizedReason is "display_revision_stale" or "display_revision_mismatch")
        {
            LogRemoteControlRateLimitedInfo(
                "input_stale_dropped",
                normalizedReason,
                requestId,
                controllerPeerId);
        }

        if (string.Equals(normalizedReason, "mapping_metadata_missing", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(normalizedReason, "mapping_unavailable", StringComparison.Ordinal))
        {
            LogRemoteControlInfo("input_inject_ignored", normalizedReason, requestId, controllerPeerId);
            return;
        }

        if (normalizedReason is "guard_runtime_state" or "guard_role" or "guard_control_state" or "stop_priority")
        {
            LogRemoteControlInfo("input_inject_ignored", normalizedReason, requestId, controllerPeerId);
            return;
        }

        LogRemoteControlViolation("input_inject_ignored", normalizedReason, requestId, controllerPeerId);
    }

    private void TriggerRemoteControlMismatchStop(string? requestId, string? controllerPeerId)
    {
        RunCountedBackgroundTask(
            async () =>
            {
                try
                {
                    await StopRemoteControlAsync("display_id_mismatch", CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Runtime teardown path; nothing to do.
                }
                catch (Exception ex)
                {
                    LogRemoteControlViolation("display_id_mismatch_stop_failed", ex.GetType().Name, requestId, controllerPeerId);
                }
            },
            countAsTransportTask: false);
    }

    private void LogRemoteControlTransition(
        RemoteControlSessionState previous,
        RemoteControlSessionState next,
        string reason)
    {
        if (previous.ControlState == next.ControlState &&
            string.Equals(previous.CurrentControlRequestId, next.CurrentControlRequestId, StringComparison.Ordinal) &&
            string.Equals(previous.ControllerPeerId, next.ControllerPeerId, StringComparison.Ordinal) &&
            string.Equals(previous.ConsentToken, next.ConsentToken, StringComparison.Ordinal))
        {
            return;
        }

        LocalOperationalLog.Info(
            "RemoteControl",
            $"event=state_transition; from={previous.ControlState}; to={next.ControlState}; request_id={next.CurrentControlRequestId ?? "(none)"}; controller_peer_id={next.ControllerPeerId ?? "(none)"}; role={role}; reason={reason}; token_expiry_utc={pendingRemoteControlConsentToken?.ExpiresAtUtc.ToString("O") ?? "(none)"}");
    }

    private void UpdateRemoteControlStatusHint(string? reason, ControlState nextState)
    {
        if (nextState is ControlState.Active or ControlState.Requesting)
        {
            remoteControlStatusHintText = string.Empty;
            return;
        }

        remoteControlStatusHintText = ResolveRemoteControlStatusHintText(reason);
    }

    private static string ResolveRemoteControlStatusHintText(string? reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReason))
        {
            return string.Empty;
        }

        if (normalizedReason is "approval_expired" or "approval_grant_missing" or "capability_lost" or "security_context_changed")
        {
            return "Authorization expired or revoked";
        }

        if (normalizedReason.StartsWith("security_", StringComparison.Ordinal) ||
            normalizedReason.StartsWith("authorization_", StringComparison.Ordinal) ||
            normalizedReason.StartsWith("authorization_loss:", StringComparison.Ordinal))
        {
            return "Authorization expired or revoked";
        }

        return string.Empty;
    }

    private void LogRemoteControlRateLimitedInfo(
        string eventName,
        string reason,
        string? requestId = null,
        string? controllerPeerId = null,
        string? tokenDecision = null,
        TimeSpan? window = null)
    {
        var rateLimitKey = BuildRemoteControlDiagnosticRateLimitKey(eventName, reason, requestId, controllerPeerId, tokenDecision);
        if (!ShouldEmitRemoteControlRateLimitedLog(rateLimitKey, window))
        {
            return;
        }

        LogRemoteControlInfo(eventName, reason, requestId, controllerPeerId, tokenDecision);
    }

    private void LogRemoteControlRateLimitedViolation(
        string eventName,
        string reason,
        string? requestId = null,
        string? controllerPeerId = null,
        string? tokenDecision = null,
        TimeSpan? window = null)
    {
        var rateLimitKey = BuildRemoteControlDiagnosticRateLimitKey(eventName, reason, requestId, controllerPeerId, tokenDecision);
        if (!ShouldEmitRemoteControlRateLimitedLog(rateLimitKey, window))
        {
            return;
        }

        LogRemoteControlViolation(eventName, reason, requestId, controllerPeerId, tokenDecision);
    }

    private static string BuildRemoteControlDiagnosticRateLimitKey(
        string eventName,
        string reason,
        string? requestId,
        string? controllerPeerId,
        string? tokenDecision)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "(none)" : reason.Trim();
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? "(none)" : requestId.Trim();
        var normalizedControllerPeerId = string.IsNullOrWhiteSpace(controllerPeerId) ? "(none)" : controllerPeerId.Trim();
        var normalizedTokenDecision = string.IsNullOrWhiteSpace(tokenDecision) ? "(none)" : tokenDecision.Trim();
        return $"diag:{eventName}:{normalizedReason}:{normalizedRequestId}:{normalizedControllerPeerId}:{normalizedTokenDecision}";
    }

    private void LogRemoteControlInfo(
        string eventName,
        string reason,
        string? requestId = null,
        string? controllerPeerId = null,
        string? tokenDecision = null)
    {
        var effectiveRequestId = string.IsNullOrWhiteSpace(requestId)
            ? remoteControlSessionState.CurrentControlRequestId ?? pendingRemoteControlConsentToken?.RequestId
            : requestId.Trim();
        var effectiveControllerPeerId = string.IsNullOrWhiteSpace(controllerPeerId)
            ? remoteControlSessionState.ControllerPeerId ?? pendingRemoteControlConsentToken?.ControllerPeerId
            : controllerPeerId.Trim();
        var tokenExpiry = pendingRemoteControlConsentToken?.ExpiresAtUtc.ToString("O") ?? "(none)";
        var tokenDecisionValue = string.IsNullOrWhiteSpace(tokenDecision) ? "(none)" : tokenDecision;

        LocalOperationalLog.Info(
            "RemoteControl",
            $"event={eventName}; role={role}; state={remoteControlSessionState.ControlState}; reason={reason}; request_id={effectiveRequestId ?? "(none)"}; controller_peer_id={effectiveControllerPeerId ?? "(none)"}; token_expiry_utc={tokenExpiry}; token_decision={tokenDecisionValue}");
    }

    private void LogRemoteControlViolation(
        string eventName,
        string reason,
        string? requestId = null,
        string? controllerPeerId = null,
        string? tokenDecision = null)
    {
        var effectiveRequestId = string.IsNullOrWhiteSpace(requestId)
            ? remoteControlSessionState.CurrentControlRequestId ?? pendingRemoteControlConsentToken?.RequestId
            : requestId.Trim();
        var effectiveControllerPeerId = string.IsNullOrWhiteSpace(controllerPeerId)
            ? remoteControlSessionState.ControllerPeerId ?? pendingRemoteControlConsentToken?.ControllerPeerId
            : controllerPeerId.Trim();
        var tokenExpiry = pendingRemoteControlConsentToken?.ExpiresAtUtc.ToString("O") ?? "(none)";
        var tokenDecisionValue = string.IsNullOrWhiteSpace(tokenDecision) ? "(none)" : tokenDecision;

        LocalOperationalLog.Warn(
            "RemoteControl",
            $"event={eventName}; role={role}; state={remoteControlSessionState.ControlState}; reason={reason}; request_id={effectiveRequestId ?? "(none)"}; controller_peer_id={effectiveControllerPeerId ?? "(none)"}; token_expiry_utc={tokenExpiry}; token_decision={tokenDecisionValue}");
    }

    private static string FormatControlInputLogSummary(ControlInputMessageV1 message)
    {
        var kind = string.IsNullOrWhiteSpace(message.Kind) ? "(none)" : message.Kind;
        var mapping =
            $"; display_id={message.DisplayId ?? "(none)"}; display_rev={message.DisplayInfoRevision?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}";
        return kind switch
        {
            "mouse_move" => $"type=mouse_move; seq={message.Seq}; nx={FormatOptionalCoordinate(message.Nx)}; ny={FormatOptionalCoordinate(message.Ny)}{mapping}",
            "mouse_button" => $"type=mouse_button; seq={message.Seq}; action={message.Action ?? "(none)"}; button={message.Button ?? "(none)"}; nx={FormatOptionalCoordinate(message.Nx)}; ny={FormatOptionalCoordinate(message.Ny)}{mapping}",
            "mouse_wheel" => $"type=mouse_wheel; seq={message.Seq}; dx={FormatOptionalCoordinate(message.DeltaX)}; dy={FormatOptionalCoordinate(message.DeltaY)}; nx={FormatOptionalCoordinate(message.Nx)}; ny={FormatOptionalCoordinate(message.Ny)}{mapping}",
            "key" => $"type=key; seq={message.Seq}; action={message.Action ?? "(none)"}; key={message.Key ?? "(none)"}; physical_key={message.PhysicalKey ?? "(none)"}; shift={message.Shift ?? false}; ctrl={message.Ctrl ?? false}; alt={message.Alt ?? false}; meta={message.Meta ?? false}; repeat={message.Repeat ?? false}{mapping}",
            _ => $"type={kind}; seq={message.Seq}{mapping}",
        };
    }

    private static string FormatControlDisplayInfoLogSummary(ControlDisplayInfoMessageV1 message)
    {
        return $"display_id={message.DisplayId}; revision={message.Revision}; frame={message.FrameWidth}x{message.FrameHeight}; " +
               $"capture_region={message.CaptureRegionX},{message.CaptureRegionY},{message.CaptureRegionWidth}x{message.CaptureRegionHeight}; " +
               $"virtual_desktop={message.VirtualDesktopX},{message.VirtualDesktopY},{message.VirtualDesktopWidth}x{message.VirtualDesktopHeight}; " +
               $"dpi_scale={message.DpiScale?.ToString("0.###", CultureInfo.InvariantCulture) ?? "(none)"}";
    }

    private void ClearRemoteControlRevisionMismatchCache()
    {
        hasRemoteControlRevisionMismatchCache = false;
        lastRemoteControlRevisionMismatchDisplayId = null;
        lastRemoteControlRevisionMismatchIncomingRevision = 0;
        lastRemoteControlRevisionMismatchExpectedRevision = 0;
    }

    private static bool HasDisplayInfoMappingChanged(ControlDisplayInfoMessageV1 previous, ControlDisplayInfoMessageV1 current)
    {
        // Mapping-critical fields only. Encoded frame size can change during adaptive
        // streaming without changing the capture coordinate space, so it must not force
        // a remote-control stop.
        return !string.Equals(previous.DisplayId, current.DisplayId, StringComparison.Ordinal) ||
               previous.VirtualDesktopX != current.VirtualDesktopX ||
               previous.VirtualDesktopY != current.VirtualDesktopY ||
               previous.VirtualDesktopWidth != current.VirtualDesktopWidth ||
               previous.VirtualDesktopHeight != current.VirtualDesktopHeight ||
               previous.CaptureRegionX != current.CaptureRegionX ||
               previous.CaptureRegionY != current.CaptureRegionY ||
               previous.CaptureRegionWidth != current.CaptureRegionWidth ||
               previous.CaptureRegionHeight != current.CaptureRegionHeight;
    }

    private void ClearRemoteControlDisplayInfo(string reason, bool notifyStateChanged)
    {
        if (latestRemoteControlDisplayInfo is null &&
            remoteControlCoordinatorDisplayInfoState.Equals(RemoteControlDisplayInfoState.Empty))
        {
            return;
        }

        latestRemoteControlDisplayInfo = null;
        remoteControlCoordinatorDisplayInfoState = RemoteControlDisplayInfoState.Empty;
        ClearRemoteControlRevisionMismatchCache();
        CancelRemoteControlScreenChangedStatus();
        LogRemoteControlInfo("display_info_cleared", reason);
        if (notifyStateChanged)
        {
            NotifyRemoteControlStateChanged();
        }
    }

    private static bool IsLowPriorityMouseMoveInput(ControlInputMessageV1 message)
    {
        return string.Equals(message.Kind, "mouse_move", StringComparison.Ordinal);
    }

    private static string DescribeRemoteControlInjectionWorkItemKind(RemoteControlInjectionWorkItem workItem)
    {
        if (workItem.Message is { } message)
        {
            return string.IsNullOrWhiteSpace(message.Kind) ? "(none)" : message.Kind.Trim();
        }

        return workItem.Snapshot is null ? "(none)" : "state_snapshot";
    }

    private static string? GetRemoteControlInjectionWorkItemRequestId(RemoteControlInjectionWorkItem workItem)
    {
        if (workItem.Message is { } message)
        {
            return message.RequestId;
        }

        return workItem.Snapshot?.RequestId;
    }

    private void ClearQueuedRemoteControlMouseMoves(string? reason = null)
    {
        var flushed = false;
        var hadPendingMove = false;
        lock (remoteControlMouseMoveQueueGate)
        {
            hadPendingMove = queuedRemoteControlMouseMove is not null;
            flushed = queuedRemoteControlMouseMove is not null || remoteControlMouseMoveSenderActive;
            queuedRemoteControlMouseMove = null;
            remoteControlMouseMoveSenderActive = false;
        }

        if (flushed)
        {
            IncrementRemoteControlDebugQueueFlushCount();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                LogRemoteControlInfo(
                    "input_send_queue_flushed",
                    $"reason={reason}; had_pending_move={hadPendingMove}");
            }
        }

        PublishRemoteControlDebugDiagnostics();
    }

    private void ClearQueuedRemoteControlInjections(string? reason = null)
    {
        var flushed = false;
        var clearedCount = 0;
        lock (remoteControlInjectionQueueGate)
        {
            clearedCount = remoteControlInjectionQueue.Count;
            flushed = remoteControlInjectionQueue.Count > 0 ||
                      queuedRemoteControlInjectionMouseMoveNode is not null ||
                      queuedRemoteControlInjectionSnapshotNode is not null;
            remoteControlInjectionQueue.Clear();
            queuedRemoteControlInjectionMouseMoveNode = null;
            queuedRemoteControlInjectionSnapshotNode = null;
        }

        if (flushed)
        {
            IncrementRemoteControlDebugQueueFlushCount();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                LogRemoteControlInfo(
                    "input_inject_queue_flushed",
                    $"reason={reason}; cleared={clearedCount.ToString(CultureInfo.InvariantCulture)}",
                    requestId: remoteControlSessionState.CurrentControlRequestId,
                    controllerPeerId: remoteControlSessionState.ControllerPeerId);
            }
        }

        ResetRemoteControlWheelDeltaCarry();
        PublishRemoteControlDebugDiagnostics();
    }

    private void ResetRemoteControlWheelDeltaCarry()
    {
        lock (remoteControlWheelDeltaGate)
        {
            remoteControlWheelDeltaCarryX = 0d;
            remoteControlWheelDeltaCarryY = 0d;
        }
    }

    private static bool IsUsableRemoteControlDisplayInfo(ControlDisplayInfoMessageV1? message)
    {
        if (message is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(message.DisplayId) &&
               message.Revision > 0 &&
               message.VirtualDesktopWidth > 0 &&
               message.VirtualDesktopHeight > 0 &&
               message.CaptureRegionWidth > 0 &&
               message.CaptureRegionHeight > 0 &&
               message.FrameWidth > 0 &&
               message.FrameHeight > 0;
    }

    private static string FormatOptionalCoordinate(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "(none)";
        }

        return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void EnsureRemoteControlStoppedForSessionState(SessionRuntimeState nextState)
    {
        if (nextState == SessionRuntimeState.Connected ||
            remoteControlSessionState.ControlState == ControlState.Off)
        {
            return;
        }

        var requestId = remoteControlSessionState.CurrentControlRequestId;
        var controllerPeerId = remoteControlSessionState.ControllerPeerId;
        MarkRemoteControlStopPriority($"session_state_{nextState}", requestId, controllerPeerId);
        TrySendRemoteControlStopFireAndForget(requestId, controllerPeerId, $"session_state_{nextState}");
        ResetRemoteControlState($"session_state_{nextState}");
    }

    private void EnsureRemoteControlStoppedForTransportState(TransportState nextTransportState, string reason)
    {
        if (remoteControlSessionState.ControlState == ControlState.Off)
        {
            return;
        }

        if (nextTransportState is not (TransportState.Reconnecting or TransportState.Failed or TransportState.Idle or TransportState.Disposed))
        {
            return;
        }

        var requestId = remoteControlSessionState.CurrentControlRequestId;
        var controllerPeerId = remoteControlSessionState.ControllerPeerId;
        MarkRemoteControlStopPriority($"transport_state_{nextTransportState}:{reason}", requestId, controllerPeerId);
        TrySendRemoteControlStopFireAndForget(requestId, controllerPeerId, $"transport_state_{nextTransportState}");
        ResetRemoteControlState($"transport_state_{nextTransportState}");
    }

    private void TrySendRemoteControlStopFireAndForget(string? requestId, string? controllerPeerId, string reason)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            transport is not IRemoteControlSignalingTransport controlTransport)
        {
            return;
        }

        var stopReason = string.IsNullOrWhiteSpace(reason) ? "stop" : reason.Trim();
        RunCountedBackgroundTask(async () =>
        {
            try
            {
                await controlTransport.SendControlStopAsync(
                    new ControlStopMessageV1
                    {
                        RequestId = requestId,
                        Reason = stopReason,
                    },
                    CancellationToken.None).ConfigureAwait(false);
                LogRemoteControlInfo("stop_sent_auto", stopReason, requestId, controllerPeerId);
            }
            catch (Exception ex)
            {
                LogRemoteControlViolation("stop_send_auto_failed", ex.GetType().Name, requestId, controllerPeerId);
            }
        }, countAsTransportTask: false);
    }

    private void EnsureRemoteControlStoppedForAuthorizationLoss(string reason)
    {
        if (remoteControlSessionState.ControlState == ControlState.Off)
        {
            return;
        }

        var requestId = remoteControlSessionState.CurrentControlRequestId;
        var controllerPeerId = remoteControlSessionState.ControllerPeerId;
        var stopReason = GetAuthorizationLossRemoteControlStopReason(reason);
        MarkRemoteControlStopPriority($"authorization_loss:{stopReason}", requestId, controllerPeerId);
        LogRemoteControlRateLimitedViolation(
            "security_stop_initiated",
            stopReason,
            requestId,
            controllerPeerId,
            window: TimeSpan.FromSeconds(5));
        if (!disposed &&
            !resetInProgress &&
            state == SessionRuntimeState.Connected)
        {
            TrySendRemoteControlStopFireAndForget(requestId, controllerPeerId, stopReason);
        }

        ResetRemoteControlState(stopReason);
    }

    private static string GetAuthorizationLossRemoteControlStopReason(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "authorization_lost" : reason.Trim();
        return normalizedReason switch
        {
            "approval_expired" => "security_approval_expired",
            "security_context_changed" => "security_context_invalidated",
            "capability_lost" => "security_capability_lost",
            "approval_grant_missing" => "security_approval_missing",
            "security_transport_required" => "security_transport_required",
            _ => "security_authorization_lost:" + normalizedReason,
        };
    }

    private string GetRunIdForLog() => TransportTelemetryContext.RunId;

    private string GetScenarioForTelemetry() => TransportTelemetryContext.GetScenarioLabel();

    private string GetScenarioForLog()
    {
        var scenario = GetScenarioForTelemetry();
        return string.IsNullOrWhiteSpace(scenario) ? "(none)" : scenario;
    }

    private string GetBridgeReuseModeForTelemetry() => bridgeReusePolicy.Label;

    private string GetBridgeReuseModeForLog() => bridgeReusePolicy.Label;

    private string GetSessionIdForLog()
    {
        return string.IsNullOrWhiteSpace(sessionId) ? "(none)" : sessionId;
    }

    private void LogTransportFailure(TransportFailure failure, string reason)
    {
        lastTransportFailure = failure;
        var duration = GetLastDurationMetricMilliseconds("connect_duration_ms");
        var durationField = duration.HasValue ? duration.Value.ToString("F2") : "(none)";
        var transportKind = GetCurrentTransportKind();
        var sessionIdForLog = GetSessionIdForLog();

        LocalOperationalLog.Error(
            "Session",
            $"event=transport_failed; category={failure.Category}; is_transient={failure.IsTransient}; message={failure.Message}; exception_type={failure.ExceptionType}; attempt={connectAttempt}; transport={transportKind}; bridge_reuse_mode={GetBridgeReuseModeForLog()}; state={transportState}; duration_ms={durationField}; run_id={GetRunIdForLog()}; session_id={sessionIdForLog}; scenario={GetScenarioForLog()}; correlation_id={failure.CorrelationId}; reason={reason}; raw={failure.RawError}");
        telemetrySink.OnFailure(new TransportFailureTelemetryEvent(
            failure.Category,
            failure.IsTransient,
            failure.Message,
            failure.ExceptionType,
            GetRunIdForLog(),
            GetScenarioForTelemetry(),
            GetBridgeReuseModeForTelemetry(),
            connectAttempt,
            transportKind,
            transportState.ToString(),
            duration,
            sessionIdForLog));
    }

    private void SetState(SessionRuntimeState nextState, string nextStatusText)
    {
        EnsureRemoteControlStoppedForSessionState(nextState);
        state = nextState;
        statusText = nextStatusText;

        // Helpee hosting ("Waiting for helper…") should never be subject to a connect
        // watchdog. Cancel any stale watchdog that may have been armed before state settled.
        if (role == SessionRuntimeRole.Helpee && nextState == SessionRuntimeState.Waiting)
        {
            CancelWatchdog();
        }

        LocalOperationalLog.Info(
            "Session",
            $"state={state}; role={role}; status={SanitizeStatusForLog(statusText)}");

        StateChanged?.Invoke(
            this,
            new SessionRuntimeStateChangedEventArgs(state, role, statusText));
        RefreshSessionFlowProjection();
    }

    private void ThrowIfStartInProgress()
    {
        if (startInProgress)
        {
            throw new InvalidOperationException("A session start is already in progress.");
        }
    }

    private void LogResetRequest(string action, string callerMember, string callerFilePath, int callerLineNumber)
    {
        var caller = string.IsNullOrWhiteSpace(callerMember) ? "(unknown)" : callerMember;
        var fileName = ExtractFileName(callerFilePath);
        var line = callerLineNumber > 0 ? callerLineNumber.ToString(CultureInfo.InvariantCulture) : "(none)";
        LocalOperationalLog.Info(
            "Session",
            $"event={action}_requested; caller={caller}; caller_file={fileName}; caller_line={line}; " +
            $"state={state}; transport_state={transportState}; role={role}; attempt={connectAttempt}; " +
            $"transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
    }

    private static string ExtractFileName(string callerFilePath)
    {
        if (string.IsNullOrWhiteSpace(callerFilePath))
        {
            return "(none)";
        }

        var normalized = callerFilePath.Trim();
        var separatorIndex = normalized.LastIndexOfAny(new[] { '\\', '/' });
        if (separatorIndex >= 0 && separatorIndex + 1 < normalized.Length)
        {
            return normalized[(separatorIndex + 1)..];
        }

        return normalized;
    }

    private static bool ShouldNotifyRemoteSessionEnd(
        ISignalingTransport? oldTransport,
        SessionRuntimeRole oldRole,
        SessionRuntimeState oldState)
    {
        if (oldRole == SessionRuntimeRole.None)
        {
            return false;
        }

        if (oldTransport is not NknSignalingTransport nknTransport || !nknTransport.CanSendSessionEnd)
        {
            return false;
        }

        return oldState is SessionRuntimeState.Waiting
            or SessionRuntimeState.IncomingJoinRequest
            or SessionRuntimeState.Connecting
            or SessionRuntimeState.Connected;
    }

    private static async Task TrySendRemoteSessionEndAsync(
        ISignalingTransport? oldTransport,
        SessionRuntimeRole oldRole,
        SessionRuntimeState oldState)
    {
        if (oldTransport is NknSignalingTransport pendingJoinTransport &&
            oldState == SessionRuntimeState.Connecting &&
            pendingJoinTransport.CanSendPendingJoinCancel)
        {
            using var pendingJoinCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await pendingJoinTransport.SendPendingJoinCancelAsync(pendingJoinCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort only.
            }

            return;
        }

        if (!ShouldNotifyRemoteSessionEnd(oldTransport, oldRole, oldState))
        {
            return;
        }

        var nknTransport = (NknSignalingTransport)oldTransport!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await nknTransport.SendSessionEndAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static async Task TrySendRemoteControlStopAsync(
        ISignalingTransport? oldTransport,
        ControlState oldControlState,
        string? oldRequestId,
        string reason)
    {
        if (oldControlState == ControlState.Off ||
            string.IsNullOrWhiteSpace(oldRequestId) ||
            oldTransport is not IRemoteControlSignalingTransport controlTransport)
        {
            return;
        }

        try
        {
            await controlTransport.SendControlStopAsync(
                new ControlStopMessageV1
                {
                    RequestId = oldRequestId,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "stop" : reason,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort only.
        }
    }

}

