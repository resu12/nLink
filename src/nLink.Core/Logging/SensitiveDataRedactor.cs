using System.Text.RegularExpressions;
using System.Text;

namespace NLink.Core.Logging;

public static partial class SensitiveDataRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "payloadBase64",
        "sharedKey",
        "seedBase64",
        "seed",
        "privateKey",
        "private_key",
        "secret",
        "identifier",
        "key_path",
        "keyPath",
        "inviteToken",
        "invite_token",
        "rootKey",
        "root_key",
        "sessionRootKey",
        "session_root_key",
        "fallbackCode",
        "fallback_code",
        "verificationFallbackCode",
        "verification_fallback_code",
    };

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var protectedSnapshotEvidenceValues = new List<string>();
        var redacted = ProtectSnapshotEvidenceValues(
            text,
            protectedSnapshotEvidenceValues);
        redacted = ProtectCheckpointContinuityEvidenceValues(redacted, protectedSnapshotEvidenceValues);
        redacted = ProtectRepairOwnerEvidenceValues(redacted, protectedSnapshotEvidenceValues);
        redacted = ProtectDataPathEnvelopeEvidenceValues(redacted, protectedSnapshotEvidenceValues);
        redacted = ProtectFileTransferSecurityEvidenceValues(redacted, protectedSnapshotEvidenceValues);
        redacted = ProtectTunaDeliveryTraceValues(redacted, protectedSnapshotEvidenceValues);
        var protectedFallbackBootstrapProofKinds = new List<string>();
        redacted = ProtectFallbackBootstrapProofKinds(
            redacted,
            protectedFallbackBootstrapProofKinds);
        var protectedEventValues = new List<string>();
        redacted = ProtectStructuredEventValues(redacted, protectedEventValues);
        redacted = SecretKeyValueRegex().Replace(redacted, "[redacted]");
        redacted = ChatPlaintextKeyValueRegex().Replace(redacted, "[redacted]");
        var protectedAuthorityIdValues = new List<string>();
        redacted = ProtectStructuredAuthorityIdValues(redacted, protectedAuthorityIdValues);
        var protectedStructuredKeys = new List<string>();
        redacted = ProtectStructuredKeys(redacted, protectedStructuredKeys);
        redacted = LongSecretTokenRegex().Replace(redacted, "[redacted]");
        redacted = RestoreFallbackBootstrapProofKinds(
            redacted,
            protectedFallbackBootstrapProofKinds);
        redacted = RestoreStructuredKeys(redacted, protectedStructuredKeys);
        redacted = RestoreSnapshotEvidenceValues(
            redacted,
            protectedSnapshotEvidenceValues);
        redacted = RestoreStructuredAuthorityIdValues(redacted, protectedAuthorityIdValues);
        redacted = RestoreStructuredEventValues(redacted, protectedEventValues);
        return redacted;
    }

    public static string RedactValueForKey(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return SensitiveKeys.Contains(key) ? "[redacted]" : value!;
    }

    public static string FormatStructuredFields(string separator, params (string Key, string? Value)[] fields)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(fields[i].Key)
                .Append('=')
                .Append(RedactValueForKey(fields[i].Key, fields[i].Value));
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\b(?:payloadBase64|sharedKey|seedBase64|seed|privateKey|private_key|secret|identifier|key_path|keyPath|inviteToken|invite_token|rootKey|root_key|sessionRootKey|session_root_key|fallbackCode|fallback_code|verificationFallbackCode|verification_fallback_code)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s|,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"\b(?:chat|message|text)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPlaintextKeyValueRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{32,}|(?=[A-Za-z0-9+/_-]{40,}={0,2}(?![A-Za-z0-9]))(?=[A-Za-z0-9+/_-]*[0-9+/-])[A-Za-z0-9+/_-]{40,}={0,2})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretTokenRegex();

    [GeneratedRegex(@"\bevent=(?<value>[A-Za-z0-9._/-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EventKeyValueRegex();

    [GeneratedRegex(@"\b(?<key>authority_id|predecessor_authority_id|successor_authority_id|requested_predecessor_authority_id|release_authority_id|retained_predecessor_authority_id|current_authority_id|current_successor_authority_id|receive_recovery_operation_authority_id|recovery_liveness_lease_authority_id|retry_authority_id)=(?<value>fta[0-9]+-[A-Za-z0-9._/:-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorityIdKeyValueRegex();

    [GeneratedRegex(@"\boperation_key=(?<value>[A-Za-z0-9._/:-]+\|[A-Za-z0-9._/:-]+\|(?:fte1-[a-f0-9]{24}|\(none\))\|[0-9]+\|[A-Za-z0-9._/:-]+)(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotEvidenceOperationKeyValueRegex();

    [GeneratedRegex(@"\bsnapshot_catalog_version=(?<value>snapshot-authority-v1(?:-receiver-barrier-v1(?:-checkpoint-repair-continuity-v1(?:-fallback-bootstrap-correlation-v1(?:-fallback-bootstrap-initiation-v1(?:-source-callback-v1(?:-repair-owner-v1(?:-activation-retry-v1)?)?)?)?)?)?)?)(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotEvidenceCatalogVersionRegex();

    [GeneratedRegex(@"^[^\r\n]*(?<![A-Za-z0-9._/-])event=(?:filetransfer_fallback_frontier_repair_(?:authority_granted|contract_settled|dispatched|progress_observed)|filetransfer_v6_frontier_request_(?:sent|deferred)|filetransfer_v6_frontier_stall_grace_recheck_decided|filetransfer_v6_fallback_frontier_repair_progress_state_forced|filetransfer_v6_receiver_state_sent|filetransfer_control_plane_delivery_observed)(?=;\s|$)[^\r\n]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointContinuityEvidenceLineRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])(?:(?<key>continuity_profile)=(?<value>snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1(?:-fallback-bootstrap-correlation-v1(?:-fallback-bootstrap-initiation-v1(?:-source-callback-v1(?:-repair-owner-v1(?:-activation-retry-v1)?)?)?)?)?)|(?<key>continuity_(?:coordinator_id|source_fingerprint|acceptance_id|origin_receipt_id|origin_receipt_fingerprint|receipt_id|receipt_fingerprint|current_scope_fingerprint|repair_authority_id|request_id|correlation_id))=(?<value>fte1-[a-f0-9]{24}))(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointContinuityEvidenceValueRegex();

    [GeneratedRegex(@"^[^\r\n]*(?<![A-Za-z0-9._/-])event=filetransfer_fallback_frontier_repair_(?:authority_granted|contract_settled)(?=;\s|$)[^\r\n]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RepairOwnerEvidenceLineRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])repair_owner_profile=(?<value>snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1(?:-activation-retry-v1)?)(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RepairOwnerEvidenceProfileRegex();

    private static string ProtectRepairOwnerEvidenceValues(string text, List<string> protectedValues)
        => RepairOwnerEvidenceLineRegex().Replace(text, line =>
        {
            if (EventKeyValueRegex().Matches(line.Value).Count != 1) { return line.Value; }
            return RepairOwnerEvidenceProfileRegex().Replace(line.Value, match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"repair_owner_profile={placeholder}";
            });
        });

    [GeneratedRegex(@"^[^\r\n]*(?<![A-Za-z0-9._/-])event=(?:filetransfer_data_path_(?:send|receive)_result|filetransfer_tuna_data_path_(?:peer_visible|mismatch)|filetransfer_data_frame_ignored)(?=;\s|$)[^\r\n]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DataPathEnvelopeEvidenceLineRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])(?:(?<key>data_path_envelope_fingerprint)=(?<value>ftenv1-[a-f0-9]{64})|(?<key>data_path_(?:session|transfer)_id)=(?<value>fte1-[a-f0-9]{24}))(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DataPathEnvelopeEvidenceValueRegex();

    [GeneratedRegex(@"^[^\r\n]*(?<![A-Za-z0-9._/-])event=(?:filetransfer_security_context_(?:installed|retired)|filetransfer_data_path_(?:send|receive)_result|filetransfer_tuna_data_path_(?:peer_visible|mismatch)|filetransfer_data_frame_ignored)(?=;\s|$)[^\r\n]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FileTransferSecurityEvidenceLineRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])(?:(?<key>security_transport_id)=(?<value>ftst1-[a-f0-9]{32})|(?<key>security_installation_id)=(?<value>ftsi1-[a-f0-9]{32})|(?<key>security_establishment_fingerprint)=(?<value>ftest1-[a-f0-9]{64})|(?<key>security_payload_fingerprint)=(?<value>ftpay1-[a-f0-9]{64})|(?<key>security_(?:session|transfer)_id)=(?<value>fte1-[a-f0-9]{24}))(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FileTransferSecurityEvidenceValueRegex();

    private static string ProtectFileTransferSecurityEvidenceValues(string text, List<string> protectedValues)
        => FileTransferSecurityEvidenceLineRegex().Replace(text, line =>
        {
            if (EventKeyValueRegex().Matches(line.Value).Count != 1) { return line.Value; }
            return FileTransferSecurityEvidenceValueRegex().Replace(line.Value, match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"{match.Groups["key"].Value}={placeholder}";
            });
        });

    [GeneratedRegex(@"\bproof_kind=(?<value>file_transfer_(?:bulk_frame|v4_state_frame|v6_state_frame|v6_frontier_request_frame)_(?:received|sent))(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FallbackBootstrapProofKindValueRegex();

    [GeneratedRegex(@"\b(?<key>snapshot_catalog_hash|coordinator_instance_id|session_id|transfer_id|transition_id|snapshot_sha256|snapshot_payload|initial_state_hash|final_state_hash|operation_authority_id|effect_identity|receipt_identity|receipt_fingerprint|checkpoint_request_id|data_session_identity)=(?<value>fte1-[a-f0-9]{24}|[a-f0-9]{64}|[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotEvidenceKeyValueRegex();

    [GeneratedRegex(@"(?<prefix>(?:^|[\s|,;]))(?<key>[A-Za-z_][A-Za-z0-9._/-]{1,127})(?<separator>\s*[:=])", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredKeyRegex();

    private static string ProtectStructuredEventValues(string text, List<string> protectedEventValues)
    {
        return EventKeyValueRegex().Replace(text, match =>
        {
            var placeholder = $"EVP{protectedEventValues.Count}";
            protectedEventValues.Add(match.Groups["value"].Value);
            return $"event={placeholder}";
        });
    }

    private static string RestoreStructuredEventValues(string text, List<string> protectedEventValues)
    {
        if (protectedEventValues.Count == 0)
        {
            return text;
        }

        var restored = text;
        for (var i = 0; i < protectedEventValues.Count; i++)
        {
            restored = restored.Replace($"event=EVP{i}", $"event={protectedEventValues[i]}", StringComparison.Ordinal);
        }

        return restored;
    }

    private static string ProtectStructuredAuthorityIdValues(
        string text,
        List<string> protectedAuthorityIdValues)
    {
        return AuthorityIdKeyValueRegex().Replace(text, match =>
        {
            var placeholder = $"AIVP{protectedAuthorityIdValues.Count}";
            protectedAuthorityIdValues.Add(match.Groups["value"].Value);
            return $"{match.Groups["key"].Value}={placeholder}";
        });
    }

    private static string RestoreStructuredAuthorityIdValues(
        string text,
        List<string> protectedAuthorityIdValues)
    {
        var restored = text;
        for (var i = protectedAuthorityIdValues.Count - 1; i >= 0; i--)
        {
            restored = restored.Replace(
                $"AIVP{i}",
                protectedAuthorityIdValues[i],
                StringComparison.Ordinal);
        }

        return restored;
    }

    private static string ProtectSnapshotEvidenceValues(
        string text,
        List<string> protectedValues)
    {
        if (!text.Contains(
                "event=filetransfer_coordinator_",
                StringComparison.Ordinal))
        {
            return text;
        }

        var protectedText = SnapshotEvidenceCatalogVersionRegex().Replace(text, match =>
        {
            var placeholder = $"SEVP{protectedValues.Count}";
            protectedValues.Add(match.Groups["value"].Value);
            return $"snapshot_catalog_version={placeholder}";
        });
        protectedText = SnapshotEvidenceOperationKeyValueRegex().Replace(
            protectedText,
            match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"operation_key={placeholder}";
            });

        return SnapshotEvidenceKeyValueRegex().Replace(protectedText, match =>
        {
            var placeholder = $"SEVP{protectedValues.Count}";
            protectedValues.Add(match.Groups["value"].Value);
            return $"{match.Groups["key"].Value}={placeholder}";
        });
    }

    private static string RestoreSnapshotEvidenceValues(
        string text,
        List<string> protectedValues)
    {
        var restored = text;
        for (var i = protectedValues.Count - 1; i >= 0; i--)
        {
            restored = restored.Replace(
                $"SEVP{i}",
                protectedValues[i],
                StringComparison.Ordinal);
        }

        return restored;
    }

    [GeneratedRegex(@"^[^\r\n]*(?<![A-Za-z0-9._/-])event=tuna_delivery_trace(?=;\s|$)[^\r\n]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TunaDeliveryTraceLineRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._/-])(?:(?<key>trace_wire)=(?<value>ftwire1-[a-f0-9]{64})|(?<key>data_path_envelope_fingerprint)=(?<value>ftenv1-[a-f0-9]{64})|(?<key>data_path_session_id|data_path_transfer_id)=(?<value>fte1-[a-f0-9]{24}))(?=;\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TunaDeliveryTraceValueRegex();

    private static string ProtectTunaDeliveryTraceValues(string text, List<string> protectedValues)
        => TunaDeliveryTraceLineRegex().Replace(text, line =>
        {
            if (EventKeyValueRegex().Matches(line.Value).Count != 1) return line.Value;
            return TunaDeliveryTraceValueRegex().Replace(line.Value, match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"{match.Groups["key"].Value}={placeholder}";
            });
        });

    private static string ProtectCheckpointContinuityEvidenceValues(string text, List<string> protectedValues)
        => CheckpointContinuityEvidenceLineRegex().Replace(text, line =>
        {
            if (EventKeyValueRegex().Matches(line.Value).Count != 1)
            {
                return line.Value;
            }
            return CheckpointContinuityEvidenceValueRegex().Replace(line.Value, match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"{match.Groups["key"].Value}={placeholder}";
            });
        });

    private static string ProtectDataPathEnvelopeEvidenceValues(string text, List<string> protectedValues)
        => DataPathEnvelopeEvidenceLineRegex().Replace(text, line =>
        {
            if (EventKeyValueRegex().Matches(line.Value).Count != 1)
            {
                return line.Value;
            }

            return DataPathEnvelopeEvidenceValueRegex().Replace(line.Value, match =>
            {
                var placeholder = $"SEVP{protectedValues.Count}";
                protectedValues.Add(match.Groups["value"].Value);
                return $"{match.Groups["key"].Value}={placeholder}";
            });
        });

    private static string ProtectFallbackBootstrapProofKinds(
        string text,
        List<string> protectedValues)
    {
        if (!text.Contains(
                "event=filetransfer_fallback_v6_proof_bootstrap_",
                StringComparison.Ordinal))
        {
            return text;
        }

        return FallbackBootstrapProofKindValueRegex().Replace(text, match =>
        {
            var placeholder = $"FBPKP{protectedValues.Count}";
            protectedValues.Add(match.Groups["value"].Value);
            return $"proof_kind={placeholder}";
        });
    }

    private static string RestoreFallbackBootstrapProofKinds(
        string text,
        List<string> protectedValues)
    {
        var restored = text;
        for (var i = protectedValues.Count - 1; i >= 0; i--)
        {
            restored = restored.Replace(
                $"FBPKP{i}",
                protectedValues[i],
                StringComparison.Ordinal);
        }

        return restored;
    }

    private static string ProtectStructuredKeys(string text, List<string> protectedStructuredKeys)
    {
        return StructuredKeyRegex().Replace(text, match =>
        {
            var placeholder = $"SKP{protectedStructuredKeys.Count}";
            protectedStructuredKeys.Add(match.Groups["key"].Value);
            return $"{match.Groups["prefix"].Value}{placeholder}{match.Groups["separator"].Value}";
        });
    }

    private static string RestoreStructuredKeys(string text, List<string> protectedStructuredKeys)
    {
        if (protectedStructuredKeys.Count == 0)
        {
            return text;
        }

        var restored = text;
        for (var i = protectedStructuredKeys.Count - 1; i >= 0; i--)
        {
            restored = restored.Replace($"SKP{i}", protectedStructuredKeys[i], StringComparison.Ordinal);
        }

        return restored;
    }
}
