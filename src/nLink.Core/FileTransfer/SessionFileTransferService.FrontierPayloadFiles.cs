using System.Diagnostics;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed partial class FrontierPayloadCatalog
    {
        private static readonly TimeSpan MaximumOutputTime = TimeSpan.FromSeconds(5);
        private bool payloadOutputAttempted;
        private PayloadFilesEvidence? payloadFiles;

        private PayloadFilesEvidence? TryWritePayloadFiles(PreparedCatalog candidate)
        {
            FrontierBoundaryObservation.PreparedBoundaryPayload[] payloads;
            SourceFileEvidence source;
            lock (gate)
            {
                if (!Owns(candidate) || nativeLease is null || payloadOutputAttempted) { return null; }
                payloadOutputAttempted = true;
                if (sourceFile is null || !sourceFile.IsCurrent) { Invalidate("payload_output_source_missing"); return null; }
                source = sourceFile;
                payloads = slots.Select(s => s.Payload!).ToArray();
            }
            var clock = Stopwatch.StartNew();
            var files = new List<FrontierNativePayloadFile>();
            var transferred = false;
            try
            {
                // All copying, writing and byte verification occurs outside the
                // catalog/service gates. A failed catalog never exposes a prefix.
                foreach (var payload in payloads)
                {
                    if (!source.IsCurrent || clock.Elapsed > MaximumOutputTime) { return null; }
                    var file = FrontierNativePayloadFile.TryCreate(LocalOperationalLog.LogsDirectoryPath, payload.CopyBytes());
                    if (file is null) { return null; }
                    files.Add(file);
                    if (file.ByteCount != payload.ByteCount || file.Sha256 != payload.Sha256 ||
                        !source.IsCurrent || clock.Elapsed > MaximumOutputTime) { return null; }
                }
                lock (gate)
                {
                    if (!Owns(candidate) || !source.IsCurrent || files.Any(f => !f.IsCurrent) || clock.Elapsed > MaximumOutputTime)
                    { return null; }
                    payloadFiles = new(this, candidate, source, files.ToArray());
                    transferred = true;
                    return payloadFiles;
                }
            }
            catch { return null; }
            finally
            {
                if (!transferred)
                {
                    foreach (var file in files) { file.Dispose(); }
                    lock (gate) { Invalidate("payload_output_failed"); }
                }
            }
        }

        private bool OwnsPayloadFiles(PayloadFilesEvidence candidate, PreparedCatalog snapshot, SourceFileEvidence source)
        {
            lock (gate)
            {
                if (!ReferenceEquals(payloadFiles, candidate)) { return false; }
                if (Owns(snapshot) && source.IsCurrent && candidate.Files.All(f => f.IsCurrent)) { return true; }
                Invalidate("payload_output_revoked"); return false;
            }
        }

        // App-local native files only. Neither this wrapper nor metadata copies
        // are a cross-process descriptor or a publication completion.
        public sealed class PayloadFilesEvidence(FrontierPayloadCatalog owner, PreparedCatalog snapshot,
            SourceFileEvidence source, FrontierNativePayloadFile[] files)
        {
            internal FrontierNativePayloadFile[] Files { get; } = files;
            public int Count => Files.Length;
            public int ByteCount => Files.Sum(f => f.ByteCount);
            public SourceFileEvidence Source { get; } = source;
            public bool IsCurrent => owner.OwnsPayloadFiles(this, snapshot, Source);
            private FrontierNativePayloadFile? GetFile(int index) => IsCurrent && index >= 0 && index < Files.Length ? Files[index] : null;
            public string? GetFileIdentity(int index) => GetFile(index)?.FileIdentity;
            public string? GetSha256(int index) => GetFile(index)?.Sha256;
            public long? GetByteCount(int index) => GetFile(index)?.ByteCount;
            public OrderedWriterEvidence? GetWriterEvidence(int index) => GetFile(index) is null ? null : owner.GetOrderedWriterEvidence(snapshot, index);
            public FileStream? TryOpenRead(int index)
            {
                var read = GetFile(index)?.TryOpenRead();
                if (IsCurrent) { return read; }
                read?.Dispose(); return null;
            }
            internal void Retire() { foreach (var file in Files) { file.Dispose(); } }
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }
    }
}
