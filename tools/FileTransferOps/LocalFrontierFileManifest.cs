#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NLink.Tools
{
    // Internal wire data, NOT original producer/writer/request authority. The
    // Core's private catalog issuer supplies these bytes from original objects.
    internal sealed class LocalFrontierFileManifest
    {
        internal const int Magic = 0x4e464831, Version = 1, MaximumBytes = 32768;
        internal const int MaximumPayloads = 16, MaximumPayloadBytes = 262144;
        internal const long MaximumSourceBytes = 268435456;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal readonly Guid GuiRequest, AppRequest;
        internal readonly int AppId;
        internal readonly long AppCreation;
        internal readonly string Writer;
        internal readonly FileEntry Source;
        internal readonly Range Drain;
        internal readonly Payload[] Payloads;

        internal LocalFrontierFileManifest(Guid gui, Guid app, int pid, long creation, string writer,
            FileEntry source, Range drain, Payload[] payloads)
        { GuiRequest = gui; AppRequest = app; AppId = pid; AppCreation = creation; Writer = writer;
            Source = source; Drain = drain; Payloads = payloads; }

        internal sealed class FileEntry
        {
            internal readonly long Handle, Length;
            internal readonly uint Volume, IndexHigh, IndexLow;
            internal FileEntry(long handle, uint volume, uint high, uint low, long length)
            { Handle = handle; Volume = volume; IndexHigh = high; IndexLow = low; Length = length; }
            internal bool SameFile(FileEntry other)
            { return Volume == other.Volume && IndexHigh == other.IndexHigh && IndexLow == other.IndexLow; }
        }
        internal sealed class Range
        {
            internal readonly long Generation, First, Last, AttemptTicks, BatchBytes, Offset, Length;
            internal readonly byte[] BatchHash, RawHash;
            internal Range(long generation, long first, long last, long ticks, long bytes,
                long offset, long length, byte[] batchHash, byte[] rawHash)
            { Generation = generation; First = first; Last = last; AttemptTicks = ticks; BatchBytes = bytes;
                Offset = offset; Length = length; BatchHash = batchHash; RawHash = rawHash; }
        }
        internal sealed class Payload
        {
            internal readonly Guid Observation;
            internal readonly FileEntry File;
            internal readonly byte[] Hash;
            internal readonly Range Completion, Marker;
            internal Payload(Guid observation, FileEntry file, byte[] hash, Range completion, Range marker)
            { Observation = observation; File = file; Hash = hash; Completion = completion; Marker = marker; }
        }

        internal byte[] Encode()
        {
            try
            {
                if (!Valid()) return null;
                using (var stream = new MemoryStream())
                using (var output = new BinaryWriter(stream, Utf8))
                {
                    output.Write(Magic); output.Write(Version);
                    output.Write(Payloads.Length == 0 ? 2 : 1); // Explicit writer-only variant.
                    output.Write(Payloads.Length); output.Write(GuiRequest.ToByteArray()); output.Write(AppRequest.ToByteArray());
                    output.Write(AppId); output.Write(0); output.Write(AppCreation);
                    var writer = Utf8.GetBytes(Writer); output.Write(writer.Length); output.Write(writer);
                    WriteFile(output, Source); WriteRange(output, Drain);
                    foreach (var payload in Payloads)
                    {
                        output.Write(payload.Observation.ToByteArray()); WriteFile(output, payload.File); output.Write(payload.Hash);
                        WriteRange(output, payload.Completion); WriteRange(output, payload.Marker);
                    }
                    output.Flush(); return stream.Length <= MaximumBytes ? stream.ToArray() : null;
                }
            }
            catch { return null; }
        }

        internal static LocalFrontierFileManifest Decode(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length < 217 || bytes.Length > MaximumBytes) return null;
                using (var stream = new MemoryStream(bytes, false))
                using (var input = new BinaryReader(stream, Utf8))
                {
                    if (input.ReadInt32() != Magic || input.ReadInt32() != Version) return null;
                    int kind = input.ReadInt32(), count = input.ReadInt32();
                    if (count < 0 || count > MaximumPayloads || kind != (count == 0 ? 2 : 1)) return null;
                    var gui = new Guid(ReadExact(input, 16)); var app = new Guid(ReadExact(input, 16));
                    int pid = input.ReadInt32(); if (input.ReadInt32() != 0) return null;
                    long creation = input.ReadInt64(); int writerBytes = input.ReadInt32();
                    if (writerBytes < 1 || writerBytes > 128 || bytes.Length != 216 + writerBytes + 316 * count) return null;
                    string writer = Utf8.GetString(ReadExact(input, writerBytes));
                    var source = ReadFile(input); var drain = ReadRange(input);
                    var payloads = new Payload[count];
                    for (int i = 0; i < count; i++) payloads[i] = new Payload(new Guid(ReadExact(input, 16)),
                        ReadFile(input), ReadExact(input, 32), ReadRange(input), ReadRange(input));
                    var result = new LocalFrontierFileManifest(gui, app, pid, creation, writer, source, drain, payloads);
                    return stream.Position == stream.Length && result.Valid() ? result : null;
                }
            }
            catch { return null; }
        }

        private bool Valid()
        {
            if (GuiRequest == Guid.Empty || AppRequest == Guid.Empty || AppId <= 0 || AppCreation <= 0 ||
                String.IsNullOrWhiteSpace(Writer) || Writer == "(none)" || Writer.Length > 128 || Utf8.GetByteCount(Writer) > 128 ||
                Writer.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 || !ValidFile(Source, MaximumSourceBytes) ||
                Payloads == null || Payloads.Length > MaximumPayloads || !ValidRange(Drain)) return false;
            var observations = new HashSet<Guid>(); var handles = new HashSet<long>(); handles.Add(Source.Handle);
            var files = new List<FileEntry>(); files.Add(Source);
            var ranges = new List<Range>(); ranges.Add(Drain);
            long total = 0;
            foreach (var payload in Payloads)
            {
                if (payload == null || payload.Observation == Guid.Empty || !observations.Add(payload.Observation) ||
                    !ValidFile(payload.File, MaximumPayloadBytes) || !handles.Add(payload.File.Handle) || !Hash(payload.Hash) ||
                    !ValidRange(payload.Completion) || !ValidRange(payload.Marker) ||
                    !Before(payload.Completion, payload.Marker) || !Before(payload.Marker, Drain)) return false;
                foreach (var file in files) if (file.SameFile(payload.File)) return false;
                files.Add(payload.File); total += payload.File.Length;
                ranges.Add(payload.Completion); ranges.Add(payload.Marker);
            }
            ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (int i = 1; i < ranges.Count; i++) if (!Before(ranges[i - 1], ranges[i])) return false;
            return total <= 4194304;
        }
        private bool ValidRange(Range range)
        {
            return range != null && range.Generation > 0 && range.First > 0 && range.Last >= range.First &&
                range.AttemptTicks > 0 && range.AttemptTicks <= DateTime.MaxValue.Ticks && range.BatchBytes > 0 &&
                range.Offset >= 0 && range.Length > 0 && range.Length <= Source.Length &&
                range.Offset <= Source.Length - range.Length && Hash(range.BatchHash) && Hash(range.RawHash);
        }
        private static bool Before(Range a, Range b)
        { return a.Generation < b.Generation && a.Last < b.First && a.Offset + a.Length <= b.Offset; }
        private static bool ValidFile(FileEntry file, long maximum)
        { return file != null && file.Handle > 0 && (IntPtr.Size == 8 || file.Handle <= Int32.MaxValue) && file.Length > 0 && file.Length <= maximum; }
        private static bool Hash(byte[] bytes) { return bytes != null && bytes.Length == 32; }
        private static byte[] ReadExact(BinaryReader input, int count)
        { var bytes = input.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return bytes; }
        private static void WriteFile(BinaryWriter output, FileEntry file)
        { output.Write(file.Handle); output.Write(file.Volume); output.Write(file.IndexHigh); output.Write(file.IndexLow); output.Write(file.Length); }
        private static FileEntry ReadFile(BinaryReader input)
        { return new FileEntry(input.ReadInt64(), input.ReadUInt32(), input.ReadUInt32(), input.ReadUInt32(), input.ReadInt64()); }
        private static void WriteRange(BinaryWriter output, Range range)
        {
            output.Write(range.Generation); output.Write(range.First); output.Write(range.Last); output.Write(range.AttemptTicks);
            output.Write(range.BatchBytes); output.Write(range.Offset); output.Write(range.Length);
            output.Write(range.BatchHash); output.Write(range.RawHash);
        }
        private static Range ReadRange(BinaryReader input)
        { return new Range(input.ReadInt64(), input.ReadInt64(), input.ReadInt64(), input.ReadInt64(), input.ReadInt64(),
            input.ReadInt64(), input.ReadInt64(), ReadExact(input, 32), ReadExact(input, 32)); }
    }
}
