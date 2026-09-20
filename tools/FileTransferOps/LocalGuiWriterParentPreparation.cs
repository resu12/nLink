using System;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    internal sealed partial class LocalFrontierFileAdoption
    {
        internal bool PinParentInputs(LocalFrontierPreparedFiles target, out LocalFrontierFileManifest first, out LocalFrontierFileManifest second)
        {
            first = null; second = null;
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || !IsSourceVerified || adopted.Count != 2 ||
                    processingRunning || !sourceReadEnded || !target.Add(0, sealedSource.SafeFileHandle, sealedLength, -1, null)) return false;
                for (int slot = 0; slot < 2; slot++)
                {
                    var item = adopted.Find(c => c.Manifest.AppRequest == requests[slot]);
                    if (item == null) return false;
                    if (slot == 0) first = item.Manifest; else second = item.Manifest;
                    for (int i = 0; i < item.Manifest.Payloads.Length; i++)
                        if (!target.Add(10, item.Files[i + 1].SafeFileHandle, item.Manifest.Payloads[i].File.Length, slot, item.Manifest.Payloads[i])) return false;
                }
                return IsSourceVerified;
            }
        }
    }

    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentPreparationAttempted;
            private FrontierParentPreparation parentPreparation;
            public FrontierParentPreparation TryPrepareParentFiles()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false; LocalFrontierPreparedFiles pins = null;
                    try
                    {
                        if (parentPreparationAttempted) return null; parentPreparationAttempted = true;
                        if (!OwnsParentHandoff(parentHandoff) || sourceReader == null || sourceReader.ProcessingWorkActive) return null;
                        var adoption = files.GetOriginalAdoption(); var launches = files.GetOriginalRecoveryLaunches();
                        if (adoption == null || launches == null || launches.Length != 2) return null;
                        var capture = cursorMatch.Result.StreamCapture;
                        pins = new LocalFrontierPreparedFiles(adoption.FinalizationLifetime(sourceReader));
                        // Resource methods in this callback take only resource
                        // locks. Never re-enter GUI/finalizer ownership locks.
                        if (!adoption.TryRunRecoveryRead(() =>
                        {
                            LocalFrontierFileManifest first, second;
                            if (!adoption.PinParentInputs(pins, out first, out second) ||
                                !capture.PinParentOutputs(pins, semanticCopy.Output) || !persistedEvidence.Resource.PinParentEvidence(pins)) return false;
                            for (int i = 0; i < 4; i++)
                                if (!launches[i / 2].Output.RecoveryOutputs[i % 2].PinParentOutput(pins, 6 + i)) return false;
                            return pins.Seal(first, second);
                        })) return null;
                        parentPreparation = new FrontierParentPreparation(this, parentHandoff, persistedEvidence, cursorMatch, recoveryMarkers, capture, pins);
                        success = OwnsParentPreparation(parentPreparation); return success ? parentPreparation : null;
                    }
                    catch { return null; }
                    finally { if (!success) { Dispose(); if (pins != null) pins.Dispose(); } }
                }
            }
            internal bool OwnsParentPreparation(FrontierParentPreparation value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(parentPreparation, value)) return false;
                    if (Object.ReferenceEquals(value.Handoff, parentHandoff) && OwnsParentHandoff(parentHandoff) &&
                        Object.ReferenceEquals(value.Evidence, persistedEvidence) && Object.ReferenceEquals(value.Cursor, cursorMatch) &&
                        Object.ReferenceEquals(value.Markers, recoveryMarkers) && Object.ReferenceEquals(value.Capture, cursorMatch.Result.StreamCapture) &&
                        value.Files.IsCurrent) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposeParentPreparation(FrontierParentPreparation value)
            {
                lock (sourceSync)
                { if (Object.ReferenceEquals(parentPreparation, value)) { Dispose(); value.Files.Dispose(); } }
            }
        }
        // Preparation only: all native pins and original immutable facts stay
        // in the GUI. No parent adoption, acknowledgment or publication follows.
        public sealed class FrontierParentPreparation : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            internal readonly FrontierParentHandoff Handoff;
            internal readonly FrontierPersistedEvidence Evidence;
            internal readonly FrontierCursorMatch Cursor;
            internal readonly FrontierRecoveryMarkers Markers;
            internal readonly GuiSmokeLedgerStreamCapture Capture;
            internal readonly LocalFrontierPreparedFiles Files;
            internal FrontierParentPreparation(FrontierFinalizerBinding owner, FrontierParentHandoff handoff, FrontierPersistedEvidence evidence,
                FrontierCursorMatch cursor, FrontierRecoveryMarkers markers, GuiSmokeLedgerStreamCapture capture, LocalFrontierPreparedFiles files)
            { this.owner = owner; Handoff = handoff; Evidence = evidence; Cursor = cursor; Markers = markers; Capture = capture; Files = files; }
            public bool IsCurrent { get { return owner.OwnsParentPreparation(this); } }
            public bool FilesPrepared { get { return IsCurrent; } }
            public int FileCount { get { return IsCurrent ? Files.Count : 0; } }
            public int PayloadCount { get { return IsCurrent ? Files.PayloadCount : 0; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeParentPreparation(this); }
        }
    }
}

namespace NLink.Tools.FrontierLedger
{
    public sealed partial class GuiSmokeLedgerStreamCapture
    {
        internal bool PinParentOutputs(NLink.Tools.LocalFrontierPreparedFiles target, GuiSmokeLedgerCapturedOutput copy)
        { return Object.ReferenceEquals(original, this) && !disposed && outputs != null && outputs.PinParentOutputs(target, copy); }
    }
    internal sealed partial class GuiSmokeLedgerRetainedOutputs
    {
        internal bool PinParentOutputs(NLink.Tools.LocalFrontierPreparedFiles target, GuiSmokeLedgerCapturedOutput copy)
        {
            lock (sync)
            {
                if (!IsCurrent || !OwnsSemanticCopy(copy)) return false;
                for (int i = 0; i < 3; i++) if (!target.Add(i + 1, files[i].SafeFileHandle, metadata[i].ByteCount, -1, null)) return false;
                return target.Add(4, semanticCopy.SafeFileHandle, semanticCopyInfo.ByteCount, -1, null) && IsCurrent;
            }
        }
    }
}
