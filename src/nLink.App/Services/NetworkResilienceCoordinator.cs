using System;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Logging;

namespace NLink.App.Services;

[Flags]
internal enum ExternalRecoveryTrigger
{
    None = 0,
    NetworkAvailable = 1 << 0,
    NetworkAddressChanged = 1 << 1,
    Resume = 1 << 2,
}

internal sealed class NetworkAvailabilitySignalEventArgs : EventArgs
{
    public NetworkAvailabilitySignalEventArgs(bool isAvailable)
    {
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }
}

internal interface INetworkEventSource : IDisposable
{
    event EventHandler<NetworkAvailabilitySignalEventArgs>? NetworkAvailabilityChanged;
    event EventHandler? NetworkAddressChanged;
    event EventHandler? Resume;
}

internal sealed class SystemNetworkEventSource : INetworkEventSource
{
    private EventInfo? powerModeChangedEvent;
    private Delegate? powerModeChangedHandler;
    private bool disposed;

    public SystemNetworkEventSource()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        TrySubscribePowerModeChanged();
    }

    public event EventHandler<NetworkAvailabilitySignalEventArgs>? NetworkAvailabilityChanged;
    public event EventHandler? NetworkAddressChanged;
    public event EventHandler? Resume;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        TryUnsubscribePowerModeChanged();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        NetworkAvailabilityChanged?.Invoke(this, new NetworkAvailabilitySignalEventArgs(e.IsAvailable));
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        NetworkAddressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPowerModeChangedReflection(object? sender, object? e)
    {
        if (disposed)
        {
            return;
        }

        var modeText = e?.GetType().GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(e)?.ToString();
        if (!string.Equals(modeText, "Resume", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Resume?.Invoke(this, EventArgs.Empty);
    }

    private void TrySubscribePowerModeChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var systemEventsType = Type.GetType("Microsoft.Win32.SystemEvents, Microsoft.Win32.SystemEvents", throwOnError: false);
            powerModeChangedEvent = systemEventsType?.GetEvent("PowerModeChanged", BindingFlags.Public | BindingFlags.Static);
            if (powerModeChangedEvent?.EventHandlerType is null)
            {
                return;
            }

            var method = GetType().GetMethod(
                nameof(OnPowerModeChangedReflection),
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method is null)
            {
                return;
            }

            powerModeChangedHandler = Delegate.CreateDelegate(powerModeChangedEvent.EventHandlerType, this, method);
            powerModeChangedEvent.AddEventHandler(null, powerModeChangedHandler);
        }
        catch
        {
            powerModeChangedEvent = null;
            powerModeChangedHandler = null;
            // Best-effort. Some environments block SystemEvents registration.
        }
    }

    private void TryUnsubscribePowerModeChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (powerModeChangedEvent is not null && powerModeChangedHandler is not null)
            {
                powerModeChangedEvent.RemoveEventHandler(null, powerModeChangedHandler);
            }
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            powerModeChangedEvent = null;
            powerModeChangedHandler = null;
        }
    }
}

internal sealed class NetworkResilienceCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly INetworkEventSource networkEventSource;
    private readonly ITimer debounceTimer;
    private readonly Func<ExternalRecoveryTrigger, CancellationToken, Task> handleRecoveryAsync;
    private readonly TimeSpan debounceDelay;
    private readonly CancellationTokenSource disposeCts = new();
    private ExternalRecoveryTrigger pendingTriggers;
    private bool recoveryInFlight;
    private bool disposed;

    public NetworkResilienceCoordinator(
        INetworkEventSource networkEventSource,
        Func<ExternalRecoveryTrigger, CancellationToken, Task> handleRecoveryAsync,
        ITimer? debounceTimer = null,
        TimeSpan? debounceDelay = null)
    {
        this.networkEventSource = networkEventSource ?? throw new ArgumentNullException(nameof(networkEventSource));
        this.handleRecoveryAsync = handleRecoveryAsync ?? throw new ArgumentNullException(nameof(handleRecoveryAsync));
        this.debounceTimer = debounceTimer ?? new ThreadPoolTimerAdapter();
        this.debounceDelay = debounceDelay ?? TimeSpan.FromSeconds(2);

        this.networkEventSource.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        this.networkEventSource.NetworkAddressChanged += OnNetworkAddressChanged;
        this.networkEventSource.Resume += OnResume;
    }

    internal ExternalRecoveryTrigger PendingTriggersForTests
    {
        get
        {
            lock (gate)
            {
                return pendingTriggers;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        networkEventSource.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        networkEventSource.NetworkAddressChanged -= OnNetworkAddressChanged;
        networkEventSource.Resume -= OnResume;
        disposeCts.Cancel();
        debounceTimer.Stop();
        debounceTimer.Dispose();
        disposeCts.Dispose();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilitySignalEventArgs e)
    {
        if (!e.IsAvailable)
        {
            return;
        }

        Enqueue(ExternalRecoveryTrigger.NetworkAvailable);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Enqueue(ExternalRecoveryTrigger.NetworkAddressChanged);
    }

    private void OnResume(object? sender, EventArgs e)
    {
        Enqueue(ExternalRecoveryTrigger.Resume);
    }

    private void Enqueue(ExternalRecoveryTrigger trigger)
    {
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            pendingTriggers |= trigger;
        }

        debounceTimer.Start(debounceDelay, Timeout.InfiniteTimeSpan, OnDebounceElapsed);
    }

    private void OnDebounceElapsed()
    {
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        ExternalRecoveryTrigger toHandle = ExternalRecoveryTrigger.None;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (recoveryInFlight)
            {
                return;
            }

            toHandle = pendingTriggers;
            pendingTriggers = ExternalRecoveryTrigger.None;
            if (toHandle == ExternalRecoveryTrigger.None)
            {
                return;
            }

            recoveryInFlight = true;
        }

        try
        {
            LocalOperationalLog.Info("Network", $"event=external_recovery_dispatch; triggers={toHandle}");
            await handleRecoveryAsync(toHandle, disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("Network", $"event=external_recovery_dispatch_failed; triggers={toHandle}; ex={ex.GetType().Name}");
        }
        finally
        {
            var shouldReschedule = false;
            lock (gate)
            {
                recoveryInFlight = false;
                shouldReschedule = pendingTriggers != ExternalRecoveryTrigger.None && !disposed;
            }

            if (shouldReschedule)
            {
                debounceTimer.Start(debounceDelay, Timeout.InfiniteTimeSpan, OnDebounceElapsed);
            }
        }
    }
}
