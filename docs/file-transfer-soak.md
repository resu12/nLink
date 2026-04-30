# File-Transfer Soak

This file documents the retained-log analyzer, deterministic local soak runner, and artifact contract for file-transfer stabilization.

For existing logs, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode AnalyzeRetained
```

Useful options:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode AnalyzeRetained -LogDir "$env:LOCALAPPDATA\nLink\logs" -IncludeRawSlices
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode AnalyzeRetained -LogPath .\some\nlink.log -ArtifactDir artifacts\filetransfer-soak\manual
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode AnalyzeRetained -TransferId transfer_123 -TailMinutes 20
```

For deterministic local coverage, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 64KiB -Cycles 1
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalMixed -PayloadSizes 64KiB -Cycles 1
```

`LocalFast` uses the real `SessionRuntime` file-transfer path over `DevLocalTransport`, alternates transfer direction by default, verifies the received file hash and size, writes a retained-log slice, then runs the retained analyzer over all transfers from the run.

`LocalImpaired` adds deterministic DevLocal impairment to file-transfer chunk data/batches. Its default profile is `ReorderBurst`; `LossBurst` drops only the first send of selected chunks/batches so repair must recover.

`LocalMixed` starts synthetic H.264-labeled screen-share frames over `DevLocalTransport`, warms screen-share for 3 seconds, runs the file transfer while media remains active, then stops screen-share cleanly. Its default profile is `ScreenSharePressure`.

For live NKN operator coverage, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 1MiB -Cycles 1
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -PayloadSizes 1MiB -Cycles 1
```

`NknFast` launches the packaged GUI app through the GUI smoke harness, uses live `NKN`, sends deterministic payload files through the real chat file-transfer UI, accepts on the receiver, verifies saved file size/SHA-256, then runs the retained analyzer over the run log slice.

`NknMixed` starts live screen-share first, waits for helper-side frame evidence, runs the same file-transfer cycles, and stops screen-share after terminal file-transfer evidence. It is an operator soak, not a CI requirement.

Useful options:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 1MiB,16MiB,64MiB -Cycles 3
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 64KiB -Cycles 2 -ArtifactDir artifacts\filetransfer-soak\local-fast-manual
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -SafeBaselineArtifactDir artifacts\filetransfer-soak\known-safe -FailOnGate
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -ImpairmentProfile LossBurst -PayloadSizes 128KiB -Cycles 1
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalMixed -ImpairmentProfile ScreenSharePressure -PayloadSizes 64KiB -Cycles 1
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB,64MiB -Cycles 2 -SafeBaselineArtifactDir artifacts\filetransfer-soak\known-live-safe -FailOnGate
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -ExternalTopologyProfile DefaultKeepAlive -PayloadSizes 16MiB -Cycles 1
```

Payload efficiency experiments are opt-in. `Current` is the production default; candidate profiles are `Packed3x20KiB`, `Packed3x21KiB`, and `LargeSingle48KiB`.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Packed3x21KiB
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Packed3x21KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\nkn-fast-packed3x21
```

Read `payload-efficiency-summary.txt` after the verdict to compare selected profile, frame density, payload fill, V3 batch ratio, goodput, repair/reorder pressure, payload rejects, and bridge bulk health. Live `NknMixed` runs reject non-`Current` candidate profiles by default: public NKN bridge-only probes reproduced receive stalls when screen-share-sized media was mixed with near-budget bulk payloads. Use `NknFast` or local modes for candidate profiles; set `NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE=1` only for controlled stall reproduction.

## Phase B: Prove V3-Only File Transfer With Soaks

Phase B proves that new same-version transfers negotiate and start only V3, while preserving completion, integrity, V3 batching, bridge health, and screen-share coexistence. It does not tune chunk sizes, batching, repair, bridge queues, or buffering.

