# Receive-recovery terminal checkpoint convergence: RCA and fix plan

Date: 2026-09-20. Status: bounded analyzer correction and fixture-history repair
implemented; focused/GUI cases pass, stabilized combined gate 26/26 PASS. Historical
stack cause remains unconfirmed. No live rerun or commit.

## Conclusion and scope

The fresh live-256 run failed its authoritative snapshot-validation gate because
the analyzer does not recognize one coordinator-produced terminal carry:
`StartTransfer / Accepted` replaces the current leg, and an unchanged invalidated
receive-recovery terminal's projected source/effective checkpoint generations
converge from 1 to 0. The existing analyzer permits that convergence for a route
commit, but not for this leg replacement.

This is a producer/analyzer contract-coverage gap, not evidence that transport
completion failed. The recommended repair is a narrowly guarded analyzer case,
backed by a fresh coordinator-generated regression. Do not change runtime
checkpoint behavior merely to satisfy the analyzer.

This conclusion concerns the first **artifact-validation** failure. It does not
resolve the two separately reported stall-grace obligations, prove that every
downstream finding is secondary, or turn this run into an acceptance pass.

## Evidence and provenance

- HEAD and tested checkpoint: `b3b4837e3ce8ffeacfc32abba32e490627da02e5`.
- Fresh portable build, strict preflights and source provenance passed. One live
  attempt ran; no setup/goodput retry, full matrix or push occurred.
- Run: `20260920T150102Z`; acceptance ID:
  `bcfab1b5-f219-478e-a7a4-737f25d4d32b`.
- Scenario: `regular-v4-live-activation-off-on-off-256mb`.
- Both endpoints completed 268435456 bytes with matching SHA256. Raw observations
  include regular-v4, Tuna-v4, fallback-v6, second Tuna-v4, final fallback-v6.
  These are not a substitute for accepted coordinator snapshot proof.
- The original analyzer exited 0 without timing out, but its original receipt is
  `proof_incomplete`, with `completedProofArtifacts=false`. The parent failed
  closed; the operator verdict is `FAIL_ARTIFACT_EVIDENCE`.
- Full/compact parity passed: 7513 boundaries, 7512 transitions, 420 effects each.
  Matching parity does not make invalid evidence valid.

The [original receipt][receipt] binds the 80431-line retained input before/after
to SHA256 `55cee026c02b66740327840741a6337029c218f6f16062a199dcc5af07623e07`.
The [original full summary][full] SHA256 is
`0d1eabfb81caf8ba4f3b706c5c9dfa65bded8d4a3f38ab92b2a734fb42428bc3`.
The [original compact summary][compact] SHA256 is
`dce3efc9eaaa555779be308feae2da9ef7f586fb4dd82e81b8716ecdf9e91014`.

[Extracted witnesses][witnesses] retain the relevant operation history, prior and
failing terminal descriptors, and a recursive field comparison. They are
read-only RCA excerpts, not another authority evaluation or acceptance receipt.

## Exact failing boundary

Full evidence reports `snapshot_receive_recovery_root_lifecycle_invalid:27505`;
compact evidence reports the same reason at compact index `2566`. Both identify:

- Boundary `fte1-c7420a01bffde520f1cbfd0c`.
- Coordinator `fte1-f84377ba81579051b063265a`.
- Writer `p40640rbbdcc981815c4ce8`, sequence `16230`.
- State generation `268 -> 269`.
- Decision `StartTransfer / Accepted / state_refresh_received`.

Do not confuse this coordinator decision with the analyzer's synthetic
`ArtifactEvidenceValidation / Rejected` finding that references it.

The root authority is `fte1-3f38e46928ed3b2fdb40af78`, generation 1, kind
`BridgeReceiveStallRecovery`. Its terminal remains `Invalidated:None` throughout
the failing carry. The validated `Adopted` receipt, command fingerprint, proof,
data-session identity and all other operation fields remain unchanged.

The entire prior/current terminal descriptor comparison has exactly two deltas:

| Field | Prior | Failing boundary |
| --- | --- | --- |
| `receiveRecovery.sourceBoundary.checkpointGeneration` | 1 | 0 |
| `receiveRecovery.effectiveBoundary.checkpointGeneration` | 1 | 0 |

Both checkpoint request IDs remain `(none)`. Source and effective boundaries
remain equal to each other. Their other fields continue to describe the original
regular-v4 recovery scope; they do not acquire the current fallback route's
authority.

Relevant history from the original full summary:

