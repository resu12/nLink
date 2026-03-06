using System;
using NLink.Core.RemoteControl;

namespace NLink.App.Services.RemoteControl;

[Flags]
internal enum RemoteControlCoordinatorSideEffect
{
    None = 0,
    BecameActive = 1 << 0,
    ClearedControlContext = 1 << 1,
    CapabilitiesChanged = 1 << 2,
    SendControlStop = 1 << 3,
    FlushLowLane = 1 << 4,
    CancelInjectionQueue = 1 << 5,
    SetTransientStatus = 1 << 6,
    DisplayInfoUpdated = 1 << 7,
}

internal enum RemoteControlCoordinatorEventKind
{
    SetState = 0,
    Reset = 1,
    SyncCapabilities = 2,
    DisplayInfoChanged = 3,
}

internal readonly record struct RemoteControlDisplayInfoState(
    string? DisplayId,
    long Revision,
    int? VirtualDesktopX,
    int? VirtualDesktopY,
    int? VirtualDesktopWidth,
    int? VirtualDesktopHeight,
    int? CaptureRegionX,
    int? CaptureRegionY,
    int? CaptureRegionWidth,
    int? CaptureRegionHeight,
    int? FrameWidth,
    int? FrameHeight)
{
    public static RemoteControlDisplayInfoState Empty =>
        new(
            DisplayId: null,
            Revision: 0,
            VirtualDesktopX: null,
            VirtualDesktopY: null,
            VirtualDesktopWidth: null,
            VirtualDesktopHeight: null,
            CaptureRegionX: null,
            CaptureRegionY: null,
            CaptureRegionWidth: null,
            CaptureRegionHeight: null,
            FrameWidth: null,
            FrameHeight: null);
}

internal readonly record struct RemoteControlCoordinatorEvent(
    RemoteControlCoordinatorEventKind Kind,
    string Reason,
    ControlState? NextControlState = null,
    string? RequestId = null,
    string? ConsentToken = null,
    string? ControllerPeerId = null,
    bool? SupportsRemoteControl = null,
    bool? PeerSupportsRemoteControl = null,
    string? DisplayId = null,
    long? DisplayRevision = null,
    int? VirtualDesktopX = null,
    int? VirtualDesktopY = null,
    int? VirtualDesktopWidth = null,
    int? VirtualDesktopHeight = null,
    int? CaptureRegionX = null,
    int? CaptureRegionY = null,
    int? CaptureRegionWidth = null,
    int? CaptureRegionHeight = null,
    int? FrameWidth = null,
    int? FrameHeight = null);

internal readonly record struct RemoteControlCoordinatorResult(
    RemoteControlSessionState PreviousState,
    RemoteControlSessionState NextState,
    RemoteControlCoordinatorSideEffect SideEffects,
    RemoteControlDisplayInfoState PreviousDisplayInfo,
    RemoteControlDisplayInfoState NextDisplayInfo,
    string? ControlStopReason = null,
    string? TransientStatusText = null);

internal static class RemoteControlCoordinator
{
    private const string DisplayChangedStopReason = "DisplayChanged";
    private const string DisplayChangedStatusText = "Screen changed, control stopped";

    public static RemoteControlCoordinatorResult Apply(
        RemoteControlSessionState current,
        in RemoteControlCoordinatorEvent evt)
    {
        return Apply(current, RemoteControlDisplayInfoState.Empty, evt);
    }

