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
        }

        public static Packet Expand(byte[] original, byte[] native, IDictionary<string, int> p, int lines, int bookmark, int[] counts)
        {
            var packet = new Packet { Native = (byte[])native.Clone() };
            var prefix = new byte[p["index-count"]]; Array.Copy(original, prefix, prefix.Length);
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
            int outputsStart = p["index-first"] + 8 * BitConverter.ToInt32(original, p["index-count"]);
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
            }
            return packet;
        }

        private static void Set(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static void Set(byte[] bytes, int offset, long value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 8); }
    }
}
