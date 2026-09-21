using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    private static byte[] HashDurableFixtureFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    private static async Task ContinueSecondTunaToDurableFallbackAsync(SessionFileTransferService service, object context,
        InitialBootstrapCheckpointTransport bridge, InitialBootstrapRecordingDataSession data,
        FileStream destination, string destinationPath,
        Func<IReadOnlyList<FileTransferRouteHintNotification>, string?, Task> prepareReceive, object previousExecution)
    {
        var previousPublication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        var previousOwner = previousPublication.RuntimeOwner;
        var (proof, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, data, fallbackEpoch: 2);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, proof);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        await WaitUntilAsync(() => GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution") is null, 5000);
        var publication = AssertDurableFallbackPublication(context, previousPublication);
        var hints = bridge.ObservedRouteHints.Where(item => item.PublicationAuthority?.LiveRouteEpoch == 4).ToArray();
        Assert.Contains(hints, item => item.FallbackLegCanSendData);
        Assert.All(hints, item => Assert.Equal(publication.PublicationGeneration, item.PublicationAuthority!.RoutePublicationGeneration));
        var contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract");
        var repairId = contract is null ? null : GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority").ProtocolRepairRequestId;
        Assert.Same(data, GetPrivatePropertyValue(context, "DataSession"));
        var sessionGeneration = GetPrivateProperty<long>(context, "DataSessionGeneration");
        await prepareReceive(hints, repairId);
        var received = await data.ReceiveWithMetadataAsync(GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Token);
        Assert.Same(data, GetPrivatePropertyValue(context, "DataSession"));
        Assert.Equal(sessionGeneration, GetPrivateProperty<long>(context, "DataSessionGeneration"));
        Assert.Equal(FileTransferTransportKind.RegularNkn, received.TransportKind);
        var batch = Assert.IsType<FileTransferChunkBatchFrameV6>(received.Frame);
        Assert.Equal(data.SessionId, batch.SessionId);
        Assert.Equal(data.TransferId, batch.TransferId);
        Assert.Equal(100, batch.StartChunkIndex);
        Assert.Equal(2, batch.ChunkCount);
        Assert.Equal(proof.TransportEpoch, batch.TransportEpoch);
        var beforeBytes = GetPrivateProperty<long>(context, "BytesTransferred");
        var beforeFrontier = GetPrivateProperty<int>(context, "NextChunkIndex");
        Assert.Equal(100, beforeFrontier);
        Assert.Equal(100L * 21 * 1024, beforeBytes);
        await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("HandleInboundV6ChunkBatchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, batch, received.TransportKind]));
        Assert.Equal(102, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Equal(beforeBytes + 42 * 1024, GetPrivateProperty<long>(context, "BytesTransferred"));
        Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Empty(GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite"));
        Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        destination.Flush(flushToDisk: true);
        var written = new byte[42 * 1024];
        using (var reopened = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            reopened.Seek(100L * 21 * 1024, SeekOrigin.Begin);
            await reopened.ReadExactlyAsync(written);
        }
        Assert.Equal(batch.DataSegments.SelectMany(bytes => bytes), written);
        Assert.Equal(Enumerable.Repeat((byte)17, 21 * 1024).Concat(Enumerable.Repeat((byte)34, 21 * 1024)), written);
        var afterWrite = SecondTunaStateFingerprint(context);
        var fileHash = HashDurableFixtureFile(destinationPath);
        await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("RecheckInboundFileTransferRouteTransitionDrainAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, previousExecution]));
        Assert.Equal(afterWrite, SecondTunaStateFingerprint(context));
        Assert.Equal(fileHash, HashDurableFixtureFile(destinationPath));
        Assert.Equal(102, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Equal(beforeBytes + 42 * 1024, GetPrivateProperty<long>(context, "BytesTransferred"));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(destinationPath)!, "service-durable-progress.json"),
            JsonSerializer.Serialize(new
            {
                PreviousEpoch = previousPublication.LiveRouteEpoch, Epoch = publication.LiveRouteEpoch,
                Publication = publication.PublicationGeneration, Protocol = publication.ProtocolVersion,
                OwnerChanged = previousOwner != publication.RuntimeOwner, BeforeFrontier = beforeFrontier, AfterFrontier = 102,
                BeforeBytes = beforeBytes, AfterBytes = GetPrivateProperty<long>(context, "BytesTransferred"),
                WrittenRangeSha256 = Convert.ToHexString(SHA256.HashData(written)).ToLowerInvariant(),
                FlushedAndReopened = true, WholeTransferCompleted = false,
                BoundSessionGeneration = sessionGeneration, StaleTunaDrainIgnored = true,
            }));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static RoutePublicationToken AssertDurableFallbackPublication(object context, RoutePublicationToken previous)
    {
        var state = CreateCoordinatorStateForTest(context);
        var operation = state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        var publication = state.CurrentRoutePublicationToken!.Value;
        Assert.Equal(FileTransferRouteTransitionState.Committed, operation.State);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, operation.DispositionReceipt!.Value.Status);
        Assert.Equal(publication, operation.DispositionCommand!.Value.RoutePublicationToken);
        Assert.Equal(publication.RuntimeOwner, state.CurrentRuntimeOwner);
        Assert.Equal("post_tuna_fallback_v6", publication.RouteToken);
        Assert.Equal(6, publication.ProtocolVersion);
        Assert.Equal(4, publication.LiveRouteEpoch);
        Assert.Equal(previous.PublicationGeneration + 1, publication.PublicationGeneration);
        Assert.NotEqual(previous.RuntimeOwner, publication.RuntimeOwner);
        return publication;
    }
}
