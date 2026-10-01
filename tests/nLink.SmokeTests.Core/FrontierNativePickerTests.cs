using NLink.App.Views;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using System.Reflection;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierNativePickerTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "request")]
    [InlineData("pwsh", "missing")]
    [InlineData("pwsh", "partial")]
    [InlineData("pwsh", "cancel")]
    [InlineData("pwsh", "runtime-valid")]
    [InlineData("pwsh", "runtime-disposed")]
    [InlineData("pwsh", "queued-pump")]
    public async Task OriginalPicker_UsesNativeCustodyAndCurrentRuntime(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-native-picker-20260929", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "writer-source-read-native-" + fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var output = string.Join("\n", await Task.WhenAll(Directory.GetFiles(directory, "*-stdout.log", SearchOption.AllDirectories).Select(path => File.ReadAllTextAsync(path))));
        Assert.Contains("native-picker=" + fault, output);
    }

    [Fact]
    public async Task NativeOptInWithoutOriginalLifetime_RejectsPathFallback()
    {
        using var developer = ReleaseOverridePolicy.OverrideUnsafeDeveloperModeForTests(true);
        var previous = Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_SOURCE_V1");
        var previousPath = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            Environment.SetEnvironmentVariable("NLINK_APP_CANONICAL_SOURCE_V1", "1");
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", path);
            await Assert.ThrowsAsync<IOException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", path + ".missing");
            await Assert.ThrowsAsync<IOException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", null);
            await Assert.ThrowsAsync<IOException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_APP_CANONICAL_SOURCE_V1", previous);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", previousPath);
            File.Delete(path);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunNativePickerAsync(SessionFileTransferService.FrontierAppLifetime lifetime,
        string directory, string signal, string scenario)
    {
        using var ready = EventWaitHandle.OpenExisting(signal + ".sender-source-packet-ready");
        Assert.True(ready.WaitOne(20000)); // Fixture scheduling, never source authority.
        using var developer = ReleaseOverridePolicy.OverrideUnsafeDeveloperModeForTests(true);
        var fault = scenario["writer-source-read-native-".Length..];
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", Path.Combine(directory, "filetransfer-tuna-gui-payload.bin"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_SOURCE_V1"));
        if (fault is "missing" or "partial" or "request")
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<IOException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<IOException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
            Console.WriteLine("native-picker=" + fault);
            return;
        }
        if (fault == "cancel")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync(cancellation.Token));
        }
        var selection = Assert.IsType<FileTransferPickerSelection>(await NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
        var native = Assert.IsType<SessionFileTransferService.NativeSenderSelection>(selection.OpenReadStreamAsync.Target);
        await Assert.ThrowsAsync<IOException>(() => selection.OpenReadStreamAsync(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var clone = typeof(object).GetMethod("MemberwiseClone", flags)!;
        var copy = (SessionFileTransferService.NativeSenderSelection)clone.Invoke(native, null)!;
        copy.Dispose();
        using var foreign = new SessionFileTransferService();
        Assert.False(copy.TryBindForSend(foreign, selection.Descriptor));
        await Assert.ThrowsAsync<IOException>(() => copy.OpenReadStreamAsync(CancellationToken.None));
        if (fault == "queued-pump")
        {
            await NknAccelerationTransportTests.RunNativeQueuedPumpAsync(native);
        }
        else if (fault.StartsWith("runtime-", StringComparison.Ordinal))
        {
            await NknAccelerationTransportTests.RunNativeRuntimeReadsAsync(native, fault);
        }
        else
        {
            await SessionFileTransferV4SenderTests.ReadNativePickerMetadataAsync(native);
        }
        Console.WriteLine("native-picker=" + fault);
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task ReadNativePickerMetadataAsync(SessionFileTransferService.NativeSenderSelection selection)
    {
        using var senderTransport = new LoopbackFileTransferTransport("native-picker");
        using var receiverTransport = new LoopbackFileTransferTransport("native-picker");
        senderTransport.Connect(receiverTransport);
        using var service = SessionFileTransferService.CreateForApp();
        using var foreign = new SessionFileTransferService();
        Assert.Null(await foreign.TryStartSendAsync(selection.Descriptor, selection.OpenReadStreamAsync, CancellationToken.None));
        Assert.Null(await service.TryStartSendAsync(selection.Descriptor with { }, selection.OpenReadStreamAsync, CancellationToken.None));
        service.AttachTransport(senderTransport);
        var prepared = new TaskCompletionSource<FileTransferTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TransferChanged += (_, args) =>
        {
            if (args.Snapshot.Outbound is not { State: FileTransferTransferState.AwaitingStart } snapshot) return;
            service.ResetSessionState();
            prepared.TrySetResult(snapshot);
        };
        Assert.NotNull(await service.TryStartSendAsync(selection.Descriptor, selection.OpenReadStreamAsync, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => selection.OpenReadStreamAsync(CancellationToken.None));
        var offer = Assert.Single(senderTransport.SentOffers);
        await receiverTransport.SendFileTransferAcceptAsync(new FileTransferAcceptV1
        {
            SessionId = offer.SessionId, TransferId = offer.TransferId,
            AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
        }, CancellationToken.None);
        var snapshot = await prepared.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var source = (NLink.Tools.LocalCanonicalSenderSource)selection.GetType().GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selection)!;
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(source.Sha256)), snapshot.Sha256Base64);
        await Assert.ThrowsAsync<IOException>(() => selection.OpenReadStreamAsync(CancellationToken.None));
        Assert.Null(await service.TryStartSendAsync(selection.Descriptor, selection.OpenReadStreamAsync, CancellationToken.None));
        Assert.True(source.IsCurrent); // Runtime retirement did not close the app's anchor.
    }
}
