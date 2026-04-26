using System;

namespace NLink.App.Services;

internal interface IHelperRemoteScreenSharePressurePublishTarget
{
    void PublishHelperRemoteScreenSharePressureState(bool timerDriven);
}

internal sealed class HelperRemoteScreenSharePressurePublisher
{
    private readonly IHelperRemoteScreenSharePressurePublishTarget target;

    public HelperRemoteScreenSharePressurePublisher(IHelperRemoteScreenSharePressurePublishTarget target)
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void Publish()
    {
        Publish(timerDriven: false);
    }

    public void Publish(bool timerDriven)
    {
        target.PublishHelperRemoteScreenSharePressureState(timerDriven);
    }
}
