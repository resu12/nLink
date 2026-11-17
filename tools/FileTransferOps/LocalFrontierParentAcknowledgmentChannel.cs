using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // Two-sided receipt only. Neither state transfers native ownership.
    internal sealed class LocalFrontierParentAcknowledgmentChannel : IDisposable
    {
        private const int Bytes = 80, Magic = 0x4e504134;
        private readonly object sync = new object();
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private EventWaitHandle ready, cancelled;
        private byte[] snapshot;
        private int disposed, lastState = 1;
        internal byte[] BindingHash { get { using (var hash = SHA256.Create()) return hash.ComputeHash(snapshot); } }
        internal int State { get { try { return view.ReadInt32(8); } catch { return -1; } } }
        private LocalFrontierParentAcknowledgmentChannel() { }
        internal static LocalFrontierParentAcknowledgmentChannel Create(string endpoint, byte[] offer, byte[] facts)
        { return OpenCore(endpoint, offer, facts, true); }
        internal static LocalFrontierParentAcknowledgmentChannel Open(string endpoint, byte[] offer, byte[] facts)
        { return OpenCore(endpoint, offer, facts, false); }
        private static LocalFrontierParentAcknowledgmentChannel OpenCore(string endpoint, byte[] offer, byte[] facts, bool create)
        {
            var value = new LocalFrontierParentAcknowledgmentChannel();
            try
            {
                if (offer == null || offer.Length != 32 || facts == null || facts.Length != 32) return null;
                string name = endpoint + ".ack-v1";
                value.map = create ? MemoryMappedFile.CreateNew(name, Bytes) : MemoryMappedFile.OpenExisting(name);
                value.view = value.map.CreateViewAccessor(0, Bytes);
                value.ready = create ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready") : EventWaitHandle.OpenExisting(name + ".ready");
                value.cancelled = create ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled") : EventWaitHandle.OpenExisting(name + ".cancelled");
                value.snapshot = new byte[Bytes];
                Array.Copy(BitConverter.GetBytes(Magic), 0, value.snapshot, 0, 4);
                Array.Copy(BitConverter.GetBytes(1), 0, value.snapshot, 4, 4);
                Array.Copy(BitConverter.GetBytes(1), 0, value.snapshot, 8, 4);
                Array.Copy(offer, 0, value.snapshot, 16, 32); Array.Copy(facts, 0, value.snapshot, 48, 32);
                if (create) { value.view.WriteArray(0, value.snapshot, 0, Bytes); value.ready.Set(); }
                if (!value.IsCurrent || value.State != 1) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            // A rejected open must not cancel the original parent's channel.
            finally { if (value != null) { if (create) value.Dispose(); else value.Close(); } }
        }
        internal bool IsCurrent
        {
            get
            {
                lock (sync)
                {
                    try
                    {
                        if (disposed != 0 || !ready.WaitOne(0) || cancelled.WaitOne(0)) return false;
                        int state = State; if (state < lastState || state < 1 || state > 2) return false; lastState = state;
                        for (int i = 0; i < Bytes; i++) if ((i < 8 || i >= 12) && view.ReadByte(i) != snapshot[i]) return false;
                        return true;
                    }
                    catch { return false; }
                }
            }
        }
        internal bool MarkConfirmed()
        { lock (sync) { if (!IsCurrent || State != 1) return false; view.Write(8, 2); lastState = 2; return IsCurrent; } }
        public void Dispose() { try { if (cancelled != null) cancelled.Set(); } catch { } Close(); }
        private void Close()
        {
            lock (sync)
            {
                if (disposed != 0) return; disposed = 1;
                if (view != null) view.Dispose(); if (map != null) map.Dispose();
                if (ready != null) ready.Dispose(); if (cancelled != null) cancelled.Dispose();
            }
        }
    }
}
