using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static bool IsPrimaryRegularNknBulkV6Runtime(FileTransferSparseCreditRuntimeKind runtimeKind)
        => runtimeKind == FileTransferSparseCreditRuntimeKind.PrimaryRegularNknBulkV6;

    private static string FormatPrimaryRegularNknBulkV6State(PrimaryRegularNknBulkV6State state)
        => state switch
        {
            PrimaryRegularNknBulkV6State.Opening => "opening",
            PrimaryRegularNknBulkV6State.ManifestExchange => "manifest_exchange",
            PrimaryRegularNknBulkV6State.AwaitingManifest => "awaiting_manifest",
            PrimaryRegularNknBulkV6State.CreditGranted => "credit_granted",
            PrimaryRegularNknBulkV6State.SendingBulk => "sending_bulk",
            PrimaryRegularNknBulkV6State.ReceivingBulk => "receiving_bulk",
            PrimaryRegularNknBulkV6State.AwaitingReceiverState => "awaiting_receiver_state",
            PrimaryRegularNknBulkV6State.StateRefreshRequested => "state_refresh_requested",
            PrimaryRegularNknBulkV6State.CheckpointSyncRequested => "checkpoint_sync_requested",
            PrimaryRegularNknBulkV6State.Rebinding => "rebinding",
            PrimaryRegularNknBulkV6State.RebindConfirmed => "rebind_confirmed",
            PrimaryRegularNknBulkV6State.Finalizing => "finalizing",
            PrimaryRegularNknBulkV6State.Completed => "completed",
            PrimaryRegularNknBulkV6State.Failed => "failed",
            PrimaryRegularNknBulkV6State.Cancelled => "cancelled",
            _ => state.ToString().ToLowerInvariant(),
        };

    private static void LogPrimaryRegularNknBulkV6State(
        OutboundTransferContext context,
        PrimaryRegularNknBulkV6State state,
        string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_state; direction=outbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; state={FormatPrimaryRegularNknBulkV6State(state)}; reason={FormatProtocolLogValue(reason)}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; sparse_credit=1; v6_frames_only=1");
    }

    private static void LogPrimaryRegularNknBulkV6State(
        InboundTransferContext context,
        PrimaryRegularNknBulkV6State state,
        string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_state; direction=inbound; transfer_id={context.TransferId}; session_id={FormatProtocolLogValue(context.SessionId)}; state={FormatPrimaryRegularNknBulkV6State(state)}; reason={FormatProtocolLogValue(reason)}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; bytes_transferred={context.BytesTransferred}; sparse_credit=1; v6_frames_only=1");
    }

    private static bool IsPrimaryRegularNknBulkV6ContextLocked(OutboundTransferContext context)
        => context.RuntimeProfile == FileTransferRuntimeProfile.PrimaryRegularNknBulkV6 &&
           context.V6RegularNknBulkSparseProfileActive &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);

    private static bool IsPrimaryRegularNknBulkV6ContextLocked(InboundTransferContext context)
        => context.RuntimeProfile == FileTransferRuntimeProfile.PrimaryRegularNknBulkV6 &&
           context.V6RegularNknBulkSparseProfileActive &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);

    private static bool IsPrimaryRegularNknBulkV6CheckpointSyncRequest(FileTransferFrontierRequestFrameV6 request)
        => string.Equals(request.RecoveryMode, V6RegularNknCheckpointSyncRecoveryMode, StringComparison.Ordinal) ||
           (request.RepairRequestId?.StartsWith(V6RegularNknCheckpointSyncRequestPrefix, StringComparison.Ordinal) ?? false);

    private static void LogPrimaryRegularNknBulkV6CheckpointRequestPrepared(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int availableCreditChunks,
        int inFlightFrames)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_prepared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; rebind_generation={context.PullTransportRebindGeneration}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; in_flight_frames={inFlightFrames}");
    }

    private static void LogPrimaryRegularNknBulkV6CheckpointReceived(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_received; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; checkpoint_sequence={state.Epoch}; request_id={FormatProtocolLogValue(state.RepairRequestId ?? "(none)")}; recovery_mode={FormatProtocolLogValue(state.RecoveryMode ?? "(none)")}; rebind_generation={context.PullTransportRebindGeneration}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; terminal_ready={(state.TerminalReady ? 1 : 0)}");
    }

    private static void LogPrimaryRegularNknBulkV6CheckpointSent(
        InboundTransferContext context,
        FileTransferStateFrameV4 state,
        string reason,
        bool sent)
    {
        if (state is not FileTransferReceiverStateFrameV6 receiverState ||
            !string.Equals(receiverState.RecoveryMode, V6RegularNknCheckpointSyncRecoveryMode, StringComparison.Ordinal))
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; sent={(sent ? 1 : 0)}; checkpoint_sequence={receiverState.Epoch}; request_id={FormatProtocolLogValue(receiverState.RepairRequestId ?? "(none)")}; rebind_generation={context.PullTransportRebindGeneration}; contiguous_committed_chunk_index={receiverState.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={receiverState.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={receiverState.CreditUntilChunkIndexExclusive}; missing_range_count={receiverState.MissingRanges.Count}; terminal_ready={(receiverState.TerminalReady ? 1 : 0)}");
    }

    private static void LogPrimaryRegularNknBulkV6FrontierFeedbackFailedRecoverable(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason,
        string recoveryAction,
        int failureCount,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int availableCreditChunks)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; recovery_action={FormatProtocolLogValue(recoveryAction)}; request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}; recovery_mode={FormatProtocolLogValue(request.RecoveryMode ?? "(none)")}; priority={FormatProtocolLogValue(request.Priority ?? "(none)")}; failure_count={failureCount}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
    }
}
