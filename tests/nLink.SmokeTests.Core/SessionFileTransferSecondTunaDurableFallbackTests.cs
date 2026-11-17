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
        await CommitDurableFallbackBatchAndSettleYieldAsync(service, context, data, batch,
            received.TransportKind, publication, previousExecution, destinationPath);
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
        var logStart = GetOperationalLogLength();
        var afterWrite = CaptureDurableFallbackState(service, context);
        var fileHash = HashDurableFixtureFile(destinationPath);
        await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("RecheckInboundFileTransferRouteTransitionDrainAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, previousExecution]));
        var afterDrain = CaptureDurableFallbackState(service, context);
        if (afterWrite.Fingerprint != afterDrain.Fingerprint)
        {
            var diff = FileTransferFixtureStateDiff.Describe(afterWrite.State, afterDrain.State);
            var diagnosticPath = Path.Combine(Path.GetDirectoryName(destinationPath)!, "service-durable-state-diff.json");
            await File.WriteAllTextAsync(diagnosticPath, JsonSerializer.Serialize(new
            {
                Before = afterWrite, After = afterDrain, Diff = diff,
                InterveningLog = ReadOperationalLogTail(logStart),
                Note = "Observation interval includes asynchronous work; this diff does not attribute changes to the stale callback.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Fail($"Coordinator state fingerprint changed across stale drain. Before={afterWrite.Fingerprint}; " +
                $"After={afterDrain.Fingerprint}; diagnostics={diagnosticPath}{Environment.NewLine}{diff}");
        }
        Assert.Equal(afterWrite.Fingerprint, afterDrain.Fingerprint);
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

    private sealed record DurableFallbackStateSnapshot(string Fingerprint, JsonElement State);

    private static async Task CommitDurableFallbackBatchAndSettleYieldAsync(SessionFileTransferService service,
        object context, InitialBootstrapRecordingDataSession data, FileTransferChunkBatchFrameV6 batch,
        FileTransferTransportKind transportKind, RoutePublicationToken publication, object previousExecution,
        string destinationPath)
    {
        var entered = new TaskCompletionSource<FileTransferFallbackTailYieldFrameV6>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proofSendCount = 0;
        var previousSend = data.BeforeSendAsyncForTest;
        data.BeforeSendAsyncForTest = async (frame, ct) =>
        {
            if (previousSend is not null) { await previousSend(frame, ct); }
            if (frame is FileTransferFallbackTailYieldFrameV6 yield && yield.LiveRouteEpoch == publication.LiveRouteEpoch)
            {
                Interlocked.Increment(ref proofSendCount);
                entered.TrySetResult(yield);
                await release.Task.WaitAsync(ct);
            }
        };
        try
        {
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("HandleInboundV6ChunkBatchAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, batch, transportKind]))
                .WaitAsync(TimeSpan.FromSeconds(5));
            var frame = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            FileTransferFallbackTailYieldOperation pendingYield;
            lock (GetServiceGate(service))
            {
                pendingYield = Assert.IsType<FileTransferFallbackTailYieldOperation>(
                    CreateCoordinatorStateForTest(context).PersistentState.FallbackTailYield.CurrentOperation);
                Assert.Equal(frame.AuthorityId, pendingYield.Decision.AuthorityId.ToString());
                Assert.Equal(frame.YieldGeneration, pendingYield.Decision.YieldGeneration);
                Assert.Null(pendingYield.LocalProof);
            }
            Assert.False(release.Task.IsCompleted);
            var pending = CaptureDurableFallbackState(service, context);
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("RecheckInboundFileTransferRouteTransitionDrainAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, previousExecution]));
            Assert.Equal(pending.Fingerprint, CaptureDurableFallbackState(service, context).Fingerprint);

            // The chunk handler does not await this proof. Observe its exact Coordinator
            // receipt before the existing stale-drain assertion, not a delay or hash exemption.
            var logStart = GetOperationalLogLength();
            release.TrySetResult();
            await WaitUntilAsync(() =>
            {
                lock (GetServiceGate(service))
                {
                    var current = CreateCoordinatorStateForTest(context).PersistentState.FallbackTailYield.CurrentOperation;
                    return current is { LocalProof: { } receipt } &&
                        current.Value.Decision == pendingYield.Decision && receipt.Decision == pendingYield.Decision;
                }
            }, 5000);
            Assert.Equal(1, Volatile.Read(ref proofSendCount));
            var settled = CaptureDurableFallbackState(service, context);
            Assert.NotEqual(pending.Fingerprint, settled.Fingerprint);
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(destinationPath)!, "service-durable-yield-settlement.json"),
                JsonSerializer.Serialize(new
                {
                    Before = pending, After = settled,
                    Diff = FileTransferFixtureStateDiff.Describe(pending.State, settled.State),
                    InterveningLog = ReadOperationalLogTail(logStart),
                    ChunkHandlerCompletedWhileProofHeld = true, StaleDrainPreservedPendingState = true,
                    Note = "Only the held current proof was released between snapshots; no stale callback was invoked in this interval.",
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            release.TrySetResult();
            data.BeforeSendAsyncForTest = previousSend;
        }
    }

    private static DurableFallbackStateSnapshot CaptureDurableFallbackState(
        SessionFileTransferService service, object context)
    {
        // Freeze each observation under the existing gate; never hold it across
        // an asynchronous callback or proof settlement.
        lock (GetServiceGate(service))
        {
            var state = CreateCoordinatorStateForTest(context);
            var retry = state.PersistentState.RuntimeUnlockRetryLifecycle;
            var snapshot = JsonSerializer.SerializeToElement(new
            {
                Coordinator = state,
                RuntimeUnlockRetryLifecycle = retry with
                {
                    AttemptHistory = retry.AttemptHistory.IsDefault ? [] : retry.AttemptHistory,
                },
                state.PersistentState.FallbackTailYield,
                state.PersistentState.FileTransferReceiveRecovery,
                state.PersistentState.InitialActivationReceiverBarrier,
                state.PersistentState.CheckpointRepairContinuityOrigin,
                state.PersistentState.BootstrapSourceCallbackLineage,
                state.PersistentState.CoordinatorInstanceId,
                state.PersistentState.SnapshotStateGeneration,
                state.PersistentState.SnapshotStateFingerprint,
                Frontier = GetPrivateProperty<int>(context, "NextChunkIndex"),
                BytesTransferred = GetPrivateProperty<long>(context, "BytesTransferred"),
                TransitionExecution = GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"),
            });
            return new(FileTransferCoordinator.CalculateSnapshotStateFingerprint(state), snapshot);
        }
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
