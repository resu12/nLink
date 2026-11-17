using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.FileTransfer;

public abstract record FileTransferFallbackBootstrapFrameV6 : FileTransferDataFrame, IFileTransferTransportMetadataFrame
{
    public long TransportEpoch { get; init; }
    public string? BatchId { get; init; }
    public string? RepairRequestId { get; init; }
    public string? Priority { get; init; } = FileTransferProtocol.FallbackV6ProofBootstrapPriority;
    public string? RecoveryMode { get; init; } = FileTransferBootstrapCorrelation.RecoveryMode;
    public string? SenderRequestId { get; init; }
    public string? ReceiverChallengeId { get; init; }
}

public sealed record FileTransferFallbackBootstrapRequestFrameV6 : FileTransferFallbackBootstrapFrameV6
{
    public FileTransferFallbackBootstrapRequestFrameV6()
    {
        Type = FileTransferProtocol.FallbackBootstrapRequestFrameTypeV6;
    }
}

public sealed record FileTransferFallbackBootstrapStateFrameV6 : FileTransferFallbackBootstrapFrameV6
{
    public FileTransferFallbackBootstrapStateFrameV6()
    {
        Type = FileTransferProtocol.FallbackBootstrapStateFrameTypeV6;
    }

    public int Epoch { get; init; }
    public int ContiguousCommittedChunkIndex { get; init; }
    public int DurableReceivedHighestChunkIndex { get; init; }
    public int CreditUntilChunkIndexExclusive { get; init; }
    public long BytesCommitted { get; init; }
    public bool ReceiverMemoryPressure { get; init; }
    public bool ReceiverDiskPressure { get; init; }
    public bool TerminalReady { get; init; }
    public bool TransferPaused { get; init; }
    public string? TransferPauseReason { get; init; }
}

internal readonly record struct FileTransferBootstrapCorrelationId(string IssuerId, long Generation, string ScopeHash);

internal static class FileTransferBootstrapCorrelation
{
    internal const int Version = 1;
    internal const int MaxIdLength = 154;
    internal const string RecoveryMode = "post_tuna_fallback_v6_proof_bootstrap_correlated_v1";

    internal static bool IsIssuerId(string? value)
        => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsValidSelection(int? version, string? issuer)
        => version is null ? issuer is null : version == Version && IsIssuerId(issuer);

    internal static bool TryParseId(string? value, out FileTransferBootstrapCorrelationId id)
    {
        id = default;
        if (value is null || value.Length > MaxIdLength || !value.StartsWith("fbc1:", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length != 4 || !IsIssuerId(parts[1]) || !IsIssuerId(parts[3]) ||
            parts[2].Length == 0 || parts[2][0] is < '1' or > '9' ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ||
            generation <= 0)
        {
            return false;
        }

        id = new(parts[1], generation, parts[3]);
        return true;
    }

    internal static string CreateIssuer(string coordinatorInstanceId, string sessionId, string transferId, FileTransferDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        if (direction is not (FileTransferDirection.Outbound or FileTransferDirection.Inbound))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }
        return HashFields("fallback-bootstrap-issuer-v1", coordinatorInstanceId, sessionId, transferId,
            direction == FileTransferDirection.Outbound ? "outbound" : "inbound");
    }

    // Canonical strings are UTF-8, each prefixed by its unsigned 32-bit big-endian byte count.
    internal static string HashFields(params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static bool IsValidFrame(FileTransferFallbackBootstrapFrameV6 frame)
    {
        if (frame.Kind != FileTransferProtocol.Kind ||
            !FileTransferPayloadCodec.TryNormalizeRequiredEnvelope(frame.Kind, frame.Type, frame.Type,
                frame.SessionId, frame.TransferId, out var session, out var transfer) ||
            session != frame.SessionId || transfer != frame.TransferId ||
            frame.TransportEpoch <= 0 || !string.IsNullOrEmpty(frame.BatchId) || !string.IsNullOrEmpty(frame.RepairRequestId) ||
            frame.Priority != FileTransferProtocol.FallbackV6ProofBootstrapPriority || frame.RecoveryMode != RecoveryMode ||
            frame.SenderRequestId is not null && !TryParseId(frame.SenderRequestId, out _) ||
            frame.ReceiverChallengeId is not null && !TryParseId(frame.ReceiverChallengeId, out _))
        {
            return false;
        }

        return frame switch
        {
            FileTransferFallbackBootstrapRequestFrameV6 => frame.Type == FileTransferProtocol.FallbackBootstrapRequestFrameTypeV6 &&
                frame.SenderRequestId is not null,
            FileTransferFallbackBootstrapStateFrameV6 state => frame.Type == FileTransferProtocol.FallbackBootstrapStateFrameTypeV6 &&
                state.ReceiverChallengeId is not null && state.Epoch > 0 &&
                state.ContiguousCommittedChunkIndex >= 0 && state.ContiguousCommittedChunkIndex <= FileTransferProtocol.MaxChunkCountV6 &&
                state.DurableReceivedHighestChunkIndex >= state.ContiguousCommittedChunkIndex - 1 &&
                state.DurableReceivedHighestChunkIndex < FileTransferProtocol.MaxChunkCountV6 &&
                state.CreditUntilChunkIndexExclusive == state.ContiguousCommittedChunkIndex && state.BytesCommitted >= 0 &&
                !state.ReceiverMemoryPressure && !state.ReceiverDiskPressure && !state.TerminalReady && state.TransferPaused &&
                state.TransferPauseReason == RecoveryMode,
            _ => false,
        };
    }
}
