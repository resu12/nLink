# ScreenShare Soak

For the operator decision tree and Track D closeout state, start with `docs/screenshare-operability.md` and `tools\ScreenShare-Ops.ps1`. This file is the technical reference for local soak output and retained Track B closeout evidence.

For live NKN artifacts, the first operator-facing artifact to read is `screenshare-operator-verdict.txt`. Produce it with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode AnalyzeRetained -ArtifactDir artifacts\soak\<timestamp>
```

Only open the individual retained analyzer files after the verdict points to a specific diagnostic path.

For low-quality or blurry/pixelated reports, open `quality-presentation-summary.txt` next. It keeps encode target, preset, sender mode, helper surface size, render scaling, and interpolation evidence with the soak artifact, so quality and transport classification can stay separate.

For low-FPS, cursor-tail, or catch-up reports, open `low-fps-catch-up-summary.txt` after the quality summary. It keeps active target FPS, inferred visible apply FPS, sender mode counts/transitions, helper pressure/recovery evidence, capture/encode blockers, local queue/drop/send-failure fields, and external delivery classification together so FPS reduction is not confused with presentation smoothing or external delivery latency.

For external NKN delivery/topology work, open `external-topology-summary.txt` after the verdict. It records the operator topology profile, selected RPC key/stage, control/media/bulk subclient counts, socket receive latency, health churn, local queue/send gates, and the next action. Compare profile runs with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode ExternalTopologyAudit -ArtifactDirs "artifacts\soak\<default>;artifacts\soak\<candidate>"
```

For support capture, copy app Diagnostics first. The Diagnostics page reads existing screenshare evidence and includes a compact `Screenshare evidence` block when `screenshare-operator-verdict.txt` is available. For hangs, use Save Hang Report; hang report folders include `screenshare-evidence.txt`. Attach the full soak artifact only when that evidence points to one or support asks for raw retained analyzer output.

Manual long-run screenshare soak is available from the app CLI and is not intended for CI.

Run on a Windows desktop session with a visible primary display:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode LocalSoak -Configuration Release -DurationSeconds 300
```

Optional sample interval override:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode LocalSoak -Configuration Release -DurationSeconds 300 -SampleIntervalSeconds 10
```

The wrapper delegates to:

```powershell
dotnet run --project src/nLink.App -c Release -- --screenshare-soak --seconds 300
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
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode Test -Configuration Debug
```

For the retained Track B closeout safety slice only, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode TrackBRetained -Configuration Debug
```

## Retained Track B Closeout Evidence

The retained Track B analyzer chain is kept as closeout evidence for the parked local runtime boundary, not as the default invitation to continue Track B experimentation in normal repo work. `AnalyzeRetained` now writes `low-fps-catch-up-summary.txt` and `screenshare-operator-verdict.txt`; read the verdict first, then `quality-presentation-summary.txt`, then the low-FPS summary, before opening individual retained analyzer files.

`AnalyzeRetained` also writes `external-topology-summary.txt` when enough live topology evidence is present. This keeps NKN route variance, RPC selection, and media fanout evidence separate from sender/helper tuning.

The current promoted NKN topology default keeps control/bulk clients at 4 subclients and uses 8 media subclients. The `MediaFanout8` operator profile remains available for explicit confirmation runs.

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
