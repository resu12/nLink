using NLink.Core.FileTransfer;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed class FileTransferPayloadCodecTests
{
    [Fact]
    public void Offer_RoundTrips_AndNormalizesFields()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferOfferV1
            {
                Kind = "filetransfer",
                Type = FileTransferProtocol.OfferTypeV1,
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                FileName = " report.pdf ",
                FileSizeBytes = 123,
                Sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeOffer(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("report.pdf", message.FileName);
        Assert.Equal(FileTransferProtocol.Kind, message.Kind);
        Assert.Equal(FileTransferProtocol.OfferTypeV1, message.Type);
    }

    [Fact]
    public void Offer_RejectsInvalidHashLength()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferOfferV1
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                FileName = "report.pdf",
                FileSizeBytes = 123,
                Sha256Base64 = Convert.ToBase64String(new byte[16]),
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeOffer(payload, out _));
    }

    [Fact]
    public void Offer_RejectsMissingTransferId()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.OfferTypeV1,
                sessionId = "session_a",
                transferId = "",
                fileName = "report.pdf",
                fileSizeBytes = 123L,
                sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeOffer(payload, out _));
    }

    [Fact]
    public void Accept_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferAcceptV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeAccept(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(FileTransferProtocol.AcceptTypeV1, message.Type);
    }

    [Fact]
    public void Accept_RejectsMissingSessionId()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.AcceptTypeV1,
                sessionId = "",
                transferId = "transfer_a",
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeAccept(payload, out _));
    }

    [Fact]
    public void Start_RoundTrips_AndPreservesNegotiatedChunkSettings()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferStartV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                FileName = " report.pdf ",
                FileSizeBytes = 4096,
                Sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
                ChunkCount = 4,
                ChunkSizeBytes = 1024,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeStart(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("report.pdf", message.FileName);
        Assert.Equal(4096, message.FileSizeBytes);
        Assert.Equal(4, message.ChunkCount);
        Assert.Equal(1024, message.ChunkSizeBytes);
    }

    [Fact]
    public void Start_RejectsMissingSessionId()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.StartTypeV1,
                sessionId = "",
                transferId = "transfer_a",
                fileName = "report.pdf",
                fileSizeBytes = 4096L,
                sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
                chunkCount = 4,
                chunkSizeBytes = 1024,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeStart(payload, out _));
    }

    [Fact]
    public void Chunk_RoundTrips_WithinBudget()
    {
        var chunkBytes = new byte[1024];
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferChunkV1
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                ChunkIndex = 0,
                ChunkCount = 4,
                DataBase64 = Convert.ToBase64String(chunkBytes),
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeChunk(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(0, message.ChunkIndex);
        Assert.Equal(4, message.ChunkCount);
    }

    [Fact]
    public void Chunk_RejectsMissingSessionId()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.ChunkTypeV1,
                sessionId = "",
                transferId = "transfer_a",
                chunkIndex = 0,
                chunkCount = 1,
                dataBase64 = Convert.ToBase64String(new byte[8]),
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeChunk(payload, out _));
    }

    [Fact]
    public void Chunk_RejectsOversizedRawPayload()
    {
        var chunkBytes = new byte[FileTransferProtocol.MaxChunkRawBytes + 1];
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new FileTransferChunkV1
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(chunkBytes),
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeChunk(payload, out _));
    }

    [Fact]
    public void Chunk_Serialize_MaxChunkRawBytes_StaysWithinBudget()
    {
        var chunkBytes = new byte[FileTransferProtocol.MaxChunkRawBytes];
        new Random(12345).NextBytes(chunkBytes);

        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferChunkV1
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(chunkBytes),
            });

        Assert.InRange(payload.Length, 1, FileTransferProtocol.MaxSerializedChunkPayloadBytes);
    }

    [Fact]
    public void ComputeSafeRawChunkSizeForBudget_ReducesRequestedChunkSize_WhenIdsIncreaseOverhead()
    {
        var sessionId = new string('s', 37);
        var transferId = new string('t', 32);
        var requestedChunkSize = 32 * 1024;
        var safeChunkSize = FileTransferPayloadCodec.ComputeSafeRawChunkSizeForBudget(
            sessionId,
            transferId,
            chunkCount: 3761,
            requestedMaxChunkSize: requestedChunkSize);

        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferChunkV1
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = 0,
                ChunkCount = 3761,
                DataBase64 = Convert.ToBase64String(new byte[safeChunkSize]),
            });

        Assert.True(safeChunkSize < requestedChunkSize);
        Assert.InRange(payload.Length, 1, FileTransferProtocol.MaxSerializedChunkPayloadBytes);
    }

    [Fact]
    public void Decline_RoundTrips_AndNormalizesReason()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferDeclineV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                Reason = " busy ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeDecline(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("busy", message.Reason);
    }

    [Fact]
    public void Decline_RejectsReasonThatExceedsBudget()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.DeclineTypeV1,
                sessionId = "session_a",
                transferId = "transfer_a",
                reason = new string('x', FileTransferProtocol.MaxReasonLength + 1),
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeDecline(payload, out _));
    }

    [Fact]
    public void Cancel_RoundTrips_AndNormalizesReason()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferCancelV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                Reason = " user_canceled ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeCancel(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("user_canceled", message.Reason);
    }

    [Fact]
    public void WindowUpdate_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferWindowUpdateV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                NextExpectedChunkIndex = 4,
                GrantedUntilChunkIndexExclusive = 12,
                BytesReceived = 16_384,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeWindowUpdate(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(4, message.NextExpectedChunkIndex);
        Assert.Equal(12, message.GrantedUntilChunkIndexExclusive);
        Assert.Equal(16_384, message.BytesReceived);
    }

    [Fact]
    public void WindowUpdate_RejectsInvalidGrantRange()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.WindowUpdateTypeV2,
                sessionId = "session_a",
                transferId = "transfer_a",
                nextExpectedChunkIndex = 4,
                grantedUntilChunkIndexExclusive = 3,
                bytesReceived = 16_384L,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeWindowUpdate(payload, out _));
    }

    [Fact]
    public void MissingRange_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferMissingRangeV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                Ranges =
                [
                    new FileTransferChunkRangeV1
                    {
                        StartChunkIndex = 4,
                        EndChunkIndexInclusive = 7,
                    },
                ],
                NextExpectedChunkIndex = 4,
                HighestBufferedChunkIndex = 12,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeMissingRange(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Single(message.Ranges);
        Assert.Equal(4, message.Ranges[0].StartChunkIndex);
        Assert.Equal(7, message.Ranges[0].EndChunkIndexInclusive);
        Assert.Equal(4, message.NextExpectedChunkIndex);
        Assert.Equal(12, message.HighestBufferedChunkIndex);
    }

    [Fact]
    public void MissingRange_RejectsInvalidRange()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.MissingRangeTypeV1,
                sessionId = "session_a",
                transferId = "transfer_a",
                ranges = new[]
                {
                    new
                    {
                        startChunkIndex = 8,
                        endChunkIndexInclusive = 7,
                    },
                },
                nextExpectedChunkIndex = 4,
                highestBufferedChunkIndex = 12,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeMissingRange(payload, out _));
    }

    [Fact]
    public void Error_RoundTrips_AndNormalizesMessage()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferErrorV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                ErrorCode = " hash_mismatch ",
                Message = " verification failed ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeError(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("hash_mismatch", message.ErrorCode);
        Assert.Equal("verification failed", message.Message);
    }

    [Fact]
    public void Error_RejectsMissingErrorCode()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.ErrorTypeV1,
                sessionId = "session_a",
                transferId = "transfer_a",
                errorCode = "",
                message = "verification failed",
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeError(payload, out _));
    }

    [Fact]
    public void Complete_RoundTrips_WithVerifiedSizeAndHash()
    {
        var expectedHash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferCompleteV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                FileSizeBytes = 4096,
                Sha256Base64 = expectedHash,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeComplete(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(4096, message.FileSizeBytes);
        Assert.Equal(expectedHash, message.Sha256Base64);
    }

    [Fact]
    public void Complete_RejectsInvalidHashLength()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.CompleteTypeV1,
                sessionId = "session_a",
                transferId = "transfer_a",
                fileSizeBytes = 4096L,
                sha256Base64 = Convert.ToBase64String(new byte[8]),
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeComplete(payload, out _));
    }
}
