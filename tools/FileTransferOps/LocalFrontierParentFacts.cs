using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    // A bounded immutable wire snapshot, never a reconstructed original capability.
    internal sealed partial class LocalFrontierParentFacts
    {
        internal const int MaximumBytes = 4194304, MaximumText = 262144, MaximumLines = 262144;
        internal const int MaximumOriginEntries = GuiSmokeLedgerStreamRead.MaximumOriginEntries;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly byte[] bytes;
        internal readonly int SourceLines, OriginCount, MarkerCount;
        private readonly string ledger;
        private readonly long bookmark;
        private readonly LocalGuiWriterRequest.Output[] outputs;
        private readonly Dictionary<string, long> writers;
        private readonly int[] semanticOrigins;
        private readonly long[] lineStarts;
        private readonly long sourceBytes;
        private readonly LocalFrontierFileManifest[] manifests;
        internal int PayloadCount { get { return manifests[0].Payloads.Length + manifests[1].Payloads.Length; } }
        internal LocalFrontierFileManifest.Payload PayloadAt(int index, out string writer)
        {
            writer = null;
            if (index < 0 || index >= PayloadCount) return null;
            int slot = index < manifests[0].Payloads.Length ? 0 : 1;
            writer = manifests[slot].Writer;
            return manifests[slot].Payloads[index - (slot == 0 ? 0 : manifests[0].Payloads.Length)];
        }
        internal int SemanticLineCount { get { return semanticOrigins.Length; } }
        internal int SemanticOrigin(int index) { return index < 0 || index >= semanticOrigins.Length ? 0 : semanticOrigins[index]; }
        internal int SemanticIndex(int physical) { int index = Array.BinarySearch(semanticOrigins, physical); return index < 0 ? 0 : index + 1; }
        internal long LineStart(int line) { return line < 1 || line > lineStarts.Length ? -1 : lineStarts[line - 1]; }
        internal long LineEnd(int line) { return line < 1 || line > lineStarts.Length ? -1 : line == lineStarts.Length ? sourceBytes : lineStarts[line]; }
        internal int LineAt(long offset) { int index = Array.BinarySearch(lineStarts, offset); return index < 0 ? 0 : index + 1; }
        internal int RangeLastLine(long end) { return end == sourceBytes ? lineStarts.Length : LineAt(end) - 1; }
        private LocalFrontierParentFacts(byte[] bytes, int lines, int origins, int markers, string ledger, long bookmark,
            LocalGuiWriterRequest.Output[] outputs, Dictionary<string, long> writers, int[] semanticOrigins, LocalFrontierFileManifest[] manifests,
            long[] lineStarts, long sourceBytes)
        { this.bytes = bytes; SourceLines = lines; OriginCount = origins; MarkerCount = markers; this.ledger = ledger; this.bookmark = bookmark; this.outputs = outputs; this.writers = writers; this.semanticOrigins = semanticOrigins; this.manifests = manifests; this.lineStarts = lineStarts; this.sourceBytes = sourceBytes; }
        internal bool MatchesCompletion(LocalGuiWriterRequest.Completion value)
        {
            if (value == null || value.LedgerId != ledger || value.FrozenLineCount != SourceLines || value.Bookmark != bookmark ||
                value.Outputs == null || value.Outputs.Count != 4 || value.Writers == null || value.Writers.Count != writers.Count) return false;
            // The native copy has its own creation path. The unchanged legacy
            // codec supplies only its fixed analyzer label; neither is reopened.
            if (outputs[3].Path != Path.Combine(value.ArtifactRoot, "filetransfer-frontier-retained-log-slice.log") ||
                value.Outputs[3].Path != Path.Combine(value.ArtifactRoot, "filetransfer-retained-log-slice.log")) return false;
            for (int i = 0; i < 4; i++)
            {
                var expected = outputs[i]; var actual = value.Outputs[i];
                // The legacy codec synthesizes names for roles 0..2; those are
                // not the owned files' creation paths. Join native identities,
                // never reopen those labels.
                if (actual.Sha256 != expected.Sha256 || actual.ByteCount != expected.ByteCount || actual.LineCount != expected.LineCount ||
                    (i < 3 && actual.OriginalFileIdentity != expected.OriginalFileIdentity)) return false;
            }
            foreach (var writer in value.Writers) { long last; if (!writers.TryGetValue(writer.WriterId, out last) || writer.LastSequence != last) return false; }
            return true;
        }

        internal static byte[] Encode(LocalGuiWriterRequest.FrontierParentPreparation preparation,
            LocalGuiWriterRequest.FrontierSealedFiles source, byte[] nativePacket, Action check)
        {
            using (var stream = new MemoryStream(new byte[MaximumBytes], 0, MaximumBytes, true, true))
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                var text = new TextWriter(writer); var capture = preparation.Capture; var validation = capture.Validation;
                check(); writer.Write(0x4e504631); writer.Write(1);
                writer.Write(nativePacket.Length); writer.Write(nativePacket);
                text.Write(capture.AcceptanceRunId); text.Write(capture.ScenarioRunId); text.Write(source.SourceSha256);
                writer.Write(capture.SourceBytes); writer.Write(capture.SourceLines); writer.Write(capture.Bookmark); writer.Write(source.VerifiedRangeCount);
                foreach (var item in new[] { preparation.Cursor.Initial, preparation.Cursor.Final })
                {
                    text.Write(item.LedgerId); text.Write(item.GenerationId); writer.Write(item.IsFinal ? 1 : 0);
                    writer.Write(item.LineCount); writer.Write(item.BytesRead); writer.Write(item.CommittedBytes);
                    writer.Write(item.SynchronizationCount); writer.Write(item.Bookmark);
                }
                writer.Write(capture.CursorLineStarts.Count);
                for (int i = 0; i < capture.CursorLineStarts.Count; i++)
                { if (i % 4096 == 0) check(); writer.Write(capture.CursorLineStarts[i]); }
                var outputs = new[] { capture.Retained, capture.Writer, capture.Semantic, preparation.Evidence.Copy.Output, preparation.Evidence.Output };
                foreach (var output in outputs)
                {
                    text.Write(output.CreationPath); text.Write(output.FileIdentity); text.Write(output.Sha256);
                    writer.Write(output.ByteCount); writer.Write(output.LineCount);
                }
                foreach (var origins in new[] { capture.RetainedOrigins, capture.WriterOrigins, capture.SemanticOrigins })
                {
                    writer.Write(origins.Count);
                    for (int i = 0; i < origins.Count; i++) { if (i % 4096 == 0) check(); writer.Write(origins[i]); }
                }
                writer.Write(validation.SourceLineVisitCount); writer.Write(validation.WriterGapCount); writer.Write(validation.WriterDuplicateCount);
                writer.Write(validation.WriterIdentityMismatchCount); writer.Write(validation.ChronologyInversionCount); writer.Write(validation.ChronologyCollisionCount);
                writer.Write(validation.ChronologyBatchInvalid ? 1 : 0); writer.Write(validation.WriterBoundaryMismatchCount); writer.Write(validation.WriterDrainMissingCount);
                writer.Write(validation.MetadataCodeUnits); writer.Write(validation.Writers.Count);
                foreach (var item in validation.Writers)
                {
                    text.Write(item.WriterId); text.Write(item.AcceptanceRunId); text.Write(item.ScenarioRunId);
                    writer.Write(item.FirstSequence); writer.Write(item.LastSequence); writer.Write(item.ObservedCount);
                    writer.Write(item.DrainCount); writer.Write(item.DrainSequence);
                }
                writer.Write(validation.InitialWriters.Count);
                foreach (var item in validation.InitialWriters)
                {
                    text.Write(item.WriterId); text.Write(item.AcceptanceRunId); text.Write(item.ScenarioRunId);
                    writer.Write(item.FirstSequence); writer.Write(item.LastSequence); writer.Write(item.ObservedCount);
                }
                writer.Write(validation.DeferredBatches.Count);
                foreach (var item in validation.DeferredBatches)
                {
                    text.Write(item.WriterId); text.Write(item.PayloadSha256); writer.Write(item.BatchGeneration);
                    writer.Write(item.BatchFirstSequence); writer.Write(item.BatchLastSequence); writer.Write(item.AttemptUtcTicks);
                    writer.Write(item.MinSequence); writer.Write(item.MaxSequence); writer.Write(item.LineCount); writer.Write(item.TupleValid ? 1 : 0);
                }
                foreach (var manifest in preparation.Files.Writers)
                { check(); var encoded = manifest.Encode(); if (encoded == null) throw Invalid(); writer.Write(encoded.Length); writer.Write(encoded); }
                var recovery = preparation.Markers;
                writer.Write(recovery.SourceLineVisitCount); writer.Write(recovery.MetadataCodeUnits); writer.Write(recovery.Markers.Count);
                foreach (var item in recovery.Markers)
                {
                    check(); text.Write(item.EventName); text.Write(item.WriterId); text.Write(item.AcceptanceRunId); text.Write(item.ScenarioRunId);
                    text.Write(item.PayloadSha256); text.Write(item.FailureCode); text.Write(item.BatchKey);
                    writer.Write(item.BatchGeneration); writer.Write(item.FirstSequence); writer.Write(item.LastSequence); writer.Write(item.LineCount);
                    writer.Write(item.AttemptUtcTicks); writer.Write(item.AttemptCount); writer.Write(item.StreamIndex); writer.Write(item.SourceLine);
                }
                writer.Write(recovery.DeferredBatchKeys.Count); foreach (string key in recovery.DeferredBatchKeys) text.Write(key);
                check(); writer.Flush();
                var result = new byte[checked((int)stream.Position)]; Array.Copy(stream.GetBuffer(), result, result.Length); return result;
            }
        }
        private static InvalidDataException Invalid() { return new InvalidDataException("frontier_parent_facts_invalid"); }
        private static void Require(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        { if (!condition) throw new InvalidDataException("frontier_parent_facts_invalid:" + line.ToString(CultureInfo.InvariantCulture)); }
        private static bool Hash(string value)
        { if (value.Length != 64) return false; foreach (char c in value) if (!Uri.IsHexDigit(c)) return false; return true; }
        private static bool Equal(byte[] a, byte[] b)
        { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private sealed class TextWriter
        {
            private readonly BinaryWriter writer; private int total;
            internal TextWriter(BinaryWriter writer) { this.writer = writer; }
            internal void Write(string value)
            {
                Require(!String.IsNullOrWhiteSpace(value) && value.Length <= 4096 && value.IndexOf('\0') < 0 && value.Length <= MaximumText - total);
                total += value.Length; var encoded = Utf8.GetBytes(value); writer.Write(encoded.Length); writer.Write(encoded);
            }
        }
        private sealed class Reader : IDisposable
        {
            internal readonly BinaryReader Input; internal int TextUnits;
            internal Reader(byte[] bytes) { Input = new BinaryReader(new MemoryStream(bytes, false), Utf8); }
            internal byte[] Blob(int maximum)
            { int length = Count(maximum); Require(length <= Input.BaseStream.Length - Input.BaseStream.Position); var value = Input.ReadBytes(length); Require(value.Length == length); return value; }
            internal int Count(int maximum) { int count = Input.ReadInt32(); Require(count >= 0 && count <= maximum); return count; }
            internal string Text(int maximum)
            {
                string value = Utf8.GetString(Blob(3 * maximum));
                Require(!String.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.IndexOf('\0') < 0 && value.Length <= MaximumText - TextUnits);
                TextUnits += value.Length; return value;
            }
            internal long Number(long minimum, long maximum)
            { long number = Input.ReadInt64(); Require(number >= minimum && number <= maximum); return number; }
            public void Dispose() { Input.Dispose(); }
        }
    }
}
