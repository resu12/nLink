namespace NLink.Core.ScreenShare;

public sealed record ScreenShareStopMessageV1
{
    public string Kind { get; init; } = "screenshare";

    public string Type { get; init; } = ScreenSharePayloadCodec.ScreenShareStopTypeV1;

    public string SessionId { get; init; } = string.Empty;

    public string? Reason { get; init; }
}
