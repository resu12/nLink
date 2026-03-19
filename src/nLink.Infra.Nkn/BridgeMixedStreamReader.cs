using System.Text;

namespace NLink.Infra.Nkn;

internal sealed class BridgeMixedStreamReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task ReadAsync(
        Stream stream,
        Func<string, CancellationToken, Task> onJsonLineAsync,
        Func<BridgeBinaryFrame, CancellationToken, Task> onBinaryFrameAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onJsonLineAsync);
        ArgumentNullException.ThrowIfNull(onBinaryFrameAsync);

        while (true)
        {
            var firstByte = await ReadSingleByteAsync(stream, ct).ConfigureAwait(false);
            if (firstByte < 0)
            {
                return;
            }

            if (firstByte is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
            {
                continue;
            }

            if (firstByte == BridgeBinaryProtocol.FrameMagic)
            {
                var headerBuffer = new byte[BridgeBinaryProtocol.HeaderSize];
                headerBuffer[0] = (byte)firstByte;
                await ReadExactlyAsync(stream, headerBuffer.AsMemory(1), ct).ConfigureAwait(false);
                var header = BridgeBinaryProtocol.ParseHeader(headerBuffer);
                var bodyBuffer = new byte[header.BodyLength];
                if (bodyBuffer.Length > 0)
                {
                    await ReadExactlyAsync(stream, bodyBuffer, ct).ConfigureAwait(false);
                }

                var frame = BridgeBinaryProtocol.DecodeFrame(header, bodyBuffer);
                await onBinaryFrameAsync(frame, ct).ConfigureAwait(false);
                continue;
            }

            using var lineBuffer = new MemoryStream(capacity: 256);
            lineBuffer.WriteByte((byte)firstByte);
            while (true)
            {
                var nextByte = await ReadSingleByteAsync(stream, ct).ConfigureAwait(false);
                if (nextByte < 0 || nextByte == '\n')
                {
                    break;
                }

                if (nextByte != '\r')
                {
                    lineBuffer.WriteByte((byte)nextByte);
                }
            }

            if (lineBuffer.Length == 0)
            {
                continue;
            }

            var line = Utf8.GetString(lineBuffer.GetBuffer(), 0, checked((int)lineBuffer.Length));
            if (!string.IsNullOrWhiteSpace(line))
            {
                await onJsonLineAsync(line, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> ReadSingleByteAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading bridge mixed stream.");
            }

            offset += read;
        }
    }
}
