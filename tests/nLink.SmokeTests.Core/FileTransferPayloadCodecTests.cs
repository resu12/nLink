using NLink.Core.FileTransfer;
using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferPayloadCodecTests
{
    [Fact]
    public void Offer_RoundTrips_AndNormalizesFields()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferOfferV2
            {
                Kind = "filetransfer",
                Type = FileTransferProtocol.OfferTypeV2,
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                FileName = " report.pdf ",
                FileSizeBytes = 123,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeOffer(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("report.pdf", message.FileName);
        Assert.Equal(FileTransferProtocol.Kind, message.Kind);
        Assert.Equal(FileTransferProtocol.OfferTypeV2, message.Type);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, message.PreferredDataProtocolVersion);
    }

    [Fact]
    public void Offer_RejectsMissingTransferId()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.OfferTypeV2,
                sessionId = "session_a",
                transferId = "",
                fileName = "report.pdf",
                fileSizeBytes = 123L,
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeAccept(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(FileTransferProtocol.AcceptTypeV1, message.Type);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, message.AcceptedDataProtocolVersion);
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
    public void SessionOpenV4_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferSessionOpenV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = " receiver ",
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 8,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeSessionOpen(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, message.ProtocolVersion);
        Assert.Equal(FileTransferProtocol.SessionRoleReceiver, message.SessionRole);
        Assert.Equal(FileTransferProtocol.SessionOpenTypeV2, message.Type);
    }

    [Fact]
    public void SessionOpen_RejectsMissingSessionId()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.SessionOpenTypeV2,
                sessionId = "",
                transferId = "transfer_a",
                protocolVersion = FileTransferProtocol.ProtocolVersionV4,
                sessionRole = FileTransferProtocol.SessionRoleReceiver,
                chunkSizeBytes = 4096,
                initialPipelineDepth = 8,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeSessionOpen(payload, out _));
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
