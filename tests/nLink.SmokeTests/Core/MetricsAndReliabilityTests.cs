using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class MetricsAndReliabilityTests : CoreSmokeTestsBase
{
[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task RetryPolicy_RetriesWithBoundedBackoff_AndTracksAttemptCounts()
    {
        var observedDelays = new List<TimeSpan>();
        var events = new List<RetryEvent>();
        var resets = 0;
        var operationCalls = 0;

        var policy = new RetryPolicy(
            new RetryPolicyOptions(
                MaxAttempts: 4,
                InitialDelay: TimeSpan.FromMilliseconds(100),
                MaxDelay: TimeSpan.FromMilliseconds(500),
                JitterRatio: 0d),
            delayAsync: (delay, _) =>
            {
                observedDelays.Add(delay);
                return Task.CompletedTask;
            });
        policy.EventEmitted += (_, e) => events.Add(e);

        var result = await policy.ExecuteAsync(
            operationAsync: (_, _) =>
            {
                operationCalls++;
                if (operationCalls < 3)
                {
                    throw new InvalidOperationException("retry_me");
                }

                return Task.CompletedTask;
            },
            resetBetweenAttemptsAsync: (_, _) =>
            {
                resets++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Null(result.LastException);
        Assert.Equal(3, operationCalls);
        Assert.Equal(2, resets);
        Assert.Equal(new[] { 100d, 200d }, observedDelays.Select(d => d.TotalMilliseconds).ToArray());

        Assert.Equal(3, events.Count(e => e.Kind == RetryEventKind.AttemptStart));
        Assert.Equal(2, events.Count(e => e.Kind == RetryEventKind.AttemptScheduled));
        Assert.Equal(1, events.Count(e => e.Kind == RetryEventKind.AttemptSuccess));
        Assert.DoesNotContain(events, e => e.Kind == RetryEventKind.FinalFail);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task RetryPolicy_DelayBounds_WithJitter_StayWithinConfiguredRange()
    {
        static RetryPolicy Create(double random, List<TimeSpan> delays)
            => new(
                new RetryPolicyOptions(
                    MaxAttempts: 3,
                    InitialDelay: TimeSpan.FromMilliseconds(1000),
                    MaxDelay: TimeSpan.FromMilliseconds(2000),
                    JitterRatio: 0.20),
                delayAsync: (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                },
                nextRandom: () => random);

        async Task<List<TimeSpan>> RunAndCaptureAsync(double random)
        {
            var delays = new List<TimeSpan>();
            var policy = Create(random, delays);
            await policy.ExecuteAsync(
                operationAsync: (_, _) => throw new InvalidOperationException("always_fail"),
                resetBetweenAttemptsAsync: null,
                CancellationToken.None);
            return delays;
        }

        var minJitterDelays = await RunAndCaptureAsync(0d);
        var maxJitterDelays = await RunAndCaptureAsync(1d);

        Assert.Equal(2, minJitterDelays.Count);
        Assert.Equal(2, maxJitterDelays.Count);

        // Attempt 1 base=1000ms, jitter ±20%
        Assert.InRange(minJitterDelays[0].TotalMilliseconds, 800d, 2000d);
        Assert.InRange(maxJitterDelays[0].TotalMilliseconds, 800d, 2000d);
        Assert.InRange(minJitterDelays[0].TotalMilliseconds, 800d, 1000d);
        Assert.InRange(maxJitterDelays[0].TotalMilliseconds, 1000d, 1200d);

        // Attempt 2 base=2000ms (clamped), jitter ±20% but clamped to max 2000ms
        Assert.InRange(minJitterDelays[1].TotalMilliseconds, 1600d, 2000d);
        Assert.InRange(maxJitterDelays[1].TotalMilliseconds, 1600d, 2000d);
        Assert.True(minJitterDelays[1].TotalMilliseconds <= 2000d);
        Assert.True(maxJitterDelays[1].TotalMilliseconds <= 2000d);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ShareMessageBuilder_WithCode_NoUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage("123456", null);
        Assert.Equal("Install nLink and enter code 123456", text);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ShareMessageBuilder_WithCode_AndUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage("123456", "https://example.com/nlink");
        Assert.Equal(
            "Install nLink and enter code 123456" + Environment.NewLine + "https://example.com/nlink",
            text);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ShareMessageBuilder_WithoutCode_WithUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage(null, "https://example.com/nlink");
        Assert.Equal(
            "Install nLink" + Environment.NewLine + "https://example.com/nlink",
            text);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ShareMessageBuilder_HelperInstallMessage_IncludesConfiguredUrl_AndTrailingNewline()
    {
        var text = ShareMessageBuilder.BuildHelperInstallMessage("https://example.com/releases");

        Assert.Equal(
            "Install nLink and open it." + Environment.NewLine +
            "Download: https://example.com/releases" + Environment.NewLine,
            text);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ShareMessageBuilder_HelperInstallMessage_DoesNotIncludeInternalDiagnosticsText()
    {
        var text = ShareMessageBuilder.BuildHelperInstallMessage("https://example.com/releases");

        Assert.DoesNotContain("Bridge PID", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NKN", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last_error", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identifier", text, StringComparison.OrdinalIgnoreCase);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task MetricsRegistry_IsThreadSafe_AndSnapshotIsConsistent()
    {
        var registry = new MetricsRegistry(new[] { 10d, 50d, 100d });
        var counter = registry.Counter("connect_attempts", transport: "NKN", scenario: "A", result: "success");
        var gauge = registry.Gauge("bridge_pid", transport: "NKN");
        var histogram = registry.Histogram("connect_duration_ms", transport: "NKN", scenario: "A");

        const int workers = 8;
        const int iterations = 1_000;
        var tasks = new List<Task>(workers);

        for (var w = 0; w < workers; w++)
        {
            var workerIndex = w;
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    counter.Inc();
                    histogram.Observe((workerIndex * iterations + i) % 120);
                }

                gauge.Set(workerIndex);
            }));
        }

        await Task.WhenAll(tasks);

        var snapshot = registry.Snapshot();

        var counterSnap = Assert.Single(snapshot.Counters.Where(c => c.Name == "connect_attempts"));
        Assert.Equal(workers * iterations, counterSnap.Value);
        Assert.Equal("NKN", counterSnap.Tags.Transport);
        Assert.Equal("A", counterSnap.Tags.Scenario);
        Assert.Equal("success", counterSnap.Tags.Result);

        var gaugeSnap = Assert.Single(snapshot.Gauges.Where(g => g.Name == "bridge_pid"));
        Assert.True(gaugeSnap.Value >= 0);
        Assert.True(gaugeSnap.Value <= workers - 1);

        var histogramSnap = Assert.Single(snapshot.Histograms.Where(h => h.Name == "connect_duration_ms"));
        Assert.Equal(workers * iterations, histogramSnap.Count);
        Assert.True(histogramSnap.Sum >= 0);
        Assert.True(histogramSnap.Min >= 0);
        Assert.True(histogramSnap.Max <= 119);
        Assert.Equal(histogramSnap.Count, histogramSnap.Buckets.Sum(b => b.Count));
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void MetricsRegistry_JsonExport_MatchesSnapshot_AndIncludesLabels()
    {
        var registry = new MetricsRegistry(new[] { 5d, 10d });
        registry.Counter("transport_failed", transport: "NKN", result: "failed", failureCategory: "HandshakeTimeout").Inc(2);
        registry.Gauge("active_sessions", transport: "DEVLOCAL").Set(1);
        registry.Histogram("handshake_duration_ms", transport: "NKN", scenario: "B").Observe(7);
        registry.Histogram("handshake_duration_ms", transport: "NKN", scenario: "B").Observe(12);

        var snapshot = registry.Snapshot();
        var json = registry.ExportJson(indented: false);

        Assert.Contains("\"Counters\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Histograms\"", json, StringComparison.Ordinal);
        Assert.Contains("\"transport_failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"HandshakeTimeout\"", json, StringComparison.Ordinal);
        Assert.Contains("\"NKN\"", json, StringComparison.Ordinal);
        Assert.Contains("\"B\"", json, StringComparison.Ordinal);

        var counterSnap = Assert.Single(snapshot.Counters.Where(c => c.Name == "transport_failed"));
        Assert.Equal(2, counterSnap.Value);

        var histSnap = Assert.Single(snapshot.Histograms.Where(h => h.Name == "handshake_duration_ms"));
        Assert.Equal(2, histSnap.Count);
        Assert.Equal(19, histSnap.Sum, precision: 6);
        Assert.Equal(7, histSnap.Min, precision: 6);
        Assert.Equal(12, histSnap.Max, precision: 6);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void MetricsRegistry_JsonExport_MatchesGoldenSchema()
    {
        var registry = new MetricsRegistry(new[] { 5d, 10d });
        registry.Counter("transport_connect_attempts_total", transport: "NKN", scenario: "A").Inc(2);
        registry.Counter("transport_connect_success_total", transport: "NKN", scenario: "A", result: "success").Inc(1);
        registry.Gauge("bridge_pid", transport: "NKN").Set(1234);
        registry.Histogram("transport_connect_duration_ms", new[] { 5d, 10d }, transport: "NKN", scenario: "A", result: "success").Observe(4);
        registry.Histogram("transport_connect_duration_ms", new[] { 5d, 10d }, transport: "NKN", scenario: "A", result: "success").Observe(9);
        registry.Histogram("transport_connect_duration_ms", new[] { 5d, 10d }, transport: "NKN", scenario: "A", result: "success").Observe(25);

        var actual = registry.ExportJson(indented: true).Replace("\r\n", "\n");
        var goldenPath = FindFileUpwards(Path.Combine("tests", "nLink.SmokeTests", "GoldenFiles", "metrics-snapshot-schema.golden.json"));
        Assert.True(goldenPath is not null, "Golden file not found for metrics snapshot schema.");
        var expected = File.ReadAllText(goldenPath!).Replace("\r\n", "\n");

        Assert.Equal(NormalizeJson(expected), NormalizeJson(actual));
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void MetricsTelemetrySink_SuccessfulConnect_RecordsAttemptsSuccess_AndDurations()
    {
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);

        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(),
            SessionRuntimeWatchdogOptions.Default with { Enabled = false },
            telemetrySink: sink);

        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "start_helper"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "nkn_bridge_starting"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeReady, "nkn_bridge_ready_assumed"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "join_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "join_request_sent"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connected, "transport_approved"));

        var snapshot = registry.Snapshot();

        var connectAttempts = Assert.Single(snapshot.Counters.Where(c => c.Name == "transport_connect_attempts_total"));
        Assert.Equal(1, connectAttempts.Value);

        var connectSuccess = Assert.Single(snapshot.Counters.Where(c => c.Name == "transport_connect_success_total"));
        Assert.Equal(1, connectSuccess.Value);

        Assert.Empty(snapshot.Counters.Where(c => c.Name == "transport_connect_failure_total"));

        var bridgeStarts = Assert.Single(snapshot.Counters.Where(c => c.Name == "bridge_start_total"));
        Assert.Equal(1, bridgeStarts.Value);

        var connectDuration = Assert.Single(snapshot.Histograms.Where(h => h.Name == "transport_connect_duration_ms"));
        Assert.Equal(1, connectDuration.Count);
        Assert.True(connectDuration.Sum >= 0);

        var handshakeDuration = Assert.Single(snapshot.Histograms.Where(h => h.Name == "transport_handshake_duration_ms"));
        Assert.Equal(1, handshakeDuration.Count);
        Assert.True(handshakeDuration.Sum >= 0);

        var bridgeDuration = Assert.Single(snapshot.Histograms.Where(h => h.Name == "bridge_start_duration_ms"));
        Assert.Equal(1, bridgeDuration.Count);
        Assert.True(bridgeDuration.Sum >= 0);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void MetricsTelemetrySink_ScenarioLabel_IsAbsent_WhenNotSet()
    {
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);

        sink.OnStateChanged(new TransportStateChangedTelemetryEvent(
            From: TransportState.Idle,
            To: TransportState.TransportInitializing,
            Reason: "start_helper",
            RunId: "run123",
            Scenario: "",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            SessionId: "sess123"));

        var snapshot = registry.Snapshot();
        var counter = Assert.Single(snapshot.Counters.Where(c => c.Name == "transport_connect_attempts_total"));
        Assert.Equal(string.Empty, counter.Tags.Scenario);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void MetricsTelemetrySink_ScenarioLabel_IsPresent_WhenSet()
    {
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);

        sink.OnStateChanged(new TransportStateChangedTelemetryEvent(
            From: TransportState.Idle,
            To: TransportState.TransportInitializing,
            Reason: "start_helper",
            RunId: "run123",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            SessionId: "sess123"));

        sink.OnTimingCompleted(new TransportTimingCompletedTelemetryEvent(
            EventName: "connect_completed",
            MetricName: "connect_duration_ms",
            DurationMs: 12.5,
            Failed: false,
            Reason: "transport_approved",
            RunId: "run123",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            SessionId: "sess123"));

        var snapshot = registry.Snapshot();
        Assert.Contains(snapshot.Counters, c => c.Name == "transport_connect_attempts_total" && c.Tags.Scenario == "A");
        Assert.Contains(snapshot.Counters, c => c.Name == "transport_connect_success_total" && c.Tags.Scenario == "A");
        Assert.Contains(snapshot.Histograms, h => h.Name == "transport_connect_duration_ms" && h.Tags.Scenario == "A");
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void MetricsTelemetrySink_BridgeLifecycle_MapsToMetrics()
    {
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);

        sink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            EventName: "bridge_spawned",
            StartMode: "cold",
            Pid: 4242,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: null,
            ExitCode: null,
            ExitReason: string.Empty,
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            SessionId: "sess1"));

        sink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            EventName: "bridge_ready",
            StartMode: "cold",
            Pid: 4242,
            ReadyTimeMs: 123,
            PingRttMs: 9,
            UptimeMs: 123,
            ExitCode: null,
            ExitReason: string.Empty,
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            SessionId: "sess1"));

        sink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            EventName: "bridge_exited",
            StartMode: string.Empty,
            Pid: 4242,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: 2500,
            ExitCode: 1,
            ExitReason: "crash",
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            SessionId: "sess1"));

        sink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            EventName: "bridge_spawned",
            StartMode: "warm",
            Pid: 4243,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: null,
            ExitCode: null,
            ExitReason: string.Empty,
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "KeepAlive",
            Attempt: 2,
            Transport: "NKN",
            SessionId: "sess2"));

        sink.OnBridgeLifecycle(new BridgeLifecycleTelemetryEvent(
            EventName: "bridge_ready",
            StartMode: "cold",
            Pid: 4244,
            ReadyTimeMs: 999,
            PingRttMs: 10,
            UptimeMs: 999,
            ExitCode: null,
            ExitReason: string.Empty,
            RunId: "run1",
            Scenario: "B",
            BridgeReuseMode: "PerSession",
            Attempt: 3,
            Transport: "NKN",
            SessionId: "sess3"));

        var snapshot = registry.Snapshot();

        Assert.Contains(snapshot.Counters, c => c.Name == "bridge_spawn_total" && c.Tags.Result == "cold" && c.Value == 1);
        Assert.Contains(snapshot.Counters, c => c.Name == "bridge_exit_total" && c.Tags.Result == "crash" && c.Value == 1);
        Assert.Contains(snapshot.Counters, c => c.Name == "bridge_crash_total" && c.Value == 1);

        Assert.Contains(snapshot.Gauges, g => g.Name == "bridge_pid" && Math.Abs(g.Value - 4242) < 0.001);
        Assert.Contains(snapshot.Gauges, g => g.Name == "bridge_process_running" && Math.Abs(g.Value) < 0.001);
        Assert.Contains(snapshot.Gauges, g => g.Name == "bridge_exit_code" && Math.Abs(g.Value - 1) < 0.001);
        Assert.Contains(snapshot.Gauges, g => g.Name == "bridge_cold_start_ms" && Math.Abs(g.Value - 123) < 0.001);
        Assert.DoesNotContain(snapshot.Gauges, g => g.Name == "bridge_cold_start_ms" && Math.Abs(g.Value - 999) < 0.001);

        Assert.Equal(2, snapshot.Histograms.Where(h => h.Name == "bridge_ready_time_ms").Sum(h => h.Count));
        Assert.Equal(2, snapshot.Histograms.Where(h => h.Name == "bridge_ping_rtt_ms").Sum(h => h.Count));
        Assert.Contains(snapshot.Histograms, h => h.Name == "bridge_uptime_ms" && h.Count == 1);
        Assert.Contains(snapshot.Gauges, g => g.Name == "bridge_warm_start_ratio" && g.Tags.BridgeReuseMode == "KeepAlive" && g.Value > 0d);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReliabilityGate_Passes_WhenThresholdsAreMet()
    {
        var registry = new MetricsRegistry();
        registry.Counter("transport_connect_attempts_total", transport: "DEVLOCAL", bridgeReuseMode: "PerSession").Inc();
        registry.Counter("transport_connect_success_total", transport: "DEVLOCAL", bridgeReuseMode: "PerSession").Inc();
        registry.Histogram("transport_connect_duration_ms", transport: "DEVLOCAL", result: "success", bridgeReuseMode: "PerSession").Observe(12);
        registry.Histogram("transport_handshake_duration_ms", transport: "DEVLOCAL", result: "success", bridgeReuseMode: "PerSession").Observe(3);

        var result = ReliabilityGate.Evaluate(
            new ReliabilityGateInput(
                registry.Snapshot(),
                SuccessRatePercent: 100,
                Transport: "DEVLOCAL",
                BridgeReuseMode: "PerSession"),
            new ReliabilityGateThresholds(
                MinSuccessRatePercent: 99,
                RequireNoUnknownFailures: true,
                RequireNoStuckStates: true,
                FailOnBridgeCrash: false));

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.UnknownFailures);
        Assert.Equal(0, result.StateStuckCount);
        Assert.Equal(0, result.BridgeCrashTotal);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReliabilityGate_Fails_WithClearReasons()
    {
        var registry = new MetricsRegistry();
        registry.Counter("transport_failure_total", transport: "NKN", failureCategory: "Unknown", bridgeReuseMode: "PerSession").Inc();
        registry.Counter("state_stuck_count", transport: "NKN", failureCategory: "HandshakeTimeout", bridgeReuseMode: "PerSession").Inc(2);
        registry.Counter("bridge_crash_total", transport: "NKN", bridgeReuseMode: "PerSession").Inc();

        var result = ReliabilityGate.Evaluate(
            new ReliabilityGateInput(
                registry.Snapshot(),
                SuccessRatePercent: 80,
                Transport: "NKN",
                BridgeReuseMode: "PerSession"),
            new ReliabilityGateThresholds(
                MinSuccessRatePercent: 95,
                RequireNoUnknownFailures: true,
                RequireNoStuckStates: true,
                FailOnBridgeCrash: true));

        Assert.False(result.Passed);
        Assert.Equal(1, result.UnknownFailures);
        Assert.Equal(2, result.StateStuckCount);
        Assert.Equal(1, result.BridgeCrashTotal);
        Assert.Contains(result.Failures, f => f.Code == "success_rate_below_target");
        Assert.Contains(result.Failures, f => f.Code == "unknown_failures_present");
        Assert.Contains(result.Failures, f => f.Code == "state_stuck_detected");
        Assert.Contains(result.Failures, f => f.Code == "bridge_crash_detected");
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportFailureMapper_EmptySignals_MapsToUserCancelled_NotUnknown()
    {
        var failure = TransportFailureMapper.FromSignals(
            rawError: null,
            exceptionType: null,
            lastDisconnectReason: null,
            fallbackMessage: "Connection lost.");

        Assert.Equal(TransportFailureCategory.UserCancelled, failure.Category);
        Assert.True(failure.IsTransient);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_ConnectSuccessFlow_TransitionsToConnected()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);

        Assert.Equal(UserStatusKind.Idle, presenter.CurrentStatus.Kind);

        source.SetAttempt(1);
        source.SetTransportState(TransportState.Connecting);
        source.RaiseTransient(isVisible: true, text: "Connecting… (attempt 1)", canCancel: true);

        Assert.Equal(UserStatusKind.Connecting, presenter.CurrentStatus.Kind);
        Assert.Equal(1, presenter.CurrentStatus.Attempt);
        Assert.True(presenter.CurrentStatus.CanCancel);

        source.SetSessionUiState(SessionRuntimeState.Connected);
        source.SetTransportState(TransportState.Connected);
        source.RaiseTransient(isVisible: false, text: string.Empty, canCancel: false);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Connected, presenter.CurrentStatus.Kind);
        Assert.Equal("Connected", presenter.CurrentStatus.Title);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_RetryFlow_ShowsAttemptAndCountdown()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);

        source.SetAttempt(3);
        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 3, next retry in 2s)", canCancel: true);

        Assert.Equal(UserStatusKind.Reconnecting, presenter.CurrentStatus.Kind);
        Assert.Equal(3, presenter.CurrentStatus.Attempt);
        Assert.Equal(2, presenter.CurrentStatus.NextRetryInSeconds);
        Assert.True(presenter.CurrentStatus.CanCancel);
        Assert.Equal(FailureSeverity.Warning, presenter.CurrentStatus.Severity);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_HandshakeAndApprovalStatuses_UseExplicitTitles()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);

        source.SetTransportState(TransportState.Handshake);
        source.SetSessionUiState(SessionRuntimeState.Connecting);
        source.SetStatusText(string.Empty);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Handshake, presenter.CurrentStatus.Kind);
        Assert.Equal("Finalizing connection", presenter.CurrentStatus.Title);
        Assert.Equal("Finalizing connection…", presenter.CurrentStatus.Message);

        source.SetSessionUiState(SessionRuntimeState.IncomingJoinRequest);
        source.SetStatusText(string.Empty);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Handshake, presenter.CurrentStatus.Kind);
        Assert.Equal("Waiting for approval", presenter.CurrentStatus.Title);
        Assert.Equal("Waiting for approval…", presenter.CurrentStatus.Message);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusBanner_HandshakeDefaultTitle_IsFinalizingConnection()
    {
        var banner = new StatusBanner
        {
            Status = new UserFacingStatus(
                UserStatusKind.Handshake,
                Title: string.Empty,
                Message: "Finalizing connection…",
                Severity: FailureSeverity.Info)
        };

        Assert.Equal("Finalizing connection", banner.StatusTitle);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_FailureMapping_UsesFailureCopyMap()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);

        source.SetAttempt(2);
        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.HandshakeTimeout,
            "timeout",
            exceptionType: nameof(TimeoutException),
            rawError: "handshake timeout",
            isTransient: true,
            correlationId: "abc123"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();

        var expected = FailureCopyMap.For(TransportFailureCategory.HandshakeTimeout);
        Assert.Equal(UserStatusKind.Failed, presenter.CurrentStatus.Kind);
        Assert.Equal(expected.Title, presenter.CurrentStatus.Title);
        Assert.Equal(expected.Message, presenter.CurrentStatus.Message);
        Assert.True(presenter.CurrentStatus.CanCopyDiagnostics);
        Assert.Equal("abc123", presenter.CurrentStatus.CorrelationId);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_DuplicateFailureWithinWindow_DoesNotReemit()
    {
        var source = new FakeStatusPresenterSource();
        var now = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero);
        using var presenter = new StatusPresenter(source, countdownTimer: null, nowProvider: () => now, failureDedupeWindow: TimeSpan.FromSeconds(10));
        var emitted = 0;
        presenter.StatusChanged += (_, _) => emitted++;

        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.HandshakeTimeout,
            "timeout",
            exceptionType: nameof(TimeoutException),
            rawError: "handshake timeout",
            isTransient: true,
            correlationId: "corr-a"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();

        Assert.Equal(1, emitted);
        var expected = FailureCopyMap.For(TransportFailureCategory.HandshakeTimeout);
        Assert.Equal(expected.Title, presenter.CurrentStatus.Title);

        now = now.AddSeconds(5);
        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.HandshakeTimeout,
            "timeout again",
            exceptionType: nameof(TimeoutException),
            rawError: "handshake timeout",
            isTransient: true,
            correlationId: "corr-b"));
        source.RaiseStateChanged();

        Assert.Equal(1, emitted);
        Assert.Equal(expected.Title, presenter.CurrentStatus.Title);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_DifferentFailureCategory_AlwaysReemits()
    {
        var source = new FakeStatusPresenterSource();
        var now = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero);
        using var presenter = new StatusPresenter(source, countdownTimer: null, nowProvider: () => now, failureDedupeWindow: TimeSpan.FromSeconds(10));
        var emitted = 0;
        presenter.StatusChanged += (_, _) => emitted++;

        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.HandshakeTimeout,
            "timeout",
            exceptionType: nameof(TimeoutException),
            rawError: "handshake timeout",
            isTransient: true,
            correlationId: "corr-a"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();

        now = now.AddSeconds(1);
        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.BridgeStartFailure,
            "bridge failed",
            exceptionType: nameof(InvalidOperationException),
            rawError: "bridge start failed",
            isTransient: false,
            correlationId: "corr-b"));
        source.RaiseStateChanged();

        Assert.Equal(2, emitted);
        var expected = FailureCopyMap.For(TransportFailureCategory.BridgeStartFailure);
        Assert.Equal(expected.Title, presenter.CurrentStatus.Title);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_UserCancelled_ReturnsToIdle()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);

        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.UserCancelled,
            "cancelled",
            exceptionType: null,
            rawError: null,
            isTransient: true,
            correlationId: null));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Idle, presenter.CurrentStatus.Kind);
        Assert.Equal(string.Empty, presenter.CurrentStatus.Title);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_ReconnectCountdown_DecrementsProperly()
    {
        var source = new FakeStatusPresenterSource();
        using var timer = new FakeManualTimer();
        using var presenter = new StatusPresenter(source, timer);

        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 2, next retry in 3s)", canCancel: true);

        Assert.Equal(3, presenter.CurrentStatus.NextRetryInSeconds);
        timer.Tick();
        Assert.Equal(2, presenter.CurrentStatus.NextRetryInSeconds);
        timer.Tick();
        Assert.Equal(1, presenter.CurrentStatus.NextRetryInSeconds);
        timer.Tick();
        Assert.Equal(0, presenter.CurrentStatus.NextRetryInSeconds);
        Assert.False(timer.IsRunning);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_ReconnectCountdown_CancelsOnConnectSuccess()
    {
        var source = new FakeStatusPresenterSource();
        using var timer = new FakeManualTimer();
        using var presenter = new StatusPresenter(source, timer);

        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 1, next retry in 5s)", canCancel: true);
        Assert.True(timer.IsRunning);

        source.SetTransportState(TransportState.Connected);
        source.SetSessionUiState(SessionRuntimeState.Connected);
        source.RaiseTransient(isVisible: false, text: string.Empty, canCancel: false);
        source.RaiseStateChanged();

        var before = presenter.CurrentStatus;
        Assert.Equal(UserStatusKind.Connected, before.Kind);
        Assert.False(timer.IsRunning);

        timer.Tick();
        Assert.Equal(before, presenter.CurrentStatus);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void StatusPresenter_ReconnectCountdown_StopsOnDispose()
    {
        var source = new FakeStatusPresenterSource();
        using var timer = new FakeManualTimer();
        var presenter = new StatusPresenter(source, timer);

        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 1, next retry in 2s)", canCancel: true);
        Assert.True(timer.IsRunning);

        presenter.Dispose();
        Assert.False(timer.IsRunning);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void InlineTransientText_Show_MakesTextVisible()
    {
        using var timer = new FakeManualTimer();
        using var feedback = new NLink.App.Services.InlineTransientText(timer);

        feedback.Show("Copied");

        Assert.True(feedback.IsVisible);
        Assert.Equal("Copied", feedback.Text);
        Assert.True(timer.IsRunning);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void InlineTransientText_AutoHides_AfterTimerTick()
    {
        using var timer = new FakeManualTimer();
        using var feedback = new NLink.App.Services.InlineTransientText(timer);

        feedback.Show("Copied");
        timer.Tick();

        Assert.False(feedback.IsVisible);
        Assert.Equal(string.Empty, feedback.Text);
        Assert.False(timer.IsRunning);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void InlineTransientText_MultipleTriggers_ReplaceMessageWithoutOverlap()
    {
        using var timer = new FakeManualTimer();
        using var feedback = new NLink.App.Services.InlineTransientText(timer);

        feedback.Show("First");
        feedback.Show("Second");

        Assert.True(feedback.IsVisible);
        Assert.Equal("Second", feedback.Text);
        Assert.True(timer.IsRunning);

        timer.Tick();

        Assert.False(feedback.IsVisible);
        Assert.Equal(string.Empty, feedback.Text);
        Assert.False(timer.IsRunning);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void BridgeExitClassifier_UsesExpectedClassification_WithFakeProcessRunner()
    {
        var fakeRunner = new FakeBridgeProcessRunner();

        var normal = BridgeExitClassifier.Classify(shuttingDown: true, forcedKill: fakeRunner.WasForcedKillRequested, exitCode: 0);
        Assert.Equal(BridgeExitReasonKind.Normal, normal.ReasonKind);
        Assert.Equal("normal", normal.ReasonText);

        var crash = BridgeExitClassifier.Classify(shuttingDown: false, forcedKill: fakeRunner.WasForcedKillRequested, exitCode: 1);
        Assert.Equal(BridgeExitReasonKind.Crash, crash.ReasonKind);
        Assert.Equal("crash", crash.ReasonText);

        fakeRunner.WasForcedKillRequested = true;
        var killed = BridgeExitClassifier.Classify(shuttingDown: true, forcedKill: fakeRunner.WasForcedKillRequested, exitCode: null);
        Assert.Equal(BridgeExitReasonKind.Killed, killed.ReasonKind);
        Assert.Equal("killed", killed.ReasonText);
    }

[Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public void MetricsTelemetrySink_FailureInjection_RecordsClassifiedFailures_AndNoUnknown()
    {
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);

        sink.OnFailure(new TransportFailureTelemetryEvent(
            Category: TransportFailureCategory.BridgeCrashed,
            IsTransient: false,
            Message: "Bridge crashed",
            ExceptionType: nameof(InvalidOperationException),
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            State: TransportState.Connected.ToString(),
            DurationMs: 100,
            SessionId: "sess1"));

        sink.OnFailure(new TransportFailureTelemetryEvent(
            Category: TransportFailureCategory.JsonProtocolError,
            IsTransient: false,
            Message: "Protocol parse error",
            ExceptionType: "JsonException",
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 1,
            Transport: "NKN",
            State: TransportState.Handshake.ToString(),
            DurationMs: 50,
            SessionId: "sess1"));

        sink.OnFailure(new TransportFailureTelemetryEvent(
            Category: TransportFailureCategory.HandshakeTimeout,
            IsTransient: true,
            Message: "Timed out",
            ExceptionType: nameof(TimeoutException),
            RunId: "run1",
            Scenario: "A",
            BridgeReuseMode: "PerSession",
            Attempt: 2,
            Transport: "NKN",
            State: TransportState.Handshake.ToString(),
            DurationMs: 5000,
            SessionId: "sess2"));

        var snapshot = registry.Snapshot();

        Assert.Contains(snapshot.Counters, c => c.Name == "bridge_crash_total" && c.Value > 0);
        Assert.Contains(snapshot.Counters, c => c.Name == "transport_failure_total" && c.Tags.FailureCategory == nameof(TransportFailureCategory.BridgeCrashed) && c.Value > 0);
        Assert.Contains(snapshot.Counters, c => c.Name == "transport_failure_total" && c.Tags.FailureCategory == nameof(TransportFailureCategory.JsonProtocolError) && c.Value > 0);
        Assert.Contains(snapshot.Counters, c => c.Name == "transport_failure_total" && c.Tags.FailureCategory == nameof(TransportFailureCategory.HandshakeTimeout) && c.Value > 0);
        Assert.DoesNotContain(snapshot.Counters, c => c.Name == "transport_failure_total" && c.Tags.FailureCategory == nameof(TransportFailureCategory.Unknown) && c.Value > 0);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void UserErrorMapper_KeyMessages_AreShortAndUserFriendly()
    {
        Assert.Equal("No response from target address.", UserErrorMapper.HelperDiscoveryTimeout());
        Assert.Equal("No response yet.", UserErrorMapper.HelperApprovalTimeout());
        Assert.Equal("Connection lost.", UserErrorMapper.HelperDisconnected());
        Assert.Equal("Connection lost.", UserErrorMapper.HelperGenericConnectFailure());
        Assert.Equal("Please reinstall.", UserErrorMapper.NknStartFailedReinstall());
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void FailurePresenter_MapsEveryTransportFailureCategory()
    {
        foreach (var category in Enum.GetValues<TransportFailureCategory>())
        {
            var presented = FailurePresenter.Present(category);
            Assert.False(string.IsNullOrWhiteSpace(presented.Title), $"Missing title for {category}");
            Assert.False(string.IsNullOrWhiteSpace(presented.Message), $"Missing message for {category}");
            Assert.False(string.IsNullOrWhiteSpace(presented.RecommendedAction), $"Missing action for {category}");
            Assert.Contains("Copy Diagnostics", presented.RecommendedAction, StringComparison.OrdinalIgnoreCase);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportFailureMapper_MapsTimeout_ToHandshakeTimeout()
    {
        var failure = TransportFailureMapper.FromException(new TimeoutException("Timed out waiting for approve."));

        Assert.Equal(TransportFailureCategory.HandshakeTimeout, failure.Category);
        Assert.True(failure.IsTransient);
        Assert.Equal(nameof(TimeoutException), failure.ExceptionType);
        Assert.False(string.IsNullOrWhiteSpace(failure.CorrelationId));
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportFailureMapper_MapsProcessExit_ToUnexpectedProcessExit()
    {
        var failure = TransportFailureMapper.FromSignals(
            rawError: "nkn_client_disconnected",
            lastDisconnectReason: "process exited",
            fallbackMessage: "Connection lost.");

        Assert.Equal(TransportFailureCategory.UnexpectedProcessExit, failure.Category);
        Assert.True(failure.IsTransient);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportFailureMapper_MapsJsonParse_ToJsonProtocolError()
    {
        var failure = TransportFailureMapper.FromException(new System.Text.Json.JsonException("invalid json"));

        Assert.Equal(TransportFailureCategory.JsonProtocolError, failure.Category);
        Assert.False(failure.IsTransient);
        Assert.Equal(nameof(System.Text.Json.JsonException), failure.ExceptionType);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReliabilityLog_RingBuffer_CapsAt50()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "reliability.jsonl");

        SessionReliabilityLog.SetStoragePathOverrideForTests(logPath);
        SessionReliabilityLog.ResetForTests();

        try
        {
            for (var i = 0; i < 60; i++)
            {
                SessionReliabilityLog.RecordStandalone(
                    "Helper",
                    "NKN",
                    SessionReliabilityStage.Disconnected,
                    errorCode: "e" + i.ToString("D2"),
                    errorHint: null);
            }

            var snapshot = SessionReliabilityLog.SnapshotRecent(100);
            Assert.Equal(50, snapshot.Count);
            Assert.Equal("e10", snapshot[0].ErrorCode);
            Assert.Equal("e59", snapshot[^1].ErrorCode);
        }
        finally
        {
            SessionReliabilityLog.ResetForTests();
            SessionReliabilityLog.SetStoragePathOverrideForTests(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void RollingFileLogger_CreatesLogFile_AndContainsAppStart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "nlink.log");

        try
        {
            var logger = new RollingFileLogger(logPath, maxFileBytes: 1024 * 1024);
            logger.WriteLine("app start | version=0.1.0-alpha.test");

            Assert.True(File.Exists(logPath));
            var text = File.ReadAllText(logPath);
            Assert.Contains("app start", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupDirectoryIfExists(tempDir);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void RollingFileLogger_Rotates_WhenSizeLimitExceeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "nlink.log");

        try
        {
            var logger = new RollingFileLogger(logPath, maxFileBytes: 80);
            logger.WriteLine(new string('A', 120));
            logger.WriteLine("second line");

            Assert.True(File.Exists(logPath));
            Assert.True(File.Exists(Path.Combine(tempDir, "nlink.1.log")));
            var current = File.ReadAllText(logPath);
            var rotated = File.ReadAllText(Path.Combine(tempDir, "nlink.1.log"));
            Assert.Contains("second line", current, StringComparison.Ordinal);
            Assert.Contains("AAA", rotated, StringComparison.Ordinal);
        }
        finally
        {
            CleanupDirectoryIfExists(tempDir);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void RollingFileLogger_Rotation_And_Write_NeverThrow_WhenFileLocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "nlink.log");

        try
        {
            File.WriteAllText(logPath, new string('X', 256));
            var logger = new RollingFileLogger(logPath, maxFileBytes: 32);

            using var lockStream = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var rotateEx = Record.Exception(() => logger.RotateIfNeeded());
            var writeEx = Record.Exception(() => logger.WriteLine("line while locked"));

            Assert.Null(rotateEx);
            Assert.Null(writeEx);
        }
        finally
        {
            CleanupDirectoryIfExists(tempDir);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReliabilityLog_Persists_JsonlLines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "reliability.jsonl");

        SessionReliabilityLog.SetStoragePathOverrideForTests(logPath);
        SessionReliabilityLog.ResetForTests();

        try
        {
            var attempt = SessionReliabilityLog.StartAttempt("Helpee", "DevLocal");
            SessionReliabilityLog.RecordStage(attempt, SessionReliabilityStage.CodeGenerated);
            SessionReliabilityLog.RecordStage(attempt, SessionReliabilityStage.Completed);

            Assert.True(File.Exists(logPath));
            var lines = File.ReadAllLines(logPath);
            Assert.True(lines.Length >= 3); // Started + CodeGenerated + Completed
            Assert.Contains("\"Stage\":\"Completed\"", lines[^1]);
            Assert.Contains("\"Mode\":\"Helpee\"", string.Join(Environment.NewLine, lines));
        }
        finally
        {
            SessionReliabilityLog.ResetForTests();
            SessionReliabilityLog.SetStoragePathOverrideForTests(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReliabilityLog_Redacts_SecretLikeTokens()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "reliability.jsonl");

        SessionReliabilityLog.SetStoragePathOverrideForTests(logPath);
        SessionReliabilityLog.ResetForTests();

        const string fakePayload = "payloadBase64=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==";
        const string fakeKey = "sharedKey=0123456789abcdef0123456789abcdef0123456789abcdef";

        try
        {
            SessionReliabilityLog.RecordStandalone(
                "Helper",
                "NKN",
                SessionReliabilityStage.Disconnected,
                errorCode: "bridge_ping_timeout",
                errorHint: $"{fakePayload} {fakeKey}");

            var line = File.ReadAllText(logPath);
            Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==", line);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef0123456789abcdef", line);
            Assert.Contains("[redacted]", line);
        }
        finally
        {
            SessionReliabilityLog.ResetForTests();
            SessionReliabilityLog.SetStoragePathOverrideForTests(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void RemoteControlDiagnostics_LogStaleDrops_RateLimited()
    {
        using var runtime = new SessionRuntime(
            () => new DevLocalTransport(),
            SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        var logStart = GetOperationalLogLength();
        var peerId = "diag-peer-" + Guid.NewGuid().ToString("N");
        var staleInputRequestId = "diag-stale-input-" + Guid.NewGuid().ToString("N");
        var staleSnapshotRequestId = "diag-stale-snapshot-" + Guid.NewGuid().ToString("N");

        InvokePrivateMethod(runtime, "LogRemoteControlInjectionSuppressed", "display_revision_stale", staleInputRequestId, peerId);
        InvokePrivateMethod(runtime, "LogRemoteControlInjectionSuppressed", "display_revision_stale", staleInputRequestId, peerId);
        InvokePrivateMethod(runtime, "LogRemoteControlSnapshotSuppressed", "duplicate_seq", staleSnapshotRequestId, peerId);
        InvokePrivateMethod(runtime, "LogRemoteControlSnapshotSuppressed", "duplicate_seq", staleSnapshotRequestId, peerId);

        var tail = ReadOperationalLogTail(logStart);
        var staleInputLines = tail
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
            line.Contains("[RemoteControl]", StringComparison.Ordinal) &&
            line.Contains("event=input_stale_dropped", StringComparison.Ordinal) &&
            line.Contains("reason=display_revision_stale", StringComparison.Ordinal))
            .ToList();
        Assert.Single(staleInputLines);

        var staleSnapshotLines = tail
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
            line.Contains("[RemoteControl]", StringComparison.Ordinal) &&
            line.Contains("event=snapshot_stale_dropped", StringComparison.Ordinal) &&
            line.Contains("reason=duplicate_seq", StringComparison.Ordinal))
            .ToList();
        Assert.Single(staleSnapshotLines);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void RemoteControlDiagnostics_LogSecurityStop_RateLimited()
    {
        using var runtime = new SessionRuntime(
            () => new DevLocalTransport(),
            SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        var logStart = GetOperationalLogLength();
        var requestId = "diag-security-stop-" + Guid.NewGuid().ToString("N");
        var peerId = "diag-security-peer-" + Guid.NewGuid().ToString("N");
        var activeState = RemoteControlSessionState.Default with
        {
            ControlState = ControlState.Active,
            CurrentControlRequestId = requestId,
            ControllerPeerId = peerId,
            SupportsRemoteControl = true,
            PeerSupportsRemoteControl = true,
        };

        SetPrivateField(runtime, "remoteControlSessionState", activeState);
        InvokePrivateMethod(runtime, "EnsureRemoteControlStoppedForAuthorizationLoss", "capability_lost");
        SetPrivateField(runtime, "remoteControlSessionState", activeState);
        InvokePrivateMethod(runtime, "EnsureRemoteControlStoppedForAuthorizationLoss", "capability_lost");

        var lines = ReadOperationalLogTail(logStart)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
            line.Contains("[RemoteControl]", StringComparison.Ordinal) &&
            line.Contains("event=security_stop_initiated", StringComparison.Ordinal) &&
            line.Contains("reason=security_capability_lost", StringComparison.Ordinal))
            .ToList();
        Assert.Single(lines);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareDiagnostics_LogSuppressedLateDisplayInfoSend_RateLimited()
    {
        var displayId = "diag-display-" + Guid.NewGuid().ToString("N");
        var logStart = GetOperationalLogLength();
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => new FakeScreenCaptureSource(),
            sendPayloadAsync: static (_, _) => Task.CompletedTask);
        var message = new ControlDisplayInfoMessageV1
        {
            DisplayId = displayId,
            Revision = 17,
            FrameWidth = 1280,
            FrameHeight = 720,
        };

        InvokePrivateMethod(coordinator, "LogDisplayInfoSendSuppressed", message);
        InvokePrivateMethod(coordinator, "LogDisplayInfoSendSuppressed", message);

        var lines = ReadOperationalLogTail(logStart)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
            line.Contains("[ScreenShareTransport]", StringComparison.Ordinal) &&
            line.Contains("event=display_info_send_suppressed", StringComparison.Ordinal) &&
            line.Contains("reason=ownership_changed", StringComparison.Ordinal))
            .ToList();
        Assert.Single(lines);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void CpuUsageCalculator_ReturnsNonNegative_AndStableForFixedInputs()
    {
        var value1 = CpuUsageCalculator.CalculatePercent(deltaCpuMs: 50, elapsedWallMs: 1000, processorCount: 4);
        var value2 = CpuUsageCalculator.CalculatePercent(deltaCpuMs: 50, elapsedWallMs: 1000, processorCount: 4);
        var zero = CpuUsageCalculator.CalculatePercent(deltaCpuMs: -1, elapsedWallMs: 1000, processorCount: 4);

        Assert.InRange(value1, 0, 100);
        Assert.Equal(value1, value2);
        Assert.Equal(0, zero);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ResourceSnapshot_JsonSerialization_IsDeterministic()
    {
        var snapshot = new ResourceSnapshot(
            TimestampUtc: new DateTimeOffset(2026, 2, 26, 12, 0, 0, TimeSpan.Zero),
            App: new ResourceProcessSnapshot(123, "nLink", 100.5, 90.25, 50.75, 1, 2, 3, 25, 120, 1.25),
            Bridge: new ResourceProcessSnapshot(456, "node", 80.5, 70.25, 0, 0, 0, 0, 12, 80, 0.75),
            ActiveCounters: new ActiveResourceCountersSnapshot(0, 0, 0, 0, 0, 0));

        var json1 = snapshot.ToJson();
        var json2 = snapshot.ToJson();

        Assert.Equal(json1, json2);
        Assert.Contains("\"App\"", json1, StringComparison.Ordinal);
        Assert.Contains("\"Bridge\"", json1, StringComparison.Ordinal);
        Assert.Contains("\"ActiveCounters\"", json1, StringComparison.Ordinal);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ResourceGate_EvaluatesThresholdsAndGrowth()
    {
        var baseSnapshot = new ResourceSnapshot(
            DateTimeOffset.UtcNow,
            new ResourceProcessSnapshot(1, "app", 100, 100, 10, 0, 0, 0, 10, 100, 1),
            null,
            new ActiveResourceCountersSnapshot(0, 0, 0, 0, 0, 0));
        var endSnapshot = new ResourceSnapshot(
            DateTimeOffset.UtcNow.AddSeconds(10),
            new ResourceProcessSnapshot(1, "app", 140, 150, 12, 1, 0, 0, 12, 120, 2),
            null,
            new ActiveResourceCountersSnapshot(0, 0, 0, 0, 0, 0));

        var summary = ResourceSummaryBuilder.BuildSummary(new[] { baseSnapshot, endSnapshot });
        var thresholds = new ResourceGateThresholds(
            AppWorkingSetMaxMB: 200,
            AppPrivateBytesMaxMB: 200,
            AppThreadMax: 100,
            AppHandleMax: 500,
            AppCpuIdleAvgMaxPct: 50,
            GrowthWarnPercent: 5,
            GrowthFailPercent: 60);
        var result = ResourceGate.Evaluate(new ResourceGateInput(summary, "DEVLOCAL", "PerSession", "test"), thresholds);

        Assert.True(result.Passed);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("growth", StringComparison.OrdinalIgnoreCase));
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ResourceGate_CanDisableGenericGrowthChecks_WhileKeepingCleanupChecks()
    {
        var snapshotA = new ResourceSnapshot(
            DateTimeOffset.UtcNow,
            new ResourceProcessSnapshot(1, "app", 100, 100, 10, 0, 0, 0, 10, 100, 0),
            null,
            new ActiveResourceCountersSnapshot(0, 0, 0, 0, 0, 0));
        var snapshotB = snapshotA with
        {
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            App = snapshotA.App with { WorkingSetMB = 250, PrivateBytesMB = 250, ThreadCount = 20, HandleCount = 300 }
        };

        var summary = ResourceSummaryBuilder.BuildSummary(new[] { snapshotA, snapshotB });
        var thresholds = new ResourceGateThresholds(
            AppWorkingSetMaxMB: 1000,
            AppPrivateBytesMaxMB: 1000,
            AppThreadMax: 1000,
            AppHandleMax: 1000,
            AppCpuIdleAvgMaxPct: 100,
            GrowthWarnPercent: 1,
            GrowthFailPercent: 1,
            EvaluateGrowthChecks: false);

        var result = ResourceGate.Evaluate(new ResourceGateInput(summary, "DEVLOCAL", "PerSession", "test"), thresholds);
        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
        Assert.Empty(result.Warnings);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ResourceBenchmarkRunner_LeakCheck_ShortRun_LeavesActiveCountersAtZero_AndWritesArtifacts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-resource-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempRoot;
            ActiveRuntimeCounters.ResetForTests();

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await ResourceBenchmarkRunner.RunAsync(
                new[]
                {
                    "--leak-check",
                    "--cycles", "50",
                    "--transport", "devlocal",
                    "--bridge-reuse-mode", "persession",
                    "--delay-ms", "0",
                    "--leak-growth-fail-percent", "1000"
                },
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);

            var resourcesDir = Path.Combine(tempRoot, "artifacts", "resources");
            Assert.True(Directory.Exists(resourcesDir));
            Assert.NotEmpty(Directory.GetFiles(resourcesDir, "leak-check-*.json", SearchOption.TopDirectoryOnly));
            Assert.True(File.Exists(Path.Combine(resourcesDir, "leak-check-summary.txt")));

            var counters = ActiveRuntimeCounters.Snapshot();
            Assert.Equal(0, counters.ActiveSessions);
            Assert.Equal(0, counters.ActiveConnectAttempts);
            Assert.Equal(0, counters.ActiveRetryTimers);
            Assert.Equal(0, counters.ActiveWatchdogs);
            Assert.Equal(0, counters.ActiveTransportTasks);
            Assert.Equal(0, counters.ActiveBridgeIoReaders);

            var nkn = NknRuntimeDiagnostics.Snapshot();
            if (nkn.BridgePid > 0)
            {
                bool isRunning;
                try
                {
                    using var process = Process.GetProcessById(nkn.BridgePid);
                    isRunning = !process.HasExited;
                }
                catch (ArgumentException)
                {
                    isRunning = false;
                }

                Assert.False(isRunning, $"Expected no orphan bridge PID after DevLocal leak-check, but PID {nkn.BridgePid} is still running.");
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
            ActiveRuntimeCounters.ResetForTests();
        }
    }

}