Use fixed artifact directories so runs can be compared:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode Test
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~SessionFileTransferProtocolNegotiationTests
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~NknFileTransfer

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 1MiB,16MiB,64MiB -Cycles 3 -ArtifactDir artifacts\filetransfer-soak\phase-b\local-fast-safe -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 1MiB,16MiB -Cycles 2 -ImpairmentProfile ReorderBurst -ArtifactDir artifacts\filetransfer-soak\phase-b\local-impaired-reorder -SafeBaselineArtifactDir artifacts\filetransfer-soak\phase-b\local-fast-safe -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 1MiB,16MiB -Cycles 2 -ImpairmentProfile LossBurst -ArtifactDir artifacts\filetransfer-soak\phase-b\local-impaired-loss -SafeBaselineArtifactDir artifacts\filetransfer-soak\phase-b\local-fast-safe -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalMixed -PayloadSizes 1MiB,16MiB -Cycles 2 -ArtifactDir artifacts\filetransfer-soak\phase-b\local-mixed -SafeBaselineArtifactDir artifacts\filetransfer-soak\phase-b\local-fast-safe -FailOnGate

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 1MiB -Cycles 1 -TimeoutSeconds 900 -ArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-smoke -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB,64MiB -Cycles 2 -TimeoutSeconds 1200 -ArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-safe -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -PayloadSizes 16MiB -Cycles 1 -TimeoutSeconds 1200 -ArtifactDir artifacts\filetransfer-soak\phase-b\nkn-mixed -SafeBaselineArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-safe -FailOnGate
```

Optional topology comparisons are report-only and should use `-StrongBaselineArtifactDir`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 1 -ExternalTopologyProfile PinnedMainnetRpc -ArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-pinned-rpc -StrongBaselineArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-safe
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 1 -ExternalTopologyProfile DefaultKeepAlive -ArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-keepalive -StrongBaselineArtifactDir artifacts\filetransfer-soak\phase-b\nkn-fast-safe
```

For every run, read `filetransfer-operator-verdict.txt` first. Clean proof runs (`LocalFast`, `LocalMixed`, `NknFast`, and `NknMixed`) should report `PASS`. `LocalImpaired` may report `WARN_RECOVERED_PRESSURE` only when completion and integrity are clean and the impairment evidence explains the warning. Safe-baseline throughput gates and impairment-driven file-transfer pressure counters are skipped for `LocalImpaired`; bridge/media counters and hard-failure counters still gate.

V3-only proof counters live in `protocol-shape-summary.txt`:

- `legacy_data_protocol_started_count=0`
- `legacy_negotiation_rejected_count=0` for same-version soaks
- `legacy_v2_request_frame_during_v3_count=0`

Hard failures for Phase B include terminal failure, protocol/integrity failure, payload reject, decode/security/message rejection, bridge bulk send failure or stale clear, legacy V2 data start, and `WARN_COHABITATION_PRESSURE` in mixed proof runs.

Old-app compatibility is covered by negotiation tests and should fail cleanly with `transport_incompatible`; it is not expected to succeed in soak runs.

When `-CycleTimeoutSeconds` is omitted, live NKN modes use a 600 second cycle timeout so larger public-network payloads are not failed by the local-mode default. Reused live artifact directories are cleaned at the start of each run.

## Phase 5: Payload Efficiency Experiment

Phase 5 compares better-filled V3 bulk payloads without changing the default profile. Use fixed artifact directories:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode Test

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Current -ArtifactDir artifacts\filetransfer-soak\phase-5\local-current -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Packed3x20KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\local-packed3x20 -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Packed3x21KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\local-packed3x21 -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile LargeSingle48KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\local-large-single -FailOnGate

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Current -ArtifactDir artifacts\filetransfer-soak\phase-5\nkn-current
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Packed3x20KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\nkn-packed3x20
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile Packed3x21KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\nkn-packed3x21
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB -Cycles 2 -PayloadEfficiencyProfile LargeSingle48KiB -ArtifactDir artifacts\filetransfer-soak\phase-5\nkn-large-single
```

A candidate needs at least 20% better average live goodput than `Current`, zero hard failures, no payload rejects/decode/security failures, no bridge bulk failures or queue clears, and no more than 25% higher reorder/retry/timeout counts. Do not run packed live `NknMixed` candidates by default; first validate the candidate with `NknFast` and bridge-only probes, then use the unsafe mixed override only when intentionally reproducing or investigating receive stalls.

## Live Receive-Stall Troubleshooting

When a mixed live run hangs, use the receive-stall matrix with short watchdogs so the run fails into artifacts instead of waiting for manual termination:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-FileTransferReceiveStallMatrix.ps1 -Build -PayloadSize 16MiB -TimeoutSeconds 180 -CycleTimeoutSeconds 120 -ProgressTimeoutSeconds 30
```

