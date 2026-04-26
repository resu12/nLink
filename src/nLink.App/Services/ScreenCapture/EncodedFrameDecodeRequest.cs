using System;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct EncodedFrameDecodeRequest(
    string Encoding,
    ReadOnlyMemory<byte> EncodedFrameBytes,
    bool IsKeyFrame = false,
    long StreamEpoch = 0,
    long FrameId = -1,
    string SessionId = "",
    bool RequiresReservedApply = false,
    bool BypassesAgeBudget = false,
    ScreenShareRecoveryDeliveryClass RecoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal,
    long FrameReadyObservedUtcMs = 0,
    long ViewerAcceptedUtcMs = 0,
    long DecodeEnqueuedUtcMs = 0,
    long DecodeStartedUtcMs = 0);
