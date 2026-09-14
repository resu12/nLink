using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        private ParentPublicationSource parentPublicationSource;
        private ParentAdoptionCustody adoptionCustody;
        private int adoptionAttempted;

        private sealed class ParentPublicationSource
        {
            internal readonly string Owner, Run, Scenario, Path, Hash;
            internal readonly long Lines;
            internal ParentPublicationSource(string owner, string run, string scenario, string path, string hash, long lines)
            { Owner = owner; Run = run; Scenario = scenario; Path = path; Hash = hash; Lines = lines; }
        }

        // Claimed by the actual retained parent before launching its worker.
        // Keep this capability private; never pass it to an optional consumer.
        public ParentAdoptionCustody TryBeginAdoption(string artifactDirectory, string routeMode, bool compactRequested, string startedUtc)
        {
            if (Interlocked.Exchange(ref adoptionAttempted, 1) != 0) return null;
            try
            {
                DateTimeOffset started;
                if (Volatile.Read(ref disposed) != 0 || workerExit != null || parentPublicationSource == null ||
                    publicationChannel == null || !compactRequested || !TextValid(artifactDirectory, 4096) ||
                    !TextValid(routeMode, 128) || !TextValid(startedUtc, 128) || !DateTimeOffset.TryParse(startedUtc, out started)) return null;
                adoptionCustody = new ParentAdoptionCustody(this, Path.GetFullPath(artifactDirectory), routeMode, startedUtc);
                return adoptionCustody;
            }
            catch { return null; }
        }

        private bool ParentPublicationCurrent()
        {
            try
            {
                int code;
                return Volatile.Read(ref disposed) == 0 && frozenSource != null && frozenSource.CanRead &&
                    workerExit != null && workerId > 0 && workerCreation > 0 && workerExit.WaitOne(0) &&
                    GetExitCodeProcess(workerExit.SafeWaitHandle, out code) && code == 0;
            }
            catch { return false; }
        }

        public sealed class ParentAdoptionCustody : IDisposable
        {
            private readonly LocalRetainedSourceRequest owner;
            private readonly string directory, routeMode, started;
            private readonly Stopwatch work = new Stopwatch();
            private int state; // 0 unprepared, 1 prepared, 2 written, 3 consumed, 4 omitted
            private IDictionary original;
            private Dictionary<string, object> snapshot;
            private object returnedReceipt;
            private TerminalPublicationReceipt terminal;
            internal ParentAdoptionCustody(LocalRetainedSourceRequest owner, string directory, string routeMode, string started)
            { this.owner = owner; this.directory = directory; this.routeMode = routeMode; this.started = started; }
            public long ActiveMilliseconds { get { return work.ElapsedMilliseconds; } }
            public string OmissionReason { get; private set; }

            // Trusted original receipt writer only, immediately before its
            // existing atomic write. No JSON conversion or input read.
            public bool PrepareReceipt(IDictionary receipt, string path)
            {
                if (state != 0) return Omit();
                state = 1; work.Start();
                try
                {
                    Check();
                    if (!SamePath(path, Path.Combine(directory, "filetransfer-retained-analysis-attempt-receipt.json"))) return Omit();
                    snapshot = Snapshot(receipt);
                    var source = owner.parentPublicationSource;
                    if (!Equals(snapshot["schemaVersion"], 1) || Text("event") != "filetransfer_retained_analysis_attempt_receipt" ||
                        Text("owner") != source.Owner || Text("attemptId") != owner.RequestId.ToString("N") ||
                        Text("acceptanceRunId") != source.Run || Text("scenarioRunId") != source.Scenario ||
                        !SamePath(Text("analyzerInputPath"), source.Path) || Text("analyzerInputSha256Before") != source.Hash ||
                        Text("analyzerInputSha256After") != source.Hash || Number("analyzerInputLineCountBefore") != source.Lines ||
                        Number("analyzerInputLineCountAfter") != source.Lines || !Flag("analyzerInputUnchanged") ||
                        Text("liveRouteProofMode") != routeMode || !Flag("compactRequested") || Number("processId") != owner.workerId ||
                        Text("startedUtc") != started || Flag("timedOut") || !Flag("exitCodeAvailable") || Number("exitCode") != 0 ||
                        Text("status") != (Flag("completedProofArtifacts") ? "completed" : "proof_incomplete") ||
                        Text("coordinatorSnapshotMode") != "SnapshotV1Authoritative" || Number("coordinatorSnapshotContractVersion") != 1 ||
                        Text("coordinatorSnapshotParityVerdict") != "pass" || !Hash(Text("coordinatorSnapshotCatalogHash")) ||
                        !Hash(Text("coordinatorSnapshotBoundaryHash")) || !TextValid(Text("coordinatorSnapshotCatalogVersion"), 1024)) return Omit();
                    DateTimeOffset first, last;
                    if (!DateTimeOffset.TryParse(started, out first) || !DateTimeOffset.TryParse(Text("endedUtc"), out last) || last < first) return Omit();
                    string[] keys = { "stdout", "stderr", "diagnostics", "stageDiagnostics", "compactSummary", "coordinatorSnapshotFullSummary",
                        "coordinatorSnapshotCompactSummary", "coordinatorSnapshotParitySummary", "routeSummary", "operatorVerdict", "stabilitySummary" };
                    string[] names = { "filetransfer-retained-analysis.stdout.txt", "filetransfer-retained-analysis.stderr.txt",
                        "filetransfer-retained-analysis-diagnostics.json", "filetransfer-retained-analysis-stage-diagnostics.json",
                        "filetransfer-retained-compact-summary.json", "filetransfer-coordinator-snapshot-full-summary.json",
                        "filetransfer-coordinator-snapshot-compact-summary.json", "filetransfer-coordinator-snapshot-parity-summary.json",
                        "filetransfer-route-consistency-summary.txt", "filetransfer-operator-verdict.txt", "stability-gates-summary.txt" };
                    for (int i = 0; i < keys.Length; i++)
                        if (!SamePath(Text(keys[i] + ".path"), Path.Combine(directory, names[i])) || !Hash(Text(keys[i] + ".sha256"))) return Omit();
                    var codec = new PublicationCodec(Math.Max(1, 250 - (int)work.ElapsedMilliseconds), () => owner.ParentPublicationCurrent());
                    string catalog = codec.Object("contractVersion", 1, "catalogVersion", Text("coordinatorSnapshotCatalogVersion"),
                        "catalogHash", Text("coordinatorSnapshotCatalogHash"), "boundaryHash", Text("coordinatorSnapshotBoundaryHash"));
                    terminal = new TerminalPublicationReceipt(owner.RequestId, source.Owner, source.Run, source.Scenario,
                        Path.GetFullPath(path), Text("status"), codec.Hash(catalog));
                    original = receipt; Check(); return true;
                }
                catch (Exception error) { OmissionReason = error.Message; return Omit(); }
                finally { work.Stop(); }
            }

            // Only after the original atomic write returns successfully. The
            // mutable returned receipt is an identity anchor, not trusted data.
            public bool RecordWrittenReceipt(IDictionary receipt, object actualReturn)
            {
                if (state != 1) return Omit();
                work.Start();
                try
                {
                    Check();
                    if (actualReturn == null || !Object.ReferenceEquals(original, receipt) || !Unchanged(receipt)) return Omit();
                    returnedReceipt = actualReturn; state = 2; Check(); return true;
                }
                catch (Exception error) { OmissionReason = error.Message; return Omit(); }
                finally { work.Stop(); }
            }

            public AdoptedPublication Take(object actualReturn)
            {
                if (state != 2) { Omit(); return null; }
                state = 3; work.Start();
                try
                {
                    Check();
                    if (!Object.ReferenceEquals(returnedReceipt, actualReturn) || !Unchanged(original))
                    { OmissionReason = "terminal_receipt_identity_or_content"; return null; }
                    var value = owner.TakePublicationAfterExit();
                    if (value == null || value.Completion.CatalogSha256 != terminal.CatalogSha256)
                    { OmissionReason = "publication_transport_or_catalog"; return null; }
                    Check(); return new AdoptedPublication(terminal, value);
                }
                catch (Exception error) { OmissionReason = error.Message; return null; }
                finally { work.Stop(); Dispose(); }
            }

            private void Check()
            { if (work.ElapsedMilliseconds > 250 || !owner.ParentPublicationCurrent()) throw new InvalidOperationException("parent_adoption_owner_or_budget"); }
            private string Text(string key) { return snapshot[key] as string; }
            private bool Flag(string key)
            {
                if (!(snapshot[key] is bool)) throw new InvalidOperationException("receipt_boolean_type");
                return (bool)snapshot[key];
            }
            private long Number(string key)
            {
                object value = snapshot[key];
                if (!(value is int) && !(value is long)) throw new InvalidOperationException("receipt_integer_type");
                return Convert.ToInt64(value);
            }
            private static bool Hash(string value) { return value != null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z"); }
            private static bool SamePath(string a, string b)
            { return a != null && String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            private Dictionary<string, object> Snapshot(IDictionary value)
            {
                if (value == null || value.Count > 64) throw new InvalidOperationException("receipt_shape_limit");
                var result = new Dictionary<string, object>(StringComparer.Ordinal); int characters = 0, nested = 0;
                foreach (DictionaryEntry entry in value)
                {
                    Check(); string key = entry.Key as string;
                    if (!TextValid(key, 128) || key.IndexOf('.') >= 0) throw new InvalidOperationException("receipt_key");
                    var fields = entry.Value as IDictionary;
                    if (fields == null) AddScalar(result, key, entry.Value, ref characters);
                    else
                    {
                        if (++nested > 11 || fields.Count != 2 || !fields.Contains("path") || !fields.Contains("sha256")) throw new InvalidOperationException("receipt_artifact_shape");
                        AddScalar(result, key + ".path", fields["path"], ref characters);
                        AddScalar(result, key + ".sha256", fields["sha256"], ref characters);
                    }
                }
                return result;
            }
            private static void AddScalar(Dictionary<string, object> result, string key, object value, ref int characters)
            {
                // Original PowerShell command results can wrap even a string.
                // Unwrap only the genuine runtime wrapper, never properties or
                // conversions on a caller-authored lookalike.
                var wrapped = value as PSObject;
                if (wrapped != null) value = wrapped.BaseObject;
                var text = value as string;
                if (text != null)
                { if (text.Length > 4096 || (characters += key.Length + text.Length) > 32768) throw new InvalidOperationException("receipt_text_limit"); }
                else if (!(value is int) && !(value is long) && !(value is bool)) throw new InvalidOperationException("receipt_scalar_type:" + key + ":" + (value == null ? "null" : value.GetType().FullName));
                result.Add(key, value);
            }
            private bool Unchanged(IDictionary value)
            {
                var now = Snapshot(value);
                if (now.Count != snapshot.Count) return false;
                foreach (var pair in snapshot) { object current; if (!now.TryGetValue(pair.Key, out current) || !Equals(pair.Value, current)) return false; }
                return true;
            }
            private bool Omit() { if (OmissionReason == null) OmissionReason = "terminal_receipt_binding_or_state"; Dispose(); return false; }
            public void Dispose()
            {
                state = 4; original = null; snapshot = null; returnedReceipt = null; terminal = null;
                if (owner.publicationChannel != null) owner.publicationChannel.Freeze();
            }
        }

        public sealed class TerminalPublicationReceipt
        {
            public Guid RequestId { get; private set; }
            public string Owner { get; private set; }
            public string AcceptanceRunId { get; private set; }
            public string ScenarioRunId { get; private set; }
            public string Path { get; private set; }
            public string Status { get; private set; }
            public string CatalogSha256 { get; private set; }
            internal TerminalPublicationReceipt(Guid request, string owner, string run, string scenario, string path, string status, string catalog)
            { RequestId = request; Owner = owner; AcceptanceRunId = run; ScenarioRunId = scenario; Path = path; Status = status; CatalogSha256 = catalog; }
        }
        public sealed class AdoptedPublication
        {
            private readonly TransportPublication transport;
            private readonly long adoptedAt = Stopwatch.GetTimestamp();
            private int persistenceAttempted;
            public TerminalPublicationReceipt TerminalReceipt { get; private set; }
            public PublicationCompletion Completion { get { return transport.Completion; } }
            public string OutputNamespace { get { return transport.OutputNamespace; } }
            internal AdoptedPublication(TerminalPublicationReceipt terminal, TransportPublication transport)
            { TerminalReceipt = terminal; this.transport = transport; }
            public byte[] CopyPayload() { return transport.CopyPayload(); }

            // A historical diagnostic, never route/runtime authority. The
            // trusted callsite supplies scope captured before this invocation.
            public bool TryPersistForAttempt(string artifactDirectory, string run, string scenario, long attemptStarted)
            {
                string stage = null; bool stageOwned = false;
                try
                {
                    if (attemptStarted <= 0 || adoptedAt < attemptStarted || !TextValid(artifactDirectory, 4096) ||
                        !Regex.IsMatch(artifactDirectory, @"\A[A-Za-z]:[\\/]") || TerminalReceipt.Owner != "RouteAcceptance" ||
                        TerminalReceipt.AcceptanceRunId != run || TerminalReceipt.ScenarioRunId != scenario) return false;
                    string directory = Path.GetFullPath(artifactDirectory);
                    if (!String.Equals(TerminalReceipt.Path, Path.Combine(directory, "filetransfer-retained-analysis-attempt-receipt.json"),
                        StringComparison.OrdinalIgnoreCase) || Interlocked.Exchange(ref persistenceAttempted, 1) != 0) return false;
                    var codec = new PublicationCodec(250, () => true);
                    if (new DriveInfo(Path.GetPathRoot(directory)).DriveType != DriveType.Fixed) return false;
                    var ancestor = new DirectoryInfo(directory); int depth = 0;
                    while (ancestor != null)
                    {
                        codec.Check();
                        if (++depth > 64 || (File.GetAttributes(ancestor.FullName) & FileAttributes.ReparsePoint) != 0) return false;
                        ancestor = ancestor.Parent;
                    }
                    string terminal = codec.Object("requestId", TerminalReceipt.RequestId.ToString("D"), "owner", TerminalReceipt.Owner,
                        "acceptanceRunId", TerminalReceipt.AcceptanceRunId, "scenarioRunId", TerminalReceipt.ScenarioRunId,
                        "path", TerminalReceipt.Path, "status", TerminalReceipt.Status, "catalogSha256", TerminalReceipt.CatalogSha256,
                        "workerProcessId", Completion.WorkerProcessId);
                    string payload = new UTF8Encoding(false, true).GetString(CopyPayload());
                    // Raw JSON is exclusively the privately issued canonical
                    // payload. No caller-authored object/JSON is deserialized.
                    byte[] bytes = codec.Bytes(codec.Object("format", "nlink-adopted-local-publication-v1",
                        "authorityEffect", "none", "terminalReceipt", new PublicationCodec.Raw(terminal),
                        "publication", new PublicationCodec.Raw(payload)));
                    if (bytes.Length > PublicationCodec.MaximumBytes) return false;
                    string destination = Path.Combine(directory, "filetransfer-local-publication-diagnostic.json");
                    stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    codec.Check();
                    using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stageOwned = true; stream.Write(bytes, 0, bytes.Length); stream.Flush();
                    }
                    codec.Check();
                    File.Move(stage, destination); // Atomic same-directory rename; never overwrite.
                    stageOwned = false; return true;
                }
                catch { return false; }
                finally { if (stageOwned) { try { File.Delete(stage); } catch { } } }
            }
        }
    }
}
