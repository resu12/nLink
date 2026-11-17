# File Transfer Soak Workflow

This workflow targets the current route-aware file-transfer model:

- regular NKN -> `regular_nkn_v4_fast`, protocol `4`,
- active file Tuna -> `file_tuna_v4`, protocol `4`,
- live post-Tuna fallback -> one-shot `post_tuna_fallback_v6`, protocol `6`, for the affected transfer,
- diagnostic regular-NKN V6 -> `diagnostic_regular_nkn_v6`, explicit unsafe developer/test opt-in only.

V5 and legacy active `file_tuna_v6` evidence are obsolete protocol inputs and should fail retained analysis or payload parsing. Do not re-enable legacy data-protocol compatibility during soak triage.

## Guardrails

- Production bridge defaults remain unchanged.
- Regular NKN carries control; `FileTransferCoordinator` and its issued DTOs/tokens alone authorize lifecycle, liveness, route publication, V6 fallback, and recovery effects. Readiness, ACKs, send acceptance, and transport observations are evidence only.
- Tuna remains experimental and default-off unless explicitly enabled for a test or session.
- Do not redesign screen sharing, wallet UX, payer policy, caps, sidecar startup, installer behavior, or Diagnostics/Options UI while tuning file-transfer soak.
- All generated artifacts must stay under repo `artifacts/`. Manual scripts and runbooks must never delete or clean Downloads or other user data folders.
- Run .NET tests serially on Windows to avoid DLL file locks; see `docs/build-test-lock-avoidance.md`.
- Regular NKN promotion uses a `1.5 MB/s` app-goodput target. Prefer stability, terminal correctness, and payload efficiency over chasing higher burst speed on variable public NKN paths.
- Record active Tuna goodput against the existing locked baseline and numeric performance policy. Phase 5 records performance separately from strict correctness; retain a performance **FAIL**, warnings, and contaminated measurements. This does not change legacy/Phase 4 gates or promise throughput.
- Controlled fallback speed is not a Phase 5 correctness requirement; retain its separate performance warnings. Survival, SHA/integrity, terminals, and route correctness remain strict.
- A successful final `post_tuna_fallback_v6` completion must consume the post-fallback state; the next new file transfer resolves from current transport state (`file_tuna_v4` when Tuna is active, otherwise `regular_nkn_v4_fast`), not stale fallback state.

## Common Runs

Use focused affected tests during implementation. `FileTransfer-Ops.ps1 -Mode Test` is a broader deterministic operator check, not a prerequisite for every small diagnostic change:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode Test
```

Supported operator modes:

- `LocalFast`
- `LocalImpaired`
- `LocalMixed`
- `NknFast`
- `NknMixed`
- `AnalyzeRetained`
- `SupportCapture`

Local route/runtime proof:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 1 -FailOnGate
```

Guarded mixed proof:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -Build -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1800 -ProgressTimeoutSeconds 120 -FailOnGate
```

Local modes build by default; `-Build` belongs only to live modes. Run only a necessary targeted live scenario after focused regressions and a portable rebuild. Reserve broad matrices for a stable candidate, not automatic continuation through diagnostic batches.

All live examples below require an interactive Windows desktop and explicit escalation outside a restricted sandbox, including bridge/network readiness preflights. Never bypass a failed preflight. Use a freshly rebuilt packaged executable, not an old `bin` output, for runtime qualification.

## Regular NKN Regression Evidence

Use the regular-NKN GUI soak when the installed app appears slow:

```powershell
.\tools\Run-FileTransferNknSoak.ps1 -Mode nkn-fast -ExePath ".\artifacts\portable\nLink\win-x64\nLink.exe" -PayloadSizes "128MB" -Cycles 1 -Direction helpee-to-helper -CycleTimeoutSeconds 240 -ProgressTimeoutSeconds 90 -TimeoutSeconds 360 -ExternalTopologyProfile Default -PayloadEfficiencyProfile Auto -FailOnGate
```

Read `filetransfer-live-nkn-summary.txt`, `transfer-terminal-summary.txt`, `throughput-summary.txt`, `payload-efficiency-summary.txt`, and `filetransfer-route-consistency-summary.txt` together.

Clean regular-NKN evidence should show:

- route `regular_nkn_v4_fast`,
- protocol `4`,
- completed sender and receiver terminals,
- SHA/integrity OK,
- `bridge_bulk_send_failure_count=0`,
- no regular-NKN bridge queue clear,
- average goodput recorded against the `1,500,000 B/s` target (historical baseline: nLink 0.6.2); on public NKN, below-target goodput is triage evidence rather than an automatic release blocker when route, integrity, terminal, and bridge-failure gates pass.

Historical May route reference cells (not current-code qualification):

- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/regular-nkn-v4-64mb-r2/`: regular NKN V4 passed with SHA OK, completed terminals, no bridge bulk send failures, and `1,769,711 B/s`.
- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-v4-64mb-r2/`: active Tuna V4 passed with SHA OK, completed terminals, and `4,087,486 B/s`.
- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-fallback-64mb/`: historical restart-style fallback reference. Keep it for regression history only; current Phase 5 acceptance requires live-route epoch proof on the same transfer.

