namespace NLink.Core.FileTransfer;

public static class FileTransferProtocol
{
    public const string Kind = "filetransfer";

    public const string OfferTypeV1 = "filetransfer.offer.v1";
    public const string OfferTypeV2 = "filetransfer.offer.v2";
    public const string AcceptTypeV1 = "filetransfer.accept.v1";
    public const string DeclineTypeV1 = "filetransfer.decline.v1";
    public const string StartTypeV1 = "filetransfer.start.v1";
    public const string StartTypeV2 = "filetransfer.start.v2";
    public const string ChunkTypeV1 = "filetransfer.chunk.v1";
    public const string WindowUpdateTypeV1 = "filetransfer.window_update.v1";
    public const string WindowUpdateTypeV2 = "filetransfer.window_update.v2";
    public const string MissingRangeTypeV1 = "filetransfer.missing_range.v1";
    public const string PressureStateTypeV1 = "filetransfer.pressure_state.v1";
    public const string SessionOpenTypeV2 = "filetransfer.session_open.v2";
    public const string CancelTypeV1 = "filetransfer.cancel.v1";
    public const string ErrorTypeV1 = "filetransfer.error.v1";
    public const string CompleteTypeV1 = "filetransfer.complete.v1";
    public const string ManifestFrameTypeV2 = "filetransfer.manifest.v2";
    public const string RequestChunksFrameTypeV2 = "filetransfer.request_chunks.v2";
    public const string ChunkDataFrameTypeV2 = "filetransfer.chunk_data.v2";
    public const string ChunkBatchFrameTypeV2 = "filetransfer.chunk_batch.v2";
    public const string AckProgressFrameTypeV2 = "filetransfer.ack_progress.v2";
    public const string SessionCancelFrameTypeV2 = "filetransfer.session_cancel.v2";
    public const string SessionCompleteFrameTypeV2 = "filetransfer.session_complete.v2";
    public const string ManifestFrameTypeV3 = "filetransfer.manifest.v3";
    public const string GrantWindowFrameTypeV3 = "filetransfer.grant_window.v3";
    public const string AckProgressFrameTypeV3 = "filetransfer.ack_progress.v3";
    public const string ChunkDataFrameTypeV3 = "filetransfer.chunk_data.v3";
    public const string ChunkBatchFrameTypeV3 = "filetransfer.chunk_batch.v3";
    public const string RepairRequestFrameTypeV3 = "filetransfer.repair_request.v3";

    public const string PressureModeNormal = "Normal";
    public const string PressureModeCatchUpOnly = "CatchUpOnly";
    public const string PressureReasonGapRepair = "GapRepair";
    public const string PressureReasonMediaProtection = "MediaProtection";
    public const string PressureReasonBulkBacklog = "BulkBacklog";
    public const string SessionRoleSender = "Sender";
    public const string SessionRoleReceiver = "Receiver";
    public const int ProtocolVersionV2 = 2;
    public const int ProtocolVersionV3 = 3;

    public const int Sha256LengthBytes = 32;
    public const int MaxTransferIdLength = 128;
    public const int MaxFileNameLength = 240;
    public const int MaxReasonLength = 256;
    public const int MaxErrorCodeLength = 64;
    public const int MaxErrorMessageLength = 256;
    public const int MaxChunkRawBytes = FileTransferChunkBudget.MaxRawChunkBytes;
    public const int MaxSerializedChunkPayloadBytes = FileTransferChunkBudget.MaxSerializedChunkPayloadBytes;
}
