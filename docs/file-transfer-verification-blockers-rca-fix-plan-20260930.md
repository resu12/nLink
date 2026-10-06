# File transfer verification blockers RCA and fix plan

2026-09-30, updated 2026-10-01. Branch `v0.7.0`, base HEAD
`f42e2548f4c331e6f91f00e59894b3683e5e5782`.

All five original failing tests found while verifying the native sender
stream fix now pass in retained focused runs. Two exposed a local-stop
admission ordering defect. The third combined an incomplete test snapshot
history with a production receipt-adoption defect. The initial-Tuna recovery
fixture now uses negotiated preparation and correlated fallback and passes.
The joined-network route-cycle fixture and its early-proof variant now pass.
Pending-receive failure and physical-send hold coverage are restored, and the
original cycle test now delegates to the joined fixture.

The early-peer-proof ordering defect now has a bounded coordinator fix and
a versioned analyzer contract for its retained evidence. Frozen parent catalogs
remain unchanged. A deterministic joined-network test now proves early receipt,
exact checkpoint promotion, consumption of that same proof and a second Tuna
publication on both sides in its earlier capture. A receive-install gap that
lost a second-fallback checkpoint now has a narrow production guard and a
passing deterministic regression. The apparent receiver second-stop rejection
was a misleading nearby transport observation: coordinator snapshots prove the
stop was accepted. The sender then invalidated a newly prepared source hold
because a paused callback was stale, blocking the next peer wake. That capture
ordering defect now has a bounded fix and regression. Sender startup evidence
now follows each authorized, committed sparse pump installation, and receiver
evidence binds the running loop to the current session and committed transition.
The latest full joined cycle accepts both fallback checkpoints, completes
matching file bytes and passes the evidence checks for both Tuna publications
on both sides. Exact pending-proof cleanup and coordinator-owned terminal
projection retirement now pass on both endpoints, including a subsequent
Regular V4 transfer. The receive-gap regression is also corrected: it now
holds a validly issued receive before invalidating its generation, preserving
the exact stale-dispatch and no-successor-install assertions. No production
change was needed for that fixture correction. Fresh early-proof captures located
the first missing edge at receiver progress feedback after local fallback yield,
not physical-send retirement. A bounded feedback fix now passes a deterministic
regression, the synchronized early-proof join and the full offline route cycle.
The two later lifecycle failures are now corrected: initial fallback startup
uses its successor publication for diagnostics, and the Tuna unit fixture
supplies explicit readiness evidence. The separate outbound offer mismatch after
coordinator fallback now has a coordinator-owned ordering fix and 51 passing
focused and affected tests. The initial-owner analyzer now recognizes the exact
ownerless Tuna V4 to Regular V4 selection, with full/compact and cross-host
parity on the unchanged service capture. After a bounded five-capture accounting
investigation did not reproduce the intermittent failure, a controlled snapshot
delay exposed two analyzer ordering errors on one retained input. Both are now
corrected, and the targeted accounting gate passes for fresh normal and delayed
captures on both PowerShell hosts. Runtime behavior remains unchanged in this
accounting step. The deleted original input cannot establish whether these were
also its causes. Accumulated-diff review then reproduced and fixed one stale
fallback-cleanup race: data-session currentness was checked before waiting for
the projection lock. The focused, affected projection and joined-cycle checks
now pass, 107 cases total. A clean local checkpoint remains before portable
rebuild and strict live preflights.

This is offline Core, fake-network and analyzer verification, not live acceptance.
No portable package, live GUI run, commit or push was performed in this step.
The native stream fix remains intact. Route ownership, protocol requirements,
snapshot continuity, timeout limits and throughput policy are unchanged.

## Local stop rejected behind a retained peer frame

The original failing cases were
`BootstrapSourceFailure_HoldAfterEligibilityCannotTerminalize` with
`retired-handoff` and `retired-next-stop`. Both reproduced without the new native
stream installation condition in the earlier installer-baseline comparison.
The [fresh diagnostic run](<../artifacts/verification-fixture-rca-20260930/callback-red.trx>)
again failed both cases, while the two recovery-order variants passed. Its
[log](<../artifacts/verification-fixture-rca-20260930/callback-red/nlink.log>)
records `bootstrap_initiation_source_not_current`.

The first missing step was admission of a current, coordinator-bound local stop
after reactivation. `IsBootstrapCorrelationInitiationCurrentLocked` inspected
retained peer-frame custody before checking the local-stop source. An older
frame and binding therefore rejected the stop before
`TryIssueFallbackV6ProofBootstrapLockedCore` could perform its existing
source-change cleanup. The local stop and peer wake are independent initiation
sources; the old peer frame must not grant authority or veto an otherwise
current local stop.

The fix checks a local stop's existing exact source validation before the
peer-frame-only checks. It remains behind the bridge identity and correlation
capability gates. The issue path still retires obsolete peer evidence and asks
the coordinator to authorize the source hold and exchange. No peer evidence is
relabeled as local authority, and no route is published by this predicate.

Four additional sender/receiver cases cover stale peer bindings with either a
current or stale local stop. Before the fix, the two current-stop cases failed
and the two stale-stop cases passed
([red report](<../artifacts/verification-fixture-rca-20260930/local-stop-stale-peer-red.trx>)).
After the fix all four pass, together with the four original retired-callback
variants ([green report](<../artifacts/verification-fixture-rca-20260930/local-stop-stale-peer-green.trx>)).
Rejected evaluations now also assert that the retained frame and binding are
unchanged. Existing route publication, runtime owner, no-dispatch and no-pause
assertions remain in place.

## Checkpoint delivery lost its coordinator envelope

The failing test was
`OutboundSparseWorker_CoordinatorResumedCheckpointAppliesDeliveredAcrossInstallReplacement`.
Its fixture directly assigned `CoordinatorPersistentState` after several
recovery decisions. Those assignments skipped the corresponding transition
envelopes. Disposal then detected a gap in the emitted snapshot history,
masking the earlier delivery failure.

The fixture now adopts each issued, started, held, completed, resumed and
redelivery decision through the service's typed recovery-decision overload.
This preserves the coordinator-authored history and leaves the delivery,
same-command, same-binding and install-replacement assertions unchanged.

With that correction alone, the test still failed waiting for `Delivered`
([intermediate report](<../artifacts/verification-fixture-rca-20260930/checkpoint-after-envelopes.trx>)).
The [fresh log](<../artifacts/verification-fixture-rca-20260930/checkpoint-after-envelopes/nlink.log>)
shows the checkpoint queued and successor installation completed, without an
applied delivery receipt.

Inspection identified the next missing step:
`ApplyOutboundReceiveRecoveryFallbackCheckpointDeliveryReceiptLocked` passed
`decision.State` into the legacy snapshot-adoption overload. That overload calls
`AssertNoAuthoritativeFamilyOperations`, which rejects recovery-family changes
without their coordinator-authored envelope. The method now passes the complete
`FileTransferReceiveRecoveryDecision` to the existing typed overload. The
coordinator's decision and its snapshot are adopted together; the guard itself
is unchanged.

The checkpoint test passes after that production correction
([green report](<../artifacts/verification-fixture-rca-20260930/checkpoint-typed-receipt-green.trx>)).
It also passes on the default test-process stack alongside the native queued-pump
regression ([default-stack report](<../artifacts/verification-fixture-rca-20260930/native-checkpoint-default-stack.trx>)).
That result does not certify all Debug call paths against the historical stack
overflow issue.

## Original route fixture migration findings

The two tests originally requiring migration were:

- `RegularNknV4Fast_LiveTunaActivationCanThenCycleOffOnOffSameTransfer`.
- `FileTunaV4_LiveSwitchOffRecovery_DoesNotUseLiveV4RepairAndCompletesSameTransfer`.

Both originally used the base `LoopbackFileTransferTransport`. Its
`FallbackV6ProofBootstrapEvaluationRequested` event discards subscriptions,
`SupportsFallbackBootstrapCorrelation` inherits the interface default `false`,
and `SetConnectedDataSessionsUnavailableForTests` only changes session
availability. Neither original fixture supplied a current local-stop evaluation followed by
the correlated bootstrap exchange. Both then wait for fallback publication.

The route-cycle fixture additionally uses the synthetic reasons
`header_switch_off_first` and `helper_switch_off_second`. Those labels should
not be treated as real local-disable authority. Merely changing the labels or
waiting longer would not supply the missing exchange.

The [isolated pre-correction report](<../artifacts/native-queued-pump-20260930/native-queued-pump-isolated-routes.trx>)
and [log](<../artifacts/native-queued-pump-20260930/isolated-route-checks/nlink.log>)
show the fallback waits failing without bootstrap initiation records. This
establishes a missing fixture prerequisite, not proof that all later runtime
paths are correct. The native factory predicate is not used by these generic
MemoryStream fixtures. No claim is made that their complete behavior has been
baseline-certified.

A [final rerun against the corrected working tree](<../artifacts/verification-fixture-rca-20260930/unmigrated-routes.trx>)
fails at the same two waits (lines 10457 and 31434). Its fresh
[log](<../artifacts/verification-fixture-rca-20260930/unmigrated-routes/nlink.log>)
contains zero bootstrap events and zero fallback route publications. This
confirms that the two fixes above do not supply the fixtures' missing initiation.

### Bounded migration plan

1. Reuse the existing joined NKN fake-network setup for real transport bootstrap
   negotiation and local disable. Keep the source factory generic for these
   cases; native custody already has its own regression. Do not change the
   default loopback behavior for unrelated tests.
2. Preserve the route-cycle test's four transitions, two coordinator-published
   TunaV4 routes on each side, retired receive rejection, same transfer identity,
   exact output bytes and regular-route next transfer. Keep injected payload and
   feedback barriers, but do not suppress bootstrap protocol messages as though
   they were ordinary feedback.
3. Preserve the initial-Tuna recovery test's initial readiness proof, fallback
   V6 publication, checkpoint convergence, absence of V4 fallback repair, exact
   output bytes and single offer/session-open assertions.
4. Assert negotiated correlation and a dispatched current local stop before
   each fallback wait. Require actual correlated phase delivery and settlement;
   do not synthesize successful receipts or assign route state to bypass them.
5. Rerun only these two migrated cases. If either advances to another missing
   proof step, retain that failure and investigate it separately. Do not weaken
   route acceptance or substitute completion/SHA for a second Tuna publication.

### Initial Tuna recovery migration completed

The original `FileTunaV4_LiveSwitchOffRecovery_DoesNotUseLiveV4RepairAndCompletesSameTransfer`
test now delegates to the joined fixture in
[NknBootstrapCorrelationRouteRecoveryTests.cs](<../tests/nLink.SmokeTests.Core/NknBootstrapCorrelationRouteRecoveryTests.cs>).
Both real services attach before session approval. The existing preparation
handshake fake supplies physical lane evidence; the public negotiation request,
Core preparation admission and exact `header_switch_off` initiate the real
correlated exchange. No authority, route state or successful receipt is seeded.

