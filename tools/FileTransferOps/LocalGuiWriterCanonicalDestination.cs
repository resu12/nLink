using System;
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
        public sealed partial class CanonicalAttemptDispatch
        {
            private bool destinationAttempted;
            private CanonicalAttemptDestination destination;
            private CancellationTokenSource destinationWaitCancellation;
            private NamedPipeServerStream destinationWaitPipe;
            private Task<CanonicalAttemptDestination> destinationWaitTask;

            // Arm before Send without spending the handoff budget while the
            // transfer is running. The original GUI supplies its remaining
            // scenario budget; the receiver still has its own ten-second limit.
            public bool TryStartDestinationAdoption(FrontierAppRequest request, int remainingMilliseconds)
            {
                lock (rolesSync)
                {
                    string expectedHash = DestinationExpectedHash(request, source, CancellationToken.None);
                    if (remainingMilliseconds <= 0 || expectedHash == null || destinationAttempted) return false;
                    destinationAttempted = true;
                    try
                    {
                        destinationWaitCancellation = new CancellationTokenSource(remainingMilliseconds);
                        destinationWaitPipe = new NamedPipeServerStream("nlink-destination-" + request.RequestId.ToString("N"),
                            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        var heldSource = source;
                        var pipe = destinationWaitPipe;
                        var token = destinationWaitCancellation.Token;
                        destinationWaitTask = Task.Run(() => AdoptDestinationCore(request, heldSource,
                            268435456, expectedHash, token, pipe));
                        return true;
                    }
                    catch
                    {
                        if (destinationWaitPipe != null) destinationWaitPipe.Dispose();
                        if (destinationWaitCancellation != null) destinationWaitCancellation.Dispose();
                        return false;
                    }
                }
            }

            // Called before role shutdown. Failure cancels immediately; normal
            // completion waits only within the scenario and existing GUI cap.
            public CanonicalAttemptDestination TryCompleteDestinationAdoption(bool cancel)
            {
                if (!Object.ReferenceEquals(owner.attemptDispatch, this)) return null;
                Task<CanonicalAttemptDestination> pending;
                lock (rolesSync) { pending = destinationWaitTask; }
                if (pending == null) return null;
                try
                {
                    if (cancel || !IsCurrent) CancelDestinationAdoption();
                    if (!pending.Wait(75000)) CancelDestinationAdoption();
                    var result = pending.GetAwaiter().GetResult();
                    if (cancel && result != null) { result.Dispose(); return null; }
                    return result != null && result.IsCurrent ? result : null;
                }
                catch { return null; } // Optional custody cannot replace scenario cleanup.
                finally
                {
                    if (destinationWaitCancellation != null) destinationWaitCancellation.Dispose();
                }
            }

            private void CancelDestinationAdoption()
            {
                // Owner teardown can hold its lock: signal only here, never join.
                try { if (destinationWaitCancellation != null) destinationWaitCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            private string DestinationExpectedHash(FrontierAppRequest request,
                CanonicalAttemptSource expectedSource, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested || !IsCurrent || consumed != 1 || !SourceCurrent(expectedSource) ||
                    !expectedSource.SenderAttempted ||
                    !MatchesRoleLaunch("helper", request, helper.Reports, helper.Process)) return null;
                var digest = expectedSource.Held.ExpectedSha256;
                var hash = new byte[32];
                for (int i = 0; i < hash.Length; i++) hash[i] = Convert.ToByte(digest.Substring(i * 2, 2), 16);
                return Convert.ToBase64String(hash);
            }

            public CanonicalAttemptDestination TryAdoptDestination(FrontierAppRequest request,
                CanonicalAttemptSource expectedSource, CancellationToken cancellationToken)
            {
                string expectedHash;
                lock (rolesSync)
                {
                    expectedHash = DestinationExpectedHash(request, expectedSource, cancellationToken);
                    if (expectedHash == null) return null;
                }
                return AdoptDestinationCore(request, expectedSource, 268435456, expectedHash, cancellationToken);
            }

            // Only the public entry derives expectations from original source
            // custody. The private seam is also used by the small offline fixture.
            private CanonicalAttemptDestination AdoptDestinationCore(FrontierAppRequest request,
                CanonicalAttemptSource expectedSource, long expectedBytes, string expectedHash, CancellationToken cancellationToken,
                NamedPipeServerStream armedPipe = null)
            {
                lock (rolesSync)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        (armedPipe == null ? destinationAttempted : !Object.ReferenceEquals(armedPipe, destinationWaitPipe)) ||
                        (expectedSource != null && !SourceCurrent(expectedSource)) ||
                        !MatchesRoleLaunch("helper", request, helper.Reports, helper.Process))
                    {
                        if (armedPipe != null) armedPipe.Dispose();
                        return null;
                    }
                    destinationAttempted = true;
                }
                var candidate = new CanonicalAttemptDestination(this, expectedSource);
                bool accepted = false;
                try
                {
                    using (var pipe = armedPipe ?? new NamedPipeServerStream("nlink-destination-" + request.RequestId.ToString("N"),
                        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    // Framework pipe reads can ignore token cancellation after I/O starts.
                    using (deadline.Token.Register(pipe.Dispose))
                    {
                        if (armedPipe != null) pipe.WaitForConnectionAsync(cancellationToken).GetAwaiter().GetResult();
                        deadline.CancelAfter(75000);
                        var ct = deadline.Token;
                        if (armedPipe == null) pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                        uint pid;
                        if (!CanonicalAttemptDestination.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) ||
                            pid != request.launch.AppProcessId ||
                            !MatchesRoleLaunch("helper", request, helper.Reports, helper.Process)) return null;
                        int length = BitConverter.ToInt32(CanonicalAttemptDestination.Read(pipe, 4, ct), 0);
                        if (length < 80 || length > 4096) return null;
                        var packet = CanonicalAttemptDestination.Read(pipe, length, ct);
                        byte[] nonce;
                        using (var body = new MemoryStream(packet, false))
                        using (var reader = new BinaryReader(body, new UTF8Encoding(false, true)))
                        {
                            if (reader.ReadInt32() != 0x4e444731) return null;
                            nonce = reader.ReadBytes(16);
                            if (new Guid(reader.ReadBytes(16)) != request.RequestId || reader.ReadInt32() != request.launch.AppProcessId ||
                                reader.ReadInt64() != request.launch.AppProcessCreationFileTime) return null;
                            long raw = reader.ReadInt64();
                            candidate.identity = reader.ReadString(); candidate.bytes = reader.ReadInt64();
                            candidate.session = reader.ReadString(); candidate.transfer = reader.ReadString();
                            candidate.hash = Convert.ToBase64String(Convert.FromBase64String(reader.ReadString()));
                            if (body.Position != body.Length || raw <= 0 || candidate.bytes != expectedBytes ||
                                candidate.hash != expectedHash || String.IsNullOrEmpty(candidate.session) || String.IsNullOrEmpty(candidate.transfer)) return null;
                            InspectDestinationOffer(candidate);
                            if (!CanonicalAttemptDestination.DuplicateHandle(request.launch.Exit.SafeWaitHandle, new IntPtr(raw),
                                CanonicalAttemptDestination.GetCurrentProcess(), out candidate.file, 0x80000000, false, 0)) return null;
                        }
                        if (!candidate.MatchesFile() || !MatchesRoleLaunch("helper", request, helper.Reports, helper.Process)) return null;
                        if (!candidate.VerifyHash(ct)) return null;
                        if (ct.IsCancellationRequested || !candidate.MatchesSource() || !candidate.MatchesFile() ||
                            !MatchesRoleLaunch("helper", request, helper.Reports, helper.Process)) return null;
                        var acknowledgment = (byte[])nonce.Clone();
                        bool cancel = false;
                        PrepareDestinationAcknowledgment(acknowledgment, ref cancel);
                        if (cancel) return null;
                        pipe.WriteAsync(acknowledgment, 0, acknowledgment.Length, ct).GetAwaiter().GetResult();
                        var commit = CanonicalAttemptDestination.Read(pipe, nonce.Length, ct);
                        for (int i = 0; i < nonce.Length; i++) if (nonce[i] != commit[i]) return null;
                        lock (rolesSync)
                        {
                            if (ct.IsCancellationRequested || !candidate.MatchesSource() || !candidate.MatchesFile() ||
                                !MatchesRoleLaunch("helper", request, helper.Reports, helper.Process)) return null;
                            destination = candidate; candidate.committed = true; accepted = true; return candidate;
                        }
                    }
                }
                catch (IOException) { return null; }
                catch (OperationCanceledException) { return null; }
                catch (ArgumentException) { return null; }
                catch (FormatException) { return null; }
                catch (ObjectDisposedException) { return null; }
                finally { if (!accepted) candidate.Dispose(); }
            }

            internal bool DestinationCurrent(CanonicalAttemptDestination candidate)
            {
                lock (rolesSync)
                {
                    if (!Object.ReferenceEquals(destination, candidate) || !candidate.IsOriginal || candidate.retired) return false;
                    bool current = IsCurrent && candidate.committed && candidate.MatchesFile() && candidate.MatchesSource();
                    if (current) ValidateDestinationAssociation(candidate, ref current);
                    if (current)
                        current = helper.Process.HasExited ? helper.Process.ExitCode == 0 && owner.AllAppRequestsCompletedSuccessfully &&
                            owner.AllAppOutputsDrainedSuccessfully : MatchesRoleLaunch("helper", helper.Request, helper.Reports, helper.Process);
                    if (!current) candidate.Dispose();
                    return current;
                }
            }

            partial void InspectDestinationOffer(CanonicalAttemptDestination candidate);
            partial void PrepareDestinationAcknowledgment(byte[] acknowledgment, ref bool cancel);
            partial void ValidateDestinationAssociation(CanonicalAttemptDestination candidate, ref bool current);
        }

        // Provisional GUI custody only. Parent/analyzer adoption is not supplied.
        public sealed partial class CanonicalAttemptDestination : IDisposable
        {
            private readonly CanonicalAttemptDestination original;
            private readonly CanonicalAttemptDispatch owner;
            internal SafeFileHandle file;
            internal string identity, session, transfer, hash;
            internal long bytes;
            internal bool retired, committed;
            private readonly CanonicalAttemptSource source;
            internal CanonicalAttemptDestination(CanonicalAttemptDispatch owner, CanonicalAttemptSource source)
            { original = this; this.owner = owner; this.source = source; }
            internal bool MatchesSource() { return source == null || owner.SourceCurrent(source); }
            internal bool IsOriginal { get { return Object.ReferenceEquals(original, this); } }
            public bool IsCurrent { get { return IsOriginal && owner.DestinationCurrent(this); } }
            public long ByteCount { get { return IsCurrent ? bytes : 0; } }
            public string Sha256Base64 { get { return IsCurrent ? hash : null; } }
            public string FileIdentity { get { return IsCurrent ? identity : null; } }
            public bool LiveProofEligible { get { return false; } }
            public bool TransferIntegrityEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal bool MatchesFile()
            {
                lock (original)
                {
                    Information info;
                    return !retired && file != null && !file.IsClosed && !file.IsInvalid && GetFileType(file) == 1 &&
                        GetFileInformationByHandle(file, out info) && (((long)info.SizeHigh << 32) | info.SizeLow) == bytes &&
                        info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") == identity;
                }
            }
            internal bool VerifyHash(CancellationToken token)
            {
                IntPtr map = IntPtr.Zero, view = IntPtr.Zero;
                try
                {
                    if (bytes <= 0 || bytes > 268435456 || !MatchesFile()) return false;
                    map = CreateFileMapping(file, IntPtr.Zero, 2, 0, 0, null);
                    if (map == IntPtr.Zero) return false;
                    view = MapViewOfFile(map, 4, 0, 0, new UIntPtr((ulong)bytes));
                    if (view == IntPtr.Zero) return false;
                    using (var sha = SHA256.Create())
                    {
                        var buffer = new byte[65536];
                        for (long offset = 0; offset < bytes; offset += buffer.Length)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!owner.IsCurrent || !MatchesFile()) return false;
                            int count = (int)Math.Min(buffer.Length, bytes - offset);
                            Marshal.Copy(new IntPtr(view.ToInt64() + offset), buffer, 0, count);
                            sha.TransformBlock(buffer, 0, count, buffer, 0);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        return MatchesFile() && Convert.ToBase64String(sha.Hash) == hash;
                    }
                }
                finally { if (view != IntPtr.Zero) UnmapViewOfFile(view); if (map != IntPtr.Zero) CloseHandle(map); }
            }
            public void Dispose()
            {
                if (!IsOriginal) return;
                lock (original) { if (retired) return; retired = true; if (file != null) file.Dispose(); }
            }
            internal static byte[] Read(Stream stream, int length, CancellationToken token)
            {
                var bytes = new byte[length]; int offset = 0;
                while (offset < length)
                {
                    int read = stream.ReadAsync(bytes, offset, length - offset, token).GetAwaiter().GetResult();
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                return bytes;
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct Information
            {
                internal uint Attributes;
                internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
                internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            }
            [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll")] internal static extern bool DuplicateHandle(SafeWaitHandle process, IntPtr source,
                IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
            [DllImport("kernel32.dll")] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
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
