using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Opaque optional transport only. No proof evaluation or production caller.
    public sealed class LocalEvidenceExportSlot : IDisposable
    {
        public const int MaximumPayloadBytes = 262144;
        private const int HeaderBytes = 64;
        private const int Magic = 0x4e4c4531;
        private const int Writing = 1;
        private const int Ready = 2;
        private readonly Guid requestId;
        private readonly string expectedSha256;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex gate;
        private EventWaitHandle closed;
        private ProcessExitHandle producerExit;
        private int producerId;
        private int sealedState;

        public string Name { get; private set; }

        public LocalEvidenceExportSlot(Guid requestId, string expectedSha256)
        {
            if (requestId == Guid.Empty || expectedSha256 == null || !Regex.IsMatch(expectedSha256, @"\A[0-9a-f]{64}\z"))
                throw new ArgumentException("A parent request and independent payload digest are required.");
            this.requestId = requestId;
            this.expectedSha256 = expectedSha256;
            Name = "Local\\nlink-local-evidence-" + Guid.NewGuid().ToString("N");
            try
            {
                map = MemoryMappedFile.CreateNew(Name, HeaderBytes + MaximumPayloadBytes);
                view = map.CreateViewAccessor();
                gate = new Mutex(false, Name + ".gate");
                closed = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".closed");
                view.Write(0, Magic);
                view.WriteArray(16, requestId.ToByteArray(), 0, 16);
            }
            catch { Dispose(); throw; }
        }

        public bool BindProducer(Process process)
        {
            bool held = false;
            try
            {
                if (Volatile.Read(ref sealedState) != 0 || producerExit != null || process == null || process.HasExited)
                    return false;
                try { held = gate.WaitOne(0); }
                catch (AbandonedMutexException) { held = true; return false; }
                if (!held || closed.WaitOne(0) || Volatile.Read(ref sealedState) != 0 || producerExit != null) return false;
                SafeWaitHandle duplicate;
                if (!DuplicateHandle(GetCurrentProcess(), process.Handle, GetCurrentProcess(), out duplicate, 0, false, 2))
                    return false;
                producerExit = new ProcessExitHandle(duplicate);
                producerId = process.Id;
                view.Write(8, producerId);
                return true;
            }
            catch { return false; }
            finally { Release(gate, held); }
        }

        public static bool TryOffer(string name, Guid requestId, byte[] payload)
        {
            if (String.IsNullOrEmpty(name) || requestId == Guid.Empty || payload == null ||
                payload.Length == 0 || payload.Length > MaximumPayloadBytes) return false;
            try
            {
                using (var gate = Mutex.OpenExisting(name + ".gate"))
                using (var closed = EventWaitHandle.OpenExisting(name + ".closed"))
                using (var map = MemoryMappedFile.OpenExisting(name))
                using (var view = map.CreateViewAccessor())
                {
                    bool held = false;
                    try
                    {
                        if (closed.WaitOne(0)) return false;
                        try { held = gate.WaitOne(0); }
                        catch (AbandonedMutexException) { held = true; return false; }
                        if (!held || closed.WaitOne(0) || view.ReadInt32(0) != Magic || view.ReadInt32(4) != 0 ||
                            view.ReadInt32(8) != GetCurrentProcessId() || ReadRequest(view) != requestId)
                            return false;
                        view.Write(4, Writing);
                        view.Write(32, payload.Length);
                        view.WriteArray(HeaderBytes, payload, 0, payload.Length);
                        if (closed.WaitOne(0)) return false;
                        view.Write(4, Ready);
                        return true;
                    }
                    finally { Release(gate, held); }
                }
            }
            catch { return false; } // Optional transport never changes worker exit.
        }

        public byte[] TakeAfterExit()
        {
            if (Interlocked.Exchange(ref sealedState, 1) != 0) return null;
            bool held = false;
            try
            {
                closed.Set();
                int exitCode;
                if (producerExit == null || !producerExit.WaitOne(0) ||
                    !GetExitCodeProcess(producerExit.SafeWaitHandle, out exitCode) || exitCode != 0) return null;
                try { held = gate.WaitOne(0); }
                catch (AbandonedMutexException) { held = true; return null; }
                if (!held || view.ReadInt32(0) != Magic || view.ReadInt32(4) != Ready ||
                    view.ReadInt32(8) != producerId || ReadRequest(view) != requestId) return null;
                int length = view.ReadInt32(32);
                if (length <= 0 || length > MaximumPayloadBytes) return null;
                var payload = new byte[length];
                if (view.ReadArray(HeaderBytes, payload, 0, length) != length) return null;
                using (var sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
                    if (!String.Equals(actual, expectedSha256, StringComparison.Ordinal)) return null;
                }
                return payload;
            }
            catch { return null; }
            finally { Release(gate, held); }
        }

        public void Cancel()
        {
            Interlocked.Exchange(ref sealedState, 1);
            try { if (closed != null) closed.Set(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            Cancel();
            if (view != null) { view.Dispose(); view = null; }
            if (map != null) { map.Dispose(); map = null; }
            if (closed != null) { closed.Dispose(); closed = null; }
            if (gate != null) { gate.Dispose(); gate = null; }
            if (producerExit != null) { producerExit.Dispose(); producerExit = null; }
            // The primary workflow independently owns its original Process object.
        }

        private static Guid ReadRequest(MemoryMappedViewAccessor accessor)
        {
            var bytes = new byte[16];
            if (accessor.ReadArray(16, bytes, 0, 16) != 16) return Guid.Empty;
            return new Guid(bytes);
        }

        private static void Release(Mutex mutex, bool held)
        {
            if (!held || mutex == null) return;
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { } catch (ObjectDisposedException) { }
        }

        private sealed class ProcessExitHandle : WaitHandle
        {
            public ProcessExitHandle(SafeWaitHandle handle) { SafeWaitHandle = handle; }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
            out SafeWaitHandle targetHandle, uint desiredAccess, bool inheritHandle, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeWaitHandle processHandle, out int exitCode);
    }
}
