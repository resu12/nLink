# Initial activation: pre-admission receiver barrier recovery

Status: bounded implementation and deterministic verification complete,
2026-09-23; live validation remains pending. The RCA below describes the original
checkpoint; implementation and verification notes follow at the end.
Reviewed source: `c7fc9627c8e166c4af56f03c4b442a8fe715199a` (`v0.7.0`).

## Conclusion and confidence

The latest live-256 attempt failed before its first Tuna route publication. The
initial probe was sent, the Tuna stream closed without a recorded probe ACK,
and the sender's coordinator accepted rollback. The receiver's initial-activation
barrier was bound but never closed; it was invalidated only at terminalization.
Regular-v4 receiver feedback remained suppressed while data continued to arrive,
until the sender exhausted credit against an obsolete acknowledged frontier.

The first missing recovery edge is the receiver's sidecar-failure observation
reaching coordinator-owned barrier closure. The checkpoint source had two coupled gaps
for the observed **semantic ACK -> waiting launch -> EOF before peer probe** path:

1. Binding retains negotiation launch generation 0. The waiting launch changes
   the retained responder negotiation to generation 1 without refreshing the
   barrier. Failure capture requires complete negotiation-record equality and
   silently skips a mismatched barrier.
2. Even an exact observation cannot close a merely `Bound` barrier: close
   preparation requires an attached, current runtime-unlock admission. The
   waiting responder has no such admission in this capture. Moving the bind or
   merely relaxing the transport equality check would therefore be insufficient.

The ordering, bound-without-admission state, lack of closure, and continuing
feedback suppression are directly observed. The equality rejection is a
source-level explanation consistent with those observations, not a logged
branch decision: failure-origin tokens and the skipped-callback reason are not
recorded. A focused deterministic reproduction was the first implementation
gate (now completed below). Do not claim that this establishes the underlying cause of stream EOF or
excludes every other failure-capture gate.

## Evidence and preservation

Artifact root: `artifacts/phase5-live256-20260922-c7fc9627`.
Scenario directory beneath that root:
`live/20260922T202927Z/regular-v4-live-activation-off-on-off-256mb`.
Run ID: `98cf3f06-68e8-4c9e-a09d-5c990184206d`.
Original primary attempt: `d8a4b132c6314a48a5bdc91346e68ae3`.

Strict live preflights passed outside the restricted sandbox. This capture was
produced by the reviewed checkpoint and its freshly rebuilt portable package. All 85
entries in the original `FINAL-VERIFICATION.json` were rechecked unchanged during
this review. No analyzer, coordinator replay, or second authority evaluation was
run. Snapshot findings below read the original completed primary summary.

| File within the scenario directory | SHA-256 |
| --- | --- |
| `filetransfer-retained-log-slice.log` | `b88a3dbf33f798aca0d057a49762aaf71ddc3a959ddf0e371c738ec94fa45420` |
| `filetransfer-gui-log-ledger-slice.log` | `e4215980476c772e0b07927c182e328c7c09ec6d1c1805faac83a14971d0cf07` |
| `filetransfer-coordinator-snapshot-full-summary.json` | `64bf142c651ec7d238ff6e623c54008a7e13f2dc3b63c4753460274ae0affccc` |

The following are manual diagnostic witnesses, not a replacement analyzer verdict
or primary-owned failure selection. All log line numbers are **one-based**;
`evidenceLineIndex` in the original JSON is zero-based. `R` means retained slice;
`L` means full GUI ledger. Some relevant plain-text events are absent from R.
Initiator/sender writer: `p7504rc69906000e5b493b`.
Responder/receiver writer: `p33496r6552620d104d4163`.

