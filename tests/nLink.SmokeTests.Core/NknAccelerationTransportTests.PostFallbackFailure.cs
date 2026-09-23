using System.Reflection;
using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasJoinedFallbackPair(SessionFileTransferService service, object context)
        => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context)
            .PersistentState.FallbackTailYield.CurrentOperation?.PairedRelease is not null;

    private static async Task<RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs?> AssertJoinedFallbackEofHandoffAsync(SessionFileTransferService sender, object outbound,
        NknSignalingTransport host, RetryableTunaAccelerationSession lane, IFileTransferDataSession data,
        object installedReceipt, string reason, string? control, CancellationToken token)
    {
        // Keep large value-type assertion frames out of the real callback stack.
        var before = CaptureStarted();
        var failures = new List<RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs>();
        ((IRuntimeUnlockRetryDispatchAuthorityBridge)host).RuntimeUnlockPostFallbackPreCommitProbeFailureObserved +=
            (_, args) => failures.Add(args);
        if (control == "retirement_stage_generation_conflict")
        {
            var shadow = GetPrivateField(host, "runtimeUnlockRetryDispatchInstallShadow")!;
            var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shadow, null)!;
            var generation = copy.GetType().GetProperty("LastScopeInvalidationGeneration")!;
            generation.SetValue(copy, (long)generation.GetValue(copy)! + 1);
            SetPrivateField(host, "runtimeUnlockRetryDispatchInstallShadow", copy);
            await lane.StopAsync(reason, token);
            var rejected = Assert.Single(failures).Decision;
            Assert.Equal(RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Rejected, rejected.Status);
            Assert.Equal("post_fallback_precommit_probe_failure_rollback_rejected", rejected.Reason);
            AssertNoRetirement();
            return null;
        }
        if (control is not null)
        {
            IFileTransferDataSession? replacement = null;
            try
            {
                if (control is "data_session_disposed" or "data_session_replaced")
                {
                    data.Dispose();
                    if (control == "data_session_replaced")
                    {
                        replacement = await host.OpenFileTransferDataSessionAsync(before.SessionId, before.TransferId, token);
                    }
                }
                else if (control == "service_disposed") { sender.Dispose(); }
                else { MutateStaleObservation(); }
                await lane.StopAsync(reason, token);
                await lane.StopAsync("remote_closed", token);
                Assert.Empty(failures);
                AssertNoSuccessor();
            }
            finally { replacement?.Dispose(); }
            return null;
        }
        await lane.StopAsync(reason, token);
        var failure = Assert.Single(failures);
        var after = AssertRolledBack(failure);
        await lane.StopAsync("remote_closed", token);
        await lane.StopAsync(reason, token);
        Assert.Single(failures);
        var replay = new RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs(failure.Observation);
        typeof(SessionFileTransferService).GetMethod("OnRuntimeUnlockPostFallbackPreCommitProbeFailureObserved",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, [host, replay]);
        Assert.NotEqual(RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Accepted, replay.Decision.Status);
        AssertUnchanged();
        return failure;

        [MethodImpl(MethodImplOptions.NoInlining)]
        FileTransferCoordinatorState CaptureStarted()
        {
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            Assert.Equal(FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
                state.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
            Assert.Equal(installedReceipt, (object?)state.PersistentState.FallbackTailYield.LastConsumingInstallReceipt);
            Assert.Equal(FileTransferRouteTransitionState.Draining,
                state.PersistentState.FileTransferRouteTransition.CurrentOperation!.Value.State);
            var premature = FileTransferCoordinator.PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(state,
                FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason);
            Assert.False(premature.Accepted);
            Assert.Same(state, premature.State);
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        FileTransferCoordinatorState AssertRolledBack(RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs failure)
        {
            Assert.Equal(RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Accepted, failure.Decision.Status);
            Assert.Equal(RuntimeUnlockPostFallbackPreCommitProbeFailureRole.Initiator, failure.Observation.Role);
            Assert.Equal(before.PersistentState.FallbackTailYield.LastConsumingInstallReceipt, failure.Observation.InstallReceipt);
            Assert.Equal(RuntimeUnlockPathProbeState.Started.ToString(), failure.Observation.Probe.PathProbeState);
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            var predecessor = before.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision;
            var successor = state.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision;
            Assert.Equal(predecessor.AuthorityId, successor.PredecessorAuthorityId);
            Assert.Equal(predecessor.YieldGeneration + 1, successor.YieldGeneration);
            Assert.Equal(FileTransferRoute.PostTunaFallbackV6, state.RouteSelection.Route);
            Assert.Equal(before.CurrentRoutePublicationToken, state.CurrentRoutePublicationToken);
            Assert.Null(state.PersistentState.FileTransferRouteTransition.CurrentOperation);
            var terminal = state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
            Assert.Equal(before.PersistentState.FileTransferRouteTransition.CurrentOperation!.Value.Authority, terminal.Authority);
            Assert.Equal(FileTransferRouteTransitionState.Invalidated, terminal.State);
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, terminal.DispositionReceipt!.Value.Status);
            Assert.Equal(FileTransferRouteTransitionDispositionKind.RolledBack, terminal.DispositionReceipt.Value.Command.Kind);
            Assert.Equal(predecessor, state.PersistentState.FallbackTailYield.LastTerminalOperation!.Value.Decision);
            Assert.Equal(FileTransferFallbackTailYieldPairingState.Invalidated,
                state.PersistentState.FallbackTailYield.LastTerminalOperation!.Value.PairingState);
            var lifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
            var retirement = lifecycle.LastScopeInvalidationReceipt!.Value;
            Assert.True(retirement.Accepted);
            Assert.Equal(FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason, retirement.Command.Reason);
            Assert.Equal(RuntimeUnlockRetryDispatchScopeInvalidationKind.FullScope, retirement.Command.Kind);
            Assert.Equal(before.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt, retirement.Command.InstalledAttempt);
            Assert.Equal(installedReceipt, (object?)retirement.Command.InstalledAttempt!.Value.InstallReceipt);
            Assert.Null(lifecycle.PendingScopeInvalidationCommand);
            Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AssertUnchanged()
        {
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            Assert.Equal(after.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision,
                state.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision);
            Assert.Equal(after.CurrentRoutePublicationToken, state.CurrentRoutePublicationToken);
            Assert.Equal(after.PersistentState.RuntimeUnlockRetryLifecycle.LastScopeInvalidationReceipt,
                state.PersistentState.RuntimeUnlockRetryLifecycle.LastScopeInvalidationReceipt);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AssertNoRetirement()
        {
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            Assert.Null(state.PersistentState.FileTransferRouteTransition.CurrentOperation);
            Assert.Equal(FileTransferRouteTransitionState.Invalidated,
                state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.State);
            Assert.Equal(before.PersistentState.FallbackTailYield.CurrentOperation, state.PersistentState.FallbackTailYield.CurrentOperation);
            Assert.Equal(FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
                state.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
            Assert.Null(state.PersistentState.RuntimeUnlockRetryLifecycle.PendingScopeInvalidationCommand);
            Assert.Null(state.PersistentState.RuntimeUnlockRetryLifecycle.LastScopeInvalidationReceipt);
            Assert.Equal(installedReceipt, (object?)GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
            Assert.Equal(before.CurrentRoutePublicationToken, state.CurrentRoutePublicationToken);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void MutateStaleObservation()
        {
            if (control == "raw_transfer_conflict")
            {
                var transaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(host, "runtimeUnlockTransactionState"));
                SetPrivateField(host, "runtimeUnlockTransactionState", transaction with { TransferId = "foreign-transfer" });
            }
            else if (control == "superseded_launch")
            {
                var started = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(GetPrivateField(host, "runtimeUnlockPreCommitProbeStartedSnapshot"));
                SetPrivateField(host, "runtimeUnlockPreCommitProbeStartedSnapshot", started with { LaunchGeneration = started.LaunchGeneration + 1 });
            }
            else
            {
                var lease = Assert.IsType<RuntimeUnlockTunaPathLeaseSnapshot>(GetPrivateField(host, "runtimeUnlockTunaPathLeaseState"));
                SetPrivateField(host, "runtimeUnlockTunaPathLeaseState", control == "retired_lease"
                    ? lease with { State = RuntimeUnlockTunaPathLeaseState.Retired }
                    : lease with { TransferId = "foreign-transfer" });
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AssertNoSuccessor()
        {
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            Assert.Equal(before.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision,
                state.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision);
            Assert.Equal(before.CurrentRoutePublicationToken, state.CurrentRoutePublicationToken);
        }
    }
}
