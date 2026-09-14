using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        private PublicationChannel publicationChannel;
        private string publicationRequestHash;
        private int publicationTaken;

        // Opt-in only. The accepted GUI owner and the parent's original source
        // hash pass remain prerequisites; no caller-authored request digest.
        public static LocalRetainedSourceRequest TryCreateWithPublication(Guid attempt, string owner, string run, string scenario,
            LocalInputReadLease lease, long lines, LocalGuiWriterRequest.Completion writer)
        {
            if (lease == null || !lease.IsVerified || writer == null ||
                !writer.MatchesSource(run, scenario, lease.Path, lease.ExpectedSha256, lease.ByteCount, lines)) return null;
            return TryCreateCore(attempt, owner, run, scenario, lease, lines, writer, true);
        }

        // Transport receipt only, not the parent's primary diagnostic/terminal
        // acceptance gate. Live parents must call only AFTER their original
        // checks and immutable terminal receipt, including scoped live opt-in.
        public TransportPublication TakePublicationAfterExit()
        {
            if (Interlocked.Exchange(ref publicationTaken, 1) != 0) return null;
            try
            {
                if (publicationChannel == null) return null;
                bool alreadyFrozen = publicationChannel.Freeze();
                int code;
                if (alreadyFrozen || Volatile.Read(ref disposed) != 0 || frozenSource == null || !frozenSource.CanRead ||
                    workerExit == null || !workerExit.WaitOne(0) || workerId <= 0 || workerCreation <= 0 ||
                    !GetExitCodeProcess(workerExit.SafeWaitHandle, out code) || code != 0) return null;
                return publicationChannel.Take(RequestId, workerId, workerCreation, publicationRequestHash);
            }
            catch { return null; }
        }

        public sealed partial class Worker
        {
            private PublicationChannel publicationChannel;
            internal string PublicationNamespace { get; private set; }
            internal void OpenPublicationChannel(string name)
            { publicationChannel = PublicationChannel.Open(name); PublicationNamespace = name; }
            internal bool Publish(PreparedPublication value, Func<bool> current)
            { return publicationChannel == null || publicationChannel.Publish(value, current); }
            internal void CancelPublicationChannel()
            { if (publicationChannel != null) publicationChannel.Freeze(); }
            private void ClosePublicationChannel()
            { if (publicationChannel != null) publicationChannel.Dispose(); }
        }

        public sealed class TransportPublication
        {
            private readonly byte[] payload;
            public PublicationCompletion Completion { get; private set; }
            public string OutputNamespace { get; private set; }
            internal TransportPublication(string name, PublicationCompletion completion, byte[] bytes)
            { OutputNamespace = name; Completion = completion; payload = bytes; }
            public byte[] CopyPayload() { return (byte[])payload.Clone(); }
        }

        // No public endpoint, writer, digest setter, offer method or deserializer.
        // The request-selected endpoint is opened before the original evaluations;
        // its only publisher is the private primary custody's actual issuer.
        private sealed class PublicationChannel : IDisposable
        {
            private const int Magic = 0x4e504332, ControlBytes = 384, MaximumPayloadBytes = 262144;
            private readonly string name;
            private readonly bool parent;
            private MemoryMappedFile controlMap, payloadMap;
            private MemoryMappedViewAccessor control, payload;
            private Mutex gate;
            private EventWaitHandle frozen;
            private int offered, disposed;
            private PublicationChannel(string name, bool parent) { this.name = name; this.parent = parent; }
            internal static PublicationChannel Create(string name) { return OpenCore(name, true); }
            internal static PublicationChannel Open(string name) { return OpenCore(name, false); }
            private static PublicationChannel OpenCore(string name, bool create)
            {
                var result = new PublicationChannel(name, create);
                try
                {
                    result.controlMap = create ? MemoryMappedFile.CreateNew(name + ".control", ControlBytes) :
                        MemoryMappedFile.OpenExisting(name + ".control");
                    result.payloadMap = create ? MemoryMappedFile.CreateNew(name + ".payload", MaximumPayloadBytes) :
                        MemoryMappedFile.OpenExisting(name + ".payload");
                    result.control = result.controlMap.CreateViewAccessor(0, ControlBytes);
                    result.payload = result.payloadMap.CreateViewAccessor(0, MaximumPayloadBytes);
                    result.gate = create ? new Mutex(false, name + ".gate") : Mutex.OpenExisting(name + ".gate");
                    result.frozen = create ? new EventWaitHandle(false, EventResetMode.ManualReset, name + ".frozen") :
                        EventWaitHandle.OpenExisting(name + ".frozen");
                    if (create) result.control.Write(0, Magic);
                    else if (result.frozen.WaitOne(0) || result.control.ReadInt32(0) != Magic) throw new InvalidOperationException();
                    return result;
                }
                catch { result.Dispose(); throw; }
            }
            internal bool Freeze()
            {
                try
                {
                    if (frozen == null) return true;
                    bool wasFrozen = frozen.WaitOne(0); frozen.Set(); return wasFrozen;
                }
                catch (ObjectDisposedException) { return true; }
            }
            internal bool Publish(PreparedPublication value, Func<bool> current)
            {
                if (parent || Interlocked.Exchange(ref offered, 1) != 0) return false;
                bool held = false;
                try
                {
                    if (!current() || frozen.WaitOne(0) || value == null) return false;
                    held = gate.WaitOne(0);
                    if (!held || frozen.WaitOne(0) || control.ReadInt32(0) != Magic || control.ReadInt32(4) != 0) return false;
                    var receipt = value.Completion;
                    byte[] bytes = value.CopyPayload();
                    if (bytes == null || bytes.Length < 1 || bytes.Length > MaximumPayloadBytes || bytes.Length != receipt.PayloadBytes) return false;
                    control.Write(4, 1); // Partial until the final current-owner check.
                    control.Write(8, receipt.WorkerProcessId); control.Write(16, receipt.WorkerCreationFileTime);
                    WriteGuid(24, receipt.RequestId); WriteGuid(40, receipt.FullInvocationId); WriteGuid(56, receipt.CompactInvocationId);
                    WriteHash(72, receipt.RequestSha256); WriteHash(136, receipt.InputDescriptorsSha256);
                    WriteHash(200, receipt.SelectedRowsSha256); control.Write(264, bytes.Length); WriteHash(268, receipt.PayloadSha256);
                    var catalog = new byte[32];
                    for (int i = 0; i < catalog.Length; i++) catalog[i] = Convert.ToByte(receipt.CatalogSha256.Substring(2 * i, 2), 16);
                    control.WriteArray(332, catalog, 0, catalog.Length);
                    payload.WriteArray(0, bytes, 0, bytes.Length);
                    if (!current() || frozen.WaitOne(0)) return false;
                    control.Write(4, 2);
                    return true;
                }
                catch (AbandonedMutexException) { held = true; return false; }
                catch { return false; }
                finally { Release(gate, held); }
            }
            internal TransportPublication Take(Guid request, int worker, long creation, string expectedRequestHash)
            {
                bool held = false;
                try
                {
                    held = gate.WaitOne(0);
                    if (!parent || !held || control.ReadInt32(0) != Magic || control.ReadInt32(4) != 2 ||
                        control.ReadInt32(8) != worker || control.ReadInt64(16) != creation || ReadGuid(24) != request ||
                        ReadHash(72) != expectedRequestHash) return null;
                    Guid full = ReadGuid(40), compact = ReadGuid(56);
                    string inputs = ReadHash(136), selection = ReadHash(200), expectedPayload = ReadHash(268);
                    int length = control.ReadInt32(264);
                    if (full == Guid.Empty || compact == Guid.Empty || full == compact || inputs == null || selection == null ||
                        expectedPayload == null || length < 1 || length > MaximumPayloadBytes) return null;
                    var bytes = new byte[length];
                    if (payload.ReadArray(0, bytes, 0, length) != length) return null;
                    using (var sha = SHA256.Create())
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        if (actual != expectedPayload) return null;
                    }
                    var catalog = new byte[32];
                    if (control.ReadArray(332, catalog, 0, catalog.Length) != catalog.Length) return null;
                    string catalogHash = BitConverter.ToString(catalog).Replace("-", "").ToLowerInvariant();
                    return new TransportPublication(name, new PublicationCompletion(request, worker, creation, full, compact,
                        expectedRequestHash, inputs, selection, length, expectedPayload, catalogHash), bytes);
                }
                catch (AbandonedMutexException) { held = true; return null; }
                catch { return null; }
                finally { Release(gate, held); }
            }
            private void WriteGuid(int offset, Guid value) { control.WriteArray(offset, value.ToByteArray(), 0, 16); }
            private Guid ReadGuid(int offset)
            {
                var bytes = new byte[16]; return control.ReadArray(offset, bytes, 0, 16) == 16 ? new Guid(bytes) : Guid.Empty;
            }
            private void WriteHash(int offset, string value) { control.WriteArray(offset, Encoding.ASCII.GetBytes(value), 0, 64); }
            private string ReadHash(int offset)
            {
                var bytes = new byte[64];
                if (control.ReadArray(offset, bytes, 0, 64) != 64) return null;
                string value = Encoding.ASCII.GetString(bytes);
                return Regex.IsMatch(value, @"\A[0-9a-f]{64}\z") ? value : null;
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                if (parent && frozen != null) Freeze();
                if (control != null) control.Dispose(); if (payload != null) payload.Dispose();
                if (controlMap != null) controlMap.Dispose(); if (payloadMap != null) payloadMap.Dispose();
                if (gate != null) gate.Dispose(); if (frozen != null) frozen.Dispose();
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeWaitHandle processHandle, out int code);
    }
}
