using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class WindowsFileTransferDestinationTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardDestination_RetainsSameReadOnlyFileAcrossNativeRename(bool longUnicodePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture(longUnicodePath: longUnicodePath);
        var handle = fixture.Handle;
        var payload = Enumerable.Range(0, 2 * 1024 * 1024 + 23).Select(i => (byte)(i % 251)).ToArray();
        Assert.True(handle.Stream.IsAsync);
        handle.Stream.Position = payload.Length / 2;
        await handle.Stream.WriteAsync(payload.AsMemory(payload.Length / 2));
        handle.Stream.Position = 0;
        await handle.Stream.WriteAsync(payload.AsMemory(0, payload.Length / 2));
        await handle.Stream.FlushAsync();
        var writer = handle.Stream.SafeFileHandle;
        Assert.True(OperationalLogNativeSource.TryDescribe(writer, out var identity, out var length));
        Assert.Equal(payload.Length, length);
        AssertProtected(handle.Plan.TempPath);
        handle.WindowsAfterRenameForTests = () =>
        {
            Assert.False(writer.IsClosed);
            Assert.False(File.Exists(handle.Plan.TempPath));
            Assert.True(OperationalLogNativeSource.TryDescribe(writer, out var renamedIdentity, out _));
            Assert.Equal(identity, renamedIdentity);
            AssertProtected(handle.Plan.FinalPath);
            return Task.CompletedTask;
        };

        await handle.FinalizeAsync(CancellationToken.None);
        var retained = Retained(handle);
        Assert.True(writer.IsClosed);
        Assert.False(File.Exists(handle.Plan.TempPath));
        Assert.True(OperationalLogNativeSource.TryDescribe(retained, out var finalIdentity, out var finalLength));
        Assert.Equal(identity, finalIdentity);
        Assert.Equal(payload.Length, finalLength);
        Assert.Equal(payload, ReadMapped(retained, payload.Length));
        // An async duplicate still belongs to the original I/O completion port.
        // Mapping avoids rebinding it or changing its shared file position.
        using var writable = CreateFileMapping(retained, IntPtr.Zero, 4, 0, 0, null);
        Assert.True(writable.IsInvalid);
        Assert.Equal(5, Marshal.GetLastPInvokeError());
        Assert.False(SetFileInformationByHandle(retained, 4, [1], 1));
        Assert.Equal(5, Marshal.GetLastPInvokeError());
        AssertProtected(handle.Plan.FinalPath);
        await handle.FinalizeAsync(CancellationToken.None);
        Assert.Same(retained, Retained(handle));
        await handle.DisposeAsync();
        handle.Dispose();
        Assert.True(retained.IsClosed);
        Assert.Equal(payload, await File.ReadAllBytesAsync(handle.Plan.FinalPath));
        using var released = File.Open(handle.Plan.FinalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuardDestination_LateCollisionPreservesBothArtifacts(bool directory, bool overwrite)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture(overwrite);
        var handle = fixture.Handle;
        await handle.Stream.WriteAsync("received"u8.ToArray());
        if (directory) Directory.CreateDirectory(handle.Plan.FinalPath);
        else await File.WriteAllTextAsync(handle.Plan.FinalPath, "existing");
        await Assert.ThrowsAsync<IOException>(() => handle.FinalizeAsync(CancellationToken.None));
        Assert.Null(RetainedOrNull(handle));
        Assert.False(Finalized(handle));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle.FinalizeAsync(CancellationToken.None));
        await handle.DisposeAsync();
        Assert.Equal("received", await File.ReadAllTextAsync(handle.Plan.TempPath));
        if (directory) Assert.True(Directory.Exists(handle.Plan.FinalPath));
        else Assert.Equal("existing", await File.ReadAllTextAsync(handle.Plan.FinalPath));
    }

    [Fact]
    public async Task GuardDestination_ExplicitOverwriteReplacesOnlyTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture(overwrite: true);
        var handle = fixture.Handle;
        await File.WriteAllTextAsync(handle.Plan.FinalPath, "old");
        await handle.Stream.WriteAsync("new"u8.ToArray());
        await handle.FinalizeAsync(CancellationToken.None);
        Assert.Equal("new"u8.ToArray(), ReadMapped(Retained(handle), 3));
        AssertProtected(handle.Plan.FinalPath);
        handle.Dispose();
        Assert.Equal("new", await File.ReadAllTextAsync(handle.Plan.FinalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardDestination_CancelBeforeFinalizeDeletesOnlyOwnedTemporaryFile(bool asyncDispose)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture();
        var handle = fixture.Handle;
        await handle.Stream.WriteAsync("partial"u8.ToArray());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.FinalizeAsync(canceled.Token));
        Assert.Null(RetainedOrNull(handle));
        if (asyncDispose) await handle.DisposeAsync(); else handle.Dispose();
        handle.Dispose();
        Assert.False(File.Exists(handle.Plan.TempPath));
        Assert.False(File.Exists(handle.Plan.FinalPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.FinalizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GuardDestination_ExternallyClosedWriterDoesNotDeleteReusedTemporaryPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture();
        var handle = fixture.Handle;
        await handle.Stream.WriteAsync("original"u8.ToArray());
        await handle.Stream.DisposeAsync();
        var moved = Path.Combine(fixture.Root, "original.part");
        File.Move(handle.Plan.TempPath, moved);
        await File.WriteAllTextAsync(handle.Plan.TempPath, "replacement");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.FinalizeAsync(CancellationToken.None));
        handle.Dispose();
        Assert.Equal("replacement", await File.ReadAllTextAsync(handle.Plan.TempPath));
        Assert.Equal("original", await File.ReadAllTextAsync(moved));
        Assert.False(File.Exists(handle.Plan.FinalPath));
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("close-writer")]
    public async Task GuardDestination_PostRenameFailureNeverDeletesFinalOrReusedTemporaryPath(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture();
        var handle = fixture.Handle;
        using var cancellation = new CancellationTokenSource();
        await handle.Stream.WriteAsync("received"u8.ToArray());
        handle.WindowsAfterRenameForTests = () =>
        {
            File.WriteAllText(handle.Plan.TempPath, "replacement");
            if (fault == "throw") throw new IOException("injected_after_rename");
            if (fault == "cancel") cancellation.Cancel();
            if (fault == "close-writer") handle.Stream.Dispose();
            return Task.CompletedTask;
        };
        if (fault == "throw") await Assert.ThrowsAsync<IOException>(() => handle.FinalizeAsync(cancellation.Token));
        else if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.FinalizeAsync(cancellation.Token));
        else await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.FinalizeAsync(cancellation.Token));
        Assert.False(Finalized(handle));
        Assert.Null(RetainedOrNull(handle));
        handle.Dispose();
        Assert.Equal("received", await File.ReadAllTextAsync(handle.Plan.FinalPath));
        Assert.Equal("replacement", await File.ReadAllTextAsync(handle.Plan.TempPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardDestination_DisposalWaitsForNativeFinalization(bool asyncDispose)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture();
        var handle = fixture.Handle;
        await handle.Stream.WriteAsync("received"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handle.WindowsAfterRenameForTests = () => { entered.TrySetResult(); return release.Task; };
        var finalizing = handle.FinalizeAsync(CancellationToken.None);
        Task disposing = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (asyncDispose) disposing = handle.DisposeAsync().AsTask();
            else
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                disposing = Task.Run(() => { started.TrySetResult(); handle.Dispose(); });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            Assert.False(disposing.IsCompleted);
            AssertProtected(handle.Plan.FinalPath);
        }
        finally { release.TrySetResult(); await Task.WhenAll(finalizing, disposing).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(Finalized(handle));
        Assert.Null(RetainedOrNull(handle));
        Assert.Equal("received", await File.ReadAllTextAsync(handle.Plan.FinalPath));
    }

    [Fact]
    public async Task GuardDestination_ConcurrentFinalizationRunsOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture();
        var handle = fixture.Handle;
        await handle.Stream.WriteAsync("received"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        handle.WindowsAfterRenameForTests = () => { Interlocked.Increment(ref calls); entered.TrySetResult(); return release.Task; };
        var first = handle.FinalizeAsync(CancellationToken.None);
        Task second = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            second = handle.FinalizeAsync(CancellationToken.None);
            Assert.False(second.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, calls);
        Assert.Equal("received"u8.ToArray(), ReadMapped(Retained(handle), 8));
    }

    [Fact]
    public async Task ExternalStreamConstructor_KeepsLegacyBehaviorWithoutNativeRetention()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DestinationFixture();
        var plan = fixture.Handle.Plan;
        fixture.Handle.Dispose();
        using var legacy = new FileTransferWriteHandle(plan, new FileStream(plan.TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
        await legacy.Stream.WriteAsync("legacy"u8.ToArray());
        await legacy.FinalizeAsync(CancellationToken.None);
        Assert.Null(RetainedOrNull(legacy));
        Assert.Equal("legacy", await File.ReadAllTextAsync(plan.FinalPath));
    }

    [Fact]
    public async Task GuardDestination_RealServiceCompletesAndReleasesNativeHold()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string sessionId = "native-destination", transferId = "native-guard-service";
        var bytes = Enumerable.Range(0, 2 * 1024 * 1024 + 23).Select(i => (byte)(i % 251)).ToArray();
        using var outgoing = new LoopbackFileTransferTransport(sessionId);
        using var incoming = new LoopbackFileTransferTransport(sessionId);
        outgoing.Connect(incoming);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(outgoing);
        receiver.AttachTransport(incoming);
        DestinationFixture? fixture = null;
        var completedAccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TransferChanged += (_, args) =>
        {
            if (args.Snapshot.InboundState != FileTransferTransferState.Completed) return;
            try
            {
                using var opened = File.Open(fixture!.Handle.Plan.FinalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                completedAccess.TrySetResult();
            }
            catch (Exception ex) { completedAccess.TrySetException(ex); }
        };
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("received.bin", bytes.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (offer, _) =>
            {
                Assert.Equal(sessionId, offer.SessionId);
                Assert.Equal(transferId, offer.TransferId);
                Assert.Null(fixture);
                fixture = new DestinationFixture();
                var handle = fixture.Handle;
                return Task.FromResult(FileTransferReceiveDestination.FromGuardOwnedHandle(handle));
            }, CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Completed &&
                sender.Snapshot.OutboundState == FileTransferTransferState.Completed, timeoutMs: 10000);
            Assert.NotNull(fixture);
            await completedAccess.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => RetainedOrNull(fixture.Handle) is null);
            Assert.Equal(fixture.Handle.Plan.FinalPath, receiver.Snapshot.Inbound!.SavedFilePath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Handle.Plan.FinalPath));
            Assert.False(File.Exists(fixture.Handle.Plan.TempPath));
        }
        finally { fixture?.Dispose(); }
    }

    private static SafeFileHandle Retained(FileTransferWriteHandle handle)
        => Assert.IsType<SafeFileHandle>(RetainedOrNull(handle));

    private static object? RetainedOrNull(FileTransferWriteHandle handle)
        => typeof(FileTransferWriteHandle).GetField("windowsRetainedRead", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(handle);

    private static bool Finalized(FileTransferWriteHandle handle)
        => (bool)typeof(FileTransferWriteHandle).GetField("finalized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handle)!;

    private static byte[] ReadMapped(SafeFileHandle file, int length)
    {
        using var map = CreateFileMapping(file, IntPtr.Zero, 2, 0, 0, null);
        Assert.False(map.IsInvalid);
        var view = MapViewOfFile(map, 4, 0, 0, (nuint)length);
        Assert.NotEqual(IntPtr.Zero, view);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(view, bytes, 0, length);
            return bytes;
        }
        finally { Assert.True(UnmapViewOfFile(view)); }
    }

    private static void AssertProtected(string path)
    {
        Assert.Throws<IOException>(() => { using var file = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
        Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
        Assert.Throws<IOException>(() => File.Delete(path));
    }

    private sealed class DestinationFixture : IDisposable
    {
        internal string Root { get; }
        internal FileTransferWriteHandle Handle { get; }
        internal DestinationFixture(bool overwrite = false, bool longUnicodePath = false)
        {
            Root = Path.Combine(FindRepoRoot(), "artifacts", "windows-destination-lifetime-20260926", Guid.NewGuid().ToString("N"));
            if (longUnicodePath) Root = Path.Combine(Root, new string('x', 100), new string('y', 100));
            Directory.CreateDirectory(Root);
            var now = DateTimeOffset.UtcNow;
            var session = new SessionId("native-destination");
            var helper = new PeerAddress("native.destination.helper");
            var grant = new SessionGrant(helper, CapabilityGrant.FileTransfer, session, now.AddMinutes(5));
            var state = (SessionSecurityState.Empty with
            {
                SessionId = session, HelpeeAddress = new PeerAddress("native.destination.helpee"),
                HelperAddress = helper, InviteValidated = true,
            }).WithHandshakeVerified(helper).WithApproval(grant);
            var result = new SessionFileTransferGuard(() => now).OpenReceiveWriteStream(true, state, grant,
                new FileTransferDescriptor(session, helper, longUnicodePath ? "príjem-測試.bin" : "received.bin", 2 * 1024 * 1024 + 23),
                new FileTransferStoragePolicy(Root, AllowOverwrite: overwrite));
            Assert.True(result.IsAllowed, result.Access.Message);
            Handle = Assert.IsType<FileTransferWriteHandle>(result.Handle);
        }
        public void Dispose() => Handle.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileMapping(SafeFileHandle file, IntPtr security, uint protection, uint high, uint low, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(SafeFileHandle mapping, uint access, uint high, uint low, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int information, byte[] data, uint bytes);
}
