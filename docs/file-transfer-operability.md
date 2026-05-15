# File-Transfer Operability

For the runtime architecture and current V6 receiver-driven data-session and transport-epoch pipeline, see `docs/file-transfer-implementation.md`.

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

## Regular NKN V6 Efficiency Triage

For slow installed-build reports, first distinguish a protocol stall from inefficient completion:

- If both terminal summaries are `Completed` and SHA/integrity is clean, treat low goodput as a regular-NKN efficiency regression, not a session teardown bug.
- Check `throughput-summary.txt` for raw bytes sent versus delivered payload. A raw-to-payload ratio near `1.0` is the target; ratios near `2.0` usually mean resend pressure or delayed duplicate delivery.
- Check `v6_unsolicited_chunk_ignored_count`, `post_completion_late_sender_frame`, and `filetransfer_v6_normal_refill_near_frontier_resend_bypassed`. The near-frontier normal resend bypass is a rescue path for a non-advancing receiver frontier; it should be rare while the receiver frontier is advancing.
- Check `transfer-terminal-summary.txt` before judging speed. Sender/receiver terminal divergence, missing terminal evidence, or non-empty error codes still outrank goodput analysis.

Current regular-NKN reference artifacts:

- `artifacts/filetransfer-soak/20260515-172434/`: completed, but slow and inefficient. App goodput was `946,388 B/s`, raw V6 sender bytes were `270,413,824` for a `134,217,728` byte payload, `v6_unsolicited_chunk_ignored_count=5086`, and `post_completion_late_sender_frame=416`.
- `artifacts/filetransfer-soak/20260515-173810/`: current fixed reference. App goodput was `1,626,888 B/s`, raw V6 sender bytes were `144,926,720` for a `134,217,728` byte payload, `v6_unsolicited_chunk_ignored_count=574`, and `post_completion_late_sender_frame=0`.

## Support Capture

For support handoff, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode SupportCapture
```

Attach app Diagnostics, retained logs from `%LOCALAPPDATA%\nLink\logs`, the full analyzer artifact directory, and for live NKN runs the packaged app version, selected `ExternalTopologyProfile`, and baseline artifact directory names.
