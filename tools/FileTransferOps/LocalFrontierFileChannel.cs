#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    // Separate v1 handshake. This owns control bytes, not producer authority.
#if NET8_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    internal sealed class LocalFrontierFileChannel : IDisposable
    {
        internal const int HeaderBytes = 192, Magic = 0x4e464a31;
        internal const int Ready = 0, Attached = 1, Submitted = 2, Adopting = 3, Adopted = 4, Acknowledged = 5;
        internal MemoryMappedFile Map;
        internal MemoryMappedViewAccessor View;
        internal Mutex Gate;
        internal EventWaitHandle ReadyEvent, SubmittedEvent, AdoptedEvent;
        internal EventWaitHandle cancelled;
        private byte[] registration, body, digest;
        private Guid submission;
        private int disposed, lastState;
        internal Guid GuiRequest { get; private set; }
        internal int Slot { get; private set; }
        private LocalFrontierFileChannel() { }
        internal int State { get { return View.ReadInt32(8); } }

        internal static LocalFrontierFileChannel Create(string name, Guid gui, Guid app, int slot,
            int guiId, long guiTime, int appId, long appTime)
        {
            var channel = new LocalFrontierFileChannel();
            try
            {
                string endpoint = name + ".files-v1";
                channel.Map = MemoryMappedFile.CreateNew(endpoint, HeaderBytes + LocalFrontierFileManifest.MaximumBytes);
                channel.View = channel.Map.CreateViewAccessor();
                channel.Gate = new Mutex(false, endpoint + ".gate");
                channel.ReadyEvent = NewEvent(endpoint + ".ready");
                channel.SubmittedEvent = NewEvent(endpoint + ".submitted");
                channel.AdoptedEvent = NewEvent(endpoint + ".adopted");
                channel.cancelled = NewEvent(endpoint + ".cancelled");
                channel.View.Write(0, Magic); channel.View.Write(4, 1); channel.View.Write(12, slot);
                channel.WriteId(16, gui); channel.WriteId(32, app); channel.WriteId(48, Guid.NewGuid());
                channel.View.Write(64, guiId); channel.View.Write(68, appId);
                channel.View.Write(72, guiTime); channel.View.Write(80, appTime);
                channel.SaveRegistration();
                var result = channel; channel = null; return result;
            }
            catch { return null; }
            finally { if (channel != null) channel.Dispose(); }
        }
        internal static LocalFrontierFileChannel Open(string name, Guid app, int guiId, long guiTime, int appId, long appTime)
        {
            var channel = new LocalFrontierFileChannel();
            try
            {
                string endpoint = name + ".files-v1";
                channel.Map = MemoryMappedFile.OpenExisting(endpoint); channel.View = channel.Map.CreateViewAccessor(0, HeaderBytes + LocalFrontierFileManifest.MaximumBytes);
                channel.Gate = Mutex.OpenExisting(endpoint + ".gate");
                channel.ReadyEvent = EventWaitHandle.OpenExisting(endpoint + ".ready");
                channel.SubmittedEvent = EventWaitHandle.OpenExisting(endpoint + ".submitted");
                channel.AdoptedEvent = EventWaitHandle.OpenExisting(endpoint + ".adopted");
                channel.cancelled = EventWaitHandle.OpenExisting(endpoint + ".cancelled");
                if (!channel.ReadyEvent.WaitOne(0) || channel.State != Ready || channel.View.ReadInt32(0) != Magic ||
                    channel.View.ReadInt32(4) != 1 || channel.ReadId(16) == Guid.Empty || channel.ReadId(32) != app ||
                    channel.ReadId(48) == Guid.Empty || channel.View.ReadInt32(64) != guiId || channel.View.ReadInt32(68) != appId ||
                    channel.View.ReadInt64(72) != guiTime || channel.View.ReadInt64(80) != appTime ||
                    channel.View.ReadInt32(12) < 0 || channel.View.ReadInt32(12) > 1 || !channel.EmptySubmission()) return null;
                channel.SaveRegistration();
                var result = channel; channel = null; return result;
            }
            catch { return null; }
            // Failed foreign opens must not revoke the actual channel.
            finally { if (channel != null) channel.Close(); }
        }
        private void SaveRegistration()
        {
            registration = new byte[88]; View.ReadArray(0, registration, 0, registration.Length);
            GuiRequest = ReadId(16); Slot = View.ReadInt32(12);
        }
        internal bool IsCurrent
        {
            get
            {
                try
                {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                    long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                    if (Volatile.Read(ref disposed) != 0 || cancelled.WaitOne(0)) return false;
                    // Fresh bounded snapshots only: retain every comparison,
                    // without acquiring the mapped view separately per byte.
                    var header = new byte[registration.Length];
                    if (View.ReadArray(0, header, 0, header.Length) != header.Length) return false;
                    for (int i = 0; i < header.Length; i++) if ((i < 8 || i >= 12) && header[i] != registration[i]) return false;
                    int state = State;
                    if (state < Ready || state > Acknowledged || View.ReadInt32(92) != 0) return false;
                    int previous;
                    do { previous = Volatile.Read(ref lastState); if (state < previous) return false; }
                    while (Interlocked.CompareExchange(ref lastState, state, previous) != previous);
                    var reserved = new byte[HeaderBytes - 160];
                    if (View.ReadArray(160, reserved, 0, reserved.Length) != reserved.Length) return false;
                    for (int i = 0; i < reserved.Length; i++) if (reserved[i] != 0) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                    NLink.Tests.NativeContentParserProfileForTest.ReaderStage(24, sample);
#endif
                    return body == null || MatchesSubmission();
                }
                catch { return false; }
            }
        }
        internal bool EmptySubmission()
        { for (int i = 88; i < HeaderBytes; i++) if (View.ReadByte(i) != 0) return false; return true; }
        internal bool RememberSubmission(byte[] bytes)
        {
            if (body != null || bytes == null || bytes.Length > LocalFrontierFileManifest.MaximumBytes) return false;
            body = (byte[])bytes.Clone(); using (var hash = SHA256.Create()) digest = hash.ComputeHash(body);
            submission = Guid.NewGuid(); return true;
        }
        internal void PublishSubmission()
        {
            View.WriteArray(HeaderBytes, body, 0, body.Length); View.Write(88, body.Length);
            View.WriteArray(96, digest, 0, digest.Length); WriteId(128, submission);
            View.Write(8, Submitted);
        }
        internal byte[] CaptureSubmission()
        {
            if (body != null || !IsCurrent || State != Submitted || !SubmittedEvent.WaitOne(0)) return null;
            int length = View.ReadInt32(88);
            if (length < 217 || length > LocalFrontierFileManifest.MaximumBytes || ReadId(128) == Guid.Empty || ReadId(144) != Guid.Empty) return null;
            var bytes = new byte[length]; if (View.ReadArray(HeaderBytes, bytes, 0, length) != length) return null;
            body = bytes; using (var hash = SHA256.Create()) digest = hash.ComputeHash(bytes);
            submission = ReadId(128);
            return MatchesSubmission() ? (byte[])bytes.Clone() : null;
        }
        private bool MatchesSubmission()
        {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
            if (View.ReadInt32(88) != body.Length || ReadId(128) != submission) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(23, sample);
#endif
            if (!MatchesBytes(96, digest) || !MatchesBytes(HeaderBytes, body)) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
            int state = State;
            bool result = state >= Submitted && (state < Adopted ? ReadId(144) == Guid.Empty : ReadId(144) == submission);
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(23, sample); return result;
#else
            int state = State;
            return state >= Submitted && (state < Adopted ? ReadId(144) == Guid.Empty : ReadId(144) == submission);
#endif
        }
        private bool MatchesBytes(int offset, byte[] expected)
        {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
            var actual = new long[expected.Length / 8];
            if (View.ReadArray(offset, actual, 0, actual.Length) != actual.Length) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(20, sample);
            sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
            // Compare every byte against the retained packet on every call.
            // Group complete words without caching approval or skipping tails.
            for (int i = 0; i < actual.Length; i++) if (actual[i] != BitConverter.ToInt64(expected, i * 8)) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(21, sample);
            sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
            for (int i = actual.Length * 8; i < expected.Length; i++) if (View.ReadByte(offset + i) != expected[i]) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(22, sample);
#endif
            return true;
        }
        // The original callers signal peers only after releasing the mutex.
        internal void PublishAdopted() { WriteId(144, submission); View.Write(8, Adopted); }
        internal void Cancel() { try { if (cancelled != null) cancelled.Set(); } catch (ObjectDisposedException) { } }
        public void Dispose() { Cancel(); Close(); }
        internal void Close()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (View != null) View.Dispose(); if (Map != null) Map.Dispose(); if (Gate != null) Gate.Dispose();
            if (ReadyEvent != null) ReadyEvent.Dispose(); if (SubmittedEvent != null) SubmittedEvent.Dispose();
            if (AdoptedEvent != null) AdoptedEvent.Dispose(); if (cancelled != null) cancelled.Dispose();
        }
        internal static void Release(Mutex gate, bool held)
        { if (held) { try { gate.ReleaseMutex(); } catch (ApplicationException) { } catch (ObjectDisposedException) { } } }
        private static EventWaitHandle NewEvent(string name) { return new EventWaitHandle(false, EventResetMode.ManualReset, name); }
        private void WriteId(int offset, Guid id) { View.WriteArray(offset, id.ToByteArray(), 0, 16); }
        private Guid ReadId(int offset) { var bytes = new byte[16]; View.ReadArray(offset, bytes, 0, 16); return new Guid(bytes); }
    }
}
