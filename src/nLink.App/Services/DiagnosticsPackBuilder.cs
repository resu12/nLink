using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services;

internal static class DiagnosticsPackBuilder
{
    public static async Task<string> CreateAsync(
        string logsFolderPath,
        string diagnosticsText,
        string outputFolderPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsFolderPath);
        ArgumentNullException.ThrowIfNull(diagnosticsText);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolderPath);

        Directory.CreateDirectory(outputFolderPath);
        var filePath = Path.Combine(outputFolderPath, $"diagnostics-pack-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");

        using var file = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        var diagEntry = zip.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
        await using (var diagStream = diagEntry.Open())
        await using (var writer = new StreamWriter(diagStream, new UTF8Encoding(false)))
        {
            var safeDiagnostics = DiagnosticsRedactor.Redact(diagnosticsText);
            await writer.WriteAsync(safeDiagnostics.AsMemory(), ct).ConfigureAwait(false);
        }

        if (Directory.Exists(logsFolderPath))
        {
            foreach (var logFile in Directory.GetFiles(logsFolderPath, "*.log", SearchOption.TopDirectoryOnly))
            {
                await AddFileWithLockResilienceAsync(zip, logFile, ct).ConfigureAwait(false);
            }
        }

        var resourcesDir = Path.GetFullPath(Path.Combine("artifacts", "resources"));
        if (Directory.Exists(resourcesDir))
        {
            foreach (var resourceFile in Directory.GetFiles(resourcesDir, "*.txt", SearchOption.TopDirectoryOnly))
            {
                await AddFileWithLockResilienceAsync(zip, resourceFile, ct, "resources").ConfigureAwait(false);
            }

            foreach (var resourceJson in Directory.GetFiles(resourcesDir, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(3))
            {
                await AddFileWithLockResilienceAsync(zip, resourceJson, ct, "resources").ConfigureAwait(false);
            }
        }

        return filePath;
    }

    private static async Task AddFileWithLockResilienceAsync(ZipArchive zip, string sourcePath, CancellationToken ct, string folderName = "logs")
    {
        var fileName = Path.GetFileName(sourcePath);
        Exception? last = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var safeContent = DiagnosticsRedactor.Redact(content);

                var entry = zip.CreateEntry(Path.Combine(folderName, fileName), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var entryWriter = new StreamWriter(entryStream, new UTF8Encoding(false));
                await entryWriter.WriteAsync(safeContent.AsMemory(), ct).ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                if (attempt < 3)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }
            }
        }

        var note = zip.CreateEntry(Path.Combine(folderName, fileName + ".note.txt"), CompressionLevel.Optimal);
        await using var noteStream = note.Open();
        await using var noteWriter = new StreamWriter(noteStream, new UTF8Encoding(false));
        await noteWriter.WriteLineAsync($"Could not copy log file '{fileName}'.");
        await noteWriter.WriteLineAsync($"Reason: {last?.GetType().Name ?? "IOException"}");
    }
}