The test retains the 768,000-byte payload, single offer/session-open/manifest,
checkpoint request and acceptance, V6 feedback, no cancel or legacy cleanup,
and exact output-byte checks. It also rejects actual V4 chunk frames after
fallback disposition. The old delayed-repair counter was never armed; the new
wire observation makes this check substantive. V6 chunk types inherit the V4
type, so the assertion explicitly excludes V6 from the V4-only rejection.

The old transport-epoch log assertion is mapped to the correlated path's
live-route epoch publication on both sides, checking the exact source route,
target route, protocol and handoff. Full bootstrap delivery, settlement,
source-hold receipt and current publication checks are additional gates.
The [final original-name run](<../artifacts/joined-route-migration-20260930/initial-default-stack.trx>)
passes with `DOTNET_DefaultStackSize` removed. Its
[log](<../artifacts/joined-route-migration-20260930/initial-default-stack/nlink.log>)
is separate from the failed development runs, which remain retained.

### Initial route cycle candidate and coverage gaps

At the initial migration boundary,
`JoinedBootstrapCorrelation_RegularTransferCyclesOffOnOff` was a failing
candidate, not a replacement or acceptance waiver. The original loopback cycle
test was unchanged. The candidate requires four route epochs, two distinct
coordinator `RouteCommitPublication` tokens for TunaV4 on each side, both real
local-stop paths, exact bytes, and the next regular-V4 transfer. Later assertions
not reached by a failing run are not claimed as verified.

The payload tail is withheld across V4 and V6 because bytes retained from the
initial V4 route can otherwise complete the transfer before reactivation.
Bootstrap and checkpoint-refresh feedback are excluded from the ordinary
feedback suppression. The candidate injects the retired owner's failure at
`FailOutboundV4Async`, using the actual old owner, session and install. This
proves rejection at that boundary but did not reproduce the old fixture's
pending receive-task failure and receive-ordinal replacement. That coverage gap
had to be closed before replacing the original cycle test. The old physical tail
hold was also represented as packet loss, not a blocked send; these were
fixture differences, not equivalent physical-drain proofs. The completed
migration and its additional coverage are recorded below.

The pre-fix [source-scope run](<../artifacts/joined-route-migration-20260930/route-cycle-source-scope.trx>)
passes the first activation, correlated fallback, checkpoint recovery and stale
owner rejection, but times out waiting for the second Tuna publication. Its
[log](<../artifacts/joined-route-migration-20260930/route-cycle-source-scope/nlink.log>)
shows inbound fallback-tail pairing committed while outbound admission remains
denied with `paired_fallback_tail_yield_missing`. This is the first missing
proof step in that run. The captured first local-stop scope matches Core,
including data-session identity and provider generation. The run does not reach
the receiver's second local stop, so it cannot diagnose that stop's scope.

Two earlier outcomes remain separate evidence, not substituted passes:

- The [checkpoint-gated run](<../artifacts/joined-route-migration-20260930/route-cycle-checkpoint.trx>)
  reaches the second Tuna publication, then rejects the receiver's local stop
  with `bootstrap_initiation_source_not_current`. The exact mismatching field
  is not yet established.
- The [retained-tail run](<../artifacts/joined-route-migration-20260930/route-cycle-retained-tail.trx>)
  reaches all four route epochs and exact-byte completion, then fails the
  retained `filetransfer_runtime_started` publication assertion. Both Tuna
  epochs have route-selected, epoch-started/recovered and outbound sparse-worker
  install records, but no matching runtime-start record. Source inspection finds
  that runtime-start emission is called at sparse-loop startup, whereas queued
  replacement installs emit the sparse-worker handoff record. This explains the
  diagnostic gap; it is not proof that the acceptance assertion may be removed.

### Early peer proof lost before checkpoint acceptance

The earlier cycle log identifies the exact mismatch. The receiver sends its
quiescent proof with checkpoint `(none)`, while the sender still owns checkpoint
`v6-regular-nkn-state-refresh:route-transition:2:1`. The sender rejects all early
copies with `fallback_tail_peer_proof_scope_mismatch` before its checkpoint is
accepted. When the sender subsequently yields, the receiver proof is no longer
available. This explains the unpaired sender without weakening the shared
route or checkpoint matching rule.

The [deterministic reproduction](<../artifacts/early-yield-checkpoint-20260930/early-proof-red.trx>)
failed in both directions for that same reason before the fix. The coordinator
now retains one exact proof in `PendingPeerProofAwaitingCheckpoint`, separate
from the peer-first yield slot. Retention cannot clear a checkpoint, issue a
yield, reserve a release or request a retry. Only acceptance of the exact local
checkpoint may promote it, after revalidating the full execution scope and
unchanged checkpoint generation. Proof identity, wire fingerprint and original
observation time survive promotion. Retirement, successor checkpoints, local
yield before acceptance and scope changes do not promote it. The shared pairing
predicate is unchanged.

Verification for this bounded change is:

- [23 final custody and scope guards](<../artifacts/early-yield-checkpoint-20260930/early-proof-final-guards.trx>)
  pass at the default stack setting, including both directions, duplicates,
  conflicts, wrong checkpoints, stale scopes and immutable snapshot boundaries.
- Five existing initial-peer-proof cases pass in the
  [focused selection](<../artifacts/early-yield-checkpoint-20260930/early-proof-snapshot-guards.trx>).
  Its other 21 cases are included in the final 23 above, not additional cases.
- [51 affected tail and checkpoint contract cases](<../artifacts/early-yield-checkpoint-20260930/affected-tail-checkpoint-diagnostic-stack.trx>)
  pass with the existing diagnostic 32 MiB stack setting. The preceding
  [default-stack attempt](<../artifacts/early-yield-checkpoint-20260930/affected-tail-checkpoint-contracts.trx>)
  aborted with stack overflow in `FallbackTailYield_TransportEpochReauthorizationPreservesExactPredecessorAcrossRestart`;
  this is not a default-stack pass or a diagnosis of that overflow's cause.

The [new joined cycle run](<../artifacts/early-yield-checkpoint-20260930/joined-cycle.trx>)
reaches Tuna epochs 1 and 3 and fallback epochs 2 and 4 on both sides, then
completes the payload byte-for-byte. It still fails at
`Missing runtime_started for Outbound Tuna epoch 1, publication 2.` Assertions
after that point, including next-transfer cleanup, were not reached. Its
[log](<../artifacts/early-yield-checkpoint-20260930/joined-cycle/nlink.log>)
does not exercise early-checkpoint custody; the proof arrives after acceptance
in this ordering. Thus it demonstrates progress through the cycle, not a
deterministic end-to-end proof of the new retention path. It ran before the final
canonical-observation-key guard; that guard has focused regression coverage.

The new snapshot role `peer_proof_awaiting_checkpoint` was not in the frozen
`snapshot-authority-v1` analyzer catalog (hash
`17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2`).
The [analyzer reproduction](<../artifacts/early-yield-evidence-20260930/analyzer-red.trx>)
rejected the production-generated retention and promotion envelopes in both
directions. The follow-up below extends the catalog rather than changing that
frozen profile. The coordinator change remains a working-tree candidate, not
a commit-ready or live-accepted fix.

### Analyzer contract for early checkpoint proof retention

The service now emits the existing initial-data-session profile with the suffix
`-early-checkpoint-proof-v1`. Its pinned hash is
`a5589d2ced4470105f2643a73921f2f9918e97804043581b9b10ef2c367c5be4`.
The parent profile hash remains
`90cf0fdafc50d49c866bbb9bdda9097b3306ae5a7fd9d1198b03c02c66ef18ab`;
the base hash above also remains unchanged. Redaction preserves only the exact
known profile, and inherited bootstrap, checkpoint, repair, retry and initial
session checks retain their existing contracts under the new profile.

The new analyzer module validates the retained descriptor and walks the same
coordinator and writer's contiguous generation, fingerprint and scope history.
It requires one original `peer_proof_observed` effect bound to the retained
receipt, followed by exact checkpoint acceptance. Promotion must preserve proof
and command identity and checkpoint generation while changing only the cleared
checkpoint scope and its derived receipt/owner/pair bindings. A subsequent local
yield must consume that promoted descriptor. Its existing local proof and paired
release requirements remain in force; retention grants neither route nor retry
authority.

Checkpoint retirement, replacement or local yield before acceptance discards
custody. Histories missing the original observation or acceptance, replaying a
discarded proof, or copying an observation onto a duplicate or promotion fail
closed. A yield that discards custody is checked as an independent unpaired
yield, not a promoted peer proof. Writer identity and sequence checks prevent
cross-writer or earlier-effect evidence from satisfying retention.

The fixtures use actual Core envelopes, including idempotent duplicates,
conflicting proofs and a rejected wrong-checkpoint acceptance. Separate tests
run the production emitter and redactor: only the original receipt emits a peer
observation. Stage callbacks in these tests are modeled; they are not evidence
of actual transport delivery or a complete route cycle. Full input, compact
snapshot records and the offline GUI failure classifier are compared on both
Windows PowerShell and PowerShell 7. Positive partial histories intentionally
omit earlier route publication and do not constitute route acceptance.

Compatibility verification also exposed two test defects. The initial fallback
service fixture sorted absent writer-sequence fields, producing an unstable
order in Windows PowerShell; it now uses retained source-line order and still
checks every generation, hash and scope link. The joined stale-request replay
used reflection with four arguments after `Deliver` gained a fifth optional
yield-evidence argument. It now supplies the default `null` explicitly. Neither
correction changes runtime admission or relaxes an assertion.

Verification for this analyzer step is:

- The [final focused selection](<../artifacts/early-yield-evidence-20260930/analyzer-final-contracts.trx>)
  passes eight tests: four retention-history cases, two production-emitter
  cases and two correlated initial-fallback service cases. Each retention case
  checks 29 inputs: five valid and 24 rejected. That is 116 fixture checks
  across both directions and both PowerShell hosts, each with full/compact/GUI
  classifier parity. This report also contains the separate accounting failure
  below; it is not an all-green report.
- The [profile and redaction selection](<../artifacts/early-yield-evidence-20260930/profile-and-history.trx>)
  passes 52 catalog/redaction tests. Its two development fixture failures were
  corrected before the final focused selection; they are not concealed or
  counted as additional passing tests.
- The recent-progress supersession case passes in the
  [compatibility selection](<../artifacts/early-yield-evidence-20260930/analyzer-crosshost-inheritance.trx>).
  The other failures in that report led to the two fixture corrections above.
- With the reflection call corrected, the
  [isolated joined stale-request scenario](<../artifacts/early-yield-evidence-20260930/superseded-source-after-replay.trx>)
  passes. Its [retained capture](<../artifacts/early-yield-evidence-20260930/superseded-source-after-replay/joined.log>)
  also passes the accounting script and its negative controls.

