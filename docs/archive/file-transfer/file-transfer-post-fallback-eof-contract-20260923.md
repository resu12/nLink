# Post-fallback precommit EOF handoff and retry-cycle snapshot contract

## Status

Offline implementation and selected regression checks complete on top of
`8a67d0a90e3133cad1e8a6fde25266a422bd8022` (`v0.7.0`). These changes are not yet
committed or pushed. No portable build, live run, full matrix, or historical
authority re-evaluation was performed for this milestone.

Evidence: `artifacts/post-fallback-eof-contract-20260923/`. The previous live
campaign remains failed and its primary receipt remains `proof_incomplete`:
`artifacts/fallback-retirement-live-20260923-8a67d0a9/live/20260923T103740Z/second-transfer-after-reactivation`.
Its second transfer never started. This milestone does not claim live recovery
or identify the physical cause of the stream EOF.

## First missing edges

The current-HEAD live campaign reported
`settled_post_fallback_retry_probe_sidecar_failure_not_propagated_to_coordinator_pair_supersession`.
Focused joined-peer reproduction exposed two capture gaps:

1. `listener_terminal_tuna_stream_eof` was not admitted by the post-fallback
   precommit-failure capture path.
2. Accepting the answer clears current and retired offer bookkeeping while the
   precommit probe can still be `Started`. Failure capture depended on that
   bookkeeping and therefore lost the exact installed retry after this normal
   cleanup. Merely adding the EOF reason did not fix the reproduction.

An independent snapshot failure occurred at receiver writer
`p18908rdcd21256812b4872`, sequence `10163`, transition
`fte1-f2ca1ff2edf931459ddc19bb`, generations `414 -> 415`. The family-4 catalog
rejected a coordinator-issued quiescent successor because it had a predecessor.
The preceding accepted rejected-probe rollback had invalidated that exact pair.
This is not permission to accept arbitrary predecessor-bearing quiescent events.

## Bounded implementation

The transport now captures the existing coordinator-issued current pause
binding for `PostTunaFallbackV6ToTunaV4`. It requires the installed pause receipt,
exact retry install and source publication, accepted launch tuple/generation,
current lease and started probe, current data-session/source scope, and observed
dispatch settlement. It rejects disposed services/sessions and rechecks binding
identity under the transport lock. No offer state is retained for this purpose.
The existing service/coordinator handler still alone decides pair supersession
and applies the route-transition rollback.

The EOF reason is admitted only by this failure-capture path; listener-rearm
policy is unchanged. There is no change to throughput, queues, retry cadence,
chunk size, route publication, fallback policy, or the successful commit path.

The snapshot catalog recognizes only a single fresh `YieldIssued` successor
after an origin-proven paired rollback, with exact predecessor, next generation,
reason, retained terminal receipts, pair/owner identity, and all source scopes.
Missing history or contradictory proof remains invalid. The existing ordered
slot reducer checks stream continuity before accepting that history. Full and
compact evidence use the same validation. Peer-first successor forms are not
newly accepted by this change.

## Verification

The final per-case audit contains **46 distinct selected tests, all with a
passing latest result**. This is a union of targeted runs, not a claim that each
intermediate run passed or that a full aggregate suite ran.

| Retained run | Passed | Failed at that time |
| --- | ---: | ---: |
| `final-focused.trx` | 13 | 2 |
| `affected-final.trx` | 32 | 1 |
| `final-followups.trx` | 2 | 1 |
| `final-gui.trx` | 1 | 0 |

Coverage includes two real joined-peer EOF handoffs, seven stale/disposed input
controls, both existing joined success/repair paths, initial activation failure
handoff, directly affected coordinator/responder paths, retained analyzer
full/compact behavior, and the indexed GUI failure classifier. Each EOF positive
requires exactly one accepted handoff, the exact invalidated pair and applied
rollback receipt, the next yield generation, unchanged fallback publication,
and rejection of duplicate/stale callbacks.

Two catalog cases exercise the sidecar-failure and pressure-rejection reasons,
each with full/compact parity and 16 negative controls. These prove the bounded
production-generated responder sequence, not complete live campaign validity.

The intermediate failures identified stale isolated test fixtures, not a reason
to relax GUI acceptance. Family-only fixtures now assert their actual missing
route-publication rejection while retaining strict family/provenance and semantic
checks. The extracted GUI classifier fixture now imports its actual snapshot
and frontier helpers. The corrected cases pass; original failing TRXs remain.
Build warnings include unavailable NuGet vulnerability metadata in the restricted
environment; no live-network conclusion was drawn from that warning.

Historical artifact hashes are checked read-only. The earlier source pin for
`NknSignalingTransport.Acceleration.cs` intentionally differs in two manifests
because of this implementation; the audit distinguishes that source delta from
artifact tampering. Previous manifests and primary receipts are not rewritten.

## Next gate

Review and locally checkpoint this bounded change, rebuild the portable package,
then run strict readiness/provenance preflights and only
`second-transfer-after-reactivation` outside the restricted sandbox. A failed
preflight must stop the run. Preserve the new campaign's primary receipt and
classify its first proof edge before considering any further fix or full matrix.
No previous campaign should be re-analyzed to manufacture acceptance.
