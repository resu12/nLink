using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class ScreenShareCursorStateCodecTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void CursorStateCodec_RoundTripsValidState()
    {
        var state = new ScreenShareCursorStateV1
        {
            SessionId = "session-1",
            Seq = 42,
            TsUtcMs = 1_775_000_000_000,
            DisplayId = "display-main",
            DisplayInfoRevision = 7,
            Nx = 0.25,
            Ny = 0.75,
            Visible = true,
            Source = "wgc",
            Status = "captured_cursor_disabled",
            CapturedCursorEnabled = false,
            CursorCaptureControlSupported = true,
        };

        var json = ScreenShareCursorStateCodec.Serialize(state);
        Assert.True(ScreenShareCursorStateCodec.TryDeserialize(json, out var decoded));

        Assert.Equal(ScreenShareCursorStateProtocol.Kind, decoded.Kind);
        Assert.Equal(ScreenShareCursorStateProtocol.CursorStateTypeV1, decoded.Type);
        Assert.Equal(state.SessionId, decoded.SessionId);
        Assert.Equal(state.Seq, decoded.Seq);
        Assert.Equal(state.TsUtcMs, decoded.TsUtcMs);
        Assert.Equal(state.DisplayId, decoded.DisplayId);
        Assert.Equal(state.DisplayInfoRevision, decoded.DisplayInfoRevision);
        Assert.Equal(state.Nx, decoded.Nx, precision: 3);
        Assert.Equal(state.Ny, decoded.Ny, precision: 3);
        Assert.True(decoded.Visible);
        Assert.Equal("wgc", decoded.Source);
        Assert.False(decoded.CapturedCursorEnabled);
        Assert.True(decoded.CursorCaptureControlSupported);
    }

    [Theory]
    [InlineData("", 0.5, 0.5)]
    [InlineData("session-1", -0.01, 0.5)]
    [InlineData("session-1", 0.5, 1.01)]
    public void CursorStateCodec_RejectsInvalidIdentityOrCoordinates(string sessionId, double nx, double ny)
    {
        var state = new ScreenShareCursorStateV1
        {
            SessionId = sessionId,
            Seq = 1,
            TsUtcMs = 1,
            DisplayId = "display-main",
            DisplayInfoRevision = 1,
            Nx = nx,
            Ny = ny,
        };

        Assert.ThrowsAny<ArgumentException>(() => ScreenShareCursorStateCodec.Serialize(state));
    }
}
