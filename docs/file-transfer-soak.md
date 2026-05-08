# File Transfer Soak Workflow

This workflow targets the current V5-only file-transfer protocol. The current stable build remains the rollback point; do not re-enable removed legacy data-protocol paths during soak triage.

## Guardrails

- Production bridge default remains fanout.
- Mixed screen-share transfer is still controlled by the legacy-named `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE=1` flag while the V5 runtime cleanup is in progress.
- With the mixed flag off, mixed file transfer must fail cleanly with `v4_file_only_required` until the result code is renamed.
- Do not add new file-transfer wire frames as part of soak tuning unless they are part of the V5 handoff/recovery protocol.
- Optimize mixed transfer for screen-share safety and completion before speed.
- Run .NET tests serially on Windows to avoid DLL file locks.

## Common Runs

Use `FileTransfer-Ops.ps1` as the entry point:

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

Local V5 file-only proof:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 1 -Build -FailOnGate
```

Guarded V5 mixed proof:

```powershell
$env:NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE='1'
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -Build -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1800 -ProgressTimeoutSeconds 120 -FailOnGate
Remove-Item Env:\NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE -ErrorAction SilentlyContinue
```

Run the three-cycle public NKN proof only after a shorter mixed proof is clean.

## Evidence

Primary artifacts:

- `filetransfer-operator-verdict.txt`
- `filetransfer-live-nkn-summary.txt`
- `transfer-terminal-summary.txt`
- `protocol-shape-summary.txt`
- `payload-efficiency-summary.txt`
- `transport-budget-summary.txt`
- `bridge-bulk-summary.txt`
- `coexistence-summary.txt`
- `stability-gates-summary.txt`
- `baseline-comparison.txt`

Acceptance for guarded experimental mixed:

- Completed cycles with SHA match.
- `error_code=(none)`.
- `data_protocol_version=5`.
- `payload_efficiency_profile=v4_default_21k`.
- No payload rejects, decode failures, message rejects, bridge bulk failures, media queue severe events, or progress timeout.
- Screen-share frames continue during transfer.

## Promotion Checks

Do not promote from a one-cycle smoke, an inconclusive run, a progress timeout, a cross-protocol baseline, or any run with hard failure counters. Baseline comparison gates only when current and baseline artifacts both report `data_protocol_version=5`; protocol mismatches are report-only.

Note: `v4_default_21k`, `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE`, and `v4_file_only_required` are legacy names that still appear in logs and scripts. They do not mean the negotiated data protocol is V4.
