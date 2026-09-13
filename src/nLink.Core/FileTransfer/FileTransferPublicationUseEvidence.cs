using System.Runtime.CompilerServices;

namespace NLink.Core.FileTransfer;

internal enum FileTransferPublicationUseKind
{
    V4PhysicalSubmission,
    V4PrePumpRequest,
    ReceiveGuard,
    ReceiveDispatch,
}

// Diagnostic association by object identity, outside authority equality and wire data.
internal sealed record FileTransferPublicationUseEvidence(
    FileTransferRoutePublicationEvidence Publication,
    FileTransferPublicationUseKind Kind)
{
    private static readonly ConditionalWeakTable<FileTransferDataPathSendAuthority,
        FileTransferPublicationUseEvidence> sends = new();

    internal static void CaptureSend(FileTransferDataPathSendAuthority? authority,
        FileTransferRoutePublicationEvidence? publication, FileTransferPublicationUseKind kind)
    {
        if (authority is not null && publication is not null &&
            kind is FileTransferPublicationUseKind.V4PhysicalSubmission or FileTransferPublicationUseKind.V4PrePumpRequest)
        {
            // Each producer creates its authority before this one capture. Never
            // relabel an existing object's capture, including after an await.
            _ = sends.GetValue(authority, _ => new(publication, kind));
        }
    }

    internal static FileTransferPublicationUseEvidence? GetSend(FileTransferDataPathSendAuthority? authority)
        => authority is not null && sends.TryGetValue(authority, out var evidence) ? evidence : null;

    internal string FormatLogSuffix()
        => Publication.FormatLogSuffix() + "; publication_use_kind=" + (Kind switch
        {
            FileTransferPublicationUseKind.V4PhysicalSubmission => "v4_physical_submission",
            FileTransferPublicationUseKind.V4PrePumpRequest => "v4_pre_pump_request",
            FileTransferPublicationUseKind.ReceiveGuard => "receive_guard",
            FileTransferPublicationUseKind.ReceiveDispatch => "receive_dispatch",
            _ => "unbound",
        });
}
