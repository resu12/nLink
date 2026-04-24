using System.Reflection;
using System.Threading;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "RemoteControl")]
public sealed class RemoteControlCoordinateMathTests : RemoteControlP4TestBase
{
    [Fact]
    public void DefaultRemoteCoordinateMapper_ClampsCoordinates()
    {
        var (x, y) = DefaultRemoteCoordinateMapper.MapNormalizedToBounds(
            nx: -0.2d,
            ny: 1.4d,
            bounds: new RemoteDesktopBounds(Left: 100, Top: 50, Width: 1920, Height: 1080));

        Assert.Equal(100, x);
        Assert.Equal(1129, y);
    }

    [Fact]
    public void DefaultRemoteCoordinateMapper_MapsMidpointForTypicalBounds()
    {
        var (x, y) = DefaultRemoteCoordinateMapper.MapNormalizedToBounds(
            nx: 0.5d,
            ny: 0.5d,
            bounds: new RemoteDesktopBounds(Left: 100, Top: 50, Width: 1920, Height: 1080));

        Assert.Equal(1060, x);
        Assert.Equal(590, y);
    }

    [Fact]
    public void WindowsRemoteInputMath_PixelToAbsoluteCoordinate_ClampsToRange()
    {
        Assert.Equal(0, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: -500, origin: 0, length: 1920));
        Assert.Equal(0, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: 0, origin: 0, length: 1920));
        Assert.Equal(65535, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: 1919, origin: 0, length: 1920));
        Assert.Equal(65535, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: 999999, origin: 0, length: 1920));
    }

    [Fact]
    public void WindowsRemoteInputMath_ScaleWheelDelta_UsesWheelTicks()
    {
        Assert.Equal(120, WindowsRemoteInputMath.ScaleWheelDelta(1));
        Assert.Equal(-240, WindowsRemoteInputMath.ScaleWheelDelta(-2));
        Assert.Equal(int.MaxValue, WindowsRemoteInputMath.ScaleWheelDelta(int.MaxValue));
        Assert.Equal(int.MinValue, WindowsRemoteInputMath.ScaleWheelDelta(int.MinValue));
    }
}
