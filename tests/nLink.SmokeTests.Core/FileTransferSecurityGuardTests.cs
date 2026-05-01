using System.IO;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferSecurityGuardTests
{
    [Fact]
    public void AuthorizeSend_DeniesWhenFileTransferCapabilityMissing()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.Chat);
        var grant = new SessionGrant(
            state.HelperAddress!.Value,
            CapabilityGrant.Chat,
            state.SessionId!.Value,
            nowUtc.AddMinutes(5));

        var result = guard.AuthorizeSend(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant);

        Assert.False(result.IsAllowed);
        Assert.Equal(FileTransferValidationFailure.AuthorizationDenied, result.Failure);
        Assert.Equal(SessionAuthorizationFailure.CapabilityMissing, result.AuthorizationFailure);
    }

    [Fact]
    public void OpenReceiveWriteStream_RejectsInvalidFileName()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "bad:name.txt",
                    128),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.InvalidFileName, result.Access.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void OpenReceiveWriteStream_RejectsPathTraversal()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "..\\escape.txt",
                    128),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.PathTraversalDetected, result.Access.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void OpenReceiveWriteStream_RejectsReservedDeviceName()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "CON.txt",
                    128),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.InvalidFileName, result.Access.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void OpenReceiveWriteStream_RejectsOversizedFile()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "large.bin",
                    FileTransferStoragePolicy.DefaultMaxFileSizeBytes + 1),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.FileTooLarge, result.Access.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void ValidateReceiveMetadata_AllowsDefaultTwentyFiveGiBFileSize()
    {
        const long expectedDefaultMaxFileSizeBytes = 25L * 1024 * 1024 * 1024;
        const int currentDefaultV4ChunkSizeBytes = 21 * 1024;
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            Assert.Equal(expectedDefaultMaxFileSizeBytes, FileTransferStoragePolicy.DefaultMaxFileSizeBytes);
            Assert.True(
                (FileTransferStoragePolicy.DefaultMaxFileSizeBytes + currentDefaultV4ChunkSizeBytes - 1) /
                currentDefaultV4ChunkSizeBytes <= FileTransferProtocol.MaxChunkCountV4);

            var result = guard.ValidateReceiveMetadata(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "large-allowed.bin",
                    FileTransferStoragePolicy.DefaultMaxFileSizeBytes),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.True(result.IsAllowed, result.Message);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void ValidateReceiveMetadata_RejectsSessionIdMismatch()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.ValidateReceiveMetadata(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    new SessionId("other_file_transfer_session"),
                    state.HelperAddress!.Value,
                    "safe.txt",
                    128),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.SessionIdMismatch, result.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void ValidateReceiveMetadata_RejectsHelperIdentityMismatch()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.ValidateReceiveMetadata(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    new PeerAddress("unexpected.helper"),
                    "safe.txt",
                    128),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.HelperIdentityMismatch, result.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public void ValidateChunk_RejectsOversizedChunk()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.ValidateChunk(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferChunkDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "chunked.bin",
                    4096,
                    FileTransferStoragePolicy.DefaultMaxChunkSizeBytes + 1),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.False(result.IsAllowed);
            Assert.Equal(FileTransferValidationFailure.ChunkTooLarge, result.Failure);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task OpenReceiveWriteStream_CreatesFileWithinAllowedRoot_AndNumbersDuplicateName()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var first = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "report.txt",
                    5),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.True(first.IsAllowed);
            Assert.NotNull(first.Plan);
            Assert.NotNull(first.Handle);
            Assert.StartsWith(
                Path.GetFullPath(tempRoot) + Path.DirectorySeparatorChar,
                first.Plan!.FinalPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Assert.EndsWith(".part", first.Plan.TempPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(first.Plan.FinalPath));

            await using (first.Handle!)
            {
                Assert.True(first.Handle.Stream.CanRead);
                Assert.True(first.Handle.Stream.CanSeek);
                Assert.True(first.Handle.Stream.CanWrite);
                await first.Handle.Stream.WriteAsync("hello"u8.ToArray());
                Assert.True(File.Exists(first.Plan.TempPath));
                Assert.False(File.Exists(first.Plan.FinalPath));
                await first.Handle.FinalizeAsync(CancellationToken.None);
            }

            Assert.True(File.Exists(first.Plan.FinalPath));
            Assert.False(File.Exists(first.Plan.TempPath));

            var second = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "report.txt",
                    5),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.True(second.IsAllowed);
            Assert.NotNull(second.Plan);
            Assert.NotNull(second.Handle);
            Assert.Equal("report (1).txt", second.Plan!.SafeFileName);

            await using (second.Handle!)
            {
                await second.Handle.Stream.WriteAsync("world"u8.ToArray());
                await second.Handle.FinalizeAsync(CancellationToken.None);
            }

            Assert.True(File.Exists(Path.Combine(tempRoot, "report.txt")));
            Assert.True(File.Exists(Path.Combine(tempRoot, "report (1).txt")));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task FinalizeAsync_PreservesTempArtifact_WhenFinalPathAppearsBeforeMove()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionFileTransferGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.FileTransfer);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.FileTransfer);
        var tempRoot = CreateTempRoot();

        try
        {
            var result = guard.OpenReceiveWriteStream(
                hasSecurityTransport: true,
                securityState: state,
                grant: grant,
                descriptor: new FileTransferDescriptor(
                    state.SessionId!.Value,
                    state.HelperAddress!.Value,
                    "late-collision.txt",
                    5),
                storagePolicy: new FileTransferStoragePolicy(tempRoot));

            Assert.True(result.IsAllowed);
            Assert.NotNull(result.Plan);
            Assert.NotNull(result.Handle);

            await using var handle = result.Handle!;
            await handle.Stream.WriteAsync("hello"u8.ToArray());
            File.WriteAllText(result.Plan!.FinalPath, "existing");

            var ex = await Assert.ThrowsAsync<IOException>(() => handle.FinalizeAsync(CancellationToken.None));
            Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.Plan.FinalPath));
            Assert.True(File.Exists(result.Plan.TempPath));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static SessionSecurityState CreateApprovedSecurityState(DateTimeOffset nowUtc, CapabilityGrant capabilities)
    {
        var sessionId = new SessionId("file_transfer_guard_session");
        var helpeeAddress = new PeerAddress("file.transfer.helpee");
        var helperAddress = new PeerAddress("file.transfer.helper");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(
              helperAddress,
              capabilities,
              sessionId,
              nowUtc.AddMinutes(5)));
    }

    private static SessionGrant CreateGrant(SessionSecurityState state, DateTimeOffset nowUtc, CapabilityGrant capabilities)
    {
        return new SessionGrant(
            state.HelperAddress!.Value,
            capabilities,
            state.SessionId!.Value,
            nowUtc.AddMinutes(5));
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
