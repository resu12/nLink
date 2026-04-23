namespace NLink.App.Services.ScreenCapture;

internal enum WindowsRawCaptureBackendKind
{
    Unknown = 0,
    WindowsGraphicsCapture = 1,
    DesktopDuplication = 2,
}

internal interface IWindowsRawCaptureBackendDescriptor
{
    WindowsRawCaptureBackendKind BackendKind { get; }
}
