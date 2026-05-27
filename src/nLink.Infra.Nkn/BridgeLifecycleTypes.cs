namespace NLink.Infra.Nkn;

internal enum BridgeLifecycleEventKind
{
    Spawned,
    Ready,
    Exited,
    ReceiveStallRecoveryStarted,
    ReceiveStallRecoveryCompleted,
    ReceiveStallRecoveryReceiveResumed,
    ReceiveStallRecoveryExhausted,
}

internal enum BridgeStartMode
{
    Cold,
    Warm,
}

internal enum BridgeExitReasonKind
{
    Normal,
    Crash,
    Killed,
    Unknown,
}

internal readonly record struct BridgeLifecycleEvent(
    BridgeLifecycleEventKind Kind,
    BridgeStartMode? StartMode,
    int? Pid,
    double? ReadyTimeMs,
    double? PingRttMs,
    double? UptimeMs,
    int? ExitCode,
    BridgeExitReasonKind? ExitReasonKind,
    string ExitReasonText);

internal readonly record struct BridgeExitClassification(
    BridgeExitReasonKind ReasonKind,
    string ReasonText);

internal interface IBridgeProcessRunner
{
    bool WasForcedKillRequested { get; }
}

internal readonly record struct BridgeProcessDebugState(
    bool HasProcessReference,
    bool HasStdinReference,
    bool HasStdoutReaderTaskReference,
    bool HasStderrReaderTaskReference,
    int TrackedPid);

internal static class BridgeExitClassifier
{
    internal static BridgeExitClassification Classify(bool shuttingDown, bool forcedKill, int? exitCode)
    {
        if (forcedKill)
        {
            return new BridgeExitClassification(BridgeExitReasonKind.Killed, "killed");
        }

        if (!exitCode.HasValue)
        {
            return new BridgeExitClassification(BridgeExitReasonKind.Unknown, "unknown");
        }

        if (shuttingDown)
        {
            return exitCode.Value == 0
                ? new BridgeExitClassification(BridgeExitReasonKind.Normal, "normal")
                : new BridgeExitClassification(BridgeExitReasonKind.Unknown, "unknown");
        }

        return new BridgeExitClassification(
            exitCode.Value == 0 ? BridgeExitReasonKind.Unknown : BridgeExitReasonKind.Crash,
            exitCode.Value == 0 ? "unknown" : "crash");
    }
}
