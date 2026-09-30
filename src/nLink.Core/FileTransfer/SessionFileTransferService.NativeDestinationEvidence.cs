using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Explicit native-attempt opt-in, not a normal-download retention policy.
    internal NativeDestinationEvidence? TryRequestNativeDestinationEvidence(FileTransferReceiveDestination destination)
        => TryRequestNativeDestinationEvidenceCore(destination, null, null);

    private NativeDestinationEvidence? TryRequestNativeDestinationEvidenceCore(FileTransferReceiveDestination destination,
        InboundTransferContext? requiredContext, InboundFinalizationSource? requiredSource)
    {
        InboundTransferContext context;
        lock (gate)
        {
            if (!ReferenceEquals(nativeDestinationServiceOriginal, this) || frontierAppLifetime is null ||
                inboundTransfer is not { IsTerminal: false } current ||
                current.LifetimeCts.IsCancellationRequested || current.NativeDestinationEvidence is not null ||
                !ReferenceEquals(current.WriteDestination, destination) || !destination.HasGuardOwnedHandle ||
                (requiredSource is not null && (!ReferenceEquals(current, requiredContext) ||
                    !IsInboundFinalizationSourceCurrentLocked(current, requiredSource)))) return null;
            context = current;
        }
        // Native lease validation must remain outside the Service gate.
        var evidence = frontierAppLifetime.TryCreateNativeDestinationEvidence(this, destination);
        if (evidence is null) return null;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal &&
                !context.LifetimeCts.IsCancellationRequested && context.NativeDestinationEvidence is null &&
                ReferenceEquals(context.WriteDestination, destination) && ReferenceEquals(context.WriteStream, destination.Stream) &&
                (requiredSource is null || IsInboundFinalizationSourceCurrentLocked(context, requiredSource)))
            {
                context.NativeDestinationEvidence = evidence;
                return evidence;
            }
        }
        evidence.Dispose();
        return null;
    }

    internal sealed partial class FrontierAppLifetime
    {
        private NativeDestinationEvidence? nativeDestinationEvidence;

        internal NativeDestinationEvidence? TryCreateNativeDestinationEvidence(SessionFileTransferService service,
            FileTransferReceiveDestination destination)
        {
            lock (admissionGate)
            {
                if (!ReferenceEquals(original, this) || !ReferenceEquals(service.frontierAppLifetime, this) ||
                    !ReferenceEquals(Current, this) || !ready || admissionRejected ||
                    Volatile.Read(ref finishAttempted) != 0 || lease?.IsCurrent != true || nativeDestinationEvidence is not null)
                    return null;
                return nativeDestinationEvidence = new NativeDestinationEvidence(this, lease, service, destination);
            }
        }

        internal bool NativeDestinationAdmissionOpen => ReferenceEquals(original, this) &&
            ReferenceEquals(Current, this) && Volatile.Read(ref finishAttempted) == 0;

        internal bool OwnsNativeDestinationEvidence(NativeDestinationEvidence evidence)
            => NativeDestinationAdmissionOpen && ReferenceEquals(Volatile.Read(ref nativeDestinationEvidence), evidence);
    }

    // Original in-process capability, not a reconstructible receipt DTO. At most
    // one per native app attempt; no handle/path import or external handle getter.
    internal sealed partial class NativeDestinationEvidence : IDisposable
    {
        private readonly NativeDestinationEvidence original;
        private readonly object sync = new();
        private readonly FrontierAppLifetime app;
        private readonly LocalFrontierAppRequest.AppLease lease;
        private readonly SessionFileTransferService service;
        private readonly FileTransferReceiveDestination destination;
        private SafeFileHandle? file;
        private MemoryMappedFile? map;
        private Stream? reader;
        private bool retired, readTaken;

        internal NativeDestinationEvidence(FrontierAppLifetime app, LocalFrontierAppRequest.AppLease lease,
            SessionFileTransferService service, FileTransferReceiveDestination destination)
        { original = this; this.app = app; this.lease = lease; this.service = service; this.destination = destination; }

        internal string? SessionId { get; private set; }
        internal string? TransferId { get; private set; }
        internal string? Sha256Base64 { get; private set; }
        internal string? FileIdentity { get; private set; }
        internal long ByteCount { get; private set; }

        // No native lease wait or reader I/O under the Service gate. Retirement
        // and transfer of the existing storage hold are serialized locally.
        internal bool TryComplete(SessionFileTransferService candidate, FileTransferReceiveDestination target)
        {
            lock (sync)
            {
                if (!ReferenceEquals(original, this) || retired || file is not null || !app.OwnsNativeDestinationEvidence(this) ||
                    !ReferenceEquals(candidate, service) || !ReferenceEquals(target, destination) ||
                    !Monitor.IsEntered(service.gate) || service.inboundTransfer is not { State: FileTransferTransferState.Completed } context ||
                    !ReferenceEquals(context.NativeDestinationEvidence, this) || !ReferenceEquals(context.WriteDestination, destination)) return false;
                file = destination.TryTakeFinalizedNativeRead();
                if (file is null) return false;
                SessionId = context.SessionId; TransferId = context.TransferId;
                ByteCount = context.FileSizeBytes; Sha256Base64 = context.Sha256Base64;
                return true;
            }
        }

        internal Stream? TryOpenRead(SessionFileTransferService candidate, FileTransferReceiveDestination target,
            LocalFrontierAppRequest.AppLease candidateLease)
        {
            lock (sync)
            {
                if (!ReferenceEquals(original, this) || !ReferenceEquals(candidate, service) ||
                    !ReferenceEquals(target, destination) || !ReferenceEquals(candidateLease, lease) || readTaken || file is null)
                    return null;
                if (!IsCurrentLocked()) return null;
                readTaken = true;
                try
                {
                    if (!OperationalLogNativeSource.TryDescribe(file, out var identity, out var bytes) || bytes != ByteCount)
                    { Dispose(); return null; }
                    FileIdentity = identity;
                    map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
                        HandleInheritability.None, leaveOpen: true);
                    reader = map.CreateViewStream(0, ByteCount, MemoryMappedFileAccess.Read);
                    return new EvidenceReader(this);
                }
                catch { Dispose(); return null; }
            }
        }

        private bool IsCurrentLocked()
        {
            if (retired) return false;
            if (app.OwnsNativeDestinationEvidence(this) && lease.IsCurrent && file is { IsClosed: false, IsInvalid: false }) return true;
            Dispose();
            return false;
        }

        public void Dispose()
        {
            if (!ReferenceEquals(original, this)) return;
            lock (sync)
            {
                if (retired) return;
                retired = true;
                reader?.Dispose(); map?.Dispose(); file?.Dispose();
                reader = null; map = null; file = null;
            }
        }

        private sealed class EvidenceReader : Stream
        {
            private readonly EvidenceReader original;
            private readonly NativeDestinationEvidence owner;
            internal EvidenceReader(NativeDestinationEvidence owner) { original = this; this.owner = owner; }
            public override bool CanRead { get { lock (owner.sync) return ReferenceEquals(original, this) && owner.IsCurrentLocked(); } }
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => owner.ByteCount;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (owner.sync)
                {
                    ObjectDisposedException.ThrowIf(!ReferenceEquals(original, this) || !owner.IsCurrentLocked(), this);
                    return owner.reader!.Read(buffer, offset, count);
                }
            }
            protected override void Dispose(bool disposing) { if (disposing && ReferenceEquals(original, this)) owner.Dispose(); base.Dispose(disposing); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
