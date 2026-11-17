# Superseded-source accounting contract

## Status and decision

Reviewed 2026-09-13 against committed HEAD
`dad138284a470342245dc2249c19ca32076c6f12`. The user approved bounded implementation
and the follow-on initiating retry evidence extension on 2026-09-13. Both are now
implemented and verified in the working tree. The original first-gate stop below
is retained as history, not the current blocker. See the final implementation
section for the runtime prerequisite found and fixed during verification.

Version 2 adds a third source-obligation disposition, `Superseded`, alongside
`Recovered` and `Unresolved`. It means that an exactly identified old-source
obligation has been retired by a proven peer-bound target handoff. It does not
mean that repair A succeeded, that B completed a repair, or that the file
transfer succeeded. The obligation is an analyzer subject, not a newly issued
Coordinator runtime authority.

The first implementation gate is an exact eligibility resolver using existing
validated evidence. A local `SourceSuperseded` diagnostic is necessary but not
sufficient. Do not enable neutral accounting until the joined proof and its
negative controls pass. If an authority link cannot be represented, keep the
episode unresolved and report that first missing edge; this proposal does not
authorize filling it with a runtime change or heuristic.

## Current evidence and limits

The pinned joined fake-peer capture is
`artifacts/repair-owner-evidence-20260912/joined-repair-owner-01.log`, SHA-256
`2FF0096BC5BFEF8D6552D997B12748E3FB62A8CE37E1AA16DF3F95DAD531E13A`.
The committed producer includes the versioned repair-owner evidence contract;
the capture's catalog hash is
`02ecc01ad2fae32da2da0688930b6720a55d5a4833f59e986aeee90e521039d7`.

The focused read-only inventory in
`artifacts/superseded-accounting-review-20260913/evidence.json` confirms a passing,
complete shared snapshot, two validated `paired_yield` bindings, and commit and
applied-disposition records for both peers. The reciprocal bindings share pair
key `fte1-3710b19c9836b7950290a5a5`; each local proof identity/fingerprint equals
the other peer's referenced proof identity/fingerprint. The peers have distinct
Coordinator identities even though they share one process log writer.

The current diagnostic selects the receiver's local replacement chain:
hold 558, A grant 588 and stalled settlement 734, B grant 738 and retirement 810,
then target publication 826. A remains `SettledStalled`; B remains `StaleRejected`
with reason `tuna_reactivation_replaced_fallback_owner`. Existing committed
consumer verification retains Raw/Distinct/Recovered/Unresolved = `1/1/0/1`.
This review rechecked evidence and diagnostic selection, not that full consumer
verification lane.

The inventory is not an implemented cross-peer eligibility proof. In particular:

- The typed route-transition payload has proof/checkpoint fingerprints but no
  direct paired-release authority field. Retry and responder-handoff payloads
  carry release lineage; responder handoffs also carry a linked transition
  authority and generation. The resolver must demonstrate the complete join,
  including the initiating peer's link, rather than assume it from coexistence.
- Raw commit `fallback_yield_authority_id` is redacted, and raw `pair_key` is
  partially redacted. Neither can replace exact typed linkage.
- Receiver and sender commit/disposition pairs retain their own checkpoint and
  activation-proof-boundary fingerprints. Those fingerprints differ between peers. Raw offer
  generations are respectively 4 and 1, not a cross-peer equality requirement.
- Retained-leg checkpoint generation 0, active-lease generation 0 and durable
  transition generation 1 are distinct roles. Equality across those roles is
  neither required nor sufficient.
- A route-transition target data-session identity is raw in this catalog while
  aggregate/source identities are projected. Compare through the documented
  field-specific projection, never guessed raw/projected equivalence or double
  hashing. Each peer must retain its own identity and generation; this fixture's
  equal peer values do not establish a general cross-peer identity rule.

The joined tests assert checkpoint frontier `100/255` and retained data-session
objects on both sides. Payload work is held by a test barrier. There is no target
payload-progress, file-completion, SHA or live-network result here. Real
data-session replacement during staging remains untested.

## Eligibility: all edges required

Only an already admitted, otherwise unresolved stall-grace episode may become
`Superseded`. Preserve existing recovery, admission, duplicate coalescing and
pre-deadline exclusion rules.

