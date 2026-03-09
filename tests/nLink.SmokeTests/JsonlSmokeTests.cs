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
        var output = new StringWriter();
        using var writer = new JsonlWriter(output);
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

        await WaitUntilAsync(() => output.ToString().Contains('\n'), TimeSpan.FromSeconds(1));

        var line = output.ToString().TrimEnd('\r', '\n');
        using var doc = JsonDocument.Parse(line);
        var id = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        client.HandleStdoutJsonLine($"{{\"event\":\"ok\",\"id\":\"{id}\"}}");
        await sendTask;

        Assert.Equal(NknBridgePayloadAccounting.MeasureSerializedJsonlBytes(line), reportedBytes);
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

