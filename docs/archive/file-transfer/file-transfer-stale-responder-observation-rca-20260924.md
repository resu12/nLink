# Stale responder observation after completed initial Tuna handoff

## Scope and current evidence

Initial review: RCA and fix plan only. The subsequently approved offline
implementation and verification are recorded below; no live rerun.
Current HEAD: `b497f70144b0e3cb047766bd9aa5ae17c5d126e2` on `v0.7.0`.
The latest full GUI campaign is
`artifacts/full-gui-matrix-20260924-b497f701/live/20260924T144758Z`.
All 473 matrix seal pins were reverified without modifying the capture or
rerunning its analyzer. The matrix remains **FAIL**.

The selected failing scenario is
`regular-v4-live-activation-off-on-off-256mb`. Its primary first failure is
`runtime_unlock_responder_acked_probe_route_transition_handoff_missing`, reason
`data_session_availability_stale`: one raw/distinct/unresolved incident.
Snapshot evidence, full/compact parity and route consistency passed. The five
required route publications, including the second Tuna-v4 activation, occurred;
the final received payload also matched its hash. Neither fact alone discharges
a missing handoff proof.

Bounded extraction: `artifacts/acked-probe-stale-session-rca-20260924`.
`Capture-Evidence.ps1` copies 31 exact source records and eight already-evaluated
boundaries, verifies each raw snapshot payload hash, and compares the two
rejected observations described below. It calls no evaluator or reducer.
`RCA-EVIDENCE.json` SHA256:
`d0f21e31b525b9b78c013c81c1f5c35c07ec1de77f9b1a199226e31deeef0964`.

## Resolved primary witness and chronology

The compact report's `(coordinator_snapshot)` line coordinate **228 is not a
physical log line**. Its sequence 2389 resolves through the full primary report
to boundary `fte1-7db8a30c90d0effe691ec8de`, zero-based evidence index 4926,
physical retained-log line **4927**, receiver writer
`p7804rac622187559a41bc`, writer sequence **2389**. The raw payload hash is
`c3dd8a0efd3c55bc5feac3431cc20e671efd4624afe4b8e9eb8097f752407060`.

All times below are 2026-09-24, UTC. Lines refer to the scenario's
`filetransfer-retained-log-slice.log`.

| Physical line / writer sequence | Time | Coordinator evidence |
| --- | --- | --- |
| 4582 / 2272 | 15:01:51.644 | Accepted initial peer observation; aggregate state stays 18. |
| 4699 / 2300 | 15:01:51.735 | Prepared linked route transition generation 1; state 18 to 19. |
| 4759 / 2317 | 15:01:51.775 | Physical drain; state 19 to 20. |
| 4862 / 2362 | 15:01:51.837 | Committed Tuna-v4 and publication generation 2; state 20 to 21. |
| 4920 / 2382 | 15:01:51.879 | Applied exact route-transition disposition; state 21 to 22. |
| 4926 / 2388 | 15:01:51 | Service forwards the coordinator's committed publication. |
| 4927 / 2389 | 15:01:51.883 | Stale availability rejection; state remains 22 with identical before/after hash. |
| 4945 / 2402 | Later, same state 22 | Same rejected observation, reason `accepted`; primary records no incident. |

The responder lineage uses authority
`fte1-32d03363491bae211a1a63a9`, generation 1, observation command fingerprint
`fte1-babe02037a95dae6b82007bf`, admission 1, transaction 1, offer 6 and launch 1.
Preparation and drain link route authority
`fte1-0fda2bd61b51c1fb7c295163`, generation 1. The retained source is
regular-NKN-v4, runtime/publication/leg 1; the rejected observation's effective
and target scope are Tuna-v4, runtime/publication/leg 2, commit 1, live epoch 1,
with the same data session generation 1.

The later sequence-2402 operation and observation are identical to the failing
sequence-2389 operation except for `decisionReason`, typed receipt reason and
the corresponding receipt fingerprint. Both are `Rejected`, `stateApplied=false`,
at the same aggregate generation and hash. This comparison is copied from the
existing primary output, not a fresh acceptance decision over historical input.

