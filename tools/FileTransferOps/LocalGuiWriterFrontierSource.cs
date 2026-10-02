using System;
using System.IO;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private readonly object sourceSync = new object();
            private bool sourceAttempted;
            private FrontierSourceRead sourceReader;

            public FrontierSourceRead TryOpenSource()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    if (sourceAttempted) { Dispose(); return null; }
                    sourceAttempted = true;
                    var adoption = files.GetOriginalAdoption();
                    if (adoption == null) { Dispose(); return null; }
                    var reader = new FrontierSourceRead(this, adoption);
                    if (!adoption.TryBeginSealedRead(reader) || !IsCurrent) { Dispose(); return null; }
                    sourceReader = reader;
                    return reader;
                }
            }
        }

        // Borrowed access to the original sealed handle, never a handle export.
        // EOF means bytes were read, not that a parser validated or persisted them.
        public sealed class FrontierSourceRead : Stream
        {
            private readonly FrontierSourceRead original;
            private readonly FrontierFinalizerBinding binding;
            private readonly LocalFrontierFileAdoption adoption;
            internal FrontierSourceRead(FrontierFinalizerBinding binding, LocalFrontierFileAdoption adoption)
            { original = this; this.binding = binding; this.adoption = adoption; }
            public bool IsCurrent { get { return Object.ReferenceEquals(original, this) && binding.IsCurrent; } }
            public bool ReachedEnd { get { return IsCurrent && adoption.SealedReadEnded(this) && IsCurrent; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public override bool CanRead { get { return IsCurrent; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { if (!IsCurrent) throw new IOException("frontier_source_not_current"); return adoption.SealedLength; } }
            public override long Position
            {
                get { long value = adoption.SealedReadPosition(this); if (!IsCurrent || value < 0) throw new IOException("frontier_source_not_current"); return value; }
                set { throw new NotSupportedException(); }
            }
            // Work accounting only, not a parser/finalizer success receipt.
            // Cancellation is a resource-cleanup signal only, never approval.
            public CancellationToken ProcessingLifetime
            { get { return IsCurrent ? adoption.ProcessingLifetime(this) : new CancellationToken(true); } }
            public bool TryBeginProcessingWork()
            {
                if (!IsCurrent) return false;
                if (adoption.BeginProcessingWork(this) && IsCurrent) return true;
                binding.Dispose(); return false;
            }
            internal bool ProcessingWorkActive { get { return IsCurrent && adoption.ProcessingWorkActive(this) && IsCurrent; } }
            internal bool BufferedProcessingWorkCurrent
            { get { return Object.ReferenceEquals(original, this) && adoption.BufferedWorkCurrent(this); } }
            public bool TryFinishProcessingWork()
            {
                if (!Object.ReferenceEquals(original, this)) return false;
                if (adoption.FinishProcessingWork(this) && IsCurrent) return true;
                binding.Dispose(); return false;
            }
            internal bool MatchWriterFacts(string[] writers, long[] lastSequences)
            {
                if (!IsCurrent) return false;
                if (adoption.TryMatchWriters(this, writers, lastSequences) && IsCurrent) return true;
                binding.Dispose(); return false;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                // Never enter the finalizer/GUI locks from under the adopter lock.
                int read;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                if (!IsCurrent) throw new IOException("frontier_source_not_current");
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                NLink.Tests.NativeContentParserProfileForTest.ReaderStage(5, sample);
                sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                if (!adoption.TryReadSealedSource(this, buffer, offset, count, out read) || !IsCurrent)
                { binding.Dispose(); throw new IOException("frontier_source_read_failed"); }
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                NLink.Tests.NativeContentParserProfileForTest.ReaderStage(6, sample);
#endif
                return read;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && Object.ReferenceEquals(original, this))
                { adoption.FinishProcessingWork(this); binding.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
