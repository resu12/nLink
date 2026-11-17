# Exact production wait method with real local wait handles and a modeled owner.
# This checks scheduling/bounds, not native process or file custody.
$waitSource=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterFrontierFiles.cs'))
$waitMethod=[regex]::Match($waitSource,'(?s)public bool WaitForAppExitSignals\(int timeoutMilliseconds\).*?(?=\s+private bool FileAdoptionWithinDeadline)')
if(-not $waitMethod.Success) {throw 'Original exit-signal wait missing'}
$waitModel=@'
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
public sealed class ExitSignalWaitModel {
    readonly object sync = new object();
    bool disposed, appExitSignalWaitAttempted;
    bool sealedLaunches = true, fileAdoptionWaitAttempted = true;
    bool[] fileAdoptAttempted = {true, true};
    EventWaitHandle closed = new ManualResetEvent(false), parentExit = new ManualResetEvent(false);
    class Channel { public EventWaitHandle cancelled = new ManualResetEvent(false); }
    class Launch { public EventWaitHandle Exit = new ManualResetEvent(false); }
    class Request { public Launch launch = new Launch(); }
    Channel[] fileChannels = {new Channel(), new Channel()};
    Request[] fileRequests = {new Request(), new Request()};
    bool IsCurrent { get { lock(sync) {return !disposed && !closed.WaitOne(0) && !parentExit.WaitOne(0) &&
        !fileChannels[0].cancelled.WaitOne(0) && !fileChannels[1].cancelled.WaitOne(0);} } }
    void Dispose() {lock(sync) {disposed = true;} }
    public static bool Check(string mode) {
        var model = new ExitSignalWaitModel();
        Task<bool> waiting = null;
        try {
            if (mode == "zero-budget") return !model.WaitForAppExitSignals(0) && model.disposed;
            if (mode == "before-adoption") {
                model.fileAdoptAttempted[1] = false;
                return !model.WaitForAppExitSignals(2000) && model.disposed;
            }
            if (mode == "timeout") {
                var clock = Stopwatch.StartNew();
                bool result = model.WaitForAppExitSignals(100);
                return !result && model.disposed && clock.ElapsedMilliseconds >= 90;
            }
            waiting = Task.Run(() => model.WaitForAppExitSignals(2000));
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref model.appExitSignalWaitAttempted), 1000)) return false;
            // Another thread can take the ownership lock while native exit
            // signals are absent. Cancellation does not wait on that lock.
            if (!Monitor.TryEnter(model.sync, 500)) return false;
            try { if (waiting.IsCompleted) return false; }
            finally { Monitor.Exit(model.sync); }
            if (mode == "cancel") model.closed.Set();
            else if (mode == "parent-exit") model.parentExit.Set();
            else if (mode == "channel-cancel") model.fileChannels[1].cancelled.Set();
            else {
                model.fileRequests[0].launch.Exit.Set();
                if (waiting.IsCompleted) return false;
                model.fileRequests[1].launch.Exit.Set();
            }
            if (!waiting.Wait(1500)) return false;
            if (mode != "both-exits") return !waiting.Result && model.disposed;
            return waiting.Result && model.IsCurrent && !model.WaitForAppExitSignals(2000) && model.IsCurrent;
        }
        finally {
            model.closed.Set();
            if (waiting != null) waiting.Wait(2500);
            model.Dispose(); model.closed.Dispose(); model.parentExit.Dispose();
            foreach (var channel in model.fileChannels) channel.cancelled.Dispose();
            foreach (var request in model.fileRequests) request.launch.Exit.Dispose();
        }
    }
'@
Add-Type -TypeDefinition ($waitModel+$waitMethod.Value+"`n}")
foreach($mode in @('both-exits','cancel','parent-exit','channel-cancel','timeout','zero-budget','before-adoption')) {
    if(-not [ExitSignalWaitModel]::Check($mode)) {throw "Exit signal wait failed: $mode"}
}
'exit-signal-wait-ok'
