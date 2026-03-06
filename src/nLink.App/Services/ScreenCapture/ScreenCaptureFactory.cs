using System;
using System.Runtime.InteropServices;

namespace NLink.App.Services.ScreenCapture;

/// <summary>
/// Creates a platform-appropriate screen-capture source.
/// </summary>
public static class ScreenCaptureFactory
{
    /// <summary>
    /// Creates the capture source for the current operating system.
    /// </summary>
    /// <returns>A supported capture source on Windows, otherwise a non-supported stub.</returns>
    public static IScreenCaptureSource Create() => CreateDefault();

    /// <summary>
    /// Creates the default capture source for the current operating system.
    /// </summary>
    /// <returns>A supported capture source on Windows, otherwise a non-supported stub.</returns>
    public static IScreenCaptureSource CreateDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // TODO(v0.5.0-P7): resolve capture source from monitor/region selection once
            // the selection UI and persisted capture-target settings are implemented.
            var type = Type.GetType("NLink.App.Services.ScreenCapture.WindowsScreenCaptureSource, nLink", throwOnError: false);
            if (type is not null &&
                Activator.CreateInstance(type, nonPublic: true) is IScreenCaptureSource captureSource)
            {
                return captureSource;
            }
        }

        return new NotSupportedCaptureSource();
    }
}
