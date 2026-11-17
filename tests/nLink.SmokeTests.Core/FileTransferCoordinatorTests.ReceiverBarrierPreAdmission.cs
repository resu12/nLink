using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_PreAdmissionFailureClosesWithoutMintingAdmission(long launchGeneration)
    {
        var fixture = CreatePreAdmissionReceiverBarrierFixture();
        var negotiation = fixture.Negotiation;
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, bound.Status);
        Assert.Null(bound.State.CurrentRuntimeUnlockAdmission);
        var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(bound.State,
            bound.Authority!, fixture.Source, 1, negotiation with { LaunchGeneration = launchGeneration }, ReceiverBarrierOrigin());
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, prepared.Status);
        Assert.NotNull(prepared.Command);
        Assert.Equal(InitialActivationReceiverBarrierCloseMode.InitialResponderBeforeProbe, prepared.Command.Mode);
        Assert.Null(prepared.Command.Admission);
        Assert.Null(prepared.State.CurrentRuntimeUnlockAdmission);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Idempotent,
            FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(prepared.State, bound.Authority!, fixture.Source,
                1, prepared.Command.Negotiation, prepared.Command.FailureOrigin).Status);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale,
            FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(prepared.State, bound.Authority!, fixture.Source,
                1, prepared.Command.Negotiation with { LaunchGeneration = 2 }, prepared.Command.FailureOrigin).Status);
        var closed = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(prepared.State, fixture.Source,
            new(prepared.Command!, InitialActivationReceiverBarrierCloseApplyStatus.Applied, "exact_pre_admission_close"));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, closed.Status);
        Assert.Null(closed.State.CurrentRuntimeUnlockAdmission);
        Assert.Equal(fixture.Source.RuntimeOwner, closed.State.CurrentRuntimeOwner);
        Assert.Equal(fixture.Source.RoutePublication, closed.State.CurrentRoutePublicationToken);
        Assert.Null(closed.State.CurrentRouteCommit);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("offer")]
    [InlineData("transaction")]
    [InlineData("payer")]
    [InlineData("ack")]
    [InlineData("answer")]
    [InlineData("lease")]
    [InlineData("negative_launch")]
    [InlineData("bound_launch_replaced")]
    [InlineData("no_semantic_ack")]
    [InlineData("admission_present")]
    [InlineData("retired_admission")]
    [InlineData("retired_unattached_admission")]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_PreAdmissionRejectsChangedOrAdmittedScope(string mutation)
    {
        var fixture = CreatePreAdmissionReceiverBarrierFixture();
        var negotiation = fixture.Negotiation;
        if (mutation == "bound_launch_replaced") { negotiation = negotiation with { LaunchGeneration = 1 }; }
        if (mutation == "no_semantic_ack") { negotiation = negotiation with { Ack = null }; }
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, negotiation);
        var state = bound.State;
        if (mutation is "admission_present" or "retired_admission" or "retired_unattached_admission")
        {
            state = AdmitReceiverBarrier(state);
            if (mutation == "retired_admission")
            {
                state = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(state, bound.Authority!, fixture.Source,
                    state.CurrentRuntimeUnlockAdmission!.Value, negotiation).State with { CurrentRuntimeUnlockAdmission = null };
            }
            if (mutation == "retired_unattached_admission") { state = state with { CurrentRuntimeUnlockAdmission = null }; }
        }
        var observed = negotiation with { LaunchGeneration = 2 };
        observed = mutation switch
        {
            "nonce" => observed with { Nonce = "replacement" },
            "offer" => observed with { OfferGeneration = observed.OfferGeneration + 1 },
            "transaction" => observed with { TransactionGeneration = observed.TransactionGeneration + 1 },
            "payer" => observed with { PayerDecisionId = 9 },
            "ack" => observed with { Ack = observed.Ack! with { MessageId = "replacement" } },
            "answer" => observed with { AnswerMessageId = "replacement" },
            "lease" => observed with { TunaPathLeaseListenerRunId = "replacement" },
            "negative_launch" => observed with { LaunchGeneration = -1 },
            _ => observed,
        };
        var rejected = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(state, bound.Authority!, fixture.Source,
            1, observed, ReceiverBarrierOrigin());
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, rejected.Status);
        Assert.Null(rejected.Command);
        Assert.Equal(state.CurrentRuntimeOwner, rejected.State.CurrentRuntimeOwner);
        Assert.Equal(state.CurrentRoutePublicationToken, rejected.State.CurrentRoutePublicationToken);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_PreAdmissionPreparedCloseCannotSurviveAdmission()
    {
        var fixture = CreatePreAdmissionReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(bound.State, bound.Authority!, fixture.Source,
            1, fixture.Negotiation with { LaunchGeneration = 1 }, ReceiverBarrierOrigin());
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, prepared.Status);
        var admitted = AdmitReceiverBarrier(prepared.State);
        Assert.Null(admitted.PersistentState.InitialActivationReceiverBarrier.Current);
        Assert.Equal("receiver_barrier_admission_replaced", admitted.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.Reason);
        var late = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(admitted, fixture.Source,
            new(prepared.Command!, InitialActivationReceiverBarrierCloseApplyStatus.Applied, "late_receipt"));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, late.Status);
        Assert.Equal(admitted.CurrentRuntimeUnlockAdmission, late.State.CurrentRuntimeUnlockAdmission);
    }

    internal static (FileTransferCoordinatorState State, InitialActivationReceiverBarrierSource Source,
        InitialActivationReceiverBarrierNegotiation Negotiation) CreatePreAdmissionReceiverBarrierFixture()
    {
        var fixture = CreateReceiverBarrierFixture();
        return (fixture.State, fixture.Source, fixture.Negotiation with
        {
            PendingAnswerAckGeneration = 6, TunaPathLeaseGeneration = 1,
            TunaPathLeaseListenerRunId = "peer-offer-dialer-1",
            AnswerMessageId = "initial-answer", AnswerDeadlineUtcMs = 2000,
            Ack = new("initial-answer-ack", new string('a', 64), new string('b', 64), 1000),
        });
    }
}
