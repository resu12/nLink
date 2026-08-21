using System;
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
        string? transportReason)
    {
        var hasActiveTransfer = false;
        var activeTransferUsesTuna = false;

        Inspect(snapshot.Outbound);
        Inspect(snapshot.Inbound);

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
        return new TunaHeaderStateProjection(activeTransferUsesTuna, reason);

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
}
