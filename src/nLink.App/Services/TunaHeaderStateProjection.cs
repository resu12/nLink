using System;
using NLink.Core;
using NLink.Core.FileTransfer;

namespace NLink.App.Services;

internal sealed record TunaHeaderStateProjection(bool IsActive, string StatusReason);

internal static class TunaHeaderStateProjector
{
    private const string FileTunaV4RouteToken = "file_tuna_v4";
    private const string ActiveFileTunaReason = "file_transfer_route_file_tuna_v4";

    public static TunaHeaderStateProjection FromSnapshot(
        SessionFileTransferSnapshot snapshot,
        bool transportActive,
        string? transportReason,
        TransportAccelerationPendingFileTransfer? pendingFileTransfer = null)
    {
        var hasActiveTransfer = false;
        var activeTransferUsesTuna = false;

        Inspect(snapshot.Outbound);
        Inspect(snapshot.Inbound);

        if (!transportActive && !activeTransferUsesTuna && transportReason == "runtime_unlock_pending_route_commit" &&
            HasFinishedPendingTransfer(snapshot, pendingFileTransfer))
        {
            return new TunaHeaderStateProjection(false, "file_transfer_tuna_idle");
        }

        if (!hasActiveTransfer)
        {
            return new TunaHeaderStateProjection(
                transportActive,
                string.IsNullOrWhiteSpace(transportReason) ? "inactive" : transportReason);
        }

        var reason = !string.IsNullOrWhiteSpace(transportReason)
            ? transportReason
            : activeTransferUsesTuna
                ? ActiveFileTunaReason
                : "inactive";
        // A regular-NKN file route does not deactivate the session's Tuna connection.
        // This is header presentation only; the transfer's route remains unchanged.
        return new TunaHeaderStateProjection(transportActive || activeTransferUsesTuna, reason);

        void Inspect(FileTransferTransferSnapshot? transfer)
        {
            if (transfer is null || transfer.IsTerminal)
            {
                return;
            }

            hasActiveTransfer = true;
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
