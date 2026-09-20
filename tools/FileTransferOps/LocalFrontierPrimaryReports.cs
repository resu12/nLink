using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        // Starting the primary is not optional. Only native diagnostic custody
        // may be omitted; a started worker always returns its ordinary reports.
        public FrontierPrimaryReports StartFrontierWorkerWithReports(Process process, string directory, string routeMode, string startedUtc)
        {
            lock (frontierInputSync)
            {
                if (!Object.ReferenceEquals(frontierOriginal, this))
                    throw new InvalidOperationException("frontier_primary_launch_repeated_or_foreign");
                if (frontierOutputAttempted)
                {
                    if (frontierOutput != null) frontierOutput.Close();
                    throw new InvalidOperationException("frontier_primary_launch_repeated_or_foreign");
                }
                if (process == null || process.StartInfo.UseShellExecute || !process.StartInfo.RedirectStandardOutput || !process.StartInfo.RedirectStandardError)
                    throw new ArgumentException("frontier_primary_redirects_required");
                bool started = false; try { int id = process.Id; started = true; } catch (InvalidOperationException) { }
                if (started) throw new InvalidOperationException("frontier_primary_already_started");
                frontierOutputAttempted = true;
                LocalGuiWriterRequest.FrontierOutputCapture capture = null;
                try
                {
                    if (frontierInputs != null && frontierInputs.IsCurrent && TextValid(directory, 4096) && Directory.Exists(directory))
                        capture = new LocalGuiWriterRequest.FrontierOutputCapture(Path.Combine(Path.GetFullPath(directory), "frontier-primary-output-" + RequestId.ToString("N")));
                }
                catch { if (capture != null) capture.Revoke(); capture = null; }
                try { if (!process.Start()) throw new InvalidOperationException("frontier_primary_start_failed"); }
                catch { if (capture != null) capture.Revoke(); throw; }

                // No diagnostic checks can prevent the two ordinary readers
                // from starting. They remain independent of request disposal.
                var reports = new FrontierPrimaryReports(process, capture);
                try
                {
                    reports.SourceBound = BindWorker(process);
                    if (reports.SourceBound && capture != null && frontierInputs.IsCurrent)
                    {
                        frontierOutput = new FrontierPrimaryOutput(this, capture, new LocalFrontierPrimaryScope(map, frontierBody),
                            process, Path.GetFullPath(directory), routeMode, startedUtc);
                        frontierOutput.Watch();
                        if (frontierOutput.Check()) reports.DiagnosticOutput = frontierOutput;
                    }
                }
                catch { } // Never lose ordinary output because optional binding failed.
                finally
                {
                    if (reports.DiagnosticOutput == null)
                    { if (frontierOutput != null) frontierOutput.Close(); if (capture != null) capture.Revoke(); }
                }
                return reports;
            }
        }
        public sealed partial class FrontierPrimaryReports
        {
            public Task<string> Stdout { get; private set; }
            public Task<string> Stderr { get; private set; }
            public bool SourceBound { get; internal set; }
            public FrontierPrimaryOutput DiagnosticOutput { get; internal set; }
            internal FrontierPrimaryReports(Process process, LocalGuiWriterRequest.FrontierOutputCapture capture)
            {
                if (capture == null)
                { Stdout = process.StandardOutput.ReadToEndAsync(); Stderr = process.StandardError.ReadToEndAsync(); }
                else
                { var tasks = capture.StartReports(process); Stdout = tasks[0]; Stderr = tasks[1]; }
            }
        }
    }
    public sealed partial class LocalGuiWriterRequest
    {
        internal sealed partial class FrontierOutputCapture
        {
            internal Task<string>[] StartReports(Process process)
            {
                started = true;
                var output = ReadReport(process.StandardOutput, stdoutTarget, RecoveryOutputs[0]);
                var error = ReadReport(process.StandardError, stderrTarget, RecoveryOutputs[1]);
                stdout = output; stderr = error; return new[] { output, error };
            }
            private Task<string> ReadReport(StreamReader original, FileStream target, FrontierRecoveryOutput retained)
            {
                return Task.Run(async () =>
                {
                    try
                    {
                        using (var stream = new ReportTee(this, original.BaseStream, target))
                        using (var reader = new StreamReader(stream, original.CurrentEncoding, true, 4096))
                        {
                            string text = await reader.ReadToEndAsync().ConfigureAwait(false);
                            // A failed optional write/seal does not fault the
                            // original report task or stop its pipe consumption.
                            try { if (!stream.Captured || !retained.Seal(target)) Interlocked.Exchange(ref failed, 1); }
                            catch { Interlocked.Exchange(ref failed, 1); }
                            return text;
                        }
                    }
                    catch { Interlocked.Exchange(ref failed, 1); throw; }
                    finally { try { target.Dispose(); } catch { Interlocked.Exchange(ref failed, 1); } }
                });
            }
            private sealed class ReportTee : Stream
            {
                private readonly FrontierOutputCapture owner;
                private readonly Stream input;
                private readonly FileStream target;
                internal bool Captured = true;
                internal ReportTee(FrontierOutputCapture owner, Stream input, FileStream target) { this.owner = owner; this.input = input; this.target = target; }
                public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
                {
                    int read = await input.ReadAsync(buffer, offset, count, cancellation).ConfigureAwait(false);
                    if (Captured)
                    {
                        try
                        {
                            if (read == 0) await target.FlushAsync().ConfigureAwait(false);
                            else await target.WriteAsync(buffer, offset, read).ConfigureAwait(false);
                        }
                        catch { Captured = false; Interlocked.Exchange(ref owner.failed, 1); }
                    }
                    return read;
                }
                // The process owns its pipes; disposing a tee never closes them.
                public override bool CanRead { get { return true; } }
                public override bool CanSeek { get { return false; } }
                public override bool CanWrite { get { return false; } }
                public override int Read(byte[] buffer, int offset, int count) { return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult(); }
                public override long Length { get { throw new NotSupportedException(); } }
                public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
                public override void SetLength(long value) { throw new NotSupportedException(); }
                public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            }
        }
    }
}
