#if NLINK_TEST_SELECTED_NATIVE_WRITER && NLINK_TEST_TERMINAL_REQUEST && NLINK_TEST_MEMBERSHIP_BORROWER
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NLink.Tests;

namespace NLink.Tests
{
    public sealed partial class HeldFileNativeParent
    {
        // A separate log-only fixture owner. It cannot supply a transfer binding,
        // source/destination payloads, terminal integrity or a terminal owner.
        public sealed class SeededCollectorLogForTest : IDisposable
        {
            private readonly SeededCollectorLogForTest original;
            private readonly FileStream file;
            internal readonly Measurement Log;
            internal readonly string Run, Scenario;
            private object collector;
            private int disposed;
            private SeededCollectorLogForTest(FileStream file, string path, string run, string scenario)
            {
                original = this; this.file = file; Run = run; Scenario = scenario;
                Information info;
                if (GetFileType(file.SafeFileHandle) != 1 || !GetFileInformationByHandle(file.SafeFileHandle, out info))
                    throw new InvalidDataException("seeded_log_identity");
                Log = new Measurement("log", path, Identity(info), file.Length, null);
            }
            public static SeededCollectorLogForTest Open(string path, string run, string scenario)
            {
                if (String.IsNullOrWhiteSpace(run) || String.IsNullOrWhiteSpace(scenario)) return null;
                FileStream file = null;
                try
                {
                    path = Path.GetFullPath(path);
                    file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length < 1 || file.Length > 4 * 1024 * 1024) return null;
                    var result = new SeededCollectorLogForTest(file, path, run, scenario);
                    file = null; return result;
                }
                finally { if (file != null) file.Dispose(); }
            }
            public bool IsCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(original, this) || disposed != 0) return false;
                    try
                    {
                        Information info;
                        return file.CanRead && !file.CanWrite && GetFileInformationByHandle(file.SafeFileHandle, out info) &&
                            Identity(info) == Log.Identity && (((long)info.SizeHigh << 32) | info.SizeLow) == Log.Bytes;
                    }
                    catch { return false; }
                }
            }
            internal void Send(object owner, Process child, Guid request)
            {
                if (!IsCurrent || Interlocked.CompareExchange(ref collector, owner, null) != null)
                    throw new InvalidDataException("seeded_log_collector_owner");
                using (var packet = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(packet, Encoding.UTF8, true))
                    {
                        writer.Write(CollectorMagic); writer.Write(request.ToByteArray());
                        writer.Write(child.Id); writer.Write(child.StartTime.ToUniversalTime().ToFileTimeUtc());
                        WriteText(writer, Run); WriteText(writer, Scenario);
                        writer.Write(DuplicateForChild(child, file.SafeFileHandle));
                        WriteText(writer, Log.Path); WriteText(writer, Log.Identity); writer.Write(Log.Bytes);
                    }
                    byte[] body = packet.ToArray();
                    using (var writer = new BinaryWriter(child.StandardInput.BaseStream, Encoding.UTF8, true))
                    { writer.Write(body.Length); writer.Write(body); writer.Flush(); }
                    child.StandardInput.Close();
                }
            }
            public void Dispose()
            { if (Object.ReferenceEquals(original, this) && Interlocked.Exchange(ref disposed, 1) == 0) file.Dispose(); }
        }
    }
}

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public bool SendTerminalCollectorLogForTest(HeldFileNativeParent.SeededCollectorLogForTest files, Process process, string mutation)
        {
            if (!Object.ReferenceEquals(original, this) || files == null || !files.IsCurrent || mutation != "" ||
                files.Run != run || files.Scenario != scenario || Volatile.Read(ref sealedState) != 0 ||
                closed == null || closed.WaitOne(0) || workerExit == null || workerExit.WaitOne(0) ||
                process == null || process.HasExited || process.Id != workerId ||
                process.StartTime.ToUniversalTime().ToFileTimeUtc() != workerCreation ||
                Interlocked.Exchange(ref collectorSent, 1) != 0) return false;
            files.Send(this, process, RequestId); return true;
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        private SeededCollectorBindingForTest seededCollectorBinding;
        private int seededBindingAttempted;
        public SeededCollectorBindingForTest TryBindSeededCollectorForTest(HeldFileNativeParent.SeededCollectorLogForTest log,
            LocalGuiWriterRequest.CollectorOutputs outputs, LocalGuiWriterRequest.Completion writer, LocalInputReadLease lease)
        {
            if (!Object.ReferenceEquals(frontierOriginal, this) || Interlocked.Exchange(ref seededBindingAttempted, 1) != 0) return null;
            if (log == null || !log.IsCurrent || outputs == null || writer == null || !writer.WasAcceptedHere() ||
                !FrontierScopeCurrent(writer, true) || !outputs.OwnsSourceLease(writer, lease) ||
                !outputs.origins.MatchesProducer(log.Log) || !writer.MatchesSource(log.Run, log.Scenario, lease.Path,
                    lease.ExpectedSha256, lease.ByteCount, writer.Outputs[3].LineCount) ||
                FileIdentity(frozenSource.SafeFileHandle) != lease.FileIdentity || frozenSource.Length != lease.ByteCount) return null;
            var binding = new SeededCollectorBindingForTest(this, log, outputs, writer, lease);
            if (!outputs.ClaimSourceRequest(binding, writer, lease)) return null;
            seededCollectorBinding = binding; return binding;
        }

        public sealed class SeededCollectorBindingForTest
        {
            private readonly LocalRetainedSourceRequest request;
            private readonly HeldFileNativeParent.SeededCollectorLogForTest log;
            private readonly LocalGuiWriterRequest.CollectorOutputs outputs;
            private readonly LocalGuiWriterRequest.Completion writer;
            private readonly LocalInputReadLease lease;
            private int sent, acknowledged;
            internal SeededCollectorBindingForTest(LocalRetainedSourceRequest request, HeldFileNativeParent.SeededCollectorLogForTest log,
                LocalGuiWriterRequest.CollectorOutputs outputs, LocalGuiWriterRequest.Completion writer, LocalInputReadLease lease)
            { this.request = request; this.log = log; this.outputs = outputs; this.writer = writer; this.lease = lease; }
            public bool IsCurrent
            {
                get { return Object.ReferenceEquals(request.seededCollectorBinding, this) && log.IsCurrent &&
                    request.FrontierScopeCurrent(writer, false) && outputs.OwnedBySourceRequest(this, writer, lease); }
            }
            private bool OwnsWorker(Process process)
            {
                return IsCurrent && process != null && !process.HasExited && request.workerExit != null &&
                    process.Id == request.workerId && process.StartTime.ToUniversalTime().ToFileTimeUtc() == request.workerCreation;
            }
            public bool SendToPrimaryForTest(Process process)
            {
                if (!OwnsWorker(process) || Interlocked.Exchange(ref sent, 1) != 0) return false;
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(request.frontierBody);
                using (var packet = new MemoryStream())
                {
                    using (var header = new BinaryWriter(packet, Encoding.UTF8, true))
                    {
                        header.Write(0x534e5731); header.Write(request.RequestId.ToByteArray());
                        header.Write(process.Id); header.Write(process.StartTime.ToUniversalTime().ToFileTimeUtc());
                        header.Write(hash); header.Write(log.Log.Identity); header.Write(log.Log.Bytes);
                    }
                    using (var target = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, true))
                    { byte[] body = packet.ToArray(); target.Write(body.Length); target.Write(body); target.Flush(); }
                }
                outputs.SendPrimaryOutputs(this, process, request.RequestId, hash, ""); return true;
            }
            public bool ReleaseAfterAcknowledgementForTest(Process process, string acknowledgement)
            {
                if (!OwnsWorker(process) || sent != 1 || acknowledgement != "nlink-seeded-primary-adopted:" + request.RequestId.ToString("D") ||
                    Interlocked.Exchange(ref acknowledged, 1) != 0) return false;
                outputs.Dispose(); lease.Dispose(); request.frozenSource.Dispose();
                foreach (var output in writer.Outputs)
                {
                    bool denied = false;
                    try { using (File.Open(output.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
                    catch (IOException error) { if ((error.HResult & 0xffff) != 32) throw; denied = true; }
                    if (!denied) throw new InvalidDataException("seeded_primary_unprotected_after_parent_release");
                }
                byte[] release = request.RequestId.ToByteArray();
                process.StandardInput.BaseStream.Write(release, 0, release.Length);
                process.StandardInput.BaseStream.Flush(); process.StandardInput.Close(); return true;
            }
        }

        public sealed partial class Worker
        {
            private HeldFileNativeParent.Measurement seededProducer;
            private int seededReceiveAttempted;
            private bool seededAdopted;
            public void ReceiveSeededCollectorForTest()
            {
                if (!Object.ReferenceEquals(frontierOriginal, this) || !IsCurrent || frontierRequested ||
                    !Console.IsInputRedirected || Interlocked.Exchange(ref seededReceiveAttempted, 1) != 0)
                    throw new InvalidDataException("seeded_primary_receive_state");
                bool success = false;
                try
                {
                    var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8, true);
                    int length = input.ReadInt32();
                    if (length < 1 || length > 16384) throw new InvalidDataException("seeded_primary_header_size");
                    byte[] header = input.ReadBytes(length), hash;
                    using (var body = new MemoryStream())
                    {
                        using (var writer = new BinaryWriter(body, new UTF8Encoding(false, true), true))
                        {
                            writer.Write(Owner); writer.Write(AcceptanceRunId); writer.Write(ScenarioRunId); writer.Write(SourcePath);
                            writer.Write(SourceSha256); writer.Write(SourceIdentity); writer.Write(SourceBytes); writer.Write(SourceLines);
                            if (WriterContext == null) throw new InvalidDataException("seeded_primary_writer_missing");
                            WriterContext.WriteTo(writer);
                        }
                        if (body.Length > MaximumWriterRequestBytes) throw new InvalidDataException("seeded_primary_descriptor_size");
                        using (var sha = SHA256.Create()) hash = sha.ComputeHash(body.ToArray());
                    }
                    using (var body = new MemoryStream(header, false))
                    using (var reader = new BinaryReader(body, new UTF8Encoding(false, true)))
                    {
                        if (header.Length != length || reader.ReadInt32() != 0x534e5731 || new Guid(reader.ReadBytes(16)) != RequestId ||
                            reader.ReadInt32() != ProcessId || reader.ReadInt64() != ProcessCreationFileTime ||
                            Convert.ToBase64String(reader.ReadBytes(32)) != Convert.ToBase64String(hash))
                            throw new InvalidDataException("seeded_primary_request");
                        string identity = reader.ReadString(); long bytes = reader.ReadInt64();
                        if (identity.Length < 1 || identity.Length > 128 || bytes < 1 || bytes > 4 * 1024 * 1024 || body.Position != body.Length)
                            throw new InvalidDataException("seeded_primary_producer");
                        seededProducer = new HeldFileNativeParent.Measurement("log", null, identity, bytes, null);
                    }
                    ReceiveCollectorOutputs(hash, null);
                    Console.Out.WriteLine("nlink-seeded-primary-adopted:" + RequestId.ToString("D")); Console.Out.Flush();
                    byte[] release = input.ReadBytes(16);
                    if (release.Length != 16 || new Guid(release) != RequestId || !IsCurrent || !collectorPrimaryFiles.IsCurrent)
                        throw new InvalidDataException("seeded_primary_release");
                    seededAdopted = true; success = true;
                }
                finally { if (!success && collectorPrimaryFiles != null) collectorPrimaryFiles.Dispose(); }
            }
            internal bool OwnsSeededCollectorPrimary(CollectorPrimaryFilesForTest files, SourceReader source,
                PrimaryEvaluationCustody custody, CompletedEvaluations pair, object context, string[] full, string[] compact,
                object fullView, object compactView)
            {
                return Object.ReferenceEquals(frontierOriginal, this) && seededAdopted &&
                    Object.ReferenceEquals(collectorPrimaryFiles, files) && files.IsCurrent && OwnsCompletedSource(source) &&
                    Object.ReferenceEquals(primary, custody) && custody != null &&
                    custody.Owns(pair, context, full, compact, fullView, compactView);
            }
        }
    }
}
#endif
