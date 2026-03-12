using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed class LegacyScreenShareMediaTransportAdapter : IScreenShareMediaTransport
{
    private readonly IScreenShareSignalingTransport signalingTransport;
    private readonly IScreenShareTransportBackpressureProbe? backpressureProbe;

    public LegacyScreenShareMediaTransportAdapter(
        IScreenShareSignalingTransport signalingTransport,
        IScreenShareTransportBackpressureProbe? backpressureProbe = null)
    {
        this.signalingTransport = signalingTransport ?? throw new ArgumentNullException(nameof(signalingTransport));
        this.backpressureProbe = backpressureProbe;
    }

    public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted
    {
        add => signalingTransport.ScreenShareFrameCompleted += value;
        remove => signalingTransport.ScreenShareFrameCompleted -= value;
    }

    public event EventHandler? ScreenShareStopped
    {
        add => signalingTransport.ScreenShareStopped += value;
        remove => signalingTransport.ScreenShareStopped -= value;
    }

    public bool IsCongested => backpressureProbe?.IsScreenShareTransportCongested ?? false;

    public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => signalingTransport.SendScreenSharePayloadAsync(payload, ct);
}
