using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using NLink.Core.FileTransfer;

namespace NLink.App.Services;

// Smoke-only, local, diagnostic carrier. The request cannot choose a path or command.
internal sealed class GuiSmokeSessionPreparationBridge : IDisposable
{
    internal const string RootVariable = "NLINK_GUI_PREPARATION_OBSERVATION_ROOT";
    internal const string BootVariable = "NLINK_GUI_PREPARATION_OBSERVATION_BOOT";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    internal sealed record Request(string RequestId, string ExpectedSessionId, string Direction, long RequestedUtcMs);
    internal sealed record Identity(int ProcessId, string BootId, long ProcessStartedUtcMs,
        string LocalRole, SessionPreparationDiagnosticScope? Scope);
    internal sealed record Response(string RequestId, long RequestedUtcMs, long CapturedUtcMs,
        Identity Identity, SessionPreparationDiagnostic Observation);

    private readonly object gate = new();
    private readonly SessionFileTransferService service;
    private readonly Func<string> localRole;
    private readonly string directory;
    private readonly string bootId;
    private readonly long processStartedUtcMs;
    private readonly Timer timer;
    private Identity? lastIdentity;
    private string? lastRequest;
    private bool disposed;

    internal static GuiSmokeSessionPreparationBridge? TryStart(SessionFileTransferService service, Func<string> localRole)
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        var boot = Environment.GetEnvironmentVariable(BootVariable);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS")) ||
            string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            !Guid.TryParseExact(boot, "N", out _)) return null;
        try { return new(service, localRole, root, boot!); }
        catch { return null; } // Diagnostic I/O must not disrupt the app.
    }

    private GuiSmokeSessionPreparationBridge(SessionFileTransferService service, Func<string> localRole, string root, string bootId)
    {
        this.service = service;
        this.localRole = localRole;
        this.bootId = bootId;
        using var process = Process.GetCurrentProcess();
        processStartedUtcMs = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        directory = Path.Combine(Path.GetFullPath(root), $"{Environment.ProcessId}-{bootId}");
        Directory.CreateDirectory(directory);
        timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (disposed) return;
            var identity = new Identity(Environment.ProcessId, bootId, processStartedUtcMs,
                localRole(), service.GetSessionPreparationDiagnosticScope());
            if (identity != lastIdentity)
            {
                WriteAtomic("identity.json", identity);
                lastIdentity = identity;
            }
            var path = Path.Combine(directory, "request.json");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > 4096) return;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var request = JsonSerializer.Deserialize<Request>(bytes, JsonOptions);
            if (request is null || request.RequestId == lastRequest ||
                !Guid.TryParseExact(request.RequestId, "N", out _) ||
                string.IsNullOrWhiteSpace(request.ExpectedSessionId) || request.ExpectedSessionId.Length > 256 ||
                request.Direction is not ("Outbound" or "Inbound") || request.RequestedUtcMs <= 0 ||
                request.RequestedUtcMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return;
            var observation = service.ObserveSessionPreparationForDiagnostics(request.ExpectedSessionId,
                request.Direction == "Outbound" ? FileTransferDirection.Outbound : FileTransferDirection.Inbound,
                DateTimeOffset.FromUnixTimeMilliseconds(request.RequestedUtcMs));
            if (identity.Scope != observation.Scope || identity.LocalRole != localRole())
                observation = observation with { Ready = false, Reason = "scope_changed_during_capture", Revoked = true };
            WriteAtomic("response.json", new Response(request.RequestId, request.RequestedUtcMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), identity, observation));
            lastRequest = request.RequestId;
        }
        catch { /* A partial/malformed request or failed I/O is diagnostic-only. */ }
        finally { Monitor.Exit(gate); }
    }

    private void WriteAtomic<T>(string name, T value)
    {
        var path = Path.Combine(directory, name);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer.Dispose();
        }
    }
}
