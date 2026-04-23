using System.Text;
using System.Text.Json;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed class JsonlSmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task JsonlReader_ReconstructsLines_FromRandomChunkSizes()
    {
        var payloadLines = new[]
        {
            "{\"event\":\"a\"}",
            "",
            "{\"event\":\"b\",\"n\":1}",
            "   ",
            "{\"event\":\"c\"}"
        };

        var expected = new[]
        {
            "{\"event\":\"a\"}",
            "{\"event\":\"b\",\"n\":1}",
            "{\"event\":\"c\"}"
        };

        var jsonl = string.Join("\n", payloadLines) + "\n";
        var bytes = Encoding.UTF8.GetBytes(jsonl);
        var chunks = BuildPseudoRandomChunks(bytes.Length);
        using var stream = new ChunkedReadStream(bytes, chunks);
        var reader = new JsonlReader();
        var actual = new List<string>();

        await reader.ReadLinesAsync(
            stream,
            (line, _) =>
            {
                actual.Add(line);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task JsonlWriter_AlwaysAppendsNewline_AndFlushes()
    {
        await using var stream = new MemoryStream();
        using var writer = new JsonlWriter(stream, leaveOpen: true);

        await writer.WriteLineAsync("{\"x\":1}", CancellationToken.None);
        await writer.WriteLineAsync("{\"y\":2}", CancellationToken.None);

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var content = await reader.ReadToEndAsync();

        Assert.Equal("{\"x\":1}\n{\"y\":2}\n", content);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BridgeProtocolClient_SendCommandAndWaitAckAsync_ReportsSerializedJsonlBytes()
    {
        await using var output = new MemoryStream();
        using var writer = new BridgeStdioWriter(output, leaveOpen: true);
        int? reportedBytes = null;

        var client = new BridgeProtocolClient(
            getWriter: () => writer,
            log: static _ => { },
            onReady: static _ => { },
            onRpcProgress: static (_, _) => { },
            onMessage: static _ => { },
            onDisconnected: static _ => { },
            onHelloOk: static _ => { },
            onPong: static _ => { },
            onScreenShareQueueState: static _ => { },
            onBridgeEventLoopSummary: static _ => { },
            onBridgeMediaSendSummary: static _ => { },
            onBridgeTransportHealthSummary: static _ => { },
            onUnmatchedBridgeError: static _ => { });

        var sendTask = client.SendCommandAndWaitAckAsync(
            cmd: "send",
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["destination"] = "peer.test",
                ["payloadBase64"] = "AQID",
            },
            timeout: TimeSpan.FromSeconds(2),
            ct: CancellationToken.None,
            onSerialized: bytes => reportedBytes = bytes);

        await WaitUntilAsync(() => Encoding.UTF8.GetString(output.ToArray()).Contains('\n'), TimeSpan.FromSeconds(1));

        var line = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\r', '\n');
        using var doc = JsonDocument.Parse(line);
        var id = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        client.HandleStdoutJsonLine($"{{\"event\":\"ok\",\"id\":\"{id}\"}}");
        await sendTask;

        Assert.Equal(NknBridgePayloadAccounting.MeasureSerializedJsonlBytes(line), reportedBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BridgeStdioWriter_WriteSendFrameAsync_WritesBinarySendFrame()
    {
        await using var stream = new MemoryStream();
        using var writer = new BridgeStdioWriter(stream, leaveOpen: true);

        var payload = new byte[] { 1, 2, 3, 4 };
        await writer.WriteSendFrameAsync("peer.test", payload, NknBridgeChannel.Bulk, CancellationToken.None);

        var bytes = stream.ToArray();
        var header = BridgeBinaryProtocol.ParseHeader(bytes.AsSpan(0, BridgeBinaryProtocol.HeaderSize));
        var frame = BridgeBinaryProtocol.DecodeFrame(header, bytes.AsSpan(BridgeBinaryProtocol.HeaderSize));

        Assert.Equal(BridgeBinaryFrameKind.Send, frame.Kind);
        Assert.Equal(NknBridgeChannel.Bulk, frame.Channel);
        Assert.Equal("peer.test", frame.PrimaryText);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void BridgeProtocolClient_RoutesBridgeMediaSendSummary()
    {
        var receivedQueueDepth = -1;

        var client = new BridgeProtocolClient(
            getWriter: static () => throw new NotSupportedException(),
            log: static _ => { },
            onReady: static _ => { },
            onRpcProgress: static (_, _) => { },
            onMessage: static _ => { },
            onDisconnected: static _ => { },
            onHelloOk: static _ => { },
            onPong: static _ => { },
            onScreenShareQueueState: static _ => { },
            onBridgeEventLoopSummary: static _ => { },
            onBridgeMediaSendSummary: root => receivedQueueDepth = root.GetProperty("queue_depth").GetInt32(),
            onBridgeTransportHealthSummary: static _ => { },
            onUnmatchedBridgeError: static _ => { });

        client.HandleStdoutJsonLine("{\"event\":\"bridge_media_send_summary\",\"queue_depth\":3}");

        Assert.Equal(3, receivedQueueDepth);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void BridgeProtocolClient_RoutesBridgeTransportHealthSummary()
    {
        var receivedFramesSentSinceLast = -1;

        var client = new BridgeProtocolClient(
            getWriter: static () => throw new NotSupportedException(),
            log: static _ => { },
            onReady: static _ => { },
            onRpcProgress: static (_, _) => { },
            onMessage: static _ => { },
            onDisconnected: static _ => { },
            onHelloOk: static _ => { },
            onPong: static _ => { },
            onScreenShareQueueState: static _ => { },
            onBridgeEventLoopSummary: static _ => { },
            onBridgeMediaSendSummary: static _ => { },
            onBridgeTransportHealthSummary: root => receivedFramesSentSinceLast = root.GetProperty("frames_sent_since_last").GetInt32(),
            onUnmatchedBridgeError: static _ => { });

        client.HandleStdoutJsonLine("{\"event\":\"bridge_transport_health_summary\",\"frames_sent_since_last\":5}");

        Assert.Equal(5, receivedFramesSentSinceLast);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BridgeMixedStreamReader_ReadsJsonLines_AndBinaryFrames_FromSameStream()
    {
        var json = Encoding.UTF8.GetBytes("{\"event\":\"ready\",\"protocol\":2}\n");
        var binary = BridgeBinaryProtocol.BuildMessageFrame(
            source: "peer.test",
            payload: new byte[] { 9, 8, 7 },
            channel: NknBridgeChannel.Media,
            isTopic: false,
            topic: null);
        var combined = new byte[json.Length + binary.Length];
        json.CopyTo(combined, 0);
        binary.CopyTo(combined, json.Length);

        using var stream = new ChunkedReadStream(combined, BuildPseudoRandomChunks(combined.Length));
        var reader = new BridgeMixedStreamReader();
        string? jsonLine = null;
        BridgeBinaryFrame? binaryFrame = null;

        await reader.ReadAsync(
            stream,
            (line, _) =>
            {
                jsonLine = line;
                return Task.CompletedTask;
            },
            (frame, _) =>
            {
                binaryFrame = frame;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("{\"event\":\"ready\",\"protocol\":2}", jsonLine);
        Assert.NotNull(binaryFrame);
        Assert.Equal(BridgeBinaryFrameKind.Message, binaryFrame!.Kind);
        Assert.Equal(NknBridgeChannel.Media, binaryFrame.Channel);
        Assert.Equal("peer.test", binaryFrame.PrimaryText);
        Assert.Equal(new byte[] { 9, 8, 7 }, binaryFrame.Payload);
        Assert.True(binaryFrame.BinaryFrameDecodedUtcMs > 0);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.True(predicate(), $"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static int[] BuildPseudoRandomChunks(int length)
    {
        var sizes = new List<int>();
        var remaining = length;
        var seed = 17;
        while (remaining > 0)
        {
            seed = unchecked(seed * 1103515245 + 12345);
            var size = Math.Abs(seed % 7) + 1; // 1..7
            if (size > remaining)
            {
                size = remaining;
            }

            sizes.Add(size);
            remaining -= size;
        }

        return sizes.ToArray();
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] data;
        private readonly int[] chunks;
        private int offset;
        private int chunkIndex;

        public ChunkedReadStream(byte[] data, int[] chunks)
        {
            this.data = data;
            this.chunks = chunks;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset >= data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var size = chunkIndex < chunks.Length ? chunks[chunkIndex++] : destination.Length;
            if (size > destination.Length)
            {
                size = destination.Length;
            }

            if (size > data.Length - offset)
            {
                size = data.Length - offset;
            }

            data.AsSpan(offset, size).CopyTo(destination.Span);
            offset += size;
            return ValueTask.FromResult(size);
        }
    }
}