The final selection's accounting failure is
`local_source_supersession_unproven`. The child network fixture had passed, and
the snapshot itself passed analysis; the accounting consumer could not prove
the local old-source retirement. That failing capture was deleted by the
fixture's original cleanup, so its exact missing link cannot be reconstructed.
The fixture now retains its input on any future failure. A single
[scoped rerun](<../artifacts/early-yield-evidence-20260930/superseded-source-retained.trx>)
passes the accounting checks and reports on both hosts. This does not establish
a fix for the intermittent failure. No runtime code or acceptance condition was
changed in response to it.

These runs use the default test-process stack. No full contract suite or live
GUI matrix was run. The [analyzer review receipt](<../artifacts/early-yield-evidence-20260930/implementation-review.json>)
pins the source and retained verification artifacts at that earlier step.

### Deterministic joined early proof verification

`JoinedBootstrapCorrelation_EarlyCheckpointProofPromotesThroughReactivation`
now passes with actual service and transport decisions on the fake NKN network
([final report](<../artifacts/early-yield-joined-20260930/final-ordering.trx>)).
The test establishes a real contiguous prefix, then withholds encrypted
receiver-state packets while allowing the receiver's genuine yield proof to
arrive. Before releasing the packets, it requires a current checkpoint lease
and retained proof, with no local yield, peer-first promotion, reservation or
retry recheck. The retained fingerprint must match an actual captured wire
frame. No proof, route or successful receipt is assigned to the coordinator.

The unchanged encrypted packets are then delivered through the fake client's
normal receive boundary. The fixture allows one real fallback recovery batch
and continues to withhold the final batch. Production snapshots must show
retention, exact acceptance and consumption in writer order, with the same
proof identity and fingerprint and exactly one original peer-observation
effect. Both services must publish a current coordinator-authorized TunaV4
route at epoch 3. This is a focused ordering test: it stops there and does not
claim second-stop, completion, runtime-start or next-transfer coverage.

Two fixture prerequisites mattered. With checkpoint feedback withheld while
chunk zero was still missing, the receiver could not legitimately yield
([first report](<../artifacts/early-yield-joined-20260930/checkpoint-barrier.trx>)).
With a contiguous prefix but every remaining fallback chunk withheld, sender
frontier recovery could not settle
([prefix report](<../artifacts/early-yield-joined-20260930/ordered-prefix.trx>)).
A provisional checkpoint-continuation change remained blocked by that owner
work and was removed. Allowing the required recovery batch passes with the
production code unchanged from the previous step
([recovery report](<../artifacts/early-yield-joined-20260930/ordered-recovery.trx>)).
Neither development failure establishes a missing production yield recheck.

Windows PowerShell 5.1 and PowerShell 7 analyze the final capture identically:
retention, checkpoint promotion and local-yield consumption boundaries pass,
the stream is complete and full/compact parity passes
([Windows PowerShell report](<../artifacts/early-yield-joined-20260930/analyzer-powershell.json>),
[PowerShell 7 report](<../artifacts/early-yield-joined-20260930/analyzer-pwsh.json>)).
This is not an all-clear analyzer result. After the second Tuna publication,
the deliberately incomplete fixture leaves an unresolved inbound frontier
pressure lifecycle boundary at source line 2893, classified as
`regular_v4_frontier_repair_pressure_authority_lifecycle_split`. The earlier
recovery capture also reported `snapshot_bootstrap_callback_consumption_missing`;
the later capture's artifact-level pass does not diagnose or waive that result.

### Second-fallback checkpoint receive-install gap

The [shared-fixture regression report](<../artifacts/early-yield-joined-20260930/shared-route-regression.trx>)
passes initial-Tuna switch-off recovery but fails the full joined cycle at
same-transfer completion. Both Tuna publications and both fallback publications
were reached. The original missing-frontier and tail barriers and all later
full-cycle assertions remain unchanged; the early-proof variant did not replace
that gate.

In the [fresh log](<../artifacts/early-yield-joined-20260930/shared-route-regression/nlink.log>),
the receiver accepts checkpoint
`v6-regular-nkn-state-refresh:route-transition:4:1` at line 4411. There is no
corresponding outbound checkpoint acceptance. At line 4430 a receive result
has current fallback epoch 4/runtime generation 5 but no sparse-pump install,
and is rejected with `receive_install_missing`. Subsequent frontier requests
are rejected with `checkpoint_pending_frontier_request` (first at line 4614).
The transfer remains at 69 of 74 committed chunks. Earlier stale-owner
rejections are not themselves defects.

The receive supervisor prefetched a new read before checking the completed
frame. Between route installations, neither the old installed descriptor nor
a pending successor was current. Nevertheless, the prefetch started with a
null install and the current runtime owner. The successor was subsequently
installed and dispatched its checkpoint request, but the already-issued read
consumed the response without that installation binding. The strict receive
check correctly rejected it. Dispatching the request after installation was
not sufficient to bind an earlier receive task.

`StartSessionGenerationReceiveIfNeeded` now waits for a current pending or
installed descriptor before issuing a read. Pending descriptors remain eligible,
so source reopening does not prevent transport draining. Dispatch still checks
the exact current tuple. No stale receiver state is rebound, checkpoint bypassed,
route authority added, or timeout extended.

The new unit regression seeds a fallback scope and replaces its publication to
force the installation gap; it is not an end-to-end authority test. Production
staging, checkpoint issuance and acceptance paths are then used. Barriers hold
an old frame and source reopening, prove that the stale frame does not advance
progress, and deliver one exact checkpoint response after the successor is
installed. Before the guard, the response was rejected with
`receive_install_missing` and frontier stayed at zero
([red report](<../artifacts/sparse-receive-install-gap-20260930/red-capture.trx>),
[red log](<../artifacts/sparse-receive-install-gap-20260930/red-capture/install-gap.log>)).
With the guard, the regression passes: the checkpoint lease is cleared, frontier
advances to two, publication is unchanged, stale-frame rejection remains, and
no missing-install rejection occurs
([focused report](<../artifacts/sparse-receive-install-gap-20260930/focused-final.trx>)).

The [affected receive/handoff selection](<../artifacts/sparse-receive-install-gap-20260930/affected-receive-handoff.trx>)
passes all 33 cases, excluding the new regression already run. This includes
pending-install draining while source reopening is blocked, stale callback
rejection, frontier-request rebinding, data-session replacement and checkpoint
delivery across install replacement. These runs use the default process stack.
The intermediate report named `green.trx` is a failure, not green evidence:
its log shows successful checkpoint acceptance, but synchronous log reads in
the wait predicate exhausted the polling window. Removing that diagnostic I/O
from the predicate, without changing the success condition or timeout, produced
the focused pass. Both reports and logs are retained.

### Earlier joined-route verification remained blocked

The [new joined selection](<../artifacts/sparse-receive-install-gap-20260930/joined-routes.trx>)
passes initial-Tuna switch-off recovery and fails both cycle variants. It does
not validate second-fallback completion with the new guard:

- The full cycle reaches the second Tuna publication, then times out waiting
  for the second correlated fallback. At line 4836 of the
  [retained log](<../artifacts/sparse-receive-install-gap-20260930/joined-routes/nlink.log>)
  a separate transport observation is rejected as
  `bootstrap_initiation_source_not_current`. The earlier RCA incorrectly
  attributed that line to the local stop. The coordinator snapshots instead
  record inbound source-hold preparation, capture, drain and exchange issuance
  at lines 4856, 4859, 4866 and 4867. The sender capture failure below is the
  first missing edge; the receiver's exact source checks need no relaxation.
- The early-proof variant retains peer evidence, promotes it at line 7055 and
  accepts the outbound checkpoint at line 7061. Its subsequent local-yield
  recheck is held for `frontier_repair` at line 7107. It times out before the
  second Tuna publication, with sender frontier 69 and receiver frontier 72.
  The earlier focused pass therefore does not establish stable end-to-end
  coverage. The missing frontier completion/recheck must be traced before
  another runtime change; this run alone does not attribute the stall to the
  receive guard.

There are no `receive_install_missing` rejections in this joined capture, but
neither failing variant reaches the former second-fallback checkpoint edge.
The runtime-start, completion and next-transfer assertions remain unchanged
and unexercised in these failures. Do not replace them with the focused pass.

The [receive-gap review receipt](<../artifacts/sparse-receive-install-gap-20260930/implementation-review.json>)
pins that source state and retained artifacts. That step changes only receive
issuance, adds its regression and updates this RCA. No catalog, route contract,
performance limit, portable build or live run was changed.

### Sender pause invalidated a hold before callback capture

In the retained full-cycle log, the sender enters transport pause at line 4955.
Its first new source-hold preparation succeeds at line 4963, but capture rejects
an outstanding callback as `bootstrap_source_hold_outbound_callback_unbound`
and invalidates the hold at line 4964. The callback is consumed at line 4965.
A subsequent peer wake reaches the coordinator at line 4969 but receives
`bootstrap_source_hold_conflict`: an invalidated hold for that same source is
already recorded. The receiver's phase delivery receipts at lines 4957 and
4962 further rule out the earlier alleged local-stop rejection.

`TryEnsureBootstrapSourceHoldLocked` prepared a coordinator hold before checking
whether pending sender callbacks still matched the session, provider, pause
instance and source scope. Transport pausing can correctly make a callback
stale before its completion is consumed. Rejecting that callback is required;
recording and invalidating a new hold first needlessly prevents the next valid
peer wake from preparing one after the callback is gone.

The fix moves only the existing outbound-callback checks before hold
preparation, under the same service gate. An unbound callback now produces a
diagnostic rejection without changing coordinator state. A later peer delivery
must still pass the existing custody and source checks and obtain a fresh
coordinator command, capture and drain receipt. This does not revive an
invalidated same-source hold, reclassify a stale callback, trigger a retry from
callback completion, or grant runtime or route authority.

The new barrier regression seeds a correlated Tuna source, admits a real sender
callback, pauses before its completion is consumed, and supplies an exact peer
proof. It then consumes the stale callback and delivers the same proof again.
Before the fix, the second delivery cannot start the exchange
([red report](<../artifacts/bootstrap-pause-capture-20260930/red-final.trx>),
[red trace](<../artifacts/bootstrap-pause-capture-20260930/red-final/pause-capture.log>)).
After the ordering change it passes
([focused report](<../artifacts/bootstrap-pause-capture-20260930/focused.trx>)).
The test also requires unchanged route/runtime tokens, no progress credit from
the stale completion, and a `Stale`/`none` callback-consumption record. The final
version additionally checks that the first peer wake leaves coordinator state
unchanged. Two earlier fixture-development failures occurred before callback
admission (unconnected loopback and an undersized default source); they are
retained but are not evidence of the production defect.

The [affected source-hold selection](<../artifacts/bootstrap-pause-capture-20260930/affected.trx>)
passes 97 of 98 cases on the default stack, including that strengthened
regression. The sole failure is the existing `receive-gap` callback-handoff
variant, which waits for `frontier_receive_authority_gap` after replacing the
data-session generation. A bounded comparison restored only the source-hold
file to its pre-change version, retaining the prior receive-install guard and
all other working-tree changes. It fails at the same assertion
([comparison report](<../artifacts/bootstrap-pause-capture-20260930/receive-gap-baseline.trx>)).
This establishes that the failure predates the capture-ordering fix, not that
it can be waived. The receive/install issuance contract and this regression's
expected observation need review. No assertion was removed or timeout changed;
the source-hold fix was restored before the subsequent rebuild.

