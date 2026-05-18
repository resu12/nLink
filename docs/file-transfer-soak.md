# File Transfer Soak Workflow

This workflow targets the current V6-only file-transfer protocol. V5, V4, null, or mismatched peers must fail cleanly as transport-incompatible; do not re-enable legacy data-protocol compatibility during soak triage.

## Guardrails

- Production bridge defaults remain unchanged.
- Regular NKN control remains authoritative for lifecycle, liveness, V6 epoch acknowledgements, and terminalization.
- Tuna remains experimental and default-off unless the Phase 6 paid gate passes.
- Do not redesign screen sharing, wallet UX, payer policy, caps, sidecar startup, installer behavior, or Diagnostics/Options UI while tuning file-transfer soak.
- All generated artifacts must stay under repo `artifacts/`. Manual scripts and runbooks must never delete or clean Downloads or other user data folders.
- Run .NET tests serially on Windows to avoid DLL file locks; see `docs/build-test-lock-avoidance.md`.
- Regular NKN promotion uses a `1.5 MB/s` app-goodput target. Prefer stability, terminal correctness, and payload efficiency over chasing higher burst speed on variable public NKN paths.

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

Local V6 file-only proof:

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

Read `filetransfer-live-nkn-summary.txt`, `transfer-terminal-summary.txt`, `throughput-summary.txt`, and `payload-efficiency-summary.txt` together. A clean terminal pass below `1.5 MB/s` is still a regression candidate when raw sent bytes, unsolicited chunks, or late sender frames show poor efficiency.

Recent regular-NKN reference cells:

- `artifacts/filetransfer-soak/20260515-172434/`: completed with clean terminals but regressed efficiency; `946,388 B/s`, raw sent `270,413,824` bytes for `128MB`, `v6_unsolicited_chunk_ignored_count=5086`, `post_completion_late_sender_frame=416`.
- `artifacts/filetransfer-soak/20260515-173810/`: current fixed reference; completed with clean terminals, `1,626,888 B/s`, raw sent `144,926,720` bytes for `128MB`, `v6_unsolicited_chunk_ignored_count=574`, `post_completion_late_sender_frame=0`.

The V6 regular-NKN near-frontier normal resend bypass is intentionally narrow. It should recover a non-advancing frontier without continuously refilling stale chunks while the receiver frontier is already moving.

## Phase 6 Paid Tuna Gate

Before paid Tuna time:

```powershell
dotnet build src\nLink.App\nLink.App.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
$version = (Get-Content VERSION -Raw).Trim()
go -C tools\nkn-tuna-sidecar build -ldflags "-X main.sidecarVersion=$version" -o ..\..\artifacts\tuna-sidecar\nlink-tuna-sidecar.exe .
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter "FullyQualifiedName~SessionFileTransferV6TunaIntegrationTests|FullyQualifiedName~SessionFileTransferV6TransportEpochTests|FullyQualifiedName~SessionFileTransferV6RuntimeTests|FullyQualifiedName~SessionFileTransferPauseTests|FullyQualifiedName~NknAccelerationTransportTests|FullyQualifiedName~NknFileTransferTransportTests|FullyQualifiedName~DiagnosticsAndLoggingTests|FullyQualifiedName~FileTransferOpsScriptsTests" -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet build tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet build-server shutdown
```

Run the short paid matrix only from an explicit opt-in shell:

```powershell
$env:NLINK_RUN_MANUAL_BRIDGE = "1"
$env:NLINK_RUN_TUNA_PHASE6_SHORT_MATRIX = "1"
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"
dotnet build-server shutdown
```

The Phase 6 short matrix writes artifacts under `artifacts/tuna-sidecar/phase6-short-<timestamp>/`. Read `phase6-operator-verdict.txt` first, then keep `summary.json`, `runs.jsonl`, redacted app log tail, listener stdout/stderr, and sidecar cleanup evidence.

## Paid Tuna GUI Handoff/Fallback Smoke

Use the GUI smoke when the visual file-transfer card, pause/resume buttons, or session shell behavior needs to be exercised with real windows. This is an opt-in paid test and writes artifacts under `artifacts/gui-smoke/`.

```powershell
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferTunaGuiSmoke.ps1 `
  -WalletPath ".\artifacts\tuna-poc\wallet-test-nkn.json" `
  -PayerMode helpee `
  -Fault switch-off `
  -Direction helpee-to-helper `
  -PayloadSize 128MiB
```

The runner launches two GUI app instances, connects them over NKN, starts a regular-NKN V6 file transfer, unlocks Tuna during the active transfer to prove `NormalToTunaActivation`, then triggers fallback and waits for completion. It also clicks Pause/Resume by default and verifies `pause_control.v6` lifecycle evidence.

Useful variants:

```powershell
# Kill the payer-side Tuna sidecar instead of switching Tuna off.
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferTunaGuiSmoke.ps1 -Fault sidecar-kill

