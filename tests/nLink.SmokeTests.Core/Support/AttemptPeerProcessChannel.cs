#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NLink.Tools;

namespace NLink.Tests
{
    // Explicit offline fixture only. The original GUI launch passes a reduced
    // QUERY_LIMITED_INFORMATION/SYNCHRONIZE handle, never a PID-based reopen.
    internal sealed class AttemptPeerProcessChannel : IDisposable
    {
        private const int Magic = 0x41505031;
        private static int receiveAttempted;
        private readonly AttemptPeerProcessChannel original;
        private readonly LocalFrontierAppRequest.AppLease lease;
        private SafeWaitHandle process;
        private int pid, retired;
        private long creation;
        internal Guid PeerRequest { get; private set; }
        private AttemptPeerProcessChannel(LocalFrontierAppRequest.AppLease lease) { original = this; this.lease = lease; }

        internal static bool Send(SafeWaitHandle peer, SafeWaitHandle target, Stream output,
            Guid targetRequest, int targetPid, long targetCreation, Guid peerRequest, int peerPid, long peerCreation, string fault)
        {
            IntPtr remote;
            if (!DuplicateHandle(GetCurrentProcess(), peer, target, out remote, 0x00101000, false, 0)) return false;
            // Published remote handles are closed by the importing original
            // child or its exit; never close a possibly reused remote number.
            using (var body = new MemoryStream())
            using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(remote.ToInt64());
                writer.Write((fault == "request" ? Guid.NewGuid() : targetRequest).ToByteArray());
                writer.Write(targetPid); writer.Write(targetCreation); writer.Write(peerRequest.ToByteArray());
                writer.Write(peerPid); writer.Write(fault == "creation" ? 1L : peerCreation); writer.Flush();
                using (var packet = new BinaryWriter(output, Encoding.UTF8, true))
                { packet.Write((int)body.Length); packet.Write(body.ToArray()); packet.Flush(); }
                return true;
            }
        }

        internal static AttemptPeerProcessChannel Receive(LocalFrontierAppRequest.AppLease lease, Stream input)
        {
            if (lease == null || !lease.IsCurrent || Interlocked.Exchange(ref receiveAttempted, 1) != 0) return null;
            var owner = new AttemptPeerProcessChannel(lease);
            bool retained = false;
            try
            {
                using (var channel = new BinaryReader(input, Encoding.UTF8, true))
                {
                    int length = channel.ReadInt32();
                    if (length < 12 || length > 128) return null;
                    var bytes = channel.ReadBytes(length);
                    using (var body = new MemoryStream(bytes, false))
                    using (var reader = new BinaryReader(body, Encoding.UTF8))
                    {
                        if (reader.ReadInt32() != Magic) return null;
                        long raw = reader.ReadInt64();
                        if (raw <= 0) return null;
                        owner.process = new SafeWaitHandle(new IntPtr(raw), true);
                        if (new Guid(reader.ReadBytes(16)) != lease.RequestId || reader.ReadInt32() != lease.AppProcessId ||
                            reader.ReadInt64() != lease.AppProcessCreationFileTime) return null;
                        owner.PeerRequest = new Guid(reader.ReadBytes(16)); owner.pid = reader.ReadInt32(); owner.creation = reader.ReadInt64();
                        if (bytes.Length != length || body.Position != body.Length || owner.PeerRequest == Guid.Empty ||
                            owner.PeerRequest == lease.RequestId || owner.pid == lease.AppProcessId || !owner.IsCurrent) return null;
                    }
                }
                retained = true; return owner;
            }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            finally { if (!retained) owner.Dispose(); }
        }

        internal bool IsCurrent
        {
            get
            {
                if (!Object.ReferenceEquals(original, this) || retired != 0) return false;
                lock (original)
                {
                    long created, exited, kernel, user;
                    if (lease.IsCurrent && process != null && !process.IsClosed && !process.IsInvalid &&
                        GetProcessId(process) == pid && WaitForSingleObject(process, 0) == 258 &&
                        GetProcessTimes(process, out created, out exited, out kernel, out user) && created == creation) return true;
                    Dispose(); return false;
                }
            }
        }

        internal bool MatchesPipe(SafePipeHandle pipe, bool server)
        {
            if (!IsCurrent) return false;
            uint actual;
            bool matched = (server ? GetNamedPipeClientProcessId(pipe, out actual) : GetNamedPipeServerProcessId(pipe, out actual)) && actual == pid && IsCurrent;
            if (!matched) Dispose();
            return matched;
        }

        public void Dispose()
        {
            if (!Object.ReferenceEquals(original, this)) return;
            lock (original)
            {
                if (Interlocked.Exchange(ref retired, 1) != 0) return;
                if (process != null) process.Dispose();
            }
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeWaitHandle source,
            SafeWaitHandle targetProcess, out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] private static extern int GetProcessId(SafeWaitHandle process);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeWaitHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    }
}
