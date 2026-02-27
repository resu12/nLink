namespace NLink.Core.Metrics;

public static class MetricsNames
{
    public const string TransportConnectAttemptsTotal = "transport_connect_attempts_total";
    public const string TransportConnectSuccessTotal = "transport_connect_success_total";
    public const string TransportConnectFailureTotal = "transport_connect_failure_total";
    public const string TransportReconnectAttemptsTotal = "transport_reconnect_attempts_total";
    public const string TransportFailureTotal = "transport_failure_total";
    public const string TransportConnectDurationMs = "transport_connect_duration_ms";
    public const string TransportHandshakeDurationMs = "transport_handshake_duration_ms";
    public const string BridgeStartTotal = "bridge_start_total";
    public const string BridgeRestartTotal = "bridge_restart_total";
    public const string BridgeSpawnTotal = "bridge_spawn_total";
    public const string BridgeExitTotal = "bridge_exit_total";
    public const string BridgeCrashTotal = "bridge_crash_total";
    public const string BridgeKilledTotal = "bridge_killed_total";
    public const string BridgeUnresponsiveTotal = "bridge_unresponsive_total";
    public const string BridgeStartDurationMs = "bridge_start_duration_ms";
    public const string BridgeReadyTimeMs = "bridge_ready_time_ms";
    public const string BridgeColdStartMs = "bridge_cold_start_ms";
    public const string BridgePingRttMs = "bridge_ping_rtt_ms";
    public const string BridgeUptimeMs = "bridge_uptime_ms";
    public const string BridgeProcessRunning = "bridge_process_running";
    public const string BridgePid = "bridge_pid";
    public const string BridgeExitCode = "bridge_exit_code";
    public const string BridgeWarmStartRatio = "bridge_warm_start_ratio";
    public const string StateStuckCount = "state_stuck_count";
    public const string ActiveSessions = "active_sessions";
    public const string ActiveConnectAttempts = "active_connect_attempts";
    public const string ActiveRetryTimers = "active_retry_timers";
    public const string ActiveWatchdogs = "active_watchdogs";
    public const string ActiveTransportTasks = "active_transport_tasks";
    public const string ActiveBridgeIoReaders = "active_bridge_io_readers";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        TransportConnectAttemptsTotal,
        TransportConnectSuccessTotal,
        TransportConnectFailureTotal,
        TransportReconnectAttemptsTotal,
        TransportFailureTotal,
        TransportConnectDurationMs,
        TransportHandshakeDurationMs,
        BridgeStartTotal,
        BridgeRestartTotal,
        BridgeSpawnTotal,
        BridgeExitTotal,
        BridgeCrashTotal,
        BridgeKilledTotal,
        BridgeUnresponsiveTotal,
        BridgeStartDurationMs,
        BridgeReadyTimeMs,
        BridgePingRttMs,
        BridgeUptimeMs,
        BridgeProcessRunning,
        BridgePid,
        BridgeExitCode,
        BridgeWarmStartRatio,
        StateStuckCount,
        ActiveSessions,
        ActiveConnectAttempts,
        ActiveRetryTimers,
        ActiveWatchdogs,
        ActiveTransportTasks,
        ActiveBridgeIoReaders,
    };
}
