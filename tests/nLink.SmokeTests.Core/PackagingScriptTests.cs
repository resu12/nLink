using System.Diagnostics;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class PackagingScriptTests : CoreSmokeTestsBase
{
    [Fact]
    public void InstallerScripts_DefaultToDownloadOptimizedPackaging_AndExposeInstalledSizeMode()
    {
        var portableScript = File.ReadAllText(RequireRepoFile(Path.Combine("installer", "Build-Portable.ps1")));
        var installerScript = File.ReadAllText(RequireRepoFile(Path.Combine("installer", "Build-Installer.ps1")));
        var preReleaseScript = File.ReadAllText(RequireRepoFile(Path.Combine("tools", "PreRelease-Check.ps1")));
        var betaReadinessScript = File.ReadAllText(RequireRepoFile(Path.Combine("tools", "BetaReadiness-Check.ps1")));

        Assert.Contains("[switch]$LocalOnly", portableScript, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet(\"DownloadSize\", \"InstalledSize\")]", portableScript, StringComparison.Ordinal);
        Assert.Contains("[string]$OptimizeFor = \"DownloadSize\"", portableScript, StringComparison.Ordinal);
        Assert.Contains("$singleFileCompressionEnabled = [string]::Equals($OptimizeFor, \"InstalledSize\"", portableScript, StringComparison.Ordinal);
        Assert.Contains("/p:EnableCompressionInSingleFile=$singleFileCompressionValue", portableScript, StringComparison.Ordinal);
        Assert.DoesNotContain("/p:EnableCompressionInSingleFile=true", portableScript, StringComparison.Ordinal);
        Assert.Contains("Single-file compression:", portableScript, StringComparison.Ordinal);
        Assert.Contains("LocalOnly: skipped release asset copy", portableScript, StringComparison.Ordinal);

        Assert.Contains("[switch]$CopyHelperAlias", installerScript, StringComparison.Ordinal);
        Assert.Contains("[switch]$LocalOnly", installerScript, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet(\"DownloadSize\", \"InstalledSize\")]", installerScript, StringComparison.Ordinal);
        Assert.Contains("[string]$OptimizeFor = \"DownloadSize\"", installerScript, StringComparison.Ordinal);
        Assert.Contains("-OptimizeFor $OptimizeFor", installerScript, StringComparison.Ordinal);
        Assert.Contains("-CopyHelperAlias:$CopyHelperAlias", installerScript, StringComparison.Ordinal);
        Assert.Contains("-LocalOnly:$LocalOnly", installerScript, StringComparison.Ordinal);
        Assert.Contains("optimize_for=$OptimizeFor", installerScript, StringComparison.Ordinal);
        Assert.Contains("single_file_compression=$singleFileCompressionValue", installerScript, StringComparison.Ordinal);
        Assert.Contains("-SingleFileCompression:$singleFileCompressionEnabled", installerScript, StringComparison.Ordinal);
        Assert.Contains("Previous package comparison", installerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("`n    -CopyHelperAlias`r", installerScript, StringComparison.Ordinal);
        Assert.Contains("package-size-summary.txt", installerScript, StringComparison.Ordinal);

        Assert.Contains("Build-Installer.ps1\" -Runtime $Runtime -CopyHelperAlias", preReleaseScript, StringComparison.Ordinal);
        Assert.Contains("Build-Installer.ps1\" -Runtime $Runtime -CopyHelperAlias", betaReadinessScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPackageManifest_RejectsBridgeManifestWithStaleAppVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptPath = RequireRepoFile(Path.Combine("build", "verify-package-manifest.ps1"));
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-packaging-manifest-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stageDir = Path.Combine(tempRoot, "stage");
            var bridgeDir = Path.Combine(stageDir, "bridge", "win-x64");
            Directory.CreateDirectory(bridgeDir);

            File.WriteAllText(
                Path.Combine(bridgeDir, "index.js"),
                "const bulkClient = true; const bulkAddress = ''; const SUPPORTED_CHANNELS = ['bulk'];");
            File.WriteAllText(Path.Combine(bridgeDir, "package.json"), """{"name":"test-bridge"}""");
            File.WriteAllText(Path.Combine(bridgeDir, "package-lock.json"), """{"lockfileVersion":3}""");
            File.WriteAllText(Path.Combine(bridgeDir, "bridge-dependencies.json"), """{"nodeModulesShipped":false}""");

            var manifestPath = Path.Combine(tempRoot, "package-manifest.txt");
            File.WriteAllLines(
                manifestPath,
                [
                    "bridge/win-x64/index.js",
                    "bridge/win-x64/package.json",
                    "bridge/win-x64/package-lock.json",
                    "bridge/win-x64/bridge-manifest.json",
                    "bridge/win-x64/bridge-dependencies.json",
                ]);

            WriteBridgeManifest(bridgeDir, appVersion: "1.2.2");
            var stale = await RunPowerShellAsync(
                scriptPath,
                "-StageDir",
                stageDir,
                "-ManifestPath",
                manifestPath,
                "-ExpectedAppVersion",
                "1.2.3");

            Assert.NotEqual(0, stale.ExitCode);
            Assert.Contains("appVersion mismatch", stale.Stdout + stale.Stderr, StringComparison.Ordinal);

            WriteBridgeManifest(bridgeDir, appVersion: "1.2.3");
            var current = await RunPowerShellAsync(
                scriptPath,
                "-StageDir",
                stageDir,
                "-ManifestPath",
                manifestPath,
                "-ExpectedAppVersion",
                "1.2.3");

            Assert.Equal(0, current.ExitCode);
            Assert.Contains("Package manifest verified", current.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task CleanPackagingArtifacts_DryRunDoesNotDelete_ApplyPrunesGeneratedArtifactsOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptPath = RequireRepoFile(Path.Combine("tools", "Clean-PackagingArtifacts.ps1"));
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-packaging-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            File.WriteAllText(Path.Combine(tempRoot, "VERSION"), "1.2.3");

            var artifacts = Path.Combine(tempRoot, "artifacts");
            Directory.CreateDirectory(artifacts);
            WriteSizedFile(Path.Combine(artifacts, "publish", "old.bin"), 16);
            WriteSizedFile(Path.Combine(artifacts, "size-probes", "probe.bin"), 16);
            WriteSizedFile(Path.Combine(artifacts, "portable", "helper", "nLink.exe"), 16);
            WriteSizedFile(Path.Combine(artifacts, "portable", "nLink-Portable-win-x64-1.2.2.zip"), 16);
            WriteSizedFile(Path.Combine(artifacts, "portable", "nLink-Portable-win-x64-1.2.3.zip"), 16);
            WriteSizedFile(Path.Combine(artifacts, "installer", "nLink-Setup-win-x64-1.2.2.exe"), 16);
            WriteSizedFile(Path.Combine(artifacts, "installer", "nLink-Setup-win-x64-1.2.3.exe"), 16);
            WriteSizedFile(Path.Combine(artifacts, "releases", "1.0.0", "old.zip"), 16);
            WriteSizedFile(Path.Combine(artifacts, "releases", "1.2.2", "prev.zip"), 16);
            WriteSizedFile(Path.Combine(artifacts, "releases", "1.2.3", "current.zip"), 16);
            WriteSizedFile(Path.Combine(artifacts, "soak", "20260425", "evidence.txt"), 16);
            Directory.SetLastWriteTimeUtc(Path.Combine(artifacts, "releases", "1.0.0"), DateTime.UtcNow.AddDays(-10));
            Directory.SetLastWriteTimeUtc(Path.Combine(artifacts, "releases", "1.2.2"), DateTime.UtcNow.AddDays(-1));
            Directory.SetLastWriteTimeUtc(Path.Combine(artifacts, "releases", "1.2.3"), DateTime.UtcNow.AddDays(-5));

            var dryRun = await RunPowerShellAsync(scriptPath, "-RepoRoot", tempRoot);
            Assert.Equal(0, dryRun.ExitCode);
            Assert.Contains("DRY-RUN", dryRun.Stdout, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(artifacts, "publish", "old.bin")));
            Assert.True(File.Exists(Path.Combine(artifacts, "soak", "20260425", "evidence.txt")));

            var apply = await RunPowerShellAsync(scriptPath, "-RepoRoot", tempRoot, "-Apply", "-KeepReleaseVersions", "2");
            Assert.Equal(0, apply.ExitCode);
            Assert.Contains("Packaging cleanup complete", apply.Stdout, StringComparison.Ordinal);

            Assert.False(Directory.Exists(Path.Combine(artifacts, "publish")));
            Assert.False(Directory.Exists(Path.Combine(artifacts, "size-probes")));
            Assert.False(Directory.Exists(Path.Combine(artifacts, "portable", "helper")));
            Assert.False(File.Exists(Path.Combine(artifacts, "portable", "nLink-Portable-win-x64-1.2.2.zip")));
            Assert.True(File.Exists(Path.Combine(artifacts, "portable", "nLink-Portable-win-x64-1.2.3.zip")));
            Assert.False(File.Exists(Path.Combine(artifacts, "installer", "nLink-Setup-win-x64-1.2.2.exe")));
            Assert.True(File.Exists(Path.Combine(artifacts, "installer", "nLink-Setup-win-x64-1.2.3.exe")));
            Assert.False(Directory.Exists(Path.Combine(artifacts, "releases", "1.0.0")));
            Assert.True(Directory.Exists(Path.Combine(artifacts, "releases", "1.2.2")));
            Assert.True(Directory.Exists(Path.Combine(artifacts, "releases", "1.2.3")));
            Assert.True(File.Exists(Path.Combine(artifacts, "soak", "20260425", "evidence.txt")));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string RequireRepoFile(string relativePath)
    {
        var path = FindFileUpwards(relativePath);
        Assert.False(string.IsNullOrWhiteSpace(path), $"Expected repo file: {relativePath}");
        return path!;
    }

    private static void WriteSizedFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray());
    }

    private static void WriteBridgeManifest(string bridgeDir, string appVersion)
    {
        var indexHash = ComputeSha256(Path.Combine(bridgeDir, "index.js"));
        var packageLockHash = ComputeSha256(Path.Combine(bridgeDir, "package-lock.json"));
        File.WriteAllText(
            Path.Combine(bridgeDir, "bridge-manifest.json"),
            $$"""
              {
                "manifestVersion": 1,
                "appVersion": "{{appVersion}}",
                "runtime": "win-x64",
                "buildTimestampUtc": "2026-05-05T00:00:00Z",
                "bridgeScriptSha256": "{{indexHash}}",
                "nodeVersion": "v24.13.1",
                "nodeArchiveSha256": "test",
                "packageLockSha256": "{{packageLockHash}}",
                "npmVersion": "test",
                "nccVersion": "test",
                "bridgePackageVersion": "test",
                "nodeModulesShipped": false,
                "dependencyEvidenceFile": "bridge-dependencies.json"
              }
              """);
    }

    private static string ComputeSha256(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(string scriptPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
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
            // Best effort test cleanup.
        }
    }
}
