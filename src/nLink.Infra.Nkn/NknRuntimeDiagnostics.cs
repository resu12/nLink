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
    private static long bridgeControlMessagesSent;
    private static long bridgeControlMessagesReceived;
    private static long bridgeControlBytesSent;
    private static long bridgeControlBytesReceived;
    private static long bridgeMediaMessagesSent;
    private static long bridgeMediaMessagesReceived;
    private static long bridgeMediaBytesSent;
    private static long bridgeMediaBytesReceived;
    private static long screenShareOutboundBusyDrops;
    private static long screenSharePayloadBytesSent;
    private static long screenShareMessagesSent;
    private static long screenShareBridgeBytesSent;
    private static long controlLaneQueueDepth;
    private static long controlLanePeakQueueDepth;
    private static long controlLaneInFlight;
    private static long controlLaneWaitCount;
    private static long controlLaneRejected;
    private static long controlLaneBytesSent;
    private static long controlLaneMessagesSent;
    private static long fileTransferLaneQueueDepth;
    private static long fileTransferLanePeakQueueDepth;
    private static long fileTransferLaneInFlight;
    private static long fileTransferLaneWaitCount;
    private static long fileTransferLaneRejected;
    private static long fileTransferLaneBytesSent;
    private static long fileTransferLaneMessagesSent;
    private static long fileTransferHintLaneQueueDepth;
    private static long fileTransferHintLanePeakQueueDepth;
    private static long fileTransferHintLaneInFlight;
    private static long fileTransferHintLaneWaitCount;
    private static long fileTransferHintLaneRejected;
    private static long fileTransferHintLaneBytesSent;
    private static long fileTransferHintLaneMessagesSent;
    private static long fileTransferNextOutboundSecureSequence;
    private static long fileTransferHintSent;
    private static long fileTransferHintReplaced;
    private static long fileTransferHintDropped;
    private static long fileTransferStartIdempotentAccepts;
    private static long fileTransferDuplicateCancelAcked;
    private static long stalePostTerminalIgnored;
    private static long activeFileTransferTombstones;
    private static long screenShareLaneQueueDepth;
    private static long screenShareLanePeakQueueDepth;
    private static long screenShareLaneInFlight;
    private static long screenShareLaneWaitCount;
    private static long screenShareLaneRejected;
    private static long screenShareLaneBytesSent;
    private static long screenShareLaneMessagesSent;
    private static long screenShareLaneCongestionHits;
    private static long screenShareLaneStaleFrameDrops;
    private static string lastScreenShareDroppedFrameId = "(none)";
    private static long controlPlaneAckTimeouts;
    private static long controlPlaneRequestAckTimeouts;
    private static double lastControlStopDispatchLatencyMs = -1d;
    private static long controlPlaneReconnectCount;
    private static long controlPlaneRehandshakeCount;
    private static long controlPlaneCapabilityLossStopCount;
    private static string lastControlPlaneRejectReason = "(none)";
    private static long mediaPlaneFramesSent;
    private static long mediaPlaneFramesDroppedForFreshness;
    private static long mediaPlaneSendFailures;
    private static long lastMediaCaptureToSendAgeMs = -1;
    private static long lastMediaFrameRenderedAgeMs = -1;
    private static long mediaPlanePolicyRejectCount;
    private static long mediaPlaneReplayRejectCount;
    private static long mediaPlaneSessionMismatchRejectCount;
    private static long mediaPlaneGeneration;
    private static int mediaPlaneAttached;
    private static string lastMediaPlaneRejectReason = "(none)";
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
    private static string helperAddressSource = "(none)";
    private static int helperAddressAuthoritative;
    private static int helperVerificationCodeVisible;
    private static long helperIdentityRegeneratedCount;
    private static long helperIdentityLastRegeneratedUtcTicks;

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

    public static void SetHelperBootstrapDiagnostics(
        string source,
        bool authoritative,
        bool verificationCodeVisible)
    {
        lock (Gate)
        {
            helperAddressSource = string.IsNullOrWhiteSpace(source) ? "(none)" : SanitizeDiagnosticText(source);
            helperAddressAuthoritative = authoritative ? 1 : 0;
            helperVerificationCodeVisible = verificationCodeVisible ? 1 : 0;
        }
    }

    public static void RecordIdentityRegenerated(DateTimeOffset regeneratedUtc)
    {
        Interlocked.Increment(ref helperIdentityRegeneratedCount);
        Interlocked.Exchange(ref helperIdentityLastRegeneratedUtcTicks, regeneratedUtc.UtcTicks);
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

    public static void IncrementBridgeControlMessagesSent() => Interlocked.Increment(ref bridgeControlMessagesSent);

    public static void IncrementBridgeControlMessagesReceived() => Interlocked.Increment(ref bridgeControlMessagesReceived);

    public static void IncrementBridgeMediaMessagesSent() => Interlocked.Increment(ref bridgeMediaMessagesSent);

    public static void IncrementBridgeMediaMessagesReceived() => Interlocked.Increment(ref bridgeMediaMessagesReceived);

    public static void AddBridgeControlBytesSent(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref bridgeControlBytesSent, bytes);
        }
    }

    public static void AddBridgeControlBytesReceived(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref bridgeControlBytesReceived, bytes);
        }
    }

    public static void AddBridgeMediaBytesSent(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref bridgeMediaBytesSent, bytes);
        }
    }

    public static void AddBridgeMediaBytesReceived(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref bridgeMediaBytesReceived, bytes);
        }
    }

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

    public static void IncrementControlPlaneAckTimeout(bool isControlRequest)
    {
        Interlocked.Increment(ref controlPlaneAckTimeouts);
        if (isControlRequest)
        {
            Interlocked.Increment(ref controlPlaneRequestAckTimeouts);
        }
    }

    public static void SetLastControlStopDispatchLatencyMs(double? latencyMs)
    {
        lock (Gate)
        {
            lastControlStopDispatchLatencyMs = latencyMs.GetValueOrDefault(-1d);
        }
    }

    public static void IncrementControlPlaneReconnectCount() => Interlocked.Increment(ref controlPlaneReconnectCount);

    public static void IncrementControlPlaneRehandshakeCount() => Interlocked.Increment(ref controlPlaneRehandshakeCount);

    public static void IncrementControlPlaneCapabilityLossStopCount() => Interlocked.Increment(ref controlPlaneCapabilityLossStopCount);

    public static void SetLastControlPlaneRejectReason(string? reason)
    {
        lock (Gate)
        {
            lastControlPlaneRejectReason = string.IsNullOrWhiteSpace(reason)
                ? "(none)"
                : SanitizeDiagnosticText(reason!);
        }
    }

    public static void IncrementMediaPlaneFramesSent() => Interlocked.Increment(ref mediaPlaneFramesSent);

    public static void AddMediaPlaneFramesDroppedForFreshness(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref mediaPlaneFramesDroppedForFreshness, count);
    }

    public static void SetMediaPlaneFramesDroppedForFreshness(long count)
    {
        Interlocked.Exchange(ref mediaPlaneFramesDroppedForFreshness, Math.Max(0L, count));
    }

    public static void IncrementMediaPlaneSendFailures() => Interlocked.Increment(ref mediaPlaneSendFailures);

    public static void SetLastMediaCaptureToSendAgeMs(long ageMs)
        => Interlocked.Exchange(ref lastMediaCaptureToSendAgeMs, ageMs < 0 ? -1 : ageMs);

    public static void SetLastMediaFrameRenderedAgeMs(long ageMs)
        => Interlocked.Exchange(ref lastMediaFrameRenderedAgeMs, ageMs < 0 ? -1 : ageMs);

    public static void IncrementMediaPlanePolicyRejectCount() => Interlocked.Increment(ref mediaPlanePolicyRejectCount);

    public static void IncrementMediaPlaneReplayRejectCount() => Interlocked.Increment(ref mediaPlaneReplayRejectCount);

    public static void IncrementMediaPlaneSessionMismatchRejectCount() => Interlocked.Increment(ref mediaPlaneSessionMismatchRejectCount);

    public static void SetMediaPlaneGeneration(long generation)
        => Interlocked.Exchange(ref mediaPlaneGeneration, Math.Max(0L, generation));

    public static void SetMediaPlaneAttached(bool attached)
        => Interlocked.Exchange(ref mediaPlaneAttached, attached ? 1 : 0);

    public static void SetLastMediaPlaneRejectReason(string? reason)
    {
        lock (Gate)
        {
            lastMediaPlaneRejectReason = string.IsNullOrWhiteSpace(reason)
                ? "(none)"
                : SanitizeDiagnosticText(reason!);
        }
    }

    public static void SetOutboundLaneQueueDepth(string lane, int depth, int peakDepth)
    {
        switch (lane)
        {
            case "control":
                Interlocked.Exchange(ref controlLaneQueueDepth, depth);
                Interlocked.Exchange(ref controlLanePeakQueueDepth, peakDepth);
                break;
            case "file_transfer":
                Interlocked.Exchange(ref fileTransferLaneQueueDepth, depth);
                Interlocked.Exchange(ref fileTransferLanePeakQueueDepth, peakDepth);
                break;
            case "file_transfer_hint":
                Interlocked.Exchange(ref fileTransferHintLaneQueueDepth, depth);
                Interlocked.Exchange(ref fileTransferHintLanePeakQueueDepth, peakDepth);
                break;
            case "screenshare":
                Interlocked.Exchange(ref screenShareLaneQueueDepth, depth);
                Interlocked.Exchange(ref screenShareLanePeakQueueDepth, peakDepth);
                break;
        }
    }

    public static void SetOutboundLaneInFlight(string lane, int inFlight)
    {
        switch (lane)
        {
            case "control":
                Interlocked.Exchange(ref controlLaneInFlight, inFlight);
                break;
            case "file_transfer":
                Interlocked.Exchange(ref fileTransferLaneInFlight, inFlight);
                break;
            case "file_transfer_hint":
                Interlocked.Exchange(ref fileTransferHintLaneInFlight, inFlight);
                break;
            case "screenshare":
                Interlocked.Exchange(ref screenShareLaneInFlight, inFlight);
                break;
        }
    }

    public static void IncrementOutboundLaneWaitCount(string lane)
    {
        switch (lane)
        {
            case "control":
                Interlocked.Increment(ref controlLaneWaitCount);
                break;
            case "file_transfer":
                Interlocked.Increment(ref fileTransferLaneWaitCount);
                break;
            case "file_transfer_hint":
                Interlocked.Increment(ref fileTransferHintLaneWaitCount);
                break;
            case "screenshare":
                Interlocked.Increment(ref screenShareLaneWaitCount);
                break;
        }
    }

    public static void IncrementOutboundLaneRejected(string lane)
    {
        switch (lane)
        {
            case "control":
                Interlocked.Increment(ref controlLaneRejected);
                break;
            case "file_transfer":
                Interlocked.Increment(ref fileTransferLaneRejected);
                break;
            case "file_transfer_hint":
                Interlocked.Increment(ref fileTransferHintLaneRejected);
                break;
            case "screenshare":
                Interlocked.Increment(ref screenShareLaneRejected);
                break;
        }
    }

    public static void AddOutboundLaneSent(string lane, long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        switch (lane)
        {
            case "control":
                Interlocked.Increment(ref controlLaneMessagesSent);
                Interlocked.Add(ref controlLaneBytesSent, bytes);
                break;
            case "file_transfer":
                Interlocked.Increment(ref fileTransferLaneMessagesSent);
                Interlocked.Add(ref fileTransferLaneBytesSent, bytes);
                break;
            case "file_transfer_hint":
                Interlocked.Increment(ref fileTransferHintLaneMessagesSent);
                Interlocked.Add(ref fileTransferHintLaneBytesSent, bytes);
                break;
            case "screenshare":
                Interlocked.Increment(ref screenShareLaneMessagesSent);
                Interlocked.Add(ref screenShareLaneBytesSent, bytes);
                break;
        }
    }

    public static void IncrementFileTransferHintSent() => Interlocked.Increment(ref fileTransferHintSent);

    public static void IncrementFileTransferHintReplaced() => Interlocked.Increment(ref fileTransferHintReplaced);

    public static void IncrementFileTransferHintDropped() => Interlocked.Increment(ref fileTransferHintDropped);

    public static void IncrementFileTransferStartIdempotentAccepts() => Interlocked.Increment(ref fileTransferStartIdempotentAccepts);

    public static void IncrementFileTransferDuplicateCancelAcked() => Interlocked.Increment(ref fileTransferDuplicateCancelAcked);

    public static void IncrementStalePostTerminalIgnored() => Interlocked.Increment(ref stalePostTerminalIgnored);

    public static void SetActiveFileTransferTombstones(int count)
        => Interlocked.Exchange(ref activeFileTransferTombstones, Math.Max(0, count));

    public static void SetFileTransferNextOutboundSecureSequence(long sequence)
    {
        Interlocked.Exchange(ref fileTransferNextOutboundSecureSequence, Math.Max(0L, sequence));
    }

    public static void IncrementScreenShareLaneCongestionHit() => Interlocked.Increment(ref screenShareLaneCongestionHits);

    public static void AddScreenShareLaneStaleFrameDrops(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref screenShareLaneStaleFrameDrops, count);
    }

    public static void SetLastScreenShareDroppedFrameId(string? frameId)
    {
        lock (Gate)
        {
            lastScreenShareDroppedFrameId = string.IsNullOrWhiteSpace(frameId)
                ? "(none)"
                : SanitizeDiagnosticText(frameId!);
        }
    }

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
            var controlLane = new OutboundLaneDiagnosticsSummary(
                QueueCapacity: 16,
                MaxInFlight: 1,
                CurrentQueueDepth: Interlocked.Read(ref controlLaneQueueDepth),
                PeakQueueDepth: Interlocked.Read(ref controlLanePeakQueueDepth),
                CurrentInFlight: Interlocked.Read(ref controlLaneInFlight),
                WaitCount: Interlocked.Read(ref controlLaneWaitCount),
                RejectedOrDroppedCount: Interlocked.Read(ref controlLaneRejected),
                BytesSent: Interlocked.Read(ref controlLaneBytesSent),
                MessagesSent: Interlocked.Read(ref controlLaneMessagesSent));
            var fileTransferLane = new OutboundLaneDiagnosticsSummary(
                QueueCapacity: 8,
                MaxInFlight: 1,
                CurrentQueueDepth: Interlocked.Read(ref fileTransferLaneQueueDepth),
                PeakQueueDepth: Interlocked.Read(ref fileTransferLanePeakQueueDepth),
                CurrentInFlight: Interlocked.Read(ref fileTransferLaneInFlight),
                WaitCount: Interlocked.Read(ref fileTransferLaneWaitCount),
                RejectedOrDroppedCount: Interlocked.Read(ref fileTransferLaneRejected),
                BytesSent: Interlocked.Read(ref fileTransferLaneBytesSent),
                MessagesSent: Interlocked.Read(ref fileTransferLaneMessagesSent));
            var fileTransferHintLane = new OutboundLaneDiagnosticsSummary(
                QueueCapacity: 8,
                MaxInFlight: 1,
                CurrentQueueDepth: Interlocked.Read(ref fileTransferHintLaneQueueDepth),
                PeakQueueDepth: Interlocked.Read(ref fileTransferHintLanePeakQueueDepth),
                CurrentInFlight: Interlocked.Read(ref fileTransferHintLaneInFlight),
                WaitCount: Interlocked.Read(ref fileTransferHintLaneWaitCount),
                RejectedOrDroppedCount: Interlocked.Read(ref fileTransferHintLaneRejected),
                BytesSent: Interlocked.Read(ref fileTransferHintLaneBytesSent),
                MessagesSent: Interlocked.Read(ref fileTransferHintLaneMessagesSent));
            var screenShareLane = new OutboundLaneDiagnosticsSummary(
                QueueCapacity: 2,
                MaxInFlight: 1,
                CurrentQueueDepth: Interlocked.Read(ref screenShareLaneQueueDepth),
                PeakQueueDepth: Interlocked.Read(ref screenShareLanePeakQueueDepth),
                CurrentInFlight: Interlocked.Read(ref screenShareLaneInFlight),
                WaitCount: Interlocked.Read(ref screenShareLaneWaitCount),
                RejectedOrDroppedCount: Interlocked.Read(ref screenShareLaneRejected),
                BytesSent: Interlocked.Read(ref screenShareLaneBytesSent),
                MessagesSent: Interlocked.Read(ref screenShareLaneMessagesSent));
            var controlPlane = new ControlPlaneDiagnosticsSummary(
                Lane: controlLane,
                AckTimeouts: Interlocked.Read(ref controlPlaneAckTimeouts),
                ControlRequestAckTimeouts: Interlocked.Read(ref controlPlaneRequestAckTimeouts),
                LastStopDispatchLatencyMs: lastControlStopDispatchLatencyMs,
                ReconnectCount: Interlocked.Read(ref controlPlaneReconnectCount),
                RehandshakeCount: Interlocked.Read(ref controlPlaneRehandshakeCount),
                CapabilityLossStopCount: Interlocked.Read(ref controlPlaneCapabilityLossStopCount),
                LastRejectReason: string.IsNullOrWhiteSpace(lastControlPlaneRejectReason) ? "(none)" : lastControlPlaneRejectReason);
            var mediaPlane = new MediaPlaneDiagnosticsSummary(
                FramesSent: Interlocked.Read(ref mediaPlaneFramesSent),
                FramesDroppedForFreshness: Interlocked.Read(ref mediaPlaneFramesDroppedForFreshness),
                SendFailures: Interlocked.Read(ref mediaPlaneSendFailures),
                LastCaptureToSendAgeMs: Interlocked.Read(ref lastMediaCaptureToSendAgeMs),
                LastFrameRenderedAgeMs: Interlocked.Read(ref lastMediaFrameRenderedAgeMs),
                PolicyRejectCount: Interlocked.Read(ref mediaPlanePolicyRejectCount),
                ReplayRejectCount: Interlocked.Read(ref mediaPlaneReplayRejectCount),
                SessionMismatchRejectCount: Interlocked.Read(ref mediaPlaneSessionMismatchRejectCount),
                MediaGeneration: Interlocked.Read(ref mediaPlaneGeneration),
                Attached: Interlocked.CompareExchange(ref mediaPlaneAttached, 0, 0) != 0,
                LastRejectReason: string.IsNullOrWhiteSpace(lastMediaPlaneRejectReason) ? "(none)" : lastMediaPlaneRejectReason);

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
                BridgeControlMessagesSent: Interlocked.Read(ref bridgeControlMessagesSent),
                BridgeControlMessagesReceived: Interlocked.Read(ref bridgeControlMessagesReceived),
                BridgeControlBytesSent: Interlocked.Read(ref bridgeControlBytesSent),
                BridgeControlBytesReceived: Interlocked.Read(ref bridgeControlBytesReceived),
                BridgeMediaMessagesSent: Interlocked.Read(ref bridgeMediaMessagesSent),
                BridgeMediaMessagesReceived: Interlocked.Read(ref bridgeMediaMessagesReceived),
                BridgeMediaBytesSent: Interlocked.Read(ref bridgeMediaBytesSent),
                BridgeMediaBytesReceived: Interlocked.Read(ref bridgeMediaBytesReceived),
                ScreenShareOutboundBusyDrops: Interlocked.Read(ref screenShareOutboundBusyDrops),
                ScreenSharePayloadBytesSent: Interlocked.Read(ref screenSharePayloadBytesSent),
                ScreenShareMessagesSent: Interlocked.Read(ref screenShareMessagesSent),
                ScreenShareBridgeBytesSent: Interlocked.Read(ref screenShareBridgeBytesSent),
                ControlPlane: controlPlane,
                MediaPlane: mediaPlane,
                ControlLane: controlLane,
                FileTransferLane: fileTransferLane,
                FileTransferHintLane: fileTransferHintLane,
                ScreenShareLane: screenShareLane,
                ControlLaneQueueDepth: controlLane.CurrentQueueDepth,
                ControlLanePeakQueueDepth: controlLane.PeakQueueDepth,
                ControlLaneInFlight: controlLane.CurrentInFlight,
                ControlLaneWaitCount: controlLane.WaitCount,
                ControlLaneRejected: controlLane.RejectedOrDroppedCount,
                ControlLaneBytesSent: controlLane.BytesSent,
                ControlLaneMessagesSent: controlLane.MessagesSent,
                FileTransferLaneQueueDepth: fileTransferLane.CurrentQueueDepth,
                FileTransferLanePeakQueueDepth: fileTransferLane.PeakQueueDepth,
                FileTransferLaneInFlight: fileTransferLane.CurrentInFlight,
                FileTransferLaneWaitCount: fileTransferLane.WaitCount,
                FileTransferLaneRejected: fileTransferLane.RejectedOrDroppedCount,
                FileTransferLaneBytesSent: fileTransferLane.BytesSent,
                FileTransferLaneMessagesSent: fileTransferLane.MessagesSent,
                FileTransferHintSent: Interlocked.Read(ref fileTransferHintSent),
                FileTransferHintReplaced: Interlocked.Read(ref fileTransferHintReplaced),
                FileTransferHintDropped: Interlocked.Read(ref fileTransferHintDropped),
                FileTransferStartIdempotentAccepts: Interlocked.Read(ref fileTransferStartIdempotentAccepts),
                FileTransferDuplicateCancelAcked: Interlocked.Read(ref fileTransferDuplicateCancelAcked),
                StalePostTerminalIgnored: Interlocked.Read(ref stalePostTerminalIgnored),
                ActiveFileTransferTombstones: Interlocked.Read(ref activeFileTransferTombstones),
                FileTransferNextOutboundSecureSequence: Interlocked.Read(ref fileTransferNextOutboundSecureSequence),
                ScreenShareLaneQueueDepth: screenShareLane.CurrentQueueDepth,
                ScreenShareLanePeakQueueDepth: screenShareLane.PeakQueueDepth,
                ScreenShareLaneInFlight: screenShareLane.CurrentInFlight,
                ScreenShareLaneWaitCount: screenShareLane.WaitCount,
                ScreenShareLaneRejected: screenShareLane.RejectedOrDroppedCount,
                ScreenShareLaneBytesSent: screenShareLane.BytesSent,
                ScreenShareLaneMessagesSent: screenShareLane.MessagesSent,
                ScreenShareLaneCongestionHits: Interlocked.Read(ref screenShareLaneCongestionHits),
                ScreenShareLaneStaleFrameDrops: Interlocked.Read(ref screenShareLaneStaleFrameDrops),
                LastScreenShareDroppedFrameId: string.IsNullOrWhiteSpace(lastScreenShareDroppedFrameId) ? "(none)" : lastScreenShareDroppedFrameId,
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
                FirstColdStartUtcTicks: Interlocked.Read(ref firstColdStartUtcTicks),
                HelperAddressSource: string.IsNullOrWhiteSpace(helperAddressSource) ? "(none)" : helperAddressSource,
                HelperAddressAuthoritative: helperAddressAuthoritative != 0,
                HelperVerificationCodeVisible: helperVerificationCodeVisible != 0,
                HelperIdentityRegeneratedCount: Interlocked.Read(ref helperIdentityRegeneratedCount),
                HelperIdentityLastRegeneratedUtcTicks: Interlocked.Read(ref helperIdentityLastRegeneratedUtcTicks));
        }
    }

    private static string SanitizeDiagnosticText(string value)
    {
        var sanitized = SensitiveDataRedactor.Redact(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "(none)" : sanitized;
    }
}

