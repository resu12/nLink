using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    // A first-install bridge only. It does not emulate offers, probes, route
    // proof, successor policy or scope invalidation, and never writes Core state.
    private sealed class QueuedRetryAuthorityLoopbackTransport(string sessionId) :
        PeerVisibleControlPlaneLoopbackTransport(sessionId), IRuntimeUnlockRetryDispatchAuthorityBridge
    {
        private readonly object retryGate = new();
        private object? coreGate;
        private object? coreContext;
        private string? boundTransferId;
        private FileTransferDirection boundDirection;
        private RuntimeUnlockRetryDispatchAuthorityRequest? capturedRequest;
        private RuntimeUnlockRetryDispatchDecision? stagedDecision;
        private RuntimeUnlockRetryDispatchInstallReceipt? proposedReceipt;
        private long stagedUtcMs;
        private long expiresUtcMs;
        private bool drained;
        private EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? authorityRequested;

        public RuntimeUnlockRetryDispatchAuthorityRequest? CapturedRequest { get { lock (retryGate) { return capturedRequest; } } }
        public RuntimeUnlockRetryDispatchDecision? LastDecision { get; private set; }
        public RuntimeUnlockRetryDispatchDecisionStageResult? LastStageResult { get; private set; }
        public RuntimeUnlockRetryDispatchInstallResult? LastInstallResult { get; private set; }
        public RuntimeUnlockRetryDispatchInstallReceipt? LastCommittedReceipt { get; private set; }
        public RuntimeUnlockRetryDispatchContract? InfraCurrentContract { get; private set; }
        public int InstallCount { get; private set; }
        public bool LastStageObservedUnderCoreGate { get; private set; }
        public bool LastCommitObservedUnderCoreGate { get; private set; }
        public bool LastConfirmationObservedUnderCoreGate { get; private set; }
        public long LastConfirmationCoordinatorGeneration { get; private set; }
        public bool ReturnMismatchedInstallReceipt { get; set; }
        public bool RejectConfirmation { get; set; }

        public event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? RuntimeUnlockRetryDispatchAuthorityRequested
        { add { lock (retryGate) { authorityRequested += value; } } remove { lock (retryGate) { authorityRequested -= value; } } }
        public event EventHandler<RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs>? RuntimeUnlockRetryDispatchInitialEvaluationRequested
        { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>? RuntimeUnlockRetryDispatchSettlementReported
        { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs>? RuntimeUnlockRetryDispatchDeadlineEvaluationRequested
        { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchExecutionObservedEventArgs>? RuntimeUnlockRetryDispatchExecutionObserved
        { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs>? RuntimeUnlockRetryDispatchSuccessorEvaluationRequested
        { add { } remove { } }

        public void BindCoreContext(SessionFileTransferService service, object context)
        {
            var gate = GetServiceGate(service);
            string transferId;
            FileTransferDirection direction;
            lock (gate)
            {
                var state = CreateCoordinatorStateForTest(context);
                if (state.SessionId != CurrentFileTransferSessionId) { throw new InvalidOperationException("Retry bridge session does not match Core."); }
                transferId = state.TransferId;
                direction = state.Direction;
            }
            lock (retryGate)
            {
                if (coreContext is not null) { throw new InvalidOperationException("Retry bridge is already bound."); }
                coreGate = gate;
                coreContext = context;
                boundTransferId = transferId;
                boundDirection = direction;
            }
        }

        public bool QueueRuntimeUnlockRetryDispatchAuthorityRequest(RuntimeUnlockRetryDispatchAuthorityRequest request)
        {
            lock (retryGate)
            {
                if (capturedRequest is { } retained) { return retained == request; }
                if (coreContext is null || request.IsDefault || request.SessionId != CurrentFileTransferSessionId ||
                    request.TransferId != boundTransferId || request.Direction != boundDirection ||
                    request.FallbackPairedTailYieldEvidence is null || request.PredecessorAuthorityId is not null ||
                    request.RequestActivePredecessorSupersession || request.MissingDispatchSuccessorGeneration != 0 ||
                    authorityRequested?.GetInvocationList().Length != 1) { return false; }
                capturedRequest = request;
                return true;
            }
        }

        public bool DrainQueuedAuthorityRequest()
        {
            if (coreGate is null || Monitor.IsEntered(coreGate))
            { throw new InvalidOperationException("Drain the retry request outside the bound service gate."); }
            RuntimeUnlockRetryDispatchAuthorityRequest request;
            EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs> handler;
            lock (retryGate)
            {
                if (drained || capturedRequest is not { } queued || authorityRequested is not { } subscriber ||
                    subscriber.GetInvocationList().Length != 1) { return false; }
                drained = true;
                request = queued;
                handler = subscriber;
            }
            handler(this, new RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(request));
            return true;
        }

        public RuntimeUnlockRetryDispatchDecisionStageResult StageRuntimeUnlockRetryDispatchDecision(
            RuntimeUnlockRetryDispatchDecision decision)
        {
            RuntimeUnlockRetryDispatchAuthorityRequest? request;
            lock (retryGate) { request = drained ? capturedRequest : null; }
            LastStageObservedUnderCoreGate = coreGate is not null && Monitor.IsEntered(coreGate);
            var current = false;
            if (!LastStageObservedUnderCoreGate && request is { } expected && coreGate is { } gate && coreContext is { } context &&
                FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(decision, expected, out _))
            {
                lock (gate)
                {
                    var state = CreateCoordinatorStateForTest(context);
                    current = FileTransferCoordinator.IsRuntimeUnlockRetryDispatchAuthorityCurrent(
                        state, decision.Contract!.Authority, requireInstalled: false, out _) &&
                        state.PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt is
                            { State: RuntimeUnlockRetryLifecycleAttemptState.Authorized } attempt &&
                        attempt.Request == expected;
                }
            }
            RuntimeUnlockRetryDispatchDecisionStageResult result;
            lock (retryGate)
            {
                LastDecision = decision;
                if (!current || capturedRequest != request || stagedDecision is not null || proposedReceipt is not null ||
                    InfraCurrentContract is not null)
                {
                    return (LastStageResult = new RuntimeUnlockRetryDispatchDecisionStageResult(
                        RuntimeUnlockRetryDispatchDecisionStageStatus.Rejected, "joined_retry_stage_not_exact_first_request", decision)
                    { StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.Rejected }).Value;
                }
                stagedDecision = decision;
                stagedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                expiresUtcMs = stagedUtcMs + 15_000;
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Staged, "joined_retry_decision_staged", decision)
                {
                    StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.Retained,
                    StagedUtcMs = stagedUtcMs, StageExpiresUtcMs = expiresUtcMs,
                };
                LastStageResult = result;
            }
            return result;
        }

        public RuntimeUnlockRetryDispatchInstallResult CommitRuntimeUnlockRetryDispatchInstall(
            RuntimeUnlockRetryDispatchInstallReceipt receipt)
        {
            // The production service calls this while holding its gate, after
            // adopting the Coordinator-issued receipt. Read it, never issue one.
            LastCommitObservedUnderCoreGate = coreGate is not null && Monitor.IsEntered(coreGate);
            var current = LastCommitObservedUnderCoreGate && coreContext is not null &&
                CreateCoordinatorStateForTest(coreContext).PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt is
                    { State: RuntimeUnlockRetryLifecycleAttemptState.Staged } attempt &&
                attempt.InstallReceipt == receipt && attempt.Request == receipt.Request && attempt.Authority == receipt.Authority;
            lock (retryGate)
            {
                if (!current || receipt.IsDefault || stagedDecision is not { Contract: { } contract } decision ||
                    capturedRequest != receipt.Request || decision.Request != receipt.Request || contract.Authority != receipt.Authority ||
                    proposedReceipt is not null || InfraCurrentContract is not null ||
                    receipt.StageStatus != RuntimeUnlockRetryDispatchDecisionStageStatus.Staged ||
                    receipt.StageDisposition != RuntimeUnlockRetryDispatchStageDisposition.Retained || receipt.StageCleared ||
                    receipt.StagedUtcMs != stagedUtcMs || receipt.StageExpiresUtcMs != expiresUtcMs)
                {
                    return (LastInstallResult = new RuntimeUnlockRetryDispatchInstallResult(
                        RuntimeUnlockRetryDispatchInstallStatus.Rejected, "joined_retry_install_receipt_not_exact", receipt)).Value;
                }
                proposedReceipt = receipt;
                var returnedReceipt = ReturnMismatchedInstallReceipt
                    ? receipt with { InstallGeneration = receipt.InstallGeneration + 1 }
                    : receipt;
                return (LastInstallResult = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Installed, "joined_retry_install_proposed", returnedReceipt)).Value;
            }
        }

        public bool ConfirmRuntimeUnlockRetryDispatchInstall(RuntimeUnlockRetryDispatchInstallReceipt receipt)
        {
            LastConfirmationObservedUnderCoreGate = coreGate is not null && Monitor.IsEntered(coreGate);
            if (LastConfirmationObservedUnderCoreGate && coreContext is not null)
            {
                LastConfirmationCoordinatorGeneration = GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    coreContext, "CoordinatorPersistentState").SnapshotStateGeneration;
            }
            var current = LastConfirmationObservedUnderCoreGate && coreContext is not null &&
                CreateCoordinatorStateForTest(coreContext).PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt is
                    { State: RuntimeUnlockRetryLifecycleAttemptState.Installed } attempt &&
                attempt.InstallReceipt == receipt && attempt.Authority == receipt.Authority && attempt.Request == receipt.Request;
            lock (retryGate)
            {
                if (RejectConfirmation || !current || proposedReceipt != receipt || stagedDecision is not { Contract: { } contract } ||
                    contract.Authority != receipt.Authority || capturedRequest != receipt.Request || InfraCurrentContract is not null)
                { return false; }
                InfraCurrentContract = contract;
                LastCommittedReceipt = receipt;
                proposedReceipt = null;
                stagedDecision = null;
                InstallCount++;
                return true;
            }
        }

        public bool ObserveRuntimeUnlockRetryDispatchNonGrantDecision(RuntimeUnlockRetryDispatchDecision decision)
        {
            lock (retryGate)
            {
                if (decision.Granted || !drained || capturedRequest != decision.Request || InfraCurrentContract is not null)
                { return false; }
                LastDecision = decision;
                stagedDecision = null;
                proposedReceipt = null;
                return true;
            }
        }

        public bool ObserveRuntimeUnlockRetryDispatchSettlementDecision(RuntimeUnlockRetryDispatchSettlementDecision decision) => false;
        public RuntimeUnlockRetryDispatchScopeInvalidationStageResult StageRuntimeUnlockRetryDispatchScopeInvalidation(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command) => new(command,
                RuntimeUnlockRetryDispatchScopeInvalidationStageStatus.Rejected, "joined_retry_scope_invalidation_not_supported");
        public RuntimeUnlockRetryDispatchScopeInvalidationReceipt ApplyRuntimeUnlockRetryDispatchScopeInvalidation(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command) => new(command,
                RuntimeUnlockRetryDispatchScopeInvalidationStatus.Rejected, "joined_retry_scope_invalidation_not_supported");
    }
}
