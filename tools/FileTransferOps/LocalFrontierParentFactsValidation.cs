using System;
using System.Collections.Generic;
using System.Globalization;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    internal sealed partial class LocalFrontierParentFacts
    {
        internal static LocalFrontierParentFacts Decode(byte[] input, byte[] nativePacket, string run, string scenario, Action check)
        { try { return Parse(input, nativePacket, run, scenario, check); } catch { return null; } }
        private static LocalFrontierParentFacts Parse(byte[] input, byte[] nativePacket, string run, string scenario, Action check)
        {
            {
                if (input == null || input.Length < 1 || input.Length > MaximumBytes) return null;
                var bytes = (byte[])input.Clone(); var native = LocalFrontierParentFilePacket.Decode(nativePacket); if (native == null) return null;
                using (var reader = new Reader(bytes))
                {
                    var r = reader.Input; check(); Require(r.ReadInt32() == 0x4e504631 && r.ReadInt32() == 1);
                    Require(Equal(reader.Blob(LocalFrontierParentFilePacket.MaximumBytes), nativePacket));
                    Require(reader.Text(4096) == run && reader.Text(4096) == scenario && Hash(reader.Text(64)));
                    long sourceBytes = reader.Number(1, 268435456); int lines = checked((int)reader.Number(1, MaximumLines));
                    long bookmark = reader.Number(0, lines); int rangeCount = reader.Count(66);
                    Require(sourceBytes == native.Entries[0].File.Length && lines <= sourceBytes && rangeCount == 2 + 2 * (native.Entries.Length - 10));
                    string ledger = null, generation = null; long initialRead = 0, initialCommitted = 0, initialSync = 0;
                    for (int i = 0; i < 2; i++)
                    {
                        string currentLedger = reader.Text(32), currentGeneration = reader.Text(32); Guid parsed;
                        Require(Guid.TryParseExact(currentLedger, "N", out parsed) && parsed != Guid.Empty && Guid.TryParseExact(currentGeneration, "N", out parsed) && parsed != Guid.Empty);
                        Require(r.ReadInt32() == i); int count = reader.Count(lines); long read = reader.Number(0, sourceBytes), committed = reader.Number(0, read);
                        long sync = reader.Number(1, Int64.MaxValue); Require(reader.Count(lines) == bookmark);
                        if (i == 0) { ledger = currentLedger; generation = currentGeneration; initialRead = read; initialCommitted = committed; initialSync = sync; Require(count == bookmark); }
                        else Require(currentLedger == ledger && currentGeneration == generation && count == lines && read == sourceBytes && committed == sourceBytes && sync >= initialSync && read >= initialRead);
                    }
                    Require(reader.Count(MaximumLines) == lines); long previous = -1; var lineStarts = new long[lines];
                    for (int i = 0; i < lines; i++)
                    {
                        if (i % 4096 == 0) check(); long offset = reader.Number(0, sourceBytes - 1);
                        Require(offset > previous && (i != 0 || offset == 0));
                        if (i == bookmark) Require(offset == initialCommitted); previous = offset; lineStarts[i] = offset;
                    }
                    if (bookmark == lines) Require(initialCommitted == sourceBytes);
                    var outputLines = new long[5]; var outputBytes = new long[5]; var outputHashes = new string[5];
                    var outputs = new LocalGuiWriterRequest.Output[5];
                    for (int i = 0; i < 5; i++)
                    {
                        string path = reader.Text(4096); var file = native.Entries[i + 1].File;
                        string identity = reader.Text(25); Require(identity == file.Volume.ToString("x8") + ":" + file.IndexHigh.ToString("x8") + file.IndexLow.ToString("x8"));
                        outputHashes[i] = reader.Text(64); Require(Hash(outputHashes[i]));
                        outputBytes[i] = reader.Number(0, i == 4 ? 65536 : 268959744); Require(outputBytes[i] == file.Length);
                        outputLines[i] = reader.Number(0, MaximumLines); Require(outputLines[i] <= outputBytes[i]);
                        outputs[i] = new LocalGuiWriterRequest.Output(path, outputHashes[i], identity, outputBytes[i], outputLines[i]);
                    }
                    Require(outputHashes[2] == outputHashes[3] && outputBytes[2] == outputBytes[3] && outputLines[2] == outputLines[3]);
                    int origins = 0; int[] semanticOrigins = null;
                    for (int role = 0; role < 3; role++)
                    {
                        int count = reader.Count(MaximumLines - origins); origins += count; Require(count == outputLines[role]); int last = 0;
                        if (role == 0) Require(count == lines - bookmark);
                        if (role == 2) semanticOrigins = new int[count];
                        for (int i = 0; i < count; i++)
                        { if (i % 4096 == 0) check(); int line = reader.Count(lines); Require(line > last && (role != 0 || line == bookmark + i + 1)); last = line; if (role == 2) semanticOrigins[i] = line; }
                    }
                    Require(reader.Number(0, MaximumLines) == lines);
                    for (int i = 0; i < 5; i++) Require(r.ReadInt64() == 0);
                    for (int i = 0; i < 3; i++) Require(r.ReadInt32() == 0);
                    int validationText = reader.Count(65536), measuredText = 0; Require(reader.Count(64) == 2);
                    var writers = new Dictionary<string, long>(StringComparer.Ordinal);
                    for (int i = 0; i < 2; i++)
                    {
                        int start = reader.TextUnits; string id = reader.Text(1024);
                        Require(reader.Text(1024) == run && reader.Text(1024) == scenario); measuredText += reader.TextUnits - start;
                        Require(r.ReadInt64() == 1); long last = reader.Number(1, Int64.MaxValue);
                        Require(r.ReadInt64() == last && r.ReadInt32() == 1 && r.ReadInt64() == last); writers.Add(id, last);
                    }
                    int prefixes = reader.Count(2); var prefixIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < prefixes; i++)
                    {
                        string id = reader.Text(1024); Require(prefixIds.Add(id) && writers.ContainsKey(id));
                        Require(reader.Text(1024) == run && reader.Text(1024) == scenario && r.ReadInt64() == 1);
                        long last = reader.Number(1, writers[id]); Require(r.ReadInt64() == last);
                    }
                    int batchCount = reader.Count(64); var batches = new Dictionary<string, GuiSmokeLedgerValidationBatch>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < batchCount; i++)
                    {
                        int start = reader.TextUnits; var batch = new GuiSmokeLedgerBatchResult { WriterId = reader.Text(1024), PayloadSha256 = reader.Text(64) };
                        measuredText += reader.TextUnits - start; Require(writers.ContainsKey(batch.WriterId) && Hash(batch.PayloadSha256));
                        batch.BatchGeneration = reader.Number(1, Int64.MaxValue); batch.BatchFirstSequence = reader.Number(1, Int64.MaxValue);
                        batch.BatchLastSequence = reader.Number(batch.BatchFirstSequence, Int64.MaxValue); batch.AttemptUtcTicks = reader.Number(1, DateTime.MaxValue.Ticks);
                        batch.MinSequence = r.ReadInt64(); batch.MaxSequence = r.ReadInt64(); batch.LineCount = r.ReadInt64(); batch.TupleValid = r.ReadInt32() == 1;
                        Require(batch.TupleValid && batch.MinSequence == batch.BatchFirstSequence && batch.MaxSequence == batch.BatchLastSequence &&
                            batch.LineCount == batch.BatchLastSequence - batch.BatchFirstSequence + 1);
                        batches.Add(Key(batch.WriterId, batch.BatchGeneration), new GuiSmokeLedgerValidationBatch(batch));
                    }
                    Require(validationText == measuredText);
                    var manifests = new LocalFrontierFileManifest[2]; int payloadIndex = 10;
                    var ranges = new List<LocalFrontierFileManifest.Range>();
                    for (int slot = 0; slot < 2; slot++)
                    {
                        check(); var manifest = LocalFrontierFileManifest.Decode(reader.Blob(LocalFrontierFileManifest.MaximumBytes)); manifests[slot] = manifest;
                        Require(manifest != null && manifest.GuiRequest == native.Gui && manifest.AppRequest == (slot == 0 ? native.First : native.Second) &&
                            manifest.Source.SameFile(native.Entries[0].File) && manifest.Source.Length <= sourceBytes && writers.ContainsKey(manifest.Writer) && writers[manifest.Writer] == manifest.Drain.Last);
                        ranges.Add(manifest.Drain);
                        foreach (var payload in manifest.Payloads)
                        {
                            Require(payloadIndex < native.Entries.Length); var entry = native.Entries[payloadIndex++];
                            Require(entry.Slot == slot && entry.Observation == payload.Observation && entry.File.SameFile(payload.File) && entry.File.Length == payload.File.Length);
                            ranges.Add(payload.Completion); ranges.Add(payload.Marker);
                        }
                    }
                    Require(payloadIndex == native.Entries.Length && manifests[0].Writer != manifests[1].Writer &&
                        (manifests[0].AppId != manifests[1].AppId || manifests[0].AppCreation != manifests[1].AppCreation));
                    ranges.Sort((a,b) => a.Offset.CompareTo(b.Offset));
                    for (int i = 1; i < ranges.Count; i++) Require(ranges[i - 1].Offset + ranges[i - 1].Length <= ranges[i].Offset);
                    long recoveryLines = reader.Number(0, MaximumLines); int recoveryText = reader.Count(65536), measuredRecovery = 0;
                    int markerCount = reader.Count(128); Require(markerCount == 2 * batchCount);
                    var deferred = new Dictionary<string, FrontierRecoveryMarker>(StringComparer.OrdinalIgnoreCase);
                    var recovered = new Dictionary<string, FrontierRecoveryMarker>(StringComparer.OrdinalIgnoreCase);
                    var positions = new long[4]; int previousStream = 0;
                    for (int i = 0; i < markerCount; i++)
                    {
                        check(); var fields = new Dictionary<string, string>(); int start = reader.TextUnits;
                        var names = new[] { "event", "log_writer_id", "acceptance_run_id", "scenario_run_id", "payload_sha256", "failure_code" };
                        foreach (string name in names) fields.Add(name, reader.Text(1024)); string key = reader.Text(1048);
                        measuredRecovery += reader.TextUnits - start;
                        foreach (string name in new[] { "batch_generation", "first_sequence", "last_sequence", "line_count", "attempt_utc_ticks", "attempt_count" })
                            fields.Add(name, reader.Number(1, Int64.MaxValue).ToString(CultureInfo.InvariantCulture));
                        int stream = reader.Count(3); long line = reader.Number(1, recoveryLines);
                        Require(stream >= previousStream && line > positions[stream]); positions[stream] = line; previousStream = stream;
                        var marker = new FrontierRecoveryMarker(fields, stream, line);
                        Require(marker.BatchKey == key && marker.AcceptanceRunId == run && marker.ScenarioRunId == scenario &&
                            marker.WriterId == manifests[stream / 2].Writer && batches.ContainsKey(key) && marker.Matches(batches[key]));
                        if (marker.EventName == "operational_log_write_deferred") deferred.Add(key, marker);
                        else { Require(marker.EventName == "operational_log_write_recovered"); recovered.Add(key, marker); }
                    }
                    Require(measuredRecovery == recoveryText && deferred.Count == batchCount && recovered.Count == batchCount);
                    foreach (var pair in deferred) Require(recovered.ContainsKey(pair.Key) && pair.Value.SameTuple(recovered[pair.Key]));
                    Require(reader.Count(64) == deferred.Count); var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < deferred.Count; i++) { string key = reader.Text(1048); Require(keys.Add(key) && deferred.ContainsKey(key)); }
                    Require(r.BaseStream.Position == r.BaseStream.Length); check();
                    return new LocalFrontierParentFacts(bytes, lines, origins, markerCount, ledger, bookmark, outputs, writers, semanticOrigins, manifests, lineStarts, sourceBytes);
                }
            }
        }
        private static string Key(string writer, long generation)
        { return writer.Length.ToString(CultureInfo.InvariantCulture) + ":" + writer + ":" + generation.ToString(CultureInfo.InvariantCulture); }
    }
}
