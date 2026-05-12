# File Transfer Soak Workflow

This workflow targets the current V6-only file-transfer protocol. V5, V4, null, or mismatched peers must fail cleanly as transport-incompatible; do not re-enable legacy data-protocol compatibility during soak triage.

## Guardrails

- Production bridge defaults remain unchanged.
- Regular NKN control remains authoritative for lifecycle, liveness, V6 epoch acknowledgements, and terminalization.
- Tuna remains experimental and default-off unless the Phase 6 paid gate passes.
- Do not redesign screen sharing, wallet UX, payer policy, caps, sidecar startup, installer behavior, or Diagnostics/Options UI while tuning file-transfer soak.
- All generated artifacts must stay under repo `artifacts/`. Manual scripts and runbooks must never delete or clean Downloads or other user data folders.
- Run .NET tests serially on Windows to avoid DLL file locks.

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

## Phase 6 Paid Tuna Gate

Before paid Tuna time:

```powershell
dotnet build src\nLink.App\nLink.App.csproj -c Release
$version = (Get-Content VERSION -Raw).Trim()
go -C tools\nkn-tuna-sidecar build -ldflags "-X main.sidecarVersion=$version" -o ..\..\artifacts\tuna-sidecar\nlink-tuna-sidecar.exe .
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter "FullyQualifiedName~SessionFileTransferV6TunaIntegrationTests|FullyQualifiedName~SessionFileTransferV6TransportEpochTests|FullyQualifiedName~SessionFileTransferV6RuntimeTests|FullyQualifiedName~SessionFileTransferPauseTests|FullyQualifiedName~NknAccelerationTransportTests|FullyQualifiedName~NknFileTransferTransportTests|FullyQualifiedName~DiagnosticsAndLoggingTests|FullyQualifiedName~FileTransferOpsScriptsTests" -c Release
```

Run the short paid matrix only from an explicit opt-in shell:

```powershell
$env:NLINK_RUN_MANUAL_BRIDGE = "1"
$env:NLINK_RUN_TUNA_PHASE6_SHORT_MATRIX = "1"
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"
```

The Phase 6 short matrix writes artifacts under `artifacts/tuna-sidecar/phase6-short-<timestamp>/`. Read `phase6-operator-verdict.txt` first, then keep `summary.json`, `runs.jsonl`, redacted app log tail, listener stdout/stderr, and sidecar cleanup evidence.

Provider readiness warnings are now split so a degraded startup can be distinguished from a persistent provider-path problem:

- `providerDegradedAccepted` means the listener started with the allowed 3 usable Tuna paths.
- `providerRecoveredAfterDegraded` means usable paths later reached the full 4-path target.
- `providerStillDegradedAtEnd` means the cell ended before full 4-path readiness was observed, and the verdict reports `provider_paths_degraded`.
- `activation_cleanup_late_peer_close` is a clean-activation warning only: full bytes, SHA match, and terminal sender/receiver snapshots are accepted even if peer-close evidence arrives late or is absent.

Latest local Phase 6 reference run:

- Artifact root: `artifacts/tuna-sidecar/phase6-short-20260512T151146Z/`
- Verdict: `PASS`
- Cells: `12/12`
- Notes: all cells reported `provider_paths_degraded`; two activation cells used the allowed single retry. Treat this as a passed V6/Tuna gate with follow-up diagnostics, not as promotion evidence for enabling Tuna by default.

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
