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
using NLink.Core.Logging;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public class SmokeTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void FeatureFlags_DefaultsMatchScreenShareReleaseRollout_WhenNoEnvironmentOverridesArePresent()
    {
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_CHAT_HARDENING")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_CAPTURE")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_PREVIEW")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_RESPONSIVE_LAYOUT")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SESSION_HEADER")));

        Assert.True(FeatureFlags.EnableChatHardening);
        Assert.True(FeatureFlags.EnableResponsiveLayout);
        Assert.True(FeatureFlags.EnableScreenShareScaffold);
        Assert.True(FeatureFlags.EnableScreenShareCapture);
        Assert.True(FeatureFlags.EnableScreenSharePreview);
        Assert.True(FeatureFlags.EnableScreenShareTransport);
        Assert.True(FeatureFlags.EnableSessionHeader);
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_WithCode_NoUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage("123456", null);
        Assert.Equal("Install nLink and enter code 123456", text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_WithCode_AndUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage("123456", "https://example.com/nlink");
        Assert.Equal(
            "Install nLink and enter code 123456" + Environment.NewLine + "https://example.com/nlink",
            text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_WithoutCode_WithUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage(null, "https://example.com/nlink");
        Assert.Equal(
            "Install nLink" + Environment.NewLine + "https://example.com/nlink",
            text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_HelperInstallMessage_IncludesConfiguredUrl_AndTrailingNewline()
    {
        var text = ShareMessageBuilder.BuildHelperInstallMessage("https://example.com/releases");

        Assert.Equal(
            "Install nLink and open it." + Environment.NewLine +
            "Download: https://example.com/releases" + Environment.NewLine,
            text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_HelperInstallMessage_DoesNotIncludeInternalDiagnosticsText()
    {
        var text = ShareMessageBuilder.BuildHelperInstallMessage("https://example.com/releases");

        Assert.DoesNotContain("Bridge PID", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NKN", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last_error", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identifier", text, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_StatusBanner_ReactsToFailedReconnectingConnected()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        var transportConfig = CreateDevLocalTestConfig();

        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            statusPresenter: presenter);

        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.HandshakeTimeout,
            "Timed out",
            exceptionType: nameof(TimeoutException),
            rawError: "handshake timeout",
            isTransient: true,
            correlationId: "corr1"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Failed, helper.BannerStatus.Kind);
        Assert.True(helper.ShowStatusBanner);

        source.SetAttempt(2);
        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 2, next retry in 1s)", canCancel: true);

        Assert.Equal(UserStatusKind.Reconnecting, helper.BannerStatus.Kind);
        Assert.True(helper.ShowStatusBanner);

        source.SetSessionUiState(SessionRuntimeState.Connected);
        source.SetTransportState(TransportState.Connected);
        source.RaiseTransient(isVisible: false, text: string.Empty, canCancel: false);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Connected, helper.BannerStatus.Kind);
        Assert.False(helper.ShowStatusBanner);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_StatusBanner_ReactsToFailedReconnectingConnected()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        var transportConfig = CreateDevLocalTestConfig();

        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            statusPresenter: presenter);

        source.SetFailure(TransportFailure.Create(
            TransportFailureCategory.BridgeStartFailure,
            "Bridge unavailable",
            exceptionType: nameof(InvalidOperationException),
            rawError: "bridge_start_failed",
            isTransient: false,
            correlationId: "corr2"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Failed, helpee.BannerStatus.Kind);
        Assert.True(helpee.ShowStatusBanner);

        source.SetAttempt(3);
        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 3, next retry in 2s)", canCancel: true);

        Assert.Equal(UserStatusKind.Reconnecting, helpee.BannerStatus.Kind);
        Assert.True(helpee.ShowStatusBanner);

        source.SetSessionUiState(SessionRuntimeState.Connected);
        source.SetTransportState(TransportState.Connected);
        source.RaiseTransient(isVisible: false, text: string.Empty, canCancel: false);
        source.RaiseStateChanged();

        Assert.Equal(UserStatusKind.Connected, helpee.BannerStatus.Kind);
        Assert.False(helpee.ShowStatusBanner);
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_KeepAliveBridge_IdleTimeout_DisposesCachedBridge_AndRecordsKilledMetric()
    {
        FakeNknClient.ResetNetwork();

        var idleDelayTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);
        var fakeClient = new FakeNknClient("keepalive.host.addr");
        var transport = new NknSignalingTransport(
            fakeClient,
            LoadNknOptionsWithOverrides(
                Path.Combine(Path.GetTempPath(), "nlink-test-keepalive-" + Guid.NewGuid().ToString("N") + ".json"),
                "keepalive-host"),
            new NknIdentity("keepalive-host", "keepalive.host.addr"));

        using var runtime = new SessionRuntime(
            () => transport,
            SessionRuntimeWatchdogOptions.Default with { Enabled = false },
            telemetrySink: sink,
            bridgeReusePolicy: new BridgeReusePolicy(BridgeReuseMode.KeepAlive, TimeSpan.FromSeconds(1)),
            bridgeIdleDelayAsync: (_, _) => idleDelayTcs.Task);

        await runtime.StartHelpeeAsync(CancellationToken.None);
        await runtime.ResetAsync();

        Assert.True(runtime.HasCachedBridgeTransportForTests());

        idleDelayTcs.TrySetResult();
        await WaitUntilAsync(() => !runtime.HasCachedBridgeTransportForTests(), TimeSpan.FromSeconds(2));

        var snapshot = registry.Snapshot();
        Assert.Contains(snapshot.Counters, c => c.Name == "bridge_exit_total" && c.Tags.Result == "killed" && c.Value >= 1);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void UserErrorMapper_KeyMessages_AreShortAndUserFriendly()
    {
        Assert.Equal("No response from target address.", UserErrorMapper.HelperDiscoveryTimeout());
        Assert.Equal("No response yet.", UserErrorMapper.HelperApprovalTimeout());
        Assert.Equal("Connection lost.", UserErrorMapper.HelperDisconnected());
        Assert.Equal("Connection lost.", UserErrorMapper.HelperGenericConnectFailure());
        Assert.Equal("Please reinstall.", UserErrorMapper.NknStartFailedReinstall());
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void AppAssembly_InformationalVersion_Matches_VERSION_File()
    {
        var assembly = typeof(DiagnosticsPageViewModel).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(infoVersion));

        var versionPath = FindFileUpwards("VERSION");
        Assert.True(versionPath is not null, "VERSION file not found when walking parent directories from test output.");

        var expected = File.ReadAllText(versionPath!).Trim();
        Assert.Equal(expected, infoVersion);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Program_Parses_SelfTest_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasSelfTestArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSelfTest = (bool)method!.Invoke(null, new object[] { new[] { "--self-test" } })!;
        var noSelfTest = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasSelfTest);
        Assert.False(noSelfTest);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Program_Parses_Benchmark_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasBenchmarkArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasBench = (bool)method!.Invoke(null, new object[] { new[] { "--bench" } })!;
        var noBench = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasBench);
        Assert.False(noBench);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Program_Parses_Soak_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasSoakArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSoak = (bool)method!.Invoke(null, new object[] { new[] { "--soak" } })!;
        var noSoak = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasSoak);
        Assert.False(noSoak);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Program_Parses_ScreenShareSoak_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasScreenShareSoakArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSoak = (bool)method!.Invoke(null, new object[] { new[] { "--screenshare-soak" } })!;
        var noSoak = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasSoak);
        Assert.False(noSoak);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void BenchmarkRunner_Parses_Defaults_And_Overrides()
    {
        Assert.True(BenchmarkRunner.TryParseOptionsForTests(new[] { "--bench" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.Equal(50, defaults!.Cycles);
        Assert.Equal(0, defaults.DelayMs);
        Assert.Equal("devlocal", defaults.Transport);
        Assert.Equal(BridgeReuseMode.PerSession, defaults.BridgeReuseMode);
        Assert.False(defaults.MemoryCheck);
        Assert.Equal(5d, defaults.MemoryTolerancePercent);

        Assert.True(BenchmarkRunner.TryParseOptionsForTests(
            new[] { "--bench", "--cycles", "3", "--delay-ms", "25", "--transport", "nkn", "--bridge-reuse-mode", "keepalive", "--memory-check", "--memory-tolerance-percent", "7.5" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.Equal(3, custom!.Cycles);
        Assert.Equal(25, custom.DelayMs);
        Assert.Equal("nkn", custom.Transport);
        Assert.Equal(BridgeReuseMode.KeepAlive, custom.BridgeReuseMode);
        Assert.True(custom.MemoryCheck);
        Assert.Equal(7.5d, custom.MemoryTolerancePercent);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SoakRunner_Parses_And_Maps_To_BenchmarkArgs()
    {
        Assert.True(SoakRunner.TryParseOptionsForTests(new[] { "--soak" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.False(defaults!.FailOnGate);

        var defaultBenchArgs = SoakRunner.BuildBenchmarkArgsForTests(defaults);
        Assert.Contains("--bench", defaultBenchArgs);
        Assert.DoesNotContain("--reliability-gate", defaultBenchArgs);

        Assert.True(SoakRunner.TryParseOptionsForTests(
            new[] { "--soak", "--cycles", "10", "--delay-ms", "5", "--transport", "devlocal", "--bridge-reuse-mode", "persession", "--fail-on-gate" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.True(custom!.FailOnGate);

        var mappedArgs = SoakRunner.BuildBenchmarkArgsForTests(custom);
        Assert.Contains("--bench", mappedArgs);
        Assert.Contains("--cycles", mappedArgs);
        Assert.Contains("10", mappedArgs);
        Assert.Contains("--delay-ms", mappedArgs);
        Assert.Contains("5", mappedArgs);
        Assert.Contains("--transport", mappedArgs);
        Assert.Contains("devlocal", mappedArgs);
        Assert.Contains("--bridge-reuse-mode", mappedArgs);
        Assert.Contains("persession", mappedArgs);
        Assert.Contains("--reliability-gate", mappedArgs);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ScreenShareSoakRunner_Parses_Defaults_And_Overrides()
    {
        Assert.True(ScreenShareSoakRunner.TryParseOptionsForTests(new[] { "--screenshare-soak" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.SampleInterval);

        Assert.True(ScreenShareSoakRunner.TryParseOptionsForTests(
            new[] { "--screenshare-soak", "--seconds", "90", "--sample-interval-seconds", "15" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.Equal(TimeSpan.FromSeconds(90), custom!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(15), custom.SampleInterval);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_TransportStateMachine_AllowsExpectedTransitions_AndStoresMonotonicTimestamps()
    {
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Idle, TransportState.TransportInitializing));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Idle, TransportState.Connected));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.TransportInitializing, TransportState.BridgeStarting));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeStarting, TransportState.BridgeReady));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeStarting, TransportState.Handshake));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeReady, TransportState.Connecting));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Connecting, TransportState.Handshake));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Handshake, TransportState.Connected));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Connected, TransportState.Reconnecting));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Reconnecting, TransportState.Idle));

        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        var idleTs = runtime.GetTransportStateEntryTimestamp(TransportState.Idle);
        Assert.True(idleTs > 0);

        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test"));
        Assert.True(runtime.GetTransportStateEntryTimestamp(TransportState.TransportInitializing) >= idleTs);
        Assert.Equal(TransportState.TransportInitializing, runtime.TransportLifecycleState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_TransportStateMachine_BlocksInvalidTransitions()
    {
        Assert.False(SessionRuntime.IsTransportTransitionAllowed(TransportState.Idle, TransportState.Handshake));
        Assert.False(SessionRuntime.IsTransportTransitionAllowed(TransportState.Disposed, TransportState.Idle));
        Assert.False(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeReady, TransportState.BridgeStarting));

        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        var idleTs = runtime.GetTransportStateEntryTimestamp(TransportState.Idle);

        var changed = runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "invalid_test");
        Assert.False(changed);
        Assert.Equal(TransportState.Idle, runtime.TransportLifecycleState);
        Assert.Equal(idleTs, runtime.GetTransportStateEntryTimestamp(TransportState.Idle));
        Assert.Equal(0, runtime.GetTransportStateEntryTimestamp(TransportState.Handshake));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_TransportDurations_AreRecorded_OnSuccess_AndNonNegative()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());

        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "bridge"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeReady, "bridge_ready"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "connect"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "hs"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connected, "done"));

        var bridgeMs = runtime.GetLastDurationMetricMilliseconds("bridge_start_duration_ms");
        var initMs = runtime.GetLastDurationMetricMilliseconds("transport_init_duration_ms");
        var connectMs = runtime.GetLastDurationMetricMilliseconds("connect_duration_ms");
        var handshakeMs = runtime.GetLastDurationMetricMilliseconds("handshake_duration_ms");

        Assert.NotNull(bridgeMs);
        Assert.NotNull(initMs);
        Assert.NotNull(connectMs);
        Assert.NotNull(handshakeMs);
        Assert.True(bridgeMs!.Value >= 0);
        Assert.True(initMs!.Value >= 0);
        Assert.True(connectMs!.Value >= 0);
        Assert.True(handshakeMs!.Value >= 0);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_TransportDurations_AreRecorded_OnFailure_AndNonNegative()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());

        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "bridge"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Failed, "bridge_fail"));

        var bridgeMs = runtime.GetLastDurationMetricMilliseconds("bridge_start_duration_ms");
        var initMs = runtime.GetLastDurationMetricMilliseconds("transport_init_duration_ms");
        var connectMs = runtime.GetLastDurationMetricMilliseconds("connect_duration_ms");

        Assert.NotNull(bridgeMs);
        Assert.NotNull(initMs);
        Assert.NotNull(connectMs);
        Assert.True(bridgeMs!.Value >= 0);
        Assert.True(initMs!.Value >= 0);
        Assert.True(connectMs!.Value >= 0);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_WatchdogTimeout_Handshake_TransitionsToFailed_AndClassifiesFailure()
    {
        var delay = new ControlledDelayScheduler();
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            HandshakeTimeout = TimeSpan.FromSeconds(30),
        };

        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(),
            options,
            delay.DelayAsync);

        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "connect_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "handshake_start"));

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();

        await WaitUntilAsync(
            () => runtime.TransportLifecycleState == TransportState.Failed,
            TimeSpan.FromSeconds(2));

        Assert.Equal(TransportFailureCategory.HandshakeTimeout, runtime.GetLastFailureCategoryForTests());
        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("No response yet.", runtime.StatusText);
        Assert.NotNull(runtime.GetLastDurationMetricMilliseconds("handshake_duration_ms"));
        Assert.True(runtime.GetLastDurationMetricMilliseconds("handshake_duration_ms")!.Value >= 0);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_WatchdogTimeout_BridgeStarting_TransitionsToFailed_AndClassifiesFailure()
    {
        var delay = new ControlledDelayScheduler();
        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(),
            SessionRuntimeWatchdogOptions.Default,
            delay.DelayAsync);

        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "bridge_start"));

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();

        await WaitUntilAsync(
            () => runtime.TransportLifecycleState == TransportState.Failed,
            TimeSpan.FromSeconds(2));

        Assert.Equal(TransportFailureCategory.BridgeStartFailure, runtime.GetLastFailureCategoryForTests());
        Assert.Equal("Please reinstall.", runtime.StatusText);
        Assert.NotNull(runtime.GetLastDurationMetricMilliseconds("bridge_start_duration_ms"));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_WatchdogTimeout_AutoRetryEnabled_ResetsToIdle()
    {
        var delay = new ControlledDelayScheduler();
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            AutoRetryEnabled = true,
            ConnectingTimeout = TimeSpan.FromSeconds(30),
        };

        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(),
            options,
            delay.DelayAsync);

        await runtime.StartHelpeeAsync(CancellationToken.None);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "test_handshake"));

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();

        await WaitUntilAsync(
            () => runtime.TransportLifecycleState == TransportState.Idle && runtime.State == SessionRuntimeState.Idle,
            TimeSpan.FromSeconds(3));

        Assert.Equal(TransportFailureCategory.HandshakeTimeout, runtime.GetLastFailureCategoryForTests());
        Assert.NotNull(runtime.GetLastDurationMetricMilliseconds("connect_duration_ms"));
        Assert.True(runtime.GetLastDurationMetricMilliseconds("connect_duration_ms")!.Value >= 0);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_HelpeeConnectingFromBridgeReady_DoesNotWatchdogTimeoutWhileIdle()
    {
        var delay = new ControlledDelayScheduler();
        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(),
            SessionRuntimeWatchdogOptions.Default,
            delay.DelayAsync);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "start_helpee"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "nkn_bridge_starting"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeReady, "bridge_ready"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "bridge_ready"));

        // The helpee hosting path should not arm a connecting watchdog while idle.
        await Task.Delay(100);

        Assert.Equal(0, delay.PendingCount);
        Assert.Equal(TransportState.Connecting, runtime.TransportLifecycleState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_HelpeeIdleDisconnect_DuplicateEvents_DoNotStartMultipleRehosts()
    {
        var created = new List<ScriptedSignalingTransport>();
        var factory = new CountingTransportFactory(() =>
        {
            var transport = new ScriptedSignalingTransport();
            lock (created)
            {
                created.Add(transport);
            }

            return transport;
        });

        using var runtime = new SessionRuntime(factory.Create);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        ScriptedSignalingTransport first;
        lock (created)
        {
            first = Assert.IsType<ScriptedSignalingTransport>(created.Single());
        }

        // Duplicate disconnected notifications can happen around bridge/process teardown.
        first.RaiseDisconnected();
        first.RaiseDisconnected();

        await WaitUntilAsync(() => factory.CreateCount >= 2, TimeSpan.FromSeconds(2));
        await Task.Delay(200);

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_IgnoresStaleTransportDisconnectedEvent_AfterResetAndRehost()
    {
        var first = new ScriptedSignalingTransport();
        var second = new ScriptedSignalingTransport();
        var queue = new Queue<ISignalingTransport>(new ISignalingTransport[] { first, second });

        using var runtime = new SessionRuntime(() => queue.Dequeue());

        await runtime.StartHelpeeAsync(CancellationToken.None);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);

        await runtime.ResetAsync();
        await runtime.StartHelpeeAsync(CancellationToken.None);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);

        var onDisconnected = typeof(SessionRuntime).GetMethod(
            "OnTransportDisconnected",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onDisconnected);

        onDisconnected!.Invoke(runtime, new object?[] { first, EventArgs.Empty });

        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for helper…", runtime.StatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportFailureMapper_MapsTimeout_ToHandshakeTimeout()
    {
        var failure = TransportFailureMapper.FromException(new TimeoutException("Timed out waiting for approve."));

        Assert.Equal(TransportFailureCategory.HandshakeTimeout, failure.Category);
        Assert.True(failure.IsTransient);
        Assert.Equal(nameof(TimeoutException), failure.ExceptionType);
        Assert.False(string.IsNullOrWhiteSpace(failure.CorrelationId));
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportFailureMapper_MapsJsonParse_ToJsonProtocolError()
    {
        var failure = TransportFailureMapper.FromException(new System.Text.Json.JsonException("invalid json"));

        Assert.Equal(TransportFailureCategory.JsonProtocolError, failure.Category);
        Assert.False(failure.IsTransient);
        Assert.Equal(nameof(System.Text.Json.JsonException), failure.ExceptionType);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_ConnectAttempt_IncrementsOnRetry_SameSession()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
            onJoinByAddressAsync: static (_, _) => Task.CompletedTask));
        var targetAddress = new PeerAddress("scripted.connect.retry");

        await runtime.StartHelperAsync(targetAddress, CancellationToken.None);
        var firstAttempt = runtime.GetConnectAttemptForTests();
        var firstSessionId = runtime.GetSessionIdForTests();

        Assert.Equal(1, firstAttempt);
        Assert.False(string.IsNullOrWhiteSpace(firstSessionId));

        await runtime.ResetAsync();
        await runtime.StartHelperAsync(targetAddress, CancellationToken.None);

        Assert.Equal(2, runtime.GetConnectAttemptForTests());
        Assert.Equal(firstSessionId, runtime.GetSessionIdForTests());
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_ConnectAttempt_ResetsForNewSession()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
            onJoinByAddressAsync: static (_, _) => Task.CompletedTask));

        await runtime.StartHelperAsync(new PeerAddress("scripted.connect.first"), CancellationToken.None);
        var firstSessionId = runtime.GetSessionIdForTests();
        Assert.Equal(1, runtime.GetConnectAttemptForTests());

        await runtime.ResetAsync();
        await runtime.StartHelperAsync(new PeerAddress("scripted.connect.second"), CancellationToken.None);

        Assert.Equal(1, runtime.GetConnectAttemptForTests());
        Assert.NotEqual(firstSessionId, runtime.GetSessionIdForTests());
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Diagnostics_CopyExport_IncludesRuntimeBasics_AndNoPayloadOrChatHistory()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            SessionTimeline.Clear();
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Disconnected", "timeout");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
            runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test");
            runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "test");
            runtime.TryTransitionTransportStateForTests(TransportState.Failed, "test");
            await runtime.FailAsync(
                TransportFailure.Create(TransportFailureCategory.HandshakeTimeout, "Timed out", exceptionType: nameof(TimeoutException), rawError: "timeout", isTransient: true),
                "No response yet.");
            var metrics = new MetricsRegistry();
            metrics.Counter("transport_connect_attempts_total", transport: "NKN", scenario: "A").Inc(2);
            metrics.Counter("transport_connect_success_total", transport: "NKN", scenario: "A").Inc(1);
            metrics.Histogram("transport_connect_duration_ms", transport: "NKN", scenario: "A").Observe(10);
            metrics.Histogram("transport_connect_duration_ms", transport: "NKN", scenario: "A").Observe(30);
            var vm = new DiagnosticsPageViewModel(static () => { }, config, sessionRuntime: runtime, metricsRegistry: metrics);

            string? copied = null;
            vm.CopyReliabilityLogRequested += (_, text) => copied = text;

            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.False(string.IsNullOrWhiteSpace(copied));
            Assert.Contains("App version:", copied!, StringComparison.Ordinal);
            Assert.Contains("OS:", copied!, StringComparison.Ordinal);
            Assert.Contains("Process architecture:", copied!, StringComparison.Ordinal);
            Assert.Contains("OS architecture:", copied!, StringComparison.Ordinal);
            Assert.Contains("Bridge RID:", copied!, StringComparison.Ordinal);
            Assert.Contains("current_state:", copied!, StringComparison.Ordinal);
            Assert.Contains("attempt:", copied!, StringComparison.Ordinal);
            Assert.Contains("Transport:", copied!, StringComparison.Ordinal);
            Assert.Contains("Forced by environment:", copied!, StringComparison.Ordinal);
            Assert.Contains("bridge_process_status:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_connect_duration_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_handshake_duration_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_bridge_start_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_failure_category:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_failure_message:", copied!, StringComparison.Ordinal);
            Assert.Contains("Metrics snapshot", copied!, StringComparison.Ordinal);
            Assert.Contains("connect_attempts_total:", copied!, StringComparison.Ordinal);
            Assert.Contains("connect_success_rate_pct:", copied!, StringComparison.Ordinal);
            Assert.Contains("transport_connect_duration_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("Session timeline (last 30)", copied!, StringComparison.Ordinal);
            Assert.Contains("Started", copied!, StringComparison.Ordinal);
            Assert.Contains("Disconnected | timeout", copied!, StringComparison.Ordinal);

            Assert.DoesNotContain("payloadBase64", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hello from helper", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", copied!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void DiagnosticsPageViewModel_ExportsMetricsJson_ToArtifactsDiagnostics_WithDeterministicTimestamp()
    {
        var metrics = new MetricsRegistry();
        metrics.Counter("transport_connect_attempts_total", transport: "NKN").Inc();

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-metrics-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var config = CreateDevLocalTestConfig();
            var vm = new DiagnosticsPageViewModel(
                static () => { },
                config,
                metricsRegistry: metrics,
                nowProvider: static () => new DateTimeOffset(2026, 2, 24, 12, 34, 56, TimeSpan.Zero),
                diagnosticsExportRootProvider: () => tempRoot);

            var path = vm.ExportMetricsJsonForTests();
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRoot, "metrics-20260224-123456.json")), path);
            Assert.True(File.Exists(path));

            var json = File.ReadAllText(path);
            Assert.Contains("\"Counters\"", json, StringComparison.Ordinal);
            Assert.Contains("transport_connect_attempts_total", json, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Diagnostics_And_OperationalLog_Redact_Sensitive_Content()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var uniqueChatText = "hello-from-helper-" + Guid.NewGuid().ToString("N");
        var sensitive = string.Join(' ', new[]
        {
            "payloadBase64=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==",
            "sharedKey=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "seedBase64=QkFTRTY0U0VFRA==",
            "seed=supersecretseedvalue",
            "identifier=nlink-private-identifier",
            $"chat={uniqueChatText}"
        });

        try
        {
            SessionTimeline.Clear();
            SessionTimeline.Record("ChatReceived", sensitive);
            NknRuntimeDiagnostics.SetLastDisconnectReason(sensitive);
            NknRuntimeDiagnostics.SetLastError("NKN_START_FAILED: " + sensitive);

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);
            string? diagnostics = null;
            vm.CopyReliabilityLogRequested += (_, text) => diagnostics = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(diagnostics);
            Assert.DoesNotContain("payloadBase64", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", diagnostics!, StringComparison.OrdinalIgnoreCase);

            var source = "UnitTestPrivacy" + Guid.NewGuid().ToString("N")[..8];
            LocalOperationalLog.Info(source, sensitive);

            var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
            var matchingLine = logText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains($"[{source}]", StringComparison.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(matchingLine));
            Assert.DoesNotContain("payloadBase64", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", matchingLine!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionTimeline.Clear();
            NknRuntimeDiagnostics.SetLastDisconnectReason("(none)");
            NknRuntimeDiagnostics.SetLastError("(none)");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionTimeline_IsCappedAt30_AndDiagnosticsExportUsesLatestEntries()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            SessionTimeline.Clear();
            for (var i = 0; i < 35; i++)
            {
                SessionTimeline.Record("Event" + i.ToString("D2"));
            }

            var snapshot = SessionTimeline.SnapshotRecent(100);
            Assert.Equal(30, snapshot.Count);
            Assert.Equal("Event05", snapshot[0].EventName);
            Assert.Equal("Event34", snapshot[^1].EventName);

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);
            string? export = null;
            vm.CopyReliabilityLogRequested += (_, text) => export = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(export);
            Assert.Contains("Event34", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event00", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event01", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event02", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event03", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event04", export!, StringComparison.Ordinal);
        }
        finally
        {
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseDefault_SelectsNkn_WhenBridgeBundled()
    {
#if DEBUG
        return;
#endif
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var bridgeRid = GetCurrentBridgeRidForTests();
        var bridgeRoot = Path.Combine(AppContext.BaseDirectory, "bridge", bridgeRid);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            PrepareFakeBridgeBundle(bridgeRoot);

            var config = TransportRuntimeConfig.Select();

            Assert.Equal("NKN", config.Key);
            Assert.True(config.AutoSelected);
            Assert.False(config.ForcedByEnvironment);
            Assert.False(config.HasStartupWarning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            CleanupDirectoryIfExists(bridgeRoot);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseDefault_SelectsDevLocal_WithWarning_WhenBridgeMissing()
    {
#if DEBUG
        return;
#endif
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var bridgeRid = GetCurrentBridgeRidForTests();
        var bridgeRoot = Path.Combine(AppContext.BaseDirectory, "bridge", bridgeRid);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var config = TransportRuntimeConfig.Select();

            Assert.Equal("DevLocal", config.Key);
            Assert.False(config.AutoSelected);
            Assert.False(config.ForcedByEnvironment);
            Assert.True(config.HasStartupWarning);
            Assert.Contains("missing the bridge runtime", config.StartupWarningText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            CleanupDirectoryIfExists(bridgeRoot);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_EnvNkn_SelectsNkn_AndHelperFailsLoudlyBeforeConnect_WhenBridgeMissing()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "NKN");
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var config = TransportRuntimeConfig.Select();
            Assert.Equal("NKN", config.Key);
            Assert.True(config.ForcedByEnvironment);

            using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
                onJoinByAddressAsync: static (_, __) => throw new InvalidOperationException("bridge missing")));
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                config,
                runtime,
                openDiagnosticsAction: static () => { },
                approvalTimeout: TimeSpan.FromMilliseconds(100),
                connectFailureCooldown: TimeSpan.Zero);

            Assert.True(helper.IsStartupBlocked);
            Assert.Equal("Please reinstall.", helper.StatusText);
            Assert.False(helper.ConnectCommand.CanExecute(null));
            Assert.True(helper.ShowOpenDiagnosticsLink);
            Assert.Equal(SessionRuntimeState.Failed, runtime.State);

            using var scripted = new ScriptedSignalingTransport();
            SetPrivateField(runtime, "transport", scripted);
            InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);

            Assert.Equal("Please reinstall.", runtime.StatusText);
            Assert.Equal("Please reinstall.", helper.StatusText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_EnvDevLocal_SelectsDevLocal()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            Assert.Equal("DevLocal", config.Key);
            Assert.True(config.ForcedByEnvironment);
            Assert.False(config.AutoSelected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatKeyAgreement_ProducesSameSessionKey_OnBothSides()
    {
        using var a = ChatKeyAgreement.CreateKeyPair();
        using var b = ChatKeyAgreement.CreateKeyPair();

        var aKey = a.DeriveSharedKey(b.PublicKey);
        var bKey = b.DeriveSharedKey(a.PublicKey);

        Assert.Equal(32, aKey.Length);
        Assert.Equal(aKey, bKey);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatAesGcm_EncryptDecrypt_RoundTrip()
    {
        var key = SHA256LikeDeterministicBytes("test-key", 32);
        var nonce = SHA256LikeDeterministicBytes("test-nonce", ChatAesGcmCrypto.NonceSize);
        var plaintext = Encoding.UTF8.GetBytes("hello chat");

        var encrypted = ChatAesGcmCrypto.EncryptWithNonce(key, plaintext, nonce);
        var roundTrip = ChatAesGcmCrypto.Decrypt(key, encrypted.Nonce, encrypted.Tag, encrypted.Ciphertext);

        Assert.Equal(plaintext, roundTrip);
        Assert.Equal(ChatAesGcmCrypto.TagSize, encrypted.Tag.Length);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatEnvelope_SerializeDeserialize_IsStableAndVersioned()
    {
        var envelope = new ChatEnvelope
        {
            Version = ChatProtocol.Version,
            Type = ChatProtocol.ChatMessageType,
            NonceBase64 = "AQIDBAUGBwgJCgsM",
            TagBase64 = "AAAAAAAAAAAAAAAAAAAAAA==",
            CiphertextBase64 = "SGVsbG8=",
        };

        var bytes = ChatEnvelopeCodec.SerializeEnvelope(envelope);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            "{\"v\":1,\"t\":\"chat.message\",\"n\":\"AQIDBAUGBwgJCgsM\",\"g\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"c\":\"SGVsbG8=\"}",
            json);

        var parsed = ChatEnvelopeCodec.DeserializeEnvelope(bytes);
        Assert.Equal(ChatProtocol.Version, parsed.Version);
        Assert.Equal(ChatProtocol.ChatMessageType, parsed.Type);
        Assert.Equal(envelope.NonceBase64, parsed.NonceBase64);
        Assert.Equal(envelope.TagBase64, parsed.TagBase64);
        Assert.Equal(envelope.CiphertextBase64, parsed.CiphertextBase64);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task DevLocalTransport_HostJoin_RaisesJoinRequestApproveAndRejectEvents()
    {
        await VerifyHandshakeAsync(approve: true);
        await VerifyHandshakeAsync(approve: false);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_SecurityState_TracksVerifiedHandshake_BeforeApprovalGrant()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out var rawToken);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => helperRuntime.SecurityState.HandshakeState == SessionHandshakeState.Verified, TimeSpan.FromSeconds(2));

        Assert.Equal(SessionHandshakeState.Verified, helpeeRuntime.SecurityState.HandshakeState);
        Assert.True(helpeeRuntime.SecurityState.HandshakeCompleted);
        Assert.False(helpeeRuntime.SecurityState.ApprovalGranted);
        Assert.NotNull(helpeeRuntime.SecurityState.SessionId);
        Assert.NotNull(helpeeRuntime.SecurityState.HelperAddress);
        Assert.NotNull(helpeeRuntime.PendingApprovalRequest);
        Assert.Equal(helpeeRuntime.SecurityState.SessionId, helpeeRuntime.PendingApprovalRequest!.SessionId);
        Assert.Equal(helpeeRuntime.SecurityState.HelperAddress, helpeeRuntime.PendingApprovalRequest.HelperIdentity);
        Assert.Equal(
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl,
            helpeeRuntime.PendingApprovalRequest.RequestedCapabilities);
        Assert.False(helperRuntime.SecurityState.ApprovalGranted);

        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected,
            TimeSpan.FromSeconds(2));

        Assert.True(helpeeRuntime.SecurityState.ApprovalGranted);
        Assert.NotNull(helpeeRuntime.CurrentSessionGrant);
        Assert.NotNull(helperRuntime.CurrentSessionGrant);
        Assert.Equal(helpeeRuntime.CurrentSessionGrant!.Capabilities, helperRuntime.CurrentSessionGrant!.Capabilities);
        Assert.True(helpeeRuntime.IsCapabilityGranted(CapabilityGrant.Chat));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_ApproveAsync_UsesExplicitCapabilitySubset()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.ScreenShare, cts.Token);
        await WaitUntilAsync(
            () => helpeeRuntime.CurrentSessionGrant is not null &&
                  helperRuntime.CurrentSessionGrant is not null,
            TimeSpan.FromSeconds(2));

        Assert.Equal(
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare,
            helpeeRuntime.CurrentSessionGrant!.Capabilities);
        Assert.Equal(
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare,
            helperRuntime.CurrentSessionGrant!.Capabilities);
        Assert.True(helperRuntime.CanPerform(SessionCapability.Chat));
        Assert.True(helperRuntime.CanPerform(SessionCapability.ScreenShare));
        Assert.False(helperRuntime.CanPerform(SessionCapability.RemoteControl));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_ApprovalGrant_Expires_And_ClearsCapabilities()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(
            () => new DevLocalTransport(hostAddress),
            watchdogOptions: null,
            nowProvider: () => nowUtc);
        using var helperRuntime = new SessionRuntime(
            () => new DevLocalTransport(),
            watchdogOptions: null,
            nowProvider: () => nowUtc);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat | InviteCapabilities.RemoteControl);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(
            () => helpeeRuntime.CurrentSessionGrant is not null &&
                  helperRuntime.CurrentSessionGrant is not null,
            TimeSpan.FromSeconds(2));

        Assert.True(helperRuntime.IsCapabilityGranted(CapabilityGrant.Chat));
        Assert.True(helperRuntime.IsCapabilityGranted(CapabilityGrant.RemoteControl));

        nowUtc = nowUtc.Add(SessionSecurityDefaults.GrantLifetime).Add(TimeSpan.FromSeconds(1));

        Assert.False(helpeeRuntime.IsCapabilityGranted(CapabilityGrant.Chat));
        Assert.False(helperRuntime.IsCapabilityGranted(CapabilityGrant.Chat));
        Assert.Null(helpeeRuntime.CurrentSessionGrant);
        Assert.Null(helperRuntime.CurrentSessionGrant);
        Assert.False(helpeeRuntime.SecurityState.ApprovalGranted);
        Assert.False(helperRuntime.SecurityState.ApprovalGranted);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_RequestRemoteControl_RequiresGrantedCapability()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected,
            TimeSpan.FromSeconds(2));

        var requested = await helperRuntime.RequestRemoteControlAsync(cts.Token);

        Assert.False(helperRuntime.IsCapabilityGranted(CapabilityGrant.RemoteControl));
        Assert.False(requested);
        Assert.False(helpeeRuntime.HasPendingRemoteControlConsentPrompt);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_TrySendChatText_RequiresGrantedCapability()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        ChatMessageRecord? received = null;
        helpeeRuntime.ChatMessageReceived += (_, e) => received = e.Message;

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.ScreenShare);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected,
            TimeSpan.FromSeconds(2));

        var sent = await helperRuntime.TrySendChatTextAsync("blocked", cts.Token);
        await Task.Delay(150, cts.Token);

        Assert.False(helperRuntime.CanPerform(SessionCapability.Chat));
        Assert.Null(sent);
        Assert.Null(received);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_InboundChat_IsRejected_WhenChatCapabilityMissing()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        ChatMessageRecord? received = null;
        helpeeRuntime.ChatMessageReceived += (_, e) => received = e.Message;

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.ScreenShare);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected &&
                  helpeeRuntime.HasSessionKey &&
                  helperRuntime.HasSessionKey,
            TimeSpan.FromSeconds(2));

        var chatServiceField = typeof(SessionRuntime).GetField("chatService", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(chatServiceField);
        var helperChatService = Assert.IsType<SessionChatService>(chatServiceField!.GetValue(helperRuntime));

        var bypassSent = await helperChatService.TrySendTextAsync("bypass-chat", cts.Token);
        await Task.Delay(200, cts.Token);

        Assert.NotNull(bypassSent);
        Assert.False(helperRuntime.CanPerform(SessionCapability.Chat));
        Assert.False(helpeeRuntime.CanPerform(SessionCapability.Chat));
        Assert.Null(received);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionAuthorizationGuard_DeniesScreenShare_WhenHandshakeNotVerified()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_100_000_000);
        var sessionId = new SessionId("sess_guard_screenshare");
        var helpeeAddress = new PeerAddress("nlink.guard.helpee");
        var helperAddress = new PeerAddress("nlink.guard.helper");
        var securityState = SessionSecurityState.CreateHelperPending(
            sessionId,
            helpeeAddress,
            helperAddress,
            inviteValidated: true);
        var grant = new SessionGrant(
            helperAddress,
            CapabilityGrant.ScreenShare,
            sessionId,
            nowUtc.AddMinutes(5));
        var guard = new SessionAuthorizationGuard(() => nowUtc);

        var result = guard.Evaluate(
            hasSecurityTransport: true,
            securityState: securityState,
            grant: grant,
            capability: SessionCapability.ScreenShare);

        Assert.False(result.IsAuthorized);
        Assert.Equal(SessionAuthorizationFailure.HandshakeIncomplete, result.Failure);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_ScreenShareFrames_RequireGrantedCapability()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var frameRaised = false;
        helperRuntime.ScreenShareFrameCompleted += (_, _) => frameRaised = true;

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));

        InvokePrivateMethod(
            helperRuntime,
            "OnTransportScreenShareFrameCompleted",
            null,
            new ScreenShareFrameCompletedEventArgs(
                1,
                1,
                1,
                "jpeg",
                new byte[] { 0x01 },
                SessionId: helperRuntime.SecurityState.SessionId!.Value.Value));

        Assert.False(helperRuntime.CanPerform(SessionCapability.ScreenShare));
        Assert.False(frameRaised);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_ScreenView_WithoutApproval_IsRejected()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(
            CreateVerifiedSecurityState(
                new PeerAddress("screenview.noapproval.helpee"),
                new PeerAddress(scripted.LocalPeerAddress)));

        var frameRaised = false;
        runtime.ScreenShareFrameCompleted += (_, _) => frameRaised = true;

        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            scripted,
            new ScreenShareFrameCompletedEventArgs(
                1,
                1,
                1,
                "jpeg",
                new byte[] { 0x01 },
                SessionId: runtime.SecurityState.SessionId!.Value.Value));

        Assert.False(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.Null(runtime.CurrentSessionGrant);
        Assert.False(frameRaised);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_ScreenShareFrameDispatch_RejectsSessionIdMismatch()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");

        var approvedState = CreateApprovedSecurityState(
            new PeerAddress(scripted.LocalPeerAddress),
            new PeerAddress("scripted.helper.peer"),
            CapabilityGrant.ScreenShare);
        InvokePrivateMethod(runtime, "ApplyTransportSecurityState", approvedState);

        var frameRaised = false;
        runtime.ScreenShareFrameCompleted += (_, _) => frameRaised = true;

        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            scripted,
            new ScreenShareFrameCompletedEventArgs(
                1,
                1,
                1,
                "jpeg",
                new byte[] { 0x01 },
                SessionId: "sess_other_screenshare"));

        Assert.True(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.False(frameRaised);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_ScreenShareFrameDispatch_SuppressesLateFrameImmediatelyAfterStop()
    {
        using var scripted = new ScriptedSignalingTransport();
        var nowUtc = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        using var runtime = new SessionRuntime(
            () => scripted,
            watchdogOptions: null,
            nowProvider: () => nowUtc);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");

        var approvedState = CreateApprovedSecurityState(
            new PeerAddress(scripted.LocalPeerAddress),
            new PeerAddress("scripted.helper.peer"),
            CapabilityGrant.ScreenShare);
        InvokePrivateMethod(runtime, "ApplyTransportSecurityState", approvedState);

        var frameRaised = false;
        runtime.ScreenShareFrameCompleted += (_, _) => frameRaised = true;

        InvokePrivateMethod(runtime, "OnTransportScreenShareStopped", scripted, EventArgs.Empty);
        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            scripted,
            new ScreenShareFrameCompletedEventArgs(
                1,
                1,
                1,
                "jpeg",
                new byte[] { 0x01 },
                SessionId: runtime.SecurityState.SessionId!.Value.Value));

        Assert.False(frameRaised);

        nowUtc = nowUtc.AddSeconds(1);
        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            scripted,
            new ScreenShareFrameCompletedEventArgs(
                2,
                1,
                1,
                "jpeg",
                new byte[] { 0x02 },
                SessionId: runtime.SecurityState.SessionId!.Value.Value));

        Assert.True(frameRaised);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_UnauthorizedRemoteControlInjection_IsRejected()
    {
        var hostAddress = CreateTestPeerAddress();
        var injector = new CountingRemoteInputInjector();
        using var helpeeRuntime = new SessionRuntime(
            () => new DevLocalTransport(hostAddress),
            watchdogOptions: null,
            remoteInputInjector: injector);
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var received = false;
        helpeeRuntime.RemoteControlInputReceived += (_, _) => received = true;

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helperRuntime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));

        var handleTask = (Task?)InvokePrivateMethod(
            helpeeRuntime,
            "HandleRemoteControlInputReceivedAsync",
            new RemoteControlInputReceivedEventArgs(
                new ControlInputMessageV1
                {
                    SessionId = helperRuntime.CurrentSessionGrant!.SessionId.Value,
                    RequestId = "req_unauthorized_input",
                    Seq = 1,
                    Kind = "mouse_move",
                    Nx = 0.5,
                    Ny = 0.5,
                    DisplayId = "display-primary",
                    DisplayInfoRevision = 1,
                },
                "devlocal-peer"));
        Assert.NotNull(handleTask);
        await handleTask!;

        Assert.False(helpeeRuntime.CanPerform(SessionCapability.RemoteControl));
        Assert.False(received);
        Assert.Equal(0, injector.MouseMoveCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_RequestRemoteControl_WithoutApproval_IsRejected()
    {
        var sentRequests = 0;
        using var scripted = new ScriptedSignalingTransport(
            onSendControlRequestAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentRequests);
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(
            CreateVerifiedSecurityState(
                new PeerAddress("remotecontrol.noapproval.helpee"),
                new PeerAddress(scripted.LocalPeerAddress)));

        var requested = await runtime.RequestRemoteControlAsync();

        Assert.False(runtime.CanPerform(SessionCapability.RemoteControl));
        Assert.False(requested);
        Assert.Equal(ControlState.Off, runtime.ControlState);
        Assert.Equal(0, Volatile.Read(ref sentRequests));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_SendRemoteControlKeyInput_DoesNotRequireMappingMetadata()
    {
        ControlInputMessageV1? sentMessage = null;
        using var scripted = new ScriptedSignalingTransport(
            onSendControlInputAsync: (message, _) =>
            {
                sentMessage = message;
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        InvokePrivateMethod(
            runtime,
            "ApplyTransportSecurityState",
            CreateApprovedSecurityState(
                new PeerAddress("helper.keysend.helpee"),
                new PeerAddress(scripted.LocalPeerAddress),
                CapabilityGrant.RemoteControl));
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Active,
                "helper.keysend.helpee",
                "req_keyboard_send",
                null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));

        var sent = await runtime.SendRemoteControlInputAsync(
            new ControlInputMessageV1
            {
                Kind = "key",
                Action = "down",
                Key = "A",
            });

        Assert.True(sent);
        Assert.NotNull(sentMessage);
        Assert.Equal("req_keyboard_send", sentMessage!.RequestId);
        Assert.Equal("key", sentMessage.Kind);
        Assert.Null(sentMessage.DisplayId);
        Assert.Null(sentMessage.DisplayInfoRevision);
        Assert.Equal("A", sentMessage.Key);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_RemoteControlKeyInjection_DoesNotRequireDisplayMetadata()
    {
        var injector = new CountingRemoteInputInjector();
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(
            () => scripted,
            watchdogOptions: null,
            remoteInputInjector: injector);

        var helpeeAddress = new PeerAddress(scripted.LocalPeerAddress);
        var helperAddress = new PeerAddress("helper.keyinject.peer");

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        InvokePrivateMethod(
            runtime,
            "ApplyTransportSecurityState",
            CreateApprovedSecurityState(
                helpeeAddress,
                helperAddress,
                CapabilityGrant.RemoteControl));
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Active,
                helperAddress.Value,
                "req_keyboard_inject",
                null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));

        var handleTask = (Task?)InvokePrivateMethod(
            runtime,
            "HandleRemoteControlInputReceivedAsync",
            new RemoteControlInputReceivedEventArgs(
                new ControlInputMessageV1
                {
                    SessionId = runtime.SecurityState.SessionId!.Value.Value,
                    RequestId = "req_keyboard_inject",
                    Seq = 1,
                    Kind = "key",
                    Action = "down",
                    Key = "A",
                },
                helperAddress.Value));
        Assert.NotNull(handleTask);
        await handleTask!;

        await WaitUntilAsync(() => injector.KeyInjectionCount == 1, TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_RemoteControlDisplayInfoReceive_RequiresGrantedCapability()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("scripted.aux.helpee"),
                new PeerAddress(scripted.LocalPeerAddress),
                CapabilityGrant.Chat));

        var handleTask = (Task?)InvokePrivateMethod(
            runtime,
            "HandleRemoteControlDisplayInfoReceivedAsync",
            new RemoteControlDisplayInfoReceivedEventArgs(
                new ControlDisplayInfoMessageV1
                {
                    SessionId = runtime.SecurityState.SessionId!.Value.Value,
                    DisplayId = "display-primary",
                    VirtualDesktopX = 0,
                    VirtualDesktopY = 0,
                    VirtualDesktopWidth = 1920,
                    VirtualDesktopHeight = 1080,
                    CaptureRegionX = 0,
                    CaptureRegionY = 0,
                    CaptureRegionWidth = 1920,
                    CaptureRegionHeight = 1080,
                    FrameWidth = 1280,
                    FrameHeight = 720,
                    DpiScale = 1.0,
                    Revision = 1,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                "remote-helpee"));
        Assert.NotNull(handleTask);
        await handleTask!;

        Assert.False(runtime.CanPerform(SessionCapability.RemoteControl));
        Assert.False(runtime.RemoteControlMappingAvailable);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_RemoteControlAllowResponse_IsRejectedWithoutGrantedCapability()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("scripted.aux.helpee"),
                new PeerAddress(scripted.LocalPeerAddress),
                CapabilityGrant.Chat));
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Requesting,
                ControllerPeerId: "remote-helpee",
                CurrentControlRequestId: "req_capability_lost",
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));

        var handleTask = (Task?)InvokePrivateMethod(
            runtime,
            "HandleRemoteControlResponseReceivedAsync",
            new RemoteControlResponseReceivedEventArgs(
                new ControlResponseMessageV1
                {
                    RequestId = "req_capability_lost",
                    Decision = "allow",
                    ConsentToken = "consent-token",
                    TtlMs = 60_000,
                },
                "remote-helpee"));
        Assert.NotNull(handleTask);
        await handleTask!;

        Assert.False(runtime.CanPerform(SessionCapability.RemoteControl));
        Assert.Equal(ControlState.Off, runtime.ControlState);
        Assert.Null(runtime.ConsentToken);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_FileTransferSend_WithoutApproval_IsRejected()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(
            CreateVerifiedSecurityState(
                new PeerAddress("filetransfer.noapproval.helpee"),
                new PeerAddress(scripted.LocalPeerAddress)));

        var allowed = Assert.IsType<bool>(InvokePrivateMethod(runtime, "TryAuthorizeFileTransferSend")!);

        Assert.False(runtime.CanPerform(SessionCapability.FileTransfer));
        Assert.False(allowed);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_ExpiredApproval_BlocksPrivilegedOperations()
    {
        var sentRequests = 0;
        using var scripted = new ScriptedSignalingTransport(
            onSendControlRequestAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentRequests);
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(
            CreateExpiredApprovedSecurityState(
                new PeerAddress("expired.approval.helpee"),
                new PeerAddress(scripted.LocalPeerAddress),
                CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl | CapabilityGrant.FileTransfer));

        var frameRaised = false;
        runtime.ScreenShareFrameCompleted += (_, _) => frameRaised = true;

        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            scripted,
            new ScreenShareFrameCompletedEventArgs(
                1,
                1,
                1,
                "jpeg",
                new byte[] { 0x01 },
                SessionId: runtime.SecurityState.SessionId!.Value.Value));

        var requested = await runtime.RequestRemoteControlAsync();
        var fileTransferAllowed = Assert.IsType<bool>(InvokePrivateMethod(runtime, "TryAuthorizeFileTransferSend")!);

        Assert.False(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.False(runtime.CanPerform(SessionCapability.RemoteControl));
        Assert.False(runtime.CanPerform(SessionCapability.FileTransfer));
        Assert.Null(runtime.CurrentSessionGrant);
        Assert.False(runtime.SecurityState.ApprovalGranted);
        Assert.False(frameRaised);
        Assert.False(requested);
        Assert.False(fileTransferAllowed);
        Assert.Equal(0, Volatile.Read(ref sentRequests));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_StartHelperAsync_RequiresSecurityTransport()
    {
        using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => runtime.StartHelperAsync(new PeerAddress("nonsecurity.helper.target"), cts.Token));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task IncomingJoinRequest_ApproveAsync_RequiresExplicitDecision_WhenApprovalRequestPresent()
    {
        var approvalRequest = new ApprovalRequest(
            new PeerAddress("helper.security.test"),
            CapabilityGrant.Chat,
            new SessionId("approval_request_test"));
        var approved = false;

        var joinRequest = new IncomingJoinRequestEventArgs(
            approveAsync: (decision, _) =>
            {
                Assert.NotNull(decision);
                approved = true;
                return Task.CompletedTask;
            },
            rejectAsync: _ => Task.CompletedTask,
            approvalRequest: approvalRequest);

        await Assert.ThrowsAsync<InvalidOperationException>(() => joinRequest.ApproveAsync(CancellationToken.None));
        Assert.False(approved);
        Assert.False(joinRequest.IsHandled);

        var nowUtc = DateTimeOffset.UtcNow;
        var decision = approvalRequest.CreateDecision(
            CapabilityGrant.Chat,
            nowUtc.Add(SessionSecurityDefaults.GrantLifetime),
            nowUtc);

        await joinRequest.ApproveAsync(decision, CancellationToken.None);
        Assert.True(approved);
        Assert.True(joinRequest.IsHandled);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_FileTransferCommand_RequiresGrantedCapability()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));

        var transportConfig = CreateDevLocalTestConfig();
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        Assert.False(helperRuntime.CanPerform(SessionCapability.FileTransfer));
        Assert.False(helper.CanSendFiles);
        Assert.False(helper.SendFileCommand.CanExecute(null));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_FileTransferRequest_IsBlockedByRuntimeGuard_WhenUiFlagIsStale()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));

        var transportConfig = CreateDevLocalTestConfig();
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);
        var requested = false;
        helper.SendFileRequested += (_, _) => requested = true;

        SetPrivateField(helper, "canSendFiles", true);
        InvokePrivateMethod(helper, "RequestSendFileWindow");
        await Task.Delay(150, cts.Token);

        Assert.False(requested);
        Assert.False(helperRuntime.CanPerform(SessionCapability.FileTransfer));
        Assert.False(helper.CanSendFiles);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_FileTransferWriteOpen_RequiresGrantedCapability()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-smoke-" + Guid.NewGuid().ToString("N"));

        try
        {
            runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
            SetPrivateField(runtime, "transport", scripted);
            SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
            SetPrivateField(runtime, "statusText", "Connected");
            InvokePrivateMethod(runtime, "WireTransport", scripted);
            scripted.SetSessionSecurityStateForTests(
                CreateApprovedSecurityState(
                    new PeerAddress(scripted.LocalPeerAddress),
                    new PeerAddress("scripted.helper.peer"),
                    CapabilityGrant.Chat));

            var result = Assert.IsType<FileTransferWriteOpenResult>(
                InvokePrivateMethod(
                    runtime,
                    "OpenAuthorizedInboundFileWriteStream",
                    new FileTransferDescriptor(
                        runtime.SecurityState.SessionId!.Value,
                        runtime.SecurityState.HelperAddress!.Value,
                        "safe.txt",
                        128),
                    new FileTransferStoragePolicy(tempRoot))!);

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.AuthorizationDenied, result.Access.Failure);
            Assert.Equal(SessionAuthorizationFailure.CapabilityMissing, result.Access.AuthorizationFailure);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_AddressJoin_OnSecurityTransport_IsRejectedBeforeApprovalUi()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        await helperRuntime.StartHelperAsync(new PeerAddress(hostAddress), cts.Token);

        await WaitUntilAsync(() => helperRuntime.State == SessionRuntimeState.Rejected, TimeSpan.FromSeconds(2));
        await Task.Delay(150, cts.Token);

        Assert.NotEqual(SessionRuntimeState.IncomingJoinRequest, helpeeRuntime.State);
        Assert.False(helpeeRuntime.HasPendingJoinRequest);
        Assert.Equal(SessionRuntimeState.Rejected, helperRuntime.State);
        Assert.Equal("Permission was declined.", helperRuntime.StatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task DevLocalTransport_Chat_HelperToHelpee_And_HelpeeToHelper_RoundTrip()
    {
        ChatRuntimeCounters.ResetForTests();

        var hostAddress = CreateTestPeerAddress();
        using var hostTransport = new DevLocalTransport(hostAddress);
        using var helperTransport = new DevLocalTransport();
        using var helpeeChat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 18, 0, 0, TimeSpan.Zero));
        using var helperChat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 18, 0, 5, TimeSpan.Zero));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        helpeeChat.AttachTransport(hostTransport);
        helperChat.AttachTransport(helperTransport);

        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostTransport.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };

        var helpeeMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preApprovalNoticeRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        helpeeChat.MessageReceived += (_, e) => helpeeMessageTcs.TrySetResult(e.Message.Text);
        helpeeChat.MessageReceivedBeforeApproved += (_, _) => preApprovalNoticeRaised.TrySetResult();
        helperChat.MessageReceived += (_, e) => helperMessageTcs.TrySetResult(e.Message.Text);

        _ = hostTransport.HostByAddressAsync(cts.Token);
        await Task.Delay(75, cts.Token);
        var invite = CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out var rawToken, InviteCapabilities.Chat);
        await helperTransport.JoinByInviteAsync(rawToken, invite, cts.Token).WaitAsync(TimeSpan.FromSeconds(3));

        var helperSent = await helperChat.TrySendTextAsync("Hi, it's me", cts.Token);
        Assert.Null(helperSent);
        Assert.False(preApprovalNoticeRaised.Task.IsCompleted);

        await joinRaised.Task.WaitAsync(cts.Token);
        await pendingJoin!.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await WaitUntilAsync(() => helperChat.IsApproved && helpeeChat.IsApproved, TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => helpeeChat.HasSessionKey && helperChat.HasSessionKey, TimeSpan.FromSeconds(3));

        helperSent = await helperChat.TrySendTextAsync("Hi, it's me", cts.Token);
        Assert.NotNull(helperSent);
        var helpeeReceived = await helpeeMessageTcs.Task.WaitAsync(cts.Token);

        var helpeeSent = await helpeeChat.TrySendTextAsync("I can see your message", cts.Token);
        Assert.NotNull(helpeeSent);
        var helperReceived = await helperMessageTcs.Task.WaitAsync(cts.Token);

        Assert.Equal("Hi, it's me", helpeeReceived);
        Assert.Equal("I can see your message", helperReceived);

        var counters = ChatRuntimeCounters.Snapshot();
        Assert.True(counters.ChatSent >= 2);
        Assert.True(counters.ChatReceived >= 2);
        Assert.Equal(0, counters.ChatDecryptFailed);

        helperTransport.Dispose();
        hostTransport.Dispose();
        cts.Cancel();
        await Task.Delay(50, CancellationToken.None);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task DevLocalTransport_ChatSubscriberThrow_DoesNotDisconnectSession()
    {
        var hostAddress = CreateTestPeerAddress();
        using var hostTransport = new DevLocalTransport(hostAddress);
        using var helperTransport = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostDisconnected = 0;
        var helperDisconnected = 0;
        var receiveAttempts = 0;

        hostTransport.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        hostTransport.Approved += (_, _) => hostApproved.TrySetResult();
        helperTransport.Approved += (_, _) => helperApproved.TrySetResult();
        hostTransport.Disconnected += (_, _) => Interlocked.Increment(ref hostDisconnected);
        helperTransport.Disconnected += (_, _) => Interlocked.Increment(ref helperDisconnected);
        hostTransport.ChatMessageReceived += (_, e) =>
        {
            if (Interlocked.Increment(ref receiveAttempts) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            hostReceived.TrySetResult(Encoding.UTF8.GetString(e.Payload));
        };

        _ = hostTransport.HostByAddressAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out var rawToken, InviteCapabilities.Chat);
        await helperTransport.JoinByInviteAsync(rawToken, invite, cts.Token).WaitAsync(TimeSpan.FromSeconds(3));
        await joinRaised.Task.WaitAsync(cts.Token);

        await pendingJoin!.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await helperApproved.Task.WaitAsync(cts.Token);
        await hostApproved.Task.WaitAsync(cts.Token);

        await helperTransport.SendChatMessageAsync(Encoding.UTF8.GetBytes("first"), cts.Token);
        await helperTransport.SendChatMessageAsync(Encoding.UTF8.GetBytes("second"), cts.Token);

        var received = await hostReceived.Task.WaitAsync(cts.Token);
        Assert.Equal("second", received);
        Assert.Equal(2, Volatile.Read(ref receiveAttempts));
        Assert.Equal(0, Volatile.Read(ref hostDisconnected));
        Assert.Equal(0, Volatile.Read(ref helperDisconnected));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionChatService_ValidReceivedPayload_IncrementsChatReceived()
    {
        ChatRuntimeCounters.ResetForTests();

        using var transport = new FakeSignalingTransport();
        using var chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 0, 0, TimeSpan.Zero));

        var receivedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.MessageReceived += (_, e) => receivedText.TrySetResult(e.Message.Text);

        chat.AttachTransport(transport);

        var key = SHA256LikeDeterministicBytes("chat-key-valid", 32);
        transport.RaiseSessionKeyReady(key);

        var payloadBytes = CreateEncryptedChatEnvelopeBytes(
            key,
            messageId: "msg-valid-1",
            text: "hello from helper",
            timestampUnixMs: new DateTimeOffset(2026, 2, 23, 19, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            nonceSeed: "nonce-valid-1");

        transport.RaiseChatMessage(payloadBytes);

        var text = await receivedText.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("hello from helper", text);

        var counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1, counters.ChatReceived);
        Assert.Equal(0, counters.ChatDecryptFailed);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionChatService_InvalidEncryptedPayload_IncrementsDecryptFailed()
    {
        ChatRuntimeCounters.ResetForTests();

        using var transport = new FakeSignalingTransport();
        using var chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 5, 0, TimeSpan.Zero));

        chat.AttachTransport(transport);

        var key = SHA256LikeDeterministicBytes("chat-key-invalid", 32);
        transport.RaiseSessionKeyReady(key);

        var payloadBytes = CreateEncryptedChatEnvelopeBytes(
            key,
            messageId: "msg-invalid-1",
            text: "hello",
            timestampUnixMs: new DateTimeOffset(2026, 2, 23, 19, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            nonceSeed: "nonce-invalid-1");

        var envelope = ChatEnvelopeCodec.DeserializeEnvelope(payloadBytes);
        var tagBytes = Convert.FromBase64String(envelope.TagBase64);
        tagBytes[0] ^= 0xFF;

        var tamperedBytes = ChatEnvelopeCodec.SerializeEnvelope(
            new ChatEnvelope
            {
                Version = envelope.Version,
                Type = envelope.Type,
                NonceBase64 = envelope.NonceBase64,
                TagBase64 = Convert.ToBase64String(tagBytes),
                CiphertextBase64 = envelope.CiphertextBase64,
            });

        transport.RaiseChatMessage(tamperedBytes);

        var counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1, counters.ChatReceived);
        Assert.Equal(1, counters.ChatDecryptFailed);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ScreenShareAndChat_Coexist_WithoutStarvingChatProcessing()
    {
        ChatRuntimeCounters.ResetForTests();

        var network = new FakeSessionTransportNetwork();
        using var hostTransport = network.CreateTransport("helpee");
        using var helperTransport = network.CreateTransport("helper");
        using var helpeeChat = new SessionChatService(() => new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));
        using var helperChat = new SessionChatService(() => new DateTimeOffset(2026, 3, 3, 18, 0, 5, TimeSpan.Zero));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var screenShareClock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));
        var reassembler = new ScreenShareFrameReassembler();
        var receivedChatTexts = new ConcurrentQueue<string>();
        var allChatReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedFrameCount = 0;

        helpeeChat.AttachTransport(hostTransport);
        helperChat.AttachTransport(helperTransport);
        helpeeChat.MessageReceived += (_, e) =>
        {
            receivedChatTexts.Enqueue(e.Message.Text);
            if (receivedChatTexts.Count >= 50)
            {
                allChatReceived.TrySetResult();
            }
        };
        reassembler.FrameReady += (_, _) => Interlocked.Increment(ref completedFrameCount);

        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostTransport.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };

        var hostAddress = hostTransport.LocalPeerAddress;
        _ = hostTransport.HostByAddressAsync(cts.Token);
        await helperTransport.JoinByAddressAsync(hostAddress, cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
        await joinRaised.Task.WaitAsync(cts.Token);
        await pendingJoin!.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await WaitUntilAsync(() => helperChat.IsApproved && helpeeChat.IsApproved, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => helperChat.HasSessionKey && helpeeChat.HasSessionKey, TimeSpan.FromSeconds(2));

        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendChunkAsync: async (chunk, _) =>
            {
                reassembler.OnChunk(chunk);
                await Task.Yield();
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: screenShareClock);

        var chatTask = Task.WhenAll(
            Enumerable.Range(0, 50).Select(i => helperChat.TrySendTextAsync($"chat-{i:D2}", cts.Token)));
        var screenShareTask = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                screenShareClock.Advance(TimeSpan.FromMilliseconds(125));
                await pipeline.EnqueueFrameAsync(
                    sessionId: "session-chat-share",
                    width: 640 + i,
                    height: 360 + i,
                    encoding: "jpeg",
                    encodedFrameBytes: new byte[] { (byte)(i + 1), (byte)(i + 2), (byte)(i + 3) },
                    timestampUnixMilliseconds: 1000 + i,
                    cancellationToken: cts.Token);
                await Task.Yield();
            }
        }, cts.Token);

        await Task.WhenAll(chatTask, screenShareTask);
        var chatRecords = await chatTask;
        await allChatReceived.Task.WaitAsync(cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref completedFrameCount) >= 1, TimeSpan.FromSeconds(2));

        Assert.Equal(50, receivedChatTexts.Count);
        Assert.True(Volatile.Read(ref completedFrameCount) >= 1);
        Assert.DoesNotContain(chatRecords, record => record is null);

        var senderMetrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(50, ChatRuntimeCounters.Snapshot().ChatReceived);
        Assert.Equal(5, senderMetrics.FramesCaptured);
        Assert.True(senderMetrics.FramesQueued >= 1);
        Assert.True(senderMetrics.ChunksSent >= 1);
        Assert.True(reassembler.GetMetricsSnapshot().FramesCompleted >= 1);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ViewModelFlow_HelpeeApproves_HelperAndHelpeeReachConnectedState()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-viewmodel-flow-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-viewmodel-flow-" + Guid.NewGuid().ToString("N")));

        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        _ = await WaitForShareInviteAsync(helpee);

        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);

        await connectTask;

        await WaitUntilAsync(
            () => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected",
            TimeSpan.FromSeconds(5));

        Assert.Equal("Connected", helpee.ConnectionState);
        Assert.Equal("Connected", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_HeaderStatusText_UsesStatusTextOrReady_AndIsNeverEmpty()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "connectionState", "Idle");
        SetPrivateField(helper, "statusText", "Waiting for code");
        Assert.Equal("Waiting for code", helper.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));

        SetPrivateField(helper, "statusText", string.Empty);
        Assert.Equal("Ready", helper.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Recovering);
        Assert.Equal("Reconnecting… • Viewing screen", helper.HeaderStatusText);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helper, "failureTitle", "Connection failed");
        Assert.Equal("Connection failed", helper.HeaderStatusText);

        SetPrivateField(helper, "failureTitle", string.Empty);
        SetPrivateField(helper, "statusText", "The other person ended the session.");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Ended);
        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_TransientStatusPanel_HidesWhenItDuplicatesHeader()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connecting);
        SetPrivateField(helper, "showTransientBanner", true);
        SetPrivateField(helper, "transientBannerText", "Connecting…");

        Assert.False(helper.ShowTransientStatusPanel);

        SetPrivateField(helper, "transientBannerText", "Trying bridge fallback");
        Assert.True(helper.ShowTransientStatusPanel);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_RemoteEnd_UsesSessionEndedCopy_NotConnectionFailed()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "wasConnected", true);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "statusText", "The other person ended the session.");
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.Equal("The other person ended the session.", helper.StatusText);
        Assert.Equal("Idle", helper.ConnectionState);
        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
        Assert.False(helper.ShowFailurePanel);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_OnDisconnected_GenericDisconnect_DoesNotLeaveConnectedStateStuck()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "statusText", "Connection lost.");
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(
            helper,
            "OnScreenShareViewerPropertyChanged",
            helper.ScreenShareViewer,
            new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));

        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);

        InvokePrivateMethod(helper, "OnDisconnected", helperRuntime, EventArgs.Empty);

        await WaitUntilAsync(
            () => string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        Assert.NotEqual("Connected", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_OnDisconnected_RemoteEnd_ClearsActiveSessionAffordancesImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper, "wasConnected", true);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);
        SetPrivateField(
            helperRuntime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Active,
                "peer",
                "req-1",
                null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(
            helper,
            "OnScreenShareViewerPropertyChanged",
            helper.ScreenShareViewer,
            new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        InvokePrivateMethod(helper, "OnRemoteControlStateChanged", helperRuntime, EventArgs.Empty);

        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.True(helper.ShowStopControlAction);
        Assert.True(helper.ShowRemoteControlActiveStatus);

        InvokePrivateMethod(helper, "OnDisconnected", helperRuntime, EventArgs.Empty);

        await WaitUntilAsync(
            () => helper.EffectivePhase == SessionUiPhase.Ended &&
                  string.Equals(helper.ConnectionState, "Idle", StringComparison.Ordinal) &&
                  !helper.ShowRemoteScreenShareFrame &&
                  !helper.ShowStopControlAction &&
                  !helper.ShowRemoteControlActiveStatus,
            TimeSpan.FromSeconds(5));

        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
        Assert.False(helper.IsChatInputEnabled);
        Assert.False(helper.ShowFailurePanel);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionUxPhaseMapper_SessionEndedBannerStatus_MapsToEndedPhase()
    {
        var status = new UserFacingStatus(
            UserStatusKind.Failed,
            "Session ended",
            "The other person ended the session.",
            FailureSeverity.Error);

        Assert.Equal(SessionUiPhase.Ended, SessionUxPhaseMapper.FromBannerStatus(status));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_HeaderStatusText_UsesConnectionStatusOrReady_AndIsNeverEmpty()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);

        SetPrivateField(helpee, "connectionState", "Waiting");
        SetPrivateField(helpee, "connectionStatus", "Waiting for helper…");
        Assert.Equal("Waiting for helper…", helpee.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helpee.HeaderStatusText));

        SetPrivateField(helpee, "connectionStatus", string.Empty);
        Assert.Equal("Ready", helpee.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helpee.HeaderStatusText));

        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", false);
        Assert.Equal("Connected", helpee.HeaderStatusText);

        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        Assert.Equal("Waiting for your approval… • Screen sharing", helpee.HeaderStatusText);
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", false);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", false);

        SetPrivateField(
            helpeeRuntime,
            "remoteControlSessionState",
            RemoteControlSessionState.Default with { ControlState = ControlState.Active });
        Assert.Equal("Waiting for fresh mapping", helpee.HeaderStatusText);

        SetPrivateField(
            helpeeRuntime,
            "remoteControlSessionState",
            RemoteControlSessionState.Default);
        SetPrivateField(helpeeRuntime, "remoteControlStatusHintText", "Authorization expired or revoked");
        Assert.Equal("Authorization expired or revoked", helpee.HeaderStatusText);
        SetPrivateField(helpeeRuntime, "remoteControlStatusHintText", string.Empty);

        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        Assert.Equal("Connected • Screen sharing", helpee.HeaderStatusText);

        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connecting);
        Assert.Equal("Connecting… • Screen sharing", helpee.HeaderStatusText);

        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helpee, "failureTitle", "Connection failed");
        Assert.Equal("Connection failed", helpee.HeaderStatusText);

        SetPrivateField(helpee, "failureTitle", string.Empty);
        SetPrivateField(helpee, "connectionStatus", "The helper ended the session.");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Ended);
        Assert.Equal("The helper ended the session.", helpee.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_RemoteControlAffordances_ClearImmediately_WhenBackendLeavesConnected()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);
        var helperIdentity = new PeerAddress("helper-affordance-test");
        var sessionId = new SessionId("helper-affordance-session");
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);

        SetPrivateProperty(helper, "ConnectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "transportState", TransportState.Connected);
        SetPrivateField(helperRuntime, "transport", new DevLocalTransport());
        SetPrivateField(
            helperRuntime,
            "sessionSecurityState",
            SessionSecurityState.Empty with
            {
                SessionId = sessionId,
                HelperAddress = helperIdentity,
                ApprovalGranted = true,
                HandshakeCompleted = true,
                HandshakeState = SessionHandshakeState.Verified,
                InviteValidated = true,
                ApprovedCapabilities = CapabilityGrant.RemoteControl | CapabilityGrant.ScreenShare,
                ApprovalExpiresAt = expiresAtUtc,
            });
        SetPrivateField(
            helperRuntime,
            "currentSessionGrant",
            new SessionGrant(helperIdentity, CapabilityGrant.RemoteControl | CapabilityGrant.ScreenShare, sessionId, expiresAtUtc));
        SetPrivateField(
            helperRuntime,
            "remoteControlSessionState",
            RemoteControlSessionState.Default with
            {
                ControlState = ControlState.Off,
                SupportsRemoteControl = true,
                PeerSupportsRemoteControl = true,
            });
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));

        Assert.True(helper.ShowRequestControlAction);
        Assert.True(helper.CanRequestControl);

        SetPrivateField(
            helperRuntime,
            "remoteControlSessionState",
            RemoteControlSessionState.Default with
            {
                ControlState = ControlState.Active,
                SupportsRemoteControl = true,
                PeerSupportsRemoteControl = true,
            });
        Assert.True(helper.ShowStopControlAction);
        Assert.True(helper.ShowRemoteControlActiveStatus);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);

        Assert.False(helper.ShowRequestControlAction);
        Assert.False(helper.CanRequestControl);
        Assert.False(helper.ShowStopControlAction);
        Assert.False(helper.CanStopControl);
        Assert.False(helper.ShowRemoteControlActiveStatus);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_KeyboardToggle_RemainsAvailable_WhenMappingIsUnavailable()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport();
        using var helperRuntime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        var focusRequested = false;
        helper.RemoteControlViewerFocusRequested += (_, _) => focusRequested = true;

        SetPrivateProperty(helper, "ConnectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        helperRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(helperRuntime, "transport", scripted);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "transportState", TransportState.Connected);
        SetPrivateField(helperRuntime, "statusText", "Connected");
        InvokePrivateMethod(helperRuntime, "WireTransport", scripted);
        InvokePrivateMethod(
            helperRuntime,
            "ApplyTransportSecurityState",
            CreateApprovedSecurityState(
                new PeerAddress("helper.keyboardtoggle.helpee"),
                new PeerAddress(scripted.LocalPeerAddress),
                CapabilityGrant.RemoteControl | CapabilityGrant.ScreenShare));
        SetPrivateField(
            helperRuntime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Active,
                "helper.keyboardtoggle.helpee",
                "req_keyboard_toggle",
                null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(
            helper,
            "OnScreenShareViewerPropertyChanged",
            helper.ScreenShareViewer,
            new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));

        Assert.False(helper.RemoteControlMappingAvailable);
        Assert.True(helper.ShowRemoteControlActiveStatus);
        Assert.True(helper.ShowControlModeToggle);
        Assert.True(helper.CanControlModeToggle);
        Assert.False(helper.IsRemoteControlInputCaptureEnabled);
        Assert.False(helper.IsRemoteControlKeyboardCaptureEnabled);

        helper.ToggleControlModeCommand.Execute(null);

        Assert.True(helper.IsRemoteControlKeyboardCaptureEnabled);
        Assert.Equal("Keyboard to remote: On", helper.ControlModeButtonText);
        Assert.True(focusRequested);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_RemoteControlAffordances_ClearImmediately_WhenBackendLeavesConnected()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);

        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));

        Assert.True(helpee.ShowRemoteControlConsentDialog);

        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", false);
        SetPrivateField(
            helpeeRuntime,
            "remoteControlSessionState",
            RemoteControlSessionState.Default with
            {
                ControlState = ControlState.Active,
                SupportsRemoteControl = true,
                PeerSupportsRemoteControl = true,
            });
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));

        Assert.True(helpee.ShowStopControlAction);
        Assert.True(helpee.ShowRemoteControlActiveStatus);
        Assert.True(helpee.ShowRemoteControlPreviewActiveCue);

        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);

        Assert.False(helpee.ShowRemoteControlConsentDialog);
        Assert.False(helpee.ShowStopControlAction);
        Assert.False(helpee.CanStopControl);
        Assert.False(helpee.ShowRemoteControlActiveStatus);
        Assert.False(helpee.ShowRemoteControlPreviewActiveCue);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_ToggleScreenSharePreviewCommand_CanExecute_FollowsPreviewState()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));

        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(
            runtime,
            "ApplyTransportSecurityState",
            CreateApprovedSecurityState(
                new PeerAddress(transport.LocalPeerAddress),
                new PeerAddress("helpee.screenshare.helper"),
                CapabilityGrant.ScreenShare));

        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));

        SetPrivateProperty(
            helpee,
            "ScreenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Starting, null, DateTimeOffset.UtcNow));
        Assert.False(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));

        SetPrivateProperty(
            helpee,
            "ScreenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));

        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", false);
        SetPrivateProperty(
            helpee,
            "ScreenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow));
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_StopScreenShareWhileConsentPending_ClearsApprovalUiImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));

        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Requesting,
                ControllerPeerId: "helper-peer",
                CurrentControlRequestId: "req-preview-stop",
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
        SetPrivateField(runtime, "hasPendingRemoteControlConsentPrompt", true);
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);

        Assert.True(helpee.ShowRemoteControlConsentDialog);
        Assert.Equal("Waiting for your approval… • Screen sharing", helpee.HeaderStatusText);

        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", false);

        Assert.False(helpee.ShowRemoteControlConsentDialog);
        Assert.Equal("Connected", helpee.HeaderStatusText);

        await WaitUntilAsync(
            () => runtime.ControlState == ControlState.Off &&
                  !runtime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(1));

        await WaitUntilAsync(
            () => !helpee.ShowRemoteControlConsentDialog &&
                  helpee.HeaderStatusText == "Connected",
            TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_StopScreenShareWhileControlActive_ClearsActiveUiImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));

        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Active,
                ControllerPeerId: "helper-peer",
                CurrentControlRequestId: "req-preview-stop-active",
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);

        Assert.True(helpee.ShowRemoteControlActiveStatus);
        Assert.True(helpee.ShowStopControlAction);
        Assert.Contains("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);

        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", false);

        Assert.False(helpee.ShowRemoteControlActiveStatus);
        Assert.False(helpee.ShowStopControlAction);
        Assert.Equal("Connected", helpee.HeaderStatusText);

        await WaitUntilAsync(
            () => runtime.ControlState == ControlState.Off,
            TimeSpan.FromSeconds(1));

        await WaitUntilAsync(
            () => !helpee.ShowRemoteControlActiveStatus &&
                  !helpee.ShowStopControlAction &&
                  helpee.HeaderStatusText == "Connected",
            TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_ScreenShareStartFailure_ShowsHeaderStatus_AndRemainsInactive()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        var failingSource = new FakeScreenCaptureSource
        {
            StartException = new InvalidOperationException("capture init failed"),
        };
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(failingSource));

        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(
            runtime,
            "ApplyTransportSecurityState",
            CreateApprovedSecurityState(
                new PeerAddress(transport.LocalPeerAddress),
                new PeerAddress("helpee.screenshare.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);

        helpee.ToggleScreenSharePreviewCommand.Execute(null);

        await WaitUntilAsync(
            () => helpee.ScreenSharePreviewStatus.State == ScreenShareState.Failed,
            TimeSpan.FromSeconds(2));

        Assert.False(helpee.IsScreenSharingPreviewActive);
        Assert.False(helpee.ShowScreenSharePreviewFrame);
        Assert.True(helpee.ShowScreenShareViewerError);
        Assert.Equal("Screen sharing failed to start", helpee.ScreenShareViewerMessage);
        Assert.Equal("Connected • Screen sharing failed to start", helpee.HeaderStatusText);
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_TransientStatusPanel_HidesWhenItDuplicatesHeader()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);

        SetPrivateField(helpee, "connectionStatus", "Waiting for helper…");
        SetPrivateField(helpee, "showTransientBanner", true);
        SetPrivateField(helpee, "transientBannerText", "Waiting for helper…");

        Assert.False(helpee.ShowTransientStatusPanel);

        SetPrivateField(helpee, "transientBannerText", "Recovering session");
        Assert.True(helpee.ShowTransientStatusPanel);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_PreviewFrames_DoNotReRaiseShellVisibility_WhenVisibilityStaysTrue()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        var visibilityChangedCount = 0;

        helpee.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HelpeePageViewModel.ShowScreenSharePreviewFrame))
            {
                visibilityChangedCount++;
            }
        };

        SetPrivateField(
            helpee,
            "screenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));

        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(2, 1));

        Assert.True(helpee.ShowScreenSharePreviewFrame);
        Assert.Equal(1, visibilityChangedCount);

        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", null);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_RemoteFrames_DoNotReRaiseShellVisibility_WhenVisibilityStaysTrue()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);
        var visibilityChangedCount = 0;

        helper.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HelperPageViewModel.ShowRemoteScreenShareFrame))
            {
                visibilityChangedCount++;
            }
        };

        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));

        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(2, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));

        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.Equal(1, visibilityChangedCount);

        SetPrivateField(helper.ScreenShareViewer, "currentFrame", null);
        SetPrivateField(helper.ScreenShareViewer, "isActive", false);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_ScreenShareStopped_ClearsRemoteViewer()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));

        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);

        InvokePrivateMethod(helper, "OnScreenShareStopped", helperRuntime, EventArgs.Empty);

        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.Null(helper.RemoteScreenShareFrame);
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_RequestPendingWithoutVisibleScreen_DoesNotKeepWaitingApprovalHeader()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(
            helperRuntime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Requesting,
                ControllerPeerId: "helpee-peer",
                CurrentControlRequestId: "req-no-screen",
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));

        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_ScreenShareDecodeError_ShowsClearViewerMessage_AndClearsOnStop()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", null);
        SetPrivateField(helper.ScreenShareViewer, "statusText", "Invalid frame received");
        InvokePrivateMethod(
            helper,
            "OnScreenShareViewerPropertyChanged",
            helper.ScreenShareViewer,
            new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.StatusText)));

        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.True(helper.ShowScreenShareViewerError);
        Assert.Equal(
            "Screen sharing is active, but the latest frame could not be displayed.",
            helper.ScreenShareViewerMessage);
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);

        InvokePrivateMethod(helper, "OnScreenShareStopped", helperRuntime, EventArgs.Empty);

        Assert.False(helper.ShowScreenShareViewerError);
        Assert.Equal(string.Empty, helper.ScreenShareViewerMessage);
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_BlankScreenSharePlaceholder_DoesNotAppendViewingSuffix()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", null);
        SetPrivateField(helper.ScreenShareViewer, "statusText", string.Empty);
        InvokePrivateMethod(
            helper,
            "OnScreenShareViewerPropertyChanged",
            helper.ScreenShareViewer,
            new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.StatusText)));

        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.False(helper.ShowScreenShareViewerError);
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_CanEndSession_IsTrueOnlyForConnectedConnectingOrRecoveringPhases()
    {
        Assert.True(InvokeCanEndForPhase(typeof(HelperPageViewModel), SessionUiPhase.Connected));
        Assert.False(InvokeCanEndForPhase(typeof(HelperPageViewModel), SessionUiPhase.Failed));
        Assert.False(InvokeCanEndForPhase(typeof(HelperPageViewModel), SessionUiPhase.Ended));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_CanEndSession_IsTrueOnlyForConnectedConnectingOrRecoveringPhases()
    {
        Assert.True(InvokeCanEndForPhase(typeof(HelpeePageViewModel), SessionUiPhase.Connected));
        Assert.False(InvokeCanEndForPhase(typeof(HelpeePageViewModel), SessionUiPhase.Failed));
        Assert.False(InvokeCanEndForPhase(typeof(HelpeePageViewModel), SessionUiPhase.Ended));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatView_DoesNotShowTopBar_WhenSessionHeaderIsEnabled()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        Assert.False(helper.ShowChatTopBar);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Beta5_HeaderState_RemainsAuthoritative_ForConnectedChat()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-beta5-pill-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-beta5-pill-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => helper.EffectivePhase == SessionUiPhase.Connected &&
                  helper.IsChatInputEnabled,
            TimeSpan.FromSeconds(5));

        Assert.StartsWith("Connected", helper.HeaderStatusText);
        Assert.True(helper.IsChatInputEnabled);

        await helperRuntime.DisconnectAsync();

        await WaitUntilAsync(
            () => (helper.EffectivePhase is SessionUiPhase.Failed or SessionUiPhase.Idle or SessionUiPhase.Waiting or SessionUiPhase.Ended) &&
                  !helper.IsChatInputEnabled,
            TimeSpan.FromSeconds(5));

        Assert.False(helper.IsChatInputEnabled);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Beta5_EndSession_DisablesChat_And_Command_Helper()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-beta5-end-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-beta5-end-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => helper.EffectivePhase == SessionUiPhase.Connected &&
                  helper.IsChatInputEnabled &&
                  helper.CanEndSession,
            TimeSpan.FromSeconds(5));

        helper.CodeInput = "stale-invite-should-clear";
        Assert.True(helper.IsChatInputEnabled);
        Assert.True(helper.CanEndSession);

        helper.EndSessionCommand.Execute(null);

        Assert.False(helper.IsChatInputEnabled);
        Assert.False(helper.CanEndSession);
        Assert.Equal(string.Empty, helper.CodeInput);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_EndSession_DeactivatesViewer_AndResetsSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-end-session-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-end-session-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(
            cancelAction: () => _ = helpeeRuntime.DisconnectAsync(),
            transportConfig,
            helpeeRuntime);
        using var helper = new HelperPageViewModel(
            cancelAction: () => _ = helperRuntime.DisconnectAsync(),
            transportConfig,
            helperRuntime);

        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => helper.EffectivePhase == SessionUiPhase.Connected &&
                  helper.IsChatInputEnabled &&
                  helper.CanEndSession,
            TimeSpan.FromSeconds(5));

        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        Assert.True(helper.ScreenShareViewer.IsActive);

        helper.EndSessionCommand.Execute(null);

        await WaitUntilAsync(
            () => !helper.ScreenShareViewer.IsActive &&
                  !helper.IsChatInputEnabled &&
                  helperRuntime.State == SessionRuntimeState.Idle,
            TimeSpan.FromSeconds(5));

        Assert.False(helper.CanEndSession);
        Assert.False(helper.ShowRemoteScreenShareFrame);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndSession_StopsPreviewCapture_AndClearsPreviewState()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var fakeSource = new FakeScreenCaptureSource();
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));
        using var previewCts = new CancellationTokenSource();

        var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coordinatorField);
        var coordinator = coordinatorField!.GetValue(helpee);
        Assert.NotNull(coordinator);

        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "wasConnected", true);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateField(
            helpee,
            "screenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);

        Assert.Equal("Connected • Screen sharing", helpee.HeaderStatusText);

        helpee.EndSessionCommand.Execute(null);

        await WaitUntilAsync(
            () => !helpee.IsScreenSharingPreviewActive &&
                  helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off,
            TimeSpan.FromSeconds(2));

        Assert.True(fakeSource.StopCallCount >= 1);
        Assert.True(fakeSource.DisposeCallCount >= 1);
        Assert.False(fakeSource.IsStarted);
        Assert.False(helpee.CanEndSession);
        Assert.DoesNotContain("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndSession_DoesNotBlockUi_WhenPreviewStopIsSlow()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var fakeSource = new FakeScreenCaptureSource
        {
            StopBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));
        using var previewCts = new CancellationTokenSource();

        var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coordinatorField);
        var coordinator = coordinatorField!.GetValue(helpee);
        Assert.NotNull(coordinator);

        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "wasConnected", true);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateField(
            helpee,
            "screenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);

        Assert.Equal("Connected • Screen sharing", helpee.HeaderStatusText);

        var stopwatch = Stopwatch.StartNew();
        helpee.EndSessionCommand.Execute(null);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"EndSession command blocked for {stopwatch.Elapsed}.");

        fakeSource.StopBlocker.TrySetResult(true);

        await WaitUntilAsync(
            () => !helpee.IsScreenSharingPreviewActive &&
                  helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off,
            TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task UnsentChatDraft_DoesNotLeakIntoNextSession_AfterEndAndRestart()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        helper.ChatDraft = "unsent helper draft";
        helpee.ChatDraft = "unsent helpee draft";
        helper.ChatMessages.Add(new ChatLineViewModel { Text = "old helper message", IsLocal = true });
        helpee.ChatMessages.Add(new ChatLineViewModel { Text = "old helpee message", IsLocal = true });
        SetPrivateField(helper, "endSessionRequested", true);
        SetPrivateField(helper, "endReason", Enum.Parse(typeof(SessionEndReason), "UserEnded"));
        SetPrivateField(helpee, "endSessionRequested", true);
        SetPrivateField(helpee, "endReason", Enum.Parse(typeof(SessionEndReason), "UserEnded"));

        InvokePrivateMethod(helper, "PrepareForNewSession", true);
        InvokePrivateMethod(helpee, "PrepareForNewSession");

        Assert.Equal(string.Empty, helper.ChatDraft);
        Assert.Equal(string.Empty, helpee.ChatDraft);
        Assert.Empty(helper.ChatMessages);
        Assert.Empty(helpee.ChatMessages);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_Dispose_StopsPreviewCapture_BeforeReturning()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var fakeSource = new FakeScreenCaptureSource();
        using var previewCts = new CancellationTokenSource();
        var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));

        var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coordinatorField);
        var coordinator = coordinatorField!.GetValue(helpee);
        Assert.NotNull(coordinator);
        var disposeCountBeforeDispose = fakeSource.DisposeCallCount;
        var stopCountBeforeDispose = fakeSource.StopCallCount;

        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateField(
            helpee,
            "screenSharePreviewStatus",
            new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);

        helpee.Dispose();

        Assert.True(previewCts.IsCancellationRequested);
        Assert.Equal(stopCountBeforeDispose + 1, fakeSource.StopCallCount);
        Assert.Equal(disposeCountBeforeDispose + 1, fakeSource.DisposeCallCount);
        Assert.False(fakeSource.IsStarted);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_SendChatFailure_RestoresDraft_AndKeepsSessionConnected()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(
            onSendChatAsync: static (_, _) => throw new TimeoutException("Ack was not received."));
        using var runtime = new SessionRuntime(() => scripted);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, runtime);

        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");

        var chatServiceField = typeof(SessionRuntime).GetField("chatService", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(chatServiceField);
        var chatService = chatServiceField!.GetValue(runtime);
        Assert.NotNull(chatService);
        SetPrivateFieldDynamic(chatService!, "transport", scripted);
        SetPrivateFieldDynamic(chatService!, "sessionKey", Enumerable.Repeat((byte)7, 32).ToArray());
        SetPrivateFieldDynamic(chatService!, "isApproved", true);

        SetPrivateField(helpee, "isChatInputEnabled", true);
        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "connectionStatus", "Connected");
        helpee.ChatDraft = "hello during screenshare";

        var sendTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "SendChatAsync"));
        await sendTask;

        Assert.Equal("hello during screenshare", helpee.ChatDraft);
        Assert.Empty(helpee.ChatMessages);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_FailedPhase_DisablesChatInput()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        var uiStateStore = new SessionUiStateStore();
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime, uiStateStore: uiStateStore);

        uiStateStore.SetPhase(SessionUiPhase.Failed, "test");
        await WaitUntilAsync(() => !helper.IsChatInputEnabled, TimeSpan.FromSeconds(1));
        Assert.False(helper.IsChatInputEnabled);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndedPhase_DisablesEndSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        var uiStateStore = new SessionUiStateStore();
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime, uiStateStore: uiStateStore);

        uiStateStore.SetPhase(SessionUiPhase.Ended, "test");
        await WaitUntilAsync(() => !helpee.CanEndSession, TimeSpan.FromSeconds(1));
        Assert.False(helpee.CanEndSession);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HeaderStatusText_IsNeverEmpty_InDefaultVmStates()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);

        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));
        Assert.False(string.IsNullOrWhiteSpace(helpee.HeaderStatusText));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ChatHardening_WhenEnabled_PreservesExactChatMessageInsertionOrder()
    {
        if (!FeatureFlags.EnableChatHardening)
        {
            return;
        }

        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-chat-hardening-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-chat-hardening-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected",
            TimeSpan.FromSeconds(5));

        var helperTexts = new[] { "helper-1", "helper-2", "helper-3" };
        var helpeeTexts = new[] { "helpee-1", "helpee-2", "helpee-3" };

        for (var i = 0; i < helperTexts.Length; i++)
        {
            helper.ChatDraft = helperTexts[i];
            await helper.SendChatCommand.ExecuteAsync(null);

            var expectedAfterHelperSend = (i * 2) + 1;
            await WaitUntilAsync(
                () => helper.ChatMessages.Count == expectedAfterHelperSend &&
                      helpee.ChatMessages.Count == expectedAfterHelperSend,
                TimeSpan.FromSeconds(2));

            helpee.ChatDraft = helpeeTexts[i];
            await helpee.SendChatCommand.ExecuteAsync(null);

            var expectedAfterHelpeeSend = (i * 2) + 2;
            await WaitUntilAsync(
                () => helper.ChatMessages.Count == expectedAfterHelpeeSend &&
                      helpee.ChatMessages.Count == expectedAfterHelpeeSend,
                TimeSpan.FromSeconds(2));
        }

        Assert.Equal(
            new[]
            {
                (true, "helper-1"),
                (false, "helpee-1"),
                (true, "helper-2"),
                (false, "helpee-2"),
                (true, "helper-3"),
                (false, "helpee-3"),
            },
            helper.ChatMessages.Select(line => (line.IsLocal, line.Text)).ToArray());

        Assert.Equal(
            new[]
            {
                (false, "helper-1"),
                (true, "helpee-1"),
                (false, "helper-2"),
                (true, "helpee-2"),
                (false, "helper-3"),
                (true, "helpee-3"),
            },
            helpee.ChatMessages.Select(line => (line.IsLocal, line.Text)).ToArray());
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FakeClient_HostJoinApproveAndChat_RoundTrip()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.fake.address");
            var helperClient = new FakeNknClient("helper.fake.address");
            var hostIdentity = new NknIdentity("host-id", "host.fake.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.fake.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.SessionKeyReady += (_, e) => hostKeyReady.TrySetResult(e.SharedKey);
            helper.SessionKeyReady += (_, e) => helperKeyReady.TrySetResult(e.SharedKey);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);

            var hostKey = await hostKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var helperKey = await helperKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(hostKey, helperKey);
            Assert.Equal(32, hostKey.Length);

            var chatPayload = Encoding.UTF8.GetBytes("opaque-encrypted-payload");
            await helper.SendChatMessageAsync(chatPayload, cts.Token);
            var received = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(chatPayload, received);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_InvalidInvite_DoesNotRaiseIncomingJoinRequest()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.invalidinvite.address");
            var helperClient = new FakeNknClient("helper.invalidinvite.address");
            var hostIdentity = new NknIdentity("host-id", "host.invalidinvite.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.invalidinvite.address");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out _);
            CreateValidatedInviteForTarget(new PeerAddress("host.invalidinvite.other"), out var wrongTargetToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() => helper.JoinByInviteAsync(wrongTargetToken, invite, cts.Token));
            await Task.Delay(300, cts.Token);

            Assert.False(joinRequestRaised.Task.IsCompleted);
            Assert.Equal(SessionHandshakeState.Failed, helper.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal("invite_binding_mismatch", helper.CurrentSessionSecurityState.HandshakeFailureReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_TamperedInvite_IsRejectedBeforeHandshakeStart()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tamperedinvite.address");
            var helperClient = new FakeNknClient("helper.tamperedinvite.address");
            var hostIdentity = new NknIdentity("host-id", "host.tamperedinvite.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.tamperedinvite.address");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            var tokenParts = rawToken.Split('.', StringSplitOptions.None);
            Assert.Equal(3, tokenParts.Length);
            var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(tokenParts[1]));
            var tamperedJson = payloadJson.Replace(host.LocalPeerAddress, "host.tamperedinvite.other", StringComparison.Ordinal);
            var tamperedToken = $"{tokenParts[0]}.{EncodeBase64Url(Encoding.UTF8.GetBytes(tamperedJson))}.{tokenParts[2]}";

            await Assert.ThrowsAsync<InvalidOperationException>(() => helper.JoinByInviteAsync(tamperedToken, invite, cts.Token));
            await Task.Delay(300, cts.Token);

            Assert.False(joinRequestRaised.Task.IsCompleted);
            Assert.Equal(SessionHandshakeState.Failed, helper.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal("invite_signature_invalid", helper.CurrentSessionSecurityState.HandshakeFailureReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_SendScreenSharePayloadAsync_RejectsOldSessionId()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var helperClient = new FakeNknClient("helper.screenshare.send.address");
            var hostClient = new FakeNknClient("host.screenshare.send.address");
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using var helperTransport = new NknSignalingTransport(helperClient, options, helperIdentity);

            await helperClient.ConnectAsync(CancellationToken.None);
            await hostClient.ConnectAsync(CancellationToken.None);

            var sentCount = 0;
            helperClient.BeforeSendAsync = (_, _, _) =>
            {
                Interlocked.Increment(ref sentCount);
                return Task.CompletedTask;
            };

            var approvedState = CreateApprovedSecurityState(
                new PeerAddress(hostClient.Address),
                new PeerAddress(helperClient.Address),
                CapabilityGrant.ScreenShare);
            SetPrivateField(helperTransport, "remoteEndpoint", hostClient.Address);
            InvokePrivateMethod(helperTransport, "UpdateSessionSecurityState", approvedState);

            var mismatchedPayload = ScreenSharePayloadCodec.Serialize(
                new ScreenShareFrameChunkV1
                {
                    SessionId = "sess_old_screenshare",
                    FrameId = 1,
                    Width = 1,
                    Height = 1,
                    TimestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Encoding = "jpeg",
                    ChunkIndex = 0,
                    ChunkCount = 1,
                    DataBase64 = Convert.ToBase64String(new byte[] { 0x01 }),
                });

            await helperTransport.SendScreenSharePayloadAsync(mismatchedPayload, CancellationToken.None);
            Assert.Equal(0, Volatile.Read(ref sentCount));

            var matchingPayload = ScreenSharePayloadCodec.Serialize(
                new ScreenShareFrameChunkV1
                {
                    SessionId = approvedState.SessionId!.Value.Value,
                    FrameId = 2,
                    Width = 1,
                    Height = 1,
                    TimestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Encoding = "jpeg",
                    ChunkIndex = 0,
                    ChunkCount = 1,
                    DataBase64 = Convert.ToBase64String(new byte[] { 0x02 }),
                });

            await helperTransport.SendScreenSharePayloadAsync(matchingPayload, CancellationToken.None);
            Assert.Equal(1, Volatile.Read(ref sentCount));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ExpiredInvite_IsRejectedBeforeHandshakeStart()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.expiredinvite.address");
            var helperClient = new FakeNknClient("helper.expiredinvite.address");
            var hostIdentity = new NknIdentity("host-id", "host.expiredinvite.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.expiredinvite.address");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);

            await host.HostByAddressAsync(cts.Token);

            var nowUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
            var create = factory.Create(
                new InviteTokenCreateRequest(
                    IssuerAddress: new PeerAddress(host.LocalPeerAddress),
                    TargetAddress: new PeerAddress(host.LocalPeerAddress),
                    SessionId: new SessionId("sess_expired_transport"),
                    Capabilities: InviteCapabilities.Chat,
                    Lifetime: TimeSpan.FromSeconds(20)),
                nowUtc);
            Assert.True(create.IsSuccess, create.Message);
            Assert.NotNull(create.Token);
            Assert.NotNull(create.Payload);

            var expiredInvite = new ValidatedInviteV1(
                create.Payload!,
                create.Payload!.TargetAddress,
                create.Payload.IssuerAddress,
                create.Payload.SessionId);

            await Assert.ThrowsAsync<InvalidOperationException>(() => helper.JoinByInviteAsync(create.Token!, expiredInvite, cts.Token));
            await Task.Delay(300, cts.Token);

            Assert.False(joinRequestRaised.Task.IsCompleted);
            Assert.Equal(SessionHandshakeState.Failed, helper.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal("invite_expired", helper.CurrentSessionSecurityState.HandshakeFailureReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_HelperBoundInvite_ForDifferentHelper_IsRejectedBeforeHandshakeStart()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.boundinvite.address");
            var helperClient = new FakeNknClient("helper.boundinvite.address");
            var hostIdentity = new NknIdentity("host-id", "host.boundinvite.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.boundinvite.address");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                new PeerAddress(host.LocalPeerAddress),
                out var rawToken,
                boundHelperAddress: new PeerAddress("helper.boundinvite.expected"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => helper.JoinByInviteAsync(rawToken, invite, cts.Token));
            await Task.Delay(300, cts.Token);

            Assert.False(joinRequestRaised.Task.IsCompleted);
            Assert.Equal(SessionHandshakeState.Failed, helper.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal("invite_helper_mismatch", helper.CurrentSessionSecurityState.HandshakeFailureReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ReusedInvite_DoesNotRaiseSecondIncomingJoinRequest()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.invitereplay.address");
            var helperClientOne = new FakeNknClient("helper.invitereplay.one");
            var hostIdentity = new NknIdentity("host-id", "host.invitereplay.address");
            var helperIdentityOne = new NknIdentity("helper-one", "helper.invitereplay.one");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helperOne = new NknSignalingTransport(helperClientOne, options, helperIdentityOne);

            var joinRequestCount = 0;
            var firstJoinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstHelperRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) =>
            {
                if (Interlocked.Increment(ref joinRequestCount) == 1)
                {
                    firstJoinRequestRaised.TrySetResult(e);
                }
            };
            helperOne.Rejected += (_, _) => firstHelperRejected.TrySetResult();

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);

            await helperOne.JoinByInviteAsync(rawToken, invite, cts.Token);
            var pendingJoin = await firstJoinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await pendingJoin.RejectAsync(cts.Token);
            await firstHelperRejected.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            await helperOne.JoinByInviteAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(
                () => helperOne.CurrentSessionSecurityState.HandshakeState == SessionHandshakeState.Failed &&
                      string.Equals(helperOne.CurrentSessionSecurityState.HandshakeFailureReason, "invite_replay_detected", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            await Task.Delay(300, cts.Token);

            Assert.Equal(1, Volatile.Read(ref joinRequestCount));
            Assert.Equal(SessionHandshakeState.Failed, helperOne.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal("invite_replay_detected", helperOne.CurrentSessionSecurityState.HandshakeFailureReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_OrphanedJoinRequest_Expires_AllowingSubsequentInviteJoin()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.orphanjoin.address");
            var attackerClient = new FakeNknClient("attacker.orphanjoin.address");
            var helperClient = new FakeNknClient("helper.orphanjoin.address");
            var hostIdentity = new NknIdentity("host-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);

            await host.HostByAddressAsync(cts.Token);
            await attackerClient.ConnectAsync(cts.Token);

            using var attackerEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var orphanJoinPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                helperEndpoint = attackerClient.Address,
                helperIdentifier = "attacker",
                helperEcdhPublicKey = Convert.ToBase64String(attackerEcdh.ExportSubjectPublicKeyInfo()),
                remoteControlSupported = false,
            });
            var orphanJoinEnvelope = new Envelope(
                Version: 1,
                Code: "addr.orphanjoin",
                MessageId: Guid.NewGuid().ToString("N"),
                Type: MsgType.JoinRequest,
                Payload: orphanJoinPayload,
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyTo: null);
            await attackerClient.SendAsync(host.LocalPeerAddress, EnvelopeCodec.Serialize(orphanJoinEnvelope), cts.Token);

            await Task.Delay(TimeSpan.FromSeconds(7), cts.Token);

            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            Assert.NotNull(pendingJoin);
            Assert.NotNull(pendingJoin.ApprovalRequest);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ReplayedHandshakeResponse_IsIgnoredAfterVerification()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.replay.address");
            var helperClient = new FakeNknClient("helper.replay.address");
            var hostIdentity = new NknIdentity("host-id", "host.replay.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.replay.address");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestCount = 0;
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[]? replayEnvelopeBytes = null;
            helperClient.BeforeSendAsync = (_, payload, _) =>
            {
                if (replayEnvelopeBytes is not null)
                {
                    return Task.CompletedTask;
                }

                if (!EnvelopeCodec.TryDeserialize(payload, out var env) || env.Type != MsgType.SessionHandshakeResponse)
                {
                    return Task.CompletedTask;
                }

                replayEnvelopeBytes = EnvelopeCodec.Serialize(env with { MessageId = Guid.NewGuid().ToString("N") });
                return Task.CompletedTask;
            };

            host.IncomingJoinRequest += (_, e) =>
            {
                Interlocked.Increment(ref joinRequestCount);
                joinRequestRaised.TrySetResult(e);
            };

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            Assert.NotNull(pendingJoin);
            Assert.NotNull(replayEnvelopeBytes);

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendAsync(host.LocalPeerAddress, replayEnvelopeBytes!, cts.Token);
            await Task.Delay(300, cts.Token);

            Assert.Equal(1, Volatile.Read(ref joinRequestCount));
            Assert.Equal(SessionHandshakeState.Verified, host.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal("handshake_response_replay_detected", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ControlRequest_WithSessionIdMismatch_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.controlmismatch.address");
            var helperClient = new FakeNknClient("helper.controlmismatch.address");
            var hostIdentity = new NknIdentity("host-id", "host.controlmismatch.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.controlmismatch.address");
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlRequestCount = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.RemoteControlRequestReceived += (_, _) => Interlocked.Increment(ref controlRequestCount);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                new PeerAddress(host.LocalPeerAddress),
                out var rawToken,
                InviteCapabilities.Chat | InviteCapabilities.RemoteControl);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helper.SendControlRequestAsync(
                new ControlRequestMessageV1
                {
                    SessionId = "sess_control_mismatch",
                    RequestId = "req_control_mismatch",
                    Caps = new[] { "mouse", "keyboard" },
                    Reason = "tampered",
                },
                cts.Token);
            await Task.Delay(300, cts.Token);

            Assert.Equal(0, Volatile.Read(ref controlRequestCount));
            Assert.Equal("control_request_session_id_mismatch", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ControlInput_WithSourceIdentityMismatch_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.controlsource.address");
            var helperClient = new FakeNknClient("helper.controlsource.address");
            var attackerClient = new FakeNknClient("attacker.controlsource.address");
            var hostIdentity = new NknIdentity("host-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            var attackerIdentity = new NknIdentity("attacker-id", attackerClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            using var attacker = new NknSignalingTransport(attackerClient, options, attackerIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlInputCount = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.RemoteControlInputReceived += (_, _) => Interlocked.Increment(ref controlInputCount);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                new PeerAddress(host.LocalPeerAddress),
                out var rawToken,
                InviteCapabilities.Chat | InviteCapabilities.RemoteControl);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await attackerClient.ConnectAsync(cts.Token);

            var envelopeCode = Assert.IsType<string>(GetPrivateField(helper, "currentEnvelopeCode"));
            SetPrivateField(attacker, "currentEnvelopeCode", envelopeCode);
            SetPrivateField(attacker, "remoteEndpoint", host.LocalPeerAddress);
            InvokePrivateMethod(attacker, "UpdateSessionSecurityState", helper.CurrentSessionSecurityState);

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await attacker.SendControlInputAsync(
                new ControlInputMessageV1
                {
                    SessionId = host.CurrentSessionSecurityState.SessionId!.Value.Value,
                    RequestId = "req_control_source_mismatch",
                    Seq = 1,
                    Kind = "key",
                    Action = "down",
                    Key = "A",
                },
                cts.Token);
            await Task.Delay(300, cts.Token);

            Assert.Equal(0, Volatile.Read(ref controlInputCount));
            Assert.Equal("control_input_source_identity_mismatch", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_Reject_WithSourceIdentityMismatch_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rejectsource.address");
            var helperClient = new FakeNknClient("helper.rejectsource.address");
            var attackerClient = new FakeNknClient("attacker.rejectsource.address");
            var hostIdentity = new NknIdentity("host-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            var attackerIdentity = new NknIdentity("attacker-id", attackerClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            using var attacker = new NknSignalingTransport(attackerClient, options, attackerIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            helper.Approved += (_, _) => helperApproved.TrySetResult();

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            var connectTask = helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            await WaitUntilAsync(
                () => GetPrivateField(helper, "helperJoinRequestMessageId") is string replyTo && !string.IsNullOrWhiteSpace(replyTo),
                TimeSpan.FromSeconds(2));
            var replyTo = Assert.IsType<string>(GetPrivateField(helper, "helperJoinRequestMessageId"));
            var envelopeCode = Assert.IsType<string>(GetPrivateField(helper, "currentEnvelopeCode"));
            var sessionId = helper.CurrentSessionSecurityState.SessionId!.Value.Value;

            SetPrivateField(attacker, "currentEnvelopeCode", envelopeCode);
            SetPrivateField(attacker, "remoteEndpoint", helper.LocalPeerAddress);
            InvokePrivateMethod(attacker, "UpdateSessionSecurityState", helper.CurrentSessionSecurityState);

            var rejectPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sessionId,
                reason = "join_rejected",
            });
            var rejectEnvelope = new Envelope(
                Version: 1,
                Code: envelopeCode,
                MessageId: Guid.NewGuid().ToString("N"),
                Type: MsgType.Reject,
                Payload: rejectPayload,
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyTo: replyTo);

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await attackerClient.ConnectAsync(cts.Token);
            await attackerClient.SendAsync(helper.LocalPeerAddress, EnvelopeCodec.Serialize(rejectEnvelope), cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await connectTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal("reject_source_identity_mismatch", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
            Assert.True(helper.CurrentSessionSecurityState.ApprovalGranted);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_SessionEnd_WithSourceIdentityMismatch_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.sessionendsource.address");
            var helperClient = new FakeNknClient("helper.sessionendsource.address");
            var attackerClient = new FakeNknClient("attacker.sessionendsource.address");
            var hostIdentity = new NknIdentity("host-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            var attackerIdentity = new NknIdentity("attacker-id", attackerClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            using var attacker = new NknSignalingTransport(attackerClient, options, attackerIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var remoteSessionEndedCount = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.RemoteSessionEnded += (_, _) => Interlocked.Increment(ref remoteSessionEndedCount);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            var envelopeCode = Assert.IsType<string>(GetPrivateField(helper, "currentEnvelopeCode"));
            SetPrivateField(attacker, "currentEnvelopeCode", envelopeCode);
            SetPrivateField(attacker, "remoteEndpoint", host.LocalPeerAddress);
            InvokePrivateMethod(attacker, "UpdateSessionSecurityState", helper.CurrentSessionSecurityState);

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await attackerClient.ConnectAsync(cts.Token);
            var sendException = await Record.ExceptionAsync(() => attacker.SendSessionEndAsync(cts.Token));
            Assert.True(sendException is TimeoutException or OperationCanceledException);
            await Task.Delay(300);

            Assert.Equal(0, Volatile.Read(ref remoteSessionEndedCount));
            Assert.Contains(
                NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason,
                new[]
                {
                    "session_end_source_identity_mismatch",
                    "duplicate",
                });
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknSignalingTransport_SessionEnd_RetriesUntilDelivered_WhenFirstPacketIsDropped()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.sessionendretry.address");
            var helperClient = new FakeNknClient("helper.sessionendretry.address");
            var hostIdentity = new NknIdentity("host-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var remoteSessionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionEndSendAttempts = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.RemoteSessionEnded += (_, _) => remoteSessionEnded.TrySetResult();

            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (!string.Equals(destination, "host.sessionendretry.address", StringComparison.Ordinal))
                {
                    return Task.FromResult(true);
                }

                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("t", out var typeProperty) ||
                    !string.Equals(typeProperty.GetString(), "SessionEnd", StringComparison.Ordinal))
                {
                    return Task.FromResult(true);
                }

                var nextAttempt = Interlocked.Increment(ref sessionEndSendAttempts);
                return Task.FromResult(nextAttempt != 1);
            };

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            await helper.SendSessionEndAsync(cts.Token);
            await remoteSessionEnded.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            Assert.True(Volatile.Read(ref sessionEndSendAttempts) >= 2);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknSignalingTransport_HandshakeStart_RetriesUntilDelivered_WhenFirstPacketIsDropped()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.handshakestartretry.address");
            var helperClient = new FakeNknClient("helper.handshakestartretry.address");
            var hostIdentity = new NknIdentity("host-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handshakeStartSendAttempts = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();

            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (!string.Equals(destination, "host.handshakestartretry.address", StringComparison.Ordinal))
                {
                    return Task.FromResult(true);
                }

                if (!EnvelopeCodec.TryDeserialize(payload, out var env) || env.Type != MsgType.SessionHandshakeStart)
                {
                    return Task.FromResult(true);
                }

                var nextAttempt = Interlocked.Increment(ref handshakeStartSendAttempts);
                return Task.FromResult(nextAttempt != 1);
            };

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.True(Volatile.Read(ref handshakeStartSendAttempts) >= 2);
            Assert.True(helper.CurrentSessionSecurityState.ApprovalGranted);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task DevLocalTransport_ApprovalDecision_CapabilityEscalation_IsRejected()
    {
        var hostAddress = CreateTestPeerAddress();
        using var host = new DevLocalTransport(hostAddress);
        using var helper = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);

        var hostTask = host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);

        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat);
        await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

        var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.NotNull(pendingJoin.ApprovalRequest);

        var approvalRequest = pendingJoin.ApprovalRequest!;
        var escalatedDecision = new ApprovalDecision(
            ApprovedCapabilities: approvalRequest.RequestedCapabilities | CapabilityGrant.RemoteControl,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            HelperIdentity: approvalRequest.HelperIdentity,
            SessionId: approvalRequest.SessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pendingJoin.ApproveAsync(escalatedDecision, cts.Token));
        await Task.Delay(150, cts.Token);

        Assert.False(host.CurrentSessionSecurityState.ApprovalGranted);
        Assert.False(helper.CurrentSessionSecurityState.ApprovalGranted);

        cts.Cancel();
        await Task.WhenAny(hostTask, Task.Delay(150, CancellationToken.None));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_HelperReconnect_DoesNotReuseOldApproval()
    {
        var hostAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var reconnectRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat | InviteCapabilities.RemoteControl);
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(
            () => helperRuntime.CurrentSessionGrant is not null &&
                  helpeeRuntime.CurrentSessionGrant is not null,
            TimeSpan.FromSeconds(2));

        var oldSessionId = helperRuntime.CurrentSessionGrant!.SessionId;

        await helperRuntime.DisconnectAsync();
        await WaitUntilAsync(
            () => helpeeRuntime.CurrentSessionGrant is null &&
                  !helpeeRuntime.SecurityState.ApprovalGranted,
            TimeSpan.FromSeconds(3));
        await helpeeRuntime.StartHelpeeAsync(cts.Token);

        var reconnectInvite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var reconnectToken,
            InviteCapabilities.Chat | InviteCapabilities.RemoteControl);
        var reconnectTask = reconnectRuntime.StartHelperAsync(reconnectToken, reconnectInvite, cts.Token);

        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));

        Assert.Null(reconnectRuntime.CurrentSessionGrant);
        Assert.False(reconnectRuntime.SecurityState.ApprovalGranted);
        Assert.False(await reconnectRuntime.RequestRemoteControlAsync(cts.Token));

        await helpeeRuntime.ApproveAsync(cts.Token);
        await reconnectTask;
        await WaitUntilAsync(() => reconnectRuntime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));

        Assert.NotEqual(oldSessionId, reconnectRuntime.CurrentSessionGrant!.SessionId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_RepeatCycle_ResetAndRetry_FiveIterations_ReturnsToIdle()
    {
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-" + Guid.NewGuid().ToString("N")));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var helperChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < 5; i++)
        {
            helperChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnHelperChat(object? _, ChatMessageEventArgs e) => helperChatReceived.TrySetResult(e.Message.Text);
            void OnHelpeeChat(object? _, ChatMessageEventArgs e) => helpeeChatReceived.TrySetResult(e.Message.Text);

            helperRuntime.ChatMessageReceived += OnHelperChat;
            helpeeRuntime.ChatMessageReceived += OnHelpeeChat;

            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);

            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(1));

            await helpeeRuntime.ApproveAsync(cts.Token);

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected &&
                      helpeeRuntime.HasSessionKey &&
                      helperRuntime.HasSessionKey,
                TimeSpan.FromSeconds(1));

            var helperText = $"hello-{i}";
            var helpeeText = $"reply-{i}";

            var helperSent = await helperRuntime.TrySendChatTextAsync(helperText, cts.Token);
            Assert.NotNull(helperSent);
            Assert.Equal(helperText, await helpeeChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));

            helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            helperRuntime.ChatMessageReceived -= OnHelperChat;
            helpeeRuntime.ChatMessageReceived -= OnHelpeeChat;
            helperRuntime.ChatMessageReceived += OnHelperChat;
            helpeeRuntime.ChatMessageReceived += OnHelpeeChat;

            var helpeeSent = await helpeeRuntime.TrySendChatTextAsync(helpeeText, cts.Token);
            Assert.NotNull(helpeeSent);
            Assert.Equal(helpeeText, await helperChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));

            helperRuntime.ChatMessageReceived -= OnHelperChat;
            helpeeRuntime.ChatMessageReceived -= OnHelpeeChat;

            await helperRuntime.ResetAsync();
            await helpeeRuntime.ResetAsync();

            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.Equal(SessionRuntimeState.Idle, helpeeRuntime.State);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioA_HappyPath_HeadlessSessionRuntime_CompletesConnectAndChat()
    {
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-a-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-a-" + Guid.NewGuid().ToString("N")));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var helperReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        helperRuntime.ChatMessageReceived += (_, e) => helperReceived.TrySetResult(e.Message.Text);
        helpeeRuntime.ChatMessageReceived += (_, e) => helpeeReceived.TrySetResult(e.Message.Text);

        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(1));

        await helpeeRuntime.ApproveAsync(cts.Token);

        await WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected &&
                  helpeeRuntime.HasSessionKey &&
                  helperRuntime.HasSessionKey,
            TimeSpan.FromSeconds(1));

        Assert.NotNull(await helperRuntime.TrySendChatTextAsync("hello-a", cts.Token));
        Assert.Equal("hello-a", await helpeeReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));

        helperReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        helperRuntime.ChatMessageReceived += (_, e) => helperReceived.TrySetResult(e.Message.Text);
        Assert.NotNull(await helpeeRuntime.TrySendChatTextAsync("reply-a", cts.Token));
        Assert.Equal("reply-a", await helperReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_NknRemoteSessionEnd_ShowsFriendlyMessage_AndCanReset()
    {
        FakeNknClient.ResetNetwork();

        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(
                new FakeNknClient("helpee.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helpee-test", "helpee.test.fake"));
            using var helperTransport = new NknSignalingTransport(
                new FakeNknClient("helper.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helper-test", "helper.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(GetHostedAddressOrThrow(helpeeRuntime), out var rawToken);
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);

            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected,
                TimeSpan.FromSeconds(2));

            await helperRuntime.DisconnectAsync();

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.Equal("The helper ended the session.", helpeeRuntime.StatusText);

            await helpeeRuntime.ResetAsync();
            Assert.Equal(SessionRuntimeState.Idle, helpeeRuntime.State);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioC_SessionEnd_HeadlessRemoteEnd_ShowsFriendlyMessage()
    {
        FakeNknClient.ResetNetwork();

        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(
                new FakeNknClient("helpee.c.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helpee-c", "helpee.c.fake"));
            using var helperTransport = new NknSignalingTransport(
                new FakeNknClient("helper.c.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helper-c", "helper.c.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(GetHostedAddressOrThrow(helpeeRuntime), out var rawToken);
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected,
                TimeSpan.FromSeconds(2));

            await helperRuntime.DisconnectAsync();

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_CopyInstallMessageCommand_UsesClipboardService()
    {
        var fakeClipboard = new FakeClipboardService();
        var transportConfig = CreateDevLocalTestConfig();
        var shareConfig = new ShareMessageConfig("https://example.com/nlink");
        using var runtime = new SessionRuntime(() => new FakeSignalingTransport());

        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            clipboardService: fakeClipboard,
            shareMessageConfig: shareConfig);

        await helper.CopyInstallMessageCommand.ExecuteAsync(null);

        Assert.Equal(
            "Install nLink and open it." + Environment.NewLine +
            "Download: https://example.com/nlink" + Environment.NewLine,
            fakeClipboard.LastText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_WrongCode_TransitionsToFailed_WithMappedMessage_AndReconnectEnabled()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(
            onJoinByAddressAsync: static (_, __) => throw new TimeoutException("Could not find target session"));
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.timeout.target"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  string.Equals(runtime.StatusText, "No response from target address.", StringComparison.Ordinal) &&
                  string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal) &&
                  helper.ConnectCommand.CanExecute(null),
            TimeSpan.FromSeconds(1));

        Assert.Equal("No response from target address.", runtime.StatusText);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.Equal("Failed", helper.ConnectionState);

        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);

        await WaitUntilAsync(
            () => string.Equals(runtime.StatusText, "No response from target address.", StringComparison.Ordinal) &&
                  string.Equals(helper.StatusText, "No response from target address.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioB_WrongCodeTimeout_HeadlessHelperVm_ShowsFriendlyFailure_AndReconnect()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
            onJoinByAddressAsync: static (_, __) => throw new TimeoutException("Could not find target session")));
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.timeout.alpha3"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  string.Equals(runtime.StatusText, "No response from target address.", StringComparison.Ordinal) &&
                  string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal) &&
                  helper.ConnectCommand.CanExecute(null),
            TimeSpan.FromSeconds(1));

        Assert.Equal("No response from target address.", runtime.StatusText);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.Equal("Failed", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_ApprovalTimeout_TransitionsToFailed_WithMappedMessage()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(
            onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.approval.timeout"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("No response yet.", helper.StatusText);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.Equal("Failed", helper.ConnectionState);

        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);

        await WaitUntilAsync(
            () => string.Equals(runtime.StatusText, "No response yet.", StringComparison.Ordinal) &&
                  string.Equals(helper.StatusText, "No response yet.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_Cooldown_PreventsRapidSecondConnectAttempt()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var factory = new CountingTransportFactory(() => new ScriptedSignalingTransport(
            onJoinByAddressAsync: static (_, __) => throw new TimeoutException("Could not find target session")));
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.FromSeconds(2));

        CreateValidatedInviteForTarget(new PeerAddress("scripted.cooldown.target"), out var inviteToken);
        helper.CodeInput = inviteToken;

        await helper.ConnectCommand.ExecuteAsync(null);
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, factory.CreateCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_RawAddressInput_IsRejected_WithoutStartingRuntime()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var factory = new CountingTransportFactory(() => new ScriptedSignalingTransport());
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        helper.CodeInput = "nlink-helpee.raw-address";
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("InvalidInput", helper.ConnectionState);
        Assert.Equal("Use the helpee invite token.", helper.StatusText);
        Assert.Equal(SessionRuntimeState.Idle, runtime.State);
        Assert.Equal(0, factory.CreateCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_FirstConnectAfterEndedSession_PreservesInviteAndStartsJoin()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var joinByInviteCalls = 0;
        string? observedInviteToken = null;
        using var scripted = new ScriptedSignalingTransport(
            onJoinByInviteAsync: (inviteToken, _, _) =>
            {
                observedInviteToken = inviteToken;
                Interlocked.Increment(ref joinByInviteCalls);
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.connect.after-ended"), out var inviteToken);
        SetPrivateField(helper, "endReason", SessionEndReason.UserEnded);
        helper.CodeInput = inviteToken;

        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(inviteToken, observedInviteToken);
        Assert.Equal(1, Volatile.Read(ref joinByInviteCalls));
        Assert.Equal(inviteToken, helper.CodeInput);
        Assert.NotEqual("InvalidInput", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_TransportDisconnect_TransitionsToFailed_WithConnectionLost()
    {
        var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        using var runtime = new SessionRuntime(() => scripted);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await runtime.StartHelperAsync(new PeerAddress("scripted.transport.disconnect"), cts.Token);
        scripted.RaiseDisconnected();

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  string.Equals(runtime.StatusText, "Connection lost.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        Assert.Equal("Connection lost.", runtime.StatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_DisconnectAfterMappedFail_KeepsMappedStatusText()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "statusText", "No response yet.");

        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);

        Assert.Equal("No response yet.", runtime.StatusText);
        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_TransportApproved_DoesNotAutoStartTransportScreenShare()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "statusText", "Connecting…");
        scripted.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress(scripted.LocalPeerAddress),
                new PeerAddress("scripted.helper.peer")));

        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);

        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
        Assert.False(runtime.IsTransportScreenShareActiveForTests);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_TransportDisconnect_DisablesScreenShareAutoStart_ForLaterApproval()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        scripted.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress(scripted.LocalPeerAddress),
                new PeerAddress("scripted.helper.peer")));

        Assert.True(runtime.CanAutoStartTransportScreenShareForTests);

        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);

        Assert.False(runtime.CanAutoStartTransportScreenShareForTests);
        scripted.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress(scripted.LocalPeerAddress),
                new PeerAddress("scripted.helper.peer")));

        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);

        Assert.False(runtime.CanAutoStartTransportScreenShareForTests);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperViewModel_NknMissing_ShowsFriendlyError_AndDiagnosticsLink()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "NKN");
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var config = TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                config,
                runtime,
                openDiagnosticsAction: static () => { });

            Assert.True(helper.IsStartupBlocked);
            Assert.Equal("Please reinstall.", helper.StatusText);
            Assert.True(helper.ShowOpenDiagnosticsLink);
            Assert.False(helper.ShowConnectAction);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_Disconnect_ShowsRetry_AndRetryReturnsToIdle()
    {
        var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.disconnect.retry"), out var inviteToken);
        helper.CodeInput = inviteToken;
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Connecting, TimeSpan.FromSeconds(1));
        scripted.RaiseDisconnected();
        await connectTask;

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  helper.ConnectionState == "Failed" &&
                  helper.ShowRetryAction &&
                  string.Equals(helper.StatusText, "Connection lost.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
        Assert.True(helper.RetryCommand.CanExecute(null));

        await helper.RetryCommand.ExecuteAsync(null);

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Idle &&
                  helper.ConnectionState == "Idle" &&
                  !helper.ShowRetryAction,
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioD_DisconnectAndRetry_HeadlessHelperVm_ReturnsToIdle()
    {
        var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.disconnect.alpha3"), out var inviteToken);
        helper.CodeInput = inviteToken;
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Connecting, TimeSpan.FromSeconds(1));
        scripted.RaiseDisconnected();
        await connectTask;

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  string.Equals(helper.StatusText, "Connection lost.", StringComparison.Ordinal) &&
                  helper.ShowRetryAction,
            TimeSpan.FromSeconds(2));
        Assert.True(helper.RetryCommand.CanExecute(null));

        await helper.RetryCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Idle &&
                  helper.ConnectionState == "Idle" &&
                  !helper.ShowRetryAction,
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_CancelTransientWhileConnecting_ReturnsToIdle_AndCodeInputRemainsEditable()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        CreateValidatedInviteForTarget(new PeerAddress("scripted.connect.cancel"), out var inviteToken);
        helper.CodeInput = inviteToken;
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(helper, "isConnecting", true);
        SetPrivateField(helper, "connectionState", "Connecting");
        SetPrivateField(helper, "showTransientBanner", true);
        SetPrivateField(helper, "canCancelTransient", true);

        var cancelTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "CancelTransientAsync"));
        await cancelTask;

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Idle &&
                  helper.ConnectionState == "Idle" &&
                  !helper.IsConnecting &&
                  !helper.ShowTransientBanner &&
                  helper.ConnectCommand.CanExecute(null),
            TimeSpan.FromSeconds(3));

        helper.CodeInput = "nlink-invite.editable";
        Assert.Equal("nlink-invite.editable", helper.CodeInput);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.False(helper.ShowTransientBanner);
        Assert.True(string.IsNullOrWhiteSpace(helper.TransientBannerText));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_DisconnectAfterConnected_AutoRegeneratesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-auto-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-auto-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var initialInvite = await WaitForShareInviteAsync(helpee);

        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));

        helpee.AllowCommand.Execute(null);
        await WaitUntilAsync(
            () => helpee.IsConnectedView &&
                  helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected,
            TimeSpan.FromSeconds(2));

        await helperRuntime.DisconnectAsync();

        string latestInvite = initialInvite;
        await WaitUntilAsync(
            () =>
            {
                latestInvite = helpee.ShareInvite;
                return helpee.ShowWaitingPanel &&
                       !helpee.IsConnectedView &&
                       !string.IsNullOrWhiteSpace(latestInvite) &&
                       !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(8));

        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_DisconnectAfterConnected_AutoRegeneratesCode_OnceAndStaysStable()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-auto-rehost-stable-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-auto-rehost-stable-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var initialInvite = await WaitForShareInviteAsync(helpee);

        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));

        helpee.AllowCommand.Execute(null);
        await WaitUntilAsync(
            () => helpee.IsConnectedView &&
                  helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected,
            TimeSpan.FromSeconds(2));

        await helperRuntime.DisconnectAsync();

        string rotatedInvite = initialInvite;
        await WaitUntilAsync(
            () =>
            {
                rotatedInvite = helpee.ShareInvite;
                return helpee.ShowWaitingPanel &&
                       !helpee.IsConnectedView &&
                       !string.IsNullOrWhiteSpace(rotatedInvite) &&
                       !string.Equals(rotatedInvite, initialInvite, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(8));

        await Task.Delay(1800);
        var stableInvite = helpee.ShareInvite;
        Assert.Equal(rotatedInvite, stableInvite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_UserEndsConnectedSession_AutoRegeneratesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-user-end-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-user-end-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(
            cancelAction: () => _ = helpeeRuntime.DisconnectAsync(),
            transportConfig,
            helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var initialInvite = await WaitForShareInviteAsync(helpee);

        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));

        helpee.AllowCommand.Execute(null);
        await WaitUntilAsync(
            () => helpee.IsConnectedView &&
                  helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected,
            TimeSpan.FromSeconds(2));

        helpee.EndSessionCommand.Execute(null);

        string latestInvite = initialInvite;
        await WaitUntilAsync(
            () =>
            {
                latestInvite = helpee.ShareInvite;
                return helpee.ShowWaitingPanel &&
                       !helpee.IsConnectedView &&
                       !string.IsNullOrWhiteSpace(latestInvite) &&
                       !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(8));

        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_DeclineIncomingRequest_AutoRegeneratesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-decline-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-decline-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var initialInvite = await WaitForShareInviteAsync(helpee);

        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));

        await helpee.DeclineCommand.ExecuteAsync(null);

        string latestInvite = initialInvite;
        await WaitUntilAsync(
            () =>
            {
                latestInvite = helpee.ShareInvite;
                return helpee.ShowWaitingPanel &&
                       !helpee.IsIncomingRequestView &&
                       !string.IsNullOrWhiteSpace(latestInvite) &&
                       !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(8));

        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingRequestTimeout_ReturnsToWaitingWithUsableCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-timeout-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-timeout-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helpeeRuntime,
            incomingRequestTimeout: TimeSpan.FromMilliseconds(250));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var usableInvite = string.Empty;

        _ = await WaitForShareInviteAsync(helpee);

        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(
            () =>
            {
                usableInvite = helpee.ShareInvite;
                return helpee.ShowWaitingPanel &&
                       !helpee.IsIncomingRequestView &&
                       string.Equals(helpee.ConnectionState, "Waiting", StringComparison.Ordinal) &&
                       !string.IsNullOrWhiteSpace(usableInvite);
            },
            TimeSpan.FromSeconds(8));

        Assert.False(string.IsNullOrWhiteSpace(usableInvite));
        Assert.Equal("Waiting", helpee.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_HelperDisconnectsDuringIncomingRequest_ClearsAllowPanel_AndRotatesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-incoming-cancel-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-incoming-cancel-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        var initialInvite = await WaitForShareInviteAsync(helpee);

        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(
            () => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel,
            TimeSpan.FromSeconds(2));

        Assert.False(helpee.ShowTransientBanner);

        await helperRuntime.DisconnectAsync();

        string latestInvite = initialInvite;
        await WaitUntilAsync(
            () =>
            {
                latestInvite = helpee.ShareInvite;
                return !helpee.IsIncomingRequestView &&
                       helpee.ShowWaitingPanel &&
                       !helpee.HasIncomingRequest &&
                       !string.IsNullOrWhiteSpace(latestInvite) &&
                       !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(8));

        Assert.NotEqual(initialInvite, latestInvite);
        Assert.False(helpee.ShowTransientBanner);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperCancelDuringConnecting_ClearsHelpeeAllowPanel_AndRotatesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-helper-cancel-flow-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-helper-cancel-flow-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helperRuntime,
            connectFailureCooldown: TimeSpan.Zero);

        var initialInvite = await WaitForShareInviteAsync(helpee);
        helper.CodeInput = await WaitForShareInviteAsync(helpee);

        var connectTask = helper.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(
            () => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel && helpee.HasIncomingRequest,
            TimeSpan.FromSeconds(3));

        await helper.CancelTransientCommand.ExecuteAsync(null);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));

        string latestInvite = initialInvite;
        await WaitUntilAsync(
            () =>
            {
                latestInvite = helpee.ShareInvite;
                return !helpee.IsIncomingRequestView &&
                       !helpee.ShowIncomingRequestPanel &&
                       !helpee.HasIncomingRequest &&
                       helpee.ShowWaitingPanel &&
                       !string.IsNullOrWhiteSpace(latestInvite) &&
                       !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(8));

        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingRequest_DoesNotExposeTransientCancel()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-incoming-no-cancel-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-incoming-no-cancel-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        _ = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);

        await WaitUntilAsync(
            () => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel,
            TimeSpan.FromSeconds(2));

        Assert.False(helpee.CanCancelTransient);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingJoinRequest_SwitchesToApprovalPanel()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-ui-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-ui-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        Assert.False(helpee.IsIncomingRequestView);
        Assert.True(helpee.ShowWaitingPanel);

        _ = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);

        await WaitUntilAsync(
            () => helpee.IsIncomingRequestView &&
                  helpee.ShowIncomingRequestPanel &&
                  !helpee.ShowWaitingPanel,
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ApprovalVerificationCode_IsExposedOnHelpeeAndHelper_FromSharedHelperIdentity()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-verify-code-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-verify-code-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);

        await WaitUntilAsync(
            () => helpee.IsIncomingRequestView &&
                  helpee.ShowIncomingRequestPanel &&
                  helpee.HasIncomingHelperVerificationCode &&
                  helper.HasHelperVerificationCode,
            TimeSpan.FromSeconds(3));

        Assert.Equal(helpee.IncomingHelperVerificationCode, helper.HelperVerificationCode);
        Assert.True(helpee.HasIncomingTechnicalDetails);
        Assert.False(string.IsNullOrWhiteSpace(helpee.IncomingTechnicalHelperIdentityText));
        Assert.False(string.IsNullOrWhiteSpace(helpee.IncomingTechnicalSessionIdText));
        Assert.True(helper.HasHelperTechnicalDetails);
        Assert.False(string.IsNullOrWhiteSpace(helper.HelperTechnicalIdentityText));
        Assert.False(string.IsNullOrWhiteSpace(helper.HelperTechnicalSessionIdText));
        Assert.True(helper.ShowHelperVerificationCode);

        await helpee.DeclineCommand.ExecuteAsync(null);
        await connectTask;
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperVerificationCode_IsHiddenUntilStableSessionSecurityHelperIdentityExists()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

        var localTransport = new ScriptedSignalingTransport(localPeerAddress: "helper.local.identity");
        SetPrivateField(helperRuntime, "transport", localTransport);
        Assert.False(helper.HasHelperVerificationCode);
        Assert.False(helper.ShowHelperVerificationCode);

        SetPrivateField(
            helperRuntime,
            "sessionSecurityState",
            SessionSecurityState.CreateHelperPending(
                new SessionId("session-verify"),
                new PeerAddress("helpee.target.identity"),
                new PeerAddress("helper.stable.identity"),
                inviteValidated: true));

        var expected = HelperVerificationCodeFormatter.Format(new PeerAddress("helper.stable.identity"));

        Assert.Equal(expected, helper.HelperVerificationCode);
        Assert.Equal("helper.stable.identity", helper.HelperTechnicalIdentityText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_Startup_HealthCheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = ResolveBridgeRuntimeDirectoryForHealthCheck(out var attemptedPath, out var runtimeDirSource);
        Assert.True(bundleDir is not null,
            $"Bridge runtime not found. Source={runtimeDirSource}, attempted='{attemptedPath}'. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");

        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js")) ?? Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath),
            $"Bridge runtime not found. Expected bundled node at '{nodePath}'. Run installer/Build-BridgeBundle.ps1.");
        Assert.True(File.Exists(bridgePath),
            $"Bridge script not found. Expected workspace tools/nkn-bridge/index.js or bundled bridge script at '{bridgePath}'.");

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("smoke-bridge", "smoke-bridge.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);

            await adapter.PingBridgeAsync(cts.Token);

            var snapshotAfterPing = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshotAfterPing.BridgePid > 0);
            Assert.False(string.IsNullOrWhiteSpace(snapshotAfterPing.NodeVersion));
            Assert.True(snapshotAfterPing.BridgeLastPongUtcTicks > 0);

            await adapter.DisconnectAsync();

            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));

            var snapshotAfterShutdown = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshotAfterShutdown.BridgeLastExitCode >= 0 || snapshotAfterShutdown.BridgeLastExitReason != "(none)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_Startup_WithMockBridge_DelayedPong_EmitsReadyAfterPong()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-delay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-delay.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 250, respondToPing: true));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-delay", "mock-delay.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            BridgeLifecycleEvent? readyEvent = null;
            adapter.BridgeLifecycle += (_, e) =>
            {
                if (e.Kind == BridgeLifecycleEventKind.Ready)
                {
                    readyEvent = e;
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            await adapter.StartBridgeAsync(cts.Token);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds >= 150, "Bridge start completed before delayed pong should have arrived.");
            Assert.True(adapter.IsBridgeProcessRunning);
            Assert.True(readyEvent.HasValue);
            Assert.Equal(BridgeLifecycleEventKind.Ready, readyEvent.Value.Kind);
            Assert.True(readyEvent.Value.PingRttMs.HasValue);
            Assert.True(readyEvent.Value.PingRttMs.Value >= 150);
            Assert.True(NknRuntimeDiagnostics.Snapshot().BridgeLastPongUtcTicks > 0);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Startup_WithMockBridge_NoPong_FailsAsBridgeUnresponsive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-nopong", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-nopong.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: false));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-nopong", "mock-nopong.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartBridgeAsync(cts.Token));
            Assert.Contains("hello failed", ex.Message, StringComparison.OrdinalIgnoreCase);

            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.Contains("NKN_START_FAILED", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bridge_unresponsive", snapshot.LastError, StringComparison.OrdinalIgnoreCase);

            var failure = TransportFailureMapper.FromSignals(snapshot.LastError);
            Assert.Equal(TransportFailureCategory.BridgeUnresponsive, failure.Category);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_ConcurrentConnectAsync_SharesSingleConnectAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-concurrent-connect", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-concurrent.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(
            connectBehaviorJs: $@"
connectCount++;
fs.writeFileSync({JsonSerializer.Serialize(countFile)}, String(connectCount));
emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
setTimeout(() => emit({{ event:'ready', address:'mock.concurrent.addr', connectId: msg.connectId ?? null }}), 200);
return;
"));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-concurrent");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var t1 = adapter.ConnectAsync(cts.Token);
            var t2 = adapter.ConnectAsync(cts.Token);
            await Task.WhenAll(t1, t2);

            await adapter.DisconnectAsync();

            var connectCountText = File.Exists(countFile) ? File.ReadAllText(countFile).Trim() : string.Empty;
            Assert.Equal("1", connectCountText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_StaleReady_Ignored_UntilMatchingConnectIdArrives()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-stale-ready", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-stale-ready.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(
            connectBehaviorJs: @"
emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
setTimeout(() => emit({ event:'ready', address:'wrong.addr', connectId:'ffffffffffffffffffffffffffffffff' }), 50);
setTimeout(() => emit({ event:'ready', address:'correct.addr', connectId: msg.connectId ?? null }), 220);
return;
"));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-stale-ready");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var sw = Stopwatch.StartNew();
            await adapter.ConnectAsync(cts.Token);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds >= 150, "ConnectAsync completed too early; stale ready may have been accepted.");
            Assert.Equal("correct.addr", adapter.Address);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_ConnectFailure_ResetsInflight_AndUsesNewConnectIdNextAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-connect-reset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var idsFile = Path.Combine(tempDir, "connect-ids.json");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-connect-reset.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(
            connectBehaviorJs: $@"
connectIds.push(String(msg.connectId || ''));
fs.writeFileSync({JsonSerializer.Serialize(idsFile)}, JSON.stringify(connectIds));
emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
if (connectIds.length >= 2) {{
  setTimeout(() => emit({{ event:'ready', address:'second-success.addr', connectId: msg.connectId ?? null }}), 40);
}}
return;
"));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-connect-reset");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.SetConnectReadyTimeoutForTests(TimeSpan.FromMilliseconds(120));

            await Assert.ThrowsAnyAsync<TimeoutException>(() => adapter.ConnectAsync(CancellationToken.None));
            await adapter.ConnectAsync(CancellationToken.None);
            Assert.Equal("second-success.addr", adapter.Address);

            await adapter.DisconnectAsync();

            var idsJson = File.Exists(idsFile) ? File.ReadAllText(idsFile) : "[]";
            using var doc = JsonDocument.Parse(idsJson);
            var ids = doc.RootElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
            Assert.True(ids.Length >= 2);
            Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
            Assert.NotEqual(ids[0], ids[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_ConnectPayload_RespectsPreflightOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-preflight-payload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var payloadFile = Path.Combine(tempDir, "payload.json");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-preflight-payload.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(
            connectBehaviorJs: $@"
fs.writeFileSync({JsonSerializer.Serialize(payloadFile)}, JSON.stringify(msg));
emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
setTimeout(() => emit({{ event:'ready', address:'payload-test.addr', connectId: msg.connectId ?? null }}), 20);
return;
"));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED");
        var prevTimeout = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS");
        var prevConc = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY");
        var prevTtl = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            // Disabled by default -> no preflight fields.
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS", null);

            var disabledOptions = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id-disabled.json"), "mock-preflight-disabled");
            var disabledIdentity = NknIdentityStore.LoadOrCreate(disabledOptions);
            using (var adapterDisabled = new RealNknClientAdapter(disabledIdentity, disabledOptions))
            {
                await adapterDisabled.ConnectAsync(CancellationToken.None);
                await adapterDisabled.DisconnectAsync();
            }

            using (var payloadDoc = JsonDocument.Parse(File.ReadAllText(payloadFile)))
            {
                var root = payloadDoc.RootElement;
                Assert.True(root.TryGetProperty("connectId", out _));
                Assert.False(root.TryGetProperty("preflightRpcEnabled", out _));
                Assert.False(root.TryGetProperty("preflightTimeoutMs", out _));
                Assert.False(root.TryGetProperty("preflightConcurrency", out _));
                Assert.False(root.TryGetProperty("preflightCacheTtlMs", out _));
            }

            // Enabled -> fields present.
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", "true");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS", "701");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY", "9");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS", "600001");

            var enabledOptions = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id-enabled.json"), "mock-preflight-enabled");
            var enabledIdentity = NknIdentityStore.LoadOrCreate(enabledOptions);
            using (var adapterEnabled = new RealNknClientAdapter(enabledIdentity, enabledOptions))
            {
                await adapterEnabled.ConnectAsync(CancellationToken.None);
                await adapterEnabled.DisconnectAsync();
            }

            using (var payloadDoc = JsonDocument.Parse(File.ReadAllText(payloadFile)))
            {
                var root = payloadDoc.RootElement;
                Assert.True(root.TryGetProperty("preflightRpcEnabled", out var enabledProp) && enabledProp.ValueKind is JsonValueKind.True);
                Assert.Equal(701, root.GetProperty("preflightTimeoutMs").GetInt32());
                Assert.Equal(9, root.GetProperty("preflightConcurrency").GetInt32());
                Assert.Equal(600001, root.GetProperty("preflightCacheTtlMs").GetInt32());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", prevEnabled);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS", prevTimeout);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY", prevConc);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS", prevTtl);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_ProgressDiagnostics_AreRecorded_OnConnectReadyTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-progress-timeout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-progress-timeout.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(
            connectBehaviorJs: @"
emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
emit({ event:'rpc_selected', rpc:'https://mock-rpc-1.example:30003', connectId: msg.connectId ?? null, ts: Date.now() });
return;
"));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-progress-timeout");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.SetConnectReadyTimeoutForTests(TimeSpan.FromMilliseconds(150));

            await Assert.ThrowsAnyAsync<TimeoutException>(() => adapter.ConnectAsync(CancellationToken.None));

            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.Equal("rpc_selected", snapshot.LastProgressEventType);
            Assert.Equal("https://mock-rpc-1.example:30003", snapshot.LastSelectedRpc);
            Assert.True(snapshot.LastProgressEventUtcTicks > 0);
            Assert.Contains("NKN_START_FAILED", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("progress=rpc_selected", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Disconnect_WithUnresponsiveShutdownBridge_ForcesKill_AndCleansProcessHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-ignore-shutdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-ignore-shutdown.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: false));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-ignore-shutdown", "mock-ignore-shutdown.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);

            await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));
            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdinReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
            Assert.Equal(0, debugState.TrackedPid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Dispose_AfterStart_ShutsDownProcess_AndClearsHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-dispose", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-dispose.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        Process? bridgeProcess = null;
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-dispose", "mock-dispose.fake");
            var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);

            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshot.BridgePid > 0);
            bridgeProcess = Process.GetProcessById(snapshot.BridgePid);

            adapter.Dispose();

            await WaitUntilAsync(() =>
            {
                try { return bridgeProcess.HasExited; } catch { return true; }
            }, TimeSpan.FromSeconds(3));

            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdinReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
            Assert.Equal(0, debugState.TrackedPid);
        }
        finally
        {
            try { bridgeProcess?.Dispose(); } catch { }
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Dispose_WithUnresponsiveShutdownBridge_ForcesKill_AndClearsHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-dispose-ignore-shutdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-dispose-ignore-shutdown.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: false));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        Process? bridgeProcess = null;
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-dispose-force-kill", "mock-dispose-force-kill.fake");
            var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);

            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshot.BridgePid > 0);
            bridgeProcess = Process.GetProcessById(snapshot.BridgePid);

            adapter.Dispose();

            await WaitUntilAsync(() =>
            {
                try { return bridgeProcess.HasExited; } catch { return true; }
            }, TimeSpan.FromSeconds(4));

            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdinReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
            Assert.Equal(0, debugState.TrackedPid);
        }
        finally
        {
            try { bridgeProcess?.Dispose(); } catch { }
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_StderrSpam_DoesNotHang_AndShutsDownCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-stderr-spam", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-stderr-spam.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithStderrSpam());

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-stderr-spam", "mock-stderr-spam.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            await adapter.StartBridgeAsync(cts.Token);
            await adapter.PingBridgeAsync(cts.Token);
            await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));
            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_RapidStartDisposeCycles_DoNotLeaveOrphanProcessesOrHandleRefs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-rapid-cycles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-rapid-cycles.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            for (var i = 0; i < 50; i++)
            {
                var options = NknTransportOptions.Load();
                var identity = new NknIdentity("mock-cycle-" + i, "mock-cycle.fake");
                using var adapter = new RealNknClientAdapter(identity, options);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                await adapter.StartBridgeAsync(cts.Token);
                Assert.True(adapter.IsBridgeProcessRunning);
                await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));

                var debugState = adapter.GetDebugStateForTests();
                Assert.False(debugState.HasProcessReference);
                Assert.False(debugState.HasStdinReference);
                Assert.False(debugState.HasStdoutReaderTaskReference);
                Assert.False(debugState.HasStderrReaderTaskReference);
                Assert.Equal(0, debugState.TrackedPid);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_RapidStartDisposeCycles200_Promotion_NoOrphansOrHandleRefs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-rapid-cycles-200", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-rapid-cycles-200.js");
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            for (var i = 0; i < 200; i++)
            {
                var options = NknTransportOptions.Load();
                var identity = new NknIdentity("mock-cycle200-" + i, "mock-cycle200.fake");
                using var adapter = new RealNknClientAdapter(identity, options);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                await adapter.StartBridgeAsync(cts.Token);
                Assert.True(adapter.IsBridgeProcessRunning);
                await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));

                var debugState = adapter.GetDebugStateForTests();
                Assert.False(debugState.HasProcessReference);
                Assert.False(debugState.HasStdinReference);
                Assert.False(debugState.HasStdoutReaderTaskReference);
                Assert.False(debugState.HasStderrReaderTaskReference);
                Assert.Equal(0, debugState.TrackedPid);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_TrackedPidCleanup_KillsOrphanNodeProcess_ByPidAndStartTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-orphan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "idle-node.js");
        File.WriteAllText(scriptPath, "setInterval(() => {}, 1000);");

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.StartInfo.ArgumentList.Add(scriptPath);
        Assert.True(proc.Start());

        var pid = proc.Id;
        var startTimeUtcFileTime = proc.StartTime.ToUniversalTime().ToFileTimeUtc();

        try
        {
            Assert.True(RealNknClientAdapter.TryCleanupTrackedNodeProcessForTests(pid, startTimeUtcFileTime));
            await WaitUntilAsync(() =>
            {
                try { return proc.HasExited; } catch { return true; }
            }, TimeSpan.FromSeconds(3));
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort
            }
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task Bridge_ProcessKill_RestartsAndUpdatesDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        Assert.True(bundleDir is not null,
            "Bridge runtime not found. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");

        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bridgePath = Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath), $"Missing bundled node runtime: {nodePath}");
        Assert.True(File.Exists(bridgePath), $"Missing bundled bridge script: {bridgePath}");

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("manual-restart", "manual-restart.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await adapter.StartBridgeAsync(cts.Token);
            await adapter.PingBridgeAsync(cts.Token);

            var before = NknRuntimeDiagnostics.Snapshot();
            Assert.True(before.BridgePid > 0, "Bridge PID was not recorded after hello/ping.");

            using (var bridgeProcess = Process.GetProcessById(before.BridgePid))
            {
                bridgeProcess.Kill(entireProcessTree: true);
            }

            await WaitUntilAsync(() =>
            {
                var snap = NknRuntimeDiagnostics.Snapshot();
                return snap.BridgeRestartCount > before.BridgeRestartCount &&
                       snap.BridgePid > 0 &&
                       snap.BridgePid != before.BridgePid;
            }, TimeSpan.FromSeconds(10));

            var after = NknRuntimeDiagnostics.Snapshot();
            Assert.True(after.BridgeRestartCount > before.BridgeRestartCount, "Bridge restart count did not increment.");
            Assert.NotEqual(before.BridgePid, after.BridgePid);
            Assert.Equal("crash", after.BridgeLastExitReason);

            await adapter.DisconnectAsync();
            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
        }
    }

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task NknTransport_RealBridge_SingleMachine_HostJoinApproveAndChat_RoundTrip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        Assert.True(bundleDir is not null,
            "Bridge runtime not found. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");

        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bridgePath = Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath), $"Missing bundled node runtime: {nodePath}");
        Assert.True(File.Exists(bridgePath), $"Missing bundled bridge script: {bridgePath}");

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-real-nkn-manual", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var hostKeyPath = Path.Combine(tempDir, "host-identity.json");
            var helperKeyPath = Path.Combine(tempDir, "helper-identity.json");

            var hostOptions = LoadNknOptionsWithOverrides(hostKeyPath, "manual-host-" + Guid.NewGuid().ToString("N")[..8]);
            var helperOptions = LoadNknOptionsWithOverrides(helperKeyPath, "manual-helper-" + Guid.NewGuid().ToString("N")[..8]);

            var hostIdentity = NknIdentityStore.LoadOrCreate(hostOptions);
            var helperIdentity = NknIdentityStore.LoadOrCreate(helperOptions);

            using var hostClient = new RealNknClientAdapter(hostIdentity, hostOptions);
            using var helperClient = new RealNknClientAdapter(helperIdentity, helperOptions);
            using var host = new NknSignalingTransport(hostClient, hostOptions, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, helperOptions, helperIdentity);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.SessionKeyReady += (_, e) => hostKeyReady.TrySetResult(e.SharedKey);
            helper.SessionKeyReady += (_, e) => helperKeyReady.TrySetResult(e.SharedKey);
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);

            var hostKey = await hostKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            var helperKey = await helperKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            Assert.Equal(hostKey, helperKey);
            Assert.Equal(32, hostKey.Length);

            var chatPayload = Encoding.UTF8.GetBytes("manual-real-nkn-chat-payload");
            await helper.SendChatMessageAsync(chatPayload, cts.Token);
            var received = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            Assert.Equal(chatPayload, received);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task VerifyHandshakeAsync(bool approve)
    {
        var hostAddress = CreateTestPeerAddress();
        using var host = new DevLocalTransport(hostAddress);
        using var joiner = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var joinRequestRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IncomingJoinRequestEventArgs? pendingJoinRequest = null;

        host.IncomingJoinRequest += (_, e) =>
        {
            pendingJoinRequest = e;
            joinRequestRaised.TrySetResult();
        };

        joiner.Approved += (_, _) => approvedRaised.TrySetResult();
        joiner.Rejected += (_, _) => rejectedRaised.TrySetResult();
        joiner.Disconnected += (_, _) => disconnectedRaised.TrySetResult();

        _ = host.HostByAddressAsync(cts.Token);
        await Task.Delay(75, cts.Token);

        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat);
        await WaitStepAsync("joiner join", joiner.JoinByInviteAsync(rawToken, invite, cts.Token), TimeSpan.FromSeconds(3));
        await WaitStepAsync("join request raised", joinRequestRaised.Task, TimeSpan.FromSeconds(3));
        Assert.NotNull(pendingJoinRequest);

        if (approve)
        {
            await WaitStepAsync(
                "approve request",
                pendingJoinRequest!.ApproveAsync(pendingJoinRequest.CreateApprovalDecision(), CancellationToken.None),
                TimeSpan.FromSeconds(3));
        }
        else
        {
            await WaitStepAsync("reject request", pendingJoinRequest!.RejectAsync(CancellationToken.None), TimeSpan.FromSeconds(3));
        }

        if (approve)
        {
            await WaitStepAsync("approved event", approvedRaised.Task, TimeSpan.FromSeconds(3));
            Assert.False(rejectedRaised.Task.IsCompleted);
        }
        else
        {
            await WaitStepAsync("rejected event", rejectedRaised.Task, TimeSpan.FromSeconds(3));
            Assert.False(approvedRaised.Task.IsCompleted);
        }

        // Reject path may close immediately. Approve path should keep the session alive.
        if (approve)
        {
            Assert.False(disconnectedRaised.Task.IsCompleted);
        }

        joiner.Dispose();
        host.Dispose();
        cts.Cancel();
        await Task.Delay(50, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static async Task WaitStepAsync(string stepName, Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out while waiting for step: {stepName}", ex);
        }
    }

    private static PeerAddress GetHostedAddressOrThrow(SessionRuntime runtime)
    {
        if (runtime.CurrentLocalPeerAddress is PeerAddress address)
        {
            return address;
        }

        throw new InvalidOperationException("Active helpee transport did not expose a local peer address.");
    }

    private static async Task<string> WaitForShareInviteAsync(HelpeePageViewModel helpee, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), effectiveTimeout);
        return helpee.ShareInvite;
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities = CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionId = new SessionId($"scripted_session_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, nowUtc.Add(SessionSecurityDefaults.GrantLifetime)));
    }

    private static SessionSecurityState CreateVerifiedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        SessionId? sessionId = null,
        bool inviteValidated = true)
    {
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId ?? new SessionId($"scripted_verified_{Guid.NewGuid():N}"),
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = inviteValidated,
        }).WithHandshakeVerified(helperAddress);
    }

    private static SessionSecurityState CreateExpiredApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionId = new SessionId($"scripted_expired_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, nowUtc.AddSeconds(-5)));
    }

    private static ValidatedInviteV1 CreateValidatedInviteForTarget(
        PeerAddress targetAddress,
        out string rawToken,
        InviteCapabilities capabilities = InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl,
        SessionId? sessionId = null,
        PeerAddress? boundHelperAddress = null)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var create = factory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: targetAddress,
                TargetAddress: targetAddress,
                SessionId: sessionId ?? new SessionId($"sess_smoke_{Guid.NewGuid():N}"),
                Capabilities: capabilities,
                Lifetime: TimeSpan.FromMinutes(5),
                BoundHelperAddress: boundHelperAddress),
            nowUtc);
        Assert.True(create.IsSuccess, create.Message);
        Assert.NotNull(create.Token);

        rawToken = create.Token!;
        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validation = validator.Validate(rawToken, nowUtc.AddSeconds(1));
        Assert.True(validation.IsSuccess, validation.Message);
        Assert.NotNull(validation.Invite);
        return validation.Invite!;
    }

    private static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        while (normalized.Length % 4 != 0)
        {
            normalized += "=";
        }

        return Convert.FromBase64String(normalized);
    }

    private static string CreateTestPeerAddress()
    {
        return $"devlocal.test.{Math.Abs(HashCode.Combine(Environment.ProcessId, Environment.TickCount64))}";
    }

    private static string? FindFileUpwards(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static string GetCurrentBridgeRidForTests()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException("Unsupported macOS architecture for bridge RID test.")
            };
        }

        throw new NotSupportedException("Unsupported platform for bridge RID test.");
    }

    private static void PrepareFakeBridgeBundle(string bridgeRoot)
    {
        CleanupDirectoryIfExists(bridgeRoot);
        Directory.CreateDirectory(bridgeRoot);

        var nodeFileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        File.WriteAllText(Path.Combine(bridgeRoot, "index.js"), "// fake");
        File.WriteAllText(Path.Combine(bridgeRoot, nodeFileName), "fake");
        Directory.CreateDirectory(Path.Combine(bridgeRoot, "node_modules"));
    }

    private static string BuildMockBridgeScript(int delayPongMs, bool respondToPing, bool respondToShutdown = true)
    {
        var delay = Math.Max(0, delayPongMs);
        var respond = respondToPing ? "true" : "false";
        var shutdownRespond = respondToShutdown ? "true" : "false";
        return
$@"'use strict';
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 1, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    if ({respond}) {{
      setTimeout(() => emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }}), {delay});
    }}
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    if ({shutdownRespond}) {{
      emit({{ event:'ok', id: msg.id ?? null, cmd: 'shutdown' }});
      emit({{ event:'disconnected', reason:'shutdown' }});
      setTimeout(() => process.exit(0), 10);
    }}
    return;
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

    private static string BuildMockBridgeScriptWithCustomConnect(string connectBehaviorJs, int delayPongMs = 0, bool respondToPing = true, bool respondToShutdown = true)
    {
        var delay = Math.Max(0, delayPongMs);
        var respond = respondToPing ? "true" : "false";
        var shutdownRespond = respondToShutdown ? "true" : "false";
        return
$@"'use strict';
const fs = require('fs');
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
let connectCount = 0;
const connectIds = [];
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 1, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    if ({respond}) {{
      setTimeout(() => emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }}), {delay});
    }}
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    if ({shutdownRespond}) {{
      emit({{ event:'ok', id: msg.id ?? null, cmd: 'shutdown' }});
      emit({{ event:'disconnected', reason:'shutdown' }});
      setTimeout(() => process.exit(0), 10);
    }}
    return;
  }}
  if (msg.cmd === 'connect') {{
    {connectBehaviorJs}
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

    private static string BuildMockBridgeScriptWithStderrSpam()
    {
        return
@"'use strict';
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity, terminal: false });
function emit(obj) { process.stdout.write(JSON.stringify(obj) + '\n'); }
let spamTimer = null;
function startSpam() {
  if (spamTimer) return;
  let n = 0;
  spamTimer = setInterval(() => {
    for (let i = 0; i < 50; i++) {
      process.stderr.write('spam-line-' + (n++) + ' xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\\n');
    }
  }, 5);
}
function stopSpam() {
  if (spamTimer) {
    clearInterval(spamTimer);
    spamTimer = null;
  }
}
rl.on('line', (line) => {
  if (!line || !line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { emit({ event:'error', id:null, cmd:null, reason:'Invalid JSON' }); return; }
  if (msg.cmd === 'hello') { emit({ event:'hello_ok', id: msg.id ?? null, protocol: 1, sdk: 'mock-sdk@1.0.0' }); startSpam(); return; }
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) { emit({ type:'pong', id: msg.id ?? null, ts: Date.now() }); return; }
  if (msg.cmd === 'shutdown') {
    emit({ event:'ok', id: msg.id ?? null, cmd: 'shutdown' });
    emit({ event:'disconnected', reason:'shutdown' });
    stopSpam();
    setTimeout(() => process.exit(0), 10);
    return;
  }
  emit({ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null });
});";
    }

    private static void CleanupDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private static void SetPrivateField<TTarget>(TTarget target, string fieldName, object? value)
    {
        var field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? GetPrivateField<TTarget>(TTarget target, string fieldName)
    {
        var field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private static void SetPrivateFieldDynamic(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length)
            ?? methods[0];
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static List<string> FindOperationalLogLines(Func<string, bool> predicate)
    {
        var logText = File.Exists(LocalOperationalLog.LogFilePath)
            ? File.ReadAllText(LocalOperationalLog.LogFilePath)
            : string.Empty;
        return logText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(predicate)
            .ToList();
    }

    private static int GetOperationalLogLength()
    {
        return File.Exists(LocalOperationalLog.LogFilePath)
            ? File.ReadAllText(LocalOperationalLog.LogFilePath).Length
            : 0;
    }

    private static string ReadOperationalLogTail(int startIndex)
    {
        var logText = File.Exists(LocalOperationalLog.LogFilePath)
            ? File.ReadAllText(LocalOperationalLog.LogFilePath)
            : string.Empty;
        if (startIndex <= 0 || startIndex >= logText.Length)
        {
            return startIndex >= logText.Length ? string.Empty : logText;
        }

        return logText[startIndex..];
    }

    private static void SetPrivateProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private static bool InvokeCanEndForPhase(Type viewModelType, SessionUiPhase phase)
    {
        var method = viewModelType.GetMethod("CanEndForPhase", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object[] { phase })!;
    }

    private static TransportRuntimeConfig CreateDevLocalTestConfig()
    {
        var previous = Environment.GetEnvironmentVariable("FRH_TRANSPORT");

        try
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", null);
            return TransportRuntimeConfig.Select();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", previous);
        }
    }

    private static byte[] SHA256LikeDeterministicBytes(string input, int length)
    {
        var source = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        if (length == source.Length)
        {
            return source;
        }

        var buffer = new byte[length];
        Array.Copy(source, buffer, length);
        return buffer;
    }

    private static Bitmap CreateTestBitmap(int width, int height)
    {
        _ = width;
        _ = height;
        return (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
    }

    private static byte[] CreateEncryptedChatEnvelopeBytes(
        byte[] key,
        string messageId,
        string text,
        long timestampUnixMs,
        string nonceSeed)
    {
        var payload = new ChatMessagePayload
        {
            MessageId = messageId,
            Text = text,
            TimestampUnixMilliseconds = timestampUnixMs,
        };

        var payloadBytes = ChatEnvelopeCodec.SerializePayload(payload);
        var nonce = SHA256LikeDeterministicBytes(nonceSeed, ChatAesGcmCrypto.NonceSize);
        var encrypted = ChatAesGcmCrypto.EncryptWithNonce(key, payloadBytes, nonce);

        var envelope = new ChatEnvelope
        {
            Version = ChatProtocol.Version,
            Type = ChatProtocol.ChatMessageType,
            NonceBase64 = Convert.ToBase64String(encrypted.Nonce),
            TagBase64 = Convert.ToBase64String(encrypted.Tag),
            CiphertextBase64 = Convert.ToBase64String(encrypted.Ciphertext),
        };

        return ChatEnvelopeCodec.SerializeEnvelope(envelope);
    }

    private static string? TryFindBridgeBundleDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "bridge", "win-x64");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveBridgeRuntimeDirectoryForHealthCheck(out string attemptedPath, out string source)
    {
        var envValue = Environment.GetEnvironmentVariable("NLINK_BRIDGE_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            source = "env:NLINK_BRIDGE_RUNTIME_DIR";
            attemptedPath = ResolvePathFromRepoRoot(envValue);
            return Directory.Exists(attemptedPath) ? attemptedPath : null;
        }

        source = "default:artifacts/bridge/win-x64";
        attemptedPath = ResolvePathFromRepoRoot(Path.Combine("artifacts", "bridge", "win-x64"));
        if (Directory.Exists(attemptedPath))
        {
            return attemptedPath;
        }

        return TryFindBridgeBundleDirectory();
    }

    private static string ResolvePathFromRepoRoot(string pathValue)
    {
        if (Path.IsPathRooted(pathValue))
        {
            return Path.GetFullPath(pathValue);
        }

        var versionPath = FindFileUpwards("VERSION");
        if (!string.IsNullOrWhiteSpace(versionPath))
        {
            var repoRoot = Path.GetDirectoryName(versionPath)!;
            return Path.GetFullPath(Path.Combine(repoRoot, pathValue));
        }

        return Path.GetFullPath(pathValue);
    }

    private static NknTransportOptions LoadNknOptionsWithOverrides(string keyPath, string identifier)
    {
        var prevKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
        var prevIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", identifier);
            return NknTransportOptions.Load();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", prevKeyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", prevIdentifier);
        }
    }