| UTC / witness | Writer sequence | Observation |
| --- | --- | --- |
| 20:30:55, R6984 | receiver 2978 | Coordinator accepts `BindInitialActivationReceiverBarrier`, state `Bound`, pause generation 1; admission and failure-origin fingerprints `(none)`. |
| 20:30:55, R6988 | receiver 2979 | Semantic ACK retained, `AwaitingPeerProbe`, transaction 1, offer/answer-ACK generation 6, launch 0, lease 1, no peer probe/admission. |
| 20:30:55, R6992 | receiver 2982 | `ResponderWaitingForPeerProbe` launch 1, same transaction/offer/lease, `tuple_mutation=0`. |
| 20:30:55, R7038 | sender 9021 | Initial runtime-unlock Tuna probe sent. |
| 20:31:00, L12348 | sender 9162 | Listener `tuna_stream_eof`, `tuna_to_app`, stage `read`; zero forwarded frames, 545 moved bytes, zero payload bytes. These counters are not a probe acceptance receipt. |
| 20:31:00, R7331 and R7333 | sender 9169 and 9172 | Lease and initial path probe fail; reason `runtime_unlock_tuna_probe_sidecar_closed`. |
| 20:31:00, R7343 and R7349 | sender 9184 and 9190 | Rollback disposition applied and coordinator accepts probe retirement. |
| 20:31:00, L12379 | sender 9191 | `initial_activation_precommit_probe_failure_rollback_accepted`. |
| 20:31:00, L12400 | receiver 3295 | Dialer also reports read-side `tuna_stream_eof`; one forwarded frame, zero payload bytes. |
| 20:31:00, L12405 and L12406 | receiver 3300 and 3301 | `remote_closed`, then responder convergence invalidated; still launch 1, no peer receipt/admission. |
| 20:32:15, L20072 | receiver 9816 | Activation-barrier feedback suppression persists: committed frontier 3773, highest 3772, reported bytes 81,134,592, suppression count 1045. Not completion/SHA proof. |
| 20:45:10, L37233 | sender 22444 | No available credit; next-unsent/credit ceiling 3773, but remote frontier still 648. |
| 20:45:11, L37262 | sender 22459 | Canceled; 13,934,592 bytes acknowledged versus 81,134,592 normal bytes sent. These are different counters, not contradictory completion claims. |
| R21885 | receiver 14897 | Original coordinator terminalization snapshot removes the bound barrier and records `Invalidated`, not `Closed`; admission/failure fingerprints remain `(none)`. |

The original summary contains no receiver runtime-unlock admission, admission
attachment, close preparation, close commit, or barrier-close authority effect for
this attempt. Sender and receiver offer generations are local; their difference
(1 versus 6) is not itself a defect.

Both endpoints report read-side EOF. The capture does not establish which endpoint
or provider initiated closure. Raw dialer stderr is not retained by the operational
logger (`NknTunaAccelerationLane.cs` logs unparsed stderr line lengths). Do not infer
wallet, payment, network, or framing failure from absent telemetry. The later
logging mutex/backlog warning is not evidence for the earlier EOF cause.

## Source trace and coverage gap

- `NknSignalingTransport.Acceleration.cs:38297` binds the receiver barrier after
  semantic ACK, before requesting the responder-waiting launch.
- In the same file, `RequestRuntimeUnlockPreCommitProbeLaunchWithRole` writes
  `convergence.LaunchGeneration` at line 4284. The waiting role deliberately does
  not request the normal data-session handoff. There is no barrier refresh there.
- `NknSignalingTransport.InitialActivationReceiverBarrier.cs:53` captures that
  launch generation as part of the full negotiation record. Its line 165 equality
  gate skips a changed negotiation before publishing any failure observation.
- `OnAccelerationStateChanged` captures barrier failures at
  `NknSignalingTransport.Acceleration.cs:13228`, before negotiation reset. Reset
  invalidation is therefore not the initial source of this 0-to-1 mismatch.
  `remote_closed` is an accepted sidecar-drop reason.
- `SessionFileTransferService.InitialActivationReceiverBarrier.cs:7` can bind a
  new barrier, or attach an existing barrier only when admission already exists;
  it provides no pre-admission negotiation-successor path.
