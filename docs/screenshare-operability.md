# ScreenShare Operability

Use this guide to choose the right screenshare validation or support path. Track D keeps the operator model simple: start with the smallest flow that answers the question, and use the retained Track B evidence only when you are explicitly validating the parked latency boundary.

## Flow Matrix

| Flow | Use when | Command or action | Expected outcome |
|---|---|---|---|
| Code-change validation | You changed screenshare code or nearby test harness code. | `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode Test -Configuration Debug` | The ScreenShare ownership lane passes, or the failure identifies a local regression to fix before any live soak. |
| Local stability soak | You need deterministic local stability evidence before release or after screenshare pipeline changes. | `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode LocalSoak -Configuration Release -DurationSeconds 300` | The local soak runs for 5-10 minutes on a visible Windows desktop and exits cleanly with stable capture/send/render metrics. |
| Live NKN evidence | You need live transport evidence, artifact materialization, or behavior-first gate output. | `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode NknSoak -DurationSeconds 30`, then `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode AnalyzeRetained -ArtifactDir artifacts\soak\<timestamp>` | A fresh artifact appears under `artifacts\soak\<timestamp>\`, and `screenshare-operator-verdict.txt` gives the operator verdict before deeper retained analyzer files are read. |
| Support/debug capture | A user or tester reports a problem and you need evidence without starting a new investigation branch. | `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode SupportCapture` | Copy app Diagnostics first; it includes the latest screenshare evidence summary when available. Use Save Hang Report for freezes, then attach a full soak artifact only when the diagnostics evidence points to one. |

## Outcome Model

- Pass: The selected flow succeeds and the result matches the expected outcome above.
- Fail with local regression: `ScreenShare` lane or local soak fails before live transport evidence is needed. Fix the local issue first.
- Fail with live transport evidence: live NKN soak fails but still materializes artifacts. Produce and read `screenshare-operator-verdict.txt`, then use the artifact directory as the source of truth for the next plan.
- Inconclusive or missing artifact: the command did not run to completion, the artifact is missing, or required summaries are absent. Treat this as tooling or environment validation first.

## Operator Defaults

- Prefer `ScreenShare` lane for normal code validation.
- Prefer local soak for deterministic stability checks.
- Use live NKN soak only when the question requires live transport behavior.
- After live NKN evidence, run `AnalyzeRetained` and read `screenshare-operator-verdict.txt` before opening individual analyzer reports.
- For support/debug capture, start with `Diagnostics -> Copy diagnostics`; the copied text includes a compact `Screenshare evidence` block when an analyzed artifact exists.
- For hangs or freezes, use `Diagnostics -> Save Hang Report`; the report folder includes `screenshare-evidence.txt` with the same summary.
- Attach a full `artifacts\soak\<timestamp>` directory only when the diagnostics evidence points to one or support asks for the raw artifact.
- Do not manually extend the retained Track B analyzer chain during normal screenshare work.
- Track B remains parked at `steady_external_delivery_latency`; reopening that conclusion requires a new explicit plan.

## Track D Closeout State

Track D is parked with one supported screenshare operator topology:

- `tools\ScreenShare-Ops.ps1` is the only screenshare operator entry point.
- `screenshare-operator-verdict.txt` is the first-read live evidence artifact.
- App Diagnostics and Save Hang Report are the first support capture surfaces.
- Retained Track B analyzers are preserved closeout evidence, not the normal path for new screenshare work.
- Future changes that add a mode, wrapper, artifact, or diagnostic surface must update this guide, `docs/screenshare-soak.md`, and the architecture guardrails in the same change.

## Technical References

`tools\ScreenShare-Ops.ps1` is the operator entry point. These lower-level commands remain available for focused debugging:

- `powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane ScreenShare -Configuration Debug`
- `dotnet run --project src/nLink.App -c Release -- --screenshare-soak --seconds 300`
- `powershell -ExecutionPolicy Bypass -File .\tools\Run-ScreenShareNknSoak.ps1 -DurationSeconds 30`
- `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode AnalyzeRetained -ArtifactDir artifacts\soak\<timestamp>` writes `screenshare-operator-verdict.txt` after the retained analyzer chain.

## Related References

- `docs/screenshare-soak.md` for local soak details and retained Track B closeout evidence.
- `docs/screenshare-stabilization-protocol.md` for branch promotion rules when screenshare runtime behavior changes.
- `docs/supportability.md` for general diagnostics, hang report, and issue evidence guidance.
- `docs/test-lanes.md` for the domain test lane matrix.
