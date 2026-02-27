using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Resources;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public sealed class ResourceRuntimeTracker : IDisposable
{
    private readonly ResourceSampler sampler;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();
    private ResourceSnapshot? lastSnapshot;
    private ResourceSnapshot? peakSnapshot;
    private Task? loopTask;
    private bool started;
    private bool disposed;

    public ResourceRuntimeTracker(TimeSpan? interval = null)
    {
        this.interval = interval.GetValueOrDefault(TimeSpan.FromSeconds(1));
        sampler = new ResourceSampler(() =>
        {
            var pid = NknRuntimeDiagnostics.Snapshot().BridgePid;
            return pid > 0 ? pid : null;
        });
    }

    public void Start()
    {
        lock (gate)
        {
            if (disposed || started)
            {
                return;
            }

            started = true;
            loopTask = Task.Run(() => RunLoopAsync(cts.Token));
        }
    }

    public ResourceSnapshot? GetLastSnapshot()
    {
        lock (gate)
        {
            return lastSnapshot;
        }
    }

    public ResourceSnapshot? GetPeakSnapshot()
    {
        lock (gate)
        {
            return peakSnapshot;
        }
    }

    public string? TryReadLatestResourceSummary()
        => TryReadLatestSummaryLike("resource-summary.txt");

    public string? TryReadLatestLeakCheckSummary()
        => TryReadLatestSummaryLike("leak-check-summary.txt");

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            CaptureAndStore();
            using var timer = new PeriodicTimer(interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                CaptureAndStore();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Diagnostics helper only; never crash the app.
        }
    }

    private void CaptureAndStore()
    {
        ResourceSnapshot snapshot;
        try
        {
            snapshot = sampler.Capture();
        }
        catch
        {
            return;
        }

        lock (gate)
        {
            lastSnapshot = snapshot;
            if (peakSnapshot is null || Score(snapshot) >= Score(peakSnapshot))
            {
                peakSnapshot = snapshot;
            }
        }
    }

    private static double Score(ResourceSnapshot s)
        => s.App.WorkingSetMB + (s.Bridge?.WorkingSetMB ?? 0d);

    private static string? TryReadLatestSummaryLike(string fileName)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine("artifacts", "resources"));
            if (!Directory.Exists(root))
            {
                return null;
            }

            var latest = Directory.GetFiles(root, "*.txt", SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null)
            {
                return null;
            }

            return File.ReadAllText(latest);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        cts.Cancel();
        try
        {
            loopTask?.Wait(250);
        }
        catch
        {
        }
        cts.Dispose();
    }
}

