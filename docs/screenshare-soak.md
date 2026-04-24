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

For automated screenshare test ownership, use the ScreenShare domain lane:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane ScreenShare -Configuration Debug
```

For the retained Track B closeout safety slice only, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane TrackBRetained -Configuration Debug
```

## Retained Track B Closeout Evidence

The retained Track B analyzer chain is kept as closeout evidence for the parked local runtime boundary, not as the default invitation to continue Track B experimentation in normal repo work.

Keep and use these analyzers together when validating the final local screenshare boundary:

- `Analyze-ScreenShareLatencyRegression.ps1`
- `Analyze-ScreenShareHelperUpstreamLatency.ps1`
- `Analyze-ScreenShareHelperReadyPath.ps1`
- `Analyze-ScreenShareHelperReceivePath.ps1`
- `Analyze-ScreenShareHelperBridgeIngress.ps1`
- `Analyze-ScreenShareHelperNknReceive.ps1`
- `Analyze-ScreenShareHelperWsReceive.ps1`
- `Analyze-ScreenShareHelperSocketReceive.ps1`
- `Analyze-ScreenShareExternalDelivery.ps1`
- `Analyze-ScreenShareExternalTransportHealth.ps1`

These scripts preserve the proof chain that the remaining latency after local Track B work was external to the repo-owned runtime path. If future work revisits that conclusion, do it from a new explicit plan rather than by extending the old Track B investigation line by default. The `TrackBRetained` lane runs the contract tests that keep this analyzer chain wired after the Track C project split.
