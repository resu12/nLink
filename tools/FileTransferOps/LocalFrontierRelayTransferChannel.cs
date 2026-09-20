using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // One bounded offer. Pulled/forwarded are observations, never acceptance.
    // Only an original relay's successful terminal take may accept its pins.
    internal sealed class LocalFrontierRelayTransferChannel : IDisposable
    {
        private const int Header = 96;
        private const int Maximum = LocalFrontierParentFilePacket.MaximumBytes + LocalFrontierParentFacts.MaximumBytes + LocalGuiWriterRequest.MaximumCompletionBytes;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private EventWaitHandle ready, pulled, cancelled;
        private byte[] snapshot;
        private int lastState = 1;
        private bool closed;
        internal byte[] Files, Facts, Completion;
        internal WaitHandle Pulled { get { return pulled; } }
        internal WaitHandle Cancelled { get { return cancelled; } }
        internal int State { get { try { return view.ReadInt32(8); } catch { return -1; } } }

        internal static bool Offered(string endpoint)
        {
            try { using (var signal = EventWaitHandle.OpenExisting(endpoint + ".transfer-v1.ready")) return signal.WaitOne(0); }
            catch { return false; }
        }
        internal static LocalFrontierRelayTransferChannel Create(string endpoint, byte[] binding,
            byte[] files, byte[] facts, byte[] completion, Action check)
        {
            if (!Lengths(files, facts, completion) || binding == null || binding.Length != 32) return null;
            var value = new LocalFrontierRelayTransferChannel(); bool owns = false;
            try
            {
                check(); string name = endpoint + ".transfer-v1";
                value.map = MemoryMappedFile.CreateNew(name, Header + Maximum);
                value.view = value.map.CreateViewAccessor(0, Header + Maximum);
                bool created;
                value.ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready", out created); if (!created) return null;
                value.pulled = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".pulled", out created); if (!created) return null;
                value.cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled", out created); if (!created) return null;
                owns = true;
                value.snapshot = new byte[Header + files.Length + facts.Length + completion.Length];
                var bytes = value.snapshot;
                Array.Copy(BitConverter.GetBytes(0x4e525432), 0, bytes, 0, 4);
                Array.Copy(BitConverter.GetBytes(1), 0, bytes, 4, 4); Array.Copy(BitConverter.GetBytes(1), 0, bytes, 8, 4);
                Array.Copy(BitConverter.GetBytes(files.Length), 0, bytes, 12, 4);
                Array.Copy(BitConverter.GetBytes(facts.Length), 0, bytes, 16, 4);
                Array.Copy(BitConverter.GetBytes(completion.Length), 0, bytes, 20, 4);
                Array.Copy(binding, 0, bytes, 24, 32);
                Array.Copy(files, 0, bytes, Header, files.Length);
                Array.Copy(facts, 0, bytes, Header + files.Length, facts.Length);
                Array.Copy(completion, 0, bytes, Header + files.Length + facts.Length, completion.Length);
                using (var hash = SHA256.Create()) Array.Copy(hash.ComputeHash(bytes, Header, bytes.Length - Header), 0, bytes, 64, 32);
                value.view.WriteArray(0, bytes, 0, bytes.Length);
                value.Split(); check(); value.ready.Set();
                if (!value.CheckCurrent(check)) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) { if (owns) value.Dispose(); else value.Release(); } }
        }
        internal static LocalFrontierRelayTransferChannel Open(string endpoint, byte[] binding, Action check)
        {
            var value = new LocalFrontierRelayTransferChannel();
            try
            {
                check(); string name = endpoint + ".transfer-v1";
                value.map = MemoryMappedFile.OpenExisting(name); value.view = value.map.CreateViewAccessor(0, Header + Maximum);
                value.ready = EventWaitHandle.OpenExisting(name + ".ready"); value.pulled = EventWaitHandle.OpenExisting(name + ".pulled");
                value.cancelled = EventWaitHandle.OpenExisting(name + ".cancelled");
                if (!value.ready.WaitOne(0) || value.pulled.WaitOne(0) || value.cancelled.WaitOne(0)) return null;
                var header = new byte[Header]; value.view.ReadArray(0, header, 0, Header);
                int files = BitConverter.ToInt32(header, 12), facts = BitConverter.ToInt32(header, 16), completion = BitConverter.ToInt32(header, 20);
                if (binding == null || binding.Length != 32 || BitConverter.ToInt32(header, 0) != 0x4e525432 ||
                    BitConverter.ToInt32(header, 4) != 1 || BitConverter.ToInt32(header, 8) != 1 ||
                    files < 584 || files > LocalFrontierParentFilePacket.MaximumBytes || facts < 1 || facts > LocalFrontierParentFacts.MaximumBytes ||
                    completion < 1 || completion > LocalGuiWriterRequest.MaximumCompletionBytes) return null;
                for (int i = 0; i < 32; i++) if (header[24 + i] != binding[i]) return null;
                for (int i = 56; i < 64; i++) if (header[i] != 0) return null;
                value.snapshot = new byte[Header + files + facts + completion];
                value.view.ReadArray(0, value.snapshot, 0, value.snapshot.Length);
                for (int i = 0; i < Header; i++) if (header[i] != value.snapshot[i]) return null;
                using (var hash = SHA256.Create())
                { var digest = hash.ComputeHash(value.snapshot, Header, value.snapshot.Length - Header); for (int i = 0; i < 32; i++) if (digest[i] != header[64 + i]) return null; }
                var buffer = new byte[32768];
                for (int i = value.snapshot.Length; i < Header + Maximum; i += buffer.Length)
                {
                    check(); int count = Math.Min(buffer.Length, Header + Maximum - i); value.view.ReadArray(i, buffer, 0, count);
                    for (int j = 0; j < count; j++) if (buffer[j] != 0) return null;
                }
                value.Split(); if (!value.CheckCurrent(check)) return null;
                var result = value; value = null; return result;
            }
            catch { return null; }
            finally { if (value != null) value.Release(); }
        }
        private static bool Lengths(byte[] files, byte[] facts, byte[] completion)
        { return files != null && files.Length >= 584 && files.Length <= LocalFrontierParentFilePacket.MaximumBytes &&
            facts != null && facts.Length > 0 && facts.Length <= LocalFrontierParentFacts.MaximumBytes &&
            completion != null && completion.Length > 0 && completion.Length <= LocalGuiWriterRequest.MaximumCompletionBytes; }
        private void Split()
        {
            Files = new byte[BitConverter.ToInt32(snapshot, 12)]; Facts = new byte[BitConverter.ToInt32(snapshot, 16)]; Completion = new byte[BitConverter.ToInt32(snapshot, 20)];
            Array.Copy(snapshot, Header, Files, 0, Files.Length); Array.Copy(snapshot, Header + Files.Length, Facts, 0, Facts.Length);
            Array.Copy(snapshot, Header + Files.Length + Facts.Length, Completion, 0, Completion.Length);
        }
        internal bool IsCurrent { get { return CheckCurrent(() => { }); } }
        internal bool CheckCurrent(Action check)
        {
            try
            {
                if (closed || cancelled.WaitOne(0) || !ready.WaitOne(0)) return false;
                int state = State; if (state < lastState || state < 1 || state > 3) return false; lastState = state;
                var buffer = new byte[32768];
                for (int i = 0; i < snapshot.Length; i += buffer.Length)
                {
                    check(); int count = Math.Min(buffer.Length, snapshot.Length - i); view.ReadArray(i, buffer, 0, count);
                    for (int j = 0; j < count; j++) if ((i + j < 8 || i + j >= 12) && buffer[j] != snapshot[i + j]) return false;
                }
                return !cancelled.WaitOne(0);
            }
            catch { return false; }
        }
        internal bool MarkPulled() { if (!IsCurrent || State != 1) return false; view.Write(8, 2); lastState = 2; pulled.Set(); return IsCurrent; }
        internal bool MarkForwarded(byte[] body, Action check)
        { if (!CheckCurrent(check) || State != 2 || !Matches(body)) return false; view.Write(8, 3); lastState = 3; return CheckCurrent(check); }
        internal bool Matches(byte[] body)
        { if (body == null || body.Length != Completion.Length) return false; for (int i = 0; i < body.Length; i++) if (body[i] != Completion[i]) return false; return true; }
        internal void Release()
        {
            if (closed) return; closed = true;
            if (view != null) view.Dispose(); if (map != null) map.Dispose();
            if (ready != null) ready.Dispose(); if (pulled != null) pulled.Dispose(); if (cancelled != null) cancelled.Dispose();
        }
        public void Dispose() { try { if (!closed && cancelled != null) cancelled.Set(); } catch { } Release(); }
    }
}