### Full joined cycle now reaches runtime-start evidence

The [fresh single-cycle report](<../artifacts/bootstrap-pause-capture-20260930/joined-cycle.trx>)
reaches both Tuna publications and both correlated fallbacks. Its unchanged
completion, byte-equality, single-offer/open/manifest and no-cancel checks pass.
In its [log](<../artifacts/bootstrap-pause-capture-20260930/joined-cycle/nlink.log>),
the second fallback checkpoint is accepted inbound at line 3273 and outbound
at line 3312; both sides complete at lines 3567 and 3584. This run has neither
missing-install rejection nor callback-unbound rejection, so the barrier
regression, not this timing-dependent run, proves the repaired capture race.

That cycle **failed** at `Missing runtime_started for Outbound Tuna epoch 1,
publication 2`. Outbound installation events exist for epoch 1/publication 2 at
line 1018 and epoch 3/publication 4 at line 2932. They are not substitutes for
the required runtime-start evidence. The later next-transfer assertions are
not reached. This established the actual authorized installation to
runtime-start evidence boundary as the next bounded diagnosis.

The [pause-capture review receipt](<../artifacts/bootstrap-pause-capture-20260930/implementation-review.json>)
supersedes the earlier local-stop attribution and pins the then-current sources and
retained reports. No portable build, live run, full matrix, commit or push was
performed.

### Sender runtime evidence follows committed pump startup

The sender supervisor survives route changes, but its `runtime_started` call
ran only during initial startup. Later sparse pump installations therefore
had installation evidence without a corresponding runtime-start record.
The [red regression](<../artifacts/sparse-runtime-start-20260930/red.trx>)
reproduces this directly: the first committed Tuna pump produces zero records
where one is required. Six other initial cases pass before the change.

`RunOutboundV4SenderPumpAsync` now emits through the existing authorized
publisher on entry, only for a non-canceled, exact committed installation
whose coordinator tuple and data session are current. A diagnostic-only
publication token suppresses duplicate sender records. Authorization is checked
before deduplication, so a previously logged but now-stale token is still
rejected. The supervisor's existing startup validation remains in place.
No route, retry, progress, callback or runtime-ownership decision changes.

The [focused report](<../artifacts/sparse-runtime-start-20260930/focused.trx>)
passes the initial seven cases. The final regression expands to ten cases,
covering Tuna/fallback/Tuna publications, duplicate startup, pending and unbound
installs, cancellation, stale publication/owner/leg/session generation and
terminal context. All ten pass within the
[affected selection](<../artifacts/sparse-runtime-start-20260930/affected.trx>),
which totals 35 passes and two failures. The unit fixture seeds publication
tuples; production coordinator publication is exercised by the joined cycle.

Both affected-selection failures reproduce with only this turn's diagnostic
change removed, while retaining all preceding working-tree changes
([baseline report](<../artifacts/sparse-runtime-start-20260930/lifecycle-baseline.trx>)).
The baseline has four passes and the same two failures. Its
[log](<../artifacts/sparse-runtime-start-20260930/lifecycle-baseline/nlink.log>)
shows initial fallback sender startup rejected for
`route_publication_generation_mismatch` at line 42. The synthetic active-Tuna
fixture is rejected for `initial_tuna_readiness_missing` at line 178 and
`offer_route_not_active` at line 179, before its pending-decision wait can pass.
These failures predate this change; neither is waived. The fix was restored
before rebuilding the joined-cycle test.

The [fresh joined cycle](<../artifacts/sparse-runtime-start-20260930/joined-cycle.trx>)
passes all four required evidence checks for each outbound Tuna publication.
Its [log](<../artifacts/sparse-runtime-start-20260930/joined-cycle/nlink.log>)
records the epoch 1/publication 2 installation at line 1011 and runtime start
at line 1012; epoch 3/publication 4 follows at lines 2541 and 2542. The second
fallback checkpoint is accepted inbound at line 2984 and outbound at line 2994.
Completed terminal epochs appear at lines 5108 and 5125. The unchanged byte
equality, completion, single-offer/open/manifest and no-cancel checks pass.

That test remained **failed** at `Missing runtime_started for Inbound Tuna epoch 1,
publication 2`. The inbound sparse receive loop reported startup only once.
This established the receiver's route-adoption boundary as the next review,
without inferring runtime authority from an arriving frame or transport
observation. The later next-transfer assertions were not reached.

The [sender evidence review receipt](<../artifacts/sparse-runtime-start-20260930/implementation-review.json>)
pins those sources and retained evidence. All runs used the default stack.
No acceptance assertions, timeout limits, analyzer catalogs or live behavior
were relaxed, and no portable build, live run, full matrix, commit or push ran.

### Receiver runtime evidence follows committed route adoption

The sparse receiver can retain one pending transport read across a route
transition. Its loop must not need a newly received packet, or a second reader,
to acknowledge that it is running on a newly committed route. Conversely, a
published route alone does not establish that a receiver loop exists.

The loop now records diagnostic execution evidence for its exact data session,
service generation and optional provider generation, and clears its own evidence
on exit. Initial startup emits from that binding. A coordinator-accepted,
current route-transition disposition can emit startup evidence for the surviving
loop only while that binding is still current. The existing publication
validator runs before per-publication deduplication. These fields are not read
by receive dispatch, route selection, progress, retries or callback authority.

The [focused receiver report](<../artifacts/sparse-receiver-runtime-start-20260930/focused.trx>)
passes all 12 cases. The tests exercise the real receive loop with one pending
read and seeded publication tuples. They cover repeated publications, duplicate
emission, never-started and exited loops, replaced context/session/generations,
terminal/canceled contexts and missing or stale publication/owner/leg authority.
Emission leaves coordinator state unchanged and sends no frames.

The [affected report](<../artifacts/sparse-receiver-runtime-start-20260930/affected.trx>)
has 34 passes and the same two lifecycle failures established by the previous
baseline comparison. All 24 write-tracking cases and all six second-Tuna
transition controls pass; the lifecycle lane has four passes and two failures
at the same assertions. The prior failures are not waived or relabeled as
success, and the aggregate suites were not rerun.

The [fresh joined-cycle report](<../artifacts/sparse-receiver-runtime-start-20260930/joined-cycle.trx>)
now passes all four per-publication evidence requirements for both Tuna
publications on both sides. Its
[log](<../artifacts/sparse-receiver-runtime-start-20260930/joined-cycle/nlink.log>)
has inbound startup at lines 1017 and 3130 for epochs 1 and 3, publications 2
and 4; outbound startup is at lines 1009 and 3146. The second fallback checkpoint
is accepted inbound at line 3573 and outbound at line 3599. Completed terminal
epochs appear inbound at line 5674 and outbound at line 5689. The unchanged
byte-equality, single-offer/open/manifest, no-cancel and stale-sender rejection
assertions all pass before the new first failure.

The cycle remains **failed** at line 285 of
`NknBootstrapCorrelationRouteRecoveryTests.cs`: the host's
`IsPostTunaFileFallbackActiveForRouteSelection` is still true. The helper's
equivalent assertion and the next-transfer checks have not run. That property
reads the session's non-None file fallback lane with no projection-retirement
command; transfer completion alone does not establish that this projection was
retired. The existing retirement path is bound to a coordinator-committed
reactivation. The next investigation must distinguish a missing terminal
retirement contract or continuation from an incorrect immediate test expectation.
No adapter flag, cleanup policy or acceptance assertion was changed here.

The [receiver implementation receipt](<../artifacts/sparse-receiver-runtime-start-20260930/implementation-review.json>)
pins the sources and reports. Verification used the default stack and offline
fixtures only. No portable build, live run, full matrix, commit or push ran.

### Fallback cleanup compares different generation domains

The terminal review found two distinct missing effects. The first was before
terminalization: the second committed fallback never clears the adapter's
pending proof. The second is after terminalization: no coordinator-issued
terminal disposition retires that transfer's session file projection. Clearing
the projection in a generic completion callback would hide the first defect
and give an observation authority it does not have.

The fresh [counter diagnostic report](<../artifacts/fallback-terminal-projection-20260930/joined-counters.trx>)
retains the unchanged host failure, now at test line 290. Its failure message
captures both endpoints before the assertion. Both coordinators are terminal
at generation 1 with no current publication. Both adapter projections still
reference the completed transfer's fallback publication 5, live epoch 4,
runtime generation 5 and leg 5. Both are unretired, with file state `Pending`,
fallback epoch 2, transport epoch 1 and pending proof generation 3. The actual
transport inputs resolve to a next-route candidate of `post_tuna_fallback_v6`.
This is a resolver result, not a new admitted or completed transfer: the
next-transfer portion remains unreached.

In the [same capture](<../artifacts/fallback-terminal-projection-20260930/joined-counters/nlink.log>),
the first fallback commits and clears proof generation 1 at lines 1652–1676.
The second fallback commits at lines 3426/3429 outbound and 3498/3501 inbound,
with peer proof generation 2 and transport epoch 1. It has no matching second
cleanup event. Completed terminal events follow at lines 5437 and 5455.
Both Tuna publications' four evidence checks, exact payload equality,
single offer/open/manifest, no-cancel and stale-sender rejection still pass
before the host projection assertion.

The source explains the counter divergence. `MarkFileTransferFallbackNknProofPending`
increments the adapter counter, and `CompleteFileTransferFallbackProofUnsafe`
increments it again when clearing the first episode: 1, then 2, then 3 on the
second stop. Correlated peer proof uses the coordinator's
`BootstrapGeneration`, which advances once per bootstrap: 1, then 2.
`CompleteCommittedPostTunaFallbackFileTransition` compared those numbers for
equality and returned before cleanup on the second episode. Equal values on
the first episode concealed the domain mismatch. Do not remove this guard,
renumber either counter or infer a mapping such as `2 * generation - 1`.

Terminalization is a separate lifecycle gap. `FileTransferCoordinator.Terminalize`
invalidates transfer ownership and removes the publication, but produces no
exact terminal command for this adapter projection. `ObserveFileTransferTerminal`
settles retry-dispatch state; `ObserveFileTransferRouteCompleted` marks transport
completion and intentionally does not clear fallback state. The existing
projection retirement command authorizes only committed fallback-to-Tuna
reactivation. `TerminalInvalidated` ends an in-flight transition and is not a
substitute for retiring a completed fallback publication. Even corrected proof
cleanup would leave the file lane `Recovered`, which still counts as an active
fallback projection without retirement.

The [focused controls](<../artifacts/fallback-terminal-projection-20260930/focused.trx>)
pass 4/4. Two new direction-specific cases prove that matching and wrong-transfer
terminal/completion observations cannot retire a committed projection or mutate
its publication, epoch or pending-proof identity. The existing unscoped pending
proof control and transport-busy cleanup control also pass. These seeded
transition fixtures verify negative authority boundaries, not successful terminal
retirement. The joined fixture supplies the real Service/Coordinator lifecycle
evidence. An earlier [diagnostic capture](<../artifacts/fallback-terminal-projection-20260930/joined-cycle.trx>)
failed at the same assertion before the counter fields were added; both failures
are retained, not counted as passes.

