using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    private readonly record struct ScreenShareTransportPayloadBuildResult(
        IReadOnlyList<byte[]> Payloads,
        int BatchPayloadCount,
        int LegacyPayloadCount);

    private void OnFrameArrived(object? sender, ScreenCaptureFrameEventArgs e)
    {
        ScreenShareFrameSendPipeline? currentPipeline;
        IScreenCaptureSource? currentCaptureSource;
        string currentSessionId;
        Task enqueueTask;

        lock (gate)
        {
            currentPipeline = sendPipeline;
            currentCaptureSource = captureSource;
            currentSessionId = sessionId;

            if (currentPipeline is null || string.IsNullOrWhiteSpace(currentSessionId))
            {
                return;
            }

            inFlightEnqueues++;
        }

        if (currentCaptureSource is not null)
        {
            TryPublishDisplayInfo(currentCaptureSource, e.Width, e.Height);
        }

        enqueueTask = TryEnqueueFrameAsync(currentPipeline, currentSessionId, e);
        _ = enqueueTask.ContinueWith(
            static (_, state) => ((TransportScreenShareCoordinator)state!).OnEnqueueCompleted(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task TryEnqueueFrameAsync(
        ScreenShareFrameSendPipeline currentPipeline,
        string currentSessionId,
        ScreenCaptureFrameEventArgs e)
    {
        try
        {
            var heldForRecoveryOwnerPending = false;
            var heldForRecoveryOwnerAwaitingAck = false;
            var heldForRecoverySettle = false;
            ScreenShareVideoStreamConfigV1? streamConfigForFrame = e.StreamConfig;
            if (sendVideoStreamConfigAsync is not null)
            {
                ScreenShareVideoStreamConfigV1? stampedConfigToSend = null;
                string? configSendReason = null;
                int configSendAttempt = 0;
                var pendingStreamConfigMissingResend = false;
                lock (gate)
                {
                    if (streamConfigForFrame is not null)
                    {
                        stampedConfigToSend = StampStreamConfigForTransport(currentSessionId, e.StreamEpoch, streamConfigForFrame);
                        bootstrapStreamConfig = stampedConfigToSend;
                        bootstrapStreamConfigEpoch = e.StreamEpoch;
                        bootstrapStreamConfigSendCount = 1;
                        streamConfigMissingResendPending = false;
                        configSendReason = "initial";
                        configSendAttempt = bootstrapStreamConfigSendCount;
                    }
                    else if (bootstrapStreamConfig is not null &&
                             bootstrapStreamConfigEpoch == e.StreamEpoch &&
                             e.IsKeyFrame &&
                             streamConfigMissingResendPending)
                    {
                        bootstrapStreamConfigSendCount++;
                        streamConfigMissingCachedResendCount++;
                        stampedConfigToSend = bootstrapStreamConfig;
                        streamConfigMissingResendPending = false;
                        configSendReason = "stream_config_missing_recovery";
                        configSendAttempt = bootstrapStreamConfigSendCount;
                        pendingStreamConfigMissingResend = true;
                    }
                    else if (bootstrapStreamConfig is not null &&
                             bootstrapStreamConfigEpoch == e.StreamEpoch &&
                             bootstrapStreamConfigSendCount > 0 &&
                             bootstrapStreamConfigSendCount < StreamConfigBootstrapSendAttempts)
                    {
                        bootstrapStreamConfigSendCount++;
                        stampedConfigToSend = bootstrapStreamConfig;
                        configSendReason = "bootstrap_redundant";
                        configSendAttempt = bootstrapStreamConfigSendCount;
                    }
                    else if (bootstrapStreamConfig is not null &&
                             bootstrapStreamConfigEpoch == e.StreamEpoch &&
                             e.IsKeyFrame &&
                             IsHelperVisibleProofMissingForEpoch_NoLock(e.StreamEpoch))
                    {
                        bootstrapStreamConfigSendCount++;
                        stampedConfigToSend = bootstrapStreamConfig;
                        configSendReason = "keyframe_until_visible_baseline";
                        configSendAttempt = bootstrapStreamConfigSendCount;
                    }
                }

                if (stampedConfigToSend is not null)
                {
                    await sendVideoStreamConfigAsync(
                            stampedConfigToSend,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_video_stream_config_sent; session_id={currentSessionId}; stream_epoch={Math.Max(0, stampedConfigToSend.StreamEpoch)}; attempt={configSendAttempt}; reason={configSendReason}; is_keyframe={(e.IsKeyFrame ? 1 : 0)}; pending_stream_config_missing={(pendingStreamConfigMissingResend ? 1 : 0)}; config_bytes={stampedConfigToSend.DecoderConfigData?.Length ?? 0}");
                    streamConfigForFrame = null;
                }
            }

            lock (gate)
            {
                if (ShouldHoldPreOwnerSameEpochNonKeyFrame_NoLock(e.StreamEpoch, e.IsKeyFrame))
                {
                    RecordHeldPreOwnerSameEpochNonKeyFrame_NoLock();
                    heldForRecoveryOwnerPending = true;
                }
                else if (ShouldHoldSameEpochFrameWhileOwnerAwaitingHelperAck_NoLock(e.StreamEpoch, e.IsKeyFrame))
                {
                    heldForRecoveryOwnerAwaitingAck = true;
                }
                else if (ShouldHoldSameEpochFrameDuringRecoveryPostAckHold_NoLock(e.StreamEpoch, e.IsKeyFrame))
                {
                    heldForRecoverySettle = true;
                }
            }

            if (heldForRecoveryOwnerPending || heldForRecoveryOwnerAwaitingAck || heldForRecoverySettle)
            {
                return;
            }

            if (e.IsKeyFrame)
            {
                long rebindGenerationToLog = 0;
                string rebindReasonToLog = string.Empty;
                lock (gate)
                {
                    if (transportRebindPendingGeneration > 0)
                    {
                        rebindGenerationToLog = transportRebindPendingGeneration;
                        rebindReasonToLog = transportRebindReason;
                        transportRebindPendingGeneration = 0;
                    }
                }

                if (rebindGenerationToLog > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_transport_rebind_keyframe_sent; direction=outbound; session_id={currentSessionId}; stream_epoch={e.StreamEpoch}; reason={(string.IsNullOrWhiteSpace(rebindReasonToLog) ? "transport_rebind" : rebindReasonToLog)}; rebind_generation={rebindGenerationToLog}");
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_tuna_handoff_keyframe_forced; direction=outbound; session_id={currentSessionId}; stream_epoch={e.StreamEpoch}; reason={(string.IsNullOrWhiteSpace(rebindReasonToLog) ? "transport_rebind" : rebindReasonToLog)}; rebind_generation={rebindGenerationToLog}");
                }
            }

            await currentPipeline.EnqueueFrameAsync(
                currentSessionId,
                e.Width,
                e.Height,
                e.Encoding,
                e.EncodedFrameData,
                e.CapturedTsUtcMs > 0
                    ? e.CapturedTsUtcMs
                    : clock.UtcNow.ToUnixTimeMilliseconds(),
                e.IsKeyFrame,
                e.StreamEpoch,
                streamConfigForFrame,
                CancellationToken.None,
                preserveOrdering: false).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            LogDebug("Frame enqueue ignored because sender pipeline was already disposed.");
        }
        catch (InvalidOperationException)
        {
            LogDebug("Frame enqueue ignored because sender pipeline was already completed.");
        }
        catch (OperationCanceledException)
        {
            LogDebug("Frame enqueue canceled during shutdown.");
        }
        catch (Exception ex)
        {
            LogDebug($"Frame enqueue failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal ScreenShareMetrics GetMetricsSnapshot()
    {
        lock (gate)
        {
            var sourceFreshnessMetrics = GetCaptureFreshnessMetricsSnapshot(captureSource);
            var nowUtc = clock.UtcNow;
            var senderOperatingState = ScreenShareSenderAutoTuneEvaluator.MapOperatingState(senderFreshnessMode);
            var senderGuardState = GetCurrentSenderGuardState_NoLock(nowUtc, sourceFreshnessMetrics.CurrentStreamEpoch);
            if (sendPipeline is not null)
            {
                var pipelineMetrics = sendPipeline.GetMetricsSnapshot();
                lastMetricsSnapshot = pipelineMetrics with
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
                    FreshnessMode = FormatSenderFreshnessMode(senderFreshnessMode),
                    EmittedDisplayableFrames = sourceFreshnessMetrics.EmittedDisplayableFrames,
                    EmittedNonDisplayableUnits = sourceFreshnessMetrics.EmittedNonDisplayableUnits,
                    DisplayableFrameRatio = sourceFreshnessMetrics.DisplayableFrameRatio,
                    IdrFramesEmitted = sourceFreshnessMetrics.IdrFramesEmitted,
                    PFramesEmitted = sourceFreshnessMetrics.PFramesEmitted,
                    DroppedBFrames = sourceFreshnessMetrics.DroppedBFrames,
                    DroppedMultiPictureUnits = sourceFreshnessMetrics.DroppedMultiPictureUnits,
                    IdrFrameRatio = sourceFreshnessMetrics.IdrFrameRatio,
                    AverageEncodedFrameBytes = sourceFreshnessMetrics.AverageEncodedFrameBytes,
                    TransportIpOnlyMode = sourceFreshnessMetrics.TransportIpOnlyMode,
                    LastAccessUnitKind = sourceFreshnessMetrics.LastAccessUnitKind,
                    LowDelayConfigApplied = sourceFreshnessMetrics.LowDelayConfigApplied,
                    PromotionBlockerRateGateTicks = promotionBlockerRateGateTicks,
                    PromotionBlockerHelperPressureTicks = promotionBlockerHelperPressureTicks,
                    PromotionBlockerHelperWarmupTicks = promotionBlockerHelperWarmupTicks,
                    PromotionBlockerHelperApplyCountTicks = promotionBlockerHelperApplyCountTicks,
                    PromotionBlockerBridgeHealthTicks = promotionBlockerBridgeHealthTicks,
                    PromotionBlockerRecoveryLockTicks = promotionBlockerRecoveryLockTicks,
                    PromotionBlockerQueueEvictTicks = promotionBlockerQueueEvictTicks,
                    PromotionBlockerCaptureAgeTicks = promotionBlockerCaptureAgeTicks,
                    PromotionBlockerEncodeBudgetTicks = promotionBlockerEncodeBudgetTicks,
                    PromotionBlockerTransitionGraceTicks = promotionBlockerTransitionGraceTicks,
                    HealthyTickResetReasonCounts = FormatHealthyTickResetReasonCounts_NoLock(),
                    SenderOperatingState = ScreenShareConceptualModelFormatter.FormatSenderOperatingState(senderOperatingState),
                    SenderGuardState = ScreenShareConceptualModelFormatter.FormatSenderGuardState(senderGuardState),
                };
                lastMetricsSnapshot = lastMetricsSnapshot with
                {
                    DominantPressureBlocker = DetermineDominantPressureBlocker(lastMetricsSnapshot),
                };
            }
            else
            {
                lastMetricsSnapshot = lastMetricsSnapshot with
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
                    AverageTransportPayloadsPerFrame = ComputeAverage(
                        Interlocked.Read(ref transportPayloadsSent),
                        Interlocked.Read(ref encodedFramesSent)),
                    FreshnessMode = FormatSenderFreshnessMode(senderFreshnessMode),
                    EmittedDisplayableFrames = sourceFreshnessMetrics.EmittedDisplayableFrames,
                    EmittedNonDisplayableUnits = sourceFreshnessMetrics.EmittedNonDisplayableUnits,
                    DisplayableFrameRatio = sourceFreshnessMetrics.DisplayableFrameRatio,
                    IdrFramesEmitted = sourceFreshnessMetrics.IdrFramesEmitted,
                    PFramesEmitted = sourceFreshnessMetrics.PFramesEmitted,
                    DroppedBFrames = sourceFreshnessMetrics.DroppedBFrames,
                    DroppedMultiPictureUnits = sourceFreshnessMetrics.DroppedMultiPictureUnits,
                    IdrFrameRatio = sourceFreshnessMetrics.IdrFrameRatio,
                    AverageEncodedFrameBytes = sourceFreshnessMetrics.AverageEncodedFrameBytes,
                    TransportIpOnlyMode = sourceFreshnessMetrics.TransportIpOnlyMode,
                    LastAccessUnitKind = sourceFreshnessMetrics.LastAccessUnitKind,
                    LowDelayConfigApplied = sourceFreshnessMetrics.LowDelayConfigApplied,
                    PromotionBlockerRateGateTicks = promotionBlockerRateGateTicks,
                    PromotionBlockerHelperPressureTicks = promotionBlockerHelperPressureTicks,
                    PromotionBlockerHelperWarmupTicks = promotionBlockerHelperWarmupTicks,
                    PromotionBlockerHelperApplyCountTicks = promotionBlockerHelperApplyCountTicks,
                    PromotionBlockerBridgeHealthTicks = promotionBlockerBridgeHealthTicks,
                    PromotionBlockerRecoveryLockTicks = promotionBlockerRecoveryLockTicks,
                    PromotionBlockerQueueEvictTicks = promotionBlockerQueueEvictTicks,
                    PromotionBlockerCaptureAgeTicks = promotionBlockerCaptureAgeTicks,
                    PromotionBlockerEncodeBudgetTicks = promotionBlockerEncodeBudgetTicks,
                    PromotionBlockerTransitionGraceTicks = promotionBlockerTransitionGraceTicks,
                    HealthyTickResetReasonCounts = FormatHealthyTickResetReasonCounts_NoLock(),
                    SenderOperatingState = ScreenShareConceptualModelFormatter.FormatSenderOperatingState(senderOperatingState),
                    SenderGuardState = ScreenShareConceptualModelFormatter.FormatSenderGuardState(senderGuardState),
                };
                lastMetricsSnapshot = lastMetricsSnapshot with
                {
                    DominantPressureBlocker = DetermineDominantPressureBlocker(lastMetricsSnapshot),
                };
            }

            return lastMetricsSnapshot;
        }
    }

    private ScreenShareSenderGuardState GetCurrentSenderGuardState_NoLock(DateTimeOffset nowUtc, long currentStreamEpoch)
    {
        if (recoveryLockActive)
        {
            return ScreenShareSenderGuardState.RecoveryLocked;
        }

        if (IsPostAckModeGraceActive_NoLock(currentStreamEpoch, nowUtc))
        {
            return ScreenShareSenderGuardState.PostAckGrace;
        }

        if (IsBeforeFirstVisibleApplyBootstrapGraceActive_NoLock(currentStreamEpoch, nowUtc))
        {
            return ScreenShareSenderGuardState.BootstrapGrace;
        }

        if (IsTransportProfileTransitionGraceActive_NoLock(nowUtc))
        {
            return ScreenShareSenderGuardState.TransitionGrace;
        }

        return ScreenShareSenderGuardState.None;
    }

    private static string DetermineDominantPressureBlocker(ScreenShareMetrics metrics)
    {
        var dominant = ("none", long.MinValue);

        dominant = PickDominantBlocker(dominant, "rate_gate", metrics.PromotionBlockerRateGateTicks);
        dominant = PickDominantBlocker(dominant, "helper_pressure", metrics.PromotionBlockerHelperPressureTicks);
        dominant = PickDominantBlocker(dominant, "helper_warmup", metrics.PromotionBlockerHelperWarmupTicks);
        dominant = PickDominantBlocker(dominant, "helper_apply_count", metrics.PromotionBlockerHelperApplyCountTicks);
        dominant = PickDominantBlocker(dominant, "bridge_health", metrics.PromotionBlockerBridgeHealthTicks);
        dominant = PickDominantBlocker(dominant, "recovery_lock", metrics.PromotionBlockerRecoveryLockTicks);
        dominant = PickDominantBlocker(dominant, "queue_evict", metrics.PromotionBlockerQueueEvictTicks);
        dominant = PickDominantBlocker(dominant, "capture_age", metrics.PromotionBlockerCaptureAgeTicks);
        dominant = PickDominantBlocker(dominant, "encode_budget", metrics.PromotionBlockerEncodeBudgetTicks);
        dominant = PickDominantBlocker(dominant, "transition_grace", metrics.PromotionBlockerTransitionGraceTicks);

        return dominant.Item1;
    }

    private static (string, long) PickDominantBlocker((string, long) current, string candidateName, long candidateCount)
    {
        if (candidateCount <= 0 || candidateCount <= current.Item2)
        {
            return current;
        }

        return (candidateName, candidateCount);
    }

    private void OnEnqueueCompleted()
    {
        TaskCompletionSource<bool>? drained = null;

        lock (gate)
        {
            if (inFlightEnqueues > 0)
            {
                inFlightEnqueues--;
            }

            if (inFlightEnqueues == 0 && inFlightDrainedTcs is not null)
            {
                drained = inFlightDrainedTcs;
                inFlightDrainedTcs = null;
            }
        }

        drained?.TrySetResult(true);
    }

    private ScreenShareVideoStreamConfigV1 StampStreamConfigForTransport(
        string currentSessionId,
        long frameStreamEpoch,
        ScreenShareVideoStreamConfigV1 streamConfig)
    {
        lock (gate)
        {
            var normalizedSessionId = string.IsNullOrWhiteSpace(currentSessionId)
                ? streamConfig.SessionId
                : currentSessionId;
            var effectiveEpoch = frameStreamEpoch > 0 ? frameStreamEpoch : streamConfig.StreamEpoch;
            var effectiveDisplayInfoRevision = Math.Max(lastSentDisplayInfoRevision, streamConfig.DisplayInfoRevision);

            return streamConfig with
            {
                SessionId = normalizedSessionId,
                StreamEpoch = effectiveEpoch > 0 ? effectiveEpoch : streamConfig.StreamEpoch,
                DisplayInfoRevision = effectiveDisplayInfoRevision,
            };
        }
    }

    private bool IsHelperVisibleProofMissingForEpoch_NoLock(long streamEpoch)
    {
        if (streamEpoch <= 0)
        {
            return false;
        }

        if (helperCurrentEpochStateStreamEpoch > 0 &&
            helperCurrentEpochStateStreamEpoch != streamEpoch)
        {
            return false;
        }

        return helperCurrentEpochApplyCount <= 0 &&
               helperVisibleHeadFrameId < 0 &&
               helperAppliedHeadFrameId < 0 &&
               !helperSteadyVisibleProgressActive;
    }

    private static ScreenShareTransportPayloadizationPolicy DetermineTransportPayloadizationPolicy(
        bool isKeyFrame,
        string? recoverySendRole)
    {
        _ = isKeyFrame;
        _ = recoverySendRole;
        return ScreenShareTransportPayloadizationPolicy.BatchWhenFits;
    }

    private ScreenShareTransportPayloadBuildResult BuildScreenShareTransportPayloads(
        IReadOnlyList<ScreenShareVideoFragmentV1> fragments,
        ScreenShareTransportPayloadizationPolicy payloadizationPolicy)
    {
        var serializedFragments = new List<byte[]>(fragments.Count);
        foreach (var fragment in fragments)
        {
            serializedFragments.Add(ScreenShareVideoPayloadCodec.SerializeFragment(fragment));
        }

        if (payloadizationPolicy == ScreenShareTransportPayloadizationPolicy.LegacyFragmentsOnly)
        {
            return new ScreenShareTransportPayloadBuildResult(
                Payloads: serializedFragments,
                BatchPayloadCount: 0,
                LegacyPayloadCount: serializedFragments.Count);
        }

        var transportPayloads = new List<byte[]>(serializedFragments.Count);
        var currentBatch = new List<byte[]>(serializedFragments.Count);
        var localBatchPayloadCount = 0;
        var localLegacyPayloadCount = 0;

        void FlushCurrentBatch()
        {
            if (currentBatch.Count == 0)
            {
                return;
            }

            var batchPayload = ScreenShareVideoPayloadCodec.SerializeFragmentBatch(currentBatch);
            if (FitsScreenShareTransportPayloadBudget(batchPayload))
            {
                transportPayloads.Add(batchPayload);
                localBatchPayloadCount++;
            }
            else if (currentBatch.Count == 1)
            {
                transportPayloads.Add(currentBatch[0]);
                localLegacyPayloadCount++;
            }
            else
            {
                foreach (var serializedFragment in currentBatch)
                {
                    transportPayloads.Add(serializedFragment);
                    localLegacyPayloadCount++;
                }
            }

            currentBatch.Clear();
        }

        foreach (var serializedFragment in serializedFragments)
        {
            if (currentBatch.Count == 0)
            {
                currentBatch.Add(serializedFragment);
                continue;
            }

            currentBatch.Add(serializedFragment);
            var candidatePayload = ScreenShareVideoPayloadCodec.SerializeFragmentBatch(currentBatch);
            if (FitsScreenShareTransportPayloadBudget(candidatePayload))
            {
                continue;
            }

            currentBatch.RemoveAt(currentBatch.Count - 1);
            FlushCurrentBatch();
            currentBatch.Add(serializedFragment);
        }

        FlushCurrentBatch();
        return new ScreenShareTransportPayloadBuildResult(
            Payloads: transportPayloads,
            BatchPayloadCount: localBatchPayloadCount,
            LegacyPayloadCount: localLegacyPayloadCount);
    }

    private bool FitsScreenShareTransportPayloadBudget(byte[] payload)
    {
        long measuredBytes;
        try
        {
            measuredBytes = MeasureScreenShareTransportPayloadBytes(payload);
        }
        catch (Exception ex) when (estimateBridgeBytes is not null && ex is InvalidOperationException or OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }

        return estimateBridgeBytes is not null
            ? measuredBytes <= ScreenShareBridgePayloadBudgetBytes
            : payload.Length <= ScreenShareFallbackBatchPayloadBudgetBytes;
    }

    private long MeasureScreenShareTransportPayloadBytes(ReadOnlyMemory<byte> payload)
    {
        if (estimateBridgeBytes is null)
        {
            return payload.Length;
        }

        var measuredBytes = Math.Max(0L, estimateBridgeBytes(payload));
        return measuredBytes > 0 ? measuredBytes : payload.Length;
    }
}