- `FileTransferCoordinator.InitialActivationReceiverBarrier.cs:212` requires
  exact negotiation equality, current attached admission, and `AdmissionAttached`
  or `ClosePrepared` state before preparing close. Its close command also requires
  non-null admission. This is a contract limitation, not an adapter-only typo.
- The generic transport resume helpers correctly refuse to release a
  coordinator-owned receiver barrier. Preserve that restriction.
- `NknInitialActivationReceiverBarrierTests.cs:248` explicitly requests admission
  before inducing failure and seeds initiator-style offer evidence, not the live
  responder ACK/waiting-launch sequence. Likewise the coordinator test named
  `ReceiverBarrier_PreAdmissionBindExactClosePreservesRouteAndCannotRevive` binds
  before admission but obtains admission before closing. Neither proves close
  **before admission**.
- `tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1:1293` requires admission
  fingerprints for closed/prepared states, and line 1550 only recognizes
  `AdmissionAttached -> ClosePrepared`. Runtime-only changes would leave the
  evidence contract inconsistent.

## Bounded fix plan

1. **Reproduce both gates independently before production edits.** Add a focused
   real service/transport receiver fixture with semantic ACK, launch 0-to-1, no
   peer probe/admission/Prepared route, buffered regular-v4 data, and a current
   physical failure origin. Assert one coordinator-authorized release and fresh
   state feedback. Separately exercise coordinator close from a genuinely Bound,
   never-admitted barrier; do not hide the gap by requesting test-only admission.

2. **Specify a pre-admission close contract, owned by the coordinator.** Carry the
   exact bound authority/negotiation plus the captured current waiting-launch
   witness to a coordinator decision. Permit only the observed initial-responder
   0-to-positive launch extension, preserving transaction, offer, answer-ACK,
   nonce, payer, lease/run, semantic ACK, source route/publication, physical data
   session, and pause identity. No peer receipt may be invented or removed.
   Prefer validating this bounded successor as part of close preparation so an
   extra out-of-lock refresh cannot leave an EOF race window. The adapter submits
   evidence; it must not declare negotiation compatibility or release authority.

3. **Distinguish never admitted from stale admission.** A new explicit
   pre-admission close mode must require both barrier admission and current
   coordinator admission to be absent, and the exact eligible Bound state.
   Preserve the existing admitted path's token equality. Carry that distinction
   in the coordinator-issued command and snapshot evidence; do not make all
   missing-admission closes permissive. Granting artificial probe admission on
   EOF is not the fix. A new admission, route preparation, replacement session,
   pause, or terminal transition must invalidate an in-flight pre-admission close
   before physical application/finalization, including its execution fence.

4. **Reuse exact receipt/fence/application mechanics.** Capture the failure
   before reset, validate the physical lane/client/process failure origin and
   separate transport/Core session generations, prepare through the coordinator,
   apply only to that session, and finalize its exact receipt before Core resumes
   regular-v4 feedback. Preserve the original owner/publication/leg/epoch and
   never mark Tuna active, create a route commit, schedule repair/retry, or resume
   unrelated sessions from a rejected callback. Do not invoke service handlers
   while holding the transport acceleration lock.

5. **Update evidence contracts deliberately.** The new close mode and bound/current
   negotiation lineage need explicit, versioned projection and validation, not a
   blanket removal of admission or history checks. Keep old-profile validation
   unchanged. Add synthetic valid pre-admission-close history plus forged-mode,
   missing-lineage, changed-origin, changed-source, and stale-admission negatives.
   No historical capture reanalysis or rewriting of existing receipts/catalog pins.

