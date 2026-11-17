using System;
using System.Collections.Generic;
using System.IO;

namespace NLink.Tools
{
    // Native-file transport data only, not origin/validation/recovery facts.
    internal sealed class LocalFrontierParentFilePacket
    {
        internal const int MaximumBytes = 64 + 52 * 42;
        internal readonly Guid Gui, First, Second;
        internal readonly Entry[] Entries;
        internal sealed class Entry
        {
            internal readonly int Role, Slot;
            internal readonly Guid Observation;
            internal readonly LocalFrontierFileManifest.FileEntry File;
            internal Entry(int role, int slot, Guid observation, LocalFrontierFileManifest.FileEntry file)
            { Role = role; Slot = slot; Observation = observation; File = file; }
        }
        internal LocalFrontierParentFilePacket(Guid gui, Guid first, Guid second, Entry[] entries)
        { Gui = gui; First = first; Second = second; Entries = entries; }
        internal byte[] Encode()
        {
            if (!Valid()) return null;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x4e504631); writer.Write(1); writer.Write(Entries.Length); writer.Write(0);
                writer.Write(Gui.ToByteArray()); writer.Write(First.ToByteArray()); writer.Write(Second.ToByteArray());
                foreach (var item in Entries)
                {
                    writer.Write(item.Role); writer.Write(item.Slot); writer.Write(item.Observation.ToByteArray());
                    writer.Write(item.File.Handle); writer.Write(item.File.Volume); writer.Write(item.File.IndexHigh);
                    writer.Write(item.File.IndexLow); writer.Write(item.File.Length);
                }
                writer.Flush(); return stream.ToArray();
            }
        }
        internal static LocalFrontierParentFilePacket Decode(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length < 584 || bytes.Length > MaximumBytes) return null;
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != 0x4e504631 || reader.ReadInt32() != 1) return null;
                    int count = reader.ReadInt32();
                    if (count < 10 || count > 42 || bytes.Length != 64 + count * 52 || reader.ReadInt32() != 0) return null;
                    var gui = new Guid(reader.ReadBytes(16)); var first = new Guid(reader.ReadBytes(16)); var second = new Guid(reader.ReadBytes(16));
                    var entries = new Entry[count];
                    for (int i = 0; i < count; i++) entries[i] = new Entry(reader.ReadInt32(), reader.ReadInt32(), new Guid(reader.ReadBytes(16)),
                        new LocalFrontierFileManifest.FileEntry(reader.ReadInt64(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt64()));
                    var result = new LocalFrontierParentFilePacket(gui, first, second, entries);
                    return stream.Position == stream.Length && result.Valid() ? result : null;
                }
            }
            catch { return null; }
        }
        private bool Valid()
        {
            if (Gui == Guid.Empty || First == Guid.Empty || Second == Guid.Empty || First == Second ||
                Entries == null || Entries.Length < 10 || Entries.Length > 42) return false;
            var observations = new HashSet<Guid>(); var handles = new HashSet<long>(); var counts = new int[2];
            int lastSlot = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                var item = Entries[i]; if (item == null || item.File == null) return false;
                if (i < 10 ? item.Role != i || item.Slot != -1 || item.Observation != Guid.Empty :
                    item.Role != 10 || item.Slot < lastSlot || item.Slot > 1 || item.Observation == Guid.Empty || !observations.Add(item.Observation)) return false;
                if (i >= 10) { lastSlot = item.Slot; if (++counts[item.Slot] > 16) return false; }
                long maximum = item.Role == 0 ? 268435456L : item.Role <= 4 ? 268959744L : item.Role == 5 ? 65536L : item.Role <= 9 ? 67108864L : 262144L;
                if (item.File.Handle <= 0 || (IntPtr.Size == 4 && item.File.Handle > Int32.MaxValue) || !handles.Add(item.File.Handle) ||
                    item.File.Length < 0 || item.File.Length > maximum || ((item.Role == 0 || item.Role == 5 || item.Role == 10) && item.File.Length == 0)) return false;
                for (int j = 0; j < i; j++) if (item.File.SameFile(Entries[j].File)) return false;
            }
            return true;
        }
    }
    internal sealed partial class LocalFrontierPreparedFiles
    {
        internal byte[] ExportParentPacket()
        {
            lock (sync)
            {
                if (!IsCurrent || Writers[0].GuiRequest != Writers[1].GuiRequest) return null;
                var ordered = new List<Entry>();
                for (int role = 0; role < 10; role++) ordered.Add(entries.Find(e => e.Role == role));
                for (int slot = 0; slot < 2; slot++)
                    foreach (var payload in Writers[slot].Payloads)
                        ordered.Add(entries.Find(e => e.Slot == slot && Object.ReferenceEquals(e.Payload, payload)));
                var result = new LocalFrontierParentFilePacket.Entry[ordered.Count];
                for (int i = 0; i < result.Length; i++)
                {
                    var entry = ordered[i]; var info = entry.Identity;
                    result[i] = new LocalFrontierParentFilePacket.Entry(entry.Role, entry.Slot, entry.Payload == null ? Guid.Empty : entry.Payload.Observation,
                        new LocalFrontierFileManifest.FileEntry(entry.Handle.DangerousGetHandle().ToInt64(), info.Volume, info.IndexHigh, info.IndexLow, Length(info)));
                }
                var bytes = new LocalFrontierParentFilePacket(Writers[0].GuiRequest, Writers[0].AppRequest, Writers[1].AppRequest, result).Encode();
                return IsCurrent ? bytes : null;
            }
        }
    }
}
