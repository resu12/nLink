using NLink.App.Services.RemoteControl;
using NLink.Core.RemoteControl;

namespace NLink.SmokeTests;

public sealed class RemoteControlCoordinatorTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_SetStateToActive_NormalizesContext_AndMarksBecameActive()
    {
        var current = RemoteControlSessionState.Default with
        {
            SupportsRemoteControl = true,
            PeerSupportsRemoteControl = true,
        };

        var transition = RemoteControlCoordinator.Apply(
            current,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.SetState,
                "test_active",
                NextControlState: ControlState.Active,
                RequestId: " req-1 ",
                ConsentToken: " token-1 ",
                ControllerPeerId: " peer-1 "));

        Assert.Equal(ControlState.Active, transition.NextState.ControlState);
        Assert.Equal("req-1", transition.NextState.CurrentControlRequestId);
        Assert.Equal("token-1", transition.NextState.ConsentToken);
        Assert.Equal("peer-1", transition.NextState.ControllerPeerId);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.BecameActive);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_SetStateToOff_ClearsContext_AndMarksClearedSideEffect()
    {
        var current = new RemoteControlSessionState(
            ControlState: ControlState.Active,
            ControllerPeerId: "peer-1",
            CurrentControlRequestId: "req-1",
            ConsentToken: "token-1",
            SupportsRemoteControl: true,
            PeerSupportsRemoteControl: true);

        var transition = RemoteControlCoordinator.Apply(
            current,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.SetState,
                "test_off",
                NextControlState: ControlState.Off,
                RequestId: null,
                ConsentToken: null,
                ControllerPeerId: null));

        Assert.Equal(ControlState.Off, transition.NextState.ControlState);
        Assert.Null(transition.NextState.CurrentControlRequestId);
        Assert.Null(transition.NextState.ConsentToken);
        Assert.Null(transition.NextState.ControllerPeerId);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.ClearedControlContext);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_Reset_AlwaysReturnsOffWithClearedContext()
    {
        var current = new RemoteControlSessionState(
            ControlState: ControlState.Requesting,
            ControllerPeerId: "peer-2",
            CurrentControlRequestId: "req-2",
            ConsentToken: "token-2",
            SupportsRemoteControl: true,
            PeerSupportsRemoteControl: false);

        var transition = RemoteControlCoordinator.Apply(
            current,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.Reset,
                "test_reset"));

        Assert.Equal(ControlState.Off, transition.NextState.ControlState);
        Assert.Null(transition.NextState.CurrentControlRequestId);
        Assert.Null(transition.NextState.ConsentToken);
        Assert.Null(transition.NextState.ControllerPeerId);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.ClearedControlContext);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_SyncCapabilities_OnlyMarksChangedWhenFlagsDiffer()
    {
        var current = RemoteControlSessionState.Default with
        {
            SupportsRemoteControl = true,
            PeerSupportsRemoteControl = false,
        };

        var unchanged = RemoteControlCoordinator.Apply(
            current,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.SyncCapabilities,
                "unchanged",
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: false));
        Assert.Equal(RemoteControlCoordinatorSideEffect.None, unchanged.SideEffects);

        var changed = RemoteControlCoordinator.Apply(
            current,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.SyncCapabilities,
                "changed",
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            changed.SideEffects & RemoteControlCoordinatorSideEffect.CapabilitiesChanged);
        Assert.True(changed.NextState.SessionSupportsRemoteControl);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_DisplayInfoChanged_WhenActive_TransitionsOffAndEmitsStopSideEffects()
    {
        var current = new RemoteControlSessionState(
            ControlState: ControlState.Active,
            ControllerPeerId: "peer-1",
            CurrentControlRequestId: "req-1",
            ConsentToken: null,
            SupportsRemoteControl: true,
            PeerSupportsRemoteControl: true);

        var transition = RemoteControlCoordinator.Apply(
            current,
            RemoteControlDisplayInfoState.Empty,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.DisplayInfoChanged,
                "display_info_changed",
                DisplayId: " primary ",
                DisplayRevision: 5,
                VirtualDesktopX: 0,
                VirtualDesktopY: 0,
                VirtualDesktopWidth: 1920,
                VirtualDesktopHeight: 1080,
                CaptureRegionX: 0,
                CaptureRegionY: 0,
                CaptureRegionWidth: 1280,
                CaptureRegionHeight: 720,
                FrameWidth: 1280,
                FrameHeight: 720));

        Assert.Equal(ControlState.Off, transition.NextState.ControlState);
        Assert.Null(transition.NextState.CurrentControlRequestId);
        Assert.Null(transition.NextState.ControllerPeerId);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.SendControlStop);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.FlushLowLane);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.CancelInjectionQueue);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.SetTransientStatus);
        Assert.Equal("DisplayChanged", transition.ControlStopReason);
        Assert.Equal("Screen changed, control stopped", transition.TransientStatusText);
        Assert.Equal("primary", transition.NextDisplayInfo.DisplayId);
        Assert.Equal(5, transition.NextDisplayInfo.Revision);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_DisplayInfoChanged_WhenAlreadyOff_DoesNotEmitStopSideEffects()
    {
        var current = RemoteControlSessionState.Default;
        var currentDisplay = new RemoteControlDisplayInfoState(
            DisplayId: "primary",
            Revision: 8,
            VirtualDesktopX: 0,
            VirtualDesktopY: 0,
            VirtualDesktopWidth: 2560,
            VirtualDesktopHeight: 1440,
            CaptureRegionX: 0,
            CaptureRegionY: 0,
            CaptureRegionWidth: 2560,
            CaptureRegionHeight: 1440,
            FrameWidth: 1920,
            FrameHeight: 1080);

        var first = RemoteControlCoordinator.Apply(
            current,
            currentDisplay,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.DisplayInfoChanged,
                "display_info_changed",
                DisplayId: "primary",
                DisplayRevision: 9));

        Assert.Equal(ControlState.Off, first.NextState.ControlState);
        Assert.Equal(
            RemoteControlCoordinatorSideEffect.None,
            first.SideEffects & RemoteControlCoordinatorSideEffect.SendControlStop);
        Assert.Equal(
            RemoteControlCoordinatorSideEffect.None,
            first.SideEffects & RemoteControlCoordinatorSideEffect.FlushLowLane);
        Assert.Equal(
            RemoteControlCoordinatorSideEffect.None,
            first.SideEffects & RemoteControlCoordinatorSideEffect.CancelInjectionQueue);
        Assert.Null(first.ControlStopReason);
        Assert.Null(first.TransientStatusText);
        Assert.Equal(9, first.NextDisplayInfo.Revision);

        var repeated = RemoteControlCoordinator.Apply(
            first.NextState,
            first.NextDisplayInfo,
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.DisplayInfoChanged,
                "display_info_changed",
                DisplayId: "primary",
                DisplayRevision: 9));

        Assert.Equal(ControlState.Off, repeated.NextState.ControlState);
        Assert.Equal(
            RemoteControlCoordinatorSideEffect.None,
            repeated.SideEffects & RemoteControlCoordinatorSideEffect.SendControlStop);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Apply_DisplayInfoChanged_WhenRequesting_TransitionsOffAndEmitsStopSideEffects()
    {
        var current = new RemoteControlSessionState(
            ControlState: ControlState.Requesting,
            ControllerPeerId: "peer-requesting",
            CurrentControlRequestId: "req-requesting",
            ConsentToken: null,
            SupportsRemoteControl: true,
            PeerSupportsRemoteControl: true);

        var transition = RemoteControlCoordinator.Apply(
            current,
            new RemoteControlDisplayInfoState(
                DisplayId: "primary",
                Revision: 10,
                VirtualDesktopX: 0,
                VirtualDesktopY: 0,
                VirtualDesktopWidth: 2560,
                VirtualDesktopHeight: 1440,
                CaptureRegionX: 0,
                CaptureRegionY: 0,
                CaptureRegionWidth: 2560,
                CaptureRegionHeight: 1440,
                FrameWidth: 1920,
                FrameHeight: 1080),
            new RemoteControlCoordinatorEvent(
                RemoteControlCoordinatorEventKind.DisplayInfoChanged,
                "display_info_changed",
                DisplayId: "primary",
                DisplayRevision: 11));

        Assert.Equal(ControlState.Off, transition.NextState.ControlState);
        Assert.Null(transition.NextState.CurrentControlRequestId);
        Assert.Null(transition.NextState.ControllerPeerId);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.SendControlStop);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.FlushLowLane);
        Assert.NotEqual(
            RemoteControlCoordinatorSideEffect.None,
            transition.SideEffects & RemoteControlCoordinatorSideEffect.CancelInjectionQueue);
        Assert.Equal("DisplayChanged", transition.ControlStopReason);
    }
}
