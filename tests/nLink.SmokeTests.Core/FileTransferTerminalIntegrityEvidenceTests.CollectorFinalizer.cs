using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalCollectorFinalizer_WindowsPowerShellConsumesOwnedOutput()
        => RunSourceBindingCaseAsync("native-collector-finalizer-powershell", nameof(TerminalCollectorFinalizer_WindowsPowerShellConsumesOwnedOutput));

    [Fact]
    public Task TerminalCollectorFinalizer_PowerShell7ConsumesOwnedOutput()
        => RunSourceBindingCaseAsync("native-collector-finalizer-pwsh", nameof(TerminalCollectorFinalizer_PowerShell7ConsumesOwnedOutput));

    private static void CheckCollectorFinalizerResult(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", "valid", "worker-result.json")));
        var result = document.RootElement.GetProperty("NativeCollectorFinalizer");
        Assert.Equal(4, result.GetProperty("RejectedBeforeRead").GetInt32());
        Assert.Equal(5, result.GetProperty("RejectedCaptureSubstitutions").GetInt32());
        foreach (var name in new[] { "RepeatedProcessingRejected", "FinalizerOwnedStreamCapture", "FinalizerOwnedNativeCapture",
            "ClosedAfterOriginalFinalization", "WriteAndReplacementBlocked", "ReleasedAfterFinalization" })
        { Assert.True(result.GetProperty(name).GetBoolean(), name); }
        foreach (var name in new[] { "WriterCopyProvenanceProven", "PrimaryProvenanceHandoffProven", "AcceptanceChanged" })
        { Assert.False(result.GetProperty(name).GetBoolean(), name); }
    }
}
