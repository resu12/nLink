using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    // A digest of the private offline receipt, not a new Snapshot authority kind.
    // Only projected identities and hashes enter the operational writer.
    internal sealed record FrontierPublicationWriterBinding(string ReceiptHash, string CompletionHash,
        string ObservationId, string CoordinatorId, string CompletionId, string Outcome, int LifecycleEnd)
    {
        internal const string EventName = "filetransfer_frontier_publication_receipt_closed";
        internal const string HashDomain = "sha256-utf8-canonical-offline-frontier-receipt-v2";
        internal const string Clock = "offline_injected_v1";

        internal static FrontierPublicationWriterBinding Create(FrontierPublicationReceipt receipt)
        {
            var completion = SessionFileTransferService.CreateCoordinatorSnapshotEvidence(receipt.Completion);
            var payload = CanonicalPayload(receipt);
            return new("ftfr1-" + Hash(payload), "ftfr1-" + completion.CalculatePayloadSha256(),
                "ftfr1-" + Hash(receipt.Lifecycle.ObservationId), completion.CoordinatorInstanceId,
                completion.TransitionId, receipt.Outcome, receipt.Lifecycle.LastSequence);
        }

        // Preserve the exact hashed bytes across PowerShell hosts; reparsing and
        // reserializing dates/escapes is not the receipt hash domain.
        internal static string CanonicalPayload(FrontierPublicationReceipt receipt)
            => FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(new
            {
                HashDomain, ClockDomain = FrontierPublicationOfflineCapture.ClockDomain, Receipt = receipt,
            }, new JsonSerializerOptions());

        internal string ToLogMessage()
            => $"event={EventName}; authority_effect=none; frontier_receipt_version=1; frontier_receipt_clock={Clock}" +
                $"; frontier_receipt_hash={ReceiptHash}; frontier_completion_hash={CompletionHash}" +
                $"; frontier_observation_id={ObservationId}; frontier_coordinator_id={CoordinatorId}" +
                $"; frontier_completion_id={CompletionId}; frontier_receipt_outcome={Outcome}; frontier_lifecycle_end={LifecycleEnd}";

        private static string Hash(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
