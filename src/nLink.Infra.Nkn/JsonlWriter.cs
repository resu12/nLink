using System.Text;

namespace NLink.Infra.Nkn;

internal sealed class JsonlWriter : IDisposable
{
    private readonly TextWriter writer;
    private readonly bool disposeWriter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public JsonlWriter(Stream stream, Encoding? encoding = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: leaveOpen)
        {
            AutoFlush = true
        };
        disposeWriter = true;
    }

    public JsonlWriter(TextWriter writer, bool leaveOpen = true)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        disposeWriter = !leaveOpen;
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(line);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (disposeWriter)
        {
            try { writer.Dispose(); } catch { }
        }
        gate.Dispose();
    }
}
