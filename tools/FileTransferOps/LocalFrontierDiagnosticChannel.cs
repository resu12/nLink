using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // Separate diagnostic transport. State: ready, writing, offered, adopted,
    // committed. Cancellation is independent and always wins.
    internal sealed partial class LocalFrontierDiagnosticChannel : IDisposable
    {
        internal const int HeaderBytes = 160, MaximumBytes = 4194304;
        private const int Magic = 0x4e464431;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex gate;
        private EventWaitHandle cancelled;
        private readonly bool parent;
        private byte[] binding, snapshot, bytes, secret;
        private bool committed;
        private int disposed;
        private LocalFrontierDiagnosticChannel(bool parent) { this.parent = parent; }
        internal WaitHandle Cancelled { get { return cancelled; } }
        internal int State { get { try { return view.ReadInt32(8); } catch { return -1; } } }
        internal bool IsOpen { get { try { return disposed == 0 && !cancelled.WaitOne(0); } catch { return false; } } }
        internal static byte[] Binding(byte[] scope, byte[] offer, byte[] facts)
        {
            var all = new byte[96]; Array.Copy(scope, 0, all, 0, 32); Array.Copy(offer, 0, all, 32, 32); Array.Copy(facts, 0, all, 64, 32);
            using (var sha = SHA256.Create()) return sha.ComputeHash(all);
        }
        internal static LocalFrontierDiagnosticChannel Open(string endpoint, byte[] binding, bool parent)
        {
            var value = new LocalFrontierDiagnosticChannel(parent);
            try
            {
                string name = endpoint + ".diagnostic-v1"; value.binding = (byte[])binding.Clone();
                value.map = parent ? MemoryMappedFile.CreateNew(name, HeaderBytes + MaximumBytes) : MemoryMappedFile.OpenExisting(name);
                value.view = value.map.CreateViewAccessor(0, HeaderBytes + MaximumBytes);
                value.gate = parent ? new Mutex(false, name + ".gate") : Mutex.OpenExisting(name + ".gate");
                value.cancelled = parent ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled") : EventWaitHandle.OpenExisting(name + ".cancelled");
                if (!value.OpenRendezvous(name)) throw new InvalidOperationException();
                if (parent) { value.view.Write(0, Magic); value.view.Write(4, 1); value.view.WriteArray(16, binding, 0, 32); }
                if (!value.BaseHeader(value.Header()) || value.State != 0 || !value.IsOpen) throw new InvalidOperationException();
                return value;
            }
            catch { value.Dispose(); return null; }
        }
        private byte[] Header() { var h = new byte[HeaderBytes]; if (view.ReadArray(0, h, 0, h.Length) != h.Length) throw new InvalidOperationException(); return h; }
        private bool BaseHeader(byte[] h)
        {
            if (BitConverter.ToInt32(h, 0) != Magic || BitConverter.ToInt32(h, 4) != 1) return false;
            for (int i = 0; i < 32; i++) if (h[16 + i] != binding[i]) return false;
            for (int i = 144; i < HeaderBytes; i++) if (h[i] != 0) return false;
            return true;
        }
        private static byte[] Commitment(byte[] header, byte[] token)
        {
            var data = new byte[96]; Array.Copy(header, 16, data, 0, 64); Array.Copy(token, 0, data, 64, 32);
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }
        private bool Locked(Func<bool> action)
        {
            bool held = false;
            try { held = gate.WaitOne(0); return held && IsOpen && action(); }
            catch (AbandonedMutexException) { held = true; return false; }
            catch { return false; }
            finally { if (held) try { gate.ReleaseMutex(); } catch { } }
        }
        internal bool Offer(byte[] payload, Action check)
        {
            bool success = !parent && Locked(delegate
            {
                check(); var h = Header();
                if (!BaseHeader(h) || State != 0 || BitConverter.ToInt32(h, 12) != 0 || payload == null || payload.Length < 1 || payload.Length > MaximumBytes) return false;
                for (int i = 48; i < 144; i++) if (h[i] != 0) return false;
                view.Write(8, 1); view.Write(12, payload.Length);
                using (var sha = SHA256.Create()) view.WriteArray(48, sha.ComputeHash(payload), 0, 32);
                secret = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
                h = Header(); view.WriteArray(80, Commitment(h, secret), 0, 32);
                for (int i = 0; i < payload.Length; i += 32768) { check(); view.WriteArray(HeaderBytes + i, payload, i, Math.Min(32768, payload.Length - i)); }
                check(); if (!IsOpen) return false;
                view.Write(8, 2); snapshot = Header(); bytes = payload; return true;
            });
            // Wake the peer only after releasing the nonblocking channel gate.
            return success && Signal(offeredSignal);
        }
        internal bool Adopt(Func<byte[], bool> validate, Action check)
        {
            bool success = parent && Locked(delegate
            {
                check(); var h = Header(); int length = BitConverter.ToInt32(h, 12);
                if (!BaseHeader(h) || State != 2 || length < 1 || length > MaximumBytes) return false;
                for (int i = 112; i < 144; i++) if (h[i] != 0) return false;
                var copy = new byte[length];
                for (int i = 0; i < length; i += 32768)
                { check(); int count = Math.Min(32768, length - i); if (view.ReadArray(HeaderBytes + i, copy, i, count) != count) return false; }
                using (var sha = SHA256.Create()) { var hash = sha.ComputeHash(copy); for (int i = 0; i < 32; i++) if (hash[i] != h[48 + i]) return false; }
                if (!validate(copy)) return false;
                check(); if (!IsOpen) return false;
                snapshot = h; bytes = copy;
                if (!Exact(2, check)) return false;
                view.Write(8, 3); return true;
            });
            return success && Signal(adoptedSignal);
        }
        private bool Exact(int state, Action check)
        {
            if (!IsOpen || snapshot == null || bytes == null || State != state) return false;
            var h = Header();
            for (int i = 0; i < h.Length; i++) if ((i < 8 || i >= 12) && (i < 112 || i >= 144) && h[i] != snapshot[i]) return false;
            var buffer = new byte[32768];
            for (int i = 0; i < bytes.Length; i += buffer.Length)
            {
                check(); int count = Math.Min(buffer.Length, bytes.Length - i);
                if (view.ReadArray(HeaderBytes + i, buffer, 0, count) != count) return false;
                for (int j = 0; j < count; j++) if (buffer[j] != bytes[i + j]) return false;
            }
            if (state == 4)
            {
                var token = new byte[32]; Array.Copy(h, 112, token, 0, 32); var hash = Commitment(h, token);
                for (int i = 0; i < 32; i++) if (hash[i] != snapshot[80 + i]) return false;
            }
            else for (int i = 112; i < 144; i++) if (h[i] != 0) return false;
            check(); return IsOpen;
        }
        internal bool Commit(Action check)
        {
            return !parent && Locked(delegate
            {
                if (secret == null || !Exact(3, check)) return false;
                check(); if (!IsOpen) return false;
                view.WriteArray(112, secret, 0, 32); view.Write(8, 4); committed = true; return true;
            });
        }
        internal byte[] Take(Action check)
        {
            byte[] result = null;
            if (parent && Locked(delegate { if (!Exact(4, check)) return false; result = bytes; bytes = null; return true; })) return result;
            return null;
        }
        internal void Cancel() { try { if (cancelled != null) cancelled.Set(); } catch { } }
        internal void CloseWorker() { if (!committed) Cancel(); Dispose(); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (secret != null) Array.Clear(secret, 0, secret.Length);
            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            bytes = secret = snapshot = null;
            if (offeredSignal != null) offeredSignal.Dispose(); if (adoptedSignal != null) adoptedSignal.Dispose();
            if (view != null) view.Dispose(); if (map != null) map.Dispose(); if (gate != null) gate.Dispose(); if (cancelled != null) cancelled.Dispose();
        }
    }
}
