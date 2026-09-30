#if NLINK_TEST_NATIVE_WITNESS_ADAPTER && NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Diagnostics;
using System.Management.Automation;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class PrimaryEvaluationCustody
        {
            internal bool OwnsNativeWitnessWriterInputs(CollectorPrimaryFilesForTest files,
                SourceReader candidateSource, object inputs, Stopwatch work)
            {
                if (!Object.ReferenceEquals(source, candidateSource) || !Object.ReferenceEquals(context, inputs) ||
                    work == null || !work.IsRunning || work.ElapsedMilliseconds > 20000 ||
                    !files.OwnsPrimary(owner, source, this, issued, context, full, compact, fullView, compactView)) return false;
                var value = PSObject.AsPSObject(context);
                return Object.ReferenceEquals(value.Properties["Work"].Value, work) &&
                    Object.ReferenceEquals(value.Properties["NativeOwner"].Value, nativeOwner) &&
                    Object.ReferenceEquals(value.Properties["NativeInputs"].Value, nativeInputs) &&
                    Object.ReferenceEquals(value.Properties["FullLines"].Value, full) &&
                    Object.ReferenceEquals(value.Properties["CompactLines"].Value, compact) &&
                    Object.ReferenceEquals(value.Properties["FullView"].Value, fullView) &&
                    Object.ReferenceEquals(value.Properties["CompactView"].Value, compactView) &&
                    Object.ReferenceEquals(value.Properties["SourceOwner"].Value, owner) &&
                    Object.ReferenceEquals(value.Properties["SourceReader"].Value, source) &&
                    full.Length > 0 && full.Length <= 4096 &&
                    7L * full.Length + 4L * compact.Length + 2L * owner.WriterContext.Outputs[1].LineCount + full.Length <= 4096;
            }
        }

        public sealed partial class Worker
        {
            // Acquisition only: the original membership verifier must still
            // perform its one EOF/hash pass and select the requested source row.
            // No custody or failure-witness capability is passed to this helper.
            public WriterReader TryOpenNativeWitnessWriterForTest(CollectorPrimaryFilesForTest files,
                SourceReader source, object context, Stopwatch work)
            {
                if (!Object.ReferenceEquals(frontierOriginal, this) || frontierRequested ||
                    files == null || !Object.ReferenceEquals(collectorPrimaryFiles, files) || primary == null ||
                    !primary.OwnsNativeWitnessWriterInputs(files, source, context, work) ||
                    Interlocked.CompareExchange(ref writerOpened, 1, 0) != 0) return null;
                LocalInputReadLease lease = null;
                try
                {
                    lease = LocalInputReadLease.OpenCollectorWriter(files.files[1].SafeFileHandle, WriterContext.Outputs[1]);
                    if (lease == null || lease.IsVerified ||
                        !primary.OwnsNativeWitnessWriterInputs(files, source, context, work)) return null;
                    writerReader = new WriterReader(this, source, lease); lease = null;
                    return writerReader;
                }
                finally { if (lease != null) lease.Dispose(); }
            }
        }
    }
}
#endif
