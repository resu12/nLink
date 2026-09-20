using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierPrimaryOutput
        {
            public sealed partial class TerminalReceiptCustody
            {
                private bool diagnosticAttempted;
                private LocalFrontierEvidenceFile diagnosticFile;
                private TerminalDiagnosticEvidence diagnosticEvidence;
                private void RequireDiagnosticInput()
                {
                    Check();
                    if (state != 3 || !snapshot.Matches(original) || !snapshot.MatchesReturn(returned))
                        throw new InvalidOperationException("frontier_terminal_diagnostic_input");
                }
                internal TerminalDiagnosticEvidence PersistDiagnostic(TerminalReceipt value)
                {
                    lock (output.owner.frontierInputSync) lock (output.sync)
                    {
                        if (!Original || value == null || !Object.ReferenceEquals(accepted, value)) return null;
                        bool success = false; byte[] packet = null, bytes = null; work.Start();
                        try
                        {
                            if (diagnosticAttempted) return null; diagnosticAttempted = true;
                            RequireDiagnosticInput();
                            packet = join.Transport.CopyPayload();
                            if (packet == null || packet.Length < 1 || packet.Length > LocalFrontierDiagnosticChannel.MaximumBytes) return null;
                            bytes = EncodeDiagnostic(packet); RequireDiagnosticInput();
                            diagnosticFile = new LocalFrontierEvidenceFile(CancellationToken.None);
                            string destination = Path.Combine(output.terminalDirectory, "filetransfer-frontier-terminal-diagnostic.json");
                            if (!diagnosticFile.TryPersistDiagnostic(destination, bytes, () => { RequireDiagnosticInput(); return true; })) return null;
                            RequireDiagnosticInput();
                            diagnosticEvidence = new TerminalDiagnosticEvidence(this, diagnosticFile.Output);
                            success = diagnosticFile.IsCurrent; return success ? diagnosticEvidence : null;
                        }
                        catch { return null; }
                        finally
                        {
                            work.Stop(); if (packet != null) Array.Clear(packet, 0, packet.Length);
                            if (bytes != null) Array.Clear(bytes, 0, bytes.Length); if (!success) CloseDiagnostic();
                        }
                    }
                }
                private byte[] EncodeDiagnostic(byte[] packet)
                {
                    // Fixed-schema observations from the original native join.
                    // No formatter, caller JSON, destination, or success flag.
                    var json = new FrontierEvidenceJson();
                    json.Add("{\"schemaVersion\":1,\"event\":\"filetransfer_frontier_terminal_diagnostic\",\"diagnosticOnly\":true");
                    foreach (string key in new[] { "owner", "attemptId", "acceptanceRunId", "scenarioRunId", "status", "liveRouteProofMode", "startedUtc", "endedUtc",
                        "analyzerInputSha256After", "coordinatorSnapshotCatalogVersion", "coordinatorSnapshotCatalogHash", "coordinatorSnapshotBoundaryHash" })
                    { json.Add(","); json.Text(key); json.Add(":"); json.Text(snapshot.Text(key)); }
                    json.Add(",\"processId\":"); json.Number(output.owner.workerId);
                    json.Add(",\"analyzerInputLineCount\":"); json.Number(snapshot.Number("analyzerInputLineCountAfter"));
                    json.Add(",\"fullInvocationId\":"); json.Text(join.Transport.FullInvocationId.ToString("D"));
                    json.Add(",\"compactInvocationId\":"); json.Text(join.Transport.CompactInvocationId.ToString("D"));
                    json.Add(",\"observationId\":"); json.Text(join.Transport.ObservationId.ToString("D"));
                    json.Add(",\"witnessCount\":"); json.Number(join.Transport.WitnessCount);
                    json.Add(",\"stdoutReportSha256\":"); json.Text(snapshot.Text("stdout.sha256"));
                    json.Add(",\"stderrReportSha256\":"); json.Text(snapshot.Text("stderr.sha256"));
                    json.Add(",\"packetFormat\":\"nlink-frontier-diagnostic-v2\",\"packetByteCount\":"); json.Number(packet.Length);
                    json.Add(",\"packetSha256\":");
                    using (var hash = SHA256.Create()) json.Text(LocalFrontierDiagnosticPacket.Hex(hash.ComputeHash(packet)));
                    json.Add(",\"liveProofEligible\":false,\"nativeWriterCustodyEstablished\":false,\"publicationCompletionIssued\":false,\"packetBase64\":\"");
                    byte[] header = json.Encode();
                    byte[] payload = Encoding.ASCII.GetBytes(Convert.ToBase64String(packet));
                    byte[] result = new byte[checked(header.Length + payload.Length + 2)];
                    Buffer.BlockCopy(header, 0, result, 0, header.Length); Buffer.BlockCopy(payload, 0, result, header.Length, payload.Length);
                    result[result.Length - 2] = (byte)'"'; result[result.Length - 1] = (byte)'}';
                    Array.Clear(payload, 0, payload.Length); return result;
                }
                internal bool DiagnosticCurrent(TerminalDiagnosticEvidence value)
                {
                    lock (output.owner.frontierInputSync) lock (output.sync)
                    {
                        if (!Original || value == null || !Object.ReferenceEquals(diagnosticEvidence, value)) return false;
                        work.Start();
                        try { RequireDiagnosticInput(); if (diagnosticFile != null && diagnosticFile.IsCurrent) return true; }
                        catch { } finally { work.Stop(); }
                        CloseDiagnostic(); return false;
                    }
                }
                private void CloseDiagnostic()
                { if (diagnosticFile != null) diagnosticFile.Dispose(); diagnosticFile = null; diagnosticEvidence = null; }
                internal void DisposeDiagnostic(TerminalDiagnosticEvidence value)
                { lock (output.owner.frontierInputSync) lock (output.sync) { if (Original && Object.ReferenceEquals(diagnosticEvidence, value)) CloseDiagnostic(); } }
            }
            public sealed class TerminalDiagnosticEvidence : IDisposable
            {
                private readonly TerminalReceiptCustody owner;
                public GuiSmokeLedgerCapturedOutput Output { get; private set; }
                internal TerminalDiagnosticEvidence(TerminalReceiptCustody owner, GuiSmokeLedgerCapturedOutput output) { this.owner = owner; Output = output; }
                public bool IsCurrent { get { return owner.DiagnosticCurrent(this); } }
                public bool LiveProofEligible { get { return false; } }
                public bool NativeWriterCustodyEstablished { get { return false; } }
                public bool PublicationCompletionIssued { get { return false; } }
                public void Dispose() { owner.DisposeDiagnostic(this); }
            }
        }
    }
}
