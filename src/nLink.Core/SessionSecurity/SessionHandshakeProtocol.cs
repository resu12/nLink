using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public sealed record SessionHandshakeStart(
    SessionId SessionId,
    PeerAddress HelperAddress,
    string? InviteToken);

public sealed record SessionHandshakeChallenge(
    SessionId SessionId,
    PeerAddress HelpeeAddress,
    string ChallengeNonce,
    long ExpiresAtUtcMs,
    string HelpeeEcdhPublicKeyBase64);

public sealed record SessionHandshakeResponse(
    SessionId SessionId,
    PeerAddress HelperAddress,
    string ChallengeNonce,
    string MacBase64);

public sealed record SessionHandshakeResult(
    SessionId SessionId,
    bool Verified,
    string? FailureReason);

public static class SessionSecurityDefaults
{
    public static TimeSpan HandshakeTimeout { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan GrantLifetime { get; } = TimeSpan.FromMinutes(30);
    public static CapabilityGrant AllCapabilityGrants { get; } =
        CapabilityGrant.Chat |
        CapabilityGrant.ScreenShare |
        CapabilityGrant.RemoteControl |
        CapabilityGrant.FileTransfer |
        CapabilityGrant.Clipboard;
    public static CapabilityGrant DefaultApprovedCapabilities { get; } =
        CapabilityGrant.Chat |
        CapabilityGrant.ScreenShare |
        CapabilityGrant.FileTransfer |
        CapabilityGrant.Clipboard;
}

public static class SessionHandshakeProtocol
{
    private const string SessionIdPrefix = "sess_";
    private const string MacProtocolLabel = "nlink-session-handshake-v1";

    public static SessionId CreateSessionId()
    {
        return new SessionId(SessionIdPrefix + Guid.NewGuid().ToString("N"));
    }

