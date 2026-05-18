using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

internal interface ITunaRuntimePreferenceStore
{
    TunaRuntimePreferenceState Load();

    void Save(TunaRuntimePreferenceState state);
}

internal interface ITunaUsageAccountingStore
{
    TunaUsageAccountingState Load();

    void Save(TunaUsageAccountingState state);
}

internal interface ITunaRuntimePilotService
{
    TunaRuntimePreferenceState Preferences { get; }

    TunaUsageAccountingState Usage { get; }

    string RuntimeStatus { get; }

    string StartupTimingSummary { get; }

    bool HasSessionUnlock { get; }

    Task<TunaRuntimeUnlockState> GetUnlockStateAsync(CancellationToken ct = default);

    Task<TunaRuntimeUnlockResult> UnlockForSessionAsync(char[]? password, TunaRuntimeUnlockSource source, CancellationToken ct = default);

    Task<TunaRuntimeUnlockResult> LockOrStopForSessionAsync(string reason, TunaRuntimeUnlockSource source, CancellationToken ct = default);

    void SavePreferences(TunaRuntimePreferenceState state);

    void UnlockForNextSession(TunaWalletLinkState walletState, char[] password);

    void ReportSessionUnlockFailed(string reason);

    void ClearSessionUnlock();

    ISignalingTransport CreateNknTransport();

    event EventHandler? StateChanged;
}

internal enum TunaRuntimeUnlockSource
{
    Options,
    Header,
}

internal sealed record TunaRuntimeUnlockState(
    bool IsVisible,
    bool CanToggle,
    bool IsOn,
    string RuntimeStatus,
    string StatusText,
    string UserMessage,
    bool IsCooldownActive,
    TimeSpan CooldownRemaining);

internal sealed record TunaRuntimeUnlockResult(
    bool Success,
    string Status,
    string Message,
    bool IsCooldownActive,
    TimeSpan CooldownRemaining)
{
    public static TunaRuntimeUnlockResult FromState(bool success, TunaRuntimeUnlockState state, string? message = null)
        => new(
            success,
            state.RuntimeStatus,
            string.IsNullOrWhiteSpace(message) ? state.UserMessage : message.Trim(),
            state.IsCooldownActive,
            state.CooldownRemaining);
}

internal sealed class TunaRuntimePreferenceState
{
    public const string AllowDegradedProviderReadyEnvVar = "NLINK_NKN_TUNA_ALLOW_DEGRADED_PROVIDER_READY";
    public const string RequireStrictProviderReadyEnvVar = "NLINK_NKN_TUNA_REQUIRE_STRICT_PROVIDER_READY";
    public const string DegradedProviderGraceSecondsEnvVar = "NLINK_NKN_TUNA_DEGRADED_PROVIDER_GRACE_SECONDS";
    public const string DefaultMaxPriceNknPerMb = "0.0002";
    public const int DefaultMaxTotalMiB = 2048;
    public const int DefaultMaxDurationSec = 1800;

    public bool Enabled { get; init; }

    public bool FileLaneEnabled { get; init; } = true;

    public bool ScreenLaneEnabled { get; init; } = true;

    public string MaxPriceNknPerMb { get; init; } = DefaultMaxPriceNknPerMb;

    public int MaxTotalMiB { get; init; } = DefaultMaxTotalMiB;

    public int MaxDurationSec { get; init; } = DefaultMaxDurationSec;

    public bool AllowDegradedProviderReady { get; init; } = false;

    public string LastRuntimeStatus { get; init; } = "off";

    [JsonIgnore]
    public NknAccelerationLaneKind Lanes
    {
        get
        {
            var lanes = NknAccelerationLaneKind.None;
            if (FileLaneEnabled)
            {
                lanes |= NknAccelerationLaneKind.File;
            }

            if (ScreenLaneEnabled)
            {
                lanes |= NknAccelerationLaneKind.Screen;
            }

            return lanes == NknAccelerationLaneKind.None
                ? NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen
                : lanes;
        }
    }

    public static TunaRuntimePreferenceState Default { get; } = new();

    public TunaRuntimePreferenceState WithStatus(string status)
        => new()
        {
            Enabled = Enabled,
            FileLaneEnabled = FileLaneEnabled,
            ScreenLaneEnabled = ScreenLaneEnabled,
            MaxPriceNknPerMb = MaxPriceNknPerMb,
            MaxTotalMiB = MaxTotalMiB,
            MaxDurationSec = MaxDurationSec,
            AllowDegradedProviderReady = AllowDegradedProviderReady,
            LastRuntimeStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim(),
        };

    public TunaRuntimePreferenceState Normalized()
    {
        var price = decimal.TryParse(MaxPriceNknPerMb, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPrice) &&
                    parsedPrice > 0m
            ? parsedPrice.ToString("0.########", CultureInfo.InvariantCulture)
            : DefaultMaxPriceNknPerMb;
        var status = string.IsNullOrWhiteSpace(LastRuntimeStatus) ? "off" : LastRuntimeStatus.Trim();
        if (string.Equals(status, "unlocked_for_next_session", StringComparison.Ordinal))
        {
            status = "waiting_for_approved_session";
        }

        return new TunaRuntimePreferenceState
        {
            Enabled = Enabled,
            FileLaneEnabled = FileLaneEnabled,
            ScreenLaneEnabled = ScreenLaneEnabled,
            MaxPriceNknPerMb = price,
            MaxTotalMiB = Math.Clamp(MaxTotalMiB, 1, 65_536),
            MaxDurationSec = Math.Clamp(MaxDurationSec, 1, 86_400),
            AllowDegradedProviderReady = AllowDegradedProviderReady,
            LastRuntimeStatus = status,
        };
    }
}

internal static class TunaPaymentTelemetryStatus
{
    public const string Pending = "pending";
    public const string Reported = "reported";
    public const string NoPaymentTelemetryReported = "no_payment_telemetry_reported";
    public const string AccountingIncomplete = "accounting_incomplete";
    public const string None = "none";
}

