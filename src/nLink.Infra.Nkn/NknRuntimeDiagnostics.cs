using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public static class NknRuntimeDiagnostics
{
    private static readonly object Gate = new();
    private static bool initialized;
    private static string address = "(not initialized)";
    private static string identifier = "(not initialized)";
    private static string keyPath = "(not initialized)";
    private static string seedRpc = "(default)";
    private static string lastError = string.Empty;
    private static int bridgePid;
    private static string nodeVersion = "(unknown)";
    private static long bridgeLastPongUtcTicks;
    private static long bridgeRestartCount;
    private static int bridgeLastExitCode = -1;
    private static string bridgeLastExitReason = "(none)";
    private static double bridgeLastUptimeMs = -1;
    private static long messagesSent;
    private static long messagesReceived;
    private static long bridgeRawMessagesReceived;
    private static long screenShareOutboundBusyDrops;
    private static long screenSharePayloadBytesSent;
    private static long screenShareMessagesSent;
    private static long screenShareBridgeBytesSent;
    private static long highPriorityControlQueueOverflows;
    private static long highPriorityControlRejected;
    private static long highPriorityControlCoalesced;
    private static long highPriorityControlDroppedForStop;
    private static string lastBridgeMessageSource = "(none)";
    private static bool? lastBridgeMessageIsTopic;
    private static string lastEnvelopeType = "(none)";
    private static string lastEnvelopeDropReason = "(none)";
    private static string lastProgressEventType = "(none)";
    private static long lastProgressEventUtcTicks;
    private static string lastSelectedRpc = "(none)";
    private static bool authoritativeConnectedAddressResolved;
    private static long joinRequestsReceived;
    private static long incomingJoinRequestRaisedCount;
    private static long acksReceived;
    private static long acksIgnoredSourceMismatch;
    private static string lastDisconnectReason = "(none)";
    private static double firstColdStartMs = -1d;
    private static long firstColdStartUtcTicks;
    private static int firstColdStartObserved;

    public static void SetIdentity(string address, string identifier, string keyPath, string? seedRpc)
    {
        lock (Gate)
        {
            initialized = true;
            NknRuntimeDiagnostics.address = string.IsNullOrWhiteSpace(address) ? "(unknown)" : address;
            NknRuntimeDiagnostics.identifier = string.IsNullOrWhiteSpace(identifier) ? "(unknown)" : identifier;
            NknRuntimeDiagnostics.keyPath = string.IsNullOrWhiteSpace(keyPath) ? "(unknown)" : keyPath;
            NknRuntimeDiagnostics.seedRpc = string.IsNullOrWhiteSpace(seedRpc) ? "(default)" : seedRpc!;
            authoritativeConnectedAddressResolved = false;
        }
    }

    public static void SetAuthoritativeConnectedAddressResolved(bool resolved)
    {
        lock (Gate)
        {
            authoritativeConnectedAddressResolved = resolved;
        }
    }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (initialized)
            {
                return;
            }
        }

        try
        {
            var options = NknTransportOptions.Load();
            var identity = NknIdentityStore.LoadOrCreate(options);
            SetIdentity(identity.Address, identity.Identifier, options.KeyPath, options.SeedRpc);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
        }
    }

    public static void IncrementMessagesSent() => Interlocked.Increment(ref messagesSent);

    public static void IncrementMessagesReceived() => Interlocked.Increment(ref messagesReceived);

    public static void IncrementBridgeRawMessagesReceived() => Interlocked.Increment(ref bridgeRawMessagesReceived);

    public static void IncrementScreenShareOutboundBusyDrops() => Interlocked.Increment(ref screenShareOutboundBusyDrops);

    public static void AddScreenSharePayloadBytesSent(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref screenSharePayloadBytesSent, bytes);
    }

    public static void IncrementScreenShareMessagesSent() => Interlocked.Increment(ref screenShareMessagesSent);

    public static void AddScreenShareBridgeBytesSent(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref screenShareBridgeBytesSent, bytes);
    }

    public static void IncrementHighPriorityControlQueueOverflows() => Interlocked.Increment(ref highPriorityControlQueueOverflows);

    public static void IncrementHighPriorityControlRejected() => Interlocked.Increment(ref highPriorityControlRejected);

    public static void AddHighPriorityControlCoalesced(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref highPriorityControlCoalesced, count);
    }

    public static void AddHighPriorityControlDroppedForStop(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref highPriorityControlDroppedForStop, count);
    }

    public static void SetLastBridgeMessage(string? source, bool isTopic)
    {
        lock (Gate)
        {
            lastBridgeMessageSource = string.IsNullOrWhiteSpace(source) ? "(none)" : source!;
            lastBridgeMessageIsTopic = isTopic;
        }
    }

    public static void SetLastEnvelopeType(string? type)
    {
        lock (Gate)
        {
            lastEnvelopeType = string.IsNullOrWhiteSpace(type) ? "(none)" : type!;
        }
    }

    public static void SetLastEnvelopeDropReason(string? reason)
    {
        lock (Gate)
        {
            lastEnvelopeDropReason = string.IsNullOrWhiteSpace(reason) ? "(none)" : reason!;
        }
    }

    public static void SetLastProgressEvent(string? eventType, DateTimeOffset utcTime, string? selectedRpc = null)
    {
        lock (Gate)
        {
            lastProgressEventType = string.IsNullOrWhiteSpace(eventType) ? "(none)" : eventType!;
            if (!string.IsNullOrWhiteSpace(selectedRpc))
            {
                lastSelectedRpc = selectedRpc!;
            }
        }

        Interlocked.Exchange(ref lastProgressEventUtcTicks, utcTime.UtcDateTime.Ticks);
    }

    public static void IncrementJoinRequestsReceived() => Interlocked.Increment(ref joinRequestsReceived);

    public static void IncrementIncomingJoinRequestRaised() => Interlocked.Increment(ref incomingJoinRequestRaisedCount);

    public static void IncrementAcksReceived() => Interlocked.Increment(ref acksReceived);

    public static void IncrementAcksIgnoredSourceMismatch() => Interlocked.Increment(ref acksIgnoredSourceMismatch);

    public static void SetLastDisconnectReason(string? reason)
    {
        lock (Gate)
        {
            lastDisconnectReason = string.IsNullOrWhiteSpace(reason)
                ? "(none)"
                : SanitizeDiagnosticText(reason!);
        }
    }

    public static void SetBridgeProcessInfo(int pid, string? nodeVersion)
    {
        lock (Gate)
        {
            if (pid > 0)
            {
                bridgePid = pid;
            }

            if (!string.IsNullOrWhiteSpace(nodeVersion))
            {
                NknRuntimeDiagnostics.nodeVersion = nodeVersion!;
            }
        }
    }

    public static void SetBridgeLastPongUtc(DateTimeOffset utcTime) =>
        Interlocked.Exchange(ref bridgeLastPongUtcTicks, utcTime.Ticks);

    public static void IncrementBridgeRestartCount() => Interlocked.Increment(ref bridgeRestartCount);

    public static void SetBridgeLastExit(int? exitCode, string? reason)
    {
        lock (Gate)
        {
            bridgeLastExitCode = exitCode ?? -1;
            bridgeLastExitReason = string.IsNullOrWhiteSpace(reason) ? "(none)" : reason!;
        }
    }

    public static void SetBridgeLastUptimeMs(double? uptimeMs)
    {
        lock (Gate)
        {
            bridgeLastUptimeMs = uptimeMs.GetValueOrDefault(-1d);
        }
    }

    public static void RecordFirstColdStart(double? readyTimeMs, DateTimeOffset utcTime)
    {
        if (!readyTimeMs.HasValue || readyTimeMs.Value < 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref firstColdStartObserved, 1, 0) != 0)
        {
            return;
        }

        lock (Gate)
        {
            firstColdStartMs = readyTimeMs.Value;
        }

        Interlocked.Exchange(ref firstColdStartUtcTicks, utcTime.UtcDateTime.Ticks);
    }

    public static void SetLastError(string message)
    {
        lock (Gate)
        {
            lastError = SanitizeDiagnosticText(message);
        }
    }

    public static void SetLastError(Exception ex)
    {
        lock (Gate)
        {
            lastError = ex.GetType().Name + ": " + SanitizeDiagnosticText(ex.Message);
        }
    }

    public static NknRuntimeDiagnosticsSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new NknRuntimeDiagnosticsSnapshot(
                Address: address,
                Identifier: identifier,
                KeyPath: keyPath,
                SeedRpc: seedRpc,
                MessagesSent: Interlocked.Read(ref messagesSent),
                MessagesReceived: Interlocked.Read(ref messagesReceived),
                LastError: string.IsNullOrWhiteSpace(lastError) ? "(none)" : lastError,
                BridgePid: bridgePid,
                NodeVersion: string.IsNullOrWhiteSpace(nodeVersion) ? "(unknown)" : nodeVersion,
                BridgeLastPongUtcTicks: Interlocked.Read(ref bridgeLastPongUtcTicks),
                BridgeRestartCount: Interlocked.Read(ref bridgeRestartCount),
                BridgeLastExitCode: bridgeLastExitCode,
                BridgeLastExitReason: string.IsNullOrWhiteSpace(bridgeLastExitReason) ? "(none)" : bridgeLastExitReason,
                BridgeLastUptimeMs: bridgeLastUptimeMs,
                BridgeRawMessagesReceived: Interlocked.Read(ref bridgeRawMessagesReceived),
                ScreenShareOutboundBusyDrops: Interlocked.Read(ref screenShareOutboundBusyDrops),
                ScreenSharePayloadBytesSent: Interlocked.Read(ref screenSharePayloadBytesSent),
                ScreenShareMessagesSent: Interlocked.Read(ref screenShareMessagesSent),
                ScreenShareBridgeBytesSent: Interlocked.Read(ref screenShareBridgeBytesSent),
                HighPriorityControlQueueOverflows: Interlocked.Read(ref highPriorityControlQueueOverflows),
                HighPriorityControlRejected: Interlocked.Read(ref highPriorityControlRejected),
                HighPriorityControlCoalesced: Interlocked.Read(ref highPriorityControlCoalesced),
                HighPriorityControlDroppedForStop: Interlocked.Read(ref highPriorityControlDroppedForStop),
                LastBridgeMessageSource: string.IsNullOrWhiteSpace(lastBridgeMessageSource) ? "(none)" : lastBridgeMessageSource,
                LastBridgeMessageIsTopic: lastBridgeMessageIsTopic,
                LastEnvelopeType: string.IsNullOrWhiteSpace(lastEnvelopeType) ? "(none)" : lastEnvelopeType,
                LastEnvelopeDropReason: string.IsNullOrWhiteSpace(lastEnvelopeDropReason) ? "(none)" : lastEnvelopeDropReason,
                LastProgressEventType: string.IsNullOrWhiteSpace(lastProgressEventType) ? "(none)" : lastProgressEventType,
                LastProgressEventUtcTicks: Interlocked.Read(ref lastProgressEventUtcTicks),
                LastSelectedRpc: string.IsNullOrWhiteSpace(lastSelectedRpc) ? "(none)" : lastSelectedRpc,
                AuthoritativeConnectedAddressResolved: authoritativeConnectedAddressResolved,
                JoinRequestsReceived: Interlocked.Read(ref joinRequestsReceived),
                IncomingJoinRequestRaisedCount: Interlocked.Read(ref incomingJoinRequestRaisedCount),
                AcksReceived: Interlocked.Read(ref acksReceived),
                AcksIgnoredSourceMismatch: Interlocked.Read(ref acksIgnoredSourceMismatch),
                LastDisconnectReason: string.IsNullOrWhiteSpace(lastDisconnectReason) ? "(none)" : lastDisconnectReason,
                FirstColdStartObserved: Interlocked.CompareExchange(ref firstColdStartObserved, 0, 0) != 0,
                FirstColdStartMs: firstColdStartMs,
                FirstColdStartUtcTicks: Interlocked.Read(ref firstColdStartUtcTicks));
        }
    }

    private static string SanitizeDiagnosticText(string value)
    {
        var sanitized = SensitiveDataRedactor.Redact(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "(none)" : sanitized;
    }
}

