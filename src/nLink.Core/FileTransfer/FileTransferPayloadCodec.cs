using System.Text.Encodings.Web;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public static class FileTransferPayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(FileTransferOfferV2 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferAcceptV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferDeclineV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferStartV2 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferChunkV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        var payload = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        if (payload.Length > FileTransferProtocol.MaxSerializedChunkPayloadBytes)
        {
            var diagnostics = BuildChunkSerializationDiagnostics(msg, payload.Length);
            LocalOperationalLog.Warn("FileTransferPayload", $"event=serialize_chunk_payload_budget_exceeded; {diagnostics}");
            throw new InvalidOperationException(
                $"Serialized file-transfer chunk payload exceeded safe budget of {FileTransferProtocol.MaxSerializedChunkPayloadBytes} bytes ({diagnostics}).");
        }

        return payload;
    }

    public static byte[] Serialize(FileTransferWindowUpdateV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferMissingRangeV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferPressureStateV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferSessionOpenV2 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static int ComputeSafeRawChunkSizeForBudget(
        string sessionId,
        string transferId,
        int chunkCount,
        int requestedMaxChunkSize)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(transferId))
        {
            throw new ArgumentException("Transfer id is required.", nameof(transferId));
        }

        if (chunkCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCount), "Chunk count must be positive.");
        }

        return FileTransferChunkBudget.ComputeLargestFittingRawChunkSize(
            requestedMaxChunkSize,
            candidate => DoesChunkPayloadFitBudget(sessionId, transferId, chunkCount, candidate),
            "No valid file-transfer chunk size fits within the payload budget.");
    }

    public static byte[] Serialize(FileTransferCancelV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferErrorV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferCompleteV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static bool TryDeserializeOffer(ReadOnlySpan<byte> utf8Json, out FileTransferOfferV2 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferOfferV2? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.OfferTypeV2, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !TryNormalizeFileName(parsed.FileName, out var fileName) ||
            parsed.FileSizeBytes <= 0 ||
            !TryNormalizeOptionalProtocolVersion(parsed.PreferredDataProtocolVersion, out var preferredDataProtocolVersion))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.OfferTypeV2,
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            PreferredDataProtocolVersion = preferredDataProtocolVersion,
        };
        return true;
    }

    public static bool TryDeserializeAccept(ReadOnlySpan<byte> utf8Json, out FileTransferAcceptV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferAcceptV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.AcceptTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !TryNormalizeOptionalProtocolVersion(parsed.AcceptedDataProtocolVersion, out var acceptedDataProtocolVersion))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.AcceptTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            AcceptedDataProtocolVersion = acceptedDataProtocolVersion,
        };
        return true;
    }

    public static bool TryDeserializeDecline(ReadOnlySpan<byte> utf8Json, out FileTransferDeclineV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferDeclineV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.DeclineTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !TryNormalizeOptional(parsed.Reason, FileTransferProtocol.MaxReasonLength, out var reason))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.DeclineTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            Reason = reason,
        };
        return true;
    }

    public static bool TryDeserializeStart(ReadOnlySpan<byte> utf8Json, out FileTransferStartV2 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferStartV2? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.StartTypeV2, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !TryNormalizeFileName(parsed.FileName, out var fileName) ||
            parsed.FileSizeBytes <= 0 ||
            !TryNormalizeSha256(parsed.Sha256Base64, out var sha256Base64) ||
            parsed.ChunkCount <= 0 ||
            parsed.ChunkSizeBytes <= 0 ||
            parsed.ChunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes)
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.StartTypeV2,
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            Sha256Base64 = sha256Base64,
        };
        return true;
    }

    public static bool TryDeserializeChunk(ReadOnlySpan<byte> utf8Json, out FileTransferChunkV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferChunkV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.ChunkTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.ChunkIndex < 0 ||
            parsed.ChunkCount <= 0 ||
            parsed.ChunkIndex >= parsed.ChunkCount ||
            !TryNormalizeChunkData(parsed.DataBase64, out var dataBase64))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.ChunkTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            DataBase64 = dataBase64,
        };
        return true;
    }

    public static bool TryDeserializeCancel(ReadOnlySpan<byte> utf8Json, out FileTransferCancelV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferCancelV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.CancelTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !TryNormalizeOptional(parsed.Reason, FileTransferProtocol.MaxReasonLength, out var reason))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.CancelTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            Reason = reason,
        };
        return true;
    }

    public static bool TryDeserializeSessionOpen(ReadOnlySpan<byte> utf8Json, out FileTransferSessionOpenV2 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferSessionOpenV2? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.SessionOpenTypeV2, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !IsSupportedDataProtocolVersion(parsed.ProtocolVersion) ||
            !TryNormalizeSessionRole(parsed.SessionRole, out var sessionRole) ||
            parsed.ChunkSizeBytes <= 0 ||
            parsed.ChunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes ||
            parsed.InitialPipelineDepth <= 0)
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.SessionOpenTypeV2,
            SessionId = sessionId,
            TransferId = transferId,
            SessionRole = sessionRole,
        };
        return true;
    }

    internal static bool IsSupportedDataProtocolVersion(int protocolVersion)
        => protocolVersion is FileTransferProtocol.ProtocolVersionV2
            or FileTransferProtocol.ProtocolVersionV3
            or FileTransferProtocol.ProtocolVersionV4;

    internal static bool TryNormalizeOptionalProtocolVersion(int? protocolVersion, out int? normalizedProtocolVersion)
    {
        normalizedProtocolVersion = null;
        if (protocolVersion is null)
        {
            return true;
        }

        if (!IsSupportedDataProtocolVersion(protocolVersion.Value))
        {
            return false;
        }

        normalizedProtocolVersion = protocolVersion.Value;
        return true;
    }

    public static bool TryDeserializeWindowUpdate(ReadOnlySpan<byte> utf8Json, out FileTransferWindowUpdateV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferWindowUpdateV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.WindowUpdateTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.NextExpectedChunkIndex < 0 ||
            parsed.GrantedUntilChunkIndexExclusive < 0 ||
            parsed.GrantedUntilChunkIndexExclusive < parsed.NextExpectedChunkIndex ||
            parsed.BytesReceived < 0)
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.WindowUpdateTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
        };
        return true;
    }

    public static bool TryDeserializeMissingRange(ReadOnlySpan<byte> utf8Json, out FileTransferMissingRangeV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferMissingRangeV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.MissingRangeTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.StartChunkIndex < 0 ||
            parsed.EndChunkIndexExclusive <= parsed.StartChunkIndex)
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.MissingRangeTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
        };
        return true;
    }

    public static bool TryDeserializePressureState(ReadOnlySpan<byte> utf8Json, out FileTransferPressureStateV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferPressureStateV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.PressureStateTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.Revision < 0 ||
            parsed.SuggestedSendAheadChunks < 0 ||
            parsed.ReceiverNextExpectedChunkIndex < 0 ||
            !TryNormalizePressureMode(parsed.Mode, out var mode) ||
            !TryNormalizePressureReason(parsed.Reason, out var reason))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.PressureStateTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            Mode = mode,
            Reason = reason,
        };
        return true;
    }

    public static bool TryDeserializeError(ReadOnlySpan<byte> utf8Json, out FileTransferErrorV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferErrorV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.ErrorTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            !TryNormalizeRequiredBounded(parsed.ErrorCode, FileTransferProtocol.MaxErrorCodeLength, out var errorCode) ||
            !TryNormalizeOptional(parsed.Message, FileTransferProtocol.MaxErrorMessageLength, out var message))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.ErrorTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            ErrorCode = errorCode,
            Message = message,
        };
        return true;
    }

    public static bool TryDeserializeComplete(ReadOnlySpan<byte> utf8Json, out FileTransferCompleteV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferCompleteV1? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.CompleteTypeV1, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.FileSizeBytes <= 0 ||
            !TryNormalizeSha256(parsed.Sha256Base64, out var sha256Base64))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.CompleteTypeV1,
            SessionId = sessionId,
            TransferId = transferId,
            Sha256Base64 = sha256Base64,
        };
        return true;
    }

    private static bool TryDeserialize<T>(ReadOnlySpan<byte> utf8Json, out T? value)
    {
        value = default;
        if (utf8Json.IsEmpty)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(utf8Json, JsonOptions);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryNormalizeRequiredEnvelope(
        string? kind,
        string? type,
        string expectedType,
        string? sessionId,
        string? transferId,
        out string normalizedSessionId,
        out string normalizedTransferId)
    {
        normalizedSessionId = string.Empty;
        normalizedTransferId = string.Empty;

        if (!string.IsNullOrWhiteSpace(kind) &&
            !string.Equals(kind.Trim(), FileTransferProtocol.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(type?.Trim(), expectedType, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryNormalizeRequiredBounded(transferId, FileTransferProtocol.MaxTransferIdLength, out normalizedTransferId))
        {
            return false;
        }

        if (!TryNormalizeRequiredToken(sessionId, out normalizedSessionId))
        {
            return false;
        }

        return true;
    }

    private static bool TryNormalizeRequiredToken(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length > 0;
    }

    private static bool TryNormalizeRequiredBounded(string? value, int maxLength, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalizeRequiredToken(value, out normalized))
        {
            return false;
        }

        return normalized.Length <= maxLength;
    }

    internal static bool TryNormalizeOptional(string? value, int maxLength, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Length == 0 || candidate.Length > maxLength)
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    internal static bool TryNormalizeFileName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalizeRequiredBounded(value, FileTransferProtocol.MaxFileNameLength, out normalized))
        {
            return false;
        }

        return normalized.Length > 0;
    }

    internal static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value.Trim());
            if (bytes.Length != FileTransferProtocol.Sha256LengthBytes)
            {
                return false;
            }

            normalized = Convert.ToBase64String(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool TryNormalizeChunkData(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value.Trim());
            if (bytes.Length == 0 || bytes.Length > FileTransferProtocol.MaxChunkRawBytes)
            {
                return false;
            }

            normalized = Convert.ToBase64String(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryNormalizePressureMode(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalizeRequiredToken(value, out var candidate))
        {
            return false;
        }

        normalized = candidate switch
        {
            var mode when string.Equals(mode, FileTransferProtocol.PressureModeNormal, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.PressureModeNormal,
            var mode when string.Equals(mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.PressureModeCatchUpOnly,
            _ => string.Empty,
        };

        return normalized.Length > 0;
    }

    private static bool TryNormalizePressureReason(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalizeRequiredToken(value, out var candidate))
        {
            return false;
        }

        normalized = candidate switch
        {
            var reason when string.Equals(reason, FileTransferProtocol.PressureReasonGapRepair, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.PressureReasonGapRepair,
            var reason when string.Equals(reason, FileTransferProtocol.PressureReasonMediaProtection, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.PressureReasonMediaProtection,
            var reason when string.Equals(reason, FileTransferProtocol.PressureReasonBulkBacklog, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.PressureReasonBulkBacklog,
            _ => string.Empty,
        };

        return normalized.Length > 0;
    }

    private static bool TryNormalizeSessionRole(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalizeRequiredToken(value, out var candidate))
        {
            return false;
        }

        normalized = candidate switch
        {
            var role when string.Equals(role, FileTransferProtocol.SessionRoleSender, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.SessionRoleSender,
            var role when string.Equals(role, FileTransferProtocol.SessionRoleReceiver, StringComparison.OrdinalIgnoreCase)
                => FileTransferProtocol.SessionRoleReceiver,
            _ => string.Empty,
        };

        return normalized.Length > 0;
    }

    private static string BuildChunkSerializationDiagnostics(FileTransferChunkV1 msg, int serializedLength)
    {
        var rawChunkBytes = EstimateRawBytesFromBase64(msg.DataBase64);
        return string.Join(
            "; ",
            $"serialized_bytes={serializedLength}",
            $"budget_bytes={FileTransferProtocol.MaxSerializedChunkPayloadBytes}",
            $"raw_chunk_bytes={rawChunkBytes}",
            $"base64_bytes={msg.DataBase64.Length}",
            $"session_id_len={msg.SessionId?.Length ?? 0}",
            $"transfer_id_len={msg.TransferId?.Length ?? 0}",
            $"chunk={msg.ChunkIndex + 1}/{msg.ChunkCount}");
    }

    private static bool DoesChunkPayloadFitBudget(
        string sessionId,
        string transferId,
        int chunkCount,
        int rawChunkBytes)
    {
        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(
                new FileTransferChunkV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ChunkIndex = Math.Max(0, chunkCount - 1),
                    ChunkCount = chunkCount,
                    DataBase64 = Convert.ToBase64String(new byte[rawChunkBytes]),
                },
                JsonOptions);
            return serialized.Length <= FileTransferProtocol.MaxSerializedChunkPayloadBytes;
        }
        catch
        {
            return false;
        }
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