internal sealed record TunaUsageSessionRecord
{
    public string SessionRunId { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? EndedUtc { get; init; }

    public string Role { get; init; } = "listener";

    public long BytesMoved { get; init; }

    public decimal AppPayloadMb { get; init; }

    public decimal PaidNkn { get; init; }

    public decimal AverageNknPerMb { get; init; }

    public int PaymentEventCount { get; init; }

    public string PaymentTelemetryStatus { get; init; } = TunaPaymentTelemetryStatus.Pending;

    public string StopReason { get; init; } = string.Empty;

    public string CapReason { get; init; } = string.Empty;

    public string FallbackReason { get; init; } = string.Empty;

    public bool CompletedFromSummary { get; init; }

    public TunaUsageSessionRecord Normalized()
    {
        var bytes = Math.Max(0, BytesMoved);
        var mb = AppPayloadMb > 0m ? AppPayloadMb : bytes / 1_000_000m;
        var paid = Math.Max(0m, PaidNkn);
        var status = NormalizeStatus(PaymentTelemetryStatus, bytes, paid, PaymentEventCount, CompletedFromSummary);
        return this with
        {
            SessionRunId = string.IsNullOrWhiteSpace(SessionRunId) ? Guid.NewGuid().ToString("N") : SessionRunId.Trim(),
            Role = string.IsNullOrWhiteSpace(Role) ? "listener" : SanitizeStatusValue(Role),
            BytesMoved = bytes,
            AppPayloadMb = Math.Max(0m, mb),
            PaidNkn = paid,
            AverageNknPerMb = mb > 0m && paid > 0m ? paid / mb : 0m,
            PaymentEventCount = Math.Max(0, PaymentEventCount),
            PaymentTelemetryStatus = status,
            StopReason = SanitizeStatusValue(StopReason),
            CapReason = SanitizeStatusValue(CapReason),
            FallbackReason = SanitizeStatusValue(FallbackReason),
        };
    }

    public TunaUsageSessionRecord AddPayment(decimal amountNkn, long bytesMoved, DateTimeOffset now)
    {
        var nextBytes = Math.Max(BytesMoved, bytesMoved);
        var nextMb = Math.Max(AppPayloadMb, nextBytes / 1_000_000m);
        var nextPaid = Math.Max(0m, PaidNkn + Math.Max(0m, amountNkn));
        return (this with
        {
            BytesMoved = nextBytes,
            AppPayloadMb = nextMb,
            PaidNkn = nextPaid,
            AverageNknPerMb = nextMb > 0m && nextPaid > 0m ? nextPaid / nextMb : 0m,
            PaymentEventCount = Math.Max(0, PaymentEventCount) + 1,
            PaymentTelemetryStatus = TunaPaymentTelemetryStatus.Reported,
        }).Normalized();
    }

    public TunaUsageSessionRecord Complete(
        long bytesMoved,
        decimal paidNkn,
        int paymentEventCount,
        bool paymentTelemetryObserved,
        string? paymentTelemetryStatus,
        string? stopReason,
        string? capReason,
        string? fallbackReason,
        bool completedFromSummary,
        DateTimeOffset now)
    {
        var nextBytes = Math.Max(BytesMoved, bytesMoved);
        var nextMb = Math.Max(AppPayloadMb, nextBytes / 1_000_000m);
        var nextPaid = Math.Max(PaidNkn, paidNkn);
        var nextPaymentEventCount = Math.Max(PaymentEventCount, paymentEventCount);
        var status = NormalizeStatus(
            paymentTelemetryStatus,
            nextBytes,
            nextPaid,
            nextPaymentEventCount,
            completedFromSummary);
        if (paymentTelemetryObserved && status is not TunaPaymentTelemetryStatus.NoPaymentTelemetryReported)
        {
            status = TunaPaymentTelemetryStatus.Reported;
        }

        return (this with
        {
            EndedUtc = now,
            BytesMoved = nextBytes,
            AppPayloadMb = nextMb,
            PaidNkn = nextPaid,
            AverageNknPerMb = nextMb > 0m && nextPaid > 0m ? nextPaid / nextMb : 0m,
            PaymentEventCount = nextPaymentEventCount,
            PaymentTelemetryStatus = status,
            StopReason = SanitizeStatusValue(stopReason),
            CapReason = SanitizeStatusValue(capReason),
            FallbackReason = SanitizeStatusValue(fallbackReason),
            CompletedFromSummary = completedFromSummary,
        }).Normalized();
    }

    internal static string NormalizeStatus(
        string? value,
        long bytesMoved,
        decimal paidNkn,
        int paymentEventCount,
        bool completedFromSummary)
    {
        var normalized = SanitizeStatusValue(value);
        if (normalized is TunaPaymentTelemetryStatus.Reported or
            TunaPaymentTelemetryStatus.NoPaymentTelemetryReported or
            TunaPaymentTelemetryStatus.AccountingIncomplete or
            TunaPaymentTelemetryStatus.None or
            TunaPaymentTelemetryStatus.Pending)
        {
            if (normalized is TunaPaymentTelemetryStatus.Pending && completedFromSummary && bytesMoved <= 0 && paidNkn <= 0m)
            {
                return TunaPaymentTelemetryStatus.None;
            }

            return normalized;
        }

        if (paymentEventCount > 0 || paidNkn > 0m)
        {
            return TunaPaymentTelemetryStatus.Reported;
        }

        if (!completedFromSummary)
        {
            return bytesMoved > 0
                ? TunaPaymentTelemetryStatus.AccountingIncomplete
                : TunaPaymentTelemetryStatus.None;
        }

        return bytesMoved > 0
            ? TunaPaymentTelemetryStatus.NoPaymentTelemetryReported
            : TunaPaymentTelemetryStatus.None;
    }

    private static string SanitizeStatusValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 96)];
        var written = 0;
        foreach (var ch in value.Trim())
        {
            if (written >= buffer.Length)
            {
                break;
            }

            buffer[written++] = char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_';
        }

        return written == 0 ? string.Empty : new string(buffer[..written]);
    }
}

internal sealed class TunaUsageAccountingState
{
    private const int MaxSessionRecords = 100;

    public decimal TotalPaidNkn { get; init; }

    public decimal TotalAppPayloadMb { get; init; }

    public decimal TotalKnownAppPayloadMb { get; init; }

    public decimal TotalUnknownAppPayloadMb { get; init; }

    public bool HasUnknownCost { get; init; }

    public decimal LastSessionPaidNkn { get; init; }

    public decimal LastSessionAppPayloadMb { get; init; }

    public bool LastSessionCostUnknown { get; init; }

    public DateTimeOffset? LastUpdatedUtc { get; init; }

    public List<TunaUsageSessionRecord> SessionRecords { get; init; } = [];

    [JsonIgnore]
    public TunaUsageSessionRecord? LastSessionRecord => SessionRecords.Count == 0 ? null : SessionRecords[^1].Normalized();

    [JsonIgnore]
    public decimal AverageNknPerMb => TotalKnownAppPayloadMb > 0m ? TotalPaidNkn / TotalKnownAppPayloadMb : 0m;

    [JsonIgnore]
    public decimal LastSessionAverageNknPerMb => LastSessionAppPayloadMb > 0m ? LastSessionPaidNkn / LastSessionAppPayloadMb : 0m;

    [JsonIgnore]
    public bool HasPaymentTelemetryGaps => HasUnknownCost || TotalUnknownAppPayloadMb > 0m;

    public static TunaUsageAccountingState Empty { get; } = new();

    public TunaUsageAccountingState AddPayment(decimal amountNkn, DateTimeOffset now)
        => AddPayment(null, amountNkn, 0, now);

    public TunaUsageAccountingState AddPayment(string? sessionRunId, decimal amountNkn, long bytesMoved, DateTimeOffset now)
    {
        var sanitizedAmount = Math.Max(0m, amountNkn);
        var mb = Math.Max(0m, bytesMoved / 1_000_000m);
        var currentSessionMb = Math.Max(0m, LastSessionAppPayloadMb);
        var deltaMb = Math.Max(0m, mb - currentSessionMb);
        return new TunaUsageAccountingState
        {
            TotalPaidNkn = Math.Max(0m, TotalPaidNkn + sanitizedAmount),
            TotalAppPayloadMb = Math.Max(0m, TotalAppPayloadMb + deltaMb),
            TotalKnownAppPayloadMb = Math.Max(0m, TotalKnownAppPayloadMb + deltaMb),
            TotalUnknownAppPayloadMb = TotalUnknownAppPayloadMb,
            HasUnknownCost = HasUnknownCost,
            LastSessionPaidNkn = Math.Max(0m, LastSessionPaidNkn + sanitizedAmount),
            LastSessionAppPayloadMb = Math.Max(currentSessionMb, mb),
            LastSessionCostUnknown = false,
            LastUpdatedUtc = now,
            SessionRecords = UpdateSessionRecord(
                sessionRunId,
                record => record.AddPayment(sanitizedAmount, bytesMoved, now)),
        }.Normalized();
    }

    public TunaUsageAccountingState CompleteSession(long bytesMoved, bool paymentTelemetryObserved, DateTimeOffset now)
        => CompleteSession(
            null,
            bytesMoved,
            paymentTelemetryObserved,
            null,
            null,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            completedFromSummary: true,
            now);

    public TunaUsageAccountingState CompleteSession(
        string? sessionRunId,
        long bytesMoved,
        bool paymentTelemetryObserved,
        decimal? cumulativeSpendNkn,
        string? paymentTelemetryStatus,
        int paymentEventCount,
        string? stopReason,
        string? capReason,
        string? fallbackReason,
        bool completedFromSummary,
        DateTimeOffset now)
    {
        var paidNkn = LastSessionPaidNkn;
        if (cumulativeSpendNkn is { } cumulativeSpend && cumulativeSpend > paidNkn)
        {
            paidNkn = cumulativeSpend;
        }

        var status = TunaUsageSessionRecord.NormalizeStatus(
            paymentTelemetryStatus,
            bytesMoved,
            paidNkn,
            Math.Max(0, paymentEventCount),
            completedFromSummary);
        if (paymentTelemetryObserved &&
            status is not TunaPaymentTelemetryStatus.NoPaymentTelemetryReported &&
            status is not TunaPaymentTelemetryStatus.AccountingIncomplete)
        {
            status = TunaPaymentTelemetryStatus.Reported;
        }

        var mb = Math.Max(0m, bytesMoved / 1_000_000m);
        var currentSessionMb = Math.Max(0m, LastSessionAppPayloadMb);
        var deltaMb = Math.Max(0m, mb - currentSessionMb);
        var paidDelta = Math.Max(0m, paidNkn - LastSessionPaidNkn);
        var isKnownCost = string.Equals(status, TunaPaymentTelemetryStatus.Reported, StringComparison.Ordinal);
        var isUnknownCost = mb > 0m && !isKnownCost;
        return new TunaUsageAccountingState
        {
            TotalPaidNkn = Math.Max(0m, TotalPaidNkn + paidDelta),
            TotalAppPayloadMb = Math.Max(0m, TotalAppPayloadMb + deltaMb),
            TotalKnownAppPayloadMb = Math.Max(0m, TotalKnownAppPayloadMb + (isKnownCost ? deltaMb : 0m)),
            TotalUnknownAppPayloadMb = Math.Max(0m, TotalUnknownAppPayloadMb + (isUnknownCost ? deltaMb : 0m)),
            HasUnknownCost = HasUnknownCost || isUnknownCost,
            LastSessionPaidNkn = Math.Max(0m, paidNkn),
            LastSessionAppPayloadMb = Math.Max(currentSessionMb, mb),
            LastSessionCostUnknown = isUnknownCost,
            LastUpdatedUtc = now,
            SessionRecords = UpdateSessionRecord(
                sessionRunId,
                record => record.Complete(
                    bytesMoved,
                    paidNkn,
                    paymentEventCount,
                    paymentTelemetryObserved,
                    status,
                    stopReason,
                    capReason,
                    fallbackReason,
                    completedFromSummary,
                    now)),
        }.Normalized();
    }

