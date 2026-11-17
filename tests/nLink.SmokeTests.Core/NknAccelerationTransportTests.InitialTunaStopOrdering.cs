using System.Collections;
using System.Reflection;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private sealed class InitialFallbackPendingCallbacks : IDisposable
    {
        private readonly SessionFileTransferService service;
        private readonly NknSignalingTransport transport;
        private readonly object context;
        private readonly FileTransferSessionPreparationAdmissionLease admission;
        private readonly TaskCompletionSource physical = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<object> callbacks = [];
        private bool resetObserved;
        private bool localStopObservedBeforeRetirement;
        private object holdExecution = null!;
        private long proofGeneration;

        public InitialFallbackPendingCallbacks(SessionFileTransferService service, NknSignalingTransport transport, object context)
        {
            this.service = service; this.transport = transport; this.context = context;
            admission = AdmissionLease(service);
            Assert.True(admission.IsAdopted);
            lock (GetPrivateField(service, "gate")!)
            {
                Assert.False((bool)ContextProperty(context, "PullTransportPaused")!);
                var session = ContextProperty(context, "DataSession")!;
                var register = typeof(SessionFileTransferService).GetMethod("TryRegisterOutboundPhysicalSendLeaseLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (var index = 0; index < 6; index++)
                {
                    object?[] args = [context, session, 0, 1, false, "", null, null];
                    Assert.True(Assert.IsType<bool>(register.Invoke(service, args)));
                    var lease = typeof(SessionFileTransferService).GetMethod("BindOutboundPhysicalSendLeaseCompletionLocked",
                        BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context, args[6], physical.Task])!;
                    callbacks.Add(ContextProperty(lease, "BootstrapCallback")!);
                    _ = Call("ObserveOutboundPhysicalSendLeaseAsync", context, lease, physical.Task);
                }
                Assert.Equal(6, ((IDictionary)ContextProperty(context, "ActiveBootstrapSourceCallbacks")!).Count);
            }
            transport.TransportAccelerationStateChanged += OnReset;
            ((IFileTransferRouteTransitionBridge)transport).FallbackV6ProofBootstrapEvaluationRequested += OnLocalStop;
        }

        private void OnReset(object? sender, TransportAccelerationStateChangedEventArgs e)
        {
            if (e.IsActive || resetObserved) { return; }
            resetObserved = true;
            // Replay the retained live interleaving: reset/retirement exposes a failed send before
            // local-stop delivery. Use the real currentness check and legacy pause mutation.
            lock (GetPrivateField(service, "gate")!)
            {
                if (Assert.IsType<FileTransferBootstrapSourceCallbackDisposition>(
                    Call("ResolveBootstrapSourceCallbackLocked", context, callbacks[0])) == FileTransferBootstrapSourceCallbackDisposition.Held)
                { return; }
                Call("EnsureBootstrapSourceCallbackCurrentLocked", context, callbacks[0]);
                object?[] args = [context, new FileTransferRequiredTunaBulkUnavailableException("header_switch_off"), null, 0];
                Assert.True(Assert.IsType<bool>(Call("TryDeferOutboundV4SendForFileTunaFallbackHandoffLocked", args)));
            }
        }

        private void OnLocalStop(object? sender, FileTransferFallbackV6ProofBootstrapEvaluationRequestedEventArgs e)
        {
            if (e.Evaluation.LocalStop is null) { return; }
            localStopObservedBeforeRetirement = admission.IsAdopted &&
                Assert.IsType<bool>(InvokePrivateMethod(transport, "IsAccelerationNegotiatedAndHealthy"));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void AssertCapturedThenSettle()
        {
            Assert.True(resetObserved);
            Assert.True(admission.Cancellation.IsCancellationRequested);
            lock (GetPrivateField(service, "gate")!)
            {
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                var hold = Assert.IsType<FileTransferBootstrapSourceHoldOperation>(
                    state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold);
                Assert.False(hold.Invalidated);
                Assert.Equal(6, hold.Capture!.OutboundCallbacks.Length);
                Assert.Equal(6, hold.Capture.OutboundPhysicalLeases.Length);
                Assert.Null(hold.Receipt);
                Assert.Null(state.PersistentState.FileTransferRouteTransition.BootstrapExchange);
                holdExecution = ContextProperty(context, "BootstrapSourceHoldExecution")!;
                proofGeneration = hold.Command.Initiation.ObservationGeneration;
                Assert.Equal(true, GetPrivateField(transport, "fileTransferFallbackProofPending"));
                Assert.Equal(proofGeneration, GetPrivateField(transport, "fileTransferFallbackProofGeneration"));
                foreach (var callback in callbacks)
                    Assert.Equal(FileTransferBootstrapSourceCallbackDisposition.Held,
                        Assert.IsType<FileTransferBootstrapSourceCallbackDisposition>(Call("ResolveBootstrapSourceCallbackLocked", context, callback)));
            }
            Assert.True(localStopObservedBeforeRetirement);
            physical.TrySetException(new IOException("modeled retired Tuna send"));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void AssertRetiredByExactDispositionAndStaleDrainIsInert()
        {
            lock (GetPrivateField(service, "gate")!)
            {
                var before = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                var transition = before.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
                var boundary = Assert.IsType<FileTransferFallbackTransitionProofBoundary>(transition.Authority.ProofBoundary);
                Assert.Equal(proofGeneration, boundary.PeerProofEvidence.ProofGeneration);
                Assert.Equal(transition.Authority, transition.DispositionReceipt!.Value.Command.Authority);
                Assert.Equal(before.CurrentRoutePublicationToken, transition.DispositionReceipt.Value.Command.RoutePublicationToken);
                Assert.Equal(false, GetPrivateField(transport, "fileTransferFallbackProofPending"));
                var successorProofGeneration = GetPrivateField(transport, "fileTransferFallbackProofGeneration");
                var bytes = ContextProperty(context, "BytesTransferred");

                // Replay the real drain callback after its source has been superseded by the committed fallback.
                Assert.True(Assert.IsAssignableFrom<Task>(Call("CompleteBootstrapSourceHoldDrainAsync", holdExecution)).IsCompletedSuccessfully);
                var after = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                Assert.Equal(before.PersistentState, after.PersistentState);
                Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
                Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
                Assert.Equal(bytes, ContextProperty(context, "BytesTransferred"));
                Assert.Equal(false, GetPrivateField(transport, "fileTransferFallbackProofPending"));
                Assert.Equal(successorProofGeneration, GetPrivateField(transport, "fileTransferFallbackProofGeneration"));
            }
        }

        private object? Call(string name, params object?[] args) => typeof(SessionFileTransferService)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args);

        public void Dispose()
        {
            transport.TransportAccelerationStateChanged -= OnReset;
            ((IFileTransferRouteTransitionBridge)transport).FallbackV6ProofBootstrapEvaluationRequested -= OnLocalStop;
            physical.TrySetResult();
        }
    }
}