Read `filetransfer-operator-verdict.txt` first, then `external-transport-health-summary.txt` and `throughput-decomposition-summary.txt`. A receive-liveness stall is indicated by `ready_sending_zero_receive_window_count` and high `*_last_received_age_ms` while bridge channels remain ready and sends continue.
The matrix skips packed mixed payload profiles by default. Add `-IncludeUnsafePackedMixed` only when you intentionally want the known-risk `Packed3x21KiB` mixed case.

To isolate the bridge/NKN layer without app session logic, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Run-NknBridgeReceiveProbe.ps1 -DurationSeconds 60
```

The probe writes `bridge-receive-probe-summary.txt`, `bridge-receive-probe-summary.json`, and `bridge-receive-probe-events.jsonl`.

## Phase S4: Reassess V3 Capacity After Bug Fixes

Phase S4 checks whether the current V3 file-only NKN path can reliably reach `>=2 MiB/s` after the sticky-limited, sender/receiver pump, and telemetry fixes. It is evidence-only: do not change payload profile, grant/window policy, repair, bridge concurrency, sender pump, receiver pump, sparse receive, or protocol shape while running this matrix.

Preflight:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode Test
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~SessionFileTransferPullWindowingTests
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~SessionFileTransferPullRepairTests
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~SessionFileTransferReceiverFeedbackPumpTests
```

Local sanity:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 16MiB -Cycles 1 -PayloadEfficiencyProfile Auto -NoBuild -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 16MiB -Cycles 1 -ImpairmentProfile ReorderBurst -NoBuild -FailOnGate
```

Live default and fixed-window diagnostics:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 64MiB -Cycles 2 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-s4\nkn-default -FailOnGate

$env:NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES="8388608"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-s4\nkn-fixed-8m -FailOnGate
Remove-Item Env:\NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES

$env:NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES="16777216"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-s4\nkn-fixed-16m -FailOnGate
Remove-Item Env:\NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES
```

Rollback isolation is diagnostic-only. Run these one-cycle comparisons only if the default run stalls or regresses:

```powershell
$env:NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP="0"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-s4\nkn-receiver-feedback-pump-off -FailOnGate
Remove-Item Env:\NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP

$env:NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP="0"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-s4\nkn-async-sender-pump-off -FailOnGate
Remove-Item Env:\NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP
```

