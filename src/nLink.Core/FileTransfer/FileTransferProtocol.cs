namespace NLink.Core.FileTransfer;

public static class FileTransferProtocol
{
    internal const string FallbackV6ProofBootstrapPriority = "proof";
    internal const string FallbackV6ProofBootstrapRecoveryMode =
        "post_tuna_fallback_v6_proof_bootstrap";
    internal const string FallbackV6ProofBootstrapCorrelationPrefix =
        "fallback-v6-proof-bootstrap:";

    public const string Kind = "filetransfer";

    public const string OfferTypeV2 = "filetransfer.offer.v2";
    public const string AcceptTypeV1 = "filetransfer.accept.v1";
    public const string DeclineTypeV1 = "filetransfer.decline.v1";
    public const string SessionOpenTypeV2 = "filetransfer.session_open.v2";
    public const string CancelTypeV1 = "filetransfer.cancel.v1";
    public const string ErrorTypeV1 = "filetransfer.error.v1";
    public const string CompleteTypeV1 = "filetransfer.complete.v1";
    public const string ManifestFrameTypeV4 = "filetransfer.manifest.v4";
    public const string StateFrameTypeV4 = "filetransfer.state.v4";
    public const string ChunkBatchFrameTypeV4 = "filetransfer.chunk_batch.v4";
    public const string SessionCompleteFrameTypeV4 = "filetransfer.complete.v4";
    public const string SessionCancelFrameTypeV4 = "filetransfer.cancel.v4";
    public const string ErrorFrameTypeV4 = "filetransfer.error.v4";
    public const string PauseControlFrameTypeV4 = "filetransfer.pause_control.v4";
    public const string ManifestFrameTypeV6 = "filetransfer.manifest.v6";
    public const string ReceiverStateFrameTypeV6 = "filetransfer.receiver_state.v6";
    public const string ChunkBatchFrameTypeV6 = "filetransfer.chunk_batch.v6";
    public const string TransportEpochFrameTypeV6 = "filetransfer.transport_epoch.v6";
    public const string TransportProbeFrameTypeV6 = "filetransfer.transport_probe.v6";
    public const string RuntimeUnlockPreCommitProbeFrameType = "filetransfer.runtime_unlock.precommit_probe";
    public const string RuntimeUnlockPreCommitProbeAckFrameType = "filetransfer.runtime_unlock.precommit_probe_ack";
    public const string FallbackTailYieldFrameTypeV6 = "filetransfer.fallback_tail.yield.v6";
    public const string FrontierRequestFrameTypeV6 = "filetransfer.frontier_request.v6";
    public const string RepairProofFrameTypeV6 = "filetransfer.repair_proof.v6";
    public const string SessionCompleteFrameTypeV6 = "filetransfer.complete.v6";
    public const string SessionCancelFrameTypeV6 = "filetransfer.cancel.v6";
    public const string ErrorFrameTypeV6 = "filetransfer.error.v6";
    public const string PauseControlFrameTypeV6 = "filetransfer.pause_control.v6";
    public const string HeartbeatFrameTypeV6 = "filetransfer.heartbeat.v6";
    public const string FallbackBootstrapRequestFrameTypeV6 = "filetransfer.fallback_bootstrap_request.v6";
    public const string FallbackBootstrapStateFrameTypeV6 = "filetransfer.fallback_bootstrap_state.v6";

    public const string SessionRoleSender = "Sender";
    public const string SessionRoleReceiver = "Receiver";
    public const int ProtocolVersionV4 = 4;
    public const int ProtocolVersionV6 = 6;

