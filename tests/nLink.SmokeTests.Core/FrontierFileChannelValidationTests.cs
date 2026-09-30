using NLink.Tools;

namespace NLink.SmokeTests;

public sealed class FrontierFileChannelValidationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeChannel_RejectsWordTailAndRetainedPacketMutations(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierFileChannelComparison.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-file-channel-comparison-ok", result.Stdout);
    }

    private static LocalFrontierFileChannel Create(int length)
    {
        var channel = LocalFrontierFileChannel.Create("Local\\nlink-channel-validation-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), Guid.NewGuid(), 0, 1, 1, 2, 2)!;
        Assert.NotNull(channel);
        Assert.True(channel.RememberSubmission(Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray()));
        channel.PublishSubmission(); channel.PublishAdopted();
        channel.View.Write(8, LocalFrontierFileChannel.Acknowledged);
        Assert.True(channel.IsCurrent);
        return channel;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1025)]
    public void EveryControlAndPayloadByte_IsRevalidatedOnEveryCall(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = Create(length);
        for (var offset = 0; offset < LocalFrontierFileChannel.HeaderBytes + length; offset++)
        {
            var prior = channel.View.ReadByte(offset);
            channel.View.Write(offset, (byte)(prior ^ 1));
            Assert.False(channel.IsCurrent, "Mutation admitted at byte " + offset);
            channel.View.Write(offset, prior);
            Assert.True(channel.IsCurrent); // A previous snapshot must not be reused.
        }
    }

    [Fact]
    public void MaximumPayload_IsFullyBoundedAndChecksItsTail()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = Create(LocalFrontierFileManifest.MaximumBytes);
        foreach (var index in new[] { 0, 1, LocalFrontierFileManifest.MaximumBytes / 2, LocalFrontierFileManifest.MaximumBytes - 1 })
        {
            var offset = LocalFrontierFileChannel.HeaderBytes + index;
            var prior = channel.View.ReadByte(offset);
            channel.View.Write(offset, (byte)(prior ^ 1));
            Assert.False(channel.IsCurrent);
            channel.View.Write(offset, prior); Assert.True(channel.IsCurrent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevokedOrClosedView_FailsClosed(bool close)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = Create(1025);
        if (close) channel.View.Dispose(); else channel.Cancel();
        Assert.False(channel.IsCurrent); Assert.False(channel.IsCurrent);
    }
}
