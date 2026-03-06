using System;
using NLink.Core.RemoteControl;

namespace NLink.App.Views;

public sealed class RemoteControlInputProducedEventArgs : EventArgs
{
    public RemoteControlInputProducedEventArgs(ControlInputMessageV1 message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public ControlInputMessageV1 Message { get; }
}
