# Native frontier witness window: RCA and bounded next-step plan

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Status: missing native witness prerequisites; no runtime or acceptance change justified by this run.

## Outcome

The peer-first yield guard restored repair progress and the 65,536-byte transfer completed with matching SHA-256. The native evidence episode did not meet the reviewed timing contract: the next frontier commit occurred **230.149 ms after the original stall deadline**, not before it. The receiver reported `admitted=True; continuity=True; status=EvidenceIncomplete`; no positive payload marker was emitted.

This is not evidence that a successful transfer was incorrectly rejected by the writer. Payload correctness and positive native witness production are separate requirements. The exact first internal observation-close branch is not recorded, so this RCA does not claim to identify that branch.

## Evidence and timing

Use the original small-pair run from the immediately preceding implementation, not an older live artifact:

`artifacts/frame-hold-implementation-20260928/pwsh-frontier-hold-bcc409f00e1a47ceabd68fcc4dffaeb0`

Its `live-operational-log/nlink.log` has 1,886 lines and SHA-256 `6dd502bb75ff075d3af3f4a22dd52b8c64dab919f365fccaaf7e1914f227748c`. Selected exact records are retained in `artifacts/native-witness-window-rca-20260928/EVIDENCE.json`.

| Original log line | Recorded event or field |
| --- | --- |
| 1174 | Released epoch-0 batch starting at chunk 0 is rejected: `fallback_frontier_protocol_alias_missing`. |
| 1211, 1214 | Authorized repair advances frontier 0 to 1; committed progress UTC is `08:14:35.1889855`. |
| 1353 | Frontier-1 stall root UTC is `08:14:36.2046076`; original deadline is `08:14:36.9546076`. |
| 1357 | Recent-progress recheck decides `stall_grace_armed`. |
| 1373 | Stall wake is rearmed with `stall_grace_deadline_pending`, retaining the same root/deadline. |
| 1469, 1473 | Frontier request is sent; stall wake reports `exact_receiver_request_dispatched`. |
| 1483, 1485, 1487 | Repair advances frontier 1 to 2; next commit UTC is `08:14:37.1847566`. |
| 1691 | Repair advances frontier 2 to 4. |
| 1775, 1805 | Outbound and inbound transfer complete. |
| 1885, 1886 | Both operational-log writers record their drain events. |

All listed UTC fields are on 2026-09-28. Subtracting the original fields gives a 750 ms stall window and a next commit 980.149 ms after its root: **230.149 ms late**. This is diagnostic arithmetic, not a new timing/authority evaluation; no QPC samples or positive timing result were manufactured.

There are **zero exact `filetransfer_v6_receiver_state_deferred` events with `reason=frontier_stalled`**. Request-window `frontier_stalled=1` fields and the earlier `chunk_batch_frontier_stalled` control-coalescing event do not substitute for that event. `LocalNativeFrontierWitness.ps1:95` requires the exact event/reason and fallback-v6 scope. Retained primary analysis never started, so no selected primary failure or acceptance verdict is claimed here.

## Observation closure versus writer handoff

- `SessionFileTransferService.FrontierPublicationBoundary.cs:184`: after source binding, the first follow-up write needs the original root, rechecked tip, recent-progress record, progress record, matching frontier, and the same current stall-wake object. A missing/replaced tip closes the observation negatively.
- `SessionFileTransferService.PullTransferSessionV6.cs:7975`: successful deadline-triggered request dispatch clears the current stall wake before the dispatch diagnostic. The run records dispatch before the next commit. If still open at that write, the observation cannot satisfy the current-tip check. A repeated repair callback also closes an already source-bound observation negatively (`FrontierPublicationBoundary.cs:209`). The retained log does not distinguish which close occurred first.
- `FileTransferCoordinator.FrontierPublicationClock.cs:75`: even if those ownership checks were satisfied, a first commit at or after the original deadline cannot prove a pre-deadline commit. The independently recorded UTC condition already fails.
- `SessionFileTransferService.FrontierPublicationPayload.cs:53`: payload preparation refuses an observation that is not closed with a positive matching timing outcome. Completed file SHA does not override this requirement.
- `SessionFileTransferService.FrontierAppLifetime.cs:76`: producer preparation, completion-write readiness, file adoption, acknowledgment, and attachment completion are separate gates. Ordinary log flush runs in the finalization path; its return value is separate from `HandoffCompleted`.
- `FrontierAttemptWritersJoin.ps1:3`: the generic shutdown error combines current-owner, file-handoff acknowledgment, app-request completion, and output-drain predicates. The native test also has separate flush and handoff assertions. The generic error and assertion text do not identify every failed predicate.

Thus the saved negative observation explains why a positive payload is unavailable, while the drain records prevent treating the generic wrapper message as proof of a disk-flush defect. No writer-drain workaround is warranted.

## First unmet episode and bounded fix plan

The one-shot opaque hold was sufficient to expose a real gap and exercise authorized repair. It was not sufficient to establish the later pre-deadline publication episode required by `file-transfer-same-transfer-witness-contract-20260928.md`.

The current carrier holds the first eligible Regular bulk frame, releases it after a 2-second test delay, and retains no second candidate. Its released old-epoch batch is rejected before the first repair. The subsequent frontier-1 repair is requested only after the original stall deadline; delivering that response faster cannot create an earlier authorized packet.

**Next scope: test-only delivery-schedule feasibility/design review, not an observer or runtime fix.**

1. Identify whether a genuine, already-authorized, current-scope data frame can exist independently of the post-deadline repair request and remain deliverable after the real recent-grace recheck but before the original stall deadline. Establish this from the actual sender/receiver call path before proposing a gate.
2. Account separately for the required genuine deferred-state event during recent grace. Do not substitute a request-window flag, inject a diagnostic, or override primary failure selection. A timely publication can also leave no eligible unresolved primary failure; these gates remain separate.
3. If logical-frame selection is necessary, document the explicit test-only seam and review its scope before implementation. Preserve opaque exact bytes, bounded ownership/cancellation, and the existing one-frame-per-direction / 1 MiB limits unless a separately reviewed contract changes them. No decryption/rewrite, private state mutation, fabricated clocks, new repair authority, or coordinator bypass.
4. If no authorized candidate can exist in that window under the current contract, report the feasibility boundary. Do not silently extend deadlines, reopen a negative observation, retain a stale wake as authority, reuse the rejected epoch-0 frame, or synthesize a positive handoff.
5. Only after an implementable schedule is agreed: add directly affected carrier/producer regressions, then perform one rebuilt small offline characterization. Require the complete original native episode and honest finalization; do not retry identical schedules or start a 256 MiB run to search for luck.

No timeout, retry cadence, chunk size, queue/concurrency, throughput, transport ownership, resolver acceptance, or timing threshold change is proposed.

## Verification and preservation

This RCA used saved original evidence and source inspection only: no tests, builds, new analyzer/authority evaluations, or transfer reruns. The previous 15 passing focused cases are prior evidence, not new executions.

Before documentation edits, all 1,206 prior pins and all 462 existing dirty-file statuses/hashes matched the previous verification seal. This turn adds this report and ignored evidence/preservation records only; source, tests, binaries, original logs, and acceptance rules remain unchanged. Final audit records are stored beside the extracted evidence. No commit or push was made.

The small positive-evidence prerequisite remains unmet. The 256 MiB, live, and full-matrix gates remain blocked.
