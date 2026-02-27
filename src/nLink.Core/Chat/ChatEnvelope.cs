using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLink.Core.Chat;

public static class ChatProtocol
{
    public const int Version = 1;
    public const string ChatMessageType = "chat.message";
}

public sealed class ChatEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("t")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("n")]
    public string NonceBase64 { get; init; } = string.Empty;

    [JsonPropertyName("g")]
    public string TagBase64 { get; init; } = string.Empty;

    [JsonPropertyName("c")]
    public string CiphertextBase64 { get; init; } = string.Empty;
}

public sealed class ChatMessagePayload
{
    [JsonPropertyName("id")]
    public string MessageId { get; init; } = string.Empty;

    [JsonPropertyName("ts")]
    public long TimestampUnixMilliseconds { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

public static class ChatEnvelopeCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] SerializeEnvelope(ChatEnvelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    public static ChatEnvelope DeserializeEnvelope(ReadOnlySpan<byte> data)
    {
        return JsonSerializer.Deserialize<ChatEnvelope>(data, JsonOptions)
               ?? throw new InvalidOperationException("Chat envelope is empty.");
    }

    public static byte[] SerializePayload(ChatMessagePayload payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    public static ChatMessagePayload DeserializePayload(ReadOnlySpan<byte> data)
    {
        return JsonSerializer.Deserialize<ChatMessagePayload>(data, JsonOptions)
               ?? throw new InvalidOperationException("Chat payload is empty.");
    }
}
