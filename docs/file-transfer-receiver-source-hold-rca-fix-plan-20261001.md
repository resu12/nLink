# Receiver fallback source-hold RCA and fix plan

2026-10-01. Branch `v0.7.0`, investigated HEAD
`39748e1819ab2942e18c15401e37b2099af7dab5`.

Status: the bounded service fix is implemented and verified offline. The
implementation and retained results are recorded below. Acceptance rules,
coordinator authority, limits and the failed live artifacts are unchanged.
There is no new live acceptance result, commit or push.

## Finding

The first missing fallback edge is the receiver's source-hold capture. The
sender successfully prepared, captured and drained its hold, then issued the
correlated bootstrap exchange. The receiver accepted the initiation far enough
to prepare its hold, but immediately invalidated it with
`bootstrap_source_hold_inbound_work_unbound`. There was no receiver capture or
drain receipt, no fallback V6 publication and no second Tuna publication.

An isolated offline probe of unchanged production code reproduces a concrete
defect at that boundary: feedback remains tracked until all physical sends
settle, but its currentness check still requires its parent write operation to
remain active. The write can already have finalized after feedback delivery,
while a secondary physical copy remains pending. Valid outstanding feedback
then fails capture, and the newly prepared hold becomes invalidated. Even a
fresh same-source evaluation after physical settlement cannot recover it.

Confidence distinction: the live rejection boundary and the offline lifetime
defect are confirmed. Their exact association in the live run is probable, not
proven: the live rejection combines four predicate classes without identifying
the failing member. Do not claim a particular live write or feedback operation
was observed stale.

## Evidence and corrected diagnosis

Run: `20261001T081928Z`, scenario
`regular-v4-live-activation-off-on-off-256mb`, one attempt, strict preflight and
isolation passed. Run source commit equals the investigated HEAD. All 106
original artifact pins in the
[post-run review](<../artifacts/live-checkpoint-39748e18-20261001/20261001T081928Z/post-run-review.json>)
were rechecked: zero size or SHA-256 mismatches.

The [full coordinator snapshot summary](<../artifacts/live-checkpoint-39748e18-20261001/20261001T081928Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-coordinator-snapshot-full-summary.json>)
records the following boundaries. Line numbers refer to its `evidenceLineIndex`
in the [retained log slice](<../artifacts/live-checkpoint-39748e18-20261001/20261001T081928Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-retained-log-slice.log>).

| UTC | Side | Decision / reason | Line | Writer sequence |
| --- | --- | --- | ---: | ---: |
| 08:21:15.942 | Sender | Hold prepared | 14236 | 16583 |
| 08:21:15.962 | Sender | Hold capture applied | 14264 | 16586 |
| 08:21:15.978 | Sender | Hold drained | 14288 | 16597 |
| 08:21:16.023 | Sender | Bootstrap exchange issued | 14311 | 16606 |
| 08:21:16.310 | Receiver | Hold prepared | 14498 | 7043 |
| 08:21:16.317 | Receiver | Hold invalidated: inbound work unbound | 14502 | 7047 |

Sender writer: `p38480r0f0f1ed55aef4269`; receiver writer:
`p43128ra9fafaadd0ff45df`. The receiver's failing boundary is
`fte1-2d55ed4c193a91433f25bdfe`. Its initiation is `PeerEvidence`, source
`correlated_bootstrap_peer_frame`, against Tuna V4 publication generation 2,
runtime generation 2, leg generation 2 and live route epoch 1. Both sides had
an enabled negotiated correlation agreement. The receiver had received the
bootstrap request over Regular NKN control before preparing the hold.

This corrects two hypotheses in the historical post-run review:

- Nearby session-only trigger rejections were not proof that the exact local
  stop was missing. The sender's coordinator decisions prove it was issued.
