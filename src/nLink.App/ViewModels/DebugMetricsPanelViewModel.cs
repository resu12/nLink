using System;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using NLink.App.Services;
using NLink.Core.Metrics;

namespace NLink.App.ViewModels;

public sealed class DebugMetricsPanelViewModel : ViewModelBase, IDisposable
{
    private readonly SessionRuntime? sessionRuntime;
    private readonly MetricsRegistry? metricsRegistry;
    private readonly DispatcherTimer timer;
    private bool isVisible;
    private bool disposed;
    private string currentTransportState = "Idle";
    private string lastFailureCategory = "(none)";
    private string connectDuration = "(none)";
    private string handshakeDuration = "(none)";
    private string bridgeStartDuration = "(none)";
    private string attemptsText = "0";
    private string failuresText = "0";
    private string successRateText = "0%";
    private string connectP95Text = "(none)";
    private string handshakeP95Text = "(none)";

    public DebugMetricsPanelViewModel(SessionRuntime? sessionRuntime, MetricsRegistry? metricsRegistry)
    {
        this.sessionRuntime = sessionRuntime;
        this.metricsRegistry = metricsRegistry;

        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += OnTimerTick;
        timer.Start();

        Refresh();
    }

    public bool IsVisible
    {
        get => isVisible;
        private set => SetProperty(ref isVisible, value);
    }

    public string CurrentTransportState
    {
        get => currentTransportState;
        private set => SetProperty(ref currentTransportState, value);
    }

    public string LastFailureCategory
    {
        get => lastFailureCategory;
        private set => SetProperty(ref lastFailureCategory, value);
    }

    public string ConnectDuration
    {
        get => connectDuration;
        private set => SetProperty(ref connectDuration, value);
    }

    public string HandshakeDuration
    {
        get => handshakeDuration;
        private set => SetProperty(ref handshakeDuration, value);
    }

    public string BridgeStartDuration
    {
        get => bridgeStartDuration;
        private set => SetProperty(ref bridgeStartDuration, value);
    }

    public string AttemptsText
    {
        get => attemptsText;
        private set => SetProperty(ref attemptsText, value);
    }

    public string FailuresText
    {
        get => failuresText;
        private set => SetProperty(ref failuresText, value);
    }

    public string SuccessRateText
    {
        get => successRateText;
        private set => SetProperty(ref successRateText, value);
    }

    public string ConnectP95Text
    {
        get => connectP95Text;
        private set => SetProperty(ref connectP95Text, value);
    }

    public string HandshakeP95Text
    {
        get => handshakeP95Text;
        private set => SetProperty(ref handshakeP95Text, value);
    }

    public void ToggleVisible()
    {
        IsVisible = !IsVisible;
        if (IsVisible)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        var session = sessionRuntime?.GetDiagnosticsSnapshot();
        var metrics = metricsRegistry?.Snapshot();

        CurrentTransportState = string.IsNullOrWhiteSpace(session?.CurrentState) ? "Idle" : session.Value.CurrentState;
        LastFailureCategory = string.IsNullOrWhiteSpace(session?.LastFailureCategory) ? "(none)" : session.Value.LastFailureCategory!;
        ConnectDuration = FormatMs(session?.LastConnectDurationMs);
        HandshakeDuration = FormatMs(session?.LastHandshakeDurationMs);
        BridgeStartDuration = FormatMs(session?.LastBridgeStartDurationMs);

        if (metrics is null)
        {
            AttemptsText = "0";
            FailuresText = "0";
            SuccessRateText = "0%";
            ConnectP95Text = "(none)";
            HandshakeP95Text = "(none)";
            return;
        }

        var attempts = SumCounters(metrics, "transport_connect_attempts_total");
        var failures = SumCounters(metrics, "transport_connect_failure_total");
        var successes = SumCounters(metrics, "transport_connect_success_total");
        AttemptsText = attempts.ToString(CultureInfo.InvariantCulture);
        FailuresText = failures.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(AttemptsFailuresSummary));
        SuccessRateText = attempts > 0
            ? ((successes * 100d) / attempts).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "0%";

        ConnectP95Text = FormatMs(EstimateHistogramPercentile(metrics, "transport_connect_duration_ms", 0.95));
        HandshakeP95Text = FormatMs(EstimateHistogramPercentile(metrics, "transport_handshake_duration_ms", 0.95));
    }

    public string AttemptsFailuresSummary => $"{AttemptsText} / {FailuresText}";

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        Refresh();
    }

    private static string FormatMs(double? value)
    {
        if (!value.HasValue || value.Value < 0 || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "(none)";
        }

        return value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
    }

    private static long SumCounters(MetricsSnapshot snapshot, string name)
    {
        long total = 0;
        foreach (var counter in snapshot.Counters)
        {
            if (string.Equals(counter.Name, name, StringComparison.Ordinal))
            {
                total += counter.Value;
            }
        }

        return total;
    }

    private static double? EstimateHistogramPercentile(MetricsSnapshot snapshot, string name, double percentile)
    {
        var matching = snapshot.Histograms.Where(h => string.Equals(h.Name, name, StringComparison.Ordinal)).ToArray();
        if (matching.Length == 0)
        {
            return null;
        }

        long totalCount = 0;
        double globalMin = double.PositiveInfinity;
        double globalMax = double.NegativeInfinity;
        foreach (var histogram in matching)
        {
            totalCount += histogram.Count;
            if (histogram.Count > 0)
            {
                globalMin = Math.Min(globalMin, histogram.Min);
                globalMax = Math.Max(globalMax, histogram.Max);
            }
        }

        if (totalCount <= 0)
        {
            return null;
        }

        var target = (long)Math.Ceiling(totalCount * percentile);
        target = Math.Max(1, target);

        var bucketCountsByUpper = matching
            .SelectMany(h => h.Buckets)
            .GroupBy(b => b.UpperBound)
            .OrderBy(g => g.Key)
            .Select(g => new { Upper = g.Key, Count = g.Sum(x => x.Count) })
            .ToArray();

        long cumulative = 0;
        double lowerBound = double.IsFinite(globalMin) ? globalMin : 0d;
        foreach (var bucket in bucketCountsByUpper)
        {
            cumulative += bucket.Count;
            if (cumulative < target)
            {
                if (double.IsFinite(bucket.Upper))
                {
                    lowerBound = bucket.Upper;
                }

                continue;
            }

            if (double.IsInfinity(bucket.Upper))
            {
                return double.IsFinite(globalMax) ? globalMax : lowerBound;
            }

            return bucket.Upper;
        }

        return double.IsFinite(globalMax) ? globalMax : null;
    }
}
