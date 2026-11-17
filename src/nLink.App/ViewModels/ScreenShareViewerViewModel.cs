using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.App.Services.ScreenCapture;
using NLink.Infra.Nkn;
using System.Diagnostics;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.ViewModels;

internal sealed class ScreenShareViewerFrameAppliedEventArgs : EventArgs
{
    public ScreenShareViewerFrameAppliedEventArgs(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
    {
        AgeMs = ageMs;
        StreamEpoch = streamEpoch;
        FrameId = frameId;
        VisibleHeadFrameId = visibleHeadFrameId;
        StableVisibleHeadFrameId = stableVisibleHeadFrameId;
        FramesAppliedSinceLastGap = Math.Max(0, framesAppliedSinceLastGap);
    }

    public long AgeMs { get; }

    public long StreamEpoch { get; }

    public long FrameId { get; }

    public long VisibleHeadFrameId { get; }

    public long StableVisibleHeadFrameId { get; }

    public long FramesAppliedSinceLastGap { get; }
}

internal sealed class ScreenShareViewerStaleFrameDroppedEventArgs : EventArgs
{
    public ScreenShareViewerStaleFrameDroppedEventArgs(
        long renderedAgeMs,
        long streamEpoch,
        bool referenceContinuityPreserved = false)
    {
        RenderedAgeMs = renderedAgeMs;
        StreamEpoch = streamEpoch;
        ReferenceContinuityPreserved = referenceContinuityPreserved;
    }

    public long RenderedAgeMs { get; }

    public long StreamEpoch { get; }

    public bool ReferenceContinuityPreserved { get; }
}

internal sealed class ScreenShareViewerDecodeNeedsMoreInputEventArgs : EventArgs
{
    public ScreenShareViewerDecodeNeedsMoreInputEventArgs(long streamEpoch)
    {
        StreamEpoch = streamEpoch;
    }

    public long StreamEpoch { get; }
}

internal sealed class ScreenShareViewerContinuityLostEventArgs : EventArgs
{
    public ScreenShareViewerContinuityLostEventArgs(
        string reason,
        long streamEpoch,
        long currentEpochNeedMoreInputCount,
        bool shouldRequestRecoveryKeyframe,
        long expectedNextFrameId = -1,
        long receivedFrameId = -1,
        long lastCleanFrameId = -1)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "continuity_loss" : reason.Trim();
        StreamEpoch = streamEpoch;
        CurrentEpochNeedMoreInputCount = currentEpochNeedMoreInputCount;
        ShouldRequestRecoveryKeyframe = shouldRequestRecoveryKeyframe;
        ExpectedNextFrameId = expectedNextFrameId;
        ReceivedFrameId = receivedFrameId;
        LastCleanFrameId = lastCleanFrameId;
    }

    public string Reason { get; }

    public long StreamEpoch { get; }

    public long CurrentEpochNeedMoreInputCount { get; }

    public bool ShouldRequestRecoveryKeyframe { get; }

    public long ExpectedNextFrameId { get; }

    public long ReceivedFrameId { get; }

    public long LastCleanFrameId { get; }
}

internal sealed class ScreenShareViewerRecoveryKeyframeAppliedEventArgs : EventArgs
{
    public ScreenShareViewerRecoveryKeyframeAppliedEventArgs(long ageMs, long streamEpoch)
    {
        AgeMs = ageMs;
        StreamEpoch = streamEpoch;
    }

    public long AgeMs { get; }

    public long StreamEpoch { get; }
}

internal sealed class ScreenShareViewerRecoveryWindowStateChangedEventArgs : EventArgs
{
    public ScreenShareViewerRecoveryWindowStateChangedEventArgs(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason = null)
    {
        StreamEpoch = streamEpoch;
        RecoveryFrameId = recoveryFrameId;
        LastContiguousFrameId = lastContiguousFrameId;
        ContiguousFollowerApplyCount = Math.Max(0, contiguousFollowerApplyCount);
        Status = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
        AbortReason = string.IsNullOrWhiteSpace(abortReason) ? string.Empty : abortReason.Trim();
    }

    public long StreamEpoch { get; }

    public long RecoveryFrameId { get; }

    public long LastContiguousFrameId { get; }

    public int ContiguousFollowerApplyCount { get; }

    public string Status { get; }

    public string AbortReason { get; }
}

internal readonly record struct ScreenShareViewerFrameGapObservation(
    long ExpectedNextFrameId,
    long ReceivedFrameId,
    long LastCleanFrameId);

internal enum ScreenShareViewerContinuityHandlingKind
{
    None = 0,
    HardRecovery,
    SoftStaleCleanup,
}

internal readonly record struct ScreenShareViewerContinuityHandlingResult(
    ScreenShareViewerContinuityHandlingKind Kind,
    string Reason)
{
    public static ScreenShareViewerContinuityHandlingResult None { get; } =
        new(ScreenShareViewerContinuityHandlingKind.None, string.Empty);
}

// Owns remote-view rendering only. Local preview uses HelpeeScreenShareCoordinator;
// both consume the shared LatestEncodedFrameDecodeWorker.
public sealed partial class ScreenShareViewerViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RenderStatsLogInterval = TimeSpan.FromSeconds(2);
    private const long StaleFrameThresholdMs = 750;
    private const long StaleFrameDropThresholdMs = 1500;
    private const int HelperRemoteMaxPendingEncodedFrames = 4;
    private const long HelperRemoteMaxPendingEncodedFrameAgeMs = 300;
    private const int HelperRemoteNeedMoreInputBurstThreshold = 2;
    private const int HelperRemotePostRecoveryFollowerWindowSize = 2;
    private const int HelperRemotePostRecoveryReservedApplyCount = 2;
    private const long HelperRemoteH264ReferenceQuarantineQuietWindowMs = 300;
    private const long HelperRemotePostQuarantineSettleWindowMs = 150;
    private const long HelperRemotePostQuarantineSettleMaxHoldMs = 350;
    private const long CursorOverlayStaleTimeoutMs = 750;
    private static readonly TimeSpan CursorOverlayStaleCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HelperRemoteStartupCorridorStallTimeout = TimeSpan.FromMilliseconds(400);
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly EncodedFrameBitmapDecoder encodedFrameDecoder;
    private readonly H264DecodeStreamState h264StreamState;
    private readonly Func<Action, Task> postFrameToUiAsync;
    private readonly Func<Action, Task> postStatusToUiAsync;
    private readonly LatestEncodedFrameDecodeWorker decodeWorker;
    private readonly HelperRemoteScreenShareSessionController helperRemoteSessionController;
    private readonly string logRole;

    private Bitmap? currentFrame;
    private bool isActive;
    private string statusText = string.Empty;
    private bool cursorOverlayVisible;
    private double cursorOverlayNx;
    private double cursorOverlayNy;
    private string cursorDeliveryMode = "captured_video";
    private string cursorOverlayLastStatus = "not_started";
    private long lastRenderedFrameAgeMs = -1;
    private int generation;
    private string lastHelperRemoteEpochDetailLogSignature = string.Empty;
    private long framesReceived;
    private long framesApplied;
    private long decodeErrors;
    private long needMoreInputCount;
    private long completedWithoutPictureCount;
    private long continuityLossCount;
    private long recoveryKeyframesRequested;
    private long framesDroppedWaitingForRecoveryKeyframe;
    private long frameGapContinuityLossCount;
    private long framesDroppedForFrameGap;
    private long framesCoalesced;
    private long chunksDroppedOlderFrame;
    private long assembliesExpired;
    private long lastRenderStatsLogTick;
    private long lastRenderedUtcMs;
    private long staleFrameRenders;
    private long lastDecodedUtcMs;
    private long decodeIntervalsObserved;
    private long totalDecodeIntervalMs;
    private long renderIntervalsObserved;
    private long totalRenderIntervalMs;
    private long captureToRenderObserved;
    private long totalCaptureToRenderMs;
    private long helperDecodeCompleteToVisibleApplyObserved;
    private long totalHelperDecodeCompleteToVisibleApplyMs;
    private long helperUiPostApplyObserved;
    private long totalHelperUiPostApplyMs;
    private long helperVisibleHeadLagObserved;
    private long totalHelperVisibleHeadLagFrames;
    private long helperStableHeadLagObserved;
    private long totalHelperStableHeadLagFrames;
    private long staleFrameDropVisibleStableCount;
    private long staleFrameDropVisibleStableLastAgeMs = -1;
    private long ordinaryNonKeyAgeBudgetBypassCount;
    private long lastLoggedPreparedEpoch = long.MinValue;
    private long lastLoggedDroppedEpoch = long.MinValue;
    private long lastLoggedDecodeSuccessEpoch = long.MinValue;
    private long lastLoggedDecodeFailureEpoch = long.MinValue;
    private long lastLoggedRecoveryWaitingEpoch = long.MinValue;
    private long lastAppliedStreamEpoch;
    private long lastObservedReceiverDroppedFrameCount;
    private long lastObservedAssembliesExpiredCount;
    private HelperRemoteRecoveryState helperRemoteRecoveryState => helperRemoteSessionController.RecoveryState;
    private HelperRemoteFollowerState helperRemoteFollowerState => helperRemoteSessionController.FollowerState;
    private HelperRemoteVisibleProgressState helperRemoteVisibleProgressState => helperRemoteSessionController.VisibleProgressState;
    private long postRecoveryVisibleGenerationResetCount;
    private long postRecoveryPurgedPreRecoveryFollowerCount;
    private long postRecoveryStaleDropBypassCount;
    private long recoveryFollowerWindowBufferedCount;
    private long recoveryFollowerWindowAppliedCount;
    private long recoveryFollowerWindowTrimmedCount;
    private long recoveryProgressCorridorCount;
    private long recoveryProgressCorridorSuccessCount;
    private long recoveryProgressCorridorAbortCount;
    private long recoveryProgressCorridorAppliedCount;
    private long staleSupersededRecoverySuppressedCount;
    private long softStaleCleanupCount;
    private long preCandidateGapTailEmittedToViewerCount;
    private long recoveryKeyframePendingVisibleApplyCount;
    private long startupCorridorBufferedFollowerCount;
    private long startupCorridorReleaseCount;
    private long startupCorridorAbortCount;
    private string startupCorridorAbortReason = "none";
    private long protectedRecoveryDeliveryCount;
    private long recoveryRunwayContiguousFollowerBufferCount;
    private long recoveryRunwayContiguousFollowerApplyCount;
    private long h264ReferenceTaintEnterCount;
    private long h264ReferenceTaintReleaseCount;
    private long h264ReferenceTaintDroppedNonKeyCount;
    private long h264ReferenceTaintDecoderResetCount;
    private long h264ReferenceTaintStaleVisibleStableEnterCount;
    private long staleNormalNonKeyVisibleSuppressCount;
    private long decodedStaleVisibleSuppressCount;
    private long postQuarantineSettleSuppressCount;
    private long h264ReferenceQuarantineReleaseBlockedCount;
    private long h264ReferenceQuarantineQuietReleaseCount;
    private long h264ReferenceQuarantinePendingReleaseEpoch;
    private long h264ReferenceQuarantinePendingReleaseFrameId = -1;
    private long h264ReferenceQuarantineReleaseDueUtcMs = -1;
    private long h264ReferenceQuarantineLastLossEpoch;
    private long h264ReferenceQuarantineLastLossUtcMs = -1;
    private long helperRemotePostQuarantineSettleEpoch;
    private long helperRemotePostQuarantineSettleStartedUtcMs = -1;
    private long helperRemotePostQuarantineSettleUntilUtcMs = -1;
    private long helperRemotePostQuarantineSettleLastContiguousFrameId = -1;
    private string h264ReferenceQuarantineLastBlocker = "none";
    private string h264ReferenceQuarantineLastLossReason = "none";
    private readonly long forcedHelperRemoteRecoveryAfterApplies = ReadForcedHelperRemoteRecoveryAfterApplies();
    private bool forcedHelperRemoteRecoveryTriggered;
    private bool disposed;
