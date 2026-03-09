# ScreenShare Soak

Manual long-run screenshare soak is available from the app CLI and is not intended for CI.

Run on a Windows desktop session with a visible primary display:

```powershell
dotnet run --project src/nLink.App -c Release -- --screenshare-soak --seconds 300
```

Optional sample interval override:

```powershell
dotnet run --project src/nLink.App -c Release -- --screenshare-soak --seconds 300 --sample-interval-seconds 10
```

The runner prints periodic snapshots:

- `FramesCaptured`
- `FramesSent`
- `FramesDropped`
- `FramesDroppedByRateGate`
- `FramesDroppedByQueueEvict`
- `FramesCompleted`
- `DecodeErrors`
- `EnqueueFailures`
- `DisplayInfoSends`
- `AvgCaptureToEnqueueMs`
- `AvgEnqueueToSendMs`
- `AvgCaptureToSendMs`
- `LastCaptureToSendAgeMs`
- `AvgRawFrameBytes`
- `AvgSerializedChunkBytes`
- `AvgBridgeBytes`
- `AvgRenderIntervalMs`
- `AvgCaptureToRenderMs`
- `StaleFrameRenders`

On shutdown it:

1. Stops capture
2. Disposes the sender pipeline
3. Waits for viewer decode work to become idle
4. Verifies metrics stabilize before reporting success

Use this for 5–10 minute stability validation before release or after screenshare pipeline changes.
