using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferFileRiskClassifierTests
{
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("INSTALL.MSI")]
    [InlineData("deploy.ps1")]
    [InlineData("photo.jpg.exe")]
    [InlineData("shortcut.lnk")]
    public void Assess_ExecutableOrScriptExtensions_ReturnsExecutableWarning(string fileName)
    {
        var risk = FileTransferFileRiskClassifier.Assess(fileName);

        Assert.Equal(FileTransferFileRiskLevel.ExecutableOrScript, risk.Level);
        Assert.True(risk.IsRisky);
        Assert.Contains("run commands", risk.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bundle.zip")]
    [InlineData("backup.7z")]
    [InlineData("logs.TAR")]
    [InlineData("archive.gz")]
    public void Assess_ArchiveExtensions_ReturnsArchiveWarning(string fileName)
    {
        var risk = FileTransferFileRiskClassifier.Assess(fileName);

        Assert.Equal(FileTransferFileRiskLevel.Archive, risk.Level);
        Assert.True(risk.IsRisky);
        Assert.Contains("Archives", risk.WarningText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("photo.jpg")]
    [InlineData("notes.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void Assess_SafeOrEmptyNames_ReturnsNoRisk(string fileName)
    {
        var risk = FileTransferFileRiskClassifier.Assess(fileName);

        Assert.Equal(FileTransferFileRiskLevel.None, risk.Level);
        Assert.False(risk.IsRisky);
        Assert.Equal(string.Empty, risk.WarningText);
    }
}
