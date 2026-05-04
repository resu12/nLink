# Supportability Guide

Use this guide when collecting evidence for a bug report, RC issue, or support handoff. The goal is to capture enough context without asking users to run internal tools unless the issue specifically needs them.

## First Evidence To Collect

1. Open `Diagnostics` in the app.
2. Click `Copy diagnostics`.
3. Paste the copied text into the issue or support thread.
4. If the app is frozen or intermittently hangs, click `Save Hang Report` and attach the generated folder or ZIP when requested.

Diagnostics and hang reports are best-effort redacted. Review them before sharing outside the project.

## What To Include In Issues

- nLink version, for example `v0.6.2`.
- Install type: Installer or Portable.
- Your role: Helper, Helpee, or both.
- Area affected: Connection, chat, file transfer, screen share, remote control, install/update, or diagnostics.
- Transport if known: NKN, DEVLOCAL, or not sure.
- Clear steps to reproduce and what happened instead.
- Diagnostics paste from `Diagnostics -> Copy diagnostics`.
- Hang Report path or attachment if the app froze.

## Screenshare Evidence

For screenshare issues, start with the normal diagnostics paste. If a screenshare artifact has already been analyzed, Diagnostics includes a `Screenshare evidence` block with the latest operator verdict.

Use `tools\ScreenShare-Ops.ps1` only when new screenshare evidence is needed:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode NknSoak -DurationSeconds 30
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode AnalyzeRetained -ArtifactDir artifacts\soak\<timestamp>
```

Read `screenshare-operator-verdict.txt` first. Attach the full `artifacts\soak\<timestamp>` directory only when the verdict or support request points to the raw artifact.

## Logs And Artifacts

- App logs live under `%LOCALAPPDATA%\nLink\logs`.
- Hang reports live under `%LOCALAPPDATA%\nLink\artifacts\hang\hang-<timestamp>\`.
- Local support artifacts under `artifacts\` are not automatically safe to share; review them or prefer Diagnostics/Hang Report output first.

## Related Docs

- `docs/screenshare-operability.md` for screenshare flow selection.
- `docs/test-lanes.md` for validation lanes.
- `docs/release/rc-validation-checklist.md` for RC support evidence checks.
