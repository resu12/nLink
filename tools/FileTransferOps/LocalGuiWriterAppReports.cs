using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            internal FrontierAppReports StartAppWithReports(Process process, FrontierAppRequest request, Func<int, string[]> createReports)
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(frontierLaunchOwner, this) || !request.IsOriginalFor(this) ||
                        request.outputLaunchAttempted || request.launch != null)
                        throw new InvalidOperationException("frontier_app_launch_repeated_or_foreign");
                    if (process == null || process.StartInfo.UseShellExecute || !process.StartInfo.RedirectStandardOutput ||
                        !process.StartInfo.RedirectStandardError || createReports == null)
                        throw new ArgumentException("frontier_app_reports_required");
                    try { int existing = process.Id; throw new ArgumentException("frontier_app_already_started"); }
                    catch (InvalidOperationException) { }
                    request.outputLaunchAttempted = true;
                    FrontierOutputCapture capture = null;
                    try
                    {
                        if (IsCurrent && !sealedLaunches && apps.Count < MaximumAppLaunches)
                            capture = new FrontierOutputCapture(Path.Combine(artifactRoot, "frontier-process-output-" + id.ToString("N") + "-" + apps.Count));
                    }
                    catch { if (capture != null) capture.Revoke(); capture = null; }
                    bool prepared = false;
                    try { prepared = request.native.PrepareStart(process.StartInfo, capture != null && IsCurrent); }
                    catch { }
                    if (!prepared) { if (capture != null) capture.Revoke(); capture = null; }
                    try { if (!process.Start()) throw new InvalidOperationException("frontier_app_start_failed"); }
                    catch { if (capture != null) capture.Revoke(); Dispose(); throw; }

                    string[] paths;
                    try
                    {
                        // Ordinary filenames depend on the actual PID. This
                        // synchronous callback only creates ordinary reports;
                        // neither returned path can establish native custody.
                        paths = createReports(process.Id);
                        if (paths == null || paths.Length != 2 || String.IsNullOrEmpty(paths[0]) || String.IsNullOrEmpty(paths[1]))
                            throw new ArgumentException("frontier_app_report_paths_required");
                    }
                    catch
                    {
                        if (capture != null) capture.Revoke(); Dispose();
                        // Preserve the original setup failure, but never leave
                        // an already-started app blocked on an unread pipe.
                        DrainFailedSetup(process.StandardOutput.BaseStream);
                        DrainFailedSetup(process.StandardError.BaseStream);
                        throw;
                    }
                    var reports = new FrontierAppReports(process, paths, capture);
                    try
                    {
                        if (capture != null && IsCurrent) reports.Launch = BindAppCore(process, request, capture);
                        if (reports.Launch != null)
                        {
                            reports.NativeLaunchReady = request.native.PublishStartReady();
                            if (!reports.NativeLaunchReady) reports.Launch = null;
                        }
                    }
                    catch { } // Optional binding must not lose ordinary readers.
                    finally
                    {
                        if (reports.Launch == null) { if (capture != null) capture.Revoke(); Dispose(); }
                    }
                    return reports;
                }
            }
            private static void DrainFailedSetup(Stream input)
            { Task.Run(async () => { try { await input.CopyToAsync(Stream.Null, 32768).ConfigureAwait(false); } catch { } }); }
        }

        // Ordinary live report tasks survive optional ownership retirement.
        // Completion means pipe EOF and successful ordinary writes, not exit
        // success, app attachment, writer closure or permission to publish.
        public sealed class FrontierAppReports
        {
            public Task Stdout { get; private set; }
            public Task Stderr { get; private set; }
            public FrontierAppLaunch Launch { get; internal set; }
            public bool NativeLaunchReady { get; internal set; }
            internal FrontierAppReports(Process process, string[] paths, FrontierOutputCapture capture)
            {
                var tasks = FrontierOutputCapture.StartLineReports(process, paths, capture);
                Stdout = tasks[0]; Stderr = tasks[1];
            }
        }

        internal sealed partial class FrontierOutputCapture
        {
            internal static Task[] StartLineReports(Process process, string[] paths, FrontierOutputCapture capture)
            {
                if (capture != null) capture.started = true;
                var output = ReadLineReport(process.StandardOutput, paths[0], capture, 0);
                var error = ReadLineReport(process.StandardError, paths[1], capture, 1);
                if (capture != null) { capture.stdout = output; capture.stderr = error; }
                return new[] { output, error };
            }
            private static Task ReadLineReport(StreamReader original, string path, FrontierOutputCapture capture, int role)
            {
                return Task.Run(async () =>
                {
                    FileStream target = capture == null ? null : (role == 0 ? capture.stdoutTarget : capture.stderrTarget);
                    var tee = capture == null ? null : new ReportTee(capture, original.BaseStream, target);
                    Exception reportFailure = null;
                    try
                    {
                        // Match Process's line events: the original encoding,
                        // CR/LF framing, nonempty EOF tail and no BOM stripping.
                        // Keep only the current line, never the whole report.
                        using (var reader = new StreamReader(tee == null ? original.BaseStream : tee, original.CurrentEncoding, false, 4096, true))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                if (reportFailure != null) continue;
                                try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); }
                                catch (Exception error) { reportFailure = error; }
                            }
                        }
                        if (capture != null)
                        {
                            try { if (!tee.Captured || reportFailure != null || !capture.RecoveryOutputs[role].Seal(target)) Interlocked.Exchange(ref capture.failed, 1); }
                            catch { Interlocked.Exchange(ref capture.failed, 1); }
                        }
                        if (reportFailure != null) throw reportFailure;
                    }
                    catch { if (capture != null) Interlocked.Exchange(ref capture.failed, 1); throw; }
                    finally
                    {
                        if (target != null) { try { target.Dispose(); } catch { Interlocked.Exchange(ref capture.failed, 1); } }
                    }
                });
            }
        }
    }
}
