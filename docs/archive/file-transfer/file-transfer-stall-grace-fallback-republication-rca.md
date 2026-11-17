# Stall-grace obligation across fallback republication

2026-09-20. Base HEAD `7c28d20701d3dd253d6c1c16f8f673eb3aef082a`.
Status: the receive-path review found retired checkpoint requests reopening
receiver ownership across local owner replacement. The defect is reproduced and
the bounded replay-key fix passes eight affected tests. The prior neutral
accounting/cross-peer-join hypothesis below is superseded for this live episode.
Accounting remains fail-closed; a fresh targeted live run is now needed to
validate the runtime fix. See the final replay RCA section.

## Finding

The relevant raw episode is receiver wake 137, frontier 3878, under fallback-v6
publication/runtime/leg 6 and bridge generation 2. Its timer did dispatch an
authorized request. That repair stalled; subsequent repairs did not advance the
frontier before the source owner was retired. The later frontier advance belongs
to publication/runtime/leg 7 and bridge generation 3, not the original source.

This is not evidence for a missing wake, a successful old repair, or a broken
runtime retirement path. Initially the investigation treated an eligible
disposition across fallback-to-fallback republication as the first missing
closure. The subsequent request-history review disproves that as the next fix:
the republication itself was triggered by a retired request replay. Do not add a
neutral disposition to excuse it. The existing neutral supersession contract
still covers the separately proved fallback-to-Tuna route transition only.

The completed original primary reports 36 raw / 36 distinct / 35 recovered /
0 superseded / 1 unresolved stall-grace obligations. It does not export the
selected stall source line because an earlier data-path failure occupies its
overall first-failure slot. Identification here is a raw/source-control-flow
inspection, not a newly emitted primary witness or a recalculated live verdict.
No retained analyzer, correlator or authority evaluator was replayed.

## Exact original witnesses

Source directory:
`artifacts/terminal-checkpoint-live-20260920/live/20260920T170220Z/regular-v4-live-activation-off-on-off-256mb`.
Line numbers refer to `filetransfer-retained-log-slice.log`; receiver writer is
`p14428r50cd0dfb83874cd8`.
Input SHA256: `b6fc6d07a3c93404d0208a927eaae54c4cb5dfe4e817cd5e10b7f4d5696e7c61`.

| Line / writer sequence | Evidence |
| --- | --- |
| 31172 / 16046 | Prior receiver repair progress reaches frontier 3878 under publication 6. |
| 31174 / 16049 | Exact prior repair progress state is forced. |
| 31633 / 16385 | Wake 137 starts at `17:06:14.7187365Z`, deadline `17:06:15.4687365Z`, highest received 3877. |
| 31746..31748 / 16483..16485 | Same wake emits three `rearmed / stall_grace_deadline_pending` observations. |
| 31756 / 16493 | Repair generation 79 is granted for frontier 3878. |
| 31762 / 16501 | Exact repair dispatch observed. |
| 31763 / 16502 | Exact frontier request sent for `frontier_stall_grace_elapsed`. |
| 31775 / 16514 | Wake 137 reports `dispatched / exact_receiver_request_dispatched`, authority effect none. |
| 32188 / 16815 | Repair 79 settles `SettledStalled`, dispatch observed, no successor claimed by this settlement. |
| 37929 / 20021 | Current repair generation 109 is issued under publication 6 after intervening stalled attempts. |
| 38087 / 20060 | Original validated snapshot: accepted `StartTransfer`, reason `state_refresh_received`, state generation 892 to 893. |
| 38091 / 20063 | Coordinator publication 7 is projected, still `post_tuna_fallback_v6`, protocol 6. |
| 38099 / 20072 | Current repair 109 settles `StaleRejected`, reason `runtime_owner_retired_state_refresh_received`. |
| 38102 | Service records retirement of the old runtime's workers. |
| 38152 / 20130 | A new publication-7 repair is granted. |
| 60602 / 32738 | Publication-7 repair generation 236 advances frontier 3878 to 3884. |

Repair 79 authority: `fta2-repair_replay-2506a9db27f58c5852783363`.
Request: `v6-frontier:3878:67`; correlation: `frq-cf5be00dc8f8d85e55910dc2`.
Current retired repair 109: `fta2-repair_replay-88e98ba8b238f9bc43d71652`.
Later advancing repair 236: `fta2-repair_replay-187c96156a4d66b4559ba35c`.
Do not collapse these different attempts into one successful repair or assume
repair 109 directly names repair 79 as its predecessor.

