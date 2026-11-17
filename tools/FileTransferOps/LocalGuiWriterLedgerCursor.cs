using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            // Scenario entry selects only this owner's original creation/cursor;
            // a caller bookmark or another live origin cannot be substituted.
            public FrontierLedgerObservation TryCaptureInitialLedgerObservation()
            {
                lock (sync)
                {
                    if (!IsCurrent) return null;
                    if (ledgerOrigin == null) { Dispose(); return null; }
                    return ledgerOrigin.TryCaptureInitialObservation();
                }
            }

            // Before handoff, sync serializes cursor work with app binding and
            // adopter creation. Afterwards the adopter serializes the same clock.
            internal bool SynchronizeLedger(FrontierLedgerOrigin value, bool initial)
            {
                LocalFrontierFileAdoption adoption;
                lock (sync)
                {
                    if (!Object.ReferenceEquals(ledgerOrigin, value) || !IsCurrent) return false;
                    if (fileSealAttempted || (initial && (apps.Count != 1 || apps[0].Exit.WaitOne(0) || sealedLaunches || fileAdoption != null)))
                    { Dispose(); return false; }
                    adoption = fileAdoption;
                    if (adoption == null)
                    {
                        bool success = false; frontierWork.Start();
                        try { success = value.Synchronize(initial, () => LedgerCursorScopeCurrent() && (!initial || !apps[0].Exit.WaitOne(0))); return success; }
                        finally { frontierWork.Stop(); if (!success) Dispose(); }
                    }
                }
                bool retained = adoption.TryRunControl(() => value.Synchronize(false, LedgerCursorScopeCurrent));
                if (!retained || !IsCurrent) { Dispose(); return false; }
                return true;
            }
            // Called under the adopter/origin locks; never acquire the GUI lock.
            private bool LedgerCursorScopeCurrent()
            {
                try
                {
                    return Object.ReferenceEquals(frontierLaunchOwner, this) && !disposed && !fileSealAttempted &&
                        !parentExit.WaitOne(0) && !closed.WaitOne(0) && MatchesRequest() && AppRequestsCurrent() &&
                        FileChannelsCurrent() && LedgerOriginCurrent();
                }
                catch { return false; }
            }
        }

        public sealed partial class FrontierLedgerOrigin
        {
            public const int MaximumIndexedLines = 262144;
            private readonly List<long> lineStarts = new List<long>();
            private readonly SHA256 cursorHash = SHA256.Create();
            private long readPosition, committedPosition, synchronizationCount;
            private FrontierLedgerObservation initialObservation, finalObservation;

            public bool TrySynchronize() { return owner.SynchronizeLedger(this, false); }
            public FrontierLedgerObservation TryCaptureInitialObservation()
            {
                if (!owner.SynchronizeLedger(this, true) || !IsCurrent) return null;
                lock (sync) { return initialObservation; }
            }
            internal bool Synchronize(bool initial, Func<bool> current)
            {
                lock (sync)
                {
                    try
                    {
                        if (!ResourceCurrent || finalObservation != null || (initial && initialObservation != null) || !current()) return false;
                        long length = file.Length;
                        var buffer = new byte[32768];
                        while (readPosition < length)
                        {
                            if (!current() || !ResourceCurrent) return false;
                            long start = readPosition;
                            int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, length - start));
                            readPosition += read;
                            if (read == 0 || !current() || !ResourceCurrent) return false;
                            cursorHash.TransformBlock(buffer, 0, read, buffer, 0);
                            for (int i = 0; i < read; i++)
                            {
                                // Only LF completes a physical line. Retain the
                                // incomplete tail's start without rereading bytes.
                                if (buffer[i] != 10) continue;
                                if (lineStarts.Count == MaximumIndexedLines) return false;
                                lineStarts.Add(committedPosition); committedPosition = start + i + 1;
                            }
                        }
                        synchronizationCount++;
                        if (!current() || !ResourceCurrent) return false;
                        if (initial) initialObservation = new FrontierLedgerObservation(this, false, lineStarts.Count, readPosition, committedPosition, synchronizationCount, lineStarts.Count);
                        return true;
                    }
                    catch { return false; }
                }
            }
            internal FrontierLedgerObservation CaptureFinal(LocalFrontierFileAdoption adoption)
            {
                lock (sync)
                {
                    if (initialObservation == null || finalObservation != null ||
                        !Synchronize(false, () => adoption.IsSourceVerified)) return null;
                    if (readPosition != committedPosition || readPosition != adoption.SealedLength) return null;
                    cursorHash.TransformFinalBlock(new byte[0], 0, 0);
                    string hash = BitConverter.ToString(cursorHash.Hash).Replace("-", "").ToLowerInvariant();
                    if (!String.Equals(hash, adoption.SealedHash, StringComparison.Ordinal)) return null;
                    finalObservation = new FrontierLedgerObservation(this, true, lineStarts.Count, readPosition, committedPosition, synchronizationCount, initialObservation.Bookmark);
                    return finalObservation;
                }
            }
            internal bool OwnsObservation(FrontierLedgerObservation value)
            {
                if (value == null || !IsCurrent) return false;
                lock (sync) { return ResourceCurrent && (Object.ReferenceEquals(initialObservation, value) || Object.ReferenceEquals(finalObservation, value)); }
            }
            internal void DisposeObservation(FrontierLedgerObservation value)
            { if (OwnsObservation(value)) Dispose(); }
        }

        public sealed partial class FrontierFinalizerBinding
        {
            private bool ledgerFinalAttempted;
            public FrontierLedgerObservation TryCaptureLedgerFinal()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false;
                    try
                    {
                        if (ledgerFinalAttempted) return null; ledgerFinalAttempted = true;
                        var origin = LedgerOrigin; var adoption = files.GetOriginalAdoption();
                        if (origin == null || adoption == null || sourceReader == null ||
                            !sourceReader.ProcessingWorkActive || sourceReader.Position != 0) return null;
                        FrontierLedgerObservation observation = null;
                        // Original source work serializes this final read; the
                        // callback enters only the origin resource, not GUI locks.
                        success = adoption.TryRunRecoveryRead(() =>
                            adoption.ProcessingWorkActive(sourceReader) && adoption.SealedReadPosition(sourceReader) == 0 &&
                            (observation = origin.CaptureFinal(adoption)) != null) &&
                            sourceReader.ProcessingWorkActive && IsCurrent;
                        return success ? observation : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
        }

        // Exact original observations only, not imported snapshots, writer
        // validation, processor invocation agreement or publication authority.
        public sealed class FrontierLedgerObservation : IDisposable
        {
            private readonly FrontierLedgerOrigin owner;
            public string LedgerId { get; private set; }
            public string GenerationId { get; private set; }
            public bool IsFinal { get; private set; }
            public int LineCount { get; private set; }
            public long BytesRead { get; private set; }
            public long CommittedBytes { get; private set; }
            public long SynchronizationCount { get; private set; }
            public int Bookmark { get; private set; }
            public bool IsCurrent { get { return owner.OwnsObservation(this); } }
            public bool CursorHistoryValidated { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierLedgerObservation(FrontierLedgerOrigin owner, bool final, int lines, long bytes, long committed, long synchronizations, int bookmark)
            {
                this.owner = owner; LedgerId = owner.LedgerId; GenerationId = owner.GenerationId; IsFinal = final; LineCount = lines;
                BytesRead = bytes; CommittedBytes = committed; SynchronizationCount = synchronizations; Bookmark = bookmark;
            }
            public void Dispose() { owner.DisposeObservation(this); }
        }
    }
}
