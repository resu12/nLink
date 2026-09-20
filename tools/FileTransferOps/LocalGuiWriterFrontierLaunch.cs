using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private static int frontierLaunchAttempted;
        private static FrontierLaunchOwner frontierLaunchOwner;
        [DllImport("kernel32.dll", EntryPoint = "GetProcessId")]
        private static extern int GetFrontierProcessId(SafeWaitHandle process);

        // Separate early ownership. Do not consume the existing finalizer's
        // attach slot or write its header/completion format.
        public static FrontierLaunchOwner TryAttachFrontierLauncher(string name, Guid id, string run, string scenario, string artifactRoot)
        { return TryAttachFrontierLauncherCore(name, id, run, scenario, artifactRoot, false); }

        private static FrontierLaunchOwner TryAttachFrontierLauncherCore(string name, Guid id, string run, string scenario, string artifactRoot, bool waitForReady)
        {
            if (Interlocked.Exchange(ref frontierLaunchAttempted, 1) != 0) return null;
            if (waitForReady && !WaitForFrontierGuiStart(name, id)) return null;
            var owner = new FrontierLaunchOwner();
            bool held = false;
            try
            {
                if (name == null || !Regex.IsMatch(name, @"\ALocal\\nlink-gui-writer-[0-9a-f]{32}\z") || id == Guid.Empty ||
                    !TextValid(run, 1024) || !TextValid(scenario, 1024) || !CanonicalPath(artifactRoot)) return null;
                using (var bytes = new MemoryStream())
                using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                { writer.Write(run); writer.Write(scenario); writer.Write(artifactRoot); writer.Flush(); owner.registration = bytes.ToArray(); }
                if (owner.registration.Length > RequestBytes) return null;
                owner.map = MemoryMappedFile.OpenExisting(name); owner.view = owner.map.CreateViewAccessor();
                owner.nativeGate = Mutex.OpenExisting(name + ".gate");
                owner.closed = EventWaitHandle.OpenExisting(name + ".closed");
                held = owner.nativeGate.WaitOne(0);
                if (!held || owner.closed.WaitOne(0)) return null;
                using (var current = Process.GetCurrentProcess())
                { owner.guiId = current.Id; owner.guiCreation = current.StartTime.ToUniversalTime().ToFileTimeUtc(); }
                owner.id = id; owner.endpointName = name;
                owner.run = run; owner.scenario = scenario; owner.artifactRoot = artifactRoot;
                owner.parentId = owner.view.ReadInt32(4); owner.parentCreation = owner.view.ReadInt64(8);
                if (owner.parentId <= 0 || owner.parentId == owner.guiId || owner.parentCreation <= 0 || !owner.MatchesRequest()) return null;
                var parent = OpenProcess(0x00101000, false, owner.parentId);
                if (parent.IsInvalid) { parent.Dispose(); return null; }
                owner.parentExit = new ProcessWait(parent);
                long creation, exit, kernel, user;
                if (owner.parentExit.WaitOne(0) || !GetProcessTimes(parent, out creation, out exit, out kernel, out user) ||
                    creation != owner.parentCreation || !owner.MatchesRequest()) return null;
                frontierLaunchOwner = owner;
                Release(owner.nativeGate, held); held = false;
                var result = owner; owner = null; return result;
            }
            catch (AbandonedMutexException) { held = true; return null; }
            catch { return null; }
            finally { if (owner != null) { Release(owner.nativeGate, held); owner.Dispose(); } }
        }

        public sealed partial class FrontierLaunchOwner : IDisposable
        {
            // This bounded lane is the existing Helper + Sharer topology, not
            // permission to ignore or evict an additional app launch.
            public const int MaximumAppLaunches = 2;
            internal MemoryMappedFile map;
            internal MemoryMappedViewAccessor view;
            internal Mutex nativeGate;
            internal EventWaitHandle closed;
            internal ProcessWait parentExit;
            internal byte[] registration;
            internal Guid id;
            internal string run, scenario, endpointName;
            internal string artifactRoot;
            internal int parentId, guiId;
            internal long parentCreation, guiCreation;
            private readonly object sync = new object();
            private readonly List<FrontierAppLaunch> apps = new List<FrontierAppLaunch>();
            private bool disposed, sealedLaunches, sealAttempted;
            internal FrontierLaunchOwner() { }
            public Guid RequestId { get { return id; } }
            public int GuiProcessId { get { return guiId; } }
            public long GuiProcessCreationFileTime { get { return guiCreation; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public bool IsCurrent
            {
                get
                {
                    lock (sync)
                    {
                        if (!Object.ReferenceEquals(frontierLaunchOwner, this) || disposed) return false;
                        try { if (!parentExit.WaitOne(0) && !closed.WaitOne(0) && MatchesRequest() && AppRequestsCurrent() && FileChannelsCurrent() && LedgerOriginCurrent() && ParentRegistrationCurrent()) return true; }
                        catch { }
                        Dispose(); return false;
                    }
                }
            }
            internal bool MatchesRequest()
            {
                int stage = view.ReadInt32(48);
                if (view.ReadInt32(0) != Magic || ReadId(view) != id || view.ReadInt32(4) != parentId ||
                    view.ReadInt64(8) != parentCreation || view.ReadInt32(16) != guiId || view.ReadInt64(40) != guiCreation ||
                    view.ReadInt32(20) != registration.Length || (stage != 0 && stage != 1) || view.ReadInt32(52) != 0 ||
                    view.ReadInt64(56) != 0) return false;
                var bytes = new byte[registration.Length];
                if (view.ReadArray(HeaderBytes, bytes, 0, bytes.Length) != bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++) if (bytes[i] != registration[i]) return false;
                return true;
            }

            public FrontierAppLaunch BindApp(Process process)
            { return BindAppCore(process, null); }

            internal FrontierAppLaunch BindAppCore(Process process, FrontierAppRequest request, FrontierOutputCapture output = null)
            {
                lock (sync)
                {
                    if (request != null && !appRequests.Contains(request)) return null;
                    if (!IsCurrent) return null;
                    SafeWaitHandle handle = null;
                    bool retained = false;
                    try
                    {
                        if ((appRequests.Count != 0 && request == null) || (request != null && request.launch != null) ||
                            sealedLaunches || apps.Count == MaximumAppLaunches || process == null || process.HasExited ||
                            !DuplicateHandle(GetCurrentProcess(), process.Handle, GetCurrentProcess(), out handle, 0, false, 2)) return null;
                        int processId = GetFrontierProcessId(handle);
                        long creation, exit, kernel, user;
                        if (processId <= 0 || processId == guiId || processId == parentId || !GetProcessTimes(handle, out creation, out exit, out kernel, out user)) return null;
                        foreach (var app in apps) if (app.AppProcessId == processId && app.AppProcessCreationFileTime == creation) return null;
                        var wait = new ProcessWait(handle); handle = null;
                        var binding = new FrontierAppLaunch(this, wait, processId, creation, output);
                        apps.Add(binding);
                        if (request != null)
                        {
                            if (!request.native.BindApp(process)) return null;
                            request.launch = binding;
                        }
                        if (wait.WaitOne(0) || !IsCurrent) return null;
                        retained = true; return binding;
                    }
                    catch { return null; }
                    finally { if (handle != null) handle.Dispose(); if (!retained) Dispose(); }
                }
            }

            public bool SealLaunches()
            {
                lock (sync)
                {
                    if (!IsCurrent || sealAttempted) return false;
                    sealAttempted = true;
                    if (apps.Count != MaximumAppLaunches ||
                        (appRequests.Count != 0 && (appRequests.Count != MaximumAppLaunches || appRequests.Exists(r => r.launch == null))))
                    { Dispose(); return false; }
                    sealedLaunches = true; return true;
                }
            }
            // Process exit evidence only: this does not prove writer drain, file
            // handoff, stdout EOF, source sealing or a successful acceptance run.
            public bool AllAppsExitedSuccessfully
            {
                get
                {
                    lock (sync)
                    {
                        if (!IsCurrent || !sealedLaunches) return false;
                        foreach (var app in apps) if (!app.ExitedSuccessfully) return false;
                        return IsCurrent;
                    }
                }
            }
            internal bool Owns(FrontierAppLaunch binding)
            {
                lock (sync) { return IsCurrent && apps.Contains(binding); }
            }
            public void Dispose()
            {
                lock (sync)
                {
                    // A diagnostic clone cannot close the original's handles.
                    if (frontierLaunchOwner != null && !Object.ReferenceEquals(frontierLaunchOwner, this)) return;
                    if (disposed) return;
                    disposed = true;
                    if (parentRegistration != null) parentRegistration.Dispose();
                    if (ledgerOrigin != null) ledgerOrigin.Close();
                    if (fileChannels != null) foreach (var channel in fileChannels) if (channel != null) channel.Dispose();
                    if (fileAdoption != null) fileAdoption.Dispose();
                    foreach (var request in appRequests) request.native.Dispose();
                    appRequests.Clear();
                    foreach (var app in apps)
                    {
                        if (app.Output != null) app.Output.Revoke();
                        app.Exit.Dispose();
                    }
                    apps.Clear();
                    if (parentExit != null) parentExit.Dispose();
                    if (view != null) view.Dispose();
                    if (map != null) map.Dispose();
                    if (nativeGate != null) nativeGate.Dispose();
                    if (closed != null) closed.Dispose();
                }
            }
        }

        public sealed class FrontierAppLaunch
        {
            private readonly FrontierLaunchOwner owner;
            internal readonly ProcessWait Exit;
            internal readonly FrontierOutputCapture Output;
            public int AppProcessId { get; private set; }
            public long AppProcessCreationFileTime { get; private set; }
            internal FrontierAppLaunch(FrontierLaunchOwner owner, ProcessWait exit, int process, long creation, FrontierOutputCapture output)
            { this.owner = owner; Exit = exit; AppProcessId = process; AppProcessCreationFileTime = creation; Output = output; }
            public bool IsCurrent { get { return owner.Owns(this); } }
            // EOF plus successful capture flush only; not native exit, writer
            // drain, source integrity, or permission to publish diagnostics.
            public bool OutputDrainedSuccessfully
            { get { return IsCurrent && Output != null && Output.Drained && IsCurrent; } }
            public bool ExitedSuccessfully
            {
                get
                {
                    try
                    {
                        int code;
                        return IsCurrent && Exit.WaitOne(0) && GetExitCodeProcess(Exit.SafeWaitHandle, out code) && code == 0 && IsCurrent;
                    }
                    catch { return false; }
                }
            }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
        }
    }
}
