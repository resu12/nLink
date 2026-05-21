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
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = " FILE_TUNA_V6 ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeOffer(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal("report.pdf", message.FileName);
        Assert.Equal(FileTransferProtocol.Kind, message.Kind);
        Assert.Equal(FileTransferProtocol.OfferTypeV2, message.Type);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, message.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.FileTunaV6Token, message.FileTransferRoute);
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

    [Theory]
    [InlineData(null)]
    [InlineData(FileTransferProtocol.ProtocolVersionV5)]
    [InlineData(99)]
    public void Offer_RejectsMissingLegacyOrUnsupportedProtocol(int? preferredVersion)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.OfferTypeV2,
                sessionId = "session_a",
                transferId = "transfer_a",
                fileName = "report.pdf",
                fileSizeBytes = 123L,
                preferredDataProtocolVersion = preferredVersion,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeOffer(payload, out _));
    }

    [Fact]
    public void Offer_AcceptsV4Protocol()
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
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, message.PreferredDataProtocolVersion);
    }

    [Fact]
    public void Accept_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferAcceptV1
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = " FILE_TUNA_V6 ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeAccept(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(FileTransferProtocol.AcceptTypeV1, message.Type);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, message.AcceptedDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.FileTunaV6Token, message.FileTransferRoute);
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

    [Theory]
    [InlineData(null)]
    [InlineData(FileTransferProtocol.ProtocolVersionV5)]
    [InlineData(99)]
    public void Accept_RejectsMissingLegacyOrUnsupportedProtocol(int? acceptedVersion)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.AcceptTypeV1,
                sessionId = "session_a",
                transferId = "transfer_a",
                acceptedDataProtocolVersion = acceptedVersion,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeAccept(payload, out _));
    }

    [Fact]
    public void Accept_AcceptsV4Protocol()
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
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, message.AcceptedDataProtocolVersion);
    }

    [Fact]
    public void SessionOpenV6_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferSessionOpenV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = " FILE_TUNA_V6 ",
                SessionRole = " receiver ",
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 8,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeSessionOpen(payload, out var message);

        Assert.True(parsed);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, message.ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.FileTunaV6Token, message.FileTransferRoute);
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
                protocolVersion = FileTransferProtocol.ProtocolVersionV6,
                sessionRole = FileTransferProtocol.SessionRoleReceiver,
                chunkSizeBytes = 4096,
                initialPipelineDepth = 8,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeSessionOpen(payload, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(FileTransferProtocol.ProtocolVersionV5)]
    [InlineData(99)]
    public void SessionOpen_RejectsMissingLegacyOrUnsupportedProtocol(int? protocolVersion)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.SessionOpenTypeV2,
                sessionId = "session_a",
                transferId = "transfer_a",
                protocolVersion,
                sessionRole = FileTransferProtocol.SessionRoleReceiver,
                chunkSizeBytes = 4096,
                initialPipelineDepth = 8,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeSessionOpen(payload, out _));
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

    [Theory]
    [InlineData("offer")]
    [InlineData("accept")]
    [InlineData("session_open")]
    public void RouteToken_Missing_RemainsCompatible(string payloadKind)
    {
        var parsed = payloadKind switch
        {
            "offer" => FileTransferPayloadCodec.TryDeserializeOffer(
                FileTransferPayloadCodec.Serialize(
                    new FileTransferOfferV2
                    {
                        SessionId = "session_a",
                        TransferId = "transfer_a",
                        FileName = "report.pdf",
                        FileSizeBytes = 123,
                        PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    }),
                out var offer) && offer.FileTransferRoute is null,
            "accept" => FileTransferPayloadCodec.TryDeserializeAccept(
                FileTransferPayloadCodec.Serialize(
                    new FileTransferAcceptV1
                    {
                        SessionId = "session_a",
                        TransferId = "transfer_a",
                        AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    }),
                out var accept) && accept.FileTransferRoute is null,
            "session_open" => FileTransferPayloadCodec.TryDeserializeSessionOpen(
                FileTransferPayloadCodec.Serialize(
                    new FileTransferSessionOpenV2
                    {
                        SessionId = "session_a",
                        TransferId = "transfer_a",
                        ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                        SessionRole = FileTransferProtocol.SessionRoleSender,
                        ChunkSizeBytes = 4096,
                        InitialPipelineDepth = 1,
                    }),
                out var sessionOpen) && sessionOpen.FileTransferRoute is null,
            _ => false,
        };

        Assert.True(parsed);
    }

    [Theory]
    [InlineData("offer")]
    [InlineData("accept")]
    [InlineData("session_open")]
    public void RouteToken_Invalid_IsRejected(string payloadKind)
    {
        var payload = payloadKind switch
        {
            "offer" => JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    kind = FileTransferProtocol.Kind,
                    type = FileTransferProtocol.OfferTypeV2,
                    sessionId = "session_a",
                    transferId = "transfer_a",
                    fileName = "report.pdf",
                    fileSizeBytes = 123L,
                    preferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    fileTransferRoute = "not_a_route",
                }),
            "accept" => JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    kind = FileTransferProtocol.Kind,
                    type = FileTransferProtocol.AcceptTypeV1,
                    sessionId = "session_a",
                    transferId = "transfer_a",
                    acceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    fileTransferRoute = "not_a_route",
                }),
            _ => JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    kind = FileTransferProtocol.Kind,
                    type = FileTransferProtocol.SessionOpenTypeV2,
                    sessionId = "session_a",
                    transferId = "transfer_a",
                    protocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    sessionRole = FileTransferProtocol.SessionRoleSender,
                    chunkSizeBytes = 4096,
                    initialPipelineDepth = 1,
                    fileTransferRoute = "not_a_route",
                }),
        };

        AssertRoutePayloadRejected(payloadKind, payload);
    }

    [Theory]
    [InlineData("offer")]
    [InlineData("accept")]
    [InlineData("session_open")]
    public void RouteToken_ProtocolMismatch_IsRejected(string payloadKind)
    {
        var payload = payloadKind switch
        {
            "offer" => JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    kind = FileTransferProtocol.Kind,
                    type = FileTransferProtocol.OfferTypeV2,
                    sessionId = "session_a",
                    transferId = "transfer_a",
                    fileName = "report.pdf",
                    fileSizeBytes = 123L,
                    preferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    fileTransferRoute = FileTransferRouteResolver.FileTunaV6Token,
                }),
            "accept" => JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    kind = FileTransferProtocol.Kind,
                    type = FileTransferProtocol.AcceptTypeV1,
                    sessionId = "session_a",
                    transferId = "transfer_a",
                    acceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    fileTransferRoute = FileTransferRouteResolver.FileTunaV6Token,
                }),
            _ => JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    kind = FileTransferProtocol.Kind,
                    type = FileTransferProtocol.SessionOpenTypeV2,
                    sessionId = "session_a",
                    transferId = "transfer_a",
                    protocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    sessionRole = FileTransferProtocol.SessionRoleSender,
                    chunkSizeBytes = 4096,
                    initialPipelineDepth = 1,
                    fileTransferRoute = FileTransferRouteResolver.FileTunaV6Token,
                }),
        };

        AssertRoutePayloadRejected(payloadKind, payload);
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

    [Fact]
    public void PauseControlV6_RoundTrips_AndNormalizesMetadata()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferPauseControlV6
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                Epoch = 4,
                Paused = true,
                Reason = " user_pause ",
                TransportEpoch = 7,
                BatchId = " batch-1 ",
                RepairRequestId = " repair-1 ",
                Priority = " frontier ",
                RecoveryMode = " fallback ",
            });

        var parsed = FileTransferPayloadCodec.TryDeserializePauseControl(payload, out var message);

        Assert.True(parsed);
        Assert.Equal(FileTransferProtocol.PauseControlFrameTypeV6, message.Type);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(4, message.Epoch);
        Assert.True(message.Paused);
        Assert.Equal("user_pause", message.Reason);
        Assert.Equal(7, message.TransportEpoch);
        Assert.Equal("batch-1", message.BatchId);
        Assert.Equal("repair-1", message.RepairRequestId);
        Assert.Equal("frontier", message.Priority);
        Assert.Equal("fallback", message.RecoveryMode);
    }

    [Fact]
    public void PauseControlV6_RejectsMalformedMetadata()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.PauseControlFrameTypeV6,
                sessionId = "session_a",
                transferId = "transfer_a",
                epoch = -1,
                paused = true,
                transportEpoch = 0L,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializePauseControl(payload, out _));
    }

    [Fact]
    public void HeartbeatV6_RoundTrips()
    {
        var payload = FileTransferPayloadCodec.Serialize(
            new FileTransferHeartbeatV6
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                TransportEpoch = 3,
                Sequence = 12,
                SentUnixTimeMilliseconds = 1_725_000_123_456,
            });

        var parsed = FileTransferPayloadCodec.TryDeserializeHeartbeat(payload, out var message);

        Assert.True(parsed);
        Assert.Equal(FileTransferProtocol.HeartbeatFrameTypeV6, message.Type);
        Assert.Equal("session_a", message.SessionId);
        Assert.Equal("transfer_a", message.TransferId);
        Assert.Equal(3, message.TransportEpoch);
        Assert.Equal(12, message.Sequence);
        Assert.Equal(1_725_000_123_456, message.SentUnixTimeMilliseconds);
    }

    [Theory]
    [InlineData(0, 1_725_000_123_456L)]
    [InlineData(1, 0L)]
    public void HeartbeatV6_RejectsMalformedPayload(long sequence, long sentUnixTimeMilliseconds)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                kind = FileTransferProtocol.Kind,
                type = FileTransferProtocol.HeartbeatFrameTypeV6,
                sessionId = "session_a",
                transferId = "transfer_a",
                transportEpoch = 0L,
                sequence,
                sentUnixTimeMilliseconds,
            });

        Assert.False(FileTransferPayloadCodec.TryDeserializeHeartbeat(payload, out _));
    }

    private static void AssertRoutePayloadRejected(string payloadKind, byte[] payload)
    {
        var rejected = payloadKind switch
        {
            "offer" => !FileTransferPayloadCodec.TryDeserializeOffer(payload, out _),
            "accept" => !FileTransferPayloadCodec.TryDeserializeAccept(payload, out _),
            "session_open" => !FileTransferPayloadCodec.TryDeserializeSessionOpen(payload, out _),
            _ => false,
        };

        Assert.True(rejected);
    }
}
