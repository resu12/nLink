# File-Transfer Operability

For the runtime architecture and current route-aware V4/V6 data-session pipeline, see `docs/file-transfer-implementation.md`.

Start every retained file-transfer investigation with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode AnalyzeRetained
```

For a deterministic local regression check before or after a file-transfer change, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalFast
```

Use `LocalImpaired` for deterministic reorder/loss recovery checks and `LocalMixed` for synthetic screen-share coexistence:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalImpaired -PayloadSizes 64KiB -Cycles 1
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode LocalMixed -PayloadSizes 64KiB -Cycles 1
```

Use `NknFast` and `NknMixed` for live operator evidence through the packaged app and NKN bridge:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknFast -Build -PayloadSizes 1MiB -Cycles 1
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode NknMixed -PayloadSizes 1MiB -Cycles 1
```

The first file to read is `filetransfer-operator-verdict.txt`. Open the detailed summaries only after the verdict points to the next artifact.

## Evidence Model

File-transfer evidence is classified before any tuning is proposed:

- `PASS`: both visible terminal sides completed with `error_code=(none)` and no hard protocol, payload, decode, security, or bridge bulk failure evidence.
- `FAIL_PROTOCOL_OR_INTEGRITY`: terminal failure, non-empty error code, payload rejection, data-frame decode failure, chunk rejection, file-transfer message rejection, receiver-buffer exhaustion, bridge stdout protocol violation, or bridge bulk send failure/clear.
- Post-completion live NKN sender data frames may be classified as `event=filetransfer_data_frame_ignored` with `reason=post_completion_late_sender_frame`. These are authenticated frames for a successfully completed transfer that arrived after receiver completion/teardown; count them as benign late delivery, not as `FAIL_PROTOCOL_OR_INTEGRITY`.
- Late sender data frames after declined, canceled, or failed transfers are not benign. They should appear as `filetransfer_message_rejected` with `reason=post_terminal_late_sender_frame_*` and must be treated as protocol/integrity evidence.
- `WARN_RECOVERED_PRESSURE`: completion succeeded but repair, reorder, degraded-mode, or fallback pressure was high enough to explain risk.
- `WARN_EXTERNAL_TRANSPORT`: completion succeeded but bridge/NKN health churn overlapped the transfer.
- `WARN_COHABITATION_PRESSURE`: completion succeeded but screen-share media queue pressure overlapped the transfer.
- `INCONCLUSIVE`: transfer frames exist but terminal evidence is missing or only one terminal side is visible.
- `INVALID_SETUP`: logs are missing, no transfer id is present, or the transfer was canceled/declined by user action.
- `FAIL_REGRESSION_BUDGET`: the local or live run completed, but its `baseline-comparison.txt` crossed a safe baseline gate.

## Operator Flow

1. Read `filetransfer-operator-verdict.txt`.
2. If the verdict is `FAIL_PROTOCOL_OR_INTEGRITY`, read `stability-gates-summary.txt` and `transport-budget-summary.txt`.
3. If the verdict is `WARN_RECOVERED_PRESSURE`, read `repair-reorder-summary.txt`.
4. If the verdict is `WARN_EXTERNAL_TRANSPORT`, read `external-transport-health-summary.txt`.
5. If the verdict is `WARN_COHABITATION_PRESSURE`, read `coexistence-summary.txt` and `bridge-bulk-summary.txt`.
6. If the verdict is `INCONCLUSIVE`, collect both peers' retained logs or rerun with a wider retained window.
7. If the verdict is `FAIL_REGRESSION_BUDGET`, read `baseline-comparison.txt` before changing transfer code.

## Rules

- Do not tune chunk size, pipeline depth, retries, or batching from a single live retained-log artifact.
- Treat data integrity and terminal completion as higher priority than throughput.
- Treat `1.5 MB/s` as the regular NKN app-goodput target; below-target runs are optimization evidence only when terminal correctness and artifact integrity are clean.
- Separate repo-owned protocol failures from external NKN/bridge health churn.
- For mixed screen-share reports, judge file transfer and media queue evidence together.
- Prefer narrow fixes backed by artifacts over additive recovery logic.
- Treat local soak modes as regression guards for core/runtime behavior, not proof of live NKN throughput.
- Treat live NKN soak artifacts as operator evidence. They may vary with topology, so compare them only with matching safe/strong baseline artifact directories.
- Pause/resume is active-session only. Restarting either app does not resume a partial transfer, and partial files must not be presented as resumable release artifacts.

## Regular NKN V4 Efficiency Triage

For slow installed-build reports, first distinguish a protocol stall from inefficient completion:

- If both terminal summaries are `Completed` and SHA/integrity is clean, treat low goodput as a regular-NKN efficiency regression, not a session teardown bug.
- Check `throughput-summary.txt` for raw bytes sent versus delivered payload. A raw-to-payload ratio near `1.0` is the target; ratios near `2.0` usually mean resend pressure or delayed duplicate delivery.
- Check protocol/route first. Current regular NKN should report route `regular_nkn_v4_fast` and protocol `4`; any `diagnostic_regular_nkn_v6` evidence in release-default runs is a hard route-selection bug.
- Check `post_completion_late_sender_frame`, repair/reorder pressure, bridge bulk send/clear counters, and route consistency before judging speed.
- Check `transfer-terminal-summary.txt` before judging speed. Sender/receiver terminal divergence, missing terminal evidence, or non-empty error codes still outrank goodput analysis.

Current regular-NKN reference artifacts:

- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/regular-nkn-v4-64mb-r2/`: current route reference. Route `regular_nkn_v4_fast`, protocol `4`, SHA OK, completed terminals, bridge bulk send failures `0`, goodput `1,769,711 B/s`.
- Older `20260515-*` V6 regular-NKN artifacts remain useful as regression history for the removed regular V6 path, not as production-route baselines.

## Tuna And Controlled Fallback Triage

Active Tuna should report route `file_tuna_v4`, protocol `4`, V4 sender/receiver runtime, and Tuna accelerated file-frame evidence. The active Tuna no-fault gate requires goodput greater than `4,000,000 B/s` when the transport is healthy.

Controlled fallback is restart-based and one-shot. The setup transfer should prove `file_tuna_v4` / protocol `4` and then terminalize or cancel cleanly after Tuna is switched off. The measured transfer must be a fresh `post_tuna_fallback_v6` / protocol `6` transfer. Current evidence shows V6 fallback is slower and more variable than the V4 regular/Tuna path, so fallback speed is informational; route consistency, SHA/integrity, and completed terminals are the gate. After a successful measured fallback, the next new transfer must return to `regular_nkn_v4_fast` / protocol `4`; a repeated `post_tuna_fallback_v6` route means fallback state was not consumed.

Current references:

- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-v4-64mb-r2/`: active Tuna V4 passed with `4,087,486 B/s`.
- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-fallback-64mb/`: measured fallback `post_tuna_fallback_v6` passed with SHA OK and completed terminals.

## Support Capture

For support handoff, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode SupportCapture
```

Attach app Diagnostics, retained logs from `%LOCALAPPDATA%\nLink\logs`, the full analyzer artifact directory, and for live NKN runs the packaged app version, selected `ExternalTopologyProfile`, and baseline artifact directory names.
