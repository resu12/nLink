using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.SmokeTests;

namespace NLink.Tests;

// Retained identities and pure currentness checks, not another authority model.
internal sealed class AttemptCanonicalPrefixScope
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private readonly SessionFileTransferService service;
    private readonly object gate, context, dataSession;
    private readonly string field;
    private readonly long generation;
    private readonly RuntimeOwnerToken owner;
    private readonly RoutePublicationToken publication;
    private readonly CancellationTokenSource contextLifetime;
    private readonly Action checkPeer;
    private readonly bool sender;
    internal string Session { get; }
    internal string Transfer { get; }
    internal CancellationToken ContextToken => contextLifetime.Token;

    internal AttemptCanonicalPrefixScope(SessionFileTransferService service, object context, bool sender, Action checkPeer)
    {
        this.service = service; this.context = context;
        this.sender = sender;
        this.checkPeer = checkPeer;
        gate = Field(service, "gate")!; field = sender ? "outboundTransfer" : "inboundTransfer";
        lock (gate)
        {
            Session = Property<string>(context, "SessionId"); Transfer = Property<string>(context, "TransferId");
            dataSession = Property<object?>(context, "DataSession") ?? throw new IOException("canonical_prefix_session_missing");
            generation = Property<long>(context, "DataSessionGeneration");
            contextLifetime = Property<CancellationTokenSource>(context, "LifetimeCts");
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            owner = state.CurrentRuntimeOwner ?? throw new IOException("canonical_prefix_runtime_missing");
            publication = state.CurrentRoutePublicationToken ?? throw new IOException("canonical_prefix_publication_missing");
            CheckSetup();
        }
    }

    // Pipe setup is not a prefix witness. Bootstrap feedback may still pause
    // the sender; receiver readiness and witness consumption remain strict.
    internal void CheckSetup() => Check(allowSenderPeerPause: sender);
    internal void Check() => Check(allowSenderPeerPause: false);

    private void Check(bool allowSenderPeerPause)
    {
        lock (gate)
        {
            checkPeer();
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            if (!ReferenceEquals(Field(service, field), context) ||
                !ReferenceEquals(Property<object?>(context, "DataSession"), dataSession) ||
                Property<long>(context, "DataSessionGeneration") != generation || generation <= 0 ||
                !ReferenceEquals(Property<object>(context, "LifetimeCts"), contextLifetime) || contextLifetime.IsCancellationRequested ||
                Property<string>(context, "SessionId") != Session || Property<string>(context, "TransferId") != Transfer ||
                Property<bool>(context, "IsTerminal") ||
                state.IsTerminal || state.CurrentRuntimeOwner != owner || state.CurrentRoutePublicationToken != publication ||
                owner.Route != FileTransferRoute.PostTunaFallbackV6 || owner.ProtocolVersion != 6 ||
                publication.Route != FileTransferRoute.PostTunaFallbackV6 || publication.ProtocolVersion != 6 ||
                !FileTransferCoordinator.IsRuntimeOwnerCurrent(state, owner, out _) ||
                !FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _))
                throw new IOException("canonical_prefix_scope_retired");
            if (Property<bool>(context, "UserPaused") || (!allowSenderPeerPause && Property<bool>(context, "PeerPaused")))
                throw new IOException("canonical_prefix_scope_paused");
            AttemptCanonicalTailProfile.RequireGeometry(Property<long>(context, "FileSizeBytes"),
                Property<int>(context, "ChunkSizeBytes"), Property<int>(context, "ChunkCount"));
        }
    }

    internal bool Ready()
    {
        lock (gate)
        {
            Check();
            return ReadReady(context, Session, Transfer);
        }
    }

    // Invoked only inside the service gate; exports no context or bitmap.
    internal static bool ReadReady(object context, string session, string transfer)
    {
        AttemptCanonicalTailProfile.RequireGeometry(Property<long>(context, "FileSizeBytes"),
            Property<int>(context, "ChunkSizeBytes"), Property<int>(context, "ChunkCount"));
        if (Property<string>(context, "SessionId") != session || Property<string>(context, "TransferId") != transfer ||
            !Property<bool>(context, "PullManifestReceived") || Property<bool>(context, "IsTerminal") ||
            Property<bool>(context, "UserPaused") || Property<bool>(context, "PeerPaused"))
            throw new IOException("canonical_prefix_context_invalid");
        AttemptOfflineLatePublicationProgress.RequireUnboundObservation(Property<object?>(context, "FrontierBoundaryObservation"));
        var next = Property<int>(context, "NextChunkIndex");
        var chunks = Property<int>(context, "ChunksTransferred");
        var highest = Property<int>(context, "PullHighestReceivedChunkIndex");
        var committed = Property<long>(context, "BytesTransferred");
        var written = Property<long>(context, "ReceiverSparseBytesWritten");
        var pending = Property<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite");
        var buffered = Property<long>(context, "BufferedBytes");
        var bitmap = Property<BitArray?>(context, "ReceiverSparseChunksWritten");
        const int first = AttemptCanonicalTailProfile.FirstChunk;
        if (next < 0 || next > first || chunks != next || highest < next - 1 || highest >= first ||
            committed != (long)next * AttemptCanonicalTailProfile.ChunkBytes || written < committed ||
            written > AttemptCanonicalTailProfile.PrefixBytes || buffered < 0 ||
            bitmap?.Length != AttemptCanonicalTailProfile.ChunkCount || pending.Any(i => i < next || i >= first))
            throw new IOException("canonical_prefix_shape_invalid");
        if (next < first) return false;
        if (highest != first - 1 || written != committed || pending.Count != 0 || buffered != 0)
            throw new IOException("canonical_prefix_boundary_inconsistent");
        return true; // Committed prefix bitmap bits have already been cleared by Core.
    }
}
