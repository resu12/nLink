using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
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
    public const string DefaultMaxPriceNknPerMb = "0.0002";
    public const int DefaultMaxTotalMiB = 2048;
    public const int DefaultMaxDurationSec = 1800;

    public bool Enabled { get; init; }

    public bool FileLaneEnabled { get; init; } = true;

    public bool ScreenLaneEnabled { get; init; } = true;

    public string MaxPriceNknPerMb { get; init; } = DefaultMaxPriceNknPerMb;

    public int MaxTotalMiB { get; init; } = DefaultMaxTotalMiB;

    public int MaxDurationSec { get; init; } = DefaultMaxDurationSec;

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
            LastRuntimeStatus = status,
        };
    }
}

internal sealed class TunaUsageAccountingState
{
    public decimal TotalPaidNkn { get; init; }

    public decimal TotalAppPayloadMb { get; init; }

    public bool HasUnknownCost { get; init; }

    public decimal LastSessionPaidNkn { get; init; }

    public decimal LastSessionAppPayloadMb { get; init; }

    public bool LastSessionCostUnknown { get; init; }

    public DateTimeOffset? LastUpdatedUtc { get; init; }

    [JsonIgnore]
    public decimal AverageNknPerMb => TotalAppPayloadMb > 0m ? TotalPaidNkn / TotalAppPayloadMb : 0m;

    [JsonIgnore]
    public decimal LastSessionAverageNknPerMb => LastSessionAppPayloadMb > 0m ? LastSessionPaidNkn / LastSessionAppPayloadMb : 0m;

    public static TunaUsageAccountingState Empty { get; } = new();

    public TunaUsageAccountingState AddPayment(decimal amountNkn, DateTimeOffset now)
        => new()
        {
            TotalPaidNkn = Math.Max(0m, TotalPaidNkn + Math.Max(0m, amountNkn)),
            TotalAppPayloadMb = TotalAppPayloadMb,
            HasUnknownCost = HasUnknownCost,
            LastSessionPaidNkn = Math.Max(0m, LastSessionPaidNkn + Math.Max(0m, amountNkn)),
            LastSessionAppPayloadMb = LastSessionAppPayloadMb,
            LastSessionCostUnknown = false,
            LastUpdatedUtc = now,
        };

    public TunaUsageAccountingState CompleteSession(long bytesMoved, bool paymentTelemetryObserved, DateTimeOffset now)
    {
        var mb = Math.Max(0m, bytesMoved / 1_000_000m);
        var currentSessionMb = Math.Max(0m, LastSessionAppPayloadMb);
        var deltaMb = Math.Max(0m, mb - currentSessionMb);
        var unknownCost = mb > 0m && !paymentTelemetryObserved;
        return new TunaUsageAccountingState
        {
            TotalPaidNkn = TotalPaidNkn,
            TotalAppPayloadMb = Math.Max(0m, TotalAppPayloadMb + deltaMb),
            HasUnknownCost = paymentTelemetryObserved ? HasUnknownCost : HasUnknownCost || unknownCost,
            LastSessionPaidNkn = LastSessionPaidNkn,
            LastSessionAppPayloadMb = Math.Max(currentSessionMb, mb),
            LastSessionCostUnknown = paymentTelemetryObserved ? false : LastSessionCostUnknown || unknownCost,
            LastUpdatedUtc = now,
        };
    }

    public TunaUsageAccountingState StartNewSession()
        => new()
        {
            TotalPaidNkn = TotalPaidNkn,
            TotalAppPayloadMb = TotalAppPayloadMb,
            HasUnknownCost = HasUnknownCost,
            LastSessionPaidNkn = 0m,
            LastSessionAppPayloadMb = 0m,
            LastSessionCostUnknown = false,
            LastUpdatedUtc = LastUpdatedUtc,
        };

