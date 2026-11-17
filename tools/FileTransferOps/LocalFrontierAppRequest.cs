#if NET8_0_OR_GREATER
// This shared C# 5 source uses explicit null checks rather than annotations.
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
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
    // Native app attachment only. This is a separate trusted-workflow request,
    // not the GUI writer completion or a same-account security boundary.
#if NET8_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public sealed partial class LocalFrontierAppRequest : IDisposable
    {
        private const int Magic = 0x4e464131, Version = 1, HeaderBytes = 64;
        public const int MaximumRegistrationBytes = 4096;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex gate;
        private EventWaitHandle cancelled;
        private ProcessWait appExit;
        private byte[] registration;
        private int parentId, appId, bindAttempted, taken, disposed, revoked;
        private long parentCreation, appCreation;
        private string run, scenario;
        public string Name { get; private set; }
        public Guid RequestId { get; private set; }
        private LocalFrontierAppRequest() { }

        public static LocalFrontierAppRequest TryCreate(string run, string scenario)
        {
            var request = new LocalFrontierAppRequest();
            try
            {
                byte[] body = Registration(run, scenario);
                if (body == null) return null;
                request.RequestId = Guid.NewGuid(); request.run = run; request.scenario = scenario;
                request.registration = body;
                request.Name = "Local\\nlink-frontier-app-" + Guid.NewGuid().ToString("N");
                request.map = MemoryMappedFile.CreateNew(request.Name, HeaderBytes + MaximumRegistrationBytes);
                request.view = request.map.CreateViewAccessor();
                request.gate = new Mutex(false, request.Name + ".gate");
                request.cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, request.Name + ".cancelled");
                bool created;
                request.filesReady = new EventWaitHandle(false, EventResetMode.ManualReset, request.Name + FilesReadySuffix, out created);
                if (!created) return null;
                using (var current = Process.GetCurrentProcess())
                { request.parentId = current.Id; request.parentCreation = current.StartTime.ToUniversalTime().ToFileTimeUtc(); }
                request.view.Write(0, Magic); request.view.Write(4, Version); request.view.Write(12, request.parentId);
                request.view.Write(16, request.parentCreation); request.view.WriteArray(40, request.RequestId.ToByteArray(), 0, 16);
                request.view.Write(56, body.Length); request.view.WriteArray(HeaderBytes, body, 0, body.Length);
                var result = request; request = null; return result;
            }
            catch { return null; }
            finally { if (request != null) request.Dispose(); }
        }

        // The caller supplies the actual launched Process, never a claimed PID.
        // Any failed first binding closes this request rather than rebinding it.
        public bool BindApp(Process process)
        {
            if (Interlocked.Exchange(ref bindAttempted, 1) != 0) return false;
            bool held = false, bound = false;
            try
            {
                if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref taken) != 0 || process == null || process.HasExited) return false;
                held = gate.WaitOne(0);
                if (!held || cancelled.WaitOne(0) || view.ReadInt32(8) != 0 || !MatchesHeader(view, RequestId, parentId, parentCreation, 0, 0, registration)) return false;
                SafeWaitHandle handle;
                if (!DuplicateHandle(GetCurrentProcess(), process.Handle, GetCurrentProcess(), out handle, 0, false, 2)) return false;
                appExit = new ProcessWait(handle);
                long exit, kernel, user;
                appId = GetProcessId(handle);
                if (appId <= 0 || appId == parentId || appExit.WaitOne(0) ||
                    !GetProcessTimes(handle, out appCreation, out exit, out kernel, out user)) return false;
                view.Write(24, appId); view.Write(32, appCreation); view.Write(8, 1);
                bound = true; return true;
            }
            catch (AbandonedMutexException) { held = true; return false; }
            catch { return false; }
            finally { Release(gate, held); if (!bound) Cancel(); }
        }

        public static AppLease TryAttach(string name, Guid requestId, string run, string scenario)
        { return TryAttachCore(name, requestId, run, scenario, null); }

        private static AppLease TryAttachCore(string name, Guid requestId, string run, string scenario, EventWaitHandle startFailed)
        {
            if (name == null || !Regex.IsMatch(name, @"\ALocal\\nlink-frontier-app-[0-9a-f]{32}\z") || requestId == Guid.Empty) return null;
            var lease = new AppLease();
            bool held = false;
            try
            {
                lease.registration = Registration(run, scenario);
                if (lease.registration == null) return null;
                lease.map = MemoryMappedFile.OpenExisting(name); lease.view = lease.map.CreateViewAccessor();
                lease.gate = Mutex.OpenExisting(name + ".gate");
                lease.cancelled = EventWaitHandle.OpenExisting(name + ".cancelled");
                held = lease.gate.WaitOne(0);
                if (!held || lease.cancelled.WaitOne(0) || (startFailed != null && startFailed.WaitOne(0)) || lease.view.ReadInt32(8) != 1) return null;
                using (var current = Process.GetCurrentProcess())
                { lease.appId = current.Id; lease.appCreation = current.StartTime.ToUniversalTime().ToFileTimeUtc(); }
                lease.parentId = lease.view.ReadInt32(12); lease.parentCreation = lease.view.ReadInt64(16);
                if (lease.parentId <= 0 || lease.parentId == lease.appId || lease.parentCreation <= 0 ||
                    !MatchesHeader(lease.view, requestId, lease.parentId, lease.parentCreation, lease.appId, lease.appCreation, lease.registration)) return null;
                var parent = OpenProcess(0x00101000, false, lease.parentId);
                if (parent.IsInvalid) { parent.Dispose(); return null; }
                lease.parentExit = new ProcessWait(parent);
                long creation, exit, kernel, user;
                if (lease.parentExit.WaitOne(0) || !GetProcessTimes(parent, out creation, out exit, out kernel, out user) || creation != lease.parentCreation) return null;
                lease.endpointName = name;
                lease.RequestId = requestId; lease.AcceptanceRunId = run; lease.ScenarioRunId = scenario;
                lease.view.Write(8, 2);
                Release(lease.gate, held); held = false;
                var result = lease; lease = null; return result;
            }
            catch (AbandonedMutexException) { held = true; return null; }
            catch { return null; }
            finally { if (lease != null) { Release(lease.gate, held); lease.Dispose(); } }
        }

        // This proves attachment/completion of the bound native app only.
        // It does not prove a payload, operational writer, GUI finalizer or source.
        public AttachmentCompletion TakeAfterExit()
        {
            if (Interlocked.Exchange(ref taken, 1) != 0) return null;
            bool held = false;
            try
            {
                if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0 || cancelled.WaitOne(0)) return null;
                cancelled.Set();
                int code;
                if (appExit == null || !appExit.WaitOne(0) || !GetExitCodeProcess(appExit.SafeWaitHandle, out code) || code != 0) return null;
                held = gate.WaitOne(0);
                if (!held || view.ReadInt32(8) != 3 || !MatchesHeader(view, RequestId, parentId, parentCreation, appId, appCreation, registration) ||
                    Volatile.Read(ref revoked) != 0 || Volatile.Read(ref disposed) != 0) return null;
                return new AttachmentCompletion(RequestId, run, scenario, parentId, parentCreation, appId, appCreation);
            }
            catch (AbandonedMutexException) { held = true; return null; }
            catch { return null; }
            finally { Release(gate, held); }
        }

        public void Cancel()
        {
            lock (startSync)
            {
                Interlocked.Exchange(ref revoked, 1);
                try { if (cancelled != null) cancelled.Set(); } catch (ObjectDisposedException) { }
                if (startSignal != null) startSignal.Fail();
            }
        }

        // Internal to the original GUI-owned request wrapper. Neither caller
        // metadata nor an attachment-completion DTO can import this ownership.
        internal bool MatchesGuiLaunch(int gui, long guiTime, int app, long appTime)
        {
            try
            {
                int stage = view.ReadInt32(8);
                return Volatile.Read(ref disposed) == 0 && Volatile.Read(ref revoked) == 0 && Volatile.Read(ref taken) == 0 &&
                    !cancelled.WaitOne(0) && parentId == gui && parentCreation == guiTime && appId == app && appCreation == appTime &&
                    (app == 0 ? stage == 0 : stage >= 1 && stage <= 3) &&
                    MatchesHeader(view, RequestId, parentId, parentCreation, appId, appCreation, registration);
            }
            catch { return false; }
        }
        internal bool GuiAttachmentCompleted { get { try { return view.ReadInt32(8) == 3; } catch { return false; } } }
        internal bool GuiAttachmentOpen { get { try { return view.ReadInt32(8) == 2; } catch { return false; } } }

        public void Dispose()
        {
            lock (startSync)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                Cancel();
                if (startSignal != null) startSignal.Dispose();
                if (appExit != null) appExit.Dispose();
                if (view != null) view.Dispose();
                if (map != null) map.Dispose();
                if (gate != null) gate.Dispose();
                if (cancelled != null) cancelled.Dispose();
                if (filesReady != null) filesReady.Dispose();
            }
        }

        public sealed partial class AppLease : IDisposable
        {
            internal MemoryMappedFile map;
            internal MemoryMappedViewAccessor view;
            internal Mutex gate;
            internal EventWaitHandle cancelled;
            internal ProcessWait parentExit;
            internal byte[] registration;
            internal int parentId, appId;
            internal long parentCreation, appCreation;
            private int attempted, completed, disposed, catalogClaimed;
            private object catalogOwner;
            private readonly AppLease original;
            internal string endpointName;
            public Guid RequestId { get; internal set; }
            public string AcceptanceRunId { get; internal set; }
            public string ScenarioRunId { get; internal set; }
            public int AppProcessId { get { return appId; } }
            public long AppProcessCreationFileTime { get { return appCreation; } }
            internal AppLease() { original = this; }
            public bool IsCurrent
            {
                get { return Volatile.Read(ref attempted) == 0 && OwnsRequest(); }
            }
            // Opaque in-process identity only. Claiming a slot cannot turn an
            // arbitrary object into a Core producer, writer or output capability.
            public bool TryBindCatalog(object owner)
            {
                if (Interlocked.Exchange(ref catalogClaimed, 1) != 0 || owner == null || !IsCurrent) return false;
                Interlocked.Exchange(ref catalogOwner, owner);
                if (OwnsCatalog(owner)) return true;
                Interlocked.CompareExchange(ref catalogOwner, null, owner);
                return false;
            }
            public bool OwnsCatalog(object owner)
            { return owner != null && Object.ReferenceEquals(Volatile.Read(ref catalogOwner), owner) && IsCurrent; }
            private bool OwnsRequest()
            {
                try { return Object.ReferenceEquals(original, this) && Volatile.Read(ref disposed) == 0 && parentExit != null &&
                    !parentExit.WaitOne(0) && !cancelled.WaitOne(0) && view.ReadInt32(8) == 2 &&
                    MatchesHeader(view, RequestId, parentId, parentCreation, appId, appCreation, registration); }
                catch { return false; }
            }
            public bool CompleteAttachment()
            {
                if (!Object.ReferenceEquals(original, this)) return false;
                if (Interlocked.Exchange(ref attempted, 1) != 0) return false;
                if (fileHandoff != null && !fileHandoff.Acknowledged) fileHandoff.Cancel();
                bool held = false;
                try
                {
                    if (!OwnsRequest()) return false;
                    held = gate.WaitOne(0);
                    if (!held || !OwnsRequest()) return false;
                    if (parentExit.WaitOne(0) || cancelled.WaitOne(0)) return false;
                    view.Write(8, 3); Volatile.Write(ref completed, 1); return true;
                }
                catch (AbandonedMutexException) { held = true; return false; }
                catch { return false; }
                finally { Release(gate, held); if (Volatile.Read(ref completed) == 0 && fileHandoff != null) fileHandoff.Cancel(); }
            }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this)) return;
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                if (fileHandoff != null) fileHandoff.Dispose();
                Interlocked.Exchange(ref catalogOwner, null);
                // A failed foreign attach has no RequestId and cannot revoke
                // the actual app's request. An abandoned admitted lease can.
                if (RequestId != Guid.Empty && Volatile.Read(ref completed) == 0)
                { try { cancelled.Set(); } catch (ObjectDisposedException) { } }
                if (parentExit != null) parentExit.Dispose();
                if (view != null) view.Dispose();
                if (map != null) map.Dispose();
                if (gate != null) gate.Dispose();
                if (cancelled != null) cancelled.Dispose();
            }
        }

        public sealed class AttachmentCompletion
        {
            public Guid RequestId { get; private set; }
            public string AcceptanceRunId { get; private set; }
            public string ScenarioRunId { get; private set; }
            public int ParentProcessId { get; private set; }
            public long ParentProcessCreationFileTime { get; private set; }
            public int AppProcessId { get; private set; }
            public long AppProcessCreationFileTime { get; private set; }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal AttachmentCompletion(Guid id, string run, string scenario, int parent, long parentTime, int app, long appTime)
            { RequestId = id; AcceptanceRunId = run; ScenarioRunId = scenario; ParentProcessId = parent;
                ParentProcessCreationFileTime = parentTime; AppProcessId = app; AppProcessCreationFileTime = appTime; }
        }

        private static byte[] Registration(string run, string scenario)
        {
            if (!ValidText(run) || !ValidText(scenario)) return null;
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Utf8))
            { writer.Write(run); writer.Write(scenario); writer.Flush(); return bytes.Length <= MaximumRegistrationBytes ? bytes.ToArray() : null; }
        }
        private static bool ValidText(string text)
        { return !String.IsNullOrWhiteSpace(text) && text != "(none)" && text.Length <= 1024 && text.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0; }
        private static bool MatchesHeader(MemoryMappedViewAccessor view, Guid id, int parent, long parentTime, int app, long appTime, byte[] expected)
        {
            return view.ReadInt32(24) == app && view.ReadInt64(32) == appTime && MatchesRegistration(view, id, parent, parentTime, expected);
        }
        private static bool MatchesRegistration(MemoryMappedViewAccessor view, Guid id, int parent, long parentTime, byte[] expected)
        {
            if (view.ReadInt32(0) != Magic || view.ReadInt32(4) != Version || view.ReadInt32(12) != parent || view.ReadInt64(16) != parentTime ||
                view.ReadInt32(28) != 0 ||
                view.ReadInt32(56) != expected.Length || view.ReadInt32(60) != 0) return false;
            var guid = new byte[16]; if (view.ReadArray(40, guid, 0, 16) != 16 || new Guid(guid) != id) return false;
            var body = new byte[expected.Length]; if (view.ReadArray(HeaderBytes, body, 0, body.Length) != body.Length) return false;
            for (int i = 0; i < body.Length; i++) if (body[i] != expected[i]) return false;
            return true;
        }
        private static void Release(Mutex gate, bool held)
        { if (held && gate != null) { try { gate.ReleaseMutex(); } catch (ApplicationException) { } catch (ObjectDisposedException) { } } }
        internal sealed class ProcessWait : WaitHandle { internal ProcessWait(SafeWaitHandle handle) { SafeWaitHandle = handle; } }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int GetProcessId(SafeWaitHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target, out SafeWaitHandle duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeWaitHandle handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeWaitHandle handle, out int code);
    }
}
