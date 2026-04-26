using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareFrameLossAttributionTests : ScreenShareViewerViewModelTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public ScreenShareFrameLossAttributionTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_LateFragmentsAfterSuccessfulRecovery_AdvanceStableVisibleHeadFloor()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveRecoveryWindowSucceeded(
            "viewer-late-fragment-success",
            streamEpoch: 3,
            recoveryFrameId: 40,
            lastContiguousFrameId: 42);

        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            "viewer-late-fragment-success",
            streamEpoch: 3,
            frameId: 45,
            isKeyFrame: false);

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-late-fragment-success",
            streamEpoch: 3,
            frameId: 44,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 46,
            receivedFrameId: 44,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-late-fragment-success");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(45, snapshot.StableVisibleHeadFrameId);
        Assert.Equal(45, epochDiagnostics.StableVisibleHeadFrameId);
        Assert.Equal(45, snapshot.AppliedHeadFrameId);
        Assert.Equal(45, epochDiagnostics.AppliedHeadFrameId);
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterSuccessfulRecoveryCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterSuccessfulRecoveryCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Empty(epochDiagnostics.TopLossBursts);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_FourContiguousVisibleApplies_ActivateStableVisibleHeadFloor()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        for (var frameId = 10; frameId <= 13; frameId++)
        {
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
                "viewer-stable-visible-head",
                streamEpoch: 4,
                frameId: frameId,
                isKeyFrame: frameId == 10);
        }

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-stable-visible-head",
            streamEpoch: 4,
            frameId: 12,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 14,
            receivedFrameId: 12,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-stable-visible-head");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(13, snapshot.StableVisibleHeadFrameId);
        Assert.Equal(13, epochDiagnostics.StableVisibleHeadFrameId);
        Assert.Equal(13, snapshot.AppliedHeadFrameId);
        Assert.Equal(13, epochDiagnostics.AppliedHeadFrameId);
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal("none", snapshot.DominantReassemblerRootCause);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_LateFragmentsAfterAppliedHead_AreCountedSeparately()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            "viewer-late-fragment-applied-head",
            streamEpoch: 5,
            frameId: 20,
            isKeyFrame: true);

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-late-fragment-applied-head",
            streamEpoch: 5,
            frameId: 19,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 21,
            receivedFrameId: 19,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-late-fragment-applied-head");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(20, snapshot.AppliedHeadFrameId);
        Assert.Equal(20, epochDiagnostics.AppliedHeadFrameId);
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal("none", snapshot.DominantReassemblerRootCause);
        Assert.Empty(epochDiagnostics.TopLossBursts);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_LateFragmentsAfterVisibleRecovery_AreCountedSeparately()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(
            "viewer-late-fragment-visible-recovery",
            streamEpoch: 3,
            frameId: 40);

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-late-fragment-visible-recovery",
            streamEpoch: 3,
            frameId: 39,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 41,
            receivedFrameId: 39,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-late-fragment-visible-recovery");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(1, snapshot.LateFragmentAfterVisibleRecoveryCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterVisibleRecoveryCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Empty(epochDiagnostics.TopLossBursts);
    }

}
