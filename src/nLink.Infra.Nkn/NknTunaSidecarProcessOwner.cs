using System.Diagnostics;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class NknTunaSidecarProcessOwner : IDisposable
{
    private static readonly TimeSpan GracefulStopWait = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly string role;
    private readonly string runId;
    private Process? process;
    private WindowsKillOnCloseProcessJob? lifetimeGuard;
    private bool stopped;

    private NknTunaSidecarProcessOwner(string role, Process process, WindowsKillOnCloseProcessJob? lifetimeGuard)
    {
        this.role = Sanitize(role);
        this.process = process;
        this.lifetimeGuard = lifetimeGuard;
        runId = Guid.NewGuid().ToString("N");
        StartedUtc = DateTimeOffset.UtcNow;
        Pid = SafeGetPid(process);
    }

    public int Pid { get; }

    public DateTimeOffset StartedUtc { get; }

    public string RunId => runId;

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return process is not null && !SafeHasExited(process);
            }
        }
    }

    public static NknTunaSidecarProcessOwner Attach(string role, Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var safeRole = Sanitize(role);
        var guard = WindowsKillOnCloseProcessJob.TryAttach(
            process,
            message => LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_sidecar_job_assign_failed; role={safeRole}; pid={SafeGetPid(process)}; detail={Sanitize(message)}"));
        var owner = new NknTunaSidecarProcessOwner(safeRole, process, guard);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_sidecar_process_started; role={safeRole}; pid={owner.Pid}; run_id={owner.RunId}");
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_sidecar_job_assigned; role={safeRole}; pid={owner.Pid}; run_id={owner.RunId}; assigned={FormatBool(guard is not null)}");
        return owner;
    }

    public bool Owns(Process candidate)
    {
        lock (gate)
        {
            return ReferenceEquals(process, candidate);
        }
    }

    public void Stop(string reason)
    {
        Process? processToStop;
        WindowsKillOnCloseProcessJob? guardToDispose;
        lock (gate)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            processToStop = process;
            guardToDispose = lifetimeGuard;
            process = null;
            lifetimeGuard = null;
        }

        var safeReason = Sanitize(reason);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_sidecar_stop_requested; role={role}; pid={Pid}; run_id={runId}; reason={safeReason}");
        try
        {
            if (processToStop is not null && !SafeHasExited(processToStop))
            {
                if (!processToStop.WaitForExit((int)GracefulStopWait.TotalMilliseconds))
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_sidecar_force_killed; role={role}; pid={Pid}; run_id={runId}; reason={safeReason}");
                    processToStop.Kill(entireProcessTree: true);
                    _ = processToStop.WaitForExit((int)StopWait.TotalMilliseconds);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_sidecar_stop_failed; role={role}; pid={Pid}; run_id={runId}; reason={safeReason}; error={ex.GetType().Name}");
        }
        finally
        {
            try { guardToDispose?.Dispose(); } catch { }
            var exited = processToStop is null || SafeHasExited(processToStop);
            var exitCode = SafeGetExitCode(processToStop);
            try { processToStop?.Dispose(); } catch { }
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_sidecar_stop_completed; role={role}; pid={Pid}; run_id={runId}; reason={safeReason}; exited={FormatBool(exited)}; exit_code={exitCode}");
        }
    }

    public void Dispose()
        => Stop("disposed");

    private static int SafeGetPid(Process process)
    {
        try { return process.Id; } catch { return -1; }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static string SafeGetExitCode(Process? process)
    {
        if (process is null)
        {
            return "none";
        }

        try
        {
            return process.HasExited ? process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "running";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string FormatBool(bool value)
        => value ? "true" : "false";

    private static string Sanitize(string? value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        if (safe.Length > 96)
        {
            safe = safe[..96];
        }

        return safe
            .Replace(";", "_", StringComparison.Ordinal)
            .Replace("\r", "_", StringComparison.Ordinal)
            .Replace("\n", "_", StringComparison.Ordinal);
    }
}
