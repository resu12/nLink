# Pre-deadline Tuna supersession: implementation and offline verification

2026-09-23, working tree based on `6eb328af1155373950705d30542e98357a1a08ae`,
branch `v0.7.0`. Follow-up to
`file-transfer-predeadline-tuna-supersession-rca-20260923.md`; that original RCA
and its evidence remain unchanged. No commit or push in this step.

## Implemented contract

An admitted receiver recent-progress episode can now be `Superseded`, without
being counted as recovered, when its original fallback owner is replaced by an
exact coordinator-published Tuna-v4 owner before the captured deadline.

The closure consumes the caller's already validated, complete authoritative
snapshot. It binds the repair owner, source tuple, data session, retained-leg
checkpoint, original progress time and complete same-deadline re-arm chain.
The committed durable checkpoint must retain the exact frontier and final
highest received value. Durable checkpoint generation is not confused with
retained-leg checkpoint generation.

It reuses the existing cross-peer target proof: reciprocal paired release,
initiator retry/install and responder handoff, both committed transitions,
applied dispositions and exact target publications. Local commit, disposition
and publication must all precede the original deadline. Missing authority,
clock evidence, re-arm links, conflicting writer copies, ambiguity, pause,
terminal state, source/data replacement and ABA remain unresolved. Stale wakes
are diagnostic-only and are not positive closure evidence.

Flat/indexed and full/compact consumers share this closure. GUI classification
passes the existing snapshot and now retains disposition records in its actual
checkpoint-evidence filter. Recent accounting is explicitly version 2:

`Distinct = Recovered + Superseded + Unresolved`.

Report exports, parent imports, per-run DTOs and root summaries retain these
separate counts. Missing/invalid accounting is excluded from subtotals and
reported as incomplete; mixed policy versions cannot claim a complete total.
Legacy version-1 reports remain readable without inventing superseded counts.
Fake report overrides preserve both accounting families.

## Fresh reproduction

The offline joined-fake-peer fixture uses real service/coordinator receipts.
A physical sparse write advances the frontier from 100 to 101. Two further
sparse writes re-arm the recent wake while preserving its original one-second
deadline. The real handoff completes before expiry; the subsequent stale wake
does not change publication, runtime ownership, repair generation or frontier.
No post-start receipt or authority is fabricated.

On the same fresh persisted input, the unchanged HEAD recent reducer reports
`raw=3, distinct=1, recovered=0, unresolved=1`. The new reducer reports
`raw=3, distinct=1, recovered=0, superseded=1, unresolved=0`.
This is a deterministic regression result, not live acceptance evidence.

## Verification

Evidence directory: `artifacts/recent-progress-supersession-20260923`.

- 53 consumer cases pass on both PowerShell 5.1 and 7, including full/compact,
  actual GUI-filter parity, narrow partitions, identical/conflicting copies,
  incomplete re-arm chains, exact deadline boundaries and missing/foreign
  snapshot, peer, retry, disposition or publication proof.
- Versioned report readers/exports and parent text/JSON accounting pass on both
  hosts, including malformed, missing, partial and mixed-version results.
- Existing recent ordering/handoff, index ownership, stall supersession and
  unresolved-diagnostic regressions pass. Five end-to-end report integration
  cases also pass.
- Latest results for all 39 distinct selected .NET test cases are passing.
  The original failed test attempt is retained, not overwritten.

The initial focused run had five failures. Two ordering cases needed an explicit
writable persisted-log capture instead of the restricted default user-log path;
both pass with that existing test configuration. Two stall-only summary fixtures
needed a complete empty independent recent-family report; their assertions were
preserved. The remaining failure exposed a pre-existing catalog compatibility
gap: the unchanged HEAD stall helper rejected the current, validated
receiver-pre-admission profile. Its profile gate and exact-repair-owner lookup
now recognize that supported catalog. Stall admission, `SettledStalled` and
`StaleRejected` outcomes, and all exact cross-peer requirements remain intact;
the old positive and negative stall suites pass.

`Verify-Implementation.ps1` reproduces the old/new result on fresh fake-peer
input, consolidates latest TRX outcomes, pins implementation/evidence files and
checks all 143 original live evidence pins. It never reevaluates the historical
live input. `git diff --check` is clean. No production `src` files changed.

## Acceptance boundary and next gate

No portable rebuild, live NKN/GUI run, historical primary reevaluation, commit
or push occurred in this implementation step. Runtime timers, stale-callback
effects, retry policy, queues and performance policy are unchanged.

The old live verdict remains `FAIL/correctness/incomplete_proof`. The second
transfer's missing Tuna publication and enrichment timeout are not waived.

Next: rebuild the portable package, perform the required escalated readiness
preflights, and run only `second-transfer-after-reactivation`. Preserve a single
fresh primary evaluation and stop at its first remaining proof edge. The full
GUI matrix is not yet the appropriate next gate.
