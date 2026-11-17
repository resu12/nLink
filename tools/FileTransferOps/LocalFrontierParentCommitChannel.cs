using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // Parent-owned escrow endpoint. GUI release is distinct from cancellation.
    internal sealed class LocalFrontierParentCommitChannel : IDisposable
    {
        private const int Header = 80, Maximum = LocalGuiWriterRequest.MaximumCompletionBytes;
        private readonly object sync = new object();
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private EventWaitHandle cancelled;
        private byte[] initial, committed, body;
        private bool closed;
        private int lastState = 1;
        internal WaitHandle Cancelled { get { return cancelled; } }
        private LocalFrontierParentCommitChannel() { }
        internal static LocalFrontierParentCommitChannel Create(string endpoint, byte[] binding) { return OpenCore(endpoint, binding, true); }
        internal static LocalFrontierParentCommitChannel Open(string endpoint, byte[] binding) { return OpenCore(endpoint, binding, false); }
        private static LocalFrontierParentCommitChannel OpenCore(string endpoint, byte[] binding, bool create)
        {
            var value = new LocalFrontierParentCommitChannel();
            try
            {
                if (binding == null || binding.Length != 32) return null;
                string name = endpoint + ".commit-v1";
                value.map = create ? MemoryMappedFile.CreateNew(name, Header + Maximum) : MemoryMappedFile.OpenExisting(name);
                value.view = value.map.CreateViewAccessor(0, Header + Maximum);
                value.cancelled = create ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled") : EventWaitHandle.OpenExisting(name + ".cancelled");
                value.initial = new byte[Header];
                Array.Copy(BitConverter.GetBytes(0x4e504335), 0, value.initial, 0, 4);
                Array.Copy(BitConverter.GetBytes(1), 0, value.initial, 4, 4); Array.Copy(BitConverter.GetBytes(1), 0, value.initial, 8, 4);
                Array.Copy(binding, 0, value.initial, 16, 32);
                if (create) value.view.WriteArray(0, value.initial, 0, Header);
                if (!value.CurrentHeader() || value.view.ReadInt32(8) != 1) return null;
                for (int i = 0; i < Header + Maximum; i++) if (value.view.ReadByte(i) != (i < Header ? value.initial[i] : 0)) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) { if (create) value.Dispose(); else value.Release(); } }
        }
        private bool CurrentHeader()
        {
            if (closed || cancelled.WaitOne(0)) return false;
            int state = view.ReadInt32(8); if (state < lastState || state < 1 || state > 3) return false; lastState = state;
            for (int i = 0; i < 48; i++) if ((i < 8 || i >= 16) && view.ReadByte(i) != initial[i]) return false;
            return true;
        }
        internal bool IsArmed
        { get { lock (sync) { try { return CurrentHeader() && view.ReadInt32(8) >= 2; } catch { return false; } } } }
        internal bool Arm()
        { lock (sync) { if (!CurrentHeader() || view.ReadInt32(8) != 1) return false; view.Write(8, 2); lastState = 2; return IsArmed; } }
        internal bool Publish(byte[] value)
        {
            lock (sync)
            {
                if (!CurrentHeader() || view.ReadInt32(8) != 2 || value == null || value.Length < 1 || value.Length > Maximum) return false;
                view.WriteArray(Header, value, 0, value.Length); view.Write(12, value.Length);
                using (var hash = SHA256.Create()) { var digest = hash.ComputeHash(value); view.WriteArray(48, digest, 0, digest.Length); }
                view.Write(8, 3); lastState = 3; return IsCommitted;
            }
        }
        internal bool IsCommitted
        {
            get
            {
                lock (sync)
                {
                    try
                    {
                        if (!CurrentHeader() || view.ReadInt32(8) != 3) return false;
                        if (committed == null)
                        {
                            var header = new byte[Header]; view.ReadArray(0, header, 0, Header);
                            int length = BitConverter.ToInt32(header, 12); if (length < 1 || length > Maximum) return false;
                            var bytes = new byte[length]; view.ReadArray(Header, bytes, 0, length);
                            using (var hash = SHA256.Create()) { var digest = hash.ComputeHash(bytes); for (int i = 0; i < 32; i++) if (digest[i] != header[48 + i]) return false; }
                            for (int i = length; i < Maximum; i++) if (view.ReadByte(Header + i) != 0) return false;
                            committed = header; body = bytes;
                        }
                        for (int i = 0; i < Header; i++) if (view.ReadByte(i) != committed[i]) return false;
                        for (int i = 0; i < body.Length; i++) if (view.ReadByte(Header + i) != body[i]) return false;
                        return !cancelled.WaitOne(0);
                    }
                    catch { return false; }
                }
            }
        }
        internal bool Matches(byte[] value)
        { lock (sync) { if (!IsCommitted || value == null || value.Length != body.Length) return false; for (int i = 0; i < body.Length; i++) if (value[i] != body[i]) return false; return true; } }
        public void Dispose() { try { if (cancelled != null) cancelled.Set(); } catch { } Release(); }
        internal void Release()
        {
            lock (sync)
            { if (closed) return; closed = true; if (view != null) view.Dispose(); if (map != null) map.Dispose(); if (cancelled != null) cancelled.Dispose(); }
        }
    }
}