| Retained line | Writer sequence | Decision | Root state / checkpoint projection |
| --- | --- | --- | --- |
| 11352 | 5227 | Commit repair-pressure recovery handoff | Issued; checkpoint 0 |
| 11393 | 5272 | Observe execution start | Running; checkpoint 0 |
| 11485 | 5342 | Observe execution receipt | Completed:Retained; checkpoint 0 |
| 11550 | 5386 | Observe recovery proof | Completed:Adopted; checkpoint 0 |
| 12982 | 6712 | Commit route transition | Current retired, terminal Invalidated:None; checkpoint 0 |
| 14799 | 7561 | FallbackCheckpointAccepted | Terminal unchanged; checkpoint 0 -> 1 |
| 23880 | 14034 | Commit route transition | Terminal unchanged; checkpoint 1 -> 0, supported |
| 25533 | 15116 | FallbackCheckpointAccepted | Terminal unchanged; checkpoint 0 -> 1 |
| 27505 | 16230 | StartTransfer | Terminal unchanged; checkpoint 1 -> 0, rejected |

At the failing boundary the aggregate remains `post_tuna_fallback_v6`, protocol
6, live route epoch 4, data-session generation 1 with unchanged identity. Leg,
runtime and publication generations advance 5 -> 6. Aggregate checkpoint
generation is 0 on both sides. Same-boundary operations retire retained checkpoint
acceptance, replace the runtime owner, and replace the route publication.
The retained checkpoint-acceptance descriptor carries leg checkpoint generation
1, allowing the terminal's prior projection to be tied to that retired scope.

## Causal chain in code

1. [CreateReceiveRecoverySourceBoundary][projection] derives checkpoint generation
   from the **current leg** when its request ID equals the recovery observation's
   request ID. Null equals null, so the old root can project the current leg's
   checkpoint generation even though all its other scope fields come from its
   original observation. Root source/effective descriptors use this projection.
2. [StartTransfer][start] installs a new leg and runtime owner. [StartLeg][leg]
   does not initialize checkpoint generation/request ID; their defaults are
   0/null. The accepted replacement therefore produces the observed 1 -> 0
   terminal projection without changing that terminal's authority or receipt.
3. [Get-FileTransferCoordinatorReceiveRecoveryRootLifecycle][lifecycle] compares
   the retained terminal against its prior descriptor. Exact source equality
   fails. Its direct checkpoint exception is only for checkpoint request/accept
   decisions, so it delegates to the shared terminal-carry predicate.
4. [Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry][carry] preserves
   immutable terminal identity and permits checkpoint-only changes for checkpoint
   request/accept or `CommitFileTransferRouteTransition / Committed`. Neither
   matches this `StartTransfer / Accepted`; the predicate returns false.
5. Root lifecycle validation fails closed. Authoritative route projection is
   rejected, so empty accepted route selections do **not** mean no route events
   occurred. [The parent completion gate][parent] refuses incomplete proof
   artifacts despite child exit 0. The later finalized-attempt-selection failure
   must not be repaired by forcing selection or bypassing this gate.

The established local cause is the missing exact transition case. A fresh
coordinator-generated fixture must reproduce it before implementing the proposed
analyzer correction. No historical evaluator was replayed for this RCA.

## Bounded fix plan

1. **Reproduce with current producer output.** Extend the relevant coordinator
   snapshot fixture, or add a minimal neighboring fixture, to preserve a genuine
   adopted root through invalidation, checkpoint acceptance, and an accepted
   fallback-v6 `StartTransfer` leg replacement. Emit canonical snapshots with the
   current catalog; do not hand-author valid-looking authority or edit a frozen
   fixture's catalog hash. Confirm the original root-lifecycle rejection and
   exactly the two checkpoint deltas above. Cover both transfer directions where
   the coordinator permits the same sequence.
2. **Add one strict shared carry case.** Keep the existing root-lifecycle gate and
   shared terminal identity checks. Require all of the following, binding them
   to one validated coordinator/writer boundary rather than a reason string:
   - State-applied `receive_recovery / operation_terminal`, unchanged
     `Invalidated:None`, same operation, authority, command, receipt and proof.
   - Accepted `StartTransfer`, exact state-chain continuity, and the demonstrated
     same-route fallback-v6 leg/runtime/publication replacement.
   - Source and effective boundaries unchanged except equal positive checkpoint
     generations becoming 0; all four request IDs `(none)`; source/effective
     equality preserved. Aggregate checkpoint generation remains 0 on both sides.
   - Exact retained checkpoint-acceptance retirement and old/new runtime-owner
     and publication descriptors agree with aggregate before/after scope. Bind
     the prior terminal checkpoint to the retired leg's checkpoint generation.
   - Stable route/protocol, live epoch, data identity/generation, terminal,
     transport and bridge-recovery scope; no mismatched or missing owner chain.
   This recognizes retained diagnostic scope only. It grants no new authority,
   makes no invalidated operation current, and preserves poisoned/unresolved
   historical obligations. Do not merely add `StartTransfer` to an allowlist.
