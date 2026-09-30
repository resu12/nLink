using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [Trait("FileTransferRuntimeSuite", "CompletionBoundary")]
    [InlineData("session")]
    [InlineData("runtime")]
    [InlineData("publication")]
    [InlineData("leg")]
    [InlineData("destination")]
    [InlineData("lifetime_canceled")]
    public async Task FinalizationSource_LateSuccessCannotMutateChangedScope(string invalidation)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var fileSize = GetPrivateProperty<long>(fixture.Context, "FileSizeBytes");
        fixture.Stream.SetLength(fileSize);
        SetPrivateProperty(fixture.Context, "BytesTransferred", fileSize);
        SetPrivateProperty(fixture.Context, "NextChunkIndex", GetPrivateProperty<int>(fixture.Context, "ChunkCount"));
        SetPrivateProperty(fixture.Context, "Sha256Base64", Convert.ToBase64String(SHA256.HashData(new byte[fileSize])));
        SetPrivateProperty(fixture.Context, "Hash", IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var destination = new FileTransferReceiveDestination(fixture.Stream,
            _ => { entered.TrySetResult(); return release.Task; }, finalPath: "modeled-final-path");
        SetPrivateProperty(fixture.Context, "WriteDestination", destination);
        // Exercise finalization without bootstrap write binding: the new scope must
        // reject this continuation independently of the existing bootstrap guard.
        var finalizing = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("FinalizeInboundTransferAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, CancellationToken.None]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            switch (invalidation)
            {
                case "session": fixture.ReplaceSession(); break;
                case "runtime":
                    var owner = GetPrivateProperty<RuntimeOwnerToken>(fixture.Context, "CurrentRuntimeOwner");
                    SetPrivateProperty(fixture.Context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
                    SetPrivateProperty(fixture.Context, "LastRuntimeOwnerGeneration", owner.RuntimeGeneration + 1);
                    break;
                case "publication":
                    Assert.NotNull(GetPrivatePropertyValue(fixture.Context, "CurrentRoutePublicationToken"));
                    SetPrivateProperty(fixture.Context, "CurrentRoutePublicationToken", null);
                    break;
                case "leg":
                    var leg = GetPrivateProperty<FileTransferLeg>(fixture.Context, "CurrentTransferLeg");
                    SetPrivateProperty(leg, "Generation", leg.Generation + 1);
                    break;
                case "destination":
                    SetPrivateProperty(fixture.Context, "WriteDestination", new FileTransferReceiveDestination(fixture.Stream));
                    break;
                case "lifetime_canceled":
                    GetPrivateProperty<CancellationTokenSource>(fixture.Context, "LifetimeCts").Cancel();
                    break;
            }
            release.TrySetResult();
            await finalizing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(destination.IsFinalized); // Completed physical work is not rolled back.
            Assert.Null(GetPrivatePropertyValue(fixture.Context, "SavedFilePath"));
            Assert.False(GetPrivateProperty<bool>(fixture.Context, "IsTerminal"));
            Assert.DoesNotContain(fixture.Session.SentFrames, frame =>
                frame is FileTransferStateFrameV4 { TerminalReady: true } or FileTransferCompleteFrameV4);
            Assert.Empty(fixture.Replacement.SentFrames);
        }
        finally { release.TrySetResult(); await finalizing.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