1. **Exact old-source episode.** Use the candidate's validated publication
   reference and versioned repair-owner evidence to bind its Coordinator,
   direction, session, transfer, source publication/runtime/leg, retained
   checkpoint and data-session scope. Require the exact wake, dispatch, delivery,
   send and immutable stalled settlement of A, plus the exact successor grant B.
   Diagnostic strings, owner stamps and nearby events are not authority.
2. **Exact local replacement.** Require B's Coordinator-issued retirement for
   this handoff, the accepted route commit, applied disposition and exact target
   publication. Preserve the current diagnostic's source scope and conflict
   checks. Use Coordinator generation/hash continuity, including
   hold-observed state < A grant < B grant <= commit-before state, not asynchronous
   log drain order. A's historical settlement stays stalled.
3. **Reciprocal peer pairing.** Require the complete, passing caller-supplied
   authoritative snapshot and its validated paired-yield bindings. Join the
   same projected session/transfer and pair key, opposite directions, distinct
   Coordinator identities, reciprocal proof identities and fingerprints, and
   each binding's validated quiescent release receipt/effects and source scope.
   Equal route names, timestamps or generations do not identify a peer.
4. **Used release to target authority on each side.** Follow each exact release
   through its actual accepted flow. The initiating peer requires its validated
   release reservation/consumption and installed retry/dispatch lineage into
   the final-proof route transition. The responder requires its accepted
   peer-probe handoff and exact linked transition lineage. Do not require a
   second retry installation on the responder when that is not its flow.
   Each side must reach its own accepted preparation/drain/commit, applied
   disposition and Coordinator-published target. A proof observed or release
   paired but not linked to the used target authority is insufficient.
5. **Target checkpoint and data ownership.** Bind each accepted final proof,
   durable checkpoint and applied handoff to that peer's exact transition and
   publication. Require preservation/adoption through the validated checkpoint
   fingerprint lineage and each peer's own data-session identity/generation.
   Keep retained-leg, active-lease and durable-transition checkpoint roles
   separate. A generation alone, a raw frontier number, a transport observation
   or the fixture's hard-coded `100/255` cannot prove adoption.
6. **Unambiguous proof interval.** Both targets must be current at their own
   accepted publication boundaries. Require complete local authority continuity
   through each handoff, with no conflicting duplicate, foreign binding,
   unaccounted owner/data-session replacement, pause/terminal poison or ABA
   restoration in the required intervals. Use explicit peer-proof causality,
   not a shared clock or globally sorted peer generations. A state-neutral
   stale rejection is diagnostic only; it supplies no positive authority.

The required route transition is `post_tuna_fallback_v6` / protocol 6 to
`file_tuna_v4` / protocol 4. `regular_nkn_v4_fast` remains protocol 4 and active
`file_tuna_v6` remains invalid. Missing, unsupported, ambiguous or invalid proof
leaves the episode `Unresolved`; do not choose a nearest matching chain.

A later target failure does not rewrite an already proven historical source
retirement. It still fails its independent target/transfer gate. Supersession
does not authorize old-source replay or borrow target progress to recover A.

## Accounting version 2

The new accounting version is separate from the Coordinator evidence catalog.
Introduce `AccountingVersion = 2` and `SupersededCount` on this family result;
retain existing counter names. The public DTO counterparts are
`FileTransferFallbackFrontierStallGraceRecheckMissingAccountingVersion` and
`FileTransferFallbackFrontierStallGraceRecheckMissingSupersededCount`.
Text/operator exports use the existing
`filetransfer_fallback_frontier_stall_grace_recheck_missing_` prefix followed by
`accounting_version` and `superseded_count`.

For admitted distinct episodes, require:

`DistinctCount = RecoveredCount + SupersededCount + UnresolvedCount`

Count each disposition explicitly. Both the retained reducer and indexed merge
currently derive recovered as distinct minus unresolved; that expression must
not survive for version 2. Raw counting stays unchanged: existing raw-only
cancelled obligations and duplicate handling can make raw exceed distinct.

| Episode set under version 2 | Raw | Distinct | Recovered | Superseded | Unresolved | Local stall gate |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| One fully proven superseded episode | 1 | 1 | 0 | 1 | 0 | pass |
| One local diagnostic without the full peer join | 1 | 1 | 0 | 0 | 1 | fail |
| One recovered, one superseded, one unresolved | 3 | 3 | 1 | 1 | 1 | fail |

These are approved policy examples, not reclassified results for the old pinned
capture. That older evidence profile cannot authorize neutral supersession.