    public static string CreateChallengeNonce(int sizeBytes = 32)
    {
        if (sizeBytes < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Challenge nonce must be at least 16 bytes.");
        }

        var bytes = new byte[sizeBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static byte[] ComputeResponseMac(
        ReadOnlySpan<byte> macKey,
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce)
    {
        if (macKey.IsEmpty)
        {
            throw new ArgumentException("MAC key must not be empty.", nameof(macKey));
        }

        if (string.IsNullOrWhiteSpace(challengeNonce))
        {
            throw new ArgumentException("Challenge nonce is required.", nameof(challengeNonce));
        }

        using var hmac = new HMACSHA256(macKey.ToArray());
        return hmac.ComputeHash(BuildMacPayload(sessionId, helperAddress, helpeeAddress, challengeNonce));
    }

    public static bool VerifyResponseMac(
        ReadOnlySpan<byte> macKey,
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        ReadOnlySpan<byte> candidateMac)
    {
        if (candidateMac.IsEmpty)
        {
            return false;
        }

        var expectedMac = ComputeResponseMac(macKey, sessionId, helperAddress, helpeeAddress, challengeNonce);
        return CryptographicOperations.FixedTimeEquals(expectedMac, candidateMac);
    }

    public static CapabilityGrant ToCapabilityGrant(this InviteCapabilities capabilities)
    {
        var grants = CapabilityGrant.None;
        if ((capabilities & InviteCapabilities.Chat) != 0)
        {
            grants |= CapabilityGrant.Chat;
        }

        if ((capabilities & InviteCapabilities.ScreenShare) != 0)
        {
            grants |= CapabilityGrant.ScreenShare;
        }

        if ((capabilities & InviteCapabilities.RemoteControl) != 0)
        {
            grants |= CapabilityGrant.RemoteControl;
        }

        if ((capabilities & InviteCapabilities.FileTransfer) != 0)
        {
            grants |= CapabilityGrant.FileTransfer;
        }

        return grants;
    }

    public static byte[] Serialize(SessionHandshakeStart message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(new SessionHandshakeStartWire
        {
            SessionId = message.SessionId.Value,
            HelperAddress = message.HelperAddress.Value,
            InviteToken = message.InviteToken,
        });
    }

    public static byte[] Serialize(SessionHandshakeChallenge message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(new SessionHandshakeChallengeWire
        {
            SessionId = message.SessionId.Value,
            HelpeeAddress = message.HelpeeAddress.Value,
            ChallengeNonce = message.ChallengeNonce,
            ExpiresAtUtcMs = message.ExpiresAtUtcMs,
            HelpeeEcdhPublicKeyBase64 = message.HelpeeEcdhPublicKeyBase64,
        });
    }

    public static byte[] Serialize(SessionHandshakeResponse message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(new SessionHandshakeResponseWire
        {
            SessionId = message.SessionId.Value,
            HelperAddress = message.HelperAddress.Value,
            ChallengeNonce = message.ChallengeNonce,
            MacBase64 = message.MacBase64,
        });
    }

    public static byte[] Serialize(SessionHandshakeResult message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(new SessionHandshakeResultWire
        {
            SessionId = message.SessionId.Value,
            Verified = message.Verified,
            FailureReason = message.FailureReason,
        });
    }

    public static byte[] Serialize(ApprovalDecision message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(new ApprovalDecisionWire
        {
            SessionId = message.SessionId.Value,
            HelperAddress = message.HelperIdentity.Value,
            ApprovedCapabilities = (int)message.ApprovedCapabilities,
            ExpiresAtUtcMs = message.ExpiresAtUtc.ToUnixTimeMilliseconds(),
        });
    }

    public static bool TryDeserializeStart(byte[] payload, out SessionHandshakeStart parsed)
    {
        parsed = default!;
        if (!TryDeserialize(payload, out SessionHandshakeStartWire? wire) || wire is null ||
            !SessionId.TryParse(wire.SessionId, out var sessionId) ||
            !PeerAddress.TryParse(wire.HelperAddress, out var helperAddress))
        {
            return false;
        }

        parsed = new SessionHandshakeStart(sessionId, helperAddress, NormalizeOptional(wire.InviteToken));
        return true;
    }

    public static bool TryDeserializeChallenge(byte[] payload, out SessionHandshakeChallenge parsed)
    {
        parsed = default!;
        if (!TryDeserialize(payload, out SessionHandshakeChallengeWire? wire) || wire is null ||
            string.IsNullOrWhiteSpace(wire.ChallengeNonce) ||
            string.IsNullOrWhiteSpace(wire.HelpeeEcdhPublicKeyBase64) ||
            !SessionId.TryParse(wire.SessionId, out var sessionId) ||
            !PeerAddress.TryParse(wire.HelpeeAddress, out var helpeeAddress))
        {
            return false;
        }

        parsed = new SessionHandshakeChallenge(
            sessionId,
            helpeeAddress,
            wire.ChallengeNonce.Trim(),
            wire.ExpiresAtUtcMs,
            wire.HelpeeEcdhPublicKeyBase64.Trim());
        return true;
    }

    public static bool TryDeserializeResponse(byte[] payload, out SessionHandshakeResponse parsed)
    {
        parsed = default!;
        if (!TryDeserialize(payload, out SessionHandshakeResponseWire? wire) || wire is null ||
            string.IsNullOrWhiteSpace(wire.ChallengeNonce) ||
            string.IsNullOrWhiteSpace(wire.MacBase64) ||
            !SessionId.TryParse(wire.SessionId, out var sessionId) ||
            !PeerAddress.TryParse(wire.HelperAddress, out var helperAddress))
        {
            return false;
        }

        parsed = new SessionHandshakeResponse(
            sessionId,
            helperAddress,
            wire.ChallengeNonce.Trim(),
            wire.MacBase64.Trim());
        return true;
    }

    public static bool TryDeserializeResult(byte[] payload, out SessionHandshakeResult parsed)
    {
        parsed = default!;
        if (!TryDeserialize(payload, out SessionHandshakeResultWire? wire) || wire is null ||
            !SessionId.TryParse(wire.SessionId, out var sessionId))
        {
            return false;
        }

        parsed = new SessionHandshakeResult(sessionId, wire.Verified, NormalizeOptional(wire.FailureReason));
        return true;
    }

    public static bool TryDeserializeApprovalDecision(byte[] payload, out ApprovalDecision parsed)
    {
        parsed = default!;
        if (!TryDeserialize(payload, out ApprovalDecisionWire? wire) || wire is null ||
            wire.ExpiresAtUtcMs <= 0 ||
            !SessionId.TryParse(wire.SessionId, out var sessionId) ||
            !PeerAddress.TryParse(wire.HelperAddress, out var helperAddress))
        {
            return false;
        }

        var approvedCapabilities = (CapabilityGrant)wire.ApprovedCapabilities;
        if (approvedCapabilities == CapabilityGrant.None ||
            (approvedCapabilities & ~SessionSecurityDefaults.AllCapabilityGrants) != 0)
        {
            return false;
        }

        parsed = new ApprovalDecision(
            ApprovedCapabilities: approvedCapabilities,
            ExpiresAtUtc: DateTimeOffset.FromUnixTimeMilliseconds(wire.ExpiresAtUtcMs),
            HelperIdentity: helperAddress,
            SessionId: sessionId);
        return true;
    }

    private static byte[] BuildMacPayload(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce)
    {
        using var buffer = new MemoryStream();
        WriteField(buffer, MacProtocolLabel);
        WriteField(buffer, sessionId.Value);
        WriteField(buffer, helperAddress.Value);
        WriteField(buffer, helpeeAddress.Value);
        WriteField(buffer, challengeNonce.Trim());
        return buffer.ToArray();
    }

    private static void WriteField(Stream destination, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        destination.Write(length);
        destination.Write(bytes, 0, bytes.Length);
    }

    private static bool TryDeserialize<TWire>(byte[] payload, out TWire? wire)
    {
        wire = default;
        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            wire = JsonSerializer.Deserialize<TWire>(payload);
            return wire is not null;
        }
        catch
        {
            wire = default;
            return false;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class SessionHandshakeStartWire
    {
        public string SessionId { get; init; } = string.Empty;
        public string HelperAddress { get; init; } = string.Empty;
        public string? InviteToken { get; init; }
    }

    private sealed class SessionHandshakeChallengeWire
    {
        public string SessionId { get; init; } = string.Empty;
        public string HelpeeAddress { get; init; } = string.Empty;
        public string ChallengeNonce { get; init; } = string.Empty;
        public long ExpiresAtUtcMs { get; init; }
        public string HelpeeEcdhPublicKeyBase64 { get; init; } = string.Empty;
    }

    private sealed class SessionHandshakeResponseWire
    {
        public string SessionId { get; init; } = string.Empty;
        public string HelperAddress { get; init; } = string.Empty;
        public string ChallengeNonce { get; init; } = string.Empty;
        public string MacBase64 { get; init; } = string.Empty;
    }

    private sealed class SessionHandshakeResultWire
    {
        public string SessionId { get; init; } = string.Empty;
        public bool Verified { get; init; }
        public string? FailureReason { get; init; }
    }

    private sealed class ApprovalDecisionWire
    {
        public string SessionId { get; init; } = string.Empty;
        public string HelperAddress { get; init; } = string.Empty;
        public int ApprovedCapabilities { get; init; }
        public long ExpiresAtUtcMs { get; init; }
    }
}
