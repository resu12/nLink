using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        private bool frontierOutputAttempted;
        private FrontierPrimaryOutput frontierOutput;

        // The original parent starts an unstarted process and owns its first
        // pipe reads. A caller task, reader, EOF flag or existing file is not input.
        public FrontierPrimaryOutput TryStartFrontierWorkerWithOutput(Process process, string artifactDirectory)
        { return TryStartFrontierWorkerWithOutput(process, artifactDirectory, null, null); }

        public FrontierPrimaryOutput TryStartFrontierWorkerWithOutput(Process process, string artifactDirectory, string routeMode, string startedUtc)
        {
            lock (frontierInputSync)
            {
                if (!Object.ReferenceEquals(frontierOriginal, this)) return null;
                bool success = false; LocalGuiWriterRequest.FrontierOutputCapture capture = null;
                try
                {
                    if (frontierOutputAttempted) { if (frontierOutput != null) frontierOutput.Close(); return null; }
                    frontierOutputAttempted = true;
                    if (workerExit != null || frontierInputs == null || !frontierInputs.IsCurrent || process == null ||
                        !TextValid(artifactDirectory, 4096) || !Directory.Exists(artifactDirectory)) return null;
                    try { int existing = process.Id; return null; } catch (InvalidOperationException) { }
                    var start = process.StartInfo;
                    if (start.UseShellExecute || !start.RedirectStandardOutput || !start.RedirectStandardError) return null;
                    capture = new LocalGuiWriterRequest.FrontierOutputCapture(Path.Combine(Path.GetFullPath(artifactDirectory),
                        "frontier-primary-output-" + RequestId.ToString("N")));
                    if (!process.Start()) return null;
                    capture.Start(process);
                    if (!BindWorker(process) || !frontierInputs.IsCurrent) return null;
                    frontierOutput = new FrontierPrimaryOutput(this, capture, new LocalFrontierPrimaryScope(map, frontierBody),
                        process, Path.GetFullPath(artifactDirectory), routeMode, startedUtc);
                    frontierOutput.Watch();
                    success = frontierOutput.Check(); return success ? frontierOutput : null;
                }
                catch { return null; }
                finally
                {
                    // Never close live read pipes or kill an already-started
                    // worker here. The caller retains its outer process deadline.
                    if (!success) { if (frontierOutput != null) frontierOutput.Close(); if (capture != null) capture.Revoke(); }
                }
            }
        }

        public sealed partial class FrontierPrimaryOutput : IDisposable
        {
            private readonly LocalRetainedSourceRequest owner;
            private readonly LocalGuiWriterRequest.FrontierOutputCapture capture;
            private readonly LocalFrontierPrimaryScope scope;
            private readonly object sync = new object();
            private RegisteredWaitHandle cancellation, exit;
            private bool closed, joinAttempted;
            private FrontierPrimaryOutputJoin joined;
            internal FrontierPrimaryOutput(LocalRetainedSourceRequest owner, LocalGuiWriterRequest.FrontierOutputCapture capture, LocalFrontierPrimaryScope scope,
                Process process, string directory, string routeMode, string startedUtc)
            {
                this.owner = owner; this.capture = capture; this.scope = scope;
                terminalDirectory = directory; terminalRoute = routeMode; terminalStarted = startedUtc;
                outputEncodings = new[] { (System.Text.Encoding)process.StandardOutput.CurrentEncoding.Clone(), (System.Text.Encoding)process.StandardError.CurrentEncoding.Clone() };
            }
            private bool Original { get { return Object.ReferenceEquals(owner.frontierOriginal, owner) && Object.ReferenceEquals(owner.frontierOutput, this); } }
            private bool ExitSuccessful()
            { int code; try { return owner.workerExit != null && owner.workerExit.WaitOne(0) && GetExitCodeProcess(owner.workerExit.SafeWaitHandle, out code) && code == 0; } catch { return false; } }
            internal bool Check()
            {
                try
                {
                    return !closed && Volatile.Read(ref owner.disposed) == 0 && !owner.closed.WaitOne(0) && scope.IsCurrent &&
                        owner.frozenSource != null && owner.frozenSource.CanRead && !owner.frozenSource.CanWrite &&
                        owner.workerExit != null && (!owner.workerExit.WaitOne(0) || ExitSuccessful());
                }
                catch { return false; }
            }
            internal void Watch()
            {
                lock (sync)
                {
                    cancellation = ThreadPool.RegisterWaitForSingleObject(owner.closed, (s,t) => Close(), null, Timeout.Infinite, true);
                    exit = ThreadPool.RegisterWaitForSingleObject(owner.workerExit, (s,t) => { lock (sync) { if (!ExitSuccessful()) Close(); } }, null, Timeout.Infinite, true);
                }
            }
            public bool OutputsDrainedSuccessfully
            { get { lock (owner.frontierInputSync) lock (sync) { return Original && Check() && capture.Drained; } } }
            public long StdoutBytes { get { return OutputsDrainedSuccessfully ? capture.RecoveryOutputs[0].Length : -1; } }
            public long StderrBytes { get { return OutputsDrainedSuccessfully ? capture.RecoveryOutputs[1].Length : -1; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }

            public FrontierPrimaryOutputJoin TryJoinDiagnostic(FrontierDiagnosticTransport transport)
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original || owner.frontierDiagnosticReceiver == null || !owner.frontierDiagnosticReceiver.OwnsTransport(transport)) return null;
                    bool success = false;
                    try
                    {
                        if (joinAttempted) return null; joinAttempted = true;
                        if (!Check() || !ExitSuccessful() || !capture.Drained || !transport.IsCurrent) return null;
                        joined = new FrontierPrimaryOutputJoin(this, transport);
                        success = Check() && capture.Drained && transport.IsCurrent;
                        return success ? joined : null;
                    }
                    catch { return null; }
                    finally { if (!success) Close(); }
                }
            }
            internal bool JoinCurrent(FrontierPrimaryOutputJoin value)
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original || !Object.ReferenceEquals(joined, value)) return false;
                    if (Check() && ExitSuccessful() && capture.Drained && value.Transport.IsCurrent) return true;
                    Close(); return false;
                }
            }
            internal void DisposeJoin(FrontierPrimaryOutputJoin value)
            { lock (owner.frontierInputSync) lock (sync) { if (Original && Object.ReferenceEquals(joined, value)) Close(); } }
            internal void Close()
            {
                lock (sync)
                {
                    if (closed) return; closed = true;
                    if (terminalCustody != null) terminalCustody.Close();
                    if (cancellation != null) cancellation.Unregister(null); if (exit != null) exit.Unregister(null);
                    capture.Revoke(); scope.Dispose();
                }
            }
            public void Dispose() { lock (owner.frontierInputSync) lock (sync) { if (Original) Close(); } }
        }

        // Exact native pipe EOF/capture and diagnostic-transport association.
        // This is not a terminal-report receipt or publication approval.
        public sealed class FrontierPrimaryOutputJoin : IDisposable
        {
            private readonly FrontierPrimaryOutput owner;
            internal readonly FrontierDiagnosticTransport Transport;
            internal FrontierPrimaryOutputJoin(FrontierPrimaryOutput owner, FrontierDiagnosticTransport transport) { this.owner = owner; Transport = transport; }
            public bool IsCurrent { get { return owner.JoinCurrent(this); } }
            public FrontierPrimaryOutput.TerminalReceiptCustody TryBeginTerminalReceipt() { return owner.BeginTerminal(this); }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeJoin(this); }
        }
    }
}
