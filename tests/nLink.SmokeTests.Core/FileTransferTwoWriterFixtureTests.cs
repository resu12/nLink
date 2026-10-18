using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class FileTransferTwoWriterFixtureTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task TwoWriterFixture_SenderFailureIsReportedWhileReceiverIsPending()
    {
        var receiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("original sender failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NknFileTransferTransportTests.SuperviseTwoWriterPeerAsync(receiver.Task, Task.CompletedTask,
                () => Task.FromException(failure)));
        Assert.Same(failure, actual);
        Assert.False(receiver.Task.IsCompleted);
    }

    [Fact]
    public async Task TwoWriterFixture_SuccessfulSenderStillRequiresReceiverCompletion()
    {
        var receiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedPeer = 0;
        var supervised = NknFileTransferTransportTests.SuperviseTwoWriterPeerAsync(receiver.Task, Task.CompletedTask,
            () => { checkedPeer++; return Task.CompletedTask; });
        Assert.False(supervised.IsCompleted);
        receiver.SetResult();
        await supervised;
        Assert.Equal(1, checkedPeer);
    }

    [Fact]
    public async Task TwoWriterFixture_LocalFailureDoesNotWaitForPeerExit()
    {
        var peer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("original receiver failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NknFileTransferTransportTests.SuperviseTwoWriterPeerAsync(Task.FromException(failure), peer.Task,
                () => throw new InvalidOperationException("peer must not be checked before exit")));
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoWriterFixture_PipeFailureIsReportedWhileFrameIsPending(bool fault)
    {
        var frame = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("original packet-pump failure");
        var pending = NknFileTransferTransportTests.AwaitTwoWriterPipeAsync(frame.Task,
            fault ? Task.FromException(failure) : Task.CompletedTask, "first authenticated yield", CancellationToken.None);
        if (fault) { Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending)); }
        else
        {
            var actual = await Assert.ThrowsAsync<EndOfStreamException>(() => pending);
            Assert.Contains("first authenticated yield", actual.Message, StringComparison.Ordinal);
        }
        Assert.False(frame.Task.IsCompleted);
    }

    [Fact]
    public async Task TwoWriterFixture_PipeClosurePreservesOriginalSenderFailure()
    {
        var peer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderFailure = new InvalidOperationException("original sender assertion");
        var supervised = NknFileTransferTransportTests.SuperviseTwoWriterPeerAsync(
            Task.FromException(new EndOfStreamException("first authenticated yield")), peer.Task,
            () => Task.FromException(senderFailure));
        Assert.False(supervised.IsCompleted);
        peer.SetResult();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => supervised);
        Assert.Same(senderFailure, actual);
    }

    [Fact]
    public async Task TwoWriterFixture_PipeClosureWithSuccessfulSenderRemainsFailure()
    {
        var closure = new EndOfStreamException("first authenticated yield");
        var actual = await Assert.ThrowsAsync<EndOfStreamException>(() =>
            NknFileTransferTransportTests.SuperviseTwoWriterPeerAsync(Task.FromException(closure), Task.CompletedTask,
                () => Task.CompletedTask));
        Assert.Same(closure, actual);
    }

    [Fact]
    public async Task TwoWriterFixture_PipeClosureRetainsCauseWhenOriginalDeadlineExpires()
    {
        var closure = new EndOfStreamException("first authenticated yield");
        var actual = await Assert.ThrowsAsync<IOException>(() =>
            NknFileTransferTransportTests.SuperviseTwoWriterPeerAsync(Task.FromException(closure),
                Task.FromCanceled(new CancellationToken(canceled: true)), () => Task.CompletedTask));
        Assert.Same(closure, actual.InnerException);
    }

    [Fact]
    public async Task TwoWriterFixture_CompletedReceivePreservesExactObject()
    {
        var frame = new object();
        Assert.Same(frame, await NknFileTransferTransportTests.AwaitTwoWriterPipeAsync(
            Task.FromResult(frame), Task.CompletedTask, "yield", CancellationToken.None));
    }

    [Fact]
    public void TwoWriterFixture_FailedPeerEvidenceSurvivesParentCleanup()
    {
        var root = EvidenceDirectory();
        var peers = Path.Combine(root, "peers");
        Directory.CreateDirectory(peers);
        File.WriteAllText(Path.Combine(peers, "two-writer-failure.txt"), "original fixture failure");
        File.WriteAllText(Path.Combine(peers, "sender-test-stderr.log"), "original sender diagnostic");
        TryDeleteDirectory(root);
        Assert.Equal("original sender diagnostic", File.ReadAllText(Path.Combine(peers, "sender-test-stderr.log")));
        var disposable = Path.Combine(root, "successful-fixture");
        Directory.CreateDirectory(disposable);
        TryDeleteDirectory(disposable);
        Assert.False(Directory.Exists(disposable));
    }

    [Theory]
    [InlineData("independent_pairs")]
    [InlineData("independent_missing_receive")]
    public async Task TwoWriterFixture_GeneratedRouteCycleRetainsOriginalPeerEvidence(string payloadMode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var peers = Path.Combine(EvidenceDirectory(), "peers");
        var source = await GenerateRouteCycleSourceAsync(FindRepoRoot(), peers, payloadMode);
        var original = new[] { "nlink.log", "sender.log" }
            .SelectMany(name => File.ReadAllLines(Path.Combine(peers, name))).ToArray();
        Assert.NotEmpty(original);
        Assert.Equal(original.Order(StringComparer.Ordinal), File.ReadAllLines(source).Order(StringComparer.Ordinal));
        using var sender = JsonDocument.Parse(File.ReadAllText(Path.Combine(peers, "sender-process.json")));
        using var receiver = JsonDocument.Parse(File.ReadAllText(Path.Combine(peers, "receiver-process.json")));
        Assert.NotEqual(sender.RootElement.GetProperty("ProcessId").GetInt32(), receiver.RootElement.GetProperty("ProcessId").GetInt32());
        Assert.False(File.Exists(Path.Combine(peers, "two-writer-failure.txt")));
        foreach (var name in new[] { "sender-test-stdout.log", "sender-test-stderr.log", "receiver-test-stdout.log", "receiver-test-stderr.log" })
        {
            Assert.True(File.Exists(Path.Combine(peers, name)), name);
        }
    }

    private static string EvidenceDirectory()
        => Path.Combine(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR") ??
            Path.Combine(FindRepoRoot(), "artifacts", "two-writer-fixture-tests"), Guid.NewGuid().ToString("N"));
}
