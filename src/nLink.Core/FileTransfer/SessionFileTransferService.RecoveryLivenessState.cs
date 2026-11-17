namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    public bool TryGetActiveFileTransferRecoveryLivenessSnapshot(
        string sessionId,
        out FileTransferRecoveryLivenessSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        lock (gate)
        {
            FileTransferRecoveryLivenessSnapshot? outboundSnapshot = null;
            FileTransferRecoveryLivenessSnapshot? inboundSnapshot = null;
            var outboundMatched =
                outboundTransfer is { IsTerminal: false } outbound &&
                string.Equals(
                    outbound.SessionId,
                    normalizedSessionId,
                    StringComparison.Ordinal) &&
                TryCreateReceiveRecoveryLivenessSnapshot(
                    CreateCoordinatorStateLocked(outbound),
                    out outboundSnapshot);
            var inboundMatched =
                inboundTransfer is { IsTerminal: false } inbound &&
                string.Equals(
                    inbound.SessionId,
                    normalizedSessionId,
                    StringComparison.Ordinal) &&
                TryCreateReceiveRecoveryLivenessSnapshot(
                    CreateCoordinatorStateLocked(inbound),
                    out inboundSnapshot);
            if (outboundMatched == inboundMatched)
            {
                return false;
            }

            snapshot = outboundMatched ? outboundSnapshot! : inboundSnapshot!;
            return true;
        }
    }

    internal bool TryGetActiveFileTransferRecoveryLivenessSnapshot(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        out FileTransferRecoveryLivenessSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(transferId))
        {
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        var normalizedTransferId = transferId.Trim();
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound)
            {
                return outboundTransfer is { IsTerminal: false } outbound &&
                       string.Equals(
                           outbound.SessionId,
                           normalizedSessionId,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           outbound.TransferId,
                           normalizedTransferId,
                           StringComparison.Ordinal) &&
                       TryCreateReceiveRecoveryLivenessSnapshot(
                           CreateCoordinatorStateLocked(outbound),
                           out snapshot);
            }

            return inboundTransfer is { IsTerminal: false } inbound &&
                   string.Equals(
                       inbound.SessionId,
                       normalizedSessionId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       inbound.TransferId,
                       normalizedTransferId,
                       StringComparison.Ordinal) &&
                   TryCreateReceiveRecoveryLivenessSnapshot(
                       CreateCoordinatorStateLocked(inbound),
                       out snapshot);
        }
    }

    internal static bool TryCreateReceiveRecoveryLivenessSnapshot(
        FileTransferCoordinatorState state,
        out FileTransferRecoveryLivenessSnapshot snapshot)
    {
        snapshot = default!;
        var recovery = FileTransferCoordinator.GetReceiveRecoverySubstate(state);
        FileTransferReceiveRecoveryOperation operation;
        if (recovery.CurrentOperation is { } current &&
            FileTransferCoordinator.IsReceiveRecoveryOperationCurrent(
                state,
                current.Authority))
        {
            operation = current;
        }
        else if (recovery.LastTerminalOperation is
                 {
                     ExecutionState:
                         FileTransferReceiveRecoveryExecutionState.Exhausted,
                 } exhausted &&
                 IsReceiveRecoveryObservationCurrent(
                     state,
                     exhausted.Authority.Observation))
        {
            operation = exhausted;
        }
        else
        {
            return false;
        }

        if (operation.ExecutionState is
            FileTransferReceiveRecoveryExecutionState.Discarded or
            FileTransferReceiveRecoveryExecutionState.Invalidated)
        {
            return false;
        }

        var observation = operation.Authority.Observation;
        var createdUtc =
            DateTimeOffset.FromUnixTimeMilliseconds(observation.ObservedUtcMs);
        var receiveProofObserved = operation.ReceiveProof is not null;
        var bridgeRecoveryCompleted =
            operation.ExecutionReceipt?.Outcome ==
            FileTransferBridgeRecoveryExecutionOutcome.Completed;
        var recoveryExhausted =
            operation.ExecutionState ==
            FileTransferReceiveRecoveryExecutionState.Exhausted;
        var bridgeRecoveryStarted =
            operation.ExecutionStartStatus is
                FileTransferBridgeRecoveryExecutionStartStatus.Started or
                FileTransferBridgeRecoveryExecutionStartStatus.Idempotent ||
            operation.ExecutionState is
                FileTransferReceiveRecoveryExecutionState.Running or
                FileTransferReceiveRecoveryExecutionState.Completed or
                FileTransferReceiveRecoveryExecutionState.Exhausted;
        var recoveryState =
            recoveryExhausted
                ? FileTransferRecoveryLivenessState.Exhausted
                : receiveProofObserved
                    ? FileTransferRecoveryLivenessState.ReceiveProofObserved
                    : bridgeRecoveryCompleted
                        ? FileTransferRecoveryLivenessState
                            .BridgeRecoveryCompletedAwaitingProof
                        : bridgeRecoveryStarted
                            ? FileTransferRecoveryLivenessState
                                .BridgeRecoveryStarted
                            : FileTransferRecoveryLivenessState
                                .BridgeRecoveryRequested;
        var authorityCompleted =
            operation.ClosureState ==
            FileTransferReceiveRecoveryClosureState.Consumed;
        var regularV4ReceiveRecovery =
            observation.Route ==
                FileTransferRoute.RegularNknV4Fast &&
            observation.ProtocolVersion ==
                FileTransferProtocol.ProtocolVersionV4;
        var terminalRecommended =
            recoveryState is FileTransferRecoveryLivenessState.Exhausted or
                FileTransferRecoveryLivenessState.Completed ||
            regularV4ReceiveRecovery &&
            recoveryState ==
                FileTransferRecoveryLivenessState.ReceiveProofObserved;

        snapshot = new FileTransferRecoveryLivenessSnapshot(
            observation.SessionId,
            observation.TransferId,
            observation.RouteToken,
            observation.ProtocolVersion,
            observation.LiveRouteEpoch,
            observation.LegGeneration,
            observation.BridgeRecoveryGeneration,
            observation.TransportEpoch,
            observation.CheckpointRequestId,
            observation.Reason,
            recoveryState,
            createdUtc,
            DateTimeOffset.FromUnixTimeMilliseconds(
                operation.LivenessDeferralDeadlineUtcMs),
            BridgeRecoveryRequested: true,
            bridgeRecoveryStarted,
            bridgeRecoveryCompleted,
            receiveProofObserved,
            recoveryExhausted,
            authorityCompleted,
            terminalRecommended);
        return true;
    }

    private static bool IsReceiveRecoveryObservationCurrent(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryObservation observation)
        => !state.IsTerminal &&
           string.Equals(
               state.SessionId,
               observation.SessionId,
               StringComparison.Ordinal) &&
           string.Equals(
               state.TransferId,
               observation.TransferId,
               StringComparison.Ordinal) &&
           state.Direction == observation.Direction &&
           state.RouteSelection.Route == observation.Route &&
           string.Equals(
               state.RouteSelection.TelemetryToken,
               observation.RouteToken,
               StringComparison.Ordinal) &&
           state.RouteSelection.ProtocolVersion == observation.ProtocolVersion &&
           (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ==
               observation.LiveRouteEpoch &&
           state.CurrentRuntimeOwner == observation.RuntimeOwner &&
           state.CurrentRoutePublicationToken == observation.RoutePublication &&
           state.CurrentRouteCommit == observation.RouteCommit &&
           state.TerminalGeneration == observation.TerminalGeneration &&
           state.CurrentLeg is { } leg &&
           leg.Generation == observation.LegGeneration &&
           leg.TransportEpochId == observation.TransportEpoch &&
           leg.BridgeRecoveryGeneration ==
               observation.BridgeRecoveryGeneration &&
           string.Equals(
               leg.CheckpointRequestId,
               observation.CheckpointRequestId,
               StringComparison.Ordinal) &&
           string.Equals(
               state.CurrentDataSessionIdentity,
               observation.DataSessionIdentity,
               StringComparison.Ordinal) &&
           state.CurrentDataSessionGeneration ==
               observation.DataSessionGeneration;
}
