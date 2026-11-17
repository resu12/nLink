using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NLink.App.Services;

internal static class DiagnosticsExportBuilder
{
    internal const string BestEffortPrivacyNotice = "Diagnostics may contain sensitive operational metadata. Redaction is best-effort only. Review before sharing.";

    public static void AddBestEffortHeader(ICollection<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lines.Add("Privacy notice");
        lines.Add("--------------");
        lines.Add(BestEffortPrivacyNotice);
        lines.Add(string.Empty);
    }

    public static string RedactStructuredValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return key switch
        {
            "file_transfer_last_saved_path" => RedactPath(value),
            "screenshare_artifact_dir" => RedactPath(value),
            "screenshare_verdict_path" => RedactPath(value),
            "wallet_path" => RedactPath(value),
            "tuna_wallet_path" => RedactPath(value),
            "wallet_address" => "[REDACTED]",
            "tuna_wallet_address" => "[REDACTED]",
            "persistence_warning" => RedactFreeForm(value),
            "last_failure_message" => RedactFreeForm(value),
            "last_error" => RedactFreeForm(value),
            "last_disconnect_reason" => RedactFreeForm(value),
            "tuna_wallet_last_failure" => RedactFreeForm(value),
            _ => value.Trim(),
        };
    }

    public static string RedactFreeForm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return DiagnosticsRedactor.Redact(value.Trim());
    }

    public static string RedactStructuredEvidenceText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(RedactStructuredEvidenceLine);
        return DiagnosticsRedactor.Redact(string.Join(Environment.NewLine, lines));
    }

    private static string RedactStructuredEvidenceLine(string line)
    {
        var separatorIndex = line.IndexOfAny(new[] { ':', '=' });
        if (separatorIndex < 0)
        {
            return line;
        }

        var key = line[..separatorIndex].Trim();
        if (key is not ("screenshare_artifact_dir" or "screenshare_verdict_path" or "artifact_dir"))
        {
            return line;
        }

        var separator = line[separatorIndex];
        var value = line[(separatorIndex + 1)..].Trim();
        var redactedKey = string.Equals(key, "artifact_dir", StringComparison.Ordinal)
            ? "screenshare_artifact_dir"
            : key;
        return $"{line[..separatorIndex]}{separator} {RedactStructuredValue(redactedKey, value)}";
    }

    private static string RedactPath(string path)
    {
        var trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return "(none)";
        }

        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName)
            ? "[REDACTED_PATH]"
            : $"[REDACTED_PATH]/{fileName}";
    }
}