### Bounded fix order for fallback retirement

1. Bind pending-proof cleanup to the exact episode. Preserve the coordinator
   bootstrap generation as peer-proof identity. Carry the separately scoped
   local pending-proof observation identity through coordinator validation to
   the committed fallback disposition. Bind session, transfer, direction,
   source publication/runtime/leg, local fallback episode and pending generation;
   retain target transport epoch and exact peer-frame proof. The adapter may
   capture evidence but only the coordinator-issued disposition may retire it.
   Peer-first admission must not substitute its synthetic wake counter for the
   adapter's pending generation. Missing or replaced binding remains fail-closed.
2. Add red/green controls for distinct counter values on repeated cycles in both
   directions, plus the first-episode case. Require exactly one cleanup for the
   current committed episode. Reject missing or mismatched bindings, wrong
   session/transfer/direction/publication/epoch, rejected dispositions, duplicate
   callbacks, a newer stop and a successor episode. Preserve unrelated screen
   state, route ownership and current successor work. Rerun the joined cycle
   once to prove second-fallback cleanup; its terminal assertion may still fail.
3. Separately add a coordinator-issued terminal retirement command from accepted
   terminalization, capturing the exact retiring fallback publication before
   it is cleared and the accepted terminal generation. Apply it only to the
   matching current projection and retain an idempotent tombstone. Retire only
   that file projection: do not clear the whole session proof, screen state,
   stop flags, newer episodes or successor control-plane owners, and do not
   grant Tuna readiness. Rejected or stale terminal commands have no effects.
   Order application so a reentrant next-transfer request cannot consume the
   retired projection; use existing execution ordering where possible and do
   not call back into Core under adapter locks.
4. Verify terminal retirement in both directions, including duplicate, delayed,
   wrong-scope and successor races. Keep the generic-observation negative tests.
   Then require the unchanged joined-cycle assertions and the actual next
   transfer's Regular V4 offer, publication, completion and exact bytes. A
   resolver candidate alone is insufficient. Run only directly affected lanes
   before considering a fresh targeted live run; no full matrix is authorized
   by this diagnostic result.

That diagnostic step changed tests and this RCA only. No production cleanup, terminal
authority, acceptance condition, timeout, protocol or analyzer catalog changed.
The [diagnostic review receipt](<../artifacts/fallback-terminal-projection-20260930/implementation-review.json>)
pins that source boundary, prior receipt and retained reports. Its historical
failures remain unchanged. The following implementation covers the cleanup
portion of the plan only.

### Exact pending episode cleanup implemented

The implementation reuses the existing coordinator-issued transition and its
adapter pause binding. The binding now captures the local pending generation
and fallback epoch alongside the exact transition authority and projection
reference. This is execution evidence, not a new source of route authority.
No wire field, persistent coordinator snapshot or synthetic peer-wake counter
is needed to identify the adapter's pending episode.

The Core cleanup validator requires the exact matching transition, a valid
committed fallback V6 disposition, its scoped peer proof and a consistent
successor publication. Only application from the current pause with no retained
predecessor can carry the captured binding to cleanup. Before effects, the
adapter rechecks the applied disposition receipt, absence of a successor pause,
current data session, projection object/epoch/publication and local pending
generation and scope. Missing, stale or replaced bindings do not clear work.
The file lane becomes recovered under the same locks as proof cleanup, so an
old callback cannot recover a replacement projection between those two writes.
Notifications occur after those locks are released. A valid new fallback may
reuse a retired projection through the existing committed-reopen path.

The two counters retain their original meanings and increments. Cleanup logs
preserve `proof_generation` for the peer proof and add separate
`pending_proof_generation` and `fallback_epoch` evidence. Cleanup does not
retire the session projection, grant Tuna readiness, clear screen state or
change route publication, progress, retry policy, protocols or timeout limits.

The [red regression](<../artifacts/fallback-proof-episode-20260930/red.trx>)
had two first-episode passes and two second-episode failures before the runtime
change. The [initial green run](<../artifacts/fallback-proof-episode-20260930/green.trx>)
passed all four. The final focused suite contains 25 cases: both directions and
episodes, duplicate cleanup and a newer stop, missing pending work/binding,
replaced scope/projection/session, terminal-first and rollback/supersession,
and Core command/publication validation. It also preserves screen state and
counters, and proves cleanup is not terminal retirement.

The [affected transition and projection lane](<../artifacts/fallback-proof-episode-20260930/affected.trx>)
passed 61 cases. After the applied-receipt recheck and committed-reopen review,
the [final focused report](<../artifacts/fallback-proof-episode-20260930/focused-final.trx>)
passed the 25 episode cases plus four directly affected supersession/reopen
controls, 29 total. No unrelated aggregate lane was rerun.

The [final joined report](<../artifacts/fallback-proof-episode-20260930/joined-verified.trx>)
retains the host terminal-projection failure at test line 299. The added
assertions require exactly one cleanup event for each of transitions 2 and 4
in each direction, and no pending proof on either endpoint; all pass before
that failure. Both endpoint diagnostics now show `ProofPending=false`,
`ProofGeneration=4` and file state `Recovered`. Both still retain fallback
publication 5 and resolve a next-route candidate of fallback V6. All preceding
Tuna publication/runtime checks, byte equality, single offer/open/manifest,
no-cancel and stale-sender rejection pass. Actual next-transfer assertions
remain unreached. Earlier joined reports in the same artifact directory are
retained as intermediate failures, not full acceptance passes.

The [current implementation receipt](<../artifacts/fallback-proof-episode-20260930/implementation-review.json>)
pins the exact source and report boundary. All runs use the default stack and
offline fixtures. Frozen analyzer catalogs, acceptance requirements and the
two existing lifecycle failures remain unchanged. No portable build, live run,
full matrix, commit or push ran.

### Terminal projection retirement implemented

The offline Regular to Tuna off/on/off cycle now passes through completion and
the next Regular V4 transfer. The previous report remains the before-fix evidence:
pending-proof cleanup had succeeded, but the adapter still exposed publication 5
as a selectable fallback after the coordinator cleared that publication.

Accepted coordinator terminalization now captures the exact fallback publication,
transport epoch, data-session generation and next terminal generation before
terminalizing the leg and clearing its publication. Both service terminal paths
adopt that decision and synchronously apply its typed retirement command before
publishing terminal context or completion notifications. Unscoped completion
observations remain diagnostic-only.

The adapter validates the current data session and exact recovered projection,
rejects current transition work or pending File proof work, and records a File
projection tombstone. It preserves screen state, counters, user-stop state and
pending-proof ownership. A duplicate, mismatched command, replaced data session,
newer publication or newer stop has no retirement effect. A subsequent committed
fallback may reopen the projection only for a different transfer; an old command
cannot retire that successor. No Core callback or availability notification runs
under the adapter locks.

Verification uses the default stack and fake NKN network only:

- [Focused projection and proof cleanup report](<../artifacts/fallback-terminal-retirement-20260930/focused.trx>):
  99 passed, including 22 new terminal issuance, retirement and stale-scope cases.
- [Terminal projection reuse report](<../artifacts/fallback-terminal-retirement-20260930/reopen-final.trx>):
  4 passed, covering same-transfer rejection and different-transfer reopening in
  both directions. Its initial report failed because the seeded predecessor had
  no recovered File lane; the fixture now explicitly seeds that prerequisite.
  Production code was not changed for this test correction.
- [Existing terminal regression report](<../artifacts/fallback-terminal-retirement-20260930/terminal-regression.trx>):
  11 passed, covering first-terminal-wins, publication invalidation, stale
  fallback mutation and service stream retirement.
- [Joined offline cycle report](<../artifacts/fallback-terminal-retirement-20260930/joined.trx>):
  1 passed. Both coordinator-published Tuna V4 activations and correlated fallback
  transitions retain their per-publication runtime evidence. Exact payload
  equality covers 1,572,864 bytes, followed by a verified 1,024-byte Regular V4
  transfer. Both endpoints have no pending proof and retire the exact terminal
  fallback before their completion notifications. The log contains one terminal
  retirement per direction for publication 5, leg 5, fallback epoch 2, transport
  epoch 1 and terminal generation 1.

The [initial red report](<../artifacts/fallback-terminal-retirement-20260930/red.trx>)
preserves two expected missing-adapter-retirement failures and one inbound fixture
setup failure. The inbound fixture was corrected to use its responder Tuna setup;
the [first green report](<../artifacts/fallback-terminal-retirement-20260930/green.trx>)
passes all four initial cases. The joined fixture retains all prior integrity,
route, stale-callback and next-transfer checks; its added checks verify retirement
at completion notification, not merely after a later wait.

The [terminal retirement implementation receipt](<../artifacts/fallback-terminal-retirement-20260930/implementation-review.json>)
pins the source and retained reports. The later reuse-test-only change does not
change the joined fixture or production sources validated by the joined pass.
Acceptance contracts, frozen catalogs and runtime/performance limits are unchanged.
No portable build, live run, full matrix, commit or push ran in this step.

### Receive gap regression aligned with descriptor bound issuance

The [fresh baseline](<../artifacts/receive-gap-contract-review-20260930/baseline.trx>)
reproduces the callback `receive-gap` timeout on the current working tree. The
fixture changed the service data-session generation from 17 to 18 before
delivering a frontier frame. The receive-install guard correctly prevented a
new read without a current pending or installed descriptor, so the frame could
not reach the later `frontier_receive_authority_gap` dispatch boundary. This
was a fixture ordering mismatch, not evidence that the guard should be relaxed.

The corrected fixture first consumes the held sender callback, then delivers
the frontier frame under generation 17 and holds its completion at the existing
receive barrier. It verifies the installed descriptor and service generation,
and that a coordinator-authorized callback successor is still required. Only
then does it change the service generation to 18 and release dispatch. This
uses a synthetic generation replacement to test the stale-dispatch boundary;
it is not an end-to-end session replacement test.

The original authority-gap diagnostic, single source-open count, nonterminal
state and no-error assertions remain. Additional checks require exact rejection
reasons `bootstrap_source_callback_successor_required` and
`data_session_generation_mismatch`, the stale-frame discard, unchanged
coordinator persistent state, unchanged receiver frontier and unchanged
acknowledged bytes. The barrier is released and restored in `finally`; no sleep,
timeout extension, runtime change or acceptance relaxation was introduced.

The [focused correction](<../artifacts/receive-gap-contract-review-20260930/focused.trx>)
passes. After strengthening the final discard and generation checks, the
[affected selection](<../artifacts/receive-gap-contract-review-20260930/affected.trx>)
passes all 33 cases on the default stack: 28 callback failure/handoff variants
and five directly related receive cases. These retain coverage for the one-shot
successor checkpoint, pending-descriptor proof staging, draining while source
reopen is blocked, and exactly-once frontier dispatch across installation.

