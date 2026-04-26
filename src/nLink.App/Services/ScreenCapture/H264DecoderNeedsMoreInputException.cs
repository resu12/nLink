using System;

namespace NLink.App.Services.ScreenCapture;

internal class H264DecoderNeedsMoreInputException : InvalidOperationException
{
    public H264DecoderNeedsMoreInputException(string message)
        : base(message)
    {
    }
}