    public TunaUsageAccountingState StartNewSession()
        => StartNewSession(Guid.NewGuid().ToString("N"), "listener", DateTimeOffset.UtcNow);

    public TunaUsageAccountingState StartNewSession(string sessionRunId, string role, DateTimeOffset now)
        => new TunaUsageAccountingState
        {
            TotalPaidNkn = TotalPaidNkn,
            TotalAppPayloadMb = TotalAppPayloadMb,
            TotalKnownAppPayloadMb = TotalKnownAppPayloadMb,
            TotalUnknownAppPayloadMb = TotalUnknownAppPayloadMb,
            HasUnknownCost = HasUnknownCost,
            LastSessionPaidNkn = 0m,
            LastSessionAppPayloadMb = 0m,
            LastSessionCostUnknown = false,
            LastUpdatedUtc = now,
            SessionRecords = AppendSessionRecord(new TunaUsageSessionRecord
            {
                SessionRunId = string.IsNullOrWhiteSpace(sessionRunId) ? Guid.NewGuid().ToString("N") : sessionRunId.Trim(),
                StartedUtc = now,
                Role = string.IsNullOrWhiteSpace(role) ? "listener" : role.Trim(),
                PaymentTelemetryStatus = TunaPaymentTelemetryStatus.Pending,
            }),
        }.Normalized();

    public TunaUsageAccountingState Normalized()
    {
        var records = NormalizeSessionRecords(SessionRecords);
        var knownMb = Math.Max(0m, TotalKnownAppPayloadMb);
        var unknownMb = Math.Max(0m, TotalUnknownAppPayloadMb);
        if (knownMb <= 0m && unknownMb <= 0m && TotalAppPayloadMb > 0m)
        {
            if (HasUnknownCost || (TotalPaidNkn <= 0m && LastSessionCostUnknown))
            {
                unknownMb = Math.Max(0m, TotalAppPayloadMb);
            }
            else
            {
                knownMb = Math.Max(0m, TotalAppPayloadMb);
            }
        }

        var totalAppPayloadMb = Math.Max(Math.Max(0m, TotalAppPayloadMb), knownMb + unknownMb);
        var hasUnknownCost = HasUnknownCost || unknownMb > 0m;
        var lastSessionCostUnknown = LastSessionCostUnknown ||
                                     (LastSessionAppPayloadMb > 0m && LastSessionPaidNkn <= 0m && records.LastOrDefault()?.PaymentTelemetryStatus is not TunaPaymentTelemetryStatus.Reported);
        return new TunaUsageAccountingState
        {
            TotalPaidNkn = Math.Max(0m, TotalPaidNkn),
            TotalAppPayloadMb = totalAppPayloadMb,
            TotalKnownAppPayloadMb = knownMb,
            TotalUnknownAppPayloadMb = unknownMb,
            HasUnknownCost = hasUnknownCost,
            LastSessionPaidNkn = Math.Max(0m, LastSessionPaidNkn),
            LastSessionAppPayloadMb = Math.Max(0m, LastSessionAppPayloadMb),
            LastSessionCostUnknown = lastSessionCostUnknown,
            LastUpdatedUtc = LastUpdatedUtc,
            SessionRecords = records,
        };
    }

    private List<TunaUsageSessionRecord> UpdateSessionRecord(
        string? sessionRunId,
        Func<TunaUsageSessionRecord, TunaUsageSessionRecord> update)
    {
        var records = NormalizeSessionRecords(SessionRecords);
        var resolvedRunId = ResolveSessionRunId(sessionRunId, records);
        var index = records.FindIndex(record => string.Equals(record.SessionRunId, resolvedRunId, StringComparison.Ordinal));
        if (index < 0)
        {
            records.Add(new TunaUsageSessionRecord
            {
                SessionRunId = resolvedRunId,
                StartedUtc = DateTimeOffset.UtcNow,
                Role = "listener",
            }.Normalized());
            index = records.Count - 1;
        }

        records[index] = update(records[index]).Normalized();
        return RetainSessionRecords(records);
    }

    private List<TunaUsageSessionRecord> AppendSessionRecord(TunaUsageSessionRecord record)
    {
        var records = NormalizeSessionRecords(SessionRecords);
        records.Add(record.Normalized());
        return RetainSessionRecords(records);
    }

    private static string ResolveSessionRunId(string? sessionRunId, List<TunaUsageSessionRecord> records)
    {
        if (!string.IsNullOrWhiteSpace(sessionRunId))
        {
            return sessionRunId.Trim();
        }

        var active = records.LastOrDefault(static record => record.EndedUtc is null);
        if (active is not null)
        {
            return active.SessionRunId;
        }

        return records.LastOrDefault()?.SessionRunId ?? Guid.NewGuid().ToString("N");
    }

    private static List<TunaUsageSessionRecord> NormalizeSessionRecords(IEnumerable<TunaUsageSessionRecord>? records)
        => RetainSessionRecords((records ?? []).Select(static record => (record ?? new TunaUsageSessionRecord()).Normalized()).ToList());

    private static List<TunaUsageSessionRecord> RetainSessionRecords(List<TunaUsageSessionRecord> records)
        => records.Count <= MaxSessionRecords
            ? records
            : records.Skip(records.Count - MaxSessionRecords).ToList();
}

internal sealed class JsonTunaRuntimePreferenceStore : ITunaRuntimePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string> pathProvider;

    public JsonTunaRuntimePreferenceStore(Func<string>? pathProvider = null)
    {
        this.pathProvider = pathProvider ?? DefaultPathProvider;
    }

    public TunaRuntimePreferenceState Load()
    {
        try
        {
            var path = pathProvider();
            if (!File.Exists(path))
            {
                return TunaRuntimePreferenceState.Default;
            }

            var state = JsonSerializer.Deserialize<TunaRuntimePreferenceState>(File.ReadAllText(path), JsonOptions);
            return (state ?? TunaRuntimePreferenceState.Default).Normalized();
        }
        catch
        {
            return TunaRuntimePreferenceState.Default;
        }
    }

    public void Save(TunaRuntimePreferenceState state)
    {
        var path = pathProvider();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize((state ?? TunaRuntimePreferenceState.Default).Normalized(), JsonOptions));
    }

    internal static string DefaultPathProvider()
    {
        var root = TunaRuntimeStateRoot.Resolve();
        return Path.Combine(root, "tuna-runtime-preferences.json");
    }
}

internal sealed class JsonTunaUsageAccountingStore : ITunaUsageAccountingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string> pathProvider;

    public JsonTunaUsageAccountingStore(Func<string>? pathProvider = null)
    {
        this.pathProvider = pathProvider ?? DefaultPathProvider;
    }

    public TunaUsageAccountingState Load()
    {
        try
        {
            var path = pathProvider();
            if (!File.Exists(path))
            {
                return TunaUsageAccountingState.Empty;
            }

            return (JsonSerializer.Deserialize<TunaUsageAccountingState>(File.ReadAllText(path), JsonOptions) ??
                    TunaUsageAccountingState.Empty).Normalized();
        }
        catch
        {
            return TunaUsageAccountingState.Empty;
        }
    }

    public void Save(TunaUsageAccountingState state)
    {
        var path = pathProvider();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize((state ?? TunaUsageAccountingState.Empty).Normalized(), JsonOptions));
    }

    internal static string DefaultPathProvider()
    {
        var root = TunaRuntimeStateRoot.Resolve();
        return Path.Combine(root, "tuna-usage-accounting.json");
    }
}

internal sealed class TunaRuntimePilotService : ITunaRuntimePilotService
{
    private static readonly TimeSpan ListenerRestartUnlockRetention = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly ITunaRuntimePreferenceStore preferenceStore;
    private readonly ITunaUsageAccountingStore usageStore;
    private readonly ITunaWalletLinkStore walletLinkStore;
    private readonly ITunaWalletVerifier walletVerifier;
    private readonly Func<DateTimeOffset> nowProvider;
    private char[]? unlockedPassword;
    private CancellationTokenSource? listenerRestartUnlockRetentionCts;
    private TunaRuntimePreferenceState preferences;
    private TunaUsageAccountingState usage;
    private string runtimeStatus;
    private ITransportAccelerationControl? currentTransportControl;
    private int unlockFailureCount;
    private DateTimeOffset? unlockCooldownUntilUtc;
    private CancellationTokenSource? unlockCooldownCts;
    private int stopInProgress;
    private int unlockAttemptInProgress;
    private bool currentSessionPaymentTelemetryObserved;
    private long currentSessionObservedBytesMoved;
    private string? currentSessionRunId;
    private int currentSessionPaymentEventCount;
    private DateTimeOffset? waitingForApprovedSessionStartedUtc;
    private DateTimeOffset? listenerStartUtc;
    private DateTimeOffset? providerReadyUtc;
    private DateTimeOffset? listenerReadyUtc;
    private DateTimeOffset? peerConnectedUtc;
    private DateTimeOffset? completedUtc;