public readonly record struct NknRuntimeDiagnosticsSnapshot(
    string Address,
    string Identifier,
    string KeyPath,
    string SeedRpc,
    long MessagesSent,
    long MessagesReceived,
    string LastError,
    int BridgePid,
    string NodeVersion,
    long BridgeLastPongUtcTicks,
    long BridgeRestartCount,
    int BridgeLastExitCode,
    string BridgeLastExitReason,
    double BridgeLastUptimeMs,
    long BridgeRawMessagesReceived,
    long ScreenShareOutboundBusyDrops,
    long ScreenSharePayloadBytesSent,
    long ScreenShareMessagesSent,
    long ScreenShareBridgeBytesSent,
    long HighPriorityControlQueueOverflows,
    long HighPriorityControlRejected,
    long HighPriorityControlCoalesced,
    long HighPriorityControlDroppedForStop,
    string LastBridgeMessageSource,
    bool? LastBridgeMessageIsTopic,
    string LastEnvelopeType,
    string LastEnvelopeDropReason,
    string LastProgressEventType,
    long LastProgressEventUtcTicks,
    string LastSelectedRpc,
    bool AuthoritativeConnectedAddressResolved,
    long JoinRequestsReceived,
    long IncomingJoinRequestRaisedCount,
    long AcksReceived,
    long AcksIgnoredSourceMismatch,
    string LastDisconnectReason,
    bool FirstColdStartObserved,
    double FirstColdStartMs,
    long FirstColdStartUtcTicks);
