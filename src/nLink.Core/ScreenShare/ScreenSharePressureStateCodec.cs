using System.Text.Json;

namespace NLink.Core.ScreenShare;

public static class ScreenSharePressureStateCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Serialize(ScreenSharePressureStateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedSessionId = NormalizeRequired(message.SessionId);
        var normalizedReason = NormalizeOptional(message.Reason, 64) ?? ScreenSharePressureProtocol.PressureReasonHealthy;
        return JsonSerializer.SerializeToUtf8Bytes(
            new PressureStateEnvelope
            {
                Kind = ScreenSharePressureProtocol.Kind,
                Type = ScreenSharePressureProtocol.PressureStateTypeV1,
                SessionId = normalizedSessionId,
                Mode = FormatMode(message.Mode),
                Reason = normalizedReason,
                ObservedFrameAgeMs = Math.Max(0, message.ObservedFrameAgeMs),
                RecentStaleFrameDrops = Math.Max(0, message.RecentStaleFrameDrops),
                SentAtUtcMs = Math.Max(0, message.SentAtUtcMs),
                CurrentEpochWarmupActive = message.CurrentEpochWarmupActive,
                CurrentEpochApplyCount = message.CurrentEpochApplyCount is { } currentEpochApplyCount
                    ? Math.Max(0, currentEpochApplyCount)
                    : null,
                CurrentEpochNeedMoreInputCount = message.CurrentEpochNeedMoreInputCount is { } currentEpochNeedMoreInputCount
                    ? Math.Max(0, currentEpochNeedMoreInputCount)
                    : null,
                LastVisibleApplyFrameId = message.LastVisibleApplyFrameId is { } lastVisibleApplyFrameId
                    ? Math.Max(-1, lastVisibleApplyFrameId)
                    : null,
                VisibleHeadFrameId = message.VisibleHeadFrameId is { } visibleHeadFrameId
                    ? Math.Max(-1, visibleHeadFrameId)
                    : null,
                VisibleRecoveryFloorFrameId = message.VisibleRecoveryFloorFrameId is { } visibleRecoveryFloorFrameId
                    ? Math.Max(-1, visibleRecoveryFloorFrameId)
                    : null,
                AppliedHeadFrameId = message.AppliedHeadFrameId is { } appliedHeadFrameId
                    ? Math.Max(-1, appliedHeadFrameId)
                    : null,
                SteadyVisibleProgressActive = message.SteadyVisibleProgressActive,
                StableVisibleHeadFrameId = message.StableVisibleHeadFrameId is { } stableVisibleHeadFrameId
                    ? Math.Max(-1, stableVisibleHeadFrameId)
                    : null,
                FramesAppliedSinceLastGap = message.FramesAppliedSinceLastGap is { } framesAppliedSinceLastGap
                    ? Math.Max(0, framesAppliedSinceLastGap)
                    : null,
                CurrentEpochRecoveryKeyframeApplyCount =
                    message.CurrentEpochRecoveryKeyframeApplyCount is { } currentEpochRecoveryKeyframeApplyCount
                        ? Math.Max(0, currentEpochRecoveryKeyframeApplyCount)
                        : null,
            },
            JsonOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out ScreenSharePressureStateV1 message)
    {
        message = default!;
        try
        {
            var parsed = JsonSerializer.Deserialize<PressureStateEnvelope>(utf8Json, JsonOptions);
            if (parsed is null ||
                !string.Equals(parsed.Kind, ScreenSharePressureProtocol.Kind, StringComparison.Ordinal) ||
                !string.Equals(parsed.Type, ScreenSharePressureProtocol.PressureStateTypeV1, StringComparison.Ordinal) ||
                !TryParseMode(parsed.Mode, out var mode) ||
                !TryNormalizeRequired(parsed.SessionId, out var sessionId))
            {
                return false;
            }

            var reason = NormalizeOptional(parsed.Reason, 64) ?? ScreenSharePressureProtocol.PressureReasonHealthy;
            message = new ScreenSharePressureStateV1
            {
                SessionId = sessionId,
                Mode = mode,
                Reason = reason,
                ObservedFrameAgeMs = Math.Max(0, parsed.ObservedFrameAgeMs),
                RecentStaleFrameDrops = Math.Max(0, parsed.RecentStaleFrameDrops),
                SentAtUtcMs = Math.Max(0, parsed.SentAtUtcMs),
                CurrentEpochWarmupActive = parsed.CurrentEpochWarmupActive,
                CurrentEpochApplyCount = parsed.CurrentEpochApplyCount is { } currentEpochApplyCount
                    ? Math.Max(0, currentEpochApplyCount)
                    : null,
                CurrentEpochNeedMoreInputCount = parsed.CurrentEpochNeedMoreInputCount is { } currentEpochNeedMoreInputCount
                    ? Math.Max(0, currentEpochNeedMoreInputCount)
                    : null,
                LastVisibleApplyFrameId = parsed.LastVisibleApplyFrameId is { } lastVisibleApplyFrameId
                    ? Math.Max(-1, lastVisibleApplyFrameId)
                    : null,
                VisibleHeadFrameId = parsed.VisibleHeadFrameId is { } visibleHeadFrameId
                    ? Math.Max(-1, visibleHeadFrameId)
                    : null,
                VisibleRecoveryFloorFrameId = parsed.VisibleRecoveryFloorFrameId is { } visibleRecoveryFloorFrameId
                    ? Math.Max(-1, visibleRecoveryFloorFrameId)
                    : null,
                AppliedHeadFrameId = parsed.AppliedHeadFrameId is { } appliedHeadFrameId
                    ? Math.Max(-1, appliedHeadFrameId)
                    : null,
                SteadyVisibleProgressActive = parsed.SteadyVisibleProgressActive,
                StableVisibleHeadFrameId = parsed.StableVisibleHeadFrameId is { } stableVisibleHeadFrameId
                    ? Math.Max(-1, stableVisibleHeadFrameId)
                    : null,
                FramesAppliedSinceLastGap = parsed.FramesAppliedSinceLastGap is { } framesAppliedSinceLastGap
                    ? Math.Max(0, framesAppliedSinceLastGap)
                    : null,
                CurrentEpochRecoveryKeyframeApplyCount =
                    parsed.CurrentEpochRecoveryKeyframeApplyCount is { } currentEpochRecoveryKeyframeApplyCount
                        ? Math.Max(0, currentEpochRecoveryKeyframeApplyCount)
                        : null,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatMode(ScreenSharePressureMode mode)
        => mode switch
        {
            ScreenSharePressureMode.ReduceFps => "reduce_fps",
            ScreenSharePressureMode.CatchUpOnly => "catch_up_only",
            _ => "normal",
        };

    private static bool TryParseMode(string? text, out ScreenSharePressureMode mode)
    {
        mode = ScreenSharePressureMode.Normal;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim())
        {
            case "normal":
                mode = ScreenSharePressureMode.Normal;
                return true;
            case "reduce_fps":
                mode = ScreenSharePressureMode.ReduceFps;
                return true;
            case "catch_up_only":
                mode = ScreenSharePressureMode.CatchUpOnly;
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeRequired(string value)
        => TryNormalizeRequired(value, out var normalized)
            ? normalized
            : throw new ArgumentException("Session id is required.", nameof(value));

    private static bool TryNormalizeRequired(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length > 0;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class PressureStateEnvelope
    {
        public string Kind { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string SessionId { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;

        public string? Reason { get; init; }

        public long ObservedFrameAgeMs { get; init; }

        public long RecentStaleFrameDrops { get; init; }

        public long SentAtUtcMs { get; init; }

        public bool? CurrentEpochWarmupActive { get; init; }

        public int? CurrentEpochApplyCount { get; init; }

        public long? CurrentEpochNeedMoreInputCount { get; init; }

        public long? LastVisibleApplyFrameId { get; init; }

        public long? VisibleHeadFrameId { get; init; }

        public long? VisibleRecoveryFloorFrameId { get; init; }

        public long? AppliedHeadFrameId { get; init; }

        public bool? SteadyVisibleProgressActive { get; init; }

        public long? StableVisibleHeadFrameId { get; init; }

        public long? FramesAppliedSinceLastGap { get; init; }

        public long? CurrentEpochRecoveryKeyframeApplyCount { get; init; }
    }
}
