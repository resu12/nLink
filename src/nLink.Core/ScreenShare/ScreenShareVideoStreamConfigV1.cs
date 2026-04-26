namespace NLink.Core.ScreenShare;

public sealed record ScreenShareVideoStreamConfigV1
{
    public string Kind { get; init; } = "screenshare";

    public string Type { get; init; } = ScreenShareVideoPayloadCodec.ScreenShareVideoStreamConfigTypeV1;

    public string SessionId { get; init; } = string.Empty;

    public long StreamEpoch { get; init; }

    public string Encoding { get; init; } = "h264";

    public string CodecProfile { get; init; } = "unknown";

    public long DisplayInfoRevision { get; init; }

    public byte[] DecoderConfigData { get; init; } = Array.Empty<byte>();
}
