# Inbound peer-first frontier-yield guard: implementation and verification

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Status: bounded runtime fix verified; small transfer completes, but evidence finalization still fails.

## Change

Implemented the approved plan from `file-transfer-peer-first-frontier-yield-rca-20260928.md`.

- `SessionFileTransferService.cs`: before peer-first inbound local yield requests coordinator quiescence, veto a current sparse frontier gap or an unresolved frontier obligation, using current receiver progress. Reuse the ordinary inbound predicate and diagnostic. The transport-handoff exception cannot waive this veto.
- Added `SessionFileTransferInboundPeerFirstFrontierTests.cs`: four cases cover settled repair 0 with a new gap at 1, an unsettled repair contract, both handoff-flag values, retained peer proof without local yield/retirement/unlock, and exact pairing after fresh progress without peer-proof redelivery.
- Extended the existing inbound epoch-successor test with a real sparse-gap case through `CompleteInboundV6TransportEpochLocked`: reauthorization/handoff remain pending while the gap exists; the exact successor can issue after progress.
- Corrected two positive test setups to describe a gap-free frontier: the checkpoint-binding fixture now has highest received 7 and next expected 8; the no-gap epoch fixture has highest 99 and next expected 100. These are fixture corrections, not weakened expectations.
- No coordinator algorithm, authority DTO, stale-owner guard, acceptance rule, retry/timeout setting, transport tuning, or GUI behavior changed.

## Focused verification

Artifacts: `artifacts/peer-first-frontier-yield-fix-20260928/`.

| Check | Result |
| --- | --- |
| New regression before production fix, `red.trx` | 4 failures: pending peer proof did not remain pending. |
| Same regression after guard, `green.trx` | 4 passed. |
| Five existing affected tests, `affected.trx` | 3 passed; the two positive fixtures described above failed the stricter frontier admission. |
| Only those two tests after fixture correction and epoch-gap extension, `affected-followup.trx` | 3 passed, including both epoch cases. |
| Exact peer-proof retention/scope replacement/reauthorization plus two retirement-effect checks, `authority-contract.trx` | 5 passed. |

Final focused total: **15 passing cases**; no broad aggregate suite was run. The final harness build succeeded. Initial compilation needed the init-only fixture field set through the existing reflection helper. A separate sandbox failure came from Avalonia's telemetry file outside the workspace; subsequent build/test commands used `-p:UsedAvaloniaProducts=` to skip that telemetry target only. No runtime workaround was applied.

## One small offline characterization

Ran exactly one escalated `OriginalApps_CharacterizeFrontierHoldOnSmallPair` after rebuilding the harness. This uses the test assembly and a local named-pipe carrier, not a portable GUI/NKN network run. No wallet, live NKN connection, full GUI matrix, or 256 MiB transfer was used.

Run directory:

`artifacts/frame-hold-implementation-20260928/pwsh-frontier-hold-bcc409f00e1a47ceabd68fcc4dffaeb0`

The wrapper retains its existing 256 MiB source file and scenario naming; the native transferred payload in this profile is **65,536 bytes**.

Result: **the payload completed, but the overall test failed** in approximately 34 seconds (`small-pair.trx`).

Original operational log: 1,886 lines; SHA-256 `6dd502bb75ff075d3af3f4a22dd52b8c64dab919f365fccaaf7e1914f227748c`.

| Original log line | Observation |
| --- | --- |
| 1211 | Authorized inbound repair advances contiguous frontier 0 to 1. |
| 1288, 1300, 1305, 1310 | The new guard refuses peer-triggered local yield at frontier 1 / highest 3. |
| 1357 | The previously stale recent-progress wake now rechecks and arms stall grace. |
| 1483 | A subsequent authorized repair advances frontier 1 to 2. |
| 1565 | The frontier-2 wake also rechecks instead of becoming stale. |
| 1691 | Authorized repair advances frontier 2 to 4, closing the remaining gap. |
| 1775, 1805 | Outbound and inbound complete: 65,536 bytes, 4 chunks. |
| 1786 | Inbound recovery ownership retires only at terminal completion. |

Both original native stdout summaries report the same session/transfer and payload SHA-256:

`4b640d85ab3ba30fd02c9fc9db4a8928f416322ad27022ea58a65aaee68a4df2`

Independently hashed the saved `native-carrier-received/offline-carrier.bin`: 65,536 bytes and the same SHA-256. The native receiver's read-only observation summary now reaches:

`carrier-frontier-edge=observed; admitted=True; continuity=True; status=EvidenceIncomplete`

These diagnostics establish repair progress and payload completion, **not** a positive closed witness or acceptance pass.

## Remaining boundary and next step

The wrapper still reports `Original Core writer shutdown incomplete`; both native writers report an assertion failure after the completed-transfer summaries. The outer finalization record has `timedOut=false`, exit 1, and `processHasExited=true`. There are zero `filetransfer_frontier_payload_marker` events; retained analysis was not started.

The next RCA is the **native observation closure / producer-catalog preparation / writer handoff boundary**, not transfer repair:

1. Trace why the admitted original receiver observation remains `EvidenceIncomplete` after real repair and payload completion.
2. Distinguish missing proof content from producer readiness, completion-write readiness, and acknowledgment/handoff failure. The generic stderr assertion does not identify the exact failing predicate.
3. Inspect `FrontierAppLifetime.Finish`, `FrontierPayloadCatalog.PrepareForDrain`, and the original writer's final assertions using this run. Do not reinterpret SHA OK as a closed receipt, supply synthetic witnesses, add another authority evaluation, relax finalization checks, or increase deadlines.

No second characterization followed this failure. The small acceptance prerequisite remains unmet, so the held 256 MiB and live gates remain blocked.

## Preservation

Prior audit: 1,170 pins matched before editing. Only the three existing source/test files listed above were edited; one regression file and this report were added. Existing unrelated work is preserved. Rebuilt Core and test binaries are repinned, not mistaken for unexpected source changes.

GUI writer PID 40380 and native writer PIDs 57932/39284 have exited. No artifact was deleted. No commit or push was made. Evidence and final preservation checks are stored beside the TRX files.
