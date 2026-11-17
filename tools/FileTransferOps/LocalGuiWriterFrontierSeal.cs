using System;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            private bool fileSealAttempted;
            private FrontierSealedFiles sealedFiles;

            public FrontierSealedFiles TrySealFiles()
            {
                LocalFrontierFileAdoption adoption;
                lock (sync)
                {
                    if (!IsCurrent) return null;
                    if (fileSealAttempted || fileAdoption == null) { Dispose(); return null; }
                    fileSealAttempted = true; adoption = fileAdoption;
                }
                // Hashing holds no GUI/service/catalog/logging lock. The adopter
                // rechecks original closure under its existing cumulative budget.
                var originHandle = ledgerOrigin == null ? null : ledgerOrigin.OriginalHandle;
                if ((ledgerOrigin != null && originHandle == null) || !adoption.TrySealSource(FinalSealScopeCurrent, originHandle)) { Dispose(); return null; }
                lock (sync)
                {
                    if (!IsCurrent || !FinalSealScopeCurrent() || !adoption.IsSourceVerified) { Dispose(); return null; }
                    sealedFiles = new FrontierSealedFiles(this, adoption.SealedLength, adoption.SealedHash, adoption.SealedRangeCount);
                    return sealedFiles;
                }
            }

            private bool FinalSealScopeCurrent()
            {
                try
                {
                    return FileHandoffScopeCurrent() && FinalSealAppsCurrent() && FileHandoffScopeCurrent();
                }
                catch { return false; }
            }
            private bool FinalSealAppsCurrent()
            {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                if (fileChannels == null || fileRequests == null || fileRequests.Length != 2 || apps.Count != 2) return false;
                for (int i = 0; i < 2; i++)
                {
                    var request = fileRequests[i]; var launch = request.launch; int code;
                    if (!appRequests.Contains(request) || !apps.Contains(launch) ||
                        fileChannels[i].State != LocalFrontierFileChannel.Acknowledged || !request.native.GuiAttachmentCompleted ||
                        !launch.Exit.WaitOne(0) || !GetExitCodeProcess(launch.Exit.SafeWaitHandle, out code) || code != 0 ||
                        launch.Output == null || !launch.Output.Drained) return false;
                }
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                NLink.Tests.NativeContentParserProfileForTest.ReaderStage(16, sample);
#endif
                return true;
            }
            // Null selects the unchanged non-processing path. Once selected,
            // failure is final; never retry through recursive currentness.
            internal bool? CheckProcessingFiles(Finalizer finalizer, FrontierFinalizerBinding binding, FrontierSealedFiles value)
            {
                lock (sync)
                {
                    try
                    {
                        var reader = binding.ProcessingReaderFor(finalizer, value);
                        var adoption = fileAdoption;
                        if (reader == null || adoption == null || !adoption.ProcessingRunning) return null;
#if NLINK_TEST_BUFFERED_LEDGER
                        NLink.Tests.ProcessingCustodyProbe.Boundary("processing-open");
#endif
                        if (!Object.ReferenceEquals(sealedFiles, value) || parentRegistration != null ||
                            !finalizer.ProcessingScopeCurrent(this, binding, value, reader) || !adoption.BufferedWorkCurrent(reader) ||
                            !FileHandoffScopeCurrent() || !FinalSealAppsCurrent()) return false;
#if NLINK_TEST_BUFFERED_LEDGER
                        NLink.Tests.ProcessingCustodyProbe.Boundary("processing-files");
#endif
                        if (!adoption.ProcessingFilesCurrent(reader)) return false;
#if NLINK_TEST_BUFFERED_LEDGER
                        NLink.Tests.ProcessingCustodyProbe.Boundary("processing-close");
#endif
                        if (!FileHandoffScopeCurrent() || !FinalSealAppsCurrent()) return false;
#if NLINK_TEST_BUFFERED_LEDGER
                        NLink.Tests.ProcessingCustodyProbe.Boundary("processing-return");
#endif
                        return Object.ReferenceEquals(sealedFiles, value) && Object.ReferenceEquals(fileAdoption, adoption) && parentRegistration == null &&
                            adoption.BufferedWorkCurrent(reader) && finalizer.ProcessingScopeCurrent(this, binding, value, reader);
                    }
                    catch { return false; }
                }
            }
            internal bool OwnsSealedFiles(FrontierSealedFiles value)
            {
                lock (sync)
                {
                    return Object.ReferenceEquals(sealedFiles, value) && IsCurrent && FinalSealScopeCurrent() &&
                        fileAdoption != null && fileAdoption.IsSourceVerified;
                }
            }
            internal FrontierSealedFiles GetOriginalSealedFiles()
            {
                lock (sync) { return sealedFiles != null && OwnsSealedFiles(sealedFiles) ? sealedFiles : null; }
            }
            internal LocalFrontierFileAdoption GetSealedAdoption(FrontierSealedFiles value)
            { lock (sync) { return OwnsSealedFiles(value) ? fileAdoption : null; } }
            internal void DisposeSealedFiles(FrontierSealedFiles value)
            { lock (sync) { if (Object.ReferenceEquals(sealedFiles, value)) Dispose(); } }
        }

        // Original GUI capability retained by the finalizer binding. Scalars are
        // diagnostic only; no file handle export or publication is enabled here.
        public sealed class FrontierSealedFiles : IDisposable
        {
            private readonly FrontierLaunchOwner owner;
            internal FrontierSealedFiles(FrontierLaunchOwner owner, long bytes, string hash, int ranges)
            { this.owner = owner; SourceBytes = bytes; SourceSha256 = hash; VerifiedRangeCount = ranges; }
            public long SourceBytes { get; private set; }
            public string SourceSha256 { get; private set; }
            public int VerifiedRangeCount { get; private set; }
            public bool IsCurrent { get { return owner.OwnsSealedFiles(this); } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeSealedFiles(this); }
            internal LocalFrontierFileAdoption GetOriginalAdoption() { return owner.GetSealedAdoption(this); }
            internal FrontierLedgerOrigin GetOriginalLedgerOrigin() { return owner.GetSealedLedgerOrigin(this); }
            internal FrontierAppLaunch[] GetOriginalRecoveryLaunches() { return owner.GetRecoveryLaunches(this); }
        }
    }
}
