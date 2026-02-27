using System.Buffers;
using System.Text;

namespace NLink.Infra.Nkn;

internal sealed class JsonlReader
{
    private readonly Encoding encoding;
    private readonly int bufferSize;

    public JsonlReader(Encoding? encoding = null, int bufferSize = 4096)
    {
        this.encoding = encoding ?? Encoding.UTF8;
        this.bufferSize = bufferSize <= 0 ? 4096 : bufferSize;
    }

    public async Task ReadLinesAsync(Stream stream, Func<string, CancellationToken, Task> onLineAsync, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLineAsync);

        var decoder = encoding.GetDecoder();
        var byteBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var charBuffer = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(bufferSize));
        var lineBuilder = new StringBuilder(capacity: 256);

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(byteBuffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var charsDecoded = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0, flush: false);
                await ConsumeCharsAsync(charBuffer, charsDecoded, lineBuilder, onLineAsync, ct).ConfigureAwait(false);
            }

            var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
            if (remaining > 0)
            {
                await ConsumeCharsAsync(charBuffer, remaining, lineBuilder, onLineAsync, ct).ConfigureAwait(false);
            }

            if (lineBuilder.Length > 0)
            {
                var last = lineBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(last))
                {
                    await onLineAsync(last, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private static async Task ConsumeCharsAsync(
        char[] chars,
        int count,
        StringBuilder lineBuilder,
        Func<string, CancellationToken, Task> onLineAsync,
        CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var ch = chars[i];
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                if (lineBuilder.Length == 0)
                {
                    continue;
                }

                var line = lineBuilder.ToString();
                lineBuilder.Clear();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    await onLineAsync(line, ct).ConfigureAwait(false);
                }

                continue;
            }

            lineBuilder.Append(ch);
        }
    }
}

