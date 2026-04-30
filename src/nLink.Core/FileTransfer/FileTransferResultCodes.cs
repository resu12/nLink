namespace NLink.Core.FileTransfer;

public static class FileTransferResultCodes
{
    public const string Busy = "busy";
    public const string Declined = "declined";
    public const string InvalidState = "invalid_state";
    public const string SessionMismatch = "session_mismatch";
    public const string IntegrityMismatch = "integrity_mismatch";
    public const string SizeMismatch = "size_mismatch";
    public const string WriteOpenFailed = "write_open_failed";
    public const string WriteFailed = "write_failed";
    public const string FinalizeFailed = "finalize_failed";
    public const string PayloadBudgetExceeded = "payload_budget_exceeded";
    public const string ReadFailed = "read_failed";
    public const string TransportIncompatible = "transport_incompatible";
    public const string WindowTimeout = "window_timeout";
    public const string PullSessionStalled = "pull_session_stalled";
    public const string ReceiverBufferExhausted = "receiver_buffer_exhausted";
    public const string ReceiverFeedbackQueueExhausted = "receiver_feedback_queue_exhausted";
    public const string SenderCacheExhausted = "sender_cache_exhausted";
    public const string SenderRepairUnavailable = "sender_repair_unavailable";
    public const string V4RuntimeNotImplemented = "v4_runtime_not_implemented";
    public const string V4FileOnlyRequired = "v4_file_only_required";
    public const string V4SparseDestinationRequired = "v4_sparse_destination_required";
    public const string MetadataNotProvided = "metadata_not_provided";
    public const string CanceledLocal = "canceled_local";
    public const string CanceledRemote = "canceled_remote";
    public const string TransportDisconnected = "transport_disconnected";
    public const string TransportDetached = "transport_detached";
}
