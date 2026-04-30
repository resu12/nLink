using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed partial class MediaFoundationH264FrameEncoder : IWindowsH264FrameEncoder, IWindowsH264FrameEncoderMetricsSource
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11CreateDeviceVideoSupport = 0x800;
    private const uint D3D11UsageDefault = 0;
    private const uint D3D11UsageDynamic = 2;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessWrite = 0x10000;
    private const uint D3D11MapWrite = 2;
    private const uint D3D11MapWriteDiscard = 4;
    private const uint DxgiFormatNv12 = 103;
    private const int MfTransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfNotAccepting = unchecked((int)0xC00D36B0);
    private const ushort VariantTypeUi4 = 19;
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;
    private const uint ClsctxInprocServer = 0x1;
    private const int MftMessageSetD3DManager = 2;
    private const uint MfVideoInterlaceProgressive = 2;
    private const uint EAvEncH264VProfileMain = 77;
    private const long HnsPerSecond = 10_000_000;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const string H264Encoding = "h264";
    private const string DefaultProfile = "main";
    private const string UnsafeDirectNv12EnvironmentVariableName = "NLINK_SCREENSHARE_UNSAFE_DIRECT_NV12";
    private const string UnsafeFfmpegSwscaleEnvironmentVariableName = "NLINK_SCREENSHARE_UNSAFE_FFMPEG_SWSCALE";
    private static readonly Guid IidImfSinkWriter = new("3137f1cd-fe5e-4805-a5d8-fb477448cb3d");
    private static readonly Guid MftCategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid ClsidCmsH264EncoderMft = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidId3D11Texture2D = new("1841e5c8-16b0-489b-bcc8-44cfb0d5deae");
    private static readonly Guid IidIdxgiSurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    private static readonly Guid IidICodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtAllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MfMtFixedSizeSamples = new("b8ebefaf-b718-4e04-b0a9-116775e3321b");
    private static readonly Guid MfMtSampleSize = new("dad3ab78-1990-408b-bce2-eba673dacc10");
    private static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MfMtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtMpeg2Profile = new("ad76a80b-35e3-46bd-8f1d-b1a820f242b0");
    private static readonly Guid MfMtMpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MfReadwriteEnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MfReadwriteD3DOptional = new("216479d9-3071-42ca-bb6c-4c22102e1d18");
    private static readonly Guid MfSinkWriterD3DManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid MfSaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    private static readonly Guid MfSaD3D11BindFlags = new("eacf97ad-065c-4408-bee3-fdcbfd128be2");
    private static readonly Guid MfForcedKeyFrameDataUnitExtension = new("f72a3c6f-6eb4-4ebc-b192-09ad9759e828");
    private static readonly Guid CodecApiAvEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CodecApiAvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CodecApiAvEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8f4c1c1e-fad1-4bc9-9c5b-8964c8b24ce9");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6e6c7");
    private static readonly Guid CodecApiAvEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid CodecApiAvEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private const uint LowDelayQualityVsSpeedValue = 25;
    private const uint EAvEncCommonRateControlModeCbr = 0;
    private readonly object sync = new();
    [ThreadStatic] private static bool comInitializedForThread;
    private readonly int encoderInstanceId;
    private readonly string sourceRole;
    private readonly bool selectedHardwarePath;
    private readonly bool mediaFoundationLeaseHeld;
    private readonly Channel<EncodeWorkItem> workChannel;
    private readonly Thread workerThread;
    private bool disposed;
    private bool encoderFaulted;
    private EncoderConfiguration? configuration;
    private PersistentTransformSession? persistentTransformSession;
    private ReusablePreprocessState? reusablePreprocessState;
    private string? terminalInputBufferFailureSummary;
    private long currentStreamEpoch;
    private bool emitConfigOnNextFrame = true;
    private long? firstCapturedTsUtcMs;
    private long lastSampleTimeHns;
    private string encoderPath = "uninitialized";
    private long lastPreprocessDurationMs = -1;
    private long lastPreprocessResizeDurationMs = -1;
    private long lastPreprocessColorConvertDurationMs = -1;
    private string preprocessResizePath = string.Empty;
    private long preprocessDirectNv12Count;
    private long lastTransformEncodeDurationMs = -1;
    private long lastEncodeTotalDurationMs = -1;
    private long emittedDisplayableFrames;
    private long emittedNonDisplayableUnits;
    private long idrFramesEmitted;
    private long pFramesEmitted;
    private long droppedBFrames;
    private long droppedMultiPictureUnits;
    private long totalEncodedFrameBytes;
    private bool transportIpOnlyMode;
    private string lastAccessUnitKind = string.Empty;
    private string lowDelayConfigApplied = string.Empty;
    private bool senderContinuityRecoveryActive;
    private long senderContinuityLossCount;
    private long framesDroppedWaitingForRecoveryKeyframe;
    private string lastSenderContinuityLossReason = string.Empty;
    private bool senderContinuityWaitingLogged;
    private bool loggedPromotedStrategyUse;
    private bool loggedTerminalRootCauseSummary;
    private bool loggedTerminalEncodeFailure;
    private readonly ScreenShareMotionIntegrityGuard motionIntegrityGuard = new();
    private readonly ScreenShareMotionIdrProofTracker motionIdrProofTracker = new();
    private long motionIntegrityGuardActiveDisplayableFrames;
    private long motionIntegrityGuardActiveIdrFrames;

    private MediaFoundationH264FrameEncoder(bool selectedHardwarePath, bool mediaFoundationLeaseHeld, string sourceRole)
    {
        encoderInstanceId = Interlocked.Increment(ref nextEncoderInstanceId);
        this.sourceRole = string.IsNullOrWhiteSpace(sourceRole) ? "unknown" : sourceRole.Trim().ToLowerInvariant();
        this.selectedHardwarePath = selectedHardwarePath;
        this.mediaFoundationLeaseHeld = mediaFoundationLeaseHeld;
        workChannel = Channel.CreateUnbounded<EncodeWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "nLink-H264Encoder",
        };
        workerThread.SetApartmentState(ApartmentState.MTA);
        workerThread.Start();
    }

    public bool IsSupported => !disposed;

    public static IWindowsH264FrameEncoder? TryCreate(string sourceRole = "unknown")
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!MediaFoundationRuntime.TryAcquire())
        {
            return null;
        }

        try
        {
            var encoder = new MediaFoundationH264FrameEncoder(
                selectedHardwarePath: false,
                mediaFoundationLeaseHeld: true,
                sourceRole: sourceRole);
            encoder.LogInstanceLifecycle("screenshare_h264_encoder_selected", "path=software");
            return encoder;
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_encoder_probe_failed",
                $"source_role={Sanitize(sourceRole)}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
        }

        MediaFoundationRuntime.Release();
        return null;
    }

    public ValueTask<WindowsH264EncodedFrame?> EncodeAsync(
        WindowsRawCaptureFrame frame,
        WindowsH264EncodeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WindowsH264EncodedFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workItem = new EncodeWorkItem(frame, options, completion, cancellationToken);
        if (!workChannel.Writer.TryWrite(workItem))
        {
            throw new ObjectDisposedException(nameof(MediaFoundationH264FrameEncoder));
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state =>
            {
                var source = (TaskCompletionSource<WindowsH264EncodedFrame?>)state!;
                source.TrySetCanceled();
            }, completion);
        }

        return new ValueTask<WindowsH264EncodedFrame?>(completion.Task);
    }

    public void StartRecoveryBurst(string reason, long streamEpoch)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            var profileName = configuration?.ProfileName ?? DefaultProfile;
            EnterSenderContinuityRecovery(
                string.IsNullOrWhiteSpace(reason) ? "recovery_burst" : reason,
                Math.Max(0, streamEpoch),
                string.IsNullOrWhiteSpace(lastAccessUnitKind) ? "pending" : lastAccessUnitKind,
                bytes: 0,
                profileName);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (sync)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            ResetEncoderState();
        }

        workChannel.Writer.TryComplete();
        if (!ReferenceEquals(Thread.CurrentThread, workerThread))
        {
            workerThread.Join(TimeSpan.FromSeconds(5));
        }

        if (mediaFoundationLeaseHeld)
        {
            MediaFoundationRuntime.Release();
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsTransportSourceRole(string role)
        => string.Equals(role, "transport", StringComparison.OrdinalIgnoreCase);

    public WindowsH264FrameEncoderRuntimeMetrics GetRuntimeMetricsSnapshot()
    {
        lock (sync)
        {
            var totalAccessUnits = emittedDisplayableFrames + emittedNonDisplayableUnits;
            var averageEncodedFrameBytes = emittedDisplayableFrames > 0
                ? totalEncodedFrameBytes / (double)emittedDisplayableFrames
                : 0d;
            var motionSnapshot = motionIntegrityGuard.GetSnapshot();
            var motionIdrProofSnapshot = motionIdrProofTracker.GetSnapshot();
            return new WindowsH264FrameEncoderRuntimeMetrics(
                EncoderPath: encoderPath,
                EmittedDisplayableFrames: emittedDisplayableFrames,
                EmittedNonDisplayableUnits: emittedNonDisplayableUnits,
                DisplayableFrameRatio: totalAccessUnits > 0 ? emittedDisplayableFrames / (double)totalAccessUnits : 0d,
                IdrFramesEmitted: idrFramesEmitted,
                PFramesEmitted: pFramesEmitted,
                DroppedBFrames: droppedBFrames,
                DroppedMultiPictureUnits: droppedMultiPictureUnits,
                IdrFrameRatio: emittedDisplayableFrames > 0 ? idrFramesEmitted / (double)emittedDisplayableFrames : 0d,
                AverageEncodedFrameBytes: averageEncodedFrameBytes,
                TransportIpOnlyMode: transportIpOnlyMode,
                LastAccessUnitKind: lastAccessUnitKind,
                LowDelayConfigApplied: lowDelayConfigApplied,
                SenderContinuityRecoveryActive: senderContinuityRecoveryActive,
                SenderContinuityLossCount: senderContinuityLossCount,
                FramesDroppedWaitingForRecoveryKeyframe: framesDroppedWaitingForRecoveryKeyframe,
                LastSenderContinuityLossReason: lastSenderContinuityLossReason,
                LastPreprocessDurationMs: lastPreprocessDurationMs,
                LastPreprocessResizeDurationMs: lastPreprocessResizeDurationMs,
                LastPreprocessColorConvertDurationMs: lastPreprocessColorConvertDurationMs,
                PreprocessResizePath: preprocessResizePath,
                PreprocessDirectNv12Count: preprocessDirectNv12Count,
                LastTransformEncodeDurationMs: lastTransformEncodeDurationMs,
                LastEncodeTotalDurationMs: lastEncodeTotalDurationMs,
                MotionIntegrityGuardActive: motionSnapshot.Active,
                MotionIntegritySampledRatio: motionSnapshot.SampledMotionRatio,
                MotionIntegrityPeakSampledRatio: motionSnapshot.PeakSampledMotionRatio,
                MotionIntegrityScrollMotionActiveBandCount: motionSnapshot.ScrollMotionActiveBandCount,
                MotionIntegrityScrollMotionPeakBandRatio: motionSnapshot.ScrollMotionPeakBandRatio,
                MotionIntegrityHighMotionFrameCount: motionSnapshot.HighMotionFrameCount,
                MotionIntegrityScrollTriggerCount: motionSnapshot.ScrollTriggerCount,
                MotionIntegrityBurstEnterCount: motionSnapshot.BurstEnterCount,
                MotionIntegrityBurstExitCount: motionSnapshot.BurstExitCount,
                MotionIntegrityForcedKeyFrameCount: motionSnapshot.ForcedKeyFrameCount,
                MotionIntegrityLastTriggerKind: motionSnapshot.LastTriggerKind,
                MotionIntegrityLastReason: motionSnapshot.LastReason,
                MotionIntegrityIdrFrameRatio: motionIntegrityGuardActiveDisplayableFrames > 0
                    ? motionIntegrityGuardActiveIdrFrames / (double)motionIntegrityGuardActiveDisplayableFrames
                    : 0d,
                MotionIntegrityForcedIdrRequestedCount: motionIdrProofSnapshot.RequestedCount,
                MotionIntegrityForcedIdrConfirmedCount: motionIdrProofSnapshot.ConfirmedCount,
                MotionIntegrityForcedIdrMissedCount: motionIdrProofSnapshot.MissedCount,
                MotionIntegrityForcedIdrPendingCount: motionIdrProofSnapshot.PendingCount,
                MotionIntegrityForcedIdrConsecutiveMissCount: motionIdrProofSnapshot.ConsecutiveMissCount,
                MotionIntegrityForcedIdrBurstMissCount: motionIdrProofSnapshot.BurstMissCount,
                MotionIntegrityActiveIdrFrameRatio: motionIdrProofSnapshot.ActiveMotionIdrFrameRatio,
                MotionIntegrityForcedIdrLastMissReason: motionIdrProofSnapshot.LastMissReason,
                MotionIntegrityEncoderRebuildCount: motionIdrProofSnapshot.EncoderRebuildCount,
                MotionIntegrityEncoderRebuildSuppressedCount: motionIdrProofSnapshot.EncoderRebuildSuppressedCount,
                MotionIntegrityEncoderRebuildPending: motionIdrProofSnapshot.EncoderRebuildPending,
                MotionIntegrityEncoderRebuildLastReason: motionIdrProofSnapshot.LastRebuildReason);
        }
    }

    private void WorkerLoop()
    {
        EnsureComInitializedForCurrentThread();
        try
        {
            while (workChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (workChannel.Reader.TryRead(out var workItem))
                {
                    if (workItem.CancellationToken.IsCancellationRequested)
                    {
                        workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                        continue;
                    }

                    try
                    {
                        WindowsH264EncodedFrame? result;
                        lock (sync)
                        {
                            if (disposed)
                            {
                                throw new ObjectDisposedException(nameof(MediaFoundationH264FrameEncoder));
                            }

                            result = EncodeCore(workItem.Frame, workItem.Options);
                        }

                        workItem.Completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException oce)
                    {
                        workItem.Completion.TrySetCanceled(oce.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        if (ex is not RawInputBufferStrategyUnavailableException)
                        {
                            lock (sync)
                            {
                                encoderFaulted = true;
                                ResetEncoderState();
                            }
                        }

                        workItem.Completion.TrySetException(ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            while (workChannel.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetException(ex);
            }
        }
    }

    private WindowsH264EncodedFrame? EncodeCore(WindowsRawCaptureFrame frame, WindowsH264EncodeOptions options)
    {
        var stage = "normalize_dimensions";
        try
        {
            if (terminalInputBufferFailureSummary is not null)
            {
                throw new RawInputBufferStrategyUnavailableException(terminalInputBufferFailureSummary);
            }

            var transportIpOnly = IsTransportSourceRole(sourceRole);
            var encodeProfile = WindowsH264EncodePolicy.ResolveProfile(
                frame.Bitmap.Width,
                frame.Bitmap.Height,
                options.TargetFramesPerSecond,
                options.TuningLevel,
                transportIpOnly);
            var recoveryForceKeyFrame = options.ForceKeyFrame || (transportIpOnly && senderContinuityRecoveryActive);
            var motionGuardEligible =
                encodeProfile.TransportIpOnly &&
                options.TuningLevel == ScreenShareTransportTuningLevel.Normal &&
                string.Equals(encodeProfile.ProfileName, "normal", StringComparison.OrdinalIgnoreCase);
            var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var motionSafetyRebuild = motionIdrProofTracker.TryConsumeEncoderRebuild(nowUtcMs, motionGuardEligible);
            stage = "compute_bitrate";
            var nextBitrate = encodeProfile.TargetBitrate;
            stage = "evaluate_rebuild";
            var mustRebuild =
                configuration is null ||
                encoderFaulted ||
                configuration.Width != encodeProfile.Width ||
                configuration.Height != encodeProfile.Height ||
                configuration.TargetFramesPerSecond != encodeProfile.TargetFramesPerSecond ||
                configuration.TargetBitrate != nextBitrate ||
                !string.Equals(configuration.ProfileName, encodeProfile.ProfileName, StringComparison.Ordinal) ||
                currentStreamEpoch != options.StreamEpoch ||
                motionSafetyRebuild;

            if (mustRebuild)
            {
                stage = "rebuild_encoder";
                RebuildEncoder(encodeProfile, options, recoveryForceKeyFrame || motionSafetyRebuild);
                if (motionSafetyRebuild)
                {
                    LogInstanceLifecycle(
                        "screenshare_h264_motion_integrity_encoder_rebuilt",
                        $"epoch={options.StreamEpoch}; profile={Sanitize(encodeProfile.ProfileName)}; width={encodeProfile.Width}; height={encodeProfile.Height}; fps={encodeProfile.TargetFramesPerSecond}; bitrate={encodeProfile.TargetBitrate}; reason=forced_idr_miss");
                }
            }

            var activeConfiguration = configuration ?? throw new InvalidOperationException("H.264 encoder configuration is not initialized.");
            stage = "apply_dynamic_tuning";
            ApplyDynamicTuning(activeConfiguration, encodeProfile.TargetFramesPerSecond, nextBitrate);
            var emitStreamConfig = emitConfigOnNextFrame;

            var preprocessStartedAt = Stopwatch.GetTimestamp();
            stage = "prepare_nv12";
            var nv12Bytes = PrepareNv12Bytes(frame.Bitmap, activeConfiguration.Width, activeConfiguration.Height, options.TuningLevel);
            var preprocessDurationMs = (long)Stopwatch.GetElapsedTime(preprocessStartedAt).TotalMilliseconds;
            stage = "evaluate_motion_integrity";
            var motionDecision = motionIntegrityGuard.Evaluate(
                nv12Bytes,
                activeConfiguration.Width,
                activeConfiguration.Height,
                nowUtcMs,
                motionGuardEligible,
                recoveryForceKeyFrame);
            var effectiveForceKeyFrame = recoveryForceKeyFrame || motionSafetyRebuild || motionDecision.ShouldForceKeyFrame;
            var motionGuardActiveForFrame = motionDecision.Snapshot.Active;
            if (motionDecision.ShouldForceKeyFrame)
            {
                motionIdrProofTracker.ObserveMotionForcedKeyFrameRequested(nowUtcMs);
                LogInstanceLifecycle(
                    "screenshare_h264_motion_integrity_keyframe_forced",
                    $"epoch={options.StreamEpoch}; profile={Sanitize(activeConfiguration.ProfileName)}; sampled_motion_ratio={motionDecision.Snapshot.SampledMotionRatio.ToString("F3", CultureInfo.InvariantCulture)}; peak_sampled_motion_ratio={motionDecision.Snapshot.PeakSampledMotionRatio.ToString("F3", CultureInfo.InvariantCulture)}; scroll_active_band_count={motionDecision.Snapshot.ScrollMotionActiveBandCount}; scroll_peak_band_ratio={motionDecision.Snapshot.ScrollMotionPeakBandRatio.ToString("F3", CultureInfo.InvariantCulture)}; last_trigger_kind={Sanitize(motionDecision.Snapshot.LastTriggerKind)}; burst_enter_count={motionDecision.Snapshot.BurstEnterCount}; forced_motion_keyframe_count={motionDecision.Snapshot.ForcedKeyFrameCount}");
            }

            stage = "compute_sample_time";
            var sampleTimeHns = ComputeSampleTimeHns(frame.CapturedTsUtcMs);
            var sampleDurationHns = ComputeSampleDurationHns(activeConfiguration.TargetFramesPerSecond);

            EncoderEncodeResult encodeResult;
            if (persistentTransformSession is not null)
            {
                stage = "process_transform";
                try
                {
                    encodeResult = EncodeSingleFrameToPersistentTransform(
                        persistentTransformSession,
                        activeConfiguration,
                        nv12Bytes,
                        sampleTimeHns,
                        sampleDurationHns,
                        effectiveForceKeyFrame,
                        GetLogContext());
                }
                catch (Exception ex)
                {
                    stage = "persistent_transform_fallback";
                    LogInstanceLifecycle(
                        "screenshare_h264_encoder_path_fallback",
                        $"from=persistent_transform; to=sink_writer_fallback; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                    SwitchToSinkWriterFallback(encodeProfile, options, effectiveForceKeyFrame);
                    activeConfiguration = configuration ?? throw new InvalidOperationException("H.264 encoder fallback configuration is not initialized.");
                    emitStreamConfig = emitConfigOnNextFrame;
                    encodeResult = EncodeSingleFrameToSinkWriter(
                        activeConfiguration,
                        nv12Bytes,
                        sampleTimeHns,
                        sampleDurationHns,
                        effectiveForceKeyFrame,
                        GetLogContext());
                }
            }
            else
            {
                stage = "write_sample";
                encodeResult = EncodeSingleFrameToSinkWriter(
                    activeConfiguration,
                    nv12Bytes,
                    sampleTimeHns,
                    sampleDurationHns,
                    effectiveForceKeyFrame,
                    GetLogContext());
            }

            lastPreprocessDurationMs = preprocessDurationMs;
            lastTransformEncodeDurationMs = encodeResult.TransformEncodeDurationMs;
            lastEncodeTotalDurationMs = preprocessDurationMs + Math.Max(0, encodeResult.EncodeDurationMs);
            var encodedBytes = encodeResult.EncodedBytes;
            if (encodedBytes.Length == 0)
            {
                LogInstanceLifecycle(
                    encoderPath == "persistent_transform"
                        ? "screenshare_h264_transform_output_empty"
                        : "screenshare_h264_sink_writer_output_empty",
                    $"width={activeConfiguration.Width}; height={activeConfiguration.Height}; fps={activeConfiguration.TargetFramesPerSecond}; bitrate={activeConfiguration.TargetBitrate}");
                return null;
            }

            if (encodeResult.DecoderConfigData.Length > 0 &&
                !ByteArrayEquals(activeConfiguration.DecoderConfigData, encodeResult.DecoderConfigData))
            {
                activeConfiguration.DecoderConfigData = encodeResult.DecoderConfigData;
                activeConfiguration.NalLengthSize = ResolveNalLengthSize(activeConfiguration.DecoderConfigData, activeConfiguration.NalLengthSize);
                emitStreamConfig = true;
            }

            var accessUnitClassification = AnalyzeAccessUnit(encodedBytes);
            lastAccessUnitKind = accessUnitClassification.Kind;
            var accessUnitDecoderConfig = encodeResult.DecoderConfigData;
            if (accessUnitClassification.HasSpsOrPps)
            {
                var derivedDecoderConfig = BuildAvcConfigurationFromAnnexB(
                    encodedBytes,
                    activeConfiguration.NalLengthSize > 0 ? activeConfiguration.NalLengthSize : 4);
                if (derivedDecoderConfig.Length > 0)
                {
                    accessUnitDecoderConfig = derivedDecoderConfig;
                }
            }

            if (accessUnitDecoderConfig.Length > 0 &&
                !ByteArrayEquals(activeConfiguration.DecoderConfigData, accessUnitDecoderConfig))
            {
                activeConfiguration.DecoderConfigData = accessUnitDecoderConfig;
                activeConfiguration.NalLengthSize = ResolveNalLengthSize(activeConfiguration.DecoderConfigData, activeConfiguration.NalLengthSize);
                emitStreamConfig = true;
            }

            if (!accessUnitClassification.HasDisplayableVcl)
            {
                emittedNonDisplayableUnits++;
                if (FeatureFlags.ScreenShareDeepDiagnostics)
                {
                    LogInstanceLifecycle(
                        "screenshare_h264_encoder_non_displayable_unit_filtered",
                        $"kind={accessUnitClassification.Kind}; bytes={encodedBytes.Length}; epoch={options.StreamEpoch}; decoder_config_bytes={activeConfiguration.DecoderConfigData.Length}");
                }

                return null;
            }

            if (activeConfiguration.TransportIpOnlyMode && accessUnitClassification.PrimaryPictureCount != 1)
            {
                droppedMultiPictureUnits++;
                lastAccessUnitKind = accessUnitClassification.Kind;
                EnterSenderContinuityRecovery(
                    "invalid_primary_picture_count",
                    options.StreamEpoch,
                    accessUnitClassification.Kind,
                    encodedBytes.Length,
                    activeConfiguration.ProfileName);
                LogInstanceLifecycle(
                    "screenshare_h264_transport_contract_failure",
                    $"reason=invalid_primary_picture_count; kind={accessUnitClassification.Kind}; primary_picture_count={accessUnitClassification.PrimaryPictureCount}; bytes={encodedBytes.Length}; epoch={options.StreamEpoch}; profile={Sanitize(activeConfiguration.ProfileName)}");
                return null;
            }

            if (activeConfiguration.TransportIpOnlyMode && accessUnitClassification.HasBPicture)
            {
                droppedBFrames++;
                lastAccessUnitKind = "b_vcl";
                EnterSenderContinuityRecovery(
                    "b_picture_detected",
                    options.StreamEpoch,
                    lastAccessUnitKind,
                    encodedBytes.Length,
                    activeConfiguration.ProfileName);
                LogInstanceLifecycle(
                    "screenshare_h264_transport_contract_failure",
                    $"reason=b_picture_detected; kind={accessUnitClassification.Kind}; bytes={encodedBytes.Length}; epoch={options.StreamEpoch}; profile={Sanitize(activeConfiguration.ProfileName)}");
                return null;
            }

            if (activeConfiguration.TransportIpOnlyMode &&
                accessUnitClassification.PictureKind is AccessUnitPictureKind.Unknown or AccessUnitPictureKind.Unsupported)
            {
                EnterSenderContinuityRecovery(
                    "unsupported_picture_kind",
                    options.StreamEpoch,
                    accessUnitClassification.Kind,
                    encodedBytes.Length,
                    activeConfiguration.ProfileName);
                LogInstanceLifecycle(
                    "screenshare_h264_transport_contract_failure",
                    $"reason=unsupported_picture_kind; kind={accessUnitClassification.Kind}; bytes={encodedBytes.Length}; epoch={options.StreamEpoch}; profile={Sanitize(activeConfiguration.ProfileName)}");
                return null;
            }

            if (activeConfiguration.TransportIpOnlyMode &&
                senderContinuityRecoveryActive &&
                !accessUnitClassification.HasIdr)
            {
                framesDroppedWaitingForRecoveryKeyframe++;
                lastAccessUnitKind = accessUnitClassification.Kind;
                LogSenderWaitingForRecoveryKeyframe(
                    options.StreamEpoch,
                    accessUnitClassification.Kind,
                    encodedBytes.Length,
                    activeConfiguration.ProfileName);
                return null;
            }

            if (activeConfiguration.TransportIpOnlyMode &&
                senderContinuityRecoveryActive &&
                accessUnitClassification.HasIdr)
            {
                ExitSenderContinuityRecovery(
                    options.StreamEpoch,
                    accessUnitClassification.Kind,
                    encodedBytes.Length,
                    activeConfiguration.ProfileName);
            }

            emitConfigOnNextFrame = false;
            stage = "build_stream_config";
            var streamConfig = emitStreamConfig ? BuildStreamConfig(activeConfiguration, options.StreamEpoch) : null;
            var isKeyFrame = accessUnitClassification.HasIdr;
            activeConfiguration.PendingFirstFrame = false;
            emittedDisplayableFrames++;
            if (motionGuardActiveForFrame)
            {
                motionIntegrityGuardActiveDisplayableFrames++;
            }

            if (accessUnitClassification.HasIdr)
            {
                idrFramesEmitted++;
                if (motionGuardActiveForFrame)
                {
                    motionIntegrityGuardActiveIdrFrames++;
                }
            }
            else if (accessUnitClassification.PictureKind == AccessUnitPictureKind.P)
            {
                pFramesEmitted++;
            }

            motionIdrProofTracker.ObserveDisplayableOutput(
                accessUnitClassification.HasIdr,
                motionGuardActiveForFrame,
                motionGuardEligible,
                nowUtcMs);

            totalEncodedFrameBytes += encodedBytes.Length;

            if (streamConfig is not null)
            {
                LogInstanceLifecycle(
                    "screenshare_h264_encoder_stream_config_emitted",
                    $"epoch={options.StreamEpoch}; profile={Sanitize(streamConfig.CodecProfile)}; config_bytes={streamConfig.DecoderConfigData.Length}; width={activeConfiguration.Width}; height={activeConfiguration.Height}");
            }

            if (encodedBytes.Length <= 64 || streamConfig is not null)
            {
                LogInstanceLifecycle(
                    "screenshare_h264_encoder_frame_emitted",
                    $"epoch={options.StreamEpoch}; bytes={encodedBytes.Length}; is_key_frame={(isKeyFrame ? 1 : 0)}; extracted_key_frame={(encodeResult.IsKeyFrame ? 1 : 0)}; config_bytes={encodeResult.DecoderConfigData.Length}; prefix={HexPrefix(encodedBytes, 16)}; transport_ip_only_mode={(activeConfiguration.TransportIpOnlyMode ? 1 : 0)}");
            }

            return new WindowsH264EncodedFrame(
                EncodedBytes: encodedBytes,
                Width: activeConfiguration.Width,
                Height: activeConfiguration.Height,
                CapturedTsUtcMs: frame.CapturedTsUtcMs,
                IsKeyFrame: isKeyFrame,
                StreamEpoch: options.StreamEpoch,
                StreamConfig: streamConfig);
        }
        catch (Exception ex)
        {
            if (ex is RawInputBufferStrategyUnavailableException && !loggedTerminalRootCauseSummary)
            {
                loggedTerminalRootCauseSummary = true;
                LogInstanceLifecycle(
                    "screenshare_h264_terminal_root_cause",
                    $"root_cause={lastInputBufferRootCause}; summary={Sanitize(terminalInputBufferFailureSummary ?? lastInputBufferProbeSummary)}");
            }

            var suppressDetailedFailureLog = ex is RawInputBufferStrategyUnavailableException && loggedTerminalEncodeFailure;
            if (!suppressDetailedFailureLog && FeatureFlags.ScreenShareDeepDiagnostics)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_h264_encode_core_failed; {GetLogContext()}; stage={stage}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            }

            if (ex is RawInputBufferStrategyUnavailableException)
            {
                loggedTerminalEncodeFailure = true;
            }

            throw;
        }
    }

    private void RebuildEncoder(WindowsH264EncodeProfile encodeProfile, WindowsH264EncodeOptions options, bool forceKeyFrame)
    {
        ResetEncoderState();

        var width = encodeProfile.Width;
        var height = encodeProfile.Height;
        var bitrate = encodeProfile.TargetBitrate;
        var profileOptions = options with { TargetFramesPerSecond = encodeProfile.TargetFramesPerSecond };
        configuration = TryCreatePersistentTransformSession(encodeProfile, profileOptions)
                        ?? CreateSinkWriterFallbackConfiguration(encodeProfile, profileOptions);
        encoderFaulted = false;
        terminalInputBufferFailureSummary = null;
        currentStreamEpoch = options.StreamEpoch;
        emitConfigOnNextFrame = true;
        firstCapturedTsUtcMs = null;
        lastSampleTimeHns = 0;
        lastPreprocessDurationMs = -1;
        lastPreprocessResizeDurationMs = -1;
        lastPreprocessColorConvertDurationMs = -1;
        preprocessResizePath = string.Empty;
        preprocessDirectNv12Count = 0;
        lastTransformEncodeDurationMs = -1;
        lastEncodeTotalDurationMs = -1;
        emittedDisplayableFrames = 0;
        emittedNonDisplayableUnits = 0;
        idrFramesEmitted = 0;
        totalEncodedFrameBytes = 0;
        transportIpOnlyMode = encodeProfile.TransportIpOnly;
        lastAccessUnitKind = string.Empty;
        senderContinuityRecoveryActive = false;
        senderContinuityLossCount = 0;
        framesDroppedWaitingForRecoveryKeyframe = 0;
        lastSenderContinuityLossReason = string.Empty;
        senderContinuityWaitingLogged = false;
        loggedTerminalRootCauseSummary = false;
        loggedTerminalEncodeFailure = false;

        if (forceKeyFrame)
        {
            LogInstanceLifecycle(
                "screenshare_h264_encoder_forced_keyframe",
                $"width={width}; height={height}; epoch={options.StreamEpoch}");
        }

        LogInstanceLifecycle(
            "screenshare_h264_encoder_rebuilt",
            $"path={(selectedHardwarePath ? "hardware" : "software")}; encoder_path={encoderPath}; profile={encodeProfile.ProfileName}; width={width}; height={height}; fps={encodeProfile.TargetFramesPerSecond}; bitrate={bitrate}; epoch={options.StreamEpoch}; transport_ip_only_mode={(encodeProfile.TransportIpOnly ? 1 : 0)}");
    }

    private byte[] PrepareNv12Bytes(Bitmap source, int targetWidth, int targetHeight, ScreenShareTransportTuningLevel tuningLevel)
    {
        if (reusablePreprocessState is null ||
            reusablePreprocessState.Width != targetWidth ||
            reusablePreprocessState.Height != targetHeight)
        {
            reusablePreprocessState?.Dispose();
            reusablePreprocessState = new ReusablePreprocessState(targetWidth, targetHeight);
        }

        var result = reusablePreprocessState.PrepareNv12(source, tuningLevel);
        lastPreprocessResizeDurationMs = result.ResizeDurationMs;
        lastPreprocessColorConvertDurationMs = result.ColorConvertDurationMs;
        preprocessResizePath = result.ResizePath;
        if (result.DirectNv12)
        {
            preprocessDirectNv12Count++;
        }

        return result.Nv12Bytes;
    }

    private EncoderConfiguration? TryCreatePersistentTransformSession(
        WindowsH264EncodeProfile encodeProfile,
        WindowsH264EncodeOptions options)
    {
        var sampleDurationHns = ComputeSampleDurationHns(encodeProfile.TargetFramesPerSecond);
        var bufferLength = checked(encodeProfile.Width * encodeProfile.Height * 3 / 2);
        var preferredStrategy = EnsureInputBufferStrategy(
            encodeProfile.Width,
            encodeProfile.Height,
            encodeProfile.TargetFramesPerSecond,
            bufferLength,
            sampleDurationHns);

        foreach (var strategy in EnumerateStrategyAttempts(preferredStrategy))
        {
            IMFTransform? encoderTransform = null;
            IMFDXGIDeviceManager? deviceManager = null;
            IntPtr d3dDevice = IntPtr.Zero;
            IntPtr d3dContext = IntPtr.Zero;
            try
            {
                if (!TryCreateSoftwareTransform(out encoderTransform) || encoderTransform is null)
                {
                    continue;
                }

                if (strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate &&
                    !TryCreateSinkWriterDeviceManager(out deviceManager, out d3dDevice, out d3dContext, out _))
                {
                    continue;
                }

                var configurationResult = ConfigureTransformCore(
                    encoderTransform,
                    encodeProfile.Width,
                    encodeProfile.Height,
                    options,
                    encodeProfile.TransportIpOnly,
                    encodeProfile.TargetBitrate,
                    deviceManager,
                    strategy.ToString().ToLowerInvariant(),
                    probeMode: false);
                configurationResult.Configuration.ProfileName = encodeProfile.ProfileName;
                configurationResult.Configuration.NalLengthSize = ResolveNalLengthSize(
                    configurationResult.Configuration.DecoderConfigData,
                    configurationResult.Configuration.NalLengthSize);
                lowDelayConfigApplied = $"persistent_transform_{configurationResult.LowDelayConfigApplied}";
                persistentTransformSession = new PersistentTransformSession(
                    encoderTransform,
                    deviceManager,
                    d3dDevice,
                    d3dContext,
                    configurationResult.Configuration,
                    strategy);
                encoderTransform = null;
                deviceManager = null;
                d3dDevice = IntPtr.Zero;
                d3dContext = IntPtr.Zero;
                encoderPath = "persistent_transform";
                LogInstanceLifecycle(
                    "screenshare_h264_encoder_path_selected",
                    $"encoder_path=persistent_transform; strategy={strategy.ToString().ToLowerInvariant()}; profile={encodeProfile.ProfileName}; width={encodeProfile.Width}; height={encodeProfile.Height}; fps={encodeProfile.TargetFramesPerSecond}; bitrate={encodeProfile.TargetBitrate}; transport_ip_only_mode={(encodeProfile.TransportIpOnly ? 1 : 0)}");
                return persistentTransformSession.Configuration;
            }
            catch (Exception ex)
            {
                LogInstanceLifecycle(
                    "screenshare_h264_encoder_path_probe_failed",
                    $"encoder_path=persistent_transform; strategy={strategy.ToString().ToLowerInvariant()}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            }
            finally
            {
                ReleaseComObject(encoderTransform);
                ReleaseComObject(deviceManager);
                if (d3dContext != IntPtr.Zero)
                {
                    Marshal.Release(d3dContext);
                }

                if (d3dDevice != IntPtr.Zero)
                {
                    Marshal.Release(d3dDevice);
                }
            }
        }

        return null;
    }

    private EncoderConfiguration CreateSinkWriterFallbackConfiguration(
        WindowsH264EncodeProfile encodeProfile,
        WindowsH264EncodeOptions options)
    {
        encoderPath = "sink_writer_fallback";
        lowDelayConfigApplied = string.Empty;
        LogInstanceLifecycle(
            "screenshare_h264_encoder_path_selected",
            $"encoder_path=sink_writer_fallback; profile={encodeProfile.ProfileName}; width={encodeProfile.Width}; height={encodeProfile.Height}; fps={encodeProfile.TargetFramesPerSecond}; bitrate={encodeProfile.TargetBitrate}; transport_ip_only_mode={(encodeProfile.TransportIpOnly ? 1 : 0)}");
        var configuration = CreateSinkWriterConfiguration(encodeProfile, options, encodeProfile.TargetBitrate);
        configuration.ProfileName = encodeProfile.ProfileName;
        return configuration;
    }

    private void SwitchToSinkWriterFallback(
        WindowsH264EncodeProfile encodeProfile,
        WindowsH264EncodeOptions options,
        bool forceKeyFrame)
    {
        persistentTransformSession?.Dispose();
        persistentTransformSession = null;
        var fallbackConfiguration = CreateSinkWriterFallbackConfiguration(
            encodeProfile,
            options with { TargetFramesPerSecond = encodeProfile.TargetFramesPerSecond });
        configuration = fallbackConfiguration;
        emitConfigOnNextFrame = true;
        firstCapturedTsUtcMs = null;
        lastSampleTimeHns = 0;
        if (forceKeyFrame)
        {
            LogInstanceLifecycle(
                "screenshare_h264_encoder_forced_keyframe",
                $"width={encodeProfile.Width}; height={encodeProfile.Height}; epoch={options.StreamEpoch}");
        }
    }

    private EncoderEncodeResult EncodeSingleFrameToPersistentTransform(
        PersistentTransformSession session,
        EncoderConfiguration activeConfiguration,
        byte[] nv12Bytes,
        long sampleTimeHns,
        long sampleDurationHns,
        bool forceKeyFrame,
        string logContext)
    {
        var encodeStartedAt = Stopwatch.GetTimestamp();
        IMFSample? inputSample = null;
        try
        {
            inputSample = CreateInputSample(
                session.InputStrategy,
                activeConfiguration,
                nv12Bytes,
                sampleTimeHns,
                sampleDurationHns,
                forceKeyFrame,
                session.D3DDevice,
                session.D3DContext);

            if (forceKeyFrame)
            {
                _ = TryRequestTransformKeyFrame(session.EncoderTransform);
            }

            var hr = session.EncoderTransform.ProcessInput(0, inputSample, 0);
            if (hr == MfNotAccepting)
            {
                DrainPendingOutput(session.EncoderTransform, activeConfiguration);
                hr = session.EncoderTransform.ProcessInput(0, inputSample, 0);
            }

            Marshal.ThrowExceptionForHR(hr);

            if (!TryProcessSingleOutput(session.EncoderTransform, activeConfiguration, out var encodedBytes) ||
                encodedBytes.Length == 0)
            {
                return new EncoderEncodeResult(Array.Empty<byte>(), activeConfiguration.DecoderConfigData, false, (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds, (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds);
            }

            var normalizedBytes = NormalizeTransformOutputBytes(
                encodedBytes,
                activeConfiguration,
                out var decoderConfigData);
            if (decoderConfigData.Length == 0)
            {
                decoderConfigData = activeConfiguration.DecoderConfigData;
            }

            var isKeyFrame = ContainsIdrNalUnit(normalizedBytes);
            return new EncoderEncodeResult(
                normalizedBytes,
                decoderConfigData,
                isKeyFrame,
                (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds);
        }
        catch (Exception ex) when (IsTransformNeedMoreInputException(ex))
        {
            var totalDurationMs = (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds;
            if (FeatureFlags.ScreenShareDeepDiagnostics)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_h264_transform_needs_more_input; {logContext}; path=persistent_transform; sample_time={sampleTimeHns}; sample_duration={sampleDurationHns}; bytes={nv12Bytes.Length}");
            }

            return new EncoderEncodeResult(
                Array.Empty<byte>(),
                activeConfiguration.DecoderConfigData,
                false,
                totalDurationMs,
                totalDurationMs);
        }
        finally
        {
            ReleaseComObject(inputSample);
        }
    }

    private void ResetEncoderState()
    {
        persistentTransformSession?.Dispose();
        persistentTransformSession = null;
        reusablePreprocessState?.Dispose();
        reusablePreprocessState = null;
        configuration = null;
        firstCapturedTsUtcMs = null;
        lastSampleTimeHns = 0;
        emitConfigOnNextFrame = true;
        terminalInputBufferFailureSummary = null;
        encoderPath = "uninitialized";
        lastPreprocessDurationMs = -1;
        lastPreprocessResizeDurationMs = -1;
        lastPreprocessColorConvertDurationMs = -1;
        preprocessResizePath = string.Empty;
        preprocessDirectNv12Count = 0;
        lastTransformEncodeDurationMs = -1;
        lastEncodeTotalDurationMs = -1;
        emittedDisplayableFrames = 0;
        emittedNonDisplayableUnits = 0;
        idrFramesEmitted = 0;
        pFramesEmitted = 0;
        droppedBFrames = 0;
        droppedMultiPictureUnits = 0;
        totalEncodedFrameBytes = 0;
        motionIntegrityGuardActiveDisplayableFrames = 0;
        motionIntegrityGuardActiveIdrFrames = 0;
        motionIntegrityGuard.Reset("encoder_reset");
        motionIdrProofTracker.Reset("encoder_reset");
        transportIpOnlyMode = false;
        lastAccessUnitKind = string.Empty;
        lowDelayConfigApplied = string.Empty;
        senderContinuityRecoveryActive = false;
        senderContinuityLossCount = 0;
        framesDroppedWaitingForRecoveryKeyframe = 0;
        lastSenderContinuityLossReason = string.Empty;
        senderContinuityWaitingLogged = false;
        loggedTerminalRootCauseSummary = false;
        loggedTerminalEncodeFailure = false;
    }

    private void EnterSenderContinuityRecovery(
        string reason,
        long streamEpoch,
        string accessUnitKind,
        int bytes,
        string profileName)
    {
        lastSenderContinuityLossReason = string.IsNullOrWhiteSpace(reason) ? "continuity_loss" : reason.Trim();
        if (senderContinuityRecoveryActive)
        {
            return;
        }

        senderContinuityRecoveryActive = true;
        senderContinuityLossCount++;
        senderContinuityWaitingLogged = false;
        LogInstanceLifecycle(
            "screenshare_sender_continuity_lost",
            $"reason={Sanitize(lastSenderContinuityLossReason)}; epoch={streamEpoch}; kind={Sanitize(accessUnitKind)}; bytes={bytes}; profile={Sanitize(profileName)}; recovery_active=1");
    }

    private void LogSenderWaitingForRecoveryKeyframe(
        long streamEpoch,
        string accessUnitKind,
        int bytes,
        string profileName)
    {
        if (senderContinuityWaitingLogged)
        {
            return;
        }

        senderContinuityWaitingLogged = true;
        LogInstanceLifecycle(
            "screenshare_sender_waiting_for_recovery_keyframe",
            $"reason={Sanitize(lastSenderContinuityLossReason)}; epoch={streamEpoch}; kind={Sanitize(accessUnitKind)}; bytes={bytes}; profile={Sanitize(profileName)}; dropped_non_key_frames={framesDroppedWaitingForRecoveryKeyframe}");
    }

    private void ExitSenderContinuityRecovery(
        long streamEpoch,
        string accessUnitKind,
        int bytes,
        string profileName)
    {
        if (!senderContinuityRecoveryActive)
        {
            return;
        }

        senderContinuityRecoveryActive = false;
        senderContinuityWaitingLogged = false;
        LogInstanceLifecycle(
            "screenshare_sender_recovery_keyframe_emitted",
            $"reason={Sanitize(lastSenderContinuityLossReason)}; epoch={streamEpoch}; kind={Sanitize(accessUnitKind)}; bytes={bytes}; profile={Sanitize(profileName)}; dropped_non_key_frames={framesDroppedWaitingForRecoveryKeyframe}");
        lastSenderContinuityLossReason = string.Empty;
    }

    private static void ApplyDynamicTuning(EncoderConfiguration configuration, int targetFramesPerSecond, uint targetBitrate)
    {
        configuration.TargetFramesPerSecond = Math.Max(1, targetFramesPerSecond);
        configuration.TargetBitrate = targetBitrate;
    }

    private static bool TryProbeSinkWriter()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"nlink-mf-probe-{Guid.NewGuid():N}.mp4");
        SinkWriterContext? sinkWriterContext = null;
        IMFSinkWriter? sinkWriter = null;
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        try
        {
            outputType = CreateOutputMediaType(16, 16, 1, 1_500_000);
            inputType = CreateInputMediaType(16, 16, 1);
            sinkWriterContext = CreateSinkWriter(tempPath);
            sinkWriter = sinkWriterContext.Writer;
            Marshal.ThrowExceptionForHR(sinkWriter.AddStream(outputType, out var streamIndex));
            Marshal.ThrowExceptionForHR(sinkWriter.SetInputMediaType(streamIndex, inputType, null));
            Marshal.ThrowExceptionForHR(sinkWriter.BeginWriting());
            Marshal.ThrowExceptionForHR(sinkWriter.Finalize_());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(sinkWriter);
            ReleaseComObject(outputType);
            ReleaseComObject(inputType);
            TryDeleteFile(tempPath);
        }
    }

    private static EncoderConfiguration CreateSinkWriterConfiguration(
        WindowsH264EncodeProfile encodeProfile,
        WindowsH264EncodeOptions options,
        uint bitrate)
    {
        var width = encodeProfile.Width;
        var height = encodeProfile.Height;
        var outputType = CreateOutputMediaType(width, height, options.TargetFramesPerSecond, bitrate);
        try
        {
            var headerBytes = TryReadSequenceHeader(outputType);
            LogLifecycle(
                headerBytes.Length > 0
                    ? "screenshare_h264_encoder_sequence_header_available"
                    : "screenshare_h264_encoder_sequence_header_missing",
                $"bytes={headerBytes.Length}; width={width}; height={height}; fps={options.TargetFramesPerSecond}");
            return new EncoderConfiguration(
                width,
                height,
                Math.Max(1, options.TargetFramesPerSecond),
                bitrate,
                DefaultProfile,
                encodeProfile.ProfileName,
                encodeProfile.TransportIpOnly,
                headerBytes,
                ResolveNalLengthSize(headerBytes, 4),
                pendingFirstFrame: true);
        }
        finally
        {
            ReleaseComObject(outputType);
        }
    }

    private static LowDelayEncoderConfigurationResult CreateSinkWriterLowDelayEncodingParameters(
        int targetFramesPerSecond,
        out IMFAttributes? encodingParameters)
    {
        encodingParameters = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out encodingParameters, 4));
            var lowLatencyApplied = TrySetUInt32Attribute(encodingParameters, MfLowLatency, 1);
            var bPictureCountApplied = TrySetUInt32Attribute(encodingParameters, CodecApiAvEncMpvDefaultBPictureCount, 0);
            var gopSizeApplied = TrySetUInt32Attribute(
                encodingParameters,
                CodecApiAvEncMpvGopSize,
                (uint)Math.Max(1, targetFramesPerSecond));
            var qualityVsSpeedApplied = TrySetUInt32Attribute(encodingParameters, CodecApiAvEncCommonQualityVsSpeed, LowDelayQualityVsSpeedValue);

            return new LowDelayEncoderConfigurationResult(
                LowLatencyModeApplied: lowLatencyApplied,
                BPictureCountApplied: bPictureCountApplied,
                GopSizeApplied: gopSizeApplied,
                QualityVsSpeedApplied: qualityVsSpeedApplied);
        }
        catch
        {
            ReleaseComObject(encodingParameters);
            encodingParameters = null;
            return default;
        }
    }

    private EncoderEncodeResult EncodeSingleFrameToSinkWriter(
        EncoderConfiguration configuration,
        byte[] nv12Bytes,
        long sampleTimeHns,
        long sampleDurationHns,
        bool forceKeyFrame,
        string logContext)
    {
        var encodeStartedAt = Stopwatch.GetTimestamp();
        var tempPath = Path.Combine(Path.GetTempPath(), $"nlink-h264-{Guid.NewGuid():N}.mp4");
        SinkWriterContext? sinkWriterContext = null;
        IMFSinkWriter? sinkWriter = null;
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        IMFAttributes? encodingParameters = null;
        IMFSample? inputSample = null;
        try
        {
            outputType = CreateOutputMediaType(
                configuration.Width,
                configuration.Height,
                configuration.TargetFramesPerSecond,
                configuration.TargetBitrate);
            inputType = CreateInputMediaType(
                configuration.Width,
                configuration.Height,
                configuration.TargetFramesPerSecond);
            sinkWriterContext = CreateSinkWriter(tempPath);
            sinkWriter = sinkWriterContext.Writer;
            Marshal.ThrowExceptionForHR(sinkWriter.AddStream(outputType, out var streamIndex));
            var lowDelayEncodingParameters = CreateSinkWriterLowDelayEncodingParameters(
                configuration.TargetFramesPerSecond,
                out encodingParameters);
            try
            {
                Marshal.ThrowExceptionForHR(sinkWriter.SetInputMediaType(streamIndex, inputType, encodingParameters));
                lowDelayConfigApplied = $"sink_writer_{lowDelayEncodingParameters.State}";
            }
            catch (Exception ex)
            {
                LogLifecycle(
                    "screenshare_h264_sink_writer_low_delay_profile_failed",
                    $"{logContext}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                ReleaseComObject(encodingParameters);
                encodingParameters = null;
                Marshal.ThrowExceptionForHR(sinkWriter.SetInputMediaType(streamIndex, inputType, null));
                lowDelayConfigApplied = "sink_writer_none";
            }
            Marshal.ThrowExceptionForHR(sinkWriter.BeginWriting());
            LogLifecycle(
                "screenshare_h264_sink_writer_stream_configured",
                $"{logContext}; {DescribeMediaType("input", inputType)}; {DescribeMediaType("output", outputType)}; low_delay_config_applied={lowDelayConfigApplied}; shared_device={(sinkWriterContext.D3DDevice != IntPtr.Zero ? 1 : 0)}; shared_context={(sinkWriterContext.D3DContext != IntPtr.Zero ? 1 : 0)}");
            var preferredStrategy = EnsureInputBufferStrategy(
                configuration.Width,
                configuration.Height,
                configuration.TargetFramesPerSecond,
                nv12Bytes.Length,
                sampleDurationHns);
            Exception? lastWriteFailure = null;
            var strategyFailures = new List<string>();
            foreach (var strategy in EnumerateStrategyAttempts(preferredStrategy))
            {
                if (inputSample is not null)
                {
                    ReleaseComObject(inputSample);
                    inputSample = null;
                }

                try
                {
                    inputSample = CreateInputSample(
                        strategy,
                        configuration,
                        nv12Bytes,
                        sampleTimeHns,
                        sampleDurationHns,
                        forceKeyFrame,
                        sinkWriterContext.D3DDevice,
                        sinkWriterContext.D3DContext);
                    if (strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate)
                    {
                        var sampleUsable = IsSampleUsableForStrategy(strategy, inputSample);
                        LogLifecycle(
                            "screenshare_h264_runtime_sample_buffer_state",
                            $"{logContext}; strategy={strategy.ToString().ToLowerInvariant()}; stage=pre_write; expected_length={nv12Bytes.Length}; sample_usable={(sampleUsable ? 1 : 0)}; {DescribeSampleBuffers(inputSample, nv12Bytes.Length, true)}");
                    }
                    Marshal.ThrowExceptionForHR(sinkWriter.WriteSample(streamIndex, inputSample));
                    PromoteWorkingInputBufferStrategy(strategy);
                    if (!loggedPromotedStrategyUse &&
                        strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate)
                    {
                        loggedPromotedStrategyUse = true;
                        LogInstanceLifecycle(
                            "screenshare_h264_dxgi_strategy_used",
                            $"strategy={strategy.ToString().ToLowerInvariant()}; sample_time={sampleTimeHns}; sample_duration={sampleDurationHns}; bytes={nv12Bytes.Length}");
                    }
                    lastWriteFailure = null;
                    break;
                }
                catch (Exception ex) when (ex is ArgumentException or COMException or InputSampleCreationException)
                {
                    lastWriteFailure = ex;
                    var failureStage = "write_sample";
                    var failureException = ex;
                    if (ex is InputSampleCreationException inputSampleCreationException && inputSampleCreationException.InnerException is not null)
                    {
                        failureStage = inputSampleCreationException.Stage;
                        failureException = inputSampleCreationException.InnerException;
                    }

                    strategyFailures.Add(
                        $"strategy={strategy.ToString().ToLowerInvariant()},success=False,stage={failureStage},reason={failureException.GetType().Name},hresult=0x{failureException.HResult:X8}");
                    LogLifecycle(
                        failureStage == "write_sample"
                            ? "screenshare_h264_sink_writer_sample_rejected"
                            : "screenshare_h264_input_strategy_sample_rejected",
                        $"{logContext}; strategy={strategy.ToString().ToLowerInvariant()}; stage={failureStage}; reason={failureException.GetType().Name}; hresult=0x{failureException.HResult:X8}; message={Sanitize(failureException.Message)}; {DescribeMediaType("input", inputType)}; {DescribeMediaType("output", outputType)}; sample_time={sampleTimeHns}; sample_duration={sampleDurationHns}; bytes={nv12Bytes.Length}");
                }
            }

            if (lastWriteFailure is not null)
            {
                var failureSummary = string.Join(" | ", strategyFailures);
                lock (InputBufferProbeSync)
                {
                    inputBufferProbeCompleted = true;
                    inputBufferProbeSucceeded = false;
                    lastInputBufferProbeSummary = failureSummary;
                }

                terminalInputBufferFailureSummary = failureSummary;
                throw new RawInputBufferStrategyUnavailableException(failureSummary, lastWriteFailure);
            }

            Marshal.ThrowExceptionForHR(sinkWriter.NotifyEndOfSegment(streamIndex));
            Marshal.ThrowExceptionForHR(sinkWriter.Finalize_());

            var containerBytes = File.Exists(tempPath)
                ? File.ReadAllBytes(tempPath)
                : Array.Empty<byte>();
            var encodedBytes = ExtractAnnexBFromSingleSampleMp4(containerBytes, out var configBytes);
            var isKeyFrame = ContainsIdrNalUnit(encodedBytes);
            var totalDurationMs = (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds;
            return new EncoderEncodeResult(encodedBytes, configBytes, isKeyFrame, totalDurationMs, -1);
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_sink_writer_write_failed",
                $"{logContext}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            throw;
        }
        finally
        {
            ReleaseComObject(inputSample);
            ReleaseComObject(encodingParameters);
            sinkWriterContext?.Dispose();
            ReleaseComObject(outputType);
            ReleaseComObject(inputType);
            TryDeleteFile(tempPath);
        }
    }

    private static SinkWriterContext CreateSinkWriter(string outputPath)
    {
        IMFAttributes? attributes = null;
        IMFDXGIDeviceManager? deviceManager = null;
        IntPtr d3dDevice = IntPtr.Zero;
        IntPtr d3dContext = IntPtr.Zero;
        var path = "software";
        var deviceKind = "none";
        try
        {
            var lowLatencyKey = MfLowLatency;
            var hardwareTransformsKey = MfReadwriteEnableHardwareTransforms;
            var d3dManagerKey = MfSinkWriterD3DManager;
            var d3dOptionalKey = MfReadwriteD3DOptional;
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 4));
            Marshal.ThrowExceptionForHR(attributes.SetUINT32(ref lowLatencyKey, 1));
            Marshal.ThrowExceptionForHR(attributes.SetUINT32(ref hardwareTransformsKey, 1));

            if (TryCreateSinkWriterDeviceManager(out deviceManager, out d3dDevice, out d3dContext, out deviceKind))
            {
                Marshal.ThrowExceptionForHR(attributes.SetUnknown(ref d3dManagerKey, deviceManager!));
                Marshal.ThrowExceptionForHR(attributes.SetUINT32(ref d3dOptionalKey, 1));
                Marshal.ThrowExceptionForHR(MFCreateSinkWriterFromURL(outputPath, null, attributes, out var d3dSinkWriter));
                path = "d3d";
                LogLifecycle(
                    "screenshare_h264_sink_writer_initialized",
                    $"path={path}; device_kind={deviceKind}; d3d_optional=1; low_latency=1; output={Sanitize(outputPath)}");
                return new SinkWriterContext(d3dSinkWriter, deviceManager, d3dDevice, d3dContext);
            }

            Marshal.ThrowExceptionForHR(MFCreateSinkWriterFromURL(outputPath, null, attributes, out var sinkWriter));
            LogLifecycle(
                "screenshare_h264_sink_writer_initialized",
                $"path={path}; device_kind={deviceKind}; d3d_optional=0; low_latency=1; output={Sanitize(outputPath)}");
            return new SinkWriterContext(sinkWriter, null, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (deviceManager is not null)
        {
            LogLifecycle(
                "screenshare_h264_sink_writer_d3d_init_failed",
                $"reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            Marshal.ThrowExceptionForHR(MFCreateSinkWriterFromURL(outputPath, null, null, out var fallbackSinkWriter));
            LogLifecycle(
                "screenshare_h264_sink_writer_initialized",
                $"path=software_fallback; device_kind={deviceKind}; d3d_optional=0; low_latency=1; output={Sanitize(outputPath)}");
            return new SinkWriterContext(fallbackSinkWriter, null, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            ReleaseComObject(attributes);
            if (path != "d3d")
            {
                ReleaseComObject(deviceManager);
                if (d3dContext != IntPtr.Zero)
                {
                    Marshal.Release(d3dContext);
                }

                if (d3dDevice != IntPtr.Zero)
                {
                    Marshal.Release(d3dDevice);
                }
            }
        }
    }

    private static bool TryCreateSinkWriterDeviceManager(
        out IMFDXGIDeviceManager? deviceManager,
        out IntPtr d3dDevice,
        out IntPtr d3dContext,
        out string deviceKind)
    {
        deviceManager = null;
        d3dDevice = IntPtr.Zero;
        d3dContext = IntPtr.Zero;
        deviceKind = "none";
        try
        {
            var createFlags = D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport;
            var hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverType.Hardware,
                IntPtr.Zero,
                createFlags,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out d3dDevice,
                out _,
                out d3dContext);
            deviceKind = "hardware";
            if (hr < 0)
            {
                hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Warp,
                    IntPtr.Zero,
                    createFlags,
                    IntPtr.Zero,
                    0,
                    D3D11SdkVersion,
                    out d3dDevice,
                    out _,
                    out d3dContext);
                deviceKind = "warp";
            }

            if (hr < 0 || d3dDevice == IntPtr.Zero)
            {
                return false;
            }

            Marshal.ThrowExceptionForHR(MFCreateDXGIDeviceManager(out var resetToken, out var manager));
            Marshal.ThrowExceptionForHR(manager.ResetDevice(d3dDevice, resetToken));
            deviceManager = manager;
            return true;
        }
        catch
        {
            ReleaseComObject(deviceManager);
            deviceManager = null;
            if (d3dContext != IntPtr.Zero)
            {
                Marshal.Release(d3dContext);
                d3dContext = IntPtr.Zero;
            }

            if (d3dDevice != IntPtr.Zero)
            {
                Marshal.Release(d3dDevice);
                d3dDevice = IntPtr.Zero;
            }

            deviceKind = "none";
            return false;
        }
    }

    private static IMFTransform CreateTransform()
    {
        if (TryCreateHardwareTransform(out var hardwareTransform))
        {
            return hardwareTransform!;
        }

        if (TryCreateSoftwareTransform(out var softwareTransform))
        {
            return softwareTransform!;
        }

        throw new PlatformNotSupportedException("No Media Foundation H.264 encoder transform is available.");
    }

    private static bool TryCreateTransform(TransformProbeBackend backend, out IMFTransform? transform)
    {
        return backend switch
        {
            TransformProbeBackend.Hardware => TryCreateHardwareTransform(out transform),
            TransformProbeBackend.Software => TryCreateSoftwareTransform(out transform),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
    }

    private static bool ByteArrayEquals(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }
    private static bool IsTransformNeedMoreInputException(Exception ex)
    {
        return ex is COMException comException && comException.HResult == MfTransformNeedMoreInput;
    }

    private static byte[] ExtractAnnexBFromSingleSampleMp4(byte[] containerBytes, out byte[] decoderConfigData)
    {
        decoderConfigData = Array.Empty<byte>();
        if (containerBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (!TryFindMp4BoxPayload(containerBytes, "avcC", out var avcC) ||
            !TryFindTopLevelMp4Box(containerBytes, "mdat", out var mdatPayload, out var mdatPayloadOffset))
        {
            LogLifecycle(
                "screenshare_h264_sink_writer_mp4_parse_failed",
                $"reason=missing_boxes; bytes={containerBytes.Length}");
            return Array.Empty<byte>();
        }

        if (!TryParseAvcConfiguration(avcC, out var nalLengthSize, out var spsUnits, out var ppsUnits))
        {
            LogLifecycle(
                "screenshare_h264_sink_writer_mp4_parse_failed",
                $"reason=invalid_avcc; bytes={containerBytes.Length}; avcc_bytes={avcC.Length}");
            return Array.Empty<byte>();
        }

        decoderConfigData = avcC;

        using var stream = new MemoryStream();
        foreach (var sps in spsUnits)
        {
            WriteAnnexBNal(stream, sps);
        }

        foreach (var pps in ppsUnits)
        {
            WriteAnnexBNal(stream, pps);
        }

        var sampleBytes = mdatPayload;
        var sampleOffsetInMdat = 0;
        var sampleSize = mdatPayload.Length;
        if (TryGetSingleMp4Sample(containerBytes, mdatPayloadOffset, mdatPayload.Length, out var mp4Sample))
        {
            sampleBytes = mp4Sample.Bytes;
            sampleOffsetInMdat = mp4Sample.OffsetInMdatPayload;
            sampleSize = mp4Sample.Bytes.Length;
        }

        TryWriteLengthPrefixedSampleToAnnexB(
            sampleBytes,
            nalLengthSize,
            stream,
            out var sampleNalCount,
            out var samplePayloadBytes,
            out var parseSkipBytes);

        if (sampleNalCount == 0 || stream.Length <= 64)
        {
            var preservedPath = sampleNalCount == 0
                ? TryPreserveDebugMp4Container(containerBytes)
                : string.Empty;
            LogLifecycle(
                "screenshare_h264_sink_writer_sample_parse",
                $"container_bytes={containerBytes.Length}; mdat_bytes={mdatPayload.Length}; config_bytes={avcC.Length}; nal_length_size={nalLengthSize}; sps={spsUnits.Count}; pps={ppsUnits.Count}; sample_offset_in_mdat={sampleOffsetInMdat}; sample_bytes={sampleSize}; parse_skip_bytes={parseSkipBytes}; sample_nals={sampleNalCount}; sample_payload_bytes={samplePayloadBytes}; output_bytes={stream.Length}; mdat_prefix={HexPrefix(mdatPayload, 24)}; preserved_path={Sanitize(preservedPath)}");
        }

        return stream.ToArray();
    }

    private static bool TryFindTopLevelMp4Box(
        byte[] bytes,
        string boxType,
        out byte[] payload,
        out int payloadOffset)
    {
        payload = Array.Empty<byte>();
        payloadOffset = -1;
        if (bytes.Length < 8)
        {
            return false;
        }

        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            if (!TryReadMp4BoxHeader(bytes, offset, out var type, out var headerSize, out var effectiveSize))
            {
                return false;
            }

            if (string.Equals(type, boxType, StringComparison.Ordinal))
            {
                var payloadLength = (int)(effectiveSize - headerSize);
                payload = new byte[payloadLength];
                payloadOffset = offset + headerSize;
                Buffer.BlockCopy(bytes, payloadOffset, payload, 0, payloadLength);
                return true;
            }

            offset += (int)effectiveSize;
        }

        return false;
    }

    private static bool TryGetSingleMp4Sample(
        byte[] containerBytes,
        int mdatPayloadOffset,
        int mdatPayloadLength,
        out Mp4Sample sample)
    {
        sample = default;
        if (!TryFindMp4BoxPayload(containerBytes, "stsz", out var stszPayload) ||
            !TryParseFirstStszSampleSize(stszPayload, out var sampleSize))
        {
            return false;
        }

        long chunkOffset;
        if (TryFindMp4BoxPayload(containerBytes, "stco", out var stcoPayload))
        {
            if (!TryParseFirstStcoChunkOffset(stcoPayload, out chunkOffset))
            {
                return false;
            }
        }
        else if (TryFindMp4BoxPayload(containerBytes, "co64", out var co64Payload))
        {
            if (!TryParseFirstCo64ChunkOffset(co64Payload, out chunkOffset))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (sampleSize <= 0 || chunkOffset < 0 || chunkOffset > int.MaxValue)
        {
            return false;
        }

        var absoluteOffset = (int)chunkOffset;
        var mdatPayloadEnd = mdatPayloadOffset + mdatPayloadLength;
        if (absoluteOffset < mdatPayloadOffset ||
            absoluteOffset + sampleSize > containerBytes.Length ||
            absoluteOffset + sampleSize > mdatPayloadEnd)
        {
            return false;
        }

        var bytes = new byte[sampleSize];
        Buffer.BlockCopy(containerBytes, absoluteOffset, bytes, 0, sampleSize);
        sample = new Mp4Sample(bytes, absoluteOffset - mdatPayloadOffset);
        return true;
    }

    private static bool TryParseFirstStszSampleSize(byte[] payload, out int sampleSize)
    {
        sampleSize = 0;
        if (payload.Length < 12)
        {
            return false;
        }

        var defaultSampleSize = (int)ReadUInt32BigEndian(payload, 4);
        var sampleCount = (int)ReadUInt32BigEndian(payload, 8);
        if (sampleCount <= 0)
        {
            return false;
        }

        if (defaultSampleSize > 0)
        {
            sampleSize = defaultSampleSize;
            return true;
        }

        if (payload.Length < 16)
        {
            return false;
        }

        sampleSize = (int)ReadUInt32BigEndian(payload, 12);
        return sampleSize > 0;
    }

    private static bool TryParseFirstStcoChunkOffset(byte[] payload, out long chunkOffset)
    {
        chunkOffset = 0;
        if (payload.Length < 12)
        {
            return false;
        }

        var entryCount = (int)ReadUInt32BigEndian(payload, 4);
        if (entryCount <= 0)
        {
            return false;
        }

        chunkOffset = ReadUInt32BigEndian(payload, 8);
        return chunkOffset > 0;
    }

    private static bool TryParseFirstCo64ChunkOffset(byte[] payload, out long chunkOffset)
    {
        chunkOffset = 0;
        if (payload.Length < 16)
        {
            return false;
        }

        var entryCount = (int)ReadUInt32BigEndian(payload, 4);
        if (entryCount <= 0)
        {
            return false;
        }

        chunkOffset = (long)ReadUInt64BigEndian(payload, 8);
        return chunkOffset > 0;
    }

    private static bool TryWriteLengthPrefixedSampleToAnnexB(
        byte[] sampleBytes,
        int nalLengthSize,
        Stream destination,
        out int sampleNalCount,
        out int samplePayloadBytes,
        out int parseSkipBytes)
    {
        sampleNalCount = 0;
        samplePayloadBytes = 0;
        parseSkipBytes = 0;
        if (sampleBytes.Length <= nalLengthSize)
        {
            return false;
        }

        foreach (var skipBytes in new[] { 0, 4, 8, 12 })
        {
            if (skipBytes >= sampleBytes.Length)
            {
                continue;
            }

            using var candidateStream = new MemoryStream();
            var offset = skipBytes;
            var nalCount = 0;
            var payloadBytes = 0;
            var parseFailed = false;

            while (offset + nalLengthSize <= sampleBytes.Length)
            {
                var nalLength = ReadBigEndian(sampleBytes, offset, nalLengthSize);
                offset += nalLengthSize;
                if (nalLength <= 0 || offset + nalLength > sampleBytes.Length)
                {
                    parseFailed = true;
                    break;
                }

                var nalHeader = sampleBytes[offset] & 0x1F;
                if (nalHeader == 0 || nalHeader > 31)
                {
                    parseFailed = true;
                    break;
                }

                var nalUnit = new byte[nalLength];
                Buffer.BlockCopy(sampleBytes, offset, nalUnit, 0, nalLength);
                WriteAnnexBNal(candidateStream, nalUnit);
                nalCount++;
                payloadBytes += nalLength;
                offset += nalLength;
            }

            if (parseFailed || nalCount == 0 || offset != sampleBytes.Length)
            {
                continue;
            }

            candidateStream.Position = 0;
            candidateStream.CopyTo(destination);
            sampleNalCount = nalCount;
            samplePayloadBytes = payloadBytes;
            parseSkipBytes = skipBytes;
            return true;
        }

        return false;
    }

    private static bool TryFindMp4BoxPayload(byte[] bytes, string boxType, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (bytes.Length < 8)
        {
            return false;
        }

        for (var offset = 0; offset + 8 <= bytes.Length; offset++)
        {
            if (!TryReadMp4BoxHeader(bytes, offset, out var type, out var headerSize, out var effectiveSize))
            {
                continue;
            }

            if (string.Equals(type, boxType, StringComparison.Ordinal))
            {
                var payloadLength = (int)(effectiveSize - headerSize);
                payload = new byte[payloadLength];
                Buffer.BlockCopy(bytes, offset + headerSize, payload, 0, payloadLength);
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMp4BoxHeader(
        byte[] bytes,
        int offset,
        out string type,
        out int headerSize,
        out long effectiveSize)
    {
        type = string.Empty;
        headerSize = 0;
        effectiveSize = 0;
        if (offset < 0 || offset + 8 > bytes.Length)
        {
            return false;
        }

        var boxSize = ReadUInt32BigEndian(bytes, offset);
        type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
        headerSize = 8;
        effectiveSize = boxSize;

        if (boxSize == 1)
        {
            if (offset + 16 > bytes.Length)
            {
                return false;
            }

            effectiveSize = (long)ReadUInt64BigEndian(bytes, offset + 8);
            headerSize = 16;
        }
        else if (boxSize == 0)
        {
            effectiveSize = bytes.Length - offset;
        }

        return effectiveSize >= headerSize && offset + effectiveSize <= bytes.Length;
    }

    private static bool TryParseAvcConfiguration(
        byte[] avcC,
        out int nalLengthSize,
        out List<byte[]> spsUnits,
        out List<byte[]> ppsUnits)
    {
        nalLengthSize = 4;
        spsUnits = new List<byte[]>();
        ppsUnits = new List<byte[]>();

        if (avcC.Length < 7)
        {
            return false;
        }

        nalLengthSize = (avcC[4] & 0x03) + 1;
        var offset = 5;
        var spsCount = avcC[offset++] & 0x1F;
        for (var i = 0; i < spsCount; i++)
        {
            if (!TryReadLengthPrefixedBlob(avcC, ref offset, out var sps))
            {
                return false;
            }

            spsUnits.Add(sps);
        }

        if (offset >= avcC.Length)
        {
            return false;
        }

        var ppsCount = avcC[offset++];
        for (var i = 0; i < ppsCount; i++)
        {
            if (!TryReadLengthPrefixedBlob(avcC, ref offset, out var pps))
            {
                return false;
            }

            ppsUnits.Add(pps);
        }

        return spsUnits.Count > 0 && ppsUnits.Count > 0;
    }

    private static bool TryReadLengthPrefixedBlob(byte[] bytes, ref int offset, out byte[] blob)
    {
        blob = Array.Empty<byte>();
        if (offset + 2 > bytes.Length)
        {
            return false;
        }

        var length = (bytes[offset] << 8) | bytes[offset + 1];
        offset += 2;
        if (length <= 0 || offset + length > bytes.Length)
        {
            return false;
        }

        blob = new byte[length];
        Buffer.BlockCopy(bytes, offset, blob, 0, length);
        offset += length;
        return true;
    }

    private static void WriteAnnexBNal(Stream stream, byte[] nalUnit)
    {
        if (nalUnit.Length == 0)
        {
            return;
        }

        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(1);
        stream.Write(nalUnit, 0, nalUnit.Length);
    }

    private static int ReadBigEndian(byte[] bytes, int offset, int length)
    {
        var value = 0;
        for (var i = 0; i < length; i++)
        {
            value = (value << 8) | bytes[offset + i];
        }

        return value;
    }

    private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
    {
        return (uint)((bytes[offset] << 24) |
                      (bytes[offset + 1] << 16) |
                      (bytes[offset + 2] << 8) |
                      bytes[offset + 3]);
    }

    private static ulong ReadUInt64BigEndian(byte[] bytes, int offset)
    {
        return ((ulong)ReadUInt32BigEndian(bytes, offset) << 32) |
               ReadUInt32BigEndian(bytes, offset + 4);
    }

    private static byte[] ExtractDecoderConfigFromAnnexB(byte[] encodedBytes)
    {
        if (encodedBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var stream = new MemoryStream();
        var offset = 0;
        while (TryReadNextNalUnit(encodedBytes, ref offset, out var nalUnit))
        {
            if (nalUnit.Length == 0)
            {
                continue;
            }

            var nalType = nalUnit[0] & 0x1F;
            if (nalType is 7 or 8)
            {
                WriteAnnexBNalUnit(stream, nalUnit);
            }
        }

        return stream.ToArray();
    }

    private static byte[] NormalizeTransformOutputBytes(
        byte[] encodedBytes,
        EncoderConfiguration configuration,
        out byte[] decoderConfigData)
    {
        decoderConfigData = configuration.DecoderConfigData;
        if (encodedBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] annexBBytes;
        if (WindowsH264DecodePreparation.LooksLikeAnnexB(encodedBytes))
        {
            annexBBytes = encodedBytes;
        }
        else
        {
            annexBBytes = WindowsH264DecodePreparation.ConvertLengthPrefixedToAnnexB(
                encodedBytes,
                configuration.NalLengthSize > 0 ? configuration.NalLengthSize : 4,
                stripDecoderConfigNalUnits: false);
            if (annexBBytes.Length == 0)
            {
                annexBBytes = encodedBytes;
            }
        }

        if (decoderConfigData.Length == 0)
        {
            var derivedConfig = BuildAvcConfigurationFromAnnexB(annexBBytes, configuration.NalLengthSize > 0 ? configuration.NalLengthSize : 4);
            if (derivedConfig.Length > 0)
            {
                decoderConfigData = derivedConfig;
            }
        }

        return annexBBytes;
    }

    private static byte[] BuildAvcConfigurationFromAnnexB(byte[] annexBBytes, int nalLengthSize)
    {
        if (annexBBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var spsUnits = new List<byte[]>();
        var ppsUnits = new List<byte[]>();
        var offset = 0;
        while (TryReadNextNalUnit(annexBBytes, ref offset, out var nalUnit))
        {
            if (nalUnit.Length == 0)
            {
                continue;
            }

            var nalType = nalUnit[0] & 0x1F;
            if (nalType == 7)
            {
                spsUnits.Add(nalUnit.ToArray());
            }
            else if (nalType == 8)
            {
                ppsUnits.Add(nalUnit.ToArray());
            }
        }

        if (spsUnits.Count == 0 || ppsUnits.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var sps = spsUnits[0];
        if (sps.Length < 4)
        {
            return Array.Empty<byte>();
        }

        using var stream = new MemoryStream();
        stream.WriteByte(1);
        stream.WriteByte(sps[1]);
        stream.WriteByte(sps[2]);
        stream.WriteByte(sps[3]);
        stream.WriteByte((byte)(0xFC | Math.Clamp(nalLengthSize, 1, 4) - 1));
        stream.WriteByte((byte)(0xE0 | Math.Min(31, spsUnits.Count)));
        foreach (var spsUnit in spsUnits)
        {
            WriteLengthPrefixedBlob(stream, spsUnit);
        }

        stream.WriteByte((byte)Math.Min(255, ppsUnits.Count));
        foreach (var ppsUnit in ppsUnits)
        {
            WriteLengthPrefixedBlob(stream, ppsUnit);
        }

        return stream.ToArray();
    }

    private static void WriteLengthPrefixedBlob(Stream stream, byte[] blob)
    {
        var length = blob.Length;
        stream.WriteByte((byte)((length >> 8) & 0xFF));
        stream.WriteByte((byte)(length & 0xFF));
        stream.Write(blob, 0, blob.Length);
    }

    private static int ResolveNalLengthSize(byte[] decoderConfigData, int fallbackLengthSize)
    {
        return WindowsH264DecodePreparation.TryParseNalLengthSize(decoderConfigData ?? Array.Empty<byte>(), out var nalLengthSize)
            ? nalLengthSize
            : Math.Clamp(fallbackLengthSize, 1, 4);
    }

    private static bool ContainsIdrNalUnit(byte[] encodedBytes)
    {
        var offset = 0;
        while (TryReadNextNalUnit(encodedBytes, ref offset, out var nalUnit))
        {
            if (nalUnit.Length == 0)
            {
                continue;
            }

            if ((nalUnit[0] & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    private static AccessUnitClassification AnalyzeAccessUnit(byte[] encodedBytes)
    {
        if (encodedBytes.Length == 0)
        {
            return new AccessUnitClassification(
                HasDisplayableVcl: false,
                HasIdr: false,
                HasSps: false,
                HasPps: false,
                HasAud: false,
                HasSei: false,
                VclNalCount: 0,
                IdrNalCount: 0,
                AudNalCount: 0,
                PrimaryPictureCount: 0,
                HasPPicture: false,
                HasBPicture: false,
                HasISlice: false,
                PictureKind: AccessUnitPictureKind.None,
                Kind: "empty");
        }

        var hasDisplayableVcl = false;
        var hasIdr = false;
        var hasSps = false;
        var hasPps = false;
        var hasAud = false;
        var hasSei = false;
        var vclNalCount = 0;
        var idrNalCount = 0;
        var audNalCount = 0;
        var primaryPictureCount = 0;
        var hasPPicture = false;
        var hasBPicture = false;
        var hasISlice = false;
        var hasUnknownVcl = false;
        var hasUnsupportedVcl = false;
        var offset = 0;
        while (TryReadNextNalUnit(encodedBytes, ref offset, out var nalUnit))
        {
            if (nalUnit.Length == 0)
            {
                continue;
            }

            switch (nalUnit[0] & 0x1F)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                    hasDisplayableVcl = true;
                    vclNalCount++;
                    if (TryParseSliceHeader(nalUnit, out var nonIdrSlice))
                    {
                        if (nonIdrSlice.FirstMbInSlice == 0)
                        {
                            primaryPictureCount++;
                        }

                        switch (nonIdrSlice.PictureKind)
                        {
                            case AccessUnitPictureKind.P:
                                hasPPicture = true;
                                break;
                            case AccessUnitPictureKind.B:
                                hasBPicture = true;
                                break;
                            case AccessUnitPictureKind.I:
                                hasISlice = true;
                                break;
                            case AccessUnitPictureKind.Unsupported:
                                hasUnsupportedVcl = true;
                                break;
                            default:
                                hasUnknownVcl = true;
                                break;
                        }
                    }
                    else
                    {
                        hasUnknownVcl = true;
                    }
                    break;
                case 5:
                    hasDisplayableVcl = true;
                    hasIdr = true;
                    vclNalCount++;
                    idrNalCount++;
                    if (TryParseSliceHeader(nalUnit, out var idrSlice))
                    {
                        if (idrSlice.FirstMbInSlice == 0)
                        {
                            primaryPictureCount++;
                        }

                        switch (idrSlice.PictureKind)
                        {
                            case AccessUnitPictureKind.I:
                            case AccessUnitPictureKind.P:
                                hasISlice = true;
                                break;
                            case AccessUnitPictureKind.B:
                                hasBPicture = true;
                                break;
                            case AccessUnitPictureKind.Unsupported:
                                hasUnsupportedVcl = true;
                                break;
                            default:
                                hasUnknownVcl = true;
                                break;
                        }
                    }
                    else
                    {
                        hasUnknownVcl = true;
                    }
                    break;
                case 6:
                    hasSei = true;
                    break;
                case 7:
                    hasSps = true;
                    break;
                case 8:
                    hasPps = true;
                    break;
                case 9:
                    hasAud = true;
                    audNalCount++;
                    break;
            }
        }

        var pictureKind = AccessUnitPictureKind.None;
        var kind = hasDisplayableVcl
            ? ResolveDisplayableAccessUnitKind(
                hasIdr,
                hasPPicture,
                hasBPicture,
                hasISlice,
                hasUnknownVcl,
                hasUnsupportedVcl,
                primaryPictureCount,
                out pictureKind)
            : hasSps && hasPps
                ? "sps_pps_only"
                : hasSps || hasPps
                    ? "parameter_sets_only"
                    : hasSei || hasAud
                        ? "sei_aud_only"
                        : "non_displayable";

        return new AccessUnitClassification(
            HasDisplayableVcl: hasDisplayableVcl,
            HasIdr: hasIdr,
            HasSps: hasSps,
            HasPps: hasPps,
            HasAud: hasAud,
            HasSei: hasSei,
            VclNalCount: vclNalCount,
            IdrNalCount: idrNalCount,
            AudNalCount: audNalCount,
            PrimaryPictureCount: primaryPictureCount,
            HasPPicture: hasPPicture,
            HasBPicture: hasBPicture,
            HasISlice: hasISlice,
            PictureKind: pictureKind,
            Kind: kind);
    }

    private static string ResolveDisplayableAccessUnitKind(
        bool hasIdr,
        bool hasPPicture,
        bool hasBPicture,
        bool hasISlice,
        bool hasUnknownVcl,
        bool hasUnsupportedVcl,
        int primaryPictureCount,
        out AccessUnitPictureKind pictureKind)
    {
        if (primaryPictureCount == 0)
        {
            pictureKind = AccessUnitPictureKind.Unknown;
            return "unknown_vcl";
        }

        if (primaryPictureCount > 1)
        {
            pictureKind = AccessUnitPictureKind.Unknown;
            return "multi_picture_vcl";
        }

        if (hasBPicture)
        {
            pictureKind = AccessUnitPictureKind.B;
            return "b_vcl";
        }

        if (hasIdr)
        {
            pictureKind = AccessUnitPictureKind.Idr;
            return "idr_vcl";
        }

        if (hasUnsupportedVcl)
        {
            pictureKind = AccessUnitPictureKind.Unsupported;
            return "unknown_vcl";
        }

        if (hasUnknownVcl || (hasPPicture && hasISlice))
        {
            pictureKind = AccessUnitPictureKind.Unknown;
            return "unknown_vcl";
        }

        if (hasPPicture)
        {
            pictureKind = AccessUnitPictureKind.P;
            return "p_vcl";
        }

        if (hasISlice)
        {
            pictureKind = AccessUnitPictureKind.I;
            return "i_vcl";
        }

        pictureKind = AccessUnitPictureKind.Unknown;
        return "unknown_vcl";
    }

    private static bool TryParseSliceHeader(ReadOnlySpan<byte> nalUnit, out ParsedSliceHeader sliceHeader)
    {
        sliceHeader = default;
        if (nalUnit.Length < 2)
        {
            return false;
        }

        var rbsp = RemoveEmulationPreventionBytes(nalUnit[1..]);
        if (rbsp.Length == 0)
        {
            return false;
        }

        var reader = new H264BitReader(rbsp);
        if (!reader.TryReadUnsignedExpGolomb(out var firstMbInSlice) ||
            !reader.TryReadUnsignedExpGolomb(out var rawSliceType))
        {
            return false;
        }

        var normalizedSliceType = rawSliceType % 5;
        var pictureKind = normalizedSliceType switch
        {
            0 => AccessUnitPictureKind.P,
            1 => AccessUnitPictureKind.B,
            2 => AccessUnitPictureKind.I,
            3 or 4 => AccessUnitPictureKind.Unsupported,
            _ => AccessUnitPictureKind.Unknown,
        };

        sliceHeader = new ParsedSliceHeader(firstMbInSlice, pictureKind);
        return true;
    }

    private static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            return payload.ToArray();
        }

        using var stream = new MemoryStream(payload.Length);
        var zeroCount = 0;
        foreach (var value in payload)
        {
            if (zeroCount >= 2 && value == 0x03)
            {
                zeroCount = 0;
                continue;
            }

            stream.WriteByte(value);
            zeroCount = value == 0 ? zeroCount + 1 : 0;
        }

        return stream.ToArray();
    }

    private static void WriteAnnexBNalUnit(Stream stream, ReadOnlySpan<byte> nalUnit)
    {
        stream.Write(stackalloc byte[] { 0, 0, 0, 1 });
        stream.Write(nalUnit);
    }

    private static bool TryReadNextNalUnit(byte[] encodedBytes, ref int offset, out ReadOnlySpan<byte> nalUnit)
    {
        nalUnit = default;
        var start = FindStartCode(encodedBytes, offset, out var startCodeLength);
        if (start < 0)
        {
            offset = encodedBytes.Length;
            return false;
        }

        var nalStart = start + startCodeLength;
        var nextStart = FindStartCode(encodedBytes, nalStart, out _);
        var nalEnd = nextStart >= 0 ? nextStart : encodedBytes.Length;
        if (nalEnd <= nalStart)
        {
            offset = nalEnd;
            return false;
        }

        nalUnit = encodedBytes.AsSpan(nalStart, nalEnd - nalStart);
        offset = nalEnd;
        return true;
    }

    private static int FindStartCode(byte[] encodedBytes, int startIndex, out int startCodeLength)
    {
        startCodeLength = 0;
        for (var i = startIndex; i <= encodedBytes.Length - 3; i++)
        {
            if (encodedBytes[i] != 0 || encodedBytes[i + 1] != 0)
            {
                continue;
            }

            if (encodedBytes[i + 2] == 1)
            {
                startCodeLength = 3;
                return i;
            }

            if (i <= encodedBytes.Length - 4 && encodedBytes[i + 2] == 0 && encodedBytes[i + 3] == 1)
            {
                startCodeLength = 4;
                return i;
            }
        }

        return -1;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool TryCreateHardwareTransform(out IMFTransform? transform)
    {
        transform = null;
        IntPtr activationArray = IntPtr.Zero;
        IntPtr inputPtr = IntPtr.Zero;
        IntPtr outputPtr = IntPtr.Zero;
        try
        {
            var inputType = new MftRegisterTypeInfo
            {
                GuidMajorType = MfMediaTypeVideo,
                GuidSubtype = MfVideoFormatNv12,
            };
            var outputType = new MftRegisterTypeInfo
            {
                GuidMajorType = MfMediaTypeVideo,
                GuidSubtype = MfVideoFormatH264,
            };

            inputPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftRegisterTypeInfo>());
            outputPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftRegisterTypeInfo>());
            Marshal.StructureToPtr(inputType, inputPtr, false);
            Marshal.StructureToPtr(outputType, outputPtr, false);

            var category = MftCategoryVideoEncoder;
            var hr = MFTEnumEx(
                ref category,
                MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                inputPtr,
                outputPtr,
                out activationArray,
                out var count);

            if (hr < 0 || count <= 0 || activationArray == IntPtr.Zero)
            {
                return false;
            }

            var activationPtr = Marshal.ReadIntPtr(activationArray);
            if (activationPtr == IntPtr.Zero)
            {
                return false;
            }

            var activation = (IMFActivate)Marshal.GetObjectForIUnknown(activationPtr);
            Marshal.Release(activationPtr);
            try
            {
                var iid = IidImfTransform;
                var activateHr = activation.ActivateObject(ref iid, out var transformPtr);
                if (activateHr < 0 || transformPtr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr);
                    return true;
                }
                finally
                {
                    Marshal.Release(transformPtr);
                }
            }
            finally
            {
                ReleaseComObject(activation);
            }
        }
        catch
        {
            transform = null;
            return false;
        }
        finally
        {
            if (activationArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(activationArray);
            }

            if (inputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPtr);
            }

            if (outputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(outputPtr);
            }
        }
    }

    private static bool TryCreateSoftwareTransform(out IMFTransform? transform)
    {
        transform = null;
        var clsid = ClsidCmsH264EncoderMft;
        var iid = IidImfTransform;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var transformPtr);
        if (hr < 0 || transformPtr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr);
            return true;
        }
        finally
        {
            Marshal.Release(transformPtr);
        }
    }

    private static EncoderConfiguration ConfigureTransform(
        IMFTransform encoderTransform,
        int width,
        int height,
        WindowsH264EncodeOptions options,
        bool allIntra,
        uint bitrate,
        IMFDXGIDeviceManager? deviceManager = null,
        string strategyLabel = "unknown")
    {
        return ConfigureTransformCore(
                encoderTransform,
                width,
                height,
                options,
                allIntra,
                bitrate,
                deviceManager,
                strategyLabel,
                probeMode: false)
            .Configuration;
    }

    private static TransformConfigurationResult ConfigureTransformCore(
        IMFTransform encoderTransform,
        int width,
        int height,
        WindowsH264EncodeOptions options,
        bool allIntra,
        uint bitrate,
        IMFDXGIDeviceManager? deviceManager,
        string strategyLabel,
        bool probeMode)
    {
        var outputType = CreateOutputMediaType(width, height, options.TargetFramesPerSecond, bitrate);
        var inputType = CreateInputMediaType(width, height, options.TargetFramesPerSecond);
        IMFAttributes? transformAttributes = null;
        IMFAttributes? inputStreamAttributes = null;
        uint? reportedBindFlags = null;
        var lowDelayConfigResult = ApplyLowDelayMediaTypeProfile(outputType, inputType);
        var stage = "configure_transform";
        try
        {
            if (!probeMode)
            {
                try
                {
                    if (encoderTransform.GetAttributes(out transformAttributes) >= 0 && transformAttributes is not null)
                    {
                        var d3dAware = GetOptionalUInt32(transformAttributes, MfSaD3D11Aware);
                        var bindFlags = "unset";
                        if (encoderTransform.GetInputStreamAttributes(0, out inputStreamAttributes) >= 0 && inputStreamAttributes is not null)
                        {
                            bindFlags = GetOptionalUInt32(inputStreamAttributes, MfSaD3D11BindFlags);
                        }

                        LogLifecycle(
                            "screenshare_h264_transform_d3d_caps",
                            $"strategy={strategyLabel}; d3d11_aware={d3dAware}; d3d11_bind_flags={bindFlags}; has_device_manager={(deviceManager is null ? 0 : 1)}");
                    }
                }
                catch (Exception ex)
                {
                    LogLifecycle(
                        "screenshare_h264_transform_d3d_caps_unavailable",
                        $"strategy={strategyLabel}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                }
            }
            else
            {
                try
                {
                    string d3dAware = "unset";
                    if (encoderTransform.GetAttributes(out transformAttributes) >= 0 && transformAttributes is not null)
                    {
                        d3dAware = GetOptionalUInt32(transformAttributes, MfSaD3D11Aware);
                    }

                    LogLifecycle(
                        "screenshare_h264_transform_probe_capabilities",
                        $"strategy={strategyLabel}; d3d11_aware={d3dAware}; has_device_manager={(deviceManager is null ? 0 : 1)}");

                    if (encoderTransform.GetInputStreamAttributes(0, out inputStreamAttributes) >= 0 && inputStreamAttributes is not null &&
                        TryGetOptionalUInt32(inputStreamAttributes, MfSaD3D11BindFlags, out var bindFlagsValue))
                    {
                        reportedBindFlags = bindFlagsValue;
                    }

                    LogLifecycle(
                        "screenshare_h264_transform_probe_bind_flags",
                        $"strategy={strategyLabel}; reported_bind_flags={(reportedBindFlags.HasValue ? $"0x{reportedBindFlags.Value:X8}" : "unset")}; has_device_manager={(deviceManager is null ? 0 : 1)}");
                }
                catch (Exception ex)
                {
                    LogLifecycle(
                        "screenshare_h264_transform_probe_capabilities_unavailable",
                        $"strategy={strategyLabel}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                }
            }

            if (deviceManager is not null)
            {
                stage = "set_d3d_manager";
                var deviceManagerPtr = Marshal.GetIUnknownForObject(deviceManager);
                try
                {
                    ProcessTransformMessage(
                        encoderTransform,
                        MftMessageSetD3DManager,
                        deviceManagerPtr,
                        stage,
                        strategyLabel,
                        probeMode);
                }
                finally
                {
                    Marshal.Release(deviceManagerPtr);
                }
            }

            stage = "apply_low_delay_profile";
            lowDelayConfigResult = ApplyLowDelayTransformProfile(
                encoderTransform,
                options.TargetFramesPerSecond,
                allIntra,
                bitrate,
                strategyLabel,
                probeMode,
                lowDelayConfigResult);
            stage = "set_output_type";
            Marshal.ThrowExceptionForHR(encoderTransform.SetOutputType(0, outputType, 0));
            stage = "set_input_type";
            Marshal.ThrowExceptionForHR(encoderTransform.SetInputType(0, inputType, 0));
            stage = "notify_begin_streaming";
            ProcessTransformMessage(
                encoderTransform,
                MftMessageNotifyBeginStreaming,
                IntPtr.Zero,
                stage,
                strategyLabel,
                probeMode);
            stage = "notify_start_of_stream";
            ProcessTransformMessage(
                encoderTransform,
                MftMessageNotifyStartOfStream,
                IntPtr.Zero,
                stage,
                strategyLabel,
                probeMode);

            var headerBytes = probeMode ? Array.Empty<byte>() : TryReadSequenceHeader(outputType);
            if (!probeMode)
            {
                LogLifecycle(
                    headerBytes.Length > 0
                        ? "screenshare_h264_encoder_sequence_header_available"
                        : "screenshare_h264_encoder_sequence_header_missing",
                    $"bytes={headerBytes.Length}; width={width}; height={height}; fps={options.TargetFramesPerSecond}");
            }

            return new TransformConfigurationResult(
                new EncoderConfiguration(
                width,
                height,
                Math.Max(1, options.TargetFramesPerSecond),
                bitrate,
                DefaultProfile,
                options.TuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced ? "reduced" : "normal",
                allIntra,
                headerBytes,
                ResolveNalLengthSize(headerBytes, 4),
                pendingFirstFrame: true),
                reportedBindFlags,
                lowDelayConfigResult.State);
        }
        catch (Exception ex) when (ex is not TransformConfigurationException)
        {
            throw new TransformConfigurationException(stage, ex);
        }
        finally
        {
            ReleaseComObject(transformAttributes);
            ReleaseComObject(inputStreamAttributes);
            ReleaseComObject(outputType);
            ReleaseComObject(inputType);
        }
    }

    private static LowDelayEncoderConfigurationResult ApplyLowDelayMediaTypeProfile(
        IMFMediaType outputType,
        IMFMediaType inputType)
    {
        var lowLatencyApplied =
            TrySetUInt32Attribute(outputType, MfLowLatency, 1) |
            TrySetUInt32Attribute(inputType, MfLowLatency, 1);
        return new LowDelayEncoderConfigurationResult(
            LowLatencyModeApplied: lowLatencyApplied,
            BPictureCountApplied: false,
            GopSizeApplied: false,
            QualityVsSpeedApplied: false);
    }

    private static LowDelayEncoderConfigurationResult ApplyLowDelayTransformProfile(
        IMFTransform encoderTransform,
        int targetFramesPerSecond,
        bool transportIpOnly,
        uint targetBitrate,
        string strategyLabel,
        bool probeMode,
        LowDelayEncoderConfigurationResult currentResult)
    {
        var lowLatencyApplied = currentResult.LowLatencyModeApplied;
        var bPictureCountApplied = currentResult.BPictureCountApplied;
        var gopSizeApplied = currentResult.GopSizeApplied;
        var qualityVsSpeedApplied = currentResult.QualityVsSpeedApplied;

        IMFAttributes? transformAttributes = null;
        IntPtr transformUnknownPtr = IntPtr.Zero;
        IntPtr codecApiPtr = IntPtr.Zero;
        ICodecAPI? codecApi = null;
        try
        {
            if (encoderTransform.GetAttributes(out transformAttributes) >= 0 && transformAttributes is not null)
            {
                lowLatencyApplied |= TrySetUInt32Attribute(transformAttributes, MfLowLatency, 1);
            }

            transformUnknownPtr = Marshal.GetIUnknownForObject(encoderTransform);
            var iid = IidICodecApi;
            if (Marshal.QueryInterface(transformUnknownPtr, ref iid, out codecApiPtr) >= 0 && codecApiPtr != IntPtr.Zero)
            {
                codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(codecApiPtr);
                lowLatencyApplied |= TrySetCodecApiUInt32(codecApi, MfLowLatency, 1);
                bPictureCountApplied |= TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvDefaultBPictureCount, 0);
                gopSizeApplied |= TrySetCodecApiUInt32(
                    codecApi,
                    CodecApiAvEncMpvGopSize,
                    (uint)Math.Max(1, targetFramesPerSecond));
                qualityVsSpeedApplied |= TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonQualityVsSpeed, LowDelayQualityVsSpeedValue);
            }
        }
        catch (Exception ex)
        {
            if (!probeMode)
            {
                LogLifecycle(
                    "screenshare_h264_encoder_low_delay_profile_failed",
                    $"strategy={strategyLabel}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            }
        }
        finally
        {
            ReleaseComObject(transformAttributes);
            if (codecApiPtr != IntPtr.Zero)
            {
                Marshal.Release(codecApiPtr);
            }

            if (transformUnknownPtr != IntPtr.Zero)
            {
                Marshal.Release(transformUnknownPtr);
            }
        }

        var result = new LowDelayEncoderConfigurationResult(
            LowLatencyModeApplied: lowLatencyApplied,
            BPictureCountApplied: bPictureCountApplied,
            GopSizeApplied: gopSizeApplied,
            QualityVsSpeedApplied: qualityVsSpeedApplied);
        if (!probeMode)
        {
            LogLifecycle(
                "screenshare_h264_encoder_low_delay_profile",
                $"strategy={strategyLabel}; state={result.State}; low_latency={(result.LowLatencyModeApplied ? 1 : 0)}; b_frames_zero={(result.BPictureCountApplied ? 1 : 0)}; gop_target={Math.Max(1, targetFramesPerSecond)}; gop_applied={(result.GopSizeApplied ? 1 : 0)}; quality_vs_speed={(result.QualityVsSpeedApplied ? 1 : 0)}; quality_vs_speed_value={LowDelayQualityVsSpeedValue}; target_fps={Math.Max(1, targetFramesPerSecond)}; transport_ip_only_mode={(transportIpOnly ? 1 : 0)}; target_bitrate={targetBitrate}");
        }

        return result;
    }

    private static bool TrySetCodecApiUInt32(ICodecAPI codecApi, Guid api, uint value)
    {
        try
        {
            if (codecApi.IsSupported(ref api) < 0)
            {
                return false;
            }

            if (codecApi.IsModifiable(ref api) < 0)
            {
                return false;
            }

            var variant = CodecApiVariant.FromUInt32(value);
            var variantSize = Marshal.SizeOf<CodecApiVariant>();
            var variantPtr = Marshal.AllocHGlobal(variantSize);
            try
            {
                Marshal.StructureToPtr(variant, variantPtr, false);
                return codecApi.SetValue(ref api, variantPtr) >= 0;
            }
            finally
            {
                Marshal.FreeHGlobal(variantPtr);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRequestTransformKeyFrame(IMFTransform encoderTransform)
    {
        IntPtr transformUnknownPtr = IntPtr.Zero;
        IntPtr codecApiPtr = IntPtr.Zero;
        try
        {
            transformUnknownPtr = Marshal.GetIUnknownForObject(encoderTransform);
            var iid = IidICodecApi;
            if (Marshal.QueryInterface(transformUnknownPtr, ref iid, out codecApiPtr) < 0 || codecApiPtr == IntPtr.Zero)
            {
                return false;
            }

            var codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(codecApiPtr);
            return TrySetCodecApiUInt32(codecApi, CodecApiAvEncVideoForceKeyFrame, 1);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (codecApiPtr != IntPtr.Zero)
            {
                Marshal.Release(codecApiPtr);
            }

            if (transformUnknownPtr != IntPtr.Zero)
            {
                Marshal.Release(transformUnknownPtr);
            }
        }
    }

    private static bool TrySetUInt32Attribute(IMFAttributes attributes, Guid key, uint value)
    {
        try
        {
            return attributes.SetUINT32(ref key, value) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ProcessTransformMessage(
        IMFTransform encoderTransform,
        int message,
        IntPtr param,
        string stage,
        string strategyLabel,
        bool probeMode)
    {
        int hr;
        try
        {
            hr = encoderTransform.ProcessMessage(message, param);
        }
        catch (Exception ex) when (probeMode && ex.HResult == ENotImpl)
        {
            LogLifecycle(
                "screenshare_h264_transform_optional_message_unavailable",
                $"strategy={strategyLabel}; stage={stage}; hresult=0x{ex.HResult:X8}; probe_mode=1");
            return;
        }

        LogLifecycle(
            "screenshare_h264_transform_message_result",
            $"strategy={strategyLabel}; stage={stage}; hresult=0x{hr:X8}; probe_mode={(probeMode ? 1 : 0)}");

        if (hr >= 0)
        {
            return;
        }

        if (probeMode && hr == ENotImpl)
        {
            LogLifecycle(
                "screenshare_h264_transform_optional_message_unavailable",
                $"strategy={strategyLabel}; stage={stage}; hresult=0x{hr:X8}; probe_mode=1");
            return;
        }

        Marshal.ThrowExceptionForHR(hr);
    }

    private static IMFMediaType CreateOutputMediaType(int width, int height, int targetFramesPerSecond, uint bitrate)
    {
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out var mediaType));
        try
        {
            var majorType = MfMediaTypeVideo;
            var subtype = MfVideoFormatH264;
            var majorTypeKey = MfMtMajorType;
            var subtypeKey = MfMtSubtype;
            var avgBitrateKey = MfMtAvgBitrate;
            var frameSizeKey = MfMtFrameSize;
            var frameRateKey = MfMtFrameRate;
            var pixelAspectRatioKey = MfMtPixelAspectRatio;
            var interlaceModeKey = MfMtInterlaceMode;
            var independentSamplesKey = MfMtAllSamplesIndependent;
            var mpeg2ProfileKey = MfMtMpeg2Profile;
            Marshal.ThrowExceptionForHR(mediaType.SetGUID(ref majorTypeKey, ref majorType));
            Marshal.ThrowExceptionForHR(mediaType.SetGUID(ref subtypeKey, ref subtype));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref avgBitrateKey, bitrate));
            SetAttributeSize(mediaType, frameSizeKey, (uint)width, (uint)height);
            SetAttributeRatio(mediaType, frameRateKey, (uint)Math.Max(1, targetFramesPerSecond), 1);
            SetAttributeRatio(mediaType, pixelAspectRatioKey, 1, 1);
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref interlaceModeKey, MfVideoInterlaceProgressive));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref independentSamplesKey, 1));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref mpeg2ProfileKey, EAvEncH264VProfileMain));
            return mediaType;
        }
        catch
        {
            ReleaseComObject(mediaType);
            throw;
        }
    }

    private static IMFMediaType CreateInputMediaType(int width, int height, int targetFramesPerSecond)
    {
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out var mediaType));
        try
        {
            var majorType = MfMediaTypeVideo;
            var subtype = MfVideoFormatNv12;
            var majorTypeKey = MfMtMajorType;
            var subtypeKey = MfMtSubtype;
            var frameSizeKey = MfMtFrameSize;
            var frameRateKey = MfMtFrameRate;
            var pixelAspectRatioKey = MfMtPixelAspectRatio;
            var interlaceModeKey = MfMtInterlaceMode;
            var fixedSizeSamplesKey = MfMtFixedSizeSamples;
            var independentSamplesKey = MfMtAllSamplesIndependent;
            var sampleSizeKey = MfMtSampleSize;
            var defaultStrideKey = MfMtDefaultStride;
            Marshal.ThrowExceptionForHR(mediaType.SetGUID(ref majorTypeKey, ref majorType));
            Marshal.ThrowExceptionForHR(mediaType.SetGUID(ref subtypeKey, ref subtype));
            SetAttributeSize(mediaType, frameSizeKey, (uint)width, (uint)height);
            SetAttributeRatio(mediaType, frameRateKey, (uint)Math.Max(1, targetFramesPerSecond), 1);
            SetAttributeRatio(mediaType, pixelAspectRatioKey, 1, 1);
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref interlaceModeKey, MfVideoInterlaceProgressive));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref fixedSizeSamplesKey, 1));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref independentSamplesKey, 1));
            var sampleSize = checked((uint)(width * height * 3 / 2));
            var stride = checked((uint)width);
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref sampleSizeKey, sampleSize));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref defaultStrideKey, stride));
            return mediaType;
        }
        catch
        {
            ReleaseComObject(mediaType);
            throw;
        }
    }

    private IMFSample CreateInputSample(
        RawInputBufferStrategy strategy,
        EncoderConfiguration configuration,
        byte[] nv12Bytes,
        long sampleTimeHns,
        long sampleDurationHns,
        bool forceKeyFrame,
        IntPtr sharedD3DDevice,
        IntPtr sharedD3DContext)
    {
        var stage = "create_sample";
        IMFSample? sample = null;
        try
        {
            var tolerateClockReadbackFailure =
                strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate;
            stage = "create_sample";
            sample = CreateInputSampleUsingStrategy(
                strategy,
                configuration.Width,
                configuration.Height,
                nv12Bytes,
                sampleDurationHns,
                sharedD3DDevice,
                sharedD3DContext);
            stage = "set_sample_time";
            Marshal.ThrowExceptionForHR(sample.SetSampleTime(sampleTimeHns));
            stage = "set_sample_duration";
            Marshal.ThrowExceptionForHR(sample.SetSampleDuration(sampleDurationHns));
            if (forceKeyFrame)
            {
                stage = "set_force_keyframe_extension";
                var key = MfForcedKeyFrameDataUnitExtension;
                Marshal.ThrowExceptionForHR(sample.SetUINT32(ref key, 1));
            }

            stage = "verify_sample_clock";
            VerifySampleClockMetadata(
                sample,
                sampleTimeHns,
                sampleDurationHns,
                tolerateClockReadbackFailure,
                strategy.ToString().ToLowerInvariant());
            return sample;
        }
        catch (Exception ex)
        {
            if (ex is InputSampleCreationException inputSampleCreationException && inputSampleCreationException.InnerException is not null)
            {
                stage = inputSampleCreationException.Stage;
                ex = inputSampleCreationException.InnerException;
            }

            if (FeatureFlags.ScreenShareDeepDiagnostics)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_h264_create_input_sample_failed; {GetLogContext()}; strategy={strategy.ToString().ToLowerInvariant()}; stage={stage}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}; bytes={nv12Bytes.Length}; width={configuration.Width}; height={configuration.Height}");
            }
            if (sample is not null)
            {
                ReleaseComObject(sample);
            }

            throw;
        }
    }

    private static IMFSample CreateInputSampleUsingStrategy(
        RawInputBufferStrategy strategy,
        int width,
        int height,
        byte[] nv12Bytes,
        long sampleDurationHns,
        IntPtr sharedD3DDevice,
        IntPtr sharedD3DContext,
        uint? d3dBindFlagsOverride = null)
    {
        switch (strategy)
        {
            case RawInputBufferStrategy.CpuMemoryBufferNv12:
                return CreateCpuMemoryBufferSample(nv12Bytes);
            case RawInputBufferStrategy.Cpu2DVideoBuffer:
                return CreateCpu2DVideoBufferSample(width, height, nv12Bytes);
            case RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture:
                return CreateDxgiSurfaceNv12Sample(width, height, nv12Bytes, sharedD3DDevice, sharedD3DContext, DxgiSubmissionStrategy.FreshTexturePerFrame, d3dBindFlagsOverride ?? 0);
            case RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate:
                return CreateDxgiSurfaceNv12Sample(width, height, nv12Bytes, sharedD3DDevice, sharedD3DContext, DxgiSubmissionStrategy.ReusableTextureUpdate, d3dBindFlagsOverride ?? 0);
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null);
        }
    }

    private static void WriteBufferBytes(IMFMediaBuffer buffer, byte[] bytes)
    {
        Marshal.ThrowExceptionForHR(buffer.Lock(out var scan0, out _, out _));
        try
        {
            Marshal.Copy(bytes, 0, scan0, bytes.Length);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static IMFSample CreateCpuMemoryBufferSample(byte[] nv12Bytes)
    {
        Exception? lastFailure = null;
        foreach (var variant in new[] { "memory_buffer", "aligned_memory_buffer" })
        {
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            var stage = $"create_{variant}";
            try
            {
                Marshal.ThrowExceptionForHR(MFCreateSample(out sample));
                stage = $"create_{variant}";
                if (variant == "aligned_memory_buffer")
                {
                    Marshal.ThrowExceptionForHR(MFCreateAlignedMemoryBuffer(nv12Bytes.Length, 16, out buffer));
                }
                else
                {
                    Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(nv12Bytes.Length, out buffer));
                }

                stage = "write_memory_buffer";
                WriteBufferBytes(buffer, nv12Bytes);
                stage = "set_current_length";
                Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(nv12Bytes.Length));
                stage = "add_buffer";
                Marshal.ThrowExceptionForHR(sample.AddBuffer(buffer));
                LogLifecycle(
                    "screenshare_h264_cpu_memory_sample_created",
                    $"variant={variant}; bytes={nv12Bytes.Length}");
                var result = sample;
                sample = null;
                return result;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                LogLifecycle(
                    "screenshare_h264_cpu_memory_sample_failed",
                    $"variant={variant}; stage={stage}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}; bytes={nv12Bytes.Length}");
                if (variant == "aligned_memory_buffer")
                {
                    throw new InputSampleCreationException(stage, ex);
                }
            }
            finally
            {
                ReleaseComObject(buffer);
                ReleaseComObject(sample);
            }
        }

        throw new InputSampleCreationException("create_memory_buffer", lastFailure ?? new InvalidOperationException("CPU memory sample probe produced no result."));
    }

    private static IMFSample CreateCpu2DVideoBufferSample(int width, int height, byte[] nv12Bytes)
    {
        var stage = "create_2d_buffer";
        IMFMediaBuffer? buffer = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreate2DMediaBuffer(
                (uint)width,
                (uint)height,
                DxgiFormatNv12,
                false,
                out buffer));
            stage = "write_buffer_bytes";
            WriteBufferBytes(buffer, nv12Bytes);
            stage = "set_current_length";
            Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(nv12Bytes.Length));
            stage = "create_video_sample_from_surface";
            Marshal.ThrowExceptionForHR(MFCreateVideoSampleFromSurface(buffer, out var sample));
            return sample;
        }
        catch (Exception ex)
        {
            throw new InputSampleCreationException(stage, ex);
        }
        finally
        {
            if (buffer is not null)
            {
                ReleaseComObject(buffer);
            }
        }
    }

    private static IMFSample CreateDxgiSurfaceNv12Sample(
        int width,
        int height,
        byte[] nv12Bytes,
        IntPtr sharedD3DDevice,
        IntPtr sharedD3DContext,
        DxgiSubmissionStrategy submissionStrategy,
        uint bindFlagsOverride)
    {
        var stage = "create_d3d11_device";
        IntPtr devicePtr = IntPtr.Zero;
        IntPtr contextPtr = IntPtr.Zero;
        IntPtr texturePtr = IntPtr.Zero;
        IntPtr uploadTexturePtr = IntPtr.Zero;
        IntPtr initialDataPtr = IntPtr.Zero;
        IntPtr pixelsPtr = IntPtr.Zero;
        var usingSharedDevice = sharedD3DDevice != IntPtr.Zero;
        IMFSample? sample = null;
        try
        {
            if (usingSharedDevice)
            {
                devicePtr = sharedD3DDevice;
                Marshal.AddRef(devicePtr);
                if (sharedD3DContext != IntPtr.Zero)
                {
                    contextPtr = sharedD3DContext;
                    Marshal.AddRef(contextPtr);
                }
            }
            else
            {
                var hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Hardware,
                    IntPtr.Zero,
                    D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
                    IntPtr.Zero,
                    0,
                    D3D11SdkVersion,
                    out devicePtr,
                    out _,
                    out contextPtr);
                if (hr < 0)
                {
                    hr = D3D11CreateDevice(
                        IntPtr.Zero,
                        D3DDriverType.Warp,
                        IntPtr.Zero,
                        D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
                        IntPtr.Zero,
                        0,
                        D3D11SdkVersion,
                        out devicePtr,
                        out _,
                        out contextPtr);
                }

                Marshal.ThrowExceptionForHR(hr);
            }
            stage = "allocate_pixel_buffer";

            var textureDesc = new D3D11Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormatNv12,
                SampleDesc = new DXGISampleDesc { Count = 1, Quality = 0 },
                Usage = D3D11UsageDefault,
                BindFlags = bindFlagsOverride,
                CPUAccessFlags = 0,
                MiscFlags = 0,
            };

            pixelsPtr = Marshal.AllocHGlobal(nv12Bytes.Length);
            Marshal.Copy(nv12Bytes, 0, pixelsPtr, nv12Bytes.Length);

            if (submissionStrategy == DxgiSubmissionStrategy.FreshTexturePerFrame)
            {
                var initialData = new D3D11SubresourceData
                {
                    SysMem = pixelsPtr,
                    SysMemPitch = (uint)width,
                    SysMemSlicePitch = (uint)nv12Bytes.Length,
                };

                stage = "allocate_subresource_data";
                initialDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<D3D11SubresourceData>());
                Marshal.StructureToPtr(initialData, initialDataPtr, false);
            }

            stage = "query_d3d_device";
            using var device = QueryInterface<ID3D11Device>(devicePtr);
            LogLifecycle(
                "screenshare_h264_dxgi_texture_create_attempt",
                $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; device_qi=1; shared_device={(usingSharedDevice ? 1 : 0)}; width={textureDesc.Width}; height={textureDesc.Height}; format={textureDesc.Format}; usage={textureDesc.Usage}; bind_flags={textureDesc.BindFlags}; bind_flags_override=0x{bindFlagsOverride:X8}; cpu_access={textureDesc.CPUAccessFlags}; misc_flags={textureDesc.MiscFlags}; sample_count={textureDesc.SampleDesc.Count}; sample_quality={textureDesc.SampleDesc.Quality}");

            stage = "create_texture";
            Marshal.ThrowExceptionForHR(device.Value.CreateTexture2D(
                ref textureDesc,
                submissionStrategy == DxgiSubmissionStrategy.FreshTexturePerFrame ? initialDataPtr : IntPtr.Zero,
                out texturePtr));
            if (texturePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateTexture2D returned a null texture pointer.");
            }

            if (submissionStrategy == DxgiSubmissionStrategy.ReusableTextureUpdate)
            {
                if (contextPtr == IntPtr.Zero)
                {
                    throw new InputSampleCreationException("missing_d3d_context", new InvalidOperationException("DXGI reusable texture update requires a valid D3D11 immediate context."));
                }

                LogLifecycle(
                    "screenshare_h264_reusable_texture_identity",
                    $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; width={textureDesc.Width}; height={textureDesc.Height}; format={textureDesc.Format}; usage={textureDesc.Usage}; bind_flags={textureDesc.BindFlags}; misc_flags={textureDesc.MiscFlags}; sample_count={textureDesc.SampleDesc.Count}; sample_quality={textureDesc.SampleDesc.Quality}; device_match=skipped");

                stage = "query_d3d_context";
                if (contextPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("DXGI reusable texture update requires a valid D3D11 immediate context pointer.");
                }
                LogLifecycle(
                    "screenshare_h264_reusable_texture_upload",
                    $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; context_qi=1; width={width}; height={height}; row_pitch={(uint)width}; slice_pitch={(uint)nv12Bytes.Length}; uv_offset={(uint)(width * height)}");

                var uploadTextureDesc = textureDesc;
                uploadTextureDesc.Usage = D3D11UsageStaging;
                uploadTextureDesc.CPUAccessFlags = D3D11CpuAccessWrite;
                uploadTextureDesc.BindFlags = 0;
                uploadTextureDesc.MiscFlags = 0;

                stage = "create_upload_texture";
                Marshal.ThrowExceptionForHR(device.Value.CreateTexture2D(
                    ref uploadTextureDesc,
                    IntPtr.Zero,
                    out uploadTexturePtr));
                if (uploadTexturePtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateTexture2D returned a null upload texture pointer.");
                }

                stage = "map_upload_texture";
                Marshal.ThrowExceptionForHR(InvokeD3D11Map(
                    contextPtr,
                    uploadTexturePtr,
                    0,
                    D3D11MapWrite,
                    0,
                    out var mapped));
                try
                {
                    stage = "copy_mapped_upload_texture";
                    CopyNv12ToMappedTexture(nv12Bytes, width, height, mapped);
                }
                finally
                {
                    stage = "unmap_upload_texture";
                    InvokeD3D11Unmap(contextPtr, uploadTexturePtr, 0);
                }

                stage = "copy_upload_texture_to_sample";
                InvokeD3D11CopyResource(contextPtr, texturePtr, uploadTexturePtr);
            }

            stage = "create_dxgi_surface_buffer_sample";
            try
            {
                sample = CreateSampleFromDxgiSurfaceBuffer(texturePtr);
                LogLifecycle(
                    "screenshare_h264_dxgi_sample_created",
                    $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; method=dxgi_surface_buffer; width={width}; height={height}; bytes={nv12Bytes.Length}; format={textureDesc.Format}; usage={textureDesc.Usage}; bind_flags={textureDesc.BindFlags}; misc_flags={textureDesc.MiscFlags}; pitch={(uint)width}; slice_pitch={(uint)nv12Bytes.Length}; has_context={(contextPtr != IntPtr.Zero ? 1 : 0)}");
            }
            catch (Exception ex)
            {
                var failureStage = ex is InputSampleCreationException inputSampleCreationException
                    ? inputSampleCreationException.Stage
                    : "unknown";
                LogLifecycle(
                    "screenshare_h264_dxgi_surface_buffer_sample_failed",
                    $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; stage={failureStage}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");

                stage = "create_video_sample_from_surface";
                var texture = Marshal.GetObjectForIUnknown(texturePtr);
                IMFSample? videoSurfaceSample = null;
                try
                {
                    Marshal.ThrowExceptionForHR(MFCreateVideoSampleFromSurface(texture, out videoSurfaceSample));
                    try
                    {
                        sample = CloneBuffersIntoStandardSample(videoSurfaceSample);
                        LogLifecycle(
                            "screenshare_h264_dxgi_sample_created",
                            $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; method=video_surface_sample_clone; width={width}; height={height}; bytes={nv12Bytes.Length}; format={textureDesc.Format}; usage={textureDesc.Usage}; bind_flags={textureDesc.BindFlags}; misc_flags={textureDesc.MiscFlags}; pitch={(uint)width}; slice_pitch={(uint)nv12Bytes.Length}; has_context={(contextPtr != IntPtr.Zero ? 1 : 0)}; {DescribeSampleBuffers(sample, nv12Bytes.Length, false)}");
                    }
                    catch (Exception cloneEx)
                    {
                        LogLifecycle(
                            "screenshare_h264_dxgi_surface_sample_clone_failed",
                            $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; reason={cloneEx.GetType().Name}; hresult=0x{cloneEx.HResult:X8}; message={Sanitize(cloneEx.Message)}; surface_state={DescribeSampleBuffers(videoSurfaceSample, nv12Bytes.Length, true)}");
                        try
                        {
                            sample = CreateStandardSampleFromContiguousBuffer(videoSurfaceSample, nv12Bytes.Length);
                            LogLifecycle(
                                "screenshare_h264_dxgi_sample_created",
                                $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; method=video_surface_contiguous_clone; width={width}; height={height}; bytes={nv12Bytes.Length}; format={textureDesc.Format}; usage={textureDesc.Usage}; bind_flags={textureDesc.BindFlags}; misc_flags={textureDesc.MiscFlags}; pitch={(uint)width}; slice_pitch={(uint)nv12Bytes.Length}; has_context={(contextPtr != IntPtr.Zero ? 1 : 0)}; {DescribeSampleBuffers(sample, nv12Bytes.Length, false)}");
                        }
                        catch (Exception contiguousCloneEx)
                        {
                            LogLifecycle(
                                "screenshare_h264_dxgi_surface_contiguous_clone_failed",
                                $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; reason={contiguousCloneEx.GetType().Name}; hresult=0x{contiguousCloneEx.HResult:X8}; message={Sanitize(contiguousCloneEx.Message)}; surface_state={DescribeSampleBuffers(videoSurfaceSample, nv12Bytes.Length, true)}");
                            sample = videoSurfaceSample;
                            videoSurfaceSample = null;
                            LogLifecycle(
                                "screenshare_h264_dxgi_sample_created",
                                $"source={(usingSharedDevice ? "shared_sinkwriter_device" : "standalone_device")}; submission_model={submissionStrategy.ToString().ToLowerInvariant()}; method=video_sample_from_surface; width={width}; height={height}; bytes={nv12Bytes.Length}; format={textureDesc.Format}; usage={textureDesc.Usage}; bind_flags={textureDesc.BindFlags}; misc_flags={textureDesc.MiscFlags}; pitch={(uint)width}; slice_pitch={(uint)nv12Bytes.Length}; has_context={(contextPtr != IntPtr.Zero ? 1 : 0)}; {DescribeSampleBuffers(sample, nv12Bytes.Length, true)}");
                        }
                    }
                }
                finally
                {
                    ReleaseComObject(videoSurfaceSample);
                    if (Marshal.IsComObject(texture))
                    {
                        Marshal.ReleaseComObject(texture);
                    }
                }
            }
            var result = sample;
            sample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InputSampleCreationException(stage, ex);
        }
        finally
        {
            if (sample is not null)
            {
                ReleaseComObject(sample);
            }
            if (texturePtr != IntPtr.Zero)
            {
                Marshal.Release(texturePtr);
            }
            if (uploadTexturePtr != IntPtr.Zero)
            {
                Marshal.Release(uploadTexturePtr);
            }

            if (contextPtr != IntPtr.Zero)
            {
                Marshal.Release(contextPtr);
            }

            if (devicePtr != IntPtr.Zero)
            {
                Marshal.Release(devicePtr);
            }

            if (initialDataPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(initialDataPtr);
            }

            if (pixelsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pixelsPtr);
            }
        }
    }

    private static IEnumerable<RawInputBufferStrategy> EnumerateStrategyAttempts(RawInputBufferStrategy preferredStrategy)
    {
        yield return preferredStrategy;
        foreach (var strategy in new[]
                 {
                     RawInputBufferStrategy.CpuMemoryBufferNv12,
                     RawInputBufferStrategy.Cpu2DVideoBuffer,
                     RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate,
                     RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture,
                 })
        {
            if (strategy != preferredStrategy)
            {
                yield return strategy;
            }
        }
    }

    private static void PromoteWorkingInputBufferStrategy(RawInputBufferStrategy strategy)
    {
        lock (InputBufferProbeSync)
        {
            selectedInputBufferStrategy = strategy;
            inputBufferProbeCompleted = true;
            inputBufferProbeSucceeded = true;
            lastInputBufferRootCause = "ok";
            lastInputBufferProbeSummary = $"selected_strategy={strategy.ToString().ToLowerInvariant()}; status=write_sample_accepted";
        }
    }

    private static IMFSample CreateOutputSample(MftOutputStreamInfo outputInfo, EncoderConfiguration configuration)
    {
        Marshal.ThrowExceptionForHR(MFCreateSample(out var sample));
        IMFMediaBuffer? buffer = null;
        try
        {
            var bufferSize = outputInfo.CbSize > 0
                ? checked((int)outputInfo.CbSize)
                : checked(configuration.Width * configuration.Height * 3 / 2);
            Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(bufferSize, out buffer));
            Marshal.ThrowExceptionForHR(sample.AddBuffer(buffer));
            return sample;
        }
        catch
        {
            ReleaseComObject(sample);
            throw;
        }
        finally
        {
            if (buffer is not null)
            {
                ReleaseComObject(buffer);
            }
        }
    }

    private static string DescribeMediaType(string prefix, IMFMediaType mediaType)
    {
        return string.Join(
            "; ",
            $"{prefix}_subtype={GetMediaSubtypeName(mediaType)}",
            $"{prefix}_size={GetAttributeSize(mediaType, MfMtFrameSize)}",
            $"{prefix}_fps={GetAttributeRatio(mediaType, MfMtFrameRate)}",
            $"{prefix}_stride={GetOptionalUInt32(mediaType, MfMtDefaultStride)}",
            $"{prefix}_bitrate={GetOptionalUInt32(mediaType, MfMtAvgBitrate)}",
            $"{prefix}_profile={GetOptionalUInt32(mediaType, MfMtMpeg2Profile)}",
            $"{prefix}_interlace={GetOptionalUInt32(mediaType, MfMtInterlaceMode)}");
    }

    private static string GetMediaSubtypeName(IMFMediaType mediaType)
    {
        var subtypeKey = MfMtSubtype;
        try
        {
            Marshal.ThrowExceptionForHR(mediaType.GetGUID(ref subtypeKey, out var subtype));
            if (subtype == MfVideoFormatNv12)
            {
                return "NV12";
            }

            if (subtype == MfVideoFormatH264)
            {
                return "H264";
            }

            return subtype.ToString("D");
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetAttributeSize(IMFMediaType mediaType, Guid key)
    {
        try
        {
            Marshal.ThrowExceptionForHR(mediaType.GetUINT64(ref key, out var packed));
            return $"{(uint)(packed >> 32)}x{(uint)(packed & 0xffffffff)}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetAttributeRatio(IMFMediaType mediaType, Guid key)
    {
        try
        {
            Marshal.ThrowExceptionForHR(mediaType.GetUINT64(ref key, out var packed));
            var numerator = (uint)(packed >> 32);
            var denominator = (uint)(packed & 0xffffffff);
            return denominator == 0 ? $"{numerator}/0" : $"{numerator}/{denominator}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetOptionalUInt32(IMFMediaType mediaType, Guid key)
    {
        try
        {
            Marshal.ThrowExceptionForHR(mediaType.GetUINT32(ref key, out var value));
            return value.ToString();
        }
        catch
        {
            return "unset";
        }
    }

    private static string GetOptionalUInt32(IMFAttributes attributes, Guid key)
    {
        try
        {
            Marshal.ThrowExceptionForHR(attributes.GetUINT32(ref key, out var value));
            return value.ToString();
        }
        catch
        {
            return "unset";
        }
    }

    private static bool TryGetOptionalUInt32(IMFAttributes attributes, Guid key, out uint value)
    {
        try
        {
            Marshal.ThrowExceptionForHR(attributes.GetUINT32(ref key, out value));
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static void VerifySampleClockMetadata(
        IMFSample sample,
        long expectedTimeHns,
        long expectedDurationHns,
        bool tolerateReadbackFailure = false,
        string? strategyLabel = null)
    {
        long actualTimeHns;
        try
        {
            Marshal.ThrowExceptionForHR(sample.GetSampleTime(out actualTimeHns));
        }
        catch (Exception ex)
        {
            if (tolerateReadbackFailure)
            {
                LogLifecycle(
                    "screenshare_h264_sample_clock_readback_unavailable",
                    $"strategy={strategyLabel ?? "unknown"}; field=time; expected={expectedTimeHns}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                return;
            }

            throw new InvalidOperationException(
                $"GetSampleTime failed while verifying sample clock metadata. Expected {expectedTimeHns}. HRESULT=0x{ex.HResult:X8}.",
                ex);
        }

        long actualDurationHns;
        try
        {
            Marshal.ThrowExceptionForHR(sample.GetSampleDuration(out actualDurationHns));
        }
        catch (Exception ex)
        {
            if (tolerateReadbackFailure)
            {
                LogLifecycle(
                    "screenshare_h264_sample_clock_readback_unavailable",
                    $"strategy={strategyLabel ?? "unknown"}; field=duration; expected={expectedDurationHns}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                return;
            }

            throw new InvalidOperationException(
                $"GetSampleDuration failed while verifying sample clock metadata. Expected {expectedDurationHns}. HRESULT=0x{ex.HResult:X8}.",
                ex);
        }

        if (actualTimeHns != expectedTimeHns)
        {
            if (tolerateReadbackFailure)
            {
                LogLifecycle(
                    "screenshare_h264_sample_clock_readback_mismatch",
                    $"strategy={strategyLabel ?? "unknown"}; field=time; expected={expectedTimeHns}; actual={actualTimeHns}");
                return;
            }

            throw new InvalidOperationException(
                $"Sample time verification failed. Expected {expectedTimeHns}, actual {actualTimeHns}.");
        }

        if (actualDurationHns != expectedDurationHns)
        {
            if (tolerateReadbackFailure)
            {
                LogLifecycle(
                    "screenshare_h264_sample_clock_readback_mismatch",
                    $"strategy={strategyLabel ?? "unknown"}; field=duration; expected={expectedDurationHns}; actual={actualDurationHns}");
                return;
            }

            throw new InvalidOperationException(
                $"Sample duration verification failed. Expected {expectedDurationHns}, actual {actualDurationHns}.");
        }
    }

    private static byte[] ReadSampleBytes(IMFSample sample)
    {
        Marshal.ThrowExceptionForHR(sample.ConvertToContiguousBuffer(out var contiguousBuffer));
        try
        {
            Marshal.ThrowExceptionForHR(contiguousBuffer.GetCurrentLength(out var currentLength));
            if (currentLength <= 0)
            {
                return Array.Empty<byte>();
            }

            Marshal.ThrowExceptionForHR(contiguousBuffer.Lock(out var scan0, out _, out _));
            try
            {
                var bytes = new byte[currentLength];
                Marshal.Copy(scan0, bytes, 0, currentLength);
                return bytes;
            }
            finally
            {
                contiguousBuffer.Unlock();
            }
        }
        finally
        {
            ReleaseComObject(contiguousBuffer);
        }
    }

    private static byte[] ReadBufferBytes(IMFMediaBuffer buffer)
    {
        Marshal.ThrowExceptionForHR(buffer.GetCurrentLength(out var currentLength));
        if (currentLength <= 0)
        {
            return Array.Empty<byte>();
        }

        Marshal.ThrowExceptionForHR(buffer.Lock(out var scan0, out _, out _));
        try
        {
            var bytes = new byte[currentLength];
            Marshal.Copy(scan0, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static string DescribeSampleBuffers(IMFSample sample, int expectedLength, bool attemptNormalize)
    {
        var builder = new StringBuilder();
        try
        {
            Marshal.ThrowExceptionForHR(sample.GetBufferCount(out var bufferCount));
            builder.Append($"buffer_count={bufferCount}");
            for (uint index = 0; index < bufferCount; index++)
            {
                IMFMediaBuffer? buffer = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sample.GetBufferByIndex(index, out buffer));
                    builder.Append($"; buffer_{index}={DescribeMediaBuffer(buffer, expectedLength, attemptNormalize)}");
                }
                catch (Exception ex)
                {
                    builder.Append($"; buffer_{index}_error=0x{ex.HResult:X8}:{Sanitize(ex.Message)}");
                }
                finally
                {
                    ReleaseComObject(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            builder.Append($"buffer_count_error=0x{ex.HResult:X8}:{Sanitize(ex.Message)}");
        }

        return builder.ToString();
    }

    private static string DescribeMediaBuffer(IMFMediaBuffer buffer, int expectedLength, bool attemptNormalize)
    {
        var builder = new StringBuilder();
        int currentLength;
        int maxLength;
        try
        {
            Marshal.ThrowExceptionForHR(buffer.GetCurrentLength(out currentLength));
            builder.Append($"current={currentLength}");
        }
        catch (Exception ex)
        {
            builder.Append($"current_error=0x{ex.HResult:X8}:{Sanitize(ex.Message)}");
            return builder.ToString();
        }

        try
        {
            Marshal.ThrowExceptionForHR(buffer.GetMaxLength(out maxLength));
            builder.Append($",max={maxLength}");
        }
        catch (Exception ex)
        {
            builder.Append($",max_error=0x{ex.HResult:X8}:{Sanitize(ex.Message)}");
            return builder.ToString();
        }

        if (attemptNormalize &&
            currentLength == 0 &&
            expectedLength > 0 &&
            maxLength >= expectedLength)
        {
            try
            {
                Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(expectedLength));
                Marshal.ThrowExceptionForHR(buffer.GetCurrentLength(out currentLength));
                builder.Append($",normalized_to={expectedLength},post_current={currentLength}");
            }
            catch (Exception ex)
            {
                builder.Append($",normalize_error=0x{ex.HResult:X8}:{Sanitize(ex.Message)}");
            }
        }

        return builder.ToString();
    }

    private static bool IsSampleUsableForStrategy(RawInputBufferStrategy strategy, IMFSample sample)
    {
        if (strategy is not RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture and
            not RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate)
        {
            return true;
        }

        return TryGetSampleBufferCount(sample, out var bufferCount) && bufferCount > 0;
    }

    private static bool TryGetSampleBufferCount(IMFSample sample, out uint bufferCount)
    {
        try
        {
            Marshal.ThrowExceptionForHR(sample.GetBufferCount(out bufferCount));
            return true;
        }
        catch
        {
            bufferCount = 0;
            return false;
        }
    }

    private static void EnsureComInitializedForCurrentThread()
    {
        if (comInitializedForThread)
        {
            return;
        }

        var hr = CoInitializeEx(IntPtr.Zero, 0);
        if (hr >= 0 || hr == RpcEChangedMode)
        {
            comInitializedForThread = true;
            return;
        }

        Marshal.ThrowExceptionForHR(hr);
    }

    private ScreenShareVideoStreamConfigV1 BuildStreamConfig(EncoderConfiguration activeConfiguration, long streamEpoch)
    {
        return new ScreenShareVideoStreamConfigV1
        {
            SessionId = string.Empty,
            StreamEpoch = streamEpoch,
            Encoding = H264Encoding,
            CodecProfile = activeConfiguration.CodecProfile,
            DisplayInfoRevision = 0,
            DecoderConfigData = activeConfiguration.DecoderConfigData,
        };
    }

    private long ComputeSampleTimeHns(long capturedTsUtcMs)
    {
        if (capturedTsUtcMs <= 0)
        {
            lastSampleTimeHns += ComputeSampleDurationHns(configuration?.TargetFramesPerSecond ?? 1);
            return lastSampleTimeHns;
        }

        if (firstCapturedTsUtcMs is null)
        {
            firstCapturedTsUtcMs = capturedTsUtcMs;
            lastSampleTimeHns = 0;
            return 0;
        }

        var relative = Math.Max(0, capturedTsUtcMs - firstCapturedTsUtcMs.Value) * 10_000;
        if (relative <= lastSampleTimeHns)
        {
            relative = lastSampleTimeHns + ComputeSampleDurationHns(configuration?.TargetFramesPerSecond ?? 1);
        }

        lastSampleTimeHns = relative;
        return relative;
    }

    private static long ComputeSampleDurationHns(int targetFramesPerSecond)
    {
        var fps = Math.Max(1, targetFramesPerSecond);
        return Math.Max(1, HnsPerSecond / fps);
    }

    private static Bitmap CreateBgra32Bitmap(Bitmap source, int targetWidth, int targetHeight)
    {
        if (source.Width == targetWidth &&
            source.Height == targetHeight &&
            source.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb)
        {
            return new Bitmap(source);
        }

        var converted = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(converted);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        return converted;
    }

    private static byte[] ConvertBgra32BitmapToNv12(Bitmap bitmap, int targetWidth, int targetHeight)
    {
        var nv12 = new byte[targetWidth * targetHeight * 3 / 2];
        var rect = new Rectangle(0, 0, targetWidth, targetHeight);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            unsafe
            {
                FillNv12FromBgraPointer((byte*)data.Scan0, data.Stride, nv12, targetWidth, targetHeight);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return nv12;
    }

    private static void FillNv12FromBgraBuffer(byte[] bgraBytes, int absStride, byte[] nv12, int targetWidth, int targetHeight)
    {
        var yPlaneSize = targetWidth * targetHeight;

        for (var y = 0; y < targetHeight; y++)
        {
            var rowOffset = y * absStride;
            var yOffset = y * targetWidth;
            for (var x = 0; x < targetWidth; x++)
            {
                var pixelOffset = rowOffset + (x * 4);
                var b = bgraBytes[pixelOffset];
                var g = bgraBytes[pixelOffset + 1];
                var r = bgraBytes[pixelOffset + 2];
                nv12[yOffset + x] = ClampToByte(((66 * r) + (129 * g) + (25 * b) + 128 >> 8) + 16);
            }
        }

        for (var y = 0; y < targetHeight; y += 2)
        {
            var row0Offset = y * absStride;
            var row1Offset = Math.Min(y + 1, targetHeight - 1) * absStride;
            var uvOffset = yPlaneSize + ((y / 2) * targetWidth);
            for (var x = 0; x < targetWidth; x += 2)
            {
                var avgR = 0;
                var avgG = 0;
                var avgB = 0;

                AccumulatePixel(bgraBytes, row0Offset, x, ref avgR, ref avgG, ref avgB);
                AccumulatePixel(bgraBytes, row0Offset, Math.Min(x + 1, targetWidth - 1), ref avgR, ref avgG, ref avgB);
                AccumulatePixel(bgraBytes, row1Offset, x, ref avgR, ref avgG, ref avgB);
                AccumulatePixel(bgraBytes, row1Offset, Math.Min(x + 1, targetWidth - 1), ref avgR, ref avgG, ref avgB);

                avgR /= 4;
                avgG /= 4;
                avgB /= 4;

                nv12[uvOffset + x] = ClampToByte(((-38 * avgR) - (74 * avgG) + (112 * avgB) + 128 >> 8) + 128);
                if (x + 1 < targetWidth)
                {
                    nv12[uvOffset + x + 1] = ClampToByte(((112 * avgR) - (94 * avgG) - (18 * avgB) + 128 >> 8) + 128);
                }
            }
        }
    }

    internal static byte[] ConvertBgraBufferToNv12LegacyForTesting(byte[] bgraBytes, int absStride, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(bgraBytes);
        if (absStride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absStride));
        }

        if (targetWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        if (targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeight));
        }

        var nv12 = new byte[targetWidth * targetHeight * 3 / 2];
        FillNv12FromBgraBuffer(bgraBytes, absStride, nv12, targetWidth, targetHeight);
        return nv12;
    }

    internal static byte[] ConvertBgraBufferToNv12OptimizedForTesting(byte[] bgraBytes, int absStride, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(bgraBytes);
        if (absStride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absStride));
        }

        if (targetWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        if (targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeight));
        }

        var nv12 = new byte[targetWidth * targetHeight * 3 / 2];
        unsafe
        {
            fixed (byte* bgraPtr = bgraBytes)
            {
                FillNv12FromBgraPointer(bgraPtr, absStride, nv12, targetWidth, targetHeight);
            }
        }

        return nv12;
    }

    internal static byte[] ConvertBgraBufferToNv12SignedStrideForTesting(byte[] bgraBytes, int scan0Offset, int stride, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(bgraBytes);
        if (scan0Offset < 0 || scan0Offset >= bgraBytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(scan0Offset));
        }

        if (stride == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        if (targetWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        if (targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeight));
        }

        var nv12 = new byte[targetWidth * targetHeight * 3 / 2];
        unsafe
        {
            fixed (byte* bgraPtr = bgraBytes)
            {
                FillNv12FromBgraPointer(bgraPtr + scan0Offset, stride, nv12, targetWidth, targetHeight);
            }
        }

        return nv12;
    }

    internal static bool CanUseDirectNv12PreprocessForTesting(
        int sourceWidth,
        int sourceHeight,
        PixelFormat sourcePixelFormat,
        int targetWidth,
        int targetHeight)
        => sourceWidth > 0 &&
           sourceHeight > 0 &&
           sourceWidth == targetWidth &&
           sourceHeight == targetHeight &&
           IsSupportedDirectBgraPixelFormat(sourcePixelFormat);

    private static bool IsSupportedDirectBgraPixelFormat(PixelFormat pixelFormat)
        => pixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb;

    private static bool IsUnsafeDirectNv12PreprocessEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable(UnsafeDirectNv12EnvironmentVariableName),
            "1",
            StringComparison.Ordinal);

    private static bool IsUnsafeFfmpegSwscalePreprocessEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable(UnsafeFfmpegSwscaleEnvironmentVariableName),
            "1",
            StringComparison.Ordinal);

    private static unsafe void FillNv12FromBgraPointer(byte* bgraBytes, int stride, byte[] nv12, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (bgraBytes is null)
        {
            throw new ArgumentNullException(nameof(bgraBytes));
        }

        if (targetWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        if (targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeight));
        }

        var absStride = Math.Abs(stride);
        var minimumRowBytes = checked(targetWidth * 4);
        if (absStride < minimumRowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride is smaller than the requested BGRA row width.");
        }

        var requiredNv12Bytes = checked((targetWidth * targetHeight) + (targetWidth * ((targetHeight + 1) / 2)));
        if (nv12.Length < requiredNv12Bytes)
        {
            throw new ArgumentException("NV12 buffer is smaller than the requested frame dimensions.", nameof(nv12));
        }

        fixed (byte* nv12Bytes = nv12)
        {
            var yPlaneSize = targetWidth * targetHeight;

            for (var y = 0; y < targetHeight; y++)
            {
                var row = bgraBytes + (y * stride);
                var yPlane = nv12Bytes + (y * targetWidth);
                for (var x = 0; x < targetWidth; x++)
                {
                    var pixel = row + (x * 4);
                    var b = pixel[0];
                    var g = pixel[1];
                    var r = pixel[2];
                    yPlane[x] = ClampToByte(((66 * r) + (129 * g) + (25 * b) + 128 >> 8) + 16);
                }
            }

            for (var y = 0; y < targetHeight; y += 2)
            {
                var row0 = bgraBytes + (y * stride);
                var row1 = bgraBytes + (Math.Min(y + 1, targetHeight - 1) * stride);
                var uvPlane = nv12Bytes + yPlaneSize + ((y / 2) * targetWidth);
                for (var x = 0; x < targetWidth; x += 2)
                {
                    var avgR = 0;
                    var avgG = 0;
                    var avgB = 0;

                    AccumulatePixel(row0, x, ref avgR, ref avgG, ref avgB);
                    AccumulatePixel(row0, Math.Min(x + 1, targetWidth - 1), ref avgR, ref avgG, ref avgB);
                    AccumulatePixel(row1, x, ref avgR, ref avgG, ref avgB);
                    AccumulatePixel(row1, Math.Min(x + 1, targetWidth - 1), ref avgR, ref avgG, ref avgB);

                    avgR /= 4;
                    avgG /= 4;
                    avgB /= 4;

                    uvPlane[x] = ClampToByte(((-38 * avgR) - (74 * avgG) + (112 * avgB) + 128 >> 8) + 128);
                    if (x + 1 < targetWidth)
                    {
                        uvPlane[x + 1] = ClampToByte(((112 * avgR) - (94 * avgG) - (18 * avgB) + 128 >> 8) + 128);
                    }
                }
            }
        }
    }

    private static byte[] TryReadSequenceHeader(IMFMediaType mediaType)
    {
        var sequenceHeaderKey = MfMtMpegSequenceHeader;
        try
        {
            var blobSizeHr = mediaType.GetBlobSize(ref sequenceHeaderKey, out var blobSize);
            if (blobSizeHr < 0 || blobSize == 0)
            {
                return Array.Empty<byte>();
            }

            var blobHr = mediaType.GetAllocatedBlob(ref sequenceHeaderKey, out var blobPtr, out var actualSize);
            if (blobHr < 0 || blobPtr == IntPtr.Zero || actualSize == 0)
            {
                return Array.Empty<byte>();
            }

            try
            {
                var bytes = new byte[actualSize];
                Marshal.Copy(blobPtr, bytes, 0, checked((int)actualSize));
                return bytes;
            }
            finally
            {
                Marshal.FreeCoTaskMem(blobPtr);
            }
        }
        catch (COMException ex)
        {
            LogLifecycle(
                "screenshare_h264_encoder_sequence_header_unavailable",
                $"reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
            return Array.Empty<byte>();
        }
    }

    private static void SetAttributeSize(IMFAttributes attributes, Guid key, uint width, uint height)
    {
        var packed = PackUint32Pair(width, height);
        Marshal.ThrowExceptionForHR(attributes.SetUINT64(ref key, packed));
    }

    private static void SetAttributeRatio(IMFAttributes attributes, Guid key, uint numerator, uint denominator)
    {
        var packed = PackUint32Pair(numerator, denominator);
        Marshal.ThrowExceptionForHR(attributes.SetUINT64(ref key, packed));
    }

    private static ulong PackUint32Pair(uint high, uint low)
    {
        return ((ulong)high << 32) | low;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch
        {
        }
    }

    private static ComReleaser<T> QueryInterface<T>(object comObject)
    {
        var sourceUnknown = Marshal.GetIUnknownForObject(comObject);
        try
        {
            var iid = typeof(T).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sourceUnknown, ref iid, out var interfacePtr));
            try
            {
                return new ComReleaser<T>((T)Marshal.GetObjectForIUnknown(interfacePtr), interfacePtr);
            }
            catch
            {
                Marshal.Release(interfacePtr);
                throw;
            }
        }
        finally
        {
            Marshal.Release(sourceUnknown);
        }
    }

    private static ComReleaser<T> QueryInterface<T>(IntPtr sourceUnknown)
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sourceUnknown, ref iid, out var interfacePtr));
        try
        {
            return new ComReleaser<T>((T)Marshal.GetObjectForIUnknown(interfacePtr), interfacePtr);
        }
        catch
        {
            Marshal.Release(interfacePtr);
            throw;
        }
    }

    private static int InvokeD3D11Map(
        IntPtr contextPtr,
        IntPtr resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out D3D11MappedSubresource mappedResource)
    {
        return GetVtableDelegate<D3D11DeviceContextMapDelegate>(contextPtr, 14)(
            contextPtr,
            resource,
            subresource,
            mapType,
            mapFlags,
            out mappedResource);
    }

    private static void InvokeD3D11Unmap(IntPtr contextPtr, IntPtr resource, uint subresource)
    {
        GetVtableDelegate<D3D11DeviceContextUnmapDelegate>(contextPtr, 15)(contextPtr, resource, subresource);
    }

    private static void InvokeD3D11CopyResource(IntPtr contextPtr, IntPtr destinationResource, IntPtr sourceResource)
    {
        GetVtableDelegate<D3D11DeviceContextCopyResourceDelegate>(contextPtr, 47)(contextPtr, destinationResource, sourceResource);
    }

    private static void CopyNv12ToMappedTexture(byte[] nv12Bytes, int width, int height, D3D11MappedSubresource mapped)
    {
        if (mapped.PData == IntPtr.Zero)
        {
            throw new InvalidOperationException("Map returned a null data pointer.");
        }

        var expectedLength = width * height + (width * height / 2);
        if (nv12Bytes.Length < expectedLength)
        {
            throw new ArgumentException($"NV12 buffer too small. Expected at least {expectedLength} bytes, got {nv12Bytes.Length}.", nameof(nv12Bytes));
        }

        var rowPitch = checked((int)mapped.RowPitch);
        var yPlaneBytes = width * height;
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(
                nv12Bytes,
                y * width,
                IntPtr.Add(mapped.PData, y * rowPitch),
                width);
        }

        var uvBase = IntPtr.Add(mapped.PData, rowPitch * height);
        for (var y = 0; y < height / 2; y++)
        {
            Marshal.Copy(
                nv12Bytes,
                yPlaneBytes + (y * width),
                IntPtr.Add(uvBase, y * rowPitch),
                width);
        }
    }

    private static IMFSample CreateSampleFromDxgiSurfaceBuffer(IntPtr texturePtr)
    {
        if (TryCreateSampleFromDxgiSurfaceBufferVariant(
                texturePtr,
                IidId3D11Texture2D,
                "texture_ptr",
                out var directSample,
                out var directFailure))
        {
            return directSample;
        }

        IntPtr textureUnknownPtr = IntPtr.Zero;
        Exception? dxgiSurfaceFailure = null;
        Exception? dxgiSurfaceUnknownFailure = null;
        Exception? dxgiSurfaceQueryFailure = null;
        try
        {
            textureUnknownPtr = QueryInterfacePtr(texturePtr, IidIUnknown);
            if (TryCreateSampleFromDxgiSurfaceBufferVariant(
                    textureUnknownPtr,
                    IidId3D11Texture2D,
                    "texture_iunknown",
                    out var textureUnknownSample,
                    out var textureUnknownFailure))
            {
                return textureUnknownSample;
            }

            IntPtr dxgiSurfacePtr = IntPtr.Zero;
            try
            {
                dxgiSurfacePtr = QueryInterfacePtr(texturePtr, IidIdxgiSurface);
                if (TryCreateSampleFromDxgiSurfaceBufferVariant(
                        dxgiSurfacePtr,
                        IidIdxgiSurface,
                        "dxgi_surface_ptr",
                        out var dxgiSurfaceSample,
                        out dxgiSurfaceFailure))
                {
                    return dxgiSurfaceSample;
                }

                IntPtr dxgiSurfaceUnknownPtr = IntPtr.Zero;
                try
                {
                    dxgiSurfaceUnknownPtr = QueryInterfacePtr(dxgiSurfacePtr, IidIUnknown);
                    if (TryCreateSampleFromDxgiSurfaceBufferVariant(
                            dxgiSurfaceUnknownPtr,
                            IidIdxgiSurface,
                            "dxgi_surface_iunknown",
                            out var dxgiSurfaceUnknownSample,
                            out dxgiSurfaceUnknownFailure))
                    {
                        return dxgiSurfaceUnknownSample;
                    }
                }
                finally
                {
                    if (dxgiSurfaceUnknownPtr != IntPtr.Zero)
                    {
                        Marshal.Release(dxgiSurfaceUnknownPtr);
                    }
                }
            }
            catch (Exception dxgiSurfaceQueryEx) when (dxgiSurfaceQueryEx is not InputSampleCreationException)
            {
                dxgiSurfaceQueryFailure = dxgiSurfaceQueryEx;
                LogLifecycle(
                    "screenshare_h264_dxgi_surface_buffer_variant_failed",
                    $"variant=dxgi_surface_query; surface_iid={IidIdxgiSurface}; reason={dxgiSurfaceQueryEx.GetType().Name}; hresult=0x{dxgiSurfaceQueryEx.HResult:X8}; message={Sanitize(dxgiSurfaceQueryEx.Message)}");
            }
            finally
            {
                if (dxgiSurfacePtr != IntPtr.Zero)
                {
                    Marshal.Release(dxgiSurfacePtr);
                }
            }

            object? textureObject = null;
            try
            {
                textureObject = Marshal.GetObjectForIUnknown(texturePtr);
                if (TryCreateSampleFromDxgiSurfaceBufferObjectVariant(
                        textureObject,
                        IidId3D11Texture2D,
                        "texture_object",
                        out var textureObjectSample,
                        out var textureObjectFailure))
                {
                    return textureObjectSample;
                }

                object? dxgiSurfaceObject = null;
                try
                {
                    dxgiSurfacePtr = QueryInterfacePtr(texturePtr, IidIdxgiSurface);
                    dxgiSurfaceObject = Marshal.GetObjectForIUnknown(dxgiSurfacePtr);
                    if (TryCreateSampleFromDxgiSurfaceBufferObjectVariant(
                            dxgiSurfaceObject,
                            IidIdxgiSurface,
                            "dxgi_surface_object",
                            out var dxgiSurfaceObjectSample,
                            out var dxgiSurfaceObjectFailure))
                    {
                        return dxgiSurfaceObjectSample;
                    }

                    throw new InputSampleCreationException(
                        "create_surface_buffer",
                        new InvalidOperationException(
                            $"MFCreateDXGISurfaceBuffer failed for texture_ptr (0x{directFailure.HResult:X8}), texture_iunknown (0x{textureUnknownFailure.HResult:X8}), dxgi_surface_ptr (0x{(dxgiSurfaceFailure?.HResult ?? 0):X8}), dxgi_surface_iunknown (0x{(dxgiSurfaceUnknownFailure?.HResult ?? 0):X8}), dxgi_surface_query (0x{(dxgiSurfaceQueryFailure?.HResult ?? 0):X8}), texture_object (0x{textureObjectFailure.HResult:X8}), and dxgi_surface_object (0x{dxgiSurfaceObjectFailure.HResult:X8}) variants.",
                            dxgiSurfaceObjectFailure));
                }
                finally
                {
                    if (dxgiSurfaceObject is not null && Marshal.IsComObject(dxgiSurfaceObject))
                    {
                        Marshal.ReleaseComObject(dxgiSurfaceObject);
                    }

                    if (dxgiSurfacePtr != IntPtr.Zero)
                    {
                        Marshal.Release(dxgiSurfacePtr);
                        dxgiSurfacePtr = IntPtr.Zero;
                    }
                }
            }
            finally
            {
                if (textureObject is not null && Marshal.IsComObject(textureObject))
                {
                    Marshal.ReleaseComObject(textureObject);
                }
            }
        }
        finally
        {
            if (textureUnknownPtr != IntPtr.Zero)
            {
                Marshal.Release(textureUnknownPtr);
            }
        }
    }

    private static bool TryCreateSampleFromDxgiSurfaceBufferVariant(
        IntPtr surfacePtr,
        Guid surfaceIid,
        string variantLabel,
        out IMFSample sample,
        out Exception failure)
    {
        try
        {
            sample = CreateSampleFromDxgiSurfaceBufferCore(surfacePtr, surfaceIid, variantLabel);
            failure = null!;
            return true;
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_dxgi_surface_buffer_variant_failed",
                $"variant={variantLabel}; surface_iid={surfaceIid}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            sample = null!;
            failure = ex;
            return false;
        }
    }

    private static bool TryCreateSampleFromDxgiSurfaceBufferObjectVariant(
        object surfaceObject,
        Guid surfaceIid,
        string variantLabel,
        out IMFSample sample,
        out Exception failure)
    {
        try
        {
            sample = CreateSampleFromDxgiSurfaceBufferObjectCore(surfaceObject, surfaceIid);
            failure = null!;
            return true;
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_dxgi_surface_buffer_variant_failed",
                $"variant={variantLabel}; surface_iid={surfaceIid}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            sample = null!;
            failure = ex;
            return false;
        }
    }

    private static IMFSample CreateSampleFromDxgiSurfaceBufferCore(IntPtr surfacePtr, Guid surfaceIid, string variantLabel)
    {
        IMFMediaBuffer? surfaceBuffer = null;
        IMFSample? surfaceSample = null;
        var stage = "create_surface_buffer";
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateDXGISurfaceBuffer(
                ref surfaceIid,
                surfacePtr,
                0,
                false,
                out surfaceBuffer));
            stage = "create_sample";
            Marshal.ThrowExceptionForHR(MFCreateSample(out surfaceSample));
            stage = "add_buffer";
            Marshal.ThrowExceptionForHR(surfaceSample.AddBuffer(surfaceBuffer));

            var result = surfaceSample;
            surfaceSample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InputSampleCreationException(stage, ex);
        }
        finally
        {
            ReleaseComObject(surfaceSample);
            ReleaseComObject(surfaceBuffer);
        }
    }

    private static IMFSample CreateSampleFromDxgiSurfaceBufferObjectCore(object surfaceObject, Guid surfaceIid)
    {
        IMFMediaBuffer? surfaceBuffer = null;
        IMFSample? surfaceSample = null;
        var stage = "create_surface_buffer";
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateDXGISurfaceBufferObject(
                ref surfaceIid,
                surfaceObject,
                0,
                false,
                out surfaceBuffer));
            stage = "create_sample";
            Marshal.ThrowExceptionForHR(MFCreateSample(out surfaceSample));
            stage = "add_buffer";
            Marshal.ThrowExceptionForHR(surfaceSample.AddBuffer(surfaceBuffer));

            var result = surfaceSample;
            surfaceSample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InputSampleCreationException(stage, ex);
        }
        finally
        {
            ReleaseComObject(surfaceSample);
            ReleaseComObject(surfaceBuffer);
        }
    }

    private static IMFSample CloneBuffersIntoStandardSample(IMFSample sourceSample)
    {
        IMFSample? clonedSample = null;
        IMFMediaBuffer? buffer = null;
        var stage = "create_sample";
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateSample(out clonedSample));
            stage = "get_buffer_count";
            Marshal.ThrowExceptionForHR(sourceSample.GetBufferCount(out var bufferCount));
            if (bufferCount == 0)
            {
                throw new InvalidOperationException("Video surface sample exposed no buffers to clone.");
            }

            for (uint index = 0; index < bufferCount; index++)
            {
                stage = $"get_buffer_{index}";
                Marshal.ThrowExceptionForHR(sourceSample.GetBufferByIndex(index, out buffer));
                try
                {
                    stage = $"add_buffer_{index}";
                    Marshal.ThrowExceptionForHR(clonedSample.AddBuffer(buffer));
                }
                finally
                {
                    ReleaseComObject(buffer);
                    buffer = null;
                }
            }

            var result = clonedSample;
            clonedSample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InputSampleCreationException(stage, ex);
        }
        finally
        {
            ReleaseComObject(buffer);
            ReleaseComObject(clonedSample);
        }
    }

    private static IMFSample CreateStandardSampleFromContiguousBuffer(IMFSample sourceSample, int expectedLength)
    {
        IMFSample? standardSample = null;
        IMFMediaBuffer? contiguousBuffer = null;
        IMFMediaBuffer? copiedBuffer = null;
        var stage = "create_sample";
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateSample(out standardSample));
            stage = "convert_to_contiguous_buffer";
            Marshal.ThrowExceptionForHR(sourceSample.ConvertToContiguousBuffer(out contiguousBuffer));
            LogLifecycle(
                "screenshare_h264_contiguous_buffer_state",
                $"expected_length={expectedLength}; {DescribeMediaBuffer(contiguousBuffer, expectedLength, true)}");
            stage = "read_contiguous_buffer";
            var bytes = ReadBufferBytes(contiguousBuffer);
            stage = "create_memory_buffer";
            Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(bytes.Length, out copiedBuffer));
            stage = "write_memory_buffer";
            WriteBufferBytes(copiedBuffer, bytes);
            stage = "set_current_length";
            Marshal.ThrowExceptionForHR(copiedBuffer.SetCurrentLength(bytes.Length));
            stage = "add_buffer";
            Marshal.ThrowExceptionForHR(standardSample.AddBuffer(copiedBuffer));
            var result = standardSample;
            standardSample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InputSampleCreationException(stage, ex);
        }
        finally
        {
            ReleaseComObject(copiedBuffer);
            ReleaseComObject(contiguousBuffer);
            ReleaseComObject(standardSample);
        }
    }


    private static TDelegate GetVtableDelegate<TDelegate>(IntPtr comInterfacePtr, int slot) where TDelegate : Delegate
    {
        if (comInterfacePtr == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(comInterfacePtr));
        }

        var vtablePtr = Marshal.ReadIntPtr(comInterfacePtr);
        var methodPtr = Marshal.ReadIntPtr(vtablePtr, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }

    private static IntPtr QueryInterfacePtr(IntPtr interfacePtr, Guid iid)
    {
        var hr = Marshal.QueryInterface(interfacePtr, ref iid, out var queriedPtr);
        Marshal.ThrowExceptionForHR(hr);
        return queriedPtr;
    }

    private static void AccumulatePixel(byte[] bgraBytes, int rowOffset, int x, ref int avgR, ref int avgG, ref int avgB)
    {
        var pixelOffset = rowOffset + (x * 4);
        avgB += bgraBytes[pixelOffset];
        avgG += bgraBytes[pixelOffset + 1];
        avgR += bgraBytes[pixelOffset + 2];
    }

    private static unsafe void AccumulatePixel(byte* row, int x, ref int avgR, ref int avgG, ref int avgB)
    {
        var pixel = row + (x * 4);
        avgB += pixel[0];
        avgG += pixel[1];
        avgR += pixel[2];
    }

    private static byte ClampToByte(int value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }

    private const int MftMessageCommandFlush = 0;
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int MftMessageNotifyBeginStreaming = 0x10000000;
    private const int MftMessageNotifyStartOfStream = 0x10000003;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreate2DMediaBuffer(
        uint width,
        uint height,
        uint fourCC,
        [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenLinear,
        out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAlignedMemoryBuffer(int maxLength, uint alignment, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaBufferFromMediaType(
        IMFMediaType mediaType,
        long duration,
        uint minLength,
        uint minAlignment,
        out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager ppDeviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGISurfaceBuffer(
        ref Guid riid,
        IntPtr punkSurface,
        uint uSubresourceIndex,
        [MarshalAs(UnmanagedType.Bool)] bool fBottomUpWhenLinear,
        out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true, EntryPoint = "MFCreateDXGISurfaceBuffer")]
    private static extern int MFCreateDXGISurfaceBufferObject(
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] object punkSurface,
        uint uSubresourceIndex,
        [MarshalAs(UnmanagedType.Bool)] bool fBottomUpWhenLinear,
        out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
        [MarshalAs(UnmanagedType.Interface)] object? pByteStream,
        IMFAttributes? pAttributes,
        out IMFSinkWriter sinkWriter);

    [DllImport("evr.dll", ExactSpelling = true)]
    private static extern int MFCreateVideoSampleFromSurface(
        [MarshalAs(UnmanagedType.IUnknown)] object surface,
        out IMFSample sample);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        ref Guid guidCategory,
        uint flags,
        IntPtr inputType,
        IntPtr outputType,
        out IntPtr activates,
        out int activateCount);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr punkOuter,
        uint clsContext,
        ref Guid iid,
        out IntPtr instance);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out D3DFeatureLevel featureLevel,
        out IntPtr immediateContext);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DrawingSize(int Width, int Height);

    private readonly record struct EncodeWorkItem(
        WindowsRawCaptureFrame Frame,
        WindowsH264EncodeOptions Options,
        TaskCompletionSource<WindowsH264EncodedFrame?> Completion,
        CancellationToken CancellationToken);

    private enum AccessUnitPictureKind
    {
        None = 0,
        Idr = 1,
        P = 2,
        I = 3,
        B = 4,
        Unknown = 5,
        Unsupported = 6,
    }

    private readonly record struct ParsedSliceHeader(
        int FirstMbInSlice,
        AccessUnitPictureKind PictureKind);

    private readonly record struct AccessUnitClassification(
        bool HasDisplayableVcl,
        bool HasIdr,
        bool HasSps,
        bool HasPps,
        bool HasAud,
        bool HasSei,
        int VclNalCount,
        int IdrNalCount,
        int AudNalCount,
        int PrimaryPictureCount,
        bool HasPPicture,
        bool HasBPicture,
        bool HasISlice,
        AccessUnitPictureKind PictureKind,
        string Kind)
    {
        public bool HasSpsOrPps => HasSps || HasPps;
    }

    private readonly record struct LowDelayEncoderConfigurationResult(
        bool LowLatencyModeApplied,
        bool BPictureCountApplied,
        bool GopSizeApplied,
        bool QualityVsSpeedApplied)
    {
        public string State
        {
            get
            {
                var appliedCount =
                    (LowLatencyModeApplied ? 1 : 0) +
                    (BPictureCountApplied ? 1 : 0) +
                    (GopSizeApplied ? 1 : 0) +
                    (QualityVsSpeedApplied ? 1 : 0);
                return appliedCount switch
                {
                    4 => "full",
                    > 0 => "partial",
                    _ => "none",
                };
            }
        }
    }

    private ref struct H264BitReader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int byteIndex;
        private int bitIndex;

        public H264BitReader(ReadOnlySpan<byte> bytes)
        {
            this.bytes = bytes;
            byteIndex = 0;
            bitIndex = 0;
        }

        public bool TryReadUnsignedExpGolomb(out int value)
        {
            value = 0;
            var leadingZeroBits = 0;
            while (true)
            {
                if (!TryReadBit(out var bit))
                {
                    return false;
                }

                if (bit == 1)
                {
                    break;
                }

                leadingZeroBits++;
                if (leadingZeroBits > 31)
                {
                    return false;
                }
            }

            var suffix = 0;
            for (var i = 0; i < leadingZeroBits; i++)
            {
                if (!TryReadBit(out var bit))
                {
                    return false;
                }

                suffix = (suffix << 1) | bit;
            }

            value = ((1 << leadingZeroBits) - 1) + suffix;
            return true;
        }

        private bool TryReadBit(out int bit)
        {
            bit = 0;
            if (byteIndex >= bytes.Length)
            {
                return false;
            }

            bit = (bytes[byteIndex] >> (7 - bitIndex)) & 0x01;
            bitIndex++;
            if (bitIndex == 8)
            {
                bitIndex = 0;
                byteIndex++;
            }

            return true;
        }
    }

    private readonly record struct EncoderEncodeResult(
        byte[] EncodedBytes,
        byte[] DecoderConfigData,
        bool IsKeyFrame,
        long EncodeDurationMs,
        long TransformEncodeDurationMs);

    private readonly record struct Mp4Sample(
        byte[] Bytes,
        int OffsetInMdatPayload);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11DeviceContextMapDelegate(
        IntPtr @this,
        IntPtr resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out D3D11MappedSubresource mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11DeviceContextUnmapDelegate(
        IntPtr @this,
        IntPtr resource,
        uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11DeviceContextCopyResourceDelegate(
        IntPtr @this,
        IntPtr destinationResource,
        IntPtr sourceResource);

    private sealed class ComReleaser<T> : IDisposable
    {
        private IntPtr interfacePtr;

        public ComReleaser(T value, IntPtr interfacePtr)
        {
            Value = value;
            this.interfacePtr = interfacePtr;
        }

        public T Value { get; }

        public void Dispose()
        {
            if (interfacePtr != IntPtr.Zero)
            {
                Marshal.Release(interfacePtr);
                interfacePtr = IntPtr.Zero;
            }
        }
    }

    private sealed class SinkWriterContext : IDisposable
    {
        public SinkWriterContext(IMFSinkWriter writer, IMFDXGIDeviceManager? deviceManager, IntPtr d3dDevice, IntPtr d3dContext)
        {
            Writer = writer;
            DeviceManager = deviceManager;
            D3DDevice = d3dDevice;
            D3DContext = d3dContext;
        }

        public IMFSinkWriter Writer { get; }
        public IMFDXGIDeviceManager? DeviceManager { get; }
        public IntPtr D3DDevice { get; }
        public IntPtr D3DContext { get; }

        public void Dispose()
        {
            ReleaseComObject(Writer);
            ReleaseComObject(DeviceManager);
            if (D3DContext != IntPtr.Zero)
            {
                Marshal.Release(D3DContext);
            }

            if (D3DDevice != IntPtr.Zero)
            {
                Marshal.Release(D3DDevice);
            }
        }
    }

    internal enum RawInputBufferStrategy
    {
        CpuMemoryBufferNv12,
        Cpu2DVideoBuffer,
        DxgiSurfaceNv12FreshTexture,
        DxgiSurfaceNv12ReusableTextureUpdate,
    }

    private enum DxgiSubmissionStrategy
    {
        FreshTexturePerFrame,
        ReusableTextureUpdate,
    }

    private sealed class EncoderConfiguration
    {
        public EncoderConfiguration(
            int width,
            int height,
            int targetFramesPerSecond,
            uint targetBitrate,
            string codecProfile,
            string profileName,
            bool transportIpOnlyMode,
            byte[] decoderConfigData,
            int nalLengthSize,
            bool pendingFirstFrame)
        {
            Width = width;
            Height = height;
            TargetFramesPerSecond = targetFramesPerSecond;
            TargetBitrate = targetBitrate;
            CodecProfile = codecProfile;
            ProfileName = profileName;
            TransportIpOnlyMode = transportIpOnlyMode;
            DecoderConfigData = decoderConfigData;
            NalLengthSize = nalLengthSize;
            PendingFirstFrame = pendingFirstFrame;
        }

        public int Width { get; }
        public int Height { get; }
        public int TargetFramesPerSecond { get; set; }
        public uint TargetBitrate { get; set; }
        public string CodecProfile { get; }
        public string ProfileName { get; set; }
        public bool TransportIpOnlyMode { get; }
        public byte[] DecoderConfigData { get; set; }
        public int NalLengthSize { get; set; }
        public bool PendingFirstFrame { get; set; }
    }

    private sealed class PersistentTransformSession : IDisposable
    {
        public PersistentTransformSession(
            IMFTransform encoderTransform,
            IMFDXGIDeviceManager? deviceManager,
            IntPtr d3dDevice,
            IntPtr d3dContext,
            EncoderConfiguration configuration,
            RawInputBufferStrategy inputStrategy)
        {
            EncoderTransform = encoderTransform;
            DeviceManager = deviceManager;
            D3DDevice = d3dDevice;
            D3DContext = d3dContext;
            Configuration = configuration;
            InputStrategy = inputStrategy;
        }

        public IMFTransform EncoderTransform { get; }
        public IMFDXGIDeviceManager? DeviceManager { get; }
        public IntPtr D3DDevice { get; }
        public IntPtr D3DContext { get; }
        public EncoderConfiguration Configuration { get; }
        public RawInputBufferStrategy InputStrategy { get; }

        public void Dispose()
        {
            ReleaseComObject(EncoderTransform);
            ReleaseComObject(DeviceManager);
            if (D3DContext != IntPtr.Zero)
            {
                Marshal.Release(D3DContext);
            }

            if (D3DDevice != IntPtr.Zero)
            {
                Marshal.Release(D3DDevice);
            }
        }
    }

    private sealed class ReusablePreprocessState : IDisposable
    {
        private readonly Bitmap bgraBitmap;
        private readonly Graphics graphics;
        private readonly byte[] nv12Bytes;
        private unsafe SwsContext* swsContext;
        private bool ffmpegUnavailableLogged;

        public ReusablePreprocessState(int width, int height)
        {
            Width = width;
            Height = height;
            bgraBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            graphics = Graphics.FromImage(bgraBitmap);
            nv12Bytes = new byte[width * height * 3 / 2];
        }

        public int Width { get; }

        public int Height { get; }

        public PreprocessResult PrepareNv12(Bitmap source, ScreenShareTransportTuningLevel tuningLevel)
        {
            if (IsUnsafeDirectNv12PreprocessEnabled() &&
                CanDirectConvertSource(source) &&
                source.Width == Width &&
                source.Height == Height)
            {
                return PrepareNv12Direct(source);
            }

            if (IsUnsafeFfmpegSwscalePreprocessEnabled() &&
                CanDirectConvertSource(source) &&
                WindowsFfmpegRuntime.TryInitialize())
            {
                try
                {
                    return PrepareNv12WithFfmpegScale(source, tuningLevel);
                }
                catch (Exception ex)
                {
                    if (!ffmpegUnavailableLogged)
                    {
                        ffmpegUnavailableLogged = true;
                        LogLifecycle(
                            "screenshare_h264_preprocess_swscale_failed",
                            $"reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                    }
                }
            }

            graphics.InterpolationMode = tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
                ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
                ? System.Drawing.Drawing2D.PixelOffsetMode.HighQuality
                : System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
                ? System.Drawing.Drawing2D.CompositingQuality.HighQuality
                : System.Drawing.Drawing2D.CompositingQuality.AssumeLinear;
            var resizeStartedAt = Stopwatch.GetTimestamp();
            graphics.DrawImage(source, 0, 0, Width, Height);
            var resizeDurationMs = (long)Stopwatch.GetElapsedTime(resizeStartedAt).TotalMilliseconds;

            var rect = new Rectangle(0, 0, Width, Height);
            var data = bgraBitmap.LockBits(rect, ImageLockMode.ReadOnly, bgraBitmap.PixelFormat);
            try
            {
                unsafe
                {
                    var convertStartedAt = Stopwatch.GetTimestamp();
                    FillNv12FromBgraPointer((byte*)data.Scan0, data.Stride, nv12Bytes, Width, Height);
                    return new PreprocessResult(
                        nv12Bytes,
                        resizeDurationMs,
                        (long)Stopwatch.GetElapsedTime(convertStartedAt).TotalMilliseconds,
                        tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
                            ? "gdi_high_quality_bilinear"
                            : "gdi_high_quality_bicubic",
                        DirectNv12: false);
                }
            }
            finally
            {
                bgraBitmap.UnlockBits(data);
            }
        }

        public void Dispose()
        {
            unsafe
            {
                if (swsContext is not null)
                {
                    ffmpeg.sws_freeContext(swsContext);
                    swsContext = null;
                }
            }

            graphics.Dispose();
            bgraBitmap.Dispose();
        }

        private static bool CanDirectConvertSource(Bitmap source)
        {
            return IsSupportedDirectBgraPixelFormat(source.PixelFormat);
        }

        private PreprocessResult PrepareNv12Direct(Bitmap source)
        {
            var rect = new Rectangle(0, 0, Width, Height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                unsafe
                {
                    var convertStartedAt = Stopwatch.GetTimestamp();
                    FillNv12FromBgraPointer((byte*)data.Scan0, data.Stride, nv12Bytes, Width, Height);
                    return new PreprocessResult(
                        nv12Bytes,
                        ResizeDurationMs: 0,
                        ColorConvertDurationMs: (long)Stopwatch.GetElapsedTime(convertStartedAt).TotalMilliseconds,
                        ResizePath: "direct_same_size",
                        DirectNv12: true);
                }
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private unsafe PreprocessResult PrepareNv12WithFfmpegScale(Bitmap source, ScreenShareTransportTuningLevel tuningLevel)
        {
            var rect = new Rectangle(0, 0, source.Width, source.Height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                var flags = tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
                    ? ffmpeg.SWS_BILINEAR
                    : ffmpeg.SWS_BICUBIC;
                var resizeStartedAt = Stopwatch.GetTimestamp();
                swsContext = ffmpeg.sws_getCachedContext(
                    swsContext,
                    source.Width,
                    source.Height,
                    AVPixelFormat.AV_PIX_FMT_BGRA,
                    Width,
                    Height,
                    AVPixelFormat.AV_PIX_FMT_NV12,
                    flags,
                    null,
                    null,
                    null);
                if (swsContext is null)
                {
                    throw new InvalidOperationException("FFmpeg swscale context could not be created.");
                }

                fixed (byte* nv12 = nv12Bytes)
                {
                    var srcData = new byte_ptrArray4();
                    var srcLinesize = new int_array4();
                    srcData[0] = (byte*)data.Scan0;
                    srcLinesize[0] = data.Stride;

                    var dstData = new byte_ptrArray4();
                    var dstLinesize = new int_array4();
                    dstData[0] = nv12;
                    dstData[1] = nv12 + (Width * Height);
                    dstLinesize[0] = Width;
                    dstLinesize[1] = Width;

                    var scaled = ffmpeg.sws_scale(swsContext, srcData, srcLinesize, 0, source.Height, dstData, dstLinesize);
                    if (scaled != Height)
                    {
                        throw new InvalidOperationException($"FFmpeg swscale converted {scaled} rows, expected {Height}.");
                    }
                }

                return new PreprocessResult(
                    nv12Bytes,
                    ResizeDurationMs: (long)Stopwatch.GetElapsedTime(resizeStartedAt).TotalMilliseconds,
                    ColorConvertDurationMs: 0,
                    ResizePath: tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
                        ? "swscale_bilinear_nv12"
                        : "swscale_bicubic_nv12",
                    DirectNv12: false);
            }
            finally
            {
                source.UnlockBits(data);
            }
        }
    }

    private readonly record struct PreprocessResult(
        byte[] Nv12Bytes,
        long ResizeDurationMs,
        long ColorConvertDurationMs,
        string ResizePath,
        bool DirectNv12);

    internal sealed class RawInputBufferStrategyUnavailableException : InvalidOperationException
    {
        public RawInputBufferStrategyUnavailableException(string summary)
            : base($"No supported Media Foundation raw-video input-buffer strategy is available on this system. {summary}")
        {
        }

        public RawInputBufferStrategyUnavailableException(string summary, Exception innerException)
            : base($"No supported Media Foundation raw-video input-buffer strategy is available on this system. {summary}", innerException)
        {
        }
    }

    private sealed class InputSampleCreationException : InvalidOperationException
    {
        public InputSampleCreationException(string stage, Exception innerException)
            : base($"Input sample creation failed at stage '{stage}'.", innerException)
        {
            Stage = stage;
            HResult = innerException.HResult;
        }

        public string Stage { get; }
    }

    private sealed class TransformConfigurationException : InvalidOperationException
    {
        public TransformConfigurationException(string stage, Exception innerException)
            : base($"Transform configuration failed at stage '{stage}'.", innerException)
        {
            Stage = stage;
            HResult = innerException.HResult;
        }

        public string Stage { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid GuidMajorType;
        public Guid GuidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint DwFlags;
        public uint CbSize;
        public uint CbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftInputStreamInfo
    {
        public long HnsMaxLatency;
        public uint DwFlags;
        public uint CbSize;
        public uint CbMaxLookahead;
        public uint CbAlignment;
    }

    private enum D3DDriverType : uint
    {
        Hardware = 1,
        Warp = 5,
    }

    private enum D3DFeatureLevel : uint
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGISampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGISampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11SubresourceData
    {
        public IntPtr SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr PData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint DwStreamId;
        [MarshalAs(UnmanagedType.Interface)]
        public IMFSample PSample;
        public uint DwStatus;
        public IntPtr PEvents;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CodecApiVariant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(2)]
        public ushort Reserved1;

        [FieldOffset(4)]
        public ushort Reserved2;

        [FieldOffset(6)]
        public ushort Reserved3;

        [FieldOffset(8)]
        public uint UlVal;

        public static CodecApiVariant FromUInt32(uint value)
            => new()
            {
                Vt = VariantTypeUi4,
                UlVal = value,
            };
    }

    [ComImport]
    [Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
        int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);
        int BeginWriting();
        int WriteSample(uint dwStreamIndex, IMFSample pSample);
        int SendStreamTick(uint dwStreamIndex, long llTimestamp);
        int PlaceMarker(uint dwStreamIndex, IntPtr pvContext);
        int NotifyEndOfSegment(uint dwStreamIndex);
        int Flush(uint dwStreamIndex);
        int Finalize_();
    }

    [ComImport]
    [Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFDXGIDeviceManager
    {
        int CloseDeviceHandle(IntPtr hDevice);
        int GetVideoService(IntPtr hDevice, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppService);
        int LockDevice(IntPtr hDevice, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppUnkDevice, [MarshalAs(UnmanagedType.Bool)] bool fBlock);
        int OpenDeviceHandle(out IntPtr phDevice);
        int ResetDevice(IntPtr pUnkDevice, uint resetToken);
        int TestDevice(IntPtr hDevice);
        int UnlockDevice(IntPtr hDevice, [MarshalAs(UnmanagedType.Bool)] bool fSaveState);
    }

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        int GetItem(ref Guid guidKey, IntPtr pValue);
        int GetItemType(ref Guid guidKey, out uint pType);
        int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        int Compare(IMFAttributes theirs, uint matchType, out int result);
        int GetUINT32(ref Guid guidKey, out uint punValue);
        int GetUINT64(ref Guid guidKey, out ulong punValue);
        int GetDouble(ref Guid guidKey, out double pfValue);
        int GetGUID(ref Guid guidKey, out Guid pguidValue);
        int GetStringLength(ref Guid guidKey, out uint pcchLength);
        int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        int SetItem(ref Guid guidKey, IntPtr value);
        int DeleteItem(ref Guid guidKey);
        int DeleteAllItems();
        int SetUINT32(ref Guid guidKey, uint unValue);
        int SetUINT64(ref Guid guidKey, ulong unValue);
        int SetDouble(ref Guid guidKey, double fValue);
        int SetGUID(ref Guid guidKey, ref Guid guidValue);
        int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out uint pcItems);
        int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport]
    [Guid("7f00f10c-daed-41af-ab26-5fdfffb9c011")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out uint pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        new int Compare(IMFAttributes theirs, uint matchType, out int result);
        new int GetUINT32(ref Guid guidKey, out uint punValue);
        new int GetUINT64(ref Guid guidKey, out ulong punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new int SetItem(ref Guid guidKey, IntPtr value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, uint unValue);
        new int SetUINT64(ref Guid guidKey, ulong unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out uint pcItems);
        new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);

        int ActivateObject(ref Guid riid, out IntPtr ppv);
        int ShutdownObject();
        int DetachObject();
    }

    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out uint pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        new int Compare(IMFAttributes theirs, uint matchType, out int result);
        new int GetUINT32(ref Guid guidKey, out uint punValue);
        new int GetUINT64(ref Guid guidKey, out ulong punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new int SetItem(ref Guid guidKey, IntPtr value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, uint unValue);
        new int SetUINT64(ref Guid guidKey, ulong unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out uint pcItems);
        new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);

        int GetMajorType(out Guid pguidMajorType);
        int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
        int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        int Unlock();
        int GetCurrentLength(out int pcbCurrentLength);
        int SetCurrentLength(int cbCurrentLength);
        int GetMaxLength(out int pcbMaxLength);
    }

    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        int CreateBuffer();
        int CreateTexture1D();
        int CreateTexture2D(ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture2D);
    }

    [ComImport]
    [Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext
    {
        int GetDevice(out IntPtr device);
        int GetPrivateData();
        int SetPrivateData();
        int SetPrivateDataInterface();
        int VSSetConstantBuffers();
        int PSSetShaderResources();
        int PSSetShader();
        int PSSetSamplers();
        int VSSetShader();
        int DrawIndexed();
        int Draw();
        int Map(IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11MappedSubresource mappedResource);
        void Unmap(IntPtr resource, uint subresource);
        void PSSetConstantBuffers();
        void IASetInputLayout();
        void IASetVertexBuffers();
        void IASetIndexBuffer();
        void DrawIndexedInstanced();
        void DrawInstanced();
        void GSSetConstantBuffers();
        void GSSetShader();
        void IASetPrimitiveTopology();
        void VSSetShaderResources();
        void VSSetSamplers();
        void Begin();
        void End();
        void GetData();
        void SetPredication();
        void GSSetShaderResources();
        void GSSetSamplers();
        void OMSetRenderTargets();
        void OMSetRenderTargetsAndUnorderedAccessViews();
        void OMSetBlendState();
        void OMSetDepthStencilState();
        void SOSetTargets();
        void DrawAuto();
        void DrawIndexedInstancedIndirect();
        void DrawInstancedIndirect();
        void Dispatch();
        void DispatchIndirect();
        void RSSetState();
        void RSSetViewports();
        void RSSetScissorRects();
        void CopySubresourceRegion();
        void CopyResource(IntPtr destinationResource, IntPtr sourceResource);
    }

    [ComImport]
    [Guid("1841e5c6-16b0-489b-bcc8-44cfb0d5deae")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceChild
    {
        void GetDevice(out IntPtr device);
        int GetPrivateData();
        int SetPrivateData();
        int SetPrivateDataInterface();
    }

    [ComImport]
    [Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Resource : ID3D11DeviceChild
    {
        void GetType(out D3D11ResourceDimension resourceDimension);
        void SetEvictionPriority(uint evictionPriority);
        uint GetEvictionPriority();
    }

    [ComImport]
    [Guid("1841e5c8-16b0-489b-bcc8-44cfb0d5deae")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Texture2D : ID3D11Resource
    {
        void GetDesc(out D3D11Texture2DDesc desc);
    }

    private enum D3D11ResourceDimension : uint
    {
        Unknown = 0,
        Buffer = 1,
        Texture1D = 2,
        Texture2D = 3,
        Texture3D = 4,
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out uint pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        new int Compare(IMFAttributes theirs, uint matchType, out int result);
        new int GetUINT32(ref Guid guidKey, out uint punValue);
        new int GetUINT64(ref Guid guidKey, out ulong punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new int SetItem(ref Guid guidKey, IntPtr value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, uint unValue);
        new int SetUINT64(ref Guid guidKey, ulong unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out uint pcItems);
        new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);

        int GetSampleFlags(out uint pdwSampleFlags);
        int SetSampleFlags(uint dwSampleFlags);
        int GetSampleTime(out long phnsSampleTime);
        int SetSampleTime(long hnsSampleTime);
        int GetSampleDuration(out long phnsSampleDuration);
        int SetSampleDuration(long hnsSampleDuration);
        int GetBufferCount(out uint pdwBufferCount);
        int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        int AddBuffer(IMFMediaBuffer pBuffer);
        int RemoveBufferByIndex(uint dwIndex);
        int RemoveAllBuffers();
        int GetTotalLength(out uint pcbTotalLength);
        int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport]
    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        int GetStreamLimits(out uint inputMinimum, out uint inputMaximum, out uint outputMinimum, out uint outputMaximum);
        int GetStreamCount(out uint inputStreams, out uint outputStreams);
        int GetStreamIDs(uint inputIdArraySize, IntPtr inputIds, uint outputIdArraySize, IntPtr outputIds);
        int GetInputStreamInfo(uint inputStreamId, out MftInputStreamInfo streamInfo);
        int GetOutputStreamInfo(uint outputStreamId, out MftOutputStreamInfo streamInfo);
        int GetAttributes(out IMFAttributes attributes);
        int GetInputStreamAttributes(uint inputStreamId, out IMFAttributes attributes);
        int GetOutputStreamAttributes(uint outputStreamId, out IMFAttributes attributes);
        int DeleteInputStream(uint streamId);
        int AddInputStreams(uint streams, IntPtr streamIds);
        int GetInputAvailableType(uint inputStreamId, uint typeIndex, out IMFMediaType type);
        int GetOutputAvailableType(uint outputStreamId, uint typeIndex, out IMFMediaType type);
        int SetInputType(uint inputStreamId, IMFMediaType type, uint flags);
        int SetOutputType(uint outputStreamId, IMFMediaType type, uint flags);
        int GetInputCurrentType(uint inputStreamId, out IMFMediaType type);
        int GetOutputCurrentType(uint outputStreamId, out IMFMediaType type);
        int GetInputStatus(uint inputStreamId, out uint flags);
        int GetOutputStatus(out uint flags);
        int SetOutputBounds(long lowerBound, long upperBound);
        int ProcessEvent(uint inputStreamId, IntPtr mediaEvent);
        int ProcessMessage(int message, IntPtr param);
        int ProcessInput(uint inputStreamId, IMFSample sample, uint flags);
        int ProcessOutput(
            uint flags,
            uint outputBufferCount,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] MftOutputDataBuffer[] outputSamples,
            out uint status);
    }

    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        int IsSupported(ref Guid api);
        int IsModifiable(ref Guid api);
        int GetParameterRange(ref Guid api, IntPtr values, IntPtr modifiableValues);
        int GetParameterValues(ref Guid api, IntPtr values);
        int GetDefaultValue(ref Guid api, IntPtr value);
        int GetValue(ref Guid api, IntPtr value);
        int SetValue(ref Guid api, IntPtr value);
        int RegisterForEvent(ref Guid api, int userData);
        int UnregisterForEvent(ref Guid api);
        int SetAllDefaults();
        int SetValueWithNotify(ref Guid api, IntPtr value, IntPtr callback, int userData);
        int SetAllDefaultsWithNotify(IntPtr callback, int userData);
    }
}
