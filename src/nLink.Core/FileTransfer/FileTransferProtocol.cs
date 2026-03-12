namespace NLink.Core.FileTransfer;

public static class FileTransferProtocol
{
    public const string Kind = "filetransfer";

    public const string OfferTypeV1 = "filetransfer.offer.v1";
    public const string AcceptTypeV1 = "filetransfer.accept.v1";
    public const string DeclineTypeV1 = "filetransfer.decline.v1";
    public const string StartTypeV1 = "filetransfer.start.v1";
    public const string ChunkTypeV1 = "filetransfer.chunk.v1";
    public const string WindowUpdateTypeV1 = "filetransfer.window_update.v1";
    public const string WindowUpdateTypeV2 = "filetransfer.window_update.v2";
    public const string MissingRangeTypeV1 = "filetransfer.missing_range.v1";
    public const string CancelTypeV1 = "filetransfer.cancel.v1";
    public const string ErrorTypeV1 = "filetransfer.error.v1";
    public const string CompleteTypeV1 = "filetransfer.complete.v1";

    public const int Sha256LengthBytes = 32;
    public const int MaxTransferIdLength = 128;
    public const int MaxFileNameLength = 240;
    public const int MaxReasonLength = 256;
    public const int MaxErrorCodeLength = 64;
    public const int MaxErrorMessageLength = 256;
    public const int MaxChunkRawBytes = 24 * 1024;
    public const int MaxSerializedChunkPayloadBytes = 34 * 1024;
    public const int MaxWindowGrantChunkCount = 256;
    public const int MaxMissingRangeCount = 4;
}
