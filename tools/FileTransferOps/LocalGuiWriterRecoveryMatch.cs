using System;
using System.Collections.ObjectModel;
using System.IO;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool markerCaptureAttempted, recoveryMatchAttempted;
            private FrontierRecoveryMarkers recoveryMarkers;
            private FrontierRecoveryMarkerScan recoveryScan;
            private FrontierRecoveryMatch recoveryMatch;
            public FrontierRecoveryMatch RecoveryMatch
            { get { lock (sourceSync) { return OwnsRecoveryMatch(recoveryMatch) ? recoveryMatch : null; } } }
            private void CheckRecoveryWork()
            {
#if NLINK_TEST_BUFFERED_LEDGER
                NLink.Tests.RecoveryBufferProbe.Boundary("full");
#endif
#if NLINK_TEST_ATTEMPT_DISPATCH
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                bool completed = false;
                try
                {
#endif
                if (!IsCurrent || sourceReader == null || !sourceReader.ProcessingWorkActive)
                    throw new InvalidDataException("recovery_work_not_current");
#if NLINK_TEST_ATTEMPT_DISPATCH
                    completed = true;
                }
                finally { NLink.Tests.AttemptWorkProfileForTest.RecordRecoveryCheck(started, completed); }
#endif
            }
            public FrontierRecoveryMarkers TryCaptureRecoveryMarkers()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false;
                    try
                    {
                        if (markerCaptureAttempted) return null; markerCaptureAttempted = true;
                        CheckRecoveryWork();
                        if (sourceReader.Position != 0) return null;
                        var readers = TryOpenRecoveryOutputs();
                        if (readers == null) return null;
#if NLINK_TEST_BUFFERED_LEDGER
                        NLink.Tests.RecoveryBufferProbe.Boundary("readers", readers);
#endif
                        var scan = new FrontierRecoveryMarkerScan(CheckRecoveryWork, sourceReader, readers);
                        for (int i = 0; i < readers.Count; i++)
                        {
                            scan.Read(readers[i], i, finalizer.run, finalizer.scenario);
                            if (!readers[i].ReachedEnd) return null;
                        }
                        if (!scan.PairsMatch()) return null;
                        var adoption = files.GetOriginalAdoption();
                        foreach (var marker in scan.Markers)
                        {
                            CheckRecoveryWork(); var reader = readers[marker.StreamIndex];
                            if (!adoption.MatchesRecoveryWriter(reader.AppProcessId, reader.AppProcessCreationFileTime, marker.WriterId)) return null;
                        }
                        recoveryScan = scan;
                        recoveryMarkers = new FrontierRecoveryMarkers(this, scan);
#if NLINK_TEST_BUFFERED_LEDGER
                        NLink.Tests.RecoveryBufferProbe.Boundary("before-return", recoveryMarkers);
#endif
                        CheckRecoveryWork(); success = OwnsRecoveryMarkers(recoveryMarkers);
                        return success ? recoveryMarkers : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
            public FrontierRecoveryMatch TryMatchRecovery(GuiSmokeLedgerStreamingResult result)
            {
                lock (sourceSync)
                {
                    // Foreign/copy candidates cannot consume the original join.
                    if (!IsCurrent || writerMatch == null || !writerMatch.MatchesResult(result)) return null;
                    bool success = false;
                    try
                    {
                        if (recoveryMatchAttempted) return null; recoveryMatchAttempted = true;
                        CheckRecoveryWork();
                        if (!OwnsRecoveryMarkers(recoveryMarkers) || recoveryScan == null || !recoveryScan.Matches(writerMatch.Validation.DeferredBatches)) return null;
                        recoveryMatch = new FrontierRecoveryMatch(this, recoveryMarkers, writerMatch, result);
                        CheckRecoveryWork(); success = OwnsRecoveryMatch(recoveryMatch);
                        return success ? recoveryMatch : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
            internal bool OwnsRecoveryMarkers(FrontierRecoveryMarkers value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(value, recoveryMarkers) || !IsCurrent) return false;
                    foreach (var reader in recoveryReaders) if (!reader.IsCurrent || !reader.ReachedEnd) { Dispose(); return false; }
                    return IsCurrent;
                }
            }
            internal bool OwnsRecoveryMatch(FrontierRecoveryMatch value)
            {
                lock (sourceSync)
                { return value != null && Object.ReferenceEquals(recoveryMatch, value) && OwnsRecoveryMarkers(recoveryMarkers) && value.InputsCurrent(); }
            }
            internal void DisposeRecoveryMarkers(FrontierRecoveryMarkers value)
            { lock (sourceSync) { if (Object.ReferenceEquals(recoveryMarkers, value) && IsCurrent) Dispose(); } }
            internal void DisposeRecoveryMatch(FrontierRecoveryMatch value)
            { lock (sourceSync) { if (Object.ReferenceEquals(recoveryMatch, value) && IsCurrent) Dispose(); } }
        }
        public sealed class FrontierRecoveryMarkers : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            public ReadOnlyCollection<FrontierRecoveryMarker> Markers { get; private set; }
            public ReadOnlyCollection<string> DeferredBatchKeys { get; private set; }
            public long SourceLineVisitCount { get; private set; }
            public int MetadataCodeUnits { get; private set; }
            public bool IsCurrent { get { return owner.OwnsRecoveryMarkers(this); } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierRecoveryMarkers(FrontierFinalizerBinding owner, FrontierRecoveryMarkerScan scan)
            {
                this.owner = owner; Markers = Array.AsReadOnly(scan.Markers.ToArray());
                DeferredBatchKeys = Array.AsReadOnly(scan.DeferredKeys.ToArray());
                SourceLineVisitCount = scan.SourceLines; MetadataCodeUnits = scan.MetadataCodeUnits;
            }
            public void Dispose() { owner.DisposeRecoveryMarkers(this); }
        }
        // Recovery subgate only. Cursor/copy/persistence/parent gates are absent.
        public sealed class FrontierRecoveryMatch : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            private readonly FrontierWriterMatch writers;
            private readonly GuiSmokeLedgerStreamingResult result;
            public FrontierRecoveryMarkers Markers { get; private set; }
            public int RecoveredBatchCount { get { return Markers.DeferredBatchKeys.Count; } }
            public bool IsCurrent { get { return owner.OwnsRecoveryMatch(this); } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierRecoveryMatch(FrontierFinalizerBinding owner, FrontierRecoveryMarkers markers, FrontierWriterMatch writers, GuiSmokeLedgerStreamingResult result)
            { this.owner = owner; Markers = markers; this.writers = writers; this.result = result; }
            internal bool InputsCurrent() { return writers.MatchesResult(result) && Markers.IsCurrent; }
            public bool MatchesResult(GuiSmokeLedgerStreamingResult value) { return Object.ReferenceEquals(result, value) && IsCurrent; }
            public void Dispose() { owner.DisposeRecoveryMatch(this); }
        }
    }
}