- The absent derived-input receipt does not establish an evidence-finalization
  defect. The actual analyzer input exactly matches the ledger's input hash:
  `599f45ea880d654737cbbf93c673eb900c2a4947f50c438dc7f5f95b8a293163`.
  The manifest records `artifactFinalized=true`, passing cursor/writer integrity
  and chronology, and a valid completed retained-analysis receipt. Its
  `currentRuntimeProof=false` is expected for GUI exit 1 and an incomplete,
  non-integrity-success summary. The selected-attempt failure follows that
  negative acceptance result; it is not evidence of lost log writes. See the
  [manifest](<../artifacts/live-checkpoint-39748e18-20261001/20261001T081928Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-tuna-gui-evidence-manifest.json>)
  and [manifest predicates](<../tools/Run-FileTransferTunaGuiSmoke.ps1#L2351>).

The analyzer's later `file_tuna_data_path_mismatch` candidate at retained line
16908 concerns a Regular packet rejected before dispatch with
`authority_effect=none`. It does not supersede the earlier hold failure or
justify weakening transport ingress checks. The GUI classification-enrichment
timeout is also not a demonstrated cause of fallback failure; the main retained
analysis subsequently completed successfully. Neither limit increases nor a
new artifact/acceptance exception belong in this fix.

## Reproduced mechanism

The [offline probe](<../artifacts/receiver-source-hold-rca-20261001/probe/Program.cs>)
uses the existing committed-Tuna receiver fixture, normal chunk handling,
independently controlled delivery/physical-completion tasks, and the production
hold path. It does not modify production methods, fabricate a coordinator hold,
or perform live network I/O. Local-stop initiation exercises the shared receiver
capture path; it does not reproduce the live peer exchange end to end.

The [retained result](<../artifacts/receiver-source-hold-rca-20261001/capture/probe-results.json>)
shows:

| Case | Active writes / feedback at hold | Result |
| --- | --- | --- |
| Write feedback fully settled | 0 / 0 | Capture and receipt accepted; one phase issued |
| Write feedback delivered, physical completion pending | 0 / 1 | Feedback currentness false; hold invalidated; zero phases, including after settlement and explicit recheck |
| Standalone feedback delivered, physical completion pending | 0 / 1 | Feedback currentness true; capture accepted, receipt withheld; one phase only after physical settlement |

The probe exits successfully when these diagnostic assertions hold. This is a
confirmed reproduction of faulty behavior, not a passing regression for the
desired behavior or live acceptance. Result SHA-256:
`7050664b2911181241daeb5219f9746606c08039ea557d0ee81bb824d84cf439`.

The code sequence explains the difference:

1. [State feedback submission](<../src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV4.cs#L24810>)
   retains `WriteSource` and awaits delivery. Its finalizer starts feedback
   retirement without awaiting physical completion.
2. [Feedback retirement](<../src/nLink.Core/FileTransfer/SessionFileTransferService.BootstrapFeedbackTracking.cs#L63>)
   correctly retains that operation until physical completion. Meanwhile the
   [write finalizer](<../src/nLink.Core/FileTransfer/SessionFileTransferService.BootstrapWriteTracking.cs#L94>)
   removes the parent from `ActiveBootstrapWriteOperations` and settles it.
3. [Feedback currentness](<../src/nLink.Core/FileTransfer/SessionFileTransferService.BootstrapFeedbackTracking.cs#L42>)
   calls the write currentness check, which includes active-operation membership.
   The completed parent therefore rejects otherwise source-bound drain work.
4. [Receiver capture](<../src/nLink.Core/FileTransfer/SessionFileTransferService.BootstrapSourceHold.cs#L101>)
   prepares the coordinator hold before checking writes, feedback, queued chunks
   and pending-write ownership. Any failure invalidates the prepared hold.
5. [Coordinator preparation](<../src/nLink.Core/FileTransfer/FileTransferBootstrapSourceHold.cs#L61>)
   rejects another hold for that same source when the prior hold is invalidated.
   Physical settlement does not clear that state. The probe confirms the trap.

Existing service hold tests cover admitted feedback or writes with a hold
installed before invalidation. Their default recording session uses one task
for delivery and physical completion. The separate NKN physical-feedback tests
cover redundant-copy settlement, but do not join it to a finalized parent write
and receiver hold capture. That missing combination is the regression gap.

## Bounded fix scope

The following scope was approved for implementation and is now implemented.

1. Add deterministic regression coverage for write-generated feedback whose
   delivery and write finalization complete while physical settlement remains
   pending. Assert a valid hold captures that exact feedback, produces no drain
   receipt or bootstrap phase early, and issues once after settlement. Cover
   both local-stop and exact correlated peer-request initiation. Keep the two
   control cases above.
2. Separate drain-capture eligibility from permission to send or mutate state.
   At receiver hold capture only, allow feedback linked to an exactly settled
   parent write when its immutable source lineage and its own active operation
   remain current. Require the same context, coordinator/agreement/publication,
   session identity and generations, stream, written map, file shape and pause
   binding. Preserve the strict active-write checks for submission, progress,
   repair, terminal effects and ordinary callbacks. A settled parent is not
   authority to perform those effects.
3. Validate receiver work under the existing service lock before preparing the
   coordinator hold. A genuinely unbound write, feedback item or pending chunk
   must reject diagnostically without creating an invalidated same-source hold.
   Retain the work until real settlement; do not discard it to manufacture a
   drain. A subsequent independently valid peer request may be evaluated after
   cleanup. Stale completion itself must not dispatch a retry, resume transport
   or grant authority. Keep coordinator invalidation/conflict semantics intact.
4. Add a small rejection diagnostic identifying the failed predicate class and
   outstanding-work counts at this boundary. It is evidence only. Preserve
   existing authority decisions, catalog meaning and acceptance requirements;
   do not add an analyzer waiver.

Preferred implementation surface: the service's bootstrap write/feedback
tracking and source-hold partials, with directly corresponding tests. Do not
make the receive loop wait for every redundant copy merely to keep a parent
write alive; that would couple ordinary receive throughput to the drain fix.
Do not globally loosen `IsInboundBootstrapFeedbackCurrentLocked`, remove
`WriteSource`, reopen invalidated holds, or use adapter caches as authority.

Safety regressions must reject context/session/provider/agreement/publication,
stream/layout and pause replacements, terminal/cancelled sources, and duplicate
wakes. Delivery success or cancellation alone must never settle captured work.
Late physical success/failure may retire only the exact operation and must not
mutate successor progress, routes, retries or liveness. A hold's own captured
pause is the only permitted pause transition for its captured work.

## Verification and stop conditions

1. Run the new focused regressions first, then directly affected source-hold,
   feedback/write-tracking and stale-source tests. Run the exact peer-request
   fake-network join with independently delayed secondary feedback. No repeated
   aggregate lanes.
2. Once implementation stabilizes, run the relevant affected Core/Infra lane
   once. Check coordinator snapshot/analyzer parity for any changed diagnostic
   boundary; preserve strict acceptance. Use at most one broader contract lane
   if shared contract behavior changed.
3. Review the bounded diff, checkpoint as authorized, rebuild the portable
   package and verify its source/package pins. Then run only
   `regular-v4-live-activation-off-on-off-256mb` with strict preflights outside
   the restricted sandbox. Stop on a real preflight failure; do not bypass it.
4. Require receiver capture and actual drain, coordinator-published fallback V6
   and a second coordinator-published Tuna V4 on both sides, plus the existing
   integrity/correctness checks. SHA success or fallback completion alone does
   not pass the cycle contract. If a different first proof edge appears, retain
   that evidence and reassess before expanding the fix or running the matrix.

No new live test, aggregate suite, commit or push was performed for this RCA.
The isolated probe rebuilt its referenced projects. Its first build hit a
sandbox-only Avalonia telemetry-log permission error; rerunning with the
telemetry target disabled via `-p:UsedAvaloniaProducts=` succeeded. The retained
probe uses a workspace-local operational log, not the application's log. These
environment adjustments are diagnostic-only and do not change runtime source.

## Implementation and offline verification

The service now distinguishes feedback drain-capture eligibility from ordinary
callback currentness. Feedback from an exactly settled, retired parent write
can be captured only while the parent lineage and the feedback operation remain
current. The existing mutation/send validator still rejects that retired parent.
Physical completion remains mandatory; neither delivery nor cancellation
produces a drain receipt.

Receiver work validation now precedes coordinator hold preparation. Rejected
capture logs the individual unbound-work counts and has no authority effect.
Focused tests found one directly related caller error: a rejected capture was
still treated as a pending-write deferral. That deferral now requires a current
coordinator-accepted hold execution. Rejected work creates neither an invalidated
hold nor a retained pending-write wake that blocks the next valid peer request.

Production edits are limited to
[feedback tracking](<../src/nLink.Core/FileTransfer/SessionFileTransferService.BootstrapFeedbackTracking.cs#L50>),
[source-hold capture](<../src/nLink.Core/FileTransfer/SessionFileTransferService.BootstrapSourceHold.cs#L81>)
and the [bootstrap caller](<../src/nLink.Core/FileTransfer/SessionFileTransferService.FallbackV6ProofBootstrap.cs#L536>).
No coordinator decision, wire contract, analyzer acceptance rule, timing limit
or throughput setting changed.

The [new regression tests](<../tests/nLink.SmokeTests.Core/SessionFileTransferBootstrapReceiverHoldTests.cs>)
cover 42 cases: local and exact peer initiation, duplicate wakes, settled and
standalone controls, delayed physical success/failure, pre-capture and
post-capture source replacement, and rejection without poisoning later requests.
Two cases join the production NKN redundant-send implementation to Core's
write/feedback lifecycle over `FakeNknClient`, proving that the peer response
waits for the secondary copy to settle. Their bootstrap phase recorder observes
issuance; they are not live network or full GUI acceptance tests.

The [verification index](<../artifacts/receiver-hold-fix-20261001/verification.json>)
records source hashes, retained TRX hashes, the expected baseline failures and
the latest result for the focused selections. Across the runs, 151 distinct
cases have passing latest results and no unresolved failures. This includes
the 42 new cases, 107 affected-lane cases (two overlap the new cases), and four
existing fake-network header-stop/route-cycle cases. The affected lane includes
the source-hold full/compact GUI-evidence parity cases and production physical
feedback tracking. All 106 original live artifact pins still match.

The baseline had eight expected failures and four passing controls. Subsequent
focused runs fixed the rejection-to-deferral issue above and corrected a test
fixture that tried to set a read-only file-size property; its shape mutation
now uses the mutable chunk size. Historical failing TRX files remain intact,
with passing follow-up results in the verification index. No test threshold or
timeout was relaxed.

Next gate: review/checkpoint the bounded changes, rebuild and pin the portable
package, then run the single targeted live-256 scenario with strict preflights.
The original live predicate is still not individually observable in its old
capture; the new diagnostic distinguishes predicate classes in a fresh run.
Full GUI matrix execution remains deferred until targeted correctness passes
or advances to a new first proof edge.