Keep every superseded episode in `CandidateRecords` and GUI episode audit with
`ObligationDisposition = 'Superseded'`, `Recovered = false`,
`RecoveryEvent = null`, and `Excluded = false` where that field exists. Retain
the original candidate/raw events and A/B outcomes. Attach `SupersessionEvidence`
containing exact source, repair, peer-release, transition, disposition,
checkpoint and publication references selected by the resolver. This is a
read-only explanation, not a new authority token.

`UnresolvedCandidateRecords` and the GUI first-unresolved selection contain only
`Unresolved` episodes. Keep `UnresolvedDiagnostic` for those episodes; do not
present its legacy `ObligationDisposition = Unresolved` as the current status of
a superseded episode. Missing/null optional diagnostics remain missing/null.
Do not set `RecoveryEvent` to a supersession proof or exclude the row to hide it.

Compatibility rules:

- Unversioned archived reports retain legacy version-1 binary accounting, with
  no inferred superseded episodes. Do not rewrite historical artifacts.
- A fresh version-2 analysis of an older unsupported evidence profile may still
  run existing admission/recovery rules, but cannot grant supersession from
  weaker evidence. Such episodes remain unresolved with superseded count zero.
- Version-2 readers require the version and all five nonnegative counters and
  validate the distinct-count equation. An unknown version, missing required
  counter or inconsistent equation is invalid evidence, not an implicit zero
  or pass. All report readers/exporters must change together; old binary readers
  are not compatible with version-2 results.
- Aggregation preserves each input's policy version. Reject a mixed legacy and
  version-2 acceptance aggregation, retaining its per-input results for diagnosis;
  do not label the mixed totals as a homogeneous version-2 acceptance result.

## Failure gates and implementation boundary

The local stall-grace gate fails exactly when unresolved count is positive;
invalid evidence still fails its evidence gate. Remove only proven superseded
episodes from this family's unresolved failure candidates. Do not globally
allowlist `filetransfer_fallback_frontier_stall_grace_recheck_missing` or return
early with a scenario pass. Select the first remaining unresolved candidate's
own diagnostic, not a later superseded candidate's metadata.

Target payload progress and file completion are not prerequisites for neutral
retirement of this old-source obligation. They remain independent acceptance
requirements wherever the scenario requires them, together with integrity,
route correctness, terminal/finalization and complete-proof gates. In particular,
SHA OK or fallback completion cannot replace the second Coordinator-published
`file_tuna_v4` route required by the off/on/off route-cycle scenario.

After approval, the bounded implementation order is:

1. Add a fail-closed, evidence-only eligibility resolver beside the frontier
   diagnostic using the existing snapshot validation helpers. First verify the
   pinned actual-emitter capture and targeted missing/swapped/ambiguous-link
   controls. Stop at an unrepresentable release-to-target or checkpoint-adoption
   edge before changing any counts.
2. Update only stall-grace disposition/counting in `ArtifactWriters.ps1`,
   `FrontierGraceCorrelationIndex.ps1` and `GuiSmoke-Windows.ps1`. The indexed
   merge is shared with recent-progress grace: preserve that family's behavior.
   Candidate partitions may consult the full validated peer snapshot but must
   not admit another partition's candidate. Keep cache scope tied to the exact
   caller evidence and accounting semantics.
3. Update affected DTOs, text/JSON writers, summary imports and aggregates in
   `ArtifactWriters.ps1` and `Run-FileTransferRouteAcceptance.ps1` atomically.
   Add no policy switch or generic classification framework.
4. Verify focused resolver, direct/indexed/full/independently compact/shared-GUI
   parity under PowerShell 5 and 7, then the directly affected reporting tests.
   Run at most one relevant broader contract lane after stabilization.

Required regressions include A/B outcome preservation; distinct same-writer
peers; delayed log drain in both writer orders; reciprocal proof substitution;
missing release consumption or responder linkage; missing either peer's commit,
disposition or publication; checkpoint-role confusion; data-session mismatch;
ABA/poison/conflicting duplicates; partition boundaries and duplicate counting;
unchanged pre-deadline exclusions and recent-progress behavior; one superseded
plus one unresolved with exact first-failure selection; absent/null metadata;
legacy import, malformed version-2 counters and mixed-version aggregation; and
an all-superseded local result with an independent target failure still failing
overall. A synthetic positive alone cannot replace the actual-emitter join.

