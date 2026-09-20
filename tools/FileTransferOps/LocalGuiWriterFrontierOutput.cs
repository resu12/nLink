using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            // The caller supplies an unstarted Process, never readers, tasks,
            // EOF booleans or output paths. This lane owns its first pipe reads.
            internal FrontierAppLaunch StartAppWithOutput(Process process, FrontierAppRequest request)
            {
                lock (sync)
                {
                    if (!appRequests.Contains(request) || !IsCurrent) return null;
                    FrontierOutputCapture output = null;
                    bool retained = false;
                    try
                    {
                        if (request.outputLaunchAttempted || request.launch != null || sealedLaunches || apps.Count == MaximumAppLaunches || process == null) return null;
                        try { int existingProcess = process.Id; return null; }
                        catch (InvalidOperationException) { }
                        var start = process.StartInfo;
                        if (start.UseShellExecute || !start.RedirectStandardOutput || !start.RedirectStandardError) return null;
                        request.outputLaunchAttempted = true;
                        string prefix = Path.Combine(artifactRoot, "frontier-process-output-" + id.ToString("N") + "-" + apps.Count);
                        output = new FrontierOutputCapture(prefix);
                        if (!process.Start()) return null;
                        output.Start(process);
                        var binding = BindAppCore(process, request, output);
                        if (binding == null) return null;
                        retained = true; return binding;
                    }
                    catch { return null; }
                    finally
                    {
                        if (!retained)
                        {
                            if (output != null) output.Revoke();
                            Dispose();
                        }
                    }
                }
            }

            public bool AllAppOutputsDrainedSuccessfully
            {
                get
                {
                    lock (sync)
                    {
                        if (!IsCurrent || !sealedLaunches || apps.Count != MaximumAppLaunches) return false;
                        foreach (var app in apps) if (!app.OutputDrainedSuccessfully) return false;
                        return IsCurrent;
                    }
                }
            }
        }

        // A bounded pair of raw-stream pumps, not Process event jobs or quiet
        // output-file polling. Capture continues after optional-lane revocation:
        // closing an app's live read pipes could change its runtime behavior.
        internal sealed partial class FrontierOutputCapture
        {
            private readonly FileStream stdoutTarget, stderrTarget;
            internal readonly FrontierRecoveryOutput[] RecoveryOutputs = { new FrontierRecoveryOutput(), new FrontierRecoveryOutput() };
            private Task stdout, stderr;
            private int revoked, failed;
            private bool started;

            internal FrontierOutputCapture(string prefix)
            {
                try
                {
                    stdoutTarget = new FileStream(prefix + "-stdout.log", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    stderrTarget = new FileStream(prefix + "-stderr.log", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                }
                catch
                {
                    if (stdoutTarget != null) stdoutTarget.Dispose();
                    throw;
                }
            }

            internal void Start(Process process)
            {
                var output = process.StandardOutput.BaseStream;
                var error = process.StandardError.BaseStream;
                started = true;
                // Even synchronously completed reads/writes must not keep one
                // stream's pump in the launch lock and delay the other stream.
                stdout = Task.Run(() => Pump(output, stdoutTarget, RecoveryOutputs[0]));
                stderr = Task.Run(() => Pump(error, stderrTarget, RecoveryOutputs[1]));
            }

            private async Task Pump(Stream input, FileStream target, FrontierRecoveryOutput retained)
            {
                bool writable = true;
                try
                {
                    var buffer = new byte[32768];
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                    {
                        if (!writable) continue;
                        try { await target.WriteAsync(buffer, 0, count).ConfigureAwait(false); }
                        catch { Interlocked.Exchange(ref failed, 1); writable = false; }
                    }
                    if (writable)
                    {
                        await target.FlushAsync().ConfigureAwait(false);
                        if (!retained.Seal(target)) Interlocked.Exchange(ref failed, 1);
                    }
                }
                catch { Interlocked.Exchange(ref failed, 1); }
                finally
                {
                    try { target.Dispose(); }
                    catch { Interlocked.Exchange(ref failed, 1); }
                }
            }

            internal bool Drained
            {
                get
                {
                    return stdout != null && stderr != null && stdout.Status == TaskStatus.RanToCompletion &&
                        stderr.Status == TaskStatus.RanToCompletion && Interlocked.CompareExchange(ref failed, 0, 0) == 0 &&
                        Interlocked.CompareExchange(ref revoked, 0, 0) == 0 &&
                        RecoveryOutputs[0].IsCurrent && RecoveryOutputs[1].IsCurrent;
                }
            }

            internal void Revoke()
            {
                Interlocked.Exchange(ref revoked, 1);
                foreach (var output in RecoveryOutputs) output.Revoke();
                if (!started) { stdoutTarget.Dispose(); stderrTarget.Dispose(); }
            }
        }
    }
}
