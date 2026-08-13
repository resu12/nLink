using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Tools
{
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
        public GuiSmokeLedgerStreamingResult()
        {
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
    }

    public static class GuiSmokeLogLedgerStreamingProcessor
    {
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

        public static GuiSmokeLedgerStreamingResult Process(
            string sourcePath,
            long frozenLineCount,
            long bookmark,
            string retainedTempPath,
            string writerTempPath,
            string expectedAcceptanceRunId,
            string expectedScenarioRunId,
            string[] deferredBatchKeys,
            string semanticTempPath = null,
            string[] semanticEventNames = null)
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

            using (var retained = NewOutput(retainedTempPath))
            using (var writer = NewOutput(writerTempPath))
            using (var semantic = writeSemantic ? (Stream)NewOutput(semanticTempPath) : Stream.Null)
            using (var retainedHash = SHA256.Create())
            using (var writerHash = SHA256.Create())
            using (var semanticHash = SHA256.Create())
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(input, utf8, true, 1024 * 1024))
            {
                for (long index = 0; index < frozenLineCount; index++)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                        throw new InvalidDataException("writer_evidence_source_short_read:index=" +
                            index.ToString(CultureInfo.InvariantCulture) + ";expected=" +
                            frozenLineCount.ToString(CultureInfo.InvariantCulture));
                    result.SourceLineVisitCount++;
                    byte[] lineBytes = utf8.GetBytes(line);

                    if (index >= bookmark)
                        WriteLine(retained, retainedHash, separator, lineBytes,
                            ref retainedLineCount, ref retainedByteCount);

                    ParsedLine parsed = Parse(line);
                    string eventName = Get(parsed, "event");
                    if (writeSemantic && semanticNames.Contains(eventName))
                        WriteLine(semantic, semanticHash, separator, lineBytes,
                            ref semanticLineCount, ref semanticByteCount);
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
            return result;
        }

        private static FileStream NewOutput(string path)
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
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
