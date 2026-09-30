using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private FrontierGuiReports payloadOriginalReports;
        private CanonicalPayloadWrapper payloadWrapper;

        // Retain the first reports issued by the original launch; never reconstruct them.
        private void RememberPayloadReports(FrontierGuiReports reports) { payloadOriginalReports = reports; }

        public CanonicalPayloadWrapper TryStartPayloadWrapper(FrontierGuiReports reports)
        {
            lock (frontierParentSync)
            {
                if (!Object.ReferenceEquals(original, this) || relayMode || payloadWrapper != null ||
                    reports == null || !Object.ReferenceEquals(payloadOriginalReports, reports) || !reports.NativeLaunchReady ||
                    !Object.ReferenceEquals(frontierParentRegistration, reports.ParentRegistration) ||
                    !ParentScopeCurrent(frontierParentRegistration) || ReadAttempt(view) == null) return null;
                payloadWrapper = new CanonicalPayloadWrapper(this, reports);
                payloadWrapper.Start(); return payloadWrapper;
            }
        }

        public sealed partial class CanonicalAttemptDispatch
        {
            private int payloadOffered;
            partial void WithholdPayloadCommitForTest(Stream pipe, ref string stage, ref bool withheld);
            public bool TryOfferClosedPayloadsToWrapper(FrontierFinalizerBinding binding)
            {
                CanonicalAttemptSource first;
                CanonicalAttemptDestination second;
                CanonicalAttemptWriters writers;
                lock (rolesSync)
                {
                    if (!IsCurrent || binding == null || source == null || destination == null ||
                        !source.IsCurrent || !destination.IsCurrent) return false;
                    first = source; second = destination;
                    writers = TryBindRoleWriters(binding, binding.WriterMatch);
                    if (writers == null || !first.TryBindClosedWriters(writers) ||
                        !second.TryBindClosedWriters(writers)) return false;
                }
                // The existing exchange releases rolesSync while awaiting the wrapper.
                return TryOfferPayloadsToWrapper(first, second, writers);
            }

            public bool TryOfferPayloadsToWrapper(CanonicalAttemptSource first, CanonicalAttemptDestination second,
                CanonicalAttemptWriters writers)
            {
                lock (rolesSync)
                {
                    if (!IsCurrent || !Object.ReferenceEquals(source, first) || !Object.ReferenceEquals(destination, second) ||
                        !Object.ReferenceEquals(roleWriters, writers) || first == null || second == null || writers == null ||
                        !first.IsCurrent || !second.IsCurrent || !writers.IsCurrent ||
                        !Object.ReferenceEquals(first.Writers, writers) || !Object.ReferenceEquals(second.writers, writers) ||
                        Interlocked.CompareExchange(ref payloadOffered, 1, 0) != 0) return false;
                }
                bool held = false, success = false;
                string stage = "source_read", failure = "none";
                long started = Stopwatch.GetTimestamp();
                try
                {
                    using (var read = first.Held.TryOpenVerifiedRead())
                    using (var pipe = new NamedPipeClientStream(".", "nlink-payload-wrapper-" + owner.id.ToString("N"),
                        PipeDirection.InOut, PipeOptions.Asynchronous))
                    using (var deadline = new CancellationTokenSource(10000))
                    {
                        if (read == null) return false;
                        second.file.DangerousAddRef(ref held);
                        Func<bool> current = () => IsCurrent && first.IsCurrent && second.IsCurrent && writers.IsCurrent;
                        var token = deadline.Token;
                        stage = "connect";
                        pipe.ConnectAsync(token).GetAwaiter().GetResult();
                        stage = "server_scope";
                        uint pid;
                        if (!CanonicalPayloadWrapper.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) ||
                            pid != owner.parentId || !current()) return false;
                        byte[] packet, nonce = Guid.NewGuid().ToByteArray();
                        stage = "packet_build";
                        using (var stream = new MemoryStream())
                        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
                        {
                            writer.Write(0x4e505731); writer.Write(nonce); writer.Write(owner.id.ToByteArray());
                            writer.Write(owner.guiId); writer.Write(owner.guiCreation);
                            writer.Write(bytes.Length); writer.Write(bytes);
                            writer.Write(second.session); writer.Write(second.transfer);
                            foreach (var manifest in new[] { writers.Helpee, writers.Helper })
                            {
                                writer.Write(manifest.AppRequest.ToByteArray()); writer.Write(manifest.AppId);
                                writer.Write(manifest.AppCreation); writer.Write(manifest.Writer); writer.Write(manifest.Drain.Last);
                            }
                            writer.Write(read.SafeFileHandle.DangerousGetHandle().ToInt64()); writer.Write(first.FileIdentity);
                            writer.Write(first.ByteCount); writer.Write(first.Sha256);
                            writer.Write(second.file.DangerousGetHandle().ToInt64()); writer.Write(second.identity);
                            writer.Write(second.bytes); writer.Write(BitConverter.ToString(Convert.FromBase64String(second.hash)).Replace("-", "").ToLowerInvariant());
                            writer.Flush(); packet = stream.ToArray();
                        }
                        stage = "packet_scope";
                        if (packet.Length > 8192 || !current()) return false;
                        var length = BitConverter.GetBytes(packet.Length);
                        stage = "packet_write";
                        pipe.WriteAsync(length, 0, length.Length, token).GetAwaiter().GetResult();
                        pipe.WriteAsync(packet, 0, packet.Length, token).GetAwaiter().GetResult();
                        stage = "first_ack";
                        if (!CanonicalPayloadWrapper.Equal(nonce, CanonicalAttemptDestination.Read(pipe, 16, token)) || !current()) return false;
                        bool withheld = false;
                        WithholdPayloadCommitForTest(pipe, ref stage, ref withheld);
                        if (withheld) return false;
                        stage = "commit_write";
                        pipe.WriteAsync(nonce, 0, nonce.Length, token).GetAwaiter().GetResult();
                        // Receipt proves the duplicate is registered before normal GUI cleanup.
                        stage = "receipt";
                        success = CanonicalPayloadWrapper.Equal(nonce, CanonicalAttemptDestination.Read(pipe, 16, token)) && current();
                        return success;
                    }
                }
                catch (Exception ex) { failure = ex.GetType().Name; return false; }
                finally
                {
                    if (held) second.file.DangerousRelease();
                    if (!success) try
                    {
                        Console.Error.WriteLine("payload-offer-failed; stage=" + stage + "; exception_type=" + failure +
                            "; elapsed_ms=" + ((Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency) +
                            "; utc=" + DateTime.UtcNow.ToString("O"));
                    } catch { }
                }
            }
        }

        // Actual transfer-file custody, separate from diagnostic catalog payloads.
        // No reader/parent forwarding/acceptance capability is issued here.
        public sealed partial class CanonicalPayloadWrapper : IDisposable
        {
            private readonly CanonicalPayloadWrapper original;
            private readonly LocalGuiWriterRequest owner;
            private readonly FrontierGuiReports reports;
            private readonly byte[] attemptBytes, registration;
            private readonly object sync = new object();
            private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            private readonly SafeFileHandle[] files = new SafeFileHandle[2];
            private readonly string[] identities = new string[2], hashes = new string[2];
            private readonly Guid[] writerRequests = new Guid[2];
            private readonly int[] writerProcesses = new int[2];
            private readonly long[] writerCreations = new long[2], writerLastSequences = new long[2];
            private readonly Stopwatch work;
            private RegisteredWaitHandle cancelled, exited;
            private Task<bool> adoption;
            private NamedPipeServerStream pipe;
            private bool retired, committed, taken, takeAttempted, verified;
            private string helpeeWriter, helperWriter, session, transfer;

            internal CanonicalPayloadWrapper(LocalGuiWriterRequest owner, FrontierGuiReports reports)
            {
                original = this; this.owner = owner; this.reports = reports;
                InitializeFaultForTest();
                work = owner.frontierParentRegistration.parentWork;
                attemptBytes = ReadAttempt(owner.view);
                registration = new byte[owner.view.ReadInt32(20)]; owner.view.ReadArray(HeaderBytes, registration, 0, registration.Length);
            }
            internal void Start()
            {
                try
                {
                    pipe = new NamedPipeServerStream("nlink-payload-wrapper-" + owner.RequestId.ToString("N"),
                        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    cancelled = ThreadPool.RegisterWaitForSingleObject(owner.closed, (s,t) => RequestClosed(), null, Timeout.Infinite, true);
                    exited = ThreadPool.RegisterWaitForSingleObject(owner.workerExit, (s,t) =>
                    { lock (sync) { if (!retired && (!committed || !GuiSucceeded())) Dispose(); } }, null, Timeout.Infinite, true);
                    adoption = Task.Run((Func<Task<bool>>)Adopt);
                }
                catch { Dispose(); }
            }
            partial void InitializeFaultForTest();
            partial void PrepareAcknowledgmentForTest(byte[] ack, ref bool cancel);
            partial void AlterPayloadIdentityForTest(int slot);
            private bool ScopeCurrent()
            {
                try
                {
                    if (retired || !Object.ReferenceEquals(owner.payloadWrapper, this) || !Object.ReferenceEquals(owner.original, owner) ||
                        !WriterScopeCurrent() || ReadId(owner.view) != owner.RequestId ||
                        owner.view.ReadInt32(0) != Magic || owner.view.ReadInt32(16) != owner.workerId ||
                        owner.view.ReadInt32(4) != owner.frontierParentRegistration.ParentId ||
                        owner.view.ReadInt64(8) != owner.frontierParentRegistration.ParentCreation ||
                        owner.view.ReadInt64(40) != owner.workerCreation || owner.view.ReadInt32(20) != registration.Length ||
                        !Equal(attemptBytes, ReadAttempt(owner.view))) return false;
                    for (int i = 0; i < registration.Length; i++) if (owner.view.ReadByte(HeaderBytes + i) != registration[i]) return false;
                    return true;
                }
                catch { return false; }
            }
            private bool GuiSucceeded()
            { try { int code; return owner.workerExit.WaitOne(0) && GetExitCodeProcess(owner.workerExit.SafeWaitHandle, out code) && code == 0; } catch { return false; } }
            private async Task<bool> Adopt()
            {
                bool success = false;
                string stage = "connect", failure = "none";
                long started = Stopwatch.GetTimestamp();
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    // Framework pipe reads can ignore token cancellation after I/O starts.
                    using (deadline.Token.Register(pipe.Dispose))
                    {
                        deadline.CancelAfter(10000); var token = deadline.Token;
                        stage = "client_scope";
                        uint pid;
                        if (!ScopeCurrent() || owner.workerExit.WaitOne(0) ||
                            !CanonicalAttemptDestination.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) || pid != owner.workerId) return false;
                        stage = "packet_length";
                        int length = BitConverter.ToInt32(await ReadAsync(pipe, 4, token).ConfigureAwait(false), 0);
                        if (length < 128 || length > 8192) return false;
                        stage = "packet_read";
                        var packet = await ReadAsync(pipe, length, token).ConfigureAwait(false);
                        byte[] nonce;
                        stage = "work_admission";
                        lock (owner.frontierParentSync)
                        lock (sync)
                        {
                            if (work.IsRunning || work.ElapsedMilliseconds > 5000) return false;
                            work.Start();
                            try
                            {
                                stage = "payload_validation";
                                if (!ScopeCurrent() || !Pull(packet, out nonce) || !FilesCurrent() || work.ElapsedMilliseconds > 5000) return false;
                            }
                            finally { work.Stop(); }
                        }
                        stage = "first_ack";
                        var ack = (byte[])nonce.Clone(); bool cancel = false;
                        PrepareAcknowledgmentForTest(ack, ref cancel);
                        if (cancel) return false;
                        await pipe.WriteAsync(ack, 0, ack.Length, token).ConfigureAwait(false);
                        stage = "commit_read";
                        var commit = await ReadAsync(pipe, 16, token).ConfigureAwait(false);
                        stage = "commit_scope";
                        lock (sync)
                        {
                            if (!Equal(nonce, commit) || !ScopeCurrent() || !FilesCurrent() || owner.workerExit.WaitOne(0)) return false;
                            committed = true;
                        }
                        stage = "receipt_write";
                        await pipe.WriteAsync(nonce, 0, nonce.Length, token).ConfigureAwait(false);
                        success = true; return true;
                    }
                }
                catch (Exception ex) { failure = ex.GetType().Name; return false; }
                finally
                {
                    if (!success) try
                    {
                        Console.Error.WriteLine("payload-wrapper-adoption-failed; stage=" + stage + "; exception_type=" + failure +
                            "; elapsed_ms=" + ((Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency) +
                            "; lifetime_cancelled=" + lifetime.IsCancellationRequested + "; work_ms=" + work.ElapsedMilliseconds +
                            "; work_running=" + work.IsRunning + "; utc=" + DateTime.UtcNow.ToString("O"));
                    } catch { }
                    if (!success) Dispose(); if (pipe != null) pipe.Dispose();
                }
            }
            private bool Pull(byte[] packet, out byte[] nonce)
            {
                nonce = null;
                using (var stream = new MemoryStream(packet, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadInt32() != 0x4e505731) return false;
                    nonce = reader.ReadBytes(16);
                    if (new Guid(reader.ReadBytes(16)) != owner.RequestId || reader.ReadInt32() != owner.workerId ||
                        reader.ReadInt64() != owner.workerCreation) return false;
                    int size = reader.ReadInt32();
                    if (size != attemptBytes.Length || !Equal(attemptBytes, reader.ReadBytes(size))) return false;
                    session = reader.ReadString(); transfer = reader.ReadString();
                    if (String.IsNullOrEmpty(session) || String.IsNullOrEmpty(transfer)) return false;
                    Guid first = Guid.Empty;
                    for (int i = 0; i < 2; i++)
                    {
                        var request = new Guid(reader.ReadBytes(16)); int pid = reader.ReadInt32(); long creation = reader.ReadInt64();
                        string writer = reader.ReadString(); long last = reader.ReadInt64();
                        if (request == Guid.Empty || request == first || pid <= 0 || pid == owner.workerId ||
                            (i == 1 && pid == writerProcesses[0]) || creation <= 0 || String.IsNullOrEmpty(writer) || last <= 0) return false;
                        writerRequests[i] = request; writerProcesses[i] = pid; writerCreations[i] = creation; writerLastSequences[i] = last;
                        if (i == 0) { first = request; helpeeWriter = writer; } else helperWriter = writer;
                    }
                    if (helpeeWriter == helperWriter) return false;
                    for (int i = 0; i < 2; i++)
                    {
                        long raw = reader.ReadInt64(); identities[i] = reader.ReadString(); long count = reader.ReadInt64(); hashes[i] = reader.ReadString();
                        if (raw <= 0 || count != 268435456 || !System.Text.RegularExpressions.Regex.IsMatch(hashes[i], @"\A[0-9a-f]{64}\z")) return false;
                        AlterPayloadIdentityForTest(i);
                        if (!CanonicalAttemptDestination.DuplicateHandle(owner.workerExit.SafeWaitHandle, new IntPtr(raw),
                            CanonicalAttemptDestination.GetCurrentProcess(), out files[i], 0x80000000, false, 0)) return false;
                    }
                    return stream.Position == stream.Length && hashes[0] == hashes[1] && identities[0] != identities[1];
                }
            }
            public bool AdoptionFinished { get { return Object.ReferenceEquals(original, this) && adoption != null && adoption.IsCompleted; } }
            public bool ProvisionalCustody { get { lock (owner.frontierParentSync) lock (sync) return Object.ReferenceEquals(original, this) && !taken && committed && ScopeCurrent() && FilesCurrent(); } }
            public bool TryTakeAfterExit(FrontierGuiReports candidate)
            {
                lock (owner.frontierParentSync)
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(reports, candidate) || takeAttempted || retired) return false;
                    if (!ScopeCurrent()) { Dispose(); return false; }
                    // A pre-exit probe cannot consume the sole terminal take.
                    if (!owner.workerExit.WaitOne(0)) return false;
                    takeAttempted = true;
                    if (!ScopeCurrent() || !GuiSucceeded() || !AdoptionFinished || !adoption.GetAwaiter().GetResult() || !committed ||
                        candidate.Stdout.Status != TaskStatus.RanToCompletion || candidate.Stderr.Status != TaskStatus.RanToCompletion || !FilesCurrent())
                    { Dispose(); return false; }
                    taken = true; return true;
                }
            }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierParentSync)
                    lock (sync)
                    {
                        if (!Object.ReferenceEquals(original, this) || retired || !taken) return false;
                        if (ScopeCurrent() && GuiSucceeded() && FilesCurrent()) return true;
                        Dispose(); return false;
                    }
                }
            }
            public bool TryVerifyFiles()
            {
                lock (owner.frontierParentSync)
                lock (sync)
                {
                    if (!IsCurrent || verified) return false;
                    if (work.IsRunning || work.ElapsedMilliseconds > 5000) { Dispose(); return false; }
                    verified = true; work.Start(); bool success = false;
                    try { success = Hash(0) && Hash(1) && IsCurrent && work.ElapsedMilliseconds <= 5000; return success; }
                    finally { work.Stop(); if (!success) Dispose(); }
                }
            }
            private bool Hash(int slot)
            {
                IntPtr map = IntPtr.Zero, view = IntPtr.Zero;
                try
                {
                    map = CreateFileMapping(files[slot], IntPtr.Zero, 2, 0, 0, null); if (map == IntPtr.Zero) return false;
                    view = MapViewOfFile(map, 4, 0, 0, new UIntPtr(268435456)); if (view == IntPtr.Zero) return false;
                    using (var hash = SHA256.Create())
                    {
                        var buffer = new byte[65536];
                        for (long offset = 0; offset < 268435456; offset += buffer.Length)
                        {
                            if (work.ElapsedMilliseconds > 5000 || !IsCurrent) return false;
                            Marshal.Copy(new IntPtr(view.ToInt64() + offset), buffer, 0, buffer.Length);
                            hash.TransformBlock(buffer, 0, buffer.Length, buffer, 0);
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant() == hashes[slot];
                    }
                }
                finally { if (view != IntPtr.Zero) UnmapViewOfFile(view); if (map != IntPtr.Zero) CloseHandle(map); }
            }
            private bool FilesCurrent()
            { return PayloadFilesMatch(files, identities); }
            internal static bool PayloadFilesMatch(SafeFileHandle[] files, string[] identities)
            {
                for (int i = 0; i < 2; i++)
                {
                    Information info;
                    if (files[i] == null || files[i].IsClosed || files[i].IsInvalid || GetFileType(files[i]) != 1 ||
                        !GetFileInformationByHandle(files[i], out info) || (info.Attributes & 16) != 0 ||
                        (((long)info.SizeHigh << 32) | info.SizeLow) != 268435456 ||
                        info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") != identities[i]) return false;
                }
                return true;
            }
            public bool LiveProofEligible { get { return false; } }
            public bool TransferIntegrityEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this)) return;
                lock (sync)
                {
                    if (retired) return; retired = true; lifetime.Cancel();
                    if (pipe != null) pipe.Dispose();
                    if (cancelled != null) cancelled.Unregister(null); if (exited != null) exited.Unregister(null);
                    RetireWriterLifetime();
                    foreach (var file in files) if (file != null) file.Dispose();
                }
            }
            internal static bool Equal(byte[] first, byte[] second)
            { if (first == null || second == null || first.Length != second.Length) return false; for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false; return true; }
            private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
            {
                var bytes = new byte[count]; int offset = 0;
                while (offset < count) { int got = await stream.ReadAsync(bytes, offset, count - offset, token).ConfigureAwait(false); if (got == 0) throw new EndOfStreamException(); offset += got; }
                return bytes;
            }
            [StructLayout(LayoutKind.Sequential)] private struct Information
            {
                internal uint Attributes;
                internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
                internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            }
            [DllImport("kernel32.dll")] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
            [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle file);
            [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Information info);
            [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateFileMapping(SafeFileHandle file, IntPtr security, uint protection, uint high, uint low, string name);
            [DllImport("kernel32.dll")] private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint high, uint low, UIntPtr bytes);
            [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
            [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
