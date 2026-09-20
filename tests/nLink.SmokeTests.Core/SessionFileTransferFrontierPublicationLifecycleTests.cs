using System.Collections.Immutable;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationLifecycle")]
    public async Task FrontierPublicationLifecycle_ExactClonesTransferSubscriptionsAndCloseReleasesThem()
    {
        FileTransferLeg? currentLeg = null;
        LiveRouteEpoch? currentEpoch = null;
        var fixture = await RunFrontierPublicationFixtureAsync("source_noop", true, context =>
        {
            var oldLeg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
            var oldEpoch = GetPrivateProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
            var clone = typeof(SessionFileTransferService).GetMethod("CloneCoordinatorRuntimeGraphForContext",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var graph = (System.Runtime.CompilerServices.ITuple)clone.Invoke(null, [CreateCoordinatorStateForTest(context)])!;
            currentEpoch = (LiveRouteEpoch)graph[0]!;
            currentLeg = (FileTransferLeg)graph[1]!;
            Assert.NotSame(oldLeg, currentLeg);
            Assert.NotSame(oldEpoch, currentEpoch);
            SetPrivateProperty(context, "CurrentLiveRouteEpoch", currentEpoch);
            SetPrivateProperty(context, "CurrentTransferLeg", currentLeg);
            var legs = GetPrivateProperty<List<FileTransferLeg>>(context, "TransferLegHistory");
            legs.Clear();
            legs.AddRange((IReadOnlyList<FileTransferLeg>)graph[2]!);
            // These objects are no longer the adopted source; their callbacks
            // must not contaminate the current observation.
            oldLeg.CheckpointGeneration++;
            oldEpoch.State = "retired_clone";
            currentLeg.CheckpointGeneration = currentLeg.CheckpointGeneration;
            currentEpoch.State = currentEpoch.State;
            SetPrivateProperty(context, "UserPaused", false);
        });
        var history = fixture.Producer.Lifecycle;
        Assert.NotNull(fixture.Producer.Receipt);
        Assert.Null(typeof(FileTransferLeg).GetField("FrontierPublicationSourceChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(currentLeg));
        Assert.Null(typeof(LiveRouteEpoch).GetField("FrontierPublicationSourceChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(currentEpoch));
        currentLeg!.CheckpointGeneration++;
        currentEpoch!.State = "after_close";
        Assert.Same(history, fixture.Producer.Lifecycle);
    }

    [Theory]
    [InlineData("RouteSelection")]
    [InlineData("CurrentLiveRouteEpoch")]
    [InlineData("CurrentTransferLeg")]
    [InlineData("TerminalGeneration")]
    [InlineData("CurrentRuntimeOwner")]
    [InlineData("RuntimeUnlockRouteCommitToken")]
    [InlineData("CurrentRoutePublicationToken")]
    [InlineData("CurrentFallbackCheckpointLease")]
    [InlineData("CoordinatorPersistentState")]
    [InlineData("CurrentTransportEpochDecision")]
    [InlineData("CurrentTransferLeg.TransportEpochId")]
    [InlineData("CurrentTransferLeg.CheckpointGeneration")]
    [InlineData("CurrentTransferLeg.CheckpointRequestId")]
    [InlineData("CurrentTransferLeg.BridgeRecoveryGeneration")]
    [InlineData("CurrentTransferLeg.State")]
    [InlineData("CurrentLiveRouteEpoch.State")]
    [InlineData("CurrentLiveRouteEpoch.RoutePublicationToken")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationLifecycle")]
    public async Task FrontierPublicationLifecycle_SourceSetterABAClosesBeforeAcceptedSend(string path)
    {
        var fixture = await RunFrontierPublicationFixtureAsync("source_mutation", true, context =>
        {
            var parts = path.Split('.');
            var target = parts.Length == 1 ? context : GetPrivatePropertyValue(context, parts[0])!;
            Assert.NotNull(target);
            var property = parts[^1];
            var original = GetPrivatePropertyValue(target, property);
            object? changed = path switch
            {
                "RouteSelection" => ((FileTransferRouteSelection)original!) with { ProtocolVersion = 4 },
                "TerminalGeneration" or "CurrentTransferLeg.CheckpointGeneration" or "CurrentTransferLeg.BridgeRecoveryGeneration"
                    => (int)original! + 1,
                "CurrentTransferLeg.TransportEpochId" => (long)original! + 1,
                "CurrentTransferLeg.CheckpointRequestId" or "CurrentLiveRouteEpoch.State" => "changed_source",
                "CurrentTransferLeg.State" => FileTransferLegState.Terminal,
                "RuntimeUnlockRouteCommitToken" => original is null ? default(RouteCommitToken) : null,
                "CurrentLiveRouteEpoch.RoutePublicationToken" => original is null ? default(RoutePublicationToken) : null,
                "CurrentFallbackCheckpointLease" => ((FallbackCheckpointLease?)original ?? default) with { LeaseGeneration = 999 },
                "CurrentTransportEpochDecision" => ((TransportEpochDecision?)original ?? default) with { TransportEpoch = 999 },
                "CoordinatorPersistentState" => ((FileTransferCoordinatorPersistentState)original!) with { CoordinatorInstanceId = "changed_source" },
                _ => null,
            };
            Assert.NotEqual(original, changed);
            SetPrivateProperty(target, property, changed);
            SetPrivateProperty(target, property, original);
            Assert.Equal(original, GetPrivatePropertyValue(target, property));
        });
        var history = Assert.IsType<FileTransferCoordinator.FrontierPublicationLifecycle>(fixture.Producer.Lifecycle);
        Assert.Equal("ObservationInvalidated", history.Outcome);
        Assert.Equal(history.Entries.Length, history.LastSequence);
        Assert.InRange(history.LastSequence, 2, 64);
        Assert.NotNull(history.Entries[^1].Boundary);
        Assert.Null(history.Entries[^1].ObservedUtc);
        Assert.Null(fixture.Producer.Receipt);
        fixture.Producer.EndObservation();
        fixture.Producer.Invalidate("PublicationRejected");
        Assert.Same(history, fixture.Producer.Lifecycle);
        Assert.Null(FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, fixture.Lines, out _));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationLifecycle")]
    public async Task FrontierPublicationLifecycle_SerializedHistoryRequiresExactOrderedClosedBindings()
    {
        var fixture = await RunFrontierPublicationFixtureAsync("post_deadline_write", true);
        var receipt = fixture.Producer.Receipt!;
        var capture = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, fixture.Lines, out var reason);
        Assert.True(capture is not null, reason);
        using var full = JsonDocument.Parse(capture!.ExportFull());
        using var compact = JsonDocument.Parse(capture.ExportCompact());
        var serialized = full.RootElement.GetProperty("Receipt").GetProperty("Lifecycle").GetRawText();
        Assert.Equal(serialized, compact.RootElement.GetProperty("Receipt").GetProperty("Lifecycle").GetRawText());
        var history = JsonSerializer.Deserialize<FileTransferCoordinator.FrontierPublicationLifecycle>(serialized)!;
        Assert.True(Valid(history));
        var renamed = history with { ObservationId = Guid.NewGuid().ToString("N") };
        Assert.True(Valid(renamed)); // Structure alone is deliberately not custody.
        var forged = System.Text.Json.Nodes.JsonNode.Parse(capture.ExportFull())!;
        forged["Receipt"]!["Lifecycle"]!["ObservationId"] = renamed.ObservationId;
        Assert.Equal("EvidenceIncomplete", capture.Validate(Canonical(forged), "full").Status);
        Assert.Equal(receipt.CompletedUtc, history.Entries[^1].ObservedUtc);
        Assert.Equal(receipt.Outcome, history.Entries[^1].Kind);
        Assert.Equal(Enumerable.Range(1, history.LastSequence), history.Entries.Select(entry => entry.Sequence));
        Reject(history with { ObservationId = "missing" });
        Reject(history with { Coverage = "live_complete" });
        Reject(history with { SourceGraphFingerprint = "changed" });
        Reject(history with { Outcome = "Recovered" });
        Reject(history with { LastSequence = history.LastSequence + 1 });
        Reject(history with { Entries = default });
        Reject(history with { Entries = history.Entries.RemoveAt(0), LastSequence = history.LastSequence - 1 });
        Reject(history with { Entries = history.Entries.RemoveAt(history.LastSequence - 1), LastSequence = history.LastSequence - 1 });
        Reject(history with { Entries = history.Entries.Reverse().ToImmutableArray() });
        Reject(history with { Entries = history.Entries.SetItem(0, null!) });
        for (var i = 0; i < history.Entries.Length; i++)
        {
            var entry = history.Entries[i];
            foreach (var changed in new[]
            {
                entry with { Sequence = entry.Sequence + 1 }, entry with { Kind = "ObservationInvalidated" },
                entry with { SourceFingerprint = "changed" }, entry with { BindingFingerprint = "changed" },
                entry with { Boundary = "hidden_source_change" },
                entry with { ObservedUtc = entry.ObservedUtc.HasValue ? null : receipt.CompletedUtc },
            }) { Reject(history with { Entries = history.Entries.SetItem(i, changed) }); }
        }
        var first = history.Entries[0];
        Reject(history with { Entries = history.Entries.SetItem(0, first with { ObservedUtc = receipt.CompletedUtc.AddSeconds(1) }) });
        // Even structurally valid history from another observation cannot supply
        // the private capture's expected identity, bytes, or writer membership.
        var other = await RunFrontierPublicationFixtureAsync("single", true);
        Reject(other.Producer.Lifecycle!);
        Assert.False(capture.Validate(capture.ExportFull(), "full").LiveProofEligible);
        var frozen = fixture.Producer.Lifecycle;
        fixture.Producer.Invalidate();
        fixture.Producer.EndObservation();
        Assert.Same(frozen, fixture.Producer.Lifecycle);

        bool Valid(FileTransferCoordinator.FrontierPublicationLifecycle value)
            => FileTransferCoordinator.FrontierPublicationOfflineCapture.ValidateLifecycle(receipt, value);
        void Reject(FileTransferCoordinator.FrontierPublicationLifecycle value) => Assert.False(Valid(value));
    }
}
