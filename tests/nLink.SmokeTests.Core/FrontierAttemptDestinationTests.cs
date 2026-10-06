using System.Reflection;
using System.IO.Pipes;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptDestinationTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "reader-dispose", "pending-dispose", "canceled", "destination", "session", "closed" }
        select new object[] { host, "writer-destination-" + scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalHelper_RetainsOnlyExactCompletedDestination(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-attempt-destination-20260926",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        Assert.Contains("attempt-role-writers-ok", await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log")));
        var app = string.Join("\n", await Task.WhenAll(Directory.GetFiles(directory, "*-stdout.log", SearchOption.AllDirectories)
            .Select(path => File.ReadAllTextAsync(path))));
        Assert.Contains("receiver-destination-handoff=" + scenario, app);
    }

}

internal sealed class FrontierAttemptDestinationReceiver : SessionFileTransferServiceTestBase
{
    internal static async Task<Action?> RunReceiverAsync(SessionFileTransferService.FrontierAppLifetime lifetime,
        string directory, string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lease = Assert.IsType<LocalFrontierAppRequest.AppLease>(lifetime.GetType().GetField("lease", flags)!.GetValue(lifetime));
        var sessionId = "destination-" + lease.RequestId.ToString("N");
        var transferId = "receive-" + Guid.NewGuid().ToString("N");
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024 + 23).Select(i => (byte)(i % 251)).ToArray();
        using var outgoing = new LoopbackFileTransferTransport(sessionId);
        using var incoming = new LoopbackFileTransferTransport(sessionId);
        outgoing.Connect(incoming);
        using var sender = new SessionFileTransferService();
        using var receiver = SessionFileTransferService.CreateForApp();
        sender.AttachTransport(outgoing); receiver.AttachTransport(incoming);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.InboundFinalizationStartedForTests = task => finalizing.TrySetResult(task);
        FileTransferWriteHandle? handle = null;
        FileTransferReceiveDestination? destination = null;
        SessionFileTransferService.NativeDestinationEvidence? evidence = null;
        bool? prematureTake = null;
        if (scenario.EndsWith("-valid", StringComparison.Ordinal))
        {
            incoming.BeforeCompleteDeliveredAsync = async (message, _) =>
            {
                await Task.Yield(); // Outside the Service's send-invocation gate.
                Assert.True(destination!.IsFinalized);
                Assert.False(receiver.Snapshot.Inbound!.IsTerminal);
                prematureTake = evidence!.TryComplete(receiver, destination);
                if (prematureTake == true) throw new InvalidOperationException("Destination custody escaped before Service completion.");
            };
        }
        var preserveUntilShutdown = false;
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("native-receive.bin", bytes.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), CancellationToken.None);
            await WaitForAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (offer, _) =>
            {
                var now = DateTimeOffset.UtcNow;
                var session = new SessionId(offer.SessionId);
                var helper = new PeerAddress("attempt.destination.helper");
                var grant = new SessionGrant(helper, CapabilityGrant.FileTransfer, session, now.AddMinutes(5));
                var state = (SessionSecurityState.Empty with
                {
                    SessionId = session, HelperAddress = helper, HelpeeAddress = new PeerAddress("attempt.destination.helpee"), InviteValidated = true,
                }).WithHandshakeVerified(helper).WithApproval(grant);
                var result = new SessionFileTransferGuard(() => now).OpenReceiveWriteStream(true, state, grant,
                    new FileTransferDescriptor(session, helper, offer.FileName, offer.FileSizeBytes),
                    new FileTransferStoragePolicy(Path.Combine(directory, "native-destination")));
                Assert.True(result.IsAllowed, result.Access.Message);
                handle = Assert.IsType<FileTransferWriteHandle>(result.Handle);
                handle.WindowsAfterRenameForTests = () => { entered.TrySetResult(); return release.Task; };
                destination = FileTransferReceiveDestination.FromGuardOwnedHandle(handle);
                return Task.FromResult(destination);
            }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(handle); Assert.NotNull(destination);
            Assert.True(OperationalLogNativeSource.TryDescribe(handle.Stream.SafeFileHandle, out var identity, out var length));
            Assert.Equal(bytes.Length, length);
            Assert.Null(receiver.TryRequestNativeDestinationEvidence(Clone(destination)));
            Assert.Null(sender.TryRequestNativeDestinationEvidence(destination));
            Assert.Null(Clone(receiver).TryRequestNativeDestinationEvidence(destination));
            Assert.Null(Clone(lifetime).TryCreateNativeDestinationEvidence(receiver, destination));
            evidence = FrontierNativeReceiverTests.Automatic
                ? Assert.IsType<SessionFileTransferService.NativeDestinationEvidence>(FrontierNativeReceiverTests.Field(lifetime, "nativeDestinationEvidence"))
                : receiver.TryRequestNativeDestinationEvidence(destination);
            Assert.NotNull(evidence);
            if (FrontierNativeReceiverTests.Automatic)
            {
                lifetime.TryStartNativeDestinationHandoff(evidence);
                Clone(lifetime).TryStartNativeDestinationHandoff(evidence);
                Assert.Null(FrontierNativeReceiverTests.Handoff(lifetime)); // still finalizing; no slot consumed
            }
            Assert.Null(receiver.TryRequestNativeDestinationEvidence(destination));
            Assert.Null(evidence.TryOpenRead(receiver, destination, lease)); // still finalizing
            var copy = Clone(evidence); copy.Dispose();
            Assert.Null(copy.TryOpenRead(receiver, destination, lease));
            var context = receiver.GetType().GetField("inboundTransfer", flags)!.GetValue(receiver)!;
            var stale = scenario.EndsWith("-destination", StringComparison.Ordinal) || scenario.EndsWith("-session", StringComparison.Ordinal) ||
                scenario.EndsWith("-canceled", StringComparison.Ordinal);
            if (scenario.EndsWith("-pending-dispose", StringComparison.Ordinal)) evidence.Dispose();
            if (scenario.EndsWith("-destination", StringComparison.Ordinal))
                context.GetType().GetProperty("WriteDestination")!.SetValue(context, new FileTransferReceiveDestination(destination.Stream));
            if (scenario.EndsWith("-session", StringComparison.Ordinal))
            {
                var generation = context.GetType().GetProperty("DataSessionGeneration")!;
                generation.SetValue(context, (long)generation.GetValue(context)! + 1);
            }
            if (scenario.EndsWith("-canceled", StringComparison.Ordinal))
                ((CancellationTokenSource)context.GetType().GetProperty("LifetimeCts")!.GetValue(context)!).Cancel();
            release.TrySetResult();
            var finalizationTask = await finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await IgnoreCancellation(finalizationTask).WaitAsync(TimeSpan.FromSeconds(5));
            if (scenario.EndsWith("-valid", StringComparison.Ordinal)) Assert.False(prematureTake);
            if (stale)
            {
                if (FrontierNativeReceiverTests.Automatic) Assert.Null(FrontierNativeReceiverTests.Handoff(lifetime));
                // The actual finalization continuation has returned; do not
                // reset first and accidentally make reset satisfy this oracle.
                Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                Assert.Null(evidence.TransferId);
                Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.InboundState);
                Assert.Null(receiver.Snapshot.Inbound!.SavedFilePath);
                Assert.DoesNotContain(incoming.SentDataFrames, f => f is FileTransferStateFrameV4 { TerminalReady: true });
                Assert.Empty(incoming.SentCompletes);
                Console.WriteLine("receiver-destination-handoff=" + scenario);
                return null;
            }
            await WaitForAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Completed &&
                sender.Snapshot.OutboundState == FileTransferTransferState.Completed);
            await WaitForAsync(() => context.GetType().GetProperty("WriteDestination")!.GetValue(context) is null);
            Assert.Equal(handle.Plan.FinalPath, receiver.Snapshot.Inbound!.SavedFilePath);
            if (scenario.EndsWith("-pending-dispose", StringComparison.Ordinal))
            {
                if (FrontierNativeReceiverTests.Automatic) Assert.Null(FrontierNativeReceiverTests.Handoff(lifetime));
                Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                Assert.Null(evidence.TransferId);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(handle.Plan.FinalPath));
            }
            else
            {
                Assert.Equal(sessionId, evidence.SessionId); Assert.Equal(transferId, evidence.TransferId);
                Assert.Equal(bytes.Length, evidence.ByteCount);
                Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), evidence.Sha256Base64);
                Assert.Null(copy.TryOpenRead(receiver, destination, lease));
                Assert.Null(evidence.TryOpenRead(sender, destination, lease));
                Assert.Null(evidence.TryOpenRead(receiver, Clone(destination), lease));
                Assert.Null(evidence.TryOpenRead(receiver, destination, Clone(lease)));
                Assert.Null(Clone(handle).TryTakeFinalizedWindowsRead());
                Assert.Null(destination.TryTakeFinalizedNativeRead()); // already moved exactly once
                Assert.Throws<IOException>(() => File.Delete(handle.Plan.FinalPath));
                if (scenario.EndsWith("-closed", StringComparison.Ordinal))
                {
                    ((SafeFileHandle)evidence.GetType().GetField("file", flags)!.GetValue(evidence)!).Dispose();
                    Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                    Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                }
                else
                {
                    var read = Assert.IsAssignableFrom<Stream>(evidence.TryOpenRead(receiver, destination, lease));
                    var readCopy = Clone(read);
                    Assert.False(readCopy.CanRead);
                    Assert.Throws<ObjectDisposedException>(() => readCopy.ReadByte());
                    readCopy.Dispose();
                    Assert.True(read.CanRead);
                    Assert.False(read.CanWrite); Assert.False(read.CanSeek);
                    Assert.Equal(identity, evidence.FileIdentity);
                    Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                    if (scenario.EndsWith("-reader-dispose", StringComparison.Ordinal))
                    {
                        Assert.Equal(bytes[0], read.ReadByte());
                        read.Dispose();
                        Assert.Throws<ObjectDisposedException>(() => read.ReadByte());
                        Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                    }
                    else
                    {
                        Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(read));
                        Assert.Equal(-1, read.ReadByte());
                        if (scenario.StartsWith("writer-destination-gui-", StringComparison.Ordinal))
                        {
                            if (FrontierNativeReceiverTests.Automatic)
                            {
                                var automatic = Assert.IsAssignableFrom<Task<bool>>(FrontierNativeReceiverTests.Handoff(lifetime));
                                Assert.True(await automatic.WaitAsync(TimeSpan.FromSeconds(15)));
                                Assert.False(await copy.TryHandoffToOriginalGuiAsync(CancellationToken.None));
                                Assert.False(await evidence.TryHandoffToOriginalGuiAsync(CancellationToken.None));
                                Clone(lifetime).TryStartNativeDestinationHandoff(evidence);
                                lifetime.TryStartNativeDestinationHandoff(copy);
                                lifetime.TryStartNativeDestinationHandoff(evidence);
                                Assert.Same(automatic, FrontierNativeReceiverTests.Handoff(lifetime));
                                Console.WriteLine("native-receiver-handoff-ok");
                                read.Dispose();
                                Assert.Throws<IOException>(() => File.Delete(handle.Plan.FinalPath));
                                return null;
                            }
                            using var pipe = new NamedPipeClientStream(".", "nlink-destination-" + lease.RequestId.ToString("N"),
                                PipeDirection.InOut, PipeOptions.Asynchronous);
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await pipe.ConnectAsync(deadline.Token);
                            Assert.False(await copy.TryHandoffToGuiAsync(receiver, destination, lease, pipe, deadline.Token));
                            Assert.False(await evidence.TryHandoffToGuiAsync(Clone(receiver), destination, lease, pipe, deadline.Token));
                            Assert.False(await evidence.TryHandoffToGuiAsync(receiver, Clone(destination), lease, pipe, deadline.Token));
                            Assert.False(await evidence.TryHandoffToGuiAsync(receiver, destination, Clone(lease), pipe, deadline.Token));
                            if (scenario.EndsWith("-cancel-token", StringComparison.Ordinal)) deadline.Cancel();
                            if (scenario.EndsWith("-closed-hold", StringComparison.Ordinal))
                                ((SafeFileHandle)evidence.GetType().GetField("file", flags)!.GetValue(evidence)!).Dispose();
                            bool accepted;
                            if (scenario.EndsWith("-foreign-server", StringComparison.Ordinal))
                            {
                                var foreignName = "nlink-destination-foreign-" + lease.RequestId.ToString("N");
                                using var foreignServer = new NamedPipeServerStream(foreignName, PipeDirection.InOut, 1,
                                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                                using var foreignClient = new NamedPipeClientStream(".", foreignName, PipeDirection.InOut, PipeOptions.Asynchronous);
                                await Task.WhenAll(foreignServer.WaitForConnectionAsync(deadline.Token), foreignClient.ConnectAsync(deadline.Token));
                                accepted = await evidence.TryHandoffToGuiAsync(receiver, destination, lease, foreignClient, deadline.Token);
                            }
                            else accepted = await evidence.TryHandoffToGuiAsync(receiver, destination, lease, pipe, deadline.Token);
                            Assert.Equal(scenario.EndsWith("-valid", StringComparison.Ordinal), accepted);
                            Assert.False(await evidence.TryHandoffToGuiAsync(receiver, destination, lease, pipe, deadline.Token));
                            read.Dispose();
                            if (accepted) Assert.Throws<IOException>(() => File.Delete(handle.Plan.FinalPath));
                            Console.WriteLine("receiver-destination-gui=" + scenario);
                            return null;
                        }
                        preserveUntilShutdown = true;
                        var automaticHandoff = FrontierNativeReceiverTests.Automatic
                            ? Assert.IsAssignableFrom<Task<bool>>(FrontierNativeReceiverTests.Handoff(lifetime)) : null;
                        if (automaticHandoff is not null) Assert.False(automaticHandoff.IsCompleted);
                        Console.WriteLine("receiver-destination-handoff=" + scenario);
                        return () =>
                        {
                            if (automaticHandoff is not null)
                            {
                                Assert.True(automaticHandoff.IsCompleted);
                                Assert.False(automaticHandoff.GetAwaiter().GetResult());
                            }
                            Assert.Throws<ObjectDisposedException>(() => read.ReadByte());
                            Assert.Null(evidence.TryOpenRead(receiver, destination, lease));
                            using var released = File.Open(handle.Plan.FinalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            read.Dispose();
                        };
                    }
                }
                using var released = File.Open(handle.Plan.FinalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            Console.WriteLine("receiver-destination-handoff=" + scenario);
            return null;
        }
        finally
        {
            release.TrySetResult();
            receiver.ResetSessionState();
            if (!preserveUntilShutdown) evidence?.Dispose();
            handle?.Dispose();
        }
    }

    private static T Clone<T>(T value) where T : class
        => (T)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static async Task IgnoreCancellation(Task task)
    { try { await task; } catch (OperationCanceledException) { } }
}
