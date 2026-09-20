using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed partial class FrontierPayloadCatalog
    {
        private bool fileHandoffAttempted;
        private LocalFrontierAppRequest.FileHandoff? fileHandoff;

        // Attach before writer registration/admission. Only this original
        // channel can select provisional retention; a failed attach never
        // falls back to freezing a potentially shared source.
        public LocalFrontierAppRequest.FileHandoff? TryAttachFileHandoff()
        {
            if (!OperatingSystem.IsWindows()) { return null; }
            LocalFrontierAppRequest.AppLease lease;
            lock (gate)
            {
                if (fileHandoffAttempted || writerBindingAttempted || admissions != 0 || taken ||
                    nativeLease is null || CurrentFailure().Length != 0) { return null; }
                fileHandoffAttempted = true; lease = nativeLease;
            }
            bool Current() { lock (gate) { return CurrentFailure().Length == 0; } }
            // Native endpoint work is outside the catalog/service/logging gates.
            var handoff = lease.TryAttachFileHandoff(this, Current);
            lock (gate)
            {
                if (handoff is not null && Current()) { return fileHandoff = handoff; }
            }
            handoff?.Dispose(); return null;
        }
    }
}
