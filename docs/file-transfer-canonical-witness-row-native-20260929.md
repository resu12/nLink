# Canonical witness row native validation: visit-budget RCA

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **the input-row time-budget edge advanced**. One fresh offline native
256 MiB attempt completed and failed closed at a later **record-visit limit**
inside lifecycle/progress witness selection. This is not a route acceptance
pass. No production or test source was changed in this phase.

## Execution and provenance

Entry verification matched 5,833 file pins and 601 dirty entries. The initial
sandboxed build was denied access to Avalonia's AppData build log; the same
build outside the sandbox succeeded with zero warnings/errors. This was a
sandbox write restriction, not a runtime defect. The Core test DLL remained
`cc53e201d12780014b4fcaf792fbdc51a4988ed3be8b074751ccd89cba59332c`.

Both strict preflights ran outside the sandbox and passed, checking 5,839
frozen pins, the exact dirty set, original prerequisite controls and the latest
56 passing projection/custody checks. Those pinned control results were
verified, not rerun. Process isolation, debug policy, high-resolution clock and
disk checks passed. Frozen input SHA256:
`3f97503f55857eb618b06defb5f169ccb40e9ec04fa93dc5d7dcbce6a279e95f`.

Exactly one
`FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`
ran: failed after **336.3060350 seconds**, without the 600-second test timeout
or analyzer timeout. There was no automatic retry, live NKN connection,
portable GUI rerun, full matrix or saved-capture authority re-evaluation.

Fixture:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-945f79fc2af34bb3ab479720d99979a7/`.

## Transfer, original evidence and cleanup

Both original peers completed 268,435,456 bytes on `post_tuna_fallback_v6`.
Both payloads match SHA256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The late-publication episode closed within the original observation bound,
with three physical copies and unchanged root. The final recheck was
**268.5219 ms before the unchanged deadline**; three retained-tail reads
completed in 605.1256 ms total. These are passing observations for this attempt,
not a claim that all intermittent scheduling behavior is solved.

The original analyzer used 73,904 full lines and 1,583 compact lines, with
three evaluations and one native read. Its input hash/count remained
unchanged. Full and compact views were SnapshotV1Authoritative, stream-complete,
evidence-pass, with 1,507 boundaries each and parity pass.

Witness count remains zero; same-pair join and diagnostic output commit remain
false. The original selected failure remains
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`,
with FAIL_PROTOCOL_OR_INTEGRITY and gate fail. The analyzer exited 1 with
completed proof artifacts, not a timeout.

All ten original report hashes match before/after and still match disk.
Native pins closed to zero, text was cleared and the evaluation retired.
Independent out-of-sandbox cleanup found no native/test process conflict;
peer PIDs 48164/44396 and primary PID 50480 exited. Both payloads are exclusively
readable, correctly sized and SHA-correct.

## Observed first failing edge

The unchanged bounds are 20,000 ms and 2,097,152 visits. At rejection the
profile records **17,938.8708 ms and 2,097,153 visits**. The increment-before-check
guard in `LocalFrontierPrimaryEvaluations.cs:65` therefore rejected the first
visit over the cap, with about 2,061.1292 ms still below the time limit.
The shared error code is `frontier_evaluation_budget`; this run is a visit-cap
failure, not the previous row-parsing time failure.

| Callback phase | Time | Charged visits | Result |
| --- | ---: | ---: | --- |
| Initial snapshot | 3.2658 ms | 3 | completed |
| Input-row projection | 3,196.6100 ms | 73,904 | all 73,904 rows materialized |
| Failure/receipt matching | 13.0166 ms | 1 | completed; one selection |
| Ownership scan | 289.6973 ms | 73,904 | completed; 9,083 owned rows |
| Lifecycle/progress selection | 249.4567 ms | 113,839 | visit cap; five partial selections |

Cumulative work at resolver entry was 14,095.1611 ms. Availability checking
took 2.2099 ms, original-state capture 57.0822 ms and the callback 3,784.4171 ms.
The original-state/currentness and ownership checks were not bypassed.
The callback caught the original budget exception and returned null; C#
recorded selection-validation before rejecting that null. No witness-capture,
final-currentness or accepted stage was reached.

The previous attempt stopped after only 6,124 of 73,955 rows. This fresh attempt
finished its entire heterogeneous native row set with exactly one visit per
row, then advanced through receipt matching and ownership. It validates that
the targeted first edge advanced, not that the complete witness flow fits.

### Narrowing the repeated-scan failure

Direct source inspection shows that phase 4 charges visits for its bounded
8–64-entry lifecycle list, one 73,904-row prior-origin guard, and `Named`
queries. Each `Named` query currently walks all 9,083 owned rows before
filtering by event name. The same first-value parsing and ambiguity checks
remain in place.

Source-derived arithmetic (not another evaluation): phase 4 consumed 113,839
visits. Subtract its full-row guard and four completed owned-row scans:
`113839 - 73904 - 4*9083 = 3603`. After the bounded lifecycle walk, the
rejecting visit lies 3,539–3,595 rows into a **fifth Named scan**. Selection
growth from one to five is consistent with those four completed queries.
The exact requested event name is not recorded by the fixed phase profile;
no more specific query is claimed.

This pins the new edge to repeated diagnostic candidate scans in `Named`
(`LocalNativeFrontierWitness.ps1:92`), not row projection, transfer integrity,
receipt eligibility, coordinator authority or wall-clock throughput.

## Bounded fix plan

1. Add deterministic cross-host fixtures comparing ordered `Named` results,
   predicate invocation order, ambiguity/duplicate behavior and first rejection
   against the original scan. Include many unrelated owned rows, absent names,
   case/Unicode names, duplicate matches and budget denial. Use synthetic
   fixtures, not saved native evidence.
2. Build a callback-local event-name lookup while the existing ownership scan
   already visits/adopts each row. Keep ownership and relevant-row scope checks
   in their existing order. Preserve original row references and source order;
   use comparison semantics equivalent to PowerShell's case-sensitive equality,
   not an assumed ordinal or case-insensitive dictionary.
3. Have `Named` inspect only its indexed candidate bucket, charging the original
   visit before every candidate inspection and leaving its predicate and
   downstream checks unchanged. Eliminate repeated inspections rather than
   discounting actual work, resetting counters or raising limits. Index building
   stays inside the original callback's time accounting and existing bounded
   ownership pass. It is diagnostic lookup data, never a custody or route token.
4. Verify focused selection/lookup and directly affected witness/custody controls
   on both hosts, followed by at most one relevant contract lane. Preserve
   fail-closed mutation/currentness checks and original verdict/report hashes.
5. Rebuild/freeze and use strict preflights for one fresh targeted native
   validation. Later full-row scans and final currentness work still consume
   visits; this plan makes no claim that eliminating this first repeated-scan
   edge will make the entire flow fit. Report any later first edge separately.

Do not alter the compiled input-row checks, the prior-origin guard, unrelated
later scans, state capture, budgets, acceptance, coordinator decisions, native
lifetime, retry/fallback policy or transport performance to make this pass.

## Checkpoint

Artifacts, frozen inputs, preflights, original TRX, raw timing lines, arithmetic,
cleanup and final verification are under
`artifacts/canonical-witness-row-native-20260929/`.
No source fix, acceptance change, extra test lane, cleanup deletion, commit or
push was performed. The full live GUI matrix remains gated.