Original snapshot boundary `fte1-120f2defa3ebf6d594c83c36` withdraws publication 6
and runtime owner 6 and enters publication 7 and runtime owner 7. It also removes
the retained checkpoint acceptance. The data-session identity/generation remain
the same. This is not a `PostTunaFallbackV6ToTunaV4` commit, and preserved data
session identity does not by itself prove successor checkpoint adoption.
Aggregate boundary bridge/checkpoint slots and execution-scoped raw fields have
different roles; their zero/nonzero values are not substituted for one another.

## Why existing classification must remain unresolved

- Later publication-7 progress cannot satisfy a publication-6 repair obligation
  by range equality or successful final SHA. The legacy reducer's source-mutation
  guard prevents that borrowing.
- `Get-FileTransferStallGraceSupersessionClosure` requires a proved local
  `SourceSuperseded` transition plus the existing reciprocal peer, retry,
  checkpoint, commit, disposition and Tuna-publication chain. StartTransfer
  republication is outside that contract. Do not bypass those guards.
- The dispatched-progress diagnostic helper requires one exact wake recheck.
  This episode has three deadline-pending observations plus its dispatch. One
  deadline-pending row's log-attempt timestamp is just after the deadline; log
  append time is not an independently captured decision clock. Do not simply
  discard rearmed rows or widen a timing tolerance to manufacture proof.
- Native frontier diagnostics are additionally gated to an overall first
  `recent_progress_grace_recheck_missing` failure. Here recent-progress unresolved
  count is zero and the overall first failure is data-path mismatch. Therefore
  the absent native frontier companion is not proof of a channel/bootstrap bug.

## Implemented verification

Added `FallbackRepublication_StalledRepairRemainsSettledWhenCurrentOwnerRetires`
to `SessionFileTransferRouteOverlapRepairRetirementTests.cs`. It reuses the
existing production-scheduler fixture to dispatch A, let the production rescue
cadence settle A stalled and issue B, then invokes the service's real
`StartInboundTransferLegLocked` path with `state_refresh_received` and a new
bridge generation. The real coordinator owns publication/runtime replacement;
the service owns applying its existing retirement behavior.

Assertions cover publication/runtime/leg progression, preserved fallback route
and data session, unchanged frontier, cleared current repair ownership, A still
stalled, B stale-rejected, and rejection of late attempts to rewrite either
terminal settlement as progress. The new test passes after a fresh build.

Limits: this is a local ownership fixture, not a complete peer state-refresh
exchange, fresh checkpoint adoption, the live run's full intermediate retry
chain, a retained-analyzer eligibility proof, or a test of deadline-pending
recheck coalescing. It does not authorize a new passing disposition.

## Reboot-resume checkpoint follow-up

The saved changes, base HEAD and previous passing result survived the unexpected
reboot. The original retained input and all eleven receipt-bound output hashes
still match the completed primary receipt. No historical analysis was rerun.

Added `FallbackRepublication_PeerCheckpointBelongsToNewOwnerNotStalledRepairs` in
`SessionFileTransferFallbackRepublicationCheckpointTests.cs`. Its fresh captured
service/Coordinator flow extends the earlier local fixture:

1. The unchanged grace/rescue scheduler dispatches A, settles A stalled, issues B,
   settles B stalled, and issues C. Both predecessor links and unchanged source
   runtime ownership are asserted; the three repairs are not collapsed.
2. The sender creates and sends its real state-refresh request over connected
   loopback data queues. The receiver passes that received object to the same
   checkpoint handler used by the receive loop. That handler, not a direct test
   call to StartTransfer, causes the fallback republication and retires C.
3. The original emitted StartTransfer payload proves withdrawal/creation of
   publication and runtime-owner operations, unchanged data-session scope, and
   the exact new publication evidence reference. The new checkpoint lease names
   the new owner and the received request.
4. The real receiver-state proof traverses the opposite queue and is accepted by
   the sender's checkpoint handler. The receiver's retained checkpoint-acceptance
   DTO names the new publication/runtime/data scope. Both leases clear; the
   frontier remains 100 with durable highest 255.
5. Replaying the completed request cannot publish another owner. A and B remain
   `SettledStalled`, C remains `StaleRejected`, and late progress-settlement
   attempts cannot rewrite any outcome. No repair progress event is emitted.

