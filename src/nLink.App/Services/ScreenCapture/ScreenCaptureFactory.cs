using System;
using System.Runtime.InteropServices;
using NLink.App.Configuration;

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
    /// Creates a capture source for the requested pipeline kind.
    /// </summary>
    public static IScreenCaptureSource Create(ScreenCapturePipelineKind pipelineKind)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new NotSupportedCaptureSource();
        }

        return pipelineKind switch
        {
            ScreenCapturePipelineKind.H264 => new WindowsH264ScreenCaptureSource(ScreenCaptureTargetStore.Load(), sourceRole: "preview"),
            _ => new NotSupportedCaptureSource(),
        };
    }

    /// <summary>
    /// Creates the default capture source for the current operating system.
    /// </summary>
    /// <returns>A supported capture source on Windows, otherwise a non-supported stub.</returns>
    public static IScreenCaptureSource CreateDefault()
    {
        return CreateDefault(() => WindowsH264ScreenCaptureSource.IsPreviewRuntimeSupported());
    }

    public static IScreenCaptureSource CreateForTransport()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new NotSupportedCaptureSource();
        }

        if (WindowsH264ScreenCaptureSource.IsRuntimeSupported())
        {
            return new WindowsH264ScreenCaptureSource(ScreenCaptureTargetStore.Load(), sourceRole: "transport");
        }

        return new NotSupportedCaptureSource();
    }

    internal static IScreenCaptureSource CreateDefault(Func<bool> h264RuntimeSupportResolver, bool requirePreviewOptIn = true)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (h264RuntimeSupportResolver())
            {
                return Create(ScreenCapturePipelineKind.H264);
            }

            return new NotSupportedCaptureSource();
        }

        return new NotSupportedCaptureSource();
    }
}