#if DEBUG
    private readonly DebugLatencyWindow decodeDurationLatency = new();
    private readonly DebugLatencyWindow endToEndLatency = new();
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif
    private Timer? cursorOverlayStaleTimer;
    private long cursorOverlayUpdatesReceivedCount;
    private long cursorOverlayUpdatesAppliedCount;
    private long cursorOverlayStaleCount;
    private long cursorOverlayLastAgeMs = -1;
    private long cursorOverlayFirstUpdateTickMs;
    private long cursorOverlayLastUpdateTickMs;
    private long cursorOverlayLastSeq;

    public ScreenShareViewerViewModel(
        Func<ReadOnlyMemory<byte>, Bitmap>? decodeFrame = null,
        Func<Action, Task>? postToUiAsync = null)
        : this(decodeFrame, postToUiAsync, h264Decoder: null, logRole: "viewer")
    {
    }

    internal ScreenShareViewerViewModel(
        Func<ReadOnlyMemory<byte>, Bitmap>? decodeFrame,
        Func<Action, Task>? postToUiAsync,
        IWindowsH264BitmapDecoder? h264Decoder,
        string? logRole = null)
    {
        this.logRole = string.IsNullOrWhiteSpace(logRole) ? "viewer" : logRole.Trim();
        var resolvedH264Decoder = h264Decoder ?? (OperatingSystem.IsWindows()
            ? WindowsH264BitmapDecoderFactory.TryCreate(this.logRole)
            : null);
        encodedFrameDecoder = new EncodedFrameBitmapDecoder(decodeFrame ?? DecodeFrame, resolvedH264Decoder);
        h264StreamState = new H264DecodeStreamState(encodedFrameDecoder);
        postFrameToUiAsync = postToUiAsync ?? PostFrameApplyToUiAsync;
        postStatusToUiAsync = postToUiAsync ?? PostStatusToUiAsync;
        decodeWorker = new LatestEncodedFrameDecodeWorker(
            decodeFrame: encodedFrameDecoder.Decode,
            onFrameDecodedAsync: OnFrameDecodedAsync,
            onDecodeFailedAsync: OnDecodeFailedAsync,
            shouldStop: () => disposed,
            getGeneration: () => Volatile.Read(ref generation),
            options: CreateDecodeWorkerOptions(this.logRole),
            onFrameEnqueued: OnDecodeWorkerFrameEnqueued,
            onFrameDecodeStarted: OnDecodeWorkerFrameDecodeStarted,
            onFrameDroppedBeforeDecode: OnDecodeWorkerFrameDroppedBeforeDecode,
            onFrameDroppedAfterDecode: OnDecodeWorkerFrameDroppedAfterDecode);
        helperRemoteSessionController = new HelperRemoteScreenShareSessionController(this);
    }

    public IImage? CurrentFrame
    {
        get => currentFrame;
        private set => SetProperty(ref currentFrame, value as Bitmap);
    }

    public bool IsActive
    {
        get => isActive;
        private set => SetProperty(ref isActive, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool CursorOverlayVisible
    {
        get => cursorOverlayVisible;
        private set => SetProperty(ref cursorOverlayVisible, value);
    }

    public double CursorOverlayNx
    {
        get => cursorOverlayNx;
        private set => SetProperty(ref cursorOverlayNx, value);
    }

    public double CursorOverlayNy
    {
        get => cursorOverlayNy;
        private set => SetProperty(ref cursorOverlayNy, value);
    }

    public string CursorDeliveryMode
    {
        get => cursorDeliveryMode;
        private set => SetProperty(ref cursorDeliveryMode, value);
    }

    public long LastRenderedFrameAgeMs
    {
        get => lastRenderedFrameAgeMs;
        private set => SetProperty(ref lastRenderedFrameAgeMs, value);
    }

    internal bool IsIdleForDiagnostics
    {
        get
        {
            return decodeWorker.IsIdle;
        }
    }

    internal string ViewerRoleForDiagnostics => logRole;
    internal event EventHandler<ScreenShareViewerFrameAppliedEventArgs>? FrameApplied;
    internal event EventHandler<ScreenShareViewerStaleFrameDroppedEventArgs>? StaleFrameDropped;
    internal event EventHandler<ScreenShareViewerDecodeNeedsMoreInputEventArgs>? DecodeNeedsMoreInput;
    internal event EventHandler<ScreenShareViewerContinuityLostEventArgs>? ContinuityLost;
    internal event EventHandler<ScreenShareViewerRecoveryKeyframeAppliedEventArgs>? RecoveryKeyframeApplied;
    internal event EventHandler<ScreenShareViewerRecoveryWindowStateChangedEventArgs>? RecoveryWindowStateChanged;

    internal HelperRemoteSessionSnapshot GetHelperRemoteSessionSnapshot()
    {
        return helperRemoteSessionController.BuildSessionSnapshot(GetHelperRemoteFrameLossSnapshot());
    }

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        var workerMetrics = decodeWorker.GetMetricsSnapshot();
        var attribution = GetHelperRemoteFrameLossSnapshot();
        var actionableLateFragmentCount = ScreenShareFrameLossAttributionRegistry.GetActionableLateFragmentCount(attribution);
        var helperSessionSnapshot = helperRemoteSessionController.BuildSessionSnapshot(attribution);
        var dominantLossClass = ScreenShareLossTaxonomyMapper.ClassifySession(
            attribution,
            softStaleCleanupCount: Interlocked.Read(ref softStaleCleanupCount),
            staleSupersededRecoverySuppressedCount: Interlocked.Read(ref staleSupersededRecoverySuppressedCount),
            postRecoveryStaleDropBypassCount: Interlocked.Read(ref postRecoveryStaleDropBypassCount));
        return new ScreenShareMetrics(
            FramesEnqueuedForDecode: workerMetrics.FramesEnqueuedForDecode,
            FramesDroppedBeforeDecode: workerMetrics.FramesDroppedBeforeDecode,
            FramesDroppedAfterDecode: workerMetrics.FramesDroppedAfterDecode,
            FramesDecoded: workerMetrics.FramesDecoded,
            FramesApplied: Interlocked.Read(ref framesApplied),
            NeedMoreInputCount: Interlocked.Read(ref needMoreInputCount),
            CompletedWithoutPictureCount: Interlocked.Read(ref completedWithoutPictureCount),
            DecodeErrors: Interlocked.Read(ref decodeErrors),
            ContinuityLossCount: Interlocked.Read(ref continuityLossCount),
            FrameGapContinuityLossCount: Interlocked.Read(ref frameGapContinuityLossCount),
            RecoveryKeyframesRequested: Interlocked.Read(ref recoveryKeyframesRequested),
            FramesDroppedWaitingForRecoveryKeyframe: Interlocked.Read(ref framesDroppedWaitingForRecoveryKeyframe),
            FramesDroppedForFrameGap: Interlocked.Read(ref framesDroppedForFrameGap),
            RecoveryActive:
                helperSessionSnapshot.Phase == HelperRemoteSessionPhase.Recovering ||
                helperSessionSnapshot.RecoveryMechanism != HelperRemoteRecoveryMechanism.None,
            FramesCoalesced: Interlocked.Read(ref framesCoalesced),
            AverageReceiveIntervalMs: workerMetrics.AverageReceiveIntervalMs,
            AverageDecodeDurationMs: workerMetrics.AverageDecodeDurationMs,
            AverageDecodeToApplyWaitMs: workerMetrics.AverageDecodeToApplyWaitMs,
            AverageEnqueueToDecodeStartMs: workerMetrics.AverageEnqueueToDecodeStartMs,
            AverageEnqueueToDropMs: workerMetrics.AverageEnqueueToDropMs,
            AverageApplyDurationMs: workerMetrics.AverageApplyDurationMs,
            AverageApplyIntervalMs: workerMetrics.AverageApplyIntervalMs,
            MaxPendingEncodedDepth: workerMetrics.MaxPendingEncodedDepth,
            MaxPendingDecodedDepth: workerMetrics.MaxPendingDecodedDepth,
            StaleFrameRenders: Interlocked.Read(ref staleFrameRenders),
            AverageDecodeIntervalMs: ComputeAverage(
                Interlocked.Read(ref totalDecodeIntervalMs),
                Interlocked.Read(ref decodeIntervalsObserved)),
            AverageRenderIntervalMs: ComputeAverage(
                Interlocked.Read(ref totalRenderIntervalMs),
                Interlocked.Read(ref renderIntervalsObserved)),
            AverageCaptureToRenderMs: ComputeAverage(
                Interlocked.Read(ref totalCaptureToRenderMs),
                Interlocked.Read(ref captureToRenderObserved)),
            ReassemblerStaleSupersededLossCount: attribution.ReassemblerStaleSupersededLossCount,
            AssemblyEvictedLossCount: attribution.AssemblyEvictedLossCount,
            ReadyFrameSkippedReplacedLossCount: attribution.ReadyFrameSkippedReplacedLossCount,
            ViewerRejectedBeforeEnqueueCount: attribution.ViewerRejectedBeforeEnqueueCount,
            WaitingForRecoveryKeyframeRejectCount: attribution.WaitingForRecoveryKeyframeRejectCount,
            BlockedByReservedRecoveryFrameRejectCount: attribution.BlockedByReservedRecoveryFrameRejectCount,
            OlderEpochIgnoredDuringRecoveryLockCount: attribution.OlderEpochIgnoredDuringRecoveryLockCount,
            NewerEpochNonKeyIgnoredDuringLockCount: attribution.NewerEpochNonKeyIgnoredDuringLockCount,
            DeferredPostRecoveryCandidateReplaceCount: attribution.DeferredPostRecoveryCandidateReplaceCount,
            DecodeWorkerDroppedBeforeDecodeCount: attribution.DecodeWorkerDroppedBeforeDecodeCount,
            DecodeQueueOverflowCount: attribution.DecodeQueueOverflowCount,
            DecodeAgeBudgetCount: attribution.DecodeAgeBudgetCount,
            DecodeGenerationChangedCount: attribution.DecodeGenerationChangedCount,
            DecodeStoppedCount: attribution.DecodeStoppedCount,
            DecodedApplyQueueOverflowCount: attribution.DecodedApplyQueueOverflowCount,
            DecodeWorkerDropQueueOverflowCount: workerMetrics.DecodeWorkerDropQueueOverflowCount,
            DecodeWorkerDropAgeBudgetCount: workerMetrics.DecodeWorkerDropAgeBudgetCount,
            DecodeWorkerDropGenerationCount: workerMetrics.DecodeWorkerDropGenerationCount,
            DecodeWorkerDropStoppedCount: workerMetrics.DecodeWorkerDropStoppedCount,
            DecodedFrameReplacedBeforeApplyCount: attribution.DecodedFrameReplacedBeforeApplyCount,
            DecodedStaleAfterRecoveryCount: attribution.DecodedStaleAfterRecoveryCount,
            DecodedBlockedByReservedRecoveryFrameCount: attribution.DecodedBlockedByReservedRecoveryFrameCount,
            DecodedNewerEpochIgnoredDuringLockCount: attribution.DecodedNewerEpochIgnoredDuringLockCount,
            DroppedWaitingForRecoveryKeyframeCount: attribution.DroppedWaitingForRecoveryKeyframeCount,
            DecodeFailedLossCount: attribution.DecodeFailedLossCount,
            StaleDroppedAfterDecodeCount: attribution.StaleDroppedAfterDecodeCount,
            ReassemblerLossCount: attribution.ReassemblerLossCount,
            EnqueueRejectCount: attribution.EnqueueRejectCount,
            DecodeWorkerDropCount: attribution.DecodeWorkerDropCount,
            PostDecodeDropCount: attribution.PostDecodeDropCount,
            UnattributedLossCount: attribution.UnattributedLossCount,
            DominantHelperAdmissionRejectReason: attribution.DominantHelperAdmissionRejectReason,
            RecoveryWaitRejectBeforeRunwayCount: attribution.WaitingForRecoveryKeyframeRejectCount,
            RecoveryRunwayOverflowRejectCount: attribution.RecoveryRunwayOverflowRejectCount,
            SuppressedEmitDuringRecoveryWaitCount: attribution.SuppressedEmitDuringRecoveryWaitCount,
            RecoveryCandidatePresentCount: attribution.RecoveryCandidatePresentCount,
            VisibleRecoveryFloorFrameId: helperSessionSnapshot.VisibleRecoveryFloorFrameId,
            StableVisibleHeadFrameId: helperSessionSnapshot.StableVisibleHeadFrameId,
            AppliedHeadFrameId: helperSessionSnapshot.AppliedHeadFrameId,
            VisibleHeadFrameId: helperSessionSnapshot.VisibleHeadFrameId,
            StaleSupersededRecoverySuppressedCount: Interlocked.Read(ref staleSupersededRecoverySuppressedCount),
            SoftStaleCleanupCount: Interlocked.Read(ref softStaleCleanupCount),
            PreCandidateGapTailEmittedToViewerCount: Interlocked.Read(ref preCandidateGapTailEmittedToViewerCount),
            PreCandidateGapTailRejectedCount: attribution.PreCandidateGapTailRejectedCount,
            FutureTailQuarantinedDuringGapCount: attribution.FutureTailQuarantinedDuringGapCount,
            FutureTailQuarantinedAfterGapCount: attribution.FutureTailQuarantinedAfterGapCount,
            LateFragmentAfterAppliedHeadCount: attribution.LateFragmentAfterAppliedHeadCount,
            LateFragmentAfterStableVisibleHeadCount: attribution.LateFragmentAfterStableVisibleHeadCount,
            LateFragmentAfterVisibleRecoveryCount: attribution.LateFragmentAfterVisibleRecoveryCount,
            RecoveryRunwayContiguousFollowerBufferCount: Interlocked.Read(ref recoveryRunwayContiguousFollowerBufferCount),
            RecoveryRunwayContiguousFollowerApplyCount: Interlocked.Read(ref recoveryRunwayContiguousFollowerApplyCount),
            RecoveryRunwayAbortCount: Interlocked.Read(ref recoveryProgressCorridorAbortCount),
            RecoveryFollowerWindowBufferedCount: Interlocked.Read(ref recoveryFollowerWindowBufferedCount),
            RecoveryFollowerWindowAppliedCount: Interlocked.Read(ref recoveryFollowerWindowAppliedCount),
            RecoveryFollowerWindowTrimmedCount: Interlocked.Read(ref recoveryFollowerWindowTrimmedCount),
            ProtectedRecoveryDeliveryCount: Interlocked.Read(ref protectedRecoveryDeliveryCount),
            RecoveryProgressCorridorCount: Interlocked.Read(ref recoveryProgressCorridorCount),
            RecoveryProgressCorridorSuccessCount: Interlocked.Read(ref recoveryProgressCorridorSuccessCount),
            RecoveryProgressCorridorAbortCount: Interlocked.Read(ref recoveryProgressCorridorAbortCount),
            RecoveryProgressCorridorAppliedCount: Interlocked.Read(ref recoveryProgressCorridorAppliedCount),
            RecoveryWindowActive: helperSessionSnapshot.RecoveryCorridorActive,
            ActiveRecoveryWindowEpoch: helperSessionSnapshot.RecoveryCorridorActive ? helperRemoteFollowerState.RecoveryProgressCorridorEpoch : -1,
            ActiveRecoveryWindowRecoveryFrameId: helperSessionSnapshot.RecoveryCorridorActive ? helperRemoteFollowerState.RecoveryProgressCorridorRecoveryFrameId : -1,
            RecoveryWindowContiguousFollowerApplyCount: helperSessionSnapshot.RecoveryCorridorActive
                ? Math.Max(0, helperRemoteFollowerState.RecoveryProgressCorridorAppliedCount - 1)
                : 0,
            RecoveryKeyframePendingVisibleApplyCount: Interlocked.Read(ref recoveryKeyframePendingVisibleApplyCount),
            StartupCorridorBufferedFollowerCount: Interlocked.Read(ref startupCorridorBufferedFollowerCount),
            StartupCorridorReleaseCount: Interlocked.Read(ref startupCorridorReleaseCount),
            StartupCorridorAbortCount: Interlocked.Read(ref startupCorridorAbortCount),
            StartupCorridorAbortReason: startupCorridorAbortReason,
            PostRecoveryVisibleGenerationResetCount: Interlocked.Read(ref postRecoveryVisibleGenerationResetCount),
            PostRecoveryPurgedPreRecoveryFollowerCount: Interlocked.Read(ref postRecoveryPurgedPreRecoveryFollowerCount),
            PostRecoveryStaleDropBypassCount: Interlocked.Read(ref postRecoveryStaleDropBypassCount),
            ActionableLateFragmentCount: actionableLateFragmentCount,
            HelperSessionPhase: ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(helperSessionSnapshot.Phase),
            HelperRecoveryMechanism: ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(helperSessionSnapshot.RecoveryMechanism),
            DominantLossClass: ScreenShareConceptualModelFormatter.FormatLossClass(dominantLossClass),
            BaselineEstablished: helperSessionSnapshot.BaselineEstablished,
            SteadyVisibleProgressActive: helperSessionSnapshot.SteadyVisibleProgressActive,
            AverageDecodeCompleteToVisibleApplyMs: ComputeAverage(
                Interlocked.Read(ref totalHelperDecodeCompleteToVisibleApplyMs),
                Interlocked.Read(ref helperDecodeCompleteToVisibleApplyObserved)),
            AverageUiPostToApplyMs: ComputeAverage(
                Interlocked.Read(ref totalHelperUiPostApplyMs),
                Interlocked.Read(ref helperUiPostApplyObserved)),
            AverageVisibleHeadLagFrames: ComputeAverage(
                Interlocked.Read(ref totalHelperVisibleHeadLagFrames),
                Interlocked.Read(ref helperVisibleHeadLagObserved)),
            AverageStableHeadLagFrames: ComputeAverage(
                Interlocked.Read(ref totalHelperStableHeadLagFrames),
                Interlocked.Read(ref helperStableHeadLagObserved)),
            StaleFrameDropVisibleStableCount: Interlocked.Read(ref staleFrameDropVisibleStableCount),
            StaleFrameDropVisibleStableLastAgeMs: Interlocked.Read(ref staleFrameDropVisibleStableLastAgeMs),
            OrdinaryNonKeyAgeBudgetBypassCount: Interlocked.Read(ref ordinaryNonKeyAgeBudgetBypassCount),
            LastReservedApplyHoldMs: helperRemoteVisibleProgressState.LastReservedApplyHoldMs,
            LastRecoveryProgressCorridorHoldMs: helperRemoteVisibleProgressState.LastRecoveryProgressCorridorHoldMs,
            LastRecoveryRunwayAbortHoldMs: helperRemoteVisibleProgressState.LastRecoveryRunwayAbortHoldMs,
            LastRecoveryProgressCorridorAbortReason: helperRemoteVisibleProgressState.LastRecoveryProgressCorridorAbortReason,
            H264ReferenceTaintActive: helperRemoteSessionController.ReferenceTaintState.Active,
            H264ReferenceTaintEnterCount: Interlocked.Read(ref h264ReferenceTaintEnterCount),
            H264ReferenceTaintReleaseCount: Interlocked.Read(ref h264ReferenceTaintReleaseCount),
            H264ReferenceTaintLastReason: helperRemoteSessionController.ReferenceTaintState.LastReason,
            H264ReferenceTaintDroppedNonKeyCount: Interlocked.Read(ref h264ReferenceTaintDroppedNonKeyCount),
            H264ReferenceTaintDecoderResetCount: Interlocked.Read(ref h264ReferenceTaintDecoderResetCount),
            H264ReferenceTaintStaleVisibleStableEnterCount: Interlocked.Read(ref h264ReferenceTaintStaleVisibleStableEnterCount),
            StaleNormalNonKeyVisibleSuppressCount: Interlocked.Read(ref staleNormalNonKeyVisibleSuppressCount),
            DecodedStaleVisibleSuppressCount: Interlocked.Read(ref decodedStaleVisibleSuppressCount),
            PostQuarantineSettleSuppressCount: Interlocked.Read(ref postQuarantineSettleSuppressCount),
            H264ReferenceQuarantineActive: helperRemoteSessionController.ReferenceTaintState.Active,
            H264ReferenceQuarantineReleaseBlockedCount: Interlocked.Read(ref h264ReferenceQuarantineReleaseBlockedCount),
            H264ReferenceQuarantineLastBlocker: h264ReferenceQuarantineLastBlocker,
            H264ReferenceQuarantineQuietReleaseCount: Interlocked.Read(ref h264ReferenceQuarantineQuietReleaseCount),
            CursorDeliveryMode: CursorDeliveryMode,
            CursorOverlayVisible: CursorOverlayVisible,
            CursorOverlayUpdatesReceivedCount: Interlocked.Read(ref cursorOverlayUpdatesReceivedCount),
            CursorOverlayUpdatesAppliedCount: Interlocked.Read(ref cursorOverlayUpdatesAppliedCount),
            CursorOverlayUpdateHz: ComputeCursorOverlayUpdateHz(),
            CursorOverlayLastAgeMs: Interlocked.Read(ref cursorOverlayLastAgeMs),
            CursorOverlayStaleCount: Interlocked.Read(ref cursorOverlayStaleCount),
            CursorOverlayLastStatus: cursorOverlayLastStatus);
    }

    public void OnEncodedFrame(
        string encoding,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs = 0,
        bool isKeyFrame = false,
        long streamEpoch = 0,
        ScreenShareVideoStreamConfigV1? streamConfig = null,
        long chunksDroppedOlderFrame = 0,
        long assembliesExpired = 0,
        long frameId = -1,
        string? sessionId = null,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal,
        long frameReadyObservedUtcMs = 0)
    {
        OnEncodedFrameCore(
            encoding,
            encodedFrameBytes,
            capturedTsUtcMs,
            isKeyFrame,
            streamEpoch,
            streamConfig,
            chunksDroppedOlderFrame,
            assembliesExpired,
            frameId,
            sessionId,
            assumeOwnership: false,
            recoveryDeliveryClass,
            frameReadyObservedUtcMs);
    }

    internal void OnOwnedEncodedFrame(
        string encoding,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs = 0,
        bool isKeyFrame = false,
        long streamEpoch = 0,
        ScreenShareVideoStreamConfigV1? streamConfig = null,
        long chunksDroppedOlderFrame = 0,
        long assembliesExpired = 0,
        long frameId = -1,
        string? sessionId = null,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal,
        long frameReadyObservedUtcMs = 0)
    {
        OnEncodedFrameCore(
            encoding,
            encodedFrameBytes,
            capturedTsUtcMs,
            isKeyFrame,
            streamEpoch,
            streamConfig,
            chunksDroppedOlderFrame,
            assembliesExpired,
            frameId,
            sessionId,
            assumeOwnership: true,
            recoveryDeliveryClass,
            frameReadyObservedUtcMs);
    }

    private void OnEncodedFrameCore(
        string encoding,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs,
        bool isKeyFrame,
        long streamEpoch,
        ScreenShareVideoStreamConfigV1? streamConfig,
        long chunksDroppedOlderFrame,
        long assembliesExpired,
        long frameId,
        string? sessionId,
        bool assumeOwnership,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass,
        long frameReadyObservedUtcMs)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);
        ArgumentNullException.ThrowIfNull(encodedFrameBytes);
        if (encodedFrameBytes.Length == 0)
        {
            throw new ArgumentException("Encoded frame bytes must not be empty.", nameof(encodedFrameBytes));
        }

        var effectiveSessionId = ResolveFrameSessionId(sessionId, streamConfig);
        var isHelperRemoteH264 = IsHelperRemoteH264(encoding);
        if (isHelperRemoteH264)
        {
            if (string.IsNullOrWhiteSpace(effectiveSessionId))
            {
                effectiveSessionId = helperRemoteSessionController.SessionId;
            }
            else
            {
                helperRemoteSessionController.SetSessionId(effectiveSessionId);
            }

            if (recoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.ProtectedFollower && isKeyFrame)
            {
                recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal;
            }
        }

        if (!TryPrepareDecoder(encoding, streamEpoch, streamConfig))
        {
            ObserveViewerRejectedBeforeEnqueue(effectiveSessionId, encoding, streamEpoch, frameId, isKeyFrame, "waiting_for_config");
            return;
        }

        var acceptedRecoveryKeyframeForDecode = false;
        if (isHelperRemoteH264)
        {
            PromoteHelperRemoteReferenceTaintFollowerIfEligible(
                streamEpoch,
                frameId,
                isKeyFrame,
                ref recoveryDeliveryClass);

            var rejectionReason = TryRejectHelperRemoteFrameBeforeDecode(
                    effectiveSessionId,
                    encoding,
                    streamEpoch,
                    frameId,
                    isKeyFrame,
                    recoveryDeliveryClass);
            if (!string.IsNullOrWhiteSpace(rejectionReason))
            {
                Interlocked.Increment(ref framesReceived);
                if (string.Equals(rejectionReason, "waiting_for_recovery_keyframe", StringComparison.Ordinal) ||
                    string.Equals(rejectionReason, "h264_reference_taint_waiting_for_recovery_keyframe", StringComparison.Ordinal) ||
                    string.Equals(rejectionReason, "blocked_by_reserved_recovery_frame", StringComparison.Ordinal))
                {
                    LogWaitingForRecoveryKeyframe(streamEpoch);
                }

                return;
            }

            if (TryDeferHelperRemotePostRecoveryCandidate(
                    effectiveSessionId,
                    encoding,
                    encodedFrameBytes,
                    capturedTsUtcMs,
                    streamEpoch,
                    frameId,
                    isKeyFrame,
                    assumeOwnership,
                    ref recoveryDeliveryClass))
            {
                Interlocked.Increment(ref framesReceived);
                return;
            }

            if (ShouldForceHelperRemoteRecoveryOnce(streamEpoch, frameId))
            {
                ActivateHelperRemoteRecovery(
                    "frame_gap",
                    streamEpoch,
                    currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                    shouldRequestRecoveryKeyframe: true,
                    expectedNextFrameId: frameId,
                    receivedFrameId: frameId,
                    lastCleanFrameId: helperRemoteRecoveryState.LastCleanFrameId);
            }

            var frameGapObservation = ObserveFrameGapContinuityLoss(streamEpoch, frameId, isKeyFrame);
            if (frameGapObservation is not null)
            {
                ActivateHelperRemoteRecovery(
                    "frame_gap",
                    streamEpoch,
                    currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                    shouldRequestRecoveryKeyframe: true,
                    expectedNextFrameId: frameGapObservation.Value.ExpectedNextFrameId,
                    receivedFrameId: frameGapObservation.Value.ReceivedFrameId,
                    lastCleanFrameId: frameGapObservation.Value.LastCleanFrameId);
            }

            var continuityHandling = ObserveReceiverContinuityLoss(streamEpoch, chunksDroppedOlderFrame, assembliesExpired);
            if (continuityHandling.Kind == ScreenShareViewerContinuityHandlingKind.HardRecovery)
            {
                ActivateHelperRemoteRecovery(
                    continuityHandling.Reason,
                    streamEpoch,
                    currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                    shouldRequestRecoveryKeyframe: ShouldRequestRecoveryKeyframeForContinuityLoss(continuityHandling.Reason));
            }
            else if (continuityHandling.Kind == ScreenShareViewerContinuityHandlingKind.SoftStaleCleanup)
            {
                ObserveSoftStaleCleanup(streamEpoch, continuityHandling.Reason);
            }

            var rejectionReasonAfterRecovery = TryRejectHelperRemoteFrameBeforeDecode(
                effectiveSessionId,
                encoding,
                streamEpoch,
                frameId,
                isKeyFrame,
                recoveryDeliveryClass);
            if (!string.IsNullOrWhiteSpace(rejectionReasonAfterRecovery))
            {
                Interlocked.Increment(ref framesReceived);
                if (string.Equals(rejectionReasonAfterRecovery, "waiting_for_recovery_keyframe", StringComparison.Ordinal) ||
                    string.Equals(rejectionReasonAfterRecovery, "h264_reference_taint_waiting_for_recovery_keyframe", StringComparison.Ordinal) ||
                    string.Equals(rejectionReasonAfterRecovery, "blocked_by_reserved_recovery_frame", StringComparison.Ordinal))
                {
                    LogWaitingForRecoveryKeyframe(streamEpoch);
                }

                return;
            }
            var acceptedByReferenceTaint =
                helperRemoteSessionController.ShouldTreatKeyframeAsReferenceTaintRecoveryOwner(
                    streamEpoch,
                    frameId,
                    isKeyFrame,
                    recoveryDeliveryClass);
            acceptedRecoveryKeyframeForDecode =
                isKeyFrame &&
                ((helperRemoteRecoveryState.RecoveryActive &&
                  streamEpoch >= helperRemoteRecoveryState.RecoveryStreamEpoch) ||
                 acceptedByReferenceTaint);
            if (acceptedRecoveryKeyframeForDecode)
            {
                recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.RecoveryOwner;
                ResetHelperRemoteH264DecoderForReferenceTaintIfNeeded(streamEpoch, frameId);
            }
        }

        var viewerAcceptedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ObserveViewerAcceptedForDecode(effectiveSessionId, encoding, streamEpoch, frameId, isKeyFrame, viewerAcceptedUtcMs);
        if (isHelperRemoteH264 &&
            recoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.ProtectedFollower)
        {
            Interlocked.Increment(ref protectedRecoveryDeliveryCount);
        }

        var bypassOrdinaryNonKeyAgeBudget =
            recoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal &&
            ShouldBypassHelperRemoteDecodeAgeBudget(encoding, streamEpoch, isKeyFrame);
        if (bypassOrdinaryNonKeyAgeBudget)
        {
            Interlocked.Increment(ref ordinaryNonKeyAgeBudgetBypassCount);
        }

        var bypassesAgeBudget =
            recoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal ||
            bypassOrdinaryNonKeyAgeBudget;
        var enqueueResult = assumeOwnership
            ? decodeWorker.EnqueueOwned(encoding, encodedFrameBytes, capturedTsUtcMs, isKeyFrame, streamEpoch, frameId, effectiveSessionId, requiresReservedApply: false, bypassesAgeBudget, recoveryDeliveryClass, frameReadyObservedUtcMs, viewerAcceptedUtcMs)
            : decodeWorker.EnqueueCopied(encoding, encodedFrameBytes, capturedTsUtcMs, isKeyFrame, streamEpoch, frameId, effectiveSessionId, requiresReservedApply: false, bypassesAgeBudget, recoveryDeliveryClass, frameReadyObservedUtcMs, viewerAcceptedUtcMs);

        Interlocked.Increment(ref framesReceived);
        if (isHelperRemoteH264 &&
            frameId >= 0 &&
            recoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.RecoveryOwner)
        {
            MarkReservedApplyPending(streamEpoch, frameId, startupKeyframePendingVisibleApply: true);
            Interlocked.Increment(ref recoveryKeyframePendingVisibleApplyCount);
            NotifyRecoveryWindowStateChanged(
                streamEpoch,
                frameId,
                frameId,
                contiguousFollowerApplyCount: 0,
                status: "started");
        }

        Interlocked.Exchange(ref this.chunksDroppedOlderFrame, Math.Max(0, chunksDroppedOlderFrame));
        Interlocked.Exchange(ref this.assembliesExpired, Math.Max(0, assembliesExpired));
        if (enqueueResult.DroppedPendingFrame)
        {
            Interlocked.Increment(ref framesCoalesced);
            if (isHelperRemoteH264 &&
                !ShouldSuppressHelperRemoteDecodeDropBeforeDecodeRecovery(streamEpoch, isKeyFrame, recoveryDeliveryClass, enqueueResult))
            {
                ActivateHelperRemoteRecovery(
                    "decode_drop_before_decode",
                    streamEpoch,
                    currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                    shouldRequestRecoveryKeyframe: true);

                if (!isKeyFrame)
                {
                    decodeWorker.ClearPending();
                    Interlocked.Increment(ref framesDroppedWaitingForRecoveryKeyframe);
                    ObserveDroppedWaitingForRecoveryKeyframe(effectiveSessionId, streamEpoch, frameId, isKeyFrame);
                    if (string.Equals(helperRemoteRecoveryState.RecoveryReason, "frame_gap", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref framesDroppedForFrameGap);
                    }

                    LogWaitingForRecoveryKeyframe(streamEpoch);
                    return;
                }
            }
        }
        PostViewerStatusUpdate(
            "Live",
            isActive: true,
            startSnapshotTimer: true);
    }

    private bool ShouldSuppressHelperRemoteDecodeDropBeforeDecodeRecovery(
        long streamEpoch,
        bool isKeyFrame,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass,
        LatestEncodedFrameEnqueueResult enqueueResult)
    {
        _ = streamEpoch;
        _ = isKeyFrame;
        _ = enqueueResult;
        return recoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal;
    }

    public void Clear()
    {
        LogHelperRemoteFrameLossSummary("clear", includeEpochDetails: true);
        Interlocked.Increment(ref generation);
        decodeWorker.ClearPending();
        h264StreamState.Reset();
        ResetLifecycleLoggingState();
        ResetRecoveryProgressCorridor();
        helperRemoteSessionController.ClearReferenceTaint();
        ResetCursorOverlayState();

        IsActive = false;
        StatusText = string.Empty;
        LastRenderedFrameAgeMs = -1;
#if DEBUG
        StopSnapshotTimer();
#endif
        ReplaceCurrentFrame(null);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Clear();
        cursorOverlayStaleTimer?.Dispose();
        cursorOverlayStaleTimer = null;
        decodeWorker.Dispose();
        GC.SuppressFinalize(this);
    }

    public void OnCursorState(ScreenShareCursorStateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (disposed)
        {
            return;
        }

        Interlocked.Increment(ref cursorOverlayUpdatesReceivedCount);
        _ = postStatusToUiAsync(() => ApplyCursorStateOnUi(message));
    }

    private void ApplyCursorStateOnUi(ScreenShareCursorStateV1 message)
    {
        if (disposed || !IsActive)
        {
            return;
        }

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ageMs = message.TsUtcMs > 0 ? Math.Max(0, nowUtcMs - message.TsUtcMs) : -1;
        Interlocked.Exchange(ref cursorOverlayLastAgeMs, ageMs);
        cursorOverlayLastStatus = string.IsNullOrWhiteSpace(message.Status)
            ? "unknown"
            : message.Status.Trim();
        cursorOverlayLastSeq = message.Seq;

        if (message.CapturedCursorEnabled || !message.CursorCaptureControlSupported)
        {
            CursorDeliveryMode = "fallback_captured";
            CursorOverlayVisible = false;
            return;
        }

        CursorDeliveryMode = "helper_overlay";
        if (!message.Visible ||
            ageMs > CursorOverlayStaleTimeoutMs ||
            !IsValidNormalizedCoordinate(message.Nx) ||
            !IsValidNormalizedCoordinate(message.Ny))
        {
            if (ageMs > CursorOverlayStaleTimeoutMs)
            {
                Interlocked.Increment(ref cursorOverlayStaleCount);
            }

            CursorOverlayVisible = false;
            ScheduleCursorOverlayStaleCheck();
            return;
        }

        CursorOverlayNx = message.Nx;
        CursorOverlayNy = message.Ny;
        CursorOverlayVisible = true;
        var nowTickMs = Environment.TickCount64;
        if (Interlocked.Read(ref cursorOverlayFirstUpdateTickMs) == 0)
        {
            Interlocked.CompareExchange(ref cursorOverlayFirstUpdateTickMs, nowTickMs, 0);
        }

        Interlocked.Exchange(ref cursorOverlayLastUpdateTickMs, nowTickMs);
        Interlocked.Increment(ref cursorOverlayUpdatesAppliedCount);
        ScheduleCursorOverlayStaleCheck();
    }

    private void ScheduleCursorOverlayStaleCheck()
    {
        cursorOverlayStaleTimer ??= new Timer(
            static state => ((ScreenShareViewerViewModel)state!).OnCursorOverlayStaleTimerTick(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        cursorOverlayStaleTimer.Change(CursorOverlayStaleCheckInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnCursorOverlayStaleTimerTick()
    {
        _ = postStatusToUiAsync(() =>
        {
            if (disposed || !CursorOverlayVisible)
            {
                return;
            }

            var lastAgeMs = Interlocked.Read(ref cursorOverlayLastAgeMs);
            var lastUpdateTickMs = Interlocked.Read(ref cursorOverlayLastUpdateTickMs);
            var elapsedMs = lastUpdateTickMs > 0
                ? Math.Max(0, Environment.TickCount64 - lastUpdateTickMs)
                : CursorOverlayStaleTimeoutMs + 1;
            if (lastAgeMs > CursorOverlayStaleTimeoutMs || elapsedMs > CursorOverlayStaleTimeoutMs)
            {
                CursorOverlayVisible = false;
                Interlocked.Increment(ref cursorOverlayStaleCount);
                cursorOverlayLastStatus = "telemetry_stale";
                return;
            }

            ScheduleCursorOverlayStaleCheck();
        });
    }

    private void ResetCursorOverlayState()
    {
        CursorOverlayVisible = false;
        CursorOverlayNx = 0;
        CursorOverlayNy = 0;
        CursorDeliveryMode = "captured_video";
        cursorOverlayLastStatus = "cleared";
        Interlocked.Exchange(ref cursorOverlayUpdatesReceivedCount, 0);
        Interlocked.Exchange(ref cursorOverlayUpdatesAppliedCount, 0);
        Interlocked.Exchange(ref cursorOverlayStaleCount, 0);
        Interlocked.Exchange(ref cursorOverlayLastAgeMs, -1);
        Interlocked.Exchange(ref cursorOverlayFirstUpdateTickMs, 0);
        Interlocked.Exchange(ref cursorOverlayLastUpdateTickMs, 0);
        Interlocked.Exchange(ref cursorOverlayLastSeq, 0);
        cursorOverlayStaleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private double ComputeCursorOverlayUpdateHz()
    {
        var applied = Interlocked.Read(ref cursorOverlayUpdatesAppliedCount);
        var firstTick = Interlocked.Read(ref cursorOverlayFirstUpdateTickMs);
        var lastTick = Interlocked.Read(ref cursorOverlayLastUpdateTickMs);
        if (applied <= 1 || firstTick <= 0 || lastTick <= firstTick)
        {
            return 0d;
        }

        var seconds = (lastTick - firstTick) / 1000d;
        return seconds > 0 ? (applied - 1) / seconds : 0d;
    }

    private static bool IsValidNormalizedCoordinate(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value is >= 0d and <= 1d;

    private async Task OnFrameDecodedAsync(LatestEncodedDecodedFrame decodedFrame)
    {
        var nextBitmap = decodedFrame.Bitmap;
        var decodeCallbackReceivedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await postFrameToUiAsync(() =>
        {
            var uiApplyStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (disposed || decodedFrame.Generation != Volatile.Read(ref generation))
            {
                if (!disposed && IsHelperRemoteH264(decodedFrame.Request.Encoding))
                {
                    ObserveDecodedFrameSuppressedAfterDecode(decodedFrame.Request, "post_recovery_visible_generation_reset");
                }

                ClearReservedApplyIfMatch(decodedFrame.Request);
                nextBitmap.Dispose();
                return;
            }

            var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordDecodeInterval(nowUtcMs);
            RecordRenderInterval(nowUtcMs);
            var ageMs = decodedFrame.CapturedTsUtcMs > 0
                ? Math.Max(0, nowUtcMs - decodedFrame.CapturedTsUtcMs)
                : -1;
            var isHelperRemoteFrame = IsHelperRemoteH264(decodedFrame.Request.Encoding);
            var helperSessionSnapshot = isHelperRemoteFrame
                ? GetHelperRemoteSessionSnapshot()
                : default;
            var isPotentialRecoveryApplyFrame =
                isHelperRemoteFrame &&
                decodedFrame.Request.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.RecoveryOwner;
            var bypassStaleFrameDrop =
                ShouldBypassStaleFrameDrop(decodedFrame.Request) ||
                isPotentialRecoveryApplyFrame;
            var isPostRecoveryGraceFrame = IsHelperRemotePostRecoveryStabilizationFrame(decodedFrame.Request);
            ObserveDecodeSucceeded(decodedFrame.Request, decodedFrame.DecodeCompletedUtcMs);
            if (isHelperRemoteFrame)
            {
                TryReleasePendingHelperRemoteH264ReferenceQuarantine("post_decode");
            }

            if (isHelperRemoteFrame &&
                helperRemoteSessionController.ReferenceTaintState.Active &&
                decodedFrame.Request.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal &&
                !IsReservedApplyRequest(decodedFrame.Request) &&
                !IsHelperRemotePostRecoveryStabilizationFrame(decodedFrame.Request) &&
                !decodedFrame.Request.IsKeyFrame)
            {
                Interlocked.Increment(ref h264ReferenceTaintDroppedNonKeyCount);
                Interlocked.Increment(ref framesDroppedWaitingForRecoveryKeyframe);
                ObserveDroppedWaitingForRecoveryKeyframe(
                    decodedFrame.Request.SessionId,
                    decodedFrame.Request.StreamEpoch,
                    decodedFrame.Request.FrameId,
                    decodedFrame.Request.IsKeyFrame);
                ClearReservedApplyIfMatch(decodedFrame.Request);
                nextBitmap.Dispose();
                return;
            }

            if (currentFrame is not null &&
                ShouldSuppressHelperRemotePostQuarantineSettleFrame(
                    decodedFrame.Request,
                    ageMs,
                    nowUtcMs))
            {
                SuppressDecodedStaleNormalNonKeyVisibleFrame(
                    decodedFrame.Request,
                    ageMs,
                    "post_quarantine_settle_stale_p_frame_suppressed",
                    incrementVisibleStableCounter: false,
                    incrementPostQuarantineSettleCounter: true);
                nextBitmap.Dispose();
                return;
            }

            if (currentFrame is not null &&
                ShouldDropHelperRemoteVisibleStableFrameForFreshness(
                    decodedFrame.Request,
                    ageMs,
                    helperSessionSnapshot))
            {
                SuppressDecodedStaleNormalNonKeyVisibleFrame(
                    decodedFrame.Request,
                    ageMs,
                    "stale_frame_drop_visible_stable",
                    incrementVisibleStableCounter: true,
                    incrementPostQuarantineSettleCounter: false);
                nextBitmap.Dispose();
                return;
            }

            if (currentFrame is not null &&
                ageMs > StaleFrameDropThresholdMs &&
                bypassStaleFrameDrop &&
                isPostRecoveryGraceFrame)
            {
                Interlocked.Increment(ref postRecoveryStaleDropBypassCount);
            }

            if (currentFrame is not null &&
                ageMs > StaleFrameDropThresholdMs &&
                !bypassStaleFrameDrop)
            {
                ObserveStaleDroppedAfterDecode(decodedFrame.Request, ResolveStaleDropReason(decodedFrame.Request));
                ClearReservedApplyIfMatch(decodedFrame.Request);
                LocalOperationalLog.Info(
                    "ScreenShare",
                    $"event=screenshare_viewer_stale_frame_dropped; role={logRole}; stream_epoch={decodedFrame.Request.StreamEpoch}; rendered_age_ms={ageMs}; threshold_ms={StaleFrameDropThresholdMs}");
                StaleFrameDropped?.Invoke(
                    this,
                    new ScreenShareViewerStaleFrameDroppedEventArgs(
                        ageMs,
                        decodedFrame.Request.StreamEpoch));
                nextBitmap.Dispose();
                return;
            }

            MaybeClearHelperRemotePostQuarantineSettleOnFreshFrame(decodedFrame.Request, ageMs, nowUtcMs);
            RecordCaptureToRender(ageMs);
            if (TryMarkEpochLogged(ref lastLoggedDecodeSuccessEpoch, decodedFrame.Request.StreamEpoch))
            {
                LocalOperationalLog.Info(
                    "ScreenShare",
                    $"event=screenshare_viewer_decode_succeeded; role={logRole}; encoding={decodedFrame.Request.Encoding}; stream_epoch={decodedFrame.Request.StreamEpoch}; frame_id={FormatFrameIdForLog(decodedFrame.Request.FrameId)}; is_keyframe={(decodedFrame.Request.IsKeyFrame ? 1 : 0)}; captured_ts_utc_ms={decodedFrame.CapturedTsUtcMs}; rendered_age_ms={ageMs}");
            }

            var isRecoveryApplyFrame = isPotentialRecoveryApplyFrame;
            if (isHelperRemoteFrame)
            {
                if (helperRemoteRecoveryState.RecoveryActive &&
                    decodedFrame.Request.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal &&
                    helperRemoteRecoveryState.RecoveryStreamEpoch == decodedFrame.Request.StreamEpoch &&
                    !decodedFrame.Request.IsKeyFrame)
                {
                    if (HasHelperRemoteBufferedRecoveryCandidate(decodedFrame.Request.SessionId, decodedFrame.Request.StreamEpoch))
                    {
                        ObserveDecodedFrameSuppressedAfterDecode(decodedFrame.Request, "recovery_runway_overflow");
                    }
                    else
                    {
                        Interlocked.Increment(ref framesDroppedWaitingForRecoveryKeyframe);
                        ObserveDroppedWaitingForRecoveryKeyframe(
                            decodedFrame.Request.SessionId,
                            decodedFrame.Request.StreamEpoch,
                            decodedFrame.Request.FrameId,
                            decodedFrame.Request.IsKeyFrame);
                        if (string.Equals(helperRemoteRecoveryState.RecoveryReason, "frame_gap", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref framesDroppedForFrameGap);
                        }

                        LogWaitingForRecoveryKeyframe(decodedFrame.Request.StreamEpoch);
                    }

                    ClearReservedApplyIfMatch(decodedFrame.Request);
                    nextBitmap.Dispose();
                    return;
                }

                Interlocked.Exchange(ref lastAppliedStreamEpoch, decodedFrame.Request.StreamEpoch);
                helperRemoteSessionController.RecordDecodedVisibleFrame(decodedFrame.Request);

                isRecoveryApplyFrame =
                    isRecoveryApplyFrame ||
                    decodedFrame.Request.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.RecoveryOwner;
            }

            var completesRecoveryResync = isRecoveryApplyFrame;
            if (completesRecoveryResync)
            {
                helperRemoteSessionController.CompleteRecoveryAfterVisibleResync();
                ResetHelperRemoteVisibleGenerationAfterRecoveryApply(decodedFrame.Request);
                StartRecoveryProgressCorridor(decodedFrame.Request.StreamEpoch, decodedFrame.Request.FrameId);
            }

            ReplaceCurrentFrame(nextBitmap);
            var visibleApplyUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.Increment(ref framesApplied);
            ObserveFrameApplied(decodedFrame.Request, visibleApplyUtcMs);
            LastRenderedFrameAgeMs = ageMs;
            if (completesRecoveryResync)
            {
                var effectiveSessionId = ResolveFrameSessionId(decodedFrame.Request.SessionId, streamConfig: null);
                if (string.IsNullOrWhiteSpace(effectiveSessionId))
                {
                    effectiveSessionId = helperRemoteSessionController.SessionId;
                }
                if (!string.IsNullOrWhiteSpace(effectiveSessionId))
                {
                    ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(
                        effectiveSessionId,
                        decodedFrame.Request.StreamEpoch,
                        decodedFrame.Request.FrameId);
                }
                NotifyRecoveryWindowStateChanged(
                    decodedFrame.Request.StreamEpoch,
                    decodedFrame.Request.FrameId,
                    decodedFrame.Request.FrameId,
                    contiguousFollowerApplyCount: 0,
                    status: "succeeded");
            }

            OnHelperRemoteFrameAppliedVisible(decodedFrame.Request);
            if (!completesRecoveryResync &&
                helperRemoteFollowerState.RecoveryProgressCorridorActive &&
                helperRemoteFollowerState.RecoveryProgressCorridorEpoch == decodedFrame.Request.StreamEpoch &&
                decodedFrame.Request.FrameId >= 0)
            {
                ObserveRecoveryProgressCorridorApply(decodedFrame.Request.StreamEpoch, decodedFrame.Request.FrameId);
            }

            if (isHelperRemoteFrame && decodedFrame.Request.FrameId >= 0)
            {
                ReleaseDeferredPostRecoveryCandidateIfMatch(
                    decodedFrame.Request.StreamEpoch,
                    decodedFrame.Request.FrameId);
            }

            var visibleApplyProgress = BuildHelperRemoteVisibleApplyProgress(decodedFrame.Request);
            RecordHelperVisibleApplyDiagnostics(
                decodedFrame,
                decodeCallbackReceivedUtcMs,
                uiApplyStartUtcMs,
                visibleApplyUtcMs,
                visibleApplyProgress);

            if (completesRecoveryResync)
            {
                Interlocked.Exchange(ref lastLoggedRecoveryWaitingEpoch, long.MinValue);
                LocalOperationalLog.Info(
                    "ScreenShare",
                    $"event=screenshare_viewer_recovery_keyframe_applied; role={logRole}; stream_epoch={decodedFrame.Request.StreamEpoch}; frame_id={FormatFrameIdForLog(decodedFrame.Request.FrameId)}; recovery_active=0; startup_bootstrap=0; delivery_class={decodedFrame.Request.RecoveryDeliveryClass}; age_ms={ageMs}");
                if (!string.IsNullOrWhiteSpace(decodedFrame.Request.SessionId))
                {
                    ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                        decodedFrame.Request.SessionId,
                        decodedFrame.Request.StreamEpoch,
                        "recovery_keyframe_applied",
                        decodedFrame.Request.FrameId);
                }
                RecoveryKeyframeApplied?.Invoke(
                    this,
                    new ScreenShareViewerRecoveryKeyframeAppliedEventArgs(
                        ageMs,
                        decodedFrame.Request.StreamEpoch));
            }
            MaybeLogRenderStats(ageMs);
            FrameApplied?.Invoke(
                this,
                new ScreenShareViewerFrameAppliedEventArgs(
                    ageMs,
                    decodedFrame.Request.StreamEpoch,
                    decodedFrame.Request.FrameId,
                    visibleApplyProgress.VisibleHeadFrameId,
                    visibleApplyProgress.StableVisibleHeadFrameId,
                    visibleApplyProgress.FramesAppliedSinceLastGap));
#if DEBUG
            decodeDurationLatency.RecordTimeSpanTicks(decodedFrame.DecodeDurationTimeSpanTicks);
            endToEndLatency.RecordTimeSpanTicks(DateTime.UtcNow.Ticks - decodedFrame.ReceivedUtcTicks);
#endif
        }).ConfigureAwait(false);
    }

    private void PostViewerStatusUpdate(string statusText, bool isActive, bool startSnapshotTimer)
    {
        var updateTask = postStatusToUiAsync(() =>
        {
            if (disposed)
            {
                return;
            }

            IsActive = isActive;
            StatusText = statusText;
#if DEBUG
            if (startSnapshotTimer)
            {
                StartSnapshotTimer();
            }
#endif
        });

        if (!updateTask.IsCompletedSuccessfully)
        {
            _ = ObserveViewerStatusUpdateAsync(updateTask, statusText, isActive);
        }
    }

    private async Task ObserveViewerStatusUpdateAsync(Task updateTask, string statusText, bool isActive)
    {
        try
        {
            await updateTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_status_update_failed; role={logRole}; status_text={SanitizeViewerStatusForLog(statusText)}; is_active={(isActive ? 1 : 0)}; reason={ex.GetType().Name}");
            LogDebug($"Viewer status update failed role={logRole}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task OnDecodeFailedAsync(LatestEncodedDecodeFailure failure)
    {
#if DEBUG
        decodeDurationLatency.RecordTimeSpanTicks(failure.DecodeDurationTimeSpanTicks);
#endif
        if (IsHelperRemoteH264(failure.Request.Encoding))
        {
            ClearReservedApplyIfMatch(failure.Request);
        }

        if (failure.Exception is H264DecoderNeedsMoreInputException)
        {
            Interlocked.Increment(ref needMoreInputCount);
            Interlocked.Increment(ref completedWithoutPictureCount);
            DecodeNeedsMoreInput?.Invoke(
                this,
                new ScreenShareViewerDecodeNeedsMoreInputEventArgs(failure.Request.StreamEpoch));
            if (IsHelperRemoteH264(failure.Request.Encoding))
            {
                if (helperRemoteRecoveryState.NeedMoreInputBurstEpoch != failure.Request.StreamEpoch)
                {
                    helperRemoteSessionController.RecordNeedMoreInput(failure.Request.StreamEpoch);
                }

                if (Interlocked.Read(ref lastAppliedStreamEpoch) == failure.Request.StreamEpoch)
                {
                    var needMoreInputBurstCount = helperRemoteSessionController.RecordNeedMoreInput(failure.Request.StreamEpoch);
                    if (needMoreInputBurstCount >= HelperRemoteNeedMoreInputBurstThreshold)
                    {
                        ActivateHelperRemoteRecovery(
                            "need_more_input_burst",
                            failure.Request.StreamEpoch,
                            currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                            shouldRequestRecoveryKeyframe: true);
                    }
                }
            }
            LogDebug($"Viewer H.264 decoder needs more input for epoch={failure.Request.StreamEpoch} bytes={failure.Request.EncodedFrameBytes.Length}.");
            return;
        }

        if (H264DecodeStreamState.IsH264Encoding(failure.Request.Encoding))
        {
            if (IsHelperRemoteH264(failure.Request.Encoding))
            {
                h264StreamState.ResetDecoderOnly();
            }
            else
            {
                h264StreamState.Reset();
            }
        }

        Interlocked.Increment(ref decodeErrors);
        ObserveDecodeFailed(failure.Request, failure.Exception);
        if (IsHelperRemoteH264(failure.Request.Encoding))
        {
            ActivateHelperRemoteRecovery(
                "decode_failed",
                failure.Request.StreamEpoch,
                currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                shouldRequestRecoveryKeyframe: true,
                receivedFrameId: failure.Request.FrameId,
                lastCleanFrameId: helperRemoteRecoveryState.LastCleanFrameId);
        }

        if (TryMarkEpochLogged(ref lastLoggedDecodeFailureEpoch, failure.Request.StreamEpoch))
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_decode_failed; role={logRole}; encoding={failure.Request.Encoding}; stream_epoch={failure.Request.StreamEpoch}; is_keyframe={(failure.Request.IsKeyFrame ? 1 : 0)}; reason={failure.Exception.GetType().Name}; payload_bytes={failure.Request.EncodedFrameBytes.Length}");
        }
        LogDebug($"Viewer frame decode/apply failed encoding={failure.Request.Encoding}: {failure.Exception.GetType().Name}: {failure.Exception.Message}");
        await postStatusToUiAsync(() =>
        {
            if (!disposed && failure.Generation == Volatile.Read(ref generation))
            {
                StatusText = "Invalid frame received";
            }
        }).ConfigureAwait(false);
    }

    private void ReplaceCurrentFrame(Bitmap? nextFrame)
    {
        var previous = currentFrame;
        if (ReferenceEquals(previous, nextFrame))
        {
            return;
        }

        CurrentFrame = nextFrame;
        if (previous is not null)
        {
            try
            {
                previous.Dispose();
            }
            catch (Exception ex)
            {
                LogDebug($"Viewer previous-frame disposal failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void MaybeLogRenderStats(long ageMs)
    {
        var nowTick = Stopwatch.GetTimestamp();
        while (true)
        {
            var lastTick = Interlocked.Read(ref lastRenderStatsLogTick);
            if (lastTick > 0 && Stopwatch.GetElapsedTime(lastTick, nowTick) < RenderStatsLogInterval)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastRenderStatsLogTick, nowTick, lastTick) == lastTick)
            {
                break;
            }
        }

        var metrics = GetMetricsSnapshot();
        var ageText = ageMs >= 0 ? ageMs.ToString() : "(none)";
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_frame_applied; role={logRole}; age_ms={ageText}; frames_completed={Interlocked.Read(ref framesReceived)}; frames_enqueued_for_decode={metrics.FramesEnqueuedForDecode}; frames_dropped_before_decode={metrics.FramesDroppedBeforeDecode}; frames_decoded={metrics.FramesDecoded}; frames_dropped_after_decode={metrics.FramesDroppedAfterDecode}; frames_applied={metrics.FramesApplied}; need_more_input_count={metrics.NeedMoreInputCount}; completed_without_picture_count={metrics.CompletedWithoutPictureCount}; continuity_loss_count={metrics.ContinuityLossCount}; frame_gap_continuity_loss_count={metrics.FrameGapContinuityLossCount}; frames_dropped_waiting_for_recovery_keyframe={metrics.FramesDroppedWaitingForRecoveryKeyframe}; frames_dropped_for_frame_gap={metrics.FramesDroppedForFrameGap}; avg_receive_interval_ms={metrics.AverageReceiveIntervalMs:F1}; avg_decode_duration_ms={metrics.AverageDecodeDurationMs:F1}; avg_decode_to_apply_wait_ms={metrics.AverageDecodeToApplyWaitMs:F1}; avg_apply_duration_ms={metrics.AverageApplyDurationMs:F1}; avg_apply_interval_ms={metrics.AverageApplyIntervalMs:F1}; avg_decode_interval_ms={metrics.AverageDecodeIntervalMs:F1}; avg_render_interval_ms={metrics.AverageRenderIntervalMs:F1}; avg_capture_to_render_ms={metrics.AverageCaptureToRenderMs:F1}; stream_epoch={h264StreamState.ConfiguredStreamEpoch}");
        LogHelperRemoteFrameLossSummary("periodic", includeEpochDetails: false);
        }

    private void RecordDecodeInterval(long nowUtcMs)
    {
        var previousDecodedUtcMs = Interlocked.Exchange(ref lastDecodedUtcMs, nowUtcMs);
        if (previousDecodedUtcMs <= 0 || nowUtcMs < previousDecodedUtcMs)
        {
            return;
        }

        Interlocked.Increment(ref decodeIntervalsObserved);
        Interlocked.Add(ref totalDecodeIntervalMs, nowUtcMs - previousDecodedUtcMs);
    }

    private void RecordRenderInterval(long nowUtcMs)
    {
        var previousRenderUtcMs = Interlocked.Exchange(ref lastRenderedUtcMs, nowUtcMs);
        if (previousRenderUtcMs <= 0 || nowUtcMs < previousRenderUtcMs)
        {
            return;
        }

        Interlocked.Increment(ref renderIntervalsObserved);
        Interlocked.Add(ref totalRenderIntervalMs, nowUtcMs - previousRenderUtcMs);
    }

    private void RecordCaptureToRender(long ageMs)
    {
        if (ageMs < 0)
        {
            return;
        }

        NknRuntimeDiagnostics.SetLastMediaFrameRenderedAgeMs(ageMs);
        Interlocked.Increment(ref captureToRenderObserved);
        Interlocked.Add(ref totalCaptureToRenderMs, ageMs);
        if (ageMs > StaleFrameThresholdMs)
        {
            Interlocked.Increment(ref staleFrameRenders);
        }
    }

    private static double ComputeAverage(long total, long count)
    {
        return count > 0 ? (double)total / count : 0;
    }

    private bool TryPrepareDecoder(string encoding, long streamEpoch, ScreenShareVideoStreamConfigV1? streamConfig)
    {
        if (!H264DecodeStreamState.IsH264Encoding(encoding))
        {
            return true;
        }

        var preparation = h264StreamState.Prepare(
            encoding,
            streamEpoch,
            streamConfig,
            onEpochChanged: () =>
            {
                Interlocked.Increment(ref generation);
                decodeWorker.ClearPending();
            });

        if (preparation.ConfigApplied &&
            TryMarkEpochLogged(ref lastLoggedPreparedEpoch, preparation.EffectiveStreamEpoch))
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_decoder_prepared; role={logRole}; encoding={encoding}; stream_epoch={preparation.EffectiveStreamEpoch}; has_stream_config=1; decoder_config_bytes={streamConfig?.DecoderConfigData?.Length ?? 0}");
        }

        if (!preparation.ShouldDecode)
        {
            if (TryMarkEpochLogged(ref lastLoggedDroppedEpoch, streamEpoch))
            {
                LocalOperationalLog.Info(
                    "ScreenShare",
                    $"event=screenshare_viewer_frame_dropped_waiting_for_config; role={logRole}; encoding={encoding}; stream_epoch={streamEpoch}; configured_epoch={preparation.ConfiguredStreamEpoch}; has_stream_config=0");
            }
            LogDebug($"Viewer H.264 frame dropped until stream config is available for epoch={streamEpoch}.");
            return false;
        }

        return true;
    }

    private void ResetLifecycleLoggingState()
    {
        Interlocked.Exchange(ref framesReceived, 0);
        Interlocked.Exchange(ref framesApplied, 0);
        Interlocked.Exchange(ref decodeErrors, 0);
        Interlocked.Exchange(ref needMoreInputCount, 0);
        Interlocked.Exchange(ref completedWithoutPictureCount, 0);
        Interlocked.Exchange(ref continuityLossCount, 0);
        Interlocked.Exchange(ref recoveryKeyframesRequested, 0);
        Interlocked.Exchange(ref framesDroppedWaitingForRecoveryKeyframe, 0);
        Interlocked.Exchange(ref framesCoalesced, 0);
        Interlocked.Exchange(ref chunksDroppedOlderFrame, 0);
        Interlocked.Exchange(ref assembliesExpired, 0);
        Interlocked.Exchange(ref lastRenderStatsLogTick, 0);
        Interlocked.Exchange(ref lastRenderedUtcMs, 0);
        Interlocked.Exchange(ref staleFrameRenders, 0);
        Interlocked.Exchange(ref lastDecodedUtcMs, 0);
        Interlocked.Exchange(ref decodeIntervalsObserved, 0);
        Interlocked.Exchange(ref totalDecodeIntervalMs, 0);
        Interlocked.Exchange(ref renderIntervalsObserved, 0);
        Interlocked.Exchange(ref totalRenderIntervalMs, 0);
        Interlocked.Exchange(ref captureToRenderObserved, 0);
        Interlocked.Exchange(ref totalCaptureToRenderMs, 0);
        Interlocked.Exchange(ref helperDecodeCompleteToVisibleApplyObserved, 0);
        Interlocked.Exchange(ref totalHelperDecodeCompleteToVisibleApplyMs, 0);
        Interlocked.Exchange(ref helperUiPostApplyObserved, 0);
        Interlocked.Exchange(ref totalHelperUiPostApplyMs, 0);
        Interlocked.Exchange(ref helperVisibleHeadLagObserved, 0);
        Interlocked.Exchange(ref totalHelperVisibleHeadLagFrames, 0);
        Interlocked.Exchange(ref helperStableHeadLagObserved, 0);
        Interlocked.Exchange(ref totalHelperStableHeadLagFrames, 0);
        Interlocked.Exchange(ref staleFrameDropVisibleStableCount, 0);
        Interlocked.Exchange(ref staleFrameDropVisibleStableLastAgeMs, -1);
        Interlocked.Exchange(ref ordinaryNonKeyAgeBudgetBypassCount, 0);
        Interlocked.Exchange(ref h264ReferenceTaintEnterCount, 0);
        Interlocked.Exchange(ref h264ReferenceTaintReleaseCount, 0);
        Interlocked.Exchange(ref h264ReferenceTaintDroppedNonKeyCount, 0);
        Interlocked.Exchange(ref h264ReferenceTaintDecoderResetCount, 0);
        Interlocked.Exchange(ref h264ReferenceTaintStaleVisibleStableEnterCount, 0);
        Interlocked.Exchange(ref staleNormalNonKeyVisibleSuppressCount, 0);
        Interlocked.Exchange(ref decodedStaleVisibleSuppressCount, 0);
        Interlocked.Exchange(ref postQuarantineSettleSuppressCount, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantineReleaseBlockedCount, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantineQuietReleaseCount, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseEpoch, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseFrameId, -1);
        Interlocked.Exchange(ref h264ReferenceQuarantineReleaseDueUtcMs, -1);
        Interlocked.Exchange(ref h264ReferenceQuarantineLastLossEpoch, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantineLastLossUtcMs, -1);
        ClearHelperRemotePostQuarantineSettle();
        h264ReferenceQuarantineLastBlocker = "none";
        h264ReferenceQuarantineLastLossReason = "none";
        Interlocked.Exchange(ref lastLoggedPreparedEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedDroppedEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedDecodeSuccessEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedDecodeFailureEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedRecoveryWaitingEpoch, long.MinValue);
        Interlocked.Exchange(ref lastAppliedStreamEpoch, 0);
        Interlocked.Exchange(ref lastObservedReceiverDroppedFrameCount, 0);
        Interlocked.Exchange(ref lastObservedAssembliesExpiredCount, 0);
        helperRemoteSessionController.ResetState();
        Interlocked.Exchange(ref postRecoveryVisibleGenerationResetCount, 0);
        Interlocked.Exchange(ref postRecoveryPurgedPreRecoveryFollowerCount, 0);
        Interlocked.Exchange(ref postRecoveryStaleDropBypassCount, 0);
        Interlocked.Exchange(ref recoveryFollowerWindowBufferedCount, 0);
        Interlocked.Exchange(ref recoveryFollowerWindowAppliedCount, 0);
        Interlocked.Exchange(ref recoveryFollowerWindowTrimmedCount, 0);
        Interlocked.Exchange(ref staleSupersededRecoverySuppressedCount, 0);
        Interlocked.Exchange(ref softStaleCleanupCount, 0);
        Interlocked.Exchange(ref preCandidateGapTailEmittedToViewerCount, 0);
        Interlocked.Exchange(ref recoveryKeyframePendingVisibleApplyCount, 0);
        Interlocked.Exchange(ref startupCorridorBufferedFollowerCount, 0);
        Interlocked.Exchange(ref startupCorridorReleaseCount, 0);
        Interlocked.Exchange(ref startupCorridorAbortCount, 0);
        startupCorridorAbortReason = "none";
        lastHelperRemoteEpochDetailLogSignature = string.Empty;
    }

    private bool ShouldLogHelperRemoteEpochDetails(
        string trigger,
        ScreenShareFrameLossSessionSnapshot snapshot,
        bool includeEpochDetails)
    {
        var signature = BuildHelperRemoteEpochDetailSignature(snapshot);
        if (includeEpochDetails || !string.Equals(trigger, "periodic", StringComparison.Ordinal))
        {
            lastHelperRemoteEpochDetailLogSignature = signature;
            return true;
        }

        if (string.Equals(signature, lastHelperRemoteEpochDetailLogSignature, StringComparison.Ordinal))
        {
            return false;
        }

        lastHelperRemoteEpochDetailLogSignature = signature;
        return true;
    }

    private static string BuildHelperRemoteEpochDetailSignature(ScreenShareFrameLossSessionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            return string.Empty;
        }

        var epochSnapshotSignature = string.Join(
            "|",
            snapshot.EpochSnapshots.Select(static epoch =>
                $"{epoch.StreamEpoch}:{epoch.FramesEmitted}:{epoch.FramesApplied}:{epoch.ReassemblerStaleSupersededLossCount}:{epoch.AssemblyEvictedLossCount}:{epoch.ReadyFrameSkippedReplacedLossCount}:{epoch.GapNonKeyPrunedCount}:{epoch.FutureTailQuarantinedDuringGapCount}:{epoch.UnattributedLossCount}:{epoch.LastAppliedFrameId}"));
        var epochDiagnosticsSignature = string.Join(
            "|",
            snapshot.EpochDiagnostics.Select(static epoch =>
                $"{epoch.StreamEpoch}:{epoch.GapCount}:{epoch.RecoveryKeyframeApplyCount}:{epoch.ResyncCount}:{epoch.FramesAppliedSinceLastGap}:{epoch.TimeToFirstApplyMs}:{epoch.TimeInRecoveryLockMs}:{epoch.DominantReassemblerRootCause}"));

        return string.Join(
            ";",
            snapshot.SessionId,
            snapshot.FramesEmitted,
            snapshot.FramesApplied,
            snapshot.ReassemblerLossCount,
            snapshot.DominantReassemblerRootCause,
            epochSnapshotSignature,
            epochDiagnosticsSignature);
    }

    private ScreenShareFrameLossSessionSnapshot GetHelperRemoteFrameLossSnapshot()
    {
        if (!string.Equals(logRole, "helper_remote", StringComparison.Ordinal))
        {
            return ScreenShareFrameLossSessionSnapshot.Empty;
        }

        return ScreenShareFrameLossAttributionRegistry.GetSnapshot(helperRemoteRecoveryState.SessionId);
    }

    internal ScreenShareFrameLossSessionSnapshot GetFrameLossSnapshotForDiagnostics()
    {
        return GetHelperRemoteFrameLossSnapshot();
    }

    private static string ResolveFrameSessionId(string? sessionId, ScreenShareVideoStreamConfigV1? streamConfig)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId.Trim();
        }

        return string.IsNullOrWhiteSpace(streamConfig?.SessionId)
            ? string.Empty
            : streamConfig!.SessionId.Trim();
    }

    private void ObserveViewerRejectedBeforeEnqueue(string sessionId, string encoding, long streamEpoch, long frameId, bool isKeyFrame, string reason)
    {
        if (!IsHelperRemoteH264(encoding))
        {
            return;
        }

        helperRemoteSessionController.SetSessionId(sessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveViewerRejectedBeforeEnqueue(
            sessionId,
            streamEpoch,
            frameId,
            isKeyFrame,
            reason);
        if (!isKeyFrame && IsActionableReferenceTaintLossReason(reason))
        {
            ObserveHelperRemoteReferenceQuarantineLoss(streamEpoch, reason);
        }

        if (ShouldEnterHelperRemoteReferenceTaintForViewerRejection(streamEpoch, frameId, isKeyFrame, reason))
        {
            EnterHelperRemoteH264ReferenceTaint(streamEpoch, reason);
        }
    }

    private void ObserveViewerAcceptedForDecode(string sessionId, string encoding, long streamEpoch, long frameId, bool isKeyFrame, long viewerAcceptedUtcMs)
    {
        if (!IsHelperRemoteH264(encoding))
        {
            return;
        }

        helperRemoteSessionController.SetSessionId(sessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveViewerAccepted(
            sessionId,
            streamEpoch,
            frameId,
            isKeyFrame,
            viewerAcceptedUtcMs);
    }

    private void ObserveDecodeSucceeded(EncodedFrameDecodeRequest request, long decodeCompletedUtcMs)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeSucceeded(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            decodeCompletedUtcMs);
    }

    private void ObserveDecodeFailed(EncodedFrameDecodeRequest request, Exception exception)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeFailed(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            exception.GetType().Name);
    }

    private void ObserveFrameApplied(EncodedFrameDecodeRequest request, long appliedUtcMs)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            appliedUtcMs);
    }

    private void ObserveStaleDroppedAfterDecode(EncodedFrameDecodeRequest request)
        => ObserveStaleDroppedAfterDecode(request, "stale_frame_drop");

    private void ObserveStaleDroppedAfterDecode(EncodedFrameDecodeRequest request, string reason)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveStaleDroppedAfterDecode(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            string.IsNullOrWhiteSpace(reason) ? "stale_frame_drop" : reason.Trim());
    }

    private void ObserveDroppedWaitingForRecoveryKeyframe(string sessionId, long streamEpoch, long frameId, bool isKeyFrame)
    {
        if (!string.Equals(logRole, "helper_remote", StringComparison.Ordinal))
        {
            return;
        }

        helperRemoteSessionController.SetSessionId(sessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDroppedWaitingForRecoveryKeyframe(
            sessionId,
            streamEpoch,
            frameId,
            isKeyFrame,
            SanitizeRecoveryReason(helperRemoteRecoveryState.RecoveryReason));
    }

    private void OnDecodeWorkerFrameEnqueued(EncodedFrameDecodeRequest request)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeEnqueued(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            request.DecodeEnqueuedUtcMs);
    }

    private void OnDecodeWorkerFrameDecodeStarted(EncodedFrameDecodeRequest request)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeStarted(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            request.DecodeStartedUtcMs);
    }

    private void OnDecodeWorkerFrameDroppedBeforeDecode(EncodedFrameDecodeRequest request, string reason)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        ClearReservedApplyIfMatch(request);
        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        var normalizedReason = NormalizeDecodeWorkerLossReason(reason);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeWorkerDroppedBeforeDecode(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            normalizedReason);
        if (request.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal &&
            !request.IsKeyFrame &&
            IsActionableReferenceTaintLossReason(normalizedReason))
        {
            ActivateHelperRemoteRecovery(
                normalizedReason,
                request.StreamEpoch,
                currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
                shouldRequestRecoveryKeyframe: true);
        }
    }

    private void OnDecodeWorkerFrameDroppedAfterDecode(EncodedFrameDecodeRequest request, string reason)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        ClearReservedApplyIfMatch(request);
        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodedFrameReplacedBeforeApply(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            NormalizeDecodeWorkerLossReason(reason));
    }

    private void LogHelperRemoteH264ReferenceTaintSummary(
        string trigger,
        string sessionId,
        ScreenShareMetrics viewerMetrics)
    {
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_h264_reference_taint_summary; role={logRole}; trigger={trigger}; session_id={sessionId}; h264_reference_taint_active={(viewerMetrics.H264ReferenceTaintActive ? 1 : 0)}; h264_reference_taint_enter_count={viewerMetrics.H264ReferenceTaintEnterCount}; h264_reference_taint_release_count={viewerMetrics.H264ReferenceTaintReleaseCount}; h264_reference_taint_last_reason={viewerMetrics.H264ReferenceTaintLastReason}; h264_reference_taint_dropped_non_key_count={viewerMetrics.H264ReferenceTaintDroppedNonKeyCount}; h264_reference_taint_decoder_reset_count={viewerMetrics.H264ReferenceTaintDecoderResetCount}; h264_reference_taint_stale_visible_stable_enter_count={viewerMetrics.H264ReferenceTaintStaleVisibleStableEnterCount}; stale_normal_non_key_visible_suppress_count={viewerMetrics.StaleNormalNonKeyVisibleSuppressCount}; decoded_stale_visible_suppress_count={viewerMetrics.DecodedStaleVisibleSuppressCount}; post_quarantine_settle_suppress_count={viewerMetrics.PostQuarantineSettleSuppressCount}; h264_reference_quarantine_active={(viewerMetrics.H264ReferenceQuarantineActive ? 1 : 0)}; h264_reference_quarantine_release_blocked_count={viewerMetrics.H264ReferenceQuarantineReleaseBlockedCount}; h264_reference_quarantine_last_blocker={viewerMetrics.H264ReferenceQuarantineLastBlocker}; h264_reference_quarantine_quiet_release_count={viewerMetrics.H264ReferenceQuarantineQuietReleaseCount}");
    }

    private void LogHelperRemoteFrameLossSummary(string trigger, bool includeEpochDetails)
    {
        if (!string.Equals(logRole, "helper_remote", StringComparison.Ordinal))
        {
            return;
        }

        var snapshot = GetHelperRemoteFrameLossSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.SessionId) || snapshot.FragmentSeenFrames <= 0)
        {
            return;
        }

        var viewerMetrics = GetMetricsSnapshot();
        var dominantHelperAdmissionRejectReason = ResolveDominantHelperAdmissionRejectReason(snapshot, viewerMetrics);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_frame_loss_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; fragment_seen_frames={snapshot.FragmentSeenFrames}; frames_assembled={snapshot.FramesAssembled}; frames_ready={snapshot.FramesReady}; frames_emitted={snapshot.FramesEmitted}; viewer_accepted_frames={snapshot.ViewerAcceptedFrames}; decode_enqueued_frames={snapshot.DecodeEnqueuedFrames}; frames_decoded={snapshot.FramesDecoded}; frames_applied={snapshot.FramesApplied}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; dominant_loss_class={viewerMetrics.DominantLossClass}; reassembler_stale_superseded_loss_count={snapshot.ReassemblerStaleSupersededLossCount}; assembly_evicted_loss_count={snapshot.AssemblyEvictedLossCount}; ready_frame_skipped_replaced_loss_count={snapshot.ReadyFrameSkippedReplacedLossCount}; viewer_rejected_before_enqueue_count={snapshot.ViewerRejectedBeforeEnqueueCount}; waiting_for_recovery_keyframe_reject_count={snapshot.WaitingForRecoveryKeyframeRejectCount}; recovery_wait_reject_before_runway_count={viewerMetrics.RecoveryWaitRejectBeforeRunwayCount}; recovery_runway_overflow_reject_count={viewerMetrics.RecoveryRunwayOverflowRejectCount}; suppressed_emit_during_recovery_wait_count={snapshot.SuppressedEmitDuringRecoveryWaitCount}; stale_superseded_recovery_suppressed_count={viewerMetrics.StaleSupersededRecoverySuppressedCount}; soft_stale_cleanup_count={viewerMetrics.SoftStaleCleanupCount}; pre_candidate_gap_tail_emitted_to_viewer_count={viewerMetrics.PreCandidateGapTailEmittedToViewerCount}; blocked_by_reserved_recovery_frame_reject_count={snapshot.BlockedByReservedRecoveryFrameRejectCount}; older_epoch_ignored_during_recovery_lock_count={snapshot.OlderEpochIgnoredDuringRecoveryLockCount}; newer_epoch_non_key_ignored_during_lock_count={snapshot.NewerEpochNonKeyIgnoredDuringLockCount}; deferred_post_recovery_candidate_replace_count={snapshot.DeferredPostRecoveryCandidateReplaceCount}; decode_worker_dropped_before_decode_count={snapshot.DecodeWorkerDroppedBeforeDecodeCount}; decode_queue_overflow_count={snapshot.DecodeQueueOverflowCount}; decode_age_budget_count={snapshot.DecodeAgeBudgetCount}; decode_generation_changed_count={snapshot.DecodeGenerationChangedCount}; decode_stopped_count={snapshot.DecodeStoppedCount}; decoded_apply_queue_overflow_count={snapshot.DecodedApplyQueueOverflowCount}; decoded_frame_replaced_before_apply_count={snapshot.DecodedFrameReplacedBeforeApplyCount}; decoded_stale_after_recovery_count={snapshot.DecodedStaleAfterRecoveryCount}; decoded_blocked_by_reserved_recovery_frame_count={snapshot.DecodedBlockedByReservedRecoveryFrameCount}; decoded_newer_epoch_ignored_during_lock_count={snapshot.DecodedNewerEpochIgnoredDuringLockCount}; dropped_waiting_for_recovery_keyframe_count={snapshot.DroppedWaitingForRecoveryKeyframeCount}; decode_failed_loss_count={snapshot.DecodeFailedLossCount}; stale_dropped_after_decode_count={snapshot.StaleDroppedAfterDecodeCount}; stale_frame_drop_visible_stable_count={viewerMetrics.StaleFrameDropVisibleStableCount}; stale_frame_drop_visible_stable_last_age_ms={(viewerMetrics.StaleFrameDropVisibleStableLastAgeMs >= 0 ? viewerMetrics.StaleFrameDropVisibleStableLastAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; ordinary_non_key_age_budget_bypass_count={viewerMetrics.OrdinaryNonKeyAgeBudgetBypassCount}; reassembler_loss_count={snapshot.ReassemblerLossCount}; enqueue_reject_count={snapshot.EnqueueRejectCount}; decode_worker_drop_count={snapshot.DecodeWorkerDropCount}; post_decode_drop_count={snapshot.PostDecodeDropCount}; gap_non_key_pruned_count={snapshot.GapNonKeyPrunedCount}; future_tail_quarantined_during_gap_count={snapshot.FutureTailQuarantinedDuringGapCount}; future_tail_quarantined_after_gap_count={snapshot.FutureTailQuarantinedAfterGapCount}; pre_candidate_gap_tail_rejected_count={snapshot.PreCandidateGapTailRejectedCount}; recovery_candidate_present_count={snapshot.RecoveryCandidatePresentCount}; visible_recovery_floor_frame_id={FormatFrameIdForLog(snapshot.VisibleRecoveryFloorFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(snapshot.StableVisibleHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(snapshot.AppliedHeadFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(snapshot.OrderedEmitHeadFrameId)}; winning_recovery_frame_id={FormatFrameIdForLog(snapshot.WinningRecoveryFrameId)}; visible_head_frame_id={FormatFrameIdForLog(viewerMetrics.VisibleHeadFrameId)}; superseded_recovery_tail_cleanup_count={snapshot.SupersededRecoveryTailCleanupCount}; late_same_epoch_after_head_advanced_drop_count={snapshot.LateSameEpochAfterHeadAdvancedDropCount}; stale_runway_window_abort_count={snapshot.StaleRunwayWindowAbortCount}; runway_candidate_expired_after_head_advance_count={snapshot.RunwayCandidateExpiredAfterHeadAdvanceCount}; runway_followers_emitted_within_actionable_window_count={snapshot.RunwayFollowersEmittedWithinActionableWindowCount}; same_epoch_recovery_owner_suppressed_count={snapshot.RecoveryKeyframeSupersededOrReplacedCount}; recovery_owner_replaced_count={snapshot.RecoveryOwnerReplacedCount}; older_epoch_cleanup_after_epoch_advance_count={snapshot.OlderEpochCleanupAfterEpochAdvanceCount}; late_fragment_after_applied_head_count={snapshot.LateFragmentAfterAppliedHeadCount}; late_fragment_after_ordered_head_count={snapshot.LateFragmentAfterOrderedHeadCount}; late_fragment_after_stable_visible_head_count={snapshot.LateFragmentAfterStableVisibleHeadCount}; late_fragment_after_visible_recovery_count={snapshot.LateFragmentAfterVisibleRecoveryCount}; recovery_runway_contiguous_follower_buffer_count={viewerMetrics.RecoveryRunwayContiguousFollowerBufferCount}; recovery_runway_contiguous_follower_apply_count={viewerMetrics.RecoveryRunwayContiguousFollowerApplyCount}; recovery_runway_abort_count={viewerMetrics.RecoveryRunwayAbortCount}; recovery_follower_window_buffered_count={viewerMetrics.RecoveryFollowerWindowBufferedCount}; recovery_follower_window_applied_count={viewerMetrics.RecoveryFollowerWindowAppliedCount}; recovery_follower_window_trimmed_count={viewerMetrics.RecoveryFollowerWindowTrimmedCount}; protected_recovery_delivery_count={viewerMetrics.ProtectedRecoveryDeliveryCount}; recovery_progress_corridor_count={viewerMetrics.RecoveryProgressCorridorCount}; recovery_progress_corridor_success_count={viewerMetrics.RecoveryProgressCorridorSuccessCount}; recovery_progress_corridor_abort_count={viewerMetrics.RecoveryProgressCorridorAbortCount}; recovery_progress_corridor_applied_count={viewerMetrics.RecoveryProgressCorridorAppliedCount}; recovery_window_active={(viewerMetrics.RecoveryWindowActive ? 1 : 0)}; active_recovery_window_epoch={FormatFrameIdForLog(viewerMetrics.ActiveRecoveryWindowEpoch)}; active_recovery_window_recovery_frame_id={FormatFrameIdForLog(viewerMetrics.ActiveRecoveryWindowRecoveryFrameId)}; recovery_window_contiguous_follower_apply_count={viewerMetrics.RecoveryWindowContiguousFollowerApplyCount}; recovery_keyframe_pending_visible_apply_count={viewerMetrics.RecoveryKeyframePendingVisibleApplyCount}; startup_corridor_buffered_follower_count={viewerMetrics.StartupCorridorBufferedFollowerCount}; startup_corridor_release_count={viewerMetrics.StartupCorridorReleaseCount}; startup_corridor_abort_count={viewerMetrics.StartupCorridorAbortCount}; startup_corridor_abort_reason={viewerMetrics.StartupCorridorAbortReason}; recovery_keyframe_resync_count={snapshot.RecoveryKeyframeResyncCount}; gap_active={(snapshot.GapActive ? 1 : 0)}; gap_expected_frame_id={FormatFrameIdForLog(snapshot.GapExpectedFrameId)}; buffered_recovery_keyframe_frame_id={FormatFrameIdForLog(snapshot.BufferedRecoveryKeyframeFrameId)}; recovery_keyframe_candidate_present={(snapshot.BufferedRecoveryKeyframeFrameId >= 0 ? 1 : 0)}; future_non_key_buffered_count={snapshot.FutureNonKeyBufferedCount}; dominant_helper_admission_reject_reason={dominantHelperAdmissionRejectReason}; post_recovery_visible_generation_reset_count={viewerMetrics.PostRecoveryVisibleGenerationResetCount}; post_recovery_purged_pre_recovery_follower_count={viewerMetrics.PostRecoveryPurgedPreRecoveryFollowerCount}; post_recovery_stale_drop_bypass_count={viewerMetrics.PostRecoveryStaleDropBypassCount}; late_fragment_after_successful_recovery_count={snapshot.LateFragmentAfterSuccessfulRecoveryCount}; actionable_late_fragment_count={viewerMetrics.ActionableLateFragmentCount}; unattributed_loss_count={snapshot.UnattributedLossCount}; last_applied_frame_id={FormatFrameIdForLog(snapshot.LastAppliedFrameId)}; last_clean_frame_id={FormatFrameIdForLog(snapshot.LastCleanFrameId)}; recent_losses={ScreenShareFrameLossAttributionRegistry.FormatRecentLosses(snapshot.RecentLosses)}");
        LogHelperRemoteH264ReferenceTaintSummary(trigger, snapshot.SessionId, viewerMetrics);

        var workerMetrics = decodeWorker.GetMetricsSnapshot();
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_decode_worker_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; frames_enqueued_for_decode={workerMetrics.FramesEnqueuedForDecode}; frames_dropped_before_decode={workerMetrics.FramesDroppedBeforeDecode}; frames_dropped_after_decode={workerMetrics.FramesDroppedAfterDecode}; frames_decoded={workerMetrics.FramesDecoded}; frames_apply_callbacks_completed={workerMetrics.FramesApplyCallbacksCompleted}; max_pending_encoded_depth={workerMetrics.MaxPendingEncodedDepth}; max_pending_decoded_depth={workerMetrics.MaxPendingDecodedDepth}; avg_enqueue_to_decode_start_ms={workerMetrics.AverageEnqueueToDecodeStartMs:F1}; avg_enqueue_to_drop_ms={workerMetrics.AverageEnqueueToDropMs:F1}; decode_worker_drop_queue_overflow_count={workerMetrics.DecodeWorkerDropQueueOverflowCount}; decode_worker_drop_age_budget_count={workerMetrics.DecodeWorkerDropAgeBudgetCount}; decode_worker_drop_generation_count={workerMetrics.DecodeWorkerDropGenerationCount}; decode_worker_drop_stopped_count={workerMetrics.DecodeWorkerDropStoppedCount}");

        var upstreamLatencySnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperUpstreamLatencySnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_upstream_latency_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; capture_to_frame_ready_avg_ms={upstreamLatencySnapshot.CaptureToFrameReadyAvgMs}; capture_to_frame_ready_median_ms={upstreamLatencySnapshot.CaptureToFrameReadyMedianMs}; capture_to_frame_ready_p95_ms={upstreamLatencySnapshot.CaptureToFrameReadyP95Ms}; capture_to_frame_ready_max_ms={upstreamLatencySnapshot.CaptureToFrameReadyMaxMs}; frame_ready_to_viewer_accept_avg_ms={upstreamLatencySnapshot.FrameReadyToViewerAcceptAvgMs}; frame_ready_to_viewer_accept_median_ms={upstreamLatencySnapshot.FrameReadyToViewerAcceptMedianMs}; frame_ready_to_viewer_accept_p95_ms={upstreamLatencySnapshot.FrameReadyToViewerAcceptP95Ms}; frame_ready_to_viewer_accept_max_ms={upstreamLatencySnapshot.FrameReadyToViewerAcceptMaxMs}; viewer_accept_to_decode_enqueue_avg_ms={upstreamLatencySnapshot.ViewerAcceptToDecodeEnqueueAvgMs}; viewer_accept_to_decode_enqueue_median_ms={upstreamLatencySnapshot.ViewerAcceptToDecodeEnqueueMedianMs}; viewer_accept_to_decode_enqueue_p95_ms={upstreamLatencySnapshot.ViewerAcceptToDecodeEnqueueP95Ms}; viewer_accept_to_decode_enqueue_max_ms={upstreamLatencySnapshot.ViewerAcceptToDecodeEnqueueMaxMs}; decode_enqueue_to_decode_start_avg_ms={upstreamLatencySnapshot.DecodeEnqueueToDecodeStartAvgMs}; decode_enqueue_to_decode_start_median_ms={upstreamLatencySnapshot.DecodeEnqueueToDecodeStartMedianMs}; decode_enqueue_to_decode_start_p95_ms={upstreamLatencySnapshot.DecodeEnqueueToDecodeStartP95Ms}; decode_enqueue_to_decode_start_max_ms={upstreamLatencySnapshot.DecodeEnqueueToDecodeStartMaxMs}; capture_to_decode_start_avg_ms={upstreamLatencySnapshot.CaptureToDecodeStartAvgMs}; capture_to_decode_start_median_ms={upstreamLatencySnapshot.CaptureToDecodeStartMedianMs}; capture_to_decode_start_p95_ms={upstreamLatencySnapshot.CaptureToDecodeStartP95Ms}; capture_to_decode_start_max_ms={upstreamLatencySnapshot.CaptureToDecodeStartMaxMs}; worst_epoch_by_capture_to_decode_start={FormatFrameIdForLog(upstreamLatencySnapshot.WorstEpochByCaptureToDecodeStart)}; worst_epoch_capture_to_decode_start_avg_ms={upstreamLatencySnapshot.WorstEpochCaptureToDecodeStartAvgMs}; dominant_upstream_latency_stage={upstreamLatencySnapshot.DominantUpstreamLatencyStage}");

        var readyPathSnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperReadyPathSnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_ready_path_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; capture_to_first_fragment_observed_avg_ms={readyPathSnapshot.CaptureToFirstFragmentObservedAvgMs}; capture_to_first_fragment_observed_median_ms={readyPathSnapshot.CaptureToFirstFragmentObservedMedianMs}; capture_to_first_fragment_observed_p95_ms={readyPathSnapshot.CaptureToFirstFragmentObservedP95Ms}; capture_to_first_fragment_observed_max_ms={readyPathSnapshot.CaptureToFirstFragmentObservedMaxMs}; first_fragment_to_last_fragment_observed_avg_ms={readyPathSnapshot.FirstFragmentToLastFragmentObservedAvgMs}; first_fragment_to_last_fragment_observed_median_ms={readyPathSnapshot.FirstFragmentToLastFragmentObservedMedianMs}; first_fragment_to_last_fragment_observed_p95_ms={readyPathSnapshot.FirstFragmentToLastFragmentObservedP95Ms}; first_fragment_to_last_fragment_observed_max_ms={readyPathSnapshot.FirstFragmentToLastFragmentObservedMaxMs}; last_fragment_to_assembly_complete_avg_ms={readyPathSnapshot.LastFragmentToAssemblyCompleteAvgMs}; last_fragment_to_assembly_complete_median_ms={readyPathSnapshot.LastFragmentToAssemblyCompleteMedianMs}; last_fragment_to_assembly_complete_p95_ms={readyPathSnapshot.LastFragmentToAssemblyCompleteP95Ms}; last_fragment_to_assembly_complete_max_ms={readyPathSnapshot.LastFragmentToAssemblyCompleteMaxMs}; assembly_complete_to_frame_emitted_avg_ms={readyPathSnapshot.AssemblyCompleteToFrameEmittedAvgMs}; assembly_complete_to_frame_emitted_median_ms={readyPathSnapshot.AssemblyCompleteToFrameEmittedMedianMs}; assembly_complete_to_frame_emitted_p95_ms={readyPathSnapshot.AssemblyCompleteToFrameEmittedP95Ms}; assembly_complete_to_frame_emitted_max_ms={readyPathSnapshot.AssemblyCompleteToFrameEmittedMaxMs}; dominant_ready_path_stage={readyPathSnapshot.DominantReadyPathStage}");

        var receivePathSnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperReceivePathSnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_receive_path_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; capture_to_envelope_send_avg_ms={receivePathSnapshot.CaptureToEnvelopeSendAvgMs}; capture_to_envelope_send_median_ms={receivePathSnapshot.CaptureToEnvelopeSendMedianMs}; capture_to_envelope_send_p95_ms={receivePathSnapshot.CaptureToEnvelopeSendP95Ms}; capture_to_envelope_send_max_ms={receivePathSnapshot.CaptureToEnvelopeSendMaxMs}; envelope_send_to_bridge_ingress_avg_ms={receivePathSnapshot.EnvelopeSendToBridgeIngressAvgMs}; envelope_send_to_bridge_ingress_median_ms={receivePathSnapshot.EnvelopeSendToBridgeIngressMedianMs}; envelope_send_to_bridge_ingress_p95_ms={receivePathSnapshot.EnvelopeSendToBridgeIngressP95Ms}; envelope_send_to_bridge_ingress_max_ms={receivePathSnapshot.EnvelopeSendToBridgeIngressMaxMs}; bridge_ingress_to_envelope_parsed_avg_ms={receivePathSnapshot.BridgeIngressToEnvelopeParsedAvgMs}; bridge_ingress_to_envelope_parsed_median_ms={receivePathSnapshot.BridgeIngressToEnvelopeParsedMedianMs}; bridge_ingress_to_envelope_parsed_p95_ms={receivePathSnapshot.BridgeIngressToEnvelopeParsedP95Ms}; bridge_ingress_to_envelope_parsed_max_ms={receivePathSnapshot.BridgeIngressToEnvelopeParsedMaxMs}; envelope_parsed_to_secure_decrypt_avg_ms={receivePathSnapshot.EnvelopeParsedToSecureDecryptAvgMs}; envelope_parsed_to_secure_decrypt_median_ms={receivePathSnapshot.EnvelopeParsedToSecureDecryptMedianMs}; envelope_parsed_to_secure_decrypt_p95_ms={receivePathSnapshot.EnvelopeParsedToSecureDecryptP95Ms}; envelope_parsed_to_secure_decrypt_max_ms={receivePathSnapshot.EnvelopeParsedToSecureDecryptMaxMs}; secure_decrypt_to_fragment_deserialize_avg_ms={receivePathSnapshot.SecureDecryptToFragmentDeserializeAvgMs}; secure_decrypt_to_fragment_deserialize_median_ms={receivePathSnapshot.SecureDecryptToFragmentDeserializeMedianMs}; secure_decrypt_to_fragment_deserialize_p95_ms={receivePathSnapshot.SecureDecryptToFragmentDeserializeP95Ms}; secure_decrypt_to_fragment_deserialize_max_ms={receivePathSnapshot.SecureDecryptToFragmentDeserializeMaxMs}; fragment_deserialize_to_first_fragment_observed_avg_ms={receivePathSnapshot.FragmentDeserializeToFirstFragmentObservedAvgMs}; fragment_deserialize_to_first_fragment_observed_median_ms={receivePathSnapshot.FragmentDeserializeToFirstFragmentObservedMedianMs}; fragment_deserialize_to_first_fragment_observed_p95_ms={receivePathSnapshot.FragmentDeserializeToFirstFragmentObservedP95Ms}; fragment_deserialize_to_first_fragment_observed_max_ms={receivePathSnapshot.FragmentDeserializeToFirstFragmentObservedMaxMs}; dominant_receive_path_stage={receivePathSnapshot.DominantReceivePathStage}");

        var bridgeIngressSnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperBridgeIngressSnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_bridge_ingress_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; envelope_send_to_bridge_message_observed_avg_ms={bridgeIngressSnapshot.EnvelopeSendToBridgeMessageObservedAvgMs}; envelope_send_to_bridge_message_observed_median_ms={bridgeIngressSnapshot.EnvelopeSendToBridgeMessageObservedMedianMs}; envelope_send_to_bridge_message_observed_p95_ms={bridgeIngressSnapshot.EnvelopeSendToBridgeMessageObservedP95Ms}; envelope_send_to_bridge_message_observed_max_ms={bridgeIngressSnapshot.EnvelopeSendToBridgeMessageObservedMaxMs}; bridge_message_observed_to_binary_frame_decoded_avg_ms={bridgeIngressSnapshot.BridgeMessageObservedToBinaryFrameDecodedAvgMs}; bridge_message_observed_to_binary_frame_decoded_median_ms={bridgeIngressSnapshot.BridgeMessageObservedToBinaryFrameDecodedMedianMs}; bridge_message_observed_to_binary_frame_decoded_p95_ms={bridgeIngressSnapshot.BridgeMessageObservedToBinaryFrameDecodedP95Ms}; bridge_message_observed_to_binary_frame_decoded_max_ms={bridgeIngressSnapshot.BridgeMessageObservedToBinaryFrameDecodedMaxMs}; binary_frame_decoded_to_bridge_ingress_avg_ms={bridgeIngressSnapshot.BinaryFrameDecodedToBridgeIngressAvgMs}; binary_frame_decoded_to_bridge_ingress_median_ms={bridgeIngressSnapshot.BinaryFrameDecodedToBridgeIngressMedianMs}; binary_frame_decoded_to_bridge_ingress_p95_ms={bridgeIngressSnapshot.BinaryFrameDecodedToBridgeIngressP95Ms}; binary_frame_decoded_to_bridge_ingress_max_ms={bridgeIngressSnapshot.BinaryFrameDecodedToBridgeIngressMaxMs}; dominant_bridge_ingress_stage={bridgeIngressSnapshot.DominantBridgeIngressStage}");

        var nknReceiveSnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperNknReceiveSnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_nkn_receive_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; envelope_send_to_sdk_handle_msg_entered_avg_ms={nknReceiveSnapshot.EnvelopeSendToSdkHandleMsgEnteredAvgMs}; envelope_send_to_sdk_handle_msg_entered_median_ms={nknReceiveSnapshot.EnvelopeSendToSdkHandleMsgEnteredMedianMs}; envelope_send_to_sdk_handle_msg_entered_p95_ms={nknReceiveSnapshot.EnvelopeSendToSdkHandleMsgEnteredP95Ms}; envelope_send_to_sdk_handle_msg_entered_max_ms={nknReceiveSnapshot.EnvelopeSendToSdkHandleMsgEnteredMaxMs}; sdk_handle_msg_entered_to_client_message_dispatch_avg_ms={nknReceiveSnapshot.SdkHandleMsgEnteredToClientMessageDispatchAvgMs}; sdk_handle_msg_entered_to_client_message_dispatch_median_ms={nknReceiveSnapshot.SdkHandleMsgEnteredToClientMessageDispatchMedianMs}; sdk_handle_msg_entered_to_client_message_dispatch_p95_ms={nknReceiveSnapshot.SdkHandleMsgEnteredToClientMessageDispatchP95Ms}; sdk_handle_msg_entered_to_client_message_dispatch_max_ms={nknReceiveSnapshot.SdkHandleMsgEnteredToClientMessageDispatchMaxMs}; client_message_dispatch_to_multiclient_message_dispatch_avg_ms={nknReceiveSnapshot.ClientMessageDispatchToMultiClientMessageDispatchAvgMs}; client_message_dispatch_to_multiclient_message_dispatch_median_ms={nknReceiveSnapshot.ClientMessageDispatchToMultiClientMessageDispatchMedianMs}; client_message_dispatch_to_multiclient_message_dispatch_p95_ms={nknReceiveSnapshot.ClientMessageDispatchToMultiClientMessageDispatchP95Ms}; client_message_dispatch_to_multiclient_message_dispatch_max_ms={nknReceiveSnapshot.ClientMessageDispatchToMultiClientMessageDispatchMaxMs}; multiclient_message_dispatch_to_bridge_message_observed_avg_ms={nknReceiveSnapshot.MultiClientMessageDispatchToBridgeMessageObservedAvgMs}; multiclient_message_dispatch_to_bridge_message_observed_median_ms={nknReceiveSnapshot.MultiClientMessageDispatchToBridgeMessageObservedMedianMs}; multiclient_message_dispatch_to_bridge_message_observed_p95_ms={nknReceiveSnapshot.MultiClientMessageDispatchToBridgeMessageObservedP95Ms}; multiclient_message_dispatch_to_bridge_message_observed_max_ms={nknReceiveSnapshot.MultiClientMessageDispatchToBridgeMessageObservedMaxMs}; dominant_nkn_receive_stage={nknReceiveSnapshot.DominantNknReceiveStage}");

        var wsReceiveSnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperWsReceiveSnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_ws_receive_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; envelope_send_to_ws_receiver_write_entered_avg_ms={wsReceiveSnapshot.EnvelopeSendToWsReceiverWriteEnteredAvgMs}; envelope_send_to_ws_receiver_write_entered_median_ms={wsReceiveSnapshot.EnvelopeSendToWsReceiverWriteEnteredMedianMs}; envelope_send_to_ws_receiver_write_entered_p95_ms={wsReceiveSnapshot.EnvelopeSendToWsReceiverWriteEnteredP95Ms}; envelope_send_to_ws_receiver_write_entered_max_ms={wsReceiveSnapshot.EnvelopeSendToWsReceiverWriteEnteredMaxMs}; ws_receiver_write_entered_to_ws_message_emitted_avg_ms={wsReceiveSnapshot.WsReceiverWriteEnteredToWsMessageEmittedAvgMs}; ws_receiver_write_entered_to_ws_message_emitted_median_ms={wsReceiveSnapshot.WsReceiverWriteEnteredToWsMessageEmittedMedianMs}; ws_receiver_write_entered_to_ws_message_emitted_p95_ms={wsReceiveSnapshot.WsReceiverWriteEnteredToWsMessageEmittedP95Ms}; ws_receiver_write_entered_to_ws_message_emitted_max_ms={wsReceiveSnapshot.WsReceiverWriteEnteredToWsMessageEmittedMaxMs}; ws_message_emitted_to_sdk_handle_msg_entered_avg_ms={wsReceiveSnapshot.WsMessageEmittedToSdkHandleMsgEnteredAvgMs}; ws_message_emitted_to_sdk_handle_msg_entered_median_ms={wsReceiveSnapshot.WsMessageEmittedToSdkHandleMsgEnteredMedianMs}; ws_message_emitted_to_sdk_handle_msg_entered_p95_ms={wsReceiveSnapshot.WsMessageEmittedToSdkHandleMsgEnteredP95Ms}; ws_message_emitted_to_sdk_handle_msg_entered_max_ms={wsReceiveSnapshot.WsMessageEmittedToSdkHandleMsgEnteredMaxMs}; dominant_ws_receive_stage={wsReceiveSnapshot.DominantWsReceiveStage}");

        var socketReceiveSnapshot = ScreenShareFrameLossAttributionRegistry.GetHelperSocketReceiveSnapshot(snapshot.SessionId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_socket_receive_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; envelope_send_to_socket_data_event_emitted_avg_ms={socketReceiveSnapshot.EnvelopeSendToSocketDataEventEmittedAvgMs}; envelope_send_to_socket_data_event_emitted_median_ms={socketReceiveSnapshot.EnvelopeSendToSocketDataEventEmittedMedianMs}; envelope_send_to_socket_data_event_emitted_p95_ms={socketReceiveSnapshot.EnvelopeSendToSocketDataEventEmittedP95Ms}; envelope_send_to_socket_data_event_emitted_max_ms={socketReceiveSnapshot.EnvelopeSendToSocketDataEventEmittedMaxMs}; socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms={socketReceiveSnapshot.SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs}; socket_data_event_emitted_to_ws_receiver_write_entered_median_ms={socketReceiveSnapshot.SocketDataEventEmittedToWsReceiverWriteEnteredMedianMs}; socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms={socketReceiveSnapshot.SocketDataEventEmittedToWsReceiverWriteEnteredP95Ms}; socket_data_event_emitted_to_ws_receiver_write_entered_max_ms={socketReceiveSnapshot.SocketDataEventEmittedToWsReceiverWriteEnteredMaxMs}; dominant_socket_receive_stage={socketReceiveSnapshot.DominantSocketReceiveStage}");

        var totalGapCount = snapshot.EpochDiagnostics.Sum(static epoch => epoch.GapCount);
        var totalRecoveryKeyframeApplyCount = snapshot.EpochDiagnostics.Sum(static epoch => epoch.RecoveryKeyframeApplyCount);
        var totalResyncCount = snapshot.EpochDiagnostics.Sum(static epoch => epoch.ResyncCount);
        var currentEpochDiagnostics = snapshot.EpochDiagnostics
            .OrderByDescending(static epoch => epoch.StreamEpoch)
            .FirstOrDefault();
        var currentEpochActionableLateFragmentCount = currentEpochDiagnostics is null
            ? 0
            : ScreenShareFrameLossAttributionRegistry.GetActionableLateFragmentCount(currentEpochDiagnostics);
        var currentEpochLossClass = currentEpochDiagnostics is null
            ? "(none)"
            : ScreenShareConceptualModelFormatter.FormatLossClass(ScreenShareLossTaxonomyMapper.ClassifyEpoch(currentEpochDiagnostics));
        var currentEpochHealthy = currentEpochDiagnostics is not null &&
                                  currentEpochDiagnostics.GapCount == 0 &&
                                  currentEpochDiagnostics.ResyncCount == 0 &&
                                  currentEpochActionableLateFragmentCount == 0 &&
                                  viewerMetrics.SteadyVisibleProgressActive;
        var visibleApplyRatio = snapshot.FramesEmitted > 0
            ? snapshot.FramesApplied / (double)snapshot.FramesEmitted
            : 0d;
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_quality_summary; role={logRole}; trigger={trigger}; session_id={snapshot.SessionId}; frames_emitted={snapshot.FramesEmitted}; viewer_accepted_frames={snapshot.ViewerAcceptedFrames}; frames_decoded={snapshot.FramesDecoded}; frames_applied={snapshot.FramesApplied}; helper_session_phase={viewerMetrics.HelperSessionPhase}; helper_recovery_mechanism={viewerMetrics.HelperRecoveryMechanism}; dominant_loss_class={viewerMetrics.DominantLossClass}; baseline_established={(viewerMetrics.BaselineEstablished ? 1 : 0)}; steady_visible_progress_active={(viewerMetrics.SteadyVisibleProgressActive ? 1 : 0)}; visible_apply_ratio={visibleApplyRatio:F2}; avg_apply_interval_ms={viewerMetrics.AverageApplyIntervalMs:F1}; avg_capture_to_render_ms={viewerMetrics.AverageCaptureToRenderMs:F1}; cursor_delivery_mode={viewerMetrics.CursorDeliveryMode}; cursor_overlay_visible={(viewerMetrics.CursorOverlayVisible ? 1 : 0)}; cursor_overlay_updates_received_count={viewerMetrics.CursorOverlayUpdatesReceivedCount}; cursor_overlay_updates_applied_count={viewerMetrics.CursorOverlayUpdatesAppliedCount}; cursor_overlay_update_hz={viewerMetrics.CursorOverlayUpdateHz:F1}; cursor_overlay_last_age_ms={(viewerMetrics.CursorOverlayLastAgeMs >= 0 ? viewerMetrics.CursorOverlayLastAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; cursor_overlay_stale_count={viewerMetrics.CursorOverlayStaleCount}; cursor_overlay_last_status={(string.IsNullOrWhiteSpace(viewerMetrics.CursorOverlayLastStatus) ? "(none)" : viewerMetrics.CursorOverlayLastStatus)}; avg_decode_complete_to_visible_apply_ms={viewerMetrics.AverageDecodeCompleteToVisibleApplyMs:F1}; avg_ui_post_apply_ms={viewerMetrics.AverageUiPostToApplyMs:F1}; avg_visible_head_lag_frames={viewerMetrics.AverageVisibleHeadLagFrames:F1}; avg_stable_head_lag_frames={viewerMetrics.AverageStableHeadLagFrames:F1}; stale_frame_drop_visible_stable_count={viewerMetrics.StaleFrameDropVisibleStableCount}; stale_frame_drop_visible_stable_last_age_ms={(viewerMetrics.StaleFrameDropVisibleStableLastAgeMs >= 0 ? viewerMetrics.StaleFrameDropVisibleStableLastAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; ordinary_non_key_age_budget_bypass_count={viewerMetrics.OrdinaryNonKeyAgeBudgetBypassCount}; last_reserved_apply_hold_ms={viewerMetrics.LastReservedApplyHoldMs}; last_recovery_progress_corridor_hold_ms={viewerMetrics.LastRecoveryProgressCorridorHoldMs}; last_recovery_runway_abort_hold_ms={viewerMetrics.LastRecoveryRunwayAbortHoldMs}; last_recovery_progress_corridor_abort_reason={viewerMetrics.LastRecoveryProgressCorridorAbortReason}; gap_count={totalGapCount}; recovery_keyframe_apply_count={totalRecoveryKeyframeApplyCount}; resync_count={totalResyncCount}; current_stream_epoch={(currentEpochDiagnostics?.StreamEpoch.ToString(CultureInfo.InvariantCulture) ?? "(none)")}; current_epoch_gap_count={currentEpochDiagnostics?.GapCount ?? 0}; current_epoch_recovery_keyframe_apply_count={currentEpochDiagnostics?.RecoveryKeyframeApplyCount ?? 0}; current_epoch_resync_count={currentEpochDiagnostics?.ResyncCount ?? 0}; current_epoch_actionable_late_fragment_count={currentEpochActionableLateFragmentCount}; current_epoch_loss_class={currentEpochLossClass}; current_epoch_healthy={(currentEpochHealthy ? 1 : 0)}; cumulative_gap_count={totalGapCount}; cumulative_recovery_keyframe_apply_count={totalRecoveryKeyframeApplyCount}; cumulative_resync_count={totalResyncCount}; dominant_reassembler_root_cause={snapshot.DominantReassemblerRootCause}; dominant_helper_admission_reject_reason={dominantHelperAdmissionRejectReason}; recovery_wait_reject_before_runway_count={viewerMetrics.RecoveryWaitRejectBeforeRunwayCount}; recovery_runway_overflow_reject_count={viewerMetrics.RecoveryRunwayOverflowRejectCount}; suppressed_emit_during_recovery_wait_count={viewerMetrics.SuppressedEmitDuringRecoveryWaitCount}; stale_superseded_recovery_suppressed_count={viewerMetrics.StaleSupersededRecoverySuppressedCount}; soft_stale_cleanup_count={viewerMetrics.SoftStaleCleanupCount}; pre_candidate_gap_tail_emitted_to_viewer_count={viewerMetrics.PreCandidateGapTailEmittedToViewerCount}; recovery_candidate_present_count={viewerMetrics.RecoveryCandidatePresentCount}; visible_recovery_floor_frame_id={FormatFrameIdForLog(viewerMetrics.VisibleRecoveryFloorFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(viewerMetrics.StableVisibleHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(viewerMetrics.AppliedHeadFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(snapshot.OrderedEmitHeadFrameId)}; winning_recovery_frame_id={FormatFrameIdForLog(snapshot.WinningRecoveryFrameId)}; visible_head_frame_id={FormatFrameIdForLog(viewerMetrics.VisibleHeadFrameId)}; superseded_recovery_tail_cleanup_count={snapshot.SupersededRecoveryTailCleanupCount}; late_same_epoch_after_head_advanced_drop_count={snapshot.LateSameEpochAfterHeadAdvancedDropCount}; stale_runway_window_abort_count={snapshot.StaleRunwayWindowAbortCount}; runway_candidate_expired_after_head_advance_count={snapshot.RunwayCandidateExpiredAfterHeadAdvanceCount}; runway_followers_emitted_within_actionable_window_count={snapshot.RunwayFollowersEmittedWithinActionableWindowCount}; same_epoch_recovery_owner_suppressed_count={snapshot.RecoveryKeyframeSupersededOrReplacedCount}; recovery_owner_replaced_count={snapshot.RecoveryOwnerReplacedCount}; older_epoch_cleanup_after_epoch_advance_count={snapshot.OlderEpochCleanupAfterEpochAdvanceCount}; future_tail_quarantined_during_gap_count={viewerMetrics.FutureTailQuarantinedDuringGapCount}; future_tail_quarantined_after_gap_count={viewerMetrics.FutureTailQuarantinedAfterGapCount}; pre_candidate_gap_tail_rejected_count={viewerMetrics.PreCandidateGapTailRejectedCount}; late_fragment_after_applied_head_count={viewerMetrics.LateFragmentAfterAppliedHeadCount}; late_fragment_after_ordered_head_count={snapshot.LateFragmentAfterOrderedHeadCount}; late_fragment_after_stable_visible_head_count={viewerMetrics.LateFragmentAfterStableVisibleHeadCount}; late_fragment_after_visible_recovery_count={viewerMetrics.LateFragmentAfterVisibleRecoveryCount}; recovery_runway_contiguous_follower_buffer_count={viewerMetrics.RecoveryRunwayContiguousFollowerBufferCount}; recovery_runway_contiguous_follower_apply_count={viewerMetrics.RecoveryRunwayContiguousFollowerApplyCount}; recovery_runway_abort_count={viewerMetrics.RecoveryRunwayAbortCount}; recovery_follower_window_buffered_count={viewerMetrics.RecoveryFollowerWindowBufferedCount}; recovery_follower_window_applied_count={viewerMetrics.RecoveryFollowerWindowAppliedCount}; recovery_follower_window_trimmed_count={viewerMetrics.RecoveryFollowerWindowTrimmedCount}; protected_recovery_delivery_count={viewerMetrics.ProtectedRecoveryDeliveryCount}; recovery_progress_corridor_count={viewerMetrics.RecoveryProgressCorridorCount}; recovery_progress_corridor_success_count={viewerMetrics.RecoveryProgressCorridorSuccessCount}; recovery_progress_corridor_abort_count={viewerMetrics.RecoveryProgressCorridorAbortCount}; recovery_progress_corridor_applied_count={viewerMetrics.RecoveryProgressCorridorAppliedCount}; recovery_window_active={(viewerMetrics.RecoveryWindowActive ? 1 : 0)}; active_recovery_window_epoch={FormatFrameIdForLog(viewerMetrics.ActiveRecoveryWindowEpoch)}; active_recovery_window_recovery_frame_id={FormatFrameIdForLog(viewerMetrics.ActiveRecoveryWindowRecoveryFrameId)}; recovery_window_contiguous_follower_apply_count={viewerMetrics.RecoveryWindowContiguousFollowerApplyCount}; recovery_keyframe_pending_visible_apply_count={viewerMetrics.RecoveryKeyframePendingVisibleApplyCount}; startup_corridor_buffered_follower_count={viewerMetrics.StartupCorridorBufferedFollowerCount}; startup_corridor_release_count={viewerMetrics.StartupCorridorReleaseCount}; startup_corridor_abort_count={viewerMetrics.StartupCorridorAbortCount}; startup_corridor_abort_reason={viewerMetrics.StartupCorridorAbortReason}; post_recovery_visible_generation_reset_count={viewerMetrics.PostRecoveryVisibleGenerationResetCount}; post_recovery_stale_drop_bypass_count={viewerMetrics.PostRecoveryStaleDropBypassCount}; late_fragment_after_successful_recovery_count={snapshot.LateFragmentAfterSuccessfulRecoveryCount}; actionable_late_fragment_count={viewerMetrics.ActionableLateFragmentCount}");

        if (!ShouldLogHelperRemoteEpochDetails(trigger, snapshot, includeEpochDetails))
        {
            return;
        }

        foreach (var epoch in snapshot.EpochSnapshots)
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_helper_frame_loss_epoch; role={logRole}; session_id={snapshot.SessionId}; stream_epoch={epoch.StreamEpoch}; fragment_seen_frames={epoch.FragmentSeenFrames}; frames_assembled={epoch.FramesAssembled}; frames_ready={epoch.FramesReady}; frames_emitted={epoch.FramesEmitted}; viewer_accepted_frames={epoch.ViewerAcceptedFrames}; decode_enqueued_frames={epoch.DecodeEnqueuedFrames}; frames_decoded={epoch.FramesDecoded}; frames_applied={epoch.FramesApplied}; reassembler_stale_superseded_loss_count={epoch.ReassemblerStaleSupersededLossCount}; assembly_evicted_loss_count={epoch.AssemblyEvictedLossCount}; ready_frame_skipped_replaced_loss_count={epoch.ReadyFrameSkippedReplacedLossCount}; viewer_rejected_before_enqueue_count={epoch.ViewerRejectedBeforeEnqueueCount}; waiting_for_recovery_keyframe_reject_count={epoch.WaitingForRecoveryKeyframeRejectCount}; recovery_wait_reject_before_runway_count={epoch.WaitingForRecoveryKeyframeRejectCount}; recovery_runway_overflow_reject_count={epoch.RecoveryRunwayOverflowRejectCount}; suppressed_emit_during_recovery_wait_count={epoch.SuppressedEmitDuringRecoveryWaitCount}; blocked_by_reserved_recovery_frame_reject_count={epoch.BlockedByReservedRecoveryFrameRejectCount}; older_epoch_ignored_during_recovery_lock_count={epoch.OlderEpochIgnoredDuringRecoveryLockCount}; newer_epoch_non_key_ignored_during_lock_count={epoch.NewerEpochNonKeyIgnoredDuringLockCount}; deferred_post_recovery_candidate_replace_count={epoch.DeferredPostRecoveryCandidateReplaceCount}; decode_worker_dropped_before_decode_count={epoch.DecodeWorkerDroppedBeforeDecodeCount}; decode_queue_overflow_count={epoch.DecodeQueueOverflowCount}; decode_age_budget_count={epoch.DecodeAgeBudgetCount}; decode_generation_changed_count={epoch.DecodeGenerationChangedCount}; decode_stopped_count={epoch.DecodeStoppedCount}; decoded_apply_queue_overflow_count={epoch.DecodedApplyQueueOverflowCount}; decoded_frame_replaced_before_apply_count={epoch.DecodedFrameReplacedBeforeApplyCount}; decoded_stale_after_recovery_count={epoch.DecodedStaleAfterRecoveryCount}; decoded_blocked_by_reserved_recovery_frame_count={epoch.DecodedBlockedByReservedRecoveryFrameCount}; decoded_newer_epoch_ignored_during_lock_count={epoch.DecodedNewerEpochIgnoredDuringLockCount}; dropped_waiting_for_recovery_keyframe_count={epoch.DroppedWaitingForRecoveryKeyframeCount}; decode_failed_loss_count={epoch.DecodeFailedLossCount}; stale_dropped_after_decode_count={epoch.StaleDroppedAfterDecodeCount}; gap_non_key_pruned_count={epoch.GapNonKeyPrunedCount}; future_tail_quarantined_during_gap_count={epoch.FutureTailQuarantinedDuringGapCount}; future_tail_quarantined_after_gap_count={epoch.FutureTailQuarantinedAfterGapCount}; pre_candidate_gap_tail_rejected_count={epoch.PreCandidateGapTailRejectedCount}; recovery_candidate_present_count={epoch.RecoveryCandidatePresentCount}; visible_recovery_floor_frame_id={FormatFrameIdForLog(epoch.VisibleRecoveryFloorFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(epoch.StableVisibleHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(epoch.AppliedHeadFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(epoch.OrderedEmitHeadFrameId)}; winning_recovery_frame_id={FormatFrameIdForLog(epoch.WinningRecoveryFrameId)}; superseded_recovery_tail_cleanup_count={epoch.SupersededRecoveryTailCleanupCount}; late_same_epoch_after_head_advanced_drop_count={epoch.LateSameEpochAfterHeadAdvancedDropCount}; stale_runway_window_abort_count={epoch.StaleRunwayWindowAbortCount}; runway_candidate_expired_after_head_advance_count={epoch.RunwayCandidateExpiredAfterHeadAdvanceCount}; runway_followers_emitted_within_actionable_window_count={epoch.RunwayFollowersEmittedWithinActionableWindowCount}; recovery_owner_replaced_count={epoch.RecoveryOwnerReplacedCount}; older_epoch_cleanup_after_epoch_advance_count={epoch.OlderEpochCleanupAfterEpochAdvanceCount}; late_fragment_after_applied_head_count={epoch.LateFragmentAfterAppliedHeadCount}; late_fragment_after_ordered_head_count={epoch.LateFragmentAfterOrderedHeadCount}; late_fragment_after_stable_visible_head_count={epoch.LateFragmentAfterStableVisibleHeadCount}; late_fragment_after_visible_recovery_count={epoch.LateFragmentAfterVisibleRecoveryCount}; dominant_helper_admission_reject_reason={epoch.DominantHelperAdmissionRejectReason}; unattributed_loss_count={epoch.UnattributedLossCount}; last_applied_frame_id={FormatFrameIdForLog(epoch.LastAppliedFrameId)}; last_clean_frame_id={FormatFrameIdForLog(epoch.LastCleanFrameId)}; recent_losses={ScreenShareFrameLossAttributionRegistry.FormatRecentLosses(epoch.RecentLosses)}");
        }

        foreach (var epochDiagnostics in snapshot.EpochDiagnostics)
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_helper_epoch_timeline; role={logRole}; session_id={snapshot.SessionId}; stream_epoch={epochDiagnostics.StreamEpoch}; gap_count={epochDiagnostics.GapCount}; recovery_keyframe_apply_count={epochDiagnostics.RecoveryKeyframeApplyCount}; resync_count={epochDiagnostics.ResyncCount}; frames_applied_since_last_gap={epochDiagnostics.FramesAppliedSinceLastGap}; time_to_first_apply_ms={epochDiagnostics.TimeToFirstApplyMs}; time_from_gap_to_keyframe_request_ms={epochDiagnostics.TimeFromGapToKeyframeRequestMs}; time_from_gap_to_recovery_keyframe_applied_ms={epochDiagnostics.TimeFromGapToRecoveryKeyframeAppliedMs}; time_in_recovery_lock_ms={epochDiagnostics.TimeInRecoveryLockMs}; timeline_events={FormatEpochTimelineEvents(epochDiagnostics.TimelineEvents)}");
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_helper_reassembler_root_cause_summary; role={logRole}; session_id={snapshot.SessionId}; stream_epoch={epochDiagnostics.StreamEpoch}; recovery_candidate_present_count={epochDiagnostics.RecoveryCandidatePresentCount}; visible_recovery_floor_frame_id={FormatFrameIdForLog(epochDiagnostics.VisibleRecoveryFloorFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(epochDiagnostics.StableVisibleHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(epochDiagnostics.AppliedHeadFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(epochDiagnostics.OrderedEmitHeadFrameId)}; winning_recovery_frame_id={FormatFrameIdForLog(epochDiagnostics.WinningRecoveryFrameId)}; fragment_gap_before_assembly_count={epochDiagnostics.FragmentGapBeforeAssemblyCount}; late_fragment_after_head_advanced_count={epochDiagnostics.LateFragmentAfterHeadAdvancedCount}; late_fragment_after_applied_head_count={epochDiagnostics.LateFragmentAfterAppliedHeadCount}; late_fragment_after_ordered_head_count={epochDiagnostics.LateFragmentAfterOrderedHeadCount}; superseded_recovery_tail_cleanup_count={epochDiagnostics.SupersededRecoveryTailCleanupCount}; recovery_owner_replaced_count={epochDiagnostics.RecoveryOwnerReplacedCount}; older_epoch_cleanup_after_epoch_advance_count={epochDiagnostics.OlderEpochCleanupAfterEpochAdvanceCount}; late_fragment_after_stable_visible_head_count={epochDiagnostics.LateFragmentAfterStableVisibleHeadCount}; late_fragment_after_visible_recovery_count={epochDiagnostics.LateFragmentAfterVisibleRecoveryCount}; late_fragment_after_successful_recovery_count={epochDiagnostics.LateFragmentAfterSuccessfulRecoveryCount}; actionable_late_fragment_count={ScreenShareFrameLossAttributionRegistry.GetActionableLateFragmentCount(epochDiagnostics)}; suppressed_emit_during_recovery_wait_count={epochDiagnostics.SuppressedEmitDuringRecoveryWaitCount}; future_tail_pruned_while_gap_active_count={epochDiagnostics.FutureTailPrunedWhileGapActiveCount}; protected_head_missing_budget_pressure_count={epochDiagnostics.ProtectedHeadMissingBudgetPressureCount}; recovery_keyframe_superseded_or_replaced_count={epochDiagnostics.RecoveryKeyframeSupersededOrReplacedCount}; ordered_emit_blocked_then_resynced_count={epochDiagnostics.OrderedEmitBlockedThenResyncedCount}; dominant_root_cause={epochDiagnostics.DominantReassemblerRootCause}; top_loss_bursts={FormatLossBursts(epochDiagnostics.TopLossBursts)}");
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_helper_recovery_epoch_investigation; role={logRole}; session_id={snapshot.SessionId}; stream_epoch={epochDiagnostics.StreamEpoch}; gap_count={epochDiagnostics.GapCount}; resync_count={epochDiagnostics.ResyncCount}; recovery_candidate_present_count={epochDiagnostics.RecoveryCandidatePresentCount}; winning_recovery_frame_id={FormatFrameIdForLog(epochDiagnostics.WinningRecoveryFrameId)}; visible_recovery_floor_frame_id={FormatFrameIdForLog(epochDiagnostics.VisibleRecoveryFloorFrameId)}; visible_head_frame_id={FormatFrameIdForLog(epochDiagnostics.VisibleHeadFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(epochDiagnostics.StableVisibleHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(epochDiagnostics.AppliedHeadFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(epochDiagnostics.OrderedEmitHeadFrameId)}; recovery_owner_replaced_count={epochDiagnostics.RecoveryOwnerReplacedCount}; older_epoch_cleanup_after_epoch_advance_count={epochDiagnostics.OlderEpochCleanupAfterEpochAdvanceCount}; actionable_late_fragment_count={ScreenShareFrameLossAttributionRegistry.GetActionableLateFragmentCount(epochDiagnostics)}; dominant_root_cause={epochDiagnostics.DominantReassemblerRootCause}; timeline_events={FormatEpochTimelineEvents(epochDiagnostics.TimelineEvents)}");
        }
    }

    private static string NormalizeDecodeWorkerLossReason(string reason)
    {
        return reason switch
        {
            "queue_overflow" => "decode_queue_overflow",
            "age_budget" => "decode_age_budget",
            "generation_changed" => "decode_generation_changed",
            "stopped_or_disposed" => "decode_stopped",
            "decoded_apply_queue_overflow" => "decoded_apply_queue_overflow",
            "decoded_frame_replaced_before_apply" => "decoded_frame_replaced_before_apply",
            "decoded_blocked_by_reserved_recovery_frame" => "decoded_blocked_by_reserved_recovery_frame",
            "decoded_newer_epoch_ignored_during_lock" => "decoded_newer_epoch_ignored_during_lock",
            _ => string.IsNullOrWhiteSpace(reason) ? "decode_unknown" : reason.Trim(),
        };
    }

    private bool TryDeferHelperRemotePostRecoveryCandidate(
        string sessionId,
        string encoding,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        bool assumeOwnership,
        ref ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
    {
        if (!IsHelperRemoteH264(encoding) ||
            isKeyFrame ||
            streamEpoch <= 0 ||
            frameId < 0)
        {
            return false;
        }

        if (!TryResolveHelperRemotePostRecoveryFollowerWindow(
                streamEpoch,
                out var recoveryFrameId,
                out var lastContiguousFrameId,
                out var reservedApplyPending))
        {
            return false;
        }

        if (frameId <= lastContiguousFrameId)
        {
            ObserveViewerRejectedBeforeEnqueue(
                sessionId,
                encoding,
                streamEpoch,
                frameId,
                isKeyFrame,
                "late_same_epoch_after_head_advanced_drop");
            return true;
        }

        var maximumActionableFrameId = recoveryFrameId + HelperRemotePostRecoveryFollowerWindowSize;
        if (frameId > maximumActionableFrameId)
        {
            EnterHelperRemoteH264ReferenceTaint(streamEpoch, "recovery_runway_overflow");
            ObserveViewerRejectedBeforeEnqueue(
                sessionId,
                encoding,
                streamEpoch,
                frameId,
                isKeyFrame,
                "recovery_runway_overflow");
            return true;
        }

        var expectedNextFrameId = lastContiguousFrameId + 1;
        if (!reservedApplyPending &&
            frameId == expectedNextFrameId)
        {
            if (recoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal &&
                helperRemoteSessionController.ReferenceTaintState.Active &&
                helperRemoteSessionController.ReferenceTaintState.StreamEpoch == streamEpoch)
            {
                recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.ProtectedFollower;
            }

            MarkReservedApplyPending(streamEpoch, frameId);
            return false;
        }

        BufferDeferredPostRecoveryCandidate(
            sessionId,
            encoding,
            assumeOwnership ? encodedFrameBytes : encodedFrameBytes.ToArray(),
            capturedTsUtcMs,
            streamEpoch,
            frameId,
            isKeyFrame,
            recoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal
                ? ScreenShareRecoveryDeliveryClass.ProtectedFollower
                : recoveryDeliveryClass,
            reservedApplyPending);
        return true;
    }

    private bool TryResolveHelperRemotePostRecoveryFollowerWindow(
        long streamEpoch,
        out long recoveryFrameId,
        out long lastContiguousFrameId,
        out bool reservedApplyPending)
    {
        recoveryFrameId = -1;
        lastContiguousFrameId = -1;
        reservedApplyPending =
            helperRemoteFollowerState.ReservedApplyActive &&
            helperRemoteFollowerState.ReservedApplyStreamEpoch == streamEpoch;

        if (helperRemoteFollowerState.RecoveryProgressCorridorActive &&
            helperRemoteFollowerState.RecoveryProgressCorridorEpoch == streamEpoch &&
            helperRemoteFollowerState.RecoveryProgressCorridorRecoveryFrameId >= 0)
        {
            recoveryFrameId = helperRemoteFollowerState.RecoveryProgressCorridorRecoveryFrameId;
            lastContiguousFrameId = Math.Max(
                helperRemoteFollowerState.RecoveryProgressCorridorLastFrameId,
                recoveryFrameId);

            return true;
        }

        if (reservedApplyPending && helperRemoteFollowerState.ReservedApplyFrameId >= 0)
        {
            recoveryFrameId = helperRemoteFollowerState.ReservedApplyFrameId;
            lastContiguousFrameId = helperRemoteFollowerState.ReservedApplyFrameId;
            return true;
        }

        return false;
    }

    private void BufferDeferredPostRecoveryCandidate(
        string sessionId,
        string encoding,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass,
        bool reservedApplyPending)
    {
        DeferredHelperRemoteFrameCandidate? replacedCandidate = null;
        var buffered = false;
        lock (helperRemoteFollowerState.DeferredFollowerGate)
        {
            var candidate = new DeferredHelperRemoteFrameCandidate(
                sessionId,
                encoding,
                encodedFrameBytes,
                capturedTsUtcMs,
                streamEpoch,
                frameId,
                isKeyFrame,
                ++helperRemoteFollowerState.DeferredPostRecoveryCandidateSequence,
                recoveryDeliveryClass);
            if (helperRemoteFollowerState.DeferredPostRecoveryCandidates.TryGetValue(frameId, out var existing) &&
                !IsBetterDeferredPostRecoveryCandidate(candidate, existing))
            {
                replacedCandidate = candidate;
            }
            else
            {
                if (helperRemoteFollowerState.DeferredPostRecoveryCandidates.TryGetValue(frameId, out existing))
                {
                    replacedCandidate = existing;
                }

                helperRemoteFollowerState.DeferredPostRecoveryCandidates[frameId] = candidate;
                buffered = true;
            }
        }

        if (replacedCandidate is not null)
        {
            ObserveViewerRejectedBeforeEnqueue(
                replacedCandidate.Value.SessionId,
                replacedCandidate.Value.Encoding,
                replacedCandidate.Value.StreamEpoch,
                replacedCandidate.Value.FrameId,
                replacedCandidate.Value.IsKeyFrame,
                "deferred_post_recovery_candidate_replaced");
        }

        if (!buffered)
        {
            return;
        }

        Interlocked.Increment(ref recoveryFollowerWindowBufferedCount);
    }

    private void PromoteHelperRemoteReferenceTaintFollowerIfEligible(
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        ref ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
    {
        if (recoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal ||
            isKeyFrame ||
            streamEpoch <= 0 ||
            frameId < 0 ||
            !helperRemoteSessionController.ReferenceTaintState.Active)
        {
            return;
        }

        var taintState = helperRemoteSessionController.ReferenceTaintState;
        if (taintState.StreamEpoch > 0 && taintState.StreamEpoch != streamEpoch)
        {
            return;
        }

        if (taintState.TrustedRecoveryOwnerFrameId < 0 ||
            !TryResolveHelperRemotePostRecoveryFollowerWindow(
                streamEpoch,
                out var recoveryFrameId,
                out _,
                out _))
        {
            return;
        }

        if (recoveryFrameId != taintState.TrustedRecoveryOwnerFrameId)
        {
            return;
        }

        recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.ProtectedFollower;
    }

    private string? TryRejectHelperRemoteFrameBeforeDecode(
        string sessionId,
        string encoding,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
    {
        TryReleasePendingHelperRemoteH264ReferenceQuarantine("pre_decode");
        var rejectionReason = helperRemoteSessionController.TryRejectFrameBeforeDecode(
            sessionId,
            encoding,
            streamEpoch,
            frameId,
            isKeyFrame,
            recoveryDeliveryClass);
        if (string.Equals(rejectionReason, "h264_reference_taint_waiting_for_recovery_keyframe", StringComparison.Ordinal) ||
            (string.Equals(rejectionReason, "waiting_for_recovery_keyframe", StringComparison.Ordinal) &&
             helperRemoteSessionController.ReferenceTaintState.Active))
        {
            Interlocked.Increment(ref h264ReferenceTaintDroppedNonKeyCount);
        }

        return rejectionReason;
    }

    private string? ResolveHelperRemotePreDecodeRejectionReason(string? sessionId, long streamEpoch, long frameId, bool isKeyFrame, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
    {
        if (recoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal)
        {
            return null;
        }

        if (helperRemoteSessionController.ReferenceTaintState.Active)
        {
            var taintEpoch = helperRemoteSessionController.ReferenceTaintState.StreamEpoch;
            if (taintEpoch > 0 && streamEpoch < taintEpoch)
            {
                return "older_epoch_ignored_during_recovery_lock";
            }

            if (!isKeyFrame)
            {
                if (taintEpoch > 0 && streamEpoch > taintEpoch)
                {
                    return "newer_epoch_non_key_ignored_during_lock";
                }

                if (helperRemoteRecoveryState.RecoveryActive &&
                    streamEpoch == helperRemoteRecoveryState.RecoveryStreamEpoch)
                {
                    return "waiting_for_recovery_keyframe";
                }

                return "h264_reference_taint_waiting_for_recovery_keyframe";
            }
        }

        if (!helperRemoteRecoveryState.RecoveryActive)
        {
            return null;
        }

        if (streamEpoch < helperRemoteRecoveryState.RecoveryStreamEpoch)
        {
            return "older_epoch_ignored_during_recovery_lock";
        }

        if (!isKeyFrame)
        {
            if (streamEpoch > helperRemoteRecoveryState.RecoveryStreamEpoch)
            {
                return "newer_epoch_non_key_ignored_during_lock";
            }

            return "waiting_for_recovery_keyframe";
        }

        return null;
    }

    private void EnterHelperRemoteH264ReferenceTaint(long streamEpoch, string reason)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        ObserveHelperRemoteReferenceQuarantineLoss(streamEpoch, reason);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseEpoch, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseFrameId, -1);
        Interlocked.Exchange(ref h264ReferenceQuarantineReleaseDueUtcMs, -1);
        if (!helperRemoteSessionController.EnterReferenceTaint(
                streamEpoch,
                reason,
                helperRemoteRecoveryState.LastCleanFrameId))
        {
            return;
        }

        Interlocked.Increment(ref h264ReferenceTaintEnterCount);
        Interlocked.Increment(ref generation);
        decodeWorker.ClearPending();
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_h264_reference_taint_entered; role={logRole}; stream_epoch={streamEpoch}; reason={reason}; last_clean_frame_id={FormatFrameIdForLog(helperRemoteRecoveryState.LastCleanFrameId)}");
    }

    private void ReleaseHelperRemoteH264ReferenceTaint(long streamEpoch, long lastContiguousFrameId)
    {
        if (HasUnresolvedHelperRemoteReferenceTaintBlocker(streamEpoch, lastContiguousFrameId, out var blockerReason))
        {
            RecordHelperRemoteReferenceQuarantineReleaseBlocked(streamEpoch, lastContiguousFrameId, blockerReason);
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_helper_h264_reference_taint_release_blocked; role={logRole}; stream_epoch={streamEpoch}; last_contiguous_frame_id={FormatFrameIdForLog(lastContiguousFrameId)}; blocker={blockerReason}");
            return;
        }

        if (TryGetHelperRemoteReferenceQuarantineQuietBlocker(streamEpoch, out blockerReason, out var releaseDueUtcMs))
        {
            RecordHelperRemoteReferenceQuarantineReleaseBlocked(streamEpoch, lastContiguousFrameId, blockerReason);
            Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseEpoch, streamEpoch);
            Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseFrameId, lastContiguousFrameId);
            Interlocked.Exchange(ref h264ReferenceQuarantineReleaseDueUtcMs, releaseDueUtcMs);
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_helper_h264_reference_quarantine_release_deferred; role={logRole}; stream_epoch={streamEpoch}; last_contiguous_frame_id={FormatFrameIdForLog(lastContiguousFrameId)}; blocker={blockerReason}; release_due_utc_ms={releaseDueUtcMs}");
            return;
        }

        if (!helperRemoteSessionController.ReleaseReferenceTaintAfterCorridorSuccess(streamEpoch, lastContiguousFrameId))
        {
            return;
        }

        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseEpoch, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseFrameId, -1);
        Interlocked.Exchange(ref h264ReferenceQuarantineReleaseDueUtcMs, -1);
        Interlocked.Increment(ref h264ReferenceTaintReleaseCount);
        StartHelperRemotePostQuarantineSettle(streamEpoch, lastContiguousFrameId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_h264_reference_taint_released; role={logRole}; stream_epoch={streamEpoch}; last_contiguous_frame_id={FormatFrameIdForLog(lastContiguousFrameId)}");
    }

    private void ObserveHelperRemoteReferenceQuarantineLoss(long streamEpoch, string reason)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        Interlocked.Exchange(ref h264ReferenceQuarantineLastLossEpoch, streamEpoch);
        Interlocked.Exchange(ref h264ReferenceQuarantineLastLossUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        h264ReferenceQuarantineLastLossReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
    }

    private void RecordHelperRemoteReferenceQuarantineReleaseBlocked(long streamEpoch, long lastContiguousFrameId, string blockerReason)
    {
        Interlocked.Increment(ref h264ReferenceQuarantineReleaseBlockedCount);
        h264ReferenceQuarantineLastBlocker = string.IsNullOrWhiteSpace(blockerReason)
            ? "unknown"
            : blockerReason.Trim();
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseEpoch, streamEpoch);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseFrameId, lastContiguousFrameId);
    }

    private bool TryGetHelperRemoteReferenceQuarantineQuietBlocker(long streamEpoch, out string blockerReason, out long releaseDueUtcMs)
    {
        blockerReason = "none";
        releaseDueUtcMs = -1;
        var lastLossEpoch = Interlocked.Read(ref h264ReferenceQuarantineLastLossEpoch);
        var lastLossUtcMs = Interlocked.Read(ref h264ReferenceQuarantineLastLossUtcMs);
        if (streamEpoch <= 0 ||
            lastLossEpoch != streamEpoch ||
            lastLossUtcMs < 0)
        {
            return false;
        }

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        releaseDueUtcMs = lastLossUtcMs + HelperRemoteH264ReferenceQuarantineQuietWindowMs;
        if (nowUtcMs >= releaseDueUtcMs)
        {
            return false;
        }

        var reason = string.IsNullOrWhiteSpace(h264ReferenceQuarantineLastLossReason)
            ? "recent_reference_loss"
            : h264ReferenceQuarantineLastLossReason.Trim();
        blockerReason = "quiet_window_recent_" + reason;
        return true;
    }

    private bool TryReleasePendingHelperRemoteH264ReferenceQuarantine(string trigger)
    {
        var streamEpoch = Interlocked.Read(ref h264ReferenceQuarantinePendingReleaseEpoch);
        var lastContiguousFrameId = Interlocked.Read(ref h264ReferenceQuarantinePendingReleaseFrameId);
        var dueUtcMs = Interlocked.Read(ref h264ReferenceQuarantineReleaseDueUtcMs);
        if (streamEpoch <= 0 ||
            lastContiguousFrameId < 0 ||
            dueUtcMs < 0 ||
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < dueUtcMs ||
            !helperRemoteSessionController.ReferenceTaintState.Active)
        {
            return false;
        }

        if (HasUnresolvedHelperRemoteReferenceTaintBlocker(streamEpoch, lastContiguousFrameId, out var blockerReason) ||
            TryGetHelperRemoteReferenceQuarantineQuietBlocker(streamEpoch, out blockerReason, out _))
        {
            h264ReferenceQuarantineLastBlocker = blockerReason;
            return false;
        }

        if (!helperRemoteSessionController.ReleaseReferenceTaintAfterCorridorSuccess(streamEpoch, lastContiguousFrameId))
        {
            return false;
        }

        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseEpoch, 0);
        Interlocked.Exchange(ref h264ReferenceQuarantinePendingReleaseFrameId, -1);
        Interlocked.Exchange(ref h264ReferenceQuarantineReleaseDueUtcMs, -1);
        Interlocked.Increment(ref h264ReferenceTaintReleaseCount);
        Interlocked.Increment(ref h264ReferenceQuarantineQuietReleaseCount);
        h264ReferenceQuarantineLastBlocker = "none";
        StartHelperRemotePostQuarantineSettle(streamEpoch, lastContiguousFrameId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_h264_reference_quarantine_quiet_released; role={logRole}; trigger={trigger}; stream_epoch={streamEpoch}; last_contiguous_frame_id={FormatFrameIdForLog(lastContiguousFrameId)}");
        return true;
    }

    private void StartHelperRemotePostQuarantineSettle(long streamEpoch, long lastContiguousFrameId)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleEpoch, streamEpoch);
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleStartedUtcMs, nowUtcMs);
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleUntilUtcMs, nowUtcMs + HelperRemotePostQuarantineSettleWindowMs);
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleLastContiguousFrameId, lastContiguousFrameId);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_h264_reference_post_quarantine_settle_started; role={logRole}; stream_epoch={streamEpoch}; last_contiguous_frame_id={FormatFrameIdForLog(lastContiguousFrameId)}; window_ms={HelperRemotePostQuarantineSettleWindowMs}");
    }

    private void ClearHelperRemotePostQuarantineSettle()
    {
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleEpoch, 0);
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleStartedUtcMs, -1);
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleUntilUtcMs, -1);
        Interlocked.Exchange(ref helperRemotePostQuarantineSettleLastContiguousFrameId, -1);
    }

    private bool HasUnresolvedHelperRemoteReferenceTaintBlocker(long streamEpoch, long lastContiguousFrameId, out string blockerReason)
    {
        blockerReason = "none";
        if (streamEpoch <= 0)
        {
            return false;
        }

        if (helperRemoteRecoveryState.RecoveryActive &&
            helperRemoteRecoveryState.RecoveryStreamEpoch == streamEpoch)
        {
            blockerReason = "active_recovery";
            return true;
        }

        if (helperRemoteFollowerState.RecoveryProgressCorridorActive &&
            helperRemoteFollowerState.RecoveryProgressCorridorEpoch == streamEpoch)
        {
            blockerReason = "recovery_corridor_active";
            return true;
        }

        if (helperRemoteFollowerState.ExpiredRecoveryRunwayActive &&
            helperRemoteFollowerState.ExpiredRecoveryRunwayEpoch == streamEpoch)
        {
            blockerReason = "expired_recovery_runway";
            return true;
        }

        if (helperRemoteFollowerState.PendingRecoveryRunwayAbortActive &&
            helperRemoteFollowerState.PendingRecoveryRunwayAbortEpoch == streamEpoch)
        {
            blockerReason = string.IsNullOrWhiteSpace(helperRemoteFollowerState.PendingRecoveryRunwayAbortReason)
                ? "pending_recovery_runway_abort"
                : helperRemoteFollowerState.PendingRecoveryRunwayAbortReason.Trim();
            return true;
        }

        lock (helperRemoteFollowerState.DeferredFollowerGate)
        {
            if (helperRemoteFollowerState.DeferredPostRecoveryCandidates.Values.Any(candidate =>
                    candidate.StreamEpoch == streamEpoch &&
                    candidate.FrameId > lastContiguousFrameId))
            {
                blockerReason = "deferred_post_recovery_candidate";
                return true;
            }
        }

        var resolvedFloor = Math.Max(
            GetHelperRemoteReferenceProofFloor(streamEpoch),
            lastContiguousFrameId);
        if (TryResolveHelperRemoteActionableReferenceLossReason(streamEpoch, resolvedFloor, out var lossReason))
        {
            blockerReason = lossReason;
            return true;
        }

        return false;
    }

    private static bool ShouldRequestRecoveryKeyframeForContinuityLoss(string reason)
    {
        return !string.IsNullOrWhiteSpace(reason) &&
               !string.Equals(reason, "stale_frame_superseded", StringComparison.Ordinal);
    }

    private bool ShouldEnterHelperRemoteReferenceTaintForViewerRejection(long streamEpoch, long frameId, bool isKeyFrame, string reason)
    {
        if (streamEpoch <= 0 || frameId < 0 || isKeyFrame)
        {
            return false;
        }

        if (helperRemoteSessionController.ReferenceTaintState.Active &&
            helperRemoteSessionController.ReferenceTaintState.StreamEpoch == streamEpoch &&
            string.Equals(reason, "waiting_for_recovery_keyframe", StringComparison.Ordinal))
        {
            return false;
        }

        return IsActionableReferenceTaintLossReason(reason);
    }

    private bool ShouldEnterHelperRemoteReferenceTaintForSoftCleanup(long streamEpoch, string reason)
    {
        return !string.Equals(reason, "stale_frame_superseded", StringComparison.Ordinal) ||
               !HasProvenHeadFloorForEpoch(streamEpoch);
    }

    private bool TryResolveHelperRemoteActionableReferenceLossReason(long streamEpoch, out string reason)
        => TryResolveHelperRemoteActionableReferenceLossReason(
            streamEpoch,
            GetHelperRemoteReferenceProofFloor(streamEpoch),
            out reason);

    private bool TryResolveHelperRemoteActionableReferenceLossReason(long streamEpoch, long resolvedFrameFloor, out string reason)
    {
        reason = string.Empty;
        if (streamEpoch <= 0 || string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            return false;
        }

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(helperRemoteRecoveryState.SessionId);
        foreach (var loss in snapshot.RecentLosses.Reverse())
        {
            if (loss.StreamEpoch != streamEpoch ||
                loss.FrameId < 0 ||
                loss.FrameId <= resolvedFrameFloor)
            {
                continue;
            }

            if (IsActionableReferenceTaintLoss(loss, resolvedFrameFloor))
            {
                reason = NormalizeReferenceTaintReason(loss.Reason);
                return true;
            }
        }

        return false;
    }

    private bool IsActionableReferenceTaintLoss(ScreenShareFrameLossBreadcrumb loss, long resolvedFrameFloor)
    {
        var reason = NormalizeReferenceTaintReason(loss.Reason);
        if (string.Equals(reason, "late_fragment_after_ordered_head", StringComparison.Ordinal))
        {
            return loss.FrameId > resolvedFrameFloor;
        }

        return IsActionableReferenceTaintLossReason(reason);
    }

    private static string NormalizeReferenceTaintReason(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();

    private static bool IsActionableReferenceTaintLossReason(string reason)
    {
        return NormalizeReferenceTaintReason(reason) switch
        {
            "assembly_incomplete" => true,
            "assembly_mismatch" => true,
            "assembly_oversize" => true,
            "fragment_oversize" => true,
            "ready_frame_skipped_replaced" => true,
            "buffer_budget_pruned" => true,
            "gap_non_key_pruned" => true,
            "future_tail_quarantined_during_gap" => true,
            "future_tail_quarantined_after_gap" => true,
            "pre_candidate_gap_tail_rejected" => true,
            "recovery_keyframe_buffered_tail_rejected" => true,
            "recovery_follower_window_trimmed" => true,
            "recovery_runway_overflow" => true,
            "waiting_for_recovery_keyframe" => true,
            "decode_drop_before_decode" => true,
            "decode_queue_overflow" => true,
            "decode_age_budget" => true,
            _ => false,
        };
    }

    private void ActivateHelperRemoteRecoveryForStaleVisibleStablePFrame(EncodedFrameDecodeRequest request)
    {
        if (!IsHelperRemoteH264(request.Encoding) ||
            request.IsKeyFrame ||
            request.RecoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal)
        {
            return;
        }

        Interlocked.Increment(ref staleNormalNonKeyVisibleSuppressCount);
        var wasTaintedForEpoch =
            helperRemoteSessionController.ReferenceTaintState.Active &&
            helperRemoteSessionController.ReferenceTaintState.StreamEpoch == request.StreamEpoch;
        ActivateHelperRemoteRecovery(
            "visible_stable_stale_p_frame_drop",
            request.StreamEpoch,
            currentEpochNeedMoreInputCount: Math.Max(0, Interlocked.Read(ref needMoreInputCount)),
            shouldRequestRecoveryKeyframe: true,
            receivedFrameId: request.FrameId,
            lastCleanFrameId: helperRemoteRecoveryState.LastCleanFrameId);
        if (!wasTaintedForEpoch &&
            helperRemoteSessionController.ReferenceTaintState.Active &&
            helperRemoteSessionController.ReferenceTaintState.StreamEpoch == request.StreamEpoch)
        {
            Interlocked.Increment(ref h264ReferenceTaintStaleVisibleStableEnterCount);
        }
    }

    private long GetHelperRemoteReferenceProofFloor(long streamEpoch)
    {
        if (streamEpoch <= 0 || string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            return helperRemoteRecoveryState.VisibleHeadStreamEpoch == streamEpoch
                ? helperRemoteRecoveryState.VisibleHeadFrameId
                : -1;
        }

        var appliedHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetAppliedHeadFrameId(helperRemoteRecoveryState.SessionId, streamEpoch);
        var stableVisibleHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetStableVisibleHeadFrameId(helperRemoteRecoveryState.SessionId, streamEpoch);
        var visibleRecoveryFloorFrameId = ScreenShareFrameLossAttributionRegistry.GetVisibleRecoveryFloorFrameId(helperRemoteRecoveryState.SessionId, streamEpoch);
        return Math.Max(
            Math.Max(appliedHeadFrameId, stableVisibleHeadFrameId),
            Math.Max(
                visibleRecoveryFloorFrameId,
                helperRemoteRecoveryState.VisibleHeadStreamEpoch == streamEpoch ? helperRemoteRecoveryState.VisibleHeadFrameId : -1));
    }

    private void ResetHelperRemoteH264DecoderForReferenceTaintIfNeeded(long streamEpoch, long frameId)
    {
        if (!helperRemoteSessionController.ConsumeReferenceTaintDecoderResetPending(streamEpoch))
        {
            return;
        }

        Interlocked.Increment(ref generation);
        decodeWorker.ClearPending();
        h264StreamState.ResetDecoderOnly();
        Interlocked.Increment(ref h264ReferenceTaintDecoderResetCount);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_helper_h264_reference_taint_decoder_reset; role={logRole}; stream_epoch={streamEpoch}; frame_id={FormatFrameIdForLog(frameId)}");
    }

    private string? ResolveHelperRemotePostRecoveryRunwayRejectionReason(string? sessionId, long streamEpoch, long frameId, bool isKeyFrame)
    {
        if (isKeyFrame || frameId < 0)
        {
            return null;
        }

        if (helperRemoteFollowerState.ExpiredRecoveryRunwayActive &&
            helperRemoteFollowerState.ExpiredRecoveryRunwayEpoch == streamEpoch &&
            frameId > helperRemoteFollowerState.ExpiredRecoveryRunwayLastContiguousFrameId &&
            (helperRemoteFollowerState.ExpiredRecoveryRunwayMaximumFrameId < 0 || frameId <= helperRemoteFollowerState.ExpiredRecoveryRunwayMaximumFrameId))
        {
            return "late_same_epoch_after_head_advanced_drop";
        }

        if (!helperRemoteFollowerState.RecoveryProgressCorridorActive ||
            helperRemoteFollowerState.RecoveryProgressCorridorEpoch != streamEpoch)
        {
            return null;
        }

        if (frameId <= helperRemoteFollowerState.RecoveryProgressCorridorLastFrameId)
        {
            return "late_same_epoch_after_head_advanced_drop";
        }

        var expectedNextFrameId = helperRemoteFollowerState.RecoveryProgressCorridorLastFrameId + 1;
        var maximumActionableFrameId = helperRemoteFollowerState.RecoveryProgressCorridorRecoveryFrameId + HelperRemotePostRecoveryFollowerWindowSize;
        if (frameId == expectedNextFrameId && frameId <= maximumActionableFrameId)
        {
            return null;
        }

        if (frameId > maximumActionableFrameId)
        {
            return "recovery_runway_overflow";
        }

        ExpireHelperRemoteRecoveryRunwayWindow(
            sessionId,
            streamEpoch,
            helperRemoteFollowerState.RecoveryProgressCorridorRecoveryFrameId,
            frameId,
            "recovery_runway_overflow");
        return "recovery_runway_overflow";
    }

    private bool HasHelperRemoteBufferedRecoveryCandidate(string? sessionId, long streamEpoch)
    {
        if (streamEpoch <= 0)
        {
            return false;
        }

        var effectiveSessionId = ResolveFrameSessionId(sessionId, streamConfig: null);
        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = helperRemoteRecoveryState.SessionId;
        }

        return !string.IsNullOrWhiteSpace(effectiveSessionId) &&
               ScreenShareFrameLossAttributionRegistry.HasBufferedRecoveryKeyframeCandidate(effectiveSessionId, streamEpoch);
    }

    private bool ShouldReserveHelperRemoteApply(long streamEpoch, long frameId, bool isKeyFrame)
    {
        _ = streamEpoch;
        _ = frameId;
        _ = isKeyFrame;
        return false;
    }

    private bool ShouldReserveHelperRemoteStartupKeyframeApply(long streamEpoch, long frameId, bool isKeyFrame)
    {
        _ = streamEpoch;
        _ = frameId;
        _ = isKeyFrame;
        return false;
    }

    private void MarkReservedApplyPending(long streamEpoch, long frameId, bool startupKeyframePendingVisibleApply = false)
    {
        helperRemoteSessionController.SetReservedApplyPending(
            streamEpoch,
            frameId,
            startupKeyframePendingVisibleApply,
            DateTimeOffset.UtcNow);
    }

    private void ClearReservedApplyIfMatch(EncodedFrameDecodeRequest request)
    {
        helperRemoteSessionController.ClearReservedApplyIfMatch(request);
    }

    private bool IsReservedApplyRequest(EncodedFrameDecodeRequest request)
    {
        return helperRemoteSessionController.IsReservedApplyRequest(request);
    }

    private bool IsReservedApplyFrame(long streamEpoch, long frameId)
    {
        return helperRemoteSessionController.IsReservedApplyFrame(streamEpoch, frameId);
    }

    private bool IsStartupKeyframePendingVisibleApplyRequest(EncodedFrameDecodeRequest request)
    {
        return helperRemoteSessionController.IsStartupKeyframePendingVisibleApplyRequest(request);
    }

    private void ClearReservedApplyThroughEpoch(long streamEpoch)
    {
        helperRemoteSessionController.ClearReservedApplyThroughEpoch(streamEpoch);
    }

    private void StartRecoveryProgressCorridor(long streamEpoch, long frameId)
    {
        if (streamEpoch <= 0 || frameId < 0)
        {
            return;
        }

        helperRemoteSessionController.StartRecoveryProgressCorridor(
            streamEpoch,
            frameId,
            DateTimeOffset.UtcNow,
            HelperRemotePostRecoveryReservedApplyCount);
        Interlocked.Increment(ref recoveryProgressCorridorCount);
        Interlocked.Increment(ref recoveryProgressCorridorAppliedCount);
        NotifyRecoveryWindowStateChanged(
            streamEpoch,
            frameId,
            frameId,
            contiguousFollowerApplyCount: 0,
            status: "started");
    }

    private void ResetRecoveryProgressCorridor()
    {
        helperRemoteSessionController.ResetRecoveryProgressCorridor();
    }

    private void SetPendingRecoveryRunwayAbort(
        long streamEpoch,
        long expectedNextFrameId,
        long receivedFrameId,
        string reason)
    {
        helperRemoteSessionController.SetPendingRecoveryRunwayAbort(
            streamEpoch,
            expectedNextFrameId,
            receivedFrameId,
            reason,
            DateTimeOffset.UtcNow);
    }

    private void ClearPendingRecoveryRunwayAbort()
    {
        helperRemoteSessionController.ClearPendingRecoveryRunwayAbort();
    }

    private void ExpireHelperRemoteRecoveryRunwayWindow(
        string? sessionId,
        long streamEpoch,
        long recoveryFrameId,
        long blockedByFrameId,
        string rejectionReason)
    {
        helperRemoteSessionController.ExpireRecoveryRunwayWindow(streamEpoch, recoveryFrameId, DateTimeOffset.UtcNow);
        var effectiveSessionId = ResolveFrameSessionId(sessionId, streamConfig: null);
        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = helperRemoteRecoveryState.SessionId;
        }

        if (!string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveStaleRunwayWindowAbort(
                effectiveSessionId,
                streamEpoch,
                recoveryFrameId,
                blockedByFrameId);
        }

        AbortRecoveryProgressCorridor(rejectionReason);
        ResetPostRecoveryCorridorState(streamEpoch, rejectionReason);
    }

    private bool TryAbortPendingRecoveryRunwayAfterCorridorStart(EncodedFrameDecodeRequest request)
    {
        var runwayAbort = helperRemoteSessionController.ConsumePendingRecoveryRunwayAbortForCorridorStart(
            request.StreamEpoch,
            DateTimeOffset.UtcNow);
        if (!runwayAbort.Matched)
        {
            return false;
        }

        ExpireHelperRemoteRecoveryRunwayWindow(
            request.SessionId,
            request.StreamEpoch,
            runwayAbort.RecoveryFrameId,
            runwayAbort.ReceivedFrameId,
            "recovery_runway_overflow");
        if (!string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                helperRemoteRecoveryState.SessionId,
                request.StreamEpoch,
                "recovery_progress_corridor_aborted",
                runwayAbort.ExpectedNextFrameId,
                runwayAbort.ReceivedFrameId);
        }
        return true;
    }

    private void AbortRecoveryProgressCorridor(string reason = "unknown")
    {
        ApplyRecoveryProgressCorridorAbort(
            helperRemoteSessionController.AbortRecoveryProgressCorridor(reason, DateTimeOffset.UtcNow));
    }

    private void ObserveRecoveryProgressCorridorApply(long streamEpoch, long frameId)
    {
        var corridorResult = helperRemoteSessionController.ObserveRecoveryProgressCorridorApply(
            streamEpoch,
            frameId,
            DateTimeOffset.UtcNow,
            HelperRemotePostRecoveryReservedApplyCount);
        if (corridorResult.Abort.Aborted)
        {
            ApplyRecoveryProgressCorridorAbort(corridorResult.Abort);
            return;
        }

        if (!corridorResult.Applied)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(
                helperRemoteRecoveryState.SessionId,
                streamEpoch,
                frameId);
            ScreenShareFrameLossAttributionRegistry.ObserveRunwayFollowerEmittedWithinActionableWindow(
                helperRemoteRecoveryState.SessionId,
                streamEpoch,
                frameId);
        }
        Interlocked.Increment(ref recoveryProgressCorridorAppliedCount);
        NotifyRecoveryWindowStateChanged(
            corridorResult.StreamEpoch,
            corridorResult.RecoveryFrameId,
            corridorResult.LastContiguousFrameId,
            corridorResult.ContiguousFollowerApplyCount,
            status: "follower_applied");
        if (corridorResult.Succeeded)
        {
            Interlocked.Increment(ref recoveryProgressCorridorSuccessCount);
            helperRemoteSessionController.CompletePostRecoveryFollowerWindow(corridorResult.StreamEpoch);
            ReleaseHelperRemoteH264ReferenceTaint(
                corridorResult.StreamEpoch,
                corridorResult.LastContiguousFrameId);
            NotifyRecoveryWindowStateChanged(
                corridorResult.StreamEpoch,
                corridorResult.RecoveryFrameId,
                corridorResult.LastContiguousFrameId,
                corridorResult.ContiguousFollowerApplyCount,
                status: "succeeded");
        }
    }

    private void ApplyRecoveryProgressCorridorAbort(HelperRemoteRecoveryProgressCorridorAbortResult abortResult)
    {
        if (!abortResult.Aborted)
        {
            return;
        }

        Interlocked.Increment(ref recoveryProgressCorridorAbortCount);
        var recoveryCorridorAbortReason = string.IsNullOrWhiteSpace(abortResult.Reason)
            ? "unknown"
            : abortResult.Reason.Trim();
        EnterHelperRemoteH264ReferenceTaint(
            abortResult.StreamEpoch,
            recoveryCorridorAbortReason);
        NotifyRecoveryWindowStateChanged(
            abortResult.StreamEpoch,
            abortResult.RecoveryFrameId,
            abortResult.LastContiguousFrameId,
            abortResult.ContiguousFollowerApplyCount,
            status: "aborted",
            abortReason: recoveryCorridorAbortReason);
    }

    private void NotifyRecoveryWindowStateChanged(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason = null)
    {
        if (streamEpoch <= 0 || recoveryFrameId < 0)
        {
            return;
        }

        var effectiveStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
        if (!string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                helperRemoteRecoveryState.SessionId,
                streamEpoch,
                effectiveStatus switch
                {
                    "started" => "recovery_window_started",
                    "follower_applied" => "recovery_window_follower_applied",
                    "succeeded" => "recovery_window_succeeded",
                    "aborted" => "recovery_window_aborted",
                    _ => "recovery_window_state_changed",
                },
                lastContiguousFrameId >= 0 ? lastContiguousFrameId : recoveryFrameId,
                recoveryFrameId);
        }

        RecoveryWindowStateChanged?.Invoke(
            this,
            new ScreenShareViewerRecoveryWindowStateChangedEventArgs(
                streamEpoch,
                recoveryFrameId,
                lastContiguousFrameId,
                contiguousFollowerApplyCount,
                effectiveStatus,
                abortReason));
    }

    private void EnsureHelperRemoteStartupCorridorNotStalled(string sessionId, long streamEpoch, long frameId)
    {
        var corridorAbort = helperRemoteSessionController.EnsureRecoveryProgressCorridorNotStalled(
            streamEpoch,
            DateTimeOffset.UtcNow,
            HelperRemoteStartupCorridorStallTimeout,
            HelperRemotePostRecoveryReservedApplyCount);
        if (!corridorAbort.Aborted)
        {
            return;
        }

        ApplyRecoveryProgressCorridorAbort(corridorAbort);
        decodeWorker.ClearPending();
        ResetPostRecoveryCorridorState(streamEpoch, "blocked_by_reserved_recovery_frame");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                sessionId,
                streamEpoch,
                "startup_corridor_timeout",
                frameId);
        }
    }

    private string ResolveReservedApplySuppressionReason(EncodedFrameDecodeRequest request)
    {
        return request.StreamEpoch > helperRemoteFollowerState.ReservedApplyStreamEpoch
            ? "decoded_newer_epoch_ignored_during_lock"
            : "decoded_blocked_by_reserved_recovery_frame";
    }

    private string ResolveReservedApplySuppressionReason(long streamEpoch)
    {
        return streamEpoch > helperRemoteFollowerState.ReservedApplyStreamEpoch
            ? "newer_epoch_non_key_ignored_during_lock"
            : "blocked_by_reserved_recovery_frame";
    }

    private static bool IsBetterDeferredPostRecoveryCandidate(
        DeferredHelperRemoteFrameCandidate candidate,
        DeferredHelperRemoteFrameCandidate current)
    {
        if (candidate.StreamEpoch != current.StreamEpoch)
        {
            return candidate.StreamEpoch > current.StreamEpoch;
        }

        if (candidate.CapturedTsUtcMs != current.CapturedTsUtcMs)
        {
            return candidate.CapturedTsUtcMs > current.CapturedTsUtcMs;
        }

        if (candidate.FrameId != current.FrameId)
        {
            return candidate.FrameId > current.FrameId;
        }

        return candidate.ArrivalSequence > current.ArrivalSequence;
    }

    private static long GetNextAllowedDeferredPostRecoveryFrameId(
        SortedDictionary<long, DeferredHelperRemoteFrameCandidate> candidates,
        long minimumDeferredFrameId)
    {
        if (minimumDeferredFrameId < 0)
        {
            return -1;
        }

        var nextFrameId = minimumDeferredFrameId + 1;
        for (var bufferedCount = 0; bufferedCount < HelperRemotePostRecoveryFollowerWindowSize; bufferedCount++)
        {
            if (!candidates.ContainsKey(nextFrameId))
            {
                return nextFrameId;
            }

            nextFrameId++;
        }

        return -1;
    }

    private int GetDeferredPostRecoveryCandidateCount()
    {
        return helperRemoteSessionController.GetDeferredPostRecoveryCandidateCount();
    }

    private void ClearDeferredPostRecoveryCandidates(string rejectionReason)
    {
        foreach (var staleCandidate in helperRemoteSessionController.ClearDeferredPostRecoveryCandidates())
        {
            ObserveViewerRejectedBeforeEnqueue(
                staleCandidate.SessionId,
                staleCandidate.Encoding,
                staleCandidate.StreamEpoch,
                staleCandidate.FrameId,
                staleCandidate.IsKeyFrame,
                rejectionReason);
        }
    }

    private void ReleaseDeferredPostRecoveryCandidateIfMatch(long streamEpoch, long previousVisibleFrameId)
    {
        var releaseResult = helperRemoteSessionController.ReleaseDeferredPostRecoveryCandidateIfMatch(
            streamEpoch,
            previousVisibleFrameId,
            HelperRemotePostRecoveryFollowerWindowSize);
        foreach (var rejectedCandidate in releaseResult.RejectedCandidates ?? Array.Empty<DeferredHelperRemoteFrameCandidate>())
        {
            var rejectionReason = rejectedCandidate.StreamEpoch != streamEpoch
                ? "post_recovery_visible_generation_reset"
                : "recovery_runway_overflow";
            if (rejectionReason == "post_recovery_visible_generation_reset")
            {
                Interlocked.Increment(ref postRecoveryPurgedPreRecoveryFollowerCount);
            }
            else
            {
                Interlocked.Increment(ref recoveryFollowerWindowTrimmedCount);
            }

            ObserveViewerRejectedBeforeEnqueue(
                rejectedCandidate.SessionId,
                rejectedCandidate.Encoding,
                rejectedCandidate.StreamEpoch,
                rejectedCandidate.FrameId,
                rejectedCandidate.IsKeyFrame,
                rejectionReason);
        }

        ApplyRecoveryProgressCorridorAbort(releaseResult.CorridorAbort);
        if (!releaseResult.HasCandidateToEnqueue)
        {
            return;
        }

        var candidate = releaseResult.CandidateToEnqueue;
        MarkReservedApplyPending(candidate.StreamEpoch, candidate.FrameId);
        if (candidate.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.ProtectedFollower)
        {
            Interlocked.Increment(ref protectedRecoveryDeliveryCount);
        }

        var viewerAcceptedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ObserveViewerAcceptedForDecode(
            candidate.SessionId,
            candidate.Encoding,
            candidate.StreamEpoch,
            candidate.FrameId,
            candidate.IsKeyFrame,
            viewerAcceptedUtcMs);
        var enqueueResult = decodeWorker.EnqueueCopied(
            candidate.Encoding,
            candidate.EncodedFrameBytes,
            candidate.CapturedTsUtcMs,
            isKeyFrame: candidate.IsKeyFrame,
            streamEpoch: candidate.StreamEpoch,
            frameId: candidate.FrameId,
            sessionId: candidate.SessionId,
            requiresReservedApply: true,
            bypassesAgeBudget: true,
            recoveryDeliveryClass: candidate.RecoveryDeliveryClass,
            frameReadyObservedUtcMs: 0,
            viewerAcceptedUtcMs: viewerAcceptedUtcMs);
        if (enqueueResult.DroppedPendingFrame)
        {
            Interlocked.Increment(ref framesCoalesced);
        }
    }

    private void PurgeDeferredPostRecoveryCandidateIfStale(EncodedFrameDecodeRequest recoveryRequest)
    {
        foreach (var candidate in helperRemoteSessionController.PurgeDeferredPostRecoveryCandidateIfStale(recoveryRequest))
        {
            Interlocked.Increment(ref postRecoveryPurgedPreRecoveryFollowerCount);
            ObserveViewerRejectedBeforeEnqueue(
                candidate.SessionId,
                candidate.Encoding,
                candidate.StreamEpoch,
                candidate.FrameId,
                candidate.IsKeyFrame,
                "post_recovery_visible_generation_reset");
        }
    }

    private void ResetPostRecoveryCorridorState(long streamEpoch, string rejectionReason)
    {
        ClearReservedApplyThroughEpoch(streamEpoch);
        ClearDeferredPostRecoveryCandidates(rejectionReason);
        helperRemoteSessionController.ResetPostRecoveryStabilization();
    }

    private void ResetHelperRemoteVisibleGenerationAfterRecoveryApply(EncodedFrameDecodeRequest recoveryRequest)
    {
        if (!IsHelperRemoteH264(recoveryRequest.Encoding))
        {
            return;
        }

        Interlocked.Increment(ref generation);
        decodeWorker.ClearPending();
        Interlocked.Increment(ref postRecoveryVisibleGenerationResetCount);
        if (!string.IsNullOrWhiteSpace(recoveryRequest.SessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                recoveryRequest.SessionId,
                recoveryRequest.StreamEpoch,
                "post_recovery_visible_generation_reset",
                recoveryRequest.FrameId);
        }
    }

    private bool ShouldBypassHelperRemoteDecodeAgeBudget(string encoding, long streamEpoch, bool isKeyFrame)
    {
        if (!IsHelperRemoteH264(encoding) || isKeyFrame)
        {
            return false;
        }

        var helperSessionSnapshot = GetHelperRemoteSessionSnapshot();
        if (helperSessionSnapshot.CurrentEpoch > 0 &&
            helperSessionSnapshot.CurrentEpoch != streamEpoch)
        {
            return false;
        }

        if (helperSessionSnapshot.Phase == HelperRemoteSessionPhase.NoVisibleBaseline)
        {
            return true;
        }

        if (helperSessionSnapshot.RecoveryActive ||
            helperSessionSnapshot.RecoveryCorridorActive ||
            helperSessionSnapshot.RunwayCleanupActive ||
            helperSessionSnapshot.PostRecoveryStabilizationActive)
        {
            return true;
        }

        if (helperSessionSnapshot.Phase == HelperRemoteSessionPhase.VisibleStable &&
            helperSessionSnapshot.BaselineEstablished &&
            helperSessionSnapshot.SteadyVisibleProgressActive)
        {
            return true;
        }

        return (helperRemoteFollowerState.ReservedApplyActive &&
                helperRemoteFollowerState.ReservedApplyStreamEpoch == streamEpoch) ||
               (helperRemoteFollowerState.StartupKeyframePendingVisibleApplyActive &&
                helperRemoteFollowerState.StartupKeyframePendingVisibleApplyStreamEpoch == streamEpoch);
    }

    private bool ShouldDropHelperRemoteVisibleStableFrameForFreshness(
        EncodedFrameDecodeRequest request,
        long ageMs,
        HelperRemoteSessionSnapshot helperSessionSnapshot)
    {
        if (!IsHelperRemoteH264(request.Encoding) ||
            request.IsKeyFrame ||
            request.RecoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal ||
            IsReservedApplyRequest(request) ||
            IsHelperRemotePostRecoveryStabilizationFrame(request) ||
            ageMs < 0)
        {
            return false;
        }

        if (helperSessionSnapshot.CurrentEpoch != request.StreamEpoch ||
            helperSessionSnapshot.Phase != HelperRemoteSessionPhase.VisibleStable ||
            !helperSessionSnapshot.BaselineEstablished ||
            !helperSessionSnapshot.SteadyVisibleProgressActive ||
            helperSessionSnapshot.RecoveryActive ||
            helperSessionSnapshot.RecoveryCorridorActive ||
            helperSessionSnapshot.RunwayCleanupActive ||
            helperSessionSnapshot.PostRecoveryStabilizationActive)
        {
            return false;
        }

        return ageMs > HelperRemoteMaxPendingEncodedFrameAgeMs;
    }

    private bool ShouldSuppressHelperRemotePostQuarantineSettleFrame(
        EncodedFrameDecodeRequest request,
        long ageMs,
        long nowUtcMs)
    {
        if (!IsHelperRemoteH264(request.Encoding) ||
            request.IsKeyFrame ||
            request.RecoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal ||
            IsReservedApplyRequest(request) ||
            IsHelperRemotePostRecoveryStabilizationFrame(request) ||
            ageMs < 0)
        {
            return false;
        }

        var settleEpoch = Interlocked.Read(ref helperRemotePostQuarantineSettleEpoch);
        var settleStartedUtcMs = Interlocked.Read(ref helperRemotePostQuarantineSettleStartedUtcMs);
        var settleUntilUtcMs = Interlocked.Read(ref helperRemotePostQuarantineSettleUntilUtcMs);
        if (settleEpoch != request.StreamEpoch ||
            settleStartedUtcMs < 0 ||
            settleUntilUtcMs < 0 ||
            nowUtcMs > settleUntilUtcMs ||
            nowUtcMs - settleStartedUtcMs > HelperRemotePostQuarantineSettleMaxHoldMs)
        {
            return false;
        }

        return ageMs > HelperRemoteMaxPendingEncodedFrameAgeMs;
    }

    private void MaybeClearHelperRemotePostQuarantineSettleOnFreshFrame(
        EncodedFrameDecodeRequest request,
        long ageMs,
        long nowUtcMs)
    {
        var settleEpoch = Interlocked.Read(ref helperRemotePostQuarantineSettleEpoch);
        if (settleEpoch != request.StreamEpoch ||
            settleEpoch <= 0 ||
            nowUtcMs > Interlocked.Read(ref helperRemotePostQuarantineSettleUntilUtcMs))
        {
            return;
        }

        if (request.IsKeyFrame ||
            (request.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal &&
             ageMs >= 0 &&
             ageMs <= HelperRemoteMaxPendingEncodedFrameAgeMs))
        {
            ClearHelperRemotePostQuarantineSettle();
        }
    }

    private void SuppressDecodedStaleNormalNonKeyVisibleFrame(
        EncodedFrameDecodeRequest request,
        long ageMs,
        string reason,
        bool incrementVisibleStableCounter,
        bool incrementPostQuarantineSettleCounter)
    {
        if (incrementVisibleStableCounter)
        {
            Interlocked.Increment(ref staleFrameDropVisibleStableCount);
            Interlocked.Exchange(ref staleFrameDropVisibleStableLastAgeMs, ageMs);
        }

        Interlocked.Increment(ref staleNormalNonKeyVisibleSuppressCount);
        Interlocked.Increment(ref decodedStaleVisibleSuppressCount);
        if (incrementPostQuarantineSettleCounter)
        {
            Interlocked.Increment(ref postQuarantineSettleSuppressCount);
        }

        ObserveStaleDroppedAfterDecode(request, reason);
        ClearReservedApplyIfMatch(request);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_viewer_stale_frame_dropped; role={logRole}; stream_epoch={request.StreamEpoch}; frame_id={FormatFrameIdForLog(request.FrameId)}; rendered_age_ms={ageMs}; threshold_ms={HelperRemoteMaxPendingEncodedFrameAgeMs}; last_rendered_age_ms={(LastRenderedFrameAgeMs >= 0 ? LastRenderedFrameAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}; reason={reason}; decoded_reference_continuity_preserved=1");
        StaleFrameDropped?.Invoke(
            this,
            new ScreenShareViewerStaleFrameDroppedEventArgs(
                ageMs,
                request.StreamEpoch,
                referenceContinuityPreserved: true));
    }

    private string ResolveStaleDropReason(EncodedFrameDecodeRequest request)
    {
        return IsHelperRemotePostRecoveryStabilizationFrame(request)
            ? "decoded_stale_after_recovery"
            : "stale_frame_drop";
    }

    private bool IsHelperRemotePostRecoveryStabilizationFrame(EncodedFrameDecodeRequest request)
    {
        return IsHelperRemoteH264(request.Encoding) &&
               helperRemoteFollowerState.PostRecoveryStabilizationEpoch == request.StreamEpoch &&
               helperRemoteFollowerState.PostRecoveryReservedAppliesRemaining > 0;
    }

    private void OnHelperRemoteFrameAppliedVisible(EncodedFrameDecodeRequest request)
    {
        var wasPostRecoveryFollower =
            IsHelperRemotePostRecoveryStabilizationFrame(request) &&
            !request.IsKeyFrame;
        helperRemoteSessionController.OnFrameAppliedVisible(request);
        if (wasPostRecoveryFollower)
        {
            Interlocked.Increment(ref recoveryFollowerWindowAppliedCount);
        }

        TryReleasePendingHelperRemoteH264ReferenceQuarantine("visible_apply");
    }

    private HelperRemoteVisibleApplyProgress BuildHelperRemoteVisibleApplyProgress(
        EncodedFrameDecodeRequest request)
    {
        return helperRemoteSessionController.BuildVisibleApplyProgress(request);
    }

    private void RecordHelperVisibleApplyDiagnostics(
        LatestEncodedDecodedFrame decodedFrame,
        long decodeCallbackReceivedUtcMs,
        long uiApplyStartUtcMs,
        long visibleApplyUtcMs,
        HelperRemoteVisibleApplyProgress visibleApplyProgress)
    {
        if (!IsHelperRemoteH264(decodedFrame.Request.Encoding))
        {
            return;
        }

        if (decodedFrame.DecodeCompletedUtcMs > 0 && visibleApplyUtcMs >= decodedFrame.DecodeCompletedUtcMs)
        {
            Interlocked.Increment(ref helperDecodeCompleteToVisibleApplyObserved);
            Interlocked.Add(
                ref totalHelperDecodeCompleteToVisibleApplyMs,
                Math.Max(0, visibleApplyUtcMs - decodedFrame.DecodeCompletedUtcMs));
        }

        if (uiApplyStartUtcMs >= decodeCallbackReceivedUtcMs)
        {
            Interlocked.Increment(ref helperUiPostApplyObserved);
            Interlocked.Add(
                ref totalHelperUiPostApplyMs,
                Math.Max(0, uiApplyStartUtcMs - decodeCallbackReceivedUtcMs));
        }

        if (visibleApplyProgress.AppliedHeadFrameId >= 0 && visibleApplyProgress.VisibleHeadFrameId >= 0)
        {
            Interlocked.Increment(ref helperVisibleHeadLagObserved);
            Interlocked.Add(
                ref totalHelperVisibleHeadLagFrames,
                Math.Max(0, visibleApplyProgress.AppliedHeadFrameId - visibleApplyProgress.VisibleHeadFrameId));
        }

        if (visibleApplyProgress.VisibleHeadFrameId >= 0 && visibleApplyProgress.StableVisibleHeadFrameId >= 0)
        {
            Interlocked.Increment(ref helperStableHeadLagObserved);
            Interlocked.Add(
                ref totalHelperStableHeadLagFrames,
                Math.Max(0, visibleApplyProgress.VisibleHeadFrameId - visibleApplyProgress.StableVisibleHeadFrameId));
        }
    }

    private void ObserveDecodedFrameSuppressedAfterDecode(EncodedFrameDecodeRequest request, string reason)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        var effectiveSessionId = ResolveFrameSessionId(request.SessionId, streamConfig: null);
        helperRemoteSessionController.SetSessionId(effectiveSessionId);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodedFrameReplacedBeforeApply(
            effectiveSessionId,
            request.StreamEpoch,
            request.FrameId,
            request.IsKeyFrame,
            reason);
    }

    private bool IsHelperRemoteH264(string encoding)
    {
        return string.Equals(logRole, "helper_remote", StringComparison.Ordinal) &&
               H264DecodeStreamState.IsH264Encoding(encoding);
    }

    private bool ShouldBypassStaleFrameDrop(EncodedFrameDecodeRequest request)
    {
        if (!IsHelperRemoteH264(request.Encoding))
        {
            return false;
        }

        if (request.RecoveryDeliveryClass != ScreenShareRecoveryDeliveryClass.Normal)
        {
            return true;
        }

        if (IsReservedApplyRequest(request) || IsHelperRemotePostRecoveryStabilizationFrame(request))
        {
            return true;
        }

        if (!request.IsKeyFrame)
        {
            return false;
        }

        if (helperRemoteRecoveryState.RecoveryActive && helperRemoteRecoveryState.RecoveryStreamEpoch == request.StreamEpoch)
        {
            return true;
        }

        if (helperRemoteRecoveryState.TrackedFrameEpoch != request.StreamEpoch)
        {
            return true;
        }

        if (!helperRemoteRecoveryState.HasCleanKeyframeForEpoch)
        {
            return true;
        }

        return lastAppliedStreamEpoch != request.StreamEpoch;
    }

    private ScreenShareViewerFrameGapObservation? ObserveFrameGapContinuityLoss(long streamEpoch, long frameId, bool isKeyFrame)
    {
        return helperRemoteSessionController.ObserveFrameGapContinuityLoss(streamEpoch, frameId, isKeyFrame);
    }

    private void ResetHelperRemoteFrameTracking(long streamEpoch)
    {
        if (helperRemoteFollowerState.RecoveryProgressCorridorActive &&
            helperRemoteFollowerState.RecoveryProgressCorridorEpoch != Math.Max(0, streamEpoch))
        {
            AbortRecoveryProgressCorridor();
        }

        helperRemoteSessionController.ResetFrameTracking(streamEpoch);
    }

    private ScreenShareViewerContinuityHandlingResult ObserveReceiverContinuityLoss(
        long streamEpoch,
        long chunksDroppedOlderFrame,
        long assembliesExpired)
    {
        if (streamEpoch <= 0)
        {
            return ScreenShareViewerContinuityHandlingResult.None;
        }

        var lastDropped = Interlocked.Read(ref lastObservedReceiverDroppedFrameCount);
        var lastExpired = Interlocked.Read(ref lastObservedAssembliesExpiredCount);
        Interlocked.Exchange(ref lastObservedReceiverDroppedFrameCount, Math.Max(0, chunksDroppedOlderFrame));
        Interlocked.Exchange(ref lastObservedAssembliesExpiredCount, Math.Max(0, assembliesExpired));

        var droppedDelta = chunksDroppedOlderFrame > lastDropped ? chunksDroppedOlderFrame - lastDropped : 0;
        var expiredDelta = assembliesExpired > lastExpired ? assembliesExpired - lastExpired : 0;
        if (droppedDelta <= 0 && expiredDelta <= 0)
        {
            return ScreenShareViewerContinuityHandlingResult.None;
        }

        if (TryResolveHelperRemoteActionableReferenceLossReason(streamEpoch, out var taintReason))
        {
            return new ScreenShareViewerContinuityHandlingResult(
                ScreenShareViewerContinuityHandlingKind.HardRecovery,
                taintReason);
        }

        if ((helperRemoteRecoveryState.RecoveryActive && helperRemoteRecoveryState.RecoveryStreamEpoch == streamEpoch) ||
            HasVisibleHeadForEpoch(streamEpoch) ||
            HasProvenHeadFloorForEpoch(streamEpoch))
        {
            return new ScreenShareViewerContinuityHandlingResult(
                ScreenShareViewerContinuityHandlingKind.SoftStaleCleanup,
                "stale_frame_superseded");
        }

        return new ScreenShareViewerContinuityHandlingResult(
            ScreenShareViewerContinuityHandlingKind.HardRecovery,
            "stale_frame_superseded");
    }

    private bool HasVisibleHeadForEpoch(long streamEpoch)
    {
        return streamEpoch > 0 &&
               helperRemoteRecoveryState.VisibleHeadStreamEpoch == streamEpoch &&
               helperRemoteRecoveryState.VisibleHeadFrameId >= 0;
    }

    private bool HasProvenHeadFloorForEpoch(long streamEpoch)
    {
        if (streamEpoch <= 0 || string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            return false;
        }

        var appliedHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetAppliedHeadFrameId(helperRemoteRecoveryState.SessionId, streamEpoch);
        var stableVisibleHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetStableVisibleHeadFrameId(helperRemoteRecoveryState.SessionId, streamEpoch);
        var visibleRecoveryFloorFrameId = ScreenShareFrameLossAttributionRegistry.GetVisibleRecoveryFloorFrameId(helperRemoteRecoveryState.SessionId, streamEpoch);
        return Math.Max(
            Math.Max(appliedHeadFrameId, stableVisibleHeadFrameId),
            Math.Max(
                visibleRecoveryFloorFrameId,
                helperRemoteRecoveryState.VisibleHeadStreamEpoch == streamEpoch ? helperRemoteRecoveryState.VisibleHeadFrameId : -1)) >= 0;
    }

    private void ObserveSoftStaleCleanup(long streamEpoch, string reason)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        Interlocked.Increment(ref softStaleCleanupCount);
        if (ShouldEnterHelperRemoteReferenceTaintForSoftCleanup(streamEpoch, reason))
        {
            EnterHelperRemoteH264ReferenceTaint(streamEpoch, reason);
        }

        if (string.Equals(reason, "stale_frame_superseded", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref staleSupersededRecoverySuppressedCount);
        }

        if (!string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                helperRemoteRecoveryState.SessionId,
                streamEpoch,
                "soft_stale_cleanup",
                helperRemoteRecoveryState.VisibleHeadFrameId,
                helperRemoteRecoveryState.LastCleanFrameId);
        }
    }

    private void ActivateHelperRemoteRecovery(
        string reason,
        long streamEpoch,
        long currentEpochNeedMoreInputCount,
        bool shouldRequestRecoveryKeyframe,
        long expectedNextFrameId = -1,
        long receivedFrameId = -1,
        long lastCleanFrameId = -1)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        var newlyActive =
            !helperRemoteRecoveryState.RecoveryActive ||
            helperRemoteRecoveryState.RecoveryStreamEpoch != streamEpoch ||
            !string.Equals(helperRemoteRecoveryState.RecoveryReason, reason, StringComparison.Ordinal);

        var activation = helperRemoteSessionController.ActivateRecovery(
            reason,
            streamEpoch,
            expectedNextFrameId,
            receivedFrameId,
            lastCleanFrameId);
        AbortRecoveryProgressCorridor();
        EnterHelperRemoteH264ReferenceTaint(streamEpoch, reason);
        for (var i = 0; i < activation.PurgedDeferredCandidateCount; i++)
        {
            Interlocked.Increment(ref postRecoveryPurgedPreRecoveryFollowerCount);
        }

        if (!activation.NewlyActive)
        {
            return;
        }

        SuppressPendingHelperRemoteUnsafeFrames(streamEpoch, reason);
        Interlocked.Increment(ref continuityLossCount);
        if (string.Equals(reason, "frame_gap", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref frameGapContinuityLossCount);
        }

        if (shouldRequestRecoveryKeyframe)
        {
            Interlocked.Increment(ref recoveryKeyframesRequested);
        }

        if (!string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId) &&
            string.Equals(reason, "frame_gap", StringComparison.Ordinal))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                helperRemoteRecoveryState.SessionId,
                streamEpoch,
                "gap_detected",
                activation.RecoveryExpectedNextFrameId,
                activation.RecoveryReceivedFrameId);
        }

        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_receiver_continuity_lost; role={logRole}; stream_epoch={streamEpoch}; reason={reason}; recovery_active=1; current_epoch_need_more_input_count={Math.Max(0, currentEpochNeedMoreInputCount)}; expected_next_frame_id={FormatFrameIdForLog(activation.RecoveryExpectedNextFrameId)}; received_frame_id={FormatFrameIdForLog(activation.RecoveryReceivedFrameId)}; last_clean_frame_id={FormatFrameIdForLog(activation.LastCleanFrameId)}");
        ContinuityLost?.Invoke(
            this,
            new ScreenShareViewerContinuityLostEventArgs(
                reason,
                streamEpoch,
                currentEpochNeedMoreInputCount,
                shouldRequestRecoveryKeyframe,
                activation.RecoveryExpectedNextFrameId,
                activation.RecoveryReceivedFrameId,
                activation.LastCleanFrameId));
    }

    private void SuppressPendingHelperRemoteUnsafeFrames(long streamEpoch, string reason)
    {
        if (string.Equals(reason, "decode_drop_before_decode", StringComparison.Ordinal))
        {
            return;
        }

        Interlocked.Increment(ref generation);
        decodeWorker.ClearPending();
        if (!string.IsNullOrWhiteSpace(helperRemoteRecoveryState.SessionId))
        {
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
                helperRemoteRecoveryState.SessionId,
                streamEpoch,
                "post_recovery_visible_generation_reset",
                helperRemoteRecoveryState.LastCleanFrameId);
        }

        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_viewer_recovery_pending_suppressed; role={logRole}; stream_epoch={streamEpoch}; reason={SanitizeRecoveryReason(reason)}; last_clean_frame_id={FormatFrameIdForLog(helperRemoteRecoveryState.LastCleanFrameId)}");
    }

    private void LogWaitingForRecoveryKeyframe(long streamEpoch)
    {
        if (!TryMarkEpochLogged(ref lastLoggedRecoveryWaitingEpoch, streamEpoch))
        {
            return;
        }

        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_viewer_waiting_for_recovery_keyframe; role={logRole}; stream_epoch={streamEpoch}; reason={SanitizeRecoveryReason(helperRemoteRecoveryState.RecoveryReason)}; recovery_active=1; current_epoch_need_more_input_count={Math.Max(0, Interlocked.Read(ref needMoreInputCount))}; expected_next_frame_id={FormatFrameIdForLog(helperRemoteRecoveryState.RecoveryExpectedNextFrameId)}; received_frame_id={FormatFrameIdForLog(helperRemoteRecoveryState.RecoveryReceivedFrameId)}; last_clean_frame_id={FormatFrameIdForLog(helperRemoteRecoveryState.LastCleanFrameId)}");
    }

    private static string SanitizeRecoveryReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "continuity_loss" : reason.Trim();
    }

    private bool ShouldForceHelperRemoteRecoveryOnce(long streamEpoch, long frameId)
    {
        return helperRemoteSessionController.ShouldForceRecoveryOnce(streamEpoch, frameId);
    }

    private static long ReadForcedHelperRemoteRecoveryAfterApplies()
    {
        var raw = Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_FORCE_HELPER_REMOTE_RECOVERY_AFTER_APPLIES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return -1;
        }

        if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            return parsed;
        }

        return -1;
    }

    private static string SanitizeViewerStatusForLog(string? statusText)
    {
        return string.IsNullOrWhiteSpace(statusText)
            ? "(none)"
            : statusText.Replace(';', ',').Trim();
    }

    private static string FormatFrameIdForLog(long frameId)
    {
        return frameId >= 0 ? frameId.ToString() : "(none)";
    }

    private static string FormatEpochTimelineEvents(IReadOnlyList<ScreenShareEpochContinuityEventSnapshot> timelineEvents)
    {
        if (timelineEvents.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            "|",
            timelineEvents.Select(static eventSnapshot =>
                $"{eventSnapshot.EventName}@{eventSnapshot.OccurredUtcMs}:{FormatFrameIdToken(eventSnapshot.FrameId)}>{FormatFrameIdToken(eventSnapshot.RelatedFrameId)}"));
    }

    private static string FormatLossBursts(IReadOnlyList<ScreenShareReassemblerLossBurstSnapshot> bursts)
    {
        if (bursts.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            "|",
            bursts.Select(static burst =>
                $"{burst.RootCause}[{FormatFrameIdToken(burst.ExpectedNextFrameId)}:{FormatFrameIdToken(burst.ReceivedFrameIdStart)}-{FormatFrameIdToken(burst.ReceivedFrameIdEnd)};future={burst.FutureNonKeyBufferedCount};recovery={FormatFrameIdToken(burst.BufferedRecoveryKeyframeFrameId)};count={burst.LossCount}]"));
    }

    private static string ResolveDominantHelperAdmissionRejectReason(
        ScreenShareFrameLossSessionSnapshot snapshot,
        ScreenShareMetrics viewerMetrics)
    {
        var effectiveRecoveryWaitRejectCount =
            Math.Max(0L, viewerMetrics.RecoveryWaitRejectBeforeRunwayCount);
        if (effectiveRecoveryWaitRejectCount <= 0 &&
            Math.Max(0L, snapshot.WaitingForRecoveryKeyframeRejectCount) > 0 &&
            Math.Max(0L, viewerMetrics.PreCandidateGapTailEmittedToViewerCount) <= 0)
        {
            effectiveRecoveryWaitRejectCount = 0;
        }

        var candidates = new[]
        {
            (Reason: "waiting_for_recovery_keyframe", Count: effectiveRecoveryWaitRejectCount),
            (Reason: "recovery_runway_overflow", Count: Math.Max(0L, viewerMetrics.RecoveryRunwayOverflowRejectCount)),
            (Reason: "blocked_by_reserved_recovery_frame", Count: Math.Max(0L, snapshot.BlockedByReservedRecoveryFrameRejectCount)),
            (Reason: "older_epoch_ignored_during_recovery_lock", Count: Math.Max(0L, snapshot.OlderEpochIgnoredDuringRecoveryLockCount)),
            (Reason: "newer_epoch_non_key_ignored_during_lock", Count: Math.Max(0L, snapshot.NewerEpochNonKeyIgnoredDuringLockCount)),
            (Reason: "deferred_post_recovery_candidate_replaced", Count: Math.Max(0L, snapshot.DeferredPostRecoveryCandidateReplaceCount)),
        };

        var best = candidates
            .OrderByDescending(static candidate => candidate.Count)
            .ThenBy(static candidate => candidate.Reason, StringComparer.Ordinal)
            .FirstOrDefault();

        return best.Count > 0 ? best.Reason : "none";
    }

    private static string FormatFrameIdToken(long frameId)
    {
        return frameId >= 0 ? frameId.ToString() : "(none)";
    }

    private static LatestEncodedFrameDecodeWorkerOptions CreateDecodeWorkerOptions(string role)
    {
        return string.Equals(role, "helper_remote", StringComparison.Ordinal)
            ? new LatestEncodedFrameDecodeWorkerOptions(
                MaxPendingEncodedFrames: HelperRemoteMaxPendingEncodedFrames,
                MaxPendingEncodedFrameAgeMs: HelperRemoteMaxPendingEncodedFrameAgeMs,
                DecoupleApplyFromDecode: true,
                MaxPendingDecodedFrames: 1)
            : new LatestEncodedFrameDecodeWorkerOptions();
    }

    private static bool TryMarkEpochLogged(ref long target, long streamEpoch)
    {
        var previous = Interlocked.Read(ref target);
        if (previous == streamEpoch)
        {
            return false;
        }

        Interlocked.Exchange(ref target, streamEpoch);
        return true;
    }

    private static Task PostFrameApplyToUiAsync(Action action)
        => PostToUiAsync(action, DispatcherPriority.Render);

    private static Task PostStatusToUiAsync(Action action)
        => PostToUiAsync(action, DispatcherPriority.Background);

    private static Task PostToUiAsync(Action action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, priority);
        return completion.Task;
    }

    private static Bitmap DecodeFrame(ReadOnlyMemory<byte> jpegBytes)
    {
        if (MemoryMarshal.TryGetArray(jpegBytes, out var segment) && segment.Array is not null)
        {
            using var pooledStream = new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: true);
            return new Bitmap(pooledStream);
        }

        using var fallbackStream = new MemoryStream(jpegBytes.ToArray(), writable: false);
        return new Bitmap(fallbackStream);
    }

