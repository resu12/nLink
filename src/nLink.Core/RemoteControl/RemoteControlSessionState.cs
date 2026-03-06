namespace NLink.Core.RemoteControl;

public readonly record struct RemoteControlSessionState(
    ControlState ControlState,
    string? ControllerPeerId,
    string? CurrentControlRequestId,
    string? ConsentToken,
    bool SupportsRemoteControl,
    bool PeerSupportsRemoteControl)
{
    public static RemoteControlSessionState Default =>
        new(
            ControlState: ControlState.Off,
            ControllerPeerId: null,
            CurrentControlRequestId: null,
            ConsentToken: null,
            SupportsRemoteControl: true,
            PeerSupportsRemoteControl: false);

    public bool SessionSupportsRemoteControl => SupportsRemoteControl && PeerSupportsRemoteControl;

    public bool RemoteControlAvailable =>
        SessionSupportsRemoteControl &&
        ControlState is ControlState.Off or ControlState.Requesting or ControlState.Active;
}
