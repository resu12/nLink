using System;
using NLink.Core;
using NLink.Core.FileTransfer;

namespace NLink.App.Services;

internal sealed record TunaHeaderStateProjection(bool IsActive, string StatusReason, bool FileUsesRegularNkn);

internal static class TunaHeaderStateProjector
{
    private const string FileTunaV4RouteToken = "file_tuna_v4";
    internal const string NoConnectionReason = "header_no_connection";
    internal const string LocalConnectionReason = "header_local_connection";

    public static TunaHeaderStateProjection FromSnapshot(
        SessionFileTransferSnapshot snapshot,
        string transportKind,
        bool transportActive,
        string? transportReason,
        TransportAccelerationPendingFileTransfer? pendingFileTransfer = null)
    {
        if (transportKind == "(none)")
        {
            return new TunaHeaderStateProjection(false, NoConnectionReason, false);
        }

        if (transportKind == "DevLocal" && !transportActive && transportReason == "transport_without_acceleration")
        {
            return new TunaHeaderStateProjection(false, LocalConnectionReason, false);
        }

        var activeTransferUsesTuna = false;
        var hasRegularFile = false;
        var hasOtherFile = false;

        Inspect(snapshot.Outbound);
        Inspect(snapshot.Inbound);

        var fileUsesRegularNkn = transportKind == "NKN" && hasRegularFile && !hasOtherFile;

        if (!transportActive && !activeTransferUsesTuna && transportReason == "runtime_unlock_pending_route_commit" &&
            HasFinishedPendingTransfer(snapshot, pendingFileTransfer))
        {
            return new TunaHeaderStateProjection(false, "file_transfer_tuna_idle", fileUsesRegularNkn);
        }

        // A file can retain its route across a disconnect. Only the transport confirms session readiness.
        return new TunaHeaderStateProjection(transportActive,
            string.IsNullOrWhiteSpace(transportReason) ? "inactive" : transportReason, fileUsesRegularNkn);

        void Inspect(FileTransferTransferSnapshot? transfer)
        {
            if (transfer is null || transfer.IsTerminal)
            {
                return;
            }

            // Offers already have a route tuple; only describe a file once its data phase starts.
            if (transfer.State is FileTransferTransferState.Sending or FileTransferTransferState.Receiving)
            {
                if ((transfer.RouteToken, transfer.ProtocolVersion) is ("regular_nkn_v4_fast", 4) or ("post_tuna_fallback_v6", 6))
                    hasRegularFile = true;
                else
                    hasOtherFile = true;
            }
            activeTransferUsesTuna |=
                transfer.ProtocolVersion == 4 &&
                string.Equals(transfer.RouteToken, FileTunaV4RouteToken, StringComparison.Ordinal);
        }
    }

    public static bool HasFinishedPendingTransfer(
        SessionFileTransferSnapshot snapshot, TransportAccelerationPendingFileTransfer? pendingFileTransfer)
    {
        return Matches(snapshot.Outbound) || Matches(snapshot.Inbound);

        bool Matches(FileTransferTransferSnapshot? transfer)
            => pendingFileTransfer is not null && transfer is { IsTerminal: true } &&
               string.Equals(transfer.SessionId, pendingFileTransfer.SessionId, StringComparison.Ordinal) &&
               string.Equals(transfer.TransferId, pendingFileTransfer.TransferId, StringComparison.Ordinal);
    }
}
