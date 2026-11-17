using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    internal sealed partial class FrontierAppLifetime
    {
        private readonly bool canonicalSourceRequested = Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_SOURCE_V1") == "1";
        private bool senderSelectionAttempted;
        private NativeSenderSelection? nativeSenderSelection;

        internal NativeSenderSelection? TrySelectNativeSender(string fileName, long byteCount, CancellationToken ct)
        {
            if (!OperatingSystem.IsWindows()) return null;
            ct.ThrowIfCancellationRequested();
            lock (admissionGate)
            {
                if (!ReferenceEquals(original, this) || !ReferenceEquals(Current, this) || !canonicalSourceRequested ||
                    !ready || admissionRejected || finishAttempted != 0 || senderSelectionAttempted || lease?.IsCurrent != true) return null;
                senderSelectionAttempted = true;
                if (byteCount != 268435456) return null;
                var source = LocalCanonicalSenderSource.ReceiveAvailable(lease, ct);
                if (source is null) return null;
                var anchor = source.TakeRead(lease);
                if (anchor is null || ct.IsCancellationRequested || finishAttempted != 0)
                { source.Dispose(); ct.ThrowIfCancellationRequested(); return null; }
                return nativeSenderSelection = new NativeSenderSelection(this, source, anchor, fileName);
            }
        }

        internal bool OwnsNativeSenderSelection(NativeSenderSelection selection) =>
            ReferenceEquals(original, this) && ReferenceEquals(Current, this) &&
            Volatile.Read(ref finishAttempted) == 0 && !Volatile.Read(ref admissionRejected) &&
            ReferenceEquals(Volatile.Read(ref nativeSenderSelection), selection);
    }

    // One original picker selection and send context. Native custody does not
    // create a route: every view requires the coordinator's current owner and
    // publication, and payload views require the exact current pump install.
    internal sealed class NativeSenderSelection : IDisposable
    {
        private readonly NativeSenderSelection original;
        private readonly FrontierAppLifetime app;
        private readonly LocalCanonicalSenderSource source;
        private readonly FileStream anchor;
        private SessionFileTransferService? service;
        private OutboundTransferContext? context;
        private RuntimeRead? active;
        private int bindingAttempted, disposed;
        private bool metadataOpened;
        private long lastInstallGeneration;
        private readonly FileTransferReadStreamFactory openReadStreamAsync;

        internal FileTransferSendDescriptor Descriptor { get; }
        internal FileTransferReadStreamFactory OpenReadStreamAsync => ReferenceEquals(original, this) ? openReadStreamAsync : RejectOpen;
        private static Task<Stream> RejectOpen(CancellationToken ct) => Task.FromException<Stream>(Retired());

        // Called under the service gate only to select reuse versus reopen.
        // Native custody and coordinator currentness still gate every access.
        internal bool IsReadBoundToPump(Stream? stream, long installGeneration) =>
            ReferenceEquals(original, this) && stream is not null && ReferenceEquals(active, stream) &&
            lastInstallGeneration == installGeneration;

        internal NativeSenderSelection(FrontierAppLifetime app, LocalCanonicalSenderSource source, FileStream anchor, string fileName)
        {
            original = this; this.app = app; this.source = source; this.anchor = anchor;
            Descriptor = new(fileName, 268435456, Guid.NewGuid().ToString("N"));
            openReadStreamAsync = OpenAsync;
        }

        internal bool TryBindForSend(SessionFileTransferService candidate, FileTransferSendDescriptor descriptor)
        {
            if (!ReferenceEquals(original, this) || !ReferenceEquals(Descriptor, descriptor) ||
                !ReferenceEquals(candidate.nativeDestinationServiceOriginal, candidate) ||
                !ReferenceEquals(candidate.frontierAppLifetime, app) || !ManagedCurrent || !source.IsCurrent ||
                Interlocked.CompareExchange(ref bindingAttempted, 1, 0) != 0) return false;
            service = candidate;
            return true;
        }

        internal bool BindContextLocked(SessionFileTransferService candidate, object transfer)
        {
            if (transfer is not OutboundTransferContext value || !ReferenceEquals(original, this) ||
                !ReferenceEquals(service, candidate) || !Monitor.IsEntered(candidate.gate) ||
                context is not null || !ManagedCurrent || value.TransferId != Descriptor.TransferId ||
                value.FileSizeBytes != Descriptor.FileSizeBytes || value.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast ||
                value.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4) return false;
            context = value;
            return true;
        }

        private bool ManagedCurrent => ReferenceEquals(original, this) && Volatile.Read(ref disposed) == 0 &&
            app.OwnsNativeSenderSelection(this) && anchor.CanRead;

        // The sparse-pump caller may hold the Service gate while invoking its
        // factory. Defer native validation/reopen until that invocation returns.
        private Task<Stream> OpenAsync(CancellationToken ct) => Task.Run(() => Open(ct), ct);

        private Stream Open(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() || !ManagedCurrent || !source.IsCurrent || service is not { } owner)
                throw Retired();
            RuntimeOwnerToken runtime;
            RoutePublicationToken publication;
            OutboundSparsePumpInstall? install;
            IFileTransferDataSession? session;
            lock (owner.gate)
            {
                if (context is null || context.CurrentRuntimeOwner is not { } currentRuntime ||
                    context.CurrentRoutePublicationToken is not { } currentPublication) throw Retired();
                runtime = currentRuntime; publication = currentPublication;
                install = metadataOpened ? context.PendingOutboundSparsePumpInstall : null;
                session = install is null ? null : context.DataSession;
                if (!CurrentLocked(runtime, publication, install, session) ||
                    (!metadataOpened ? context.State != FileTransferTransferState.PreparingMetadata :
                        install is null || install.InstallGeneration <= lastInstallGeneration)) throw Retired();
            }
            SafeFileHandle? handle = OperationalLogNativeSource.Reopen(anchor.SafeFileHandle, 1);
            FileStream? file = null;
            RuntimeRead? previous = null;
            var retained = false;
            try
            {
                if (handle.IsInvalid || !OperationalLogNativeSource.TryDescribe(handle, out var identity, out var bytes) ||
                    bytes != 268435456 || identity != source.FileIdentity || !source.IsCurrent) throw Retired();
                file = new FileStream(handle, FileAccess.Read);
                handle = null;
                ct.ThrowIfCancellationRequested();
                lock (owner.gate)
                {
                    if (!CurrentLocked(runtime, publication, install, session) ||
                        (install is null ? metadataOpened || context!.State != FileTransferTransferState.PreparingMetadata :
                            !metadataOpened || install.InstallGeneration <= lastInstallGeneration)) throw Retired();
                    var view = new RuntimeRead(this, file, runtime, publication, install, session);
                    previous = active;
                    Volatile.Write(ref active, view);
                    if (Volatile.Read(ref disposed) != 0) throw Retired();
                    metadataOpened = true;
                    if (install is not null) lastInstallGeneration = install.InstallGeneration;
                    retained = true;
                    return view;
                }
            }
            finally
            {
                if (!retained) file?.Dispose();
                handle?.Dispose();
                previous?.Dispose();
            }
        }

        private bool CurrentLocked(RuntimeOwnerToken runtime, RoutePublicationToken publication,
            OutboundSparsePumpInstall? install, IFileTransferDataSession? session)
        {
            if (!ManagedCurrent || service is null || context is null || !ReferenceEquals(service.outboundTransfer, context) ||
                context.IsTerminal || context.LifetimeCts.IsCancellationRequested) return false;
            var state = CreateCoordinatorStateLocked(context);
            var validRoute = publication.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4
                ? runtime.Route is FileTransferRoute.RegularNknV4Fast or FileTransferRoute.FileTunaV4
                : publication.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 && runtime.Route == FileTransferRoute.PostTunaFallbackV6;
            if (!validRoute || state.CurrentRuntimeOwner != runtime || state.CurrentRoutePublicationToken != publication ||
                !FileTransferCoordinator.IsRuntimeOwnerCurrent(state, runtime, out _) ||
                !FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _)) return false;
            if (install is null) return runtime.Route == FileTransferRoute.RegularNknV4Fast &&
                context.State == FileTransferTransferState.PreparingMetadata;
            return service.IsOutboundSparsePumpInstallCurrentLocked(context, install, session, out _);
        }

        private static IOException Retired() => new("native_sender_source_not_current");
        public void Dispose()
        {
            if (!ReferenceEquals(original, this) || Interlocked.Exchange(ref disposed, 1) != 0) return;
            Volatile.Read(ref active)?.Dispose(); source.Dispose();
        }

        private sealed class RuntimeRead : Stream
        {
            private readonly RuntimeRead original;
            private readonly NativeSenderSelection factory;
            private readonly RuntimeOwnerToken runtime;
            private readonly RoutePublicationToken publication;
            private readonly OutboundSparsePumpInstall? install;
            private readonly IFileTransferDataSession? session;
            internal readonly FileStream File;
            internal RuntimeRead(NativeSenderSelection factory, FileStream file, RuntimeOwnerToken runtime,
                RoutePublicationToken publication, OutboundSparsePumpInstall? install, IFileTransferDataSession? session)
            { original = this; this.factory = factory; File = file; this.runtime = runtime; this.publication = publication; this.install = install; this.session = session; }
            private bool Current
            {
                get
                {
                    if (!ReferenceEquals(original, this) || factory.service is not { } owner) return false;
                    lock (owner.gate) return ReferenceEquals(factory.active, this) && File.CanRead &&
                        factory.CurrentLocked(runtime, publication, install, session);
                }
            }
            private void Check() { if (!Current || !factory.source.IsCurrent) throw Retired(); }
            public override bool CanRead => Current;
            public override bool CanSeek => Current;
            public override bool CanWrite => false;
            public override long Length { get { Check(); return File.Length; } }
            public override long Position { get { Check(); return File.Position; } set => Seek(value, SeekOrigin.Begin); }
            public override long Seek(long offset, SeekOrigin origin) { Check(); var result = File.Seek(offset, origin); Check(); return result; }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                Check();
                try { var count = await File.ReadAsync(buffer, ct).ConfigureAwait(false); Check(); return count; }
                catch { buffer.Span.Clear(); throw; }
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override int Read(byte[] buffer, int offset, int count)
            {
                Check();
                try { var read = File.Read(buffer, offset, count); Check(); return read; }
                catch { buffer.AsSpan(offset, count).Clear(); throw; }
            }
            public override void Flush() => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            { if (disposing && ReferenceEquals(original, this)) File.Dispose(); base.Dispose(disposing); }
        }
    }
}
