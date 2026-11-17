using System.Text.RegularExpressions;

namespace NLink.Core.Logging;

public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(@"^[^\r\n]*(?<![A-Za-z0-9._/-])event=filetransfer_data_path_send_result(?=; |$)[^\r\n]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FileTransferContentEvidenceLineRegex();

    [GeneratedRegex(@"; content_evidence_profile=sender_chunk_hmac_v1; content_scope_id=(?<scope>ftcs1-[a-f0-9]{32}); content_evidence_state=(?<state>captured|retired); content_segment_count=(?<count>[0-3])(?<segments>(?:; content_segment_[0-2]_index=[0-9]{1,7}; content_segment_[0-2]_bytes=[0-9]{1,5}; content_segment_[0-2]_commitment=ftcc1-[a-f0-9]{64})*)(?=\r?$)", RegexOptions.CultureInvariant)]
    private static partial Regex FileTransferContentEvidenceSuffixRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])(?<key>content_[A-Za-z0-9_]+)=", RegexOptions.CultureInvariant)]
    private static partial Regex FileTransferContentEvidenceKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])(?<key>content_scope_id|content_segment_[0-2]_commitment)=(?<value>ftcs1-[a-f0-9]{32}|ftcc1-[a-f0-9]{64})(?=; |$)", RegexOptions.CultureInvariant)]
    private static partial Regex FileTransferContentEvidenceValueRegex();

    private static string ProtectFileTransferContentEvidenceValues(string text, List<string> protectedValues)
        => FileTransferContentEvidenceLineRegex().Replace(text, line =>
        {
            if (EventKeyValueRegex().Matches(line.Value).Count != 1) { return line.Value; }
            var suffix = FileTransferContentEvidenceSuffixRegex().Match(line.Value);
            if (!suffix.Success || suffix.Length > 768) { return line.Value; }
            var count = suffix.Groups["count"].Value[0] - '0';
            if ((suffix.Groups["state"].Value == "retired") != (count == 0)) { return line.Value; }
            var keys = FileTransferContentEvidenceKeyRegex().Matches(line.Value);
            if (keys.Count != 4 + 3 * count ||
                keys.Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal).Count() != keys.Count)
            {
                return line.Value;
            }
            var segmentKeys = FileTransferContentEvidenceKeyRegex().Matches(suffix.Groups["segments"].Value)
                .Select(match => match.Groups["key"].Value).ToArray();
            for (var slot = 0; slot < count; slot++)
            {
                if (segmentKeys.Length != 3 * count ||
                    segmentKeys[3 * slot] != $"content_segment_{slot}_index" ||
                    segmentKeys[3 * slot + 1] != $"content_segment_{slot}_bytes" ||
                    segmentKeys[3 * slot + 2] != $"content_segment_{slot}_commitment") { return line.Value; }
            }
            return FileTransferContentEvidenceValueRegex().Replace(line.Value, match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"{match.Groups["key"].Value}={placeholder}";
            });
        });
}