If the default run completes cleanly, run `BulkFanout8` report-only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -ExternalTopologyProfile BulkFanout8 -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-s4\nkn-bulkfanout8
```

Read `filetransfer-operator-verdict.txt` first, then `filetransfer-live-nkn-summary.txt`, `throughput-decomposition-summary.txt`, `repair-reorder-summary.txt`, and `bridge-bulk-summary.txt`. A capacity-clean run requires `PASS`, average goodput `>=2097152`, no payload reject, no decode/security failure, no bridge bulk failure or queue clear, no terminal failure, no timeout storm, and no V2 repair chatter.

Interpret the primary limiter conservatively:

- `file_only_sparse_window_capacity_proven`: V3 capacity is sufficient; keep V3.
- `sparse_frontier_gap_repair_stalled`: fix frontier-gap repair/fill reliability before any more throughput tuning.
- `sticky_limited_without_pressure`: the limited-state bug is not fixed.
- `receiver_feedback_blocking_limited`: fix or roll back receiver feedback pump behavior.
- `nkn_delivery_limited` or `external_transport_limited`: investigate bridge/NKN delivery, not V3 policy.
- `sender_credit_wait_limited` without gap/repair pressure: inspect sender feedback application before more grant tuning.

If fixed `16 MiB` reaches `>=2 MiB/s` but default does not, keep V3 and make adaptive policy match the fixed-window evidence. Only consider V4 if fixed windows fail with low sender credit wait and healthy bridge feed.

## V4 Phase 5: Analyzer, Ops, And Soaks

V4 file-only NKN runs use the existing `NknFast` operator path. `NknMixed` is intentionally unsupported for V4 in this phase and should fail cleanly with `v4_file_only_required`.

Read artifacts in this order:

```text
filetransfer-operator-verdict.txt
filetransfer-live-nkn-summary.txt
protocol-shape-summary.txt
payload-efficiency-summary.txt
throughput-decomposition-summary.txt
repair-reorder-summary.txt
bridge-bulk-summary.txt
baseline-comparison.txt
```

Preflight:

```powershell
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~FileTransferOpsScriptsTests
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~NknFileTransferTransportTests
dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj --filter FullyQualifiedName~SessionFileTransferV4
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode Test
```

Soak proof matrix:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast -PayloadSizes 64KiB,16MiB,64MiB -Cycles 3 -NoBuild -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 16MiB -Cycles 1 -ImpairmentProfile ReorderBurst -NoBuild -FailOnGate

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 1MiB -Cycles 1 -TimeoutSeconds 600 -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 16MiB,64MiB -Cycles 2 -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-v4-5\nkn-fast-safe -FailOnGate
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -SafeBaselineArtifactDir artifacts\filetransfer-soak\phase-v4-5\nkn-fast-safe -FailOnGate
```

Optional report-only topology comparison after the default live proof is clean:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -ExternalTopologyProfile BulkFanout8 -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-v4-5\nkn-bulkfanout8
```

Expected clean V4 file-only signals:

- `filetransfer-operator-verdict.txt` reports `PASS`.
- `filetransfer-live-nkn-summary.txt` reports `data_protocol_version=4`.
- `payload-efficiency-summary.txt` reports `payload_efficiency_profile=v4_default_21k`.
- `protocol-shape-summary.txt` shows V4 sender/receiver/state/batch/complete evidence, `legacy_data_protocol_started_count=0`, and `unexpected_legacy_data_frame_during_v4_count=0`.
- `v4_feedback_both_failed_count=0`.
- No payload reject, decode/security failure, terminal failure, bridge bulk failure, queue clear, or unexpected `v4_runtime_not_implemented`.

Baseline comparison is protocol-aware. Safe baselines gate only when `data_protocol_version` matches; V3/V4 comparisons write `baseline_protocol_mismatch=1` and are report-only. Use `NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY=0` only as a rollback diagnostic for V4 feedback redundancy.

## V4 Phase 6: Promote Or Iterate

Phase 6 turns the V4 evidence loop into a release decision. The first-read order is:

- `filetransfer-operator-verdict.txt`
- `v4-promotion-decision.txt`
- `filetransfer-live-nkn-summary.txt`
- `throughput-decomposition-summary.txt`
- `payload-efficiency-summary.txt`
- `protocol-shape-summary.txt`
- `bridge-bulk-summary.txt`
- `baseline-comparison.txt`

Mandatory long public NKN proof:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 16MiB,64MiB -Cycles 2 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-v4-6\nkn-fast-safe -FailOnGate
```

The long proof must contain four completed, integrity-clean V4 cycles: `16MiB` cycle 1, `64MiB` cycle 1, `16MiB` cycle 2, and `64MiB` cycle 2. Capture that directory only if it is clean.

Mandatory same-protocol baseline rerun after the long proof:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -SafeBaselineArtifactDir artifacts\filetransfer-soak\phase-v4-6\nkn-fast-safe -FailOnGate
```

`v4-promotion-decision.txt` reports one of:

- `promote_v4_file_only`: long proof and same-protocol baseline rerun are clean, and long-proof average goodput is at least `2097152` bytes/sec.
- `iterate_sender_pump`, `iterate_state_feedback`, `iterate_missing_range_repair`, `iterate_nkn_bulk`, or `iterate_external_transport`: correctness is clean but throughput missed target with a specific limiter.
- `hold_inconclusive`: the run is not V4, the long matrix is incomplete, a hard failure occurred, the baseline rerun is missing, or the limiter evidence is not actionable.

Optional report-only topology comparison:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -PayloadSizes 64MiB -Cycles 1 -PayloadEfficiencyProfile Auto -ExternalTopologyProfile BulkFanout8 -TimeoutSeconds 1200 -ProgressTimeoutSeconds 120 -ArtifactDir artifacts\filetransfer-soak\phase-v4-6\nkn-bulkfanout8
```

