using System.Text.Json.Serialization;

namespace NLink.Core.ScreenShare;

public sealed record ScreenShareFrameChunkV1
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ScreenSharePayloadCodec.ScreenShareFrameTypeV1;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("frameId")]
    public long FrameId { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("encoding")]
    public string Encoding { get; init; } = "jpeg";

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; init; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("dataBase64")]
    public string DataBase64 { get; init; } = string.Empty;
}
