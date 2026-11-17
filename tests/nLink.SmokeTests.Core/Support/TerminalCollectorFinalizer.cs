#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.IO;
using System.Threading;
using NLink.Tools.FrontierLedger;

namespace NLink.Tests
{
    public sealed partial class HeldFileNativeParent
    {
        public sealed partial class CollectorRead : IDisposable
        {
            private FileStream output;
            private ReadOnlyOutput reader;
            private object finalizer;
            private string target, outputIdentity, channel, root, run, scenario;
            private Guid requestId;
            private long lines;
            private bool disposed;
            public bool OutputIsCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(originalOwner, this) || disposed || output == null) return false;
                    try
                    {
                        Information info;
                        return output.CanRead && GetFileType(output.SafeFileHandle) == 1 &&
                            GetFileInformationByHandle(output.SafeFileHandle, out info) && Identity(info) == outputIdentity &&
                            (((long)info.SizeHigh << 32) | info.SizeLow) == CopiedBytes;
                    }
                    catch { return false; }
                }
            }
            // Only successful native collection installs retained output here.
            private CollectorRead originalOwner;
            internal void RetainOutput(FileStream value, string path, Guid id, string run, string scenario, long lineCount)
            {
                if (output != null || originalOwner != null || disposed) throw new InvalidDataException("collector_output_reused");
                Information info;
                if (!GetFileInformationByHandle(value.SafeFileHandle, out info)) throw new IOException("collector_output_identity");
                originalOwner = this; output = value; target = path; outputIdentity = Identity(info);
                requestId = id; this.run = run; this.scenario = scenario; lines = lineCount;
                output.Position = 0; reader = new ReadOnlyOutput(this);
            }
            internal void SetCollectorChannel(string name, string artifactRoot) { channel = name; root = artifactRoot; }
            internal bool TryClaim(object owner, Guid id, string name, string run, string scenario, string root, string source, long count, long bookmark)
            {
                return owner != null && OutputIsCurrent && requestId == id && channel == name && this.run == run &&
                    this.scenario == scenario && this.root == root && target == source && lines == count && bookmark == 0 &&
                    Interlocked.CompareExchange(ref finalizer, owner, null) == null;
            }
            internal Stream GetReader(object owner)
            { return OutputIsCurrent && Object.ReferenceEquals(finalizer, owner) ? reader : null; }
            internal bool Owns(object owner, GuiSmokeLedgerStreamingResult result)
            {
                return OutputIsCurrent && Object.ReferenceEquals(finalizer, owner) && result != null &&
                    result.OwnsStreamCapture(result.StreamCapture) && result.StreamCapture.MatchesOriginalSource(reader) &&
                    result.StreamCapture.SourceBytes == CopiedBytes && result.StreamCapture.SourceLines == lines;
            }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(originalOwner, this) || disposed) return;
                disposed = true;
                if (output != null) output.Dispose();
            }
            private sealed class ReadOnlyOutput : Stream
            {
                private readonly CollectorRead owner;
                internal ReadOnlyOutput(CollectorRead owner) { this.owner = owner; }
                public override bool CanRead { get { return owner.OutputIsCurrent; } }
                public override bool CanSeek { get { return false; } }
                public override bool CanWrite { get { return false; } }
                public override long Length { get { return owner.output.Length; } }
                public override long Position { get { return owner.output.Position; } set { throw new NotSupportedException(); } }
                public override int Read(byte[] buffer, int offset, int count)
                {
                    if (!CanRead) throw new ObjectDisposedException("collector_output");
                    return owner.output.Read(buffer, offset, count);
                }
                public override void Flush() { throw new NotSupportedException(); }
                public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
                public override void SetLength(long length) { throw new NotSupportedException(); }
                public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
                protected override void Dispose(bool disposing) { if (disposing) owner.Dispose(); base.Dispose(disposing); }
            }
        }
    }
}

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class Finalizer
        {
            private NLink.Tests.HeldFileNativeParent.CollectorRead collector;
            private GuiSmokeLedgerStreamingResult collectorResult;
            private int collectorAttempted;

            public GuiSmokeLedgerStreamingResult ProcessCollectorForTest(NLink.Tests.HeldFileNativeParent.CollectorRead value,
                string source, long lines, long bookmark, string retained, string writer, string[] keys, string semantic, string[] events,
                bool retainOutputs = false, bool expireCaptureForTest = false)
            {
                if (!Object.ReferenceEquals(original, this) || !IsCurrent || value == null || !value.OutputIsCurrent ||
                    Volatile.Read(ref collectorAttempted) != 0 ||
                    !value.TryClaim(this, id, endpointName, run, scenario, root, source, lines, bookmark)) return null;
                if (Interlocked.Exchange(ref collectorAttempted, 1) != 0) return null;
                collector = value;
                collectorResult = retainOutputs ?
                    GuiSmokeLogLedgerStreamingProcessor.ProcessFromStreamWithRetainedOutputs(value.GetReader(this), lines, bookmark,
                        retained, writer, run, scenario, keys, semantic, events, CancellationToken.None) :
                    GuiSmokeLogLedgerStreamingProcessor.ProcessFromStream(value.GetReader(this), lines, bookmark,
                        retained, writer, run, scenario, keys, semantic, events);
                if (!IsCurrent || !value.Owns(this, collectorResult)) throw new InvalidDataException("collector_finalizer_source_lost");
                var capture = collectorResult.StreamCapture;
                // Consume the processor's original metadata clock exactly once.
                // Processing retains its separate 5-second checks; metadata and
                // return-to-consumer time remain inside the unchanged 250 ms.
                if (expireCaptureForTest) Thread.Sleep(GuiSmokeLedgerNativeCapture.MaximumCaptureMilliseconds + 50);
                if (!IsCurrent || !value.Owns(this, collectorResult)) throw new InvalidDataException("collector_finalizer_source_lost");
                long beforeCaptureMilliseconds, afterCaptureMilliseconds;
                if (!capture.CaptureTerminalNativeForTest(collectorResult, value.GetReader(this), source,
                        out beforeCaptureMilliseconds, out afterCaptureMilliseconds) ||
                    !OwnsCollectorCaptureForTest(value, collectorResult, collectorResult.NativeCapture))
                    throw new InvalidDataException("collector_finalizer_capture_missing; before_capture_ms=" + beforeCaptureMilliseconds +
                        "; after_capture_ms=" + afterCaptureMilliseconds + "; budget_ms=" + GuiSmokeLedgerNativeCapture.MaximumCaptureMilliseconds +
                        "; native_capture_present=" + (collectorResult.NativeCapture != null) + "; expiry_injected=" + expireCaptureForTest);
                return collectorResult;
            }

            public bool OwnsCollectorCaptureForTest(NLink.Tests.HeldFileNativeParent.CollectorRead value,
                GuiSmokeLedgerStreamingResult result, GuiSmokeLedgerNativeCapture capture)
            {
                return Object.ReferenceEquals(original, this) && IsCurrent && value != null &&
                    Object.ReferenceEquals(collector, value) && Object.ReferenceEquals(collectorResult, result) &&
                    value.Owns(this, result) && result.OwnsNativeCapture(capture);
            }

            public void CloseCollectorForTest()
            {
                if (!Object.ReferenceEquals(original, this)) return;
                if (collectorResult != null && collectorResult.StreamCapture != null) collectorResult.StreamCapture.Dispose();
                if (collector != null) collector.Dispose();
            }
        }
    }
}
#endif
