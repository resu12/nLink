#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NLink.Tests;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CollectorOutputs
        {
            private LocalInputReadLease sourceLease;
            private object sourceRequestOwner;
            private int sourceHashAttempted;

            // Replace the ordinary parent source hash, preserving its real hash
            // pass. HashFrontier supplies native I/O only, not frontier authority.
            public LocalInputReadLease HashSourceForTest(Completion completion)
            {
                if (!OwnsCompletion(completion) || Interlocked.Exchange(ref sourceHashAttempted, 1) != 0) return null;
                var clock = Stopwatch.StartNew();
                var result = LocalInputReadLease.HashFrontier(files[3].SafeFileHandle, completion.Outputs[3],
                    delegate { return OwnsCompletion(completion) && clock.ElapsedMilliseconds <= 5000; });
                sourceLease = result == null ? null : result.Lease;
                return sourceLease;
            }

            internal bool OwnsSourceLease(Completion completion, LocalInputReadLease lease)
            {
                return OwnsCompletion(completion) && lease != null && Object.ReferenceEquals(sourceLease, lease) &&
                    lease.IsVerified && lease.FileIdentity == metadata[3].FileIdentity && lease.ByteCount == metadata[3].ByteCount;
            }

            internal bool ClaimSourceRequest(object owner, Completion completion, LocalInputReadLease lease)
            {
                return owner != null && OwnsSourceLease(completion, lease) &&
                    Interlocked.CompareExchange(ref sourceRequestOwner, owner, null) == null;
            }

            internal bool OwnedBySourceRequest(object owner, Completion completion, LocalInputReadLease lease)
            { return Object.ReferenceEquals(sourceRequestOwner, owner) && OwnsSourceLease(completion, lease); }

            internal void SendPrimaryOutputs(object owner, Process process, Guid request, byte[] descriptorHash, string mutation)
            {
                if (!Object.ReferenceEquals(sourceRequestOwner, owner) || !OwnsSourceLease(accepted, sourceLease))
                    throw new InvalidDataException("collector_primary_parent_owner");
                using (var packet = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(packet, new UTF8Encoding(false, true), true))
                    {
                        writer.Write(0x544f5031); writer.Write(request.ToByteArray());
                        writer.Write(process.Id); writer.Write(process.StartTime.ToUniversalTime().ToFileTimeUtc());
                        writer.Write(descriptorHash);
                        for (int i = 0; i < 4; i++) writer.Write(HeldFileNativeParent.DuplicateForChild(process, files[i].SafeFileHandle));
                        for (int i = 0; i < 4; i++)
                        {
                            var info = metadata[i];
                            writer.Write(info.CreationPath);
                            writer.Write(mutation == "collector-identity" && i == 3 ? "foreign" : info.FileIdentity);
                            writer.Write(info.Sha256); writer.Write(info.ByteCount); writer.Write(info.LineCount);
                        }
                        origins.Write(writer, mutation);
                    }
                    byte[] body = packet.ToArray();
                    if (body.Length > 16384) throw new InvalidDataException("collector_primary_packet_size");
                    using (var writer = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, true))
                    { writer.Write(body.Length); writer.Write(body, 0, mutation == "collector-truncated" ? body.Length / 2 : body.Length); writer.Flush(); }
                    if (mutation == "collector-truncated") process.StandardInput.Close();
                }
            }
        }
    }

    public sealed partial class LocalInputReadLease
    {
        // Negative-control leases only: duplicate the original native object,
        // not its pathname, without another hash pass or an ownership claim.
        public LocalInputReadLease DuplicateCollectorControlLeaseForTest(bool verified)
        {
            if (!Object.ReferenceEquals(frontierOriginalLease, this) || !frontierNative || !IsVerified) return null;
            var handle = DuplicateFrontierRead(frontierHashHandle);
            if (handle == null) return null;
            FileStream stream = null;
            try
            {
                stream = new FileStream(handle, FileAccess.Read); handle = null;
                var result = new LocalInputReadLease(stream, Path, ExpectedSha256, expectedLines, verified);
                stream = null; result.frontierNative = true; result.frontierOriginalLease = result;
                result.frontierHashHandle = result.anchor.SafeFileHandle; return result;
            }
            finally { if (handle != null) handle.Dispose(); if (stream != null) stream.Dispose(); }
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class TerminalFilesForTest
        {
            private LocalGuiWriterRequest.CollectorOutputs collectorOutputs;
            public bool BindCollectorOutputsForTest(LocalGuiWriterRequest.CollectorOutputs outputs)
            {
                if (!IsCurrent || sent != 0 || collectorOutputs != null || outputs == null ||
                    outputs.origins == null || !outputs.origins.MatchesProducer(files.Describe()[0]) ||
                    !outputs.ClaimSourceRequest(this, writer, lease)) return false;
                collectorOutputs = outputs; return true;
            }
            public bool OwnsCollectorOutputsForTest(LocalGuiWriterRequest.CollectorOutputs outputs,
                LocalRetainedSourceRequest candidate, LocalGuiWriterRequest.Completion completion, LocalInputReadLease source)
            {
                return Object.ReferenceEquals(collectorOutputs, outputs) && outputs != null &&
                    Owns(candidate, files, completion, source) && outputs.OwnedBySourceRequest(this, writer, lease);
            }
            private void ReleaseCollectorOutputsAfterAcknowledgement()
            {
                if (collectorOutputs == null) return;
                // All parent duplicates of the semantic source must retire too.
                // The request channel and held worker lifetime stay intact.
                collectorOutputs.Dispose(); lease.Dispose(); request.frozenSource.Dispose();
                foreach (var output in writer.Outputs)
                {
                    bool denied = false;
                    try { using (File.Open(output.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
                    catch (IOException error) { if ((error.HResult & 0xffff) != 32) throw; denied = true; }
                    if (!denied) throw new InvalidDataException("collector_primary_unprotected_after_parent_release");
                }
            }
        }

        public sealed partial class Worker
        {
            private CollectorPrimaryFilesForTest collectorPrimaryFiles;
            public CollectorPrimaryFilesForTest CollectorOutputsForTest
            { get { return collectorPrimaryFiles != null && collectorPrimaryFiles.IsCurrent ? collectorPrimaryFiles : null; } }

            private void ReceiveCollectorOutputs(byte[] descriptorHash, HeldFileNativeParent producerFiles)
            {
                if (!IsCurrent || collectorPrimaryFiles != null) throw new InvalidDataException("collector_primary_worker_state");
                var files = new CollectorPrimaryFilesForTest(this);
                bool success = false;
                try
                {
                    var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8, true);
                    int length = input.ReadInt32();
                    if (length < 1 || length > 16384) throw new InvalidDataException("collector_primary_packet_size");
                    var body = input.ReadBytes(length);
                    if (body.Length != length) throw new InvalidDataException("collector_primary_packet_truncated");
                    using (var bytes = new MemoryStream(body, false))
                    using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                    {
                        if (reader.ReadInt32() != 0x544f5031 || new Guid(reader.ReadBytes(16)) != RequestId ||
                            reader.ReadInt32() != ProcessId || reader.ReadInt64() != ProcessCreationFileTime ||
                            Convert.ToBase64String(reader.ReadBytes(32)) != Convert.ToBase64String(descriptorHash))
                            throw new InvalidDataException("collector_primary_request");
                        var handles = new HashSet<long>();
                        for (int i = 0; i < 4; i++)
                        {
                            long raw = reader.ReadInt64();
                            if (raw <= 0 || !handles.Add(raw)) throw new InvalidDataException("collector_primary_handle");
                            var handle = new SafeFileHandle(new IntPtr(raw), true);
                            try { files.files.Add(new FileStream(handle, FileAccess.Read)); }
                            catch { handle.Dispose(); throw; }
                        }
                        var identities = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < 4; i++)
                        {
                            string path = reader.ReadString(), identity = reader.ReadString(), hash = reader.ReadString();
                            long count = reader.ReadInt64(), lines = reader.ReadInt64();
                            var expected = WriterContext.Outputs[i];
                            if (path != expected.Path || hash != expected.Sha256 || count != expected.ByteCount || lines != expected.LineCount ||
                                !identities.Add(identity) || identity != (i == 3 ? SourceIdentity : expected.OriginalFileIdentity))
                                throw new InvalidDataException("collector_primary_output_identity");
                            var info = GuiSmokeLedgerCapturedOutput.Capture(files.files[i], path, hash, lines, count);
                            if (info == null || info.FileIdentity != identity || !info.Matches(files.files[i]))
                                throw new InvalidDataException("collector_primary_native_identity");
                            files.metadata.Add(info);
                        }
                        files.origins = TerminalCollectorOrigins.Read(reader, files.metadata);
#if NLINK_TEST_SELECTED_NATIVE_WRITER
                        if (!(producerFiles != null ? producerFiles.MatchesPendingCollectorOrigins(files.origins) :
                            seededProducer != null && files.origins.MatchesProducer(seededProducer)))
#else
                        if (!producerFiles.MatchesPendingCollectorOrigins(files.origins))
#endif
                            throw new InvalidDataException("collector_origins_producer");
                        if (bytes.Position != bytes.Length || !files.IsCurrent) throw new InvalidDataException("collector_primary_packet_tail");
                    }
                    collectorPrimaryFiles = files; success = true;
                }
                finally { if (!success) files.Dispose(); }
            }

            // Replace only the ordinary path open. Keep the original worker's
            // one-shot SourceReader and all downstream custody/evaluation code.
            public SourceReader TryOpenCollectorSourceForTest()
            {
                if (!Object.ReferenceEquals(frontierOriginal, this) || !IsCurrent || collectorPrimaryFiles == null ||
                    !collectorPrimaryFiles.IsCurrent || Interlocked.Exchange(ref opened, 1) != 0) return null;
                SafeFileHandle handle = null; FileStream stream = null;
                try
                {
                    if (!DuplicateCollectorSource(GetCurrentProcess(), collectorPrimaryFiles.files[3].SafeFileHandle,
                        GetCurrentProcess(), out handle, 0x80000000, false, 0)) return null;
                    stream = new FileStream(handle, FileAccess.Read); handle = null; stream.Position = 0;
                    if (stream.Length != SourceBytes || FileIdentity(stream.SafeFileHandle) != SourceIdentity || !collectorPrimaryFiles.IsCurrent) return null;
                    sourceReader = new SourceReader(this, stream); stream = null; return sourceReader;
                }
                finally { if (handle != null) handle.Dispose(); if (stream != null) stream.Dispose(); }
            }
        }

        public sealed partial class CollectorPrimaryFilesForTest : IDisposable
        {
            private readonly Worker owner;
            private readonly CollectorPrimaryFilesForTest original;
            internal readonly List<FileStream> files = new List<FileStream>();
            internal readonly List<GuiSmokeLedgerCapturedOutput> metadata = new List<GuiSmokeLedgerCapturedOutput>();
            private bool disposed;
            internal CollectorPrimaryFilesForTest(Worker owner) { this.owner = owner; original = this; }
            public bool IsCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(original, this) || disposed || !owner.IsCurrent || files.Count != 4 || metadata.Count != 4) return false;
                    try { for (int i = 0; i < 4; i++) if (!files[i].CanRead || files[i].CanWrite || !metadata[i].Matches(files[i])) return false; }
                    catch { return false; }
                    return true;
                }
            }
            public bool OwnsPrimary(Worker candidate, SourceReader source, PrimaryEvaluationCustody custody,
                CompletedEvaluations pair, object context, string[] full, string[] compact, object fullView, object compactView)
            {
#if NLINK_TEST_SELECTED_NATIVE_WRITER
                if (Object.ReferenceEquals(owner, candidate) && IsCurrent &&
                    owner.OwnsSeededCollectorPrimary(this, source, custody, pair, context, full, compact, fullView, compactView)) return true;
#endif
                return Object.ReferenceEquals(owner, candidate) && IsCurrent && owner.TerminalFilesForTest != null &&
                    owner.TerminalFilesForTest.OwnsPrimary(candidate, source, custody, pair, context, full, compact, fullView, compactView);
            }
            public void Dispose()
            { if (!Object.ReferenceEquals(original, this) || disposed) return; disposed = true; foreach (var file in files) file.Dispose(); }
        }

        [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
        private static extern bool DuplicateCollectorSource(IntPtr process, SafeFileHandle source, IntPtr target,
            out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }
}
#endif
