using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("new")]
    [InlineData("empty")]
    [InlineData("constructor_default")]
    [InlineData("constructor_empty")]
    [InlineData("constructor_equal_empty")]
    [InlineData("reset_default")]
    [InlineData("reset_empty")]
    [InlineData("reset_equal_empty")]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void PersistentRouteState_EmptyConstructionAndResetPreserveValueSemantics(string construction)
    {
        var populated = new FileTransferCoordinatorPersistentState(
            FileTransferRouteTransition: new(LastTransitionGeneration: 7));
        var actual = construction switch
        {
            "default" => default,
            "new" => new FileTransferCoordinatorPersistentState(),
            "empty" => FileTransferCoordinatorPersistentState.Empty,
            "constructor_default" => new FileTransferCoordinatorPersistentState(FileTransferRouteTransition: default),
            "constructor_empty" => new FileTransferCoordinatorPersistentState(
                FileTransferRouteTransition: FileTransferRouteTransitionSubstate.Empty),
            "constructor_equal_empty" => new FileTransferCoordinatorPersistentState(FileTransferRouteTransition: new()),
            "reset_default" => populated with { FileTransferRouteTransition = default },
            "reset_empty" => populated with { FileTransferRouteTransition = FileTransferRouteTransitionSubstate.Empty },
            "reset_equal_empty" => populated with { FileTransferRouteTransition = new() },
            _ => throw new ArgumentOutOfRangeException(nameof(construction)),
        };
        var expected = default(FileTransferCoordinatorPersistentState);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
        Assert.Same(FileTransferRouteTransitionSubstate.Empty, actual.FileTransferRouteTransition);
        Assert.Equal(7, populated.FileTransferRouteTransition.LastTransitionGeneration);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotFingerprint(expected),
            FileTransferCoordinator.CalculateSnapshotFingerprint(actual));

        var state = EmptyState(FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast));
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(state with { PersistentState = expected }),
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(state with { PersistentState = actual }));
        var (_, deconstructed, _) = actual;
        Assert.Same(FileTransferRouteTransitionSubstate.Empty, deconstructed);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void PersistentRouteState_NonemptyCopiesPreserveEqualityAndPriorGraphs()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();
        var before = setup.State.PersistentState.FileTransferRouteTransition;
        var beforeFingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(setup.State);
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State, setup.Request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);

        var original = prepared.State.PersistentState.FileTransferRouteTransition;
        var equalCopy = original with { };
        Assert.NotSame(original, equalCopy);
        Assert.Equal(original, equalCopy);
        Assert.Equal(original.GetHashCode(), equalCopy.GetHashCode());
        var copiedState = prepared.State with
        {
            PersistentState = prepared.State.PersistentState with { FileTransferRouteTransition = equalCopy },
        };
        Assert.Equal(prepared.State.PersistentState, copiedState.PersistentState);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(prepared.State),
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(copiedState));

        var updated = copiedState.PersistentState with
        {
            FileTransferRouteTransition = equalCopy with { LastTransitionGeneration = original.LastTransitionGeneration + 1 },
        };
        Assert.NotEqual(prepared.State.PersistentState, updated);
        Assert.Equal(original.LastTransitionGeneration, copiedState.PersistentState.FileTransferRouteTransition.LastTransitionGeneration);
        Assert.Same(before, setup.State.PersistentState.FileTransferRouteTransition);
        Assert.Equal(beforeFingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(setup.State));

        var duplicate = FileTransferCoordinator.PrepareFileTransferRouteTransition(copiedState, setup.Request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Idempotent, duplicate.Status);
        Assert.Same(copiedState, duplicate.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void PersistentRouteState_CanonicalProjectionPreservesLegacyShape(bool populated)
    {
        var transition = FileTransferRouteTransitionSubstate.Empty;
        if (populated)
        {
            var setup = CreateRegularV4ToTunaRouteTransitionSetup();
            var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State, setup.Request);
            Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);
            transition = prepared.State.PersistentState.FileTransferRouteTransition;
        }

        // The prior value type exposed these same fields to canonical serialization.
        var legacyShape = new
        {
            transition.CurrentOperation,
            transition.PendingSupersession,
            transition.LastTerminalOperation,
            transition.LastTransitionGeneration,
            transition.PendingFallbackV6ProofBootstrap,
            transition.LastFallbackV6ProofBootstrapReceipt,
            transition.LastFallbackV6ProofBootstrapGeneration,
            transition.BootstrapAgreement,
            transition.BootstrapExchange,
            transition.BootstrapSourceHold,
        };
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotFingerprint(legacyShape),
            FileTransferCoordinator.CalculateSnapshotFingerprint(transition));
    }
}
