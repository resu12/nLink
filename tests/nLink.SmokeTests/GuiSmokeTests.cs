using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace NLink.SmokeTests;

public sealed class GuiSmokeTests
{
    private readonly ITestOutputHelper output;

    public GuiSmokeTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public async Task Windows_GuiSmoke_Scenarios_Pass()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        Assert.True(File.Exists(scriptPath), $"GUI smoke script not found: {scriptPath}");

        var exePath = ResolveGuiSmokeExe(repoRoot);
        Assert.True(File.Exists(exePath), $"nLink executable not found for GUI smoke: {exePath}");
        var selectedScenarios = GetSelectedScenarios();
        var wrapperTimeout = TimeSpan.FromSeconds(CalculateWrapperTimeoutSeconds(selectedScenarios));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -ExePath \"{exePath}\"",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(wrapperTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw new TimeoutException(
                $"GUI smoke timed out after {wrapperTimeout.TotalSeconds:N0} seconds " +
                $"for scenarios: {string.Join(",", selectedScenarios)}.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            output.WriteLine("GUI smoke STDOUT:");
            output.WriteLine(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            output.WriteLine("GUI smoke STDERR:");
            output.WriteLine(stderr);
        }

        if (process.ExitCode != 0)
        {
            var artifactPath = TryExtractGuiSmokeArtifactsPath(stdout);
            if (!string.IsNullOrWhiteSpace(artifactPath))
            {
                output.WriteLine($"GUI smoke artifacts: {artifactPath}");
            }

            throw new Xunit.Sdk.XunitException(
                $"GUI smoke failed with exit code {process.ExitCode}.{Environment.NewLine}" +
                $"{(string.IsNullOrWhiteSpace(artifactPath) ? string.Empty : $"GUI smoke artifacts: {artifactPath}{Environment.NewLine}")}" +
                $"STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{stderr}");
        }

        Assert.Contains("PASS", stdout, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetSelectedScenarios()
    {
        var raw = Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new[] { "A" };
        }

        var parsed = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Where(x => x is "A" or "B" or "C" or "D")
            .Distinct()
            .ToArray();

        return parsed.Length == 0 ? new[] { "A" } : parsed;
    }

    private static int CalculateWrapperTimeoutSeconds(IReadOnlyList<string> scenarios)
    {
        // Mirror script per-scenario caps with some buffer for process startup/teardown.
        var total = 15; // startup / capture buffer
        foreach (var scenario in scenarios)
        {
            total += scenario switch
            {
                "B" => 60,
                _ => 90
            };
        }

        return Math.Max(total, 90);
    }

    private static string? TryExtractGuiSmokeArtifactsPath(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var match = Regex.Match(
            stdout,
            @"Collecting failure artifacts in (?<path>.+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        if (!match.Success)
        {
            return null;
        }

        return match.Groups["path"].Value.Trim();
    }

    private static string ResolveGuiSmokeExe(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "nLink.App", "bin", "Release", "net8.0", "nLink.exe"),
            Path.Combine(repoRoot, "src", "nLink.App", "bin", "Release", "net8.0", "win-x64", "nLink.exe"),
            Path.Combine(repoRoot, "artifacts", "portable", "nLink", "win-x64", "nLink.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "nLink.sln")) &&
                File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test output directory.");
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            try { process.Kill(); } catch { }
        }
    }
}
