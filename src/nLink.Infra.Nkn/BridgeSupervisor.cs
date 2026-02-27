using System.Diagnostics;
using System.Text;
using NLink.Core.Resources;

namespace NLink.Infra.Nkn;

internal sealed class BridgeSupervisor : IBridgeProcessRunner
{
    private const int ShutdownWaitMilliseconds = 2000;

    private readonly object gate = new();
    private readonly BridgeSupervisorCallbacks callbacks;
    private readonly Func<string> resolveNodePath;
    private readonly Func<string> resolveBridgePath;
    private readonly Func<string, string, bool, CancellationToken, Task> onStdoutLineAsync;
    private readonly Func<string, string, bool, CancellationToken, Task> onStderrLineAsync;
    private readonly Func<string> getCleanupReasonPrefix;
    private readonly Func<bool> isDisposed;
    private readonly Func<bool> isShuttingDown;
    private readonly Func<string> getReliabilityModeHint;
    private readonly Func<double?> getCurrentUptimeMs;

    private Process? process;
    private Task? stdoutReaderTask;
    private Task? stderrReaderTask;
    private CancellationTokenSource? readerLoopCts;
    private StreamWriter? stdin;
    private JsonlWriter? stdinJsonlWriter;
    private bool forcedKillRequested;
    private long currentBridgeSpawnTicks;
    private int trackedBridgePid;
    private long trackedBridgeStartTimeUtcFileTime;

    public BridgeSupervisor(
        BridgeSupervisorCallbacks callbacks,
        Func<string> resolveNodePath,
        Func<string> resolveBridgePath,
        Func<string, string, bool, CancellationToken, Task> onStdoutLineAsync,
        Func<string, string, bool, CancellationToken, Task> onStderrLineAsync,
        Func<string> getCleanupReasonPrefix,
        Func<bool> isDisposed,
        Func<bool> isShuttingDown,
        Func<string> getReliabilityModeHint,
        Func<double?> getCurrentUptimeMs)
    {
        this.callbacks = callbacks;
        this.resolveNodePath = resolveNodePath;
        this.resolveBridgePath = resolveBridgePath;
        this.onStdoutLineAsync = onStdoutLineAsync;
        this.onStderrLineAsync = onStderrLineAsync;
        this.getCleanupReasonPrefix = getCleanupReasonPrefix;
        this.isDisposed = isDisposed;
        this.isShuttingDown = isShuttingDown;
        this.getReliabilityModeHint = getReliabilityModeHint;
        this.getCurrentUptimeMs = getCurrentUptimeMs;
    }

    public bool WasForcedKillRequested
    {
        get
        {
            lock (gate)
            {
                return forcedKillRequested;
            }
        }
    }

    public bool IsProcessRunning
    {
        get
        {
            lock (gate)
            {
                return process is not null && !process.HasExited;
            }
        }
    }

