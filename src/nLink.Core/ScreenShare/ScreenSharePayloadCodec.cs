using System.Text.Json;

namespace NLink.Core.ScreenShare;

public static class ScreenSharePayloadCodec
{
    public const string ScreenShareFrameTypeV1 = "screenshare.frame.v1";
    public const int MaxChunkRawBytes = 32_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(ScreenShareFrameChunkV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out ScreenShareFrameChunkV1 msg)
    {
        msg = default!;

        if (utf8Json.IsEmpty)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenShareFrameChunkV1>(utf8Json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            if (!string.Equals(parsed.Type, ScreenShareFrameTypeV1, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.SessionId) ||
                parsed.FrameId < 0 ||
                parsed.Width <= 0 ||
                parsed.Height <= 0 ||
                string.IsNullOrWhiteSpace(parsed.Encoding) ||
                parsed.ChunkIndex < 0 ||
                parsed.ChunkCount <= 0 ||
                parsed.ChunkIndex >= parsed.ChunkCount ||
                string.IsNullOrWhiteSpace(parsed.DataBase64))
            {
                return false;
            }

            try
            {
                var chunkBytes = Convert.FromBase64String(parsed.DataBase64);
                if (chunkBytes.Length == 0 || chunkBytes.Length > MaxChunkRawBytes)
                {
                    return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }

            msg = parsed with
            {
                SessionId = parsed.SessionId.Trim(),
                Encoding = parsed.Encoding.Trim(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
