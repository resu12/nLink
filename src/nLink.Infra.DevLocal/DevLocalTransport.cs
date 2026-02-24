using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Chat;

namespace NLink.Infra.DevLocal;

// DEV ONLY: local machine named-pipe transport for testing two app instances without real networking.
public sealed class DevLocalTransport : ISignalingTransport
{
    private const string JoinFrameType = "join";
    private const string HelloFrameType = "hello";
    private const string ApproveFrameType = "approve";
    private const string RejectFrameType = "reject";
    private const string ChatFrameType = "chat";
    private const int ConnectTimeoutMs = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object activeConnectionGate = new();
    private SessionConnection? activeConnection;
    private bool disposed;

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

    public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;

    public void Dispose()
    {
        disposed = true;
        ClearActiveConnection()?.Dispose();
    }

    public async Task HostAsync(SessionCode code, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var cancelRegistration = ct.Register(() => ClearActiveConnection()?.Dispose());

        while (!ct.IsCancellationRequested)
        {
            if (disposed)
            {
                break;
            }

            try
            {
                await HandleSingleHostConnectionAsync(code, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                OnDisconnected();

                try
                {
                    await Task.Delay(150, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async Task JoinAsync(SessionCode code, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var cancelRegistration = ct.Register(() => ClearActiveConnection()?.Dispose());

        SessionConnection? connection = null;

        try
        {
            var client = new NamedPipeClientStream(
                ".",
                BuildPipeName(code),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(ConnectTimeoutMs, ct);

            connection = new SessionConnection(client);
            ReplaceActiveConnection(connection);

            using var helperKeyPair = ChatKeyAgreement.CreateKeyPair();
            var helloReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var readLoopTask = RunJoinReadLoopAsync(connection, helperKeyPair, helloReceived, ct);
            connection.SetReadLoop(readLoopTask);

            await connection.WriteFrameAsync(
                new TransportFrame
                {
                    Type = JoinFrameType,
                    Data = Convert.ToBase64String(helperKeyPair.PublicKey),
                },
                ct);

            await helloReceived.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            OnDisconnected();
        }
        catch (TimeoutException)
        {
            OnDisconnected();
        }
        catch (IOException)
        {
            OnDisconnected();
        }
        catch
        {
            OnDisconnected();
        }
    }

    public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();

        var connection = GetActiveConnection();
        if (connection is null || !connection.IsConnected)
        {
            throw new InvalidOperationException("No active session connection.");
        }

        return connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = ChatFrameType,
                Data = Convert.ToBase64String(payload.Span),
            },
            ct);
    }

    private async Task HandleSingleHostConnectionAsync(SessionCode code, CancellationToken ct)
    {
        var server = new NamedPipeServerStream(
            BuildPipeName(code),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(ct);

        var connection = new SessionConnection(server);
        ReplaceActiveConnection(connection);

        try
        {
            var joinFrame = await connection.ReadFrameAsync(ct);
            if (joinFrame is null || !string.Equals(joinFrame.Type, JoinFrameType, StringComparison.Ordinal))
            {
                await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
                return;
            }

            byte[] helperPublicKey;
            try
            {
                helperPublicKey = Convert.FromBase64String(joinFrame.Data ?? string.Empty);
            }
            catch (FormatException)
            {
                await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
                return;
            }

            using var hostKeyPair = ChatKeyAgreement.CreateKeyPair();
            var sharedKey = hostKeyPair.DeriveSharedKey(helperPublicKey);

            await connection.WriteFrameAsync(
                new TransportFrame
                {
                    Type = HelloFrameType,
                    Data = Convert.ToBase64String(hostKeyPair.PublicKey),
                },
                ct);

            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));

            var joinRequestArgs = new IncomingJoinRequestEventArgs(
                approveAsync: token => ApproveHostJoinAsync(connection, token),
                rejectAsync: token => RejectHostJoinAsync(connection, token));

            var handler = IncomingJoinRequest;
            if (handler is null)
            {
                await joinRequestArgs.RejectAsync(ct);
            }
            else
            {
                try
                {
                    handler(this, joinRequestArgs);
                }
                catch
                {
                    await joinRequestArgs.RejectAsync(ct);
                }
            }

            await RunHostReadLoopAsync(connection, ct);
        }
        finally
        {
            if (ReferenceEquals(GetActiveConnection(), connection))
            {
                ClearActiveConnection();
            }

            connection.Dispose();
        }
    }

    private async Task RunJoinReadLoopAsync(
        SessionConnection connection,
        ChatKeyPair helperKeyPair,
        TaskCompletionSource helloReceived,
        CancellationToken ct)
    {
        var rejected = false;
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                var frame = await connection.ReadFrameAsync(ct);
                if (frame is null)
                {
                    break;
                }

                if (string.Equals(frame.Type, HelloFrameType, StringComparison.Ordinal))
                {
                    if (!helloReceived.Task.IsCompleted)
                    {
                        var remotePublicKey = Convert.FromBase64String(frame.Data ?? string.Empty);
                        var sharedKey = helperKeyPair.DeriveSharedKey(remotePublicKey);
                        SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
                        helloReceived.TrySetResult();
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ApproveFrameType, StringComparison.Ordinal))
                {
                    Approved?.Invoke(this, EventArgs.Empty);
                    continue;
                }

                if (string.Equals(frame.Type, RejectFrameType, StringComparison.Ordinal))
                {
                    rejected = true;
                    Rejected?.Invoke(this, EventArgs.Empty);
                    connection.Dispose();
                    break;
                }

                if (string.Equals(frame.Type, ChatFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes))
                    {
                        ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(payloadBytes));
                    }

                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            if (!helloReceived.Task.IsCompleted)
            {
                helloReceived.TrySetCanceled();
            }

            if (!rejected)
            {
                OnDisconnected();
            }
            if (ReferenceEquals(GetActiveConnection(), connection))
            {
                ClearActiveConnection();
            }
        }
    }

    private async Task RunHostReadLoopAsync(SessionConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                var frame = await connection.ReadFrameAsync(ct);
                if (frame is null)
                {
                    break;
                }

                if (string.Equals(frame.Type, ChatFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes))
                    {
                        ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(payloadBytes));
                    }

                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            OnDisconnected();
        }
    }

    private async Task ApproveHostJoinAsync(SessionConnection connection, CancellationToken ct)
    {
        await connection.WriteFrameAsync(new TransportFrame { Type = ApproveFrameType }, ct);
        Approved?.Invoke(this, EventArgs.Empty);
    }

    private async Task RejectHostJoinAsync(SessionConnection connection, CancellationToken ct)
    {
        await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
        Rejected?.Invoke(this, EventArgs.Empty);
        connection.Dispose();
    }

    private void OnDisconnected()
    {
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string BuildPipeName(SessionCode code)
    {
        return "nlink-dev-mock-" + code.Digits;
    }

    private SessionConnection? GetActiveConnection()
    {
        lock (activeConnectionGate)
        {
            return activeConnection;
        }
    }

    private SessionConnection? ClearActiveConnection()
    {
        lock (activeConnectionGate)
        {
            var previous = activeConnection;
            activeConnection = null;
            return previous;
        }
    }

    private void ReplaceActiveConnection(SessionConnection next)
    {
        SessionConnection? previous;
        lock (activeConnectionGate)
        {
            previous = activeConnection;
            activeConnection = next;
        }

        previous?.Dispose();
    }

    private static bool TryGetPayloadBytes(TransportFrame frame, out byte[] payloadBytes)
    {
        payloadBytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(frame.Data))
        {
            return false;
        }

        try
        {
            payloadBytes = Convert.FromBase64String(frame.Data);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class SessionConnection : IDisposable
    {
        private readonly PipeStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private Task? readLoop;
        private int disposed;

        public SessionConnection(PipeStream pipe)
        {
            this.pipe = pipe;
            reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
            writer = new StreamWriter(pipe, Utf8NoBom, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
        }

        public bool IsConnected => pipe.IsConnected && Volatile.Read(ref disposed) == 0;

        public void SetReadLoop(Task task)
        {
            readLoop = task;
        }

        public async Task<TransportFrame?> ReadFrameAsync(CancellationToken ct)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<TransportFrame>(line, JsonOptions);
        }

        public async Task WriteFrameAsync(TransportFrame frame, CancellationToken ct)
        {
            await writeGate.WaitAsync(ct);
            try
            {
                var line = JsonSerializer.Serialize(frame, JsonOptions);
                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
            finally
            {
                writeGate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            // Closing the pipe is enough to break any pending read/write loops.
            // Avoid disposing reader/writer synchronously here because they may block
            // while another thread is already in a pipe read during test shutdown.
            try { pipe.Dispose(); } catch { }
        }
    }

    private sealed class TransportFrame
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public string? Data { get; init; }
    }
}
