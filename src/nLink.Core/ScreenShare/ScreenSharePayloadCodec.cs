using System.Text.Encodings.Web;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Core.ScreenShare;

public static class ScreenSharePayloadCodec
{
    public const string ScreenShareFrameTypeV1 = "screenshare.frame.v1";
    public const string ScreenShareStopTypeV1 = "screenshare.stop.v1";
    // Keep each serialized screenshare message comfortably below the transport
    // budgets that have been stable in practice, while reducing chunks/frame.
    public const int MaxChunkRawBytes = 12_000;
    public const int MaxSerializedFramePayloadBytes = 18_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(ScreenShareFrameChunkV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        var payload = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        if (payload.Length > MaxSerializedFramePayloadBytes)
        {
            var diagnostics = BuildFrameChunkSerializationDiagnostics(msg, payload.Length);
            LocalOperationalLog.Warn("ScreenSharePayload", $"event=serialize_frame_payload_budget_exceeded; {diagnostics}");
            throw new InvalidOperationException(
                $"Serialized screenshare frame payload exceeded safe budget of {MaxSerializedFramePayloadBytes} bytes ({diagnostics}).");
        }

        return payload;
    }

    public static byte[] SerializeStop(ScreenShareStopMessageV1 msg)
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

            if (!string.IsNullOrWhiteSpace(parsed.Kind) &&
                !string.Equals(parsed.Kind, "screenshare", StringComparison.Ordinal))
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
                Kind = string.IsNullOrWhiteSpace(parsed.Kind) ? "screenshare" : parsed.Kind.Trim(),
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

    public static bool TryDeserializeStop(ReadOnlySpan<byte> utf8Json, out ScreenShareStopMessageV1 msg)
    {
        msg = default!;

        if (utf8Json.IsEmpty)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenShareStopMessageV1>(utf8Json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Kind) &&
                !string.Equals(parsed.Kind, "screenshare", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(parsed.Type, ScreenShareStopTypeV1, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.SessionId))
            {
                return false;
            }

            msg = parsed with
            {
                Kind = string.IsNullOrWhiteSpace(parsed.Kind) ? "screenshare" : parsed.Kind.Trim(),
                SessionId = parsed.SessionId.Trim(),
                Reason = string.IsNullOrWhiteSpace(parsed.Reason) ? null : parsed.Reason.Trim(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFrameChunkSerializationDiagnostics(ScreenShareFrameChunkV1 msg, int serializedLength)
    {
        var rawChunkBytes = EstimateRawBytesFromBase64(msg.DataBase64);
        return string.Join(
            "; ",
            $"serialized_bytes={serializedLength}",
            $"budget_bytes={MaxSerializedFramePayloadBytes}",
            $"raw_chunk_bytes={rawChunkBytes}",
            $"base64_bytes={msg.DataBase64.Length}",
            $"session_id_len={msg.SessionId?.Length ?? 0}",
            $"frame_id={msg.FrameId}",
            $"frame={msg.Width}x{msg.Height}",
            $"chunk={msg.ChunkIndex + 1}/{msg.ChunkCount}",
            $"encoding={msg.Encoding}");
    }

    private static int EstimateRawBytesFromBase64(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        var length = base64.Length;
        var padding = 0;
        if (length > 0 && base64[^1] == '=')
        {
            padding++;
        }

        if (length > 1 && base64[^2] == '=')
        {
            padding++;
        }

        return Math.Max(0, (length / 4 * 3) - padding);
    }
}
