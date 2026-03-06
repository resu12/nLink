namespace NLink.App.Services.RemoteControl;

internal interface IRemoteCoordinateMapper
{
    bool IsMappingAvailable { get; }

    (int xPx, int yPx) MapNormalizedToVirtualDesktop(double nx, double ny);
}