3. **Prove fail-closed behavior.** Mutation controls must reject changed receipt,
   proof, command, data identity, route/protocol, or non-checkpoint scope;
   asymmetric source/effective checkpoints; wrong/nonempty request IDs; wrong
   role/state; rejected/wrong decision; missing/mismatched retirement or owner
   chain; foreign/stale operation; and broken generation/state continuity.
   Historical snapshot objects must remain immutable after later decisions.
4. **Run only affected deterministic coverage.** Start with the new regression and
   negative controls. Then run the directly affected shared root lifecycle,
   route-carry, terminal-rollover and atomic-role tests using PowerShell 5.1 and
   7 where supported. Existing [script-surface tests][tests] and the
   [coordinator fixture][fixture] are the starting points. Run at most one broader
   relevant contract lane after stabilization. Preserve full/compact parity and
   existing evaluation-count/receipt rules. No full aggregate matrix.
5. **Review the remaining proof failures independently.** Use the original
   stall-grace witnesses for a separate first-edge RCA before proposing runtime
   changes. Do not claim this analyzer repair resolves them or any other derived
   mismatch count. No deadline extension, retry/performance tuning or diagnostic
   payload promotion is included.
6. **Return to the live gate only when warranted.** After reviewed implementation
   and focused verification, rebuild the portable and use strict preflights for
   one targeted live-256 attempt under the applicable authorization. A valid
   second coordinator-published `file_tuna_v4` remains required. Preserve this
   run's original failed receipt and summaries; no retrospective upgrade.

## Separate open obligations and limits

The original report counts stall-grace raw/distinct/recovered/unresolved as
**31 / 30 / 28 / 2**. Its first semantic witness is retained line `17150`, writer
sequence `8230`, event `filetransfer_v6_frontier_request_deferred`, reason
`post_tuna_fallback_frontier_stall_grace_expired_without_exact_receiver_request_recheck`.
This semantic witness occurs earlier than the artifact rejection; the two are
different gates, not competing explanations for one failure.

Recent-progress accounting is **79 / 3 / 3 / 0**. That success is limited to its
observed obligations. The report also retains other findings, including 56
unresolved Tuna data-path mismatches; this RCA does not independently adjudicate
or dismiss them.

There are no `filetransfer_frontier_payload_marker` events in the retained source
and no frontier/publication diagnostic artifact in the scenario directory.
Consequently this run does not positively demonstrate the new native payload
channel end to end. Absence alone does not identify its cause or establish a
channel implementation defect.

The initial RCA preparation changed documentation and a derived witness artifact
only. The user subsequently authorized proceeding with the plan; implementation
and verification are recorded below. See the [completed run report][report] for
the unchanged historical live outcome.

## Implementation and verification follow-up

The shared terminal-carry predicate now admits only the demonstrated inbound
`StartTransfer` checkpoint convergence. It reuses the existing checkpoint receipt
validator and requires exact retained checkpoint retirement, old/new owner and
publication scopes, stable non-checkpoint recovery identity, and unchanged
invalidated state. Existing checkpoint/route-commit branches remain unchanged.
No C# runtime, deadline, catalog hash, authority policy or live receipt was changed.

The fresh fixture extends the existing coordinator-authored route/recovery path.
It proves historical snapshots remain immutable and only the terminal source and
effective checkpoint generations change. Both directions can produce the reset,
but the retained checkpoint-repair continuity receipt is receiver-only. Therefore
inbound is the positive case and outbound remains a negative control; the fix
does not invent sender authority or expand the reviewed receiver contract.

- Before the fix, both hosts reproduced the exact root-lifecycle failure on the
  new inbound fixture (index 19). Outbound reproduced it at index 18.
- After the fix, all four focused cases pass: inbound acceptance and outbound
  rejection on PowerShell 5.1 and 7. Each inbound case also passes 36 malformed
  carry controls, fresh full/compact parity, and two full-history rejection
  controls (missing checkpoint retirement and a predecessor hash gap).
- The one relevant wider lane recorded 13 passes and 10 failures, then aborted
  with a test-host stack overflow. It is not a passing contract gate.
- All ten completed failures are older route-carry/rollover/atomic GUI assertions.
  Representative fresh fixtures from all three groups reproduce with the
  original HEAD carry predicate. The GUI selects
  `snapshot_authoritative_route_boundary_invalid`, not an invented recovery
  incident. The rollover fixture's route projection first reports missing
  contiguous publication generation 1 and an invalid initial predecessor, then
  lacks an exact committed fallback predecessor. Its snapshot artifact verdict
  alone passes. Do not weaken production route checks or simply delete assertions.