public readonly record struct OutboundLaneDiagnosticsSummary(
    int QueueCapacity,
    int MaxInFlight,
    long CurrentQueueDepth,
    long PeakQueueDepth,
    long CurrentInFlight,
    long WaitCount,
    long RejectedOrDroppedCount,
    long BytesSent,
    long MessagesSent)
{
    public double AverageBytesPerMessage =>
        MessagesSent <= 0 ? 0d : BytesSent / (double)MessagesSent;

    public bool IsCongestionActive =>
        CurrentQueueDepth > 0 || CurrentInFlight >= MaxInFlight && MaxInFlight > 0;

    public bool HasEverSaturatedQueue =>
        QueueCapacity > 0 && PeakQueueDepth >= QueueCapacity;

    public bool IsLikelyBottleneck =>
        IsCongestionActive &&
        (HasEverSaturatedQueue || WaitCount > 0 || RejectedOrDroppedCount > 0);

    public string ActivityState =>
        CurrentQueueDepth == 0 && CurrentInFlight == 0
            ? "idle"
            : IsLikelyBottleneck || IsCongestionActive
                ? "pacing"
                : "active";
}

public readonly record struct ControlPlaneDiagnosticsSummary(
    OutboundLaneDiagnosticsSummary Lane,
    long AckTimeouts,
    long ControlRequestAckTimeouts,
    double LastStopDispatchLatencyMs,
    long ReconnectCount,
    long RehandshakeCount,
    long CapabilityLossStopCount,
    string LastRejectReason);

