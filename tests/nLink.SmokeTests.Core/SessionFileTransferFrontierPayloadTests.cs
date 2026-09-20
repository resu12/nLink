using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("v6", "positive")]
    [InlineData("v4", "positive")]
    [InlineData("v6", "successors")]
    [InlineData("v4", "successors")]
    [InlineData("v6", "before_recheck")]
    [InlineData("v4", "before_recheck")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadPreparation")]
    public async Task FrontierPayload_ClosedProducerPreparesStableProjectedBytes(string path, string variant)
    {
        object? beforeStreamClose = null;
        var fixture = await RunFrontierBoundaryAsync(path, variant, true, afterCaptureClosed: observer =>
        {
            beforeStreamClose = PrepareBoundaryPayload(observer, out var earlyReason);
            Assert.True(beforeStreamClose is not null, earlyReason);
        });
        var observer = fixture.Observer;
        var clockSequence = BoundaryField(observer, "sequence");
        var timing = GetPrivatePropertyValue(observer, "Timing");
        var entries = GetPrivateProperty<int>(observer, "EntryCount");
        var payload = PrepareBoundaryPayload(observer, out var reason);
        Assert.True(payload is not null, reason);
        Assert.Same(beforeStreamClose, payload);
        Assert.Same(payload, PrepareBoundaryPayload(observer, out reason));
        Assert.Equal("", reason);
        Assert.True(OwnsBoundaryPayload(observer, payload));
        var bytes = CopyBoundaryPayload(payload!);
        Assert.InRange(bytes.Length, 1, 262144);
        Assert.Equal(bytes.Length, GetPrivateProperty<int>(payload!, "ByteCount"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), GetPrivateProperty<string>(payload!, "Sha256"));
        Assert.True(MatchesBoundaryPayload(payload!, bytes));
        var json = new UTF8Encoding(false, true).GetString(bytes);
        Assert.False(json.StartsWith('\uFEFF'));
        using var document = JsonDocument.Parse(json);
        var p = document.RootElement;
        Assert.Equal("nlink-frontier-boundary-producer-v1", p.GetProperty("Format").GetString());
        Assert.Equal("sha256-utf8-frontier-boundary-producer-v1", p.GetProperty("HashDomain").GetString());
        Assert.Equal(FileTransferCoordinator.FrontierPublicationBoundaryTiming.Profile, p.GetProperty("ClockDomain").GetString());
        foreach (var flag in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
        { Assert.False(p.GetProperty(flag).GetBoolean()); Assert.False(GetPrivateProperty<bool>(payload!, flag)); }
        Assert.False(p.TryGetProperty("Records", out _));
        Assert.False(p.TryGetProperty("InputSha256", out _));
        Assert.False(p.TryGetProperty("CaptureId", out _));
        Assert.DoesNotContain("RawLine", json);
        Assert.DoesNotContain("CompletionPayload", json);
        Assert.DoesNotContain("RuntimeOwner\":{", json);
        Assert.DoesNotContain("RoutePublication\":{", json);

        var root = BoundaryField(observer, "root")!;
        foreach (var field in new[] { "SessionId", "TransferId", "LegId", "DataSessionIdentity", "CheckpointRequestId" })
        {
            var raw = GetPrivateProperty<string>(root, field);
            if (!string.IsNullOrWhiteSpace(raw) && raw != "(none)") { Assert.DoesNotContain(raw, json); }
        }
        Assert.Equal(GetPrivateProperty<long>(root, "WakeGeneration"), p.GetProperty("RootGeneration").GetInt64());
        Assert.Equal(GetPrivateProperty<DateTimeOffset>(root, "DueUtc").UtcTicks, p.GetProperty("OriginalDeadlineUtcTicks").GetInt64());
        var capture = FreezeBoundary(observer, fixture.Lines, out reason);
        Assert.True(capture is not null, reason);
        using var original = JsonDocument.Parse(BoundaryExport(capture!, "Full"));
        var originalProducer = original.RootElement.GetProperty("Producer");
        foreach (var field in new[] { "ObservationId", "ClockInstanceId", "ProcessId", "ProcessCreationFileTime", "Frequency",
            "RootGeneration", "RecheckedGeneration", "FirstWriteGeneration", "OriginalDeadlineUtcTicks", "PublicationDeadlineUtcTicks", "Outcome" })
        { Assert.Equal(originalProducer.GetProperty(field).GetRawText(), p.GetProperty(field).GetRawText()); }
        Assert.Equal(originalProducer.GetProperty("Lifecycle").EnumerateArray().Select(e => e.GetProperty("Clock").GetRawText()),
            p.GetProperty("Lifecycle").EnumerateArray().Select(e => e.GetProperty("Clock").GetRawText()));
        Assert.Equal(originalProducer.GetProperty("CompletionSha256").GetString(), p.GetProperty("Completion").GetProperty("SnapshotSha256").GetString());
        Assert.Equal(entries, GetPrivateProperty<int>(observer, "EntryCount"));
        Assert.Equal(clockSequence, BoundaryField(observer, "sequence"));
        Assert.Same(timing, GetPrivatePropertyValue(observer, "Timing"));

        bytes[0] ^= 1;
        Assert.False(MatchesBoundaryPayload(payload!, bytes));
        Assert.False(MatchesBoundaryPayload(payload!, null));
        Assert.True(MatchesBoundaryPayload(payload!, CopyBoundaryPayload(payload!)));
        var forged = Activator.CreateInstance(payload!.GetType(), [CopyBoundaryPayload(payload)])!;
        Assert.False(OwnsBoundaryPayload(observer, forged));
        var foreign = Activator.CreateInstance(observer.GetType())!;
        Assert.False(OwnsBoundaryPayload(foreign, payload));
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        { File.WriteAllBytes(Path.Combine(directory, $"{path}-{variant}-producer.json"), CopyBoundaryPayload(payload)); }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadPreparation")]
    public void FrontierPayload_UnstartedProducerCannotPrepare()
    {
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierBoundaryObservation", BindingFlags.NonPublic)!;
        var observer = Activator.CreateInstance(type)!;
        Assert.Null(PrepareBoundaryPayload(observer, out var reason));
        Assert.Equal("producer_not_closed_positive", reason);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("rejected")]
    [InlineData("write_before_recheck")]
    [InlineData("capacity")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadPreparation")]
    public async Task FrontierPayload_NonPositiveProducerCannotPrepare(string variant)
    {
        var fixture = await RunFrontierBoundaryAsync("v6", variant, true);
        Assert.Null(PrepareBoundaryPayload(fixture.Observer, out var reason));
        Assert.Equal("producer_not_closed_positive", reason);
        Assert.False(OwnsBoundaryPayload(fixture.Observer, null));
    }

    [Theory]
    [InlineData("ascii_boundary", true)]
    [InlineData("ascii_over", false)]
    [InlineData("multibyte_boundary", true)]
    [InlineData("multibyte_over", false)]
    [InlineData("bmp_boundary", true)]
    [InlineData("high_surrogate", false)]
    [InlineData("low_surrogate", false)]
    [InlineData("empty", false)]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadPreparation")]
    public void FrontierPayload_EncodingUsesStrictUtf8ByteBudget(string variant, bool accepted)
    {
        var text = variant switch
        {
            "ascii_boundary" => new string('a', 262144),
            "ascii_over" => new string('a', 262145),
            "multibyte_boundary" => string.Concat(Enumerable.Repeat("\U0001f4a1", 65536)),
            "multibyte_over" => string.Concat(Enumerable.Repeat("\U0001f4a1", 65537)),
            "bmp_boundary" => new string('\u20ac', 87381) + "a",
            "high_surrogate" => "\ud800",
            "low_surrogate" => "\udc00",
            _ => "",
        };
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierBoundaryObservation", BindingFlags.NonPublic)!;
        var bytes = (byte[]?)type.GetMethod("EncodeBoundaryPayload", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text]);
        Assert.Equal(accepted, bytes is not null);
        if (accepted) { Assert.Equal(262144, bytes!.Length); Assert.Equal(text, new UTF8Encoding(false, true).GetString(bytes)); }
    }

    private static object? PrepareBoundaryPayload(object observer, out string reason)
    {
        object?[] arguments = [null];
        var result = observer.GetType().GetMethod("TryPreparePayload")!.Invoke(observer, arguments);
        reason = (string)arguments[0]!;
        return result;
    }
    private static byte[] CopyBoundaryPayload(object payload) => (byte[])payload.GetType().GetMethod("CopyBytes")!.Invoke(payload, null)!;
    private static bool MatchesBoundaryPayload(object payload, byte[]? bytes) => (bool)payload.GetType().GetMethod("Matches")!.Invoke(payload, [bytes])!;
    private static bool OwnsBoundaryPayload(object observer, object? payload) => (bool)observer.GetType().GetMethod("OwnsPreparedPayload")!.Invoke(observer, [payload])!;
}