Do not promote from a one-cycle smoke, an inconclusive run, a V3/V4 cross-protocol baseline, a progress timeout, or any run with payload reject, decode/security failure, V4 feedback both-failed, V4 sender/receiver failure, bridge bulk failure, queue clear, terminal failure, unexpected `v4_runtime_not_implemented`, or V2/V3 data-frame evidence.

## Artifact Contract

Every retained analysis writes:

- `filetransfer-operator-verdict.txt`
- `transfer-terminal-summary.txt`
- `throughput-summary.txt`
- `throughput-decomposition-summary.txt`
- `payload-efficiency-summary.txt`
- `protocol-shape-summary.txt`
- `repair-reorder-summary.txt`
- `transport-budget-summary.txt`
- `bridge-bulk-summary.txt`
- `coexistence-summary.txt`
- `external-transport-health-summary.txt`
- `stability-gates-summary.txt`
- `v4-promotion-decision.txt`
- `v4-promotion-decision.json`

Local soak modes also write:

- `filetransfer-local-soak-summary.json`
- `filetransfer-local-soak-cycles.jsonl`
- `filetransfer-local-soak-summary.txt`
- `filetransfer-retained-log-slice.log`
- `filetransfer-impairment-summary.txt`
- `mixed-screenshare-summary.txt`
- `baseline-comparison.txt`

Live NKN soak modes also write:

- `filetransfer-live-nkn-summary.json`
- `filetransfer-live-nkn-cycles.jsonl`
- `filetransfer-live-nkn-summary.txt`
- `filetransfer-retained-log-slice.log`
- `baseline-comparison.txt`

When `-IncludeRawSlices` is set, it also writes `raw-log-slices.txt`.

## What Each Artifact Answers

- `transfer-terminal-summary.txt`: did the visible sender and receiver reach terminal completion?
- `throughput-summary.txt`: how many binary frames and useful bytes were observed?
- `throughput-decomposition-summary.txt`: which link most likely limited throughput?
- `payload-efficiency-summary.txt`: which payload profile was used, how full V3/V4 bulk frames were, and whether efficiency changed reliability pressure?
- `protocol-shape-summary.txt`: which frame types, profiles, V4 state/feedback events, and step-up/step-down events appeared?
- `repair-reorder-summary.txt`: did retry, timeout, reorder, degraded, V4 missing-range repair, or fallback behavior become risky?
- `transport-budget-summary.txt`: did V3/V4 batching, split fallback, bridge budget, decode, or security behavior look healthy?
- `bridge-bulk-summary.txt`: did NKN bulk queueing fail or clear stale frames?
- `coexistence-summary.txt`: did screen-share media pressure overlap file transfer?
- `external-transport-health-summary.txt`: did bridge/NKN health churn overlap the transfer?
- `stability-gates-summary.txt`: why the verdict passed, warned, failed, or stayed inconclusive.

For live NKN startup throughput, read `throughput-summary.txt` first. Clean fast-ramp evidence is `conservative_startup_probe_count>0`, `conservative_startup_fast_clean_count>0`, `startup_exit_reason=startup_fast_clean`, and `first_repair_or_timeout_before_startup_exit_count=0`. A clean transfer may still skip these counters when the payload is too small to leave conservative startup.

## Current Phase Boundary

Use a clean live artifact directory as a future `-SafeBaselineArtifactDir` or `-StrongBaselineArtifactDir`. The safe baseline is gating only when `-FailOnGate` is supplied; the strong baseline is report-only.

`LocalFast`, `LocalImpaired`, and `LocalMixed` are deterministic and local-only. `NknFast` and `NknMixed` exercise live NKN operator paths and external bridge health. None of these modes tune retry/window behavior or alter file-transfer protocol constants.
