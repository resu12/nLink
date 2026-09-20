using System.Text;
using Manifest = NLink.Tools.LocalFrontierFileManifest;

namespace NLink.SmokeTests;

public sealed class FrontierFileManifestTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public void Manifest_ExactVersionedRoundTripAndEveryTruncatedPrefix(int count)
    {
        var manifest = Create(count);
        var bytes = manifest.Encode();
        Assert.NotNull(bytes);
        Assert.Equal(216 + Encoding.UTF8.GetByteCount(manifest.Writer) + 316 * count, bytes.Length);
        Assert.Equal(count == 0 ? 2 : 1, BitConverter.ToInt32(bytes, 8));
        var decoded = Manifest.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(bytes, decoded.Encode());
        for (var size = 0; size < bytes.Length; size++) Assert.Null(Manifest.Decode(bytes[..size]));
        Assert.Null(Manifest.Decode([.. bytes, 0]));
        // The returned decode owns its arrays, not references into caller bytes.
        bytes[^1] ^= 1;
        Assert.NotEqual(bytes, decoded.Encode());
    }

    [Theory]
    [InlineData(0, 0)] // magic
    [InlineData(4, 2)] // incompatible version
    [InlineData(8, 2)] // positive catalog mislabeled writer-only
    [InlineData(12, -1)]
    [InlineData(12, 17)]
    [InlineData(12, int.MaxValue)]
    [InlineData(48, 0)] // PID
    [InlineData(52, 1)] // reserved
    [InlineData(64, 0)] // UTF-8 byte length
    [InlineData(64, 129)]
    [InlineData(64, int.MaxValue)]
    public void Manifest_RejectsHeaderAndUntrustedAllocationLengths(int offset, int value)
    {
        var bytes = Create(1).Encode();
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
        Assert.Null(Manifest.Decode(bytes));
    }

    [Theory]
    [InlineData("empty-gui")]
    [InlineData("empty-app")]
    [InlineData("creation")]
    [InlineData("writer")]
    [InlineData("utf8")]
    [InlineData("source-handle")]
    [InlineData("source-empty")]
    [InlineData("source-overflow")]
    [InlineData("payload-overflow")]
    [InlineData("duplicate-observation")]
    [InlineData("duplicate-file")]
    [InlineData("duplicate-handle")]
    [InlineData("source-alias")]
    [InlineData("range-overflow")]
    [InlineData("range-overlap")]
    [InlineData("sequence-order")]
    [InlineData("generation-order")]
    [InlineData("hash-length")]
    [InlineData("too-many")]
    public void Manifest_RejectsInvalidScopeBoundsAndReceiptShape(string fault)
    {
        var m = Create(2);
        var gui = m.GuiRequest; var app = m.AppRequest; var creation = m.AppCreation; var writer = m.Writer;
        var source = m.Source; var drain = m.Drain; var payloads = m.Payloads;
        var p = payloads[0]; var file = p.File; var completion = p.Completion; var marker = p.Marker; var hash = p.Hash;
        switch (fault)
        {
            case "empty-gui": gui = Guid.Empty; break;
            case "empty-app": app = Guid.Empty; break;
            case "creation": creation = 0; break;
            case "writer": writer = "bad\nwriter"; break;
            case "utf8": writer = "\ud800"; break;
            case "source-handle": source = File(0, 1, 100); break;
            case "source-empty": source = File(1, 1, 0); break;
            case "source-overflow": source = File(1, 1, Manifest.MaximumSourceBytes + 1); break;
            case "payload-overflow": file = File(2, 2, Manifest.MaximumPayloadBytes + 1); break;
            case "duplicate-observation": payloads[1] = new(p.Observation, payloads[1].File, hash, payloads[1].Completion, payloads[1].Marker); break;
            case "duplicate-file": file = File(2, payloads[1].File.IndexLow, 1); break;
            case "duplicate-handle": file = File(payloads[1].File.Handle, 2, 1); break;
            case "source-alias": file = File(2, 1, 1); break;
            case "range-overflow": completion = Range(1, long.MaxValue); break;
            case "range-overlap": marker = Range(2, 0); break;
            case "sequence-order": marker = new(2, 1, 1, 1, 1, 1, 1, hash, hash); break;
            case "generation-order": marker = new(1, 2, 2, 1, 1, 1, 1, hash, hash); break;
            case "hash-length": hash = new byte[31]; break;
            case "too-many": payloads = Enumerable.Repeat(p, 17).ToArray(); break;
        }
        payloads[0] = new(p.Observation, file, hash, completion, marker);
        Assert.Null(new Manifest(gui, app, m.AppId, creation, writer, source, drain, payloads).Encode());
    }

    [Fact]
    public void Manifest_ExactMaximumsAndStrictUtf8Decode()
    {
        var m = Create(16, new string('w', 128));
        var payloads = m.Payloads.Select(p => new Manifest.Payload(p.Observation,
            File(p.File.Handle, p.File.IndexLow, 262144), p.Hash, p.Completion, p.Marker)).ToArray();
        var maximum = new Manifest(m.GuiRequest, m.AppRequest, m.AppId, m.AppCreation, m.Writer,
            File(1, 1, Manifest.MaximumSourceBytes), m.Drain, payloads);
        Assert.NotNull(Manifest.Decode(maximum.Encode()));
        Assert.Null(new Manifest(m.GuiRequest, m.AppRequest, m.AppId, m.AppCreation, new string('w', 129), m.Source, m.Drain, m.Payloads).Encode());
        var bytes = Create(0).Encode(); bytes[68] = 0xff;
        Assert.Null(Manifest.Decode(bytes));
        Assert.Null(Manifest.Decode(new byte[Manifest.MaximumBytes + 1]));
    }

    internal static Manifest Create(int count, string writer = "writer")
    {
        var payloads = Enumerable.Range(0, count).Select(i => new Manifest.Payload(Guid.NewGuid(),
            File(i + 2, (uint)i + 2, 1), new byte[32], Range(2 * i + 1, 2 * i), Range(2 * i + 2, 2 * i + 1))).ToArray();
        return new(Guid.NewGuid(), Guid.NewGuid(), 123, 456, writer, File(1, 1, 100), Range(2 * count + 1, 2 * count), payloads);
    }
    private static Manifest.FileEntry File(long handle, uint index, long length) => new(handle, 1, 0, index, length);
    internal static Manifest.Range Range(long generation, long offset)
        => new(generation, generation, generation, 1, 1, offset, 1, new byte[32], new byte[32]);
}
