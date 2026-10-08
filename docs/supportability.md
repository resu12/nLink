# Supportability Guide

Use this guide when collecting evidence for a bug report, RC issue, or support handoff. The goal is to capture enough context without asking users to run internal tools unless the issue specifically needs them.

## First Evidence To Collect

1. Open `Options` in the app.
2. Go to the `Diagnostics` tab and click `Copy diagnostics`.
3. Paste the copied text into the issue or support thread.
4. If the app is frozen or intermittently hangs, click `Save Hang Report` and attach the generated folder or ZIP when requested.

Diagnostics and hang reports are best-effort redacted. Review them before sharing outside the project.

## What To Include In Issues

- nLink version, for example `v0.7.0`.
- Install type: Installer or Portable.
- Your role: Helper, Helpee, or both.
- Area affected: Connection, chat, file transfer, screen share, remote control, install/update, options, or diagnostics.
- Transport if known: NKN, DEVLOCAL, or not sure.
- Clear steps to reproduce and what happened instead.
- Diagnostics paste from `Options -> Diagnostics -> Copy diagnostics`.
- Hang Report path or attachment if the app froze.

## Screenshare Evidence

For screenshare issues, start with the normal diagnostics paste. If a screenshare artifact has already been analyzed, Options -> Diagnostics includes a `Screenshare evidence` block with the latest operator verdict.

Use `tools\ScreenShare-Ops.ps1` only when new screenshare evidence is needed:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode NknSoak -DurationSeconds 30
powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode AnalyzeRetained -ArtifactDir artifacts\soak\<timestamp>
```

Read `screenshare-operator-verdict.txt` first. Attach the full `artifacts\soak\<timestamp>` directory only when the verdict or support request points to the raw artifact.

## File Transfer And Tuna Evidence

Start with Diagnostics from both peers and a description of the visible transfer cards. Include:

- Roles, direction, approximate file size, nLink version, and Installer versus Portable.
- Whether Tuna was enabled/unlocked and whether either peer switched it off or on (including accidental keyboard interaction), with approximate local times.
- Sender/receiver terminal states, any error text, and whether pause/cancel/session end was requested. A full progress bar is not proof of successful SHA verification.
- A screenshot of both cards if progress appears persistently divergent. The displays smooth accepted/receiver-confirmed progress; brief ACK-related differences are expected, but stuck or contradictory terminal states need investigation.

Do not share `wallet.json`, passwords, seeds, private keys, or wallet paths. Wallet validation does not start paid transport; absence of payment telemetry does not prove a Tuna session was free. Review raw logs even when Diagnostics is redacted.

If a maintainer has already collected file-transfer artifacts, retain the complete directory and read `filetransfer-operator-verdict.txt` first. For strict Phase 5 runs, include `phase5-analyzer-gui-acceptance-summary.txt/.json`, `route-acceptance-run-manifest.json`, and both peers' retained slices. An operator PASS alone does not prove authenticated Tuna delivery or the complete route cycle. Do not run a new paid GUI matrix merely to prepare a support report; use the [operator guide](file-transfer-operability.md) to select only missing evidence.

## Logs And Artifacts

- App logs live under `%LOCALAPPDATA%\nLink\logs`.
- Hang reports live under `%LOCALAPPDATA%\nLink\artifacts\hang\hang-<timestamp>\`.
- Local support artifacts under `artifacts\` are not automatically safe to share; review them or prefer Diagnostics/Hang Report output first.

## Related Docs

- `docs/screenshare-operability.md` for screenshare flow selection.
- [File-transfer operability](file-transfer-operability.md) for file/Tuna triage and [Tuna implementation](nkn-tuna-implementation.md) for wallet, payer, and cap behavior.
- `docs/test-lanes.md` for validation lanes.
- `docs/release/rc-validation-checklist.md` for RC support evidence checks.
