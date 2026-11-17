using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Reflection;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private static async Task AssertJoinedIndependentInstallAfterEofAsync(SessionFileTransferService sender,
        object outbound, NknSignalingTransport host, RuntimeUnlockRetryDispatchInstallReceipt oldReceipt,
        ConcurrentQueue<Envelope> offers, int logStart, RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs eof)
    {
        await WaitUntilAsync(() => HasNewInstalledReceipt() ||
            ReadOperationalLogTail(logStart).Contains("event=runtime_unlock_retry_dispatch_install_confirmation_rejected;",
                StringComparison.Ordinal), TimeSpan.FromSeconds(8));
        var rejected = ReadOperationalLogTail(logStart).Split('\n').Where(line =>
            line.Contains("event=runtime_unlock_retry_dispatch_install_confirmation_rejected;", StringComparison.Ordinal));
        Assert.True(!rejected.Any(), "Next independent retry rejected: " + string.Join("\n", rejected));
        await WaitUntilAsync(HasNewDispatchObserved, TimeSpan.FromSeconds(5));
        Assert.True(offers.Select(offer => offer.MessageId).Distinct().Count() >= 2);
        AssertOldRetirementCannotTouchSuccessor();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AssertOldRetirementCannotTouchSuccessor()
        {
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            var retirement = state.PersistentState.RuntimeUnlockRetryLifecycle.LastScopeInvalidationReceipt!.Value;
            var current = GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host);
            Assert.NotEqual(oldReceipt, current);
            var bridge = (IRuntimeUnlockRetryDispatchAuthorityBridge)host;
            Assert.False(bridge.StageRuntimeUnlockRetryDispatchScopeInvalidation(retirement.Command).Accepted);
            Assert.False(bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(retirement.Command).Accepted);
            var staleCommit = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(state, retirement);
            Assert.False(staleCommit.Accepted);
            Assert.Same(state, staleCommit.State);
            var replay = new RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs(eof.Observation);
            typeof(SessionFileTransferService).GetMethod("OnRuntimeUnlockPostFallbackPreCommitProbeFailureObserved",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, [host, replay]);
            Assert.NotEqual(RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Accepted, replay.Decision.Status);
            Assert.Equal(current, GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
            var after = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            Assert.Equal(state.PersistentState.RuntimeUnlockRetryLifecycle, after.PersistentState.RuntimeUnlockRetryLifecycle);
            Assert.Equal(state.PersistentState.FallbackTailYield, after.PersistentState.FallbackTailYield);
            var scope = GetPrivateField(host, "runtimeUnlockRetryPhysicalSendScope");
            if (scope is not null)
            {
                Assert.Equal(current, scope.GetType().GetProperty("InstallReceipt")!.GetValue(scope));
                Assert.False((bool)scope.GetType().GetProperty("IsCancellationRequested")!.GetValue(scope)!);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool HasNewInstalledReceipt()
            => GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host) is { } receipt &&
                receipt.InstallGeneration > oldReceipt.InstallGeneration && receipt.Authority != oldReceipt.Authority;

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool HasNewDispatchObserved()
        {
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
            var terminal = state.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt;
            if (terminal?.Settlement?.Outcome != RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved ||
                terminal.Value.Authority == oldReceipt.Authority) { return false; }
            Assert.Null(terminal.Value.Authority.PredecessorAuthorityId);
            Assert.True(terminal.Value.Authority.AuthorityGeneration > oldReceipt.Authority.AuthorityGeneration);
            Assert.Equal(terminal.Value.InstallReceipt, GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
            Assert.Equal(oldReceipt.Authority.SourcePublicationAuthority,
                terminal.Value.Authority.SourcePublicationAuthority);
            Assert.Equal(FileTransferRoute.PostTunaFallbackV6, state.RouteSelection.Route);
            return true;
        }
    }
}