The [receive gap review receipt](<../artifacts/receive-gap-contract-review-20260930/implementation-review.json>)
pins the test change and retained evidence. Production sources match the
terminal-retirement checkpoint. The prior joined-cycle pass remains relevant;
this test-only correction did not rerun it or the unrelated early-proof variant.
No portable build, live run, full matrix, commit or push ran in this step.

### Early proof stall loses feedback after local fallback yield

The first missing edge is publication of newly committed receiver progress
after the receiver has locally yielded its fallback recovery owner. This is
earlier than the sender's blocked yield continuation. Physical-send retirement
is present in the failing capture, and no unsettled frontier-repair contract
remains. The sender instead retains its legacy frontier-repair flag because it
has not received the receiver's advance from chunk 69 to chunk 72.

The fresh [baseline](<../artifacts/early-proof-frontier-review-20260930/baseline.trx>)
and [repeat](<../artifacts/early-proof-frontier-review-20260930/repeat.trx>)
both pass. Their logs reveal a weaker precondition: the receiver had committed
the prefix, but the sender still began fallback recovery at frontier 0. The
checkpoint at 69 then cleared that frontier-repair flag. Those passes do not
exercise the earlier held-repair boundary at frontier 69.

The early-proof fixture now waits for both the receiver's committed prefix and
the sender's acknowledgement before the first stop. It asserts a fallback
recovery frontier of 69. All encrypted checkpoint packets still pass through
the fake network's normal receive boundary after the existing proof barrier;
the tail barrier still allows chunks 69 through 71 and holds chunks 72 and 73.
No feedback packet for frontier 72 is deliberately dropped. Original route,
proof-lineage, timeout and stale-callback assertions are unchanged. The ordinary
full-cycle variant is not changed or rerun in this step.

The [first synchronized run](<../artifacts/early-proof-frontier-review-20260930/synchronized.trx>)
fails at the second Tuna publication. After adding failure-only scope capture,
the [diagnostic run](<../artifacts/early-proof-frontier-review-20260930/synchronized-diagnostic.trx>)
passes, but its [unchanged-source repeat](<../artifacts/early-proof-frontier-review-20260930/synchronized-diagnostic-repeat.trx>)
fails at the same edge. These are five executions of one test, not five distinct
cases. Synchronizing the starting frontier removes one confounder; it does not
make the failure deterministic.

The latest [failing log](<../artifacts/early-proof-frontier-review-20260930/synchronized-diagnostic-repeat/nlink.log>)
and the report's `Frontier feedback scope` establish the following chain:

1. Recovery starts at frontier 69 at line 1605. Receiver states at lines 1638
   and 1798 still report 69. The early peer proof is promoted and the checkpoint
   accepted before the second-publication wait.
2. Both outbound fallback batch leases retire at lines 1829 and 1839. The
   sender's recheck is held for `frontier_repair` at line 1850.
3. The receiver commits three chunks at line 1866, advancing to 72. At line
   1867 it coalesces feedback with `committed_delta=3` and
   `last_sent_committed_chunk_index=69`.
4. The later frontier-stall path is suppressed at line 1871 because its exact
   fallback recovery owner was retired after local yield. That suppression is
   required; it must not be removed to make the test pass.
5. At timeout the receiver remains locally yielded, with recovery inactive,
   committed frontier 72 and last reported frontier 69. The sender is still at
   69, with repair active from 69, a pending yield recheck, no frontier contract
   and zero active physical sends. Both retain fallback V6 publication 3,
   live-route epoch 2 and data-session generation 1.

The passing [diagnostic log](<../artifacts/early-proof-frontier-review-20260930/synchronized-diagnostic/nlink.log>)
starts at the same frontier 69 and also records a held repair recheck at line
1989. It then sends frontier 72 as `chunk_batch_committed` at line 2059. The
sender clears its repair flag at line 2052, consumes the retained proof and
reaches the second Tuna publication on both endpoints. Send-completion logging
can follow receipt processing, so these log lines do not imply feedback was
received before it was sent. This passing path confirms the missing feedback
edge, not a runtime fix.

The code explains why that edge is conditional. In
`ShouldForceInboundV6ReceiverStateAfterProgress`, a three-chunk advance is below
the 16-chunk threshold; the time-based force condition is checked only when the
method runs. The fallback-specific force path requires an active recovery owner,
which local yield retires. The subsequent frontier-stall worker rejects that
retired owner. The captures prove coalescing versus publication; they do not
record which force condition produced the passing send. A timing-dependent
feedback gap is therefore the supported diagnosis, not a precisely measured
500 ms scheduling race.

#### Bounded fix plan

1. Add a focused regression with controlled cadence and exact current fallback
   scope: locally yielded receiver, last reported frontier 69, an accepted write
   advancing to 72, and no further payload. Require delivery of current progress
   without restarting the retired recovery owner. Use the joined captures as
   integration evidence, not a retry-until-green gate.
2. Make that accepted progress schedule or publish feedback through the existing
   coordinator-authorized receiver-state path. First reuse its control-plane
   token, current source binding and completion validation; introduce no parallel
   authority. Retain the unreported progress obligation until authorized send
   completion or scope invalidation. A generic retired-owner timer must not gain
   permission to repair, replay, yield again or mutate route state.
3. Verify stale write/session/publication/leg/transport-epoch and terminal
   rejection, duplicate/no-progress coalescing, and transition-pause handling.
   Do not repurpose the exact repair-progress receipt for a batch lacking its
   repair contract. Keep the sender's progress requirement and all route and
   retirement guards intact.
4. Run the directly affected feedback/coordinator cases, then the synchronized
   joined early-proof test. Require acknowledged frontier 72, unchanged proof
   lineage and second coordinator-published Tuna V4 on both endpoints. Only
   investigate another sender recheck edge if these receipts prove feedback
   arrived and its blocker cleared.

This step changes only the joined test and this RCA. The
[review receipt](<../artifacts/early-proof-frontier-review-20260930/implementation-review.json>)
pins the current sources and all five reports and logs. Runtime, cadence limits,
acceptance rules and frozen catalogs are unchanged. No aggregate lane, portable
build, live test, full matrix, commit or push ran.

### Post yield feedback fix verified offline

The 2026-10-01 implementation adds a narrow force-send condition in
`ShouldForceInboundV6ReceiverStateAfterProgress`. New, unreported progress on
the sparse fallback V6 path is sent immediately when the coordinator's current
yield operation has a local proof and its decision still matches the exact
runtime, publication, leg, transport epoch and data-session scope. The predicate
does not issue authority or mutate the yield operation. The existing receiver
state send path still obtains and validates its coordinator control-plane token.

This directly sends the accepted progress in the existing write continuation;
it needs no new timer, retained command or recovery-owner restart. The retired
frontier-stall worker remains suppressed. Duplicate and unchanged frontiers
still coalesce. The 16-chunk threshold and time limits are unchanged for other
traffic, as are source validation, transition pause, checkpoint proof and route
publication rules. Existing transport-failure recovery behavior is not changed
or claimed repaired by this step.

The new focused tests model committed progress from 69 to 72 after exact local
yield. They pin the last-state timestamp to `DateTimeOffset.MaxValue` to disable
elapsed-time forcing, so a pass cannot depend on scheduling delay. They test
the feedback predicate and authorized send boundary, not a physical write. The
joined scenarios below exercise actual payload writes. The
[corrected baseline report](<../artifacts/post-yield-progress-20261001/red-corrected.trx>)
fails at the force-send assertion with only this production change removed.
The coordinator proof's currentness is asserted before that expected failure.

The [final affected selection](<../artifacts/post-yield-progress-20261001/affected.trx>)
passes all 23 cases on the default stack:

- Twelve new cases cover the small advance, duplicate coalescing, absent local
  proof, no new write, already reported progress, stale session generation,
  publication, leg, transport epoch and bridge generation, terminal/superseded
  contexts, and activation pause. The pause case explicitly re-invokes the
  send boundary after unpause; it does not prove an automatic retry scheduler.
- Eleven existing cases cover retired-worker suppression, productive traffic
  without recovery, exact repair-progress receipts, sender repair settlement,
  yield rechecks, same-frontier cadence and coordinator control-plane tokens.

The positive unit fixture has no signaling delivery adapter. It verifies the
existing authorized data-session fallback, the issued receiver-state token,
unchanged yield/publication/owner state and a single receiver-state frame, not
a peer-visible delivery receipt. Earlier fixture attempts omitted sparse mode;
the intermediate 11/12 pass then expected an adapter-only delivery log. Those
reports are retained but are not the authoritative baseline or final gate.

The [early-proof joined report](<../artifacts/post-yield-progress-20261001/joined.trx>)
passes. Its [log](<../artifacts/post-yield-progress-20261001/joined/nlink.log>)
starts fallback at frontier 69 at line 1804, promotes the retained peer proof at
line 2004, sends receiver frontier 72 at line 2091 and clears sender repair at
line 2084. As before, send-completion logging can follow receipt processing.
The test requires the same proof identity through retention, promotion and
consumption, plus second coordinator-published Tuna V4 routes on both endpoints.

The [ordinary joined cycle](<../artifacts/post-yield-progress-20261001/joined-cycle.trx>)
also passes on the same build. It retains both Tuna activations and fallback
transitions, exact 1,572,864-byte payload equality, terminal projection retirement
and the subsequent 1,024-byte Regular V4 transfer. These two joins and the 23
affected cases are 25 distinct passing cases, not live acceptance.

The [implementation receipt](<../artifacts/post-yield-progress-20261001/implementation-review.json>)
pins the production change, focused tests, RCA and retained reports. No broad
aggregate suite, portable rebuild, GUI matrix, live run, commit or push ran.

### Original cycle coverage migration verified offline

The original
`RegularNknV4Fast_LiveTunaActivationCanThenCycleOffOnOffSameTransfer` test now
delegates to the joined fixture with a physical-send hold. Its 373-line
synthetic route-cycle body is replaced by the shared negotiated path. No
production code, shared loopback default, timeout or acceptance rule changes
in this coverage step.

The [final focused lane](<../artifacts/cycle-coverage-20261001/final-cycle-lane.trx>)
passes four distinct cases on the default stack:

- The new pending-receive regression holds an actual loopback receive across
  a Tuna-to-fallback owner replacement, confirms the same pending read ordinal
  survives successor-pump installation, then faults that read. It requires a
  newer receive ordinal, stale-owner rejection, unchanged successor publication,
  owner and progress, a nonterminal sender and no error or cancel. Authority
  replacement is synthetic in this focused case; negotiated correlation is
  covered separately by the joined cycle.
- The original-name cycle blocks one actual fallback send in
  `FakeNknClient.BeforeSendCoreAsync`. It captures that send's exact lease and
  requires the coordinator drain command to include it while the drain receipt
  is absent and the sender still publishes fallback V6. Only then does the
  fixture release the send. The completed receipt must name the same command
  and lease, with exactly one lease-retirement record. Both second Tuna V4
  publications, the second fallback, exact 1,572,864-byte output, single initial
  offer/open/manifest, per-publication runtime evidence, terminal projection
  cleanup and a subsequent 1,024-byte Regular V4 transfer remain required.
