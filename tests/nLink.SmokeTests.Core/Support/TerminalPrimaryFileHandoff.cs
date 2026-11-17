#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NLink.Tests;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class TerminalFilesForTest
        {
            private int sent, acknowledged;
            public bool WorkerAdoptionAcknowledged { get { return Volatile.Read(ref acknowledged) == 1; } }

            private bool OwnsWorker(Process process)
            {
                return IsCurrent && process != null && !process.HasExited && request.workerExit != null &&
                    request.workerId == process.Id && request.workerCreation == process.StartTime.ToUniversalTime().ToFileTimeUtc();
            }

            public bool SendToPrimaryForTest(Process process, string mutation)
            {
                if (!OwnsWorker(process) || Interlocked.Exchange(ref sent, 1) != 0) return false;
                // Request descriptor hashing is bounded metadata, not another
                // whole-file hash pass or an authority evaluation.
                files.SendToClaimedWorker(request, process, request.RequestId, mutation.StartsWith("collector-", StringComparison.Ordinal) ? "" : mutation);
                if (mutation == "truncated") { process.StandardInput.Close(); return true; }
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(request.frontierBody);
                if (mutation == "descriptor") hash[0] ^= 1;
                process.StandardInput.BaseStream.Write(hash, 0, hash.Length);
                process.StandardInput.BaseStream.Flush();
                if (collectorOutputs != null) collectorOutputs.SendPrimaryOutputs(this, process, request.RequestId, hash, mutation);
                return true;
            }

            public bool ReleaseAfterPrimaryAcknowledgementForTest(Process process, string acknowledgement)
            {
                if (!OwnsWorker(process) || sent != 1 ||
                    acknowledgement != "nlink-native-primary-adopted:" + request.RequestId.ToString("D") ||
                    Interlocked.Exchange(ref acknowledged, 1) != 0) return false;
                var descriptions = files.Describe();
                files.Dispose();
                if (files.IsCurrent || IsCurrent) throw new InvalidDataException("native_primary_parent_not_retired");
                // The producer and parent now hold none of these three files.
                // Only the actual primary's adopted handles exclude writes.
                foreach (var file in descriptions)
                {
                    bool denied = false;
                    try { using (File.Open(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
                    catch (IOException error)
                    {
                        // A missing path or other I/O failure is not protection.
                        if ((error.HResult & 0xffff) != 32) throw;
                        denied = true;
                    }
                    if (!denied) throw new InvalidDataException("native_primary_file_unprotected");
                }
                ReleaseCollectorOutputsAfterAcknowledgement();
                byte[] release = request.RequestId.ToByteArray();
                process.StandardInput.BaseStream.Write(release, 0, release.Length);
                process.StandardInput.BaseStream.Flush(); process.StandardInput.Close();
                return true;
            }
        }

        public sealed partial class Worker
        {
            private int terminalReceiveAttempted;
            private TerminalWorkerFilesForTest terminalWorkerFiles;
            public TerminalWorkerFilesForTest TerminalFilesForTest
            { get { return terminalWorkerFiles != null && terminalWorkerFiles.IsCurrent ? terminalWorkerFiles : null; } }

            // Called on the ORIGINAL source worker, before its one source read.
            // No detached worker or JSON descriptor is accepted by this entry.
            public void ReceiveTerminalFilesForTest(string originalLogPath, bool receiveCollectorOutputs = false)
            {
                if (!IsCurrent || Interlocked.Exchange(ref terminalReceiveAttempted, 1) != 0)
                    throw new InvalidDataException("native_primary_receive_repeated_or_stale");
                HeldFileNativeParent files = null;
                try
                {
                    files = HeldFileNativeParent.Receive(RequestId.ToString("D"), AcceptanceRunId, ScenarioRunId, originalLogPath);
                    var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8, true);
                    byte[] declared = input.ReadBytes(32), actual;
                    using (var body = new MemoryStream())
                    {
                        using (var writer = new BinaryWriter(body, new UTF8Encoding(false, true), true))
                        {
                            writer.Write(Owner); writer.Write(AcceptanceRunId); writer.Write(ScenarioRunId); writer.Write(SourcePath);
                            writer.Write(SourceSha256); writer.Write(SourceIdentity); writer.Write(SourceBytes); writer.Write(SourceLines);
                            if (WriterContext == null) throw new InvalidDataException("native_primary_writer_missing");
                            WriterContext.WriteTo(writer);
                        }
                        if (body.Length > MaximumWriterRequestBytes) throw new InvalidDataException("native_primary_descriptor_size");
                        using (var sha = SHA256.Create()) actual = sha.ComputeHash(body.ToArray());
                    }
                    if (declared.Length != actual.Length || Convert.ToBase64String(declared) != Convert.ToBase64String(actual) || !IsCurrent)
                        throw new InvalidDataException("native_primary_descriptor_mismatch");
                    if (receiveCollectorOutputs) ReceiveCollectorOutputs(actual, files);
                    Console.Out.WriteLine("nlink-native-primary-adopted:" + RequestId.ToString("D")); Console.Out.Flush();
                    files.ConfirmProducerRelease();
                    if (!IsCurrent || !files.TryClaimRequest(this)) throw new InvalidDataException("native_primary_worker_stale");
                    terminalWorkerFiles = new TerminalWorkerFilesForTest(this, files);
                    if (!terminalWorkerFiles.IsCurrent) throw new InvalidDataException("native_primary_adoption_lost");
                    files = null;
                }
                finally {
                    if (files != null) {
                        files.Dispose();
                        if (collectorPrimaryFiles != null) collectorPrimaryFiles.Dispose();
                    }
                }
            }

            public void CloseTerminalFilesForTest()
            {
                if (!Object.ReferenceEquals(frontierOriginal, this)) return;
                if (terminalWorkerFiles != null) terminalWorkerFiles.Dispose();
                if (collectorPrimaryFiles != null) collectorPrimaryFiles.Dispose();
            }

            internal bool OwnsTerminalPrimary(TerminalWorkerFilesForTest files, SourceReader source,
                PrimaryEvaluationCustody custody, CompletedEvaluations pair, object context, string[] full, string[] compact,
                object fullView, object compactView)
            {
                return Object.ReferenceEquals(terminalWorkerFiles, files) && files.IsCurrent && OwnsCompletedSource(source) &&
                    Object.ReferenceEquals(primary, custody) && custody != null &&
                    custody.Owns(pair, context, full, compact, fullView, compactView);
            }
        }

        public sealed class TerminalWorkerFilesForTest : IDisposable
        {
            private readonly Worker owner;
            private readonly HeldFileNativeParent files;
            private readonly TerminalWorkerFilesForTest original;
            private int disposed;
            internal TerminalWorkerFilesForTest(Worker owner, HeldFileNativeParent files)
            { this.owner = owner; this.files = files; original = this; }
            public bool IsCurrent
            { get { return Object.ReferenceEquals(original, this) && Volatile.Read(ref disposed) == 0 && owner.IsCurrent && files.IsClaimedBy(owner); } }
            public bool OwnsPrimary(Worker candidate, SourceReader source, PrimaryEvaluationCustody custody,
                CompletedEvaluations pair, object context, string[] full, string[] compact, object fullView, object compactView)
            {
                return Object.ReferenceEquals(owner, candidate) && IsCurrent &&
                    owner.OwnsTerminalPrimary(this, source, custody, pair, context, full, compact, fullView, compactView);
            }
            public HeldFileNativeParent.Measurement[] Describe()
            { if (!IsCurrent) throw new InvalidDataException("native_primary_files_inactive"); return files.Describe(); }
            internal HeldFileNativeParent.TransferBinding OwnedTransfer
            { get { return IsCurrent ? files.OwnedTransfer(owner) : null; } }
            internal bool PayloadsMatch(long bytes) { return IsCurrent && files.PayloadsMatch(owner, bytes); }
            public void Dispose()
            { if (Object.ReferenceEquals(original, this) && Interlocked.Exchange(ref disposed, 1) == 0) files.Dispose(); }
        }
    }
}
#endif
