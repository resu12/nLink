using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferBootstrapAgreementTests
{
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateSnapshotFixture(
        bool advertised, bool selected, bool outbound)
    {
        var sender = State(FileTransferDirection.Outbound);
        var receiver = State(FileTransferDirection.Inbound);
        var state = outbound ? sender : receiver;
        var offer = Offer(sender, advertised);
        var accept = Accept(receiver, selected);
        var open = Open(offer, accept);
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        Capture(FileTransferCoordinator.PinFallbackBootstrapOffer(state, offer));
        Capture(FileTransferCoordinator.ApplyFallbackBootstrapAccept(state, accept));
        Capture(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(state, open));
        Capture(FileTransferCoordinator.PinFallbackBootstrapOffer(state, offer));
        Capture(FileTransferCoordinator.ApplyFallbackBootstrapAccept(state, accept));
        Capture(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(state, open));
        Assert.Equal(selected, Agreement(state).IsEnabled);
        return envelopes;

        void Capture(FileTransferBootstrapAgreementDecision decision)
        {
            state = Apply(decision);
            envelopes.Add(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(decision.SnapshotEnvelope));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ExistingOneWayHandshake_PinsImmutableAgreement(bool advertised, bool selected)
    {
        var sender = State(FileTransferDirection.Outbound);
        var receiver = State(FileTransferDirection.Inbound);
        var offer = Offer(sender, advertised);
        sender = Apply(FileTransferCoordinator.PinFallbackBootstrapOffer(sender, offer));
        receiver = Apply(FileTransferCoordinator.PinFallbackBootstrapOffer(receiver, offer));
        var accept = Accept(receiver, selected);
        sender = Apply(FileTransferCoordinator.ApplyFallbackBootstrapAccept(sender, accept));
        receiver = Apply(FileTransferCoordinator.ApplyFallbackBootstrapAccept(receiver, accept));
        Assert.Equal(selected, Agreement(sender).IsEnabled);
        Assert.False(Agreement(receiver).IsEnabled);
        Assert.Equal(offer, Agreement(receiver).Offer);
        Assert.Equal(accept, Agreement(sender).Accept);

        var open = Open(offer, accept);
        var localValidation = FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(sender, open);
        Assert.True(localValidation.Accepted);
        Assert.Same(sender, localValidation.State); // No receiver SessionOpen is awaited or fabricated.
        receiver = Apply(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(receiver, open));
        Assert.Equal(selected, Agreement(receiver).IsEnabled);
        Assert.True(Agreement(receiver).ReceiverSessionOpenValidated);
        Assert.Same(receiver, Apply(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(receiver, open)));
        Assert.Same(receiver, Apply(FileTransferCoordinator.ApplyFallbackBootstrapAccept(receiver, accept)));
        Assert.Same(sender, Apply(FileTransferCoordinator.PinFallbackBootstrapOffer(sender, offer)));
        Assert.Null(sender.CurrentRoutePublicationToken);
        Assert.Null(receiver.CurrentRoutePublicationToken);
        Assert.Null(sender.CurrentRuntimeOwner);
        Assert.Null(receiver.CurrentRuntimeOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Handshake_MismatchAndStaleScopeCannotChangePinnedSelection(bool inbound)
    {
        var sender = State(FileTransferDirection.Outbound);
        var receiver = State(FileTransferDirection.Inbound);
        var offer = Offer(sender, true);
        var accept = Accept(receiver, true);
        var state = inbound ? receiver : sender;
        state = Apply(FileTransferCoordinator.PinFallbackBootstrapOffer(state, offer));
        state = Apply(FileTransferCoordinator.ApplyFallbackBootstrapAccept(state, accept));
        var open = Open(offer, accept);
        foreach (var invalid in new[]
        {
            open with { FallbackBootstrapIssuerId = accept.FallbackBootstrapIssuerId },
            open with { FallbackBootstrapCorrelationVersion = null, FallbackBootstrapIssuerId = null },
            open with { TransferId = "retired-transfer" }, open with { SessionId = "other-session" },
            open with { SessionRole = FileTransferProtocol.SessionRoleReceiver },
        })
        {
            RejectUnchanged(state, FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(state, invalid));
        }
        RejectUnchanged(state, FileTransferCoordinator.ApplyFallbackBootstrapAccept(state,
            accept with { FallbackBootstrapIssuerId = offer.FallbackBootstrapIssuerId }));
        RejectUnchanged(state, FileTransferCoordinator.PinFallbackBootstrapOffer(state,
            offer with { FallbackBootstrapIssuerId = accept.FallbackBootstrapIssuerId }));
        foreach (var stale in new[]
        {
            state with { IsTerminal = true },
            state with { TransferId = "replacement" },
            state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "replacement" } },
        })
        {
            RejectUnchanged(stale, FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(stale, open));
        }
        var newSession = state with { CurrentDataSessionIdentity = "replacement-data-session", CurrentDataSessionGeneration = 2 };
        Assert.Equal(Agreement(state), Agreement(newSession));
        Assert.True(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(newSession, open).Accepted);
    }

    [Fact]
    public void LegacySelection_CannotUpgradeInFlight_OrSelectUnsolicitedVersion()
    {
        var sender = State(FileTransferDirection.Outbound);
        var receiver = State(FileTransferDirection.Inbound);
        sender = Apply(FileTransferCoordinator.PinFallbackBootstrapOffer(sender, Offer(sender, false)));
        RejectUnchanged(sender, FileTransferCoordinator.ApplyFallbackBootstrapAccept(sender, Accept(receiver, true)));
        sender = Apply(FileTransferCoordinator.ApplyFallbackBootstrapAccept(sender, Accept(receiver, false)));
        RejectUnchanged(sender, FileTransferCoordinator.ApplyFallbackBootstrapAccept(sender, Accept(receiver, true)));
        Assert.False(Agreement(sender).IsEnabled);
    }

    [Fact]
    public void LocalIssuer_IsBoundToCoordinatorTransferAndDirection()
    {
        var sender = State(FileTransferDirection.Outbound);
        var issuer = FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(sender);
        Assert.Equal("6b462d8539299a496a4fcd2e8efb7826ee8968b43529d46671b3819635a34d65", issuer);
        Assert.True(FileTransferBootstrapCorrelation.IsIssuerId(issuer));
        Assert.NotEqual(issuer, FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(
            sender with { Direction = FileTransferDirection.Inbound }));
        Assert.NotEqual(issuer, FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(
            sender with { TransferId = "another" }));
        Assert.NotEqual(issuer, FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(
            sender with { PersistentState = sender.PersistentState with { CoordinatorInstanceId = "restarted" } }));
        Assert.NotEqual(FileTransferBootstrapCorrelation.HashFields("a", "bc"), FileTransferBootstrapCorrelation.HashFields("ab", "c"));
    }

    private static FileTransferCoordinatorState State(FileTransferDirection direction)
        => new("session", "transfer", direction, false,
            FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast),
            null, null, 0, 0, 0, 0, null, [],
            PersistentState: new() { CoordinatorInstanceId = direction == FileTransferDirection.Outbound ? "sender-instance" : "receiver-instance" });

    private static FileTransferOfferV2 Offer(FileTransferCoordinatorState sender, bool advertised)
        => new()
        {
            SessionId = "session", TransferId = "transfer", FileName = "file.bin", FileSizeBytes = 4096,
            PreferredDataProtocolVersion = 4, FileTransferRoute = "regular_nkn_v4_fast",
            FallbackBootstrapCorrelationVersion = advertised ? 1 : null,
            FallbackBootstrapIssuerId = advertised ? FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(sender) : null,
        };

    private static FileTransferAcceptV1 Accept(FileTransferCoordinatorState receiver, bool selected)
        => new()
        {
            SessionId = "session", TransferId = "transfer", AcceptedDataProtocolVersion = 4,
            FileTransferRoute = "regular_nkn_v4_fast", FallbackBootstrapCorrelationVersion = selected ? 1 : null,
            FallbackBootstrapIssuerId = selected ? FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(receiver) : null,
        };

    private static FileTransferSessionOpenV2 Open(FileTransferOfferV2 offer, FileTransferAcceptV1 accept)
        => new()
        {
            SessionId = "session", TransferId = "transfer", ProtocolVersion = 4,
            FileTransferRoute = "regular_nkn_v4_fast", SessionRole = FileTransferProtocol.SessionRoleSender,
            ChunkSizeBytes = 1024, InitialPipelineDepth = 1,
            FallbackBootstrapCorrelationVersion = accept.FallbackBootstrapCorrelationVersion,
            FallbackBootstrapIssuerId = accept.FallbackBootstrapCorrelationVersion is null ? null : offer.FallbackBootstrapIssuerId,
        };

    private static FileTransferBootstrapAgreement Agreement(FileTransferCoordinatorState state)
        => Assert.IsType<FileTransferBootstrapAgreement>(state.PersistentState.FileTransferRouteTransition.BootstrapAgreement);

    private static FileTransferCoordinatorState Apply(FileTransferBootstrapAgreementDecision decision)
    {
        Assert.True(decision.Accepted, decision.Reason);
        return decision.State;
    }

    private static void RejectUnchanged(FileTransferCoordinatorState state, FileTransferBootstrapAgreementDecision decision)
    {
        Assert.False(decision.Accepted, decision.Reason);
        Assert.Same(state, decision.State);
    }
}
