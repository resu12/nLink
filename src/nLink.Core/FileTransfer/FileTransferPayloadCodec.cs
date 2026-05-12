using System.Text.Encodings.Web;
using System.Text.Json;

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

    public static byte[] Serialize(FileTransferSessionOpenV2 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
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

    public static byte[] Serialize(FileTransferPauseControlV6 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferHeartbeatV6 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferTransportEpochV6 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferTransportProbeV6 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(FileTransferRepairProofV6 msg)
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
            !TryNormalizeRequiredProtocolVersion(parsed.PreferredDataProtocolVersion, out var preferredDataProtocolVersion))
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
            !TryNormalizeRequiredProtocolVersion(parsed.AcceptedDataProtocolVersion, out var acceptedDataProtocolVersion))
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
        => protocolVersion == FileTransferProtocol.ProtocolVersionV6;

    internal static bool TryNormalizeRequiredProtocolVersion(int? protocolVersion, out int normalizedProtocolVersion)
    {
        normalizedProtocolVersion = 0;
        if (protocolVersion is null || !IsSupportedDataProtocolVersion(protocolVersion.Value))
        {
            return false;
        }

        normalizedProtocolVersion = protocolVersion.Value;
        return true;
    }

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

    public static bool TryDeserializePauseControl(ReadOnlySpan<byte> utf8Json, out FileTransferPauseControlV6 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferPauseControlV6? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.PauseControlFrameTypeV6, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.Epoch < 0 ||
            parsed.TransportEpoch < 0 ||
            !TryNormalizeOptional(parsed.Reason, FileTransferProtocol.MaxReasonLength, out var reason) ||
            !TryNormalizeOptional(parsed.BatchId, FileTransferProtocol.MaxReasonLength, out var batchId) ||
            !TryNormalizeOptional(parsed.RepairRequestId, FileTransferProtocol.MaxReasonLength, out var repairRequestId) ||
            !TryNormalizeOptional(parsed.Priority, FileTransferProtocol.MaxReasonLength, out var priority) ||
            !TryNormalizeOptional(parsed.RecoveryMode, FileTransferProtocol.MaxReasonLength, out var recoveryMode))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.PauseControlFrameTypeV6,
            SessionId = sessionId,
            TransferId = transferId,
            Reason = reason,
            BatchId = batchId,
            RepairRequestId = repairRequestId,
            Priority = priority,
            RecoveryMode = recoveryMode,
        };
        return true;
    }

    public static bool TryDeserializeHeartbeat(ReadOnlySpan<byte> utf8Json, out FileTransferHeartbeatV6 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferHeartbeatV6? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.HeartbeatFrameTypeV6, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.TransportEpoch < 0 ||
            parsed.Sequence <= 0 ||
            parsed.SentUnixTimeMilliseconds <= 0)
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.HeartbeatFrameTypeV6,
            SessionId = sessionId,
            TransferId = transferId,
        };
        return true;
    }

    public static bool TryDeserializeTransportEpoch(ReadOnlySpan<byte> utf8Json, out FileTransferTransportEpochV6 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferTransportEpochV6? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.TransportEpochFrameTypeV6, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.TransportEpoch <= 0 ||
            !TryNormalizeRequiredBounded(parsed.State, FileTransferProtocol.MaxReasonLength, out var state) ||
            !TryNormalizeOptional(parsed.HandoffKind, FileTransferProtocol.MaxReasonLength, out var handoffKind) ||
            !TryNormalizeOptional(parsed.SourceTransport, FileTransferProtocol.MaxReasonLength, out var sourceTransport) ||
            !TryNormalizeOptional(parsed.TargetTransport, FileTransferProtocol.MaxReasonLength, out var targetTransport) ||
            !TryNormalizeOptional(parsed.Reason, FileTransferProtocol.MaxReasonLength, out var reason) ||
            !TryNormalizeOptional(parsed.RecoveryMode, FileTransferProtocol.MaxReasonLength, out var recoveryMode))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.TransportEpochFrameTypeV6,
            SessionId = sessionId,
            TransferId = transferId,
            State = state,
            HandoffKind = handoffKind,
            SourceTransport = sourceTransport,
            TargetTransport = targetTransport,
            Reason = reason,
            RecoveryMode = recoveryMode,
        };
        return true;
    }

    public static bool TryDeserializeTransportProbe(ReadOnlySpan<byte> utf8Json, out FileTransferTransportProbeV6 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferTransportProbeV6? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.TransportProbeFrameTypeV6, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.TransportEpoch <= 0 ||
            !TryNormalizeOptional(parsed.ProbeId, FileTransferProtocol.MaxReasonLength, out var probeId) ||
            !TryNormalizeOptional(parsed.TargetTransport, FileTransferProtocol.MaxReasonLength, out var targetTransport))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.TransportProbeFrameTypeV6,
            SessionId = sessionId,
            TransferId = transferId,
            ProbeId = probeId,
            TargetTransport = targetTransport,
        };
        return true;
    }

    public static bool TryDeserializeRepairProof(ReadOnlySpan<byte> utf8Json, out FileTransferRepairProofV6 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out FileTransferRepairProofV6? parsed) ||
            parsed is null ||
            !TryNormalizeRequiredEnvelope(parsed.Kind, parsed.Type, FileTransferProtocol.RepairProofFrameTypeV6, parsed.SessionId, parsed.TransferId, out var sessionId, out var transferId) ||
            parsed.TransportEpoch <= 0 ||
            parsed.AppliedChunkCount < 0 ||
            parsed.CommittedChunkIndex < 0 ||
            !TryNormalizeRequiredBounded(parsed.RepairRequestId, FileTransferProtocol.MaxReasonLength, out var repairRequestId) ||
            !TryNormalizeOptional(parsed.RecoveryMode, FileTransferProtocol.MaxReasonLength, out var recoveryMode))
        {
            return false;
        }

        msg = parsed with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.RepairProofFrameTypeV6,
            SessionId = sessionId,
            TransferId = transferId,
            RepairRequestId = repairRequestId,
            RecoveryMode = recoveryMode,
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

}