    public static RemoteControlCoordinatorResult Apply(
        RemoteControlSessionState current,
        in RemoteControlDisplayInfoState currentDisplayInfo,
        in RemoteControlCoordinatorEvent evt)
    {
        var next = current;
        var nextDisplayInfo = currentDisplayInfo;
        var sideEffects = RemoteControlCoordinatorSideEffect.None;
        string? controlStopReason = null;
        string? transientStatusText = null;

        switch (evt.Kind)
        {
            case RemoteControlCoordinatorEventKind.SetState:
            {
                if (!evt.NextControlState.HasValue)
                {
                    throw new ArgumentException("NextControlState is required for SetState transitions.", nameof(evt));
                }

                next = next with
                {
                    ControlState = evt.NextControlState.Value,
                    CurrentControlRequestId = Normalize(evt.RequestId),
                    ConsentToken = Normalize(evt.ConsentToken),
                    ControllerPeerId = Normalize(evt.ControllerPeerId),
                };

                if (current.ControlState != ControlState.Active &&
                    next.ControlState == ControlState.Active)
                {
                    sideEffects |= RemoteControlCoordinatorSideEffect.BecameActive;
                }

                if (next.ControlState == ControlState.Off &&
                    (current.ControlState != ControlState.Off ||
                     !string.IsNullOrWhiteSpace(current.CurrentControlRequestId) ||
                     !string.IsNullOrWhiteSpace(current.ConsentToken) ||
                     !string.IsNullOrWhiteSpace(current.ControllerPeerId)))
                {
                    sideEffects |= RemoteControlCoordinatorSideEffect.ClearedControlContext;
                }

                break;
            }

            case RemoteControlCoordinatorEventKind.Reset:
            {
                next = next with
                {
                    ControlState = ControlState.Off,
                    CurrentControlRequestId = null,
                    ConsentToken = null,
                    ControllerPeerId = null,
                };

                if (current.ControlState != ControlState.Off ||
                    !string.IsNullOrWhiteSpace(current.CurrentControlRequestId) ||
                    !string.IsNullOrWhiteSpace(current.ConsentToken) ||
                    !string.IsNullOrWhiteSpace(current.ControllerPeerId))
                {
                    sideEffects |= RemoteControlCoordinatorSideEffect.ClearedControlContext;
                }

                if (!currentDisplayInfo.Equals(RemoteControlDisplayInfoState.Empty))
                {
                    nextDisplayInfo = RemoteControlDisplayInfoState.Empty;
                    sideEffects |= RemoteControlCoordinatorSideEffect.DisplayInfoUpdated;
                }

                break;
            }

            case RemoteControlCoordinatorEventKind.SyncCapabilities:
            {
                var supports = evt.SupportsRemoteControl ?? current.SupportsRemoteControl;
                var peerSupports = evt.PeerSupportsRemoteControl ?? current.PeerSupportsRemoteControl;
                next = next with
                {
                    SupportsRemoteControl = supports,
                    PeerSupportsRemoteControl = peerSupports,
                };

                if (supports != current.SupportsRemoteControl ||
                    peerSupports != current.PeerSupportsRemoteControl)
                {
                    sideEffects |= RemoteControlCoordinatorSideEffect.CapabilitiesChanged;
                }

                break;
            }

            case RemoteControlCoordinatorEventKind.DisplayInfoChanged:
            {
                var updatedDisplayInfo = MergeDisplayInfo(currentDisplayInfo, evt);
                if (!updatedDisplayInfo.Equals(currentDisplayInfo))
                {
                    nextDisplayInfo = updatedDisplayInfo;
                    sideEffects |= RemoteControlCoordinatorSideEffect.DisplayInfoUpdated;
                }

                if (current.ControlState is ControlState.Active or ControlState.Requesting)
                {
                    next = next with
                    {
                        ControlState = ControlState.Off,
                        CurrentControlRequestId = null,
                        ConsentToken = null,
                        ControllerPeerId = null,
                    };
                    sideEffects |= RemoteControlCoordinatorSideEffect.ClearedControlContext;
                    sideEffects |= RemoteControlCoordinatorSideEffect.SendControlStop;
                    sideEffects |= RemoteControlCoordinatorSideEffect.FlushLowLane;
                    sideEffects |= RemoteControlCoordinatorSideEffect.CancelInjectionQueue;
                    sideEffects |= RemoteControlCoordinatorSideEffect.SetTransientStatus;
                    controlStopReason = DisplayChangedStopReason;
                    transientStatusText = DisplayChangedStatusText;
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(evt.Kind), evt.Kind, "Unknown remote control coordinator event.");
        }

        return new RemoteControlCoordinatorResult(
            PreviousState: current,
            NextState: next,
            SideEffects: sideEffects,
            PreviousDisplayInfo: currentDisplayInfo,
            NextDisplayInfo: nextDisplayInfo,
            ControlStopReason: controlStopReason,
            TransientStatusText: transientStatusText);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static RemoteControlDisplayInfoState MergeDisplayInfo(
        in RemoteControlDisplayInfoState current,
        in RemoteControlCoordinatorEvent evt)
    {
        return new RemoteControlDisplayInfoState(
            DisplayId: Normalize(evt.DisplayId) ?? current.DisplayId,
            Revision: evt.DisplayRevision.GetValueOrDefault(current.Revision),
            VirtualDesktopX: evt.VirtualDesktopX ?? current.VirtualDesktopX,
            VirtualDesktopY: evt.VirtualDesktopY ?? current.VirtualDesktopY,
            VirtualDesktopWidth: evt.VirtualDesktopWidth ?? current.VirtualDesktopWidth,
            VirtualDesktopHeight: evt.VirtualDesktopHeight ?? current.VirtualDesktopHeight,
            CaptureRegionX: evt.CaptureRegionX ?? current.CaptureRegionX,
            CaptureRegionY: evt.CaptureRegionY ?? current.CaptureRegionY,
            CaptureRegionWidth: evt.CaptureRegionWidth ?? current.CaptureRegionWidth,
            CaptureRegionHeight: evt.CaptureRegionHeight ?? current.CaptureRegionHeight,
            FrameWidth: evt.FrameWidth ?? current.FrameWidth,
            FrameHeight: evt.FrameHeight ?? current.FrameHeight);
    }
}
