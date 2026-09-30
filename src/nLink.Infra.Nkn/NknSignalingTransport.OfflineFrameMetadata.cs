using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

// Immutable test correlation only: no payload, context or authority is exposed.
internal sealed record OfflineFileFrameMetadata(string SessionId, string TransferId,
    bool IsRepair, int StartChunkIndex, int ChunkCount, long? TransportEpoch,
    int EnvelopeLength, string EnvelopeSha256);

public sealed partial class NknSignalingTransport
{
    internal Action<OfflineFileFrameMetadata>? OfflineFrameObserverForTests { get; set; }

    private void ObserveOfflineFileFrame(FileTransferDataFrame? frame, byte[] envelope)
    {
        var observer = OfflineFrameObserverForTests;
        if (observer is null || frame is not FileTransferChunkBatchFrame batch) return;
        // An observer cannot replace transport selection, authorize a send, or
        // change its result. Test consumers retain their own failure separately.
        try
        {
            var metadata = frame as IFileTransferTransportMetadataFrame;
            observer(new(frame.SessionId, frame.TransferId,
                !string.IsNullOrWhiteSpace(metadata?.RepairRequestId), batch.StartChunkIndex,
                batch.ChunkCount, metadata?.TransportEpoch, envelope.Length,
                Convert.ToHexString(SHA256.HashData(envelope))));
        }
        catch (Exception) { }
    }
}
