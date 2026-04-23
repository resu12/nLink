using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Linq;
using Avalonia.Headless;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.App.Services.RemoteControl;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "RemoteControl")]
public sealed class RemoteControlP6SurfaceViewTests : IClassFixture<RemoteControlP6SurfaceFixture>
{
    private readonly RemoteControlP6SurfaceFixture fixture;

    public RemoteControlP6SurfaceViewTests(RemoteControlP6SurfaceFixture fixture)
    {
        this.fixture = fixture;
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ScreenShareSurface_MouseMoveCoalescing_EmitsAtMostRatePerSecondUnderHeavyUpdates()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var view = new ScreenShareSurfaceView
            {
                CaptureEnabled = true,
                MouseMoveRateHz = 90,
            };

            var tickMethod = typeof(ScreenShareSurfaceView).GetMethod(
                "OnMouseMoveThrottleTick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasPendingField = typeof(ScreenShareSurfaceView).GetField(
                "hasPendingMouseMove",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pendingNxField = typeof(ScreenShareSurfaceView).GetField(
                "pendingMouseMoveNx",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pendingNyField = typeof(ScreenShareSurfaceView).GetField(
                "pendingMouseMoveNy",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var intervalMethod = typeof(ScreenShareSurfaceView).GetMethod(
                "GetMouseMoveThrottleInterval",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(tickMethod);
            Assert.NotNull(hasPendingField);
            Assert.NotNull(pendingNxField);
            Assert.NotNull(pendingNyField);
            Assert.NotNull(intervalMethod);

            var interval = Assert.IsType<TimeSpan>(intervalMethod!.Invoke(null, new object[] { 90 }));
            var maxPerSecond = (int)Math.Floor(1d / interval.TotalSeconds);
            Assert.Equal(90, maxPerSecond);

            var emitted = new List<ControlInputMessageV1>();
            view.RemoteControlInputProduced += (_, e) =>
            {
                emitted.Add(e.Message);
            };

            var expectedLastNx = new List<double>();
            var expectedLastNy = new List<double>();
            for (var tick = 0; tick < maxPerSecond; tick++)
            {
                // Simulate heavy pointer-move bursts between timer ticks.
                for (var burst = 0; burst < 100; burst++)
                {
                    var nx = tick + (burst / 1000d);
                    var ny = tick + (burst / 2000d);
                    pendingNxField!.SetValue(view, nx);
                    pendingNyField!.SetValue(view, ny);
                    hasPendingField!.SetValue(view, true);
                    if (burst == 99)
                    {
                        expectedLastNx.Add(nx);
                        expectedLastNy.Add(ny);
                    }
                }

                tickMethod!.Invoke(view, new object?[] { null, EventArgs.Empty });
            }

            var mouseMoves = emitted.Where(m => string.Equals(m.Kind, "mouse_move", StringComparison.Ordinal)).ToList();
            Assert.Equal(maxPerSecond, mouseMoves.Count);
            Assert.True(mouseMoves.Count <= maxPerSecond);

            for (var i = 0; i < mouseMoves.Count; i++)
            {
                Assert.Equal(expectedLastNx[i], mouseMoves[i].Nx.GetValueOrDefault());
                Assert.Equal(expectedLastNy[i], mouseMoves[i].Ny.GetValueOrDefault());
            }

            await Task.CompletedTask;
            return true;
        }, CancellationToken.None);
    }
}

public sealed class RemoteControlP6SurfaceFixture : IDisposable
{
    public RemoteControlP6SurfaceFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}

