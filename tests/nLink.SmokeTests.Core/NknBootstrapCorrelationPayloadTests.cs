using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    public Task JoinedBootstrapCorrelation_RealFilesCompleteAfterFallback()
        => RunJoinedFallbackFilesAsync(64 * 1024);

    [Fact]
    public Task JoinedBootstrapCorrelation_Real256MiBFilesCompleteAfterFallback()
        => RunJoinedFallbackFilesAsync(256 * 1024 * 1024);

    private async Task RunJoinedFallbackFilesAsync(int byteCount)
    {
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "payload-fallback-completion-20260927",
            byteCount + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new JoinedFallbackFiles(directory, byteCount);
        await files.CreateSourceAsync();
        try
        {
            await RunJoinedBootstrapCorrelationAsync(runExchange: true, files: files);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "fallback-files-completed; bytes=" + byteCount);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), error.ToString());
            throw;
        }
    }

    // File-backed capability test only. These files do not carry the original
    // cross-process native payload custody and must never substitute for it.
    private sealed class JoinedFallbackFiles(string directory, int byteCount)
    {
        private readonly string sourcePath = Path.Combine(directory, "source.bin");
        private readonly string destinationPath = Path.Combine(directory, "destination.bin");
        private int sourceOpens;
        private int destinationOpens;
        private readonly TaskCompletionSource destinationClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ByteCount => byteCount;

        internal async Task CreateSourceAsync()
        {
            var block = Enumerable.Range(0, 32768).Select(i => (byte)(i % 251)).ToArray();
            await using var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            for (var written = 0; written < byteCount; written += block.Length)
                await source.WriteAsync(block.AsMemory(0, Math.Min(block.Length, byteCount - written)));
        }

        internal Stream OpenSource(Task release)
        {
            var ordinal = Interlocked.Increment(ref sourceOpens);
            // Metadata plus one reader for each coordinator-owned runtime:
            // Regular v4, Tuna v4 and the fallback v6 successor.
            Assert.InRange(ordinal, 1, 4);
            return new JoinedFallbackFileReadGate(sourcePath, ordinal == 1 ? Task.CompletedTask : release);
        }

        internal Stream OpenDestination()
        {
            Assert.Equal(1, Interlocked.Increment(ref destinationOpens));
            return new JoinedFallbackDestination(destinationPath, destinationClosed);
        }

        internal async Task VerifyAsync(FileTransferTransferSnapshot sender, FileTransferTransferSnapshot receiver,
            string session, string transfer, CancellationToken ct)
        {
            Assert.Equal(4, sourceOpens);
            Assert.Equal(1, destinationOpens);
            foreach (var snapshot in new[] { sender, receiver })
            {
                Assert.Equal(FileTransferTransferState.Completed, snapshot.State);
                Assert.Equal(session, snapshot.SessionId);
                Assert.Equal(transfer, snapshot.TransferId);
                Assert.Equal("post_tuna_fallback_v6", snapshot.RouteToken);
                Assert.Equal(6, snapshot.ProtocolVersion);
                Assert.Equal(byteCount, snapshot.FileSizeBytes);
                Assert.Equal(byteCount, snapshot.BytesTransferred);
            }
            await destinationClosed.Task.WaitAsync(ct);
            await using var source = File.OpenRead(sourcePath);
            await using var destination = File.OpenRead(destinationPath);
            Assert.Equal(byteCount, source.Length);
            Assert.Equal(byteCount, destination.Length);
            var sourceHash = await SHA256.HashDataAsync(source, ct);
            var destinationHash = await SHA256.HashDataAsync(destination, ct);
            Assert.Equal(sourceHash, destinationHash);
            Assert.Equal(Convert.ToBase64String(sourceHash), sender.Sha256Base64);
            Assert.Equal(sender.Sha256Base64, receiver.Sha256Base64);
            await File.WriteAllTextAsync(Path.Combine(directory, "sha256.txt"), Convert.ToHexString(sourceHash).ToLowerInvariant(), ct);
        }
    }

    private sealed class JoinedFallbackDestination(string path, TaskCompletionSource closed)
        : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) closed.TrySetResult();
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            closed.TrySetResult();
        }
    }

    private sealed class JoinedFallbackFileReadGate(string path, Task release)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await release.WaitAsync(ct);
            return await base.ReadAsync(buffer, ct);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await release.WaitAsync(ct);
            return await base.ReadAsync(buffer, offset, count, ct);
        }
    }
}