- The aborted Core test was
  `RuntimeUnlockInitialActivationOfferEnvelopeRefresh_HeldReleaseCommitsOneExactSuccessorEnvelope`,
  at `TryGetRuntimeUnlockInitialActivationFallbackCheckpointProofProfile` through
  envelope-refresh reconciliation. A single isolated rerun passed unchanged.
  A combined-run stack/lifetime or JIT explanation is not yet established; no
  stack-size/JIT override or speculative runtime fix was applied.

The older GUI fixtures now capture their actual coordinator-authored startup,
initial publication and activation history. Data/coordinator identity is fixed
before the first decision, with no post-publication snapshot reset. Every
captured generation and fingerprint remains contiguous. The recovery controls,
frozen catalogs and production GUI checks are unchanged; explicit route-history
assertions were added. Eight producer/regression cases pass, followed by four
checkpoint cases including full/compact route consistency. All ten older GUI
cases also pass. The stabilized combined gate against the final script sources
completed with 26 passes, no failures/skips, in 16.58 minutes: twelve affected Core
cases and fourteen checkpoint/carry/rollover/atomic script cases. No full matrix
was run. Whitespace and PowerShell parse checks pass; production C# is unchanged.

The known Core predecessor sequence also passed (10 cases), with method-specific
JIT diagnostics only. A separate mixed selection passed five cases, but its TRX
timestamps show Core ran first, so it does not test post-async execution despite
its diagnostic filename. Selected fixed reservations range from 15,648 bytes in
the held-release helper to 116,320 in the failing profile method; the later
receipt helper reserves 152,752 bytes. These do not establish a complete active
stack or crash cause. No stack/thread/JIT-policy change or speculative runtime
fix was applied. A diagnostic-only project subsequently invoked the unchanged
Core test synchronously and after an explicit Task.Yield; both passed, so async
yield alone is not a sufficient reproducer. The original long runner continuation
chain has not been recreated, and Core ran first in the successful combined gate.
The historical crash remains an unroot-caused verification risk, not a claimed
runtime fix. These checks do not close the separate live stall-grace findings.

Read-only inspection of the first original stall witness found wake 15 followed
by same-deadline highest-progress re-arms 16 and 17, stale callbacks for 15/16,
then a wake-17 dispatch. Lines 17150, 17207, 17237, 17241, 17242 and 17275 bind
that sequence. Further direct inspection binds the repair grant at line 17246
and dispatch/send at 17263/17264 to authority
`fta2-repair_replay-399792dd79ce50e6ea17c1fd`, repair generation 8. Line 17592
grants generation 9 with that predecessor; line 17593 settles generation 8 as
`Superseded` with the exact new successor authority. Line 17795 instead settles
generation 9 and must not be mistaken for another generation-8 settlement.
This remains diagnostic context, not an accepted current-source/publication
closure. Neither the two-obligation count nor the original authority evaluation
was recomputed, and the second obligation is not independently identified by this
follow-up. No runtime stall-grace correction is justified yet. The original
retained input and full/compact snapshot hashes still match their receipt.

Detailed test results and remaining work: [implementation report][implementation].
The deterministic milestone is complete. The next acceptance milestone requires
a fresh portable build, strict preflights and one targeted live-256 attempt with
new evidence, rather than re-evaluation of the original failed attempt. No live
run or local commit was made in this follow-up.

[receipt]: <../artifacts/frontier-live-checkpoint-20260920/live/20260920T150102Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-retained-analysis-attempt-receipt.json>
[full]: <../artifacts/frontier-live-checkpoint-20260920/live/20260920T150102Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-coordinator-snapshot-full-summary.json>
[compact]: <../artifacts/frontier-live-checkpoint-20260920/live/20260920T150102Z/regular-v4-live-activation-off-on-off-256mb/filetransfer-coordinator-snapshot-compact-summary.json>
[witnesses]: <../artifacts/frontier-live-checkpoint-20260920/rca-root-lifecycle-witnesses.json>
[projection]: <../src/nLink.Core/FileTransfer/FileTransferCoordinator.Snapshots.cs#L5823>
[start]: <../src/nLink.Core/FileTransfer/FileTransferCoordinator.cs#L24818>
[leg]: <../src/nLink.Core/FileTransfer/FileTransferCoordinator.cs#L9539>
[lifecycle]: <../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L7429>
[carry]: <../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1#L7594>
[parent]: <../tools/Run-FileTransferRouteAcceptance.ps1#L7047>
[tests]: <../tests/nLink.SmokeTests.Core/FileTransferOpsScriptSurfaceTests.cs#L6679>
[fixture]: <../tests/nLink.SmokeTests.Core/FileTransferCoordinatorTests.cs#L5501>
[report]: <../artifacts/frontier-live-checkpoint-20260920/RESULTS.md>
[implementation]: <../artifacts/terminal-checkpoint-convergence-20260920/RESULTS.md>