#pragma warning disable CS0067
    private sealed class FakeSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IAddressHostSignalingTransport
    {
        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;

        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => Task.CompletedTask;

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => Task.CompletedTask;

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void RaiseSessionKeyReady(byte[] sharedKey)
        {
            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
        }

        public void RaiseChatMessage(byte[] payload)
        {
            ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(payload));
        }
    }
#pragma warning restore CS0067

    private sealed class FakeClipboardService : IClipboardService
    {
        public string LastText { get; private set; } = string.Empty;

        public Task SetTextAsync(string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingRemoteInputInjector : IRemoteInputInjector
    {
        public bool IsSupported => true;

        public int MouseMoveCount { get; private set; }
        public int KeyInjectionCount { get; private set; }

        public void InjectMouseMoveAbsolute(int xPx, int yPx)
        {
            _ = xPx;
            _ = yPx;
            MouseMoveCount++;
        }

        public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
        {
            _ = button;
            _ = action;
        }

        public void InjectMouseWheel(int deltaX, int deltaY)
        {
            _ = deltaX;
            _ = deltaY;
        }

        public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
        {
            _ = key;
            _ = action;
            _ = mods;
            KeyInjectionCount++;
        }
    }

    private sealed class CountingTransportFactory
    {
        private readonly Func<ISignalingTransport> factory;

        public CountingTransportFactory(Func<ISignalingTransport> factory)
        {
            this.factory = factory;
        }

        public int CreateCount { get; private set; }

        public ISignalingTransport Create()
        {
            CreateCount++;
            return factory();
        }
    }

    private sealed class ScriptedSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport
    {
        private readonly Func<string, CancellationToken, Task> onJoinByAddressAsync;
        private readonly Func<string, ValidatedInviteV1, CancellationToken, Task> onJoinByInviteAsync;
        private readonly Func<CancellationToken, Task> onHostByAddressAsync;
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> onSendChatAsync;
        private readonly Func<ControlRequestMessageV1, CancellationToken, Task> onSendControlRequestAsync;
        private readonly Func<ControlResponseMessageV1, CancellationToken, Task> onSendControlResponseAsync;
        private readonly Func<ControlStartMessageV1, CancellationToken, Task> onSendControlStartAsync;
        private readonly Func<ControlStopMessageV1, CancellationToken, Task> onSendControlStopAsync;
        private readonly Func<ControlInputMessageV1, CancellationToken, Task> onSendControlInputAsync;
        private readonly Func<ControlInputAckV1, CancellationToken, Task> onSendControlAckAsync;
        private readonly Func<ControlStateSnapshotV1, CancellationToken, Task> onSendControlStateSnapshotAsync;
        private readonly Func<ControlDisplayInfoMessageV1, CancellationToken, Task> onSendControlDisplayInfoAsync;
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public ScriptedSignalingTransport(
            Func<string, CancellationToken, Task>? onJoinByAddressAsync = null,
            Func<string, ValidatedInviteV1, CancellationToken, Task>? onJoinByInviteAsync = null,
            Func<CancellationToken, Task>? onHostByAddressAsync = null,
            string? localPeerAddress = null,
            Func<ReadOnlyMemory<byte>, CancellationToken, Task>? onSendChatAsync = null,
            Func<ControlRequestMessageV1, CancellationToken, Task>? onSendControlRequestAsync = null,
            Func<ControlResponseMessageV1, CancellationToken, Task>? onSendControlResponseAsync = null,
            Func<ControlStartMessageV1, CancellationToken, Task>? onSendControlStartAsync = null,
            Func<ControlStopMessageV1, CancellationToken, Task>? onSendControlStopAsync = null,
            Func<ControlInputMessageV1, CancellationToken, Task>? onSendControlInputAsync = null,
            Func<ControlInputAckV1, CancellationToken, Task>? onSendControlAckAsync = null,
            Func<ControlStateSnapshotV1, CancellationToken, Task>? onSendControlStateSnapshotAsync = null,
            Func<ControlDisplayInfoMessageV1, CancellationToken, Task>? onSendControlDisplayInfoAsync = null,
            bool localSupportsRemoteControl = true,
            bool remoteSupportsRemoteControl = true)
        {
            this.onJoinByAddressAsync = onJoinByAddressAsync ?? ((_, ct) => Task.Delay(Timeout.Infinite, ct));
            this.onJoinByInviteAsync = onJoinByInviteAsync ?? ((_, invite, ct) => this.onJoinByAddressAsync(invite.TargetAddress.Value, ct));
            this.onHostByAddressAsync = onHostByAddressAsync ?? (ct => Task.Delay(Timeout.Infinite, ct));
            LocalPeerAddress = string.IsNullOrWhiteSpace(localPeerAddress) ? "scripted.local.peer" : localPeerAddress.Trim();
            this.onSendChatAsync = onSendChatAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlRequestAsync = onSendControlRequestAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlResponseAsync = onSendControlResponseAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlStartAsync = onSendControlStartAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlStopAsync = onSendControlStopAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlInputAsync = onSendControlInputAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlAckAsync = onSendControlAckAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlStateSnapshotAsync = onSendControlStateSnapshotAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlDisplayInfoAsync = onSendControlDisplayInfoAsync ?? ((_, _) => Task.CompletedTask);
            LocalSupportsRemoteControl = localSupportsRemoteControl;
            RemoteSupportsRemoteControl = remoteSupportsRemoteControl;
        }

        public string LocalPeerAddress { get; }
        public bool LocalSupportsRemoteControl { get; }
        public bool RemoteSupportsRemoteControl { get; }
        public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
        public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
        public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
        public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
        public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
        public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
        public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
        public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => onHostByAddressAsync(ct);

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => onJoinByAddressAsync(peerAddress, ct);

        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
            => onJoinByInviteAsync(inviteToken, invite, ct);

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => onSendChatAsync(payload, ct);
        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct) => onSendControlRequestAsync(message, ct);
        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct) => onSendControlResponseAsync(message, ct);
        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct) => onSendControlStartAsync(message, ct);
        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct) => onSendControlStopAsync(message, ct);
        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct) => onSendControlInputAsync(message, ct);
        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct) => onSendControlAckAsync(message, ct);
        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => onSendControlStateSnapshotAsync(message, ct);
        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct) => onSendControlDisplayInfoAsync(message, ct);

        public void RaiseDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }

    private sealed class FixedCaptureSourceFactory : IScreenCaptureSourceFactory
    {
        private readonly IScreenCaptureSource source;

        public FixedCaptureSourceFactory(IScreenCaptureSource source)
        {
            this.source = source;
        }

        public IScreenCaptureSource Create() => source;
    }

    private sealed class ControlledDelayScheduler
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> pending = new();

        public int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return pending.Count(t => !t.Task.IsCompleted);
                }
            }
        }

        public Task DelayAsync(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            ctr = ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                ctr.Dispose();
            });

            lock (gate)
            {
                pending.Add(tcs);
            }

            return tcs.Task;
        }

        public void CompleteLatest()
        {
            lock (gate)
            {
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].TrySetResult())
                    {
                        return;
                    }
                }
            }

            throw new InvalidOperationException("No pending delay task to complete.");
        }
    }

    private sealed class FakeScreenShareClock : IScreenShareClock
    {
        private DateTimeOffset utcNow;

        public FakeScreenShareClock(DateTimeOffset initialUtcNow)
        {
            utcNow = initialUtcNow;
        }

        public DateTimeOffset UtcNow => utcNow;

        public void Advance(TimeSpan by)
        {
            utcNow = utcNow.Add(by);
        }
    }

    private sealed class FakeSessionTransportNetwork
    {
        private readonly object gate = new();
        private readonly Dictionary<string, FakeSessionTransport> hostsByAddress = new(StringComparer.Ordinal);

        public FakeSessionTransport CreateTransport(string address)
        {
            return new FakeSessionTransport(this, address);
        }

        public void RegisterHost(string address, FakeSessionTransport host)
        {
            lock (gate)
            {
                hostsByAddress[address] = host;
            }
        }

        public void UnregisterHost(FakeSessionTransport transport)
        {
            lock (gate)
            {
                foreach (var pair in hostsByAddress.ToArray())
                {
                    if (ReferenceEquals(pair.Value, transport))
                    {
                        hostsByAddress.Remove(pair.Key);
                    }
                }
            }
        }

        public FakeSessionTransport? TryFindHost(string address)
        {
            lock (gate)
            {
                return hostsByAddress.TryGetValue(address, out var host) ? host : null;
            }
        }
    }

    private sealed class FakeSessionTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, IHostReadySignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = SmokeTests.SHA256LikeDeterministicBytes("session-runtime-repeat-key", 32);
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        private FakeSessionTransport? peer;
        private bool disposed;

        public FakeSessionTransport(FakeSessionTransportNetwork network, string address)
        {
            this.network = network;
            Address = address;
        }

        public string Address { get; }
        public string LocalPeerAddress => Address;

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);

        public Task HostByAddressAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            network.RegisterHost(Address, this);
            UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(Address)));
            hostReadyTcs.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
        {
            ThrowIfDisposed();
            var host = network.TryFindHost(peerAddress) ?? throw new TimeoutException("Host not found.");
            return JoinCoreAsync(
                host,
                new SessionId($"fake_session_{Guid.NewGuid():N}"),
                SessionSecurityDefaults.AllCapabilityGrants,
                inviteValidated: true);
        }

        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(inviteToken))
            {
                throw new ArgumentException("Invite token is required.", nameof(inviteToken));
            }

            ArgumentNullException.ThrowIfNull(invite);
            var host = network.TryFindHost(invite.TargetAddress.Value) ?? throw new TimeoutException("Host not found.");
            var helperAddress = new PeerAddress(Address);
            if (invite.BoundHelperAddress is not null && invite.BoundHelperAddress != helperAddress)
            {
                throw new InvalidOperationException("Invite token is bound to a different helper identity.");
            }

            return JoinCoreAsync(
                host,
                invite.SessionId,
                invite.Payload.Capabilities.ToCapabilityGrant(),
                inviteValidated: true);
        }

        private Task JoinCoreAsync(
            FakeSessionTransport host,
            SessionId sessionId,
            CapabilityGrant requestedCapabilities,
            bool inviteValidated)
        {
            peer = host;
            host.peer = this;
            var helpeeAddress = new PeerAddress(host.Address);
            var helperAddress = new PeerAddress(Address);
            var approvalRequest = new ApprovalRequest(
                helperAddress,
                requestedCapabilities,
                sessionId);

            var verifiedState = CreateVerifiedSecurityState(sessionId, helpeeAddress, helperAddress, inviteValidated);
            UpdateSessionSecurityState(verifiedState);
            host.UpdateSessionSecurityState(verifiedState);

            var joinRequest = new IncomingJoinRequestEventArgs(
                approveAsync: (decision, _) =>
                {
                    if (decision is null)
                    {
                        throw new InvalidOperationException("Explicit approval decision is required.");
                    }

                    ValidateApprovalDecision(approvalRequest, decision);
                    var grant = decision.ToGrant();
                    host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.WithApproval(grant));
                    UpdateSessionSecurityState(CurrentSessionSecurityState.WithApproval(grant));
                    host.SessionKeyReady?.Invoke(host, new TransportSessionKeyReadyEventArgs(host.sharedKey));
                    SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
                    host.Approved?.Invoke(host, EventArgs.Empty);
                    Approved?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                rejectAsync: _ =>
                {
                    host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.Invalidate("local_reject"));
                    UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("local_reject"));
                    Rejected?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                approvalRequest: approvalRequest);

            host.IncomingJoinRequest?.Invoke(host, joinRequest);
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            var target = peer ?? throw new InvalidOperationException("No peer connected.");
            target.ChatMessageReceived?.Invoke(target, new TransportChatMessageEventArgs(payload.ToArray()));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            network.UnregisterHost(this);

            if (peer is { } target)
            {
                peer = null;
                target.peer = null;
                UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.UpdateSessionSecurityState(target.CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.Disconnected?.Invoke(target, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FakeSessionTransport));
            }
        }

        private void UpdateSessionSecurityState(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        private static SessionSecurityState CreateVerifiedSecurityState(
            SessionId sessionId,
            PeerAddress helpeeAddress,
            PeerAddress helperAddress,
            bool inviteValidated)
        {
            return (SessionSecurityState.Empty with
            {
                SessionId = sessionId,
                HelpeeAddress = helpeeAddress,
                HelperAddress = helperAddress,
                InviteValidated = inviteValidated,
            }).WithHandshakeVerified(helperAddress);
        }

        private static void ValidateApprovalDecision(ApprovalRequest approvalRequest, ApprovalDecision decision)
        {
            if (decision.SessionId != approvalRequest.SessionId ||
                decision.HelperIdentity != approvalRequest.HelperIdentity ||
                decision.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                (decision.ApprovedCapabilities & ~approvalRequest.RequestedCapabilities) != 0)
            {
                throw new InvalidOperationException("Approval decision does not match the pending approval request.");
            }
        }
    }

    private sealed class FakeBridgeProcessRunner : IBridgeProcessRunner
    {
        public bool WasForcedKillRequested { get; set; }
    }

    private sealed class FakeStatusPresenterSource : IStatusPresenterSource
    {
        private SessionRuntimeState uiState = SessionRuntimeState.Idle;
        private TransportState transportState = TransportState.Idle;
        private string statusText = string.Empty;
        private TransportFailure? failure;
        private long attempt;

        public event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;
        public event EventHandler<SessionRuntimeTransientStatusChangedEventArgs>? TransientStatusChanged;

        public SessionRuntimeState State => uiState;
        public TransportState TransportLifecycleState => transportState;
        public string StatusText => statusText;
        public TransportFailure? LastTransportFailure => failure;

        public DiagnosticsSnapshot GetDiagnosticsSnapshot()
            => new(
                CurrentState: transportState.ToString(),
                SessionUiState: uiState.ToString(),
                AttemptNumber: attempt,
                LastFailureCategory: failure?.Category.ToString() ?? string.Empty,
                LastFailureMessage: failure?.Message ?? string.Empty,
                LastConnectDurationMs: null,
                LastHandshakeDurationMs: null,
                LastBridgeStartDurationMs: null);

        public void SetAttempt(long value) => attempt = value;
        public void SetTransportState(TransportState state) => transportState = state;
        public void SetSessionUiState(SessionRuntimeState state) => uiState = state;
        public void SetStatusText(string text) => statusText = text ?? string.Empty;
        public void SetFailure(TransportFailure? transportFailure) => failure = transportFailure;

        public void RaiseStateChanged()
            => StateChanged?.Invoke(this, new SessionRuntimeStateChangedEventArgs(uiState, SessionRuntimeRole.Helper, statusText));

        public void RaiseTransient(bool isVisible, string text, bool canCancel)
        {
            statusText = text ?? string.Empty;
            TransientStatusChanged?.Invoke(this, new SessionRuntimeTransientStatusChangedEventArgs(isVisible, statusText, canCancel));
        }
    }

    private sealed class FakeManualTimer : NLink.App.Services.ITimer
    {
        private Action? callback;
        private bool disposed;

        public bool IsRunning { get; private set; }

        public void Start(TimeSpan dueTime, TimeSpan period, Action callback)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            callback = null;
        }

        public void Tick()
        {
            if (disposed || !IsRunning || callback is null)
            {
                return;
            }

            callback();
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }
    }

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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

    [Trait("Category", "Smoke")]
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
