using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    // The existing Coordinator driver supplies the pre-yield history. From
    // that exact checkpoint the Service owns both measured proof receipts.
    internal sealed class ServiceYieldHistory : IDisposable
    {
        private readonly SessionFileTransferService service = new();
        private readonly InitialBootstrapRecordingDataSession recording;
        private readonly InitialBootstrapCheckpointTransport bridge;
        private readonly object context;

        internal string CoordinatorInstanceId => GetPrivateProperty<string>(context, "CoordinatorInstanceId");
        internal string DataSessionIdentity => CreateCoordinatorStateForTest(context).CurrentDataSessionIdentity!;
        internal FileTransferCoordinatorState State => CreateCoordinatorStateForTest(context);

        internal ServiceYieldHistory(IFileTransferDataSession data, IFileTransferControlPlaneDeliveryTransport transport,
            object emitter)
        {
            recording = new(data.SessionId, data.TransferId);
            bridge = new(recording) { YieldTransportForTest = transport };
            service.AttachTransport(bridge);
            FileTransferReadStreamFactory read = _ => Task.FromResult<Stream>(new MemoryStream());
            context = Activator.CreateInstance(typeof(SessionFileTransferService).GetNestedType("OutboundTransferContext",
                    BindingFlags.NonPublic)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [new FileTransferSendDescriptor("two-writer.bin", 256L * 21 * 1024, data.TransferId), read, emitter],
                culture: null)!;
            SetPrivateProperty(context, "SessionId", data.SessionId);
            SetPrivateProperty(context, "DataSession", data);
            SetPrivateProperty(context, "DataSessionGeneration", 17L);
            SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
            SetPrivateProperty(context, "ChunkCount", 256);
            SetPrivateProperty(context, "PullSessionActive", true);
            typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, context);
        }

        internal void Initialize(FileTransferCoordinatorState state)
        {
            Assert.Equal(CoordinatorInstanceId, state.PersistentState.CoordinatorInstanceId);
            Assert.Equal(DataSessionIdentity, state.CurrentDataSessionIdentity);
            Assert.Null(state.PersistentState.FallbackTailYield.CurrentOperation);
            // Copy the already emitted checkpoint through the production context
            // projection, without adopting/emitting a new decision or receipt.
            SetPrivateProperty(context, "CoordinatorPersistentState", state.PersistentState);
            var projection = new FileTransferCoordinatorDecision(state, false, false, null, null, null, null, null,
                false, "original_pre_yield_history_handoff");
            typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                    method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == context.GetType())
                .Invoke(null, [context, projection, false, false]);
            Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(state),
                FileTransferCoordinator.CalculateSnapshotStateFingerprint(State));
        }

        internal async Task SendYieldAsync(Action drain)
        {
            lock (GetServiceGate(service))
            {
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                Assert.Equal(8, leg.ProvenCommittedChunkIndex);
                Assert.Equal(8, leg.ProvenHighestObservedChunkIndex);
                // Include the normal owner-retirement path, not just the lower
                // level issue/send helpers that omit its required effect.
                Assert.True((bool)Call("TryMarkOutboundFallbackTailLocalYieldFromCurrentLegLocked",
                    context, "two_writer_service_yield", false, null)!);
                drain();
            }
            await WaitUntilAsync(() => State.PersistentState.FallbackTailYield.CurrentOperation?.LocalProof is not null, 5000);
            drain();
            Assert.Single(bridge.Requests, request => request.Kind == FileTransferControlPlaneKind.FallbackTailYield);
        }

        internal void ApplyPeer(FileTransferReceivedDataFrame received)
        {
            var frame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(received.Frame);
            Assert.NotNull(received.YieldEvidence);
            Call("ApplyOutboundFallbackTailYieldProofWithEvidence", context, frame, received.YieldEvidence);
            Assert.NotNull(State.PersistentState.FallbackTailYield.CurrentOperation?.PairedRelease);
        }

        private object? Call(string name, params object?[] args)
            => typeof(SessionFileTransferService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, args);

        public void Dispose()
        {
            // The original history owns stream completion after its later
            // Coordinator boundaries; disposal must not terminalize an old copy.
            typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, null);
            TryCancelContextLifetime(context);
            service.Dispose();
            bridge.Dispose();
            recording.Dispose();
        }
    }
}
