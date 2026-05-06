# Screen Sharing Implementation

This document describes the current nLink screen-sharing implementation. It is an engineering reference for the runtime pipeline. For operator flow, support evidence, and soak commands, start with [`docs/screenshare-operability.md`](screenshare-operability.md).

## Current Status

- Platform: Windows x64.
- Default media path: H.264 screen video.
- Sender role: the helpee captures and sends screen media.
- Receiver role: the helper receives, decodes, and renders screen media.
- Consent boundary: screen sharing starts only inside an approved and verified nLink session with the screen-share capability granted.
- Transport boundary: normal NKN remains the default. Experimental Tuna can carry only `MsgType.ScreenShareFrame` on the media lane after session-bound Tuna negotiation succeeds.

Screen-share control messages are not accelerated by Tuna. Stop, config, keyframe, recovery, cursor, and pressure messages stay on the current NKN control path.

## High-Level Flow

1. The helper and helpee complete the normal nLink invite, approval, and verified session handshake.
2. The helpee grants screen-share capability and selects a display.
3. `SessionRuntime` starts the screen-share coordinator on the helpee side.
4. The Windows capture source produces frames and the H.264 encoder produces video access units.
5. `TransportScreenShareCoordinator` fragments the encoded payload into nLink screen-share frames.
6. `NknSignalingTransport` wraps each frame in the secure session envelope and sends it over the media channel.
7. The helper-side transport validates the source, session, replay window, and secure payload before raising `ScreenShareFrameCompleted`.
8. `ScreenShareViewerViewModel` decodes the completed H.264 stream data and updates `ScreenShareSurfaceView`.
9. Pressure, cursor, recovery, and keyframe control messages continue over the secure NKN control path.

## Main Components

### Core Contracts

- `src/nLink.Core/ScreenShare/IScreenShareSignalingTransport.cs`
  Defines the screen-share signaling surface used by the app. It includes frame delivery, stop, pressure, recovery receipt, stream config, keyframe request, and cursor-state messages.
- `src/nLink.Core/ScreenShare/ScreenShareVideoFragmenter.cs`
  Splits encoded H.264 payloads into bounded fragments for transport.
- `src/nLink.Core/ScreenShare/ScreenShareVideoFrameReassembler.cs`
  Reassembles fragments back into complete frames on the receiver side.
- `src/nLink.Core/ScreenShare/ScreenShareVideoPayloadCodec.cs`
  Encodes and decodes video payload metadata and bytes.
- `src/nLink.Core/ScreenShare/ScreenSharePressureStateV1.cs`
  Carries receiver pressure and health information back to the sender.
- `src/nLink.Core/ScreenShare/ScreenShareRecoveryReceiptV1.cs`
  Carries receiver recovery and applied-frame receipts.
- `src/nLink.Core/ScreenShare/ScreenShareCursorStateV1.cs`
  Carries cursor overlay state.

### Sender App Path

- `src/nLink.App/Services/ScreenCapture/TransportScreenShareCoordinator.cs`
  Owns the sender-side lifecycle, capture subscription, transport pacing, freshness control, diagnostics, and recovery state.
- `src/nLink.App/Services/ScreenCapture/TransportScreenShareCoordinator.FramePipeline.cs`
  Handles frame pipeline details and transport send decisions.
- `src/nLink.App/Screenshare/Capture/Windows/WindowsH264ScreenCaptureSource.cs`
  Provides the Windows H.264 capture source.
- `src/nLink.App/Screenshare/Capture/Windows/MediaFoundationH264FrameEncoder.cs`
  Encodes frames through Media Foundation and contains motion/keyframe safeguards.
- `src/nLink.App/Screenshare/Capture/Windows/*GraphicsCapture*`
  Supports Windows Graphics Capture based capture where available.
- `src/nLink.App/Screenshare/Capture/Windows/*DesktopDuplication*`
  Provides the desktop duplication fallback path.

The sender is responsible for keeping the stream fresh. When downstream delivery is congested, the coordinator can enter catch-up behavior, flush stale queued media, request or force keyframes, and adjust how aggressively frames are sent.

### Receiver App Path

- `src/nLink.App/ViewModels/ScreenShareViewerViewModel.cs`
  Owns helper-side screen-share state, receive/decode flow, visible status, and stale-frame handling.
- `src/nLink.App/ViewModels/ScreenShareViewerViewModel.HelperRemoteSession.cs`
  Connects helper remote-session behavior to the viewer.
- `src/nLink.App/Views/ScreenShareSurfaceView.axaml.cs`
  Displays decoded frames.
- `src/nLink.App/Screenshare/Decode/MediaFoundationH264BitmapDecoder.cs`
  Primary Windows H.264 decode backend.
- `src/nLink.App/Screenshare/Decode/FfmpegH264BitmapDecoder.cs`
  Fallback decoder backend when available.

The receiver validates and reassembles before decode. Decode output can still be dropped as stale if it arrives too late to improve the current visible surface.

