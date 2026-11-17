using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class PrimaryEvaluationCustody
        {
            private LocalNativeInputCapture nativeOwner;
            private LocalNativeInputCapture.NativeInputs nativeInputs;
            private int nativeAttempted, publicationAttempted;
            private WriterReader publicationWriter;
            private PreparedPublication publication;

            // Primary-only: bind the genuine token before recording either view.
            public bool BindNativeInputs(LocalNativeInputCapture capture, LocalNativeInputCapture.NativeInputs inputs)
            {
                if (Interlocked.Exchange(ref nativeAttempted, 1) != 0 || state != 0 ||
                    !owner.OwnsCompletedSource(source) || capture == null || inputs == null ||
                    !capture.Owns(inputs) || inputs.RequestId != owner.RequestId) return Omit();
                foreach (string role in new[] { "fullCapture", "fullSnapshot", "compactCapture", "compactSnapshot" })
                {
                    var input = inputs.Get(role);
                    if (input == null || input.RecordCount != (role == "compactSnapshot" ? compact.Length : full.Length)) return Omit();
                }
                nativeOwner = capture; nativeInputs = inputs; return true;
            }

            private bool NativeCurrent() { return nativeOwner == null || nativeOwner.Owns(nativeInputs); }

            // Only trusted primary preparation supplies these bounded canonical
            // components. No candidate payload, caller digest or formatter is an
            // input. This does not independently authenticate arbitrary callers
            // who already possess the primary's private capability.
            public PreparedPublication TryPreparePublication(CompletedEvaluations pair, object inputContext,
                string[] fullLines, string[] compactLines, object fullResult, object compactResult,
                LocalNativeInputCapture capture, LocalNativeInputCapture.NativeInputs inputs, WriterReader writer,
                string fullRow, string compactRow, string witness, string provenance, string catalog, int remainingMilliseconds)
            {
                if (Interlocked.Exchange(ref publicationAttempted, 1) != 0) return null;
                try
                {
                    if (!Owns(pair, inputContext, fullLines, compactLines, fullResult, compactResult) ||
                        nativeOwner == null || !Object.ReferenceEquals(nativeOwner, capture) || !Object.ReferenceEquals(nativeInputs, inputs) ||
                        !owner.OwnsVerifiedWriter(writer) || remainingMilliseconds < 1 || remainingMilliseconds > 2000) return null;
                    var codec = new PublicationCodec(remainingMilliseconds, () =>
                        state == 3 && owner.OwnsCompletedSource(source) && NativeCurrent() && owner.OwnsVerifiedWriter(writer));
                    codec.Admit(fullRow, compactRow, witness, provenance, catalog);
                    if (!String.Equals(fullRow, compactRow, StringComparison.Ordinal)) return null;
                    string sourceDescriptor = PublicationSource(codec, owner.SourcePath, owner.SourceSha256,
                        owner.SourceIdentity, owner.SourceBytes, owner.SourceLines);
                    string writerDescriptor = PublicationWriter(codec, owner.WriterContext);
                    var roles = new List<object>(8);
                    foreach (string role in new[] { "fullCapture", "fullSnapshot", "compactCapture", "compactSnapshot" })
                    {
                        var input = nativeInputs.Get(role);
                        roles.Add(role); roles.Add(new PublicationCodec.Raw(codec.Object("codeUnitCount", input.CodeUnitCount,
                            "hashDomain", LocalNativeInputCapture.Domain, "recordCount", input.RecordCount, "sha256", input.Sha256)));
                    }
                    string request = PublicationRequest(codec, owner.RequestId, owner.Owner, owner.AcceptanceRunId,
                        owner.ScenarioRunId, sourceDescriptor, writerDescriptor, owner.PublicationNamespace);
                    string descriptors = codec.Object("catalog", new PublicationCodec.Raw(catalog),
                        "native", new PublicationCodec.Raw(codec.Object(roles.ToArray())), "source", new PublicationCodec.Raw(sourceDescriptor),
                        "writer", new PublicationCodec.Raw(writerDescriptor));
                    string selection = codec.Object("provenance", new PublicationCodec.Raw(provenance),
                        "row", new PublicationCodec.Raw(fullRow), "witness", new PublicationCodec.Raw(witness));
                    string evaluations = codec.Object("compactInvocationId", pair.CompactInvocationId.ToString("D"),
                        "fullInvocationId", pair.FullInvocationId.ToString("D"));
                    string payload = codec.Object("evaluations", new PublicationCodec.Raw(evaluations),
                        "format", "nlink-local-publication-forwarding-v2", "inputs", new PublicationCodec.Raw(descriptors),
                        "peerLineageStatus", "not_evaluated", "request", new PublicationCodec.Raw(request),
                        "scope", "local_publication_forwarding", "selection", new PublicationCodec.Raw(selection));
                    byte[] bytes = codec.Bytes(payload);
                    var completion = new PublicationCompletion(owner.RequestId, owner.ProcessId, owner.ProcessCreationFileTime,
                        pair.FullInvocationId, pair.CompactInvocationId, codec.Hash(request), codec.Hash(descriptors),
                        codec.Hash(selection), bytes.Length, codec.Hash(bytes), codec.Hash(catalog));
                    codec.Check();
                    publication = new PreparedPublication(completion, bytes, codec.CharacterVisits, codec.ByteVisits);
                    publicationWriter = writer;
                    if (!OwnsPublication(publication)) { ClearPublication(); return null; }
                    // The primary issuer alone writes the separately parent-owned
                    // control record. No formatter, supplied bytes or digest API.
                    if (!owner.Publish(publication, () => { codec.Check(); return OwnsPublication(publication); }))
                    { CancelPublication(); ClearPublication(); return null; }
                    codec.Check();
                    return publication;
                }
                catch { CancelPublication(); ClearPublication(); return null; }
            }

            public bool OwnsPublication(PreparedPublication candidate)
            {
                return state == 3 && nativeOwner != null && candidate != null && Object.ReferenceEquals(publication, candidate) &&
                    candidate.HasPayload && owner.OwnsCompletedSource(source) && NativeCurrent() && owner.OwnsVerifiedWriter(publicationWriter);
            }

            private void ClearPublication()
            {
                if (publication != null) publication.Invalidate();
                publication = null; publicationWriter = null;
            }

            // Revocation can only omit diagnostics; it cannot nominate a result.
            public void CancelPublication() { owner.CancelPublicationChannel(); }
        }

        private static string PublicationSource(PublicationCodec codec, string path, string hash, string identity, long bytes, long lines)
        { return codec.Object("byteCount", bytes, "fileIdentity", identity, "hashDomain", "sha256-file-bytes-v1",
            "lineCount", lines, "path", path, "sha256", hash); }

        private static string PublicationWriter(PublicationCodec codec, LocalGuiWriterRequest.Completion writer)
        {
            var output = writer.Outputs[1];
            return codec.Object("byteCount", output.ByteCount, "fileIdentity", output.OriginalFileIdentity,
                "guiRequestId", writer.RequestId.ToString("D"), "hashDomain", "sha256-file-bytes-v1",
                "lineCount", output.LineCount, "path", output.Path, "sha256", output.Sha256);
        }

        private static string PublicationRequest(PublicationCodec codec, Guid request, string owner, string run, string scenario,
            string source, string writer, string outputNamespace)
        {
            var fields = new List<object> { "acceptanceRunId", run,
                "format", outputNamespace == null ? "nlink-local-publication-prepared-request-v1" : "nlink-local-publication-producer-request-v1",
                "maximumWitnesses", 64, "owner", owner, "requestId", request.ToString("D"), "scenarioRunId", scenario,
                "scope", "local_publication_forwarding", "selectionPolicy", "primary_reported_failure_witnesses_v1",
                "source", new PublicationCodec.Raw(source) };
            if (outputNamespace != null)
            {
                fields.Add("outputNamespace"); fields.Add(outputNamespace);
                fields.Add("writer"); fields.Add(new PublicationCodec.Raw(writer));
            }
            return codec.Object(fields.ToArray());
        }

        public sealed class PreparedPublication
        {
            private byte[] payload;
            public PublicationCompletion Completion { get; private set; }
            public long CharacterVisits { get; private set; }
            public long ByteVisits { get; private set; }
            internal bool HasPayload { get { return payload != null; } }
            internal PreparedPublication(PublicationCompletion completion, byte[] bytes, long characters, long visits)
            { Completion = completion; payload = bytes; CharacterVisits = characters; ByteVisits = visits; }
            public byte[] CopyPayload() { return payload == null ? null : (byte[])payload.Clone(); }
            internal void Invalidate() { payload = null; }
        }

        public sealed class PublicationCompletion
        {
            public Guid RequestId { get; private set; }
            public int WorkerProcessId { get; private set; }
            public long WorkerCreationFileTime { get; private set; }
            public Guid FullInvocationId { get; private set; }
            public Guid CompactInvocationId { get; private set; }
            public string RequestSha256 { get; private set; }
            public string InputDescriptorsSha256 { get; private set; }
            public string SelectedRowsSha256 { get; private set; }
            public int PayloadBytes { get; private set; }
            public string PayloadSha256 { get; private set; }
            public string CatalogSha256 { get; private set; }
            internal PublicationCompletion(Guid request, int process, long creation, Guid full, Guid compact,
                string requestHash, string inputsHash, string selectionHash, int length, string payloadHash, string catalogHash)
            {
                RequestId = request; WorkerProcessId = process; WorkerCreationFileTime = creation;
                FullInvocationId = full; CompactInvocationId = compact; RequestSha256 = requestHash;
                InputDescriptorsSha256 = inputsHash; SelectedRowsSha256 = selectionHash;
                PayloadBytes = length; PayloadSha256 = payloadHash;
                CatalogSha256 = catalogHash;
            }
        }

        // Fixed-schema metadata only. Original files, arrays and views never
        // enter this codec. Canonical components come from the trusted primary.
        private sealed class PublicationCodec
        {
            internal const int MaximumBytes = 262144;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly int milliseconds;
            private readonly Func<bool> current;
            internal long CharacterVisits, ByteVisits;
            internal PublicationCodec(int milliseconds, Func<bool> current) { this.milliseconds = milliseconds; this.current = current; }
            internal sealed class Raw { internal readonly string Value; internal Raw(string value) { Value = value; } }
            internal void Check()
            {
                if (clock.ElapsedMilliseconds > milliseconds || !current() || CharacterVisits > 2097152 || ByteVisits > 2097152)
                    throw new InvalidOperationException("publication_completion_budget_or_owner");
            }
            internal void Admit(params string[] components)
            {
                long total = 0;
                foreach (string value in components)
                {
                    if (value == null || value.Length < 2 || value.Length > MaximumBytes ||
                        value[0] != '{' || value[value.Length - 1] != '}' || (total += value.Length) > MaximumBytes)
                        throw new InvalidOperationException("publication_completion_component_limit");
                    for (int i = 0; i < value.Length; i++)
                    {
                        if ((i & 255) == 0) Check();
                        CharacterVisits++;
                        if (value[i] < 32 || value[i] > 126) throw new InvalidOperationException("publication_completion_noncanonical_component");
                    }
                }
            }
            internal string Object(params object[] pairs)
            {
                Check();
                if (pairs.Length > 32 || pairs.Length % 2 != 0) throw new InvalidOperationException("publication_completion_schema_limit");
                var fields = new SortedDictionary<string, object>(StringComparer.Ordinal);
                for (int i = 0; i < pairs.Length; i += 2) fields.Add((string)pairs[i], pairs[i + 1]);
                var result = new StringBuilder(); Append(result, "{"); string separator = "";
                foreach (var field in fields)
                {
                    Check();
                    Append(result, separator); Quote(result, field.Key); Append(result, ":");
                    var raw = field.Value as Raw;
                    if (raw != null) Append(result, raw.Value);
                    else if (field.Value is string) Quote(result, (string)field.Value);
                    else if (field.Value is int || field.Value is long) Append(result, Convert.ToString(field.Value, CultureInfo.InvariantCulture));
                    else throw new InvalidOperationException("publication_completion_scalar_type");
                    separator = ",";
                }
                Append(result, "}"); Check(); return result.ToString();
            }
            private void Append(StringBuilder target, string value)
            {
                if (value.Length > MaximumBytes - target.Length || CharacterVisits + value.Length > 2097152)
                    throw new InvalidOperationException("publication_completion_size_limit");
                CharacterVisits += value.Length; target.Append(value);
            }
            private void Quote(StringBuilder target, string value)
            {
                if (value.Length > MaximumBytes) throw new InvalidOperationException("publication_completion_string_limit");
                // Preserve the established ASCII canonical JSON representation.
                new UTF8Encoding(false, true).GetByteCount(value);
                Append(target, "\"");
                for (int i = 0; i < value.Length; i++)
                {
                    if ((i & 255) == 0) Check();
                    char character = value[i];
                    if (character == '"') Append(target, "\\\"");
                    else if (character == '\\') Append(target, "\\\\");
                    else if (character < 32 || character > 126) Append(target, "\\u" + ((int)character).ToString("x4"));
                    else Append(target, character.ToString());
                }
                Append(target, "\"");
            }
            internal byte[] Bytes(string text)
            { Check(); ByteVisits += text.Length; byte[] bytes = Encoding.ASCII.GetBytes(text); Check(); return bytes; }
            internal string Hash(string text) { return Hash(Bytes(text)); }
            internal string Hash(byte[] bytes)
            {
                Check(); ByteVisits += bytes.Length;
                using (var hash = SHA256.Create())
                { string result = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); Check(); return result; }
            }
        }
    }
}
