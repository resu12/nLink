using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private bool frontierRelayStartAttempted;
        private EventWaitHandle frontierRelayReady, frontierRelayFailed;
        public FrontierRelayReports StartFrontierRelayWorkerWithReports(Process process)
        {
            lock (frontierRelaySync)
            {
                if (!Object.ReferenceEquals(original, this) || !relayMode || Volatile.Read(ref sealedState) != 0 ||
                    frontierRelayStartAttempted || frontierRelayAttempted || workerExit != null)
                    throw new InvalidOperationException("frontier_relay_launch_unavailable_or_repeated");
                if (process == null || process.StartInfo.UseShellExecute || !process.StartInfo.RedirectStandardOutput || !process.StartInfo.RedirectStandardError)
                    throw new ArgumentException("frontier_relay_redirects_required");
                bool started = false; try { int id = process.Id; started = true; } catch (InvalidOperationException) { }
                if (started) throw new InvalidOperationException("frontier_relay_already_started");
                frontierRelayStartAttempted = true;
                var environment = process.StartInfo.EnvironmentVariables;
                environment["NLINK_GUI_WRITER_RELAY"] = Name;
                environment["NLINK_GUI_WRITER_RELAY_ID"] = RequestId.ToString("D");
                environment.Remove("NLINK_GUI_FRONTIER_RELAY_START_V1");
                try
                {
                    bool created;
                    frontierRelayReady = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".frontier-relay-start-v1.ready", out created);
                    if (!created) throw new InvalidOperationException();
                    frontierRelayFailed = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".frontier-relay-start-v1.failed", out created);
                    if (!created) throw new InvalidOperationException();
                    environment["NLINK_GUI_FRONTIER_RELAY_START_V1"] = "1";
                }
                catch
                {
                    // A collision closes local handles without signalling the foreign event.
                    if (frontierRelayReady != null) frontierRelayReady.Dispose(); if (frontierRelayFailed != null) frontierRelayFailed.Dispose();
                    frontierRelayReady = null; frontierRelayFailed = null;
                }
                try { if (!process.Start()) throw new InvalidOperationException("frontier_relay_start_failed"); }
                catch { CloseFrontierRelayStart(); throw; }
                var reports = new FrontierRelayReports(process);
                bool ready = false;
                try
                {
                    if (BindWorker(process) && frontierRelayReady != null)
                    {
                        reports.Registration = TryRegisterFrontierRelay();
                        if (reports.Registration != null && reports.Registration.IsCurrent) { frontierRelayReady.Set(); ready = true; }
                    }
                }
                catch { }
                finally
                {
                    if (!ready)
                    { reports.Registration = null; RetireFrontierRelayRegistration(); if (frontierRelayFailed != null) frontierRelayFailed.Set(); }
                }
                RememberPayloadRelayReports(reports);
                return reports;
            }
        }
        private void CloseFrontierRelayStart()
        {
            if (frontierRelayFailed != null) { try { frontierRelayFailed.Set(); } catch { } frontierRelayFailed.Dispose(); frontierRelayFailed = null; }
            if (frontierRelayReady != null) { frontierRelayReady.Dispose(); frontierRelayReady = null; }
        }
        public sealed class FrontierRelayReports
        {
            public Task<string> Stdout { get; private set; }
            public Task<string> Stderr { get; private set; }
            public FrontierRelayRegistration Registration { get; internal set; }
            internal FrontierRelayReports(Process process)
            { Stdout = process.StandardOutput.ReadToEndAsync(); Stderr = process.StandardError.ReadToEndAsync(); }
        }
        public static Relay TryAttachRelayReady(string name, Guid id, string run, string scenario, string artifactRoot)
        {
            // A failed optional registration still permits the original scalar
            // relay's exact checks, once. Readiness does not grant ownership.
            try
            {
                using (var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read))
                using (var ready = EventWaitHandle.OpenExisting(name + ".frontier-relay-start-v1.ready"))
                using (var failed = EventWaitHandle.OpenExisting(name + ".frontier-relay-start-v1.failed"))
                using (var cancelled = EventWaitHandle.OpenExisting(name + ".closed"))
                {
                    int parentId = view.ReadInt32(4);
                    if (view.ReadInt32(0) != RelayMagic || ReadId(view) != id || parentId <= 0 || parentId == GetCurrentProcessId()) return null;
                    var handle = OpenProcess(0x00101000, false, parentId);
                    using (var parent = new ProcessWait(handle))
                    {
                        long creation, exit, kernel, user;
                        if (handle.IsInvalid || !GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation != view.ReadInt64(8)) return null;
                        WaitHandle.WaitAny(new WaitHandle[] { cancelled, parent, failed, ready }, 20000);
                    }
                }
            }
            catch { }
            return TryAttachRelay(name, id, run, scenario, artifactRoot);
        }
        public sealed partial class Relay
        {
            public bool OwnsFrontierHandoff(FrontierRelayHandoff handoff)
            { return Object.ReferenceEquals(original, this) && Object.ReferenceEquals(frontierRelayHandoff, handoff) && handoff != null && handoff.IsCurrent; }
        }
    }
}
