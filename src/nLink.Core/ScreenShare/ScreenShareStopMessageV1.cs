using System.Text.Json.Serialization;

namespace NLink.Core.ScreenShare;

public sealed record ScreenShareStopMessageV1
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "screenshare";

    [JsonPropertyName("type")]
    public string Type { get; init; } = ScreenSharePayloadCodec.ScreenShareStopTypeV1;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
