using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using QRCoder;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace NLink.App.Services;

public sealed class QrCodeService : IQrCodeService
{
    private const int DefaultPixelsPerModule = 8;

    public bool TryCreatePng(string text, out byte[] pngBytes, out string? errorMessage)
    {
        pngBytes = Array.Empty<byte>();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            errorMessage = "QR content is empty.";
            return false;
        }

        try
        {
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(text.Trim(), QRCodeGenerator.ECCLevel.Q);
            var qr = new PngByteQRCode(qrData);
            pngBytes = qr.GetGraphic(DefaultPixelsPerModule);
            return pngBytes.Length > 0;
        }
        catch (Exception ex)
        {
            errorMessage = $"QR generation failed: {ex.GetType().Name}.";
            return false;
        }
    }

    public bool TryDecode(Stream imageStream, out string? decodedText, out string? errorMessage)
    {
        decodedText = null;
        errorMessage = null;

        if (imageStream is null)
        {
            errorMessage = "No image stream provided.";
            return false;
        }

        try
        {
            using var copy = new MemoryStream();
            imageStream.CopyTo(copy);
            copy.Position = 0;

            using var bitmap = new Bitmap(copy);
            var source = new BitmapLuminanceSource(bitmap);

            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                },
            };

            var result = reader.Decode(source);
            if (result is null || string.IsNullOrWhiteSpace(result.Text))
            {
                errorMessage = "QR code not found in image.";
                return false;
            }

            decodedText = result.Text.Trim();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"QR decode failed: {ex.GetType().Name}.";
            return false;
        }
    }
}