    public const int Sha256LengthBytes = 32;
    public const int MaxTransferIdLength = 128;
    public const int MaxFileNameLength = 240;
    public const int MaxReasonLength = 256;
    public const int MaxErrorCodeLength = 64;
    public const int MaxErrorMessageLength = 256;
    public const int MaxChunkRawBytes = FileTransferChunkBudget.MaxRawChunkBytes;
    public const int MaxSerializedChunkPayloadBytes = FileTransferChunkBudget.MaxSerializedChunkPayloadBytes;
    public const int MaxStateMissingRangesV4 = 16;
    public const int MaxStateMissingChunksV4 = 64;
    public const int MaxChunkCountV4 = 1_500_000;
    public const int MaxChunkBatchSegmentsV4 = 3;
    public const int MaxChunkBatchRawBytesV4 = FileTransferChunkBudget.MaxRawBatchBytes;
    public const int MaxSerializedChunkBatchPayloadBytesV4 = FileTransferChunkBudget.MaxSerializedChunkBatchPayloadBytes;
    public const int MaxStateMissingRangesV6 = 128;
    public const int MaxStateMissingChunksV6 = 8192;
    public const int MaxChunkCountV6 = MaxChunkCountV4;
    public const int MaxChunkBatchSegmentsV6 = 8;
    public const int MaxChunkBatchRawBytesV6 = MaxChunkBatchRawBytesV4;
    public const int MaxSerializedChunkBatchPayloadBytesV6 = MaxSerializedChunkBatchPayloadBytesV4;

    public static bool IsV4DataFrame(FileTransferDataFrame? frame)
        => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6
            or FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6
            or FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6
            or FileTransferCompleteFrameV4 and not FileTransferCompleteFrameV6
            or FileTransferCancelFrameV4 and not FileTransferCancelFrameV6
            or FileTransferErrorFrameV4 and not FileTransferErrorFrameV6
            or FileTransferPauseControlFrameV4 and not FileTransferPauseControlFrameV6;

    public static bool IsV6DataFrame(FileTransferDataFrame? frame)
        => frame is FileTransferManifestFrameV6
            or FileTransferReceiverStateFrameV6
            or FileTransferChunkBatchFrameV6
            or FileTransferTransportEpochFrameV6
            or FileTransferTransportProbeFrameV6
            or FileTransferRuntimeUnlockPreCommitProbeFrame
            or FileTransferRuntimeUnlockPreCommitProbeAckFrame
            or FileTransferFallbackTailYieldFrameV6
            or FileTransferFrontierRequestFrameV6
            or FileTransferRepairProofFrameV6
            or FileTransferCompleteFrameV6
            or FileTransferCancelFrameV6
            or FileTransferErrorFrameV6
            or FileTransferPauseControlFrameV6
            or FileTransferHeartbeatFrameV6
            or FileTransferFallbackBootstrapRequestFrameV6
            or FileTransferFallbackBootstrapStateFrameV6;

    public static bool IsV6DataFrameType(string? frameType)
        => frameType is ManifestFrameTypeV6
            or ReceiverStateFrameTypeV6
            or ChunkBatchFrameTypeV6
            or TransportEpochFrameTypeV6
            or TransportProbeFrameTypeV6
            or RuntimeUnlockPreCommitProbeFrameType
            or RuntimeUnlockPreCommitProbeAckFrameType
            or FallbackTailYieldFrameTypeV6
            or FrontierRequestFrameTypeV6
            or RepairProofFrameTypeV6
            or SessionCompleteFrameTypeV6
            or SessionCancelFrameTypeV6
            or ErrorFrameTypeV6
            or PauseControlFrameTypeV6
            or HeartbeatFrameTypeV6
            or FallbackBootstrapRequestFrameTypeV6
            or FallbackBootstrapStateFrameTypeV6;

    internal static string CreateFallbackV6ProofBootstrapCorrelationId(
        long bootstrapGeneration)
        => bootstrapGeneration > 0
            ? $"{FallbackV6ProofBootstrapCorrelationPrefix}{bootstrapGeneration}"
            : throw new ArgumentOutOfRangeException(nameof(bootstrapGeneration));

