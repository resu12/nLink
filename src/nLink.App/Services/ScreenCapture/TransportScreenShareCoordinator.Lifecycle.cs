using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    public async Task StartAsync(string nextSessionId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextSessionId);
        ct.ThrowIfCancellationRequested();

        var normalizedSessionId = nextSessionId.Trim();
        lock (gate)
        {
            if (captureSource is not null &&
                sendPipeline is not null &&
                string.Equals(sessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                LogDebug("StartAsync ignored because screenshare is already active for the current session.");
                return;
            }
        }

        await StopAsync(sendStopMessage: false, reason: null, CancellationToken.None).ConfigureAwait(false);

        var nextCaptureSource = captureSourceFactory();
        if (!nextCaptureSource.IsSupported)
        {
            if (nextCaptureSource is IAsyncDisposable unsupportedAsyncDisposable)
            {
                await unsupportedAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            throw new NotSupportedException("Live screenshare transport requires a supported H.264 capture source.");
        }

        var nextPipeline = new ScreenShareFrameSendPipeline(
            sendFrameAsync: async (frame, sendCt) =>
            {
                lock (gate)
                {
                    if (ShouldSuppressPreOwnerSameEpochFrameAtSendTime_NoLock(
                            frame.StreamEpoch,
                            frame.IsKeyFrame))
                    {
                        throw new ScreenShareSendSupersededException(
                            "Queued video frame was suppressed while the active recovery owner was still pending.");
                    }

                    if (ShouldSuppressFrameWhileOwnerAwaitingHelperAck_NoLock(
                            frame.StreamEpoch,
                            frame.FrameId,
                            frame.IsKeyFrame))
                    {
                        throw new ScreenShareSendSupersededException(
                            "Queued video frame was suppressed while awaiting helper ack for the active recovery owner.");
                    }
                }

                if (!string.Equals(frame.Encoding, "h264", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Live screenshare transport requires H.264 frames, but received '{frame.Encoding}'.");
                }

                if (frame.StreamConfig is not null && sendVideoStreamConfigAsync is not null)
                {
                    await sendVideoStreamConfigAsync(
                            StampStreamConfigForTransport(frame.SessionId, frame.StreamEpoch, frame.StreamConfig),
                            sendCt)
                        .ConfigureAwait(false);
                }

                var fragments = ScreenShareVideoFragmenter.FragmentAccessUnit(
                    frame.SessionId,
                    frame.StreamEpoch > 0 ? frame.StreamEpoch : 1,
                    frame.FrameId,
                    frame.TimestampUnixMilliseconds,
                    frame.Width,
                    frame.Height,
                    frame.Encoding,
                    frame.IsKeyFrame,
                    frame.EncodedFrameBytes);

                var armedRecoveryBurstTransportFallback = false;
                var recoveryBurstTransportFallbackToken = 0L;
                string? recoverySendRole = null;
                lock (gate)
                {
                    if (TryGetRecoverySendMetadata_NoLock(
                            frame.StreamEpoch,
                            frame.FrameId,
                            frame.IsKeyFrame,
                            out recoverySendRole,
                            out recoveryBurstTransportFallbackToken,
                            out armedRecoveryBurstTransportFallback) &&
                        !armedRecoveryBurstTransportFallback)
                    {
                        armedRecoveryBurstTransportFallback = false;
                    }
                }

                var payloadizationPolicy = DetermineTransportPayloadizationPolicy(
                    frame.IsKeyFrame,
                    recoverySendRole);
                var payloadBuildResult = BuildScreenShareTransportPayloads(
                    fragments,
                    payloadizationPolicy);

                if (armedRecoveryBurstTransportFallback)
                {
                    armRecoveryBurstTransportFallback?.Invoke(
                        frame.SessionId,
                        frame.StreamEpoch,
                        recoveryBurstTransportFallbackToken,
                        frame.FrameId);
                }

                try
                {
                    foreach (var payload in payloadBuildResult.Payloads)
                    {
                        if (sendPayloadWithRecoveryMetadataAsync is not null)
                        {
                            await sendPayloadWithRecoveryMetadataAsync(
                                    payload,
                                    recoverySendRole,
                                    recoveryBurstTransportFallbackToken,
                                    sendCt)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await sendPayloadAsync(payload, sendCt).ConfigureAwait(false);
                        }

                        Interlocked.Increment(ref transportPayloadsSent);
                        Interlocked.Add(ref serializedChunkBytesSent, payload.Length);
                        Interlocked.Add(ref bridgeBytesSent, MeasureScreenShareTransportPayloadBytes(payload));
                    }
                }
                catch
                {
                    if (armedRecoveryBurstTransportFallback &&
                        recoveryBurstTransportFallbackToken > 0)
                    {
                        clearRecoveryBurstTransportFallback?.Invoke(recoveryBurstTransportFallbackToken);
                    }

                    throw;
                }

                Interlocked.Increment(ref encodedFramesSent);
                Interlocked.Add(ref batchedPayloadsSent, payloadBuildResult.BatchPayloadCount);
                Interlocked.Add(ref legacyFragmentPayloadsSent, payloadBuildResult.LegacyPayloadCount);
                var isOrdinaryNonKeyTransport =
                    !frame.IsKeyFrame &&
                    string.IsNullOrWhiteSpace(recoverySendRole);
                if (isOrdinaryNonKeyTransport)
                {
                    Interlocked.Add(ref ordinaryNonKeyBatchedPayloadsSent, payloadBuildResult.BatchPayloadCount);
                    Interlocked.Add(ref ordinaryNonKeyLegacyPayloadsSent, payloadBuildResult.LegacyPayloadCount);
                }
                else if (payloadBuildResult.BatchPayloadCount > 0)
                {
                    Interlocked.Add(ref keyframeOrRecoveryBatchedPayloadsSent, payloadBuildResult.BatchPayloadCount);
                }

                HandleRecoveryBurstFrameSent(
                    frame.SessionId,
                    frame.StreamEpoch,
                    frame.FrameId,
                    frame.IsKeyFrame,
                    clock.UtcNow);

                return fragments.Count;
            },
            clock: clock,
            maxFramesPerSecond: FeatureFlags.ScreenShareTransportMaxFps);

        lock (gate)
        {
            lifecycleGeneration = checked(lifecycleGeneration + 1);
            captureSource = nextCaptureSource;
            sendPipeline = nextPipeline;
            sessionId = normalizedSessionId;
            lastActiveSessionId = normalizedSessionId;
            lastSentDisplayInfo = null;
            lastSentDisplayInfoMapping = null;
            lastSentDisplayInfoRevision = 0;
            pendingDisplayInfo = null;
            pendingDisplayInfoMapping = null;
            pendingDisplayInfoNotBeforeUtc = default;
            lastDisplayInfoIssue = string.Empty;
            displayInfoSendCount = 0;
            cursorOverlayStateSeq = 0;
            cursorOverlayUpdatesSentCount = 0;
            cursorOverlaySendFailureCount = 0;
            cursorOverlayMappingFailureCount = 0;
            cursorOverlayDeliveryMode = "captured_video";
            cursorOverlayLastStatus = "starting";
            lastCursorStateSent = null;
            lastCursorStateSentUtc = default;
            cursorTelemetryTickInFlight = 0;
            encodedFramesSent = 0;
            transportPayloadsSent = 0;
            batchedPayloadsSent = 0;
            legacyFragmentPayloadsSent = 0;
            ordinaryNonKeyBatchedPayloadsSent = 0;
            ordinaryNonKeyLegacyPayloadsSent = 0;
            keyframeOrRecoveryBatchedPayloadsSent = 0;
            lastMetricsSnapshot = new();
            var minAutoTuneFps = Math.Min(MinAutoTuneFramesPerSecond, FeatureFlags.ScreenShareTransportMaxFps);
            var configuredCap = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps),
                minAutoTuneFps,
                FeatureFlags.ScreenShareTransportMaxFps);
            captureFpsHint = Math.Clamp(ReducedSenderFramesPerSecond, minAutoTuneFps, configuredCap);
            captureToSendCatchUpPressureTicks = 0;
            remoteObservedCatchUpPressureTicks = 0;
            normalToReducedPressureTicks = 0;
            catchUpRecoveryLowPressureTicks = 0;
            reducedRecoveryLowPressureTicks = 0;
            Volatile.Write(ref preferFreshestPendingFrameOnly, 1);
            senderFreshnessMode = ScreenShareSenderFreshnessMode.Reduced;
            transportTuningLevel = ScreenShareTransportTuningLevel.BandwidthReduced;
            startupWarmupUntilUtc = clock.UtcNow.Add(ScreenShareStartupWarmupDuration);
            remotePressureMode = ScreenShareRemotePressureMode.None;
            remotePressureReason = ScreenSharePressureProtocol.PressureReasonHealthy;
            remotePressureObservedFrameAgeMs = 0;
            remotePressureRecentStaleDrops = 0;
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks = 0;
            senderCatchUpEnteredDueToRemoteHighFrameAgeCount = 0;
            remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount = 0;
            remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount = 0;
            remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount = 0;
            remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount = 0;
            remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount = 0;
            lastRemoteHighFrameAgeCatchUpSuppressionReason = string.Empty;
            catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = 0;
            catchUpExitWhileRemoteHighFrameAgePressureCount = 0;
            recoveryLockAllowedSameTuningModeChangeCount = 0;
            lastRecoveryLockAllowedSameTuningModeChange = string.Empty;
            ResetReducedPromotionDiagnostics_NoLock();
            remotePressureAppliedUtc = null;
            transitionActive = false;
            transitionStreamEpoch = 0;
            transitionStartedUtc = default;
            transitionFirstRemoteApplySeen = false;
            transitionRemoteApplyCount = 0;
            recoveryLockActive = false;
            recoveryLockStreamEpoch = 0;
            recoveryLockStartedUtc = default;
            recoveryLockReason = string.Empty;
            recoveryLockLastContinuitySignalSentAtUtcMs = 0;
            recoveryTimeoutResetIssued = false;
            recoveryTimeoutResetCount = 0;
            recoveryGapActive = false;
            recoveryGapStreamEpoch = 0;
            recoveryGapStartedUtc = default;
            StopRecoveryOwnerPendingTimer_NoLock();
            activeRecoveryBurst = null;
            recoveryFirstHelperHeadAdvanceUtc = default;
            recoveryProtectedFollowerCount = 0;
            recoveryProtectedFrameCount = 0;
            recoveryGapCount = 0;
            recoveryGapToKeyframeRequestMs = -1;
            recoveryKeyframeRequestToOwnerEmitMs = -1;
            nextRecoveryBurstToken = 0;
            recoveryOwnerEmitToFirstVisibleApplyMs = -1;
            recoveryStartAppliedHeadFrameId = -1;
            recoveryStartLastVisibleApplyFrameId = -1;
            recoveryOwnerAckFrameId = -1;
            recoveryOwnerEmitToAckMs = -1;
            recoveryAckSource = string.Empty;
            recoveryBurstControlFallbackCount = 0;
            recoveryBurstTimeoutCount = 0;
            recoveryBurstCompletedCount = 0;
            recoveryBurstRestartSuppressedCount = 0;
            recoveryBurstEncoderRerequestCount = 0;
            recoveryOwnerPendingForcedResetCount = 0;
            recoveryKeyframeEmittedAfterForcedResetCount = 0;
            recoveryBurstCompletedByHelperAckCount = 0;
            recoveryBurstCompletedByTimeoutCount = 0;
            recoveryBurstCompletedByProtectedFramesCount = 0;
            recoveryBurstProfileTransitionDeferredCount = 0;
            recoveryBurstProfileTransitionTakeoverCount = 0;
            recoveryEpochTakeoverSuppressedAfterOwnerEmitCount = 0;
            recoveryBurstStaleRequestSuppressedCount = 0;
            recoveryBurstRequestSuppressedDueToHelperAckCount = 0;
            recoveryBurstStartedWhileHelperProofHealthyCount = 0;
            recoveryBurstCompletedByAppliedHeadAckCount = 0;
            recoveryBurstCompletedByLastVisibleApplyAckCount = 0;
            recoveryBurstCompletedByVisibleRecoveryFloorCount = 0;
            recoveryBurstCompletedByVisibleApplyFallbackCount = 0;
            recoveryBurstCompletedByHelperVisibleReceiptCount = 0;
            helperProgressPastOwnerWithoutBurstAckCount = 0;
            recoveryPostAckHoldStartedCount = 0;
            recoveryPostAckHoldExpiredCount = 0;
            recoveryPostAckHoldSuppressedReopenCount = 0;
            recoveryTracker.ClearLastCompletedRecoveryOutcome();
            recoveryOwnerPendingNonKeyHeldActive = false;
            recoveryOwnerPendingNonKeyHeldCount = 0;
            recoveryOwnerPendingNonKeyReplacedCount = 0;
            helperCurrentEpochStateStreamEpoch = 0;
            helperCurrentEpochWarmupActive = true;
            helperCurrentEpochApplyCount = 0;
            helperCurrentEpochNeedMoreInputCount = 0;
            helperCurrentEpochHealthySignalCount = 0;
            helperCurrentEpochStaleDrops = 0;
            helperSteadyVisibleProgressActive = false;
            helperVisibleHeadFrameId = -1;
            helperVisibleRecoveryFloorFrameId = -1;
            helperLastVisibleApplyFrameId = -1;
            helperAppliedHeadFrameId = -1;
            helperStableVisibleHeadFrameId = -1;
            helperCurrentEpochRecoveryKeyframeApplyCount = 0;
            helperFramesAppliedSinceLastGap = 0;
            remoteHelperFactHealthyActive = false;
            remoteHelperFactHealthySource = string.Empty;
            remoteHelperFactProofFrameId = -1;
            remoteHelperFactHealthyClearCount = 0;
            remoteHelperFactHealthyClearReason = string.Empty;
            acknowledgedHelperProofEpoch = 0;
            acknowledgedHelperHeadFrameId = -1;
            acknowledgedHelperProofUtc = default;
            satisfiedRecoveryFloorEpoch = 0;
            satisfiedRecoveryFloorFrameId = -1;
            satisfiedRecoveryFloorUtc = default;
            satisfiedRecoveryFloorSource = string.Empty;
            satisfiedRecoveryFloorVisibleProofCount = 0;
            continuitySignalIgnoredDueToSatisfiedFloorCount = 0;
            continuitySignalIgnoredDueToVisibleSatisfiedFloorCount = 0;
            recoveryLockClearedByAcknowledgedProofCount = 0;
            recoveryLockClearedByVisibleProofCount = 0;
            recoveryLockLastClearReason = string.Empty;
            lastRemoteRecoveryReceiptStreamEpoch = 0;
            lastRemoteRecoveryReceiptOwnerFrameId = -1;
            lastRemoteRecoveryReceiptVisibleRecoveryFrameId = -1;
            lastRemoteRecoveryReceiptVisibleHeadFrameId = -1;
            lastRemoteRecoveryReceiptKind = string.Empty;
            remoteRecoveryReceiptRejectedCount = 0;
            lastRemoteRecoveryReceiptRejectReason = string.Empty;
            lastRemoteRecoveryReceiptRejectActiveStreamEpoch = 0;
            lastRemoteRecoveryReceiptRejectActiveOwnerFrameId = -1;
            lastRemoteRecoveryReceiptRejectActivePhase = string.Empty;
            lastRecoveryEpochTakeoverSuppressedFromEpoch = 0;
            lastRecoveryEpochTakeoverSuppressedToEpoch = 0;
            lastRecoveryEpochTakeoverSuppressedPhase = string.Empty;
            postReceiptBlockerSuppressedCount = 0;
            lastPostReceiptBlockerSuppressedSet = string.Empty;
            recoveryOwnerAckWindowMs = -1;
            recoveryTimeoutWhileHelperHeadAdvancedCount = 0;
            postAckModeGraceSuppressedHighFrameAgeCount = 0;
            bootstrapGraceSuppressedCatchUpCount = 0;
            postRecoveryAgeGraceEpoch = 0;
            postRecoveryAgeGraceUntilUtc = default;
            postRecoveryAgeGraceSuppressedCount = 0;
            helperReducedModeEntryStableVisibleHeadFrameId = -1;
            helperReducedModeEntryStreamEpoch = 0;
            bootstrapStreamConfig = null;
            bootstrapStreamConfigEpoch = 0;
            bootstrapStreamConfigSendCount = 0;
            streamConfigMissingResendPending = false;
            streamConfigMissingRequestCount = 0;
            streamConfigMissingCachedResendCount = 0;
            transitionFromTransportTuningLevel = ScreenShareTransportTuningLevel.BandwidthReduced;
            transitionToTransportTuningLevel = ScreenShareTransportTuningLevel.BandwidthReduced;
            lastAutoTuneRateGateDrops = 0;
            lastAutoTuneQueueEvictDrops = 0;
            lastLocalLaneCongestionActive = false;
            lastLocalLaneSevereCongestionActive = false;
            lastLocalLaneRecentDropActive = false;
            lastFreshnessSummaryUtc = default;
            lastSenderPromotionBlockedLogUtc = default;
            serializedChunkBytesSent = 0;
            bridgeBytesSent = 0;
            nextCaptureSource.FrameArrived += OnFrameArrived;
            if (nextCaptureSource is IScreenCaptureAdaptiveTuning tunableCaptureSource)
            {
                tunableCaptureSource.SetCaptureFrameRateHint(captureFpsHint);
                tunableCaptureSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.BandwidthReduced);
            }

            ApplyCapturedCursorPreference_NoLock(nextCaptureSource, "screenshare_start");
            nextPipeline.SetMaxFramesPerSecond(captureFpsHint);
            StartCursorTelemetryTimer_NoLock();
        }

        try
        {
            await nextCaptureSource.StartAsync(ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_startup_warmup_entered; session_id={normalizedSessionId}; warmup_fps={captureFpsHint}; warmup_until_utc={startupWarmupUntilUtc:O}");
            StartAutoTuneTimer();
#if DEBUG
            StartSnapshotTimer();
#endif
            if (fileTransferDegradedHintActive)
            {
                SetFileTransferDegradedHint(true);
            }
            else if (fileTransferCatchUpOnlyHintActive)
            {
                SetFileTransferCatchUpOnlyHint(true);
            }
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "ScreenShareTransport",
                $"event=screenshare_transport_start_failed; session_id={normalizedSessionId}; ex={ex.GetType().Name}; message={ex.Message}");
            lock (gate)
            {
                if (ReferenceEquals(captureSource, nextCaptureSource))
                {
                    captureSource = null;
                }

                if (ReferenceEquals(sendPipeline, nextPipeline))
                {
                    sendPipeline = null;
                }

                if (string.Equals(sessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    sessionId = string.Empty;
                }

                nextCaptureSource.FrameArrived -= OnFrameArrived;
            }

            await nextPipeline.DisposeAsync().ConfigureAwait(false);
            if (nextCaptureSource is IAsyncDisposable failedAsyncDisposable)
            {
                await failedAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task HandleDisconnectedAsync()
    {
        return StopAsync(sendStopMessage: false, reason: "disconnected", CancellationToken.None);
    }

    public async Task StopAsync(bool sendStopMessage, string? reason, CancellationToken ct)
    {
        IScreenCaptureSource? oldCaptureSource;
        ScreenShareFrameSendPipeline? oldPipeline;
        string oldSessionId;
        long oldLifecycleGeneration;
        ScreenShareMetrics oldMetricsSnapshot = new(
            DisplayInfoSendCount: Interlocked.Read(ref displayInfoSendCount),
            TransportPayloadsSent: Interlocked.Read(ref transportPayloadsSent),
            BatchedPayloadsSent: Interlocked.Read(ref batchedPayloadsSent),
            LegacyFragmentPayloadsSent: Interlocked.Read(ref legacyFragmentPayloadsSent),
            OrdinaryNonKeyBatchedPayloadsSent: Interlocked.Read(ref ordinaryNonKeyBatchedPayloadsSent),
            OrdinaryNonKeyLegacyPayloadsSent: Interlocked.Read(ref ordinaryNonKeyLegacyPayloadsSent),
            KeyframeOrRecoveryBatchedPayloadsSent: Interlocked.Read(ref keyframeOrRecoveryBatchedPayloadsSent));
        Task? pipelineDisposeTask = null;
        Task? captureStopTask = null;
        Task? drainTask = null;
        TaskCompletionSource<bool>? drainCompletion = null;
        long recoveryBurstTransportClearToken = 0;

        lock (gate)
        {
            oldCaptureSource = captureSource;
            oldPipeline = sendPipeline;
            oldSessionId = sessionId;
            oldLifecycleGeneration = lifecycleGeneration;
            if (oldPipeline is not null)
            {
                var pipelineMetrics = oldPipeline.GetMetricsSnapshot();
                oldMetricsSnapshot = pipelineMetrics with
                {
                    DisplayInfoSendCount = Interlocked.Read(ref displayInfoSendCount),
                    TransportPayloadsSent = Interlocked.Read(ref transportPayloadsSent),
                    BatchedPayloadsSent = Interlocked.Read(ref batchedPayloadsSent),
                    LegacyFragmentPayloadsSent = Interlocked.Read(ref legacyFragmentPayloadsSent),
                    OrdinaryNonKeyBatchedPayloadsSent = Interlocked.Read(ref ordinaryNonKeyBatchedPayloadsSent),
                    OrdinaryNonKeyLegacyPayloadsSent = Interlocked.Read(ref ordinaryNonKeyLegacyPayloadsSent),
                    KeyframeOrRecoveryBatchedPayloadsSent = Interlocked.Read(ref keyframeOrRecoveryBatchedPayloadsSent),
                    SerializedChunkBytesSent = Interlocked.Read(ref serializedChunkBytesSent),
                    BridgeBytesSent = Interlocked.Read(ref bridgeBytesSent),
                    AverageFragmentsPerFrame = ComputeAverage(
                        pipelineMetrics.ChunksSent,
                        Interlocked.Read(ref encodedFramesSent)),
                    AverageTransportPayloadsPerFrame = ComputeAverage(
                        Interlocked.Read(ref transportPayloadsSent),
                        Interlocked.Read(ref encodedFramesSent)),
                };
            }

            lifecycleGeneration = checked(lifecycleGeneration + 1);
            if (activeRecoveryBurst?.BurstToken > 0)
            {
                recoveryBurstTransportClearToken = activeRecoveryBurst.BurstToken;
            }

            StopRecoveryOwnerPendingTimer_NoLock();
            StopCursorTelemetryTimer_NoLock();
            activeRecoveryBurst = null;
            captureSource = null;
            sendPipeline = null;
            sessionId = string.Empty;
            lastSentDisplayInfo = null;
            lastSentDisplayInfoMapping = null;
            lastSentDisplayInfoRevision = 0;
            pendingDisplayInfo = null;
            pendingDisplayInfoMapping = null;
            pendingDisplayInfoNotBeforeUtc = default;
            lastDisplayInfoIssue = string.Empty;
            lastMetricsSnapshot = oldMetricsSnapshot;
            cursorOverlayDeliveryMode = "captured_video";
            cursorOverlayLastStatus = string.IsNullOrWhiteSpace(reason)
                ? "stopped"
                : $"stopped:{reason.Trim()}";
            bootstrapStreamConfig = null;
            bootstrapStreamConfigEpoch = 0;
            bootstrapStreamConfigSendCount = 0;
            streamConfigMissingResendPending = false;
            streamConfigMissingRequestCount = 0;
            streamConfigMissingCachedResendCount = 0;

            if (oldCaptureSource is not null)
            {
                oldCaptureSource.FrameArrived -= OnFrameArrived;
            }

            if (inFlightEnqueues != 0)
            {
                inFlightDrainedTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                drainCompletion = inFlightDrainedTcs;
                drainTask = drainCompletion.Task;
            }
        }

#if DEBUG
        StopSnapshotTimer();
#endif
        StopAutoTuneTimer();

        if (recoveryBurstTransportClearToken > 0)
        {
            clearRecoveryBurstTransportFallback?.Invoke(recoveryBurstTransportClearToken);
        }

        if (oldCaptureSource is null &&
            oldPipeline is null &&
            string.IsNullOrWhiteSpace(oldSessionId) &&
            drainTask is null)
        {
            LogDebug("StopAsync ignored because screenshare is already inactive.");
            return;
        }

        if (oldCaptureSource is not null)
        {
            try
            {
                captureStopTask = oldCaptureSource.StopAsync();
            }
            catch (Exception ex)
            {
                LogDebug($"Capture source stop failed during screenshare shutdown: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (oldPipeline is not null)
        {
            // Cancel queued/in-flight frame work immediately, but do not wait for the
            // send loop to finish before notifying the remote side that screensharing stopped.
            pipelineDisposeTask = oldPipeline.DisposeAsync().AsTask();
        }

        if (sendStopMessage && !string.IsNullOrWhiteSpace(oldSessionId))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_local_requested; session_id={oldSessionId}; reason={(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason)}; lifecycle_generation={oldLifecycleGeneration}");
            var stop = new ScreenShareStopMessageV1
            {
                SessionId = oldSessionId,
                Reason = reason,
            };

            await sendPayloadAsync(ScreenSharePayloadCodec.SerializeStop(stop), ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_local_dispatched; session_id={oldSessionId}; reason={(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason)}; lifecycle_generation={oldLifecycleGeneration}");
        }

        if (pipelineDisposeTask is not null)
        {
            await pipelineDisposeTask.ConfigureAwait(false);
        }

        if (captureStopTask is not null)
        {
            try
            {
                await captureStopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDebug($"Capture source stop failed during screenshare shutdown: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (drainTask is not null)
        {
            try
            {
                await drainTask.WaitAsync(InFlightEnqueueDrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogDebug("StopAsync timed out waiting for in-flight frame enqueues to drain.");
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(inFlightDrainedTcs, drainCompletion))
                    {
                        inFlightDrainedTcs = null;
                    }
                }
            }
        }

        if (oldCaptureSource is not null)
        {
            if (oldCaptureSource is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDebug($"Capture source dispose failed during screenshare shutdown: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync(sendStopMessage: false, reason: null, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void StartAutoTuneTimer()
    {
        if (autoTuneTimer is not null)
        {
            return;
        }

        autoTuneTimer = new Timer(
            static state => ((TransportScreenShareCoordinator)state!).OnAutoTuneTimerTick(),
            this,
            AutoTuneInterval,
            AutoTuneInterval);
    }

    internal bool TrySetCapturedCursorEnabledForRemoteControl(bool enabled, string reason)
    {
        IScreenCaptureSource? currentCaptureSource;
        lock (gate)
        {
            capturedCursorEnabledForTransport = enabled;
            currentCaptureSource = captureSource;
            if (currentCaptureSource is null)
            {
                LogCapturedCursorPreference("queued_before_start", enabled, supported: false, applied: false, reason);
                return false;
            }
        }

        return TryApplyCapturedCursorPreference(currentCaptureSource, enabled, reason);
    }

    private bool TryApplyCapturedCursorPreference(
        IScreenCaptureSource source,
        bool enabled,
        string reason)
    {
        lock (gate)
        {
            return ApplyCapturedCursorPreference_NoLock(source, reason, enabled);
        }
    }

    private bool ApplyCapturedCursorPreference_NoLock(IScreenCaptureSource source, string reason)
        => ApplyCapturedCursorPreference_NoLock(source, reason, capturedCursorEnabledForTransport);

    private bool ApplyCapturedCursorPreference_NoLock(
        IScreenCaptureSource source,
        string reason,
        bool enabled)
    {
        if (source is not IScreenCaptureCursorCaptureControl cursorControl)
        {
            LogCapturedCursorPreference("unsupported_source", enabled: true, supported: false, applied: false, reason);
            return false;
        }

        var supported = cursorControl.IsCursorCaptureControlSupported;
        var applied = cursorControl.TrySetCursorCaptureEnabled(enabled, reason);
        LogCapturedCursorPreference(
            applied ? "applied" : "fallback",
            cursorControl.IsCursorCaptureEnabled,
            supported,
            applied,
            reason);
        return applied;
    }

    private static void LogCapturedCursorPreference(
        string status,
        bool enabled,
        bool supported,
        bool applied,
        string reason)
    {
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_cursor_capture_mode; cursor_capture_enabled={(enabled ? 1 : 0)}; cursor_control_supported={(supported ? 1 : 0)}; applied={(applied ? 1 : 0)}; status={(string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim())}; reason={(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim())}");
    }

    private void EnsureRecoveryOwnerPendingTimer_NoLock()
    {
        recoveryOwnerPendingTimer ??= new Timer(
            static state => ((TransportScreenShareCoordinator)state!).OnRecoveryOwnerPendingTimerTick(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    private void StartRecoveryOwnerPendingTimer_NoLock()
    {
        if (disposed ||
            activeRecoveryBurst is not { } recoveryBurst ||
            recoveryBurst.Phase != RecoveryBurstPhase.OwnerPending ||
            recoveryBurst.OwnerFrameId >= 0 ||
            recoveryBurst.ForcedResetIssued)
        {
            StopRecoveryOwnerPendingTimer_NoLock();
            return;
        }

        EnsureRecoveryOwnerPendingTimer_NoLock();
        recoveryOwnerPendingTimer?.Change(RecoveryOwnerPendingForcedResetDelay, Timeout.InfiniteTimeSpan);
    }

    private void StopRecoveryOwnerPendingTimer_NoLock()
    {
        recoveryOwnerPendingTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void StopRecoveryOwnerPendingTimer()
    {
        var timer = Interlocked.Exchange(ref recoveryOwnerPendingTimer, null);
        timer?.Dispose();
        Interlocked.Exchange(ref recoveryOwnerPendingTimerInFlight, 0);
    }

    private void StopAutoTuneTimer()
    {
        Interlocked.Exchange(ref autoTuneTickInFlight, 0);
        var timer = Interlocked.Exchange(ref autoTuneTimer, null);
        timer?.Dispose();
        captureToSendCatchUpPressureTicks = 0;
        remoteObservedCatchUpPressureTicks = 0;
        normalToReducedPressureTicks = 0;
        catchUpRecoveryLowPressureTicks = 0;
        reducedRecoveryLowPressureTicks = 0;
        captureFpsHint = 0;
        Volatile.Write(ref preferFreshestPendingFrameOnly, 0);
        transportTuningLevel = ScreenShareTransportTuningLevel.Normal;
        senderFreshnessMode = ScreenShareSenderFreshnessMode.Normal;
        lastSenderPromotionBlockedLogUtc = default;
        StopRecoveryOwnerPendingTimer();
        startupWarmupUntilUtc = default;
        remotePressureMode = ScreenShareRemotePressureMode.None;
        remotePressureReason = "healthy";
        remotePressureObservedFrameAgeMs = 0;
        remotePressureRecentStaleDrops = 0;
        remoteHighFrameAgeCatchUpEntryConsecutiveTicks = 0;
        senderCatchUpEnteredDueToRemoteHighFrameAgeCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount = 0;
        lastRemoteHighFrameAgeCatchUpSuppressionReason = string.Empty;
        catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = 0;
        catchUpExitWhileRemoteHighFrameAgePressureCount = 0;
        recoveryLockAllowedSameTuningModeChangeCount = 0;
        lastRecoveryLockAllowedSameTuningModeChange = string.Empty;
        remotePressureAppliedUtc = null;
        transitionActive = false;
        transitionStreamEpoch = 0;
        transitionStartedUtc = default;
        transitionFirstRemoteApplySeen = false;
        transitionRemoteApplyCount = 0;
        recoveryLockActive = false;
        recoveryLockStreamEpoch = 0;
        recoveryLockStartedUtc = default;
        recoveryLockReason = string.Empty;
        recoveryLockLastContinuitySignalSentAtUtcMs = 0;
        recoveryTimeoutResetIssued = false;
        recoveryTimeoutResetCount = 0;
        recoveryGapActive = false;
        recoveryGapStreamEpoch = 0;
        recoveryGapStartedUtc = default;
        StopRecoveryOwnerPendingTimer_NoLock();
        activeRecoveryBurst = null;
        recoveryFirstHelperHeadAdvanceUtc = default;
        recoveryProtectedFollowerCount = 0;
        recoveryProtectedFrameCount = 0;
        recoveryGapCount = 0;
        recoveryGapToKeyframeRequestMs = -1;
        recoveryKeyframeRequestToOwnerEmitMs = -1;
        nextRecoveryBurstToken = 0;
        recoveryOwnerEmitToFirstVisibleApplyMs = -1;
        recoveryStartAppliedHeadFrameId = -1;
        recoveryStartLastVisibleApplyFrameId = -1;
        recoveryOwnerAckFrameId = -1;
        recoveryOwnerEmitToAckMs = -1;
        recoveryAckSource = string.Empty;
        recoveryBurstControlFallbackCount = 0;
        recoveryBurstTimeoutCount = 0;
        recoveryBurstCompletedCount = 0;
        recoveryBurstRestartSuppressedCount = 0;
        recoveryBurstEncoderRerequestCount = 0;
        recoveryOwnerPendingForcedResetCount = 0;
        recoveryKeyframeEmittedAfterForcedResetCount = 0;
        recoveryBurstCompletedByHelperAckCount = 0;
        recoveryBurstCompletedByTimeoutCount = 0;
        recoveryBurstCompletedByProtectedFramesCount = 0;
        recoveryBurstProfileTransitionDeferredCount = 0;
        recoveryBurstProfileTransitionTakeoverCount = 0;
        recoveryEpochTakeoverSuppressedAfterOwnerEmitCount = 0;
        recoveryBurstStaleRequestSuppressedCount = 0;
        recoveryBurstRequestSuppressedDueToHelperAckCount = 0;
        recoveryBurstStartedWhileHelperProofHealthyCount = 0;
        recoveryBurstCompletedByAppliedHeadAckCount = 0;
        recoveryBurstCompletedByLastVisibleApplyAckCount = 0;
        recoveryBurstCompletedByVisibleRecoveryFloorCount = 0;
        recoveryBurstCompletedByVisibleApplyFallbackCount = 0;
        recoveryBurstCompletedByHelperVisibleReceiptCount = 0;
        helperProgressPastOwnerWithoutBurstAckCount = 0;
        recoveryPostAckHoldStartedCount = 0;
        recoveryPostAckHoldExpiredCount = 0;
        recoveryPostAckHoldSuppressedReopenCount = 0;
        recoveryTracker.ClearLastCompletedRecoveryOutcome();
        recoveryOwnerPendingNonKeyHeldActive = false;
        recoveryOwnerPendingNonKeyHeldCount = 0;
        recoveryOwnerPendingNonKeyReplacedCount = 0;
        recoveryOwnerUnackedNonKeyHeldActive = false;
        recoveryOwnerUnackedAdmittedFollowerCount = 0;
        recoveryOwnerUnackedNonKeyHeldCount = 0;
        recoveryOwnerUnackedNonKeyReplacedCount = 0;
        recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = 0;
        recoveryOwnerReplacedBeforeAckCount = 0;
        recoveryOwnerAckWindowMs = -1;
        highFrameAgeSuppressedDuringOwnerAckCount = 0;
        recoveryTimeoutWhileHelperHeadAdvancedCount = 0;
        postAckModeGraceSuppressedHighFrameAgeCount = 0;
        bootstrapGraceSuppressedCatchUpCount = 0;
        helperCurrentEpochStateStreamEpoch = 0;
        helperCurrentEpochWarmupActive = true;
        helperCurrentEpochApplyCount = 0;
        helperCurrentEpochNeedMoreInputCount = 0;
        helperCurrentEpochHealthySignalCount = 0;
        helperCurrentEpochStaleDrops = 0;
        helperSteadyVisibleProgressActive = false;
        helperVisibleHeadFrameId = -1;
        helperVisibleRecoveryFloorFrameId = -1;
        helperLastVisibleApplyFrameId = -1;
        helperAppliedHeadFrameId = -1;
        helperStableVisibleHeadFrameId = -1;
        helperCurrentEpochRecoveryKeyframeApplyCount = 0;
        helperFramesAppliedSinceLastGap = 0;
        helperLatestVisibleProgressEpoch = 0;
        helperLatestVisibleProgressUtc = default;
        remoteHelperFactHealthyActive = false;
        remoteHelperFactHealthySource = string.Empty;
        remoteHelperFactProofFrameId = -1;
        remoteHelperFactHealthyClearCount = 0;
        remoteHelperFactHealthyClearReason = string.Empty;
        acknowledgedHelperProofEpoch = 0;
        acknowledgedHelperHeadFrameId = -1;
        acknowledgedHelperProofUtc = default;
        satisfiedRecoveryFloorEpoch = 0;
        satisfiedRecoveryFloorFrameId = -1;
        satisfiedRecoveryFloorUtc = default;
        satisfiedRecoveryFloorSource = string.Empty;
        satisfiedRecoveryFloorVisibleProofCount = 0;
        continuitySignalIgnoredDueToSatisfiedFloorCount = 0;
        continuitySignalIgnoredDueToVisibleSatisfiedFloorCount = 0;
        recoveryLockClearedByAcknowledgedProofCount = 0;
        recoveryLockClearedByVisibleProofCount = 0;
        recoveryLockLastClearReason = string.Empty;
        lastRemoteRecoveryReceiptStreamEpoch = 0;
        lastRemoteRecoveryReceiptOwnerFrameId = -1;
        lastRemoteRecoveryReceiptVisibleRecoveryFrameId = -1;
        lastRemoteRecoveryReceiptVisibleHeadFrameId = -1;
        lastRemoteRecoveryReceiptKind = string.Empty;
        remoteRecoveryReceiptRejectedCount = 0;
        lastRemoteRecoveryReceiptRejectReason = string.Empty;
        lastRemoteRecoveryReceiptRejectActiveStreamEpoch = 0;
        lastRemoteRecoveryReceiptRejectActiveOwnerFrameId = -1;
        lastRemoteRecoveryReceiptRejectActivePhase = string.Empty;
        lastRecoveryEpochTakeoverSuppressedFromEpoch = 0;
        lastRecoveryEpochTakeoverSuppressedToEpoch = 0;
        lastRecoveryEpochTakeoverSuppressedPhase = string.Empty;
        postReceiptBlockerSuppressedCount = 0;
        lastPostReceiptBlockerSuppressedSet = string.Empty;
        transitionFromTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
        transitionToTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
        lastSenderFreshnessKeyFrameRequestedUtc = null;
        lastAutoTuneRateGateDrops = 0;
        lastAutoTuneQueueEvictDrops = 0;
        lastAutoTuneSourceSupersededPendingFrames = 0;
        lastLocalLaneRecentDropActive = false;
        lastFreshnessSummaryUtc = default;
        ResetReducedPromotionDiagnostics_NoLock();
    }
}
