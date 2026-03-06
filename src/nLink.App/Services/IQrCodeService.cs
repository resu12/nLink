using System.IO;

namespace NLink.App.Services;

public interface IQrCodeService
{
    bool TryCreatePng(string text, out byte[] pngBytes, out string? errorMessage);

    bool TryDecode(Stream imageStream, out string? decodedText, out string? errorMessage);
}
