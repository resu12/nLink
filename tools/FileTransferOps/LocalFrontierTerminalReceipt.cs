using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierPrimaryOutput
        {
            private readonly string terminalDirectory, terminalRoute, terminalStarted;
            private bool terminalAttempted;
            private TerminalReceiptCustody terminalCustody;
            internal TerminalReceiptCustody BeginTerminal(FrontierPrimaryOutputJoin value)
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original || !Object.ReferenceEquals(joined, value)) return null;
                    try
                    {
                        if (terminalAttempted) { if (terminalCustody != null) terminalCustody.Close(); return null; }
                        terminalAttempted = true; DateTimeOffset started;
                        if (!JoinCurrent(value) || !TextValid(terminalRoute, 128) || !TextValid(terminalStarted, 128) ||
                            !DateTimeOffset.TryParse(terminalStarted, out started)) return null;
                        terminalCustody = new TerminalReceiptCustody(this, value); return terminalCustody;
                    }
                    catch { return null; }
                }
            }

            // Private to the trusted original parent/writer call path. It is
            // never passed to a formatter or accepted from serialized input.
            public sealed partial class TerminalReceiptCustody : IDisposable
            {
                private readonly FrontierPrimaryOutput output;
                private readonly FrontierPrimaryOutputJoin join;
                private readonly Stopwatch work = new Stopwatch();
                private int state; // 0 fresh, 1 prepared, 2 written, 3 taken, 4 retired
                private IDictionary original;
                private object returned;
                private LocalFrontierTerminalSnapshot snapshot;
                private TerminalReceipt accepted;
                internal TerminalReceiptCustody(FrontierPrimaryOutput output, FrontierPrimaryOutputJoin join) { this.output = output; this.join = join; }
                private bool Original { get { return output.Original && Object.ReferenceEquals(output.terminalCustody, this); } }
                private void Check()
                { if (state == 4 || work.ElapsedMilliseconds > 5000 || !output.JoinCurrent(join)) throw new InvalidOperationException("frontier_terminal_owner_or_budget"); }
                public string OmissionReason { get; private set; }
                public bool PrepareReceipt(IDictionary receipt, string path)
                { return PrepareReceipt(receipt, path, null); }
                public bool PrepareReceipt(IDictionary receipt, string path, LocalRetainedSourceRequest sourceRequest)
                {
                    lock (output.owner.frontierInputSync) lock (output.sync)
                    {
                        if (!Original || !Object.ReferenceEquals(output.owner, sourceRequest)) return false;
                        bool success = false; work.Start();
                        try
                        {
                            if (state != 0) return false; state = 1; Check();
                            if (!SamePath(path, Path.Combine(output.terminalDirectory, "filetransfer-retained-analysis-attempt-receipt.json"))) return false;
                            snapshot = new LocalFrontierTerminalSnapshot(receipt, Check);
                            var root = output.owner;
                            using (var source = new BinaryReader(new MemoryStream(root.frontierBody, false), new UTF8Encoding(false, true)))
                            {
                                if (snapshot.Text("owner") != source.ReadString() || snapshot.Text("acceptanceRunId") != source.ReadString() ||
                                    snapshot.Text("scenarioRunId") != source.ReadString() || !SamePath(snapshot.Text("analyzerInputPath"), source.ReadString())) return false;
                                string hash = source.ReadString(); source.ReadString(); source.ReadInt64(); long lines = source.ReadInt64();
                                if (snapshot.Text("analyzerInputSha256Before") != hash || snapshot.Text("analyzerInputSha256After") != hash ||
                                    snapshot.Number("analyzerInputLineCountBefore") != lines || snapshot.Number("analyzerInputLineCountAfter") != lines) return false;
                            }
                            if (snapshot.Number("schemaVersion") != 1 || snapshot.Text("event") != "filetransfer_retained_analysis_attempt_receipt" ||
                                snapshot.Text("attemptId") != root.RequestId.ToString("N") || !snapshot.Flag("analyzerInputUnchanged") ||
                                snapshot.Text("liveRouteProofMode") != output.terminalRoute || snapshot.Text("startedUtc") != output.terminalStarted ||
                                !snapshot.Flag("compactRequested") || snapshot.Number("processId") != root.workerId || snapshot.Flag("timedOut") ||
                                !snapshot.Flag("exitCodeAvailable") || snapshot.Number("exitCode") != 0 ||
                                snapshot.Text("status") != (snapshot.Flag("completedProofArtifacts") ? "completed" : "proof_incomplete") ||
                                snapshot.Text("coordinatorSnapshotParityVerdict") != "pass") return false;
                            DateTimeOffset first, last;
                            if (!DateTimeOffset.TryParse(output.terminalStarted, out first) || !DateTimeOffset.TryParse(snapshot.Text("endedUtc"), out last) || last < first) return false;
                            var catalog = join.Transport.TerminalCatalog;
                            if (snapshot.Text("coordinatorSnapshotMode") != catalog[0] || snapshot.Number("coordinatorSnapshotContractVersion") != 1 ||
                                snapshot.Text("coordinatorSnapshotCatalogVersion") != catalog[2] || snapshot.Text("coordinatorSnapshotCatalogHash") != catalog[3] ||
                                snapshot.Text("coordinatorSnapshotBoundaryHash") != catalog[4]) return false;
                            string[] keys = { "stdout", "stderr", "diagnostics", "stageDiagnostics", "compactSummary", "coordinatorSnapshotFullSummary",
                                "coordinatorSnapshotCompactSummary", "coordinatorSnapshotParitySummary", "routeSummary", "operatorVerdict", "stabilitySummary" };
                            string[] names = { "filetransfer-retained-analysis.stdout.txt", "filetransfer-retained-analysis.stderr.txt", "filetransfer-retained-analysis-diagnostics.json",
                                "filetransfer-retained-analysis-stage-diagnostics.json", "filetransfer-retained-compact-summary.json", "filetransfer-coordinator-snapshot-full-summary.json",
                                "filetransfer-coordinator-snapshot-compact-summary.json", "filetransfer-coordinator-snapshot-parity-summary.json", "filetransfer-route-consistency-summary.txt",
                                "filetransfer-operator-verdict.txt", "stability-gates-summary.txt" };
                            for (int i = 0; i < keys.Length; i++)
                                if (!SamePath(snapshot.Text(keys[i] + ".path"), Path.Combine(output.terminalDirectory, names[i])) ||
                                    !Regex.IsMatch(snapshot.Text(keys[i] + ".sha256") ?? "", @"\A[0-9a-f]{64}\z")) return false;
                            if (snapshot.Text("stdout.sha256") != output.ReportHash(0, Check) || snapshot.Text("stderr.sha256") != output.ReportHash(1, Check)) return false;
                            original = receipt; Check(); success = true; return true;
                        }
                        catch (Exception error) { OmissionReason = error.Message; return false; }
                        finally { work.Stop(); if (!success) Close(); }
                    }
                }
                public bool RecordWrittenReceipt(IDictionary receipt, object actualReturn)
                {
                    lock (output.owner.frontierInputSync) lock (output.sync)
                    {
                        if (!Original) return false;
                        bool success = false; work.Start();
                        try
                        {
                            Check(); if (state != 1 || !Object.ReferenceEquals(original, receipt) || !snapshot.Matches(receipt) || !snapshot.BindReturn(actualReturn)) return false;
                            returned = actualReturn; state = 2; Check(); success = true; return true;
                        }
                        catch (Exception error) { OmissionReason = error.Message; return false; }
                        finally { work.Stop(); if (!success) Close(); }
                    }
                }
                public TerminalReceipt Take(object actualReturn)
                {
                    lock (output.owner.frontierInputSync) lock (output.sync)
                    {
                        if (!Original) return null;
                        bool success = false; work.Start();
                        try
                        {
                            Check(); if (state != 2 || !Object.ReferenceEquals(returned, actualReturn) || !snapshot.Matches(original) || !snapshot.MatchesReturn(actualReturn)) return null;
                            accepted = new TerminalReceipt(this, snapshot.Text("status")); state = 3; Check(); success = true; return accepted;
                        }
                        catch (Exception error) { OmissionReason = error.Message; return null; }
                        finally { work.Stop(); if (!success) Close(); }
                    }
                }
                internal bool Current(TerminalReceipt value)
                {
                    lock (output.owner.frontierInputSync) lock (output.sync)
                    {
                        if (!Original || !Object.ReferenceEquals(accepted, value)) return false;
                        work.Start(); try { Check(); if (state == 3 && snapshot.Matches(original) && snapshot.MatchesReturn(returned)) return true; }
                        catch { } finally { work.Stop(); }
                        Close(); return false;
                    }
                }
                internal void Close()
                { CloseDiagnostic(); state = 4; original = null; returned = null; snapshot = null; accepted = null; if (OmissionReason == null) OmissionReason = "frontier_terminal_binding_or_state"; }
                public void Dispose() { lock (output.owner.frontierInputSync) lock (output.sync) { if (Original) Close(); } }
                private static bool SamePath(string first, string second)
                { return first != null && String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
            }
            public sealed class TerminalReceipt
            {
                private readonly TerminalReceiptCustody owner;
                public string Status { get; private set; }
                internal TerminalReceipt(TerminalReceiptCustody owner, string status) { this.owner = owner; Status = status; }
                public bool IsCurrent { get { return owner.Current(this); } }
                public TerminalDiagnosticEvidence TryPersistDiagnostic() { return owner.PersistDiagnostic(this); }
                public bool LiveProofEligible { get { return false; } }
                public bool NativeWriterCustodyEstablished { get { return false; } }
                public bool PublicationCompletionIssued { get { return false; } }
            }
        }
    }
}
