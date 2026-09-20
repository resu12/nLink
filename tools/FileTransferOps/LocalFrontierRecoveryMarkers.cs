using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    // Immutable facts, not an ownership receipt. Only the original finalizer
    // can bind a scan to its actual four native readers and issued writer match.
    public sealed class FrontierRecoveryMarker
    {
        public string EventName { get; private set; }
        public string WriterId { get; private set; }
        public string AcceptanceRunId { get; private set; }
        public string ScenarioRunId { get; private set; }
        public long BatchGeneration { get; private set; }
        public long FirstSequence { get; private set; }
        public long LastSequence { get; private set; }
        public long LineCount { get; private set; }
        public long AttemptUtcTicks { get; private set; }
        public long AttemptCount { get; private set; }
        public string PayloadSha256 { get; private set; }
        public string FailureCode { get; private set; }
        public int StreamIndex { get; private set; }
        public long SourceLine { get; private set; }
        public string BatchKey { get; private set; }
        internal FrontierRecoveryMarker(Dictionary<string, string> fields, int stream, long line)
        {
            EventName = Text(fields, "event").ToLowerInvariant();
            WriterId = Text(fields, "log_writer_id"); AcceptanceRunId = Text(fields, "acceptance_run_id");
            ScenarioRunId = Text(fields, "scenario_run_id"); FailureCode = Text(fields, "failure_code");
            PayloadSha256 = Text(fields, "payload_sha256");
            if (PayloadSha256.Length != 64) throw new InvalidDataException("recovery_marker_hash");
            foreach (char c in PayloadSha256) if (!Uri.IsHexDigit(c)) throw new InvalidDataException("recovery_marker_hash");
            BatchGeneration = Number(fields, "batch_generation"); FirstSequence = Number(fields, "first_sequence");
            LastSequence = Number(fields, "last_sequence"); LineCount = Number(fields, "line_count");
            AttemptUtcTicks = Number(fields, "attempt_utc_ticks"); AttemptCount = Number(fields, "attempt_count");
            if (LastSequence < FirstSequence || LineCount != LastSequence - FirstSequence + 1 || AttemptUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("recovery_marker_range");
            StreamIndex = stream; SourceLine = line;
            BatchKey = WriterId.Length.ToString(CultureInfo.InvariantCulture) + ":" + WriterId + ":" + BatchGeneration.ToString(CultureInfo.InvariantCulture);
        }
        private static string Text(Dictionary<string, string> fields, string key)
        {
            string value;
            if (!fields.TryGetValue(key, out value) || String.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("recovery_marker_text");
            return value;
        }
        private static long Number(Dictionary<string, string> fields, string key)
        {
            string text; long value;
            if (!fields.TryGetValue(key, out text) || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
                throw new InvalidDataException("recovery_marker_number");
            return value;
        }
        internal int MetadataCodeUnits
        { get { return EventName.Length + WriterId.Length + AcceptanceRunId.Length + ScenarioRunId.Length + FailureCode.Length + PayloadSha256.Length + BatchKey.Length; } }
        internal bool SameTuple(FrontierRecoveryMarker other)
        {
            return WriterId == other.WriterId && AcceptanceRunId == other.AcceptanceRunId && ScenarioRunId == other.ScenarioRunId &&
                BatchGeneration == other.BatchGeneration && FirstSequence == other.FirstSequence && LastSequence == other.LastSequence &&
                LineCount == other.LineCount && AttemptUtcTicks == other.AttemptUtcTicks &&
                String.Equals(PayloadSha256, other.PayloadSha256, StringComparison.OrdinalIgnoreCase);
        }
        internal bool Matches(GuiSmokeLedgerValidationBatch batch)
        {
            return batch.TupleValid && WriterId == batch.WriterId && BatchGeneration == batch.BatchGeneration &&
                FirstSequence == batch.BatchFirstSequence && LastSequence == batch.BatchLastSequence &&
                FirstSequence == batch.MinSequence && LastSequence == batch.MaxSequence && LineCount == batch.LineCount &&
                AttemptUtcTicks == batch.AttemptUtcTicks && String.Equals(PayloadSha256, batch.PayloadSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Bounded parser/tuple comparison only; a synthetic scan is not authority.
    internal sealed class FrontierRecoveryMarkerScan
    {
        internal const int MaximumMarkers = 128, MaximumBatches = 64, MaximumLines = 262144,
            MaximumLineCodeUnits = 262144, MaximumMetadataCodeUnits = 65536;
        internal readonly List<FrontierRecoveryMarker> Markers = new List<FrontierRecoveryMarker>();
        internal readonly List<string> DeferredKeys = new List<string>();
        internal long SourceLines;
        internal int MetadataCodeUnits;
        private readonly Dictionary<string, FrontierRecoveryMarker> deferred = new Dictionary<string, FrontierRecoveryMarker>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FrontierRecoveryMarker> recovered = new Dictionary<string, FrontierRecoveryMarker>(StringComparer.OrdinalIgnoreCase);
        private readonly Action check;
        internal FrontierRecoveryMarkerScan(Action check) { this.check = check; }

        internal void Read(Stream source, int streamIndex, string run, string scenario)
        {
            check();
            if (!source.CanRead || source.CanWrite || source.Position != 0 || source.Length < 0 || source.Length > LocalGuiWriterRequest.FrontierRecoveryOutput.MaximumBytes)
                throw new InvalidDataException("recovery_marker_source");
            // Framework StreamReader can discard an incomplete UTF-8 tail.
            // Flush our strict decoder explicitly at physical EOF on both hosts.
            var utf8 = new UTF8Encoding(false, true); var decoder = utf8.GetDecoder();
            var bytes = new byte[32768]; var buffer = new char[utf8.GetMaxCharCount(bytes.Length)];
            var line = new StringBuilder(); bool skipLf = false, first = true; long lineNumber = 0;
            while (true)
            {
                check();
                int read = source.Read(bytes, 0, bytes.Length);
                int count = decoder.GetChars(bytes, 0, read, buffer, 0, read == 0);
                check();
                for (int i = 0; i < count; i++)
                {
                    char c = buffer[i];
                    if (first) { first = false; if (c == '\uFEFF') continue; }
                    if (skipLf) { skipLf = false; if (c == '\n') continue; }
                    if (c == '\r' || c == '\n')
                    { AcceptLine(line.ToString(), streamIndex, ++lineNumber, run, scenario); line.Length = 0; skipLf = c == '\r'; }
                    else
                    {
                        if (line.Length == MaximumLineCodeUnits) throw new InvalidDataException("recovery_marker_line_bound");
                        line.Append(c);
                    }
                }
                check();
                if (read == 0) break;
            }
            if (line.Length != 0) AcceptLine(line.ToString(), streamIndex, ++lineNumber, run, scenario);
            check();
            if (source.Position != source.Length) throw new InvalidDataException("recovery_marker_incomplete");
        }

        private void AcceptLine(string line, int stream, long number, string run, string scenario)
        {
            check();
            if (++SourceLines > MaximumLines) throw new InvalidDataException("recovery_marker_lines_bound");
            if (line.IndexOf("operational_log_write_", StringComparison.OrdinalIgnoreCase) < 0) return;
            int prefix = 0;
            while (prefix < line.Length && line[prefix] == '[')
            {
                int close = line.IndexOf(']', prefix + 1);
                if (close < 0 || close + 1 == line.Length || !Char.IsWhiteSpace(line[close + 1])) break;
                prefix = close + 1; while (prefix < line.Length && Char.IsWhiteSpace(line[prefix])) prefix++;
            }
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool duplicate = false, candidate = false;
            foreach (string part in line.Substring(prefix).Split(';'))
            {
                string segment = part.Trim(); int equals = segment.IndexOf('=');
                if (equals <= 0) continue;
                string name = segment.Substring(0, equals).Trim(), value = segment.Substring(equals + 1).Trim();
                if (name.Length == 0) continue;
                if (String.Equals(name, "event", StringComparison.OrdinalIgnoreCase) && IsMarker(value)) candidate = true;
                if (fields.ContainsKey(name)) duplicate = true; else fields.Add(name, value);
            }
            if (duplicate && (candidate || Regex.IsMatch(line, @"(?:^|[;\s])event=operational_log_write_(?:deferred|recovered|failed)(?:;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                throw new InvalidDataException("recovery_marker_duplicate_field");
            if (!candidate) return;
            string eventName = fields["event"];
            if (String.Equals(eventName, "operational_log_write_failed", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("recovery_marker_terminal");
            if (Markers.Count == MaximumMarkers) throw new InvalidDataException("recovery_marker_count_bound");
            var marker = new FrontierRecoveryMarker(fields, stream, number);
            if (marker.AcceptanceRunId != run || marker.ScenarioRunId != scenario) throw new InvalidDataException("recovery_marker_scope");
            if (marker.MetadataCodeUnits > MaximumMetadataCodeUnits - MetadataCodeUnits) throw new InvalidDataException("recovery_marker_metadata_bound");
            MetadataCodeUnits += marker.MetadataCodeUnits;
            var target = marker.EventName == "operational_log_write_deferred" ? deferred : recovered;
            if (target.ContainsKey(marker.BatchKey)) throw new InvalidDataException("recovery_marker_duplicate_tuple");
            if (target.Count == MaximumBatches) throw new InvalidDataException("recovery_marker_batch_bound");
            target.Add(marker.BatchKey, marker); Markers.Add(marker);
            if (Object.ReferenceEquals(target, deferred)) DeferredKeys.Add(marker.BatchKey);
            check();
        }
        private static bool IsMarker(string value)
        {
            return String.Equals(value, "operational_log_write_deferred", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "operational_log_write_recovered", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "operational_log_write_failed", StringComparison.OrdinalIgnoreCase);
        }
        internal bool PairsMatch()
        {
            check();
            if (deferred.Count != recovered.Count) return false;
            foreach (var pair in deferred)
            {
                FrontierRecoveryMarker recovery;
                if (!recovered.TryGetValue(pair.Key, out recovery) || !pair.Value.SameTuple(recovery)) return false;
                check();
            }
            return true;
        }
        internal bool Matches(IList<GuiSmokeLedgerValidationBatch> batches)
        {
            check();
            if (batches.Count != deferred.Count) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in batches)
            {
                string key = batch.WriterId.Length.ToString(CultureInfo.InvariantCulture) + ":" + batch.WriterId + ":" + batch.BatchGeneration.ToString(CultureInfo.InvariantCulture);
                FrontierRecoveryMarker marker;
                if (!seen.Add(key) || !deferred.TryGetValue(key, out marker) || !marker.Matches(batch)) return false;
                check();
            }
            return PairsMatch();
        }
    }
}
