using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class RealNknClientAdapter : INknClient
{
    private const int ShutdownWaitMilliseconds = 2000;
    private const int MaxPayloadBytes = 64 * 1024;
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] UnexpectedExitRestartBackoff =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
    };

    private readonly object gate = new();
    private readonly NknIdentity identity;
    private readonly NknTransportOptions options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingHelloResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingPongResponses = new(StringComparer.Ordinal);

    private Process? process;
    private Task? stdoutReaderTask;
    private Task? stderrReaderTask;
    private StreamWriter? stdin;
    private TaskCompletionSource<string>? pendingReady;
    private CancellationTokenSource? pingLoopCts;
    private Task? pingLoopTask;
    private string address;
    private long nextCommandId;
    private int disconnectedRaised;
    private int unexpectedRestartLoopActive;
    private bool helloCompleted;
    private bool shuttingDown;
    private bool disposed;
    private string reliabilityModeHint = "Helper";

    public RealNknClientAdapter(NknIdentity identity, NknTransportOptions options)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        address = identity.Address;
    }

    public string Address
    {
        get
        {
            lock (gate)
            {
                return address;
            }
        }
    }

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    public event EventHandler? Disconnected;

    internal void SetReliabilityModeHint(string mode)
    {
        lock (gate)
        {
            reliabilityModeHint = string.Equals(mode, "Helpee", StringComparison.OrdinalIgnoreCase) ? "Helpee" : "Helper";
        }
    }

    internal async Task StartBridgeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        try
        {
            await EnsureProcessStartedAsync(ct);
            await EnsureHelloHandshakeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetNknStartFailed("bridge_start", ex.Message);
            throw;
        }
    }

    internal Task PingBridgeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        return SendCommandAndWaitBridgeEventAsync(
            "ping",
            payload: null,
            pendingPongResponses,
            PingTimeout,
            ct);
    }

    internal bool IsBridgeProcessRunning
    {
        get
        {
            lock (gate)
            {
                return process is not null && !process.HasExited;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        TaskCompletionSource<string>? readyWait;
        lock (gate)
        {
            if (process is not null && !process.HasExited && pendingReady is not null && pendingReady.Task.IsCompletedSuccessfully)
            {
                return;
            }
        }

        try
        {
            await EnsureProcessStartedAsync(ct);
            await EnsureHelloHandshakeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetNknStartFailed("bridge_start", ex.Message);
            throw;
        }

        lock (gate)
        {
            pendingReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            readyWait = pendingReady;
        }

        var seedBase64 = ReadPersistedSeedBase64(options.KeyPath);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["identifier"] = identity.Identifier,
            ["seedBase64"] = seedBase64,
            ["seedRpc"] = string.IsNullOrWhiteSpace(options.SeedRpc) ? null : options.SeedRpc,
        };

        await SendCommandAndWaitAckAsync("connect", payload, ct, timeoutOverride: CommandAckTimeout);

        string readyAddress;
        try
        {
            readyAddress = await readyWait.Task.WaitAsync(ConnectReadyTimeout, ct);
        }
        catch (TimeoutException ex)
        {
            NknRuntimeDiagnostics.SetLastError("bridge_connect_ready_timeout");
            SetNknStartFailed("ready_timeout", "Timed out waiting for bridge ready.");
            RecordBridgeFailure("bridge_connect_ready_timeout", "The local helper process did not become ready.");
            throw new TimeoutException("Timed out waiting for NKN bridge ready(address) after connect.", ex);
        }

        lock (gate)
        {
            address = string.IsNullOrWhiteSpace(readyAddress) ? identity.Address : readyAddress;
        }

        StartPingLoopIfNeeded();
        Log($"Connected bridge (address_len={Address.Length})");
    }

    public async Task DisconnectAsync()
    {
        if (disposed)
        {
            return;
        }

        Process? processToClose;
        lock (gate)
        {
            if (process is null)
            {
                return;
            }

            shuttingDown = true;
            processToClose = process;
        }

        try
        {
            await StopPingLoopAsync();
            await SendCommandAndWaitAckAsync(
                "shutdown",
                payload: null,
                CancellationToken.None,
                timeoutOverride: CommandAckTimeout);
        }
        catch (Exception ex)
        {
            Log($"Bridge shutdown command failed ({ex.GetType().Name})");
        }

        try
        {
            if (processToClose is not null && !processToClose.HasExited)
            {
                if (!processToClose.WaitForExit(ShutdownWaitMilliseconds))
                {
                    RecordBridgeFailure("bridge_shutdown_forced_kill", "Needed to close the local helper process.");
                    processToClose.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Bridge process stop failed ({ex.GetType().Name})");
        }
        finally
        {
            if (processToClose is not null)
            {
                var pid = processToClose.Id;
                var exitCodeText = "unknown";
                try
                {
                    if (processToClose.HasExited)
                    {
                        exitCodeText = processToClose.ExitCode.ToString();
                    }
                }
                catch
                {
                    exitCodeText = "unknown";
                }

                Log($"Bridge shutdown complete (pid={pid}, exit_code={exitCodeText})");
            }
            CleanupProcessState();
        }
    }

    public Task SubscribeAsync(string topic, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        Log($"Bridge subscribe (topic_len={topic.Length})");
        return SendCommandAndWaitAckAsync("subscribe", new Dictionary<string, object?> { ["topic"] = topic }, ct, timeoutOverride: CommandAckTimeout);
    }

    public Task UnsubscribeAsync(string topic)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return Task.CompletedTask;
        }

        Log($"Bridge unsubscribe (topic_len={topic.Length})");
        return SendCommandAndWaitAckAsync(
            "unsubscribe",
            new Dictionary<string, object?> { ["topic"] = topic },
            CancellationToken.None,
            timeoutOverride: CommandAckTimeout);
    }

    public Task PublishAsync(string topic, byte[] payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePayloadWithinLimit(payload, "publish");

        Log($"Bridge publish (topic_len={topic.Length}, payload_len={payload.Length})");
        return SendCommandAndWaitAckAsync(
            "publish",
            new Dictionary<string, object?>
            {
                ["topic"] = topic,
                ["payloadBase64"] = Convert.ToBase64String(payload),
            },
            ct,
            timeoutOverride: CommandAckTimeout);
    }

    public Task SendAsync(string destination, byte[] payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePayloadWithinLimit(payload, "send");

        Log($"Bridge send (dest_len={destination.Length}, payload_len={payload.Length})");
        return SendCommandAndWaitAckAsync(
            "send",
            new Dictionary<string, object?>
            {
                ["destination"] = destination,
                ["payloadBase64"] = Convert.ToBase64String(payload),
            },
            ct,
            timeoutOverride: CommandAckTimeout);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown in dispose.
        }
    }

    private Task EnsureProcessStartedAsync(CancellationToken ct)
    {
        Process? existing;
        lock (gate)
        {
            existing = process;
            if (existing is not null && !existing.HasExited && stdin is not null)
            {
                return Task.CompletedTask;
            }
        }

        var bridgePath = ResolveBridgeScriptPath();
        var nodePath = ResolveNodeExecutablePath();

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(bridgePath) ?? Environment.CurrentDirectory,
        };

        startInfo.ArgumentList.Add(bridgePath);

        var newProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        newProcess.Exited += OnBridgeProcessExited;

        if (!newProcess.Start())
        {
            throw new InvalidOperationException("Failed to start NKN bridge process.");
        }

        ct.ThrowIfCancellationRequested();

        var newStdin = newProcess.StandardInput;
        newStdin.AutoFlush = true;

        CleanupProcessState();

        lock (gate)
        {
            process = newProcess;
            stdin = newStdin;
            pendingReady = null;
            helloCompleted = false;
            Interlocked.Exchange(ref disconnectedRaised, 0);
            shuttingDown = false;
        }

        stdoutReaderTask = Task.Run(() => ReadStdoutLoopAsync(newProcess), CancellationToken.None);
        stderrReaderTask = Task.Run(() => ReadStderrLoopAsync(newProcess), CancellationToken.None);
        NknRuntimeDiagnostics.SetBridgeProcessInfo(newProcess.Id, nodeVersion: null);

        Log($"Bridge process started (pid={newProcess.Id}, node={Path.GetFileName(nodePath)}, script={bridgePath})");
        return Task.CompletedTask;
    }

    private async Task EnsureHelloHandshakeAsync(CancellationToken ct)
    {
        lock (gate)
        {
            if (helloCompleted)
            {
                return;
            }
        }

        try
        {
            await SendCommandAndWaitBridgeEventAsync(
                "hello",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["protocol"] = 1,
                    ["appVersion"] = GetAssemblyVersionString(),
                },
                pendingHelloResponses,
                HelloTimeout,
                ct);

            lock (gate)
            {
                helloCompleted = true;
            }

            Log("Bridge hello_ok");
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError("bridge_hello_failed");
            SetNknStartFailed("hello_failed", ex.Message);
            RecordBridgeFailure("bridge_hello_failed", "Could not start the local helper process.");
            throw new InvalidOperationException($"NKN bridge hello failed: {ex.Message}", ex);
        }
    }

    private async Task SendCommandAndWaitAckAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        CancellationToken ct,
        TimeSpan? timeoutOverride = null)
    {
        ThrowIfDisposed();

        StreamWriter writer;
        Process? currentProcess;
        lock (gate)
        {
            writer = stdin ?? throw new InvalidOperationException("NKN bridge is not running.");
            currentProcess = process;
        }

        if (currentProcess is null || currentProcess.HasExited)
        {
            throw new InvalidOperationException("NKN bridge process is not available.");
        }

        var id = Interlocked.Increment(ref nextCommandId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var wait = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingCommands.TryAdd(id, wait))
        {
            throw new InvalidOperationException("Duplicate bridge command id.");
        }

        try
        {
            var command = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["cmd"] = cmd,
            };

            if (payload is not null)
            {
                foreach (var pair in payload)
                {
                    command[pair.Key] = pair.Value;
                }
            }

            var json = JsonSerializer.Serialize(command);
            await writer.WriteLineAsync(json);

            var ackTask = wait.Task;
            var timeout = timeoutOverride ?? CommandAckTimeout;
            try
            {
                await ackTask.WaitAsync(timeout, ct);
            }
            catch (TimeoutException ex)
            {
                NknRuntimeDiagnostics.SetLastError($"bridge_{cmd}_timeout");
                if (string.Equals(cmd, "shutdown", StringComparison.Ordinal))
                {
                    RecordBridgeFailure("bridge_shutdown_timeout", "The local helper process took too long to close.");
                }
                throw new TimeoutException($"Timed out waiting for bridge response to '{cmd}'.", ex);
            }
        }
        finally
        {
            pendingCommands.TryRemove(id, out _);
        }
    }

    private async Task<JsonElement> SendCommandAndWaitBridgeEventAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingMap,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        StreamWriter writer;
        Process? currentProcess;
        lock (gate)
        {
            writer = stdin ?? throw new InvalidOperationException("NKN bridge is not running.");
            currentProcess = process;
        }

        if (currentProcess is null || currentProcess.HasExited)
        {
            throw new InvalidOperationException("NKN bridge process is not available.");
        }

        var id = Interlocked.Increment(ref nextCommandId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var wait = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingMap.TryAdd(id, wait))
        {
            throw new InvalidOperationException("Duplicate bridge command id.");
        }

        try
        {
            var command = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["cmd"] = cmd,
            };

            if (payload is not null)
            {
                foreach (var pair in payload)
                {
                    command[pair.Key] = pair.Value;
                }
            }

            var json = JsonSerializer.Serialize(command);
            await writer.WriteLineAsync(json);
            return await wait.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            pendingMap.TryRemove(id, out _);
        }
    }

    private async Task ReadStdoutLoopAsync(Process targetProcess)
    {
        try
        {
            while (!targetProcess.HasExited)
            {
                var line = await targetProcess.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                HandleStdoutJsonLine(line);
            }
        }
        catch (Exception ex)
        {
            Log($"Bridge stdout reader failed ({ex.GetType().Name})");
            SignalDisconnected($"stdout_reader_failed:{ex.GetType().Name}");
        }
    }

    private async Task ReadStderrLoopAsync(Process targetProcess)
    {
        try
        {
            while (!targetProcess.HasExited)
            {
                var line = await targetProcess.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                Log($"bridge stderr: {line}");
            }
        }
        catch
        {
            // stderr is diagnostic only
        }
    }

    private void HandleStdoutJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!TryGetString(root, "event", out var eventName))
            {
                return;
            }

            switch (eventName)
            {
                case "ok":
                    HandleCommandOk(root);
                    break;
                case "error":
                    HandleCommandError(root);
                    break;
                case "hello_ok":
                    HandleHelloOk(root);
                    break;
                case "pong":
                    HandlePong(root);
                    break;
                case "ready":
                    HandleReady(root);
                    break;
                case "message":
                    HandleMessage(root);
                    break;
                case "disconnected":
                    HandleBridgeDisconnected(root);
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log($"Bridge stdout JSON parse failed ({ex.GetType().Name})");
        }
    }

    private void HandleCommandOk(JsonElement root)
    {
        if (!TryGetId(root, out var id))
        {
            return;
        }

        if (pendingCommands.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(root.Clone());
        }
    }

    private void HandleCommandError(JsonElement root)
    {
        var reason = TryGetString(root, "reason", out var r) ? r : "bridge_command_error";
        if (TryGetId(root, out var id) && pendingCommands.TryGetValue(id, out var tcs))
        {
            tcs.TrySetException(new InvalidOperationException(reason));
        }
        else if (TryGetId(root, out id) && pendingHelloResponses.TryGetValue(id, out var helloTcs))
        {
            helloTcs.TrySetException(new InvalidOperationException(reason));
        }
        else if (TryGetId(root, out id) && pendingPongResponses.TryGetValue(id, out var pongTcs))
        {
            pongTcs.TrySetException(new InvalidOperationException(reason));
        }
        else
        {
            SignalDisconnected("bridge_error:" + reason);
        }
    }

    private void HandleHelloOk(JsonElement root)
    {
        if (!TryGetId(root, out var id))
        {
            return;
        }

        string? sdk = null;
        if (TryGetString(root, "sdk", out var sdkValue) && !string.IsNullOrWhiteSpace(sdkValue))
        {
            sdk = sdkValue;
        }

        try
        {
            lock (gate)
            {
                NknRuntimeDiagnostics.SetBridgeProcessInfo(process?.Id ?? 0, sdk);
            }
        }
        catch
        {
            NknRuntimeDiagnostics.SetBridgeProcessInfo(0, sdk);
        }

        if (pendingHelloResponses.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(root.Clone());
        }
    }

    private void HandlePong(JsonElement root)
    {
        if (!TryGetId(root, out var id))
        {
            return;
        }

        NknRuntimeDiagnostics.SetBridgeLastPongUtc(DateTimeOffset.UtcNow);

        if (pendingPongResponses.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(root.Clone());
        }
    }

    private void HandleReady(JsonElement root)
    {
        var readyAddress = TryGetString(root, "address", out var a) ? a : string.Empty;
        lock (gate)
        {
            address = string.IsNullOrWhiteSpace(readyAddress) ? identity.Address : readyAddress;
            pendingReady?.TrySetResult(address);
        }
    }

    private void HandleMessage(JsonElement root)
    {
        var source = TryGetString(root, "source", out var s) ? s : string.Empty;
        var payloadBase64 = TryGetString(root, "payloadBase64", out var p) ? p : string.Empty;
        var isTopic = root.TryGetProperty("isTopic", out var isTopicProp) && isTopicProp.ValueKind == JsonValueKind.True;
        var topic = TryGetString(root, "topic", out var t) ? t : null;

        NknRuntimeDiagnostics.IncrementBridgeRawMessagesReceived();
        NknRuntimeDiagnostics.SetLastBridgeMessage(source, isTopic);

        byte[] payloadBytes;
        try
        {
            payloadBytes = string.IsNullOrWhiteSpace(payloadBase64)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(payloadBase64);
        }
        catch (FormatException)
        {
            Log("Bridge message ignored (invalid payloadBase64)");
            return;
        }

        if (payloadBytes.Length > MaxPayloadBytes)
        {
            NknRuntimeDiagnostics.SetLastError("bridge_incoming_payload_too_large");
            Log($"Bridge message ignored (payload too large, payload_len={payloadBytes.Length})");
            return;
        }

        Log($"Bridge message (source_len={source.Length}, payload_len={payloadBytes.Length}, is_topic={isTopic})");
        MessageReceived?.Invoke(this, new NknIncomingMessage(source, payloadBytes, isTopic, topic));
    }

    private void HandleBridgeDisconnected(JsonElement root)
    {
        var reason = TryGetString(root, "reason", out var r) ? r : "bridge_disconnected";
        NknRuntimeDiagnostics.SetBridgeLastExit(exitCode: null, reason: reason);
        SignalDisconnected(reason);
    }

    private void OnBridgeProcessExited(object? sender, EventArgs e)
    {
        var p = sender as Process;
        int? exitCode = null;
        if (p is not null)
        {
            try
            {
                exitCode = p.ExitCode;
            }
            catch
            {
                exitCode = null;
            }
        }

        var reason = p is null || exitCode is null ? "bridge_process_exited" : $"bridge_process_exited:{exitCode.Value}";
        Log($"Bridge process exited (pid={(p?.Id.ToString() ?? "unknown")}, exit_code={(exitCode?.ToString() ?? "unknown")})");
        NknRuntimeDiagnostics.SetBridgeLastExit(exitCode, "process exited");
        _ = Task.Run(() => HandleUnexpectedProcessExitAsync(reason), CancellationToken.None);
    }

    private void SignalDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref disconnectedRaised, 1) != 0)
        {
            return;
        }

        if (!string.Equals(reason, "shutdown", StringComparison.OrdinalIgnoreCase))
        {
            NknRuntimeDiagnostics.SetLastDisconnectReason(reason);
        }
        Log($"Bridge disconnected ({reason})");
        FailPendingOperations(reason);

        if (!shuttingDown && !disposed)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task HandleUnexpectedProcessExitAsync(string reason)
    {
        if (disposed)
        {
            return;
        }

        bool expectedShutdown;
        bool wasConnected;
        lock (gate)
        {
            expectedShutdown = shuttingDown;
            wasConnected = pendingReady is not null && pendingReady.Task.IsCompletedSuccessfully;
        }

        if (expectedShutdown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref unexpectedRestartLoopActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Log($"Bridge exited unexpectedly ({reason})");
            FailPendingOperations(reason);
            await StopPingLoopAsync();
            CleanupProcessState();

            for (var i = 0; i < UnexpectedExitRestartBackoff.Length && !disposed; i++)
            {
                var delay = UnexpectedExitRestartBackoff[i];
                Log($"Bridge restart retry {i + 1}/{UnexpectedExitRestartBackoff.Length} in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, CancellationToken.None);

                try
                {
                    await EnsureProcessStartedAsync(CancellationToken.None);
                    await EnsureHelloHandshakeAsync(CancellationToken.None);

                    if (wasConnected)
                    {
                        await ConnectAsync(CancellationToken.None);
                    }

                    NknRuntimeDiagnostics.IncrementBridgeRestartCount();
                    Log("Bridge restart succeeded");
                    return;
                }
                catch (Exception ex)
                {
                    NknRuntimeDiagnostics.SetLastError("bridge_restart_retry_failed");
                    Log($"Bridge restart attempt failed ({ex.GetType().Name})");
                    CleanupProcessState();
                }
            }

            NknRuntimeDiagnostics.SetLastError("bridge_restart_failed");
            RecordBridgeFailure("bridge_restart_exhausted", "The local helper process kept closing.");
            SignalDisconnected("bridge_restart_failed");
        }
        finally
        {
            Interlocked.Exchange(ref unexpectedRestartLoopActive, 0);
        }
    }

    private void FailPendingOperations(string reason)
    {
        lock (gate)
        {
            pendingReady?.TrySetException(new InvalidOperationException(reason));
        }

        foreach (var pending in pendingCommands.ToArray())
        {
            if (pendingCommands.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }
        }

        foreach (var pending in pendingHelloResponses.ToArray())
        {
            if (pendingHelloResponses.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }
        }

        foreach (var pending in pendingPongResponses.ToArray())
        {
            if (pendingPongResponses.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }
        }

        CancelPingLoop();
    }

    private void CleanupProcessState()
    {
        lock (gate)
        {
            if (process is not null)
            {
                process.Exited -= OnBridgeProcessExited;
                process.Dispose();
                process = null;
            }

            stdin?.Dispose();
            stdin = null;
            pendingReady = null;
            helloCompleted = false;
            stdoutReaderTask = null;
            stderrReaderTask = null;
        }
    }

    private void StartPingLoopIfNeeded()
    {
        lock (gate)
        {
            if (pingLoopTask is not null && !pingLoopTask.IsCompleted)
            {
                return;
            }

            pingLoopCts?.Cancel();
            pingLoopCts?.Dispose();
            pingLoopCts = new CancellationTokenSource();
            pingLoopTask = Task.Run(() => PingLoopAsync(pingLoopCts.Token), CancellationToken.None);
        }
    }

    private async Task StopPingLoopAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (gate)
        {
            task = pingLoopTask;
            cts = pingLoopCts;
            pingLoopTask = null;
            pingLoopCts = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (task is not null)
        {
            try { await task; } catch { }
        }

        cts?.Dispose();
    }

    private void CancelPingLoop()
    {
        lock (gate)
        {
            try { pingLoopCts?.Cancel(); } catch { }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var consecutiveMisses = 0;

        try
        {
            while (!ct.IsCancellationRequested && !disposed)
            {
                await Task.Delay(PingInterval, ct);
                ct.ThrowIfCancellationRequested();

                try
                {
                    await SendCommandAndWaitBridgeEventAsync(
                        "ping",
                        payload: null,
                        pendingPongResponses,
                        PingTimeout,
                        ct);
                    consecutiveMisses = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveMisses++;
                    Log($"Ping miss ({consecutiveMisses}/3, ex={ex.GetType().Name})");

                    if (consecutiveMisses < 3)
                    {
                        continue;
                    }

                    NknRuntimeDiagnostics.SetLastError("bridge_ping_timeout");
                    RecordBridgeFailure("bridge_ping_timeout", "The local helper process stopped responding.");
                    await RestartBridgeProcessAsync();
                    SignalDisconnected("bridge_ping_timeout");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task RestartBridgeProcessAsync()
    {
        Process? toKill;
        lock (gate)
        {
            toKill = process;
            shuttingDown = true;
        }

        try
        {
            if (toKill is not null && !toKill.HasExited)
            {
                toKill.Kill(entireProcessTree: true);
                toKill.WaitForExit(ShutdownWaitMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Log($"Bridge restart kill failed ({ex.GetType().Name})");
        }
        finally
        {
            CleanupProcessState();
        }

        try
        {
            if (!disposed)
            {
                await EnsureProcessStartedAsync(CancellationToken.None);
                await EnsureHelloHandshakeAsync(CancellationToken.None);
                NknRuntimeDiagnostics.IncrementBridgeRestartCount();
            }
        }
        catch (Exception ex)
        {
            Log($"Bridge restart failed ({ex.GetType().Name})");
        }
        finally
        {
            lock (gate)
            {
                if (!disposed)
                {
                    shuttingDown = false;
                }
            }
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.Null)
        {
            value = string.Empty;
            return true;
        }

        value = prop.ToString();
        return true;
    }

    private static bool TryGetId(JsonElement root, out string id)
    {
        id = string.Empty;
        if (!root.TryGetProperty("id", out var prop))
        {
            return false;
        }

        switch (prop.ValueKind)
        {
            case JsonValueKind.String:
                id = prop.GetString() ?? string.Empty;
                return id.Length > 0;
            case JsonValueKind.Number:
                id = prop.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private string ResolveBridgeScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolved = Path.GetFullPath(overridePath);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        var rid = GetBridgeRid();

#if DEBUG
        foreach (var candidate in EnumerateBridgeCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
#else
        var bundledBridge = Path.Combine(AppContext.BaseDirectory, "bridge", rid, "index.js");
        if (File.Exists(bundledBridge))
        {
            return bundledBridge;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var macResourcesBridge = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, "index.js"));
            if (File.Exists(macResourcesBridge))
            {
                return macResourcesBridge;
            }
        }
#endif

        throw new FileNotFoundException(
            $"NKN bridge script not found. Expected bridge/{rid}/index.js (or macOS Resources/bridge/{rid}/index.js) in app output, or set NLINK_NKN_BRIDGE_PATH.");
    }

    private string ResolveNodeExecutablePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolved = Path.GetFullPath(overridePath);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        var rid = GetBridgeRid();
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

        var bundled = Path.Combine(AppContext.BaseDirectory, "bridge", rid, exeName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var macResourcesNode = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, exeName));
            if (File.Exists(macResourcesNode))
            {
                return macResourcesNode;
            }
        }

#if DEBUG
        return "node";
#else
        throw new FileNotFoundException(
            $"Bundled Node runtime not found. Expected bridge/{rid}/{exeName} (or macOS Resources/bridge/{rid}/{exeName}) in app output, or set NLINK_NKN_NODE_PATH.");
#endif
    }

    private static IEnumerable<string> EnumerateBridgeCandidates()
    {
        var rid = GetBridgeRid();

        yield return Path.Combine(AppContext.BaseDirectory, "bridge", rid, "index.js");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, "index.js"));
        }

        yield return Path.Combine(AppContext.BaseDirectory, "bridge", "index.js");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "nkn-bridge", "index.js");

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "tools", "nkn-bridge", "index.js");
        }
    }

    private static string? ReadPersistedSeedBase64(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            return null;
        }

        using var stream = File.OpenRead(keyPath);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("SeedBase64", out var seedProp))
        {
            return null;
        }

        return seedProp.ValueKind == JsonValueKind.String ? seedProp.GetString() : null;
    }

    private static string GetAssemblyVersionString()
    {
        var assembly = typeof(RealNknClientAdapter).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.1.0";
    }

    private static string GetBridgeRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException(
                    $"Unsupported bridge platform/architecture: macOS {RuntimeInformation.OSArchitecture}.")
            };
        }

        throw new NotSupportedException(
            $"Unsupported bridge platform/architecture: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}.");
    }

    private static void EnsurePayloadWithinLimit(byte[] payload, string cmd)
    {
        if (payload.Length <= MaxPayloadBytes)
        {
            return;
        }

        NknRuntimeDiagnostics.SetLastError($"bridge_{cmd}_payload_too_large");
        throw new InvalidOperationException($"Bridge payload too large for '{cmd}' (max {MaxPayloadBytes} bytes).");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void SetNknStartFailed(string shortReason, string? detail)
    {
        var safeDetail = (detail ?? string.Empty).Trim();
        if (safeDetail.Length > 120)
        {
            safeDetail = safeDetail[..120];
        }

        if (string.IsNullOrWhiteSpace(safeDetail))
        {
            NknRuntimeDiagnostics.SetLastError($"NKN_START_FAILED: {shortReason}");
            return;
        }

        NknRuntimeDiagnostics.SetLastError($"NKN_START_FAILED: {shortReason} ({safeDetail})");
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[nLink][NKN][Bridge] {message}");
        LocalOperationalLog.Info("NKN.Bridge", message);
    }

    private void RecordBridgeFailure(string errorCode, string? errorHint)
    {
        string mode;
        lock (gate)
        {
            mode = reliabilityModeHint;
        }

        SessionReliabilityLog.RecordStandalone(
            mode,
            transport: "NKN",
            stage: SessionReliabilityStage.Disconnected,
            errorCode: errorCode,
            errorHint: errorHint);
    }
}
