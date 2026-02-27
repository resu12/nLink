using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Threading;
using NLink.Core.Logging;

namespace NLink.App.Services;

internal sealed class UiFreezeWatchdog : IDisposable
{
    private readonly HangReportService hangReportService;
    private readonly TimeSpan heartbeatInterval;
    private readonly TimeSpan freezeThreshold;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly CancellationTokenSource disposeCts = new();
    private readonly Task loopTask;
    private long lastHeartbeatUtcTicks;
    private int incidentOpen;
    private bool disposed;

    public UiFreezeWatchdog(
        HangReportService hangReportService,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? freezeThreshold = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.hangReportService = hangReportService ?? throw new ArgumentNullException(nameof(hangReportService));
        this.heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(1);
        this.freezeThreshold = freezeThreshold ?? TimeSpan.FromSeconds(8);
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        lastHeartbeatUtcTicks = this.nowProvider().UtcTicks;
        loopTask = Task.Run(RunAsync);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposeCts.Cancel();
        try
        {
            loopTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown.
        }
        finally
        {
            disposeCts.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(heartbeatInterval);
            while (await timer.WaitForNextTickAsync(disposeCts.Token).ConfigureAwait(false))
            {
                TryPingUiThread();
                EvaluateFreezeThreshold();
            }
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("HangWatchdog", $"event=watchdog_failed; ex={ex.GetType().Name}");
        }
    }

    private void TryPingUiThread()
    {
        try
        {
            _ = UiThreadDispatch.RunAsync(() =>
            {
                Interlocked.Exchange(ref lastHeartbeatUtcTicks, nowProvider().UtcTicks);
                if (Volatile.Read(ref incidentOpen) != 0)
                {
                    Interlocked.Exchange(ref incidentOpen, 0);
                }
            });
        }
        catch
        {
            // Best-effort ping only.
        }
    }

    private void EvaluateFreezeThreshold()
    {
        var last = new DateTimeOffset(Interlocked.Read(ref lastHeartbeatUtcTicks), TimeSpan.Zero);
        var age = nowProvider() - last;
        if (age <= freezeThreshold)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref incidentOpen, 1, 0) != 0)
        {
            return;
        }

        try
        {
            hangReportService.Capture(
                HangReportTriggerKind.UiWatchdog,
                $"ui_heartbeat_missed_for={age.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("HangWatchdog", $"event=hang_capture_failed; ex={ex.GetType().Name}");
        }
    }
}