Older V6 regular-NKN artifacts remain useful as regression history only. They are not current production-route baselines.

## Route Acceptance Gate

Before release sign-off, run the strict Phase 5 gate with a rebuilt packaged app, sidecar, reviewed clean checkpoint, and low-balance test wallet. Supply a session-only password through the scoped `NLINK_TUNA_TEST_WALLET_PASSWORD` environment variable, not a command-line literal or committed script; clear it afterward. Rebuild command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1 -Runtime win-x64
```

Run outside the restricted sandbox with explicit escalation. With the password already supplied to that process:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferRouteAcceptance.ps1 `
  -MatrixMode phase5-analyzer-gui-acceptance `
  -StrictCampaignPreflight `
  -ExePath ".\artifacts\portable\nLink\win-x64\nLink.exe" `
  -WalletPath ".\artifacts\tuna-poc\wallet-test-nkn.json" `
  -SidecarPath ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe"
```

Phase 5 writes `phase5-analyzer-gui-acceptance-summary.txt/.json` and `route-acceptance-run-manifest.json` under `artifacts/filetransfer-route-acceptance/<timestamp>/`. The wrapper defaults to `legacy` without `-MatrixMode`; that is not the current release gate. Do not override limits or acceptance switches to manufacture a pass.

When running this gate from sandboxed automation, explicitly escalate the GUI wrapper, bridge readiness preflight, and live NKN connectivity checks outside that sandbox. This does not mean the per-user app/installer needs administrator rights. Restricted sandboxes can block outbound TCP and cause false bootstrap failures; confirm such failures outside the sandbox before attributing them to runtime code. Never bypass a failed Phase 5 preflight.

Required Phase 5 matrix (all require actual delivery, SHA integrity, terminal GUIs, strict coordinator route/protocol proof, and no unresolved hard failures):

- regular NKN 64 MiB: `regular_nkn_v4_fast`, protocol `4`,
- active Tuna V4 64 MiB: authenticated Tuna delivery on `file_tuna_v4`, protocol `4`,
- live switch-off from helpee: final `post_tuna_fallback_v6`, protocol `6`,
- live switch-off from helper: final `post_tuna_fallback_v6`, protocol `6`,
- helper-to-helpee payer handoff 128 MiB: coordinator-published `file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6`, with authenticated payload delivery in both Tuna phases,
- `regular-v4-live-activation-off-on-off-256mb`: coordinator-published `regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6`, including authenticated Tuna payload delivery at both activations; SHA or fallback completion cannot replace the second publication/delivery proof,
- second transfer after live reactivation: first transfer proves `post_tuna_fallback_v6,file_tuna_v4`, then the next transfer selects `file_tuna_v4`, protocol `4`.

Fallback retry is allowed only for retryable pre-measured failures, such as measured fallback never starting or a progress timeout before measured fallback produced terminal/integrity evidence. Route mismatch, protocol mismatch, missing `filetransfer_route_selected`, SHA failure, terminal failure, zombie terminal, diagnostic V6 during acceptance, or regular-NKN bridge bulk failure must not be retried into a pass.

For remediation, add `-ScenarioName <existing-scenario-name>` to select only the failing scenario after rebuilding and rerunning affected tests. The final gate covers all seven scenarios. When a campaign must stop at the first failure, invoke each selected scenario once and inspect its strict result before starting the next; the matrix wrapper can otherwise continue collecting results after a failure. After two focused fix-and-validation iterations without closing the blocker, stop patch expansion and review the implicated architectural boundary. Current dated results and exact payload provenance are in [ReleaseRunbook.md](ReleaseRunbook.md#current-qualification-update-2026-10-15).

## Paid Tuna GUI Smoke

Use the GUI smoke when the visual file-transfer card, pause/resume buttons, or session shell behavior needs to be exercised with real windows. This is an opt-in paid test and writes artifacts under `artifacts/gui-smoke/`.

Active Tuna V4 no-fault:

```powershell
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferTunaGuiSmoke.ps1 `
  -RouteMode preactivated `
  -Fault none `
  -WalletPath ".\artifacts\tuna-poc\wallet-test-nkn.json" `
  -PayerMode helpee `
  -Direction helpee-to-helper `
  -PayloadSize 128MiB
