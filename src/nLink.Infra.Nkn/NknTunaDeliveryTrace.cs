using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

// Diagnostic context only. Never consult this class to make a transport decision.
internal static class NknTunaDeliveryTrace
{
    private static readonly AsyncLocal<Producer?> TestProducer = new();
    private static readonly AsyncLocal<SendScope?> SendContext = new();
    private static readonly AsyncLocal<ReceiveBinding?> ReceiveContext = new();
    private static readonly Lazy<Producer?> EnvironmentProducer = new(() =>
        FromEnvironment(Environment.GetEnvironmentVariable));

    internal static Producer? Current => TestProducer.Value ?? EnvironmentProducer.Value;

    internal static Producer? FromEnvironment(Func<string, string?> read)
        => read("NLINK_TUNA_DELIVERY_TRACE") == "1" &&
           Guid.TryParseExact(read("NLINK_TUNA_DELIVERY_CAPTURE_ID"), "D", out var capture)
            ? new Producer(capture.ToString("D"), message => LocalOperationalLog.Info("NKN.Tuna", message))
            : null;

    internal static IDisposable OverrideForTests(Producer producer)
    {
        var previous = TestProducer.Value;
        TestProducer.Value = producer;
        return new Scope(() => TestProducer.Value = previous);
    }

    internal static string WireHash(ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("nLink/tuna-delivery/wire/v1\0"u8);
        hash.AppendData(bytes);
        return "ftwire1-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static SendScope? BeginSend(byte[] payload)
    {
        if (Current is null) return null;
        var scope = new SendScope(payload, SendContext.Value);
        SendContext.Value = scope;
        return scope;
    }

    internal static void BindSend(byte[] originalPayload, Frame? frame)
    {
        var scope = SendContext.Value;
        if (frame is not null && scope is not null && ReferenceEquals(scope.Payload, originalPayload))
            scope.Bind(frame);
    }

    private sealed record ReceiveBinding(Frame Frame, byte[] Payload);

    internal static IDisposable? EnterReceive(Frame? frame, byte[]? payload = null)
    {
        var previous = ReceiveContext.Value;
        if (previous is null && frame is null) return null;
        ReceiveContext.Value = frame is not null && payload is not null ? new ReceiveBinding(frame, payload) : null;
        return new Scope(() => ReceiveContext.Value = previous);
    }

    internal static Frame? ReceiveFrame => ReceiveContext.Value?.Frame;
    internal static Frame? ReceiveFrameFor(byte[] payload)
        => ReferenceEquals(ReceiveContext.Value?.Payload, payload) ? ReceiveContext.Value?.Frame : null;

    internal sealed class SendScope(byte[] payload, SendScope? previous) : IDisposable
    {
        private Frame? frame;
        private int ambiguous;
        internal byte[] Payload { get; } = payload;
        internal void Bind(Frame value)
        {
            if (Interlocked.CompareExchange(ref frame, value, null) is not null)
            {
                Interlocked.Exchange(ref ambiguous, 1);
                value.Connection.Producer.Incomplete("ambiguous_send_binding");
            }
        }
        internal void Result(string fields)
        {
            if (Volatile.Read(ref ambiguous) == 0) frame?.Record("app_send_result", fields);
        }
        public void Dispose() => SendContext.Value = previous;
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    internal sealed class Producer(string capture, Action<string> write)
    {
        internal const int RecordLimit = 4096;
        internal const int ByteLimit = 4 * 1024 * 1024;
        private readonly object gate = new();
        private readonly string id = Guid.NewGuid().ToString("D");
        private int connections;
        private int records;
        private int bytes;
        private bool incomplete;
        private bool capped;
        internal string Capture { get; } = capture;

        internal Connection? Connect()
        {
            lock (gate)
            {
                if (++connections > 4) { Incomplete("connection_cap"); return null; }
                var connection = new Connection(this, connections);
                Record($"trace_connection={connections}; trace_stage=connection_start; trace_tail=unproved", marker: true);
                return connection;
            }
        }

        internal void Incomplete(string reason)
        {
            lock (gate)
            {
                if (incomplete) return;
                incomplete = true;
                Record($"trace_stage=incomplete; trace_reason={reason}; trace_tail=unproved", marker: true);
            }
        }

        internal void Record(string fields, bool marker = false)
        {
            lock (gate)
            {
                if (capped && !marker) return;
                var message = $"event=tuna_delivery_trace; trace_capture={Capture}; trace_producer={id}; trace_order={records + 1}; trace_incomplete={(incomplete ? 1 : 0)}; {fields}";
                var size = Encoding.UTF8.GetByteCount(message) + 1;
                // Reserve room for all connection/direction ends and one incomplete marker.
                if ((!marker && (records >= RecordLimit - 32 || bytes + size > ByteLimit - 32768)) ||
                    records >= RecordLimit || bytes + size > ByteLimit)
                {
                    capped = true;
                    Incomplete("record_or_byte_cap");
                    return;
                }
                records++;
                bytes += size;
                try { write(message); }
                catch { incomplete = true; } // Logging must not alter delivery or throw into a callback.
            }
        }
    }

    internal sealed class Connection(Producer producer, int number)
    {
        private int outgoing;
        private int incoming;
        internal Producer Producer { get; } = producer;
        internal int Number { get; } = number;

        internal Frame? Select(string direction, ulong sequence, byte[] payload)
        {
            var occurrence = direction == "app_to_tuna"
                ? Interlocked.Increment(ref outgoing) : Interlocked.Increment(ref incoming);
            if (occurrence > 64)
            {
                if (occurrence == 65) Producer.Incomplete("frame_window_cap");
                return null;
            }
            return new Frame(this, direction, sequence, occurrence, WireHash(payload), payload.Length);
        }

        internal void End(string direction, string reason)
            => Producer.Record($"trace_connection={Number}; trace_direction={direction}; trace_stage=direction_end; trace_reason={reason}; trace_tail=unproved", marker: true);
    }

    internal sealed record Frame(Connection Connection, string Direction, ulong Sequence, int Occurrence, string Wire, int Bytes)
    {
        internal void Record(string stage, string fields = "")
            => Connection.Producer.Record($"trace_connection={Connection.Number}; trace_direction={Direction}; trace_stage={stage}; trace_seq={Sequence}; trace_occurrence={Occurrence}; trace_wire={Wire}; trace_bytes={Bytes}{fields}");
    }

    // Called with the owner captured by the actual stdout subscription, including stale output.
    // This path consumes only this diagnostic event, never ready/terminal/runtime events.
    internal static bool PreserveChildLine(string line, string? owner, string role)
    {
        var producer = Current;
        if (producer is null || !line.Contains("tuna_delivery_trace", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("event").GetString() != "tuna_delivery_trace") return false;
            if (root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                throw new FormatException();
            var capture = root.GetProperty("trace_capture").GetString();
            var child = root.GetProperty("trace_producer").GetString();
            var direction = root.GetProperty("trace_direction").GetString();
            var stage = root.GetProperty("trace_stage").GetString();
            var reason = root.GetProperty("trace_reason").GetString();
            var wire = root.GetProperty("trace_wire").GetString();
            var order = root.GetProperty("trace_order").GetInt64();
            var connection = root.GetProperty("trace_connection").GetInt32();
            var sequence = root.GetProperty("trace_seq").GetUInt64();
            var occurrence = root.GetProperty("trace_occurrence").GetInt32();
            var size = root.GetProperty("trace_bytes").GetInt32();
            var childIncomplete = root.TryGetProperty("trace_incomplete", out var incompleteValue) && incompleteValue.GetBoolean();
            var isFrameStage = stage is "read_completed" or "write_entered" or "write_completed" or "write_failed";
            if (capture != producer.Capture || !Guid.TryParseExact(child, "D", out _) ||
                !Guid.TryParseExact(owner, "N", out var ownerId) || role is not ("listener" or "dialer") ||
                direction is not ("app_to_tuna" or "tuna_to_app") ||
                stage is not ("direction_start" or "read_completed" or "write_entered" or "write_completed" or "write_failed" or "direction_end" or "incomplete") ||
                reason is null || reason.Length > 80 || reason.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) ||
                order is < 1 or > 4096 || connection is < 1 or > 4 || occurrence is < 0 or > 64 || size is < 0 or > 4 * 1024 * 1024 ||
                wire is null || (wire != "none" && (wire.Length != 72 || !wire.StartsWith("ftwire1-", StringComparison.Ordinal) || wire[8..].Any(c => !char.IsAsciiHexDigitLower(c)))))
                throw new FormatException();
            if (isFrameStage ? wire == "none" || occurrence == 0 : wire != "none" || occurrence != 0 || sequence != 0 || size != 0)
                throw new FormatException();
            producer.Record($"trace_stage=child_record; trace_owner={ownerId:D}; trace_role={role}; child_producer={child}; child_order={order}; child_connection={connection}; child_direction={direction}; child_stage={stage}; child_reason={reason}; child_seq={sequence}; child_occurrence={occurrence}; child_incomplete={(childIncomplete ? 1 : 0)}; trace_wire={wire}; trace_bytes={size}; trace_tail=unproved");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            producer.Incomplete("malformed_child_trace");
        }
        return true;
    }
}
