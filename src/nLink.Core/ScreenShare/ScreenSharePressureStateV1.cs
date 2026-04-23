using System.Text.Json.Serialization;

namespace NLink.Core.ScreenShare;

public enum ScreenSharePressureMode
{
    Normal = 0,
    ReduceFps = 1,
    CatchUpOnly = 2,
}

public static class ScreenSharePressureProtocol
{
    public const string Kind = "screenshare";
    public const string PressureStateTypeV1 = "screenshare.pressure_state.v1";
    public const string PressureReasonHealthy = "healthy";
    public const string PressureReasonHighFrameAge = "high_frame_age";
    public const string PressureReasonSlowApplyCadence = "slow_apply_cadence";
    public const string PressureReasonRepeatedStaleDrops = "repeated_stale_drops";
    public const string PressureReasonBridgeHealth = "bridge_health";
    public const string PressureReasonContinuityLoss = "continuity_loss";
}

public sealed record ScreenSharePressureStateV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public ScreenSharePressureMode Mode { get; init; } = ScreenSharePressureMode.Normal;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = ScreenSharePressureProtocol.PressureReasonHealthy;

    [JsonPropertyName("observedFrameAgeMs")]
    public long ObservedFrameAgeMs { get; init; }

    [JsonPropertyName("recentStaleFrameDrops")]
    public long RecentStaleFrameDrops { get; init; }

    [JsonPropertyName("sentAtUtcMs")]
    public long SentAtUtcMs { get; init; }

    [JsonPropertyName("currentEpochWarmupActive")]
    public bool? CurrentEpochWarmupActive { get; init; }

    [JsonPropertyName("currentEpochApplyCount")]
    public int? CurrentEpochApplyCount { get; init; }

    [JsonPropertyName("currentEpochNeedMoreInputCount")]
    public long? CurrentEpochNeedMoreInputCount { get; init; }

    [JsonPropertyName("lastVisibleApplyFrameId")]
    public long? LastVisibleApplyFrameId { get; init; }

    [JsonPropertyName("visibleHeadFrameId")]
    public long? VisibleHeadFrameId { get; init; }

    [JsonPropertyName("visibleRecoveryFloorFrameId")]
    public long? VisibleRecoveryFloorFrameId { get; init; }

    [JsonPropertyName("appliedHeadFrameId")]
    public long? AppliedHeadFrameId { get; init; }

    [JsonPropertyName("steadyVisibleProgressActive")]
    public bool? SteadyVisibleProgressActive { get; init; }

    [JsonPropertyName("stableVisibleHeadFrameId")]
    public long? StableVisibleHeadFrameId { get; init; }

    [JsonPropertyName("framesAppliedSinceLastGap")]
    public long? FramesAppliedSinceLastGap { get; init; }

    [JsonPropertyName("currentEpochRecoveryKeyframeApplyCount")]
    public long? CurrentEpochRecoveryKeyframeApplyCount { get; init; }

}
