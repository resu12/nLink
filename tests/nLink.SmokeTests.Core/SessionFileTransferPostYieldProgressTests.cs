using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackTailUnblockRecheck")]
    public async Task InboundPostYieldProgress_ReportsSmallAdvanceWithoutRestartingRecovery()
    {
        using var service = new SessionFileTransferService();
        var (context, dataSession) = CreatePostYieldProgressContext(service);
        var before = CreateCoordinatorStateForTest(context);
        Assert.True(FileTransferCoordinator.IsFallbackTailYieldDecisionCurrent(before,
            before.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision, out _));
        var logStart = GetOperationalLogLength();

        Assert.True(ShouldForcePostYieldProgress(service, context, 69));
        Assert.True(await SendPostYieldProgress(service, context, forceSend: true));

        var frame = Assert.Single(dataSession.SentFrames.OfType<FileTransferReceiverStateFrameV6>());
        Assert.Equal(72, frame.ContiguousCommittedChunkIndex);
        var after = CreateCoordinatorStateForTest(context);
        Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
        Assert.Equal(before.PersistentState.FallbackTailYield, after.PersistentState.FallbackTailYield);
        Assert.NotNull(after.CurrentFallbackControlPlaneToken);
        Assert.Equal(FileTransferControlPlaneKind.ReceiverState,
            after.CurrentFallbackControlPlaneToken!.Value.ControlPlaneKind);
        Assert.False(GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Contains("event=filetransfer_control_plane_delivery_unavailable; kind=receiver_state;",
            ReadOperationalLogTail(logStart));
        Assert.DoesNotContain(dataSession.SentFrames, item => item is FileTransferFrontierRequestFrameV6);

        // Pin cadence again: duplicate/no-progress feedback must still coalesce.
        SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.MaxValue);
        Assert.False(ShouldForcePostYieldProgress(service, context, 72));
        Assert.False(await SendPostYieldProgress(service, context, forceSend: false));
        Assert.Single(dataSession.SentFrames);
        Assert.Equal(after.PersistentState, CreateCoordinatorStateForTest(context).PersistentState);
    }

    [Theory]
    [InlineData("no-local-proof")]
    [InlineData("no-write-advance")]
    [InlineData("already-reported")]
    [InlineData("data-session-generation")]
    [InlineData("publication")]
    [InlineData("leg")]
    [InlineData("transport-epoch")]
    [InlineData("bridge-generation")]
    [InlineData("terminal")]
    [InlineData("superseded-context")]
    [Trait("FileTransferRuntimeSuite", "FallbackTailUnblockRecheck")]
    public async Task InboundPostYieldProgress_WithoutCurrentNewProgressDoesNotForce(string change)
    {
        using var service = new SessionFileTransferService();
        var (context, dataSession) = CreatePostYieldProgressContext(service, change != "no-local-proof");
        var previous = 69;
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        switch (change)
        {
            case "no-write-advance": previous = 72; break;
            case "already-reported": SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", 72); break;
            case "data-session-generation": SetPrivateProperty(context, "DataSessionGeneration", 2L); break;
            case "publication":
                var publication = CreateCoordinatorStateForTest(context).CurrentRoutePublicationToken!.Value;
                SetPrivateProperty(context, "CurrentRoutePublicationToken",
                    publication with { PublicationGeneration = publication.PublicationGeneration + 1 });
                break;
            case "leg": SetPrivateProperty(leg, "Generation", leg.Generation + 1); break;
            case "transport-epoch": leg.TransportEpochId++; break;
            case "bridge-generation": leg.BridgeRecoveryGeneration++; break;
            case "terminal": SetPrivateProperty(context, "State", FileTransferTransferState.Completed); break;
            case "superseded-context":
                typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(service, null);
                break;
        }
        var before = CreateCoordinatorStateForTest(context);
        Assert.False(ShouldForcePostYieldProgress(service, context, previous));
        Assert.False(await SendPostYieldProgress(service, context, forceSend: false));
        Assert.Empty(dataSession.SentFrames);
        Assert.Equal(before.PersistentState, CreateCoordinatorStateForTest(context).PersistentState);
        Assert.False(GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackTailUnblockRecheck")]
    public async Task InboundPostYieldProgress_ActivationPauseDoesNotPublishOrLoseUnreportedFrontier()
    {
        using var service = new SessionFileTransferService();
        var (context, dataSession) = CreatePostYieldProgressContext(service);
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPauseReason", "tuna_activation_negotiating");
        var before = CreateCoordinatorStateForTest(context);
        Assert.True(ShouldForcePostYieldProgress(service, context, 69));
        Assert.False(await SendPostYieldProgress(service, context, forceSend: true));
        Assert.Empty(dataSession.SentFrames);
        Assert.Equal(69, GetPrivateProperty<int>(context, "V6LastReceiverStateCommittedChunkIndex"));
        Assert.Equal(before.PersistentState, CreateCoordinatorStateForTest(context).PersistentState);

        SetPrivateProperty(context, "PullTransportPaused", false);
        SetPrivateProperty(context, "PullTransportPauseReason", null);
        Assert.True(ShouldForcePostYieldProgress(service, context, 69));
        Assert.True(await SendPostYieldProgress(service, context, forceSend: true));
        Assert.Single(dataSession.SentFrames.OfType<FileTransferReceiverStateFrameV6>());
        Assert.False(GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
    }

    private static (object Context, RecordingDataSession DataSession) CreatePostYieldProgressContext(
        SessionFileTransferService service, bool localProof = true)
    {
        const string transferId = "post_yield_small_progress";
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        var context = CreatePostTunaFallbackV6InboundContext(transferId, dataSession: dataSession);
        typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SetPrivateProperty(context, "NextChunkIndex", 69);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 68);
        var destinationMode = context.GetType().GetProperty("V6DestinationMode")!.PropertyType;
        SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(destinationMode, "SparseSeekable"));
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        if (localProof)
        {
            SeedLocalFallbackTailYieldForTest(context, leg);
            typeof(SessionFileTransferService)
                .GetMethod("ClearInboundFallbackTailRecoveryOwnershipAfterYieldLocked", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [context, leg, "unit_test_post_yield_progress"]);
        }
        else
        {
            SeedFallbackTailYieldAuthorityForTest(context, leg);
            SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        }
        SetPrivateProperty(context, "NextChunkIndex", 72);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 71);
        SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", 69);
        // Disable elapsed-time forcing deterministically; only the progress contract can send.
        SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.MaxValue);
        return (context, dataSession);
    }

    private static bool ShouldForcePostYieldProgress(SessionFileTransferService service, object context, int previous)
        => (bool)typeof(SessionFileTransferService)
            .GetMethod("ShouldForceInboundV6ReceiverStateAfterProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, previous])!;

    private static Task<bool> SendPostYieldProgress(SessionFileTransferService service, object context, bool forceSend)
        => (Task<bool>)typeof(SessionFileTransferService)
            .GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "chunk_batch_committed", forceSend, false, null])!;
}
