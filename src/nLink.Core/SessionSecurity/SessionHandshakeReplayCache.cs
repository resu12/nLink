using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public interface ISessionHandshakeReplayCache
{
    bool TryTrackChallenge(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset nowUtc);

    bool TryConsumeChallenge(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        DateTimeOffset nowUtc);

    bool WasChallengeConsumed(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        DateTimeOffset nowUtc);
}

public sealed class InMemorySessionHandshakeReplayCache : ISessionHandshakeReplayCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public bool TryTrackChallenge(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(challengeNonce))
        {
            throw new ArgumentException("Challenge nonce is required.", nameof(challengeNonce));
        }

        var key = BuildKey(sessionId, helperAddress, helpeeAddress, challengeNonce);
        var nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        var expiresAtUtcMs = expiresAtUtc.ToUnixTimeMilliseconds();

        lock (gate)
        {
            RemoveExpiredEntries(nowUtcMs);

            if (entries.TryGetValue(key, out var existing) &&
                existing.ExpiresAtUtcMs > nowUtcMs)
            {
                return false;
            }

            entries[key] = new Entry(expiresAtUtcMs, Consumed: false);
            return true;
        }
    }

    public bool TryConsumeChallenge(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        DateTimeOffset nowUtc)
    {
        var key = BuildKey(sessionId, helperAddress, helpeeAddress, challengeNonce);
        var nowUtcMs = nowUtc.ToUnixTimeMilliseconds();

        lock (gate)
        {
            RemoveExpiredEntries(nowUtcMs);

            if (!entries.TryGetValue(key, out var existing) ||
                existing.ExpiresAtUtcMs <= nowUtcMs ||
                existing.Consumed)
            {
                return false;
            }

            entries[key] = existing with { Consumed = true };
            return true;
        }
    }

    public bool WasChallengeConsumed(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce,
        DateTimeOffset nowUtc)
    {
        var key = BuildKey(sessionId, helperAddress, helpeeAddress, challengeNonce);
        var nowUtcMs = nowUtc.ToUnixTimeMilliseconds();

        lock (gate)
        {
            RemoveExpiredEntries(nowUtcMs);

            return entries.TryGetValue(key, out var existing) &&
                   existing.ExpiresAtUtcMs > nowUtcMs &&
                   existing.Consumed;
        }
    }

    private void RemoveExpiredEntries(long nowUtcMs)
    {
        foreach (var pair in entries.ToArray())
        {
            if (pair.Value.ExpiresAtUtcMs <= nowUtcMs)
            {
                entries.Remove(pair.Key);
            }
        }
    }

    private static string BuildKey(
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        string challengeNonce)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{sessionId.Value}|{helperAddress.Value}|{helpeeAddress.Value}|{challengeNonce.Trim()}");
    }

    private readonly record struct Entry(long ExpiresAtUtcMs, bool Consumed);
}