    public event EventHandler? StateChanged;

    public TunaRuntimePilotService(
        ITunaRuntimePreferenceStore preferenceStore,
        ITunaUsageAccountingStore usageStore,
        ITunaWalletLinkStore walletLinkStore,
        ITunaWalletVerifier walletVerifier,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.preferenceStore = preferenceStore ?? throw new ArgumentNullException(nameof(preferenceStore));
        this.usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));
        this.walletLinkStore = walletLinkStore ?? throw new ArgumentNullException(nameof(walletLinkStore));
        this.walletVerifier = walletVerifier ?? throw new ArgumentNullException(nameof(walletVerifier));
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        preferences = this.preferenceStore.Load();
        usage = this.usageStore.Load();
        runtimeStatus = string.Equals(preferences.LastRuntimeStatus, "switching_to_regular_nkn", StringComparison.Ordinal)
            ? preferences.Enabled ? "locked" : "off"
            : preferences.LastRuntimeStatus;
        preferences = preferences.WithStatus(runtimeStatus);
        UpdateStartupTiming_NoLock(runtimeStatus, this.nowProvider());
    }

    public TunaRuntimePreferenceState Preferences
    {
        get
        {
            lock (gate)
            {
                return preferences;
            }
        }
    }

    public TunaUsageAccountingState Usage
    {
        get
        {
            lock (gate)
            {
                return usage;
            }
        }
    }

    public string RuntimeStatus
    {
        get
        {
            lock (gate)
            {
                return runtimeStatus;
            }
        }
    }

    public string StartupTimingSummary
    {
        get
        {
            lock (gate)
            {
                return BuildStartupTimingSummary_NoLock(DateTimeOffset.UtcNow);
            }
        }
    }

    public bool HasSessionUnlock
    {
        get
        {
            lock (gate)
            {
                return unlockedPassword is { Length: > 0 };
            }
        }
    }

    public async Task<TunaRuntimeUnlockState> GetUnlockStateAsync(CancellationToken ct = default)
    {
        var wallet = await LoadWalletStateAsync(ct).ConfigureAwait(false);
        var availability = walletVerifier.GetAvailability();
        lock (gate)
        {
            return BuildUnlockState_NoLock(wallet, availability, nowProvider());
        }
    }

    public async Task<TunaRuntimeUnlockResult> UnlockForSessionAsync(
        char[]? password,
        TunaRuntimeUnlockSource source,
        CancellationToken ct = default)
    {
        var acquiredUnlockAttempt = false;
        if (password is { Length: > 0 } &&
            Interlocked.CompareExchange(ref unlockAttemptInProgress, 1, 0) != 0)
        {
            try
            {
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                return TunaRuntimeUnlockResult.FromState(false, state, "Tuna unlock is already in progress.");
            }
            finally
            {
                Array.Clear(password);
            }
        }

        acquiredUnlockAttempt = password is { Length: > 0 };
        try
        {
            if (password is null || password.Length == 0)
            {
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                return TunaRuntimeUnlockResult.FromState(false, state, "Password required.");
            }

            var cooldownState = await GetUnlockStateAsync(ct).ConfigureAwait(false);
            if (cooldownState.IsCooldownActive)
            {
                return TunaRuntimeUnlockResult.FromState(
                    false,
                    cooldownState,
                    $"Too many failed attempts. Try again in {FormatCooldownRemaining(cooldownState.CooldownRemaining)}.");
            }

            TunaRuntimePreferenceState currentPreferences;
            lock (gate)
            {
                currentPreferences = preferences;
            }

            if (!currentPreferences.Enabled)
            {
                SetRuntimeStatus("off");
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                return TunaRuntimeUnlockResult.FromState(false, state, "Enable Tuna acceleration in Options first.");
            }

            var wallet = await LoadWalletStateAsync(ct).ConfigureAwait(false);
            if (wallet.Status != TunaWalletLinkStatus.VerifiedFunded ||
                string.IsNullOrWhiteSpace(wallet.WalletPath))
            {
                SetRuntimeStatus(StatusForWalletWithoutPaidListener(wallet));
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                return TunaRuntimeUnlockResult.FromState(false, state, "Link and validate a funded Tuna wallet first.");
            }

            var availability = walletVerifier.GetAvailability();
            if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.SidecarPath))
            {
                ReportSessionUnlockFailed("sidecar_unavailable");
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                return TunaRuntimeUnlockResult.FromState(false, state, "Tuna sidecar is unavailable.");
            }

            SetRuntimeStatus("unlock_validating");
            var result = await walletVerifier.ValidateAsync(wallet.WalletPath, password, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                ReportSessionUnlockFailed(result.Reason ?? "validation_failed");
                RegisterUnlockFailure(result.Reason);
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                var message = FormatUnlockFailureMessage(result.Reason);
                if (state.IsCooldownActive)
                {
                    message = $"{message} Try again in {FormatCooldownRemaining(state.CooldownRemaining)}.";
                }

                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_runtime_unlock_failed; source={source.ToString().ToLowerInvariant()}; reason={ClassifyUnlockFailure(result.Reason)}");
                return TunaRuntimeUnlockResult.FromState(false, state, message);
            }

            var nextWallet = wallet.WithValidationResult(result, nowProvider());
            await walletLinkStore.SaveAsync(nextWallet, ct).ConfigureAwait(false);
            if (nextWallet.Status != TunaWalletLinkStatus.VerifiedFunded)
            {
                ReportSessionUnlockFailed("wallet_empty");
                var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
                return TunaRuntimeUnlockResult.FromState(false, state, "Wallet has no NKN. Current NKN will be used.");
            }

            ResetUnlockFailureState();
            UnlockForNextSession(nextWallet, password);
            var unlockedState = await GetUnlockStateAsync(ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_runtime_unlocked; source={source.ToString().ToLowerInvariant()}; status={SanitizeStatusToken(unlockedState.RuntimeStatus)}; startup_timing={SanitizeStatusToken(StartupTimingSummary)}");
            return TunaRuntimeUnlockResult.FromState(true, unlockedState, "Tuna unlocked for the next approved session.");
        }
        finally
        {
            if (acquiredUnlockAttempt)
            {
                Interlocked.Exchange(ref unlockAttemptInProgress, 0);
            }

            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }

    public async Task<TunaRuntimeUnlockResult> LockOrStopForSessionAsync(
        string reason,
        TunaRuntimeUnlockSource source,
        CancellationToken ct = default)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "user_locked" : reason.Trim();
        ITransportAccelerationControl? control;
        var shouldStop = false;
        var shouldQueueStop = false;
        lock (gate)
        {
            shouldStop = IsTunaRuntimeSessionEngaged(runtimeStatus);
            ClearSessionUnlock_NoLock();
            if (shouldStop && currentTransportControl is not null)
            {
                runtimeStatus = "switching_to_regular_nkn";
                shouldQueueStop = Interlocked.CompareExchange(ref stopInProgress, 1, 0) == 0;
            }
            else
            {
                runtimeStatus = preferences.Enabled ? "locked" : "off";
            }

            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, nowProvider());
            preferenceStore.Save(preferences);
            control = currentTransportControl;
        }

        NotifyStateChanged();

        if (shouldQueueStop && control is not null)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await control.StopAccelerationAsync(normalizedReason, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_runtime_stop_failed; source={source.ToString().ToLowerInvariant()}; reason={SanitizeStatusToken(normalizedReason)}; error={ex.GetType().Name}");
                    }
                    finally
                    {
                        lock (gate)
                        {
                            Interlocked.Exchange(ref stopInProgress, 0);
                            if (string.Equals(runtimeStatus, "switching_to_regular_nkn", StringComparison.Ordinal))
                            {
                                runtimeStatus = preferences.Enabled ? "locked" : "off";
                                preferences = preferences.WithStatus(runtimeStatus);
                                UpdateStartupTiming_NoLock(runtimeStatus, nowProvider());
                                preferenceStore.Save(preferences);
                            }
                        }

                        NotifyStateChanged();
                        LocalOperationalLog.Info(
                            "NKN.Tuna",
                            $"event=tuna_runtime_locked; source={source.ToString().ToLowerInvariant()}; stop_requested=1; reason={SanitizeStatusToken(normalizedReason)}");
                    }
                },
                CancellationToken.None);
        }

        if (!shouldQueueStop)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_runtime_locked; source={source.ToString().ToLowerInvariant()}; stop_requested={(shouldStop ? 1 : 0)}; reason={SanitizeStatusToken(normalizedReason)}");
        }

        var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
        return TunaRuntimeUnlockResult.FromState(true, state, shouldStop
            ? "Switching Tuna off. Current NKN remains connected."
            : "Tuna wallet locked for this session.");
    }

    public void SavePreferences(TunaRuntimePreferenceState state)
    {
        var normalized = (state ?? TunaRuntimePreferenceState.Default).Normalized();
        ITransportAccelerationControl? control = null;
        var shouldStop = false;
        lock (gate)
        {
            if (!normalized.Enabled)
            {
                shouldStop = IsTunaRuntimeSessionEngaged(runtimeStatus);
                ClearSessionUnlock_NoLock();
                control = currentTransportControl;
            }

            runtimeStatus = normalized.Enabled ? normalized.LastRuntimeStatus : "off";
            preferences = normalized.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, nowProvider());
            preferenceStore.Save(preferences);
        }

        NotifyStateChanged();
        if (shouldStop && control is not null)
        {
            _ = Task.Run(
                () => control.StopAccelerationAsync("runtime_disabled", CancellationToken.None),
                CancellationToken.None);
        }
    }

    public void UnlockForNextSession(TunaWalletLinkState walletState, char[] password)
    {
        ArgumentNullException.ThrowIfNull(walletState);
        if (password is null || password.Length == 0)
        {
            return;
        }

        ITransportAccelerationControl? control = null;
        lock (gate)
        {
            ClearSessionUnlock_NoLock();
            if (!Preferences.Enabled ||
                walletState.Status != TunaWalletLinkStatus.VerifiedFunded ||
                string.IsNullOrWhiteSpace(walletState.WalletPath))
            {
                runtimeStatus = "unlock_rejected";
                preferences = preferences.WithStatus(runtimeStatus);
                UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
                preferenceStore.Save(preferences);
                NotifyStateChanged();
                return;
            }

            unlockedPassword = new char[password.Length];
            Array.Copy(password, unlockedPassword, password.Length);
            runtimeStatus = "waiting_for_approved_session";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
            control = currentTransportControl;
        }

        NotifyStateChanged();
        if (control is not null)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await control.RequestAccelerationNegotiationAsync("runtime_unlock", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LocalOperationalLog.Warn(
                            "NKN.Tuna",
                            $"event=tuna_runtime_negotiation_request_failed; reason=runtime_unlock; error={ex.GetType().Name}");
                    }
                },
                CancellationToken.None);
        }
    }

    public void ReportSessionUnlockFailed(string reason)
    {
        lock (gate)
        {
            ClearSessionUnlock_NoLock();
        }

        SetRuntimeStatus($"unlock_failed_{ClassifyUnlockFailure(reason)}");
    }

    public void ClearSessionUnlock()
    {
        lock (gate)
        {
            ClearSessionUnlock_NoLock();
            runtimeStatus = preferences.Enabled ? "locked" : "off";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
        }

        NotifyStateChanged();
    }

    public ISignalingTransport CreateNknTransport()
    {
        var envOptions = NknTunaAccelerationOptions.Load();
        if (envOptions.Enabled)
        {
            return TrackTransport(NknSignalingTransport.CreateWithTunaAcceleration(envOptions, listenerSupervisor: null));
        }

        var currentPreferences = Preferences;
        var availability = walletVerifier.GetAvailability();
        if (!currentPreferences.Enabled)
        {
            SetRuntimeStatus("off");
            return TrackTransport(availability.IsAvailable && !string.IsNullOrWhiteSpace(availability.SidecarPath)
                ? NknSignalingTransport.CreateWithTunaAcceleration(
                    NknTunaAccelerationOptions.CreatePassiveDialer(
                        availability.SidecarPath,
                        TunaRuntimePreferenceState.Default.Lanes),
                    listenerSupervisor: null)
                : new NknSignalingTransport());
        }

        if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.SidecarPath))
        {
            SetRuntimeStatus("sidecar_unavailable");
            return TrackTransport(new NknSignalingTransport());
        }

        var wallet = LoadWalletState();
        SetRuntimeStatusForTransportCreation(wallet);

        var runtimeOptions = NknTunaAccelerationOptions.CreateRuntimePilot(availability.SidecarPath, currentPreferences.Lanes);
        INknTunaListenerSidecarSupervisor? supervisor = new RuntimeListenerSupervisor(this);
        return TrackTransport(NknSignalingTransport.CreateWithTunaAcceleration(runtimeOptions, supervisor));
    }

    internal INknTunaListenerSidecarSupervisor CreateRuntimeListenerSupervisorForTests()
        => new RuntimeListenerSupervisor(this);

    private ISignalingTransport TrackTransport(ISignalingTransport transport)
    {
        lock (gate)
        {
            currentTransportControl = transport as ITransportAccelerationControl;
        }

        return transport;
    }

    private TunaWalletLinkState LoadWalletState()
    {
        try
        {
            return walletLinkStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    private async Task<TunaWalletLinkState> LoadWalletStateAsync(CancellationToken ct)
    {
        try
        {
            return await walletLinkStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    private TunaRuntimeUnlockState BuildUnlockState_NoLock(
        TunaWalletLinkState wallet,
        TunaWalletVerifierAvailability availability,
        DateTimeOffset now)
    {
        var walletFunded = wallet.Status == TunaWalletLinkStatus.VerifiedFunded &&
                           !string.IsNullOrWhiteSpace(wallet.WalletPath);
        var runtimeEnabled = preferences.Enabled;
        var sidecarAvailable = availability.IsAvailable && !string.IsNullOrWhiteSpace(availability.SidecarPath);
        var cooldownRemaining = GetCooldownRemaining_NoLock(now);
        var cooldownActive = cooldownRemaining > TimeSpan.Zero;
        var engaged = IsTunaRuntimeSessionEngaged(runtimeStatus);
        var switchingToNkn = string.Equals(SanitizeStatusToken(runtimeStatus), "switching_to_regular_nkn", StringComparison.Ordinal);
        var unlocked = unlockedPassword is { Length: > 0 };
        var unlockAttemptInProgressNow = Volatile.Read(ref unlockAttemptInProgress) != 0;
        var toggleOn = unlocked || engaged || unlockAttemptInProgressNow;
        var visible = walletFunded || toggleOn;
        var canUnlock = walletFunded && runtimeEnabled && sidecarAvailable && !cooldownActive && !switchingToNkn && !unlockAttemptInProgressNow;
        var canToggle = !switchingToNkn && !unlockAttemptInProgressNow && (toggleOn || canUnlock);
        var statusText = cooldownActive
            ? $"Try again in {FormatCooldownRemaining(cooldownRemaining)}"
            : switchingToNkn
                ? "Switching to regular NKN"
                : unlockAttemptInProgressNow
                    ? "Unlocking..."
                    : unlocked
                    ? "Unlocked for next session"
                    : engaged
                        ? "Tuna starting/active"
                        : "Locked";
        var message = BuildUnlockUserMessage(
            wallet,
            runtimeEnabled,
            sidecarAvailable,
            unlocked,
            engaged || switchingToNkn || unlockAttemptInProgressNow,
            runtimeStatus,
            cooldownRemaining);
        return new TunaRuntimeUnlockState(
            visible,
            canToggle,
            toggleOn,
            runtimeStatus,
            statusText,
            message,
            cooldownActive,
            cooldownRemaining);
    }

    private static string BuildUnlockUserMessage(
        TunaWalletLinkState wallet,
        bool runtimeEnabled,
        bool sidecarAvailable,
        bool unlocked,
        bool engaged,
        string runtimeStatus,
        TimeSpan cooldownRemaining)
    {
        if (cooldownRemaining > TimeSpan.Zero)
        {
            return $"Too many failed attempts. Try again in {FormatCooldownRemaining(cooldownRemaining)}.";
        }

        if (unlocked)
        {
            return TunaStatusPresentationMapper.FromRuntimeStatus("waiting_for_approved_session").Text;
        }

        if (engaged)
        {
            return TunaStatusPresentationMapper.FromRuntimeStatus(runtimeStatus).Text;
        }

        if (wallet.Status != TunaWalletLinkStatus.VerifiedFunded ||
            string.IsNullOrWhiteSpace(wallet.WalletPath))
        {
            return wallet.Status switch
            {
                TunaWalletLinkStatus.VerifiedEmpty => "Tuna wallet is empty. This computer will not pay.",
                TunaWalletLinkStatus.LinkedUnverified => "Validate the Tuna wallet in Options first.",
                TunaWalletLinkStatus.ValidationFailed => "Tuna wallet validation failed. Validate it again in Options.",
                _ => "Link and validate a funded Tuna wallet in Options.",
            };
        }

        if (!runtimeEnabled)
        {
            return "Enable Tuna acceleration in Options first.";
        }

        if (!sidecarAvailable)
        {
            return "Tuna sidecar is unavailable.";
        }

        return "Unlock Tuna wallet for this session.";
    }

    private NknTunaListenerSidecarOptions? CreateListenerSidecarOptions(RuntimeUsageSink usageSink)
    {
        var currentPreferences = Preferences;
        if (!currentPreferences.Enabled)
        {
            SetRuntimeStatus("off");
            return null;
        }

        var availability = walletVerifier.GetAvailability();
        if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.SidecarPath))
        {
            SetRuntimeStatus("sidecar_unavailable");
            return null;
        }

        var wallet = LoadWalletState();
        if (wallet.Status != TunaWalletLinkStatus.VerifiedFunded || string.IsNullOrWhiteSpace(wallet.WalletPath))
        {
            SetRuntimeStatus(StatusForWalletWithoutPaidListener(wallet));
            return null;
        }

        var allowDegradedProviderReady = EffectiveAllowDegradedProviderReady();
        return new NknTunaListenerSidecarOptions
        {
            SidecarExePath = availability.SidecarPath,
            WalletPath = wallet.WalletPath,
            TakeWalletPassword = () => TakeUnlockedPasswordForListenerStart(usageSink),
            MaxPriceNknPerMb = currentPreferences.MaxPriceNknPerMb,
            MaxTotalMiB = currentPreferences.MaxTotalMiB,
            MaxDurationSec = currentPreferences.MaxDurationSec,
            AcceptTimeoutSec = 120,
            ReadyTimeoutMs = 120_000,
            ListenStartTimeoutSec = 45,
            StartupAttemptCount = 2,
            RequireProviderReady = !allowDegradedProviderReady,
            ProviderReadyAttempts = 2,
            DegradedProviderGraceSeconds = EffectiveDegradedProviderGraceSeconds(),
            UsageSink = usageSink,
            StatusChanged = SetRuntimeStatus,
            CapHandoffRequested = RequestCapHandoff,
            CanTakeWalletPassword = () => HasSessionUnlock,
        };
    }

    private static bool EffectiveAllowDegradedProviderReady()
    {
        if (IsEnabled(ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
                TunaRuntimePreferenceState.RequireStrictProviderReadyEnvVar,
                category: "nkn_tuna_provider_readiness")))
        {
            return false;
        }

        return IsEnabled(ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
            TunaRuntimePreferenceState.AllowDegradedProviderReadyEnvVar,
            category: "nkn_tuna_provider_readiness"));
    }

    private static int EffectiveDegradedProviderGraceSeconds()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
            TunaRuntimePreferenceState.DegradedProviderGraceSecondsEnvVar,
            category: "nkn_tuna_provider_readiness");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, 300)
            : 0;
    }

    private static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private char[]? TakeUnlockedPasswordForListenerStart(RuntimeUsageSink usageSink)
    {
        char[] password;
        char[] passwordForSidecar;
        lock (gate)
        {
            password = unlockedPassword ?? [];
            if (password.Length == 0)
            {
                runtimeStatus = "wallet_not_unlocked";
                preferences = preferences.WithStatus(runtimeStatus);
                UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
                preferenceStore.Save(preferences);
                NotifyStateChanged();
                return null;
            }

            passwordForSidecar = new char[password.Length];
            Array.Copy(password, passwordForSidecar, password.Length);
            ScheduleListenerRestartUnlockRetention_NoLock();
            var now = DateTimeOffset.UtcNow;
            currentSessionRunId = Guid.NewGuid().ToString("N");
            currentSessionPaymentEventCount = 0;
            runtimeStatus = "listener_starting";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, now);
            preferenceStore.Save(preferences);
            usage = usage.StartNewSession(currentSessionRunId, "listener", now);
            currentSessionPaymentTelemetryObserved = false;
            currentSessionObservedBytesMoved = 0;
            usageStore.Save(usage);
        }

        NotifyStateChanged();
        return passwordForSidecar;
    }

    private void SetRuntimeStatusForTransportCreation(TunaWalletLinkState wallet)
    {
        lock (gate)
        {
            runtimeStatus = preferences.Enabled
                ? unlockedPassword is { Length: > 0 }
                    ? "waiting_for_approved_session"
                    : StatusForWalletWithoutPaidListener(wallet)
                : "off";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
        }

        NotifyStateChanged();
    }

    private static string StatusForWalletWithoutPaidListener(TunaWalletLinkState wallet)
        => wallet.Status switch
        {
            TunaWalletLinkStatus.VerifiedFunded => "locked",
            TunaWalletLinkStatus.VerifiedEmpty => "wallet_empty_dialer_only",
            TunaWalletLinkStatus.ValidationFailed => "wallet_validation_failed_dialer_only",
            TunaWalletLinkStatus.LinkedUnverified => "wallet_unverified_dialer_only",
            _ => "wallet_missing_dialer_only",
        };

    private void RecordPayment(NknTunaPaymentTelemetry payment)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            currentSessionPaymentEventCount++;
            usage = usage.AddPayment(currentSessionRunId, payment.AmountNkn, payment.BytesMoved, now);
            currentSessionPaymentTelemetryObserved = true;
            if (payment.BytesMoved > currentSessionObservedBytesMoved)
            {
                currentSessionObservedBytesMoved = payment.BytesMoved;
            }

            usageStore.Save(usage);
        }

        NotifyStateChanged();
    }

    private void RecordSummary(NknTunaSessionUsageTelemetry summary)
    {
        TunaUsageSessionRecord? recordedSession;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var paymentObserved = currentSessionPaymentTelemetryObserved || summary.PaymentTelemetryObserved;
            var sessionBytesMoved = Math.Max(summary.BytesMoved, currentSessionObservedBytesMoved);
            currentSessionObservedBytesMoved = Math.Max(currentSessionObservedBytesMoved, sessionBytesMoved);
            var paymentEventCount = Math.Max(currentSessionPaymentEventCount, summary.PaymentEventCount);
            usage = usage.CompleteSession(
                currentSessionRunId,
                sessionBytesMoved,
                paymentObserved,
                summary.CumulativeSpendNkn,
                summary.PaymentStatus,
                paymentEventCount,
                summary.Reason,
                summary.CapReason,
                summary.FallbackReason,
                completedFromSummary: true,
                now);
            recordedSession = usage.LastSessionRecord;
            usageStore.Save(usage);
            runtimeStatus = summary.CapReached || !string.IsNullOrWhiteSpace(summary.CapReason) || string.Equals(summary.Reason, "context deadline exceeded", StringComparison.OrdinalIgnoreCase)
                ? "cap_reached"
                : "fallback_current_nkn";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, now);
            preferenceStore.Save(preferences);
            currentSessionRunId = null;
            currentSessionPaymentEventCount = 0;
            currentSessionPaymentTelemetryObserved = false;
            currentSessionObservedBytesMoved = 0;
        }

        LogUsageSessionRecorded(recordedSession);
        NotifyStateChanged();
    }

    private void RequestCapHandoff(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "cap_reached"
            : reason.Trim();
        ITransportAccelerationControl? control;
        var shouldQueueStop = false;
        lock (gate)
        {
            runtimeStatus = "cap_handoff_pending";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
            control = currentTransportControl;
            if (control is not null)
            {
                shouldQueueStop = Interlocked.CompareExchange(ref stopInProgress, 1, 0) == 0;
            }
        }

        NotifyStateChanged();
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_runtime_cap_handoff_requested; reason={SanitizeStatusToken(normalizedReason)}; stop_queued={(shouldQueueStop ? 1 : 0)}");

        if (control is null)
        {
            SetRuntimeStatus("cap_reached");
            return;
        }

        if (!shouldQueueStop)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await control.StopAccelerationAsync(normalizedReason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_runtime_cap_handoff_stop_failed; reason={SanitizeStatusToken(normalizedReason)}; error={ex.GetType().Name}");
                }
                finally
                {
                    lock (gate)
                    {
                        Interlocked.Exchange(ref stopInProgress, 0);
                        runtimeStatus = "cap_reached";
                        preferences = preferences.WithStatus(runtimeStatus);
                        UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
                        preferenceStore.Save(preferences);
                    }

                    NotifyStateChanged();
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_runtime_cap_handoff_completed; reason={SanitizeStatusToken(normalizedReason)}");
                }
            },
            CancellationToken.None);
    }

    private void RecordIncompleteSession(string reason)
    {
        TunaUsageSessionRecord? recordedSession = null;
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(currentSessionRunId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            usage = usage.CompleteSession(
                currentSessionRunId,
                currentSessionObservedBytesMoved,
                currentSessionPaymentTelemetryObserved,
                null,
                TunaPaymentTelemetryStatus.AccountingIncomplete,
                currentSessionPaymentEventCount,
                reason,
                string.Empty,
                reason,
                completedFromSummary: false,
                now);
            recordedSession = usage.LastSessionRecord;
            usageStore.Save(usage);
            currentSessionRunId = null;
            currentSessionPaymentEventCount = 0;
            currentSessionPaymentTelemetryObserved = false;
            currentSessionObservedBytesMoved = 0;
        }

        LogUsageSessionRecorded(recordedSession);
        NotifyStateChanged();
    }

    private static void LogUsageSessionRecorded(TunaUsageSessionRecord? record)
    {
        if (record is null)
        {
            return;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            "event=tuna_usage_session_recorded; " +
            $"session_run_id_hash={HashForLog(record.SessionRunId)}; " +
            $"payment_status={SanitizeStatusToken(record.PaymentTelemetryStatus)}; " +
            $"bytes_moved={record.BytesMoved}; " +
            $"app_payload_mb={record.AppPayloadMb.ToString("0.######", CultureInfo.InvariantCulture)}; " +
            $"paid_nkn={record.PaidNkn.ToString("0.########", CultureInfo.InvariantCulture)}; " +
            $"payment_event_count={record.PaymentEventCount}; " +
            $"cap_reason={SanitizeStatusToken(record.CapReason)}; " +
            $"fallback_reason={SanitizeStatusToken(record.FallbackReason)}; " +
            $"completed_from_summary={(record.CompletedFromSummary ? 1 : 0)}");
    }

    private void SetRuntimeStatus(string status)
    {
        string previousStatus;
        string nextStatus;
        string timingSummary;
        bool changed;
        lock (gate)
        {
            previousStatus = runtimeStatus;
            runtimeStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
            nextStatus = runtimeStatus;
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            timingSummary = BuildStartupTimingSummary_NoLock(DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
            changed = !string.Equals(previousStatus, nextStatus, StringComparison.Ordinal);
        }

        NotifyStateChanged();
        if (changed)
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_runtime_timeline; previous_status={SanitizeStatusToken(previousStatus)}; status={SanitizeStatusToken(nextStatus)}; startup_timing={SanitizeStatusToken(timingSummary)}");
        }
    }

    private void ClearSessionUnlock_NoLock()
    {
        listenerRestartUnlockRetentionCts?.Cancel();
        listenerRestartUnlockRetentionCts?.Dispose();
        listenerRestartUnlockRetentionCts = null;
        if (unlockedPassword is not null)
        {
            Array.Clear(unlockedPassword);
            unlockedPassword = null;
        }
    }

    private void ScheduleListenerRestartUnlockRetention_NoLock()
    {
        listenerRestartUnlockRetentionCts?.Cancel();
        listenerRestartUnlockRetentionCts?.Dispose();
        var cts = new CancellationTokenSource();
        listenerRestartUnlockRetentionCts = cts;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(ListenerRestartUnlockRetention, cts.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!ReferenceEquals(listenerRestartUnlockRetentionCts, cts))
                        {
                            return;
                        }

                        listenerRestartUnlockRetentionCts?.Dispose();
                        listenerRestartUnlockRetentionCts = null;
                        if (unlockedPassword is not null)
                        {
                            Array.Clear(unlockedPassword);
                            unlockedPassword = null;
                        }
                    }

                    NotifyStateChanged();
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        "event=tuna_runtime_listener_restart_unlock_expired; retention_sec=60");
                }
                catch (OperationCanceledException)
                {
                    // A lock, unlock, or newer listener start superseded this retention window.
                }
            },
            CancellationToken.None);
    }

    private TimeSpan RegisterUnlockFailure(string? reason)
    {
        if (!IsWrongPasswordReason(reason))
        {
            return TimeSpan.Zero;
        }

        TimeSpan cooldown;
        CancellationTokenSource? cts = null;
        lock (gate)
        {
            unlockFailureCount++;
            cooldown = unlockFailureCount switch
            {
                <= 1 => TimeSpan.Zero,
                2 => TimeSpan.FromSeconds(2),
                <= 4 => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(30),
            };
            unlockCooldownUntilUtc = cooldown > TimeSpan.Zero ? nowProvider().Add(cooldown) : null;
            unlockCooldownCts?.Cancel();
            unlockCooldownCts?.Dispose();
            unlockCooldownCts = null;
            if (cooldown > TimeSpan.Zero)
            {
                cts = new CancellationTokenSource();
                unlockCooldownCts = cts;
            }
        }

        if (cts is not null)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(cooldown, cts.Token).ConfigureAwait(false);
                        lock (gate)
                        {
                            if (!ReferenceEquals(unlockCooldownCts, cts))
                            {
                                return;
                            }

                            unlockCooldownUntilUtc = null;
                            unlockCooldownCts?.Dispose();
                            unlockCooldownCts = null;
                        }

                        NotifyStateChanged();
                    }
                    catch (OperationCanceledException)
                    {
                        // A later attempt or successful unlock superseded this cooldown.
                    }
                },
                CancellationToken.None);
        }

        return cooldown;
    }

    private void ResetUnlockFailureState()
    {
        lock (gate)
        {
            unlockFailureCount = 0;
            unlockCooldownUntilUtc = null;
            unlockCooldownCts?.Cancel();
            unlockCooldownCts?.Dispose();
            unlockCooldownCts = null;
        }
    }

    private TimeSpan GetCooldownRemaining_NoLock(DateTimeOffset now)
    {
        if (unlockCooldownUntilUtc is not { } until)
        {
            return TimeSpan.Zero;
        }

        if (now < until)
        {
            return until - now;
        }

        unlockCooldownUntilUtc = null;
        return TimeSpan.Zero;
    }

    private static bool IsTunaRuntimeSessionEngaged(string? status)
    {
        var normalized = SanitizeStatusToken(status);
        return normalized.Equals("checking_payer_priority", StringComparison.Ordinal) ||
               normalized.StartsWith("negotiation_scheduled_", StringComparison.Ordinal) ||
               normalized.Equals("unlock_validating", StringComparison.Ordinal) ||
               normalized.Equals("listener_starting", StringComparison.Ordinal) ||
               normalized.Equals("listener_paths_starting", StringComparison.Ordinal) ||
               normalized.Equals("listener_retrying", StringComparison.Ordinal) ||
               normalized.Equals("listener_start_timeout", StringComparison.Ordinal) ||
               normalized.Equals("provider_paths_retrying", StringComparison.Ordinal) ||
               normalized.Equals("provider_paths_ready", StringComparison.Ordinal) ||
               normalized.Equals("provider_paths_degraded", StringComparison.Ordinal) ||
               normalized.Equals("provider_paths_wait_timeout", StringComparison.Ordinal) ||
               normalized.Equals("listener_ready", StringComparison.Ordinal) ||
               normalized.Equals("waiting_for_peer_dial", StringComparison.Ordinal) ||
               normalized.Equals("peer_connected", StringComparison.Ordinal) ||
               normalized.Equals("waiting_for_answer", StringComparison.Ordinal) ||
               normalized.Equals("renegotiating_after_user_unlock", StringComparison.Ordinal) ||
               normalized.Equals("dialer_starting", StringComparison.Ordinal) ||
               normalized.Equals("dialer_ready", StringComparison.Ordinal) ||
               normalized.Equals("negotiated", StringComparison.Ordinal) ||
               normalized.Equals("active", StringComparison.Ordinal);
    }

    private static string FormatCooldownRemaining(TimeSpan remaining)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return seconds == 1 ? "1 second" : $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds";
    }

    private static string FormatUnlockFailureMessage(string? reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : reason.Trim().ToLowerInvariant();
        if (IsWrongPasswordReason(normalized))
        {
            return "Unlock failed. Check the wallet password and try again.";
        }

        if (normalized.Contains("missing", StringComparison.Ordinal) ||
            normalized.Contains("not_found", StringComparison.Ordinal))
        {
            return "Unlock failed. Wallet file is missing.";
        }

        if (normalized.Contains("sidecar", StringComparison.Ordinal))
        {
            return "Unlock failed. Tuna sidecar is unavailable; current NKN will be used.";
        }

        if (normalized.Contains("timeout", StringComparison.Ordinal))
        {
            return "Unlock timed out. Current NKN will be used.";
        }

        return "Unlock failed. Current NKN will be used.";
    }

    private static bool IsWrongPasswordReason(string? reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : reason.Trim().ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("decrypt", StringComparison.Ordinal) ||
               normalized.Contains("unlock wallet", StringComparison.Ordinal);
    }

    private void UpdateStartupTiming_NoLock(string status, DateTimeOffset now)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
        switch (normalized)
        {
            case "off":
            case "locked":
            case "wallet_missing_dialer_only":
            case "wallet_unverified_dialer_only":
            case "wallet_validation_failed_dialer_only":
            case "wallet_empty_dialer_only":
            case "sidecar_unavailable":
            case "wallet_not_unlocked":
            case "unlock_rejected":
            case "unlock_failed_wrong_password":
            case "unlock_failed_wallet_missing":
            case "unlock_failed_wallet_empty":
            case "unlock_failed_sidecar_unavailable":
            case "unlock_failed_timeout":
            case "unlock_failed_wallet_invalid":
            case "unlock_failed_unknown":
                waitingForApprovedSessionStartedUtc = null;
                listenerStartUtc = null;
                providerReadyUtc = null;
                listenerReadyUtc = null;
                peerConnectedUtc = null;
                completedUtc = null;
                break;
            case "waiting_for_approved_session":
                waitingForApprovedSessionStartedUtc ??= now;
                listenerStartUtc = null;
                providerReadyUtc = null;
                listenerReadyUtc = null;
                peerConnectedUtc = null;
                completedUtc = null;
                break;
            case "listener_starting":
            case "listener_paths_starting":
                waitingForApprovedSessionStartedUtc ??= now;
                listenerStartUtc = now;
                providerReadyUtc = null;
                listenerReadyUtc = null;
                peerConnectedUtc = null;
                completedUtc = null;
                break;
            case "listener_retrying":
            case "listener_start_timeout":
            case "provider_paths_retrying":
                waitingForApprovedSessionStartedUtc ??= now;
                listenerStartUtc ??= now;
                break;
            case "provider_paths_ready":
            case "provider_paths_degraded":
                providerReadyUtc ??= now;
                break;
            case "provider_paths_wait_timeout":
                completedUtc ??= now;
                break;
            case "listener_ready":
            case "waiting_for_peer_dial":
                listenerReadyUtc ??= now;
                break;
            case "peer_connected":
                peerConnectedUtc ??= now;
                break;
            case "fallback_current_nkn":
            case "cap_reached":
            case "listener_exited":
            case "listener_failed":
            case "listener_start_failed":
            case "listener_ready_timeout":
            case "sidecar_error":
            case "session_summary":
                completedUtc ??= now;
                break;
        }
    }

    private string BuildStartupTimingSummary_NoLock(DateTimeOffset now)
    {
        if (listenerStartUtc is null)
        {
            return waitingForApprovedSessionStartedUtc is null
                ? "(none)"
                : $"waiting for approved session {FormatElapsed(waitingForApprovedSessionStartedUtc.Value, now)}";
        }

        var end = completedUtc ?? now;
        var parts = new List<string>(5);
        if (waitingForApprovedSessionStartedUtc is not null &&
            waitingForApprovedSessionStartedUtc.Value < listenerStartUtc.Value)
        {
            parts.Add($"session wait {FormatElapsed(waitingForApprovedSessionStartedUtc.Value, listenerStartUtc.Value)}");
        }

        if (providerReadyUtc is not null)
        {
            parts.Add($"providers {FormatElapsed(listenerStartUtc.Value, providerReadyUtc.Value)}");
        }

        if (listenerReadyUtc is not null)
        {
            parts.Add($"listener ready {FormatElapsed(listenerStartUtc.Value, listenerReadyUtc.Value)}");
        }
        else
        {
            parts.Add($"listener starting {FormatElapsed(listenerStartUtc.Value, end)}");
        }

        if (peerConnectedUtc is not null)
        {
            parts.Add($"peer connected {FormatElapsed(listenerStartUtc.Value, peerConnectedUtc.Value)}");
        }
        else if (listenerReadyUtc is not null)
        {
            parts.Add($"waiting for peer dial {FormatElapsed(listenerReadyUtc.Value, end)}");
        }

        return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
    }

    private static string FormatElapsed(DateTimeOffset start, DateTimeOffset end)
    {
        var elapsedMs = Math.Max(0, (long)(end - start).TotalMilliseconds);
        return elapsedMs < 1000
            ? $"{elapsedMs.ToString(CultureInfo.InvariantCulture)} ms"
            : $"{(elapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} s";
    }

    private static string ClassifyUnlockFailure(string? reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : reason.Trim().ToLowerInvariant();
        if (normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("decrypt", StringComparison.Ordinal) ||
            normalized.Contains("unlock wallet", StringComparison.Ordinal))
        {
            return "wrong_password";
        }

        if (normalized.Contains("missing", StringComparison.Ordinal) ||
            normalized.Contains("not_found", StringComparison.Ordinal))
        {
            return "wallet_missing";
        }

        if (normalized.Contains("empty", StringComparison.Ordinal))
        {
            return "wallet_empty";
        }

        if (normalized.Contains("sidecar", StringComparison.Ordinal))
        {
            return "sidecar_unavailable";
        }

        if (normalized.Contains("timeout", StringComparison.Ordinal))
        {
            return "timeout";
        }

        if (normalized.Contains("json", StringComparison.Ordinal) ||
            normalized.Contains("invalid", StringComparison.Ordinal))
        {
            return "wallet_invalid";
        }

        return "unknown";
    }

    private static string SanitizeStatusToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 96)];
        var written = 0;
        foreach (var ch in value.Trim())
        {
            if (written >= buffer.Length)
            {
                break;
            }

            buffer[written++] = char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_';
        }

        return written == 0 ? "unknown" : new string(buffer[..written]);
    }

    private static string HashForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    private void NotifyStateChanged()
        => StateChanged?.Invoke(this, EventArgs.Empty);

    private sealed class RuntimeUsageSink : INknTunaUsageTelemetrySink
    {
        private readonly TunaRuntimePilotService owner;

        public RuntimeUsageSink(TunaRuntimePilotService owner)
        {
            this.owner = owner;
        }

        public void StartNewSession()
        {
            lock (owner.gate)
            {
                owner.usage = owner.usage.StartNewSession();
                owner.usageStore.Save(owner.usage);
            }

            owner.NotifyStateChanged();
        }

        public void RecordPayment(NknTunaPaymentTelemetry payment) => owner.RecordPayment(payment);

        public void RecordSummary(NknTunaSessionUsageTelemetry summary) => owner.RecordSummary(summary);

        public void RecordIncomplete(string reason) => owner.RecordIncompleteSession(reason);
    }

    private sealed class RuntimeListenerSupervisor : INknTunaListenerSidecarSupervisor
    {
        private readonly object gate = new();
        private readonly TunaRuntimePilotService owner;
        private NknTunaListenerSidecarSupervisor? supervisor;
        private string? supervisorKey;
        private bool disposed;

        public RuntimeListenerSupervisor(TunaRuntimePilotService owner)
        {
            this.owner = owner;
        }

        public bool CanOfferListener
        {
            get
            {
                lock (gate)
                {
                    if (supervisor?.CanOfferListener == true)
                    {
                        return true;
                    }
                }

                return owner.HasSessionUnlock;
            }
        }

        public async Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(NknTunaListenerStartRequest request, CancellationToken ct)
        {
            NknTunaListenerSidecarSupervisor current;
            var usageSink = new RuntimeUsageSink(owner);
            var options = owner.CreateListenerSidecarOptions(usageSink);
            if (options is null)
            {
                return null;
            }

            var nextKey = ListenerOptionsKey(options);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (supervisor is null || !string.Equals(supervisorKey, nextKey, StringComparison.Ordinal))
                {
                    supervisor?.Dispose();
                    supervisor = new NknTunaListenerSidecarSupervisor(options);
                    supervisorKey = nextKey;
                }

                current = supervisor;
            }

            return await current.EnsureStartedAsync(request, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                supervisor?.Dispose();
                supervisor = null;
                supervisorKey = null;
            }
        }

        public void Stop(string reason)
        {
            lock (gate)
            {
                supervisor?.Stop(reason);
            }
        }

        private static string ListenerOptionsKey(NknTunaListenerSidecarOptions options)
            => string.Join(
                "|",
                options.SidecarExePath,
                options.WalletPath,
                options.MaxPriceNknPerMb,
                options.MaxTotalMiB.ToString(CultureInfo.InvariantCulture),
                options.MaxDurationSec.ToString(CultureInfo.InvariantCulture),
                options.AcceptTimeoutSec.ToString(CultureInfo.InvariantCulture),
                options.ReadyTimeoutMs.ToString(CultureInfo.InvariantCulture),
                options.ListenStartTimeoutSec.ToString(CultureInfo.InvariantCulture),
                options.StartupAttemptCount.ToString(CultureInfo.InvariantCulture),
                options.RequireProviderReady ? "strict_provider_ready" : "degraded_provider_ready",
                options.ProviderReadyAttempts.ToString(CultureInfo.InvariantCulture),
                options.DegradedProviderGraceSeconds.ToString(CultureInfo.InvariantCulture));
    }
}
