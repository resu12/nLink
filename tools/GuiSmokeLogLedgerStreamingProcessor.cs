using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

#if NLINK_FRONTIER_LEDGER
namespace NLink.Tools.FrontierLedger
#else
namespace NLink.Tools
#endif
{
    // Exact processor/source/output provenance, not finalizer or publication authority.
    public sealed partial class GuiSmokeLedgerStreamCapture : IDisposable
    {
        private readonly GuiSmokeLedgerStreamCapture original;
        private readonly GuiSmokeLedgerStreamingResult owner;
        private readonly Stream source;
        private readonly GuiSmokeLedgerRetainedOutputs outputs;
        internal readonly ReadOnlyCollection<long> CursorLineStarts;
        private bool disposed;
        public string AcceptanceRunId { get; private set; }
        public string ScenarioRunId { get; private set; }
        public long SourceBytes { get; private set; }
        public long SourceLines { get; private set; }
        public long Bookmark { get; private set; }
        public GuiSmokeLedgerCapturedOutput Retained { get; private set; }
        public GuiSmokeLedgerCapturedOutput Writer { get; private set; }
        public GuiSmokeLedgerCapturedOutput Semantic { get; private set; }
        public ReadOnlyCollection<int> RetainedOrigins { get; private set; }
        public ReadOnlyCollection<int> WriterOrigins { get; private set; }
        public ReadOnlyCollection<int> SemanticOrigins { get; private set; }
        public bool HasWriterValidationErrors { get; private set; }
        public GuiSmokeLedgerValidationSnapshot Validation { get; private set; }
        public bool OwnsValidation(GuiSmokeLedgerValidationSnapshot value)
        { return Object.ReferenceEquals(original, this) && value != null && Object.ReferenceEquals(Validation, value); }
        public bool LiveProofEligible { get { return false; } }
        public bool NativeWriterCustodyEstablished { get { return false; } }
        public bool PublicationCompletionIssued { get { return false; } }
        public bool HasRetainedOutputs { get { return outputs != null && IsCurrent; } }
        internal GuiSmokeLedgerStreamCapture(GuiSmokeLedgerStreamingResult owner, GuiSmokeLedgerStreamRead input,
            string run, string scenario, long lines, long bookmark, GuiSmokeLedgerCapturedOutput retained,
            GuiSmokeLedgerCapturedOutput writer, GuiSmokeLedgerCapturedOutput semantic, GuiSmokeLedgerRetainedOutputs outputs)
        {
            original = this; this.owner = owner; source = input.Source;
            this.outputs = outputs;
            AcceptanceRunId = run; ScenarioRunId = scenario; SourceBytes = input.SourceBytes; SourceLines = lines; Bookmark = bookmark;
            Retained = retained; Writer = writer; Semantic = semantic;
            RetainedOrigins = input.Origins[0].AsReadOnly(); WriterOrigins = input.Origins[1].AsReadOnly(); SemanticOrigins = input.Origins[2].AsReadOnly();
            HasWriterValidationErrors = owner.WriterGapCount != 0 || owner.WriterDuplicateCount != 0 || owner.WriterIdentityMismatchCount != 0 ||
                owner.ChronologyBatchInvalid || owner.ChronologyInversionCount != 0 || owner.ChronologyCollisionCount != 0;
            CursorLineStarts = input.CursorLineStarts;
            Validation = GuiSmokeLedgerValidationSnapshot.TryCapture(this, owner, input.InitialWriters);
        }
        public bool IsCurrent
        {
            get
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return false;
                try
                {
                    // Source checks stay outside the output lock: owner retirement
                    // can synchronously close the outputs through cancellation.
                    if (owner.OwnsStreamCapture(this) && source.CanRead && source.Length == SourceBytes && source.Position == SourceBytes &&
                        (outputs == null || outputs.IsCurrent) && source.CanRead) return true;
                }
                catch { }
                Dispose(); return false;
            }
        }
        public bool MatchesOriginalSource(Stream value) { return Object.ReferenceEquals(source, value) && IsCurrent; }
        // Called only under the original adopter's bounded I/O interval. Do not
        // enter source/finalizer locks from inside the retained-output lock.
        internal GuiSmokeLedgerCapturedOutput CopySemantic(string destination, Func<bool> current)
        {
            return Object.ReferenceEquals(original, this) && !disposed && outputs != null ?
                outputs.CopySemantic(destination, Semantic, SemanticOrigins.Count, current) : null;
        }
        internal bool OwnsSemanticCopy(GuiSmokeLedgerCapturedOutput value)
        { return IsCurrent && outputs != null && outputs.OwnsSemanticCopy(value); }
        public void Dispose()
        {
            if (!Object.ReferenceEquals(original, this) || disposed) return;
            disposed = true;
            if (outputs != null) outputs.Dispose();
            source.Dispose();
        }
    }

    // Immutable processing facts only. Recovery markers, native writer membership,
    // cursor integrity, semantic-copy and persistence still need original gates.
    public sealed class GuiSmokeLedgerValidationSnapshot
    {
        public const int MaximumWriters = 64, MaximumDeferredBatches = 64, MaximumMetadataCodeUnits = 65536;
        private readonly GuiSmokeLedgerValidationSnapshot original;
        private readonly GuiSmokeLedgerStreamCapture owner;
        public ReadOnlyCollection<GuiSmokeLedgerValidationWriter> Writers { get; private set; }
        public ReadOnlyCollection<GuiSmokeLedgerPrefixWriter> InitialWriters { get; private set; }
        public ReadOnlyCollection<GuiSmokeLedgerValidationBatch> DeferredBatches { get; private set; }
        public long SourceLineVisitCount { get; private set; }
        public long WriterGapCount { get; private set; }
        public long WriterDuplicateCount { get; private set; }
        public long WriterIdentityMismatchCount { get; private set; }
        public long ChronologyInversionCount { get; private set; }
        public long ChronologyCollisionCount { get; private set; }
        public bool ChronologyBatchInvalid { get; private set; }
        public int WriterBoundaryMismatchCount { get; private set; }
        public int WriterDrainMissingCount { get; private set; }
        public int MetadataCodeUnits { get; private set; }
        public bool HasProcessorErrors { get { return WriterGapCount != 0 || WriterDuplicateCount != 0 || WriterIdentityMismatchCount != 0 ||
            ChronologyBatchInvalid || ChronologyInversionCount != 0 || ChronologyCollisionCount != 0; } }
        public bool HasWriterClosureErrors { get { return HasProcessorErrors || Writers.Count == 0 ||
            WriterBoundaryMismatchCount != 0 || WriterDrainMissingCount != 0; } }
        public bool IsCurrent { get { return Object.ReferenceEquals(original, this) && owner.OwnsValidation(this) && owner.IsCurrent; } }
        public bool LiveProofEligible { get { return false; } }
        public bool NativeWriterCustodyEstablished { get { return false; } }
        public bool PublicationCompletionIssued { get { return false; } }
        private GuiSmokeLedgerValidationSnapshot(GuiSmokeLedgerStreamCapture owner, GuiSmokeLedgerStreamingResult result)
        {
            original = this; this.owner = owner;
            SourceLineVisitCount = result.SourceLineVisitCount;
            WriterGapCount = result.WriterGapCount; WriterDuplicateCount = result.WriterDuplicateCount;
            WriterIdentityMismatchCount = result.WriterIdentityMismatchCount; ChronologyBatchInvalid = result.ChronologyBatchInvalid;
            ChronologyInversionCount = result.ChronologyInversionCount; ChronologyCollisionCount = result.ChronologyCollisionCount;
        }
        internal static GuiSmokeLedgerValidationSnapshot TryCapture(GuiSmokeLedgerStreamCapture owner, GuiSmokeLedgerStreamingResult result,
            ReadOnlyCollection<GuiSmokeLedgerPrefixWriter> initialWriters)
        {
            if (result.Writers.Length > MaximumWriters || result.DeferredBatches.Length > MaximumDeferredBatches ||
                result.Drains.Length > MaximumWriters) return null;
            var value = new GuiSmokeLedgerValidationSnapshot(owner, result);
            var writers = new List<GuiSmokeLedgerValidationWriter>();
            var batches = new List<GuiSmokeLedgerValidationBatch>();
            var drains = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var drain in result.Drains) drains.Add(drain.WriterId, drain.Sequences);
            foreach (var writer in result.Writers)
            {
                if (!value.AddText(writer.WriterId) || !value.AddText(writer.AcceptanceRunId) || !value.AddText(writer.ScenarioRunId)) return null;
                long[] sequences;
                drains.TryGetValue(writer.WriterId, out sequences);
                var captured = new GuiSmokeLedgerValidationWriter(writer, sequences);
                writers.Add(captured);
                if (captured.FirstSequence != 1 || captured.LastSequence < 1 || captured.ObservedCount != captured.LastSequence ||
                    captured.AcceptanceRunId != owner.AcceptanceRunId || captured.ScenarioRunId != owner.ScenarioRunId)
                    value.WriterBoundaryMismatchCount++;
                if (captured.DrainCount != 1 || captured.DrainSequence != captured.LastSequence) value.WriterDrainMissingCount++;
            }
            foreach (var batch in result.DeferredBatches)
            {
                if (!value.AddText(batch.WriterId) || !value.AddText(batch.PayloadSha256)) return null;
                batches.Add(new GuiSmokeLedgerValidationBatch(batch));
            }
            // Prefix strings are the same immutable writer/scope strings already
            // counted above, not another caller-controlled metadata allocation.
            value.InitialWriters = initialWriters;
            value.Writers = writers.AsReadOnly(); value.DeferredBatches = batches.AsReadOnly();
            return value;
        }
        private bool AddText(string text)
        {
            if (!GuiSmokeLedgerNativeCapture.ValidText(text, 1024)) return false;
            MetadataCodeUnits += text.Length;
            return MetadataCodeUnits <= MaximumMetadataCodeUnits;
        }
    }

    public sealed class GuiSmokeLedgerPrefixWriter
    {
        public string WriterId { get; private set; }
        public string AcceptanceRunId { get; private set; }
        public string ScenarioRunId { get; private set; }
        public long FirstSequence { get; private set; }
        public long LastSequence { get; private set; }
        public long ObservedCount { get; private set; }
        internal GuiSmokeLedgerPrefixWriter(GuiSmokeLedgerWriterResult writer)
        {
            WriterId = writer.WriterId; AcceptanceRunId = writer.AcceptanceRunId; ScenarioRunId = writer.ScenarioRunId;
            FirstSequence = writer.FirstSequence; LastSequence = writer.LastSequence; ObservedCount = writer.ObservedCount;
        }
    }

    public sealed class GuiSmokeLedgerValidationWriter
    {
        public string WriterId { get; private set; }
        public string AcceptanceRunId { get; private set; }
        public string ScenarioRunId { get; private set; }
        public long FirstSequence { get; private set; }
        public long LastSequence { get; private set; }
        public long ObservedCount { get; private set; }
        public int DrainCount { get; private set; }
        public long DrainSequence { get; private set; }
        internal GuiSmokeLedgerValidationWriter(GuiSmokeLedgerWriterResult writer, long[] drains)
        {
            WriterId = writer.WriterId; AcceptanceRunId = writer.AcceptanceRunId; ScenarioRunId = writer.ScenarioRunId;
            FirstSequence = writer.FirstSequence; LastSequence = writer.LastSequence; ObservedCount = writer.ObservedCount;
            DrainCount = drains == null ? 0 : drains.Length; DrainSequence = DrainCount == 1 ? drains[0] : 0;
        }
    }

    public sealed class GuiSmokeLedgerValidationBatch
    {
        public string WriterId { get; private set; }
        public long BatchGeneration { get; private set; }
        public long BatchFirstSequence { get; private set; }
        public long BatchLastSequence { get; private set; }
        public long AttemptUtcTicks { get; private set; }
        public long MinSequence { get; private set; }
        public long MaxSequence { get; private set; }
        public long LineCount { get; private set; }
        public bool TupleValid { get; private set; }
        public string PayloadSha256 { get; private set; }
        internal GuiSmokeLedgerValidationBatch(GuiSmokeLedgerBatchResult batch)
        {
            WriterId = batch.WriterId; BatchGeneration = batch.BatchGeneration;
            BatchFirstSequence = batch.BatchFirstSequence; BatchLastSequence = batch.BatchLastSequence;
            AttemptUtcTicks = batch.AttemptUtcTicks; MinSequence = batch.MinSequence; MaxSequence = batch.MaxSequence;
            LineCount = batch.LineCount; TupleValid = batch.TupleValid; PayloadSha256 = batch.PayloadSha256;
        }
    }

    // Exactly three read-only duplicates of this invocation's original output
    // handles. No pathname reopen, external handle import or public handle export.
    internal sealed partial class GuiSmokeLedgerRetainedOutputs : IDisposable
    {
        private readonly GuiSmokeLedgerRetainedOutputs original;
        private readonly object sync = new object();
        private readonly FileStream[] files = new FileStream[3];
        private readonly GuiSmokeLedgerCapturedOutput[] metadata = new GuiSmokeLedgerCapturedOutput[3];
        private readonly CancellationTokenRegistration cancellation;
        private bool disposed, sealedOutputs;
        private bool semanticCopyAttempted;
        private FileStream semanticCopy;
        private GuiSmokeLedgerCapturedOutput semanticCopyInfo;
        internal GuiSmokeLedgerRetainedOutputs(CancellationToken lifetime)
        { original = this; cancellation = lifetime.Register(Close); }
        internal void Seal(FileStream retained, FileStream writer, FileStream semantic,
            GuiSmokeLedgerCapturedOutput retainedInfo, GuiSmokeLedgerCapturedOutput writerInfo, GuiSmokeLedgerCapturedOutput semanticInfo)
        {
            lock (sync)
            {
                if (disposed || sealedOutputs) throw new InvalidDataException("stream_outputs_not_current");
                var originals = new[] { retained, writer, semantic };
                var descriptions = new[] { retainedInfo, writerInfo, semanticInfo };
                for (int i = 0; i < 3; i++)
                {
                    SafeFileHandle handle = null;
                    try
                    {
                        // Duplicate the same file object with reduced access. Its
                        // original read-only sharing continues denying write/delete
                        // even after the processor closes its writable handle.
                        if (descriptions[i] == null || !DuplicateHandle(GetCurrentProcess(), originals[i].SafeFileHandle,
                            GetCurrentProcess(), out handle, 0x80000000, false, 0))
                            throw new IOException("stream_output_duplicate_failed");
                        files[i] = new FileStream(handle, FileAccess.Read); handle = null;
                        metadata[i] = descriptions[i];
                        if (!metadata[i].Matches(files[i])) throw new InvalidDataException("stream_output_identity_mismatch");
                    }
                    finally { if (handle != null) handle.Dispose(); }
                }
                sealedOutputs = true;
            }
        }
        internal bool IsCurrent
        {
            get
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed || !sealedOutputs) return false;
                    try
                    {
                        for (int i = 0; i < 3; i++)
                            if (!files[i].CanRead || files[i].CanWrite || !metadata[i].Matches(files[i])) { Close(); return false; }
                        if (semanticCopy != null && (!semanticCopy.CanRead || semanticCopy.CanWrite ||
                            semanticCopyInfo == null || !semanticCopyInfo.Matches(semanticCopy))) { Close(); return false; }
                        return true;
                    }
                    catch { Close(); return false; }
                }
            }
        }
        internal bool OwnsSemanticCopy(GuiSmokeLedgerCapturedOutput value)
        { lock (sync) { return value != null && Object.ReferenceEquals(value, semanticCopyInfo) && semanticCopy != null && IsCurrent; } }

        internal GuiSmokeLedgerCapturedOutput CopySemantic(string destination, GuiSmokeLedgerCapturedOutput expected,
            int originCount, Func<bool> current)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this)) return null;
                bool success = false; SafeFileHandle duplicate = null;
                try
                {
                    if (!IsCurrent || semanticCopyAttempted || !Object.ReferenceEquals(expected, metadata[2]) || current == null) return null;
                    semanticCopyAttempted = true;
                    if (expected.LineCount != originCount || originCount < 0 || originCount > GuiSmokeLedgerStreamRead.MaximumOriginEntries ||
                        expected.ByteCount < 0 || expected.ByteCount > GuiSmokeLedgerStreamRead.MaximumSourceBytes +
                            (long)GuiSmokeLedgerStreamRead.MaximumLines * Environment.NewLine.Length || !current()) return null;
                    var input = files[2]; input.Position = 0;
                    // This optional gate retains creation custody, not a path-only
                    // publication. A failed/partial file is never an owned result.
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 32768))
                    using (var hash = SHA256.Create())
                    {
                        var buffer = new byte[32768]; long bytes = 0, feeds = 0; int last = -1;
                        while (bytes < expected.ByteCount)
                        {
                            if (!current() || !IsCurrent) return null;
                            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, expected.ByteCount - bytes));
                            if (read == 0 || !current()) return null;
                            output.Write(buffer, 0, read); hash.TransformBlock(buffer, 0, read, buffer, 0);
                            for (int i = 0; i < read; i++) if (buffer[i] == 10) feeds++;
                            bytes += read; last = buffer[read - 1];
                        }
                        if (!current() || !IsCurrent || input.ReadByte() != -1 || input.Position != expected.ByteCount) return null;
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        long lines = bytes == 0 ? 0 : feeds + (last == 10 ? 0 : 1);
                        string digest = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                        if (bytes != expected.ByteCount || lines != expected.LineCount || digest != expected.Sha256) return null;
                        output.Flush(true);
                        semanticCopyInfo = GuiSmokeLedgerCapturedOutput.Capture(output, destination, digest, lines, bytes);
                        if (!current() || !IsCurrent || semanticCopyInfo == null || semanticCopyInfo.FileIdentity == expected.FileIdentity ||
                            !DuplicateHandle(GetCurrentProcess(), output.SafeFileHandle, GetCurrentProcess(), out duplicate, 0x80000000, false, 0)) return null;
                        semanticCopy = new FileStream(duplicate, FileAccess.Read); duplicate = null;
                    }
                    success = current() && OwnsSemanticCopy(semanticCopyInfo);
                    return success ? semanticCopyInfo : null;
                }
                catch { return null; }
                finally { if (duplicate != null) duplicate.Dispose(); if (!success) Close(); }
            }
        }
        // The cancellation callback only closes native resources. It never
        // enters source/finalizer ownership locks or unregisters itself.
        private void Close()
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return;
                disposed = true;
                foreach (var file in files) if (file != null) file.Dispose();
                if (semanticCopy != null) semanticCopy.Dispose();
            }
        }
        public void Dispose() { if (!Object.ReferenceEquals(original, this)) return; Close(); cancellation.Dispose(); }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr process, SafeFileHandle source, IntPtr target,
            out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }

    internal sealed class GuiSmokeLedgerStreamRead : TextReader
    {
        internal const int MaximumLines = 262144, MaximumLineCodeUnits = 262144, MaximumOriginEntries = 262144;
        internal const long MaximumSourceBytes = 268435456;
        internal readonly Stream Source;
        internal readonly long SourceBytes;
        internal readonly List<int>[] Origins = { new List<int>(), new List<int>(), new List<int>() };
        private readonly StreamReader reader;
        private readonly GuiSmokeLedgerLfRead cursorReader;
        internal ReadOnlyCollection<long> CursorLineStarts { get { return cursorReader == null ? null : cursorReader.LineStarts.AsReadOnly(); } }
        internal ReadOnlyCollection<GuiSmokeLedgerPrefixWriter> InitialWriters;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly char[] buffer = new char[32768];
        private int offset, count, originCount;
        private bool skipLf;
        internal GuiSmokeLedgerStreamRead(Stream source, bool cursorMode = false)
        {
            Source = source; SourceBytes = source.Length;
            if (!source.CanRead || source.CanWrite || source.Position != 0 || SourceBytes < 1 || SourceBytes > MaximumSourceBytes)
                throw new InvalidDataException("stream_source_not_original_start");
            if (cursorMode) cursorReader = new GuiSmokeLedgerLfRead(source, CheckCurrent);
            else reader = new StreamReader(source, new UTF8Encoding(false, true), true, 32768, true);
        }
        internal void CaptureInitialWriters(ICollection<GuiSmokeLedgerWriterResult> writers)
        {
            if (cursorReader == null) return;
            if (InitialWriters != null || writers.Count > GuiSmokeLedgerValidationSnapshot.MaximumWriters)
                throw new InvalidDataException("cursor_initial_writer_limit");
            var prefix = new List<GuiSmokeLedgerPrefixWriter>();
            foreach (var writer in writers) prefix.Add(new GuiSmokeLedgerPrefixWriter(writer));
            InitialWriters = prefix.AsReadOnly();
        }
        internal void CheckCurrent()
        { if (clock.ElapsedMilliseconds > 5000 || !Source.CanRead) throw new InvalidDataException("stream_source_not_current"); }
        internal void AddOrigin(int role, long line)
        {
            CheckCurrent();
            if (originCount == MaximumOriginEntries) throw new InvalidDataException("stream_source_origin_limit");
            Origins[role].Add(checked((int)line)); originCount++;
        }
        private bool Fill()
        {
            CheckCurrent();
            if (offset != count) return true;
            count = reader.Read(buffer, 0, buffer.Length); offset = 0; CheckCurrent(); return count != 0;
        }
        public override string ReadLine()
        {
            CheckCurrent();
            if (cursorReader != null) return cursorReader.ReadLine();
            if (skipLf) { if (Fill() && buffer[offset] == '\n') offset++; skipLf = false; }
            StringBuilder line = null;
            while (Fill())
            {
                int start = offset;
                while (offset < count && buffer[offset] != '\r' && buffer[offset] != '\n') offset++;
                int length = offset - start;
                if ((line == null ? 0 : line.Length) + length > MaximumLineCodeUnits)
                    throw new InvalidDataException("stream_source_line_limit");
                if (line == null) line = new StringBuilder(length);
                line.Append(buffer, start, length);
                if (offset < count) { skipLf = buffer[offset++] == '\r'; return line.ToString(); }
            }
            return line == null ? null : line.ToString();
        }
        protected override void Dispose(bool disposing) { if (disposing && reader != null) reader.Dispose(); base.Dispose(disposing); }
    }

    // Owned ledger cursors require final LF. Native primary semantic copies
    // explicitly allow their original final record without a separator. Legacy
    // and generic readers retain their CR/LF behavior. UTF-8 tails stay strict.
    internal sealed class GuiSmokeLedgerLfRead
    {
        internal readonly List<long> LineStarts = new List<long>();
        private readonly Stream source;
        private readonly Action current;
        private readonly byte[] buffer = new byte[32768];
        private readonly UTF8Encoding utf8 = new UTF8Encoding(false, true);
        private readonly bool allowFinalLine;
        private int offset, count;
        private long position, lineStart;
        internal GuiSmokeLedgerLfRead(Stream source, Action current, bool allowFinalLine = false)
        { this.source = source; this.current = current; this.allowFinalLine = allowFinalLine; }
        internal string ReadLine()
        {
            using (var bytes = new MemoryStream())
            {
                while (true)
                {
                    current();
                    if (offset == count)
                    {
                        count = source.Read(buffer, 0, buffer.Length); offset = 0;
                        if (count == 0)
                        {
                            if (bytes.Length != 0)
                            {
                                if (!allowFinalLine) throw new InvalidDataException("cursor_unterminated_line");
                                return FinishLine(bytes);
                            }
                            return null;
                        }
                    }
                    int lf = Array.IndexOf(buffer, (byte)10, offset, count - offset);
                    int take = (lf < 0 ? count : lf) - offset;
                    if (bytes.Length + take > 4L * GuiSmokeLedgerStreamRead.MaximumLineCodeUnits + 3)
                        throw new InvalidDataException("stream_source_line_limit");
                    bytes.Write(buffer, offset, take); offset += take; position += take;
                    if (lf < 0) continue;
                    offset++; position++;
                    return FinishLine(bytes);
                }
            }
        }
        private string FinishLine(MemoryStream bytes)
        {
            if (LineStarts.Count == GuiSmokeLedgerStreamRead.MaximumLines) throw new InvalidDataException("cursor_line_limit");
            string line = utf8.GetString(bytes.ToArray());
            if (lineStart == 0 && line.Length != 0 && line[0] == '\ufeff') line = line.Substring(1);
            if (line.EndsWith("\r", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);
            if (line.Length > GuiSmokeLedgerStreamRead.MaximumLineCodeUnits) throw new InvalidDataException("stream_source_line_limit");
            LineStarts.Add(lineStart); lineStart = position; current(); return line;
        }
    }

    public sealed class GuiSmokeLedgerWriterResult
    {
        public GuiSmokeLedgerWriterResult() { WriterId = ""; AcceptanceRunId = ""; ScenarioRunId = ""; }
        public string WriterId { get; set; }
        public string AcceptanceRunId { get; set; }
        public string ScenarioRunId { get; set; }
        public long FirstSequence { get; set; }
        public long LastSequence { get; set; }
        public long ObservedCount { get; set; }
    }

    public sealed class GuiSmokeLedgerBatchResult
    {
        public GuiSmokeLedgerBatchResult() { WriterId = ""; PayloadSha256 = ""; }
        public string WriterId { get; set; }
        public long BatchGeneration { get; set; }
        public long BatchFirstSequence { get; set; }
        public long BatchLastSequence { get; set; }
        public long AttemptUtcTicks { get; set; }
        public long MinSequence { get; set; }
        public long MaxSequence { get; set; }
        public long LineCount { get; set; }
        public bool TupleValid { get; set; }
        public string PayloadSha256 { get; set; }
    }

    public sealed class GuiSmokeLedgerDrainResult
    {
        public GuiSmokeLedgerDrainResult() { WriterId = ""; Sequences = new long[0]; }
        public string WriterId { get; set; }
        public long[] Sequences { get; set; }
    }

    public sealed class GuiSmokeLedgerStreamingResult
    {
        private readonly GuiSmokeLedgerStreamingResult original;
        private GuiSmokeLedgerStreamCapture streamCapture;
        public GuiSmokeLedgerStreamingResult()
        {
            original = this;
            RetainedSha256 = "";
            WriterSha256 = "";
            SemanticSha256 = "";
            Writers = new GuiSmokeLedgerWriterResult[0];
            DeferredBatches = new GuiSmokeLedgerBatchResult[0];
            Drains = new GuiSmokeLedgerDrainResult[0];
        }
        public string RetainedSha256 { get; set; }
        public long RetainedLineCount { get; set; }
        public long RetainedByteCount { get; set; }
        public string WriterSha256 { get; set; }
        public long WriterLineCount { get; set; }
        public long WriterByteCount { get; set; }
        public string SemanticSha256 { get; set; }
        public long SemanticLineCount { get; set; }
        public long SemanticByteCount { get; set; }
        public long SourceLineVisitCount { get; set; }
        public long WriterGapCount { get; set; }
        public long WriterDuplicateCount { get; set; }
        public long WriterIdentityMismatchCount { get; set; }
        public long ChronologyInversionCount { get; set; }
        public long ChronologyCollisionCount { get; set; }
        public bool ChronologyBatchInvalid { get; set; }
        public GuiSmokeLedgerWriterResult[] Writers { get; set; }
        public GuiSmokeLedgerBatchResult[] DeferredBatches { get; set; }
        public GuiSmokeLedgerDrainResult[] Drains { get; set; }
        private GuiSmokeLedgerNativeCapture nativeCapture;
        private object nativeCaptureIdentity;
        public GuiSmokeLedgerNativeCapture NativeCapture { get { return nativeCapture; } }
        public GuiSmokeLedgerStreamCapture StreamCapture { get { return streamCapture; } }
        public bool OwnsStreamCapture(GuiSmokeLedgerStreamCapture capture)
        { return Object.ReferenceEquals(original, this) && capture != null && Object.ReferenceEquals(streamCapture, capture); }
        internal void CaptureStream(GuiSmokeLedgerStreamRead input, string run, string scenario, long lines, long bookmark,
            GuiSmokeLedgerCapturedOutput retained, GuiSmokeLedgerCapturedOutput writer, GuiSmokeLedgerCapturedOutput semantic,
            GuiSmokeLedgerRetainedOutputs outputs)
        { streamCapture = new GuiSmokeLedgerStreamCapture(this, input, run, scenario, lines, bookmark, retained, writer, semantic, outputs); }
        public bool OwnsNativeCapture(GuiSmokeLedgerNativeCapture capture)
        { return capture != null && Object.ReferenceEquals(nativeCapture, capture) && capture.IsOwnedBy(nativeCaptureIdentity); }
        internal void CaptureNativeOutputs(string sourcePath, string run, string scenario, long frozenLines, long bookmark,
            GuiSmokeLedgerCapturedOutput retained, GuiSmokeLedgerCapturedOutput writer, GuiSmokeLedgerCapturedOutput semantic,
            Stopwatch captureClock)
        {
            var identity = new object();
            nativeCapture = GuiSmokeLedgerNativeCapture.TryCreate(identity, this, sourcePath, run, scenario, frozenLines, bookmark,
                retained, writer, semantic, captureClock);
            if (nativeCapture != null) nativeCaptureIdentity = identity;
        }
    }

    // Provenance of this processor's writes, not a finalizer validation receipt,
    // current-file hash verification, retained-worker binding or authority token.
    public sealed class GuiSmokeLedgerNativeCapture
    {
        public const int MaximumWriters = 64;
        public const int MaximumMetadataCodeUnits = 16384;
        public const int MaximumCaptureMilliseconds = 250;
        private readonly object ownerIdentity;
        public string SourcePath { get; private set; }
        public string AcceptanceRunId { get; private set; }
        public string ScenarioRunId { get; private set; }
        public long FrozenLineCount { get; private set; }
        public long Bookmark { get; private set; }
        public GuiSmokeLedgerCapturedOutput Retained { get; private set; }
        public GuiSmokeLedgerCapturedOutput Writer { get; private set; }
        public GuiSmokeLedgerCapturedOutput Semantic { get; private set; }
        public ReadOnlyCollection<GuiSmokeLedgerCapturedWriter> Writers { get; private set; }
        public int MetadataCodeUnits { get; private set; }
        private GuiSmokeLedgerNativeCapture(object identity) { ownerIdentity = identity; }
        internal bool IsOwnedBy(object identity) { return Object.ReferenceEquals(ownerIdentity, identity); }
        internal static bool ValidText(string value, int maximum)
        { return !String.IsNullOrWhiteSpace(value) && value != "(none)" && value.Length <= maximum && value.IndexOf('\0') < 0; }
        internal static GuiSmokeLedgerNativeCapture TryCreate(object identity, GuiSmokeLedgerStreamingResult result, string sourcePath,
            string run, string scenario, long frozenLines, long bookmark, GuiSmokeLedgerCapturedOutput retained,
            GuiSmokeLedgerCapturedOutput writer, GuiSmokeLedgerCapturedOutput semantic, Stopwatch clock)
        {
            if (clock == null || clock.ElapsedMilliseconds > MaximumCaptureMilliseconds || retained == null || writer == null ||
                !ValidText(sourcePath, 4096) || !ValidText(run, 1024) || !ValidText(scenario, 1024) ||
                frozenLines < 1 || bookmark < 0 || bookmark > frozenLines ||
                result.SourceLineVisitCount != frozenLines || result.WriterGapCount != 0 || result.WriterDuplicateCount != 0 ||
                result.WriterIdentityMismatchCount != 0 || result.ChronologyBatchInvalid || result.ChronologyInversionCount != 0 ||
                result.ChronologyCollisionCount != 0 || result.Writers.Length < 1 || result.Writers.Length > MaximumWriters) return null;
            sourcePath = Path.GetFullPath(sourcePath);
            if (sourcePath.Length > 4096) return null;
            int units = sourcePath.Length + run.Length + scenario.Length + retained.MetadataCodeUnits + writer.MetadataCodeUnits +
                (semantic == null ? 0 : semantic.MetadataCodeUnits);
            var writers = new List<GuiSmokeLedgerCapturedWriter>();
            foreach (var entry in result.Writers)
            {
                if (entry == null || !ValidText(entry.WriterId, 1024) || entry.AcceptanceRunId != run || entry.ScenarioRunId != scenario ||
                    entry.FirstSequence != 1 || entry.LastSequence < 1 || entry.ObservedCount != entry.LastSequence ||
                    clock.ElapsedMilliseconds > MaximumCaptureMilliseconds) return null;
                units += entry.WriterId.Length;
                if (units > MaximumMetadataCodeUnits) return null;
                writers.Add(new GuiSmokeLedgerCapturedWriter(entry));
            }
            if (clock.ElapsedMilliseconds > MaximumCaptureMilliseconds) return null;
            return new GuiSmokeLedgerNativeCapture(identity)
            {
                SourcePath = sourcePath, AcceptanceRunId = run, ScenarioRunId = scenario, FrozenLineCount = frozenLines,
                Bookmark = bookmark, Retained = retained, Writer = writer, Semantic = semantic,
                Writers = writers.AsReadOnly(), MetadataCodeUnits = units
            };
        }
    }

    public sealed class GuiSmokeLedgerCapturedWriter
    {
        public string WriterId { get; private set; }
        public long FirstSequence { get; private set; }
        public long LastSequence { get; private set; }
        public long ObservedCount { get; private set; }
        internal GuiSmokeLedgerCapturedWriter(GuiSmokeLedgerWriterResult writer)
        { WriterId = writer.WriterId; FirstSequence = writer.FirstSequence; LastSequence = writer.LastSequence; ObservedCount = writer.ObservedCount; }
    }

    public sealed class GuiSmokeLedgerCapturedOutput
    {
        public string CreationPath { get; private set; }
        public string Sha256 { get; private set; }
        public string FileIdentity { get; private set; }
        public long ByteCount { get; private set; }
        public long LineCount { get; private set; }
        internal int MetadataCodeUnits { get { return CreationPath.Length + Sha256.Length + FileIdentity.Length; } }
        private GuiSmokeLedgerCapturedOutput() { }
        internal bool Matches(FileStream stream)
        {
            FileInformation info;
            return stream != null && !stream.SafeFileHandle.IsClosed && GetFileInformationByHandle(stream.SafeFileHandle, out info) &&
                (info.Attributes & 16) == 0 && ((long)info.SizeHigh << 32 | info.SizeLow) == ByteCount &&
                info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") == FileIdentity;
        }
        internal static GuiSmokeLedgerCapturedOutput Capture(FileStream stream, string path, string hash, long lines, long bytes)
        {
            if (!GuiSmokeLedgerNativeCapture.ValidText(path, 4096) || lines < 0 || bytes < 0 ||
                stream.Length != bytes) return null;
            path = Path.GetFullPath(path);
            if (path.Length > 4096) return null;
            FileInformation info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) return null;
            return new GuiSmokeLedgerCapturedOutput
            {
                CreationPath = path, Sha256 = hash, ByteCount = bytes, LineCount = lines,
                FileIdentity = info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8")
            };
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    }

    public sealed class GuiSmokeLedgerFilteredLine
    {
        public GuiSmokeLedgerFilteredLine() { Line = ""; }
        public long LineIndex { get; set; }
        public string Line { get; set; }
    }

    public sealed class GuiSmokeLedgerFilteredRangeResult
    {
        public GuiSmokeLedgerFilteredRangeResult()
        {
            Lines = new GuiSmokeLedgerFilteredLine[0];
        }

        public long SourceLineVisitCount { get; set; }
        public GuiSmokeLedgerFilteredLine[] Lines { get; set; }
    }

    public static class GuiSmokeLogLedgerStreamingProcessor
    {
        // The current shared catalog exceeds 1024 names. Aggregate metadata
        // remains capped at 65536 code units, including deferred batch keys.
        public const int MaximumSemanticEventNames = 2048;
        private static readonly HashSet<string> WriterFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "event", "acceptance_run_id", "scenario_run_id", "log_writer_id",
            "log_writer_sequence", "log_writer_batch_generation",
            "log_writer_batch_first_sequence", "log_writer_batch_last_sequence",
            "log_writer_attempt_utc_ticks"
        };

        private sealed class ParsedLine
        {
            public readonly Dictionary<string, string> Fields =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> WriterIds = new List<string>();
            public bool DuplicateWriterEvidenceField;
        }

        private sealed class BatchState
        {
            public string WriterId = "";
            public long Generation;
            public long First;
            public long Last;
            public long Ticks;
            public long Min;
            public long Max;
            public long LastLine;
            public long Count;
            public bool Valid;
            public bool CapturePayload;
            public readonly List<string> Payload = new List<string>();
        }

        public static GuiSmokeLedgerFilteredRangeResult SelectEvents(
            string sourcePath,
            long startOffset,
            long startIndex,
            long endIndex,
            string[] eventNames,
            bool coalesceDataPathProjectionEvents)
        {
            if (startOffset < 0 || startIndex < 0 || endIndex < startIndex)
                throw new InvalidDataException("filtered_range_invalid");

            var selectedNames = new HashSet<string>(
                eventNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var selected = new List<GuiSmokeLedgerFilteredLine>();
            var selectedDataPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new GuiSmokeLedgerFilteredRangeResult();
            var utf8 = new UTF8Encoding(false, true);

            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            {
                input.Position = startOffset;
                using (var reader = new StreamReader(input, utf8, true, 65536, true))
                {
                    for (long lineIndex = startIndex; lineIndex < endIndex; lineIndex++)
                    {
                        string line = reader.ReadLine();
                        if (line == null)
                            throw new InvalidDataException(
                                "filtered_range_short_read:index=" +
                                lineIndex.ToString(CultureInfo.InvariantCulture) +
                                ";end=" + endIndex.ToString(CultureInfo.InvariantCulture));
                        result.SourceLineVisitCount++;
                        string eventName = ExtractEventName(line);
                        if (selectedNames.Contains(eventName))
                        {
                            if (coalesceDataPathProjectionEvents && IsDataPathEvent(eventName))
                            {
                                ParsedLine parsed = Parse(line);
                                string projectionKey = DataPathProjectionKey(parsed);
                                if (projectionKey.Length == 0 || !selectedDataPathKeys.Add(projectionKey))
                                    continue;
                            }
                            selected.Add(new GuiSmokeLedgerFilteredLine
                            {
                                LineIndex = lineIndex,
                                Line = line
                            });
                        }
                    }
                }
            }

            result.Lines = selected.ToArray();
            return result;
        }

        public static GuiSmokeLedgerStreamingResult Process(
            string sourcePath, long frozenLineCount, long bookmark, string retainedTempPath, string writerTempPath,
            string expectedAcceptanceRunId, string expectedScenarioRunId, string[] deferredBatchKeys,
            string semanticTempPath = null, string[] semanticEventNames = null)
        {
            return ProcessCore(sourcePath, frozenLineCount, bookmark, retainedTempPath, writerTempPath,
                expectedAcceptanceRunId, expectedScenarioRunId, deferredBatchKeys, semanticTempPath, semanticEventNames, false);
        }

        public static GuiSmokeLedgerStreamingResult ProcessWithNativeCapture(
            string sourcePath, long frozenLineCount, long bookmark, string retainedTempPath, string writerTempPath,
            string expectedAcceptanceRunId, string expectedScenarioRunId, string[] deferredBatchKeys,
            string semanticTempPath = null, string[] semanticEventNames = null)
        {
            return ProcessCore(sourcePath, frozenLineCount, bookmark, retainedTempPath, writerTempPath,
                expectedAcceptanceRunId, expectedScenarioRunId, deferredBatchKeys, semanticTempPath, semanticEventNames, true);
        }

        // Stream provenance only: a generic caller stream is not frontier custody.
        // The original GUI integration separately supplies its owned reader.
        public static GuiSmokeLedgerStreamingResult ProcessFromStream(
            Stream source, long frozenLineCount, long bookmark, string retainedTempPath, string writerTempPath,
            string expectedAcceptanceRunId, string expectedScenarioRunId, string[] deferredBatchKeys,
            string semanticTempPath, string[] semanticEventNames)
        {
            return ProcessStreamCore(source, frozenLineCount, bookmark, retainedTempPath, writerTempPath,
                expectedAcceptanceRunId, expectedScenarioRunId, deferredBatchKeys, semanticTempPath, semanticEventNames,
                false, CancellationToken.None);
        }

        // The token controls cleanup only. It does not establish GUI custody.
        public static GuiSmokeLedgerStreamingResult ProcessFromStreamWithRetainedOutputs(
            Stream source, long frozenLineCount, long bookmark, string retainedTempPath, string writerTempPath,
            string expectedAcceptanceRunId, string expectedScenarioRunId, string[] deferredBatchKeys,
            string semanticTempPath, string[] semanticEventNames, CancellationToken outputLifetime)
        {
            return ProcessStreamCore(source, frozenLineCount, bookmark, retainedTempPath, writerTempPath,
                expectedAcceptanceRunId, expectedScenarioRunId, deferredBatchKeys, semanticTempPath, semanticEventNames,
                true, outputLifetime);
        }

#if NLINK_FRONTIER_LEDGER
        internal static GuiSmokeLedgerStreamingResult ProcessOwnedCursor(
            Stream source, long lines, long bookmark, string retained, string writer, string run, string scenario,
            string[] keys, string semantic, string[] events, CancellationToken lifetime)
        {
            return ProcessStreamCore(source, lines, bookmark, retained, writer, run, scenario, keys, semantic, events, true, lifetime, true);
        }
#endif

        private static GuiSmokeLedgerStreamingResult ProcessStreamCore(
            Stream source, long frozenLineCount, long bookmark, string retainedTempPath, string writerTempPath,
            string expectedAcceptanceRunId, string expectedScenarioRunId, string[] deferredBatchKeys,
            string semanticTempPath, string[] semanticEventNames, bool retainOutputs, CancellationToken outputLifetime, bool cursorMode = false)
        {
            if (retainOutputs) outputLifetime.ThrowIfCancellationRequested();
            if (source == null || frozenLineCount < 1 || frozenLineCount > GuiSmokeLedgerStreamRead.MaximumLines ||
                !GuiSmokeLedgerNativeCapture.ValidText(expectedAcceptanceRunId, 1024) ||
                !GuiSmokeLedgerNativeCapture.ValidText(expectedScenarioRunId, 1024) ||
                !GuiSmokeLedgerNativeCapture.ValidText(retainedTempPath, 4096) ||
                !GuiSmokeLedgerNativeCapture.ValidText(writerTempPath, 4096) ||
                !GuiSmokeLedgerNativeCapture.ValidText(semanticTempPath, 4096) ||
                (deferredBatchKeys != null && deferredBatchKeys.Length > 64) ||
                (semanticEventNames != null && semanticEventNames.Length > MaximumSemanticEventNames))
                throw new InvalidDataException("stream_source_bounds");
            int metadata = 0;
            foreach (var values in new[] { deferredBatchKeys, semanticEventNames })
                if (values != null) foreach (string value in values)
                {
                    if (!GuiSmokeLedgerNativeCapture.ValidText(value, 1024) || (metadata += value.Length) > 65536)
                        throw new InvalidDataException("stream_source_metadata_limit");
                }
            return ProcessCore(null, frozenLineCount, bookmark, retainedTempPath, writerTempPath,
                expectedAcceptanceRunId, expectedScenarioRunId, deferredBatchKeys, semanticTempPath, semanticEventNames, true, source,
                retainOutputs, outputLifetime, cursorMode);
        }

        private static GuiSmokeLedgerStreamingResult ProcessCore(
            string sourcePath,
            long frozenLineCount,
            long bookmark,
            string retainedTempPath,
            string writerTempPath,
            string expectedAcceptanceRunId,
            string expectedScenarioRunId,
            string[] deferredBatchKeys,
            string semanticTempPath,
            string[] semanticEventNames,
            bool captureNativeOutputs, Stream suppliedSource = null, bool retainOutputs = false,
            CancellationToken outputLifetime = default(CancellationToken), bool cursorMode = false)
        {
            if (frozenLineCount < 0 || bookmark < 0 || bookmark > frozenLineCount)
                throw new InvalidDataException("writer_evidence_invalid_frozen_range");

            var deferredKeys = new HashSet<string>(
                deferredBatchKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var semanticNames = new HashSet<string>(
                semanticEventNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            bool writeSemantic = !String.IsNullOrWhiteSpace(semanticTempPath);
            var writers = new Dictionary<string, GuiSmokeLedgerWriterResult>(StringComparer.OrdinalIgnoreCase);
            var unknownAfterBookmark = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentBatches = new Dictionary<string, BatchState>(StringComparer.OrdinalIgnoreCase);
            var deferredBatches = new Dictionary<string, BatchState>(StringComparer.OrdinalIgnoreCase);
            var seenDeferredBatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var previousTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var drains = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var result = new GuiSmokeLedgerStreamingResult();
            long retainedLineCount = 0;
            long retainedByteCount = 0;
            long writerLineCount = 0;
            long writerByteCount = 0;
            long semanticLineCount = 0;
            long semanticByteCount = 0;
            var utf8 = new UTF8Encoding(false, true);
            var separator = utf8.GetBytes(Environment.NewLine);
            GuiSmokeLedgerCapturedOutput retainedCapture = null, writerCapture = null, semanticCapture = null;
            Stopwatch captureClock = null;
            var streamRead = suppliedSource == null ? null : new GuiSmokeLedgerStreamRead(suppliedSource, cursorMode);
            var outputLease = retainOutputs ? new GuiSmokeLedgerRetainedOutputs(outputLifetime) : null;
            bool outputsTransferred = false;
            try
            {

            using (var retained = NewOutput(retainedTempPath, retainOutputs))
            using (var writer = NewOutput(writerTempPath, retainOutputs))
            using (var semantic = writeSemantic ? (Stream)NewOutput(semanticTempPath, retainOutputs) : Stream.Null)
            using (var retainedHash = SHA256.Create())
            using (var writerHash = SHA256.Create())
            using (var semanticHash = SHA256.Create())
            using (var input = suppliedSource == null ? new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan) : null)
            using (var reader = streamRead == null ? (TextReader)new StreamReader(input, utf8, true, 1024 * 1024) : streamRead)
            {
                for (long index = 0; index < frozenLineCount; index++)
                {
                    if (streamRead != null && index == bookmark) streamRead.CaptureInitialWriters(writers.Values);
                    string line = reader.ReadLine();
                    if (line == null)
                        throw new InvalidDataException("writer_evidence_source_short_read:index=" +
                            index.ToString(CultureInfo.InvariantCulture) + ";expected=" +
                            frozenLineCount.ToString(CultureInfo.InvariantCulture));
                    result.SourceLineVisitCount++;
                    byte[] lineBytes = utf8.GetBytes(line);

                    if (index >= bookmark)
                    {
                        if (streamRead != null) streamRead.AddOrigin(0, index + 1);
                        WriteLine(retained, retainedHash, separator, lineBytes,
                            ref retainedLineCount, ref retainedByteCount);
                    }

                    ParsedLine parsed = Parse(line);
                    string eventName = Get(parsed, "event");
                    if (writeSemantic && semanticNames.Contains(eventName))
                    {
                        if (streamRead != null) streamRead.AddOrigin(2, index + 1);
                        WriteLine(semantic, semanticHash, separator, lineBytes,
                            ref semanticLineCount, ref semanticByteCount);
                    }
                    string writerId = Get(parsed, "log_writer_id");
                    if (parsed.DuplicateWriterEvidenceField)
                    {
                        foreach (string candidate in parsed.WriterIds)
                        {
                            if (writers.ContainsKey(candidate))
                            {
                                writerId = candidate;
                                break;
                            }
                        }
                    }
                    string sequenceText = Get(parsed, "log_writer_sequence");
                    if (String.IsNullOrWhiteSpace(writerId) && String.IsNullOrWhiteSpace(sequenceText))
                    {
                        if (index >= bookmark) result.WriterIdentityMismatchCount++;
                        continue;
                    }

                    long sequence;
                    if (String.IsNullOrWhiteSpace(writerId) || !TryPositive(sequenceText, out sequence))
                    {
                        result.WriterIdentityMismatchCount++;
                        continue;
                    }

                    bool isStart = String.Equals(eventName, "operational_log_writer_started",
                        StringComparison.OrdinalIgnoreCase);
                    GuiSmokeLedgerWriterResult writerState;
                    if (!writers.TryGetValue(writerId, out writerState))
                    {
                        if (!isStart)
                        {
                            if (index >= bookmark && unknownAfterBookmark.Add(writerId))
                                result.WriterIdentityMismatchCount++;
                            continue;
                        }
                        string acceptance = Get(parsed, "acceptance_run_id");
                        string scenario = Get(parsed, "scenario_run_id");
                        if (!String.Equals(acceptance, expectedAcceptanceRunId, StringComparison.Ordinal) ||
                            !String.Equals(scenario, expectedScenarioRunId, StringComparison.Ordinal))
                        {
                            if (index >= bookmark) result.WriterIdentityMismatchCount++;
                            continue;
                        }
                        writerState = new GuiSmokeLedgerWriterResult
                        {
                            WriterId = writerId,
                            AcceptanceRunId = acceptance,
                            ScenarioRunId = scenario,
                            FirstSequence = sequence,
                            LastSequence = sequence,
                            ObservedCount = 1
                        };
                        writers.Add(writerId, writerState);
                        if (sequence > 1) result.WriterGapCount += sequence - 1;
                        if (sequence != 1) result.WriterIdentityMismatchCount++;
                    }
                    else
                    {
                        if (sequence == writerState.LastSequence + 1)
                        {
                            writerState.LastSequence = sequence;
                            writerState.ObservedCount++;
                        }
                        else if (sequence > writerState.LastSequence + 1)
                        {
                            result.WriterGapCount += sequence - writerState.LastSequence - 1;
                            writerState.LastSequence = sequence;
                            writerState.ObservedCount++;
                        }
                        else result.WriterDuplicateCount++;

                        if (isStart && (sequence != 1 ||
                            !String.Equals(writerState.AcceptanceRunId, Get(parsed, "acceptance_run_id"), StringComparison.Ordinal) ||
                            !String.Equals(writerState.ScenarioRunId, Get(parsed, "scenario_run_id"), StringComparison.Ordinal)))
                            result.WriterIdentityMismatchCount++;
                    }

                    if (streamRead != null) streamRead.AddOrigin(1, index + 1);
                    WriteLine(writer, writerHash, separator, lineBytes,
                        ref writerLineCount, ref writerByteCount);

                    long generation, first, last, ticks;
                    bool numeric = TryLong(Get(parsed, "log_writer_batch_generation"), out generation) &
                        TryLong(Get(parsed, "log_writer_batch_first_sequence"), out first) &
                        TryLong(Get(parsed, "log_writer_batch_last_sequence"), out last) &
                        TryLong(Get(parsed, "log_writer_attempt_utc_ticks"), out ticks);
                    BatchState batch;
                    if (!currentBatches.TryGetValue(writerId, out batch) || batch.Generation != generation)
                    {
                        if (batch != null) FoldBatch(batch, result, previousTicks);
                        string key = BatchKey(writerId, generation);
                        bool capture = deferredKeys.Contains(key);
                        batch = new BatchState
                        {
                            WriterId = writerId,
                            Generation = generation,
                            First = first,
                            Last = last,
                            Ticks = ticks,
                            Min = sequence,
                            Max = sequence,
                            LastLine = 0,
                            Count = 0,
                            CapturePayload = capture,
                            Valid = numeric && !parsed.DuplicateWriterEvidenceField && sequence > 0 &&
                                generation > 0 && first > 0 && last >= first && ticks > 0 &&
                                (!capture || seenDeferredBatches.Add(key))
                        };
                        currentBatches[writerId] = batch;
                        if (capture) deferredBatches[key] = batch;
                    }
                    else if (!numeric || batch.First != first || batch.Last != last || batch.Ticks != ticks)
                        batch.Valid = false;

                    if (parsed.DuplicateWriterEvidenceField) batch.Valid = false;
                    if (batch.Count > 0 && sequence != batch.LastLine + 1) batch.Valid = false;
                    batch.Min = Math.Min(batch.Min, sequence);
                    batch.Max = Math.Max(batch.Max, sequence);
                    batch.LastLine = sequence;
                    batch.Count++;
                    if (batch.CapturePayload) batch.Payload.Add(line);

                    if (String.Equals(eventName, "operational_log_writer_drained", StringComparison.Ordinal))
                    {
                        List<long> list;
                        if (!drains.TryGetValue(writerId, out list))
                        {
                            list = new List<long>();
                            drains.Add(writerId, list);
                        }
                        list.Add(sequence);
                    }
                }

                if (streamRead != null && bookmark == frozenLineCount) streamRead.CaptureInitialWriters(writers.Values);
                if (streamRead != null && streamRead.ReadLine() != null)
                    throw new InvalidDataException("stream_source_unconsumed_lines");
                foreach (BatchState batch in currentBatches.Values)
                    FoldBatch(batch, result, previousTicks);
                FinalizeHash(retainedHash);
                FinalizeHash(writerHash);
                FinalizeHash(semanticHash);
                retained.Flush(true);
                writer.Flush(true);
                if (writeSemantic) ((FileStream)semantic).Flush(true);
                result.RetainedSha256 = Hex(retainedHash.Hash);
                result.WriterSha256 = Hex(writerHash.Hash);
                result.RetainedLineCount = retainedLineCount;
                result.RetainedByteCount = retainedByteCount;
                result.WriterLineCount = writerLineCount;
                result.WriterByteCount = writerByteCount;
                result.SemanticSha256 = writeSemantic ? Hex(semanticHash.Hash) : "";
                result.SemanticLineCount = semanticLineCount;
                result.SemanticByteCount = semanticByteCount;
                if (captureNativeOutputs)
                {
                    try
                    {
                        captureClock = Stopwatch.StartNew();
                        retainedCapture = GuiSmokeLedgerCapturedOutput.Capture(retained, retainedTempPath,
                            result.RetainedSha256, retainedLineCount, retainedByteCount);
                        writerCapture = GuiSmokeLedgerCapturedOutput.Capture(writer, writerTempPath,
                            result.WriterSha256, writerLineCount, writerByteCount);
                        if (writeSemantic) semanticCapture = GuiSmokeLedgerCapturedOutput.Capture((FileStream)semantic, semanticTempPath,
                            result.SemanticSha256, semanticLineCount, semanticByteCount);
                    }
                    catch { captureClock = null; } // Optional provenance cannot change primary writes.
                }
                if (outputLease != null)
                    outputLease.Seal(retained, writer, (FileStream)semantic, retainedCapture, writerCapture, semanticCapture);
            }

            var writerResults = new List<GuiSmokeLedgerWriterResult>(writers.Values);
            writerResults.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.WriterId, right.WriterId));
            result.Writers = writerResults.ToArray();
            var batchResults = new List<GuiSmokeLedgerBatchResult>();
            foreach (BatchState batch in deferredBatches.Values)
            {
                batchResults.Add(new GuiSmokeLedgerBatchResult
                {
                    WriterId = batch.WriterId,
                    BatchGeneration = batch.Generation,
                    BatchFirstSequence = batch.First,
                    BatchLastSequence = batch.Last,
                    AttemptUtcTicks = batch.Ticks,
                    MinSequence = batch.Min,
                    MaxSequence = batch.Max,
                    LineCount = batch.Count,
                    TupleValid = IsBatchValid(batch),
                    PayloadSha256 = PayloadHash(batch.Payload)
                });
            }
            result.DeferredBatches = batchResults.ToArray();
            var drainResults = new List<GuiSmokeLedgerDrainResult>();
            foreach (var pair in drains)
                drainResults.Add(new GuiSmokeLedgerDrainResult { WriterId = pair.Key, Sequences = pair.Value.ToArray() });
            result.Drains = drainResults.ToArray();
            if (streamRead != null)
            {
                streamRead.CheckCurrent();
                if (retainedCapture == null || writerCapture == null || semanticCapture == null || suppliedSource.Position != streamRead.SourceBytes)
                    throw new InvalidDataException("stream_source_capture_incomplete");
                result.CaptureStream(streamRead, expectedAcceptanceRunId, expectedScenarioRunId, frozenLineCount, bookmark,
                    retainedCapture, writerCapture, semanticCapture, outputLease);
                streamRead.CheckCurrent();
                if (!result.StreamCapture.IsCurrent) throw new InvalidDataException("stream_output_capture_not_current");
            }
            else if (captureNativeOutputs && (!writeSemantic || semanticCapture != null))
            {
                try { result.CaptureNativeOutputs(sourcePath, expectedAcceptanceRunId, expectedScenarioRunId, frozenLineCount, bookmark,
                    retainedCapture, writerCapture, semanticCapture, captureClock); }
                catch { } // No partial capture or substitute expected digest.
            }
            outputsTransferred = true;
            return result;
            }
            finally { if (!outputsTransferred && outputLease != null) outputLease.Dispose(); }
        }

        private static FileStream NewOutput(string path, bool retain = false)
        {
            return new FileStream(path, FileMode.CreateNew, retain ? FileAccess.ReadWrite : FileAccess.Write, retain ? FileShare.Read : FileShare.None,
                1024 * 1024, FileOptions.SequentialScan);
        }

        private static void WriteLine(Stream stream, HashAlgorithm hash, byte[] separator,
            byte[] line, ref long lineCount, ref long byteCount)
        {
            if (lineCount > 0) Write(stream, hash, separator, ref byteCount);
            if (line.Length > 0) Write(stream, hash, line, ref byteCount);
            lineCount++;
        }

        private static void Write(Stream stream, HashAlgorithm hash, byte[] bytes, ref long byteCount)
        {
            stream.Write(bytes, 0, bytes.Length);
            hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            byteCount += bytes.Length;
        }

        private static void FinalizeHash(HashAlgorithm hash)
        {
            hash.TransformFinalBlock(new byte[0], 0, 0);
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static bool TryLong(string text, out long value)
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryPositive(string text, out long value)
        {
            return TryLong(text, out value) && value > 0;
        }

        private static string Get(ParsedLine parsed, string name)
        {
            string value;
            return parsed.Fields.TryGetValue(name, out value) ? value : "";
        }

        private static string BatchKey(string writerId, long generation)
        {
            return writerId.Length.ToString(CultureInfo.InvariantCulture) + ":" + writerId + ":" +
                generation.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsBatchValid(BatchState batch)
        {
            return batch.Valid && batch.Generation > 0 && batch.Ticks > 0 && batch.Min > 0 &&
                batch.First == batch.Min && batch.Last == batch.Max &&
                batch.Count == batch.Max - batch.Min + 1;
        }

        private static void FoldBatch(BatchState batch, GuiSmokeLedgerStreamingResult result,
            Dictionary<string, long> previousTicks)
        {
            if (!IsBatchValid(batch))
            {
                result.ChronologyBatchInvalid = true;
                return;
            }
            long ticks;
            if (previousTicks.TryGetValue(batch.WriterId, out ticks))
            {
                if (batch.Ticks < ticks) result.ChronologyInversionCount++;
                else if (batch.Ticks == ticks) result.ChronologyCollisionCount++;
            }
            previousTicks[batch.WriterId] = batch.Ticks;
        }

        private static string PayloadHash(List<string> lines)
        {
            if (lines.Count == 0) return "";
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(String.Join("\n", lines))));
        }

        private static ParsedLine Parse(string line)
        {
            var parsed = new ParsedLine();
            string payload = StripPrefixes(line);
            int start = 0;
            while (start <= payload.Length)
            {
                int end = payload.IndexOf(';', start);
                if (end < 0) end = payload.Length;
                string segment = payload.Substring(start, end - start).Trim();
                int equals = segment.IndexOf('=');
                if (equals > 0)
                {
                    string name = segment.Substring(0, equals).Trim();
                    string value = segment.Substring(equals + 1).Trim();
                    if (name.Length > 0)
                    {
                        if (String.Equals(name, "log_writer_id", StringComparison.OrdinalIgnoreCase))
                            parsed.WriterIds.Add(value);
                        if (parsed.Fields.ContainsKey(name))
                        {
                            if (WriterFields.Contains(name)) parsed.DuplicateWriterEvidenceField = true;
                        }
                        else parsed.Fields.Add(name, value);
                    }
                }
                if (end == payload.Length) break;
                start = end + 1;
            }
            return parsed;
        }

        private static string ExtractEventName(string line)
        {
            int eventStart = line.IndexOf("event=", StringComparison.OrdinalIgnoreCase);
            if (eventStart < 0) return "";
            eventStart += 6;
            int eventEnd = line.IndexOf(';', eventStart);
            if (eventEnd < 0) eventEnd = line.Length;
            return line.Substring(eventStart, eventEnd - eventStart).Trim();
        }

        private static bool IsDataPathEvent(string eventName)
        {
            return String.Equals(eventName, "filetransfer_data_path_send_result", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(eventName, "filetransfer_data_path_receive_result", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(eventName, "filetransfer_tuna_data_path_peer_visible", StringComparison.OrdinalIgnoreCase);
        }

        private static string DataPathProjectionKey(ParsedLine parsed)
        {
            string[] names =
            {
                "session_id", "transfer_id", "direction", "route", "protocol_version",
                "live_route_epoch", "runtime_generation", "terminal_generation", "leg_generation",
                "route_publication_generation", "route_commit_generation"
            };
            var values = new List<string>();
            foreach (string name in names)
            {
                string value = Get(parsed, name);
                if (String.IsNullOrWhiteSpace(value)) return "";
                values.Add(value);
            }
            return String.Join("|", values);
        }

        private static string StripPrefixes(string line)
        {
            int index = 0;
            bool stripped = false;
            while (index < line.Length && line[index] == '[')
            {
                int close = line.IndexOf(']', index + 1);
                if (close < 0) break;
                int next = close + 1;
                if (next >= line.Length || !Char.IsWhiteSpace(line[next])) break;
                while (next < line.Length && Char.IsWhiteSpace(line[next])) next++;
                index = next;
                stripped = true;
            }
            return stripped ? line.Substring(index) : line;
        }
    }
}