Final focused lane: **1/1 PASS**, zero failures/skips, after a fresh build.
Capture: `artifacts/stall-fallback-republication-20260920/republication-checkpoint-fixture-03.log`.
SHA256: `488bfddd8b2658f8f3dd4c049553cfa71c04fc8a33eaa02d3825cdb2233861ef`.
Its A/B stalled settlements are lines 30/43; accepted StartTransfer is line 67,
boundary `fte1-3f3b95b49cfaf2371af163af`, state generation 15 to 16; C retires at
line 72; receiver/sender checkpoint acceptance is at lines 87/96. The initial
setup stream ends at line 3 before the captured fixture cursor; the two tested
streams complete at lines 105/106.

The first development run reached all ownership/checkpoint assertions but timed
out in the log-completion assertion: it compared the raw transfer ID with the
snapshot's projected ID. The test now uses the emitter's projected evidence ID
and its actual `transition_id` field. This was a harness defect, not a runtime
checkpoint failure. Final verification additionally checks the original emitted
publication/runtime operation payload, not only its decision header.

### Limits of the first checkpoint follow-up

This was a loopback service-handler fixture with seeded starting contexts, not a
GUI, encrypted transport, complete receive-pump or live-256 run. It does not yet
model a retained **old-source** checkpoint acceptance being invalidated at the
replacement boundary (present in the live episode), the full live intermediate
retry chain, or the four-observation wake's decision-clock issue.

Original emitted payloads were inspected and asserted; a retained eligibility
resolver and authoritative snapshot-validation lane have not been run on this
new capture. The in-memory object/lease assertions demonstrate the runtime
exchange, but do not substitute for a retained cross-peer fingerprint/receipt
join. In particular, a runtime-owner operation with no receipt must not be
relabeled as a validated retirement receipt; its accepted aggregate boundary and
the exact repair-owner evidence have distinct roles.

Therefore the existing fallback-to-Tuna eligibility rule remains unchanged.
A fallback-republication accounting extension is still a separate contract
decision, not an approved implementation consequence of this passing test.

## Old-checkpoint retirement and retained-validation follow-up

Extended the same fixture with a second case that completes a real peer
checkpoint exchange **before** dispatching A/B/C. The receiver's accepted origin
remains the same immutable object throughout those source repairs. The next
received state-refresh request invokes the real republication path, removes that
origin, retires C, and accepts a distinct new checkpoint under the new owner.
The original no-prior-checkpoint case remains covered. Both runtime cases pass.
No checkpoint acceptance, removal or repair settlement is directly fabricated.

The test now checks the original retirement operation's exact projected
acceptance identity, receipt identity/fingerprint and accepted generation. It
preserves the historical `Accepted` receipt while changing the origin's retained
state from `Retained` to `None`; it does not invent a cancellation of A or B.

Added `FileTransferFallbackRepublicationCheckpointEvidenceTests.cs` and
`Support/FallbackRepublicationCheckpointEvidence.ps1`. Each supported PowerShell
host independently validates full and compact forms of the same original fresh
capture, then uses the existing checkpoint and repair-owner indexes. It proves:

- The exact old checkpoint birth and retirement, followed by one distinct
  checkpoint acceptance under the successor runtime/publication/data scope.
- All original repair grant/settlement events bind to issuance receipts. The
  three selected source attempts retain exact predecessor lineage, two stalled
  outcomes and one current-owner retirement; an issuance attestation is not
  mistaken for evidence that an old repair remains current.
- Missing old birth, missing retirement, consistently substituted receipt
  identity, and duplicate retirement are rejected in both snapshot forms.
  Checksums are recomputed on mutated copies to exercise semantic checks.
- Substitution of each repair's receipt ID, runtime generation or predecessor
  cannot resolve through the original validated issuance index (nine controls
  per host). No extra snapshot evaluation is used for these field controls.

Verification:

- `republication-checkpoint-focused-04.trx`: runtime cases **2/2 PASS**.
- The combined development capture 04 passes snapshot validation with complete
  streams, 71 boundaries/transitions, 3 effects, zero legacy selections, no
  unresolved boundary and full/compact parity.
- `republication-checkpoint-evidence-06.trx`: final retained-evidence lane
  **2/2 PASS** (PowerShell 5 and 7), including the fresh runtime fixture and all
  controls above. No unrelated aggregate lane was run.
- Final original capture:
  `artifacts/stall-fallback-republication-20260920/republication-checkpoint-fixture-06.log`,
  SHA256 `78c29a2af86e168d7ae89b5df5acac994f20e38f29b484326a7b15dccd8d4eee`.
  Old receiver acceptance is line 36 / boundary
  `fte1-3b6e5d49272eaf8d42d5aa9c`; republication is line 112 /
  `fte1-49d630f25d7710ea8554bfc4`, state 21 to 22, publication/runtime 2 to 3;
  C retires at line 117. New receiver acceptance is line 131 /
  `fte1-40cbc020a93b3bba6cf3ed0c`.

