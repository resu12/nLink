using System.Collections.Concurrent;
using System.Text.Json;

namespace NLink.Infra.Nkn;

internal sealed class BridgeProtocolClient
{
    private readonly Func<JsonlWriter> getWriter;
    private readonly Action<string> log;
    private readonly Action<JsonElement> onReady;
    private readonly Action<string, JsonElement> onRpcProgress;
    private readonly Action<JsonElement> onMessage;
    private readonly Action<JsonElement> onDisconnected;
    private readonly Action<JsonElement> onHelloOk;
    private readonly Action<JsonElement> onPong;
    private readonly Action<string> onUnmatchedBridgeError;
    private readonly Action<string, int>? onCommandSerialized;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingHelloResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingPongResponses = new(StringComparer.Ordinal);

    private long nextCommandId;

    public BridgeProtocolClient(
        Func<JsonlWriter> getWriter,
        Action<string> log,
        Action<JsonElement> onReady,
        Action<string, JsonElement> onRpcProgress,
        Action<JsonElement> onMessage,
        Action<JsonElement> onDisconnected,
        Action<JsonElement> onHelloOk,
        Action<JsonElement> onPong,
        Action<string> onUnmatchedBridgeError,
        Action<string, int>? onCommandSerialized = null)
    {
        this.getWriter = getWriter;
        this.log = log;
        this.onReady = onReady;
        this.onRpcProgress = onRpcProgress;
        this.onMessage = onMessage;
        this.onDisconnected = onDisconnected;
        this.onHelloOk = onHelloOk;
        this.onPong = onPong;
        this.onUnmatchedBridgeError = onUnmatchedBridgeError;
        this.onCommandSerialized = onCommandSerialized;
    }

    public async Task SendCommandAndWaitAckAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        TimeSpan timeout,
        CancellationToken ct,
        Action<int>? onSerialized = null)
    {
        var wait = await SendCommandAsync(
            cmd,
            payload,
            pendingCommands,
            ct,
            onSerialized).ConfigureAwait(false);

        await wait.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendCommandAndWaitBridgeEventAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        BridgeWaitKind waitKind,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var wait = await SendCommandAsync(
            cmd,
            payload,
            GetPendingMap(waitKind),
            ct,
            onSerialized: null).ConfigureAwait(false);

        return await wait.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendPingAndWaitPongAsync(TimeSpan timeout, CancellationToken ct)
    {
        var writer = getWriter();
        var id = Interlocked.Increment(ref nextCommandId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var wait = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingPongResponses.TryAdd(id, wait))
        {
            throw new InvalidOperationException("Duplicate bridge command id.");
        }

        try
        {
            var ping = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "ping",
                ["id"] = id,
            });

            await writer.WriteLineAsync(ping, ct).ConfigureAwait(false);
            return await wait.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            pendingPongResponses.TryRemove(id, out _);
        }
    }

    public void HandleStdoutJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                log($"Bridge stdout ignored (non_object_json, preview={BuildLinePreview(line)})");
                return;
            }

            if (!TryGetString(root, "event", out var eventName))
            {
                if (!TryGetString(root, "type", out eventName))
                {
                    log($"Bridge stdout ignored (missing_event_type, preview={BuildLinePreview(line)})");
                    return;
                }
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
                    onReady(root.Clone());
                    break;
                case "rpc_preflight":
                case "rpc_selected":
                case "rpc_fallback_attempt":
                    onRpcProgress(eventName, root.Clone());
                    break;
                case "message":
                    onMessage(root.Clone());
                    break;
                case "disconnected":
                    onDisconnected(root.Clone());
                    break;
                default:
                    log($"Bridge stdout ignored (unknown_event={eventName}, preview={BuildLinePreview(line)})");
                    break;
            }
        }
        catch (JsonException ex)
        {
            log($"Bridge stdout JSON parse failed ({ex.GetType().Name}, preview={BuildLinePreview(line)})");
        }
    }

    public void FailPendingOperations(string reason)
    {
        FailPendingMap(pendingCommands, reason);
        FailPendingMap(pendingHelloResponses, reason);
        FailPendingMap(pendingPongResponses, reason);
    }

    private async Task<TaskCompletionSource<JsonElement>> SendCommandAsync(
        string cmd,
        Dictionary<string, object?>? payload,
        ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingMap,
        CancellationToken ct,
        Action<int>? onSerialized)
    {
        var writer = getWriter();
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
            var jsonlBytes = NknBridgePayloadAccounting.MeasureSerializedJsonlBytes(json);
            onCommandSerialized?.Invoke(cmd, jsonlBytes);
            onSerialized?.Invoke(jsonlBytes);
            await writer.WriteLineAsync(json, ct).ConfigureAwait(false);
            return wait;
        }
        catch
        {
            pendingMap.TryRemove(id, out _);
            throw;
        }
    }

    private ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> GetPendingMap(BridgeWaitKind waitKind)
    {
        return waitKind switch
        {
            BridgeWaitKind.HelloOk => pendingHelloResponses,
            BridgeWaitKind.Pong => pendingPongResponses,
            _ => pendingCommands,
        };
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
            onUnmatchedBridgeError(reason);
        }
    }

    private void HandleHelloOk(JsonElement root)
    {
        if (!TryGetId(root, out var id))
        {
            return;
        }

        onHelloOk(root.Clone());

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

        onPong(root.Clone());

        if (pendingPongResponses.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(root.Clone());
        }
    }

    private static void FailPendingMap(ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> map, string reason)
    {
        foreach (var pending in map.ToArray())
        {
            if (map.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }
        }
    }

    private static bool TryGetId(JsonElement root, out string id)
    {
        id = string.Empty;
        return TryGetString(root, "id", out id);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString() ?? string.Empty;
        return true;
    }

    private static string BuildLinePreview(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "(empty)";
        }

        var compact = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length > 160)
        {
            compact = compact[..160];
        }

        return compact;
    }
}

internal enum BridgeWaitKind
{
    CommandAck,
    HelloOk,
    Pong,
}
