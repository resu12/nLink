# File-Transfer Operability

This is the operator entry point for file transfer and experimental Tuna. For runtime architecture, use [file-transfer-implementation.md](file-transfer-implementation.md); for wallet, payer, and spending details, use [nkn-tuna-implementation.md](nkn-tuna-implementation.md).

## Using File Transfer And Tuna

- Complete normal session approval and verification, and allow `Transfer files` before sending. The receiver must explicitly accept each file.
- Either side can pause, resume, or cancel the active transfer. Resume after app restart, folders, and drag-and-drop are not supported. The default cap is `25 GiB`; received files go to Downloads with collision-safe numbered names.
- Progress displays accepted/receiver-confirmed bytes, with presentation-only smoothing. A queued send is not delivery, and the displays need not be identical at every instant. A full bar alone is not final SHA verification or terminal completion.
- Tuna is optional and default-off. Link/validate a wallet in `Options -> Wallet`; this does not spend NKN or activate transport. Paid runtime use needs explicit enablement and a session-only unlock. Review price, volume, and duration caps first.
- The selected listener pays; the other peer dials without paying. When both sides unlock, helpee has priority. Payment telemetry is best-effort: no reported payment event does not prove free traffic.
- Tuna startup text and fish pulsing indicate an unlocked connection attempt. Gray means inactive, light blue identifies the non-paying side, and yellow the selected payer. Readiness or a colored icon is not authenticated file-delivery proof.
- Switching Tuna off preserves the approved session and regular NKN control. An active Tuna V4 transfer live-transitions to V6 fallback on that same transfer; explicit reactivation can return it to Tuna V4. After successful final fallback completion, the next transfer resolves from current transport state.

For an end-user problem, collect Diagnostics from both peers first; do not ask users to run paid matrices. See [supportability.md](supportability.md).

## Retained Analysis And Focused Checks

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

Local modes exercise deterministic behavior; live soak is operator evidence, not the strict release matrix. Before runtime GUI validation, run focused affected tests, rebuild the portable package, and use its packaged executable. Live NKN/GUI runs and bridge/network preflights require explicit escalation outside a restricted sandbox. Never bypass a failed preflight. See [file-transfer-soak.md](file-transfer-soak.md) for exact Phase 5 commands.

## Evidence Model

File-transfer evidence is classified before any tuning is proposed:

- `PASS`: this operator analyzer's covered terminal/integrity gates passed with `error_code=(none)` and no hard protocol, payload, decode, security, or bridge bulk failure evidence. This verdict alone does not establish the full Phase 5 route cycle, exact-binary provenance, or authenticated Tuna delivery.
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
- Only `FileTransferCoordinator` and its issued DTOs/tokens authorize route ownership, unlock/dispatch, commit/publication, Tuna-active state, repair/replay, liveness, and recovery effects. Listener readiness, ACKs, caches, queue acceptance, and transport observations are evidence only. Rejected/stale callbacks remain diagnostic-only.
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

Historical regular-NKN reference artifacts (not current-code qualification):

- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/regular-nkn-v4-64mb-r2/`: May reference, route `regular_nkn_v4_fast`, protocol `4`, SHA OK, completed terminals, bridge bulk send failures `0`, goodput `1,769,711 B/s`.
- Older `20260515-*` V6 regular-NKN artifacts remain useful as regression history for the removed regular V6 path, not as production-route baselines.

## Tuna And Controlled Fallback Triage

Active Tuna must prove actual authenticated payload delivery on coordinator-published `file_tuna_v4`, protocol `4`, with matching V4 runtime, SHA integrity, and completed terminal GUIs. Negotiation, listener readiness, send acceptance, or sidecar byte counters alone cannot close that proof. Active `file_tuna_v6` is invalid.

The current Phase 5 gate records correctness and performance separately. Existing numeric baselines and allowed reruns remain in force; a performance **FAIL** remains a failure in that dimension, not a correctness failure when all strict correctness gates pass. Legacy/Phase 4 policies must not be inferred from Phase 5's verdict separation.

Live fallback is epoch-based and one-shot for the affected transfer. When Tuna is switched off during `file_tuna_v4`, the same transfer should show explicit `filetransfer_live_route_epoch_started` and `filetransfer_live_route_epoch_recovered` evidence for `post_tuna_fallback_v6` / protocol `6`, handoff `tuna_to_normal_fallback`, target transport `regular_nkn`. Current evidence shows V6 fallback is slower and more variable than the V4 regular/Tuna path, so fallback speed is informational; route consistency, SHA/integrity, and completed terminals are the correctness gate. After successful fallback completion, stale fallback state must be consumed and the next new transfer must resolve from current transport state: `file_tuna_v4` when Tuna is active, otherwise `regular_nkn_v4_fast`.

Historical references (not proof of the current live-route contract):

- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-v4-64mb-r2/`: active Tuna V4 passed with `4,087,486 B/s`.
- `artifacts/filetransfer-route-ab/fallback-improvement-final-20260522T204000Z/tuna-fallback-64mb/`: restart-style fallback passed its then-current SHA/terminal checks; it does not prove current same-transfer live epochs or repeated activation.

## Current Release Qualification

Use the [runbook's current candidate evidence](ReleaseRunbook.md#current-qualification-update-2026-10-15) rather than historical benchmarks as the current reference. The October 15 seven-scenario Phase 5 gate on `cf8ea296` passed strict correctness; authenticated-delivery closeout also passed. It covers regular V4, authenticated Tuna V4, switch-off on either peer, helper-to-helpee payer handoff, the 256 MiB regular/Tuna/fallback/reactivation cycle, and a subsequent Tuna transfer. Separate performance FAIL/WARN results remain recorded.

The route cycle requires `regular_nkn_v4_fast -> file_tuna_v4 -> post_tuna_fallback_v6 -> file_tuna_v4 -> post_tuna_fallback_v6`, including the second coordinator publication and authenticated Tuna delivery at both activations. SHA OK or fallback completion alone is insufficient. Evidence is reusable only when the covered source, contracts, and packaged hashes remain applicable; it does not prove all network conditions or a throughput guarantee.

## Support Capture

For support handoff, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\FileTransfer-Ops.ps1 -Mode SupportCapture
```

Attach app Diagnostics, retained logs from `%LOCALAPPDATA%\nLink\logs`, the full analyzer artifact directory, and for live NKN runs the packaged app version, selected `ExternalTopologyProfile`, and baseline artifact directory names.
