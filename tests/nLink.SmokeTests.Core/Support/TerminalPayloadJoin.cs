#if NLINK_TEST_TERMINAL_REQUEST && NLINK_FRONTIER_LEDGER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class Worker
        {
            public bool TerminalPayloadJoinWasIssuedForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && collectorMembership != null && collectorMembership.TerminalPayloadWasIssued; } }
            public bool TerminalPayloadJoinRetiredForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && (collectorMembership == null || !collectorMembership.TerminalPayloadJoinedForTest); } }
            public bool TerminalIntegrityWasConsumedForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && collectorMembership != null && collectorMembership.TerminalIntegrityWasConsumed; } }
            public bool TerminalIntegrityRetiredForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && (collectorMembership == null || !collectorMembership.TerminalIntegrityCurrentForTest); } }
        }

        public sealed partial class CollectorMembershipForTest
        {
            private int terminalJoinAttempted;
            private bool terminalJoined;
            internal bool TerminalPayloadWasIssued { get { return Object.ReferenceEquals(original, this) && terminalJoined; } }
            private TerminalWorkerFilesForTest terminalFiles;
            private long terminalVisits, terminalUnits;
            private int integrityAttempted;
            private bool integrityConsumed;
            internal bool TerminalIntegrityWasConsumed
            { get { return Object.ReferenceEquals(original, this) && integrityConsumed; } }
            public bool TerminalIntegrityCurrentForTest
            { get { return Object.ReferenceEquals(original, this) && integrityConsumed && TerminalPayloadJoinedForTest; } }

            // Test-only consumption of private live state, not a diagnostic Boolean.
            // The bounded observer supplies the cost of its scalar/candidate projection.
            public bool TryConsumeTerminalIntegrityForTest(Worker candidate, TerminalWorkerFilesForTest candidateFiles,
                SourceReader reader, PrimaryEvaluationCustody evaluationCustody, CompletedEvaluations evaluations,
                object inputs, string[] fullLines, string[] compactLines, object fullResult, object compactResult,
                Stopwatch work, long observationVisits, long observationUnits)
            {
                if (!Owns(candidate, reader, evaluationCustody, evaluations, inputs, fullLines, compactLines, fullResult, compactResult) ||
                    !Object.ReferenceEquals(candidateFiles, terminalFiles) || !Object.ReferenceEquals(work, originJoinWork) ||
                    work == null || !work.IsRunning || work.ElapsedMilliseconds > 20000 || !TerminalPayloadJoinedForTest)
                    return false;
                if (Interlocked.CompareExchange(ref integrityAttempted, 1, 0) != 0) return false;
                long visits = 7L * full.Length + 4L * compact.Length + membership.RecordVisits + originJoinVisits + terminalVisits;
                if (observationVisits < 0 || observationVisits > 4096 - visits || observationUnits < 0 ||
                    observationUnits > LocalPublicationInputLimits.ProducerCodeUnits - membership.CodeUnitVisits - terminalUnits)
                    return false;
                integrityConsumed = TerminalPayloadJoinedForTest && work.ElapsedMilliseconds <= 20000;
                return TerminalIntegrityCurrentForTest;
            }
            public long TerminalPayloadRecordVisitsForTest { get { return terminalVisits; } }
            public long TerminalPayloadCodeUnitsForTest { get { return terminalUnits; } }
            public bool TerminalPayloadJoinedForTest
            {
                get { return terminalJoined && OriginsJoinedForTest && terminalFiles != null &&
                    terminalFiles.OwnsPrimary(owner, source, custody, pair, context, full, compact, fullView, compactView); }
            }

            // The supplied indexed records are evidence only. Their original
            // source references, parsed fields and producer positions must all
            // match the binding inside the original protected-file handoff.
            public bool TryJoinTerminalPayloadForTest(Worker candidate, TerminalWorkerFilesForTest candidateFiles,
                SourceReader reader, PrimaryEvaluationCustody evaluationCustody, CompletedEvaluations evaluations,
                object inputs, string[] fullLines, string[] compactLines, object fullResult, object compactResult,
                object[] terminals, Stopwatch work)
            {
                if (!Owns(candidate, reader, evaluationCustody, evaluations, inputs, fullLines, compactLines, fullResult, compactResult) ||
                    !OriginsJoinedForTest || candidateFiles == null ||
                    !candidateFiles.OwnsPrimary(candidate, reader, evaluationCustody, evaluations, inputs,
                        fullLines, compactLines, fullResult, compactResult) ||
                    !Object.ReferenceEquals(work, originJoinWork) || !work.IsRunning || work.ElapsedMilliseconds > 20000)
                    return false;
                if (Interlocked.CompareExchange(ref terminalJoinAttempted, 1, 0) != 0) return false;
                terminalFiles = candidateFiles;
                var binding = candidateFiles.OwnedTransfer;
                var originalLines = PSObject.AsPSObject(context).Properties["FullOriginal"].Value as string[];
                if (binding == null || terminals == null || terminals.Length != 2 ||
                    !membership.OwnsCollectorOriginInputs(writer, full, originalLines, work) ||
                    7L * full.Length + 4L * compact.Length + membership.RecordVisits + originJoinVisits + full.Length + 2 > 4096)
                    return false;
                int inbound = -1, outbound = -1;
                for (int i = 0; i < full.Length; i++)
                {
                    terminalVisits++;
                    if (!OriginsJoinedForTest || !candidateFiles.IsCurrent || !Object.ReferenceEquals(full[i], originalLines[i])) return false;
                    if (joinedOrigins[i].ProducerLine == binding.InboundLine) { if (inbound != -1) return false; inbound = i; }
                    if (joinedOrigins[i].ProducerLine == binding.OutboundLine) { if (outbound != -1) return false; outbound = i; }
                }
                if (inbound < 0 || outbound < 0 || inbound == outbound) return false;
                bool sawInbound = false, sawOutbound = false;
                foreach (var terminal in terminals)
                {
                    terminalVisits++;
                    if (!OriginsJoinedForTest || !candidateFiles.IsCurrent || terminal == null) return false;
                    var item = PSObject.AsPSObject(terminal);
                    var raw = item.Properties["RawLine"] == null ? null : item.Properties["RawLine"].Value as string;
                    int index = Object.ReferenceEquals(raw, full[inbound]) ? inbound :
                        Object.ReferenceEquals(raw, full[outbound]) ? outbound : -1;
                    if (index < 0 || item.Properties["LineNumber"] == null ||
                        !LanguagePrimitives.Equals(item.Properties["LineNumber"].Value, index + 1, false, CultureInfo.InvariantCulture)) return false;
                    if (raw.Length > LocalPublicationInputLimits.LineCodeUnits ||
                        4L * raw.Length > LocalPublicationInputLimits.ProducerCodeUnits - membership.CodeUnitVisits - terminalUnits) return false;
                    // Bound the prefix, split, field and whitespace scans of
                    // the two original strings; no file or whole-log replay.
                    terminalUnits += 4L * raw.Length;
                    var fields = TerminalFields(raw);
                    string direction = index == inbound ? "inbound" : "outbound";
                    if (fields == null || Value(fields, "event") != "transfer_terminal" ||
                        Value(fields, "direction") != direction || Value(fields, "terminal_session_id") != binding.Session ||
                        Value(fields, "terminal_transfer_id") != binding.Transfer || Value(fields, "error_code") != "(none)" ||
                        Value(fields, "reason") != "Transfer complete." || Value(fields, "file_size_bytes") != "262144" ||
                        Value(fields, "bytes_transferred") != "262144") return false;
                    if (index == inbound) { if (sawInbound) return false; sawInbound = true; }
                    else { if (sawOutbound) return false; sawOutbound = true; }
                }
                terminalJoined = sawInbound && sawOutbound && OriginsJoinedForTest && work.IsRunning &&
                    candidateFiles.PayloadsMatch(262144) &&
                    Owns(candidate, reader, evaluationCustody, evaluations, inputs, fullLines, compactLines, fullResult, compactResult);
                return TerminalPayloadJoinedForTest;
            }

            private static readonly HashSet<string> TerminalKeys = new HashSet<string>(StringComparer.Ordinal)
            { "event", "direction", "terminal_session_id", "terminal_transfer_id", "error_code", "reason", "file_size_bytes", "bytes_transferred" };
            internal static Dictionary<string, string> TerminalFields(string raw)
            {
                int prefix = raw.LastIndexOf("] ", StringComparison.Ordinal);
                var parts = raw.Substring(prefix < 0 ? 0 : prefix + 2).Split(new[] { ';' }, 65);
                if (parts.Length > 64) return null;
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var part in parts)
                {
                    int equals = part.IndexOf('=');
                    if (equals < 1) continue;
                    string key = part.Substring(0, equals).Trim();
                    if (!TerminalKeys.Contains(key)) continue;
                    if (fields.ContainsKey(key)) return null;
                    fields.Add(key, part.Substring(equals + 1).Trim());
                }
                return fields;
            }
            internal static string Value(Dictionary<string, string> fields, string name)
            { string value; return fields.TryGetValue(name, out value) ? value : null; }
        }
    }
}
#endif
