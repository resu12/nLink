using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NLink.Tests
{
    // Pure count/codec controls only. Expanded bytes never enter a custody channel.
    public static class FrontierOriginCapacityProbe
    {
        public static void Counts(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var total = type.GetField("originCount", flags);
            using (var input = new MemoryStream(new byte[] { 10 }, false))
            using (var reader = (TextReader)Activator.CreateInstance(type, flags, null, new object[] { input, false }, null))
            {
                var add = (Action<int, long>)Delegate.CreateDelegate(typeof(Action<int, long>), reader, type.GetMethod("AddOrigin", flags));
                var origins = (List<int>[])type.GetField("Origins", flags).GetValue(reader);
                if ((int)total.GetValue(reader) != 0) throw new Exception("Nonzero initial origins");
                for (int role = 0; role < 3; role++)
                {
                    for (int line = 1; line <= 262144; line++) add(role, line);
                    Reject(add, role, 262145);
                    if (origins[role].Count != 262144 || (int)total.GetValue(reader) != (role + 1) * 262144)
                        throw new Exception("Per-role overflow appended an origin");
                }
                Reject(add, 2, 262145);
                if ((int)total.GetValue(reader) != 786432) throw new Exception("Aggregate maximum lost");
            }
            // Isolate the aggregate guard from the necessarily full per-role lists.
            using (var input = new MemoryStream(new byte[] { 10 }, false))
            using (var reader = (TextReader)Activator.CreateInstance(type, flags, null, new object[] { input, false }, null))
            {
                var add = (Action<int, long>)Delegate.CreateDelegate(typeof(Action<int, long>), reader, type.GetMethod("AddOrigin", flags));
                var origins = (List<int>[])type.GetField("Origins", flags).GetValue(reader);
                total.SetValue(reader, 786431); add(0, 1); Reject(add, 0, 2);
                if (origins[0].Count != 1 || (int)total.GetValue(reader) != 786432)
                    throw new Exception("Aggregate overflow appended an origin");
            }
        }

        private static void Reject(Action<int, long> add, int role, long line)
        {
            try { add(role, line); }
            catch (InvalidDataException error) { if (error.Message == "stream_source_origin_limit") return; throw; }
            throw new Exception("Origin overflow accepted");
        }

        public sealed class Packet
        {
            public byte[] Body, Native;
            public int[] Counts = new int[3], First = new int[3], OutputLines = new int[5];
            public Dictionary<string, int> Positions;
        }

        public static Packet Transcode(byte[] original, IDictionary<string, int> p, bool compact)
        { return Transcode(original, p, compact ? 2 : 1); }

        public static Packet Transcode(byte[] original, IDictionary<string, int> p, int version)
        {
            var result = new Packet { Positions = new Dictionary<string, int>(p) };
            int sourceVersion = BitConverter.ToInt32(original, 4);
            bool sourceCompact = sourceVersion != 1, compact = version != 1;
            long bookmark = BitConverter.ToInt64(original, p["bookmark"]);
            long lines = BitConverter.ToInt64(original, p["source-lines"]);
            using (var reader = new BinaryReader(new MemoryStream(original, false)))
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                var prefix = new byte[p["index-count"]]; Array.Copy(original, prefix, prefix.Length); Set(prefix, 4, version); writer.Write(prefix);
                reader.BaseStream.Position = p["index-count"]; int count = reader.ReadInt32(); writer.Write(count); long last = 0;
                for (int i = 0; i < count; i++)
                {
                    long value = sourceCompact ? last + ReadDelta(reader) : reader.ReadInt64();
                    if (compact) WriteDelta(writer, value - last); else writer.Write(value); last = value;
                }
                int outputs = (int)stream.Position, shift = outputs - p["outputs-start"];
                foreach (var entry in p) if (entry.Value >= p["outputs-start"] && entry.Value < p["origin-count-0"]) result.Positions[entry.Key] += shift;
                writer.Write(original, p["outputs-start"], p["origin-count-0"] - p["outputs-start"]);
                for (int role = 0; role < 3; role++)
                {
                    reader.BaseStream.Position = p["origin-count-" + role]; count = reader.ReadInt32();
                    result.Positions["origin-count-" + role] = (int)stream.Position; writer.Write(count);
                    result.Positions["origin-first-" + role] = (int)stream.Position; last = 0;
                    for (int i = 0; i < count; i++)
                    {
                        long value = (sourceVersion >= 3 && role == 0) || (sourceVersion == 4 && role == 1 && count == lines) ? (role == 0 ? bookmark : 0) + i + 1 : sourceCompact ? last + ReadDelta(reader) : reader.ReadInt32();
                        if ((version >= 3 && role == 0) || (version == 4 && role == 1 && count == lines))
                        { if (value != (role == 0 ? bookmark : 0) + i + 1) throw new Exception("Nonconsecutive implicit origin"); }
                        else if (compact) WriteDelta(writer, value - last); else writer.Write(checked((int)value)); last = value;
                    }
                }
                shift = (int)stream.Position - p["visits"];
                foreach (var entry in p) if (entry.Value >= p["visits"]) result.Positions[entry.Key] += shift;
                writer.Write(original, p["visits"], original.Length - p["visits"]); writer.Flush(); result.Body = stream.ToArray();
            }
            return result;
        }

        public static byte[] ReplaceDelta(byte[] body, int at, byte[] replacement)
        {
            int end = at; do { } while ((body[end++] & 128) != 0);
            var result = new byte[body.Length - (end - at) + replacement.Length];
            Array.Copy(body, result, at); Array.Copy(replacement, 0, result, at, replacement.Length);
            Array.Copy(body, end, result, at + replacement.Length, body.Length - end); return result;
        }

        public static Packet PadPath(Packet packet, int count)
        {
            int start = packet.Positions["outputs-start"], length = BitConverter.ToInt32(packet.Body, start), at = start + 4 + length;
            var result = new Packet { Body = new byte[packet.Body.Length + count], Native = packet.Native, Positions = new Dictionary<string, int>(packet.Positions) };
            Array.Copy(packet.Body, result.Body, at); for (int i = 0; i < count; i++) result.Body[at + i] = (byte)'x';
            Array.Copy(packet.Body, at, result.Body, at + count, packet.Body.Length - at); Set(result.Body, start, length + count);
            foreach (var entry in packet.Positions) if (entry.Value >= at) result.Positions[entry.Key] += count;
            return result;
        }

        private static long ReadDelta(BinaryReader reader)
        {
            long value = 0; int shift = 0; byte part;
            do { part = reader.ReadByte(); value |= (long)(part & 127) << shift; shift += 7; } while ((part & 128) != 0);
            return value;
        }
        private static void WriteDelta(BinaryWriter writer, long value)
        { do { byte part = (byte)(value & 127); value >>= 7; writer.Write((byte)(part | (value == 0 ? 0 : 128))); } while (value != 0); }

        public static void SameTables(object first, object second)
        {
            if (first == null || second == null) throw new Exception("Missing codec result");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = first.GetType();
            foreach (string name in new[] { "SourceLines", "OriginCount", "MarkerCount", "sourceBytes", "ledger", "bookmark" })
                if (!Object.Equals(type.GetField(name, flags).GetValue(first), type.GetField(name, flags).GetValue(second))) throw new Exception("Codec scalar changed: " + name);
            foreach (string name in new[] { "lineStarts", "semanticOrigins" })
            {
                var a = (Array)type.GetField(name, flags).GetValue(first); var b = (Array)type.GetField(name, flags).GetValue(second);
                if (a.Length != b.Length) throw new Exception("Codec table length changed");
                for (int i = 0; i < a.Length; i++) if (!Object.Equals(a.GetValue(i), b.GetValue(i))) throw new Exception("Codec table changed: " + name);
            }
            foreach (object value in new[] { first, second })
            {
                var bytes = (byte[])type.GetField("bytes", flags).GetValue(value);
                var copied = (byte[])type.GetMethod("CopyForPrimary", flags).Invoke(value, null);
                if (Convert.ToBase64String(bytes) != Convert.ToBase64String(copied)) throw new Exception("Primary copy changed original wire bytes");
            }
        }

        public static Packet Expand(byte[] original, byte[] native, IDictionary<string, int> p, int lines, int bookmark, int[] counts)
        {
            var packet = new Packet { Native = (byte[])native.Clone() };
            var prefix = new byte[p["index-count"]]; Array.Copy(original, prefix, prefix.Length); Set(prefix, 4, 1);
            long sourceBytes = Math.Max(BitConverter.ToInt64(original, p["source-bytes"]), lines);
            long stride = sourceBytes / lines;
            long initialCommitted = bookmark == lines ? sourceBytes : stride * bookmark;
            Set(prefix, p["source-bytes"], sourceBytes); Set(prefix, p["source-lines"], (long)lines);
            Set(prefix, p["bookmark"], (long)bookmark); Set(packet.Native, 64 + 44, sourceBytes);
            for (int i = 0; i < 2; i++)
            {
                int start = p["observation-" + i];
                Set(prefix, start + 4, i == 0 ? bookmark : lines);
                Set(prefix, start + 8, i == 0 ? initialCommitted : sourceBytes);
                Set(prefix, start + 16, i == 0 ? initialCommitted : sourceBytes);
                Set(prefix, start + 32, bookmark);
            }
            int outputsStart = p["outputs-start"];
            var outputs = new byte[p["origin-count-0"] - outputsStart]; Array.Copy(original, outputsStart, outputs, 0, outputs.Length);
            for (int i = 0; i < 4; i++)
            {
                int count = counts[Math.Min(i, 2)]; int offset = p["output-bytes-" + i] - outputsStart;
                long bytes = Math.Max(BitConverter.ToInt64(outputs, offset), count);
                Set(outputs, offset, bytes); Set(outputs, offset + 8, (long)count);
                Set(packet.Native, 64 + 52 * (i + 1) + 44, bytes);
            }
            Array.Copy(packet.Native, 0, prefix, 12, packet.Native.Length);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(prefix); writer.Write(lines);
                for (int i = 0; i < lines; i++) writer.Write(stride * i);
                for (int i = 0; i < 5; i++) packet.OutputLines[i] = (int)stream.Position + p["output-bytes-" + i] - outputsStart + 8;
                writer.Write(outputs);
                for (int role = 0; role < 3; role++)
                {
                    packet.Counts[role] = (int)stream.Position; writer.Write(counts[role]); packet.First[role] = (int)stream.Position;
                    for (int i = 0; i < counts[role]; i++) writer.Write(i + 1 + (role == 0 ? bookmark : 0));
                }
                writer.Write((long)lines); writer.Write(original, p["visits"] + 8, original.Length - p["visits"] - 8);
                writer.Flush(); packet.Body = stream.ToArray();
                packet.Positions = new Dictionary<string, int>(p);
                packet.Positions["outputs-start"] = p["index-first"] + 8 * lines;
                packet.Positions["visits"] = packet.First[2] + 4 * counts[2];
                for (int role = 0; role < 3; role++) { packet.Positions["origin-count-" + role] = packet.Counts[role]; packet.Positions["origin-first-" + role] = packet.First[role]; }
            }
            return packet;
        }

        private static void Set(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static void Set(byte[] bytes, int offset, long value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 8); }
    }
}
