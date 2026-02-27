using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App;

internal static class BridgeSelfTestRunner
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            var rid = GetBridgeRid();
            var bridgePath = ResolveBridgeScriptPath(rid);
            var nodePath = ResolveNodeExecutablePath(rid);

            await output.WriteLineAsync($"nLink self-test (bridge) | RID={rid}");
            await output.WriteLineAsync($"Node: {nodePath}");
            await output.WriteLineAsync($"Bridge: {bridgePath}");

            await RunBridgeHelloPingAsync(nodePath, bridgePath, ct);

            await output.WriteLineAsync("PASS");
            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"FAIL: {ex.Message}");
            return 1;
        }
    }

    private static async Task RunBridgeHelloPingAsync(string nodePath, string bridgePath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = QuoteArgument(bridgePath),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(bridgePath) ?? Environment.CurrentDirectory
            },
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start bridge process.");
        }

        process.StandardInput.AutoFlush = true;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Best-effort diagnostics consumption only.
            }
        }, CancellationToken.None);

        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":\"1\",\"cmd\":\"hello\",\"protocol\":1,\"appVersion\":\"self-test\"}").ConfigureAwait(false);
            var helloLine = await ReadLineWithTimeoutAsync(process, HelloTimeout, ct).ConfigureAwait(false);
            var helloEvent = ParseEvent(helloLine);
            if (!string.Equals(helloEvent.Name, "hello_ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected hello response: {helloEvent.Name}");
            }

            await process.StandardInput.WriteLineAsync("{\"id\":\"2\",\"cmd\":\"ping\"}").ConfigureAwait(false);
            var pongLine = await ReadLineWithTimeoutAsync(process, PingTimeout, ct).ConfigureAwait(false);
            var pongEvent = ParseEvent(pongLine);
            if (!string.Equals(pongEvent.Name, "pong", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected ping response: {pongEvent.Name}");
            }

            await process.StandardInput.WriteLineAsync("{\"id\":\"3\",\"cmd\":\"shutdown\"}").ConfigureAwait(false);
            if (!process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds))
            {
                TryKillProcess(process);
                throw new TimeoutException("Bridge shutdown timed out.");
            }
        }
        finally
        {
            TryKillProcess(process);
        }
    }

    private static async Task<string> ReadLineWithTimeoutAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        var readTask = process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (completed != readTask)
        {
            throw new TimeoutException("Timed out waiting for bridge response.");
        }

        var line = await readTask.ConfigureAwait(false);
        if (line is null)
        {
            throw new InvalidOperationException("Bridge closed stdout unexpectedly.");
        }

        return line;
    }

    private static (string Name, JsonElement Root) ParseEvent(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Bridge returned non-object JSON line: {BuildLinePreview(line)}");
        }

        if (!TryReadEventName(root, out var eventName))
        {
            throw new InvalidOperationException($"Bridge returned JSON without event/type: {BuildLinePreview(line)}");
        }

        return (eventName, root);
    }

    private static string ResolveBridgeScriptPath(string rid)
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

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "bridge", rid, "index.js")
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, "index.js")));
        }

#if DEBUG
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "nkn-bridge", "index.js"));
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "tools", "nkn-bridge", "index.js"));
        }
#endif

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Bridge script not found. Expected bridge/{rid}/index.js.");
    }

    private static string ResolveNodeExecutablePath(string rid)
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

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "bridge", rid, exeName)
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "bridge", rid, exeName)));
        }

#if DEBUG
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "node";
#else
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Bundled node runtime not found. Expected bridge/{rid}/{exeName}.");
#endif
    }

    private static string GetBridgeRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException($"Unsupported macOS architecture: {RuntimeInformation.OSArchitecture}")
            };
        }

        throw new NotSupportedException($"Unsupported platform/architecture: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}");
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static bool TryReadEventName(JsonElement root, out string eventName)
    {
        eventName = string.Empty;
        if (root.TryGetProperty("event", out var eventProp) && eventProp.ValueKind == JsonValueKind.String)
        {
            eventName = eventProp.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(eventName);
        }

        if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            eventName = typeProp.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(eventName);
        }

        return false;
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
