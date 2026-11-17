using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private static string[] CanonicalEnvironmentProjection(string executable, int timeout, string host,
            Func<string, string> read)
        {
            var seconds = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (read("NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS") != seconds) return null;
            return new string[] { executable, read("NLINK_FILETRANSFER_SOAK_DIRECTION"),
                read("NLINK_TUNA_GUI_PAYER_MODE"), read("NLINK_TUNA_GUI_FAULT"), read("NLINK_TUNA_GUI_ROUTE_MODE"),
                read("NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES"), read("NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE"),
                seconds, read("NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS"), "RegularActivationCycle",
                "measured_live_regular_activation_cycle", host };
        }

        public sealed partial class Relay
        {
            private bool attemptForwarded;
            public bool TryForwardCanonicalAttempt(LocalGuiWriterRequest target, string executable, int timeout,
                ProcessStartInfo startInfo)
            {
                try
                {
                    return TryForwardAttemptProjection(target, 1, CanonicalEnvironmentProjection(executable, timeout,
                        startInfo.FileName, name => startInfo.EnvironmentVariables[name]));
                }
                catch { return false; }
            }

            private bool TryForwardAttemptProjection(LocalGuiWriterRequest target, int attempt, string[] projection)
            {
                lock (frontierRelaySync)
                {
                    try
                    {
                        if (!Object.ReferenceEquals(original, this) || attemptForwarded || target == null || target.relayMode ||
                            !OwnsFrontierHandoff(frontierRelayHandoff) || !owner.IsCurrent || target.run != owner.run ||
                            target.scenario != owner.scenario || target.root != owner.root || ReadId(owner.view) != owner.id ||
                            owner.view.ReadInt32(0) != RelayMagic || owner.view.ReadInt32(48) != 1) return false;
                        var bytes = ReadAttempt(owner.view); Guid origin; string name; int parent; long creation;
                        using (var current = Process.GetCurrentProcess())
                        {
                            if (!SameProjection(bytes, projection, attempt, out origin, out name, out parent, out creation) ||
                                origin != owner.id || name != owner.endpointName || parent != owner.view.ReadInt32(4) || creation != owner.view.ReadInt64(8) ||
                                current.Id != owner.view.ReadInt32(16) ||
                                current.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.view.ReadInt64(40)) return false;
                        }
                        if (!target.WriteAttempt(bytes)) return false;
                        attemptForwarded = true; return true;
                    }
                    catch { return false; }
                }
            }
        }

        public sealed partial class FrontierLaunchOwner
        {
            internal CanonicalAttemptDispatch attemptDispatch;
            public CanonicalAttemptDispatch TryConsumeCanonicalAttempt(string executable, int timeout)
            {
                CanonicalAttemptDispatch dispatch = null;
                try
                {
                    string[] projection;
                    using (var current = Process.GetCurrentProcess())
                        projection = CanonicalEnvironmentProjection(executable, timeout, current.MainModule.FileName,
                            Environment.GetEnvironmentVariable);
                    dispatch = TryOpenCanonicalAttemptDispatch(1, projection);
                    if (dispatch != null && Object.ReferenceEquals(dispatch.TryConsume(this, 1, projection), this)) return dispatch;
                }
                catch { } // Optional diagnostic scope must not replace ordinary startup.
                if (dispatch != null) dispatch.Dispose();
                return null;
            }

            public CanonicalAttemptDispatch TryOpenCanonicalAttemptDispatch(int attempt, string[] projection)
            {
                lock (sync)
                {
                    ProcessWait originExit = null;
                    MemoryMappedFile originMap = null; MemoryMappedViewAccessor originView = null; EventWaitHandle originClosed = null;
                    try
                    {
                        if (!IsCurrent || attemptDispatch != null) return null;
                        var bytes = ReadAttempt(view); Guid origin; string name; int parent; long creation;
                        if (!SameProjection(bytes, projection, attempt, out origin, out name, out parent, out creation)) return null;
                        using (var current = Process.GetCurrentProcess())
                            if (!String.Equals(current.MainModule.FileName, projection[11], StringComparison.OrdinalIgnoreCase)) return null;
                        var handle = OpenProcess(0x00101000, false, parent);
                        originExit = new ProcessWait(handle);
                        long actual, exit, kernel, user;
                        if (handle.IsInvalid || originExit.WaitOne(0) || !GetProcessTimes(handle, out actual, out exit, out kernel, out user) ||
                            actual != creation || !IsCurrent) return null;
                        originMap = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                        originView = originMap.CreateViewAccessor(0, HeaderBytes + RequestBytes, MemoryMappedFileAccess.Read);
                        originClosed = EventWaitHandle.OpenExisting(name + ".closed");
                        if (ReadId(originView) != origin || originView.ReadInt32(0) != RelayMagic || originView.ReadInt32(48) != 1 ||
                            originView.ReadInt32(4) != parent || originView.ReadInt64(8) != creation ||
                            originView.ReadInt32(16) != parentId || originView.ReadInt64(40) != parentCreation ||
                            originView.ReadInt32(20) != registration.Length || originClosed.WaitOne(0)) return null;
                        var originalRegistration = new byte[registration.Length];
                        if (originView.ReadArray(HeaderBytes, originalRegistration, 0, registration.Length) != registration.Length) return null;
                        for (int i = 0; i < registration.Length; i++) if (registration[i] != originalRegistration[i]) return null;
                        attemptDispatch = new CanonicalAttemptDispatch(this, bytes, originExit, originMap, originView, originClosed, attempt);
                        originExit = null; originMap = null; originView = null; originClosed = null; return attemptDispatch;
                    }
                    catch { return null; }
                    finally
                    {
                        if (originExit != null) originExit.Dispose(); if (originClosed != null) originClosed.Dispose();
                        if (originView != null) originView.Dispose(); if (originMap != null) originMap.Dispose();
                    }
                }
            }
        }

        public sealed partial class CanonicalAttemptDispatch : IDisposable
        {
            private readonly FrontierLaunchOwner owner;
            private readonly byte[] bytes;
            private readonly ProcessWait originExit;
            private readonly MemoryMappedFile originMap;
            private readonly MemoryMappedViewAccessor originView;
            private readonly EventWaitHandle originClosed;
            private readonly byte[] originHeader;
            private readonly int attempt;
            private int consumed, scenarioDispatched, disposed;
            internal CanonicalAttemptDispatch(FrontierLaunchOwner owner, byte[] bytes, ProcessWait originExit,
                MemoryMappedFile originMap, MemoryMappedViewAccessor originView, EventWaitHandle originClosed, int attempt)
            {
                this.owner = owner; this.bytes = bytes; this.originExit = originExit; this.attempt = attempt;
                this.originMap = originMap; this.originView = originView; this.originClosed = originClosed;
                originHeader = new byte[HeaderBytes + owner.registration.Length]; originView.ReadArray(0, originHeader, 0, originHeader.Length);
            }
            public bool IsCurrent
            {
                get
                {
                    try
                    {
                        if (!Object.ReferenceEquals(owner.attemptDispatch, this) || disposed != 0 || originExit.WaitOne(0) || originClosed.WaitOne(0) || !owner.IsCurrent) return false;
                        var header = new byte[originHeader.Length]; originView.ReadArray(0, header, 0, header.Length);
                        for (int i = 0; i < header.Length; i++) if (header[i] != originHeader[i]) return false;
                        var original = ReadAttempt(originView);
                        var current = ReadAttempt(owner.view);
                        if (current == null || current.Length != bytes.Length || original == null || original.Length != bytes.Length) return false;
                        for (int i = 0; i < bytes.Length; i++) if (bytes[i] != current[i] || bytes[i] != original[i]) return false;
                        return true;
                    }
                    catch { return false; }
                }
            }
            public FrontierLaunchOwner TryConsume(FrontierLaunchOwner candidate, int candidateAttempt, string[] projection)
            {
                try
                {
                    Guid origin; string name; int parent; long creation;
                    if (!IsCurrent || !Object.ReferenceEquals(owner, candidate) || attempt != candidateAttempt ||
                        !SameProjection(bytes, projection, attempt, out origin, out name, out parent, out creation) ||
                        Interlocked.CompareExchange(ref consumed, 1, 0) != 0 || !IsCurrent) return null;
                    return owner;
                }
                catch { return null; }
            }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(owner.attemptDispatch, this)) return;
                RetireNativeOwnership();
                TryCompleteDestinationAdoption(true);
                RetireSource(source);
                lock (rolesSync) { if (destination != null) destination.Dispose(); }
            }

            public FrontierLaunchOwner TryTakeScenarioOwner()
            {
                if (!IsCurrent || Volatile.Read(ref consumed) != 1 ||
                    Interlocked.CompareExchange(ref scenarioDispatched, 1, 0) != 0 || !IsCurrent) return null;
                return owner;
            }

            // Owner teardown holds its lock. Close only these native handles
            // there; role resources use the reverse lock order and are retired
            // by explicit dispatch disposal, outside that owner critical section.
            internal void RetireNativeOwnership()
            {
                if (!Object.ReferenceEquals(owner.attemptDispatch, this) || Interlocked.Exchange(ref disposed, 1) != 0) return;
                CancelDestinationAdoption();
                originClosed.Dispose(); originView.Dispose(); originMap.Dispose(); originExit.Dispose();
            }
        }
    }
}
