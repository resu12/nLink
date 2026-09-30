using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.SmokeTests;
using NLink.Tools;

namespace NLink.Tests;

// Compiled only into the test assembly, never the PowerShell/default loader.
internal sealed partial class AttemptSenderSourceChannel
{
    private RuntimeReads? runtimeReads;

    internal RuntimeReads? BindRuntimeReads(LocalFrontierAppRequest.AppLease candidate, SessionFileTransferService service)
    {
        var serviceGate = RuntimeReads.Field(service, "gate");
        if (serviceGate is null) return null;
        lock (serviceGate) lock (original)
        {
            if (!ReferenceEquals(lease, candidate) || !IsCurrent || taken != 0 || runtimeReads is not null ||
                !ReferenceEquals(RuntimeReads.Field(service, "nativeDestinationServiceOriginal"), service) ||
                RuntimeReads.Field(service, "frontierAppLifetime") is not { } lifetime ||
                !ReferenceEquals(lifetime, SessionFileTransferService.FrontierAppLifetime.Current) ||
                !ReferenceEquals(RuntimeReads.Field(lifetime, "lease"), lease) ||
                RuntimeReads.Field(service, "outboundTransfer") is not { } context) return null;
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            if (state.IsTerminal || state.Direction != FileTransferDirection.Outbound ||
                state.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast) return null;
            // TakeRead uses an interlocked claim rather than this lock.
            if (Interlocked.CompareExchange(ref taken, 3, 0) != 0) return null;
            runtimeReads = new RuntimeReads(this, service, serviceGate, context);
            return runtimeReads;
        }
    }

    internal sealed class RuntimeReads
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly RuntimeReads original;
        private readonly AttemptSenderSourceChannel source;
        private readonly SessionFileTransferService service;
        private readonly object gate, context;
        private RuntimeOwnerToken? previousOwner;
        private RuntimeRead? active;
        private int opened;
        internal static object? Field(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value);
        private static object? Property(object value, string name) => value.GetType().GetProperty(name, Flags)?.GetValue(value);

        internal RuntimeReads(AttemptSenderSourceChannel source, SessionFileTransferService service, object gate, object context)
        { original = this; this.source = source; this.service = service; this.gate = gate; this.context = context; }

        internal Stream? OpenRead()
        {
            lock (gate) lock (source.original)
            {
                if (!OriginalCurrent() || opened == 4 || (opened < 2 && source.activeRead?.CanRead == true)) return null;
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                if (state.CurrentRuntimeOwner is not { } owner || state.CurrentRoutePublicationToken is not { } publication) return null;
                var metadata = opened == 0;
                var expected = opened < 2 ? FileTransferRoute.RegularNknV4Fast :
                    opened == 2 ? FileTransferRoute.FileTunaV4 : FileTransferRoute.PostTunaFallbackV6;
                if (owner.Route != expected || (opened == 1 && previousOwner != owner) ||
                    (opened >= 2 && (owner.RuntimeGeneration != previousOwner!.Value.RuntimeGeneration + 1 ||
                        owner.LiveRouteEpoch != previousOwner.Value.LiveRouteEpoch + 1))) return null;
                var install = metadata ? null : Property(context, "PendingOutboundSparsePumpInstall");
                if (metadata ? Property(context, "State") is not FileTransferTransferState.PreparingMetadata : install is null) return null;
                var session = metadata ? null : Property(context, "DataSession");
                if (!Current(owner, publication, install, session)) return null;
                var handle = ReOpenFile(source.file.SafeFileHandle, 0x80000000, 1, 0x08000000);
                try
                {
                    if (handle.IsInvalid || !source.MatchesIdentity(handle) || !Current(owner, publication, install, session)) return null;
                    var file = new FileStream(handle, FileAccess.Read);
                    // Core may clean up the retired pump after opening its
                    // successor. Only the validated new owner can retire this
                    // old source view; rejected/repeated opens do not touch it.
                    active?.Dispose();
                    source.activeRead = file;
                    handle = null;
                    previousOwner = owner;
                    opened++;
                    active = new RuntimeRead(this, file, owner, publication, install, session);
                    return active;
                }
                finally { handle?.Dispose(); }
            }
        }

        private bool OriginalCurrent() => ReferenceEquals(original, this) && ReferenceEquals(source.runtimeReads, this) &&
            source.IsCurrent && ReferenceEquals(Field(service, "outboundTransfer"), context) &&
            Property(context, "LifetimeCts") is CancellationTokenSource lifetime && !lifetime.IsCancellationRequested;

        private bool Current(RuntimeOwnerToken owner, RoutePublicationToken publication, object? install, object? session)
        {
            if (!OriginalCurrent()) return false;
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            if (state.CurrentRuntimeOwner != owner || state.CurrentRoutePublicationToken != publication ||
                !FileTransferCoordinator.IsRuntimeOwnerCurrent(state, owner, out _) ||
                !FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _)) return false;
            if (install is null) return true;
            object?[] args = [context, install, session, null];
            return ReferenceEquals(Property(context, "DataSession"), session) &&
                (ReferenceEquals(Property(context, "PendingOutboundSparsePumpInstall"), install) ||
                 ReferenceEquals(Property(context, "InstalledOutboundSparsePump"), install)) &&
                (bool)typeof(SessionFileTransferService).GetMethod("IsOutboundSparsePumpInstallCurrentLocked", Flags)!.Invoke(service, args)!;
        }

        private sealed class RuntimeRead(RuntimeReads factory, FileStream file, RuntimeOwnerToken owner,
            RoutePublicationToken publication, object? install, object? session) : Stream
        {
            private bool Current
            {
                get { lock (factory.gate) lock (factory.source.original)
                    return ReferenceEquals(factory.active, this) && file.CanRead && ReferenceEquals(factory.source.activeRead, file) && factory.Current(owner, publication, install, session); }
            }
            private void Check() { if (!Current) throw new IOException("attempt_runtime_source_retired"); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                Check();
                var count = await file.ReadAsync(buffer, ct);
                if (!Current) { buffer.Span[..count].Clear(); throw new IOException("attempt_runtime_source_retired"); }
                return count;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override int Read(byte[] buffer, int offset, int count)
            {
                Check();
                var read = file.Read(buffer, offset, count);
                if (!Current) { buffer.AsSpan(offset, read).Clear(); throw new IOException("attempt_runtime_source_retired"); }
                return read;
            }
            public override bool CanRead => Current;
            public override bool CanSeek => Current;
            public override bool CanWrite => false;
            public override long Length { get { Check(); return file.Length; } }
            public override long Position { get { Check(); return file.Position; } set => Seek(value, SeekOrigin.Begin); }
            public override long Seek(long offset, SeekOrigin origin) { Check(); var position = file.Seek(offset, origin); Check(); return position; }
            public override void Flush() => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            { if (disposing && ReferenceEquals(factory.active, this)) file.Dispose(); base.Dispose(disposing); }
        }
    }
}
