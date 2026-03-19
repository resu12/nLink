using System;
using System.Collections.Generic;
using System.IO;

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
            "persistence_warning" => RedactFreeForm(value),
            "last_failure_message" => RedactFreeForm(value),
            "last_error" => RedactFreeForm(value),
            "last_disconnect_reason" => RedactFreeForm(value),
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
