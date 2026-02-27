using System.Text.Json;
using NLink.App.Services;
using NLink.Core.Metrics;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

public sealed class ContractFreezeTests
{
    private const string UpdateEnvVar = "NLINK_UPDATE_CONTRACTS";

    [Trait("Category", "ContractFreeze")]
    [Fact]
    public void TransportState_EnumNames_Freeze()
    {
        AssertApprovedLines(
            Path.Combine("tests", "nLink.SmokeTests", "GoldenFiles", "Contracts", "transport-state.approved.txt"),
            Enum.GetNames<TransportState>());
    }

    [Trait("Category", "ContractFreeze")]
    [Fact]
    public void TransportFailureCategory_EnumNames_Freeze()
    {
        AssertApprovedLines(
            Path.Combine("tests", "nLink.SmokeTests", "GoldenFiles", "Contracts", "transport-failure-category.approved.txt"),
            Enum.GetNames<TransportFailureCategory>());
    }

    [Trait("Category", "ContractFreeze")]
    [Fact]
    public void MetricsNames_Freeze()
    {
        AssertApprovedLines(
            Path.Combine("tests", "nLink.SmokeTests", "GoldenFiles", "Contracts", "metric-names.approved.txt"),
            MetricsNames.All.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Trait("Category", "ContractFreeze")]
    [Fact]
    public void DiagnosticsSummarySnapshot_Schema_Freeze()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var snapshot = runtime.GetDiagnosticsSnapshot();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

        AssertApprovedText(
            Path.Combine("tests", "nLink.SmokeTests", "GoldenFiles", "Contracts", "diagnostics-summary-snapshot.golden.json"),
            json);
    }

    private static void AssertApprovedLines(string relativePath, IEnumerable<string> lines)
    {
        var expectedPath = FindFileUpwards(relativePath);
        var content = string.Join('\n', lines) + "\n";
        AssertApprovedText(expectedPath, content);
    }

    private static void AssertApprovedText(string pathOrRelative, string actual)
    {
        var path = Path.IsPathRooted(pathOrRelative) ? pathOrRelative : FindFileUpwards(pathOrRelative);
        var normalizedActual = NormalizeText(actual);
        var update = IsUpdateMode();

        if (update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalizedActual);
            return;
        }

        Assert.True(File.Exists(path), $"Approved file not found: {path}. Run tools/UpdateContracts.ps1 to regenerate.");
        var expected = NormalizeText(File.ReadAllText(path));
        Assert.Equal(expected, normalizedActual);
    }

    private static bool IsUpdateMode()
    {
        var value = Environment.GetEnvironmentVariable(UpdateEnvVar);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string text)
        => text.Replace("\r\n", "\n").TrimEnd() + "\n";

    private static string FindFileUpwards(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        string? firstFound = null;
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                var normalized = candidate.Replace('/', '\\');
                if (!normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) &&
                    !normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                firstFound ??= candidate;
            }
        }

        return firstFound ?? Path.GetFullPath(relativePath);
    }
}
