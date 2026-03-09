using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Core.SessionConnect;

public interface IInviteRevocationStore
{
    bool IsRevoked(InvitePayloadV1 payload, DateTimeOffset nowUtc);
    void Revoke(InvitePayloadV1 payload, DateTimeOffset nowUtc, string? reason = null);
}

public interface IInviteIssueTracker
{
    bool TryRegisterIssued(InvitePayloadV1 payload, DateTimeOffset nowUtc, out string? failureReason);
}

public interface IInviteValidationThrottle
{
    bool TryAcquire(string scopeKey, DateTimeOffset nowUtc, out TimeSpan retryAfter);
}

public interface IInviteIssuedTokenStore
{
    bool TryRegisterIssuedToken(InvitePayloadV1 payload, ReadOnlySpan<byte> verificationBytes, DateTimeOffset nowUtc, out string? failureReason);
    InviteIssuedTokenConsumeResult ConsumeIssuedToken(InvitePayloadV1 payload, ReadOnlySpan<byte> verificationBytes, DateTimeOffset nowUtc);
}

public sealed record InviteSecurityStoreOptions
{
    public string? FilePath { get; init; }
    public int MaxIssueAttemptsPerScope { get; init; } = 32;
    public TimeSpan IssueWindow { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxValidationAttemptsPerScope { get; init; } = 12;
    public TimeSpan ValidationWindow { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed class PersistentInviteSecurityStore : IInviteReplayCache, IInviteRevocationStore, IInviteIssueTracker, IInviteValidationThrottle, IInviteIssuedTokenStore
{
    private const int CurrentVersion = 1;
    private const string UnknownHelperScopeKey = "(unknown)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string filePath;
    private readonly string lockFilePath;
    private readonly int maxIssueAttemptsPerScope;
    private readonly int maxValidationAttemptsPerScope;
    private readonly TimeSpan issueWindow;
    private readonly TimeSpan validationWindow;

    public PersistentInviteSecurityStore(InviteSecurityStoreOptions? options = null)
    {
        options ??= new InviteSecurityStoreOptions();
        if (options.MaxIssueAttemptsPerScope <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxIssueAttemptsPerScope));
        }

        if (options.MaxValidationAttemptsPerScope <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxValidationAttemptsPerScope));
        }

