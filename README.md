# nLink

nLink is a private, serverless Windows remote-help app for helping family and friends with chat, screen sharing, optional remote control, and single-file transfer. No accounts needed.

Created by Codex/GPT

Powered by NKN. Official website: https://nkn.org/

Minimal `.NET 8` / Avalonia desktop app for Windows with deterministic smoke tests.

## Download 0.7.0 hotfix revision 2

[Download 0.7.0 hotfix revision 2](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.2), released **17 November 2026** from `36557e07` with CI #108's exact validated artifacts. The app still reports **0.7.0**: use the release tag and its own `SHA256SUMS.txt` to distinguish packages.

- [Windows x64 installer (recommended)](https://github.com/resu12/nLink/releases/download/v0.7.0-hotfix.2/nLink-Setup-win-x64-0.7.0.exe)
- [Windows x64 portable ZIP](https://github.com/resu12/nLink/releases/download/v0.7.0-hotfix.2/nLink-Portable-win-x64-0.7.0.zip)
- [SHA256SUMS.txt](https://github.com/resu12/nLink/releases/download/v0.7.0-hotfix.2/SHA256SUMS.txt)

Hotfix 2 adds idle Tuna startup/re-establishment fixes, authenticated-session cleanup and listener-lifetime safeguards, a transfer-safe Tuna indicator, wallet unlock/cancellation safeguards, a “Preparing helper address…” state, and wallet feedback that stays visible while scrolling. It includes hotfix 1's screen-sharing encoder startup fix.

All eight required [CI #108 jobs](https://github.com/resu12/nLink/actions/runs/43920671484), including installer smoke, and RC Guardrails passed. Public downloads were verified against their checksums and the exact CI artifact pins. Optional GUI smoke was skipped; these results do not claim new live Tuna, same-version upgrade or performance qualification. See the [published release notes](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.2) for scope and limits.

**The Windows app and installer are unsigned**, under an explicit hotfix-2 exception. Windows may show publisher or SmartScreen warnings. Checksums identify the downloads but do not replace a digital signature. The [original 0.7.0 release](https://github.com/resu12/nLink/releases/tag/v0.7.0) and [hotfix 1](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.1), including their assets and checksums, remain unchanged.

## Quick Start (Windows)

1. Download the Installer (recommended) or Portable ZIP from [hotfix 2's release page](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.2). A source-branch version is not necessarily a published release.
2. Helper opens nLink, clicks `I want to help someone`, and waits for the full helper address beginning with `nlinkh1.` before copying it. Do not copy the brief short bootstrap address shown by older builds.
3. Helpee opens nLink, clicks `I need help`, enters the provided helper address, and clicks `Request help`.
4. Helper receives the incoming help request and clicks `Accept`.
5. Both sides compare the session verification symbols, then Helpee clicks `Allow`.
6. Chat opens on both sides.
7. If `Transfer files` is allowed for the session, either side can click `Send file`.
   In the 0.7.1 candidate, either side can also drop one local file from Windows Explorer into chat. When sharing a screen, drop it in the chat panel, not on the shared display. The receiver can choose **Accept** for Downloads, **Save as…** to pick a folder and filename, or **Decline**. Canceling the picker leaves the offer pending. If the chosen name is already in use, choose another name; nLink won’t replace the existing file.

## Screenshots

Screenshots supplied by the user on **16 November 2026** during hotfix-2 review. They include the new preparing-address state and show the UI in use; they are not exact-build or CI qualification. See [screenshot provenance](docs/images/README.md).

Home:

![nLink home screen with help and helper options](docs/images/home-2026-11-16.png)

Helper preparing its address:

![Helper showing Preparing helper address, with Copy and Share disabled](docs/images/helper-2026-11-16.png)

Helpee:

![Helpee screen for entering or importing the helper address](docs/images/helpee-2026-11-16.png)

Screen sharing:

![Active screen sharing with the Helpee preview, chat and Stop sharing control](docs/images/screenshare-2026-11-16.png)

File transfer:

![File transfer in progress with Pause and Cancel controls](docs/images/file-transfer-2026-11-16.png)

## Usage notes

If connection fails:
Open Options -> Diagnostics -> Copy diagnostics and include it when reporting issues. If a screenshare soak artifact has already been analyzed, Options includes a compact screenshare evidence summary; for hangs or freezes, use Options -> Diagnostics -> Save Hang Report. See [`docs/supportability.md`](docs/supportability.md) for the support evidence checklist.

For release-safe builds, Options -> Diagnostics should show:

- `invite_security_mode: issued_one_time_secret_invites`
- `invite_public_flow: verified_helper_required`
- `invite_security_release_ready: Yes`
- `invite_security_warning: none`

Normal release UX uses a verified helper-address payload (`nlinkh1.`) and an approval-time session verification sequence, then a direct helpee-side `Request help` flow with QR/share/copy support where needed. The approval check is a compact five-symbol comparison on both screens.

Notes:

- Windows x64 only
- Default screensharing uses H.264 video transport, with helper-side recovery protection for broken reference chains.
- Options -> Settings includes Balanced, High quality, Tuna quality, and High performance screen-share presets. High quality now opts into 24 capture FPS / 15 transport FPS over normal NKN, while Tuna quality remains the higher-bandwidth preset recommended for Tuna-enabled screen sharing.
- The helper-side cursor overlay, H.264 motion/keyframe safeguards, WGC GPU scaling, and same-apartment Win10 WGC teardown remain enabled.
- Chat UX keeps `Enter` to send, `Shift+Enter` for a new line, stable pane sizing in chat-only and screen-sharing layouts, and message entry remains available during screen sharing.
- File transfer in `0.7.0` is single-file only. No folders, drag-and-drop, or resume after restart yet.
- Route defaults are explicit: regular NKN uses `regular_nkn_v4_fast` / protocol `4`, active file Tuna uses `file_tuna_v4` / protocol `4`, and controlled post-Tuna fallback uses `post_tuna_fallback_v6` / protocol `6` for the affected transfer. `diagnostic_regular_nkn_v6` is unsafe developer/test opt-in only; active `file_tuna_v6` is invalid.
- Receiving a file requires explicit accept/decline, and file-transfer data is protected by nLink's session envelope plus source/session validation rather than by assuming NKN transport alone is sufficient.
- The bundled NKN bridge/runtime remains a trust boundary; transport encryption does not replace nLink's application checks.
- Active file transfers can be paused, resumed, or canceled from either side when file transfer is allowed.
- File-transfer lifecycle actions are hard-priority: cancel, pause, session end, peer down, window close, and app exit do not wait for file-data queues, repair queues, Tuna, or bulk backlog.
- If Tuna stops during a Tuna V4 transfer, the same transfer live-transitions to V6 fallback over regular NKN. Explicit reactivation can return that transfer to Tuna V4. After successful final fallback completion, the next new transfer resolves from current transport state: Tuna V4 when active, otherwise regular V4.
- Progress is based on accepted/receiver-confirmed bytes, not merely queued sends. The bars smooth reported progress without predicting future delivery; network acknowledgements can still make the two displays differ briefly. Completion requires final SHA-256 verification.
- Received files are saved into the Windows Downloads folder by default, with a numbered suffix added automatically when the target name already exists.
- Safe-by-default file size cap for `0.7.0`: `25 GiB`
- In Options -> Wallet, you can link a wallet and choose whether to enable Tuna. Linking a wallet does not start Tuna, spend NKN, or change the default transport.
- Tuna startup status and a pulsing fish indicate an unlocked connection attempt, not proof of payload delivery. Gray is inactive, light blue is the non-paying side, and yellow identifies the selected paying listener. Linking or validating a wallet does not spend NKN; starting a paid listener can. See the [Tuna guide](docs/nkn-tuna-implementation.md).
- The strict Phase 5 release gate requires actual payload delivery, SHA integrity, coordinator-published routes, authenticated Tuna delivery, fallback, and terminal GUI completion. Performance is reported separately under the existing policy, without changing numeric baselines or promising Tuna/fallback throughput.
- Large file transfers over NKN can still be noticeably slower than local or direct network copy. Live screenshare latency can also vary with NKN/network delivery.
- The `v0.7.0`, `v0.7.0-hotfix.1` and `v0.7.0-hotfix.2` Windows artifacts each have an explicit unsigned-artifact exception. Verify downloads against the checksum file from the same tag; these exceptions do not approve future release artifacts.
- Installer path: `%LOCALAPPDATA%\Programs\nLink`

License:

- MIT (see `LICENSE`)

## Documentation

- Current docs index:
  [`docs/README.md`](docs/README.md)
- Support evidence guide:
  [`docs/supportability.md`](docs/supportability.md)
- Test lane matrix:
  [`docs/test-lanes.md`](docs/test-lanes.md)
- Screenshare operator guide:
  [`docs/screenshare-operability.md`](docs/screenshare-operability.md)
- Screenshare implementation guide:
  [`docs/screenshare-implementation.md`](docs/screenshare-implementation.md)
- File transfer implementation guide:
  [`docs/file-transfer-implementation.md`](docs/file-transfer-implementation.md)
- File transfer and Tuna operator guide:
  [`docs/file-transfer-operability.md`](docs/file-transfer-operability.md)
- NKN Tuna implementation guide:
  [`docs/nkn-tuna-implementation.md`](docs/nkn-tuna-implementation.md)

## Build from source (developers)

### How to run

1. Restore dependencies:
   `dotnet restore`
2. Run the desktop app:
   `dotnet run --project src/nLink.App`

### How to build

1. Build the solution:
   `dotnet build`

### Tests

- During implementation, run focused tests for the changed behavior and its affected ownership lane. Reserve the full sweep for a planned broad regression gate:
  `powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane Core,Gui,ScreenShare,RemoteControl,Contracts`
- Run deterministic release smoke tests:
  `powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane Smoke -Configuration Release`
- See the lane matrix:
  [`docs/test-lanes.md`](docs/test-lanes.md)
- For screenshare validation and support flow selection:
  `powershell -ExecutionPolicy Bypass -File .\tools\ScreenShare-Ops.ps1 -Mode Test`
  [`docs/screenshare-operability.md`](docs/screenshare-operability.md)

### Release Validation (Maintainers, Windows)

- Planned final gate (tests, strict Phase 5 GUI route acceptance, and packaging):
  `powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1 -RunGuiSmoke -RunBetaReadiness -RunFileTransferRouteAcceptanceGate`
- Live validation needs an interactive Windows desktop, a low-balance test wallet, and a session-only password. In sandboxed automation, run live NKN/GUI checks and preflights with explicit escalation; never bypass a failed preflight. See the [runbook](docs/ReleaseRunbook.md) for setup and evidence requirements.
- Omitting `-RunFileTransferRouteAcceptanceGate` skips that gate; packaging success alone is not route qualification.
- Output release assets:
  `artifacts/releases/<version>/nLink-Portable-win-x64-<version>.zip`
  `artifacts/releases/<version>/nLink-Setup-win-x64-<version>.exe`
- Published hotfix-1 release notes:
  [`docs/releases/0.7.0-hotfix.1.md`](docs/releases/0.7.0-hotfix.1.md)
- Original 0.7.0 release notes:
  [`docs/releases/0.7.0.md`](docs/releases/0.7.0.md)
- Published hotfix-2 release notes and exact downloads:
  [0.7.0 hotfix revision 2](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.2)
- Historical hotfix-2 preparation records (pre-publication status and local-build hashes):
  [candidate notes](docs/releases/0.7.0-hotfix.2.md), [release-body draft](docs/releases/0.7.0-hotfix.2-github.md)
- RC/final validation checklist:
  [`docs/release/rc-validation-checklist.md`](docs/release/rc-validation-checklist.md)

### Dead-Code Report (Local, Non-Destructive)

- Generate a warning-based dead-code candidate report:
  `powershell -ExecutionPolicy Bypass -File .\tools\DeadCode-Report.ps1`
- Output:
  `artifacts/deadcode/report.md`

### Resource Footprint Tools

- Resource benchmark (idle/connect/idle/disconnect/idle, writes JSON + summary):
  `dotnet run --project src/nLink.App -c Release -- --resource-bench --transport devlocal`
- Leak check (cycle-based growth check, writes JSON + summary):
  `dotnet run --project src/nLink.App -c Release -- --leak-check --cycles 200 --transport devlocal`
- Enable resource gate failure (threshold + growth checks):
  add `--fail-on-gate`
- ResourceGate threshold overrides (examples):
  `--resource-growth-warn-percent 10 --resource-growth-fail-percent 20`
  `--app-working-set-max-mb 1024 --app-private-bytes-max-mb 1024 --app-thread-max 400 --app-handle-max 20000 --app-cpu-idle-avg-max-pct 40`
  `--bridge-working-set-max-mb 512 --bridge-private-bytes-max-mb 512 --bridge-thread-max 300 --bridge-handle-max 20000 --bridge-cpu-idle-avg-max-pct 40 --resource-fail-on-bridge-thresholds`
- LeakCheck growth threshold override:
  `--leak-growth-fail-percent 20`
- Pre-release helpers:
  `powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1 -RunResources -RunLeakCheck`

### Building the Portable Package and Installer

Build prerequisites are the .NET 8 SDK and Go toolchain; installer creation also needs Inno Setup 6 (`ISCC.exe`). Bridge dependencies use the pinned, hash-verified Node bootstrap and locked npm restore. End users do not need .NET, Node, npm, or Go installed.

```powershell
# Portable app, bridge, Tuna sidecar, and ZIP:
powershell -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1 -Runtime win-x64

# Alternatively, build these plus the installer and release assets:
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Runtime win-x64
```

Both scripts rebuild the canonical payload by default; a separate bridge build is not a prerequisite. `Build-Installer.ps1` calls `Build-Portable.ps1`, so running both consecutively rebuilds it twice. A new rebuild is not automatically covered by earlier exact-binary GUI evidence: retain source/contract applicability and payload hashes before release sign-off.

Outputs:

- Canonical portable folder: `artifacts/portable/nLink/win-x64`.
- Portable ZIP: `artifacts/portable/nLink-Portable-win-x64-<version>.zip`.
- Installer: `artifacts/installer/nLink-Setup-win-x64-<version>.exe`.
- Release assets and checksums: `artifacts/releases/<version>/` (unless `-LocalOnly`).

The ZIP contains a self-contained `nLink.exe` plus required bridge, Tuna, and media files; keep the folder together. Helper/helpee alias folders are optional (`-CopyHelperAlias` / `-CopyHelpeeAlias` on the portable builder), not separate products or default installer staging.

Without Inno Setup, the installer script leaves the portable output available but exits with a missing-compiler error. The installer is per-user (`%LOCALAPPDATA%\Programs\nLink`) and does not require admin elevation. For release qualification and signing policy, use the [release runbook](docs/ReleaseRunbook.md).

### Bundled NKN Bridge Runtime (No Node Install Needed on End User PC)

Canonical bridge bundle artifact (built by the portable builder):

- `artifacts/bridge/win-x64/node.exe`
- `artifacts/bridge/win-x64/index.js`
- `artifacts/bridge/win-x64/package.json`
- `artifacts/bridge/win-x64/package-lock.json`
- `artifacts/bridge/win-x64/bridge-manifest.json`
- `artifacts/bridge/win-x64/bridge-dependencies.json`

The bridge uses a clean `npm ci` restore (`npm ci --ignore-scripts --no-audit --no-fund`) against `tools/nkn-bridge/package-lock.json`, `ncc`, and a pinned, hash-verified Node runtime. There is no shipped `node_modules` tree. The manifest records the app version from `VERSION`; packaging rejects stale manifests. The bundle is copied into the canonical app output/install.

Bundled runtime location inside the app output/install (required Release layout):

- `bridge/win-x64/node.exe`
- `bridge/win-x64/index.js`
- `bridge/win-x64/package.json`
- `bridge/win-x64/package-lock.json`
- `bridge/win-x64/bridge-manifest.json`
- `bridge/win-x64/bridge-dependencies.json`

Runtime behavior:
- `Release` builds prefer the bundled bridge runtime (`bridge/<rid>/node(.exe)` + `bridge/<rid>/index.js`)
- `Debug` builds allow launching `node` from `PATH` for local development
- `installer/Build-BridgeBundle.ps1 -UseSystemNode` exists for local experiments only; release builds use the pinned, hash-verified bootstrap path

Advanced overrides (developer/test only):
- `NLINK_NKN_NODE_PATH`
- `NLINK_NKN_BRIDGE_PATH`

In `Release` builds, unsafe transport, bridge path, NKN topology/recovery, file-transfer tuning, unsafe media, and release-link environment/appsettings overrides are ignored unless `NLINK_UNSAFE_DEVELOPER_MODE=1` is set for that developer test process. Public release validation should use the bundled bridge/runtime and keep the operator shell free of unsafe override variables.

### Manual NKN Integration Test (Not CI)

Use this only as a manual two-instance sanity check, not as file-transfer release proof. Do not add deterministic CI tests that depend on real NKN connectivity. Rebuild the portable package first, use its bundled runtime, and run live checks outside a restricted sandbox with explicit escalation when applicable.

Setup:
1. (Optional) Set a seed RPC endpoint:
   `$env:NLINK_UNSAFE_DEVELOPER_MODE = "1"`
   `$env:NLINK_NKN_SEED_RPC = "<rpc-host:port>"`

Keep such overrides scoped to the deliberate developer test process, not the public release shell.

Run the test (same PC, two app instances):
1. Start the first app instance:
   `.\artifacts\portable\nLink\win-x64\nLink.exe`
2. Click `I want to help` and copy the helper address.
3. Start the second app instance from the same portable folder.
4. Click `I need help`, paste the helper address, and click `Request help`.
5. Accept the incoming request on the helper side.
6. Compare the session verification symbols on both screens, then click `Allow` on the helpee side.
7. Send chat messages both ways and confirm they appear on both sides.

If it fails (copy diagnostics):
1. In the app, open `Options`
2. Select `Diagnostics` and click `Copy diagnostics`
3. Include the copied text when reporting the issue