```

Live V4 -> V6 fallback switch-off:

```powershell
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferTunaGuiSmoke.ps1 `
  -RouteMode live-v4-switch-off `
  -Fault switch-off `
  -WalletPath ".\artifacts\tuna-poc\wallet-test-nkn.json" `
  -PayerMode helpee `
  -Direction helpee-to-helper `
  -PayloadSize 128MiB
```

The GUI summary is written to `filetransfer-tuna-gui-summary.json`.

For active Tuna, required evidence includes:

- measured route `file_tuna_v4`,
- protocol `4`,
- V4 sender/receiver start,
- `tuna_acceleration_negotiated`,
- authenticated Tuna-accelerated file payload delivery and coordinator publication (negotiation/counters alone are insufficient),
- terminal sender/receiver completion,
- SHA match.

For live fallback, required evidence includes:

- initial measured route `file_tuna_v4`,
- initial measured protocol `4`,
- explicit `filetransfer_live_route_epoch_started` and `filetransfer_live_route_epoch_recovered`,
- live route epoch route `post_tuna_fallback_v6`,
- live route epoch protocol `6`,
- handoff `tuna_to_normal_fallback`,
- target transport `regular_nkn`,
- terminal sender/receiver completion,
- SHA match,
- next-transfer route resolves from current transport state after successful fallback completion.

The measured retained slice and coordinator-owned live-route epoch proof are required evidence for fallback gating, not independent runtime authority:

- `filetransfer-retained-log-slice-full.log` keeps the complete run,
- `filetransfer-setup-retained-log-slice.log` keeps setup evidence,
- `filetransfer-measured-fallback-retained-log-slice.log` keeps measured fallback evidence,
- `measured-fallback-analysis/filetransfer-route-consistency-summary.txt` and `filetransfer-operator-verdict.txt` gate the measured fallback.

## Evidence

Primary artifacts:

- `filetransfer-operator-verdict.txt`
- `filetransfer-route-consistency-summary.txt`
- `filetransfer-live-nkn-summary.txt`
- `transfer-terminal-summary.txt`
- `protocol-shape-summary.txt`
- `payload-efficiency-summary.txt`
- `transport-budget-summary.txt`
- `bridge-config-summary.txt`
- `bridge-bulk-summary.txt`
- `coexistence-summary.txt`
- `stability-gates-summary.txt`
- `baseline-comparison.txt`

Acceptance expectations:

- Completed transfers have SHA match.
- Route/protocol/runtime/frame-family/bridge-policy evidence matches the selected route.
- Route-aware logs include `filetransfer_route_selected`.
- Regular NKN and active Tuna use protocol `4`.
- Controlled fallback measured transfer uses protocol `6`.
- No stuck `Sending...` or `Receiving...` card after cancel, peer close, session end, window close, or app exit.
- Cancel from either side and peer close terminalize locally first and notify the peer over regular NKN control.
- No orphan active sidecar remains after fallback/reset.
- No payload rejects, decode failures, message rejects, bridge bulk failures, media queue severe events, progress timeout, false recovery, or unresolved V6 fallback state.

Recovered post-Tuna fallback bridge queue-clear evidence may be warning-only after the measured fallback has route consistency, SHA OK, and completed terminals. The same evidence remains a hard failure for regular NKN.

## Promotion Checks

Do not promote from a one-cycle smoke, an inconclusive run, a progress timeout, a cross-protocol baseline, or any run with hard failure counters.

Protocol mismatches are report-only only when comparing historical artifacts. Current route acceptance treats mismatches as hard failures.
