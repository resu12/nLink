namespace NLink.Core.ScreenShare;

public sealed record ScreenShareVideoKeyframeRequestV1
{
    public string Kind { get; init; } = "screenshare";

    public string Type { get; init; } = ScreenShareVideoKeyframeRequestCodec.ScreenShareVideoKeyframeRequestTypeV1;

    public string SessionId { get; init; } = string.Empty;

    public long StreamEpoch { get; init; }

    public string Reason { get; init; } = "decoder_resync";
}
