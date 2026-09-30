using System.IO.Pipes;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private Task TryRequestAutomaticNativeDestinationAsync(InboundTransferContext context, InboundFinalizationSource source)
    {
        if (frontierAppLifetime?.CanonicalDestinationRequested != true || source.Destination is null)
            return Task.CompletedTask;
        // Finalization can be invoked from a caller holding the service gate.
        // Native lease validation must execute only after that caller releases it.
        return Task.Run(() =>
        {
            try { _ = TryRequestNativeDestinationEvidenceCore(source.Destination, context, source); }
            catch { /* Evidence admission cannot change the transfer outcome. */ }
        });
    }

    internal sealed partial class FrontierAppLifetime
    {
        private readonly bool canonicalDestinationRequested =
            Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_DESTINATION_V1") == "1";
        private readonly CancellationTokenSource nativeDestinationStopping = new();
        private Task<bool>? nativeDestinationHandoff;
        private bool nativeDestinationStopped;

        internal bool CanonicalDestinationRequested => ReferenceEquals(original, this) && canonicalDestinationRequested;

        internal void TryStartNativeDestinationHandoff(NativeDestinationEvidence? evidence)
        {
            lock (admissionGate)
            {
                if (!CanonicalDestinationRequested || !ready || admissionRejected || nativeDestinationStopped ||
                    evidence is null || !OwnsNativeDestinationEvidence(evidence) || !evidence.IsCompletedForApp(this) ||
                    nativeDestinationHandoff is not null) return;
                var token = nativeDestinationStopping.Token;
                // This worker owns no route or progress mutation. It transports
                // the already-completed native hold, outside the service gate.
                nativeDestinationHandoff = Task.Run(() => evidence.TryHandoffToOriginalGuiAsync(token));
            }
        }

        private void StopNativeDestinationHandoff()
        {
            Task<bool>? pending;
            CancellationTokenSource? cancel = null;
            lock (admissionGate)
            {
                if (!nativeDestinationStopped)
                {
                    nativeDestinationStopped = true;
                    cancel = nativeDestinationStopping;
                }
                pending = nativeDestinationHandoff;
            }
            // Never join while holding the admission or service gate.
            try
            {
                cancel?.Cancel();
                pending?.GetAwaiter().GetResult();
            }
            finally { cancel?.Dispose(); }
        }
    }

    internal sealed partial class NativeDestinationEvidence
    {
        private bool automaticHandoffAttempted;

        internal bool IsCompletedForApp(FrontierAppLifetime candidate)
        {
            lock (sync) return ReferenceEquals(original, this) && ReferenceEquals(app, candidate) && !retired && file is not null;
        }

        internal async Task<bool> TryHandoffToOriginalGuiAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows()) return false;
            bool accepted = false, claimed = false;
            try
            {
                lock (sync)
                {
                    if (!ReferenceEquals(original, this) || automaticHandoffAttempted || guiHandoffAttempted ||
                        file is null || !IsCurrentLocked()) return false;
                    automaticHandoffAttempted = true;
                    claimed = true;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Keep the existing handoff ceiling, including connection setup.
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var pipe = new NamedPipeClientStream(".", "nlink-destination-" + lease.RequestId.ToString("N"),
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                accepted = await TryHandoffToGuiAsync(service, destination, lease, pipe, deadline.Token).ConfigureAwait(false);
                return accepted;
            }
            catch { return false; } // Optional custody failure is not transfer failure.
            finally { if (claimed && !accepted) Dispose(); }
        }
    }
}
