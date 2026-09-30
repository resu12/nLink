using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // A single GUI offer and parent pull observation. State 2 is NOT an ack.
    internal sealed class LocalFrontierParentFileChannel : IDisposable
    {
        internal const int HeaderBytes = 128, Bytes = HeaderBytes + LocalFrontierParentFilePacket.MaximumBytes;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private EventWaitHandle ready, cancelled;
        private byte[] snapshot;
        private int disposed, lastState = 1;
        private CancellationTokenRegistration lifetime;
        internal LocalFrontierParentFactsChannel Facts;
        internal string Endpoint;
        internal byte[] OfferHash { get { using (var hash = SHA256.Create()) return hash.ComputeHash(snapshot); } }
        internal byte[] Body { get; private set; }
        internal WaitHandle Cancelled { get { return cancelled; } }
        internal int State { get { try { return view.ReadInt32(8); } catch { return -1; } } }
        private LocalFrontierParentFileChannel() { }
        internal static LocalFrontierParentFileChannel Create(string endpoint, byte[] binding, byte[] body, CancellationToken token)
        {
            var value = new LocalFrontierParentFileChannel();
            try
            {
                if (binding == null || binding.Length != 32 || LocalFrontierParentFilePacket.Decode(body) == null || token.IsCancellationRequested) return null;
                string name = endpoint + ".files-v1";
                value.Endpoint = name;
                value.map = MemoryMappedFile.CreateNew(name, Bytes); value.view = value.map.CreateViewAccessor(0, Bytes);
                value.ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
                value.cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled");
                value.view.Write(0, 0x4e505432); value.view.Write(4, 1); value.view.Write(8, 1);
                value.view.WriteArray(16, binding, 0, 32); value.view.WriteArray(48, Guid.NewGuid().ToByteArray(), 0, 16);
                value.view.Write(64, body.Length);
                using (var hash = SHA256.Create()) value.view.WriteArray(96, hash.ComputeHash(body), 0, 32);
                value.view.WriteArray(HeaderBytes, body, 0, body.Length);
                if (!value.Capture(binding)) return null;
                value.ready.Set(); value.lifetime = token.Register(value.Cancel);
                if (!value.IsCurrent) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) value.Dispose(); }
        }
        internal static LocalFrontierParentFileChannel Open(string endpoint, byte[] binding)
        {
            var value = new LocalFrontierParentFileChannel();
            try
            {
                string name = endpoint + ".files-v1";
                value.Endpoint = name;
                value.map = MemoryMappedFile.OpenExisting(name); value.view = value.map.CreateViewAccessor(0, Bytes);
                value.ready = EventWaitHandle.OpenExisting(name + ".ready"); value.cancelled = EventWaitHandle.OpenExisting(name + ".cancelled");
                if (!value.ready.WaitOne(0) || value.cancelled.WaitOne(0) || !value.Capture(binding) || !value.IsCurrent || value.State != 1) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            // A foreign failed open may close its handles, never cancel a peer.
            finally { if (value != null) value.Close(); }
        }
        private bool Capture(byte[] binding)
        {
            // Validate one fixed-size local snapshot in full; never validate a
            // header and then retain a different, raced shared-memory header.
            snapshot = new byte[Bytes]; view.ReadArray(0, snapshot, 0, Bytes);
            if (binding == null || binding.Length != 32 || BitConverter.ToInt32(snapshot, 0) != 0x4e505432 ||
                BitConverter.ToInt32(snapshot, 4) != 1 || BitConverter.ToInt32(snapshot, 8) != 1) return false;
            for (int i = 0; i < 32; i++) if (snapshot[16 + i] != binding[i]) return false;
            var nonce = new byte[16]; Array.Copy(snapshot, 48, nonce, 0, 16); if (new Guid(nonce) == Guid.Empty) return false;
            int length = BitConverter.ToInt32(snapshot, 64); if (length < 584 || length > LocalFrontierParentFilePacket.MaximumBytes) return false;
            Body = new byte[length]; Array.Copy(snapshot, HeaderBytes, Body, 0, length);
            if (LocalFrontierParentFilePacket.Decode(Body) == null) return false;
            using (var hash = SHA256.Create())
            { var digest = hash.ComputeHash(Body); for (int i = 0; i < 32; i++) if (snapshot[96 + i] != digest[i]) return false; }
            for (int i = 0; i < Bytes; i++)
                if ((i >= 12 && i < 16 || i >= 68 && i < 96 || i >= HeaderBytes + length) && snapshot[i] != 0) return false;
            return true;
        }
        internal bool IsCurrent
        {
            get
            {
                try
                {
                    if (Volatile.Read(ref disposed) != 0 || !ready.WaitOne(0) || cancelled.WaitOne(0)) return false;
                    int state = State; if (state < Volatile.Read(ref lastState) || state > 2 || state < 1) return false;
                    if (state == 2) Interlocked.Exchange(ref lastState, 2);
                    var current = new byte[Bytes];
                    if (view.ReadArray(0, current, 0, current.Length) != current.Length) return false;
                    for (int i = 0; i < Bytes; i++) if ((i < 8 || i >= 12) && current[i] != snapshot[i]) return false;
                    return Facts == null || Facts.IsCurrent;
                }
                catch { return false; }
            }
        }
        internal bool MarkPulled()
        { if (!IsCurrent || State != 1) return false; view.Write(8, 2); lastState = 2; return IsCurrent; }
        private void Cancel() { try { if (cancelled != null) cancelled.Set(); } catch { } if (Facts != null) Facts.Dispose(); Close(); }
        private void Close()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (view != null) view.Dispose(); if (map != null) map.Dispose();
            if (ready != null) ready.Dispose(); if (cancelled != null) cancelled.Dispose();
        }
        public void Dispose() { Cancel(); lifetime.Dispose(); }
    }
}
