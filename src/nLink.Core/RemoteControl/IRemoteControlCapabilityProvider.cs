namespace NLink.Core.RemoteControl;

public interface IRemoteControlCapabilityProvider
{
    bool LocalSupportsRemoteControl { get; }
    bool RemoteSupportsRemoteControl { get; }
    bool SessionSupportsRemoteControl { get; }
}