## Root cause of the first reported incident

The service availability handler in `SessionFileTransferService.cs` rejects an
observation when its captured availability differs from the current data
session. That guard must remain. The coordinator snapshot producer explicitly
records the attempted observation with the same before/after state and a
non-applied operation. The selected capture confirms no aggregate mutation.
This is not evidence that a new runtime handoff should be dispatched.

In `tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1`,
`Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed` requires
`decisionReason == 'accepted'` before examining the exact completed history.
The legitimate `data_session_availability_stale` diagnostic therefore exits
immediately, despite matching a handoff already accepted, prepared, drained,
committed, published and disposition-applied.

The responder incident grouping consequently retains that rejected observation.
Its recovery lookup requires preparation at or after the rejected callback's
time. The matching preparation happened earlier, so it reports one unresolved
handoff. Subsequent `accepted` rejections of the same completed observation are
already handled by the consumed-observation path. The first missing edge is
therefore **exact completed-observation recognition for this stale reason**,
not absent route commit or payload corruption.

The existing report and evidence must remain unchanged. This diagnosis does not
promote the historical scenario or matrix to PASS, nor establish the cause of
every finding in that scenario.

## Bounded fix contract

1. Extend only the consumed-observation predicate for the exact new reason
   `data_session_availability_stale` in `initial_regular_v4` inbound mode.
   Keep the existing `accepted` behavior and post-fallback path unchanged.
2. Require the unchanged aggregate generation/hash, rejected non-applied attempt,
   validated self-consistent typed receipt, exact observation identity and source
   scope. Preserve exact coordinator/writer/session/transfer/direction and all
   lifecycle fingerprints/generations. Reason alone is never sufficient.
3. Retain the unique acceptance, preparation, drain, commit, publication and
   applied disposition checks, the linked route authority/generation and exact
   source/effective/target scopes, and the post-disposition aggregate interval
   check. A stale rejection before completion must stay unresolved.
4. Keep the record as a rejected diagnostic; do not synthesize acceptance,
   mutate producer evidence, suppress its log, resume a worker, grant authority,
   or introduce a runtime retry. No runtime or catalog change is proposed.

Do not replace this with a blanket stale-callback exclusion, a route-name-only
check, a latest-prior lookup, or acceptance based on transfer completion/hash.

## Implementation and verification sequence

1. Add a fresh deterministic initial-activation service fixture that completes
   the handoff and then delivers the captured observation with stale availability.
   Assert the service remains state-neutral and emits the original rejected
   reason. Reproduce the analyzer's erroneous incident before changing it.
2. Add direct reducer controls for the bounded reason: exact post-disposition
   positive; missing/duplicate acceptance, prepare, drain, commit, publication or
   disposition; earlier rejection; changed writer/authority/probe/receipt/source
   or data-session scope; state mutation/hash gap; unknown reason and other mode.
   Every incomplete or mismatched history must remain unresolved.
3. Implement the narrow predicate extension. Verify the fresh fixture through
   full, compact and shared GUI consumption on PowerShell 5.1 and 7. Reuse the
   fresh primary-evaluated boundaries for mutation controls; do not reevaluate
   historical GUI evidence or rewrite receipts.
4. Run directly affected existing tests: exact post-commit duplicate reducer,
   preparation-first responder shared evaluation, initial responder availability
   service guard, and second-Tuna rejected-observation compatibility. No unrelated
   aggregate suites are needed for this analyzer-only change.
5. Review the independent recent-progress finding before proposing the next live
   gate. Once targeted offline correctness work is ready, rebuild the portable
   package and rerun only the 256 MiB off/on/off scenario with strict elevated
   preflights. Inspect its fresh primary proof; keep another full matrix gated.

## Separate findings and limits

