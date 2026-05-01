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

    public const string SessionRoleSender = "Sender";
    public const string SessionRoleReceiver = "Receiver";
    public const int ProtocolVersionV4 = 4;

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
    public const int MaxChunkBatchSegmentsV4 = 3;
    public const int MaxChunkBatchRawBytesV4 = FileTransferChunkBudget.MaxRawBatchBytes;
    public const int MaxSerializedChunkBatchPayloadBytesV4 = FileTransferChunkBudget.MaxSerializedChunkBatchPayloadBytes;
}