- The ordinary joined cycle preserves the missing-frontier repair coverage
  independently of the physical-hold variant's acknowledged-prefix setup.
- The early-proof case preserves exact retained-proof lineage through
  checkpoint promotion, consumption and second Tuna publication on both sides.

The final parity review also preserves the old full route-publication tuple
comparison and requires each Tuna recovered event to follow its started event.
Both full-cycle variants pass the
[targeted parity rerun](<../artifacts/cycle-coverage-20261001/publication-parity.trx>).
Only those two cases were rerun after this assertion change; the pending-read
and early-proof paths are unaffected. Two assertion helpers made unused by the
migration are removed. The final evidence covers four distinct passing cases,
not six.

Three failed physical-hold development runs are retained, not counted as passes.
The first blocked payload needed to repair the missing prefix. The second
waited for Tuna publication before releasing a send that production correctly
required to drain. The third could discard the prerequisite fallback batch
because its drop barrier depended on a test flag set after publication. The
fixture now acknowledges prefix 69, allows V6 batch 69 through 71 based on its
wire type, holds the final batch, and releases it only after the exact drain
command captures it. A
[standalone physical-hold run](<../artifacts/cycle-coverage-20261001/physical-wire-barrier.trx>)
passed before migration; the final lane verifies the original test name and
the strengthened receipt assertion. These were fixture corrections, not a
reason to weaken coordinator drain authority.

The [review receipt](<../artifacts/cycle-coverage-20261001/implementation-review.json>)
pins current sources, this RCA, the prior receipt and all reports and logs from
this coverage step. Offline coverage is complete for this migration, not proof
of live GUI behavior. No aggregate suite, portable rebuild, live run, full GUI
matrix, commit or push ran.

### Initial runtime lifecycle corrections and remaining offer mismatch

The [fresh baseline](<../artifacts/remaining-gates-20261001/baseline.trx>)
reproduces both lifecycle failures and passes the other four lifecycle cases
and the accounting test. Accounting still has no retained failing input for
`local_source_supersession_unproven`; another pass does not establish a fix.
Its existing failure-capture retention remains enabled.

Initial fallback V6 startup captured the offer publication before opening its
data session. It correctly validated that token after the open, then started
a coordinator-owned recovery leg that issued a successor publication. The
later startup diagnostic still used the old token and was rejected. The fix
captures the successor token immediately after that synchronous startup step,
under the same service lock. It does not refresh a token across an asynchronous
boundary, weaken publication validation, admit payload or change checkpoint
authority.

The new two-case regression holds the real data-session open. The positive
case requires a single startup record with the current publication, owner and
leg generations, while payload remains blocked until checkpoint acceptance.
The negative replaces the publication during the open and requires rejection,
no installed data session, no session-open or payload frames and no startup
record. Before the fix, the
[red report](<../artifacts/remaining-gates-20261001/startup-red.trx>)
fails only the positive case because the startup record is absent. Both pass
afterward. The production change is one token assignment and two comment lines.

The active-Tuna lifecycle unit fixture now supplies transfer- and direction-bound
readiness evidence, as the other protocol unit fixtures do. This is synthetic
unit evidence, not negotiated transport proof; the joined fixtures retain that
separate responsibility. Shared loopback defaults and production admission are
unchanged. The inbound route-cache-only rejection test still passes.

The [affected selection](<../artifacts/remaining-gates-20261001/affected.trx>)
has 27 passes and one failure, not an all-green result. Passing checks include
all six lifecycle variants, the two new startup cases, the ten existing startup
checkpoint cases, V4/V6 route isolation, diagnostic opt-in, fallback completion
followed by Regular V4 and the initial-fallback analyzer on both PowerShell
hosts. Each analyzer host reports full/compact snapshot and route parity,
48 binding negative controls and 308 successor negative controls. Only the
newly exposed offer mismatch below remains red in this selection.

The negative outbound Tuna test originally gave the receiver a Tuna-only route
cache without readiness and waited for acceptance before inspecting the offer.
Using a Regular receiver removes that unrelated prerequisite but still fails.
Moving the unchanged wire-route assertion before the pending-decision wait now
produces a direct, retained
[failure](<../artifacts/remaining-gates-20261001/offer-mismatch.trx>):
expected `regular_nkn_v4_fast`, actual `file_tuna_v4`.

The first missing edge is in `TryStartSendAsync`: it constructs `offerMessage`
from the requested route, and pins it when bootstrap correlation is supported,
before `EnsureOutboundTransferLegLocked` lets the coordinator reject unproven
Tuna and choose Regular V4. The context and route publication adopt Regular V4,
but the immutable message sent and retained for retry still advertises Tuna.
The initial regression exercised the legacy loopback path. The same construction
order existed in the correlated path; the follow-up below now reproduces and
verifies that path separately.

A simple move of offer pinning after leg creation is not valid under the
current contract: `PinFallbackBootstrapOfferCore` rejects a first pin when a
runtime owner or live route already exists. Rewriting only the wire message
would instead disagree with the pinned offer. Neither workaround is applied.

### Initial offer ordering implementation and verification

`ApplyInitialOutboundOffer` now performs initial route selection, optional
ownerless offer pinning, and leg creation in one synchronous coordinator call.
The existing readiness decision is evaluated once and its private result is
consumed by startup. The service adopts the existing pin and start envelopes in
order, then sends and retains the exact pinned offer. Without correlation it
normalizes the outgoing draft from the resulting coordinator selection. No
reusable admission token, late first-pin exception, second readiness evaluation,
new snapshot decision kind or mutable agreement was introduced.

The new eight-case service regression initially reproduced the wire mismatch
in all six missing/stale-readiness and retry variants across both correlation
modes. Its correlated valid-Tuna control also timed out before the production
fix: the new fixture advertised correlation without the required physical-send
and session-generation contracts. A fixture-local wrapper now supplies those
contracts, forwards the existing data-path authority and preserves transport
metadata. Shared loopback defaults and production feedback guards are unchanged.
The intermediate reports retain both this omission and the incomplete wrapper
attempts; they are not counted as product regressions or discarded results.

The final [service report](<../artifacts/initial-offer-route-20261001/service-final.trx>)
passes all eight cases, requiring offer, accept, session-open, publication and
optional immutable agreement to match, with exact payload completion on both
sides. Seven new coordinator controls passed in the
[focused report](<../artifacts/initial-offer-route-20261001/focused.trx>);
that earlier report also contains the two fixture failures subsequently corrected.
The controls cover ownerless pinning, snapshot-chain continuity, stale scope,
terminal/restarted state, repeat start, late first-pin rejection, missing
readiness and invalid issuer. The
[affected report](<../artifacts/initial-offer-route-20261001/affected.trx>)
passes 36 existing tests, including prepared-Tuna credit/payload and reset
controls, preparation scope replacement, joined negotiation, and correlated and
legacy agreement-history mutation checks. These are 51 distinct passing tests,
not a full aggregate or live-network gate.

### Initial selection analyzer correction

The [fresh service capture](<../artifacts/initial-offer-route-20261001/service-final/nlink.log>)
contains 106 complete snapshot boundaries, eight outbound starts and four
outbound offer pins. Snapshot integrity and full/compact evaluation parity passed,
but route consistency failed at all six outbound Tuna-to-Regular initial selections.
PowerShell 5.1 and 7 produced identical normalized results and first unresolved
boundary, retained in the
[analyzer results](<../artifacts/initial-offer-route-20261001/analyzer-results.json>).
The earlier diagnostic script and its failure results are preserved unchanged.
They describe the analyzer before this correction, not current acceptance.

The first missing proof edge was in
`Get-FileTransferCoordinatorSnapshotInitialPublications`: after validating an
accepted first owner birth, its invariant-field loop also required the route
to remain unchanged. Here the pre-start route is only the requested Tuna V4
selection, with owner and leg generations zero. The coordinator creates the
first Regular V4 owner and leg at generation one. Rejecting that route change
removes the initial-owner lineage used to validate the subsequent publication.
Both valid-Tuna starts and the Regular receivers retain their initial-owner
proof. This is distinct from a committed-route transition or fallback V6.

The correction permits only the route field to change at an accepted outbound
first-owner birth from Tuna V4 to Regular V4. Both protocol versions must be 4;
runtime and leg generations must change from zero to one. All other boundary
fields remain equal. Existing epoch-zero, uncommitted/nonterminal scope,
unique owner descriptor, aggregate hash/generation continuity, publication
identity and exact forwarding receipt checks still apply. Once ownership
exists, the unchanged-route rule still applies. This recognizes coordinator
ownership; transport observations do not establish authority.

Two new coordinator/emitter cases reproduced the failure before the correction
([red report](<../artifacts/initial-selection-evidence-20261001/red.trx>)).
Both now pass with correlation enabled and disabled on PowerShell 5.1 and 7
([focused report](<../artifacts/initial-selection-evidence-20261001/focused.trx>)).
Each mode/host checks 26 negative mutations at the history reducer, including
wrong routes/protocols/direction, pre-existing or noninitial owners and legs,
missing/duplicate births, broken scope/history, missing/incorrect forwarding
receipts, and a route change after ownership. Positive evidence also passes
full/compact parity and the GUI setup-classification consumer. It establishes
one Regular publication, not a Tuna route cycle. An intermediate report retains
a test-script property error corrected before those controls completed.

The [affected evidence lane](<../artifacts/initial-selection-evidence-20261001/affected.trx>)
passes all 30 existing cases: initial Regular publication and same-owner
republication, prepared initial Tuna, initial fallback, and fallback startup
successors with correlated/legacy agreements and independent generation
domains. Together with the two new regressions, 32 tests pass for this step.
No aggregate suite or unrelated runtime lane was rerun.

Reanalysis of the unchanged service input now passes route consistency on both
hosts, with 16 exact initial-publication proofs and no findings. The six original
selection failures are resolved; all 106 snapshot boundaries and their aggregate
hash remain unchanged. The [service analyzer results](<../artifacts/initial-selection-evidence-20261001/service-analyzer-results.json>)
record input SHA-256, boundary hash and full/compact/cross-host parity.

### Initial bounded accounting investigation

At this earlier boundary, `local_source_supersession_unproven` remained open.
The bounded investigation verified all 97 source and 183 artifact hashes in
the preceding receipt before making changes. Its first fresh joined run
failed before accounting with `TargetParameterCountException`: the fixture
passed 12 arguments to `StartOutboundTransferLegLocked`, whose offer-ordering
change added two optional parameters. Reflection requires those arguments
explicitly. The [failed report](<../artifacts/supersession-diagnostic-20261001/capture-1.trx>)
and incomplete input are retained; this is not the original accounting failure.

