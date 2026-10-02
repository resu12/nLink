using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            private void CheckRetainedPayload()
            { if (!RetainedResourceCurrent) throw new IOException("frontier_source_not_current"); }
            private string ReadPayload(int index, LocalFrontierFileManifest.Payload fact)
            {
                SafeFileHandle handle = null; FileStream file = null;
                byte[] bytes = null;
                try
                {
                    CheckRetainedPayload();
                    if (fact == null || fact.File.Length < 1 || fact.File.Length > LocalFrontierFileManifest.MaximumPayloadBytes ||
                        !DuplicateSourceHandle(GetCurrentProcess(), pins.Handles[10 + index], GetCurrentProcess(), out handle, 0x80000000, false, 0)) return null;
                    file = new FileStream(handle, FileAccess.Read); handle = null;
                    if (file.Length != fact.File.Length) return null;
                    using (var stream = new MappedSource(file))
                    {
                        file = null; bytes = new byte[checked((int)fact.File.Length)]; int position = 0;
                        while (position < bytes.Length)
                        {
                            CheckRetainedPayload();
                            int take = Math.Min(32768, bytes.Length - position);
                            if (stream.Read(bytes, position, take) != take) return null;
                            position += take;
                        }
                        CheckRetainedPayload();
                        return DecodePayload(bytes, fact.Hash);
                    }
                }
                finally
                {
                    if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                    if (file != null) file.Dispose(); if (handle != null) handle.Dispose();
                }
            }
            private static string DecodePayload(byte[] bytes, byte[] expectedHash)
            {
                if (bytes == null || bytes.Length < 1 || bytes.Length > LocalFrontierFileManifest.MaximumPayloadBytes ||
                    expectedHash == null || expectedHash.Length != 32) return null;
                using (var sha = SHA256.Create())
                {
                    byte[] actual = sha.ComputeHash(bytes);
                    for (int i = 0; i < actual.Length; i++) if (actual[i] != expectedHash[i]) return null;
                }
                return new UTF8Encoding(false, true).GetString(bytes);
            }

            public sealed partial class EvaluationCustody
            {
                private bool payloadAttempted;
                private NativePayloadSet payloadSet;
                public NativePayloadSet TryCapturePayloads(EvaluationPair pair, object candidateContext, string[] candidateFull,
                    string[] candidateCompact, object candidateFullView, object candidateCompactView)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) ||
                            !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView)) return null;
                        bool accepted = false; NativePayload[] payloads = null; inputs.work.Start();
                        try
                        {
                            if (payloadAttempted || !Current()) return null;
                            payloadAttempted = true;
                            int count = inputs.facts.PayloadCount;
                            if (count > 2 * LocalFrontierFileManifest.MaximumPayloads || inputs.pins.Count != 10 + count) return null;
                            payloads = new NativePayload[count];
                            for (int i = 0; i < count; i++)
                            {
                                inputs.VisitEvaluation(); string writer;
                                var fact = inputs.facts.PayloadAt(i, out writer);
                                string text = inputs.ReadPayload(i, fact);
                                if (text == null) return null;
                                payloads[i] = new NativePayload(fact, writer, text);
                            }
                            if (!Current()) return null;
                            payloadSet = new NativePayloadSet(this, pair, payloads); accepted = true; return payloadSet;
                        }
                        catch { return null; }
                        finally
                        {
                            inputs.work.Stop();
                            if (!accepted)
                            {
                                if (payloads != null) foreach (var payload in payloads) if (payload != null) payload.Clear();
                                inputs.Close();
                            }
                        }
                    }
                }
                private bool OriginalPayloads(NativePayloadSet candidate)
                { return Original && state == 3 && Object.ReferenceEquals(payloadSet, candidate) && Object.ReferenceEquals(issued, candidate.Pair); }
                internal bool PayloadsCurrent(NativePayloadSet candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { return OriginalPayloads(candidate) && CheckCurrent(); } }
                internal NativePayload[] CopyPayloads(NativePayloadSet candidate)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    { return OriginalPayloads(candidate) && CheckCurrent() ? candidate.CopyRows() : null; }
                }
                internal bool OwnsPayload(NativePayloadSet candidate, NativePayload payload)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    { return OriginalPayloads(candidate) && candidate.Contains(payload) && CheckCurrent(); }
                }
                internal void DisposePayloads(NativePayloadSet candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { if (OriginalPayloads(candidate)) inputs.Close(); } }
                private void ClosePayloads() { if (payloadSet != null) payloadSet.Clear(); }
            }

            // Original capability, not a receipt-JSON import or publication issuer.
            public sealed class NativePayloadSet : IDisposable
            {
                private readonly EvaluationCustody owner;
                internal readonly EvaluationPair Pair;
                private readonly NativePayload[] rows;
                internal NativePayloadSet(EvaluationCustody owner, EvaluationPair pair, NativePayload[] rows)
                { this.owner = owner; Pair = pair; this.rows = rows; }
                public bool IsCurrent { get { return owner.PayloadsCurrent(this); } }
                public NativePayload[] GetPayloads() { return owner.CopyPayloads(this); }
                public bool Owns(NativePayload payload) { return owner.OwnsPayload(this, payload); }
                internal NativePayload[] CopyRows() { return (NativePayload[])rows.Clone(); }
                internal int IndexOf(NativePayload payload) { return Array.IndexOf(rows, payload); }
                internal bool Contains(NativePayload payload)
                { if (payload == null) return false; foreach (var row in rows) if (Object.ReferenceEquals(row, payload)) return true; return false; }
                internal void Clear() { foreach (var row in rows) row.Clear(); }
                public void Dispose() { owner.DisposePayloads(this); }
            }

            // Diagnostic fields alone cannot recreate the owning native set.
            // Text is strict UTF-8, but its receipt schema/meaning is not evaluated.
            public sealed class NativePayload
            {
                public Guid ObservationId { get; private set; }
                public string WriterId { get; private set; }
                public string FileIdentity { get; private set; }
                public long ByteCount { get; private set; }
                public string Sha256 { get; private set; }
                public string Text { get; private set; }
                internal NativePayload(LocalFrontierFileManifest.Payload fact, string writer, string text)
                {
                    ObservationId = fact.Observation; WriterId = writer; ByteCount = fact.File.Length; Text = text;
                    FileIdentity = fact.File.Volume.ToString("x8") + ":" + fact.File.IndexHigh.ToString("x8") + fact.File.IndexLow.ToString("x8");
                    Sha256 = BitConverter.ToString(fact.Hash).Replace("-", "").ToLowerInvariant();
                }
                internal void Clear() { Text = null; }
            }
        }
    }
}
