using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NLink.Infra.Nkn;

// One instance per immutable sidecar process owner. Evidence only; no runtime callbacks.
internal sealed class NknTunaSetupDiagnostics(string role, int pid, Guid runId, Action<string> write)
{
    internal const int RecordLimit = 64;
    internal const int LineLimit = 4096;
    private const string ProviderEvent = "provider_path_state_changed";
    private const string DialPrefix = "Tuna session dial to ip ";
    private static readonly Regex DialError = new(
        @"\A(?:[0-9]{4}/[0-9]{2}/[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} )?Tuna session dial to ip (?<ip>[0-9a-fA-F:.]+) port (?<port>[1-9][0-9]{0,4}) err: (?<error>[^\r\n]+)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex StreamError = new(
        @"\A(?:[0-9]{4}/[0-9]{2}/[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} )?(?<operation>handle msg|Write message|Read message) error: (?<error>[^\r\n]+)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly object gate = new();
    private int records;
    private int details;
    private bool capped;
    private bool stopped;
    private bool streamErrorObserved;

    internal void ProviderLine(string line)
    {
        if (role != "listener" || !line.Contains(ProviderEvent, StringComparison.Ordinal)) return;
        if (line.Length > LineLimit) { Record("kind=invalid; reason=provider_line_oversized"); return; }
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("event").GetString() != ProviderEvent) return;
            if (root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                throw new FormatException();
            var index = root.GetProperty("index").GetInt32();
            var usable = root.GetProperty("usable").GetBoolean();
            var reason = root.GetProperty("stateReason").GetString();
            var endpoint = ReadHash(root, "endpointHash");
            var previous = ReadHash(root, "previousEndpointHash");
            var observed = root.GetProperty("observedUnixMs").GetInt64();
            if (root.GetProperty("role").GetString() != "listener" || index is < 0 or > 63 ||
                observed is < 0 or > 253402300799999 ||
                reason is not ("nil_addr" or "empty_endpoint" or "missing_ip" or "missing_port" or "non_public_ip" or "usable"))
                throw new FormatException();
            Record(FormattableString.Invariant($"kind=provider_path; path_index={index}; endpoint_hash={endpoint}; previous_endpoint_hash={previous}; usable={(usable ? 1 : 0)}; state_reason={reason}; observed_unix_ms={observed}"), detail: true);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            Record("kind=invalid; reason=provider_line_malformed");
        }
    }

    internal void DialErrorLine(string line)
    {
        if (role != "dialer" || !line.Contains(DialPrefix, StringComparison.Ordinal)) return;
        if (line.Length > LineLimit) { Record("kind=invalid; reason=dial_line_oversized"); return; }
        var match = DialError.Match(line);
        if (!match.Success || !IPAddress.TryParse(match.Groups["ip"].Value, out _) ||
            !ushort.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            Record("kind=invalid; reason=dial_line_malformed");
            return;
        }
        // Hash the SDK's literal host, not an IPAddress-normalized spelling, matching Go shortHash.
        var endpoint = match.Groups["ip"].Value + ":" + port.ToString(CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)).AsSpan(0, 8)).ToLowerInvariant();
        var error = match.Groups["error"].Value;
        var category = error.EndsWith(": connect: connection refused", StringComparison.Ordinal) ||
            error.EndsWith(": connectex: No connection could be made because the target machine actively refused it.", StringComparison.Ordinal)
            ? "tcp_connection_refused"
            : error.EndsWith(": i/o timeout", StringComparison.Ordinal) || error.EndsWith("context deadline exceeded", StringComparison.Ordinal)
                ? "timeout" : "other";
        Record($"kind=dial_path_error; endpoint_hash={hash}; error_category={category}", detail: true);
    }

    internal void ObserveStop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            // This is an at-stop snapshot, not proof that asynchronous output is fully drained.
            Write($"kind=stop_snapshot; records={records}; details={details}; truncated={(capped ? 1 : 0)}; diagnostic_status={(details == 0 ? "no_details_at_stop" : "details_observed")}");
        }
    }

    internal void StreamErrorLine(string line)
    {
        if (role is not ("listener" or "dialer") || line.Length > LineLimit) return;
        var match = StreamError.Match(line);
        if (!match.Success) return;
        var operation = match.Groups["operation"].Value == "Write message" ? "write" : "read";
        lock (gate)
        {
            if (streamErrorObserved) return;
            streamErrorObserved = true;
            Record($"kind=stream_error; operation={operation}; error_category={StreamErrorCategory(match.Groups["error"].Value)}", detail: true);
        }
    }

    internal static string StreamErrorCategory(string? error)
    {
        if (error is null) return "unavailable";
        if (string.IsNullOrWhiteSpace(error)) return "none";
        if (error.Length > LineLimit || error.Contains('\r') || error.Contains('\n')) return "other";
        return error.Trim() switch
        {
            "session closed" => "session_closed",
            "EOF" => "eof",
            "unexpected EOF" => "unexpected_eof",
            "context canceled" => "context_canceled",
            "context deadline exceeded" or "read deadline exceeded" or "write deadline exceeded" or "i/o timeout" => "timeout",
            "decrypt message failed" => "decrypt_failed",
            "io: read/write on closed pipe" => "closed_pipe",
            var value when value.EndsWith(": i/o timeout", StringComparison.Ordinal) => "timeout",
            var value when value.EndsWith("use of closed network connection", StringComparison.Ordinal) => "connection_closed",
            var value when value.EndsWith("connection reset by peer", StringComparison.Ordinal) ||
                value.EndsWith("An existing connection was forcibly closed by the remote host.", StringComparison.Ordinal) => "connection_reset",
            var value when value.EndsWith("broken pipe", StringComparison.Ordinal) => "broken_pipe",
            _ => "other",
        };
    }

    private static string ReadHash(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        if (value == "") return "none";
        if (value is null || value.Length != 16 || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw new FormatException();
        return value;
    }

    private void Record(string fields, bool detail = false)
    {
        lock (gate)
        {
            if (records >= RecordLimit)
            {
                if (!capped) { capped = true; Write("kind=truncated; reason=record_limit; limit=64"); }
                return;
            }
            records++;
            if (detail) details++;
            Write($"record={records}; after_stop={(stopped ? 1 : 0)}; {fields}");
        }
    }

    private void Write(string fields)
    {
        try { write($"event=tuna_setup_diagnostic; role={role}; pid={pid}; run_id={runId:D}; {fields}"); }
        catch { } // Diagnostics must not throw into process I/O or cleanup.
    }
}