public readonly record struct MediaPlaneDiagnosticsSummary(
    long FramesSent,
    long FramesDroppedForFreshness,
    long SendFailures,
    long LastCaptureToSendAgeMs,
    long LastFrameRenderedAgeMs,
    long PolicyRejectCount,
    long ReplayRejectCount,
    long SessionMismatchRejectCount,
    long MediaGeneration,
    bool Attached,
    string LastRejectReason);

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
    long BridgeControlMessagesSent,
    long BridgeControlMessagesReceived,
    long BridgeControlBytesSent,
    long BridgeControlBytesReceived,
    long BridgeMediaMessagesSent,
    long BridgeMediaMessagesReceived,
    long BridgeMediaBytesSent,
    long BridgeMediaBytesReceived,
    long ScreenShareOutboundBusyDrops,
    long ScreenSharePayloadBytesSent,
    long ScreenShareMessagesSent,
    long ScreenShareBridgeBytesSent,
    ControlPlaneDiagnosticsSummary ControlPlane,
    MediaPlaneDiagnosticsSummary MediaPlane,
    OutboundLaneDiagnosticsSummary ControlLane,
    OutboundLaneDiagnosticsSummary FileTransferLane,
    OutboundLaneDiagnosticsSummary FileTransferHintLane,
    OutboundLaneDiagnosticsSummary ScreenShareLane,
    long ControlLaneQueueDepth,
    long ControlLanePeakQueueDepth,
    long ControlLaneInFlight,
    long ControlLaneWaitCount,
    long ControlLaneRejected,
    long ControlLaneBytesSent,
    long ControlLaneMessagesSent,
    long FileTransferLaneQueueDepth,
    long FileTransferLanePeakQueueDepth,
    long FileTransferLaneInFlight,
    long FileTransferLaneWaitCount,
    long FileTransferLaneRejected,
    long FileTransferLaneBytesSent,
    long FileTransferLaneMessagesSent,
    long FileTransferHintSent,
    long FileTransferHintReplaced,
    long FileTransferHintDropped,
    long FileTransferStartIdempotentAccepts,
    long FileTransferDuplicateCancelAcked,
    long StalePostTerminalIgnored,
    long ActiveFileTransferTombstones,
    long FileTransferNextOutboundSecureSequence,
    long ScreenShareLaneQueueDepth,
    long ScreenShareLanePeakQueueDepth,
    long ScreenShareLaneInFlight,
    long ScreenShareLaneWaitCount,
    long ScreenShareLaneRejected,
    long ScreenShareLaneBytesSent,
    long ScreenShareLaneMessagesSent,
    long ScreenShareLaneCongestionHits,
    long ScreenShareLaneStaleFrameDrops,
    string LastScreenShareDroppedFrameId,
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
    long FirstColdStartUtcTicks,
    string HelperAddressSource,
    bool HelperAddressAuthoritative,
    bool HelperVerificationCodeVisible,
    long HelperIdentityRegeneratedCount,
    long HelperIdentityLastRegeneratedUtcTicks);
