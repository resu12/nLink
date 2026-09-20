#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalFrontierAppRequest
    {
        private const string StartSelector = "NLINK_APP_FRONTIER_START_V1";
        private const string StartName = "NLINK_APP_FRONTIER_REQUEST";
        private const string StartId = "NLINK_APP_FRONTIER_REQUEST_ID";
        // Same producer deadline as the GUI/primary rendezvous, with no work clock.
        private const int StartWaitMilliseconds = 20000;
        private readonly object startSync = new object();
        private AppStartSignal startSignal;
        private bool startPrepared;

        internal bool PrepareStart(ProcessStartInfo start, bool eligible)
        {
            lock (startSync)
            {
                var environment = start.EnvironmentVariables;
                environment.Remove(StartSelector); environment.Remove(StartName); environment.Remove(StartId);
                if (startPrepared || !eligible || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0 ||
                    Volatile.Read(ref taken) != 0 || Volatile.Read(ref bindAttempted) != 0) return false;
                startPrepared = true;
                startSignal = AppStartSignal.Create(Name);
                if (startSignal == null) return false;
                environment[StartName] = Name; environment[StartId] = RequestId.ToString("D");
                environment[StartSelector] = "1";
                return true;
            }
        }

        internal bool PublishStartReady()
        {
            lock (startSync)
            {
                try
                {
                    if (startSignal == null || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0 ||
                        Volatile.Read(ref taken) != 0 || cancelled.WaitOne(0) || appExit == null || appExit.WaitOne(0) ||
                        view.ReadInt32(8) != 1 || !MatchesHeader(view, RequestId, parentId, parentCreation, appId, appCreation, registration)) return false;
                    startSignal.Ready.Set(); return true;
                }
                catch { return false; }
            }
        }

        // Selectors and endpoint strings are locators only. Callers must retain
        // the returned original lease through the app-owned writer lifecycle.
        public static AppLease TryAttachReadyFromEnvironment(string run, string scenario)
        { return TryAttachReadyFromEnvironment(run, scenario, null); }

        internal static AppLease TryAttachReadyFromEnvironment(string run, string scenario, WaitHandle stopping)
        {
            if (!String.Equals(Environment.GetEnvironmentVariable(StartSelector), "1", StringComparison.Ordinal)) return null;
            Guid id;
            if (!Guid.TryParseExact(Environment.GetEnvironmentVariable(StartId), "D", out id)) return null;
            return TryAttachReady(Environment.GetEnvironmentVariable(StartName), id, run, scenario, stopping);
        }

        public static AppLease TryAttachReady(string name, Guid id, string run, string scenario)
        { return TryAttachReady(name, id, run, scenario, null); }

        private static AppLease TryAttachReady(string name, Guid id, string run, string scenario, WaitHandle stopping)
        {
            try
            {
                if (name == null || !Regex.IsMatch(name, @"\ALocal\\nlink-frontier-app-[0-9a-f]{32}\z") || id == Guid.Empty) return null;
                byte[] body = Registration(run, scenario);
                if (body == null) return null;
                using (var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, HeaderBytes + MaximumRegistrationBytes, MemoryMappedFileAccess.Read))
                using (var ready = EventWaitHandle.OpenExisting(name + ".app-start-v1.ready"))
                using (var failed = EventWaitHandle.OpenExisting(name + ".app-start-v1.failed"))
                using (var cancelled = EventWaitHandle.OpenExisting(name + ".cancelled"))
                {
                    int parentId = view.ReadInt32(12);
                    long parentCreation = view.ReadInt64(16);
                    using (var current = Process.GetCurrentProcess())
                        if (parentId <= 0 || parentId == current.Id || parentCreation <= 0 ||
                            !MatchesRegistration(view, id, parentId, parentCreation, body)) return null;
                    var handle = OpenProcess(0x00101000, false, parentId);
                    using (var parent = new ProcessWait(handle))
                    {
                        long creation, exit, kernel, user;
                        if (handle.IsInvalid || !GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation != parentCreation) return null;
                        // Never hold the request mutex or charge active work
                        // while waiting. Ready cannot substitute for binding.
                        var waits = stopping == null ? new WaitHandle[] { cancelled, parent, failed, ready } :
                            new WaitHandle[] { cancelled, parent, failed, ready, stopping };
                        if (WaitHandle.WaitAny(waits, StartWaitMilliseconds) != 3 ||
                            cancelled.WaitOne(0) || parent.WaitOne(0) || failed.WaitOne(0) ||
                            (stopping != null && stopping.WaitOne(0))) return null;
                        return TryAttachCore(name, id, run, scenario, failed);
                    }
                }
            }
            catch { return null; }
        }

        private sealed class AppStartSignal : IDisposable
        {
            internal readonly EventWaitHandle Ready, Failed;
            private AppStartSignal(EventWaitHandle ready, EventWaitHandle failed) { Ready = ready; Failed = failed; }
            internal static AppStartSignal Create(string name)
            {
                EventWaitHandle ready = null, failed = null; bool retained = false;
                try
                {
                    bool created;
                    ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".app-start-v1.ready", out created);
                    if (!created) return null;
                    failed = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".app-start-v1.failed", out created);
                    if (!created) return null;
                    retained = true; return new AppStartSignal(ready, failed);
                }
                catch { return null; }
                finally { if (!retained) { if (failed != null) failed.Dispose(); if (ready != null) ready.Dispose(); } }
            }
            internal void Fail() { try { Failed.Set(); } catch (ObjectDisposedException) { } }
            public void Dispose() { Fail(); Ready.Dispose(); Failed.Dispose(); }
        }
    }
}