6. **Verify narrowly, then prepare the next live gate.** First the new focused
   regressions; then directly affected coordinator, service/Infra, execution-fence,
   receiver-barrier snapshot/catalog tests. Cover EOF before/after waiting launch,
   EOF/admission races, duplicate EOF, launch/offer replacement, route preparation,
   terminalization, replaced physical session, and another paused transfer.
   Run the relevant broader contract lane at most once after stabilization.
   Review the diff, rebuild a fresh portable package, and only then run the
   targeted live-256 scenario with strict escalated preflights under the applicable
   live-run authorization. Do not start the full GUI matrix.

Success is exactly one authorized receiver-barrier close and fresh regular-v4
feedback for the current failed initial attempt, with no route/publication change
and no stale-callback effects. This demonstrates recovery, not successful Tuna
establishment. Full scenario acceptance still requires the complete route cycle,
including a second coordinator-published `file_tuna_v4`, completion, and integrity.

## Original RCA-only step

No runtime, tests, analyzer, UI, retry cadence, throughput, queues, chunk size,
wallet, installer, or screen-sharing behavior was changed. No test/build/live run,
commit, push, or historical re-evaluation was performed during this RCA. Existing
family-level diagnostic passes do not override the attempt's FAIL/incomplete-proof
verdict. The caught-up-frontier handoff fix remains unexercised by this attempt;
fish-pulse/status behavior has no new visual verification from this capture.

## Implementation follow-up (2026-09-23)

The coordinator now has an explicit `InitialResponderBeforeProbe` close mode.
It requires a Bound, semantic-ACK-bearing initial receiver barrier, no current
or attached admission, and no previously issued runtime-unlock admission
(`LastRuntimeUnlockAdmissionGeneration == 0`). The only permitted negotiation
change is the captured zero-to-nonnegative launch generation; every other field
must remain equal. The admitted path still requires its exact current token.

Infra submits retained and observed negotiation witnesses instead of declaring
their compatibility. Core checks the retained witness, prepares the exact close,
and uses the existing physical-origin and per-session execution fence. A newly
granted admission invalidates a prepared pre-admission close before adoption;
the race test verifies that physical availability and feedback remain paused.

The optional `preAdmissionClose` snapshot payload preserves the bound negotiation
fingerprint and records the failure witness and its launch-independent fingerprint.
It is accepted only by the new profile ending `-receiver-pre-admission-v1`, hash
`17196929ff74bd92c4866ccb8da33de05eb5b22d5436f56488f3a7fd2f4d086b`.
Previous catalog versions/hashes remain unchanged. The new profile retains all
parent bootstrap, repair-owner, and activation-retry checks.

Focused coordinator and real service/transport regressions reproduced the failure
before implementation and passed after the fix. The first test harness had a
missing launch-method argument, corrected before its valid red reproduction.
Synthetic admission initially overflowed the large receiver fixture's stack,
both in the new race callback and an existing admitted-path case. All synthetic
fixture admissions now run on a separate worker stack. A later setup timeout
logged `invite_validation_throttled`: the expanded fixture reused the same fake
peer identities. Per-case unique identities removed that interference; the
complete 14-case receiver Infra lane then passed. These are fixture corrections,
not changes to runtime stack usage or invite throttling.

The retained receipts contain 173 distinct passing cases, using each case's
latest result. This is a combined receipt count, not one aggregate suite pass:
two earlier runs aborted at the fixture stack overflow, and the first final
focused run passed 31/32 with the setup timeout described above. Its affected
receiver cases were rerun after isolation. The successful checks include the
never-admitted coordinator contract, stale/admission races, existing route
preparation and execution fences, frozen catalogs/redaction, and real service
snapshot projection on Windows PowerShell 5.1 and PowerShell 7. Final diff review
and `git diff --check` found no outstanding issue in this bounded change.

Current test receipts and final status are under
`artifacts/receiver-pre-admission-20260923/RESULTS.md`. No historical capture has
been reanalyzed, and no live run, package build, commit, or push is part of this
implementation step. Initial stream-EOF causation and live acceptance remain open.