    internal static bool TryGetFallbackV6ProofBootstrapGeneration(
        FileTransferFrontierRequestFrameV6 request,
        out long bootstrapGeneration)
    {
        bootstrapGeneration = 0;
        if (request.TransportEpoch <= 0 ||
            !string.IsNullOrWhiteSpace(request.RepairRequestId) ||
            request.MissingRanges.Count != 0 ||
            !string.Equals(
                request.Priority,
                FallbackV6ProofBootstrapPriority,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.RecoveryMode,
                FallbackV6ProofBootstrapRecoveryMode,
                StringComparison.Ordinal) ||
            string.IsNullOrEmpty(request.RequestCorrelationId) ||
            !request.RequestCorrelationId.StartsWith(
                FallbackV6ProofBootstrapCorrelationPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        var generationText = request.RequestCorrelationId[
            FallbackV6ProofBootstrapCorrelationPrefix.Length..];
        return generationText.Length > 0 &&
               generationText[0] is >= '1' and <= '9' &&
               long.TryParse(
                   generationText,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out bootstrapGeneration) &&
               bootstrapGeneration > 0 &&
               string.Equals(
                   request.RequestCorrelationId,
                   CreateFallbackV6ProofBootstrapCorrelationId(
                       bootstrapGeneration),
                   StringComparison.Ordinal);
    }

    internal static bool IsFallbackV6ProofBootstrapReceiverState(
        FileTransferReceiverStateFrameV6 state)
        => state.TransportEpoch > 0 &&
           state.Epoch > 0 &&
           string.IsNullOrWhiteSpace(state.BatchId) &&
           string.IsNullOrWhiteSpace(state.RepairRequestId) &&
           state.MissingRanges.Count == 0 &&
           !state.ReceiverMemoryPressure &&
           !state.ReceiverDiskPressure &&
           !state.TerminalReady &&
           state.TransferPaused &&
           state.CreditUntilChunkIndexExclusive ==
               state.ContiguousCommittedChunkIndex &&
           string.Equals(
               state.TransferPauseReason,
               FallbackV6ProofBootstrapRecoveryMode,
               StringComparison.Ordinal) &&
           string.Equals(
               state.Priority,
               FallbackV6ProofBootstrapPriority,
               StringComparison.Ordinal) &&
           string.Equals(
               state.RecoveryMode,
               FallbackV6ProofBootstrapRecoveryMode,
               StringComparison.Ordinal);

    internal static bool TryValidateFallbackV6ProofBootstrapCommandFrame(
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferDataFrame frame,
        out PostTunaFallbackPeerProofFrameIdentity identity)
    {
        identity = null!;
        if (command.IsDefault ||
            !string.Equals(
                frame.SessionId,
                command.Observation.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                frame.TransferId,
                command.Observation.TransferId,
                StringComparison.Ordinal) ||
            !PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                frame,
                out identity) ||
            identity.TransportEpoch != command.TargetTransportEpoch)
        {
            return false;
        }

        return command.FrameKind switch
        {
            FileTransferFallbackV6ProofBootstrapFrameKind.FrontierRequest =>
                frame is FileTransferFrontierRequestFrameV6 request &&
                TryGetFallbackV6ProofBootstrapGeneration(
                    request,
                    out var bootstrapGeneration) &&
                bootstrapGeneration == command.BootstrapGeneration,
            FileTransferFallbackV6ProofBootstrapFrameKind.ReceiverState =>
                frame is FileTransferReceiverStateFrameV6 state &&
                IsFallbackV6ProofBootstrapReceiverState(state),
            _ => false,
        };
    }

    internal static string GetFallbackV6ProofBootstrapReceiptReasonCode(
        FileTransferFallbackV6ProofBootstrapReceipt receipt)
        => receipt.Status switch
        {
            FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered =>
                "delivered",
            FileTransferFallbackV6ProofBootstrapReceiptStatus.Held =>
                string.Equals(
                    receipt.Reason,
                    "filetransfer_fallback_v6_proof_bootstrap_data_session_unavailable",
                    StringComparison.Ordinal)
                    ? "data_session_unavailable"
                    : "held_other",
            FileTransferFallbackV6ProofBootstrapReceiptStatus.Rejected =>
                receipt.Reason switch
                {
                    "filetransfer_fallback_v6_proof_bootstrap_execution_conflict" =>
                        "execution_conflict",
                    "filetransfer_fallback_v6_proof_bootstrap_execution_invalid" =>
                        "execution_invalid",
                    "filetransfer_fallback_v6_proof_bootstrap_stale_before_proof_send" =>
                        "stale_before_proof_send",
                    "filetransfer_fallback_v6_proof_bootstrap_canceled" =>
                        "canceled",
                    "filetransfer_fallback_v6_proof_bootstrap_send_failed" =>
                        "send_failed",
                    "filetransfer_fallback_v6_proof_bootstrap_bridge_failed" =>
                        "bridge_failed",
                    "filetransfer_fallback_v6_proof_bootstrap_source_scope_replaced" =>
                        "source_scope_replaced",
                    _ => "rejected_other",
                },
            _ => "rejected_other",
        };
}
