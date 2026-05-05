using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class NknTunaSidecarClient : INknAccelerationLane
{
    private const long TraceFirstFrames = 16;
    private const long TraceEveryFrames = 128;
    private const long TraceFirstSequenceGaps = 4;
    private const long TraceEverySequenceGaps = 128;
    private const ulong SequenceGapWarningMissingThreshold = 16;
    private const long IdleWarmupThresholdMs = 500;
    private const int IdleWarmupDelayMs = 20;
    private const int ControlQueueWriteTimeoutMs = 1_000;
    private const int MediaQueueWriteTimeoutMs = 250;
    private const int BulkQueueWriteTimeoutMs = 30_000;
    internal static int? BulkQueueWriteTimeoutOverrideForTests;
    private readonly NknAccelerationLaneKind configuredLanes;
    private readonly int queueCapacity;
    private readonly object gate = new();
    private readonly Channel<OutboundFrame> outbound;
    private readonly TaskCompletionSource<NknAccelerationLocalEndpoint> statusReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? tcpClient;
    private NetworkStream? stream;
    private CancellationTokenSource? cts;
    private Task? readerTask;
    private Task? writerTask;
    private int disposed;
    private int available;
    private long nextSequence;
    private long framesWritten;
    private long framesReceived;
    private long controlFramesAccepted;
    private long mediaFramesAccepted;
    private long bulkFramesAccepted;
    private long controlFramesWritten;
    private long mediaFramesWritten;
    private long bulkFramesWritten;
    private long controlFramesReceived;
    private long mediaFramesReceived;
    private long bulkFramesReceived;
    private long sendRejected;
    private long queueOverflow;
    private long sequenceGap;
    private long sequenceReordered;
    private long lastWriteUtcMs;
    private readonly long[] lastReceivedSequenceByLane = new long[byte.MaxValue + 1];
    private string lastUnavailableReason = string.Empty;

    public NknTunaSidecarClient(NknAccelerationLaneKind configuredLanes, int queueCapacity)
    {
        this.configuredLanes = configuredLanes == NknAccelerationLaneKind.None
            ? NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen
            : configuredLanes;
        this.queueCapacity = Math.Clamp(queueCapacity, 16, 4096);
        outbound = Channel.CreateBounded<OutboundFrame>(
            new BoundedChannelOptions(this.queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public bool IsAvailable => Volatile.Read(ref available) != 0;

    public string? LocalTunaAddress { get; private set; }

    public NknAccelerationLaneKind SupportedLanes { get; private set; } = NknAccelerationLaneKind.None;

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

    public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot()
        => new(
            IsAvailable,
            Volatile.Read(ref lastUnavailableReason) ?? string.Empty,
            Volatile.Read(ref controlFramesAccepted),
            Volatile.Read(ref mediaFramesAccepted),
            Volatile.Read(ref bulkFramesAccepted),
            Volatile.Read(ref controlFramesWritten),
            Volatile.Read(ref mediaFramesWritten),
            Volatile.Read(ref bulkFramesWritten),
            Volatile.Read(ref controlFramesReceived),
            Volatile.Read(ref mediaFramesReceived),
            Volatile.Read(ref bulkFramesReceived),
            Volatile.Read(ref sendRejected),
            Volatile.Read(ref queueOverflow),
            Volatile.Read(ref sequenceGap),
            Volatile.Read(ref sequenceReordered));

    public async Task<NknAccelerationLocalEndpoint> ConnectAsync(string endpoint, TimeSpan statusTimeout, CancellationToken ct)
    {
        ThrowIfDisposed();
        var (host, port) = ParseEndpoint(endpoint);
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var networkStream = client.GetStream();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        lock (gate)
        {
            tcpClient = client;
            stream = networkStream;
            cts = linkedCts;
            readerTask = Task.Run(() => ReadLoopAsync(linkedCts.Token), CancellationToken.None);
            writerTask = Task.Run(() => WriteLoopAsync(linkedCts.Token), CancellationToken.None);
        }

        var completed = await Task.WhenAny(statusReady.Task, Task.Delay(statusTimeout, ct)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, statusReady.Task))
        {
            MarkUnavailable("status_timeout");
            throw new TimeoutException("Timed out waiting for Tuna sidecar status.");
        }

        return await statusReady.Task.ConfigureAwait(false);
    }

    public async Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
    {
        if (!IsAvailable || ct.IsCancellationRequested || envelopeBytes is null || envelopeBytes.Length == 0)
        {
            Interlocked.Increment(ref sendRejected);
            return false;
        }

        var sidecarLane = NknAccelerationLaneCodec.ToSidecarLane(lane);
        var frame = new OutboundFrame(
            sidecarLane,
            checked((ulong)Interlocked.Increment(ref nextSequence)),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            envelopeBytes.AsSpan().ToArray());

        if (!await TryQueueFrameAsync(frame, GetQueueWriteTimeoutMs(sidecarLane), ct).ConfigureAwait(false))
        {
            Interlocked.Increment(ref sendRejected);
            Interlocked.Increment(ref queueOverflow);
            if (sidecarLane != NknTunaSidecarLane.Bulk)
            {
                MarkUnavailable("queue_overflow");
            }

            return false;
        }

        IncrementAccepted(sidecarLane);
        return true;
    }

    internal void MarkUnavailableFromSidecarEvent(string reason)
        => MarkUnavailable(string.IsNullOrWhiteSpace(reason) ? "sidecar_event" : reason.Trim());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        MarkUnavailable("disposed");
        outbound.Writer.TryComplete();
        try { cts?.Cancel(); } catch { }
        try { stream?.Dispose(); } catch { }
        try { tcpClient?.Dispose(); } catch { }
        cts?.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var activeStream = stream ?? throw new InvalidOperationException("sidecar_stream_missing");
            while (!ct.IsCancellationRequested)
            {
                var frame = await NknTunaSidecarFrameProtocol.ReadFrameAsync(activeStream, ct).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case NknTunaSidecarFrameType.Status:
                        HandleStatusFrame(frame.Payload);
                        break;
                    case NknTunaSidecarFrameType.Data:
                        HandleDataFrame(frame);
                        break;
                    case NknTunaSidecarFrameType.Close:
                        MarkUnavailable("remote_closed");
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            MarkUnavailable("canceled");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_sidecar_read_failed; error={ex.GetType().Name}");
            statusReady.TrySetException(ex);
            MarkUnavailable("read_failed");
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            var activeStream = stream ?? throw new InvalidOperationException("sidecar_stream_missing");
            await foreach (var frame in outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await SendIdleWarmupIfNeededAsync(activeStream, frame, ct).ConfigureAwait(false);
                await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                        activeStream,
                        NknTunaSidecarFrameType.Data,
                        frame.Lane,
                        frame.Sequence,
                        frame.TimestampUtcMs,
                        frame.Payload,
                        ct)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref lastWriteUtcMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var written = Interlocked.Increment(ref framesWritten);
                IncrementWritten(frame.Lane);
                if (ShouldTraceFrame(written))
                {
                    LocalOperationalLog.Info(
                        "NKN.Tuna",
                        $"event=tuna_sidecar_frame_written; channel={MapSidecarLane(frame.Lane)}; seq={frame.Sequence}; payload_bytes={frame.Payload.Length}; frames_written={written}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            MarkUnavailable("canceled");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_sidecar_write_failed; error={ex.GetType().Name}");
            MarkUnavailable("write_failed");
        }
    }

    private async ValueTask<bool> TryQueueFrameAsync(OutboundFrame frame, int timeoutMs, CancellationToken ct)
    {
        if (outbound.Writer.TryWrite(frame))
        {
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            while (await outbound.Writer.WaitToWriteAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                if (outbound.Writer.TryWrite(frame))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private static int GetQueueWriteTimeoutMs(NknTunaSidecarLane lane)
        => lane switch
        {
            NknTunaSidecarLane.Bulk => BulkQueueWriteTimeoutOverrideForTests ?? BulkQueueWriteTimeoutMs,
            NknTunaSidecarLane.Media => MediaQueueWriteTimeoutMs,
            _ => ControlQueueWriteTimeoutMs,
        };

    private async Task SendIdleWarmupIfNeededAsync(Stream activeStream, OutboundFrame nextFrame, CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var previousWriteMs = Volatile.Read(ref lastWriteUtcMs);
        var idleMs = previousWriteMs <= 0 ? long.MaxValue : Math.Max(0, nowMs - previousWriteMs);
        if (previousWriteMs > 0 && idleMs < IdleWarmupThresholdMs)
        {
            return;
        }

        await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                activeStream,
                NknTunaSidecarFrameType.Ping,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: nowMs,
                ReadOnlyMemory<byte>.Empty,
                ct)
            .ConfigureAwait(false);
        Interlocked.Exchange(ref lastWriteUtcMs, nowMs);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_sidecar_idle_warmup_sent; next_channel={MapSidecarLane(nextFrame.Lane)}; next_seq={nextFrame.Sequence}; idle_ms={(previousWriteMs <= 0 ? -1 : idleMs)}");
        await Task.Delay(IdleWarmupDelayMs, ct).ConfigureAwait(false);
    }

    private void HandleStatusFrame(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var address = TryGetString(root, "address");
            var protocolVersion = TryGetInt(root, "protocolVersion") ?? NknTunaSidecarFrameProtocol.ProtocolVersion;
            var sidecarLanes = TryGetStringArray(root, "lanes");
            var lanes = NknAccelerationLaneCodec.FromNames(sidecarLanes);
            lanes &= configuredLanes;
            if (string.IsNullOrWhiteSpace(address) ||
                protocolVersion != NknTunaSidecarFrameProtocol.ProtocolVersion ||
                lanes == NknAccelerationLaneKind.None)
            {
                MarkUnavailable("invalid_status");
                statusReady.TrySetException(new InvalidOperationException("Invalid Tuna sidecar status."));
                return;
            }

            LocalTunaAddress = address.Trim();
            SupportedLanes = lanes;
            Volatile.Write(ref available, 1);
            statusReady.TrySetResult(new NknAccelerationLocalEndpoint(LocalTunaAddress, lanes, protocolVersion));
            StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(true, "ready"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            MarkUnavailable("status_parse_failed");
            statusReady.TrySetException(ex);
        }
    }

    private void HandleDataFrame(NknTunaSidecarFrame frame)
    {
        if (!IsAvailable)
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var channel = NknAccelerationLaneCodec.FromSidecarLane(frame.Lane);
        var sequenceObservation = ObserveReceivedSequence((byte)frame.Lane, frame.Sequence, out var previousSequence, out var missingCount);
        var received = Interlocked.Increment(ref framesReceived);
        IncrementReceived(frame.Lane);
        if (ShouldTraceFrame(received))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_sidecar_frame_received; channel={MapSidecarLane(frame.Lane)}; seq={frame.Sequence}; payload_bytes={frame.Payload.Length}; frames_received={received}");
        }

        if (sequenceObservation == SequenceObservation.Gap)
        {
            var gaps = Interlocked.Increment(ref sequenceGap);
            if (missingCount >= SequenceGapWarningMissingThreshold)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_sidecar_sequence_gap; channel={MapSidecarLane(frame.Lane)}; previous_seq={previousSequence}; current_seq={frame.Sequence}; missing_count={missingCount}; gap_count={gaps}");
            }
            else if (ShouldTraceSequenceGap(gaps))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_sidecar_sequence_gap_observed; channel={MapSidecarLane(frame.Lane)}; previous_seq={previousSequence}; current_seq={frame.Sequence}; missing_count={missingCount}; gap_count={gaps}");
            }
        }
        else if (sequenceObservation == SequenceObservation.Reordered)
        {
            var reordered = Interlocked.Increment(ref sequenceReordered);
            if (ShouldTraceFrame(reordered))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_sidecar_sequence_reordered; channel={MapSidecarLane(frame.Lane)}; previous_seq={previousSequence}; current_seq={frame.Sequence}; missing_count=0; reordered_count={reordered}");
            }
        }

        MessageReceived?.Invoke(
            this,
            new NknIncomingMessage(
                source: string.Empty,
                payload: frame.Payload,
                isTopic: false,
                topic: null,
                channel: channel,
                bridgeIngressObservedUtcMs: nowMs,
                bridgeMessageObservedUtcMs: frame.TimestampUtcMs,
                binaryFrameDecodedUtcMs: nowMs));
    }

    private SequenceObservation ObserveReceivedSequence(byte lane, ulong sequence, out ulong previousSequence, out ulong missingCount)
    {
        previousSequence = 0;
        missingCount = 0;
        if (sequence == 0)
        {
            return SequenceObservation.InOrder;
        }

        var next = checked((long)sequence);
        while (true)
        {
            var previousLong = Volatile.Read(ref lastReceivedSequenceByLane[lane]);
            var previous = (ulong)previousLong;
            previousSequence = previous;

            if (previous == 0 || sequence == previous + 1)
            {
                if (Interlocked.CompareExchange(ref lastReceivedSequenceByLane[lane], next, previousLong) == previousLong)
                {
                    return SequenceObservation.InOrder;
                }

                continue;
            }

            if (sequence <= previous)
            {
                return SequenceObservation.Reordered;
            }

            missingCount = sequence - previous - 1;
            if (Interlocked.CompareExchange(ref lastReceivedSequenceByLane[lane], next, previousLong) == previousLong)
            {
                return SequenceObservation.Gap;
            }
        }
    }

    private enum SequenceObservation
    {
        InOrder,
        Gap,
        Reordered,
    }

    private void MarkUnavailable(string reason)
    {
        Volatile.Write(ref lastUnavailableReason, string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim());
        var previous = Interlocked.Exchange(ref available, 0);
        if (previous != 0)
        {
            StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(false, reason));
        }
    }

    private void IncrementAccepted(NknTunaSidecarLane lane)
    {
        switch (lane)
        {
            case NknTunaSidecarLane.Media:
                Interlocked.Increment(ref mediaFramesAccepted);
                break;
            case NknTunaSidecarLane.Bulk:
                Interlocked.Increment(ref bulkFramesAccepted);
                break;
            default:
                Interlocked.Increment(ref controlFramesAccepted);
                break;
        }
    }

    private void IncrementWritten(NknTunaSidecarLane lane)
    {
        switch (lane)
        {
            case NknTunaSidecarLane.Media:
                Interlocked.Increment(ref mediaFramesWritten);
                break;
            case NknTunaSidecarLane.Bulk:
                Interlocked.Increment(ref bulkFramesWritten);
                break;
            default:
                Interlocked.Increment(ref controlFramesWritten);
                break;
        }
    }

    private void IncrementReceived(NknTunaSidecarLane lane)
    {
        switch (lane)
        {
            case NknTunaSidecarLane.Media:
                Interlocked.Increment(ref mediaFramesReceived);
                break;
            case NknTunaSidecarLane.Bulk:
                Interlocked.Increment(ref bulkFramesReceived);
                break;
            default:
                Interlocked.Increment(ref controlFramesReceived);
                break;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Endpoint is required.", nameof(endpoint));
        }

        var trimmed = endpoint.Trim();
        var separatorIndex = trimmed.LastIndexOf(':');
        if (separatorIndex <= 0 ||
            separatorIndex >= trimmed.Length - 1 ||
            !int.TryParse(trimmed[(separatorIndex + 1)..], out var port) ||
            port <= 0 ||
            port > ushort.MaxValue)
        {
            throw new ArgumentException("Endpoint must be host:port.", nameof(endpoint));
        }

        return (trimmed[..separatorIndex], port);
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string[] TryGetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string value)
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static bool ShouldTraceFrame(long count)
        => count <= TraceFirstFrames ||
           TraceEveryFrames > 0 && count % TraceEveryFrames == 0;

    private static bool ShouldTraceSequenceGap(long count)
        => count <= TraceFirstSequenceGaps ||
           TraceEverySequenceGaps > 0 && count % TraceEverySequenceGaps == 0;

    private static string MapSidecarLane(NknTunaSidecarLane lane)
        => lane switch
        {
            NknTunaSidecarLane.Media => "media",
            NknTunaSidecarLane.Bulk => "bulk",
            _ => "control",
        };

    private readonly record struct OutboundFrame(
        NknTunaSidecarLane Lane,
        ulong Sequence,
        long TimestampUtcMs,
        byte[] Payload);
}
