using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using Windows.Media.Capture;

namespace NLink.App.Services;

public sealed class WindowsCameraQrCaptureService : ICameraQrCaptureService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<CameraQrCaptureResult> CapturePhotoAsync(CancellationToken ct)
    {
        if (!IsSupported)
        {
            return new CameraQrCaptureResult(
                IsSuccess: false,
                IsCancelled: false,
                FilePath: null,
                Message: "Camera QR scan is not supported on this platform.");
        }

        try
        {
            var captureUi = new CameraCaptureUI();
            captureUi.PhotoSettings.Format = CameraCaptureUIPhotoFormat.Jpeg;
            captureUi.PhotoSettings.AllowCropping = false;
            captureUi.PhotoSettings.MaxResolution = CameraCaptureUIMaxPhotoResolution.MediumXga;

            var file = await captureUi.CaptureFileAsync(CameraCaptureUIMode.Photo).AsTask(ct).ConfigureAwait(false);
            if (file is null)
            {
                return new CameraQrCaptureResult(
                    IsSuccess: false,
                    IsCancelled: true,
                    FilePath: null,
                    Message: "Camera capture canceled.");
            }

            return new CameraQrCaptureResult(
                IsSuccess: true,
                IsCancelled: false,
                FilePath: file.Path,
                Message: null);
        }
        catch (OperationCanceledException)
        {
            return new CameraQrCaptureResult(
                IsSuccess: false,
                IsCancelled: true,
                FilePath: null,
                Message: "Camera capture canceled.");
        }
        catch (Exception ex)
        {
            return new CameraQrCaptureResult(
                IsSuccess: false,
                IsCancelled: false,
                FilePath: null,
                Message: $"Camera capture failed: {ex.GetType().Name}.");
        }
    }
}
