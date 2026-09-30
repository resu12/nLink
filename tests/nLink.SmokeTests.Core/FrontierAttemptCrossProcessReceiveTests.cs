using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLink.App.Services;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Tests;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierAttemptCrossProcessReceiveTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "request", "creation", "closed", "pipe" }
        select new object[] { host, "writer-source-read-cross-process-" + scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalApps_TransferHeld256MiBToNativeDestination(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-cross-process-receive-20260926",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-role-writers-ok", gui);
        Assert.Contains("attempt-source-custody-ok", gui);
        var outputs = Directory.GetFiles(Path.Combine(directory, "process-output"), "*.stdout.log");
        foreach (var role in new[] { "helpee", "helper" })
        {
            var app = await File.ReadAllTextAsync(Assert.Single(outputs.Where(p => Path.GetFileName(p).StartsWith(role + "-", StringComparison.Ordinal))));
            if (scenario.EndsWith("-valid", StringComparison.Ordinal))
            {
                var measurement = await File.ReadAllLinesAsync(Path.Combine(directory, "source-measurement.txt"));
                Assert.Contains("cross-process-" + role + "=268435456; sha256=" + measurement[1], app);
            }
            else Assert.Contains("cross-process-peer-rejected=" + role, app);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_DoesNotExposePeerProcessChannel(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            if($null -ne ('NLink.Tests.AttemptPeerProcessChannel' -as [type])) {throw 'Peer channel leaked'}
            if($null -ne ('NLink.Tests.AttemptNknPipeClient' -as [type])) {throw 'Offline NKN carrier leaked'}
            'peer-channel-default-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("peer-channel-default-ok", result.Stdout);
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task RunCrossProcessAttemptAsync(SessionFileTransferService.FrontierAppLifetime lifetime,
        string directory, string role, string signal, string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const long payloadBytes = 268435456;
        var lease = (LocalFrontierAppRequest.AppLease)lifetime.GetType().GetField("lease", flags)!.GetValue(lifetime)!;
        var clone = typeof(object).GetMethod("MemberwiseClone", flags)!;
        using var input = Console.OpenStandardInput();
        using var source = role == "helpee" ? await Task.Run(() => AttemptSenderSourceChannel.Receive(lease, input)) : null;
        if (role == "helpee") Assert.NotNull(source);
        Assert.Null(AttemptPeerProcessChannel.Receive((LocalFrontierAppRequest.AppLease)clone.Invoke(lease, null)!, Stream.Null));
        using var peer = await Task.Run(() => AttemptPeerProcessChannel.Receive(lease, input)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(AttemptPeerProcessChannel.Receive(lease, Stream.Null));
        if (scenario.EndsWith("-request", StringComparison.Ordinal) || scenario.EndsWith("-creation", StringComparison.Ordinal))
        {
            Assert.Null(peer);
            Assert.True(lease.IsCurrent);
            Console.WriteLine("cross-process-peer-rejected=" + role);
            return;
        }
        Assert.NotNull(peer);
        var peerCopy = (AttemptPeerProcessChannel)clone.Invoke(peer, null)!;
        Assert.False(peerCopy.IsCurrent); peerCopy.Dispose(); Assert.True(peer.IsCurrent);
        if (scenario.EndsWith("-closed", StringComparison.Ordinal))
        {
            ((IDisposable)peer.GetType().GetField("process", flags)!.GetValue(peer)!).Dispose();
            Assert.False(peer.IsCurrent); Assert.False(peer.IsCurrent);
            Console.WriteLine("cross-process-peer-rejected=" + role);
            return;
        }
        if (scenario.EndsWith("-pipe", StringComparison.Ordinal))
        {
            using var pipeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var name = "nlink-frontier-foreign-" + lease.RequestId.ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await Task.WhenAll(server.WaitForConnectionAsync(pipeDeadline.Token), client.ConnectAsync(pipeDeadline.Token));
            Assert.False(peer.MatchesPipe(role == "helpee" ? server.SafePipeHandle : client.SafePipeHandle, role == "helpee"));
            Assert.False(peer.IsCurrent); Assert.False(peer.IsCurrent);
            Console.WriteLine("cross-process-peer-rejected=" + role);
            return;
        }

        if (scenario.StartsWith("writer-source-read-cross-process-carrier-", StringComparison.Ordinal))
        {
            const string fullPrefix = "writer-source-read-cross-process-carrier-full-";
            var nativeCase = scenario.StartsWith(fullPrefix, StringComparison.Ordinal) ? scenario[fullPrefix.Length..] : null;
            await NknAccelerationTransportTests.RunCrossProcessCarrierAsync(lease, peer, directory, role, signal,
                nativeCase is not null ? source : null, nativeCase,
                frontierHold: scenario.EndsWith("-frontier-hold", StringComparison.Ordinal),
                frontierSchedule: scenario.EndsWith("-frontier-schedule", StringComparison.Ordinal) || scenario.EndsWith("-frontier-late-publication", StringComparison.Ordinal),
                latePublication: scenario.EndsWith("-frontier-late-publication", StringComparison.Ordinal));
            if (source is not null) Assert.Equal(nativeCase != "source-retired", source.IsCurrent);
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var ct = deadline.Token;
        using var hostReady = EventWaitHandle.OpenExisting(signal + ".cross-host-ready");
        using var verified = EventWaitHandle.OpenExisting(signal + ".destination-verified");
        CoreSmokeTestsBase.CreateDevLocalTestConfig();
        var ownAddress = "devlocal.frontier." + lease.RequestId.ToString("N");
        var peerAddress = "devlocal.frontier." + peer.PeerRequest.ToString("N");
        using var transport = new DevLocalTransport(ownAddress);
        using var runtime = new SessionRuntime(() => transport);
        var service = (SessionFileTransferService)typeof(SessionRuntime).GetField("fileTransferService", flags)!.GetValue(runtime)!;
        if (role == "helpee")
        {
            using var invitePipe = new NamedPipeServerStream("nlink-frontier-invite-" + lease.RequestId.ToString("N"),
                PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _ = CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(ownAddress), out var token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, boundHelperAddress: new PeerAddress(peerAddress));
            await runtime.StartHelpeeAsync(ct);
            hostReady.Set();
            await invitePipe.WaitForConnectionAsync(ct);
            Assert.True(peer.MatchesPipe(invitePipe.SafePipeHandle, server: true));
            var encoded = Encoding.UTF8.GetBytes(token);
            Assert.InRange(encoded.Length, 1, 16384);
            await invitePipe.WriteAsync(BitConverter.GetBytes(encoded.Length), ct);
            await invitePipe.WriteAsync(encoded, ct);
            await invitePipe.FlushAsync(ct);
            await AwaitAttemptAsync(() => runtime.PendingApprovalRequest is not null, ct);
        }
        else
        {
            Assert.True(await Task.Run(() => hostReady.WaitOne(10000), ct));
            using var invitePipe = new NamedPipeClientStream(".", "nlink-frontier-invite-" + peer.PeerRequest.ToString("N"),
                PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await invitePipe.ConnectAsync(ct);
            Assert.True(peer.MatchesPipe(invitePipe.SafePipeHandle, server: false));
            var prefix = new byte[sizeof(int)];
            await invitePipe.ReadExactlyAsync(prefix, ct);
            var length = BitConverter.ToInt32(prefix);
            Assert.InRange(length, 1, 16384);
            var encoded = new byte[length];
            await invitePipe.ReadExactlyAsync(encoded, ct);
            var resolved = InviteTokenServiceFactory.CreateDefaultResolver().Resolve(Encoding.UTF8.GetString(encoded), DateTimeOffset.UtcNow);
            Assert.True(resolved.IsValid); Assert.NotNull(resolved.Invite);
            Assert.Equal(new PeerAddress(peerAddress), resolved.Invite.TargetAddress);
            Assert.Equal(new PeerAddress(ownAddress), resolved.Invite.BoundHelperAddress);
            await runtime.StartHelperAsync(resolved.InviteTokenText!, resolved.Invite, ct);
        }
        var connectionGate = typeof(DevLocalTransport).GetField("activeConnectionGate", flags)!.GetValue(transport)!;
        PipeStream CurrentPipe()
        {
            lock (connectionGate)
            {
                var connection = typeof(DevLocalTransport).GetField("activeConnection", flags)!.GetValue(transport)!;
                return (PipeStream)connection.GetType().GetField("pipe", flags)!.GetValue(connection)!;
            }
        }
        var pipe = CurrentPipe();
        bool PeerCurrent() => peer.IsCurrent && ReferenceEquals(pipe, CurrentPipe()) && peer.MatchesPipe(pipe.SafePipeHandle, role == "helpee");
        Assert.True(PeerCurrent());
        if (role == "helpee") await runtime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.FileTransfer, ct);
        await AwaitAttemptAsync(() => runtime.State == SessionRuntimeState.Connected, ct);

        if (role == "helpee")
        {
            AttemptSourceReadStream? metadata = null, payload = null;
            int opens = 0;
            var transferId = "native-cross-" + lease.RequestId.ToString("N");
            await runtime.StartSendAsync(new FileTransferSendDescriptor("filetransfer-tuna-gui-payload.bin", payloadBytes, transferId), token =>
            {
                token.ThrowIfCancellationRequested(); Assert.True(PeerCurrent()); Assert.True(source!.IsCurrent);
                var ordinal = Interlocked.Increment(ref opens); Assert.InRange(ordinal, 1, 2);
                if (ordinal == 2) Assert.True(metadata!.Closed.Task.IsCompletedSuccessfully);
                var read = new AttemptSourceReadStream(Assert.IsType<FileStream>(source.OpenNextRead(lease)),
                    () => source.IsCurrent && PeerCurrent(), seekable: ordinal == 2);
                if (ordinal == 1) metadata = read; else payload = read;
                return Task.FromResult<Stream>(read);
            }, ct);
            await AwaitAttemptAsync(() => service.Snapshot.Outbound?.IsTerminal == true, ct);
            var complete = service.Snapshot.Outbound!;
            Assert.Equal(FileTransferTransferState.Completed, complete.State);
            Assert.Equal(4, complete.ProtocolVersion);
            Assert.Equal("regular_nkn_v4_fast", complete.RouteToken);
            Assert.Equal(payloadBytes, complete.BytesTransferred);
            Assert.Equal(Convert.ToBase64String(Convert.FromHexString(source!.Sha256)), complete.Sha256Base64);
            Assert.Equal(2, opens); Assert.NotNull(metadata); Assert.NotNull(payload);
            await metadata.Closed.Task.WaitAsync(ct); await payload.Closed.Task.WaitAsync(ct);
            Assert.Equal(payloadBytes, metadata.BytesRead); Assert.True(metadata.ReachedEnd);
            Assert.Equal(payloadBytes, payload.BytesRead);
            Assert.Null(source.OpenNextRead(lease)); Assert.True(source.IsCurrent);
            Assert.True(await Task.Run(() => verified.WaitOne(TimeSpan.FromSeconds(20)), ct));
            Assert.True(peer.IsCurrent); Assert.True(source.IsCurrent);
            Console.WriteLine("cross-process-helpee=268435456; sha256=" + source.Sha256 + "; transfer=" + complete.TransferId);
        }
        else
        {
            await AwaitAttemptAsync(() => service.Snapshot.InboundState == FileTransferTransferState.PendingDecision, ct);
            var offer = service.Snapshot.Inbound!;
            Assert.Equal(payloadBytes, offer.FileSizeBytes);
            Assert.Equal("native-cross-" + peer.PeerRequest.ToString("N"), offer.TransferId);
            var priorRoot = Environment.GetEnvironmentVariable("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT");
            Environment.SetEnvironmentVariable("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT", Path.Combine(directory, "native-received"));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalizing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.InboundFinalizationStartedForTests = task => finalizing.TrySetResult(task);
            FileTransferReceiveDestination? destination = null;
            FileTransferWriteHandle? handle = null;
            SessionFileTransferService.NativeDestinationEvidence? evidence = null;
            try
            {
                await service.AcceptIncomingTransferAsync(offer.TransferId, async (incoming, token) =>
                {
                    Assert.True(PeerCurrent());
                    destination = await runtime.OpenInboundFileTransferDestinationAsync(incoming, token);
                    handle = (FileTransferWriteHandle)destination.GetType().GetField("guardHandle", flags)!.GetValue(destination)!;
                    handle.WindowsAfterRenameForTests = () => { entered.TrySetResult(); return release.Task; };
                    return destination;
                }, ct);
                await entered.Task.WaitAsync(ct);
                Assert.NotNull(destination); Assert.NotNull(handle); Assert.True(PeerCurrent());
                Assert.True(OperationalLogNativeSource.TryDescribe(handle.Stream.SafeFileHandle, out var identity, out var length));
                Assert.Equal(payloadBytes, length);
                evidence = FrontierNativeReceiverTests.Automatic
                    ? Assert.IsType<SessionFileTransferService.NativeDestinationEvidence>(FrontierNativeReceiverTests.Field(lifetime, "nativeDestinationEvidence"))
                    : service.TryRequestNativeDestinationEvidence(destination);
                Assert.NotNull(evidence); Assert.Null(evidence.TryOpenRead(service, destination, lease));
                release.TrySetResult();
                await (await finalizing.Task.WaitAsync(ct)).WaitAsync(ct);
                var complete = service.Snapshot.Inbound!;
                Assert.Equal(FileTransferTransferState.Completed, complete.State);
                Assert.Equal(4, complete.ProtocolVersion); Assert.Equal("regular_nkn_v4_fast", complete.RouteToken);
                Assert.Equal(payloadBytes, complete.BytesTransferred);
                Assert.Equal(complete.SessionId, evidence.SessionId); Assert.Equal(offer.TransferId, evidence.TransferId);
                Assert.Equal(payloadBytes, evidence.ByteCount); Assert.Equal(complete.Sha256Base64, evidence.Sha256Base64);
                Assert.Throws<IOException>(() => File.Delete(handle.Plan.FinalPath));
                using (var read = Assert.IsAssignableFrom<Stream>(evidence.TryOpenRead(service, destination, lease)))
                {
                    Assert.Equal(identity, evidence.FileIdentity);
                    var hash = await SHA256.HashDataAsync(read, ct);
                    Assert.Equal(Convert.FromBase64String(complete.Sha256Base64!), hash);
                    Assert.Equal(-1, read.ReadByte()); Assert.True(PeerCurrent());
                    if (scenario.EndsWith("-gui-valid", StringComparison.Ordinal))
                    {
                        if (FrontierNativeReceiverTests.Automatic)
                        {
                            Assert.True(await Assert.IsAssignableFrom<Task<bool>>(FrontierNativeReceiverTests.Handoff(lifetime)).WaitAsync(ct));
                            Console.WriteLine("native-receiver-handoff-ok");
                        }
                        else
                        {
                            using var guiPipe = new NamedPipeClientStream(".", "nlink-destination-" + lease.RequestId.ToString("N"),
                                PipeDirection.InOut, PipeOptions.Asynchronous);
                            await guiPipe.ConnectAsync(ct);
                            Assert.True(await evidence.TryHandoffToGuiAsync(service, destination, lease, guiPipe, ct));
                            Assert.False(await evidence.TryHandoffToGuiAsync(service, destination, lease, guiPipe, ct));
                        }
                    }
                    Console.WriteLine("cross-process-helper=268435456; sha256=" + Convert.ToHexString(hash).ToLowerInvariant() +
                        "; transfer=" + complete.TransferId + "; native_identity=" + identity);
                }
                Assert.Null(evidence.TryOpenRead(service, destination, lease));
                if (scenario.EndsWith("-gui-valid", StringComparison.Ordinal))
                    Assert.Throws<IOException>(() => File.Delete(handle.Plan.FinalPath));
                else
                {
                    using var released = File.Open(handle.Plan.FinalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                verified.Set();
            }
            finally
            {
                release.TrySetResult(); evidence?.Dispose();
                Environment.SetEnvironmentVariable("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT", priorRoot);
            }
        }
    }

    private static async Task AwaitAttemptAsync(Func<bool> predicate, CancellationToken ct)
    { while (!predicate()) await Task.Delay(10, ct); }
}