### Session Control

- `src/nLink.App/Services/SessionRuntime.ScreenShareControl.cs`
  Wires session-level start, stop, and permission behavior.
- `src/nLink.App/Services/SessionRuntime.ScreenSharePressurePublishing.cs`
  Publishes helper-side screen-share pressure back to the sender.
- `src/nLink.App/Services/SessionRuntimeScreenShareControlHost.cs`
  Hosts screen-share control behavior for the active session.
- `src/nLink.App/Services/HelperRemoteScreenShareSessionController.cs`
  Coordinates helper-side screen-share session actions.

## Transport And Routing

`NknSignalingTransport` implements `IScreenShareSignalingTransport`. It creates a secure nLink envelope for each screen-share payload, sends media frames through the media lane, and routes control messages through the normal control path.

Important routing rules:

- `MsgType.ScreenShareFrame` is the only screen-share payload eligible for Tuna.
- It must be sent on `NknBridgeChannel.Media` to be eligible.
- Screen-share stop, stream config, keyframe request, recovery receipt, cursor state, and pressure state stay on current NKN.
- Tuna is never a replacement for nLink's application-level session envelope, source validation, replay protection, or capability checks.

When Tuna is enabled and negotiated, accelerated inbound screen frames are injected back into the same envelope router. The normal validation path remains authoritative.

## Control Message Families

- Frame media: encoded H.264 fragments and frame metadata.
- Stop: sender or session lifecycle stop notification.
- Stream config: stream epoch, codec/config details, and receiver reset hints.
- Keyframe request: helper asks sender for an IDR/keyframe.
- Recovery receipt: helper reports applied frame and recovery state.
- Cursor state: helper-side cursor overlay metadata.
- Pressure state: helper reports queue, decode, render, or delivery pressure.

## Security And Consent

Screen sharing is session-bound:

- The invite and approval flow must already be complete.
- The verified session handshake must be complete.
- The screen-share capability must be granted.
- Envelopes are tied to the active session and expected peer identity.
- Replay windows and sequencing remain active.
- Tuna transport encryption, when present, is treated as additive only.

No screen media or control path should bypass the nLink envelope model.

## Flow Control And Recovery

Screen sharing is tuned for freshness, not guaranteed delivery of every historical frame.

Key behaviors:

- Media frames are bounded before transport.
- The transport exposes queue depth, queued bytes, oldest queued age, recent drops, and degraded state through `IScreenShareTransportBackpressureProbe`.
- The sender can enter catch-up mode through `IScreenShareTransportPolicyController`.
- The sender can flush the screen-share transport queue when stale media is harmful.
- Receiver pressure can reduce sender aggressiveness.
- Keyframe and recovery receipts repair broken H.264 reference chains.
- Stale post-decode frames can be dropped rather than rendered late.

This is why a small number of stale drops can be normal during live NKN or Tuna sessions when the visible stream remains healthy.

## Options And Presets

Options -> Settings exposes four screen-share presets:

| Preset | Capture FPS | Transport FPS | Max transport target | Scale | Quality profile |
|---|---:|---:|---:|---:|---|
| Balanced | 15 | 8 | 1440x810 | 1.0 | `normal` |
| High quality | 24 | 15 | 1440x810 | 1.0 | `normal` |
| Tuna quality | 30 | 15 | 1600x900 | 1.0 | `tuna_quality` |
| High performance | 10 | 6 | 864x486 | 0.6 | `normal` |

`Tuna quality` uses more bandwidth and is recommended only when Tuna acceleration is enabled. Current NKN remains available as fallback, and the sender can still auto-reduce if delivery becomes congested.

The presets write the same process/user settings used by the runtime:

- `NLINK_FEATURE_SCREENCAP_MAX_FPS`
- `NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS`
- `NLINK_FEATURE_SCREENCAP_SCALE`
- `NLINK_FEATURE_SCREENCAP_QUALITY_PROFILE`

The app also reads `NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE`, which is enabled by default.

## Diagnostics

The main support surfaces are:

- Options -> Diagnostics -> Copy diagnostics.
- Options -> Diagnostics -> Save Hang Report.
- `tools/ScreenShare-Ops.ps1`.
- Retained screen-share soak artifacts under `artifacts/soak/<timestamp>/`.

Useful evidence includes bridge health, screen-share lane counters, queue pressure, decoder backend selection, visual safety summaries, low-FPS/catch-up summaries, and live NKN transport summaries.

## Current Limits

- Windows x64 is the supported release target.
- Live latency depends on NKN and network delivery.
- H.264 quality depends on capture source, encoder behavior, decoder behavior, and stream recovery.
- Screen sharing is real-time media. It prefers freshness over preserving every outdated frame.
- Tuna remains experimental and optional. Current NKN remains the default and fallback path.

## Validation

For code changes near screen sharing, start with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode Test -Configuration Debug
```

For operator flow and artifact interpretation, use [`docs/screenshare-operability.md`](screenshare-operability.md).
