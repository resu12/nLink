#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalInputReadLease
    {
        // The original bounded verifier hashes this candidate during its one
        // EOF traversal. Native duplication here does not verify any bytes.
        internal static LocalInputReadLease OpenCollectorWriter(SafeFileHandle pin, LocalGuiWriterRequest.Output expected)
        {
            SafeFileHandle handle = null; FileStream stream = null; LocalInputReadLease lease = null;
            try
            {
                if (expected.LineCount < 1 || expected.LineCount > 4096 || expected.ByteCount > MaximumWriterBytes) return null;
                handle = DuplicateFrontierRead(pin); if (handle == null) return null;
                stream = new FileStream(handle, FileAccess.Read); handle = null; stream.Position = 0;
                lease = new LocalInputReadLease(stream, expected.Path, expected.Sha256, expected.LineCount, false); stream = null;
                lease.frontierNative = true; lease.frontierOriginalLease = lease; lease.frontierHashHandle = lease.anchor.SafeFileHandle;
                if (lease.FileIdentity != expected.OriginalFileIdentity || lease.ByteCount != expected.ByteCount)
                { lease.Dispose(); return null; }
                return lease;
            }
            catch { if (lease != null) lease.Dispose(); return null; }
            finally { if (handle != null) handle.Dispose(); if (stream != null) stream.Dispose(); }
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class PrimaryEvaluationCustody
        {
            internal bool OwnsCollectorMembershipInputs(object inputs, string[] originalLines, long units, Stopwatch work)
            {
                if (state != 3 || !Object.ReferenceEquals(context, inputs) || nativeOwner == null || !NativeCurrent()) return false;
                var value = PSObject.AsPSObject(inputs);
                return Object.ReferenceEquals(value.Properties["Work"].Value, work) &&
                    Object.ReferenceEquals(value.Properties["FullOriginal"].Value, originalLines) &&
                    Object.ReferenceEquals(value.Properties["NativeOwner"].Value, nativeOwner) &&
                    Object.ReferenceEquals(value.Properties["NativeInputs"].Value, nativeInputs) && units == nativeOwner.CodeUnitVisits;
            }
        }

        public sealed partial class Worker
        {
            private CollectorMembershipForTest collectorMembership;
            public CollectorMembershipForTest CollectorMembershipResultForTest
            { get { return collectorMembership != null && collectorMembership.IsCurrent ? collectorMembership : null; } }
            public bool CollectorMembershipWasIssuedForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && collectorMembership != null; } }
            public long CollectorWriterRecordsReadForTest { get { return writerReader == null ? 0 : writerReader.LinesRead; } }
            public bool CollectorWriterVerifiedForTest { get { return OwnsVerifiedWriter(writerReader); } }

            public CollectorMembershipForTest TryVerifyCollectorMembershipForTest(CollectorPrimaryFilesForTest files,
                SourceReader source, PrimaryEvaluationCustody custody, CompletedEvaluations pair, object context,
                string[] full, string[] compact, object fullView, object compactView, string[] fullOriginal,
                string lookupWriter, long lookupSequence, long initialCodeUnits, Stopwatch work, string mutation)
            {
                if (!Object.ReferenceEquals(frontierOriginal, this) || files == null ||
                    !Object.ReferenceEquals(collectorPrimaryFiles, files) ||
                    !files.OwnsPrimary(this, source, custody, pair, context, full, compact, fullView, compactView) ||
                    !custody.OwnsCollectorMembershipInputs(context, fullOriginal, initialCodeUnits, work) ||
                    work == null || !work.IsRunning || work.ElapsedMilliseconds > 20000 || full.Length < 1 || full.Length > 4096 ||
                    (mutation != "" && mutation != "foreign-member" && mutation != "partial-read") ||
                    !ValidateInputReferences(source, full, fullOriginal, compact, compact, work)) return null;
                long priorVisits = 7L * full.Length + 4L * compact.Length;
                if (priorVisits + 2L * WriterContext.Outputs[1].LineCount + full.Length > 4096 ||
                    Interlocked.Exchange(ref writerOpened, 1) != 0) return null;
                LocalInputReadLease lease = null; bool accepted = false;
                try
                {
                    lease = LocalInputReadLease.OpenCollectorWriter(files.files[1].SafeFileHandle, WriterContext.Outputs[1]);
                    if (lease == null || lease.IsVerified) return null;
                    writerReader = new WriterReader(this, source, lease); lease = null;
                    if (mutation == "partial-read")
                    {
                        using (var reader = writerReader.VerifyAndReadLines().GetEnumerator())
                            if (!reader.MoveNext() || writerReader.IsVerified) throw new InvalidDataException("collector_membership_partial_control");
                        return null;
                    }
                    // First real source record is a membership lookup only.
                    // This never constructs a reported-failure witness.
                    var membership = writerReader.TryVerifyMembership(full, fullOriginal,
                        mutation == "foreign-member" ? "foreign" : lookupWriter, lookupSequence, initialCodeUnits, work);
                    if (membership == null || membership.SelectedIndex != 0 || membership.RecordVisits + priorVisits > 4096 ||
                        !OwnsVerifiedWriter(writerReader) || work.ElapsedMilliseconds > 20000 ||
                        !files.OwnsPrimary(this, source, custody, pair, context, full, compact, fullView, compactView)) return null;
                    collectorMembership = new CollectorMembershipForTest(this, files, writerReader, membership,
                        source, custody, pair, context, full, compact, fullView, compactView);
                    accepted = collectorMembership.IsCurrent;
                    if (!accepted) collectorMembership = null;
                    return collectorMembership;
                }
                finally
                {
                    if (lease != null) lease.Dispose();
                    if (!accepted && writerReader != null) writerReader.Dispose();
                }
            }

            internal bool OwnsCollectorMembership(CollectorMembershipForTest value, WriterReader reader)
            { return Object.ReferenceEquals(collectorMembership, value) && OwnsVerifiedWriter(reader); }
        }

        // Invocation-bound membership facts only. No publication, terminal-
        // integrity, route, failure-witness or whole-scenario completion token.
        public sealed partial class CollectorMembershipForTest : IDisposable
        {
            private readonly CollectorMembershipForTest original;
            private readonly Worker owner;
            private readonly CollectorPrimaryFilesForTest files;
            private readonly WriterReader writer;
            private readonly NativeMembership membership;
            private readonly SourceReader source;
            private readonly PrimaryEvaluationCustody custody;
            private readonly CompletedEvaluations pair;
            private readonly object context, fullView, compactView;
            private readonly string[] full, compact;
            private bool disposed;
            internal CollectorMembershipForTest(Worker owner, CollectorPrimaryFilesForTest files, WriterReader writer,
                NativeMembership membership, SourceReader source, PrimaryEvaluationCustody custody, CompletedEvaluations pair,
                object context, string[] full, string[] compact, object fullView, object compactView)
            {
                original = this; this.owner = owner; this.files = files; this.writer = writer; this.membership = membership;
                this.source = source; this.custody = custody; this.pair = pair; this.context = context;
                this.full = full; this.compact = compact; this.fullView = fullView; this.compactView = compactView;
            }
            public bool IsCurrent
            {
                get { return Object.ReferenceEquals(original, this) && !disposed && owner.OwnsCollectorMembership(this, writer) &&
                    files.OwnsPrimary(owner, source, custody, pair, context, full, compact, fullView, compactView); }
            }
            public int SelectedIndex { get { return membership.SelectedIndex; } }
            public long RecordVisits { get { return membership.RecordVisits; } }
            public long CodeUnitVisits { get { return membership.CodeUnitVisits; } }
            public bool Owns(Worker candidate, SourceReader reader, PrimaryEvaluationCustody evaluationCustody,
                CompletedEvaluations evaluations, object inputs, string[] fullLines, string[] compactLines,
                object fullResult, object compactResult)
            {
                return IsCurrent && Object.ReferenceEquals(owner, candidate) && Object.ReferenceEquals(source, reader) &&
                    Object.ReferenceEquals(custody, evaluationCustody) && Object.ReferenceEquals(pair, evaluations) &&
                    Object.ReferenceEquals(context, inputs) && Object.ReferenceEquals(full, fullLines) &&
                    Object.ReferenceEquals(compact, compactLines) && Object.ReferenceEquals(fullView, fullResult) &&
                    Object.ReferenceEquals(compactView, compactResult);
            }
            public void Dispose()
            { if (!Object.ReferenceEquals(original, this) || disposed) return; disposed = true; writer.Dispose(); }
        }
    }
}
#endif
