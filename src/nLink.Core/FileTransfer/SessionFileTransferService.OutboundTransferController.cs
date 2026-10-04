using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private bool IsTransferDegradedLocked()
        => (outboundTransfer is not null &&
            !outboundTransfer.IsTerminal &&
            outboundTransfer.PullSessionDegraded) ||
           (inboundTransfer is not null &&
            !inboundTransfer.IsTerminal &&
            (inboundTransfer.PullSessionDegraded ||
             inboundTransfer.BulkFallbackModeActive ||
             inboundTransfer.DegradedRepairModeActive));

    private bool IsCatchUpOnlyPressureActiveLocked()
        => false;

    private void UpdateOutboundPressureDerivedStateLocked(OutboundTransferContext context)
    {
        context.CurrentRepairBatchSize = RepairBatchSize;
    }

    private static void UpdateOutboundAcknowledgedProgressLocked(OutboundTransferContext context)
    {
        var acknowledgedChunks = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acknowledgedBytes = acknowledgedChunks >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)acknowledgedChunks * context.ChunkSizeBytes);

        context.BytesTransferred = acknowledgedBytes;
        context.ChunksTransferred = acknowledgedChunks;
        context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, acknowledgedBytes);
    }

    private Task<bool> TransitionOutboundToTerminalAsync(
        OutboundTransferContext context,
        FileTransferTransferState terminalState,
        string? errorCode,
        string statusMessage,
        bool notifyPeer,
        string? cancelReason,
        CancellationToken ct,
        OutboundSparsePumpInstall? requiredSparsePumpInstall = null)
        => TransitionOutboundToTerminalCoreAsync(context, terminalState, errorCode, statusMessage, notifyPeer,
            cancelReason, ct, requiredSparsePumpInstall, null);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool TryPrepareOutboundTerminalAuthorityLocked(OutboundTransferContext context, FileTransferTransferState terminalState,
        string? errorCode, OutboundSparsePumpInstall? requiredSparsePumpInstall, OutboundBootstrapSourceCallback? requiredBootstrapCallback)
    {
        if (requiredBootstrapCallback is not null &&
            ResolveBootstrapSourceCallbackLocked(context, requiredBootstrapCallback) != FileTransferBootstrapSourceCallbackDisposition.Current)
        { return false; }
        if (!ReferenceEquals(outboundTransfer, context))
        {
            return false;
        }

        if (context.IsTerminal)
        {
            LogTerminalStaleUpdateRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.State,
                context.ErrorCode,
                terminalState,
                NormalizeErrorCode(errorCode),
                "transition_outbound_terminal",
                "already_terminal");
            return false;
        }

        if (requiredSparsePumpInstall is not null &&
            !IsOutboundSparsePumpInstallCurrentLocked(
                context,
                requiredSparsePumpInstall,
                context.DataSession,
                out var sparseInstallRejectionReason))
        {
            LogOutboundSparsePumpInstallRejected(
                context,
                requiredSparsePumpInstall,
                "terminal_transition",
                sparseInstallRejectionReason);
            return false;
        }

        if (context.CoordinatorPersistentState.FileTransferRouteTransition
                .CurrentOperation is { } activeRouteTransition)
        {
            InvalidateOutboundFileTransferRouteTransitionLocked(
                context,
                activeRouteTransition.Authority,
                terminalInvalidation: true,
                "outbound_transfer_terminal");

            if (context.CoordinatorPersistentState.FileTransferRouteTransition
                    .CurrentOperation is not null)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_terminal_transition_rejected; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=regular_v4_to_tuna_transition_invalidation_failed");
                return false;
            }
        }

        if (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                "outbound_transfer_terminal",
                FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal) ||
            !SettleAndClearRegularV4FrontierRepairContractLocked(
                context,
                FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal,
                "outbound_transfer_terminal"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_terminal_transition_rejected; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
            return false;
        }

        if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                context,
                "retry_dispatch_outbound_transfer_terminal"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_terminal_transition_rejected; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=runtime_unlock_retry_scope_invalidation_failed");
            return false;
        }
        return true;
    }

    private async Task<bool> TransitionOutboundToTerminalCoreAsync(
        OutboundTransferContext context, FileTransferTransferState terminalState, string? errorCode,
        string statusMessage, bool notifyPeer, string? cancelReason, CancellationToken ct,
        OutboundSparsePumpInstall? requiredSparsePumpInstall, OutboundBootstrapSourceCallback? requiredBootstrapCallback)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldNotifyPeer;
        IFileTransferDataSession? dataSessionToDispose = null;
        string sessionId;
        string transferId;
        string? normalizedCancelReason;
        int dataCancelProtocolVersion;
        long dataCancelTransportEpoch;
        IFileTransferRouteCompletionObserver? routeCompletionObserver;
        IFileTransferTerminalObserver? terminalObserver;
        string? normalizedErrorCodeForNotify;
        IFileTransferBridgeRecoveryExecutionBridge? bridgeRecoveryExecutionBridge = null;
        FileTransferBridgeRecoveryExecutionCommand? bridgeRecoveryExecutionCommand = null;
        BridgeRecoveryExecutionDispatchOrdering? bridgeRecoveryExecutionDispatchOrdering = null;

        lock (gate)
        {
            if (!TryPrepareOutboundTerminalAuthorityLocked(context, terminalState, errorCode, requiredSparsePumpInstall, requiredBootstrapCallback))
            { return false; }

            RetireBootstrapSourceCallbackLocked(context, requiredBootstrapCallback, "terminal");
            context.CancelLifetime();
            SettleAndClearFallbackFrontierRepairContractLocked(
                context,
                FallbackFrontierRepairSettlementOutcome.TransferTerminal,
                "outbound_transfer_terminal");
            ConsumeOutboundFallbackTailTerminalProofBeforeTerminalLocked(context, terminalState, "transfer_terminal");
            bridgeRecoveryExecutionBridge =
                transport as IFileTransferBridgeRecoveryExecutionBridge;
            bridgeRecoveryExecutionCommand =
                context.CurrentBridgeRecoveryExecutionCommand;
            bridgeRecoveryExecutionDispatchOrdering =
                context.BridgeRecoveryExecutionDispatchOrdering;
            var terminalDecision = FileTransferCoordinator.Apply(
                new FileTransferCoordinatorEvent(
                    FileTransferCoordinatorEventKind.Terminalized,
                    context.RouteSelection,
                    FileTransferTransportHandoffKind.None,
                    FileTransferTransportKind.Unknown,
                    "transfer_terminal",
                    context.CurrentTransferLeg?.State ?? FileTransferLegState.Terminal,
                    CanSendData: false,
                    Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    context.V6TransportEpoch?.EpochId ?? context.LastRecoveredV6TransportEpoch,
                    Math.Max(0, context.PullTransportRebindGeneration)),
                CreateCoordinatorStateLocked(context));
            if (string.IsNullOrWhiteSpace(terminalDecision.MutationRejectedReason))
            {
                terminalDecision = terminalDecision with { State = AdoptCoordinatorStateLocked(context, terminalDecision) };
                if (terminalDecision.TerminalFallbackProjectionRetirement is { } retirement &&
                    transport is IFileTransferRouteTransitionBridge bridge)
                    bridge.ApplyTerminalFallbackProjectionRetirement(retirement);
                ApplyCoordinatorDecisionToContextLocked(context, terminalDecision, adoptCoordinatorState: false);
                RetireRuntimeOwnedWorkersLocked(context, context.CurrentRuntimeOwner, "transfer_terminal");
            }
            else
            {
                LogFileTransferCoordinatorLegMutationRejected(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    FileTransferCoordinatorActionKind.ProgressMutation,
                    "terminalize_transfer",
                    terminalDecision,
                    context.CurrentTransferLeg,
                    context.CurrentTransferLeg?.CheckpointRequestId,
                    context.CurrentTransferLeg?.TransportEpochId ?? 0,
                    "transfer_terminal");
            }

            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            ClearOutboundFallbackCheckpointRetryStateLocked(context, null, "transfer_terminal");
            context.UserPaused = false;
            context.UserPauseReason = null;
            context.UserPausedSinceUtc = null;
            context.PeerPaused = false;
            context.PeerPauseReason = null;
            context.PeerPausedSinceUtc = null;
            dataCancelProtocolVersion = context.NegotiatedDataProtocolVersion;
            dataCancelTransportEpoch = context.V6TransportEpoch?.EpochId ?? context.LastRecoveredV6TransportEpoch;
            normalizedErrorCodeForNotify = context.ErrorCode;
            if (context.V6TransportEpoch is { } epoch)
            {
                TerminalizeV6TransportEpochLocked(FileTransferDirection.Outbound, context.TransferId, context.SessionId, epoch, "transfer_terminal");
                context.V6TransportEpoch = null;
            }
            if (terminalState == FileTransferTransferState.Completed)
            {
                context.BytesTransferred = context.FileSizeBytes;
                context.ChunksTransferred = context.ChunkCount;
                context.BytesAcceptedForTransport = context.FileSizeBytes;
                context.ChunksAcceptedForTransport = context.ChunkCount;
                context.BytesAcknowledgedByReceiver = context.FileSizeBytes;
            }

            snapshot = CreateSnapshotLocked();
            shouldNotifyPeer = notifyPeer;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedCancelReason = NormalizeReason(cancelReason) ?? CanceledReason;
            dataSessionToDispose = context.DetachDataSession();
            routeCompletionObserver = transport as IFileTransferRouteCompletionObserver;
            terminalObserver = transport as IFileTransferTerminalObserver;
        }

        if (bridgeRecoveryExecutionBridge is not null &&
            bridgeRecoveryExecutionCommand is { IsDefault: false } outboundExecutionCommand)
        {
            InvalidateBridgeRecoveryExecutionOrderedOutsideLock(
                bridgeRecoveryExecutionDispatchOrdering,
                bridgeRecoveryExecutionBridge,
                outboundExecutionCommand,
                "coordinator_terminalized");
        }

        LogV4EfficiencySummary(context, terminalState);
        RaiseTransferChanged(snapshot);
        try
        {
            if (shouldNotifyPeer)
            {
                if (terminalState == FileTransferTransferState.Canceled)
                {
                    var controlCancelTask = SendCancelAsync(sessionId, transferId, normalizedCancelReason, CancellationToken.None);
                    Task? redundantDataCancelTask = null;
                    var redundantDataSession = dataSessionToDispose;
                    if (redundantDataSession is not null)
                    {
                        redundantDataCancelTask = TrySendCancelDataFrameAsync(
                                redundantDataSession,
                                sessionId,
                                transferId,
                                normalizedCancelReason,
                                FileTransferDirection.Outbound,
                                dataCancelProtocolVersion,
                                dataCancelTransportEpoch,
                                "terminal_redundant",
                                CancellationToken.None);
                    }

                    if (redundantDataCancelTask is not null)
                    {
                        await redundantDataCancelTask.ConfigureAwait(false);
                        StartCancelDataFrameRetryLoop(
                            redundantDataSession!,
                            sessionId,
                            transferId,
                            normalizedCancelReason,
                            FileTransferDirection.Outbound,
                            dataCancelProtocolVersion,
                            dataCancelTransportEpoch,
                            "terminal_redundant");
                        dataSessionToDispose = null;
                    }

                    await controlCancelTask.ConfigureAwait(false);
                }
                else if (terminalState == FileTransferTransferState.Failed)
                {
                    Task? redundantDataErrorTask = null;
                    var redundantDataSession = dataSessionToDispose;
                    if (IsPeerDisconnectedErrorCode(normalizedErrorCodeForNotify) &&
                        redundantDataSession is not null)
                    {
                        redundantDataErrorTask = TrySendErrorDataFrameAsync(
                            redundantDataSession,
                            sessionId,
                            transferId,
                            normalizedErrorCodeForNotify ?? DisconnectedErrorCode,
                            context.StatusMessage,
                            FileTransferDirection.Outbound,
                            dataCancelProtocolVersion,
                            dataCancelTransportEpoch,
                            "terminal_redundant",
                            CancellationToken.None);
                    }

                    if (redundantDataErrorTask is not null)
                    {
                        await redundantDataErrorTask.ConfigureAwait(false);
                        StartErrorDataFrameRetryLoop(
                            redundantDataSession!,
                            sessionId,
                            transferId,
                            normalizedErrorCodeForNotify ?? DisconnectedErrorCode,
                            context.StatusMessage,
                            FileTransferDirection.Outbound,
                            dataCancelProtocolVersion,
                            dataCancelTransportEpoch,
                            "terminal_redundant");
                        dataSessionToDispose = null;
                    }

                    await SendErrorAsync(sessionId, transferId, context.ErrorCode ?? InvalidStateErrorCode, context.StatusMessage, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            dataSessionToDispose?.Dispose();
            context.DisposeResources();
        }

        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId: context.SessionId,
            errorCode: context.ErrorCode,
            reason: context.StatusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            routeSelection: context.RouteSelection);
        LogLiveRouteEpochRecoveredBeforeTerminalIfCompleted(
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId,
            context.CurrentLiveRouteEpoch,
            terminalState,
            context.StatusMessage);
        LogLiveRouteEpochTerminal(
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId,
            context.CurrentLiveRouteEpoch,
            terminalState,
            context.StatusMessage);
        NotifyTransferTerminal(
            terminalObserver,
            FileTransferDirection.Outbound,
            context.RouteSelection,
            terminalState,
            sessionId,
            transferId,
            context.ErrorCode,
            context.StatusMessage);
        NotifyRouteCompletedIfNeeded(routeCompletionObserver, context.RouteSelection, terminalState, sessionId, transferId);
        return true;
    }

    private Task TransitionInboundToTerminalAsync(
        InboundTransferContext context, FileTransferTransferState terminalState, string? errorCode,
        string statusMessage, bool sendError, string? errorMessage, string? cancelReason, CancellationToken ct)
        => TransitionInboundToTerminalCoreAsync(context, terminalState, errorCode, statusMessage,
            sendError, errorMessage, cancelReason, ct, requiredFeedbackOperation: null);

    private Task TransitionInboundToTerminalCoreAsync(
        InboundTransferContext context, FileTransferTransferState terminalState, string? errorCode, string statusMessage,
        bool sendError, string? errorMessage, string? cancelReason, CancellationToken ct,
        InboundBootstrapFeedbackOperation? requiredFeedbackOperation, InboundBootstrapWriteSource? requiredWriteSource = null)
        => TransitionInboundToTerminalWithFinalizationSourceAsync(context, terminalState, errorCode, statusMessage,
            sendError, errorMessage, cancelReason, ct, requiredFeedbackOperation, requiredWriteSource, null);

    private async Task TransitionInboundToTerminalWithFinalizationSourceAsync(
        InboundTransferContext context,
        FileTransferTransferState terminalState,
        string? errorCode,
        string statusMessage,
        bool sendError,
        string? errorMessage,
        string? cancelReason,
        CancellationToken ct,
        InboundBootstrapFeedbackOperation? requiredFeedbackOperation,
        InboundBootstrapWriteSource? requiredWriteSource = null,
        InboundFinalizationSource? requiredFinalizationSource = null)
    {
        NativeDestinationEvidence? completedNativeDestination = null;
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldSendError;
        string sessionId;
        string transferId;
        string? normalizedErrorCode;
        IFileTransferDataSession? dataSessionToDispose = null;
        string? normalizedCancelReason;
        bool shouldSendCancel;
        int dataCancelProtocolVersion;
        long dataCancelTransportEpoch;
        IFileTransferRouteCompletionObserver? routeCompletionObserver;
        IFileTransferTerminalObserver? terminalObserver;
        string? normalizedErrorCodeForNotify;
        IFileTransferBridgeRecoveryExecutionBridge? bridgeRecoveryExecutionBridge = null;
        FileTransferBridgeRecoveryExecutionCommand? bridgeRecoveryExecutionCommand = null;
        BridgeRecoveryExecutionDispatchOrdering? bridgeRecoveryExecutionDispatchOrdering = null;

        lock (gate)
        {
            if (requiredFinalizationSource is not null && !IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource)) { return; }
            if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return; }
            if (requiredFeedbackOperation is not null && !IsInboundBootstrapFeedbackCurrentLocked(context, requiredFeedbackOperation))
            {
                return;
            }
            if (!ReferenceEquals(inboundTransfer, context))
            {
                return;
            }

            if (context.IsTerminal)
            {
                LogTerminalStaleUpdateRejected(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.State,
                    context.ErrorCode,
                    terminalState,
                    NormalizeErrorCode(errorCode),
                    "transition_inbound_terminal",
                    "already_terminal");
                return;
            }

            if (context.CoordinatorPersistentState.FileTransferRouteTransition
                    .CurrentOperation is { } activeRouteTransition)
            {
                InvalidateInboundFileTransferRouteTransitionLocked(
                    context,
                    activeRouteTransition.Authority,
                    terminalInvalidation: true,
                    "inbound_transfer_terminal");
                if (context.CoordinatorPersistentState.FileTransferRouteTransition
                        .CurrentOperation is not null)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_terminal_transition_rejected; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=regular_v4_to_tuna_transition_invalidation_failed");
                    return;
                }
            }

            if (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                    context,
                    "inbound_transfer_terminal",
                    FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal) ||
                !SettleAndClearRegularV4FrontierRepairContractLocked(
                    context,
                    FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal,
                    "inbound_transfer_terminal"))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_terminal_transition_rejected; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
                return;
            }

            if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                    context,
                    "retry_dispatch_inbound_transfer_terminal"))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_terminal_transition_rejected; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=runtime_unlock_retry_scope_invalidation_failed");
                return;
            }

            // Only the already-validated exact finalization continuation can
            // hand off storage custody. This grants no route/completion authority.
            var nativeDestinationSourceCurrent = terminalState == FileTransferTransferState.Completed &&
                requiredFinalizationSource is not null && IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource);
            context.CancelLifetime();
            SettleAndClearFallbackFrontierRepairContractLocked(
                context,
                FallbackFrontierRepairSettlementOutcome.TransferTerminal,
                "inbound_transfer_terminal");
            ConsumeInboundFallbackTailTerminalProofBeforeTerminalLocked(context, terminalState, "transfer_terminal");
            bridgeRecoveryExecutionBridge =
                transport as IFileTransferBridgeRecoveryExecutionBridge;
            bridgeRecoveryExecutionCommand =
                context.CurrentBridgeRecoveryExecutionCommand;
            bridgeRecoveryExecutionDispatchOrdering =
                context.BridgeRecoveryExecutionDispatchOrdering;
            var terminalDecision = FileTransferCoordinator.Apply(
                new FileTransferCoordinatorEvent(
                    FileTransferCoordinatorEventKind.Terminalized,
                    context.RouteSelection,
                    FileTransferTransportHandoffKind.None,
                    FileTransferTransportKind.Unknown,
                    "transfer_terminal",
                    context.CurrentTransferLeg?.State ?? FileTransferLegState.Terminal,
                    CanSendData: false,
                    Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    Math.Max(-1, context.PullHighestReceivedChunkIndex),
                    context.V6TransportEpoch?.EpochId ?? context.V6ReceiverTransportEpoch,
                    Math.Max(0, context.PullTransportRebindGeneration)),
                CreateCoordinatorStateLocked(context));
            if (string.IsNullOrWhiteSpace(terminalDecision.MutationRejectedReason))
            {
                terminalDecision = terminalDecision with { State = AdoptCoordinatorStateLocked(context, terminalDecision) };
                if (terminalDecision.TerminalFallbackProjectionRetirement is { } retirement &&
                    transport is IFileTransferRouteTransitionBridge bridge)
                    bridge.ApplyTerminalFallbackProjectionRetirement(retirement);
                ApplyCoordinatorDecisionToContextLocked(context, terminalDecision, adoptCoordinatorState: false);
                RetireRuntimeOwnedWorkersLocked(context, context.CurrentRuntimeOwner, "transfer_terminal");
            }
            else
            {
                LogFileTransferCoordinatorLegMutationRejected(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    FileTransferCoordinatorActionKind.ProgressMutation,
                    "terminalize_transfer",
                    terminalDecision,
                    context.CurrentTransferLeg,
                    context.CurrentTransferLeg?.CheckpointRequestId,
                    context.CurrentTransferLeg?.TransportEpochId ?? 0,
                    "transfer_terminal");
            }

            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            context.AcceptInProgress = false;
            context.UserPaused = false;
            context.UserPauseReason = null;
            context.UserPausedSinceUtc = null;
            context.PeerPaused = false;
            context.PeerPauseReason = null;
            context.PeerPausedSinceUtc = null;
            dataCancelProtocolVersion = context.NegotiatedDataProtocolVersion;
            dataCancelTransportEpoch = context.V6TransportEpoch?.EpochId ?? context.V6ReceiverTransportEpoch;
            normalizedErrorCodeForNotify = context.ErrorCode;
            if (context.V6TransportEpoch is { } epoch)
            {
                TerminalizeV6TransportEpochLocked(FileTransferDirection.Inbound, context.TransferId, context.SessionId, epoch, "transfer_terminal");
                context.V6TransportEpoch = null;
                context.V6ReceiverTransportEpoch = 0;
            }
            snapshot = CreateSnapshotLocked();
            shouldSendError = sendError;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
            normalizedCancelReason = NormalizeReason(cancelReason) ?? CanceledReason;
            shouldSendCancel = terminalState == FileTransferTransferState.Canceled &&
                               !string.IsNullOrWhiteSpace(cancelReason);
            dataSessionToDispose = context.DetachDataSession();
            routeCompletionObserver = transport as IFileTransferRouteCompletionObserver;
            terminalObserver = transport as IFileTransferTerminalObserver;
            if (nativeDestinationSourceCurrent && ReferenceEquals(inboundTransfer, context) &&
                ReferenceEquals(context.WriteDestination, requiredFinalizationSource!.Destination) &&
                context.NativeDestinationEvidence is { } destinationEvidence &&
                destinationEvidence.TryComplete(this, context.WriteDestination!))
            {
                context.NativeDestinationEvidence = null; // Explicit app owner now releases the read-only hold.
                completedNativeDestination = destinationEvidence;
            }
            // Completed is observable as soon as this gate opens. Ordinary
            // downloads must be accessible then, not only after event callbacks.
            // An explicitly moved evidence hold is no longer owned by the guard.
            if (terminalState == FileTransferTransferState.Completed)
                context.WriteDestination?.ReleaseFinalizedNativeRead();
        }

        frontierAppLifetime?.TryStartNativeDestinationHandoff(completedNativeDestination);

        if (bridgeRecoveryExecutionBridge is not null &&
            bridgeRecoveryExecutionCommand is { IsDefault: false } inboundExecutionCommand)
        {
            InvalidateBridgeRecoveryExecutionOrderedOutsideLock(
                bridgeRecoveryExecutionDispatchOrdering,
                bridgeRecoveryExecutionBridge,
                inboundExecutionCommand,
                "coordinator_terminalized");
        }

        LogV4EfficiencySummary(context, terminalState);
        if (terminalState == FileTransferTransferState.Completed &&
            context.PullTransportRebindGeneration > 0)
        {
            LogInboundTransportRebindRecovered(
                context,
                "terminal_completed",
                context.PullTransportRebindGeneration,
                context.NextChunkIndex,
                context.PullHighestReceivedChunkIndex,
                context.BytesTransferred);
        }

        RaiseTransferChanged(snapshot);
        try
        {
            if (terminalState == FileTransferTransferState.Declined)
            {
                await SendDeclineAsync(sessionId, transferId, cancelReason ?? DeclinedReason, ct).ConfigureAwait(false);
            }
            else if (shouldSendCancel)
            {
                var controlCancelTask = SendCancelAsync(sessionId, transferId, normalizedCancelReason, CancellationToken.None);
                Task? redundantDataCancelTask = null;
                var redundantDataSession = dataSessionToDispose;
                if (redundantDataSession is not null)
                {
                    redundantDataCancelTask = TrySendCancelDataFrameAsync(
                            redundantDataSession,
                            sessionId,
                            transferId,
                            normalizedCancelReason,
                            FileTransferDirection.Inbound,
                            dataCancelProtocolVersion,
                            dataCancelTransportEpoch,
                            "terminal_redundant",
                            CancellationToken.None);
                }

                if (redundantDataCancelTask is not null)
                {
                    await redundantDataCancelTask.ConfigureAwait(false);
                    StartCancelDataFrameRetryLoop(
                        redundantDataSession!,
                        sessionId,
                        transferId,
                        normalizedCancelReason,
                        FileTransferDirection.Inbound,
                        dataCancelProtocolVersion,
                        dataCancelTransportEpoch,
                        "terminal_redundant");
                    dataSessionToDispose = null;
                }

                await controlCancelTask.ConfigureAwait(false);
            }
            else if (shouldSendError)
            {
                Task? redundantDataErrorTask = null;
                var redundantDataSession = dataSessionToDispose;
                if (IsPeerDisconnectedErrorCode(normalizedErrorCodeForNotify) &&
                    redundantDataSession is not null)
                {
                    redundantDataErrorTask = TrySendErrorDataFrameAsync(
                        redundantDataSession,
                        sessionId,
                        transferId,
                        normalizedErrorCodeForNotify ?? DisconnectedErrorCode,
                        errorMessage ?? statusMessage,
                        FileTransferDirection.Inbound,
                        dataCancelProtocolVersion,
                        dataCancelTransportEpoch,
                        "terminal_redundant",
                        CancellationToken.None);
                }

                if (redundantDataErrorTask is not null)
                {
                    await redundantDataErrorTask.ConfigureAwait(false);
                    StartErrorDataFrameRetryLoop(
                        redundantDataSession!,
                        sessionId,
                        transferId,
                        normalizedErrorCodeForNotify ?? DisconnectedErrorCode,
                        errorMessage ?? statusMessage,
                        FileTransferDirection.Inbound,
                        dataCancelProtocolVersion,
                        dataCancelTransportEpoch,
                        "terminal_redundant");
                    dataSessionToDispose = null;
                }

                await SendErrorAsync(sessionId, transferId, normalizedErrorCode ?? InvalidStateErrorCode, errorMessage ?? statusMessage, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            dataSessionToDispose?.Dispose();
            context.DisposeResources();
        }

        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            errorCode: normalizedErrorCode,
            reason: statusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            savedPath: context.SavedFilePath,
            routeSelection: context.RouteSelection);
        LogLiveRouteEpochRecoveredBeforeTerminalIfCompleted(
            FileTransferDirection.Inbound,
            transferId,
            sessionId,
            context.CurrentLiveRouteEpoch,
            terminalState,
            statusMessage);
        LogLiveRouteEpochTerminal(
            FileTransferDirection.Inbound,
            transferId,
            sessionId,
            context.CurrentLiveRouteEpoch,
            terminalState,
            statusMessage);
        NotifyTransferTerminal(
            terminalObserver,
            FileTransferDirection.Inbound,
            context.RouteSelection,
            terminalState,
            sessionId,
            transferId,
            normalizedErrorCode,
            statusMessage);
        NotifyRouteCompletedIfNeeded(routeCompletionObserver, context.RouteSelection, terminalState, sessionId, transferId);
    }

    private static void NotifyTransferTerminal(
        IFileTransferTerminalObserver? observer,
        FileTransferDirection direction,
        FileTransferRouteSelection routeSelection,
        FileTransferTransferState terminalState,
        string sessionId,
        string transferId,
        string? errorCode,
        string? reason)
    {
        observer?.ObserveFileTransferTerminal(
            new FileTransferTerminalNotification(
                sessionId,
                transferId,
                direction,
                routeSelection.TelemetryToken,
                routeSelection.ProtocolVersion,
                terminalState,
                errorCode,
                reason));
    }

    private static void NotifyRouteCompletedIfNeeded(
        IFileTransferRouteCompletionObserver? observer,
        FileTransferRouteSelection routeSelection,
        FileTransferTransferState terminalState,
        string sessionId,
        string transferId)
    {
        if (observer is null ||
            terminalState != FileTransferTransferState.Completed)
        {
            return;
        }

        observer.ObserveFileTransferRouteCompleted(
            new FileTransferRouteCompletedNotification(
                sessionId,
                transferId,
                routeSelection.TelemetryToken,
                routeSelection.ProtocolVersion));
    }

    private void UpdateOutboundState(
        OutboundTransferContext context,
        FileTransferTransferState state,
        long bytesTransferred,
        int chunksTransferred,
        string statusMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.State = state;
            context.BytesTransferred = bytesTransferred;
            context.ChunksTransferred = chunksTransferred;
            context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, bytesTransferred);
            context.StatusMessage = context.UserPaused
                ? "Transfer paused."
                : context.PeerPaused
                    ? "Peer paused transfer."
                    : statusMessage;
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private void SetInboundAcceptInProgress(InboundTransferContext context, bool acceptInProgress)
    {
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.AcceptInProgress = acceptInProgress;
            }
        }
    }

    private async Task SendDeclineAsync(string sessionId, string transferId, string? reason, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferDeclineAsync(
                new FileTransferDeclineV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"decline send failed: {ex.Message}");
        }
    }

    private async Task SendCancelAsync(string sessionId, string transferId, string? reason, CancellationToken ct)
    {
        var normalizedReason = NormalizeReason(reason) ?? CanceledReason;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            await TrySendCancelControlOnceAsync(sessionId, transferId, normalizedReason, attempt: 1, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(normalizedReason)}; path=control; error=timeout");
        }

        _ = Task.Run(
            () => RunCancelControlRetryLoopAsync(sessionId, transferId, normalizedReason),
            CancellationToken.None);
    }

    private async Task RunCancelControlRetryLoopAsync(string sessionId, string transferId, string? reason)
    {
        for (var index = 0; index < CancelRetryDelaysMs.Length; index++)
        {
            try
            {
                await Task.Delay(CancelRetryDelaysMs[index]).ConfigureAwait(false);
                await TrySendCancelControlOnceAsync(
                        sessionId,
                        transferId,
                        reason,
                        attempt: index + 2,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_cancel_control_retry_deferred; transfer_id={transferId}; session_id={sessionId}; attempt={index + 2}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; error=OperationCanceledException");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"cancel retry loop failed: {ex.Message}");
                return;
            }
        }
    }

    private async Task TrySendCancelControlOnceAsync(
        string sessionId,
        string transferId,
        string? reason,
        int attempt,
        CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferCancelAsync(
                new FileTransferCancelV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                },
                ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_cancel_control_sent; transfer_id={transferId}; session_id={sessionId}; attempt={attempt}");
            if (attempt == 1)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_lifecycle_priority_sent; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=control; attempt={attempt}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"cancel send failed (attempt={attempt}): {ex.Message}");
            if (attempt == 1)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=control; error={ex.GetType().Name}");
            }
        }
    }

    private async Task TrySendCancelDataFrameAsync(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string? reason,
        FileTransferDirection direction,
        int protocolVersion,
        long transportEpoch,
        string source,
        CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(CancelDataFrameBestEffortTimeoutMs));
            var frame = protocolVersion >= FileTransferProtocol.ProtocolVersionV6
                ? new FileTransferCancelFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                    TransportEpoch = Math.Max(0, transportEpoch),
                }
                : new FileTransferCancelFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                };
            if (protocolVersion >= FileTransferProtocol.ProtocolVersionV6)
            {
                var request = new FileTransferControlPlaneDeliveryRequest(
                    FileTransferControlPlaneKind.TransferCancel,
                    frame,
                    source)
                {
                    Direction = direction,
                    RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                    ProtocolVersion = protocolVersion,
                    TransportEpoch = Math.Max(0, transportEpoch),
                    PeerVisibleRequired = true,
                    IgnoreCallerCancellation = string.Equals(reason, "session_end", StringComparison.OrdinalIgnoreCase),
                    PeerCopyAttempts = 2,
                };
                await SendFileTransferControlPlaneOrDataSessionAsync(
                        dataSession,
                        request,
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                await dataSession.SendAsync(
                        frame,
                        timeout.Token)
                    .ConfigureAwait(false);
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_cancel_data_frame_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; protocol_version={protocolVersion}; transport_epoch={Math.Max(0, transportEpoch)}; source={source}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=redundant_data_frame; direction={direction.ToString().ToLowerInvariant()}; source={source}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; path=redundant_data_frame; error=timeout");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_cancel_data_frame_send_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; error={FormatProtocolLogValue(ex.Message)}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; path=redundant_data_frame; error={ex.GetType().Name}");
        }
    }

    private void StartCancelDataFrameRetryLoop(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string? reason,
        FileTransferDirection direction,
        int protocolVersion,
        long transportEpoch,
        string source)
    {
        _ = Task.Run(
            () => RunCancelDataFrameRetryLoopAsync(dataSession, sessionId, transferId, reason, direction, protocolVersion, transportEpoch, source),
            CancellationToken.None);
    }

    private async Task RunCancelDataFrameRetryLoopAsync(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string? reason,
        FileTransferDirection direction,
        int protocolVersion,
        long transportEpoch,
        string source)
    {
        try
        {
            for (var index = 0; index < CancelDataFrameRetryDelaysMs.Length; index++)
            {
                await Task.Delay(CancelDataFrameRetryDelaysMs[index]).ConfigureAwait(false);
                await TrySendCancelDataFrameAsync(
                        dataSession,
                        sessionId,
                        transferId,
                        reason,
                        direction,
                        protocolVersion,
                        transportEpoch,
                        $"{source}_retry_{index + 2}",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                dataSession.Dispose();
            }
            catch
            {
            }
        }
    }

    private async Task TrySendErrorDataFrameAsync(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string errorCode,
        string? message,
        FileTransferDirection direction,
        int protocolVersion,
        long transportEpoch,
        string source,
        CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(CancelDataFrameBestEffortTimeoutMs));
            FileTransferErrorFrameV4 frame = protocolVersion >= FileTransferProtocol.ProtocolVersionV6
                ? new FileTransferErrorFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode,
                    Message = NormalizeReason(message),
                    TransportEpoch = Math.Max(0, transportEpoch),
                }
                : new FileTransferErrorFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode,
                    Message = NormalizeReason(message),
                };
            await dataSession.SendAsync(frame, timeout.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_error_data_frame_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; protocol_version={protocolVersion}; transport_epoch={Math.Max(0, transportEpoch)}; source={source}; error_code={NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=redundant_data_frame; direction={direction.ToString().ToLowerInvariant()}; source={source}; error_code={NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=error; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; path=redundant_data_frame; error=timeout");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_error_data_frame_send_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; error={FormatProtocolLogValue(ex.Message)}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=error; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; path=redundant_data_frame; error={ex.GetType().Name}");
        }
    }

    private void StartErrorDataFrameRetryLoop(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string errorCode,
        string? message,
        FileTransferDirection direction,
        int protocolVersion,
        long transportEpoch,
        string source)
    {
        _ = Task.Run(
            () => RunErrorDataFrameRetryLoopAsync(dataSession, sessionId, transferId, errorCode, message, direction, protocolVersion, transportEpoch, source),
            CancellationToken.None);
    }

    private async Task RunErrorDataFrameRetryLoopAsync(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string errorCode,
        string? message,
        FileTransferDirection direction,
        int protocolVersion,
        long transportEpoch,
        string source)
    {
        try
        {
            for (var index = 0; index < CancelDataFrameRetryDelaysMs.Length; index++)
            {
                await Task.Delay(CancelDataFrameRetryDelaysMs[index]).ConfigureAwait(false);
                await TrySendErrorDataFrameAsync(
                        dataSession,
                        sessionId,
                        transferId,
                        errorCode,
                        message,
                        direction,
                        protocolVersion,
                        transportEpoch,
                        $"{source}_retry_{index + 2}",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                dataSession.Dispose();
            }
            catch
            {
            }
        }
    }

    private async Task SendErrorAsync(string sessionId, string transferId, string errorCode, string? message, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferErrorAsync(
                new FileTransferErrorV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode,
                    Message = NormalizeReason(message),
                },
                timeout.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=control; error_code={NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=control; error=timeout");
        }
        catch (Exception ex)
        {
            Warn($"error send failed: {ex.Message}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=control; error={ex.GetType().Name}");
        }
    }

    private async Task<bool> SendPauseControlAsync(FileTransferPauseControlV6 message, FileTransferDirection direction, string reason, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferPauseControlAsync(message, linkedCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=pause_control; transfer_id={message.TransferId}; session_id={message.SessionId}; direction={direction}; reason={FormatProtocolLogValue(reason)}; path=control; paused={(message.Paused ? 1 : 0)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=pause_control; transfer_id={message.TransferId}; session_id={message.SessionId}; direction={direction}; reason={FormatProtocolLogValue(reason)}; path=control; paused={(message.Paused ? 1 : 0)}; error=timeout");
            return false;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=pause_control; transfer_id={message.TransferId}; session_id={message.SessionId}; direction={direction}; reason={FormatProtocolLogValue(reason)}; path=control; paused={(message.Paused ? 1 : 0)}; error={ex.GetType().Name}");
            return false;
        }
    }

    private async Task<bool> SendHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferHeartbeatAsync(message, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_heartbeat_send_failed; transfer_id={message.TransferId}; session_id={message.SessionId}; sequence={message.Sequence}; error={ex.GetType().Name}");
            return false;
        }
    }

    private void FailOutboundLocally(OutboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            _ = TryInvalidateReceiveRecoveryOperationForScopeReplacementLocked(
                context,
                "local_transfer_failure");
            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            snapshot = CreateSnapshotLocked();
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId: context.SessionId,
            errorCode: context.ErrorCode,
            reason: context.StatusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount);
    }

    private void FailInboundLocally(InboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;
        string sessionId;
        string transferId;
        string? normalizedErrorCode;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            InvalidateCheckpointRepairContinuityLocked(context, "local_transfer_failure");
            _ = TryInvalidateReceiveRecoveryOperationForScopeReplacementLocked(
                context,
                "local_transfer_failure");
            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            context.AcceptInProgress = false;
            snapshot = CreateSnapshotLocked();
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            errorCode: normalizedErrorCode,
            reason: failureMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            savedPath: context.SavedFilePath);
    }

    private void DetachTransportCore(bool markActiveTransfersFailed, string failureCode, string failureMessage)
    {
        FileTransferSessionPreparationAttachment? previousPreparation;
        IFileTransferSignalingTransport? previousTransport;
        ISignalingTransport? previousLifecycle;
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;

        lock (gate)
        {
            previousPreparation = DetachPreparationTransportLocked();
            previousTransport = transport;
            previousLifecycle = transportLifecycle;
            outbound = markActiveTransfersFailed && outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = markActiveTransfersFailed && inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;
            var recoveryInvalidationReason = markActiveTransfersFailed
                ? "transport_detached"
                : "service_disposed";
            if (outboundTransfer is { IsTerminal: false } activeOutbound)
            {
                _ =
                    TryInvalidateReceiveRecoveryOperationForScopeReplacementLocked(
                        activeOutbound,
                        recoveryInvalidationReason);
            }

            if (inboundTransfer is { IsTerminal: false } activeInbound)
            {
                _ =
                    TryInvalidateReceiveRecoveryOperationForScopeReplacementLocked(
                        activeInbound,
                        recoveryInvalidationReason);
            }

            transport = null;
            transportLifecycle = null;
            runtimeUnlockPostFallbackPairedRetryOfferBindingReceipt = null;
            lastOutboundRuntimeUnlockPostFallbackProbeFailureObservation = null;
            lastInboundRuntimeUnlockPostFallbackProbeFailureObservation = null;
        }

        previousPreparation?.Dispose();
        if (previousTransport is not null)
        {
            previousTransport.FileTransferOfferReceived -= OnFileTransferOfferReceived;
            previousTransport.FileTransferAcceptReceived -= OnFileTransferAcceptReceived;
            previousTransport.FileTransferDeclineReceived -= OnFileTransferDeclineReceived;
            previousTransport.FileTransferSessionOpenReceived -= OnFileTransferSessionOpenReceived;
            previousTransport.FileTransferCancelReceived -= OnFileTransferCancelReceived;
            previousTransport.FileTransferErrorReceived -= OnFileTransferErrorReceived;
            previousTransport.FileTransferCompleteReceived -= OnFileTransferCompleteReceived;
            previousTransport.FileTransferPauseControlReceived -= OnFileTransferPauseControlReceived;
            previousTransport.FileTransferHeartbeatReceived -= OnFileTransferHeartbeatReceived;
            previousTransport.FileTransferTransportEpochReceived -= OnFileTransferTransportEpochReceived;
            previousTransport.FileTransferTransportProbeReceived -= OnFileTransferTransportProbeReceived;
            previousTransport.FileTransferRepairProofReceived -= OnFileTransferRepairProofReceived;
            previousTransport.FileTransferFallbackTailYieldProofReceived -= OnFileTransferFallbackTailYieldProofReceived;
            if (previousTransport is IInitialActivationReceiverBarrierBridge receiverBarrierBridge)
            {
                receiverBarrierBridge.ReceiverBarrierBindingRequested -= OnReceiverBarrierBindingRequested;
                receiverBarrierBridge.ReceiverBarrierFailureObserved -= OnReceiverBarrierFailureObserved;
            }
            if (previousTransport is IRuntimeUnlockRetryDispatchAuthorityBridge retryDispatchBridge)
            {
                retryDispatchBridge
                        .RuntimeUnlockPostFallbackPairedRetryOfferBindingRequested -=
                    OnRuntimeUnlockPostFallbackPairedRetryOfferBindingRequested;
                retryDispatchBridge
                        .RuntimeUnlockPostFallbackPreCommitProbeFailureObserved -=
                    OnRuntimeUnlockPostFallbackPreCommitProbeFailureObserved;
                retryDispatchBridge.RuntimeUnlockPostFallbackDialFailureObserved -=
                    OnRuntimeUnlockPostFallbackDialFailureObserved;
                retryDispatchBridge.RuntimeUnlockInitialActivationPreCommitProbeFailureObserved -=
                    OnRuntimeUnlockInitialActivationPreCommitProbeFailureObserved;
                retryDispatchBridge.RuntimeUnlockRetryDispatchInitialEvaluationRequested -=
                    OnRuntimeUnlockRetryDispatchInitialEvaluationRequested;
                retryDispatchBridge.RuntimeUnlockRetryDispatchAuthorityRequested -=
                    OnRuntimeUnlockRetryDispatchAuthorityRequested;
                retryDispatchBridge.RuntimeUnlockRetryDispatchSettlementReported -=
                    OnRuntimeUnlockRetryDispatchSettlementReported;
                retryDispatchBridge.RuntimeUnlockRetryDispatchDeadlineEvaluationRequested -=
                    OnRuntimeUnlockRetryDispatchDeadlineEvaluationRequested;
                retryDispatchBridge.RuntimeUnlockRetryDispatchExecutionObserved -=
                    OnRuntimeUnlockRetryDispatchExecutionObserved;
                retryDispatchBridge.RuntimeUnlockRetryDispatchSuccessorEvaluationRequested -=
                    OnRuntimeUnlockRetryDispatchSuccessorEvaluationRequested;
            }

            if (previousTransport is
                IRuntimeUnlockInitialActivationReceiveRecoveryHoldBridge
                    initialActivationHoldBridge)
            {
                initialActivationHoldBridge
                        .RuntimeUnlockInitialActivationReceiveRecoveryHoldRequested -=
                    OnRuntimeUnlockInitialActivationReceiveRecoveryHoldRequested;
                initialActivationHoldBridge
                        .RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequested -=
                    OnRuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequested;
            }

            if (previousTransport is IFileTransferBridgeRecoveryExecutionBridge bridgeRecoveryExecutionBridge)
            {
                bridgeRecoveryExecutionBridge.BridgeRecoveryEvaluationRequested -=
                    OnBridgeRecoveryEvaluationRequested;
                bridgeRecoveryExecutionBridge.ReceiveRecoveryProofObserved -=
                    OnReceiveRecoveryProofObserved;
                bridgeRecoveryExecutionBridge
                        .ReceiveRecoveryPeerReachabilityObserved -=
                    OnReceiveRecoveryPeerReachabilityObserved;
            }

            if (previousTransport is IFileTransferRouteTransitionBridge routeTransitionBridge)
            {
                routeTransitionBridge.FallbackV6ProofBootstrapEvaluationRequested -=
                    OnFallbackV6ProofBootstrapEvaluationRequested;
            }

            if (previousTransport is
                ISessionLivenessSignalingTransport sessionLivenessTransport)
            {
                sessionLivenessTransport.SessionLivenessProofReceived -=
                    OnReceiveRecoveryProofStatePublicationRecheckEvidence;
            }
        }

        if (previousLifecycle is not null)
        {
            previousLifecycle.Rejected -= OnTransportRejected;
            previousLifecycle.Disconnected -= OnTransportDisconnected;
        }

        if (outbound is not null)
        {
            FailOutboundLocally(outbound, failureCode, failureMessage);
        }

        if (inbound is not null)
        {
            FailInboundLocally(inbound, failureCode, failureMessage);
        }
    }

    private IFileTransferSignalingTransport GetTransportOrThrow()
        => transport ?? throw new InvalidOperationException("No file-transfer transport is attached.");

    private SessionFileTransferSnapshot CreateSnapshot()
    {
        lock (gate)
        {
            return CreateSnapshotLocked();
        }
    }

    private SessionFileTransferSnapshot CreateSnapshotLocked()
        => new(
            Outbound: outboundTransfer is { } outbound
                ? CreateTransferSnapshotLocked(outbound)
                : null,
            Inbound: inboundTransfer is { } inbound
                ? CreateTransferSnapshotLocked(inbound)
                : null);

    private FileTransferTransferSnapshot? CaptureCurrentOutboundSnapshot()
    {
        lock (gate)
        {
            return outboundTransfer is { } outbound
                ? CreateTransferSnapshotLocked(outbound)
                : null;
        }
    }

    private FileTransferTransferSnapshot? CaptureCurrentInboundSnapshot()
    {
        lock (gate)
        {
            return inboundTransfer is { } inbound
                ? CreateTransferSnapshotLocked(inbound)
                : null;
        }
    }

    private static FileTransferTransferSnapshot CreateTransferSnapshotLocked(
        OutboundTransferContext context)
        => context.ToSnapshot() with
        {
            ReceiveRecoveryObservation =
                CreateReceiveRecoveryObservationSnapshotLocked(
                    context,
                    FileTransferDirection.Outbound),
        };

    private static FileTransferTransferSnapshot CreateTransferSnapshotLocked(
        InboundTransferContext context)
        => context.ToSnapshot() with
        {
            ReceiveRecoveryObservation =
                CreateReceiveRecoveryObservationSnapshotLocked(
                    context,
                    FileTransferDirection.Inbound),
        };

    private static FileTransferReceiveRecoveryObservationSnapshot
        CreateReceiveRecoveryObservationSnapshotLocked(
            OutboundTransferContext context,
            FileTransferDirection direction,
            string source = "filetransfer_snapshot",
            string reason = "active_transfer_snapshot",
            string? observationIdentity = null,
            long? exactObservedUtcMs = null,
            FileTransferReceiveRecoveryHealthEvaluationEvidence?
                healthEvaluationEvidence = null)
    {
        var observationGeneration =
            NextReceiveRecoveryObservationGeneration(
                context.LastReceiveRecoveryObservationGeneration);
        context.LastReceiveRecoveryObservationGeneration =
            observationGeneration;
        var normalizedSource =
            NormalizeReason(source) ?? "filetransfer_snapshot";
        var normalizedReason =
            NormalizeReason(reason) ?? "active_transfer_snapshot";
        var observedUtc = exactObservedUtcMs is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(exactObservedUtcMs.Value)
            : DateTimeOffset.UtcNow;
        var exactObservationIdentity = string.IsNullOrWhiteSpace(observationIdentity)
            ? $"{normalizedSource}:{observationGeneration}"
            : observationIdentity.Trim();
        var dataSessionIdentity = CreateReceiveRecoveryDataSessionIdentity(
            context.TransferId,
            context.DataSession,
            context.DataSessionGeneration);
        var snapshot = new FileTransferReceiveRecoveryObservationSnapshot(
            context.SessionId,
            context.TransferId,
            direction,
            context.RouteSelection.TelemetryToken,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            context.TerminalGeneration,
            context.CurrentTransferLeg?.Generation ?? 0,
            context.CurrentTransferLeg?.TransportEpochId ?? 0,
            context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0,
            context.CurrentRoutePublicationToken?.PublicationGeneration ?? 0,
            context.RuntimeUnlockRouteCommitToken?.CommitGeneration,
            context.CurrentTransferLeg?.CheckpointRequestId,
            dataSessionIdentity,
            context.DataSessionGeneration,
            observationGeneration,
            normalizedSource,
            normalizedReason,
            observedUtc)
        {
            HealthEvaluationEvidence = healthEvaluationEvidence,
        };
        if (context.CurrentRuntimeOwner is not { } owner ||
            context.CurrentRoutePublicationToken is not { } publication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is null ||
            context.DataSessionGeneration <= 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            CoordinatorObservation =
                new FileTransferReceiveRecoveryObservation(
                    context.SessionId,
                    context.TransferId,
                    direction,
                    context.RouteSelection.Route,
                    context.RouteSelection.TelemetryToken,
                    context.NegotiatedDataProtocolVersion,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    owner,
                    publication,
                    context.RuntimeUnlockRouteCommitToken,
                    context.TerminalGeneration,
                    leg.Generation,
                    leg.TransportEpochId,
                    leg.BridgeRecoveryGeneration,
                    leg.CheckpointRequestId,
                    dataSessionIdentity,
                    context.DataSessionGeneration,
                    normalizedSource,
                    normalizedReason,
                    exactObservationIdentity,
                    observationGeneration,
                    observedUtc.ToUnixTimeMilliseconds())
                {
                    HealthEvaluationEvidence = healthEvaluationEvidence,
                },
        };
    }

    private static FileTransferReceiveRecoveryObservationSnapshot
        CreateReceiveRecoveryObservationSnapshotLocked(
            InboundTransferContext context,
            FileTransferDirection direction,
            string source = "filetransfer_snapshot",
            string reason = "active_transfer_snapshot",
            string? observationIdentity = null,
            long? exactObservedUtcMs = null,
            FileTransferReceiveRecoveryHealthEvaluationEvidence?
                healthEvaluationEvidence = null)
    {
        var observationGeneration =
            NextReceiveRecoveryObservationGeneration(
                context.LastReceiveRecoveryObservationGeneration);
        context.LastReceiveRecoveryObservationGeneration =
            observationGeneration;
        var normalizedSource =
            NormalizeReason(source) ?? "filetransfer_snapshot";
        var normalizedReason =
            NormalizeReason(reason) ?? "active_transfer_snapshot";
        var observedUtc = exactObservedUtcMs is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(exactObservedUtcMs.Value)
            : DateTimeOffset.UtcNow;
        var exactObservationIdentity = string.IsNullOrWhiteSpace(observationIdentity)
            ? $"{normalizedSource}:{observationGeneration}"
            : observationIdentity.Trim();
        var dataSessionIdentity = CreateReceiveRecoveryDataSessionIdentity(
            context.TransferId,
            context.DataSession,
            context.DataSessionGeneration);
        var snapshot = new FileTransferReceiveRecoveryObservationSnapshot(
            context.SessionId,
            context.TransferId,
            direction,
            context.RouteSelection.TelemetryToken,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            context.TerminalGeneration,
            context.CurrentTransferLeg?.Generation ?? 0,
            context.CurrentTransferLeg?.TransportEpochId ?? 0,
            context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0,
            context.CurrentRoutePublicationToken?.PublicationGeneration ?? 0,
            context.RuntimeUnlockRouteCommitToken?.CommitGeneration,
            context.CurrentTransferLeg?.CheckpointRequestId,
            dataSessionIdentity,
            context.DataSessionGeneration,
            observationGeneration,
            normalizedSource,
            normalizedReason,
            observedUtc)
        {
            HealthEvaluationEvidence = healthEvaluationEvidence,
        };
        if (context.CurrentRuntimeOwner is not { } owner ||
            context.CurrentRoutePublicationToken is not { } publication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is null ||
            context.DataSessionGeneration <= 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            CoordinatorObservation =
                new FileTransferReceiveRecoveryObservation(
                    context.SessionId,
                    context.TransferId,
                    direction,
                    context.RouteSelection.Route,
                    context.RouteSelection.TelemetryToken,
                    context.NegotiatedDataProtocolVersion,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    owner,
                    publication,
                    context.RuntimeUnlockRouteCommitToken,
                    context.TerminalGeneration,
                    leg.Generation,
                    leg.TransportEpochId,
                    leg.BridgeRecoveryGeneration,
                    leg.CheckpointRequestId,
                    dataSessionIdentity,
                    context.DataSessionGeneration,
                    normalizedSource,
                    normalizedReason,
                    exactObservationIdentity,
                    observationGeneration,
                    observedUtc.ToUnixTimeMilliseconds())
                {
                    HealthEvaluationEvidence = healthEvaluationEvidence,
                },
        };
    }

    private static string CreateReceiveRecoveryDataSessionIdentity(
        string transferId,
        IFileTransferDataSession? dataSession,
        long dataSessionGeneration)
        => dataSession is null
            ? "(none)"
            : $"{transferId}:{dataSessionGeneration}";

    private static long NextReceiveRecoveryObservationGeneration(
        long lastGeneration)
        => lastGeneration < long.MaxValue
            ? lastGeneration + 1
            : long.MaxValue;

    private void RaiseTransferChanged(SessionFileTransferSnapshot snapshot)
    {
        try
        {
            TransferChanged?.Invoke(this, new SessionFileTransferSnapshotChangedEventArgs(snapshot));
        }
        catch
        {
        }
    }

    private static FileTransferSendDescriptor NormalizeSendDescriptor(FileTransferSendDescriptor descriptor, Func<string> transferIdFactory)
    {
        var normalizedFileName = NormalizeRequiredBounded(descriptor.FileName, FileTransferProtocol.MaxFileNameLength, nameof(descriptor.FileName));
        if (descriptor.FileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.FileSizeBytes), "File size must be positive.");
        }

        if (descriptor.ChunkSizeBytes is int chunkSizeBytes &&
            (chunkSizeBytes <= 0 || chunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.ChunkSizeBytes), $"Chunk size must be between 1 and {FileTransferProtocol.MaxChunkRawBytes} bytes.");
        }

        var transferId = string.IsNullOrWhiteSpace(descriptor.TransferId)
            ? NormalizeTransferId(transferIdFactory())
            : NormalizeTransferId(descriptor.TransferId);

        return descriptor with
        {
            FileName = normalizedFileName,
            TransferId = transferId,
            ChunkSizeBytes = descriptor.ChunkSizeBytes,
        };
    }

    private static string NormalizeTransferId(string? transferId)
    {
        return NormalizeRequiredBounded(transferId, FileTransferProtocol.MaxTransferIdLength, nameof(transferId));
    }
}
