# Supportability Guide

Use this guide when collecting evidence for a bug report, RC issue, or support handoff. The goal is to capture enough context without asking users to run internal tools unless the issue specifically needs them.

## First Evidence To Collect

1. Open `Options` in the app.
2. Go to **Diagnostics**. In 0.7.1, click **Refresh** under **Session snapshot** while the problem is visible. The UTC time tells support when the state was sampled; copying later does not refresh it.
3. Click **Copy diagnostics**, review the text, then share it with the issue. For intermittent hangs, choose **Save Hang Report** while the app is responsive.

Redaction is best-effort. The receiving peer can see the full saved path locally. In 0.7.1, the copied diagnostics may retain the filename but mask its directory; the exported log tail also redacts destination paths. The original local log is not rewritten. Check filenames and other identifying details before sharing.

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

For a screen-share issue, say whether the Helpee saw a preview and whether the **Helper** saw a changing image. In 0.7.1, **Previous screen-share test run** shows the last saved test artifact if available. Its verdict is not a measurement of this session.

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
- The exact tooltip if the route is in question. In 0.7.1, **Tuna is ready. This file uses regular NKN.** means the session has Tuna available but the file is on regular NKN. A colored fish alone does not show where the bytes went.
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
