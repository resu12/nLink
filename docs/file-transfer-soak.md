# File Transfer Soak Workflow

This workflow targets the current route-aware file-transfer model:

- regular NKN -> `regular_nkn_v4_fast`, protocol `4`,
- active file Tuna -> `file_tuna_v4`, protocol `4`,
- controlled post-Tuna fallback -> fresh one-shot `post_tuna_fallback_v6`, protocol `6`,
- diagnostic regular-NKN V6 -> `diagnostic_regular_nkn_v6`, explicit unsafe developer/test opt-in only.

V5 and legacy active `file_tuna_v6` evidence are obsolete protocol inputs and should fail retained analysis or payload parsing. Do not re-enable legacy data-protocol compatibility during soak triage.

## Guardrails

- Production bridge defaults remain unchanged.
- Regular NKN control remains authoritative for lifecycle, liveness, route negotiation, V6 fallback proof, and terminalization.
- Tuna remains experimental and default-off unless explicitly enabled for a test or session.
- Do not redesign screen sharing, wallet UX, payer policy, caps, sidecar startup, installer behavior, or Diagnostics/Options UI while tuning file-transfer soak.
- All generated artifacts must stay under repo `artifacts/`. Manual scripts and runbooks must never delete or clean Downloads or other user data folders.
- Run .NET tests serially on Windows to avoid DLL file locks; see `docs/build-test-lock-avoidance.md`.
- Regular NKN promotion uses a `1.5 MB/s` app-goodput target. Prefer stability, terminal correctness, and payload efficiency over chasing higher burst speed on variable public NKN paths.
- Active Tuna no-fault acceptance uses a strict `> 4,000,000 B/s` goodput floor.
- Controlled fallback has no speed floor; survival, SHA/integrity, terminals, and route correctness are the gate.
- A successful controlled fallback must consume the post-fallback state; the next new file transfer should be `regular_nkn_v4_fast` / protocol `4`, not another `post_tuna_fallback_v6`, unless a new fallback event occurs.

## Common Runs

Use `FileTransfer-Ops.ps1` as the normal non-paid entry point:

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
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 1 -Build -FailOnGate
```

Guarded mixed proof:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -Build -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1800 -ProgressTimeoutSeconds 120 -FailOnGate
```

Run the three-cycle public NKN proof only after a shorter mixed proof is clean.

## Regular NKN Regression Evidence

Use the regular-NKN GUI soak when the installed app appears slow:

```powershell
.\tools\Run-FileTransferNknSoak.ps1 -Mode nkn-fast -ExePath ".\src\nLink.App\bin\Release\net8.0\nLink.exe" -PayloadSizes "128MB" -Cycles 1 -Direction helpee-to-helper -CycleTimeoutSeconds 240 -ProgressTimeoutSeconds 90 -TimeoutSeconds 360 -ExternalTopologyProfile Default -PayloadEfficiencyProfile Auto -FailOnGate
```

Read `filetransfer-live-nkn-summary.txt`, `transfer-terminal-summary.txt`, `throughput-summary.txt`, `payload-efficiency-summary.txt`, and `filetransfer-route-consistency-summary.txt` together.

Clean regular-NKN evidence should show:

- route `regular_nkn_v4_fast`,
- protocol `4`,
- completed sender and receiver terminals,
- SHA/integrity OK,
- `bridge_bulk_send_failure_count=0`,
- no regular-NKN bridge queue clear,
- average goodput recorded against the `1,500,000 B/s` target when compared with the current 0.6.2-style baseline; on public NKN, below-target goodput is triage evidence rather than an automatic release blocker when route, integrity, terminal, and bridge-failure gates pass.

Recent route reference cells:

- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/regular-nkn-v4-64mb-r2/`: regular NKN V4 passed with SHA OK, completed terminals, no bridge bulk send failures, and `1,769,711 B/s`.
- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-v4-64mb-r2/`: active Tuna V4 passed with SHA OK, completed terminals, and `4,087,486 B/s`.
- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-fallback-64mb/`: controlled fallback passed with setup `file_tuna_v4` canceled cleanly and measured `post_tuna_fallback_v6` completed at `1,419,766 B/s`.

Older V6 regular-NKN artifacts remain useful as regression history only. They are not current production-route baselines.

## Route Acceptance Gate

Before installer creation, run the route acceptance gate from an interactive Windows desktop with a packaged app, sidecar, and test wallet:

```powershell
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferRouteAcceptance.ps1 `
  -ExePath ".\artifacts\portable\nLink\win-x64\nLink.exe" `
  -WalletPath ".\artifacts\tuna-poc\wallet-test-nkn.json" `
  -SidecarPath ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe" `
  -FallbackMaxAttempts 2 `
  -AllowExternalTransportWarnings $true
```

The gate writes `route-acceptance-summary.txt` and `route-acceptance-summary.json` under `artifacts/filetransfer-route-acceptance/<timestamp>/`.

Required matrix:

- regular NKN 64 MiB quick,
- regular NKN 128 MiB target,
- active Tuna V4 128 MiB no-fault,
- controlled restart fallback 128 MiB.

Fallback retry is allowed only for retryable pre-measured failures, such as measured fallback never starting or a progress timeout before measured fallback produced terminal/integrity evidence. Route mismatch, protocol mismatch, missing `filetransfer_route_selected`, SHA failure, terminal failure, zombie terminal, diagnostic V6 during acceptance, or regular-NKN bridge bulk failure must not be retried into a pass.

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

Controlled V4 setup -> V6 fallback restart:

```powershell
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferTunaGuiSmoke.ps1 `
  -RouteMode v4-restart-v6-fallback `
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
- Tuna-accelerated file frame evidence,
- terminal sender/receiver completion,
- SHA match.

For controlled fallback, required evidence includes:

- setup phase `setup_file_tuna_v4`,
- setup route `file_tuna_v4`,
- setup protocol `4`,
- clean local setup cancel/cleanup marker before measured fallback starts,
- measured phase `measured_post_tuna_fallback_v6`,
- measured route `post_tuna_fallback_v6`,
- measured protocol `6`,
- terminal sender/receiver completion,
- SHA match.
- next-transfer route `regular_nkn_v4_fast` / protocol `4` after successful measured fallback completion.

The measured fallback retained slice is authoritative for fallback gating:

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
