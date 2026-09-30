using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NLink.Tools
{
    // Pure diagnostic projection inside the original selection callback. These
    // values carry no custody, route ownership, or publication authority.
    public static class LocalFrontierInputRows
    {
        public static int Project(string[] full, object[] events, object path, object failureEvent, List<object> rows, Action visit, Hashtable emptyFields)
        {
            long units = 0; int failurePosition = -1;
            for (int i = 0; i < full.Length; i++)
            {
                visit(); string line = full[i]; var item = PSObject.AsPSObject(events[i]);
                var raw = item.Properties["RawLine"] as PSNoteProperty;
                var file = item.Properties["FilePath"] as PSNoteProperty;
                // Original state capture already requires note properties. Do
                // not invoke substituted script properties in this projection.
                if (raw == null || file == null || !Object.ReferenceEquals(line, Unwrap(raw.Value)) ||
                    !LanguagePrimitives.Equals(file.Value, path, false, CultureInfo.InvariantCulture) ||
                    line.Length > 262144 || line.Length > LocalPublicationInputLimits.ProducerCodeUnits - units ||
                    line.Split(new[] { ';' }, 257).Length > 256)
                    throw new InvalidOperationException("native_frontier_input");
                units += line.Length;
                var fields = ParseFields(line, emptyFields);
                var identity = ReadScope(line, fields);
                if (identity.Error == "native_frontier_duplicate_scope") throw new InvalidOperationException(identity.Error);
                var dataSession = ReadDataSession(line, fields);
                rows.Add(new Row(i, fields, identity, dataSession));
                if (Object.ReferenceEquals(events[i], failureEvent))
                {
                    if (failurePosition != -1) throw new InvalidOperationException("native_frontier_failure_duplicate");
                    failurePosition = i;
                }
            }
            return failurePosition;
        }

        private static object Unwrap(object value)
        { var wrapped = value as PSObject; return wrapped == null ? value : wrapped.BaseObject; }
        private static string Text(Hashtable fields, string name)
        { return (string)fields[name] ?? ""; }
        private static bool Equal(string left, string right)
        { return String.Equals(left, right, StringComparison.InvariantCulture); }
        private static string Token(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || Equal(value, "(none)")) return "(none)";
            using (var sha = SHA256.Create())
                return "fte1-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }
        private static Hashtable ParseFields(string line, Hashtable emptyFields)
        {
            // Clone the caller's empty literal to preserve the host's comparer
            // (Windows PowerShell and PowerShell 7 differ for Unicode keys).
            var fields = (Hashtable)emptyFields.Clone();
            bool duplicate = false, writerDuplicate = false;
            if (String.IsNullOrWhiteSpace(line)) return fields;
            var prefix = Regex.Match(line, @"^(?:\[[^\]]+\]\s+)+(?<message>.*)$");
            string payload = prefix.Success ? prefix.Groups["message"].Value : line;
            foreach (string part in payload.Split(';'))
            {
                string segment = part.Trim(); int separator = segment.IndexOf('=');
                if (separator <= 0) continue;
                string name = segment.Substring(0, separator).Trim();
                if (String.IsNullOrWhiteSpace(name)) continue;
                if (fields.ContainsKey(name))
                {
                    duplicate = true;
                    foreach (string key in WriterFields)
                        if (String.Equals(key, name, StringComparison.InvariantCultureIgnoreCase)) { writerDuplicate = true; break; }
                    continue;
                }
                fields[name] = segment.Substring(separator + 1).Trim();
            }
            if (duplicate) fields["__nlink_duplicate_structured_field"] = true;
            if (writerDuplicate) fields["__nlink_duplicate_writer_evidence_field"] = true;
            return fields;
        }
        private static readonly string[] WriterFields = { "event", "acceptance_run_id", "scenario_run_id", "log_writer_id",
            "log_writer_sequence", "log_writer_batch_generation", "log_writer_batch_first_sequence",
            "log_writer_batch_last_sequence", "log_writer_attempt_utc_ticks" };
        private static readonly string[] ScopeFields = { "session_id", "transfer_id", "frontier_scope_session_id", "frontier_scope_transfer_id" };
        private static int Count(string line, string name)
        { return Regex.Matches(line, @"(?i)(?:^|;)\s*" + name + @"\s*=").Count; }
        private static bool IsToken(string value)
        { return Regex.IsMatch(value, @"\Afte1-[a-f0-9]{24}\z"); }
        private static Scope ReadScope(string line, Hashtable fields)
        {
            var result = new Scope();
            if (line.Contains("\r") || line.Contains("\n")) { result.Error = "native_frontier_scope_invalid"; return result; }
            var counts = new int[4];
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = Count(line, ScopeFields[i]);
                if (counts[i] > 1) { result.Error = "native_frontier_duplicate_scope"; return result; }
            }
            bool projected = counts[2] > 0 || counts[3] > 0;
            if (projected)
                for (int i = 2; i < 4; i++)
                    if (counts[i] != 1 || !IsToken(Text(fields, ScopeFields[i]))) { result.Error = "native_frontier_scope_invalid"; return result; }
            for (int i = 0; i < 2; i++)
            {
                string raw = Text(fields, ScopeFields[i]);
                if (counts[i] != 1 || String.IsNullOrWhiteSpace(raw) || Equal(raw, "(none)")) return result;
                bool redacted = Equal(raw, "[redacted]") || (i == 0 && Equal(raw, "sess_[redacted]"));
                if (raw.Contains("[redacted]") && (!projected || !redacted)) return result;
                string token = projected ? Text(fields, ScopeFields[i + 2]) : Token(raw);
                if (projected && !redacted && !Equal(Token(raw), token)) { result.Error = "native_frontier_scope_conflict"; return result; }
                if (i == 0) result.Session = token; else result.Transfer = token;
            }
            result.Error = ""; return result;
        }
        private static DataSession ReadDataSession(string line, Hashtable fields)
        {
            int rawCount = Count(line, "data_session_identity"), projectedCount = Count(line, "frontier_scope_data_session_identity");
            var result = new DataSession(rawCount > 0 || projectedCount > 0, projectedCount > 0);
            if (line.Contains("\r") || line.Contains("\n")) { result.Error = "native_frontier_data_session_invalid"; return result; }
            if (rawCount > 1 || projectedCount > 1) { result.Error = "native_frontier_duplicate_data_session"; return result; }
            string projected = Text(fields, "frontier_scope_data_session_identity");
            if (result.Projected && !IsToken(projected)) { result.Error = "native_frontier_data_session_invalid"; return result; }
            string raw = Text(fields, "data_session_identity");
            if (rawCount != 1 || String.IsNullOrWhiteSpace(raw) || Equal(raw, "(none)")) return result;
            bool redacted = Regex.IsMatch(raw, @"\A\[redacted\](?::[0-9]+)?\z");
            if (raw.Contains("[redacted]") && (!result.Projected || !redacted)) return result;
            string token = result.Projected ? projected : Token(raw);
            if (result.Projected && !redacted && !Equal(Token(raw), token)) { result.Error = "native_frontier_data_session_conflict"; return result; }
            result.Token = token; result.Error = ""; return result;
        }
        public sealed class Row
        {
            public int Position { get; private set; }
            public Hashtable Fields { get; private set; }
            public Scope Identity { get; private set; }
            public DataSession DataSession { get; private set; }
            internal Row(int position, Hashtable fields, Scope identity, DataSession dataSession)
            { Position = position; Fields = fields; Identity = identity; DataSession = dataSession; }
        }
        public sealed class Scope
        {
            public string Session { get; internal set; }
            public string Transfer { get; internal set; }
            public string Error { get; internal set; }
            internal Scope() { Session = Transfer = ""; Error = "native_frontier_scope_unavailable"; }
        }
        public sealed class DataSession
        {
            public string Token { get; internal set; }
            public bool Present { get; private set; }
            public bool Projected { get; private set; }
            public string Error { get; internal set; }
            internal DataSession(bool present, bool projected)
            { Token = ""; Present = present; Projected = projected; Error = "native_frontier_data_session_unavailable"; }
        }
    }
}
