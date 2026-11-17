using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        private FrontierStartSignal frontierStart;
        private bool frontierStartAttempted;

        public sealed partial class FrontierPrimaryPreparation
        {
            public FrontierPrimaryReports StartWorkerWithReports(Process process, string directory, string routeMode, string startedUtc)
            {
                lock (request.frontierInputSync)
                {
                    if (!Owned || !IsCurrent || !AnalysisInputReady || request.frontierStartAttempted || request.frontierOutputAttempted || request.workerExit != null)
                        throw new InvalidOperationException("frontier_start_unavailable_or_repeated");
                    if (process == null || process.StartInfo.UseShellExecute || !process.StartInfo.RedirectStandardOutput || !process.StartInfo.RedirectStandardError)
                        throw new ArgumentException("frontier_primary_redirects_required");
                    bool started = false; try { int id = process.Id; started = true; } catch (InvalidOperationException) { }
                    if (started) throw new InvalidOperationException("frontier_primary_already_started");
                    request.frontierStartAttempted = true;
                    request.frontierStart = FrontierStartSignal.Create(request.Name);
                    if (request.frontierStart == null) throw new InvalidOperationException("frontier_start_signal_unavailable");
                    process.StartInfo.EnvironmentVariables["NLINK_RETAINED_SOURCE_REQUEST"] = request.Name;
                    process.StartInfo.EnvironmentVariables["NLINK_RETAINED_SOURCE_REQUEST_ID"] = request.RequestId.ToString("D");
                    process.StartInfo.EnvironmentVariables["NLINK_RETAINED_FRONTIER_START_V1"] = "1";
                    FrontierPrimaryReports reports;
                    try { reports = request.StartFrontierWorkerWithReports(process, directory, routeMode, startedUtc); }
                    catch { request.frontierStart.Fail(); throw; }
                    try
                    {
                        if (reports.SourceBound && inputs.TryPrepareWorkerHandoff())
                        {
                            // Diagnostic capture/receiver failure does not block the source.
                            if (reports.DiagnosticOutput != null) reports.DiagnosticReceiver = inputs.TryPrepareDiagnosticReceiver();
                            if (inputs.IsCurrent)
                            {
                                request.frontierStart.Ready.Set(); reports.NativeHandoffReady = true;
                            }
                        }
                    }
                    catch { } // The started primary keeps its original report tasks.
                    finally { if (!reports.NativeHandoffReady) request.frontierStart.Fail(); }
                    return reports;
                }
            }
        }

        public sealed partial class FrontierPrimaryReports
        {
            public bool NativeHandoffReady { get; internal set; }
            public FrontierDiagnosticReceiver DiagnosticReceiver { get; internal set; }
        }

        public static Worker TryAttachFrontierReady(string name, Guid requestId, string path, string run, string scenario)
        { return TryAttachCore(name, requestId, path, run, scenario, true, true); }

        // This wait is outside the request mutex. Signalling only permits the
        // existing one-shot attachment to run; it replaces none of its checks.
        private static bool WaitForFrontierStart(string name, Guid requestId)
        {
            try
            {
                using (var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read))
                using (var ready = EventWaitHandle.OpenExisting(name + ".frontier-start-v1.ready"))
                using (var failed = EventWaitHandle.OpenExisting(name + ".frontier-start-v1.failed"))
                using (var cancelled = EventWaitHandle.OpenExisting(name + ".closed"))
                {
                    var id = new byte[16]; int parentId = view.ReadInt32(4);
                    if (view.ReadInt32(0) != WriterMagic || parentId <= 0 || parentId == GetCurrentProcessId() ||
                        view.ReadArray(24, id, 0, 16) != 16 || new Guid(id) != requestId) return false;
                    var handle = OpenProcess(0x00101000U, false, parentId);
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

        private sealed class FrontierStartSignal : IDisposable
        {
            internal readonly EventWaitHandle Ready, Failed;
            private FrontierStartSignal(EventWaitHandle ready, EventWaitHandle failed) { Ready = ready; Failed = failed; }
            internal static FrontierStartSignal Create(string name)
            {
                EventWaitHandle ready = null, failed = null; bool retained = false;
                try
                {
                    bool created;
                    ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".frontier-start-v1.ready", out created);
                    if (!created) return null;
                    failed = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".frontier-start-v1.failed", out created);
                    if (!created) return null;
                    retained = true; return new FrontierStartSignal(ready, failed);
                }
                catch { return null; }
                finally { if (!retained) { if (failed != null) failed.Dispose(); if (ready != null) ready.Dispose(); } }
            }
            internal void Fail() { try { Failed.Set(); } catch (ObjectDisposedException) { } }
            public void Dispose() { Fail(); Ready.Dispose(); Failed.Dispose(); }
        }
    }
}