No runtime scheduling, retry cadence, bridge/queue/concurrency, wallet, installer,
invite or screen-sharing change is in scope. No full retained report, portable
rebuild, GUI, preflight, live NKN run, commit or push is part of this review.

## Implementation gate result, 2026-09-13

The first missing edge is
`initiator_retry_install_to_route_transition_proof`. No accounting resolver that
can positively establish the approved contract has been installed. No counters,
classification branches, DTOs, runtime code or catalog were changed.

The fresh inventory contains 61 validated boundaries. The focused pinned-capture
gate runs independently under PowerShell 5 and 7 and produces identical results:
complete snapshot pass, reciprocal paired releases, exact initiating install and
dispatch settlement, local `SourceSuperseded`, and unchanged `1/1/0/1` accounting.
Its `blocked_evidence_edge` result is an expected evidence limitation, not an
acceptance pass. Results and reproducible scripts are retained in
`artifacts/superseded-accounting-implementation-20260913/`.

### Exact evidence up to the gap

The sender's state-11 install boundary
`fte1-29353ce7e715fa01184721f9` binds consumed paired release
`fte1-0d664769a5cde5af578c3149` to retry authority
`fte1-65bd848f97e18ed123c26f8f`. Consumption and install share receipt fingerprint
`fte1-23b9812b9e3c0f538f32f528` and generation 1. The same retry has a validated
`DispatchObserved` settlement at state 14. These links are represented.

The sender prepares route authority `fte1-c079a17d26f53e9ede13b0f7` at state 18,
commits at state 22 and applies disposition at state 23. Its route-transition
payloads contain neither that retry authority, the install receipt fingerprint,
nor the paired-release authority. The consumed reservation's retirement in the
same commit boundary proves retirement, not which retry receipt is embedded in
the route authority's activation proof. A same-owner scope or matching offer /
install generation cannot substitute for that missing reference.

### RCA: information is lost during evidence projection

`FileTransferTunaActivationTransitionProofBoundary` already contains
`EffectiveRetryInstallReceipt` and `FallbackTailPairedReleaseReceipt`.
`TryValidateFileTransferFallbackTunaActivationBoundaryCurrent` checks those exact
objects against the current effective retry and consumed-release ancestry.
This review establishes no runtime ownership or validation defect.

`CreateRouteTransitionOperationDescriptor` and the attempted-operation snapshot
builder serialize the enclosing proof as `proofBoundaryFingerprint`, without a
typed initiating retry/install reference. The service then projects the
fingerprint to an evidence token. The separate retry snapshots expose the
receipt fingerprint, but it cannot be recovered from the opaque enclosing hash.
Raw route logs expose only retry-install generation and a redacted release ID.

Correction to the earlier review wording: `proofBoundaryFingerprint` hashes
`authority.ProofBoundary`, not `operation.FinalProof`. The string
`runtime_unlock_final_proof` is selected when `FinalProof` exists; it does not
expose that final proof's fields or an exact retry reference. Neither the label
nor the passing legacy snapshot verdict closes this new cross-family edge.

### Narrow follow-on evidence plan, requiring scope approval

Add a versioned, read-only initiating-activation retry binding to route-transition
evidence, derived only from the Coordinator authority's actual activation proof.
The minimum join fields are effective retry authority ID and authority generation,
install generation and install receipt fingerprint, and paired-release authority
ID and release generation. Parent transition authority, source scope and proof
fingerprint remain the existing exact binding context. Do not add a new runtime
authority, derive this DTO from transport dictionaries, or copy a nearby retry.

Require the binding in a new catalog profile for applicable accepted initiating
PostFallback-to-Tuna preparations and their retained drain/commit/disposition
history. Keep older profiles unchanged and ineligible for the new neutral policy.
Project identifiers/fingerprints consistently with the existing retry and release
families. Validate the effective install against its exact issued receipt and
the existing consumed-release ancestry; a successor install must not be assumed
identical to the original consuming install. The responder remains on its
separate existing handoff lineage and must not fabricate an initiating receipt.

Verify actual-emitter projection and focused missing/foreign/stale/ambiguous
binding negatives, including a consumed-release descendant and an unrelated retry
with matching numeric generations. Then regenerate the joined fake-peer capture
and rerun the first gate under both PowerShell hosts. Preserve the old capture.
Do not change accounting until all remaining peer, target and checkpoint gates
are independently established; this evidence extension would resolve only the
first missing edge. No GUI or live-network run is required for this slice.