    public TunaUsageAccountingState Normalized()
    {
        var hasUnknownCost = HasUnknownCost ||
                             (TotalAppPayloadMb > 0m && TotalPaidNkn <= 0m);
        var lastSessionCostUnknown = LastSessionCostUnknown ||
                                     (LastSessionAppPayloadMb > 0m && LastSessionPaidNkn <= 0m);
        return new TunaUsageAccountingState
        {
            TotalPaidNkn = Math.Max(0m, TotalPaidNkn),
            TotalAppPayloadMb = Math.Max(0m, TotalAppPayloadMb),
            HasUnknownCost = hasUnknownCost,
            LastSessionPaidNkn = Math.Max(0m, LastSessionPaidNkn),
            LastSessionAppPayloadMb = Math.Max(0m, LastSessionAppPayloadMb),
            LastSessionCostUnknown = lastSessionCostUnknown,
            LastUpdatedUtc = LastUpdatedUtc,
        };
    }
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
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "nLink");
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
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "nLink");
        return Path.Combine(root, "tuna-usage-accounting.json");
    }
}

internal sealed class TunaRuntimePilotService : ITunaRuntimePilotService
{
    private readonly object gate = new();
    private readonly ITunaRuntimePreferenceStore preferenceStore;
    private readonly ITunaUsageAccountingStore usageStore;
    private readonly ITunaWalletLinkStore walletLinkStore;
    private readonly ITunaWalletVerifier walletVerifier;
    private readonly Func<DateTimeOffset> nowProvider;
    private char[]? unlockedPassword;
    private TunaRuntimePreferenceState preferences;
    private TunaUsageAccountingState usage;
    private string runtimeStatus;
    private ITransportAccelerationControl? currentTransportControl;
    private int unlockFailureCount;
    private DateTimeOffset? unlockCooldownUntilUtc;
    private CancellationTokenSource? unlockCooldownCts;
    private bool currentSessionPaymentTelemetryObserved;
    private long currentSessionObservedBytesMoved;
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
        runtimeStatus = preferences.LastRuntimeStatus;
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
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_runtime_unlocked; source={source.ToString().ToLowerInvariant()}");
            var unlockedState = await GetUnlockStateAsync(ct).ConfigureAwait(false);
            return TunaRuntimeUnlockResult.FromState(true, unlockedState, "Tuna unlocked for the next approved session.");
        }
        finally
        {
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
        lock (gate)
        {
            shouldStop = IsTunaRuntimeSessionEngaged(runtimeStatus);
            ClearSessionUnlock_NoLock();
            runtimeStatus = preferences.Enabled ? "locked" : "off";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, nowProvider());
            preferenceStore.Save(preferences);
            control = currentTransportControl;
        }

        NotifyStateChanged();

        if (shouldStop && control is not null)
        {
            try
            {
                await control.StopAccelerationAsync(normalizedReason, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_runtime_stop_failed; source={source.ToString().ToLowerInvariant()}; reason={SanitizeStatusToken(normalizedReason)}; error={ex.GetType().Name}");
            }
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_runtime_locked; source={source.ToString().ToLowerInvariant()}; stop_requested={(shouldStop ? 1 : 0)}; reason={SanitizeStatusToken(normalizedReason)}");
        var state = await GetUnlockStateAsync(ct).ConfigureAwait(false);
        return TunaRuntimeUnlockResult.FromState(true, state, shouldStop
            ? "Tuna stopped for this session. Current NKN remains connected."
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
        var unlocked = unlockedPassword is { Length: > 0 };
        var toggleOn = unlocked || engaged;
        var visible = walletFunded || toggleOn;
        var canUnlock = walletFunded && runtimeEnabled && sidecarAvailable && !cooldownActive;
        var canToggle = toggleOn || canUnlock;
        var statusText = cooldownActive
            ? $"Try again in {FormatCooldownRemaining(cooldownRemaining)}"
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
            engaged,
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
            return "Tuna wallet unlocked for the next approved session. Click to lock.";
        }

        if (engaged)
        {
            return $"Tuna is starting for this session ({SanitizeStatusToken(runtimeStatus)}). Click to stop Tuna and use current NKN.";
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

        return new NknTunaListenerSidecarOptions
        {
            SidecarExePath = availability.SidecarPath,
            WalletPath = wallet.WalletPath,
            TakeWalletPassword = () => TakeUnlockedPasswordForListenerStart(usageSink),
            MaxPriceNknPerMb = currentPreferences.MaxPriceNknPerMb,
            MaxTotalMiB = currentPreferences.MaxTotalMiB,
            MaxDurationSec = currentPreferences.MaxDurationSec,
            AcceptTimeoutSec = 120,
            UsageSink = usageSink,
            StatusChanged = SetRuntimeStatus,
        };
    }

    private char[]? TakeUnlockedPasswordForListenerStart(RuntimeUsageSink usageSink)
    {
        char[] password;
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

            unlockedPassword = null;
            runtimeStatus = "listener_starting";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
            usage = usage.StartNewSession();
            currentSessionPaymentTelemetryObserved = false;
            currentSessionObservedBytesMoved = 0;
            usageStore.Save(usage);
        }

        NotifyStateChanged();
        return password;
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
            usage = usage.AddPayment(payment.AmountNkn, now);
            currentSessionPaymentTelemetryObserved = true;
            if (payment.BytesMoved > currentSessionObservedBytesMoved)
            {
                currentSessionObservedBytesMoved = payment.BytesMoved;
                usage = usage.CompleteSession(currentSessionObservedBytesMoved, paymentTelemetryObserved: true, now);
            }

            usageStore.Save(usage);
        }

        NotifyStateChanged();
    }

    private void RecordSummary(NknTunaSessionUsageTelemetry summary)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var paymentObserved = currentSessionPaymentTelemetryObserved || summary.PaymentTelemetryObserved;
            if (summary.CumulativeSpendNkn is { } cumulativeSpend &&
                cumulativeSpend > usage.LastSessionPaidNkn)
            {
                usage = usage.AddPayment(cumulativeSpend - usage.LastSessionPaidNkn, now);
                paymentObserved = true;
            }

            var sessionBytesMoved = Math.Max(summary.BytesMoved, currentSessionObservedBytesMoved);
            currentSessionObservedBytesMoved = Math.Max(currentSessionObservedBytesMoved, sessionBytesMoved);
            usage = usage.CompleteSession(sessionBytesMoved, paymentObserved, now);
            usageStore.Save(usage);
            runtimeStatus = string.Equals(summary.Reason, "context deadline exceeded", StringComparison.OrdinalIgnoreCase)
                ? "cap_reached"
                : "fallback_current_nkn";
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, now);
            preferenceStore.Save(preferences);
        }

        NotifyStateChanged();
    }

    private void SetRuntimeStatus(string status)
    {
        lock (gate)
        {
            runtimeStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
            preferences = preferences.WithStatus(runtimeStatus);
            UpdateStartupTiming_NoLock(runtimeStatus, DateTimeOffset.UtcNow);
            preferenceStore.Save(preferences);
        }

        NotifyStateChanged();
    }

    private void ClearSessionUnlock_NoLock()
    {
        if (unlockedPassword is not null)
        {
            Array.Clear(unlockedPassword);
            unlockedPassword = null;
        }
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
               normalized.Equals("provider_paths_ready", StringComparison.Ordinal) ||
               normalized.Equals("provider_paths_wait_timeout", StringComparison.Ordinal) ||
               normalized.Equals("listener_ready", StringComparison.Ordinal) ||
               normalized.Equals("waiting_for_peer_dial", StringComparison.Ordinal) ||
               normalized.Equals("peer_connected", StringComparison.Ordinal) ||
               normalized.Equals("waiting_for_answer", StringComparison.Ordinal) ||
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
                waitingForApprovedSessionStartedUtc ??= now;
                listenerStartUtc = now;
                providerReadyUtc = null;
                listenerReadyUtc = null;
                peerConnectedUtc = null;
                completedUtc = null;
                break;
            case "provider_paths_ready":
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
                options.AcceptTimeoutSec.ToString(CultureInfo.InvariantCulture));
    }
}
