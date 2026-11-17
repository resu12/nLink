using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferInfraSuite", "ContentEvidencePrimitive")]
public sealed class FileTransferContentEvidencePrimitiveTests
{
    private const string Session = "session_ž";
    private const string Transfer = "transfer_猫";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Projection_IdentifiesExactCodecSegmentsWithoutRetainingPayload(int count)
    {
        var source = Frame(count) with { SessionId = " " + Session + " ", TransferId = " " + Transfer + " " };
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(source);
        var projection = Project(payload);
        Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(payload, out var decoded));
        var batch = Assert.IsType<FileTransferChunkBatchFrameV4>(decoded);
        Assert.Equal(batch.SessionId, projection.SessionId);
        Assert.Equal(batch.TransferId, projection.TransferId);
        Assert.Equal(batch.StartChunkIndex, projection.StartChunkIndex);
        Assert.Equal(count, projection.Segments.Count);
        for (var slot = 0; slot < count; slot++)
        {
            var segment = projection.Segments[slot];
            Assert.Equal(batch.StartChunkIndex + slot, segment.ChunkIndex);
            Assert.Equal(batch.DataSegments[slot].Length, segment.Length);
            Assert.True(payload.AsSpan(segment.Offset, segment.Length).SequenceEqual(batch.DataSegments[slot]));
        }
        var snapshot = projection.Segments.ToArray();
        Array.Clear(payload);
        Assert.Equal(snapshot, projection.Segments);
        Assert.DoesNotContain(typeof(FileTransferV4ChunkProjection).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(byte[]) || field.FieldType == typeof(ReadOnlyMemory<byte>));
        Assert.Throws<NotSupportedException>(() => ((IList<FileTransferV4ChunkSegment>)projection.Segments)[0] = default);
    }

    [Fact]
    public void Projection_AllocationsDoNotScaleWithPayloadBytes()
    {
        var small = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1, 1));
        var large = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1, FileTransferProtocol.MaxChunkRawBytes));
        _ = MeasureProjectionAllocations(small);
        _ = MeasureProjectionAllocations(large);
        var smallBytes = MeasureProjectionAllocations(small);
        var largeBytes = MeasureProjectionAllocations(large);
        Assert.InRange(largeBytes - smallBytes, -1024L, 1024L);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("type")]
    [InlineData("negative_start")]
    [InlineData("range_overflow")]
    [InlineData("range_limit")]
    [InlineData("zero_count")]
    [InlineData("too_many_segments")]
    [InlineData("count_mismatch")]
    [InlineData("zero_length")]
    [InlineData("negative_length")]
    [InlineData("oversized_segment")]
    [InlineData("trailing")]
    [InlineData("oversized_wire")]
    [InlineData("blank_session")]
    [InlineData("long_transfer")]
    public void Projection_RejectsMalformedOrOutOfBoundWire(string change)
    {
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1));
        var header = HeaderBytes(Session, Transfer);
        switch (change)
        {
            case "magic": payload[0] ^= 1; break;
            case "version": payload[4]++; break;
            case "type": payload[5] = 21; break;
            case "negative_start": SetInt(payload, header, -1); break;
            case "range_overflow": SetInt(payload, header, int.MaxValue); break;
            case "range_limit": SetInt(payload, header, FileTransferProtocol.MaxChunkCountV4); break;
            case "zero_count": SetInt(payload, header + 4, 0); SetInt(payload, header + 8, 0); break;
            case "too_many_segments": SetInt(payload, header + 4, 4); SetInt(payload, header + 8, 4); break;
            case "count_mismatch": SetInt(payload, header + 4, 2); break;
            case "zero_length": SetInt(payload, header + 12, 0); break;
            case "negative_length": SetInt(payload, header + 12, -1); break;
            case "oversized_segment": SetInt(payload, header + 12, FileTransferProtocol.MaxChunkRawBytes + 1); break;
            case "trailing": payload = [.. payload, 0]; break;
            case "oversized_wire": Array.Resize(ref payload, FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4 + 1); break;
            case "blank_session": payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1) with { SessionId = " " }); break;
            case "long_transfer": payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1) with { TransferId = new string('t', 129) }); break;
        }
        Assert.False(FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(payload, out var projection));
        Assert.Null(projection);
    }

    [Fact]
    public void Projection_RejectsEveryTruncatedPrefixAndV6OrControlFrames()
    {
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(3));
        for (var length = 0; length < payload.Length; length++)
        {
            Assert.False(FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(payload.AsSpan(0, length), out var projection));
            Assert.Null(projection);
        }
        var v6 = FileTransferDataFrameCodec.Serialize(new FileTransferChunkBatchFrameV6
        {
            SessionId = Session, TransferId = Transfer, StartChunkIndex = 12,
            ChunkCount = 1, DataSegments = [new byte[] { 17 }],
        });
        var control = FileTransferDataFrameCodec.SerializeLegacyV4(new FileTransferCancelFrameV4
        {
            SessionId = Session, TransferId = Transfer, Reason = "cancel",
        });
        Assert.False(FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(v6, out _));
        Assert.False(FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(control, out _));
    }

    [Fact]
    public void Projection_AcceptsMaximumSerializedBatchAndLastValidRange()
    {
        var rawBytes = FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4 - HeaderBytes(Session, Transfer) - 12 - 3 * 4;
        var frame = Frame(3) with
        {
            StartChunkIndex = FileTransferProtocol.MaxChunkCountV4 - 3,
            DataSegments = [new byte[21 * 1024], new byte[21 * 1024], new byte[rawBytes - 42 * 1024]],
        };
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(frame);
        Assert.Equal(FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4, payload.Length);
        var projection = Project(payload);
        Assert.Equal(FileTransferProtocol.MaxChunkCountV4 - 1, projection.Segments[2].ChunkIndex);
        Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(payload, out _));
    }

    [Fact]
    public void Commitment_MatchesIndependentLengthPrefixedHmacAndSurvivesRebatching()
    {
        using var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        var held = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1));
        var batch = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(3));
        var one = Capture(scope, held);
        var three = Capture(scope, batch);
        Assert.Equal(one.Scope.ScopeId, three.Scope.ScopeId);
        Assert.Equal(one.Segments[0], three.Segments[0]);
        Assert.Matches("^ftcs1-[0-9a-f]{32}$", one.Scope.ScopeId);
        Assert.All(three.Segments, segment => Assert.Matches("^ftcc1-[0-9a-f]{64}$", segment.Commitment));

        var key = GetOwnedSecret(scope);
        using var input = new MemoryStream();
        input.Write(Encoding.ASCII.GetBytes("nLink/filetransfer/content/v4-chunk/v1\0"));
        foreach (var id in new[] { Session, Transfer })
        {
            var bytes = Encoding.UTF8.GetBytes(id);
            input.Write(BitConverter.GetBytes((uint)bytes.Length));
            input.Write(bytes);
        }
        input.Write(BitConverter.GetBytes(12U));
        input.Write(BitConverter.GetBytes(32U));
        input.Write(Frame(1).DataSegments[0]);
        var expected = "ftcc1-" + Convert.ToHexString(HMACSHA256.HashData(key, input.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, one.Segments[0].Commitment);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("index")]
    [InlineData("length")]
    public void Commitment_ChangesForDifferentBytesIndexOrLength(string change)
    {
        using var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        var frame = Frame(1);
        var before = Capture(scope, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
        if (change == "bytes") { frame.DataSegments[0][0] ^= 1; }
        if (change == "index") { frame = frame with { StartChunkIndex = 13 }; }
        if (change == "length") { frame = frame with { DataSegments = [[.. frame.DataSegments[0], 0]] }; }
        var after = Capture(scope, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
        Assert.NotEqual(before.Segments[0].Commitment, after.Segments[0].Commitment);
    }

    [Fact]
    public void Commitment_RejectsWrongScopeAndUsesFreshSecretForAnotherScope()
    {
        using var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        using var replacement = new FileTransferContentEvidenceScope(Session, Transfer);
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(1));
        var first = Capture(scope, payload);
        var second = Capture(replacement, payload);
        Assert.NotEqual(first.Scope.ScopeId, second.Scope.ScopeId);
        Assert.NotEqual(first.Segments[0].Commitment, second.Segments[0].Commitment);
        foreach (var wrong in new[] { Frame(1) with { SessionId = Session + "_other" }, Frame(1) with { TransferId = Transfer + "_other" } })
        {
            Assert.False(scope.TryCapture(FileTransferDataFrameCodec.SerializeLegacyV4(wrong), out var evidence));
            Assert.Null(evidence);
        }
    }

    [Fact]
    public void Commitment_RejectsMalformedWireWithoutPublishingPartialEvidence()
    {
        using var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(3));
        foreach (var invalid in new[] { payload[..^1], (byte[])[.. payload, 0] })
        {
            Assert.False(scope.TryCapture(invalid, out var evidence));
            Assert.Null(evidence);
        }
        Assert.False(scope.TryCapture(ReadOnlySpan<byte>.Empty, out _));
        _ = Capture(scope, payload);
    }

    [Fact]
    public void Commitment_FreezesSerializedBytesAndRetainsNoSecretOrPayload()
    {
        using var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        var frame = Frame(3);
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(frame);
        var before = Capture(scope, payload);
        frame.DataSegments[0][0] ^= 1;
        Assert.Equal(before.Segments, Capture(scope, payload).Segments);
        Assert.NotEqual(before.Segments[0].Commitment,
            Capture(scope, FileTransferDataFrameCodec.SerializeLegacyV4(frame)).Segments[0].Commitment);
        var frozen = before.Segments.ToArray();
        Array.Clear(payload);
        Assert.Equal(frozen, before.Segments);
        Assert.DoesNotContain(typeof(FileTransferContentEvidence).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(byte[]) || field.FieldType == typeof(FileTransferContentEvidenceScope));
        Assert.DoesNotContain(typeof(FileTransferContentScopeMarker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(byte[]));
        Assert.Throws<NotSupportedException>(() => ((IList<FileTransferChunkContentCommitment>)before.Segments)[0] = default);
    }

    [Fact]
    public void Disposal_ZeroesOwnedSecretAndRetiresCapturedSuffixWithoutRecreation()
    {
        var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(3));
        var evidence = Capture(scope, payload);
        var key = GetOwnedSecret(scope);
        Assert.Equal(32, key.Length);
        scope.Dispose();
        scope.Dispose();
        Assert.All(key, value => Assert.Equal((byte)0, value));
        Assert.True(evidence.Scope.Retired);
        Assert.False(scope.TryCapture(payload, out var after));
        Assert.Null(after);
        var suffix = evidence.FormatLogSuffix();
        Assert.Contains("content_evidence_state=retired; content_segment_count=0", suffix, StringComparison.Ordinal);
        Assert.DoesNotContain("_commitment=", suffix, StringComparison.Ordinal);
    }

    [Fact]
    public void Suffix_IsAsciiInvariantAndWithinBoundWithoutExportingIdsOrKey()
    {
        using var scope = new FileTransferContentEvidenceScope(Session, Transfer);
        var payload = FileTransferDataFrameCodec.SerializeLegacyV4(Frame(3) with
        {
            StartChunkIndex = FileTransferProtocol.MaxChunkCountV4 - 3,
            DataSegments = [new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024]],
        });
        var evidence = Capture(scope, payload);
        var previous = CultureInfo.CurrentCulture;
        var expected = evidence.FormatLogSuffix();
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(expected, evidence.FormatLogSuffix());
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.All(expected, value => Assert.InRange((int)value, 0, 127));
        Assert.InRange(Encoding.UTF8.GetByteCount(expected), 1, FileTransferContentEvidence.MaximumSuffixBytes);
        Assert.DoesNotContain(Session, expected, StringComparison.Ordinal);
        Assert.DoesNotContain(Transfer, expected, StringComparison.Ordinal);
        Assert.False(expected.Contains(Convert.ToHexString(GetOwnedSecret(scope)), StringComparison.OrdinalIgnoreCase));
    }

    private static FileTransferChunkBatchFrameV4 Frame(int count, int bytes = 32)
        => new()
        {
            SessionId = Session, TransferId = Transfer, StartChunkIndex = 12, ChunkCount = count,
            DataSegments = Enumerable.Range(1, count).Select(slot => Enumerable.Repeat((byte)(17 * slot), bytes).ToArray()).ToArray(),
        };

    private static FileTransferV4ChunkProjection Project(byte[] payload)
    {
        Assert.True(FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(payload, out var projection));
        return Assert.IsType<FileTransferV4ChunkProjection>(projection);
    }

    private static FileTransferContentEvidence Capture(FileTransferContentEvidenceScope scope, byte[] payload)
    {
        Assert.True(scope.TryCapture(payload, out var evidence));
        return Assert.IsType<FileTransferContentEvidence>(evidence);
    }

    private static byte[] GetOwnedSecret(FileTransferContentEvidenceScope scope)
        => Assert.IsType<byte[]>(typeof(FileTransferContentEvidenceScope)
            .GetField("secret", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope));

    private static int HeaderBytes(string session, string transfer)
        => 6 + 2 + Encoding.UTF8.GetByteCount(session) + 2 + Encoding.UTF8.GetByteCount(transfer);

    private static void SetInt(byte[] payload, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), value);

    private static long MeasureProjectionAllocations(byte[] payload)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            if (!FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(payload, out _)) { throw new InvalidOperationException(); }
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