        if (options.IssueWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.IssueWindow));
        }

        if (options.ValidationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ValidationWindow));
        }

        filePath = string.IsNullOrWhiteSpace(options.FilePath) ? BuildDefaultPath() : options.FilePath!;
        lockFilePath = filePath + ".lock";
        maxIssueAttemptsPerScope = options.MaxIssueAttemptsPerScope;
        maxValidationAttemptsPerScope = options.MaxValidationAttemptsPerScope;
        issueWindow = options.IssueWindow;
        validationWindow = options.ValidationWindow;
    }

    public string FilePath => filePath;

    public bool TryReserve(InvitePayloadV1 payload, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            return WithDocument(
                nowUtc,
                mutate: true,
                (document, nowUtcMs) =>
                {
                    var inviteKey = BuildInviteKey(payload);
                    if (document.ReplayReservations.TryGetValue(inviteKey, out var reservedUntilUtcMs) &&
                        reservedUntilUtcMs > nowUtcMs)
                    {
                        return false;
                    }

                    document.ReplayReservations[inviteKey] = payload.ExpiresAtUtcMs;
                    return true;
                });
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=reserve; ex={ex.GetType().Name}");
            return false;
        }
    }

    public bool IsRevoked(InvitePayloadV1 payload, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            return WithDocument(
                nowUtc,
                mutate: false,
                (document, nowUtcMs) =>
                {
                    var inviteKey = BuildInviteKey(payload);
                    return document.RevokedInvites.TryGetValue(inviteKey, out var record) &&
                           record.ExpiresAtUtcMs > nowUtcMs;
                });
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=is_revoked; ex={ex.GetType().Name}");
            return true;
        }
    }

    public void Revoke(InvitePayloadV1 payload, DateTimeOffset nowUtc, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            WithDocument(
                nowUtc,
                mutate: true,
                (document, _) =>
                {
                    RevokeInvite(document, BuildInviteKey(payload), BuildIssueScopeKey(payload), payload.ExpiresAtUtcMs, reason);
                    return 0;
                });
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=revoke; ex={ex.GetType().Name}");
        }
    }

    public bool TryRegisterIssued(InvitePayloadV1 payload, DateTimeOffset nowUtc, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            var result = WithDocument(
                nowUtc,
                mutate: true,
                (document, nowUtcMs) =>
                {
                    var scopeKey = BuildIssueScopeKey(payload);
                    if (!TryRecordAttempt(
                            document.IssueAttempts,
                            scopeKey,
                            nowUtcMs,
                            issueWindow,
                            maxIssueAttemptsPerScope,
                            out var retryAfter))
                    {
                        var failure = $"Invite issuance is throttled. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))}s.";
                        LocalOperationalLog.Warn(
                            "InviteSecurity",
                            $"event=issue_throttled; target={payload.TargetAddress.Value}; helper={payload.BoundHelperAddress?.Value ?? "(unbound)"}; retry_after_ms={(long)Math.Ceiling(retryAfter.TotalMilliseconds)}");
                        return (Accepted: false, FailureReason: failure);
                    }

                    var inviteKey = BuildInviteKey(payload);
                    if (document.ActiveInvitesByScope.TryGetValue(scopeKey, out var activeInvite) &&
                        activeInvite.ExpiresAtUtcMs > nowUtcMs &&
                        !string.Equals(activeInvite.InviteKey, inviteKey, StringComparison.Ordinal))
                    {
                        document.RevokedInvites[activeInvite.InviteKey] = new RevokedInviteRecord
                        {
                            ExpiresAtUtcMs = activeInvite.ExpiresAtUtcMs,
                            Reason = "superseded",
                        };
                        LocalOperationalLog.Info(
                            "InviteSecurity",
                            $"event=invite_revoked; reason=superseded; target={payload.TargetAddress.Value}; helper={payload.BoundHelperAddress?.Value ?? "(unbound)"}; old_exp_utc_ms={activeInvite.ExpiresAtUtcMs}; new_session_id={payload.SessionId.Value}");
                    }

                    document.ActiveInvitesByScope[scopeKey] = new ActiveInviteScopeRecord
                    {
                        InviteKey = inviteKey,
                        ExpiresAtUtcMs = payload.ExpiresAtUtcMs,
                    };
                    return (Accepted: true, FailureReason: (string?)null);
                });

            failureReason = result.FailureReason;
            return result.Accepted;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=register_issue; ex={ex.GetType().Name}");
            failureReason = "Invite issuance could not be secured.";
            return false;
        }
    }

    public bool TryRegisterIssuedToken(InvitePayloadV1 payload, ReadOnlySpan<byte> verificationBytes, DateTimeOffset nowUtc, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!InviteIssuedSecretProof.IsWellFormed(verificationBytes))
        {
            failureReason = "Invite proof is invalid.";
            return false;
        }

        var proofHashKey = InviteIssuedSecretProof.ComputeHashKey(verificationBytes);
        try
        {
            var result = WithDocument(
                nowUtc,
                mutate: true,
                (document, nowUtcMs) =>
                {
                    var scopeKey = BuildIssueScopeKey(payload);
                    if (!TryRecordAttempt(
                            document.IssueAttempts,
                            scopeKey,
                            nowUtcMs,
                            issueWindow,
                            maxIssueAttemptsPerScope,
                            out var retryAfter))
                    {
                        var failure = $"Invite issuance is throttled. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))}s.";
                        LocalOperationalLog.Warn(
                            "InviteSecurity",
                            $"event=issue_throttled; target={payload.TargetAddress.Value}; helper={payload.BoundHelperAddress?.Value ?? "(unbound)"}; retry_after_ms={(long)Math.Ceiling(retryAfter.TotalMilliseconds)}");
                        return (Accepted: false, FailureReason: failure);
                    }

                    if (document.ActiveInvitesByScope.TryGetValue(scopeKey, out var activeInvite) &&
                        activeInvite.ExpiresAtUtcMs > nowUtcMs &&
                        !string.Equals(activeInvite.InviteKey, proofHashKey, StringComparison.Ordinal))
                    {
                        if (document.IssuedInvitesByProofHash.TryGetValue(activeInvite.InviteKey, out var previousIssuedInvite))
                        {
                            previousIssuedInvite.RevokedReason = "superseded";
                            document.IssuedInvitesByProofHash[activeInvite.InviteKey] = previousIssuedInvite;
                        }

                        LocalOperationalLog.Info(
                            "InviteSecurity",
                            $"event=invite_revoked; reason=superseded; target={payload.TargetAddress.Value}; helper={payload.BoundHelperAddress?.Value ?? "(unbound)"}; old_exp_utc_ms={activeInvite.ExpiresAtUtcMs}; new_session_id={payload.SessionId.Value}");
                    }

                    document.IssuedInvitesByProofHash[proofHashKey] = new IssuedInviteRecord
                    {
                        Version = payload.Version,
                        IssuerAddress = payload.IssuerAddress.Value,
                        TargetAddress = payload.TargetAddress.Value,
                        SessionId = payload.SessionId.Value,
                        Capabilities = payload.Capabilities,
                        IssuedAtUtcMs = payload.IssuedAtUtcMs,
                        Nonce = payload.Nonce,
                        BoundHelperAddress = payload.BoundHelperAddress?.Value,
                        ExpiresAtUtcMs = payload.ExpiresAtUtcMs,
                    };
                    document.ActiveInvitesByScope[scopeKey] = new ActiveInviteScopeRecord
                    {
                        InviteKey = proofHashKey,
                        ExpiresAtUtcMs = payload.ExpiresAtUtcMs,
                    };

                    return (Accepted: true, FailureReason: (string?)null);
                });

            failureReason = result.FailureReason;
            return result.Accepted;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=register_issued_token; ex={ex.GetType().Name}");
            failureReason = "Invite issuance could not be secured.";
            return false;
        }
    }

    public InviteIssuedTokenConsumeResult ConsumeIssuedToken(InvitePayloadV1 payload, ReadOnlySpan<byte> verificationBytes, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!InviteIssuedSecretProof.IsWellFormed(verificationBytes))
        {
            return InviteIssuedTokenConsumeResult.InvalidProof();
        }

        var proofHashKey = InviteIssuedSecretProof.ComputeHashKey(verificationBytes);
        try
        {
            return WithDocument(
                nowUtc,
                mutate: true,
                (document, _) =>
                {
                    if (!document.IssuedInvitesByProofHash.TryGetValue(proofHashKey, out var issuedInvite))
                    {
                        return InviteIssuedTokenConsumeResult.InvalidProof();
                    }

                    if (!PayloadMatchesIssuedRecord(payload, issuedInvite))
                    {
                        return InviteIssuedTokenConsumeResult.InvalidProof();
                    }

                    if (!string.IsNullOrWhiteSpace(issuedInvite.RevokedReason))
                    {
                        return InviteIssuedTokenConsumeResult.Revoked();
                    }

                    if (issuedInvite.ConsumedAtUtcMs is not null)
                    {
                        return InviteIssuedTokenConsumeResult.ReplayDetected();
                    }

                    issuedInvite.ConsumedAtUtcMs = nowUtc.ToUnixTimeMilliseconds();
                    document.IssuedInvitesByProofHash[proofHashKey] = issuedInvite;
                    return InviteIssuedTokenConsumeResult.Valid();
                });
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=consume_issued_token; ex={ex.GetType().Name}");
            return InviteIssuedTokenConsumeResult.InvalidProof("Invite issuance record is unavailable.");
        }
    }

    public bool TryAcquire(string scopeKey, DateTimeOffset nowUtc, out TimeSpan retryAfter)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            retryAfter = TimeSpan.Zero;
            return false;
        }

        try
        {
            var result = WithDocument(
                nowUtc,
                mutate: true,
                (document, nowUtcMs) => TryRecordAttempt(
                    document.ValidationAttempts,
                    scopeKey.Trim(),
                    nowUtcMs,
                    validationWindow,
                    maxValidationAttemptsPerScope,
                    out var calculatedRetryAfter)
                    ? (Allowed: true, RetryAfter: TimeSpan.Zero)
                    : (Allowed: false, RetryAfter: calculatedRetryAfter));

            retryAfter = result.RetryAfter;
            return result.Allowed;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_failure; op=validation_throttle; ex={ex.GetType().Name}");
            retryAfter = TimeSpan.FromSeconds(1);
            return false;
        }
    }

    public static string BuildValidationScopeKey(PeerAddress targetAddress, string? helperIdentity)
    {
        var normalizedHelperIdentity = string.IsNullOrWhiteSpace(helperIdentity)
            ? UnknownHelperScopeKey
            : helperIdentity.Trim();
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{targetAddress.Value}|{normalizedHelperIdentity}");
    }

    private static string BuildIssueScopeKey(InvitePayloadV1 payload)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{payload.Version}|{payload.IssuerAddress.Value}|{payload.TargetAddress.Value}|{payload.BoundHelperAddress?.Value ?? "*"}");
    }

    private static string BuildInviteKey(InvitePayloadV1 payload)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{payload.Version}|{payload.IssuerAddress.Value}|{payload.TargetAddress.Value}|{payload.SessionId.Value}|{payload.Nonce}|{payload.BoundHelperAddress?.Value ?? "*"}");
    }

    private T WithDocument<T>(DateTimeOffset nowUtc, bool mutate, Func<InviteSecurityDocument, long, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var lockStream = AcquireLockFileHandle();
        var document = LoadDocument();
        var nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        var dirty = CleanupExpiredEntries(document, nowUtcMs);
        var result = operation(document, nowUtcMs);
        if (mutate || dirty)
        {
            SaveDocument(document);
        }

        return result;
    }

    private FileStream AcquireLockFileHandle()
    {
        var directory = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 79)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 79)
            {
                Thread.Sleep(25);
            }
        }

        return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private InviteSecurityDocument LoadDocument()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new InviteSecurityDocument();
            }

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new InviteSecurityDocument();
            }

            var parsed = JsonSerializer.Deserialize<InviteSecurityDocument>(json, JsonOptions);
            if (parsed is null || parsed.Version != CurrentVersion)
            {
                return new InviteSecurityDocument();
            }

            parsed.ReplayReservations ??= new Dictionary<string, long>(StringComparer.Ordinal);
            parsed.RevokedInvites ??= new Dictionary<string, RevokedInviteRecord>(StringComparer.Ordinal);
            parsed.IssuedInvitesByProofHash ??= new Dictionary<string, IssuedInviteRecord>(StringComparer.Ordinal);
            parsed.ActiveInvitesByScope ??= new Dictionary<string, ActiveInviteScopeRecord>(StringComparer.Ordinal);
            parsed.IssueAttempts ??= new Dictionary<string, List<long>>(StringComparer.Ordinal);
            parsed.ValidationAttempts ??= new Dictionary<string, List<long>>(StringComparer.Ordinal);
            return parsed;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("InviteSecurity", $"event=store_reload_failed; ex={ex.GetType().Name}");
            return new InviteSecurityDocument();
        }
    }

    private void SaveDocument(InviteSecurityDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Copy(tempPath, filePath, overwrite: true);
        File.Delete(tempPath);
    }

    private bool CleanupExpiredEntries(InviteSecurityDocument document, long nowUtcMs)
    {
        var dirty = RemoveExpired(document.ReplayReservations, nowUtcMs);
        dirty |= RemoveExpired(document.RevokedInvites, nowUtcMs, static record => record.ExpiresAtUtcMs);
        dirty |= RemoveExpired(document.IssuedInvitesByProofHash, nowUtcMs, static record => record.ExpiresAtUtcMs);
        dirty |= RemoveExpired(document.ActiveInvitesByScope, nowUtcMs, static record => record.ExpiresAtUtcMs);
        dirty |= RemoveExpiredAttempts(document.IssueAttempts, nowUtcMs, issueWindow);
        dirty |= RemoveExpiredAttempts(document.ValidationAttempts, nowUtcMs, validationWindow);
        return dirty;
    }

    private static bool RemoveExpired(IDictionary<string, long> values, long nowUtcMs)
    {
        var dirty = false;
        foreach (var entry in values.ToArray())
        {
            if (entry.Value <= nowUtcMs)
            {
                values.Remove(entry.Key);
                dirty = true;
            }
        }

        return dirty;
    }

    private static bool RemoveExpired<TRecord>(IDictionary<string, TRecord> values, long nowUtcMs, Func<TRecord, long> expiresAtUtcMsSelector)
    {
        var dirty = false;
        foreach (var entry in values.ToArray())
        {
            if (expiresAtUtcMsSelector(entry.Value) <= nowUtcMs)
            {
                values.Remove(entry.Key);
                dirty = true;
            }
        }

        return dirty;
    }

    private static bool RemoveExpiredAttempts(IDictionary<string, List<long>> attemptsByScope, long nowUtcMs, TimeSpan window)
    {
        var dirty = false;
        var windowStartUtcMs = nowUtcMs - (long)window.TotalMilliseconds;
        foreach (var entry in attemptsByScope.ToArray())
        {
            var originalCount = entry.Value.Count;
            entry.Value.RemoveAll(timestampUtcMs => timestampUtcMs < windowStartUtcMs);
            if (entry.Value.Count != originalCount)
            {
                dirty = true;
            }

            if (entry.Value.Count == 0)
            {
                attemptsByScope.Remove(entry.Key);
                dirty = true;
            }
        }

        return dirty;
    }

    private static bool TryRecordAttempt(
        IDictionary<string, List<long>> attemptsByScope,
        string scopeKey,
        long nowUtcMs,
        TimeSpan window,
        int maxAttempts,
        out TimeSpan retryAfter)
    {
        if (!attemptsByScope.TryGetValue(scopeKey, out var timestamps))
        {
            timestamps = new List<long>(capacity: maxAttempts);
            attemptsByScope[scopeKey] = timestamps;
        }

        var windowStartUtcMs = nowUtcMs - (long)window.TotalMilliseconds;
        timestamps.RemoveAll(timestampUtcMs => timestampUtcMs < windowStartUtcMs);

        if (timestamps.Count >= maxAttempts)
        {
            var retryAfterMs = Math.Max(1, timestamps[0] + (long)window.TotalMilliseconds - nowUtcMs);
            retryAfter = TimeSpan.FromMilliseconds(retryAfterMs);
            return false;
        }

        timestamps.Add(nowUtcMs);
        retryAfter = TimeSpan.Zero;
        return true;
    }

    private static void RevokeInvite(
        InviteSecurityDocument document,
        string inviteKey,
        string scopeKey,
        long expiresAtUtcMs,
        string? reason)
    {
        document.RevokedInvites[inviteKey] = new RevokedInviteRecord
        {
            ExpiresAtUtcMs = expiresAtUtcMs,
            Reason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason!.Trim(),
        };

        if (document.ActiveInvitesByScope.TryGetValue(scopeKey, out var activeInvite) &&
            string.Equals(activeInvite.InviteKey, inviteKey, StringComparison.Ordinal))
        {
            document.ActiveInvitesByScope.Remove(scopeKey);
        }
    }

    private static bool PayloadMatchesIssuedRecord(InvitePayloadV1 payload, IssuedInviteRecord record)
    {
        return record.Version == payload.Version &&
               string.Equals(record.IssuerAddress, payload.IssuerAddress.Value, StringComparison.Ordinal) &&
               string.Equals(record.TargetAddress, payload.TargetAddress.Value, StringComparison.Ordinal) &&
               string.Equals(record.SessionId, payload.SessionId.Value, StringComparison.Ordinal) &&
               record.Capabilities == payload.Capabilities &&
               record.IssuedAtUtcMs == payload.IssuedAtUtcMs &&
               string.Equals(record.Nonce, payload.Nonce, StringComparison.Ordinal) &&
               record.ExpiresAtUtcMs == payload.ExpiresAtUtcMs &&
               string.Equals(record.BoundHelperAddress ?? string.Empty, payload.BoundHelperAddress?.Value ?? string.Empty, StringComparison.Ordinal);
    }

    private static string BuildDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "nLink", "security", "invite-security-store.json");
    }

    private sealed class InviteSecurityDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, long> ReplayReservations { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, RevokedInviteRecord> RevokedInvites { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, IssuedInviteRecord> IssuedInvitesByProofHash { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ActiveInviteScopeRecord> ActiveInvitesByScope { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<long>> IssueAttempts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<long>> ValidationAttempts { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RevokedInviteRecord
    {
        public long ExpiresAtUtcMs { get; set; }
        public string Reason { get; set; } = "manual";
    }

    private sealed class ActiveInviteScopeRecord
    {
        public string InviteKey { get; set; } = string.Empty;
        public long ExpiresAtUtcMs { get; set; }
    }

    private sealed class IssuedInviteRecord
    {
        public int Version { get; set; }
        public string IssuerAddress { get; set; } = string.Empty;
        public string TargetAddress { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public InviteCapabilities Capabilities { get; set; }
        public long IssuedAtUtcMs { get; set; }
        public string Nonce { get; set; } = string.Empty;
        public string? BoundHelperAddress { get; set; }
        public long ExpiresAtUtcMs { get; set; }
        public string? RevokedReason { get; set; }
        public long? ConsumedAtUtcMs { get; set; }
    }
}
