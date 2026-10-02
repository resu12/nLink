using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class Worker
        {
            private SourceReader TryOpenFrontierSource()
            {
                lock (frontierWorkerSync)
                {
                    if (!Object.ReferenceEquals(frontierOriginal, this) || frontierWorkerInputs == null) return null;
                    if (System.Threading.Interlocked.Exchange(ref opened, 1) != 0) { CloseFrontierWorker(); return null; }
                    sourceReader = frontierWorkerInputs.OpenSource();
                    return sourceReader;
                }
            }
        }

        public sealed partial class SourceReader
        {
            private readonly FrontierWorkerInputs frontier;
            internal SourceReader(Worker worker, FrontierWorkerInputs frontier) { this.worker = worker; this.frontier = frontier; }
            private IEnumerable<string> ReadFrontierLines()
            {
                if (!frontier.BeginSource(this)) throw new InvalidOperationException("frontier_source_replay_or_foreign");
                bool ended = false;
                try
                {
                    string line;
                    while ((line = frontier.ReadSource(this)) != null) { LinesRead++; yield return line; }
                    ended = frontier.SourceComplete(this);
                }
                finally { if (!ended) frontier.DisposeSource(this); }
            }
            // Original physical ledger line, not the semantic-copy ordinal.
            // Only exact strings emitted by this reader can join after EOF.
            public int GetFrontierPhysicalOrigin(int oneBasedOrdinal, string originalLine)
            { return frontier == null ? 0 : frontier.SourceOrigin(this, oneBasedOrdinal, originalLine); }
        }

        public sealed partial class FrontierWorkerInputs
        {
            private SourceReader nativeReader;
            private MappedSource nativeStream;
            private GuiSmokeLedgerLfRead nativeLines;
            private readonly List<string> sourceLines = new List<string>();
            private bool sourceStarted, sourceEnded;
            private long sourceUnits;
#if NLINK_TEST_ATTEMPT_DISPATCH
            internal Action<string> SourceReadProbeForTest = null;
            private void ProbeReadForTest(string stage)
            { var probe = SourceReadProbeForTest; if (probe != null) probe(stage); }
#endif
            internal SourceReader OpenSource()
            {
                lock (sync)
                {
#if NLINK_TEST_ATTEMPT_DISPATCH
                    long openWorkStartForTest = work.ElapsedTicks;
#endif
                    bool success = false; work.Start(); SafeFileHandle handle = null; FileStream stream = null;
                    try
                    {
                        if (!ResourceCurrent || nativeReader != null || facts.SemanticLineCount != owner.SourceLines) return null;
                        if (!DuplicateSourceHandle(GetCurrentProcess(), pins.Handles[4], GetCurrentProcess(), out handle, 0x80000000, false, 0)) return null;
                        stream = new FileStream(handle, FileAccess.Read); handle = null;
                        if (stream.Length != owner.SourceBytes) return null;
                        nativeStream = new MappedSource(stream); stream = null;
                        // Semantic copies separate records with CRLF but do not
                        // append a final separator. Raw ledger framing stays strict.
                        nativeLines = new GuiSmokeLedgerLfRead(nativeStream, CheckSourceFragment, true);
                        nativeReader = new SourceReader(owner, this);
                        success = ResourceCurrent; return success ? nativeReader : null;
                    }
                    catch { return null; }
#if NLINK_TEST_ATTEMPT_DISPATCH
                    finally { work.Stop(); PrimaryOpenWorkTicksForTest += work.ElapsedTicks - openWorkStartForTest; if (stream != null) stream.Dispose(); if (handle != null) handle.Dispose(); if (!success) Close(); }
#else
                    finally { work.Stop(); if (stream != null) stream.Dispose(); if (handle != null) handle.Dispose(); if (!success) Close(); }
#endif
                }
            }
            private bool OriginalSource(SourceReader candidate)
            { return Object.ReferenceEquals(owner.frontierWorkerInputs, this) && Object.ReferenceEquals(nativeReader, candidate); }
            private void CheckSource() { if (!ResourceCurrent) throw new IOException("frontier_source_not_current"); }
            // Abort-only while a line is private. Full validation gates every publication.
            private void CheckSourceContinuation()
            {
#if NLINK_TEST_ATTEMPT_DISPATCH
                if (!PrimarySourceCheckForTest("read:closed", !closed) || !PrimarySourceBudgetForTest("read:budget", work.ElapsedMilliseconds) ||
                    !PrimarySourceCheckForTest("read:base", PrimaryMeasuredCurrentForTest(0)) ||
                    !PrimarySourceCheckForTest("read:scope", PrimaryMeasuredCurrentForTest(1)) ||
                    !PrimarySourceCheckForTest("read:files-cancelled", !files.Cancelled.WaitOne(0)) ||
                    !PrimarySourceCheckForTest("read:facts-cancelled", !files.Facts.Cancelled.WaitOne(0)))
#else
                if (closed || work.ElapsedMilliseconds > LocalPublicationInputLimits.ProducerMilliseconds ||
                    !owner.FrontierBaseCurrent || !scope.IsCurrent || files.Cancelled.WaitOne(0) || files.Facts.Cancelled.WaitOne(0))
#endif
                    throw new IOException("frontier_source_not_current");
            }
            private void CheckSourceFragment()
            {
#if NLINK_TEST_ATTEMPT_DISPATCH
                ProbeReadForTest("fragment");
#endif
                CheckSourceContinuation();
            }
            private void CheckSourcePublication()
            {
#if NLINK_TEST_ATTEMPT_DISPATCH
                ProbeReadForTest("commit");
#endif
                CheckSource();
#if NLINK_TEST_ATTEMPT_DISPATCH
                ProbeReadForTest("verified");
#endif
                CheckSourceContinuation();
            }
            internal bool BeginSource(SourceReader candidate)
            {
                lock (owner.frontierWorkerSync)
                lock (sync)
                {
                    if (!OriginalSource(candidate)) return false;
                    if (sourceStarted || !ResourceCurrent) { Close(); return false; }
                    sourceStarted = true; return true;
                }
            }
            internal string ReadSource(SourceReader candidate)
            {
                lock (owner.frontierWorkerSync)
                lock (sync)
                {
                    if (!OriginalSource(candidate)) throw new IOException("frontier_source_foreign");
#if NLINK_TEST_ATTEMPT_DISPATCH
                    long readWorkStartForTest = work.ElapsedTicks;
                    primaryReadingForTest = true; PrimaryReadCallsForTest++;
#endif
                    work.Start();
                    try
                    {
#if NLINK_TEST_ATTEMPT_DISPATCH
                        ProbeReadForTest("entry");
                        if (!PrimarySourceCheckForTest("read:closed", !closed) || !PrimarySourceBudgetForTest("read:budget", work.ElapsedMilliseconds) ||
                            !PrimarySourceCheckForTest("read:base", PrimaryMeasuredCurrentForTest(0)))
#else
                        if (closed || work.ElapsedMilliseconds > LocalPublicationInputLimits.ProducerMilliseconds || !owner.FrontierBaseCurrent)
#endif
                            throw new IOException("frontier_source_not_current");
                        if (!sourceStarted || sourceEnded) throw new IOException("frontier_source_read_order");
                        // Fragment checks can only abort. Keep the decoded line private
                        // until its full resource validation and final budget/liveness check.
                        string line = nativeLines.ReadLine();
                        if (line == null)
                        {
                            if (sourceLines.Count != facts.SemanticLineCount || nativeStream.Position != owner.SourceBytes)
                                throw new IOException("frontier_source_incomplete");
                            CheckSourcePublication(); sourceEnded = true; return null;
                        }
                        if (sourceLines.Count >= facts.SemanticLineCount || line.Length > LocalPublicationInputLimits.InputCodeUnits - sourceUnits)
                            throw new IOException("frontier_source_limit");
                        CheckSourcePublication(); sourceUnits += line.Length; sourceLines.Add(line); return line;
                    }
#if NLINK_TEST_ATTEMPT_DISPATCH
                    catch (Exception error) { PrimarySourceExceptionForTest = error.GetType().FullName + ":" + error.Message; Close(); throw; }
#else
                    catch { Close(); throw; }
#endif
#if NLINK_TEST_ATTEMPT_DISPATCH
                    finally { work.Stop(); PrimaryReadWorkTicksForTest += work.ElapsedTicks - readWorkStartForTest; primaryReadingForTest = false; }
#else
                    finally { work.Stop(); }
#endif
                }
            }
            internal bool SourceComplete(SourceReader candidate)
            {
                lock (owner.frontierWorkerSync)
                lock (sync)
                {
                    if (!OriginalSource(candidate)) return false;
                    if (!RetainedResourceCurrent) { Close(); return false; }
                    return sourceEnded;
                }
            }
            internal int SourceOrigin(SourceReader candidate, int ordinal, string line)
            {
                lock (owner.frontierWorkerSync)
                lock (sync)
                {
                    if (!OriginalSource(candidate)) return 0;
#if NLINK_TEST_ATTEMPT_DISPATCH
                    long originWorkStartForTest = work.ElapsedTicks;
                    if (sourceEnded && PrimaryOriginEntryWorkTicksForTest == 0) PrimaryOriginEntryWorkTicksForTest = originWorkStartForTest;
                    primaryOriginForTest = true; PrimaryOriginCallsForTest++;
#endif
                    work.Start();
                    try
                    {
                        if (!RetainedResourceCurrent) { Close(); return 0; }
#if NLINK_TEST_ATTEMPT_DISPATCH
                        int originForTest = sourceEnded && ordinal > 0 && ordinal <= sourceLines.Count &&
                            Object.ReferenceEquals(sourceLines[ordinal - 1], line) ? facts.SemanticOrigin(ordinal - 1) : 0;
                        if (originForTest > 0) PrimaryOriginAcceptedForTest++;
                        return originForTest;
#else
                        return sourceEnded && ordinal > 0 && ordinal <= sourceLines.Count &&
                            Object.ReferenceEquals(sourceLines[ordinal - 1], line) ? facts.SemanticOrigin(ordinal - 1) : 0;
#endif
                    }
#if NLINK_TEST_ATTEMPT_DISPATCH
                    finally { work.Stop(); PrimaryOriginWorkTicksForTest += work.ElapsedTicks - originWorkStartForTest; primaryOriginForTest = false; }
#else
                    finally { work.Stop(); }
#endif
                }
            }
            internal void DisposeSource(SourceReader candidate)
            { lock (owner.frontierWorkerSync) lock (sync) { if (OriginalSource(candidate)) Close(); } }
            private void CloseSource()
            {
                sourceEnded = false; sourceLines.Clear();
                if (nativeStream != null) nativeStream.Dispose();
                nativeLines = null;
            }
            // Private sequential mapped access: no shared native seek, path
            // reopening, stream export, or additional content/hash pass.
            private sealed class MappedSource : Stream
            {
#if NLINK_TEST_ATTEMPT_DISPATCH
                internal long ReadTicksForTest, ReadCallsForTest;
#endif
                private readonly FileStream stream;
                private readonly MemoryMappedFile map;
                private readonly long length;
                private long position;
                internal MappedSource(FileStream stream)
                {
                    this.stream = stream; length = stream.Length;
                    if (length != 0) map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                }
                public override int Read(byte[] buffer, int offset, int count)
                {
#if NLINK_TEST_ATTEMPT_DISPATCH
                    long readStartForTest = System.Diagnostics.Stopwatch.GetTimestamp();
                    try {
#endif
                    if (buffer == null || offset < 0 || count < 0 || count > 32768 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                    int take = (int)Math.Min(count, length - position);
                    if (take != 0) using (var view = map.CreateViewAccessor(position, take, MemoryMappedFileAccess.Read))
                        if (view.ReadArray(0, buffer, offset, take) != take) throw new IOException("frontier_source_short_read");
                    position += take; return take;
#if NLINK_TEST_ATTEMPT_DISPATCH
                    } finally { ReadTicksForTest += System.Diagnostics.Stopwatch.GetTimestamp() - readStartForTest; ReadCallsForTest++; }
#endif
                }
                public override bool CanRead { get { return stream.CanRead; } }
                public override bool CanSeek { get { return false; } }
                public override bool CanWrite { get { return false; } }
                public override long Length { get { return length; } }
                public override long Position { get { return position; } set { throw new NotSupportedException(); } }
                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
                public override void SetLength(long value) { throw new NotSupportedException(); }
                public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
                protected override void Dispose(bool disposing)
                { if (disposing) { if (map != null) map.Dispose(); stream.Dispose(); } base.Dispose(disposing); }
            }
            [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle")]
            private static extern bool DuplicateSourceHandle(IntPtr source, SafeFileHandle handle, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
        }
    }
}
