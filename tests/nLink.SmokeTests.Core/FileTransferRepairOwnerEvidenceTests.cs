using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed class FileTransferRepairOwnerEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("OpsSuite", "RepairOwnerEvidence")]
    public void RepairOwnerEvidence_EmitterDoesNotIndexPhysicalAuthority()
    {
        var fixture = FileTransferCoordinatorTests.CreateRepairOwnerEvidenceFixture();
        var evidence = FileTransferCoordinator.CaptureRepairOwnerEvidence(fixture.State, fixture.Authority);
        var type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(type, nonPublic: true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("drainScheduled", flags)!.SetValue(emitter, 1);
        type.GetMethod("Enqueue", flags)!.Invoke(emitter, [evidence.SnapshotEnvelope]);
        foreach (var name in new[] { "authorityEffectBindings", "authorityEffectBindingsByOperationKey" })
        {
            Assert.Empty((System.Collections.IEnumerable)type.GetField(name, flags)!.GetValue(emitter)!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("OpsSuite", "RepairOwnerEvidence")]
    public async Task RepairOwnerEvidence_ExactProjectedBindingsAndNegativesHaveCrossHostParity(bool delayedDrain)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var fixture = FileTransferCoordinatorTests.CreateRepairOwnerEvidenceFixture();
        var evidence = FileTransferCoordinator.CaptureRepairOwnerEvidence(fixture.State, fixture.Authority);
        var lines = FileTransferCheckpointResponderCoexistenceEvidenceTests.Emit([evidence.SnapshotEnvelope!]).ToList();
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        string Format(string name, object value) => (string)typeof(SessionFileTransferService)
            .GetMethod(name, flags)!.Invoke(null, [value])!;
        // Emit holds the real emitter's drain and reads its queued projection.
        // Model the synchronous grant before that queue is drained, without
        // changing any authored receipt or the snapshot stream's own order.
        lines.Insert(delayedDrain ? 1 : lines.Count, SensitiveDataRedactor.Redact("event=filetransfer_fallback_frontier_repair_authority_granted; " +
            Format("FormatRepairOwnerEvidence", evidence.Reference!) +
            Format("FormatFallbackFrontierRepairAuthorityTuple", fixture.Authority) +
            $"; protocol_repair_request_id={fixture.Authority.ProtocolRepairRequestId}; request_correlation_id={fixture.Authority.RequestCorrelationId}"));
        for (var index = 0; index < lines.Count; index++)
        {
            var stamp = lines[index].IndexOf("; log_writer_id=", StringComparison.Ordinal);
            var message = stamp < 0 ? lines[index] : lines[index][..stamp];
            lines[index] = $"{message}; log_writer_id=repair-owner-fixture; log_writer_sequence={index + 1}";
        }
        Assert.Equal(delayedDrain,
            lines.FindIndex(line => line.Contains("event=filetransfer_fallback_frontier_repair_authority_granted;", StringComparison.Ordinal)) <
            lines.FindIndex(line => line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal)));
        var directory = Path.Combine(Path.GetTempPath(), "nlink-repair-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "evidence.log");
            await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));
            const string script = """
                param([string]$RepoRoot,[string]$InputPath)
                $ErrorActionPreference='Stop'
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/RepairOwnerEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
                """;
            string? output = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
                Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
                Assert.Contains("repair-owner-evidence-ok", result.Stdout, StringComparison.Ordinal);
                if (output is not null) { Assert.Equal(output, result.Stdout.Trim()); }
                output = result.Stdout.Trim();
            }
        }
        finally { TryDeleteDirectory(directory); }
    }
}
