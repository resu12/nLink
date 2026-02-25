using System;

namespace NLink.App.Services;

internal static class TransportTelemetryContext
{
    private static readonly string runId = Guid.NewGuid().ToString("N")[..8];

    public static string RunId => runId;

    public static string GetScenarioLabel()
    {
        var explicitScenario = NormalizeSingleScenario(Environment.GetEnvironmentVariable("NLINK_SCENARIO"));
        if (!string.IsNullOrEmpty(explicitScenario))
        {
            return explicitScenario;
        }

        // GUI smoke suite passes A/B/C/D here; only accept exactly one scenario to keep cardinality bounded.
        return NormalizeSingleScenario(Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS"));
    }

    private static string NormalizeSingleScenario(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Trim().ToUpperInvariant();
        return normalized switch
        {
            "A" or "B" or "C" or "D" => normalized,
            _ => string.Empty,
        };
    }
}