#if DEBUG
    private void StartSnapshotTimer()
    {
        if (snapshotTimer is not null)
        {
            return;
        }

        snapshotTimer = new Timer(
            static state => ((ScreenShareViewerViewModel)state!).OnSnapshotTimerTick(),
            this,
            SnapshotInterval,
            SnapshotInterval);
    }

    private void StopSnapshotTimer()
    {
        Interlocked.Exchange(ref snapshotTickInFlight, 0);
        var timer = Interlocked.Exchange(ref snapshotTimer, null);
        timer?.Dispose();
    }

    private void OnSnapshotTimerTick()
    {
        if (Interlocked.Exchange(ref snapshotTickInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            if (!IsActive)
            {
                return;
            }

            var metrics = GetMetricsSnapshot();
            var decodeSummary = decodeDurationLatency.SnapshotAndReset();
            var endToEndSummary = endToEndLatency.SnapshotAndReset();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} decoded={metrics.FramesDecoded} errors={metrics.DecodeErrors} inFlight={decodeWorker.DecodeTasksActive} " +
                $"decode={FormatLatency(decodeSummary)} e2e={FormatLatency(endToEndSummary)} age_ms={LastRenderedFrameAgeMs} avg_render_interval_ms={metrics.AverageRenderIntervalMs:F1} avg_capture_to_render_ms={metrics.AverageCaptureToRenderMs:F1} stale_frame_renders={metrics.StaleFrameRenders}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Viewer snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref snapshotTickInFlight, 0);
        }
    }
#endif

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareViewer] {message}");
    }

#if DEBUG
    private static string FormatLatency(DebugLatencySummary summary)
    {
        return !summary.HasSamples
            ? "na"
            : $"avg={summary.AverageMilliseconds:F1}ms p50={summary.P50Milliseconds:F1}ms p95={summary.P95Milliseconds:F1}ms n={summary.Count}";
    }
#endif

}

