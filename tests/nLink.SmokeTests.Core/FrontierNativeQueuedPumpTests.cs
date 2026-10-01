using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    internal static async Task RunNativeQueuedPumpAsync(SessionFileTransferService.NativeSenderSelection native)
    {
        using var suite = new NknAccelerationTransportTests();
        using var probe = new NativeQueuedPumpProbe(native);
        await suite.RunJoinedBootstrapCorrelationAsync(runExchange: true, queuedPump: probe);
    }

    // Schedule a held callback without wrapping the production native factory or
    // source stream. All route/hold/installation authority is issued by Core.
    private sealed class NativeQueuedPumpProbe(SessionFileTransferService.NativeSenderSelection native) : IDisposable
    {
        internal SessionFileTransferService.NativeSenderSelection Native => native;
        private readonly TaskCompletionSource tunaSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseTuna = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource heldControl = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseControl = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource fallbackSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource sameRouteSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseSameRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionFileTransferService sender = null!;
        private CancellationToken cancellation;
        private Func<string, string, Task>? previousCompletion;
        private Func<string, string, bool>? previousNormalCompletion;
        private int completeFallbackPump;
        private Stream predecessorRead = null!;
        private object predecessorInstall = null!;
        private bool attached;
        private object Context => GetPrivateField(sender, "outboundTransfer")!;
        private object Gate => GetPrivateField(sender, "gate")!;
        private FileTransferCoordinatorState State => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, Context);
        private Stream ActiveRead => (Stream)GetPrivateField(native, "active")!;
        private static object? Value(object owner, string name) => owner.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner);

        internal void Attach(SessionFileTransferService service, FakeNknClient host, FakeNknClient helper, CancellationToken ct)
        {
            sender = service;
            cancellation = ct;
            previousCompletion = SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests;
            previousNormalCompletion = SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests;
            attached = true;
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = (id, _) =>
                id == native.Descriptor.TransferId && Interlocked.Exchange(ref completeFallbackPump, 0) == 1;
            SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = (id, _) =>
            {
                if (id != native.Descriptor.TransferId) return Task.CompletedTask;
                lock (Gate)
                {
                    if (State.CurrentRuntimeOwner?.Route == FileTransferRoute.FileTunaV4)
                    {
                        predecessorRead = ActiveRead;
                        predecessorInstall = Value(Context, "InstalledOutboundSparsePump")!;
                        tunaSend.TrySetResult();
                        return releaseTuna.Task.WaitAsync(cancellation);
                    }
                    if (State.CurrentRuntimeOwner?.Route == FileTransferRoute.PostTunaFallbackV6)
                    {
                        if ((long)Value(Value(Context, "InstalledOutboundSparsePump")!, "InstallGeneration")! == 4)
                        {
                            sameRouteSend.TrySetResult();
                            return releaseSameRoute.Task.WaitAsync(cancellation);
                        }
                        fallbackSend.TrySetResult();
                        return releaseFallback.Task.WaitAsync(cancellation);
                    }
                }
                return Task.CompletedTask;
            };
            host.BeforeSendCoreAsync = HoldControlAsync;
            helper.BeforeSendCoreAsync = HoldControlAsync;
        }

        private Task HoldControlAsync(string address, byte[] bytes, NknBridgeChannel lane, CancellationToken ct)
        {
            lock (Gate)
            {
                if (!tunaSend.Task.IsCompleted || releaseControl.Task.IsCompleted ||
                    State.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Capture is not { } capture ||
                    capture.OutboundCallbacks.IsEmpty) return Task.CompletedTask;
                heldControl.TrySetResult();
                return releaseControl.Task.WaitAsync(ct);
            }
        }

        internal async Task WaitForTunaSendAsync()
        {
            await tunaSend.Task.WaitAsync(cancellation);
            lock (Gate)
            {
                Assert.True(predecessorRead.CanRead);
                Assert.Equal(2L, Value(predecessorInstall, "InstallGeneration"));
                Assert.True(native.IsReadBoundToPump(predecessorRead, 2));
                // A new installation needs a new view even without a route change.
                Assert.False(native.IsReadBoundToPump(predecessorRead, 3));
                Assert.False(native.IsReadBoundToPump(null, 2));
                var clone = (Stream)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(predecessorRead, null)!;
                Assert.False(native.IsReadBoundToPump(clone, 2));
                clone.Dispose();
                Assert.True(predecessorRead.CanRead);
            }
        }

        internal async Task RetireHeldCallbackAsync()
        {
            await heldControl.Task.WaitAsync(cancellation);
            lock (Gate)
            {
                var hold = State.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;
                Assert.NotEmpty(hold.Capture!.OutboundCallbacks);
                Assert.True(FileTransferCoordinator.IsFallbackBootstrapSourceHoldCurrent(State, hold.Command));
            }
            releaseTuna.TrySetResult();
            await WaitUntilAsync(() =>
            {
                lock (Gate) return ((IDictionary)Value(Context, "ActiveBootstrapSourceCallbacks")!).Count == 0;
            }, TimeSpan.FromSeconds(8));
            lock (Gate)
            {
                Assert.False(sender.Snapshot.Outbound!.IsTerminal);
                Assert.Same(predecessorInstall, Value(Context, "InstalledOutboundSparsePump"));
                Assert.Same(predecessorRead, ActiveRead);
            }
            releaseControl.TrySetResult();
        }

        internal async Task VerifySuccessorAsync()
        {
            await WaitUntilAsync(() => fallbackSend.Task.IsCompleted || sender.Snapshot.Outbound!.IsTerminal,
                TimeSpan.FromSeconds(8));
            lock (Gate)
            {
                var install = Value(Context, "InstalledOutboundSparsePump")!;
                var snapshot = sender.Snapshot.Outbound!;
                Assert.False(snapshot.IsTerminal,
                    $"Queued native pump failed: {snapshot.ErrorCode}; {snapshot.StatusMessage}; " +
                    $"same_stream={ReferenceEquals(predecessorRead, ActiveRead)}; " +
                    $"install={Value(install, "InstallGeneration")}; predecessor_readable={predecessorRead.CanRead}");
                Assert.True(fallbackSend.Task.IsCompleted);
                Assert.Equal("queued_sparse_runtime_work", Value(install, "Reason"));
                Assert.Equal(3L, Value(install, "InstallGeneration"));
                Assert.NotSame(predecessorRead, ActiveRead);
                Assert.Same(install, ActiveRead.GetType().GetField("install", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ActiveRead));
                Assert.True(native.IsReadBoundToPump(ActiveRead, 3));
                Assert.False(native.IsReadBoundToPump(predecessorRead, 2));
                Assert.True(ActiveRead.CanRead);
                Assert.False(predecessorRead.CanRead);
                Assert.Throws<IOException>(() => predecessorRead.Seek(0, SeekOrigin.Begin));
            }
            await VerifySameRouteSuccessorAsync();
        }

        private async Task VerifySameRouteSuccessorAsync()
        {
            Stream previousRead;
            FileTransferCoordinatorState before;
            lock (Gate)
            {
                previousRead = ActiveRead;
                before = State;
                Volatile.Write(ref completeFallbackPump, 1);
            }
            // Finish only the pump, not the transfer or coordinator runtime.
            releaseFallback.TrySetResult();
            await WaitUntilAsync(() => sameRouteSend.Task.IsCompleted || sender.Snapshot.Outbound!.IsTerminal,
                TimeSpan.FromSeconds(8));
            lock (Gate)
            {
                Assert.False(sender.Snapshot.Outbound!.IsTerminal, sender.Snapshot.Outbound.StatusMessage);
                Assert.True(sameRouteSend.Task.IsCompleted);
                Assert.Equal(before.CurrentRuntimeOwner, State.CurrentRuntimeOwner);
                Assert.Equal(before.CurrentRoutePublicationToken, State.CurrentRoutePublicationToken);
                var install = Value(Context, "InstalledOutboundSparsePump")!;
                Assert.Equal(4L, Value(install, "InstallGeneration"));
                Assert.Equal("queued_sparse_runtime_work", Value(install, "Reason"));
                Assert.NotSame(previousRead, ActiveRead);
                Assert.True(native.IsReadBoundToPump(ActiveRead, 4));
                Assert.True(ActiveRead.CanRead);
                Assert.False(previousRead.CanRead);
                Assert.Throws<IOException>(() => previousRead.Seek(0, SeekOrigin.Begin));
            }
        }

        public void Dispose()
        {
            releaseTuna.TrySetResult();
            releaseControl.TrySetResult();
            releaseFallback.TrySetResult();
            releaseSameRoute.TrySetResult();
            if (attached)
            {
                SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = previousCompletion;
                SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = previousNormalCompletion;
                attached = false;
            }
        }
    }
}