# Exercise helper-paid Tuna.
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-FileTransferTunaGuiSmoke.ps1 -PayerMode helper -Direction helper-to-helpee
```

The GUI summary is written to `filetransfer-tuna-gui-summary.json`. Required evidence includes V6 sender/receiver start, `tuna_acceleration_negotiated`, terminal sender/receiver completion, SHA match, and no peer-disconnect or heartbeat-timeout evidence. Clean activation runs require a recovered `NormalToTunaActivation` epoch unless Tuna drops before proof and a fallback epoch starts; fallback runs require a recovered or explicitly waiting `TunaToNormalFallback` or `RegularNknRecovery` epoch.

File-transfer progress is committed-frontier based. On sparse destinations the receiver may accept far-ahead chunks, but the UI should report only contiguous committed bytes. During Tuna fallback or NKN receive-stall recovery it is normal for visible progress to pause, then jump when the missing frontier chunk arrives. Treat this as a bug only if the sender keeps sending unrequested chunks, committed progress stops until timeout, SHA validation fails, or sender/receiver terminal states diverge.

Recent local GUI reference cells:

- `artifacts/gui-smoke/tuna-filetransfer-20260513T200617Z/`: 128 MiB clean activation, completed, SHA match, no fallback.
- `artifacts/gui-smoke/tuna-filetransfer-20260513T201011Z/`: 128 MiB switch-off fallback, completed, SHA match.
- `artifacts/gui-smoke/tuna-filetransfer-20260513T201750Z/`: 128 MiB sidecar-kill fallback, completed, SHA match.
- `artifacts/gui-smoke/tuna-filetransfer-20260513T202236Z/`: 512 MiB sidecar-kill fallback, completed, SHA match after regular-NKN receive-stall recovery.

Provider readiness warnings are now split so diagnostic degraded startup can be distinguished from a persistent provider-path problem:

- `providerDegradedAccepted` means the listener started with 3 usable Tuna paths under an explicit degraded-readiness diagnostic override.
- `providerRecoveredAfterDegraded` means usable paths later reached the full 4-path target.
- `providerStillDegradedAtEnd` means the cell ended before full 4-path readiness was observed, and the verdict reports `provider_paths_degraded`.
- `providerQualityClass` is one of `full_ready`, `degraded_recovered`, `persistent_missing_path`, `timeout_before_degraded`, or `unknown`.
- `provider-quality-report.json` is written beside `summary.json` and should be used to compare default strict readiness against explicit degraded-readiness diagnostic runs.
- `activation_cleanup_late_peer_close` is a clean-activation warning only: full bytes, SHA match, and terminal sender/receiver snapshots are accepted even if peer-close evidence arrives late or is absent.

Provider-path A/B troubleshooting sequence:

```powershell
# Strict runtime-equivalent behavior.
$env:NLINK_TUNA_TEST_REQUIRE_PROVIDER_READY = "1"
$env:NLINK_TUNA_TEST_PROVIDER_READY_ATTEMPTS = "3"
$env:NLINK_TUNA_SOAK_CELL_FILTER = "phase6-tuna-file-helper-receiving-both-activation,phase6-tuna-file-helpee-receiving-both-activation,phase6-tuna-file-helpee-receiving-helper-cap"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"

# Degraded diagnostic: wait up to 20 seconds for the fourth provider path before accepting degraded readiness.
$env:NLINK_TUNA_TEST_REQUIRE_PROVIDER_READY = $null
$env:NLINK_TUNA_TEST_PROVIDER_READY_ATTEMPTS = $null
$env:NLINK_TUNA_TEST_DEGRADED_PROVIDER_GRACE_SECONDS = "20"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"

# Strict retry: require full 4-path readiness, with up to three attempts.
$env:NLINK_TUNA_TEST_DEGRADED_PROVIDER_GRACE_SECONDS = $null
$env:NLINK_TUNA_TEST_REQUIRE_PROVIDER_READY = "1"
$env:NLINK_TUNA_TEST_PROVIDER_READY_ATTEMPTS = "3"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"
```

Latest local Phase 6 reference run:

- Artifact root: `artifacts/tuna-sidecar/phase6-short-20260512T151146Z/`
- Verdict: `PASS`
- Cells: `12/12`
- Notes: this historical run allowed degraded 3-path provider startup and all cells reported `provider_paths_degraded`; treat it as V6 protocol/fallback evidence only. Current runtime policy requires full provider readiness before Tuna is advertised as usable.

The short matrix covers exactly 12 file-transfer cells: helper-receiving and helpee-receiving, each across helpee-only unlocked, helper-only unlocked, and both-unlocked payer modes, with one clean activation and one payer-specific fallback fault per payer. Helpee-only uses switch-off fallback, helper-only uses cap reached, and both-unlocked uses sidecar drop.

## Evidence

Primary artifacts:

- `filetransfer-operator-verdict.txt`
- `phase6-operator-verdict.txt`
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

Acceptance for V6/Tuna Phase 6:

- Completed transfers have SHA match.
- `data_protocol_version=6`.
- V6 epoch logs show start plus recovered, waiting, or terminal state as appropriate.
- Recovery is proven by `filetransfer.transport_probe.v6` acknowledgement or `filetransfer.repair_proof.v6`, not by generic bridge ready, sidecar ready, send success, or bulk bytes.
- No stuck `Sending...` or `Receiving...` card after cancel, peer close, session end, window close, or app exit.
- Cancel from either side and peer close terminalize locally first and notify the peer over regular NKN control.
- No orphan active sidecar remains after fallback/reset.
- No payload rejects, decode failures, message rejects, bridge bulk failures, media queue severe events, progress timeout, false recovery, or unresolved V6 epoch except an explicit `Waiting for regular NKN` fault result.

## Promotion Checks

Do not promote from a one-cycle smoke, an inconclusive run, a progress timeout, a cross-protocol baseline, or any run with hard failure counters. Baseline comparison gates only when current and baseline artifacts both report `data_protocol_version=6`; protocol mismatches are report-only.

Legacy names such as `v4_default_21k`, `v4_*` event names, or V4-named test files may still appear in internal logs while older helper names are retired. They do not mean the negotiated data protocol is V4.
