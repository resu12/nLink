using System.Text;

namespace NLink.Infra.Nkn;

internal sealed class BridgeStdioWriter : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public BridgeStdioWriter(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }

    public async Task WriteJsonLineAsync(string line, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(line);

        var bytes = Utf8.GetBytes(line + "\n");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteBinaryFrameAsync(byte[] frameBytes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frameBytes);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frameBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteSendFrameAsync(string destination, ReadOnlyMemory<byte> payload, NknBridgeChannel channel, CancellationToken ct)
    {
        var frameBytes = BridgeBinaryProtocol.BuildSendFrame(destination, payload.Span, channel);
        await WriteBinaryFrameAsync(frameBytes, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!leaveOpen)
        {
            try { stream.Dispose(); } catch { }
        }

        gate.Dispose();
    }
}
