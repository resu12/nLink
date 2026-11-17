#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NLink.Tools;

namespace NLink.Tests
{
    // Test-only GUI -> original app channel. Separate from diagnostic payloads.
    // stdin is owned by the actual launch; packet scalars alone grant nothing.
    internal sealed partial class AttemptSenderSourceChannel : IDisposable
    {
        private LocalCanonicalSenderSource native;
        private readonly AttemptSenderSourceChannel original;
        private readonly LocalFrontierAppRequest.AppLease lease;
        private FileStream file;
        private string identity;
        private int taken, disposed;
        private int pairedReads;
        private FileStream activeRead;
        internal string Sha256 { get; private set; }
        private AttemptSenderSourceChannel(LocalFrontierAppRequest.AppLease lease)
        { original = this; this.lease = lease; }

        internal static bool Send(FileStream file, SafeWaitHandle process, Stream output, Guid request,
            int pid, long creation, string identity, string hash, string fault)
        {
            return LocalCanonicalSenderSource.Send(file, process, output,
                fault == "request" ? Guid.NewGuid() : request, pid, fault == "creation" ? 1L : creation, identity, hash);
        }

        internal static AttemptSenderSourceChannel Receive(LocalFrontierAppRequest.AppLease lease, Stream input)
        {
            var received = LocalCanonicalSenderSource.Receive(lease, input);
            if (received == null) return null;
            var clone = typeof(Object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var copied = (LocalCanonicalSenderSource)clone.Invoke(received, null);
            var copiedLease = (LocalFrontierAppRequest.AppLease)clone.Invoke(lease, null);
            if (copied.IsCurrent || copied.TakeRead(lease) != null || received.TakeRead(copiedLease) != null)
                throw new InvalidOperationException("copied_native_sender_source_admitted");
            copied.Dispose();
            if (!received.IsCurrent) throw new InvalidOperationException("copied_native_sender_source_retired_original");
            var result = new AttemptSenderSourceChannel(lease);
            result.native = received;
            result.file = received.TakeRead(lease);
            result.identity = received.FileIdentity;
            result.Sha256 = received.Sha256;
            if (received.TakeRead(lease) != null) throw new InvalidOperationException("native_sender_read_repeated");
            if (result.file != null && result.IsCurrent) return result;
            result.Dispose(); return null;
        }

        internal bool IsCurrent
        {
            get
            {
                if (!Object.ReferenceEquals(original, this) || disposed != 0) return false;
                bool current = false;
                try
                {
                    current = native.IsCurrent && lease.IsCurrent && file != null && MatchesIdentity(file.SafeFileHandle);
                }
                catch (ObjectDisposedException) { }
                if (!current) Dispose();
                return current;
            }
        }

        internal FileStream TakeRead(LocalFrontierAppRequest.AppLease candidate)
        {
            if (!Object.ReferenceEquals(lease, candidate) || !IsCurrent || Interlocked.CompareExchange(ref taken, 1, 0) != 0) return null;
            return file;
        }
        // Bounded fixture path: one metadata read, then one payload-pump read.
        // Both reopen the retained native file, never a pathname. This is not
        // a production retry/recovery factory or a route-authority capability.
        internal FileStream OpenNextRead(LocalFrontierAppRequest.AppLease candidate)
        {
            lock (original)
            {
                if (!Object.ReferenceEquals(lease, candidate) || !IsCurrent || (taken != 0 && taken != 2) || pairedReads == 2 ||
                    (activeRead != null && activeRead.CanRead)) return null;
                if (Interlocked.CompareExchange(ref taken, 2, 0) == 1) return null;
                pairedReads++;
                SafeFileHandle handle = null;
                try
                {
                    handle = ReOpenFile(file.SafeFileHandle, 0x80000000, 1, 0x08000000);
                    if (handle.IsInvalid || !MatchesIdentity(handle) || !IsCurrent) { Dispose(); return null; }
                    activeRead = new FileStream(handle, FileAccess.Read);
                    handle = null;
                    return activeRead;
                }
                catch (IOException) { Dispose(); return null; }
                catch (ObjectDisposedException) { Dispose(); return null; }
                finally { if (handle != null) handle.Dispose(); }
            }
        }
        private bool MatchesIdentity(SafeFileHandle handle)
        {
            Information info;
            return !handle.IsClosed && GetFileType(handle) == 1 && GetFileInformationByHandle(handle, out info) &&
                info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") == identity &&
                (((long)info.SizeHigh << 32) | info.SizeLow) == 268435456;
        }
        public void Dispose()
        {
            if (!Object.ReferenceEquals(original, this)) return;
            lock (original)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                if (activeRead != null) activeRead.Dispose();
                if (native != null) native.Dispose();
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Information
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
        [DllImport("kernel32.dll")] private static extern SafeFileHandle ReOpenFile(SafeFileHandle handle, uint access, uint share, uint flags);
    }
}
