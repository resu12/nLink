using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // Separate bounded facts channel. Receipt is evidence, not custody commit.
    internal sealed class LocalFrontierParentFactsChannel : IDisposable
    {
        internal const int HeaderBytes = 80;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private EventWaitHandle ready, cancelled;
        private byte[] header, body;
        private readonly object sync = new object();
        private int disposed, lastState = 1;
        internal LocalFrontierParentAcknowledgmentChannel Acknowledgment;
        internal byte[] ReceiptHash { get { using (var hash = SHA256.Create()) return hash.ComputeHash(header); } }
        internal byte[] Body { get { return body; } }
        internal WaitHandle Cancelled { get { return cancelled; } }
        internal int State { get { try { return view.ReadInt32(8); } catch { return -1; } } }
        private LocalFrontierParentFactsChannel() { }
        internal static LocalFrontierParentFactsChannel Create(string endpoint, byte[] binding, byte[] bytes, Action check)
        {
            var value = new LocalFrontierParentFactsChannel();
            try
            {
                if (binding == null || binding.Length != 32 || bytes == null || bytes.Length < 1 || bytes.Length > LocalFrontierParentFacts.MaximumBytes) return null;
                check(); value.OpenResources(endpoint, true);
                value.header = new byte[HeaderBytes];
                Array.Copy(BitConverter.GetBytes(0x4e504133), 0, value.header, 0, 4);
                Array.Copy(BitConverter.GetBytes(1), 0, value.header, 4, 4); Array.Copy(BitConverter.GetBytes(1), 0, value.header, 8, 4);
                Array.Copy(BitConverter.GetBytes(bytes.Length), 0, value.header, 12, 4); Array.Copy(binding, 0, value.header, 16, 32);
                using (var hash = SHA256.Create()) Array.Copy(hash.ComputeHash(bytes), 0, value.header, 48, 32);
                value.body = (byte[])bytes.Clone(); value.view.WriteArray(0, value.header, 0, HeaderBytes);
                for (int i = 0; i < bytes.Length; i += 32768) { check(); value.view.WriteArray(HeaderBytes + i, bytes, i, Math.Min(32768, bytes.Length - i)); }
                value.ready.Set(); if (!value.CheckCurrent(check, true)) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) value.Dispose(); }
        }
        internal static LocalFrontierParentFactsChannel Open(string endpoint, byte[] binding, Action check)
        {
            var value = new LocalFrontierParentFactsChannel();
            try
            {
                check(); value.OpenResources(endpoint, false);
                if (!value.ready.WaitOne(0) || value.cancelled.WaitOne(0)) return null;
                value.header = new byte[HeaderBytes]; value.view.ReadArray(0, value.header, 0, HeaderBytes);
                int length = BitConverter.ToInt32(value.header, 12);
                if (BitConverter.ToInt32(value.header, 0) != 0x4e504133 || BitConverter.ToInt32(value.header, 4) != 1 ||
                    BitConverter.ToInt32(value.header, 8) != 1 || length < 1 || length > LocalFrontierParentFacts.MaximumBytes || binding == null || binding.Length != 32) return null;
                for (int i = 0; i < 32; i++) if (value.header[16 + i] != binding[i]) return null;
                value.body = new byte[length];
                for (int i = 0; i < length; i += 32768) { check(); value.view.ReadArray(HeaderBytes + i, value.body, i, Math.Min(32768, length - i)); }
                using (var hash = SHA256.Create())
                { var digest = hash.ComputeHash(value.body); for (int i = 0; i < 32; i++) if (digest[i] != value.header[48 + i]) return null; }
                if (!value.CheckCurrent(check, true)) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) value.Close(); }
        }
        private void OpenResources(string endpoint, bool create)
        {
            string name = endpoint + ".facts-v1";
            map = create ? MemoryMappedFile.CreateNew(name, HeaderBytes + LocalFrontierParentFacts.MaximumBytes) : MemoryMappedFile.OpenExisting(name);
            view = map.CreateViewAccessor(0, HeaderBytes + LocalFrontierParentFacts.MaximumBytes);
            ready = create ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready") : EventWaitHandle.OpenExisting(name + ".ready");
            cancelled = create ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled") : EventWaitHandle.OpenExisting(name + ".cancelled");
        }
        internal bool IsCurrent { get { return CheckCurrent(() => { }, false); } }
        private bool CheckCurrent(Action check, bool includePadding)
        {
            lock (sync)
            {
                try
                {
                    if (disposed != 0 || !ready.WaitOne(0) || cancelled.WaitOne(0)) return false;
                    int state = State; if (state < lastState || state > 2 || state < 1) return false; lastState = state;
                    for (int i = 0; i < HeaderBytes; i++) if ((i < 8 || i >= 12) && view.ReadByte(i) != header[i]) return false;
                    // Padding is checked at publication/open only. It is never
                    // parsed or retained as facts. Revalidating unused capacity
                    // at every nested ownership check would spend the work
                    // budget on bytes outside the immutable packet.
                    check(); var buffer = new byte[32768];
                    int length = includePadding ? LocalFrontierParentFacts.MaximumBytes : body.Length;
                    for (int i = 0; i < length; i += buffer.Length)
                    {
                        if (i != 0 && i % 262144 == 0) check(); int count = Math.Min(buffer.Length, length - i);
                        view.ReadArray(HeaderBytes + i, buffer, 0, count);
                        for (int j = 0; j < count; j++) if (buffer[j] != (i + j < body.Length ? body[i + j] : 0)) return false;
                    }
                    check(); return !cancelled.WaitOne(0) && (Acknowledgment == null || Acknowledgment.IsCurrent);
                }
                catch { return false; }
            }
        }
        internal bool MarkReceived() { lock (sync) { if (!IsCurrent || State != 1) return false; view.Write(8, 2); lastState = 2; return IsCurrent; } }
        private void Close()
        {
            lock (sync)
            {
                if (disposed != 0) return; disposed = 1;
                if (view != null) view.Dispose(); if (map != null) map.Dispose();
                if (ready != null) ready.Dispose(); if (cancelled != null) cancelled.Dispose();
            }
        }
        public void Dispose()
        { try { if (cancelled != null) cancelled.Set(); } catch { } if (Acknowledgment != null) Acknowledgment.Dispose(); Close(); }
    }
}
