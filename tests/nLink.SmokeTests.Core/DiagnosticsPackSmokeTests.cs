using System.IO.Compression;
using NLink.App.Services;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class DiagnosticsPackSmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsPack_CreatesZip_WhenLogFileIsLocked_AndAddsNoteIfNeeded()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-diagpack-test-" + Guid.NewGuid().ToString("N"));
        var logsDir = Path.Combine(tempRoot, "logs");
        var outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "nlink.log"), "line1");

        var logPath = Path.Combine(logsDir, "nlink.log");
        await using var lockHandle = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        string zipPath;
        try
        {
            zipPath = await DiagnosticsPackBuilder.CreateAsync(logsDir, "diag text", outDir, CancellationToken.None);
        }
        finally
        {
            await lockHandle.DisposeAsync();
        }

        Assert.True(File.Exists(zipPath));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, e => e.FullName == "diagnostics.txt");
        Assert.Contains(zip.Entries, e =>
        {
            var fullName = e.FullName.Replace('\\', '/');
            return fullName == "logs/nlink.log" || fullName == "logs/nlink.log.note.txt";
        });

        CleanupDirectoryIfExists(tempRoot);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsPack_RedactsSensitiveDiagnosticsText_AndLogs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-diagpack-redact-" + Guid.NewGuid().ToString("N"));
        var logsDir = Path.Combine(tempRoot, "logs");
        var outDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "nlink.log"), "walletSeed: alpha beta gamma");

        var diagnosticsText = "seedBase64=QkFTRTY0U0VFRA==";
        var previousCwd = Environment.CurrentDirectory;
        string zipPath;
        try
        {
            Environment.CurrentDirectory = tempRoot;
            zipPath = await DiagnosticsPackBuilder.CreateAsync(logsDir, diagnosticsText, outDir, CancellationToken.None);
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var diagEntry = Assert.Single(zip.Entries.Where(e => e.FullName == "diagnostics.txt"));
        using var diagReader = new StreamReader(diagEntry.Open());
        var diagText = await diagReader.ReadToEndAsync();
        Assert.DoesNotContain("QkFTRTY0U0VFRA==", diagText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", diagText, StringComparison.Ordinal);

        var logEntry = zip.Entries.Single(e => e.FullName.Replace('\\', '/') == "logs/nlink.log");
        using var logReader = new StreamReader(logEntry.Open());
        var logText = await logReader.ReadToEndAsync();
        Assert.DoesNotContain("alpha beta gamma", logText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", logText, StringComparison.Ordinal);

        CleanupDirectoryIfExists(tempRoot);
    }

    private static void CleanupDirectoryIfExists(string path)
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
            // best effort
        }
    }
}