The joined fixture and the other two reflection callers now supply `null` and
`false` for the new offer parameters. This preserves their existing startup
and recovery behavior. The [focused fixture report](<../artifacts/supersession-diagnostic-20261001/fixture-regressions.trx>)
passes all three directly affected checkpoint/recovery cases. The corrected
joined scenario passes in five fresh, retained captures. Each has one stall
hold, complete passing snapshot evidence and a `SourceSuperseded` closure.
These are five repetitions of one test, not five distinct tests.

The investigation stopped at its five-capture bound without reproducing the
intermittent failure. Diagnostic-only tracing identifies taken return/exit
branches in in-memory copies of the unchanged reducers; it does not alter any
predicate, snapshot input or production file. The unchanged accounting and
report scripts also pass on the first complete capture in both Windows
PowerShell and PowerShell 7, with identical output, full/compact/GUI parity,
mixed unresolved accounting and the existing negative controls. The
[diagnostic results](<../artifacts/supersession-diagnostic-20261001/diagnostic-results.json>)
record capture hashes, outcomes, traces and both host outputs.

The original failing input was unavailable, so these five passes established
neither its root cause nor a fix. Runtime, analyzer acceptance, catalog and
limits were unchanged in that investigation. This led to the controlled ordering
checks below rather than another unstructured batch. Live validation stayed on
hold; no acceptance waiver was applied.

The [bounded investigation receipt](<../artifacts/supersession-diagnostic-20261001/implementation-review.json>)
pins the fixture corrections and bounded investigation, including the
[preceding analyzer receipt](<../artifacts/initial-selection-evidence-20261001/implementation-review.json>)
and the
[preceding offer-ordering receipt](<../artifacts/initial-offer-route-20261001/implementation-review.json>).
No aggregate suite, portable rebuild, live test, full GUI matrix, commit or
push ran in this accounting investigation.

### Accounting wake and snapshot ordering corrections

The next step verified all 99 source and 301 artifact hashes from that receipt.
A new joined-network fixture holds only receiver snapshot writer scheduling
after repair settlement and releases the original queued envelopes after route
commit and disposition. Coordinator decisions and runtime effects are unchanged.
The [retained capture](<../artifacts/supersession-ordering-20261001/delayed/joined.log>)
has complete passing snapshot evidence, but reproduced
`local_source_supersession_unproven` in two successive analyzer predicates.

The first failure preceded the forced snapshot delay. The timer naturally woke
early twice, emitting same-scope `rearmed` decisions with
`stall_grace_deadline_pending` and `authority_effect=none` at writer sequences
600 and 601. The actual dispatch followed at 643. The shared dispatched-progress
reducer required exactly one recheck record, so it rejected the two harmless
rearms together with the one valid dispatch. The
[first-failure trace](<../artifacts/supersession-ordering-20261001/rearm-first-failure.json>)
records that exit. Controlled consumer regressions reproduce this ordering;
the fixture did not force the timer itself to wake early.

The reducer now separately validates same-wake rearms and still requires exactly
one real dispatch with its complete grant, physical-send and settlement proof.
Rearms must preserve the full scope and deadline, have no authority effect, and
precede the real grant in writer and timestamp order. They cannot supply dispatch
authority. GUI and report callers now let this shared validation inspect the
records instead of rejecting them before calling it. Conflicting scope, status,
effects, ordering and missing physical proof still fail closed.

With that edge corrected, the same retained input failed the analyzer's
cross-queue write-order assumption, recorded in the
[second trace](<../artifacts/supersession-ordering-20261001/after-rearm-snapshot-order-failure.json>).
The receiver commit snapshot was authored at generations 27 to 28 and written
at 832, after its synchronous runtime diagnostic at 815. The disposition
snapshot was authored at 28 to 29 and written at 841, after its schema-2
diagnostic at 820. This is valid asynchronous emitter ordering, not stale route
authority. For exact repair-owner catalogs, the analyzer now uses the validated
coordinator generations for causality. Snapshot FIFO order, exact receipt and
scope bindings, complete history, diagnostic commit-to-disposition-to-publication
order, and peer proof remain required. Legacy catalogs retain their previous
write-order checks. No catalog, runtime, timeout or throughput policy changed.

The [early-wake regression](<../artifacts/supersession-ordering-20261001/rearm-green.trx>)
passes on PowerShell 5.1 and 7, including GUI and indexed-consumer parity and
negative controls. Fresh normal and deliberately delayed captures both pass the
[accounting regression](<../artifacts/supersession-ordering-20261001/accounting-green.trx>),
with full/compact/GUI and report parity on both hosts. Each retains the three
peer-interval poison controls and 26 invalid-DTO controls, including reversed
generation, invalid disposition step and reversed snapshot order.

The [affected analyzer lane](<../artifacts/supersession-ordering-20261001/affected-analyzers.trx>)
passed six cases and exposed one older synthetic fixture that assigned both
commit and disposition generations 40 to 41. Correcting only the exact
repair-owner fixture to author disposition at 41 to 42 restores the
[remaining cross-host test](<../artifacts/supersession-ordering-20261001/unresolved-fixture-green.trx>).
Together with the new delayed-drain fixture, 12 distinct test cases pass for this
step. Red and intermediate reports are retained; no aggregate lane was rerun.

Reanalysis of the unchanged retained input now yields `SourceSuperseded` on both
hosts, with complete passing snapshot evidence and the same input SHA-256. This
does not relabel the stalled repair as recovered: its outcome remains
`SettledStalled`, and its successor remains `StaleRejected`. The
[ordering results](<../artifacts/supersession-ordering-20261001/ordering-results.json>)
and [current checkpoint](<../artifacts/supersession-ordering-20261001/implementation-review.json>)
record the evidence. The original deleted failure cannot be retrospectively
attributed to either cause, but both newly reproduced blockers are corrected
and the current targeted accounting gate passes. No portable rebuild, live GUI
test, commit or push occurred in this step.

The original migration step changed tests and documentation only. The subsequent
early-proof step changes the coordinator and its snapshot descriptor, and this
follow-up adds the versioned analyzer extension and emitter profile. The latest
joined ordering verification added fixture and lineage assertions. The receive
gap follow-up adds the issuance guard described above; the pause-capture
follow-up moves callback validation ahead of source-hold preparation. The sender
and receiver runtime-evidence follow-ups add authorized per-publication startup
diagnostics without changing runtime decisions. Shared loopback defaults,
frozen parent catalogs, route correctness rules and runtime limits remain
unchanged. No portable rebuild, live test, commit or push was performed.

## Verification and live test boundary

### Accumulated review found a stale fallback cleanup race

The review verified all 103 source and 315 artifact hashes from the preceding
checkpoint. It inspected the combined native pump installation, checkpoint
promotion, source-hold capture, initial-offer selection, runtime evidence,
fallback projection and analyzer changes. One concrete concurrency finding
was reproduced in `CompleteCommittedPostTunaFallbackFileTransition`.

The method checked `IsFileTransferRouteTransitionDataSessionCurrent` before
acquiring `accelerationGate`. A session could be disposed while cleanup waited
for that lock. The applied receipt, projection and pending episode could still
match, allowing cleanup to increment the pending-proof generation and recover
the file lane for a retired data session.

The controlled adapter regression defers cleanup of an already applied fixture
disposition, holds the projection lock, starts the cleanup worker and disposes
the session while that worker is blocked. It preserves the fixture's original
binding and episode; it does not claim live-network or original producer proof.
Both directions fail before the fix with generation 3 changed to 4
([red report](<../artifacts/accumulated-review-20261001/cleanup-race-red.trx>)).

The fix takes the session-map lock before the projection lock and checks the
session after both are acquired, retaining the map lock through proof cleanup.
The existing order remains `gate`, `accelerationGate`, then
`fileTransferFallbackProofGate`. Coordinator disposition validation and all
receipt, publication, episode, generation and scope checks remain intact.
Notifications still run outside these locks. No retry, protocol, timeout,
performance or acceptance rule changed.

All 27 [focused cleanup cases](<../artifacts/accumulated-review-20261001/cleanup-green.trx>)
and 78 [affected projection cases](<../artifacts/accumulated-review-20261001/projection-affected.trx>)
pass. The normal and early-checkpoint joined cycles also pass in the
[scoped capture](<../artifacts/accumulated-review-20261001/joined-cycle-scoped.trx>),
including their existing route, payload, cleanup and subsequent-transfer
assertions. These are 107 distinct passing cases, not an aggregate suite.

An earlier [joined invocation](<../artifacts/accumulated-review-20261001/joined-cycle.trx>)
failed to retain its operational-log cursors because the invocation omitted
the acceptance and scenario run IDs required for workspace log redirection.
The corrected invocation supplies all four logging environment values and
restores them afterward. No runtime or test assertion was changed for that
setup error; the failed report remains retained.

The [review checkpoint](<../artifacts/accumulated-review-20261001/implementation-review.json>)
pins the changes and reports. No additional blocking finding was identified
in the reviewed paths. This does not certify all possible interleavings or
live behavior. The live provenance reducer rejects a dirty source tree, so
the next gate is a local checkpoint before portable rebuild and strict
preflights. No portable rebuild, GUI run, commit or push occurred in this step.

### Earlier verification retained for traceability

The directly affected follow-up contract selection passed 102 cases
([report](<../artifacts/verification-fixture-rca-20260930/affected-contracts.trx>)).
It covers the remaining callback failure boundaries, peer admission, local-stop
origin/currentness, source hold and drain, physical feedback settlement, and
checkpoint delivery/resume/redelivery coordinator behavior. The eight focused
local-stop cases were excluded where already tested; no full aggregate suite
was rerun.

The [affected analyzer selection](<../artifacts/verification-fixture-rca-20260930/affected-analyzers.trx>)
passes 14 cases: checkpoint handoff, held delivery retirement, coordinator
redelivery and full/compact/GUI source-hold evidence parity. Analyzer and
acceptance code were unchanged at that earlier boundary.

The preceding correction reports contain 126 distinct cases: eight local-stop/callback
cases, 102 affected contracts, the default-stack native/checkpoint pair and 14
analyzer cases. At that boundary the two route fixtures were separate failures,
not waived results. The subsequent initial-recovery pass and remaining cycle
failure are recorded above. The prior red and intermediate reports are retained as evidence.
Source and artifact hashes are recorded in the
[follow-up review](<../artifacts/verification-fixture-rca-20260930/implementation-review.json>).

The local-stop and broader contract runs used the existing diagnostic
`DOTNET_DefaultStackSize=2000000` setting (hexadecimal, 32 MiB), restored after
each process. This is not a production stack change. The native/checkpoint
pair was separately verified with that variable removed. Focused rebuilds
disabled only optional Avalonia build telemetry with `UsedAvaloniaProducts=`.

The original route-cycle migration and the two later lifecycle corrections are
complete, as is the initial-selection analyzer correction. The current targeted
accounting gate now passes after the two demonstrated ordering corrections.
The accumulated review's stale-cleanup finding is now corrected and verified.
The next step is a clean local checkpoint, followed by a portable rebuild and
strict escalated preflights before a single targeted live-256 run. Offline
results do not establish live acceptance, and the deleted original accounting
input remains an evidence limitation. The full GUI matrix is not the next gate.
