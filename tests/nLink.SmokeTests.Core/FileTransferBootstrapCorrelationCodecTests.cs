using System.Buffers.Binary;
using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferBootstrapCorrelationCodecTests
{
    private const string Issuer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Scope = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SenderId = "fbc1:" + Issuer + ":1:" + Scope;
    private const string ReceiverId = "fbc1:" + Scope + ":7:" + Issuer;

    [Theory]
    [InlineData(false, false, 55)]
    [InlineData(false, true, 55)]
    [InlineData(true, false, 56)]
    [InlineData(true, true, 56)]
    public void NewFrames_RoundTripExactBytesWithoutLegacyInheritance(bool state, bool echo, byte code)
    {
        var original = CreateFrame(state, echo);
        var bytes = FileTransferDataFrameCodec.Serialize(original);
        Assert.Equal(code, bytes[5]);
        Assert.Equal(1, bytes[4]);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(bytes, out var decoded));
        Assert.Equal(original, decoded);
        Assert.Equal(bytes, FileTransferDataFrameCodec.Serialize(decoded!));
        Assert.False(decoded is FileTransferStateFrameV4);
        Assert.False(decoded is FileTransferFrontierRequestFrameV6);
        Assert.False(FileTransferDataFrameCodec.TryDeserializeLegacyV4(bytes, out _));
        Assert.False(FileTransferDataFrameCodec.TryDeserialize([.. bytes, 0], out _));
        Assert.True(PostTunaFallbackPeerProofFrameIdentity.TryCreate(original, out var identity));
        Assert.True(identity.Matches(decoded!));
        Assert.False(identity.Matches(original with { ReceiverChallengeId = SenderId }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewFrames_MatchIndependentWireGolden(bool state)
    {
        var frame = CreateFrame(state, true);
        using var expected = new MemoryStream();
        expected.Write("NTF2"u8); // Existing 0x3246544e magic in little-endian wire order.
        expected.WriteByte(1);
        expected.WriteByte(state ? (byte)56 : (byte)55);
        WriteString(expected, "session");
        WriteString(expected, "transfer");
        WriteInt64(expected, 1);
        expected.Write([0, 0]); // No batch or repair ID.
        WriteOptional(expected, "proof");
        WriteOptional(expected, FileTransferBootstrapCorrelation.RecoveryMode);
        if (state)
        {
            WriteInt32(expected, 2);
            WriteInt32(expected, 3);
            WriteInt32(expected, 4);
            WriteInt32(expected, 3);
            WriteInt32(expected, 0);
            WriteInt64(expected, 3072);
            expected.Write([0, 0, 0, 1]);
            WriteOptional(expected, FileTransferBootstrapCorrelation.RecoveryMode);
        }
        WriteOptional(expected, SenderId);
        WriteOptional(expected, ReceiverId);
        Assert.Equal(expected.ToArray(), FileTransferDataFrameCodec.Serialize(frame));
    }

    public static IEnumerable<object[]> InvalidIds()
    {
        foreach (var id in new[]
        {
            "", " ", SenderId + " ", " " + SenderId, SenderId.ToUpperInvariant(),
            SenderId.Replace(":1:", ":0:"), SenderId.Replace(":1:", ":01:"),
            SenderId.Replace(":1:", ":+1:"), SenderId.Replace(":1:", ":-1:"),
            SenderId.Replace(":1:", ":9223372036854775808:"),
            SenderId.Replace(":1:", ":1.0:"), SenderId[..^1], SenderId + "b",
            SenderId.Replace("fbc1:", "fbc2:"), SenderId.Replace("a", "g"),
        })
        {
            yield return [id];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public void CorrelationId_RejectsNonCanonicalTextOnBothWireDirections(string id)
    {
        Assert.False(FileTransferBootstrapCorrelation.TryParseId(id, out _));
        var frame = CreateFrame(false, true) with { SenderRequestId = id };
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(frame));
        var valid = FileTransferDataFrameCodec.Serialize(CreateFrame(false, true));
        var prefixLength = valid.Length - (3 + SenderId.Length) - (3 + ReceiverId.Length);
        using var malformed = new MemoryStream();
        malformed.Write(valid.AsSpan(0, prefixLength));
        WriteOptional(malformed, id);
        WriteOptional(malformed, ReceiverId);
        Assert.False(FileTransferDataFrameCodec.TryDeserialize(malformed.ToArray(), out _));
    }

    [Fact]
    public void CorrelationId_ParsesMaximumGenerationWithoutTruncation()
    {
        var id = SenderId.Replace(":1:", ":9223372036854775807:");
        Assert.Equal(154, id.Length);
        Assert.True(FileTransferBootstrapCorrelation.TryParseId(id, out var parsed));
        Assert.Equal(Issuer, parsed.IssuerId);
        Assert.Equal(long.MaxValue, parsed.Generation);
        Assert.Equal(Scope, parsed.ScopeHash);
    }

    [Fact]
    public void NewFrames_RejectMissingRoleIdAndInvalidStateProfile()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            CreateFrame(false, true) with { SenderRequestId = null }));
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            CreateFrame(true, true) with { ReceiverChallengeId = null }));
        var state = Assert.IsType<FileTransferFallbackBootstrapStateFrameV6>(CreateFrame(true, true));
        foreach (var invalid in new[]
        {
            state with { Epoch = 0 }, state with { TransferPaused = false },
            state with { TerminalReady = true }, state with { ReceiverDiskPressure = true },
            state with { ReceiverMemoryPressure = true }, state with { CreditUntilChunkIndexExclusive = 4 },
            state with { BytesCommitted = -1 }, state with { TransportEpoch = 0 },
            state with { BatchId = "batch" }, state with { RepairRequestId = "repair" },
            state with { RecoveryMode = " " + FileTransferBootstrapCorrelation.RecoveryMode },
            state with { Priority = " proof " }, state with { TransferPauseReason = "other" },
        })
        {
            Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(invalid));
        }
    }

    [Fact]
    public void LegacyBootstrapFrames_PreservePreChangeBinaryGoldens()
    {
        // Captured from d654e55d Core DLL before this implementation was built.
        const string stateGolden = "TlRGMgEpBwBzZXNzaW9uCAB0cmFuc2ZlcgEAAAAAAAAAAAABBQBwcm9vZgElAHBvc3RfdHVuYV9mYWxsYmFja192Nl9wcm9vZl9ib290c3RyYXACAAAAAwAAAAQAAAADAAAAAAAAAAAMAAAAAAAAAAAAAQElAHBvc3RfdHVuYV9mYWxsYmFja192Nl9wcm9vZl9ib290c3RyYXA=";
        const string requestGolden = "TlRGMgEtBwBzZXNzaW9uCAB0cmFuc2ZlcgEAAAAAAAAAAAABBQBwcm9vZgElAHBvc3RfdHVuYV9mYWxsYmFja192Nl9wcm9vZl9ib290c3RyYXAAAAAAAR0AZmFsbGJhY2stdjYtcHJvb2YtYm9vdHN0cmFwOjE=";
        foreach (var golden in new[] { stateGolden, requestGolden })
        {
            var bytes = Convert.FromBase64String(golden);
            Assert.True(FileTransferDataFrameCodec.TryDeserialize(bytes, out var frame));
            Assert.False(frame is FileTransferFallbackBootstrapFrameV6);
            Assert.Equal(bytes, FileTransferDataFrameCodec.Serialize(frame!));
        }
    }

    [Fact]
    public void Signaling_AbsentFieldsPreserveLegacyBytes_AndPresentFieldsAreExact()
    {
        var accept = new FileTransferAcceptV1
        {
            SessionId = "session", TransferId = "transfer", AcceptedDataProtocolVersion = 4,
            FileTransferRoute = "regular_nkn_v4_fast",
        };
        var legacy = Encoding.UTF8.GetString(FileTransferPayloadCodec.Serialize(accept));
        Assert.Equal("{\"kind\":\"filetransfer\",\"type\":\"filetransfer.accept.v1\",\"sessionId\":\"session\",\"transferId\":\"transfer\",\"acceptedDataProtocolVersion\":4,\"fileTransferRoute\":\"regular_nkn_v4_fast\"}", legacy);
        var selected = accept with { FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = Issuer };
        Assert.True(FileTransferPayloadCodec.TryDeserializeAccept(FileTransferPayloadCodec.Serialize(selected), out var parsed));
        Assert.Equal(selected, parsed);
        foreach (var invalid in new[]
        {
            selected with { FallbackBootstrapCorrelationVersion = 2 },
            selected with { FallbackBootstrapCorrelationVersion = null },
            selected with { FallbackBootstrapIssuerId = null },
            selected with { FallbackBootstrapIssuerId = Issuer.ToUpperInvariant() },
            selected with { FallbackBootstrapIssuerId = " " + Issuer },
        })
        {
            Assert.False(FileTransferPayloadCodec.TryDeserializeAccept(FileTransferPayloadCodec.Serialize(invalid), out _));
        }
    }

    private static FileTransferFallbackBootstrapFrameV6 CreateFrame(bool state, bool echo)
        => state
            ? new FileTransferFallbackBootstrapStateFrameV6
            {
                SessionId = "session", TransferId = "transfer", TransportEpoch = 1,
                Epoch = 2, ContiguousCommittedChunkIndex = 3, DurableReceivedHighestChunkIndex = 4,
                CreditUntilChunkIndexExclusive = 3, BytesCommitted = 3072,
                TransferPaused = true, TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode,
                SenderRequestId = echo ? SenderId : null, ReceiverChallengeId = ReceiverId,
            }
            : new FileTransferFallbackBootstrapRequestFrameV6
            {
                SessionId = "session", TransferId = "transfer", TransportEpoch = 1,
                SenderRequestId = SenderId, ReceiverChallengeId = echo ? ReceiverId : null,
            };

    private static void WriteOptional(Stream stream, string value)
    {
        stream.WriteByte(1);
        WriteString(stream, value);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
