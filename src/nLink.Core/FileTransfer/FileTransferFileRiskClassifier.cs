using System.IO;

namespace NLink.Core.FileTransfer;

public enum FileTransferFileRiskLevel
{
    None = 0,
    ExecutableOrScript = 1,
    Archive = 2,
}

public readonly record struct FileTransferFileRiskAssessment(
    FileTransferFileRiskLevel Level,
    string WarningText)
{
    public bool IsRisky => Level != FileTransferFileRiskLevel.None;

    public static FileTransferFileRiskAssessment None { get; } =
        new(FileTransferFileRiskLevel.None, string.Empty);
}

public static class FileTransferFileRiskClassifier
{
    private static readonly HashSet<string> ExecutableOrScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".msi",
        ".bat",
        ".cmd",
        ".ps1",
        ".psm1",
        ".vbs",
        ".vbe",
        ".js",
        ".jse",
        ".wsf",
        ".wsh",
        ".lnk",
        ".scr",
        ".com",
        ".cpl",
        ".reg",
        ".hta",
        ".jar",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".gz",
    };

    public static FileTransferFileRiskAssessment Assess(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileTransferFileRiskAssessment.None;
        }

        var extension = Path.GetExtension(fileName.Trim());
        if (ExecutableOrScriptExtensions.Contains(extension))
        {
            return new FileTransferFileRiskAssessment(
                FileTransferFileRiskLevel.ExecutableOrScript,
                "This file type can run commands on your computer. Only accept it if you trust the sender.");
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return new FileTransferFileRiskAssessment(
                FileTransferFileRiskLevel.Archive,
                "Archives can contain executable files. Review the contents before opening anything inside.");
        }

        return FileTransferFileRiskAssessment.None;
    }
}