    public int? CurrentPid
    {
        get
        {
            lock (gate)
            {
                try
                {
                    return process is not null && !process.HasExited ? process.Id : null;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    public long CurrentSpawnTicks
    {
        get
        {
            lock (gate)
            {
                return currentBridgeSpawnTicks;
            }
        }
    }

    public BridgeProcessDebugState GetDebugStateForTests()
    {
        lock (gate)
        {
            return new BridgeProcessDebugState(
                HasProcessReference: process is not null,
                HasStdinReference: stdin is not null,
                HasStdoutReaderTaskReference: stdoutReaderTask is not null,
                HasStderrReaderTaskReference: stderrReaderTask is not null,
                TrackedPid: trackedBridgePid);
        }
    }

    public (StreamWriter Writer, JsonlWriter JsonlWriter, Process Process) GetActiveIoOrThrow()
    {
        lock (gate)
        {
            var w = stdin ?? throw new InvalidOperationException("NKN bridge is not running.");
            var jw = stdinJsonlWriter ?? throw new InvalidOperationException("NKN bridge is not running.");
            var p = process ?? throw new InvalidOperationException("NKN bridge is not running.");
            if (p.HasExited)
            {
                throw new InvalidOperationException("NKN bridge process is not available.");
            }

            return (w, jw, p);
        }
    }

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        Process? existing;
        lock (gate)
        {
            existing = process;
            if (existing is not null && !existing.HasExited && stdin is not null)
            {
                callbacks.Log($"double_spawn_prevented (reuse_existing_bridge pid={existing.Id})");
                callbacks.EmitBridgeLifecycle(new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Spawned,
                    BridgeStartMode.Warm,
                    existing.Id,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: getCurrentUptimeMs(),
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: string.Empty));
                return;
            }
        }

        var bridgePath = resolveBridgePath();
        var nodePath = resolveNodePath();

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
        var newJsonlWriter = new JsonlWriter(newStdin, leaveOpen: true);

        // Wait for old reader loops to stop before wiring the new process. This avoids
        // stale stdout/stderr lines from a previous bridge instance racing into startup.
        await CleanupStateAsync(waitForReaders: true, "start_replace").ConfigureAwait(false);

        var newReaderLoopCts = new CancellationTokenSource();

        lock (gate)
        {
            process = newProcess;
            stdin = newStdin;
            stdinJsonlWriter = newJsonlWriter;
            forcedKillRequested = false;
            currentBridgeSpawnTicks = Stopwatch.GetTimestamp();
            trackedBridgePid = newProcess.Id;
            trackedBridgeStartTimeUtcFileTime = SafeGetStartTimeUtcFileTime(newProcess);
            readerLoopCts = newReaderLoopCts;
        }

        ActiveRuntimeCounters.IncBridgeIoReaders();
        stdoutReaderTask = Task.Run(() => ReadStdoutLoopAsync(newProcess, newReaderLoopCts.Token), CancellationToken.None);
        ActiveRuntimeCounters.IncBridgeIoReaders();
        stderrReaderTask = Task.Run(() => ReadStderrLoopAsync(newProcess, newReaderLoopCts.Token), CancellationToken.None);

        NknRuntimeDiagnostics.SetBridgeProcessInfo(newProcess.Id, nodeVersion: null);
        callbacks.EmitBridgeLifecycle(new BridgeLifecycleEvent(
            BridgeLifecycleEventKind.Spawned,
            BridgeStartMode.Cold,
            newProcess.Id,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: null,
            ExitCode: null,
            ExitReasonKind: null,
            ExitReasonText: string.Empty));

        callbacks.Log($"Bridge process started (pid={newProcess.Id}, node={Path.GetFileName(nodePath)}, script={bridgePath})");
    }

    public async Task RequestShutdownAndCleanupAsync(Func<CancellationToken, Task> sendShutdownAsync, CancellationToken ct)
    {
        Process? processToClose;
        lock (gate)
        {
            processToClose = process;
        }

        if (processToClose is null)
        {
            return;
        }

        try
        {
            await sendShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            callbacks.Log($"Bridge shutdown command failed ({ex.GetType().Name})");
        }

        try
        {
            if (!processToClose.HasExited)
            {
                if (!await WaitForProcessExitAsync(processToClose, TimeSpan.FromMilliseconds(ShutdownWaitMilliseconds)).ConfigureAwait(false))
                {
                    callbacks.RecordBridgeFailure("bridge_shutdown_forced_kill", "Needed to close the local helper process.");
                    lock (gate)
                    {
                        forcedKillRequested = true;
                    }
                    processToClose.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            callbacks.Log($"Bridge process stop failed ({ex.GetType().Name})");
        }
        finally
        {
            try
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

                callbacks.Log($"Bridge shutdown complete (pid={pid}, exit_code={exitCodeText})");
            }
            catch
            {
                // ignore logging issues
            }

            await CleanupStateAsync(waitForReaders: true, "disconnect").ConfigureAwait(false);
        }
    }

    public void CleanupState()
    {
        var snapshot = DetachProcessStateSnapshot();
        CleanupDetachedProcessState(snapshot, waitForReaders: false, cleanupReason: "sync_cleanup");
    }

    public Task CleanupStateAsync(bool waitForReaders, string cleanupReason)
    {
        var snapshot = DetachProcessStateSnapshot();
        return CleanupDetachedProcessStateAsync(snapshot, waitForReaders, cleanupReason);
    }

    public void MarkForcedKillRequested()
    {
        lock (gate)
        {
            forcedKillRequested = true;
        }
    }

    public void ResetForcedKillFlag()
    {
        lock (gate)
        {
            forcedKillRequested = false;
        }
    }

    public void DetachReadersAndProcessForTestsCleanup()
    {
        CleanupState();
    }

    public void SetTrackedBridgeInfoForTests(int pid, long startTimeUtcFileTime)
    {
        lock (gate)
        {
            trackedBridgePid = pid;
            trackedBridgeStartTimeUtcFileTime = startTimeUtcFileTime;
        }
    }

    public (int pid, long startTimeUtcFileTime) GetTrackedBridgeInfoForTests()
    {
        lock (gate)
        {
            return (trackedBridgePid, trackedBridgeStartTimeUtcFileTime);
        }
    }

    internal static bool TryCleanupTrackedNodeProcessByPidForTests(int pid, long startTimeUtcFileTime)
    {
        return TryCleanupTrackedNodeProcessByPid(pid, startTimeUtcFileTime);
    }

    private async Task ReadStdoutLoopAsync(Process targetProcess, CancellationToken ct)
    {
        try
        {
            var reader = new JsonlReader(Encoding.UTF8);
            await reader.ReadLinesAsync(
                targetProcess.StandardOutput.BaseStream,
                (line, token) => onStdoutLineAsync(line, "stdout", false, token),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldIgnoreReaderLoopException(targetProcess, ex))
        {
            // Normal during shutdown / force-kill / cleanup disposal.
        }
        catch (Exception ex)
        {
            callbacks.Log($"Bridge stdout reader failed ({ex.GetType().Name})");
            callbacks.SignalDisconnected($"stdout_reader_failed:{ex.GetType().Name}");
        }
        finally
        {
            ActiveRuntimeCounters.DecBridgeIoReaders();
        }
    }

    private async Task ReadStderrLoopAsync(Process targetProcess, CancellationToken ct)
    {
        try
        {
            var reader = new JsonlReader(Encoding.UTF8);
            await reader.ReadLinesAsync(
                targetProcess.StandardError.BaseStream,
                (line, token) => onStderrLineAsync(line, "stderr", true, token),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldIgnoreReaderLoopException(targetProcess, ex))
        {
            // stderr is diagnostic only; shutdown can dispose streams while reading
        }
        catch
        {
            // stderr is diagnostic only
        }
        finally
        {
            ActiveRuntimeCounters.DecBridgeIoReaders();
        }
    }

    private void OnBridgeProcessExited(object? sender, EventArgs e)
    {
        var p = sender as Process;
        Process? currentProcess;
        int? exitCode = null;
        bool forcedKill;
        long spawnTicksSnapshot;
        int? pidSnapshot;
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

        lock (gate)
        {
            currentProcess = process;
            if (p is not null && !ReferenceEquals(p, currentProcess))
            {
                callbacks.Log($"stale_bridge_exit_ignored (pid={p.Id})");
                return;
            }

            forcedKill = forcedKillRequested;
            spawnTicksSnapshot = currentBridgeSpawnTicks;
            pidSnapshot = p is not null ? p.Id : (process is { } proc ? proc.Id : null);
        }

        var exitClassification = BridgeExitClassifier.Classify(isShuttingDown(), forcedKill, exitCode);
        var uptimeMs = ElapsedSinceTicksMilliseconds(spawnTicksSnapshot);

        callbacks.Log($"Bridge process exited (pid={(p?.Id.ToString() ?? "unknown")}, exit_code={(exitCode?.ToString() ?? "unknown")})");
        NknRuntimeDiagnostics.SetBridgeLastExit(exitCode, exitClassification.ReasonText);
        NknRuntimeDiagnostics.SetBridgeLastUptimeMs(uptimeMs);
        callbacks.EmitBridgeLifecycle(new BridgeLifecycleEvent(
            BridgeLifecycleEventKind.Exited,
            StartMode: null,
            Pid: pidSnapshot,
            ReadyTimeMs: null,
            PingRttMs: null,
            UptimeMs: uptimeMs,
            ExitCode: exitCode,
            ExitReasonKind: exitClassification.ReasonKind,
            ExitReasonText: exitClassification.ReasonText));
        callbacks.OnUnexpectedExitDetected(p is null || exitCode is null ? "bridge_process_exited" : $"bridge_process_exited:{exitCode.Value}");
    }

    private ProcessStateSnapshot DetachProcessStateSnapshot()
    {
        lock (gate)
        {
            var snapshot = new ProcessStateSnapshot(
                process,
                stdin,
                stdinJsonlWriter,
                stdoutReaderTask,
                stderrReaderTask,
                readerLoopCts,
                trackedBridgePid,
                trackedBridgeStartTimeUtcFileTime);

            process = null;
            stdin = null;
            stdinJsonlWriter = null;
            forcedKillRequested = false;
            currentBridgeSpawnTicks = 0;
            stdoutReaderTask = null;
            stderrReaderTask = null;
            readerLoopCts = null;
            trackedBridgePid = 0;
            trackedBridgeStartTimeUtcFileTime = 0;
            return snapshot;
        }
    }

    private void CleanupDetachedProcessState(ProcessStateSnapshot snapshot, bool waitForReaders, string cleanupReason)
    {
        try { snapshot.ReaderLoopCts?.Cancel(); } catch { }
        if (snapshot.Process is { } p)
        {
            TryKillDetachedBridgeProcessIfStillRunning(p, cleanupReason);
        }
        else
        {
            TryKillTrackedOrphanBridgeProcess(snapshot.TrackedPid, snapshot.TrackedStartTimeUtcFileTime, cleanupReason);
        }

        try { snapshot.JsonlWriter?.Dispose(); } catch { }
        try { snapshot.Stdin?.Dispose(); } catch { }
        try { snapshot.ReaderLoopCts?.Dispose(); } catch { }
        if (waitForReaders)
        {
            // Sync cleanup path is best-effort only.
        }

        if (snapshot.Process is not null)
        {
            try { snapshot.Process.Exited -= OnBridgeProcessExited; } catch { }
            try { snapshot.Process.Dispose(); } catch { }
        }
    }

    private async Task CleanupDetachedProcessStateAsync(ProcessStateSnapshot snapshot, bool waitForReaders, string cleanupReason)
    {
        try { snapshot.ReaderLoopCts?.Cancel(); } catch { }
        if (snapshot.Process is { } p)
        {
            await TryKillDetachedBridgeProcessIfStillRunningAsync(p, cleanupReason).ConfigureAwait(false);
        }
        else
        {
            await TryKillTrackedOrphanBridgeProcessAsync(snapshot.TrackedPid, snapshot.TrackedStartTimeUtcFileTime, cleanupReason).ConfigureAwait(false);
        }

        try { snapshot.JsonlWriter?.Dispose(); } catch { }
        try { snapshot.Stdin?.Dispose(); } catch { }
        try { snapshot.ReaderLoopCts?.Dispose(); } catch { }
        if (waitForReaders)
        {
            await WaitForReaderLoopsAsync(snapshot.StdoutReaderTask, snapshot.StderrReaderTask, TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }

        if (snapshot.Process is not null)
        {
            try { snapshot.Process.Exited -= OnBridgeProcessExited; } catch { }
            try { snapshot.Process.Dispose(); } catch { }
        }
    }

    private void TryKillDetachedBridgeProcessIfStillRunning(Process processToKill, string cleanupReason)
    {
        try
        {
            if (processToKill.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        try
        {
            callbacks.Log($"Cleaning bridge process during {cleanupReason} (pid={processToKill.Id})");
            lock (gate)
            {
                forcedKillRequested = true;
            }
            processToKill.Kill(entireProcessTree: true);
            processToKill.WaitForExit(ShutdownWaitMilliseconds);
        }
        catch (Exception ex)
        {
            callbacks.Log($"Bridge cleanup kill failed ({ex.GetType().Name})");
        }
    }

    private void TryKillTrackedOrphanBridgeProcess(int pid, long startTimeUtcFileTime, string cleanupReason)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            if (TryCleanupTrackedNodeProcessByPid(pid, startTimeUtcFileTime))
            {
                callbacks.Log($"Cleaning orphan bridge process by tracked pid (pid={pid}, reason={cleanupReason})");
                lock (gate)
                {
                    forcedKillRequested = true;
                }
            }
        }
        catch (Exception ex)
        {
            callbacks.Log($"Orphan bridge cleanup failed ({ex.GetType().Name})");
        }
    }

    private static bool TryCleanupTrackedNodeProcessByPid(int pid, long startTimeUtcFileTime)
    {
        if (pid <= 0)
        {
            return false;
        }

        Process? orphan = null;
        try
        {
            orphan = Process.GetProcessById(pid);
            if (orphan.HasExited)
            {
                return false;
            }

            if (startTimeUtcFileTime > 0)
            {
                var actualStart = SafeGetStartTimeUtcFileTime(orphan);
                if (actualStart > 0 && actualStart != startTimeUtcFileTime)
                {
                    return false;
                }
            }

            var processName = SafeGetProcessName(orphan);
            if (!processName.StartsWith("node", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            orphan.Kill(entireProcessTree: true);
            orphan.WaitForExit(ShutdownWaitMilliseconds);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            try { orphan?.Dispose(); } catch { }
        }
    }

    private static async Task WaitForReaderLoopsAsync(Task? stdoutTask, Task? stderrTask, TimeSpan timeout)
    {
        var tasks = new System.Collections.Generic.List<Task>(2);
        if (stdoutTask is not null)
        {
            tasks.Add(stdoutTask);
        }

        if (stderrTask is not null)
        {
            tasks.Add(stderrTask);
        }

        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private bool ShouldIgnoreReaderLoopException(Process targetProcess, Exception ex)
    {
        if (ex is not IOException && ex is not ObjectDisposedException && ex is not InvalidOperationException)
        {
            return false;
        }

        if (isDisposed())
        {
            return true;
        }

        if (isShuttingDown())
        {
            return true;
        }

        try
        {
            return targetProcess.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static long SafeGetStartTimeUtcFileTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().ToFileTimeUtc();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(Process processToWait, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await processToWait.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task TryKillDetachedBridgeProcessIfStillRunningAsync(Process processToKill, string cleanupReason)
    {
        try
        {
            if (processToKill.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        try
        {
            callbacks.Log($"Cleaning bridge process during {cleanupReason} (pid={processToKill.Id})");
            lock (gate)
            {
                forcedKillRequested = true;
            }

            processToKill.Kill(entireProcessTree: true);
            _ = await WaitForProcessExitAsync(processToKill, TimeSpan.FromMilliseconds(ShutdownWaitMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            callbacks.Log($"Bridge cleanup kill failed ({ex.GetType().Name})");
        }
    }

    private async Task TryKillTrackedOrphanBridgeProcessAsync(int pid, long startTimeUtcFileTime, string cleanupReason)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            if (await TryCleanupTrackedNodeProcessByPidAsync(pid, startTimeUtcFileTime).ConfigureAwait(false))
            {
                callbacks.Log($"Cleaning orphan bridge process by tracked pid (pid={pid}, reason={cleanupReason})");
                lock (gate)
                {
                    forcedKillRequested = true;
                }
            }
        }
        catch (Exception ex)
        {
            callbacks.Log($"Orphan bridge cleanup failed ({ex.GetType().Name})");
        }
    }

    private static async Task<bool> TryCleanupTrackedNodeProcessByPidAsync(int pid, long startTimeUtcFileTime)
    {
        if (pid <= 0)
        {
            return false;
        }

        Process? orphan = null;
        try
        {
            orphan = Process.GetProcessById(pid);
            if (orphan.HasExited)
            {
                return false;
            }

            if (startTimeUtcFileTime > 0)
            {
                var actualStart = SafeGetStartTimeUtcFileTime(orphan);
                if (actualStart > 0 && actualStart != startTimeUtcFileTime)
                {
                    return false;
                }
            }

            var processName = SafeGetProcessName(orphan);
            if (!processName.StartsWith("node", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            orphan.Kill(entireProcessTree: true);
            _ = await WaitForProcessExitAsync(orphan, TimeSpan.FromMilliseconds(ShutdownWaitMilliseconds)).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            try { orphan?.Dispose(); } catch { }
        }
    }

    private static double? ElapsedSinceTicksMilliseconds(long startTicks)
    {
        if (startTicks <= 0)
        {
            return null;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        if (elapsedTicks < 0)
        {
            elapsedTicks = 0;
        }

        return elapsedTicks * 1000d / Stopwatch.Frequency;
    }

    private sealed record ProcessStateSnapshot(
        Process? Process,
        StreamWriter? Stdin,
        JsonlWriter? JsonlWriter,
        Task? StdoutReaderTask,
        Task? StderrReaderTask,
        CancellationTokenSource? ReaderLoopCts,
        int TrackedPid,
        long TrackedStartTimeUtcFileTime);
}

internal sealed class BridgeSupervisorCallbacks
{
    public required Action<string> Log { get; init; }
    public required Action<string> SignalDisconnected { get; init; }
    public required Action<string> OnUnexpectedExitDetected { get; init; }
    public required Action<string, string?> RecordBridgeFailure { get; init; }
    public required Action<BridgeLifecycleEvent> EmitBridgeLifecycle { get; init; }
}
