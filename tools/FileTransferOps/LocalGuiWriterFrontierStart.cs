using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private bool frontierGuiStartAttempted;
        private FrontierGuiStartSignal frontierGuiStart;

        // One actual GUI launch. Optional registration/readiness failure cannot
        // suppress ordinary startup or discard its first stdout/stderr readers.
        public FrontierGuiReports StartFrontierGuiWorkerWithReports(Process process)
        {
            lock (frontierParentSync)
            {
                if (!Object.ReferenceEquals(original, this) || relayMode || Volatile.Read(ref sealedState) != 0 ||
                    frontierGuiStartAttempted || frontierParentAttempted || workerExit != null)
                    throw new InvalidOperationException("frontier_gui_launch_unavailable_or_repeated");
                if (process == null || process.StartInfo.UseShellExecute || !process.StartInfo.RedirectStandardOutput || !process.StartInfo.RedirectStandardError)
                    throw new ArgumentException("frontier_gui_redirects_required");
                bool started = false; try { int id = process.Id; started = true; } catch (InvalidOperationException) { }
                if (started) throw new InvalidOperationException("frontier_gui_already_started");
                frontierGuiStartAttempted = true;
                frontierGuiStart = FrontierGuiStartSignal.Create(Name);
                var environment = process.StartInfo.EnvironmentVariables;
                environment["NLINK_GUI_WRITER_REQUEST"] = Name;
                environment["NLINK_GUI_WRITER_REQUEST_ID"] = RequestId.ToString("D");
                environment.Remove("NLINK_GUI_FRONTIER_START_V1");
                if (frontierGuiStart != null) environment["NLINK_GUI_FRONTIER_START_V1"] = "1";
                try { if (!process.Start()) throw new InvalidOperationException("frontier_gui_start_failed"); }
                catch { if (frontierGuiStart != null) frontierGuiStart.Fail(); throw; }
                var reports = new FrontierGuiReports(process);
                try
                {
                    reports.WorkerBound = BindWorker(process);
                    if (reports.WorkerBound && frontierGuiStart != null)
                    {
                        reports.ParentRegistration = TryRegisterFrontierParent();
                        if (reports.ParentRegistration != null && reports.ParentRegistration.IsCurrent)
                        { frontierGuiStart.Ready.Set(); reports.NativeLaunchReady = true; }
                    }
                }
                catch { } // A started GUI keeps its ordinary reports and outcome.
                finally { if (frontierGuiStart != null && !reports.NativeLaunchReady) frontierGuiStart.Fail(); }
                return reports;
            }
        }

        public sealed class FrontierGuiReports
        {
            public Task<string> Stdout { get; private set; }
            public Task<string> Stderr { get; private set; }
            public bool WorkerBound { get; internal set; }
            public bool NativeLaunchReady { get; internal set; }
            public FrontierParentRegistration ParentRegistration { get; internal set; }
            internal FrontierGuiReports(Process process)
            { Stdout = process.StandardOutput.ReadToEndAsync(); Stderr = process.StandardError.ReadToEndAsync(); }
        }

        public static FrontierLaunchOwner TryAttachFrontierLauncherReady(string name, Guid id, string run, string scenario, string artifactRoot)
        { return TryAttachFrontierLauncherCore(name, id, run, scenario, artifactRoot, true); }

        // Idle wait outside the request mutex and all active-work clocks. Ready
        // only permits the unchanged exact GUI/parent/scope attachment checks.
        private static bool WaitForFrontierGuiStart(string name, Guid id)
        {
            try
            {
                if (name == null || !Regex.IsMatch(name, @"\ALocal\\nlink-gui-writer-[0-9a-f]{32}\z") || id == Guid.Empty) return false;
                using (var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read))
                using (var ready = EventWaitHandle.OpenExisting(name + ".frontier-gui-start-v1.ready"))
                using (var failed = EventWaitHandle.OpenExisting(name + ".frontier-gui-start-v1.failed"))
                using (var cancelled = EventWaitHandle.OpenExisting(name + ".closed"))
                {
                    int parentId = view.ReadInt32(4);
                    if (view.ReadInt32(0) != Magic || ReadId(view) != id || parentId <= 0 || parentId == GetCurrentProcessId()) return false;
                    var handle = OpenProcess(0x00101000, false, parentId);
                    using (var parent = new ProcessWait(handle))
                    {
                        long creation, exit, kernel, user;
                        if (handle.IsInvalid || !GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation != view.ReadInt64(8)) return false;
                        return WaitHandle.WaitAny(new WaitHandle[] { cancelled, parent, failed, ready }, LocalPublicationInputLimits.ProducerMilliseconds) == 3 &&
                            !cancelled.WaitOne(0) && !parent.WaitOne(0) && !failed.WaitOne(0);
                    }
                }
            }
            catch { return false; }
        }

        private sealed class FrontierGuiStartSignal : IDisposable
        {
            internal readonly EventWaitHandle Ready, Failed;
            private FrontierGuiStartSignal(EventWaitHandle ready, EventWaitHandle failed) { Ready = ready; Failed = failed; }
            internal static FrontierGuiStartSignal Create(string name)
            {
                EventWaitHandle ready = null, failed = null; bool retained = false;
                try
                {
                    bool created;
                    ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".frontier-gui-start-v1.ready", out created);
                    if (!created) return null;
                    failed = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".frontier-gui-start-v1.failed", out created);
                    if (!created) return null;
                    retained = true; return new FrontierGuiStartSignal(ready, failed);
                }
                catch { return null; }
                finally { if (!retained) { if (failed != null) failed.Dispose(); if (ready != null) ready.Dispose(); } }
            }
            internal void Fail() { try { Failed.Set(); } catch (ObjectDisposedException) { } }
            public void Dispose() { Fail(); Ready.Dispose(); Failed.Dispose(); }
        }
    }
}