- Recent-progress-grace recheck: 318 raw observations, 67 distinct episodes,
  66 recovered, **one unresolved**. No exact causal link to this initial-handoff
  diagnostic has been established. Do not call it a recurrence of the earlier
  expiry-clear race or patch timer behavior without locating its own witness.
- Both switch-off fallback scenarios passed correctness but missed their
  configured performance floors. Keep the user's fallback-performance acceptance
  discussion separate; this RCA does not change thresholds or throughput policy.
- Regular-NKN timing was contaminated by external transport churn. This is not
  evidence for changing coordinator authority behavior.

During the initial review, only this document and the ignored extraction artifacts were added. No
production/test code, historical verdict, commit, push, portable rebuild or live
run was performed during this review. The next implementation is the bounded
analyzer correction above, followed by the separate recent-progress witness RCA.

## Approved offline implementation

The only production change is in
`Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed`. It adds
the exact stale-availability reason for inbound `initial_regular_v4` only,
scopes its history to the exact lifecycle identity, and requires one accepted
observation, immutable observation equality, self-consistent typed receipts,
and cleared pending-precommit state. The existing preparation, drain, commit,
publication, applied disposition and aggregate interval checks still run.
The existing `accepted` reason and post-fallback behavior are unchanged.

The new fixture uses the existing initial receiver-barrier service setup,
captures a real service acceptance while preparation is current, completes the
physical drain and disposition, then invokes the real availability handler with
the original observation and stale availability. State fingerprint, persistent
coordinator state, publication, sent frames, route hints and transition execution
are unchanged; only diagnostic convergence effects are allowed.

Artifacts: `artifacts/stale-responder-fix-20260924` (with run logs at
`artifacts/stale-responder-*.log`). The first build stopped at Avalonia's attempt
to write its telemetry log outside the sandbox. Disabling the build-only stats
target with `UsedAvaloniaProducts=` allowed the ordinary sandboxed build and
tests; no runtime setting or permission boundary was changed.

- Red: both PowerShell 5.1 and 7 fixtures failed with the expected reopened
  handoff incident; their service state-neutrality assertions passed.
- An intermediate implementation also failed because it compared acceptance
  receipt stage with operation role. The producer's exact stage is `handoff_bind`;
  the final check uses that stage and `attempted_decision` for rejection.
- Final new regression: 2 passed, 0 failed, 0 skipped. All 14 fresh boundaries
  pass on each host; full and independently selected compact results agree, and
  the real shared GUI classifier returns no failure. The original rejected
  observation is retained.
- Each host also passes 112 direct controls over copies of its fresh evaluated
  boundaries. Controls include exact completion, missing/duplicate proof steps,
  writer/session/authority/fingerprint/scope/receipt mismatches, earlier rejection,
  hash/generation mutation, wrong reason and wrong mode. These controls perform
  zero additional authority evaluations and leave primary boundary objects intact.
- Five existing compatibility cases passed on the default Debug stack: exact
  post-commit reducer, preparation-first shared evaluation on both hosts, and
  second-Tuna service evidence with/without rejected callbacks (both hosts each).
- The combined affected run then aborted before either old initial-availability
  service-guard case returned. Isolating those two with workspace-owned logging
  exposed `Stack overflow` (exit `-1073741571`) in the unchanged coordinator
  snapshot/service call chain, before analyzer invocation. Both cases pass with
  test-process-only `DOTNET_DefaultStackSize=2000000` (hexadecimal, 32 MiB).
  This is a verification qualification, not a default-stack PASS or a runtime
  fix. No production C# or old service test was modified. The initial combined
  abort and isolated crash artifacts are preserved.

Final coverage is seven passing cases on the normal stack plus two service-guard
cases on the larger diagnostic stack. The broader Debug service-guard gate is
not certified on its default stack. No commit, push, portable build, live run,
historical reclassification or throughput policy change was performed.

The independent recent-progress episode still needs its own exact-witness RCA.
Do not treat this offline correction as a live PASS or rerun the full matrix.
