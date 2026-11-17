# Recovery-check RCA and bounded contract review — 2026-09-27

## Outcome

The original recovery checks account for **99.07%** of measured recovery
capture: **13,306 calls / 5,273.4303 ms** inside a **5,322.6808 ms** interval.
The residual is 49.2505 ms. This confirms the previous inference about
validation overhead without replaying any original ownership evaluation.

Six focused controls passed. One fresh offline canonical attempt completed
processing and source/destination coexistence, then failed at the original
payload offer because its listener had expired. Native handoff and primary
analysis did not complete. The cumulative-work risk and integration failure
remain unresolved; no acceptance rule or functional validation changed.

The recommended next step is the bounded recovery-buffer contract below.
**Its implementation requires explicit approval.** It is not presented as an
equivalent optimization of the current per-line validation contract.

## Implementation and verification performed

Starting branch/HEAD: `v0.7.0` /
`a653d4063070235c71fe211480aeb37863d9b2a4`.
All 2,812 previous pins matched, with 396 dirty entries. Previous seal:
`artifacts/gui-preparation-cost-20260927/FINAL-VERIFICATION.json`, SHA-256
`8514ac21a2ff4c8a912d8cb5b91a89e30de4992565456b870b520176537596a3`.

Only three existing source/test files were edited:

- `tools/FileTransferOps/LocalGuiWriterRecoveryMatch.cs`: under the existing
  test-profile symbol, time the original `CheckRecoveryWork` body and record
  whether it returns normally. A `finally` records exceptional exits. Its
  predicate, short-circuit order, callers and original exception are unchanged.
- `tests/nLink.SmokeTests.Core/Support/OwnedSourceCheckProfile.cs`: accumulate
  elapsed timestamp ticks, calls and exceptional exits; include their current
  totals in existing checkpoint samples and print only after existing cleanup.
  No per-call logging/allocation, owner evaluation or clock mutation is added.
- `tests/nLink.SmokeTests.Core/FrontierPayloadWriterTests.cs`: require positive
  capture counters with zero failures in the existing successful handoff test;
  add a focused exceptional-exit and reset control on both PowerShell hosts.

The exception control creates a fresh **unowned** finalizer/binding and invokes
the real check once. It must still throw `InvalidDataException` with
`recovery_work_not_current`; the sample must count one exceptional exit while
the original stopped clock remains at zero. It then verifies counter reset.
No extra check is injected into a genuine capture or the canonical flow.

The six passing controls are: original native-writer handoff, exception/reset
behavior, and normal-loader exclusion, each on Windows PowerShell and
PowerShell 7. No aggregate/coordinator/analyzer lane was repeated. The
canonical test is separately retained as **failed**. Exact test results,
run-time source/Smoke assembly hashes, profiles and file hashes are in
`artifacts/recovery-check-cost-20260927/observations.json`.

