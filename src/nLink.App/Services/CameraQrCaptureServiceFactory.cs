using System;

namespace NLink.App.Services;

public static class CameraQrCaptureServiceFactory
{
    public static ICameraQrCaptureService CreateDefault()
    {
        var type = Type.GetType("NLink.App.Services.WindowsCameraQrCaptureService, nLink", throwOnError: false);
        if (type is not null &&
            Activator.CreateInstance(type) is ICameraQrCaptureService supported &&
            supported.IsSupported)
        {
            return supported;
        }

        return new UnsupportedCameraQrCaptureService();
    }
}
