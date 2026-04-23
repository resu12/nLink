using System.Text.Json;

namespace NLink.Core.ScreenShare;

public static class ScreenShareVideoKeyframeRequestCodec
{
    public const string ScreenShareVideoKeyframeRequestTypeV1 = "screenshare.video_keyframe_request.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(ScreenShareVideoKeyframeRequestV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalized = NormalizeForSerialization(message);
        return JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out ScreenShareVideoKeyframeRequestV1 message)
    {
        message = default!;

        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenShareVideoKeyframeRequestV1>(utf8Json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            var normalized = NormalizeForSerialization(parsed);
            message = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ScreenShareVideoKeyframeRequestV1 NormalizeForSerialization(ScreenShareVideoKeyframeRequestV1 message)
    {
        var sessionId = (message.SessionId ?? string.Empty).Trim();
        var reason = string.IsNullOrWhiteSpace(message.Reason) ? "decoder_resync" : message.Reason.Trim();

        if (!string.Equals(message.Kind, "screenshare", StringComparison.Ordinal) ||
            !string.Equals(message.Type, ScreenShareVideoKeyframeRequestTypeV1, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            message.StreamEpoch <= 0)
        {
            throw new InvalidOperationException("Screen share video keyframe request payload is invalid.");
        }

        return message with
        {
            Kind = "screenshare",
            SessionId = sessionId,
            Reason = reason,
        };
    }
}
