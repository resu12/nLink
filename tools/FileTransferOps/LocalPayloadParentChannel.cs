using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // Fixed two-file control plane, separate from the diagnostic catalog. A
    // committed packet is still provisional until the original native relay take.
    internal sealed class LocalPayloadParentChannel : IDisposable
    {
        internal const int Capacity = 8192;
        private const int Header = 96;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private EventWaitHandle ready, pulled, cancelled;
        private byte[] snapshot;
        private bool closed;
        private int lastState = 1;
        internal byte[] Packet;
        internal WaitHandle Pulled { get { return pulled; } }
        internal WaitHandle Cancelled { get { return cancelled; } }
        internal int State { get { try { return view.ReadInt32(8); } catch { return -1; } } }
        private static string Name(string endpoint) { return endpoint + ".transfer-payload-pair-v1"; }
        internal static bool Offered(string endpoint)
        { try { using (var signal = EventWaitHandle.OpenExisting(Name(endpoint) + ".ready")) return signal.WaitOne(0); } catch { return false; } }
        internal static LocalPayloadParentChannel Create(string endpoint, byte[] binding, byte[] packet)
        {
            var value = new LocalPayloadParentChannel(); bool owns = false;
            try
            {
                if (binding == null || binding.Length != 32 || packet == null || packet.Length < 128 || packet.Length > Capacity) return null;
                string name = Name(endpoint); value.map = MemoryMappedFile.CreateNew(name, Header + Capacity);
                value.view = value.map.CreateViewAccessor(); bool created;
                value.ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready", out created); if (!created) return null;
                value.pulled = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".pulled", out created); if (!created) return null;
                value.cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled", out created); if (!created) return null;
                owns = true; value.snapshot = new byte[Header + Capacity];
                Buffer.BlockCopy(new[] { 0x4e505032, 1, 1, packet.Length }, 0, value.snapshot, 0, 16);
                Array.Copy(binding, 0, value.snapshot, 16, 32);
                using (var sha = SHA256.Create()) Array.Copy(sha.ComputeHash(packet), 0, value.snapshot, 48, 32);
                Array.Copy(packet, 0, value.snapshot, Header, packet.Length);
                value.view.WriteArray(0, value.snapshot, 0, value.snapshot.Length); value.ready.Set();
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) { if (owns) value.Dispose(); else value.Release(); } }
        }
        internal static LocalPayloadParentChannel Open(string endpoint, byte[] binding)
        {
            var value = new LocalPayloadParentChannel();
            try
            {
                string name = Name(endpoint); value.map = MemoryMappedFile.OpenExisting(name); value.view = value.map.CreateViewAccessor(0, Header + Capacity);
                value.ready = EventWaitHandle.OpenExisting(name + ".ready"); value.pulled = EventWaitHandle.OpenExisting(name + ".pulled");
                value.cancelled = EventWaitHandle.OpenExisting(name + ".cancelled");
                value.snapshot = new byte[Header + Capacity]; value.view.ReadArray(0, value.snapshot, 0, value.snapshot.Length);
                int length = BitConverter.ToInt32(value.snapshot, 12);
                if (binding == null || binding.Length != 32 || BitConverter.ToInt32(value.snapshot, 0) != 0x4e505032 ||
                    BitConverter.ToInt32(value.snapshot, 4) != 1 || value.State != 1 || length < 128 || length > Capacity || value.pulled.WaitOne(0)) return null;
                for (int i = 0; i < 32; i++) if (value.snapshot[16 + i] != binding[i]) return null;
                for (int i = 80; i < Header; i++) if (value.snapshot[i] != 0) return null;
                for (int i = Header + length; i < value.snapshot.Length; i++) if (value.snapshot[i] != 0) return null;
                value.Packet = new byte[length]; Array.Copy(value.snapshot, Header, value.Packet, 0, length);
                using (var sha = SHA256.Create()) { var hash = sha.ComputeHash(value.Packet); for (int i = 0; i < 32; i++) if (hash[i] != value.snapshot[48 + i]) return null; }
                if (!value.Current) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) value.Release(); }
        }
        internal bool Current
        {
            get
            {
                try
                {
                    if (closed || cancelled.WaitOne(0) || !ready.WaitOne(0)) return false;
                    int state = State; if (state < lastState || state > 3) return false; lastState = state;
                    var bytes = new byte[Header + Capacity]; view.ReadArray(0, bytes, 0, bytes.Length);
                    for (int i = 0; i < bytes.Length; i++) if ((i < 8 || i >= 12) && bytes[i] != snapshot[i]) return false;
                    return !cancelled.WaitOne(0);
                }
                catch { return false; }
            }
        }
        internal bool MarkPulled() { if (!Current || State != 1) return false; view.Write(8, 2); pulled.Set(); return Current; }
        internal bool Commit() { if (!Current || State != 2) return false; view.Write(8, 3); return Current; }
        internal void Release()
        { if (closed) return; closed = true; if (view != null) view.Dispose(); if (map != null) map.Dispose(); if (ready != null) ready.Dispose(); if (pulled != null) pulled.Dispose(); if (cancelled != null) cancelled.Dispose(); }
        public void Dispose() { try { if (!closed && cancelled != null) cancelled.Set(); } catch { } Release(); }
    }
}
