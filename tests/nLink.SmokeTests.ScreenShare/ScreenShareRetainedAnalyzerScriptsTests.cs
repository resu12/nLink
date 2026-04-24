using System.Diagnostics;
using System.Text;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareRetainedAnalyzerScriptsTests
{
    private static readonly string[] RetainedScripts =
    {
        "Analyze-ScreenShareLatencyRegression.ps1",
        "Analyze-ScreenShareHelperUpstreamLatency.ps1",
        "Analyze-ScreenShareHelperReadyPath.ps1",
        "Analyze-ScreenShareHelperReceivePath.ps1",
        "Analyze-ScreenShareHelperBridgeIngress.ps1",
        "Analyze-ScreenShareHelperNknReceive.ps1",
        "Analyze-ScreenShareHelperWsReceive.ps1",
        "Analyze-ScreenShareHelperSocketReceive.ps1",
        "Analyze-ScreenShareExternalDelivery.ps1",
        "Analyze-ScreenShareExternalTransportHealth.ps1",
    };

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RetainedTrackBAnalyzerScripts_ParseWithoutSyntaxErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scriptPaths = RetainedScripts
            .Select(name => Path.Combine(repoRoot, "tools", name))
            .ToArray();

        foreach (var scriptPath in scriptPaths)
        {
            Assert.True(File.Exists(scriptPath), $"Expected retained analyzer script to exist: {scriptPath}");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-retained-analyzer-parse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var parserHarnessPath = Path.Combine(tempRoot, "parse-retained-scripts.ps1");
            File.WriteAllText(parserHarnessPath, BuildParserHarness(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(parserHarnessPath);
            foreach (var scriptPath in scriptPaths)
            {
                process.StartInfo.ArgumentList.Add(scriptPath);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(
                process.ExitCode == 0,
                $"Retained analyzer script parser validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string BuildParserHarness()
    {
        return """
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$ScriptPaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($scriptPath in $ScriptPaths) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors) | Out-Null

    if ($errors -and $errors.Count -gt 0) {
        foreach ($error in $errors) {
            Write-Error ("{0}:{1}:{2} {3}" -f $error.Extent.File, $error.Extent.StartLineNumber, $error.Extent.StartColumnNumber, $error.Message)
        }

        exit 1
    }
}
""";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
