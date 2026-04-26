using System.Text.Json;

namespace NLink.Core.ScreenShare;

public static class ScreenShareRecoveryReceiptCodec
{
    public const string ScreenShareRecoveryReceiptTypeV1 = "screenshare.recovery_receipt.v1";
    public const string RecoveryKeyframeVisibleReceiptKind = "recovery_keyframe_visible";
    public const string VisibleProgressAfterRecoveryKeyframeReceiptKind = "visible_progress_after_recovery_keyframe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(ScreenShareRecoveryReceiptV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalized = NormalizeForSerialization(message);
        return JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out ScreenShareRecoveryReceiptV1 message)
    {
        message = default!;

        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenShareRecoveryReceiptV1>(utf8Json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            message = NormalizeForSerialization(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ScreenShareRecoveryReceiptV1 NormalizeForSerialization(ScreenShareRecoveryReceiptV1 message)
    {
        var sessionId = (message.SessionId ?? string.Empty).Trim();
        var receiptKind = (message.ReceiptKind ?? string.Empty).Trim();

        if (!string.Equals(message.Kind, "screenshare", StringComparison.Ordinal) ||
            !string.Equals(message.Type, ScreenShareRecoveryReceiptTypeV1, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            message.StreamEpoch <= 0 ||
            message.OwnerFrameId < 0 ||
            message.VisibleRecoveryFrameId < 0 ||
            message.VisibleHeadFrameId < 0 ||
            message.VisibleHeadFrameId < message.VisibleRecoveryFrameId ||
            !IsValidReceiptKind(receiptKind))
        {
            throw new InvalidOperationException("Screen share recovery receipt payload is invalid.");
        }

        return message with
        {
            Kind = "screenshare",
            SessionId = sessionId,
            ReceiptKind = receiptKind,
        };
    }

    private static bool IsValidReceiptKind(string receiptKind)
    {
        return string.Equals(receiptKind, RecoveryKeyframeVisibleReceiptKind, StringComparison.Ordinal) ||
               string.Equals(receiptKind, VisibleProgressAfterRecoveryKeyframeReceiptKind, StringComparison.Ordinal);
    }
}