## Canonical evidence

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-e36262a95b304df7afa07923a8fc96ad`.

Recovery counters were zero after final-ledger capture. Immediately after
recovery capture they were 13,306 calls, 52,734,303 elapsed ticks, zero failed
calls, at frequency 10,000,000. Four later check calls during matching add
1.4833 ms; they are excluded from the recovery-capture measurement above.

These are elapsed times, not CPU measurements or a throughput gate. The
instrumentation remains charged to the original cumulative clock. It measures
the existing composite check and its nested work, not the individual native
APIs within that check. It does not show a specific cheap native comparison
that can remove seconds while preserving every current call boundary.

Final-ledger capture took 1,112.1610 ms; observed-ledger processing took
2,688.5780 ms. Coexistence completed at 9,855.1241 ms; cleanup entry was
9,874.4959 ms. Only about 125.5 ms remained on the original allowance, and the
remaining native handoff work was not reached. This run therefore does not
resolve the earlier 10,001 ms failures or establish adequate remaining budget.

The wrapper records a pre-offer connect cancellation at 99,999 ms with
`lifetime_cancelled=False`, `work_ms=0`. The later GUI offer fails to connect
after 10,008 ms. These are the unchanged, separate listener/connection clocks,
not a new GUI budget failure in this attempt.

The source contains 101,843 lines / 92,070,312 bytes. Retained/writer/semantic
outputs contain 101,841 / 101,843 / 73,701 lines. Both apps completed
268,435,456 bytes on `post_tuna_fallback_v6`, with matching session/transfer and
SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent payload hashes agree and exclusive access succeeds after cleanup.
GUI 38436 / sender 37140 / receiver 55964 have exited. The GUI exit is 1,
`timedOut=false`. Payload correctness does not establish native evidence
handoff, primary evaluation or the second coordinator-published `file_tuna_v4`.

## Proposed contract: recovery-buffer-v1 — not approved or implemented

### Purpose and exact scope

Reduce repeated full native validation while doing pure computation on a
bounded buffer that was just read through an original owned recovery reader.
Reuse the existing rejection-only buffered-work guard pattern from the main
ledger processor. This proposal changes only the recovery parser's validation
boundaries; it does not change route acceptance, coordinator authority, time
budgets, native-reader authority or runtime file-transfer behavior.

| Operation | Proposed validation |
| --- | --- |
| Enter capture; open original four readers | Existing full checks unchanged |
| Before/after native reads, decoder work and buffer-processing passes | Existing full checks unchanged |
| Parse an already-buffered line; accumulate provisional marker facts | Original-reader/adopter identity, disposal, processing lifetime and original running work-budget rejection guard |
| EOF, stream completion, pair/tuple/writer matching | Existing full checks unchanged |
| Construct/return recovery markers or match; later copy/handoff/publication | Existing full checks unchanged; buffered guard never authorizes these |

Implementation is limited to replacing the two `check()` calls inside
`FrontierRecoveryMarkerScan.AcceptLine` with the rejection-only callback for
the finalizer's original native-reader path. All other `check()` calls in
`Read`, `PairsMatch` and `Matches`, and in the caller, remain full validation.
Keep the generic/one-callback scan path's existing full per-line checks; do not
let arbitrary streams or supplied success flags select the optimized path.

The buffered guard must use the existing original source/adopter identity and
processing state. It must reject disposal, cancellation, a stopped or spent
original clock, and a copied/non-original reader. It is **not a custody
receipt** and must never authorize reading, recovery, matching, publication,
progress, transport or reuse of a previously successful full check.

The four original streams must still be scanned, including the no-marker
case. Preserve the 32,768-byte read buffer, strict UTF-8/EOF behavior, line and
metadata bounds, tuple comparisons, immutable output facts and original
failure/disposal behavior. Do not use the separate reopened-file diagnostic
or copied text as ownership evidence.

### Material tradeoff requiring approval

External revocation can become visible at the next existing full read/block
or return boundary rather than at each buffered line. Buffered computation
may continue provisionally until then; it must not escape as accepted facts
or cause handoff/publication. A transient external mutation restored between
full checks may no longer be observed. No hard millisecond detection latency
is claimed, and a completed line may span multiple read buffers under the
unchanged line-size bound.

This is a deliberate validation-boundary contract change. Calling it caching,
an equivalent micro-optimization, or merely changing logging would be wrong.
Preserving the current contract means retaining all full per-line checks;
no demonstrated equivalent optimization currently removes the measured cost.
Increasing the original 10-second allowance is not proposed.

### Required implementation gates if approved

1. Retain the existing recovery parsing/tuple/resource controls on both hosts.
   Prove the generic scan still performs full per-line checks and the genuine
   owned path uses only the bounded rejection guard for provisional lines.
2. Exercise original owner/cancellation/budget/reader invalidation at native
   read boundaries, during a buffer, at EOF, and immediately before return.
   Persistent external revocation during a buffer must fail at the next full
   boundary with no returned markers/match or handoff; direct disposal/token
   cancellation and spent/stopped work must fail the buffered guard.
3. Preserve copied-reader/binding/marker/result rejection, no revival,
   original handle closure, no-marker scanning, strict decoding and all
   byte/identity/tuple checks. No test should silently retain the old per-line
   external-revocation timing as though it were still guaranteed.
4. Run only directly affected parser/recovery/Core contract controls, then one
   rebuilt targeted offline canonical attempt. No full GUI matrix or live
   network run is authorized by this contract review. Keep any newly exposed
   first failure and do not treat fallback/SHA completion as acceptance.

The existing `FrontierBufferedLedgerTests` and `FrontierProcessingCustodyTests`
provide precedent, not automatic approval or proof for recovery scanning.
Production edits, if approved, should stay within the recovery parser/binding
and directly required tests; do not tune native reader/adopter checks,
coordinator state, queues, retries, chunking, timers or unrelated paths.

The independent listener-lifetime correction remains a subsequent step with
its original cancellation/exit, connected-exchange and wrapper-work limits.
This review does not authorize changing it at the same time.

## Recovery checkpoint

Sealed in `artifacts/recovery-check-cost-20260927/FINAL-VERIFICATION.json`.
No functional fix, validation-boundary change, budget/timer change, live NKN
run, portable rebuild, full GUI matrix, commit or push occurred in this step.
