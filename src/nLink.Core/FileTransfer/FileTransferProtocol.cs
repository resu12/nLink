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

    public const string SessionRoleSender = "Sender";
    public const string SessionRoleReceiver = "Receiver";
    public const int ProtocolVersionV4 = 4;
    public const int ProtocolVersionV5 = 5;

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
}
