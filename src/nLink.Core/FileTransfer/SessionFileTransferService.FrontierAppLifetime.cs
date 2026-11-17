using System.Diagnostics;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Process-owned, not service-owned: desktop/service disposal precedes the
    // final operational-log drain. No imported catalog or manifest is accepted.
    internal sealed partial class FrontierAppLifetime
    {
        private static int startAttempted;
        private static FrontierAppLifetime? current;
        private static FrontierAppLifetime? serviceOwner;
        private readonly FrontierProducerRegistry producers = new();
        private readonly object admissionGate = new();
        private readonly FrontierAppLifetime original;
        private readonly ManualResetEvent stopping = new(false);
        private readonly Task setup;
        private LocalFrontierAppRequest.AppLease? lease;
        private LocalFrontierAppRequest.FileHandoff? handoff;
        private FrontierPayloadCatalog? catalog;
        private bool ready;
        private int finishAttempted;
        private bool admissionRejected;

        internal static FrontierAppLifetime? Current => Volatile.Read(ref current);
        // Keep the original closed owner as a tombstone: a late app factory
        // must not silently become an untracked standalone service.
        internal static FrontierAppLifetime? ServiceOwner => Volatile.Read(ref serviceOwner);

        // Diagnostic only; this is neither finalizer custody nor live proof.
        internal bool HandoffCompleted { get; private set; }

        private FrontierAppLifetime()
        {
            original = this;
            setup = Task.Run(SetUp);
        }

        internal static FrontierAppLifetime? StartFromEnvironment()
        {
            if (!OperatingSystem.IsWindows() ||
                Environment.GetEnvironmentVariable("NLINK_APP_FRONTIER_START_V1") != "1" ||
                Interlocked.Exchange(ref startAttempted, 1) != 0) { return null; }
            var lifetime = new FrontierAppLifetime();
            Volatile.Write(ref serviceOwner, lifetime);
            Volatile.Write(ref current, lifetime);
            return lifetime;
        }

        private void SetUp()
        {
            try
            {
                lease = LocalFrontierAppRequest.TryAttachReadyFromEnvironment(
                    Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID")!,
                    Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID")!, stopping);
                if (lease is null || !lease.WaitForFileHandoffReady(stopping) || stopping.WaitOne(0)) { return; }
                catalog = FrontierPayloadCatalog.TryCreateNativeForApp(lease, producers);
                handoff = catalog?.TryAttachFileHandoff();
                if (handoff is null || stopping.WaitOne(0) || !catalog!.TryBindOperationalWriter()) { return; }
                lock (admissionGate) { ready = !admissionRejected && !stopping.WaitOne(0); }
            }
            catch { /* Optional diagnostic setup cannot prevent app startup. */ }
            finally { if (!ready) { Release(); } }
        }

        // The caller must invoke this in its original process-exit finally.
        // Joining setup happens only after waking its native idle wait. The UI
        // thread never waits for the other app to launch during startup.
        internal static bool CompleteAndFlush(FrontierAppLifetime? lifetime, TimeSpan timeout)
            => lifetime is null ? LocalOperationalLog.CompleteAndFlush(timeout) : lifetime.Finish(timeout);

        private bool Finish(TimeSpan timeout)
        {
            if (!ReferenceEquals(original, this) || Interlocked.Exchange(ref finishAttempted, 1) != 0) { return false; }
            Interlocked.CompareExchange(ref current, null, this);
            FrontierPayloadCatalog.PreparedCatalog? positive = null;
            FrontierPayloadCatalog.WriterOnlyContribution? writerOnly = null;
            try
            {
                bool flushed;
                try
                {
                    StopNativeDestinationHandoff();
                    stopping.Set();
                    setup.GetAwaiter().GetResult();
                    FrontierPayloadCatalog? admitted;
                    lock (admissionGate) { admitted = ready && !admissionRejected ? catalog : null; }
                    var readiness = Stopwatch.StartNew();
                    var producersReady = producers.SealAndWait(timeout);
                    var remaining = timeout - readiness.Elapsed;
                    if (!producersReady) { admitted?.Cancel(); }
                    if (producersReady && admitted?.PrepareForDrain() == true &&
                        admitted.WaitForCompletionWrites(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                    {
                        if (admitted.AdmissionCount == 0) { writerOnly = admitted.TryTakeWriterOnly(); }
                        else
                        {
                            positive = admitted.TryTake(out _);
                            if (positive?.TrySubmitMarkers() != true) { positive = null; admitted.Cancel(); }
                        }
                    }
                }
                catch { Release(); }
                finally
                {
                    // Exactly the ordinary final drain, even if optional setup
                    // or preparation failed. No diagnostic write follows it.
                    flushed = LocalOperationalLog.CompleteAndFlush(timeout);
                }
                try
                {
                    if (flushed && (positive?.TrySubmitFiles() ?? writerOnly?.TrySubmitFiles() ?? false) &&
                        handoff!.WaitForAdoption() && handoff.TryTakeAcknowledgment())
                    { HandoffCompleted = lease!.CompleteAttachment(); }
                }
                catch { /* Fail closed; retain the ordinary drain result. */ }
                return flushed;
            }
            finally { Release(); stopping.Dispose(); }
        }

        private void Release()
        {
            StopNativeDestinationHandoff();
            lock (admissionGate) { nativeDestinationEvidence?.Dispose(); nativeSenderSelection?.Dispose(); }
            try { catalog?.Dispose(); }
            finally
            {
                try { handoff?.Dispose(); }
                finally { lease?.Dispose(); }
            }
        }

        internal FrontierProducerRegistry.ServiceScope? RegisterService()
        {
            if (!ReferenceEquals(original, this)) { return null; }
            var scope = producers.RegisterService();
            if (scope is null)
            {
                // App construction is outside service/registry gates. A late
                // factory must revoke even a submission already awaiting adoption.
                handoff?.Cancel();
                catalog?.Cancel();
            }
            return scope;
        }

        // Called only outside the service gate. Readiness alone cannot supply
        // an observation: the original native-bound writer/catalog admits it.
        // Opaque outside the containing service because the producer type stays
        // private. This issues only a new original observation, never imports one.
        internal object? TryAdmitService(object serviceGate)
        {
            lock (admissionGate)
            {
                if (!ReferenceEquals(original, this) || Volatile.Read(ref finishAttempted) != 0) { return null; }
                if (ready && !admissionRejected)
                { return catalog!.TryAdmitService(serviceGate, out _); }
                admissionRejected = true;
                stopping.Set();
                catalog?.Cancel();
                return null;
            }
        }
    }

    private sealed partial class FrontierPayloadCatalog
    {
        // One total readiness budget for the closed catalog, not per producer.
        // Failure is diagnostic-only; the caller still performs its ordinary drain.
        public bool WaitForCompletionWrites(TimeSpan timeout)
        {
            FrontierBoundaryObservation[] observations;
            lock (gate)
            {
                if (CurrentFailure().Length != 0 || !admissionClosed || taken || preparing ||
                    slots.Any(s => s.Payload is null)) { return false; }
                observations = slots.Select(s => s.Observation).ToArray();
            }
            var clock = Stopwatch.StartNew();
            foreach (var observation in observations)
            {
                var remaining = timeout - clock.Elapsed;
                if (!observation.WaitForCompletionWrite(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                { lock (gate) { Invalidate("completion_write_not_ready"); } return false; }
            }
            lock (gate) { return CurrentFailure().Length == 0; }
        }

        // Close admission before encoding, outside the service gate. Preparing
        // an incomplete/invalid original observation still invalidates the set.
        public bool PrepareForDrain()
        {
            FrontierBoundaryObservation[] remaining;
            lock (gate)
            {
                if (CurrentFailure().Length != 0 || taken || admissionClosed) { return false; }
                admissionClosed = true;
                if (preparing || slots.Any(s => s.Attempted && s.Payload is null))
                { Invalidate("catalog_incomplete"); return false; }
                remaining = slots.Where(s => !s.Attempted).Select(s => s.Observation).ToArray();
            }
            foreach (var observation in remaining) { observation.CloseForDrain(); }
            foreach (var observation in remaining)
            { if (!TryPrepare(observation, out _)) { return false; } }
            lock (gate) { return CurrentFailure().Length == 0; }
        }
    }
}
