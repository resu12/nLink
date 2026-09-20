using System.Reflection;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierFileRendezvousTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "delayed-channel", "delayed-adoption", "repeat-waits", "copied-waits",
            "cancel-channel", "timeout-channel", "forged-ready", "bad-channel-scope", "cancel-adoption", "catalog-cancel",
            "timeout-adoption", "forged-adopted", "bad-adoption-scope", "parent-death-channel", "parent-death-adoption" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeFileRendezvous_PreservesOriginalAttachmentAndAcknowledgment(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-file-rendezvous-20260919", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierFileRendezvous.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-file-rendezvous=" + scenario, result.Stdout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFileRendezvous_PublicationDoesNotWakePeerUnderNativeMutex(bool adoption)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // Isolate publication ordering only; these synthetic control bytes are
        // not an original file submission or proof of writer custody.
        using var channel = LocalFrontierFileChannel.Create("Local\\nlink-file-signal-test-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), Guid.NewGuid(), 0, 1, 1, 2, 2);
        Assert.NotNull(channel);
        Assert.True(channel.Gate.WaitOne(0));
        try
        {
            Assert.True(channel.RememberSubmission([1]));
            channel.PublishSubmission();
            if (adoption) { channel.PublishAdopted(); }
            Assert.False((adoption ? channel.AdoptedEvent : channel.SubmittedEvent).WaitOne(0),
                "Publication woke a peer while its one-shot native mutex check would fail");
        }
        finally { channel.Gate.ReleaseMutex(); }
    }

    // App-role calls only. Real native ownership, synthetic file contents; no
    // coordinator proof is inferred from a successful protocol rendezvous.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool WaitForFiles(LocalFrontierAppRequest.AppLease lease, bool copied, bool repeat)
    {
        if (copied)
        {
            var copy = (LocalFrontierAppRequest.AppLease)Copy(lease);
            Assert.False(copy.WaitForFileHandoffReady()); copy.Dispose(); Assert.True(lease.IsCurrent);
        }
        var ready = lease.WaitForFileHandoffReady();
        if (ready && repeat) { Assert.False(lease.WaitForFileHandoffReady()); Assert.True(lease.IsCurrent); }
        return ready;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool WaitForAdoption(FrontierFileHandoffFixture fixture, string variant)
    {
        var handoff = (LocalFrontierAppRequest.FileHandoff)typeof(FrontierFileHandoffFixture).GetField("handoff", Flags)!.GetValue(fixture)!;
        if (variant == "copied-waits")
        {
            var copy = (LocalFrontierAppRequest.FileHandoff)Copy(handoff);
            Assert.False(copy.WaitForAdoption()); copy.Dispose(); Assert.True(handoff.IsCurrent);
        }
        if (variant == "catalog-cancel")
        {
            var waiting = Task.Run(handoff.WaitForAdoption);
            var sync = typeof(LocalFrontierAppRequest.FileHandoff).GetField("sync", Flags)!.GetValue(handoff)!;
            var attempted = typeof(LocalFrontierAppRequest.FileHandoff).GetField("adoptionWaitAttempted", Flags)!;
            Assert.True(SpinWait.SpinUntil(() => (bool)attempted.GetValue(handoff)!, TimeSpan.FromSeconds(5)));
            Assert.True(Monitor.TryEnter(sync, 1000), "Adoption idle wait held its ownership lock");
            Monitor.Exit(sync);
            fixture.CancelCatalog();
            Assert.True(waiting.Wait(5000), "Catalog cancellation did not wake adoption");
            Assert.False(waiting.Result); Assert.False(handoff.IsCurrent); return false;
        }
        var ready = handoff.WaitForAdoption();
        if (ready && variant == "repeat-waits") { Assert.False(handoff.WaitForAdoption()); Assert.True(handoff.IsCurrent); }
        return ready;
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Copy(object value) => typeof(object).GetMethod("MemberwiseClone", Flags)!.Invoke(value, null)!;
}
