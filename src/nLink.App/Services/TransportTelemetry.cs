using System;
using System.Linq;
using System.Threading;
using NLink.Core.Metrics;

namespace NLink.App.Services;

public interface ITransportTelemetrySink
{
    void OnStateChanged(TransportStateChangedTelemetryEvent evt);
    void OnTimingCompleted(TransportTimingCompletedTelemetryEvent evt);
    void OnFailure(TransportFailureTelemetryEvent evt);
    void OnBridgeLifecycle(BridgeLifecycleTelemetryEvent evt);
}

public static class TransportTelemetry
{
    public static ITransportTelemetrySink Noop { get; } = new NoopTransportTelemetrySink();

    private sealed class NoopTransportTelemetrySink : ITransportTelemetrySink
    {
        public void OnFailure(TransportFailureTelemetryEvent evt) { }
        public void OnStateChanged(TransportStateChangedTelemetryEvent evt) { }
        public void OnTimingCompleted(TransportTimingCompletedTelemetryEvent evt) { }
        public void OnBridgeLifecycle(BridgeLifecycleTelemetryEvent evt) { }
    }
}

public readonly record struct TransportStateChangedTelemetryEvent(
    TransportState From,
    TransportState To,
    string Reason,
    string RunId,
    string Scenario,
    string BridgeReuseMode,
    long Attempt,
    string Transport,
    string SessionId);

public readonly record struct TransportTimingCompletedTelemetryEvent(
    string EventName,
    string MetricName,
    double DurationMs,
    bool Failed,
    string Reason,
    string RunId,
    string Scenario,
    string BridgeReuseMode,
    long Attempt,
    string Transport,
    string SessionId);

public readonly record struct TransportFailureTelemetryEvent(
    TransportFailureCategory Category,
    bool IsTransient,
    string Message,
    string ExceptionType,
    string RunId,
    string Scenario,
    string BridgeReuseMode,
    long Attempt,
    string Transport,
    string State,
    double? DurationMs,
    string SessionId);

public readonly record struct BridgeLifecycleTelemetryEvent(
    string EventName,
    string StartMode,
    int? Pid,
    double? ReadyTimeMs,
    double? PingRttMs,
    double? UptimeMs,
    int? ExitCode,
    string ExitReason,
    string RunId,
    string Scenario,
    string BridgeReuseMode,
    long Attempt,
    string Transport,
    string SessionId);

public sealed class MetricsTelemetrySink : ITransportTelemetrySink
{
    private readonly MetricsRegistry metrics;
    private int firstColdStartRecorded;

