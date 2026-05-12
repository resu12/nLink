namespace NLink.Core.FileTransfer;

public static class FileTransferProtocol
{
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
    public const string ManifestFrameTypeV5 = "filetransfer.manifest.v5";
    public const string StateFrameTypeV5 = "filetransfer.state.v5";
    public const string ChunkBatchFrameTypeV5 = "filetransfer.chunk_batch.v5";
    public const string SessionCompleteFrameTypeV5 = "filetransfer.complete.v5";
    public const string SessionCancelFrameTypeV5 = "filetransfer.cancel.v5";
    public const string ErrorFrameTypeV5 = "filetransfer.error.v5";
    public const string PauseControlFrameTypeV5 = "filetransfer.pause_control.v5";
    public const string HandoffFrameTypeV5 = "filetransfer.handoff.v5";
    public const string RepairRequestFrameTypeV5 = "filetransfer.repair_request.v5";
    public const string RepairProofFrameTypeV5 = "filetransfer.repair_proof.v5";
    public const string ManifestFrameTypeV6 = "filetransfer.manifest.v6";
    public const string ReceiverStateFrameTypeV6 = "filetransfer.receiver_state.v6";
    public const string ChunkBatchFrameTypeV6 = "filetransfer.chunk_batch.v6";
    public const string TransportEpochFrameTypeV6 = "filetransfer.transport_epoch.v6";
    public const string TransportProbeFrameTypeV6 = "filetransfer.transport_probe.v6";
    public const string FrontierRequestFrameTypeV6 = "filetransfer.frontier_request.v6";
    public const string RepairProofFrameTypeV6 = "filetransfer.repair_proof.v6";
    public const string SessionCompleteFrameTypeV6 = "filetransfer.complete.v6";
    public const string SessionCancelFrameTypeV6 = "filetransfer.cancel.v6";
    public const string ErrorFrameTypeV6 = "filetransfer.error.v6";
    public const string PauseControlFrameTypeV6 = "filetransfer.pause_control.v6";
    public const string HeartbeatFrameTypeV6 = "filetransfer.heartbeat.v6";

    public const string SessionRoleSender = "Sender";
    public const string SessionRoleReceiver = "Receiver";
    public const int ProtocolVersionV4 = 4;
    public const int ProtocolVersionV5 = 5;
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
    public const int MaxStateMissingRangesV5 = MaxStateMissingRangesV4;
    public const int MaxStateMissingChunksV5 = MaxStateMissingChunksV4;
    public const int MaxChunkCountV5 = MaxChunkCountV4;
    public const int MaxChunkBatchSegmentsV5 = MaxChunkBatchSegmentsV4;
    public const int MaxChunkBatchRawBytesV5 = MaxChunkBatchRawBytesV4;
    public const int MaxSerializedChunkBatchPayloadBytesV5 = MaxSerializedChunkBatchPayloadBytesV4;
    public const int MaxStateMissingRangesV6 = MaxStateMissingRangesV5;
    public const int MaxStateMissingChunksV6 = 4096;
    public const int MaxChunkCountV6 = MaxChunkCountV5;
    public const int MaxChunkBatchSegmentsV6 = 8;
    public const int MaxChunkBatchRawBytesV6 = MaxChunkBatchRawBytesV5;
    public const int MaxSerializedChunkBatchPayloadBytesV6 = MaxSerializedChunkBatchPayloadBytesV5;

    public static bool IsV6DataFrame(FileTransferDataFrame? frame)
        => frame is FileTransferManifestFrameV6
            or FileTransferReceiverStateFrameV6
            or FileTransferChunkBatchFrameV6
            or FileTransferTransportEpochFrameV6
            or FileTransferTransportProbeFrameV6
            or FileTransferFrontierRequestFrameV6
            or FileTransferRepairProofFrameV6
            or FileTransferCompleteFrameV6
            or FileTransferCancelFrameV6
            or FileTransferErrorFrameV6
            or FileTransferPauseControlFrameV6
            or FileTransferHeartbeatFrameV6;

    public static bool IsV6DataFrameType(string? frameType)
        => frameType is ManifestFrameTypeV6
            or ReceiverStateFrameTypeV6
            or ChunkBatchFrameTypeV6
            or TransportEpochFrameTypeV6
            or TransportProbeFrameTypeV6
            or FrontierRequestFrameTypeV6
            or RepairProofFrameTypeV6
            or SessionCompleteFrameTypeV6
            or SessionCancelFrameTypeV6
            or ErrorFrameTypeV6
            or PauseControlFrameTypeV6
            or HeartbeatFrameTypeV6;
}
