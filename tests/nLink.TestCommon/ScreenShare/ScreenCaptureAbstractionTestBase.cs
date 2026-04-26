using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using System.Collections.Concurrent;
using System.Reflection;

namespace NLink.SmokeTests;

public abstract class ScreenCaptureAbstractionTestBase
{
    protected static ScreenShareVideoStreamConfigV1 CreateVideoStreamConfig(string sessionId, long streamEpoch)
    {
        return new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[]
            {
                1,
                2,
                3
            },
        };
    }

    protected static byte[] CreateVideoFragmentPayload(string sessionId, long frameId, int width, int height, byte[] frameBytes, long streamEpoch, long capturedTsUtcMs = 0, bool isKeyFrame = true, int fragmentIndex = 0, int fragmentCount = 1)
    {
        return ScreenShareVideoPayloadCodec.SerializeFragment(new ScreenShareVideoFragmentV1 { SessionId = sessionId, StreamEpoch = streamEpoch, FrameId = frameId, Width = width, Height = height, CapturedTsUtcMs = capturedTsUtcMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : capturedTsUtcMs, Encoding = "h264", IsKeyFrame = isKeyFrame, FragmentIndex = fragmentIndex, FragmentCount = fragmentCount, Data = frameBytes, });
    }

    protected static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

}