    public MetricsTelemetrySink(MetricsRegistry metrics)
    {
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public void OnStateChanged(TransportStateChangedTelemetryEvent evt)
    {
        if (evt.To == TransportState.TransportInitializing)
        {
            metrics.Counter(
                "transport_connect_attempts_total",
                transport: evt.Transport,
                scenario: evt.Scenario,
                bridgeReuseMode: evt.BridgeReuseMode).Inc();
        }

        if (evt.To == TransportState.Reconnecting)
        {
            metrics.Counter(
                "transport_reconnect_attempts_total",
                transport: evt.Transport,
                scenario: evt.Scenario,
                bridgeReuseMode: evt.BridgeReuseMode).Inc();
            metrics.Counter(
                "bridge_restart_total",
                transport: evt.Transport,
                scenario: evt.Scenario,
                bridgeReuseMode: evt.BridgeReuseMode).Inc();
        }
    }

    public void OnTimingCompleted(TransportTimingCompletedTelemetryEvent evt)
    {
        switch (evt.EventName)
        {
            case "bridge_start_completed":
                metrics.Counter(
                    "bridge_start_total",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    result: evt.Failed ? "failed" : "success",
                    bridgeReuseMode: evt.BridgeReuseMode).Inc();
                metrics.Histogram(
                    "bridge_start_duration_ms",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    result: evt.Failed ? "failed" : "success",
                    bridgeReuseMode: evt.BridgeReuseMode).Observe(evt.DurationMs);
                break;

            case "connect_completed":
                metrics.Counter(
                    evt.Failed ? "transport_connect_failure_total" : "transport_connect_success_total",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    bridgeReuseMode: evt.BridgeReuseMode).Inc();
                metrics.Histogram(
                    "transport_connect_duration_ms",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    result: evt.Failed ? "failed" : "success",
                    bridgeReuseMode: evt.BridgeReuseMode).Observe(evt.DurationMs);
                break;

            case "handshake_completed":
                metrics.Histogram(
                    "transport_handshake_duration_ms",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    result: evt.Failed ? "failed" : "success",
                    bridgeReuseMode: evt.BridgeReuseMode).Observe(evt.DurationMs);
                break;
        }
    }

    public void OnFailure(TransportFailureTelemetryEvent evt)
    {
        metrics.Counter(
            "transport_failure_total",
            transport: evt.Transport,
            scenario: evt.Scenario,
            failureCategory: evt.Category.ToString(),
            bridgeReuseMode: evt.BridgeReuseMode).Inc();

        if (evt.Category is TransportFailureCategory.BridgeCrashed or TransportFailureCategory.UnexpectedProcessExit)
        {
            metrics.Counter(
                "bridge_crash_total",
                transport: evt.Transport,
                scenario: evt.Scenario,
                failureCategory: evt.Category.ToString(),
                bridgeReuseMode: evt.BridgeReuseMode).Inc();
        }

        if (evt.Category is TransportFailureCategory.BridgeUnresponsive)
        {
            metrics.Counter(
                "bridge_unresponsive_total",
                transport: evt.Transport,
                scenario: evt.Scenario,
                failureCategory: evt.Category.ToString(),
                bridgeReuseMode: evt.BridgeReuseMode).Inc();
        }

        if (evt.Category is TransportFailureCategory.HandshakeTimeout
            or TransportFailureCategory.BridgeStartFailure
            or TransportFailureCategory.BridgeUnresponsive)
        {
            metrics.Counter(
                "state_stuck_count",
                transport: evt.Transport,
                scenario: evt.Scenario,
                failureCategory: evt.Category.ToString(),
                bridgeReuseMode: evt.BridgeReuseMode).Inc();
        }
    }

    public void OnBridgeLifecycle(BridgeLifecycleTelemetryEvent evt)
    {
        switch (evt.EventName)
        {
            case "bridge_spawned":
                metrics.Counter(
                    "bridge_spawn_total",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    result: string.IsNullOrWhiteSpace(evt.StartMode) ? "unknown" : evt.StartMode,
                    bridgeReuseMode: evt.BridgeReuseMode).Inc();
                metrics.Gauge(
                    "bridge_process_running",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    bridgeReuseMode: evt.BridgeReuseMode).Set(1);
                if (evt.Pid.HasValue)
                {
                    metrics.Gauge(
                        "bridge_pid",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        bridgeReuseMode: evt.BridgeReuseMode).Set(evt.Pid.Value);
                }
                break;

            case "bridge_ready":
                if (evt.ReadyTimeMs.HasValue)
                {
                    metrics.Histogram(
                        "bridge_ready_time_ms",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        result: string.IsNullOrWhiteSpace(evt.StartMode) ? "unknown" : evt.StartMode,
                        bridgeReuseMode: evt.BridgeReuseMode)
                        .Observe(evt.ReadyTimeMs.Value);

                    if (string.Equals(evt.StartMode, "cold", StringComparison.OrdinalIgnoreCase) &&
                        Interlocked.CompareExchange(ref firstColdStartRecorded, 1, 0) == 0)
                    {
                        metrics.Gauge(
                            "bridge_cold_start_ms",
                            transport: evt.Transport,
                            scenario: evt.Scenario,
                            bridgeReuseMode: evt.BridgeReuseMode)
                            .Set(evt.ReadyTimeMs.Value);
                    }
                }
                if (evt.PingRttMs.HasValue)
                {
                    metrics.Histogram(
                        "bridge_ping_rtt_ms",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        result: string.IsNullOrWhiteSpace(evt.StartMode) ? "unknown" : evt.StartMode,
                        bridgeReuseMode: evt.BridgeReuseMode)
                        .Observe(evt.PingRttMs.Value);
                }
                break;

            case "bridge_exited":
                metrics.Counter(
                    "bridge_exit_total",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    result: string.IsNullOrWhiteSpace(evt.ExitReason) ? "unknown" : evt.ExitReason,
                    bridgeReuseMode: evt.BridgeReuseMode).Inc();
                if (string.Equals(evt.ExitReason, "crash", StringComparison.OrdinalIgnoreCase))
                {
                    metrics.Counter(
                        "bridge_crash_total",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        failureCategory: "BridgeCrashed").Inc();
                }
                else if (string.Equals(evt.ExitReason, "killed", StringComparison.OrdinalIgnoreCase))
                {
                    metrics.Counter(
                        "bridge_killed_total",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        failureCategory: "BridgeKilled",
                        bridgeReuseMode: evt.BridgeReuseMode).Inc();
                }
                metrics.Gauge(
                    "bridge_process_running",
                    transport: evt.Transport,
                    scenario: evt.Scenario,
                    bridgeReuseMode: evt.BridgeReuseMode).Set(0);
                if (evt.ExitCode.HasValue)
                {
                    metrics.Gauge(
                        "bridge_exit_code",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        result: string.IsNullOrWhiteSpace(evt.ExitReason) ? string.Empty : evt.ExitReason,
                        bridgeReuseMode: evt.BridgeReuseMode)
                        .Set(evt.ExitCode.Value);
                }

                if (evt.UptimeMs.HasValue)
                {
                    metrics.Histogram(
                        "bridge_uptime_ms",
                        transport: evt.Transport,
                        scenario: evt.Scenario,
                        result: string.IsNullOrWhiteSpace(evt.ExitReason) ? "unknown" : evt.ExitReason,
                        bridgeReuseMode: evt.BridgeReuseMode)
                        .Observe(evt.UptimeMs.Value);
                }
                break;
        }

        UpdateWarmStartRatioGauge(evt);
    }

    private void UpdateWarmStartRatioGauge(BridgeLifecycleTelemetryEvent evt)
    {
        if (!string.Equals(evt.EventName, "bridge_spawned", StringComparison.Ordinal))
        {
            return;
        }

        var mode = string.IsNullOrWhiteSpace(evt.StartMode) ? "unknown" : evt.StartMode;
        if (!string.Equals(mode, "cold", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "warm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var snapshot = metrics.Snapshot();
        var spawns = snapshot.Counters.Where(c =>
            string.Equals(c.Name, "bridge_spawn_total", StringComparison.Ordinal) &&
            string.Equals(c.Tags.Transport, evt.Transport, StringComparison.Ordinal) &&
            string.Equals(c.Tags.Scenario, evt.Scenario, StringComparison.Ordinal) &&
            string.Equals(c.Tags.BridgeReuseMode, evt.BridgeReuseMode, StringComparison.Ordinal)).ToArray();

        var total = spawns.Sum(c => c.Value);
        if (total <= 0)
        {
            return;
        }

        var warm = spawns.Where(c => string.Equals(c.Tags.Result, "warm", StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value);
        metrics.Gauge(
            "bridge_warm_start_ratio",
            transport: evt.Transport,
            scenario: evt.Scenario,
            bridgeReuseMode: evt.BridgeReuseMode).Set((double)warm / total);
    }
}
