using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferActivationRetryEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("OpsSuite", "ActivationRetryEvidence")]
    public async Task ActivationRetryEvidence_ActualProjectionAndExactHistoryHaveCrossHostParity(bool descendant)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreateActivationRetryEvidenceFixture(descendant);
        var lines = EmitProjection(envelopes);
        var directory = Path.Combine(Path.GetTempPath(), "nlink-activation-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "evidence.log");
            await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));
            const string script = """
                param([string]$RepoRoot,[string]$InputPath,[int]$ExpectedAncestry)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/ActivationRetryEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath -ExpectedAncestry $ExpectedAncestry
                """;
            string? previous = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, descendant ? "2" : "1"], host);
                Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
                Assert.Contains("activation-retry-evidence-ok", result.Stdout, StringComparison.Ordinal);
                if (previous is not null) { Assert.Equal(previous, result.Stdout.Trim()); }
                previous = result.Stdout.Trim();
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    private static string[] EmitProjection(IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        var type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(type, nonPublic: true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        type.GetField("drainScheduled", flags)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        Assert.Equal(true, type.GetMethod("Start", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]));
        // Projection/lineage unit fixture only. Do not manufacture physical
        // effect completion; the joined service test supplies that integration.
        foreach (var envelope in envelopes) { type.GetMethod("Enqueue", flags)!.Invoke(emitter, [envelope]); }
        type.GetMethod("Complete", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)type.GetField("pending", flags)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=activation-retry-fixture; log_writer_sequence={index + 1}")).ToArray();
    }
}