## Implemented and verified, 2026-09-13

The new catalog suffix is `-activation-retry-v1`, with hash
`6f8667e92dae781e679bbe6f59fbd4f3d6284cd893ad2f643a9e47a21d451b00`.
Its parent repair-owner catalog/hash are unchanged. The six-field read-only
`activationRetryBinding` comes from the actual authority's activation proof.
Initiating history retains the exact effective install/release binding; responder
history uses its existing linked handoff without fabricating an initiating retry.

The resolver now proves consumption/install ancestry and dispatch, reciprocal
peer release proofs, both target commits/dispositions/publications, stable source
and target intervals and role-specific checkpoint/data scope. Indexed partitions
consult full relevant peer evidence but count only their owned candidates. Raw
pause/terminal/data replacement observations can veto, never authorize, closure.
Superseded rows stay unrecovered and unexcluded in retained/indexed/GUI audit.
Early recovered branches also receive an explicit disposition. Version/counts
propagate through text/JSON writers and acceptance imports/aggregation. Invalid
mixed aggregates use version 0, retain per-run results and fail correctness.
Independent target failures and route-cycle/completion gates are not waived.

### Runtime prerequisite RCA and fix

The root Core projection fixture completed, but a consumed-release descendant
prepared successfully and then failed commit with `paired_fallback_tail_yield_missing`.
Omitting scope invalidation was also correctly rejected, with
`runtime_unlock_retry_scope_invalidation_required`.

`HasEffectiveFallbackTailPairedReleaseForRouteTransition` tried to resolve an
exactly invalidated retry using `LastConsumingInstallReceipt`, the root consumer.
Full-scope invalidation instead records the effective descendant in its command
and terminal result. The fix selects the install from the Coordinator-issued
`LastScopeInvalidationReceipt.Command.InstalledAttempt.InstallReceipt`, then uses
the unchanged exact-invalidation and consumed-release-ancestry validators.
Missing receipts, pending invalidation, substituted root terminals and substituted
commands remain rejected. Root and descendant fixtures now both commit and apply
disposition. Their projection-only logs still fail full artifact acceptance when
physical-effect records are absent; none were invented to satisfy the validator.

### Verified result and next test boundary

Fresh joined fake-peer capture:
`artifacts/activation-retry-evidence-20260913/joined-activation-02.log`, SHA-256
`C131A7A7963DAE511F20F16A7F98CAD8648CD457921B4982138FAA293A115EEA`.
PS5 and PS7 independently produce the same complete passing snapshot, one exact
initiating binding and the same supersession closure. Version-2
Raw/Distinct/Recovered/Superseded/Unresolved = `1/1/0/1/0`.
The original repair-owner capture is unchanged and reanalyzes as `1/1/0/0/1`.

Automated tests cover actual-emitter direct/indexed/full/independently compact/
shared-GUI parity under both hosts; mixed episodes across partition boundaries;
explicit three-bucket merge; both peer-boundary orders; missing, swapped and
ambiguous links; source/target ABA; peer poison; legacy/malformed report imports;
mixed-version aggregation; and an independent target failure remaining failed.

The broader route-transition lane aborted in an existing bootstrap-history test.
Selected diagnostic frames measured 400,944 bytes in its caller, 237,040 in the
history helper and 465,744 in snapshot construction. Splitting post-history
assertions into a non-inlined helper and comparing the original persistent-state
snapshot by readonly reference reduced the test entry frame to 112 bytes. The
262,128-byte assertion helper no longer overlaps history creation. All assertions
remain, including full persistent value equality. Both variants pass in a normal
Debug host; the original lane's six unexecuted cases were resumed without
repeating already passing methods and pass. No stack size, thread/JIT policy,
prewarming or skipped variant was used.

The exact run ledger and intermediate failures are retained in
`artifacts/activation-retry-evidence-20260913/RESULTS.md`. Changes are uncommitted.
No portable rebuild, GUI, bridge preflight, live NKN, live 256 MB attempt or full
retained report ran. The next phase is the portable rebuild, escalated preflight
and targeted `regular-v4-live-activation-off-on-off-256mb` scenario. Its second
Coordinator-published `file_tuna_v4` route remains mandatory; SHA or fallback
completion cannot substitute for that route-cycle proof.
