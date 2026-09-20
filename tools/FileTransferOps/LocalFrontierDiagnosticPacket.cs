using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Tools
{
    internal sealed class LocalFrontierDiagnosticPacket
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal readonly Guid Full, Compact, Observation;
        internal readonly int WitnessCount;
        internal readonly string Snapshot;
        internal readonly string[] TerminalCatalog;
        private LocalFrontierDiagnosticPacket(Guid full, Guid compact, Guid observation, int count, string snapshot, string[] catalog)
        { Full = full; Compact = compact; Observation = observation; WitnessCount = count; Snapshot = snapshot; TerminalCatalog = catalog; }
        internal static byte[] Encode(LocalRetainedSourceRequest.FrontierWorkerInputs.NativeDiagnosticPreparation preparation, Action check)
        {
            using (var stream = new MemoryStream(new byte[LocalFrontierDiagnosticChannel.MaximumBytes], 0, LocalFrontierDiagnosticChannel.MaximumBytes, true, true))
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                check(); writer.Write(0x4e464432); writer.Write(2);
                writer.Write(preparation.RequestId.ToByteArray()); writer.Write(preparation.WorkerProcessId); writer.Write(preparation.WorkerCreationFileTime);
                writer.Write(preparation.FullInvocationId.ToByteArray()); writer.Write(preparation.CompactInvocationId.ToByteArray());
                foreach (var hash in new[] { preparation.ScopeSha256, preparation.OfferSha256, preparation.FactsReceiptSha256, preparation.SourceSha256 }) Text(writer, hash, 64);
                var inputs = preparation.CopyInputs(); writer.Write(inputs.Length);
                foreach (var item in inputs)
                { check(); writer.Write(item.Role); writer.Write(item.WriterSlot); writer.Write(item.ObservationId.ToByteArray()); Text(writer, item.FileIdentity, 25); writer.Write(item.ByteCount); }
                Text(writer, preparation.DiagnosticSnapshot, 16384);
                foreach (var value in preparation.Batch.TerminalCatalog) Text(writer, value, 1024);
                var payload = preparation.Batch.Payload; writer.Write(payload.ObservationId.ToByteArray()); Text(writer, payload.WriterId, 1024);
                writer.Write(payload.ByteCount); Text(writer, payload.Sha256, 64); Text(writer, payload.Text, 262144);
                var rows = preparation.Batch.CopyRows(); writer.Write(rows.Length);
                foreach (var row in rows)
                {
                    check(); writer.Write(row.FullIndex); writer.Write(row.CompactIndex); writer.Write(row.SemanticIndex); writer.Write(row.SourceLine);
                    writer.Write(row.ByteOffset); writer.Write(row.RawByteCount); Text(writer, row.LineSha256, 64); Text(writer, row.Text, 262144);
                }
                check(); writer.Flush(); var result = new byte[checked((int)stream.Position)]; Array.Copy(stream.GetBuffer(), result, result.Length); return result;
            }
        }
        internal static LocalFrontierDiagnosticPacket Decode(byte[] bytes, Guid request, int worker, long creation,
            string scope, string offer, string factsHash, string sourceHash, LocalFrontierParentFacts facts,
            LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins pins, Action check)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > LocalFrontierDiagnosticChannel.MaximumBytes) return null;
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                check();
                if (reader.ReadInt32() != 0x4e464432 || reader.ReadInt32() != 2 || GuidValue(reader) != request || reader.ReadInt32() != worker || reader.ReadInt64() != creation) return null;
                Guid full = GuidValue(reader), compact = GuidValue(reader);
                if (full == Guid.Empty || compact == Guid.Empty || full == compact) return null;
                foreach (var expected in new[] { scope, offer, factsHash, sourceHash }) if (Text(reader, 64) != expected) return null;
                if (reader.ReadInt32() != pins.Count) return null;
                for (int i = 0; i < pins.Count; i++)
                {
                    check(); var item = pins.EntryAt(i);
                    if (reader.ReadInt32() != item.Role || reader.ReadInt32() != item.Slot || GuidValue(reader) != item.Observation ||
                        Text(reader, 25) != Identity(item.File) || reader.ReadInt64() != item.File.Length) return null;
                }
                string snapshot = Text(reader, 16384); if (snapshot.Length == 0) return null;
                var catalog = new string[5]; for (int i = 0; i < catalog.Length; i++) catalog[i] = Text(reader, 1024);
                if (catalog[0] != "SnapshotV1Authoritative" || catalog[1] != "1" || catalog[2].Length == 0 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(catalog[3], @"\A[0-9a-f]{64}\z") ||
                    !System.Text.RegularExpressions.Regex.IsMatch(catalog[4], @"\A[0-9a-f]{64}\z")) return null;
                Guid observation = GuidValue(reader); string writerId = Text(reader, 1024);
                LocalFrontierFileManifest.Payload selected = null;
                for (int i = 0; i < facts.PayloadCount; i++)
                {
                    check(); string originalWriter; var payload = facts.PayloadAt(i, out originalWriter);
                    if (payload.Observation == observation && originalWriter == writerId) { if (selected != null) return null; selected = payload; }
                }
                if (selected == null || reader.ReadInt64() != selected.File.Length || Text(reader, 64) != Hex(selected.Hash)) return null;
                string payloadText = Text(reader, 262144); byte[] payloadBytes = Utf8.GetBytes(payloadText);
                if (payloadBytes.Length != selected.File.Length || Hash(payloadBytes) != Hex(selected.Hash)) return null;
                int count = reader.ReadInt32(); if (count < 1 || count > 64) return null;
                int previousFull = 0, previousLine = 0, previousCompact = 0; bool completion = false, marker = false;
                for (int i = 0; i < count; i++)
                {
                    check(); int fullIndex = reader.ReadInt32(), compactIndex = reader.ReadInt32(), ordinal = reader.ReadInt32(), line = reader.ReadInt32();
                    long offset = reader.ReadInt64(), length = reader.ReadInt64(); string hash = Text(reader, 64), text = Text(reader, 262144);
                    if (fullIndex <= previousFull || fullIndex > facts.SemanticLineCount || compactIndex < 0 || compactIndex > fullIndex ||
                        (compactIndex > 0 && compactIndex <= previousCompact) || ordinal < 1 || ordinal > facts.SemanticLineCount ||
                        facts.SemanticOrigin(ordinal - 1) != line || line <= previousLine || facts.LineStart(line) != offset ||
                        facts.LineEnd(line) - offset != length || Hash(Utf8.GetBytes(text)) != hash) return null;
                    if (offset == selected.Completion.Offset) completion = Range(selected.Completion, line, length, facts);
                    if (offset == selected.Marker.Offset) marker = Range(selected.Marker, line, length, facts);
                    previousFull = fullIndex; previousLine = line; if (compactIndex > 0) previousCompact = compactIndex;
                }
                check(); return completion && marker && stream.Position == stream.Length ? new LocalFrontierDiagnosticPacket(full, compact, observation, count, snapshot, catalog) : null;
            }
        }
        private static bool Range(LocalFrontierFileManifest.Range range, int line, long length, LocalFrontierParentFacts facts)
        { return range.First == range.Last && length == range.Length + 2 && facts.LineAt(range.Offset) == line && facts.RangeLastLine(range.Offset + range.Length + 2) == line; }
        private static Guid GuidValue(BinaryReader reader) { return new Guid(reader.ReadBytes(16)); }
        private static string Identity(LocalFrontierFileManifest.FileEntry file) { return file.Volume.ToString("x8") + ":" + file.IndexHigh.ToString("x8") + file.IndexLow.ToString("x8"); }
        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static void Text(BinaryWriter writer, string text, int maximum)
        {
            if (text == null || text.Length > maximum) throw new InvalidOperationException("frontier_output_text_capacity");
            byte[] bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes);
        }
        private static string Text(BinaryReader reader, int maximum)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maximum * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidOperationException("frontier_output_text_capacity");
            string text = Utf8.GetString(reader.ReadBytes(length));
            if (text.Length > maximum) throw new InvalidOperationException("frontier_output_text_capacity"); return text;
        }
    }
}