### Historical retained-proof link (superseded as the live fix target)

Local origin/repair/republication linkage is now verified. **A typed cross-peer
checkpoint acceptance link is not yet proved.** In capture 06, the sender's
accepted checkpoint boundaries at lines 44 and 139 have zero operation payloads.
The receiver's accepted origin has a local receipt, but that fact does not bind
the sender's acceptance to the exact receiver-produced proof in retained
evidence. The fixture's queue-object identity assertions establish its live
in-memory exchange, not a portable peer receipt reference.

Do not infer such a reference from equal request IDs, same session/transfer,
matching frontier numbers, adjacent timestamps, transport acceptance or a final
SHA. Do not interpret zero typed operations as a failed runtime acceptance: the
service assertions and complete snapshot stream pass.

The next review must determine whether existing receive-bound frame identity and
delivery evidence can complete this join under the reviewed contract. The
current fixture dispatches directly to handlers, so absence here alone does not
justify a runtime/evidence-schema change. If the actual receive path lacks the
needed reference, specify a bounded evidence extension before implementation.
Any neutral fallback-republication accounting still needs its distinct contract
decision. The multi-recheck decision-clock and primary failure-witness visibility
issues remain separate; no archived live verdict has been recomputed.

## Receive-entry review and corrected replay RCA

The actual sparse sender dispatch entry, `ApplyOutboundV4StateAsync`, checks the
captured install/runtime-owner/data-session/generation tuple before processing
checkpoint proof. A new loopback test invokes that entry with a real staged and
committed sparse-pump installation. Four invalid continuation tuples reject
without changing checkpoint state or progress. The exact tuple applies once
and emits the expected production V4 wire fingerprint. Its sender checkpoint
acceptance still has zero typed operation payloads. This remains a fixture
evidence limitation, not justification for a new live peer-acceptance schema.

`checkpoint-receive-entry-08.trx`: **1/1 PASS**. Original capture SHA256:
`863aa9053d270618b126d64c0d969998634ce63c419ed6ef21fd20403df2592b`.
The supervisor/encrypted transport are not run by this local fixture.

### First bad ownership edge in the original live request history

Two exact request identities recur with transport epoch 1:

- A: `v6-regular-nkn-state-refresh:route-transition:2:1`.
- B: `v6-regular-nkn-state-refresh:route-transition:4:1`.

| Original retained line | Observation / decision |
| --- | --- |
| 9496, 9602, 9667 | Sender requests A; receiver and sender accept it on leg 3. |
| 9641 | Receiver already rejects an A replay on leg 3 as `retired_checkpoint_request_replay`. |
| 20166, 20179, 20262 | Sender requests B; receiver and sender accept it on leg 5. |
| 20257 | Receiver rejects a B replay on leg 5 with the same retired-request reason. |
| 25686 | Receiver receives old request A again, now while on leg 5. |
| 25694 | Accepted StartTransfer `fte1-dab63a0ca32156c8a3396d62` replaces publication/runtime/leg 5 with 6. Route remains fallback-v6; data identity/generation are unchanged. |
| 25704, 25709, 25733 | Receiver starts recovery leg 6, requests and accepts A again. |
| 25777 | Sender rejects A's response as `checkpoint_not_pending`; it has no pending checkpoint. |
| 38080, 38087 | Receiver receives old B again and the previously investigated StartTransfer replaces owner 6 with 7. |
| 38108, 38124, 38141 | Receiver starts recovery leg 7, requests and accepts B again. |
| 38164 | Sender rejects B's checkpoint proof as `checkpoint_not_pending`. |
| 38167 | Sender applies an ordinary V4-compatible receiver state; this is not a new checkpoint acceptance. |

Receiver writer is `p14428r50cd0dfb83874cd8`, sender writer
`p28564ref71516644e64ec1`. Line 38167's frame diagnostic fingerprint is
`v4sf1-C1E8A0147AB8201C.0FE4B5E560DFD32C.9D93E4C975092923.072D32947CF13250`.
That diagnostic is not promoted into peer authority.

Thus the first identified erroneous republication is **5 to 6**, before wake
137, not merely 6 to 7 afterward. The successful two-peer fixture had generated
distinct fresh requests; its valid new acceptances must not be substituted for
these live retired replays. No new sender checkpoint acceptance exists to join
at either replay. The arrival mechanism of the delayed copies is not established
by these observations and is not a reason to tune transport behavior.

### Root cause and bounded fix

