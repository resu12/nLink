using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace NLink.SmokeTests;

internal static class GuiSmokeHarness
{
    public static Task RunDefaultScenariosAsync(ITestOutputHelper output)
        => RunScenariosAsync(output, GetSelectedScenariosFromEnvironment().ToArray());

    public static async Task RunScenariosAsync(ITestOutputHelper output, params string[] scenarios)
    {
        await RunScenariosCoreAsync(output, transportOverride: null, scenarios);
    }

    public static async Task RunScenariosWithTransportAsync(ITestOutputHelper output, string transport, params string[] scenarios)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        await RunScenariosCoreAsync(output, transport.Trim(), scenarios);
    }

    private static async Task RunScenariosCoreAsync(ITestOutputHelper output, string? transportOverride, params string[] scenarios)
    {
        var selectedScenarios = NormalizeScenarios(scenarios);
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        Assert.True(File.Exists(scriptPath), $"GUI smoke script not found: {scriptPath}");

        var exePath = ResolveGuiSmokeExe(repoRoot);
        Assert.True(File.Exists(exePath), $"nLink executable not found for GUI smoke: {exePath}");
        var wrapperTimeout = TimeSpan.FromSeconds(CalculateWrapperTimeoutSeconds(selectedScenarios, transportOverride));

        var previousScenarioEnv = Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS");
        var previousTransportEnv = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        Environment.SetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS", string.Join(",", selectedScenarios));
        if (transportOverride is not null)
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", transportOverride);
        }

        try
        {
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
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS", previousScenarioEnv);
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransportEnv);
        }
    }

    private static IReadOnlyList<string> GetSelectedScenariosFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS");
        return NormalizeScenarios(raw?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray());
    }

    private static IReadOnlyList<string> NormalizeScenarios(string[]? scenarios)
    {
        var parsed = (scenarios ?? Array.Empty<string>())
            .Select(x => x?.Trim().ToUpperInvariant())
            .Where(x => x is "A" or "B" or "C" or "D" or "E" or "F" or "G" or "H" or "I" or "J" or "K" or "L" or "M" or "HEADER_CHAT_COHERENCE" or "END_SESSION_DISABLES_CHAT" or "SCREENSHARE_BUTTON_VISIBILITY" or "SCREENSHARE_VIEWER_TOGGLE" or "SCREENSHARE_CHAT_COEXISTENCE" or "SCREENSHARE_STOP_PENDING_APPROVAL" or "STATUS_TEXT_GUARDRAILS")
            .Cast<string>()
            .Distinct()
            .ToArray();

        return parsed.Length == 0 ? new[] { "A", "B", "C", "E", "F", "G", "H", "I", "J", "K", "L", "M" } : parsed;
    }

    private static int CalculateWrapperTimeoutSeconds(IReadOnlyList<string> scenarios, string? transportOverride)
    {
        var total = 15;
        foreach (var scenario in scenarios)
        {
            total += scenario switch
            {
                "B" => 60,
                "F" => 60,
                "G" => 60,
                "H" => 45,
                "I" => 60,
                "J" => 45,
                "K" => 60,
                "L" => 90,
                "M" => 90,
                "HEADER_CHAT_COHERENCE" => 90,
                "END_SESSION_DISABLES_CHAT" => 90,
                "SCREENSHARE_BUTTON_VISIBILITY" => 90,
                "SCREENSHARE_VIEWER_TOGGLE" => 90,
                "SCREENSHARE_CHAT_COEXISTENCE" => 90,
                "SCREENSHARE_STOP_PENDING_APPROVAL" => 90,
                "STATUS_TEXT_GUARDRAILS" => 90,
                "E" => 120,
                _ => 90
            };
        }

        if (string.Equals(transportOverride, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            total += 90;
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

        return match.Success ? match.Groups["path"].Value.Trim() : null;
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
            try
            {
                process.Kill();
            }
            catch
            {
            }
        }
    }
}
