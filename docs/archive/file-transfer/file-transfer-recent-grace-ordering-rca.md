# Recent-progress grace: rearmed wake and late publication

2026-09-15. Runtime and analyzer inspected at clean HEAD
`2691fc70f98d668bae943ebd41c6f8540156ec57`.

## Evidence boundary

Use the original primary report and immutable retained log from
`artifacts/publication-stall-v4-live-20260915/runs/20260915T121111Z/regular-v4-live-activation-off-on-off-256mb/`.
Retained SHA-256:
`fe8e110321846d58680123763785fd84c76c38cd5032637b683824c7b392ca8e`.
No historical authority evaluator was rerun, no optional publication artifact
was backfilled, and no original capture, report, receipt or verdict was changed.

The original primary result remains recent-progress Raw/Distinct/Recovered/
Unresolved **160/32/30/2**, correctness FAIL/incomplete_proof. All 40 stall-grace
obligations recovered. Completion, SHA and both peers' second Tuna-v4 publication
passed; those results do not discharge the remaining recent-progress obligations.

## First selected obligation

Receiver writer `p56952rc951ad05e4b347ba`, retained line 27271, writer sequence
15627, frontier 4992/highest 5084. The selected class is
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.

| Retained line | Writer sequence | Evidence |
| ---: | ---: | --- |
| 27269 | 15624 | Recent wake 167 rearmed, highest 5084. |
| 27595 | 15911 | Recent wake 178 rearmed, highest 5180; same frontier and original deadline. |
| 27605 | 15922 | Stall wake 258 armed, start 12:13:58.1511858Z, due 12:13:58.9011858Z. |
| 27606 | 15923 | Current recent wake 178 returns `rechecked/stall_grace_armed`. |
| 27607 | 15924 | Old recent wake 167 returns `stale/wake_generation_replaced`, authority effect none. |
| 27722 | 15957 | Three contiguous chunks committed: 4992 to 4995 at 12:13:58.3807554Z. |
| 28051 | 15964 | Three more contiguous chunks: 4995 to 4998 at 12:13:58.6548511Z. |
| 28336 | 16015 | Stall wake 258 rejected as generation replaced at 12:13:58.9017078Z. |
| 28338 | 16016 | Stall successor 259 rejected as generation replaced at 12:13:58.9026068Z. |
| 28511 | 16063 | Next contiguous advance to 5001 at 12:13:59.0262121Z, after the stall deadline. |
| 28668 | 16082 | Receiver state 97 publishes frontier 5001/highest 5252 at 12:13:59.1481682Z. |

Recent wakes 167 and 178 share committed-progress time 12:13:57.1458904Z and
deadline 12:13:58.1458904Z. The stall is fully scoped to fallback-v6 epoch 2,
runtime/leg/publication generation 3, terminal/route-commit generation 0,
transport/bridge-recovery/checkpoint/data-session generation 1, bridge-decision
generation 0, checkpoint request `(none)`. Its Coordinator identity is
`fte1-f1c35e55802d058ab3c6e32c`, source fingerprint
`fte1-4cb7d3af8f9ed26faeb4ae19`.

The last receiver state before the deadline is state 96 at retained line 27666,
writer sequence 15942: frontier remains 4992. There is no matching publication
of the advancing 4995/4998 frontier before the deadline in this interval. The
first later advancing publication is approximately **247 ms late**.

## Two independently rejecting predicates, not a missing timer

`Get-FileTransferRecentProgressGraceStallProgressClosure` in
`tools/FileTransferFrontierGraceHandoff.ps1` first requires an exact recent arm
whose generation matches the current recheck **and** whose sequence is no later
than the selected diagnostic. Wake 178 was armed after diagnostic 15627; the
earlier wake 167 does not match recheck 178. This is the first rejected join in
that natural-progress closure branch.

Even correcting that join would not prove this live obligation recovered.
The same helper requires every advancing write and its exact, newer, unpaused
receiver-state publication strictly before the unchanged 750 ms stall deadline.
The current chain does not satisfy that predicate. The earlier coalesced-write
contract explicitly excludes late publication; see
`docs/file-transfer-coalesced-frontier-publication-rca.md`.

Therefore neither "the timer never ran" nor "a wake join correction fixes the
live failure" is supported. Stale callbacks remain diagnostic-only. This RCA
does not classify the second unresolved obligation or infer a shared root cause.

## Offline verification

`FileTransferRecentProgressGraceOrderingTests` obtains fresh service-generated
Coordinator source, repair grant, delivery, dispatch, settlement, progress and
forced-publication receipts from the existing checkpoint-continuity fixture.
The test projects their local sequence/timing into a deterministic scenario;
later wake and physical records are explicitly synthetic. It does not claim a
new full-custody primary evaluation or live runtime acceptance.

On both PowerShell 5.1 and 7, flat and indexed consumers agree for 16 cases:
the unchanged-wake/on-time-publication positive control recovers; rearming alone,
late publication alone and their combination independently remain unresolved.
Session, transfer, runtime, publication, leg, checkpoint, data-session, deadline,
wake-generation, missing/current-versus-stale callback and cross-family wake
collision controls all fail closed. One focused xUnit test passes, covering
64 consumer/case/host evaluations. No production or analyzer behavior changed.
Invocation and fixture-debugging details are under
`artifacts/recent-grace-ordering-20260915/RESULTS.md`.

## Bounded fix-plan decision

The follow-up review is complete in `file-transfer-recent-grace-contract-review.md`.
It rejects delayed-publication relaxation and recovered-stall composition, and
defines the rearm-only implementation contract while preserving the original
publication deadline. Late-publication recovery remains a separate design gate.

1. Review the publication-closure contract before editing runtime or accepting
   later evidence. Decide whether the existing deadline must cover publication,
   or whether a distinct Coordinator-bound committed-progress disposition is
   needed. Do not silently replace publication proof with physical writes.
2. For the wake join, require an exact same-source, same-data-session chain of
   highest-only rearms from the pre-diagnostic arm to the current callback,
   preserving frontier, committed-progress time and deadline. Missing links,
   changed ownership, conflicting copies, generation/highest regression and
   cross-family collisions must remain failures; stale callbacks grant nothing.
3. A separate late-publication design must specify its authoritative receipt,
   temporal bound, first-publication selection and rejection on source/terminal/
   pause changes. Broadening a timestamp window or forcing ordinary receiver
   traffic without such a contract is not an approved fix.
4. After that contract is settled, add a failing implementation regression and
   change only the chosen edge. Run its focused tests and affected consumers.
   Keep the second unresolved obligation separate until its witnesses establish
   the same cause. Preserve all original primary results.

No commit, push, portable rebuild or new live-256 run was performed. A further
live gate still requires fresh source/build provenance and new authorization.