`RememberRetiredFallbackCheckpointRequest` retained a key consisting of local
leg generation, local bridge generation, local live-route epoch, wire transport
epoch and request ID. `IsRetiredFallbackCheckpointRequest` reconstructed it from
the **current** local leg. Replacement therefore made the previously retired
wire request miss the lookup even though the transfer context retained the set.
`MarkInboundFallbackCheckpointRequestedLocked` then ran the real recovery-leg
start and checkpoint-begin path. Coordinator acceptance of those current-owner
actions did not establish that the peer request was new.

The fix keys this existing deny-only set by `(transportEpoch, requestId)` within
its owning session/transfer/direction context. Receiver-local generations are
not wire identity. A retired entry can only reject work; absence still requires
the unchanged coordinator StartTransfer, checkpoint-begin, lease and acceptance
checks. No cache, frame observation or delivery result acquires authority.

The context-local regular request sequence is increment-only; route-transition
IDs contain coordinator transition generation. New request IDs remain distinct.
Transport epoch stays in the key, and the set is not global or shared between
transfers. Pending duplicates retain the existing coordinator lease rather than
becoming retired. The change has no persisted-key migration: these sets are
in-memory transfer-context state.

Production changes are limited to the key/helper signatures in
`SessionFileTransferService.cs` and the two updated callers in
`SessionFileTransferService.TransportEpochV6.cs`. No snapshot schema, eligibility
rule, route protocol, queue, retry cadence or transport tuning changed.

### Regression and affected verification

`SessionFileTransferFallbackCheckpointReplayTests.cs` uses real service and
coordinator decisions after seeded initial contexts. The inbound test completes
A, optionally completes distinct B to replace the owner, starts a real repair,
and then replays A. It requires rejection, an unchanged coordinator-state
fingerprint, owner/publication/checkpoint origin, no new lease, an unsettled
current repair, unchanged frontier and no recovery-start/retirement effects.
It also verifies pending duplicate idempotence, fresh B acceptance and the same
request ID in a new transport epoch obtaining a coordinator-owned lease.

The outbound test begins and retires a checkpoint through the coordinator,
replaces the owner through the real service StartTransfer path, rejects the old
request without state change, and accepts a distinct fresh request.

- Before fix, `checkpoint-replay-09.trx`: same-owner case PASS; replacement case
  FAIL at the replay rejection assertion (actual result `true`).
- First post-fix run 10 rejected the replay but exposed a test snapshot race
  with the real repair callback. Capturing and comparing both states under the
  service lock fixes the harness without weakening the unchanged-state check.
- `checkpoint-replay-11.trx`: inbound **2/2 PASS**.
- `checkpoint-affected-12.trx`: two-peer runtime, receive-entry and PS5/PS7
  retained-evidence tests **5/5 PASS**, including full/compact validation and
  existing semantic negative controls on a fresh local capture.
- `checkpoint-outbound-13.trx`: outbound **1/1 PASS** after a fresh build.
- All eight affected checks pass, zero skips. No aggregate suite/full matrix.
  Build and `git diff --check` pass.

Captures under `artifacts/stall-fallback-republication-20260920`:

- `checkpoint-replay-11.log`: SHA256
  `d2997807b209a90c6a32a9250bb7d8e8a9da44dd8b4355a13f838263c611ae91`.
- `checkpoint-affected-12.log`: SHA256
  `f35120d0fe469a0370dddaef3ce93a8beca9e266a4db7613292910537c8e6656`.
- `checkpoint-outbound-13.log`: SHA256
  `11d7baa23789aadfde6aeab26891c2407b4fda32a90536fddcabd5ede594bd54`.

The original live input and all eleven receipt-bound outputs were rehashed and
are unchanged. No archived evaluator/correlator was rerun and no live verdict is
reclassified. SHA completion does not close these correctness findings.

## Current next sequence

1. The first replay defect is locally reproduced and fixed. Do not implement the
   superseded neutral fallback-republication accounting proposal or a new peer
   acceptance schema on its behalf.
2. Rebuild the portable package, perform strict unsandboxed live preflights, then
   run only `regular-v4-live-activation-off-on-off-256mb` with fresh artifacts.
   This is the next runtime-validation boundary; no new live run has started.
3. Require the second coordinator-published `file_tuna_v4` route plus the fresh
   correctness proofs. Inspect the first remaining proof edge from that new run
   before any further runtime fix. The old thirteen data-path findings and
   multirecheck/primary-failure-witness visibility issues remain open, not waived.

Existing dirty changes are preserved. No commit, push or package change was made
in this replay-fix slice.
