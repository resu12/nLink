using System.Globalization;
using System.Text.RegularExpressions;

namespace NLink.Tests;

// Scheduling evidence only. This never creates or applies runtime authority.
internal static class AttemptInitialFallbackHandoff
{
    internal static bool Rechecked(string text, string capturedWake)
    {
        var expected = Fields(capturedWake);
        string[] scope = ["frontier_scope_session_id", "frontier_scope_transfer_id", "frontier_scope_data_session_identity",
            "route", "protocol_version", "live_route_epoch", "runtime_generation", "terminal_generation", "leg_generation",
            "transport_epoch", "bridge_recovery_generation", "bridge_decision_generation", "checkpoint_request_id",
            "checkpoint_generation", "route_publication_generation", "route_commit_generation", "data_session_generation",
            "frontier_chunk_index", "highest_received_chunk_index", "wake_generation", "stall_started_utc", "wake_deadline_utc"];
        if (expected is null || scope.Any(key => !expected.ContainsKey(key)) ||
            expected["route"] != "post_tuna_fallback_v6" || expected["protocol_version"] != "6") return false;
        foreach (var line in text.Split('\n'))
        {
            var row = Fields(line);
            if (row is null || Value(row, "event") != "filetransfer_v6_frontier_stall_grace_recheck_decided" ||
                scope.Any(key => Value(row, key) != expected[key]) || Value(row, "authority_effect") != "none") continue;
            if (Value(row, "decision_status") == "dispatched" &&
                Value(row, "decision_reason") == "exact_receiver_request_dispatched") return true;
            if (Value(row, "decision_status") != "stale" ||
                Value(row, "decision_reason") != "committed_write_source_changed" ||
                Value(row, "write_progress_binding_version") != "1" ||
                Value(row, "write_progress_binding") != "exact_current_fallback_source") continue;
            if (long.TryParse(expected["frontier_chunk_index"], out var frontier) &&
                long.TryParse(Value(row, "write_progress_frontier"), out var next) && next > frontier &&
                long.TryParse(Value(row, "write_progress_highest"), out var highest) && highest == next - 1 &&
                DateTimeOffset.TryParse(expected["stall_started_utc"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) &&
                DateTimeOffset.TryParse(expected["wake_deadline_utc"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) &&
                DateTimeOffset.TryParse(Value(row, "write_progress_utc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var progress) &&
                DateTimeOffset.TryParse(Value(row, "write_progress_recheck_utc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var recheck) &&
                progress > started && recheck >= progress && recheck >= due) return true;
        }
        return false;
    }

    private static string? Value(Dictionary<string, string> fields, string key) => fields.GetValueOrDefault(key);
    private static Dictionary<string, string>? Fields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"(?:^|[ ;])([a-z_]+)=([^;\r\n]*)", RegexOptions.CultureInvariant))
            if (!fields.TryAdd(match.Groups[1].Value, match.Groups[2].Value.Trim())) return null;
        return fields;
    }
}
