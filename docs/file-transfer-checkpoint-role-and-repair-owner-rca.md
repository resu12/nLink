# Checkpoint roles and missing repair-owner evidence

Implementation update (2026-09-12): the bounded repair-owner contract is now
implemented. The fresh joined fake-peer capture proves `SourceSuperseded`, while
A remains `SettledStalled`, B remains `StaleRejected`, and the obligation remains
unresolved (`1/1/0/1`). See the implementation section below. The original review
and withdrawn-candidate account are retained as historical evidence.

Reviewed 2026-09-12 against `63df93b28cb9a8ba4924df97c4fa72698fd4fe4a` and the
uncommitted publication-evidence implementation. This is an RCA and bounded fix
contract. No runtime bug is established and no new production change is retained
from this review. Existing source-supersession selection remains fail-closed.

## Finding: three checkpoint roles, not one generation transition

The saved joined capture is
`artifacts/publication-evidence-20260912/joined-evidence-01.log`, SHA-256
`0D47ADE187B1A72538DB12C22BBE9F15A3BEE08508841AAE1BEB8D6B8F7DD16D`.

| Evidence | Meaning | Observed generation |
| --- | --- | ---: |
| Hold 561, raw `checkpoint_generation` | Current leg's retained request checkpoint | 0 |
| Commit 812, raw `checkpoint_generation`; typed route-transition `sourceBoundary.checkpointGeneration` | Durable transition checkpoint | 1 |
| Commit snapshot `beforeBoundary.checkpointGeneration` | Current active fallback checkpoint lease | 0 |

`LogInboundV6FrontierRequestDeferred` reads the leg. In contrast,
`CreateInboundFileTransferRouteTransitionCheckpoint` and its outbound counterpart
derive the durable generation from `max(1, DataSessionGeneration)`, capped at
`int.MaxValue`. The durable checkpoint also captures the source owner, publication,
leg/transport scope and durable frontier. `CreateRouteTransitionSourceBoundary`
projects that durable generation with identity `checkpoint:<generation>`.

`CreateStateBoundarySnapshot` reads `CurrentFallbackCheckpointLease`, not the leg.
`CreateCheckpointRepairContinuityScope` deliberately records both the aggregate
lease scope and `RetainedLegCheckpointGeneration`/`RetainedLegCheckpointRequestId`.
After checkpoint acceptance, the active lease can clear while the retained leg
generation persists. Consequently neither leg-to-durable equality nor
leg-to-aggregate-lease equality establishes the required lineage. The observed
0/1 values are not evidence of an unexpected live checkpoint increment.

The diagnostic currently compares the hold's raw generation with the commit's
raw generation in `Get-FileTransferStallGraceUnresolvedDiagnostic`. That comparison
crosses roles. It must eventually be replaced with separately validated bindings,
not deleted or replaced with the equally incorrect aggregate-lease comparison.

## Next missing proof: repair ownership independent of checkpoint acceptance

The hold carries Coordinator `fte1-9d6833cb33a64c373e60f790`. Grants A at 590 and
B at 736 carry neither `continuity_coordinator_id` nor `continuity_profile`.
Their respective `RepairReplay` snapshot boundaries at 589 and 735 are present,
but both have **zero typed operations**. The capture contains no
`checkpoint_acceptance_retained` or `checkpoint_repair_continuity_attempt` operation.
An adjacent generic transition envelope is therefore not an exact repair receipt.

The grant logger emits `contract.CheckpointContinuityStamp` only when supplied by
`ValidateCheckpointRepairContinuity`. That validator returns `NotApplicable` with
no stamp when `CheckpointRepairContinuityOrigin` is absent. This fixture begins
with a controlled fallback baseline, not a completed checkpoint-acceptance chain.
The missing acceptance-specific evidence must not be manufactured to make the
fixture look like another route history.

A diagnostic-only candidate change advanced beyond the generation comparison and
reached the next rejection: A's absent Coordinator continuity ID does not match
the hold's ID. That observation identifies a further missing proof, not permission
to skip the ownership check. The existing publication reference binds a publication;
it does not validate a repair authority, its ranges, aliases, or retained leg state.

## Candidate withdrawn after contract review

A candidate compared the hold with the aggregate lease and the commit with the
typed durable checkpoint. Synthetic tests passed, including a 0/1 case, but deeper
source inspection exposed the leg/lease conflation above. The entire candidate
consumer change and its positive synthetic fixture were removed. Test passes for
that candidate are retained as exploratory results, not accepted implementation
proof. The prior publication-evidence implementation is preserved.

The only retained test edit adds three negative cases: missing Coordinator ownership
on A's grant, B's grant, or B's retirement cannot establish `SourceSuperseded` even
when the synthetic route/commit/disposition evidence otherwise matches. Existing
positive, missing/wrong receipt, duplicate, poison, metadata and consumer-parity
checks remain intact. The final test passes on both PowerShell hosts.

## Bounded fix contract and verification sequence

1. Define the smallest versioned, Coordinator-authored **repair-owner evidence**
   operation/receipt that binds the already issued repair decision and authority
   to its Coordinator, session/transfer/direction, source publication, data session
   and exact retained-leg checkpoint. Keep active lease generation/identity and
   durable transition checkpoint separate. Do not extend runtime token authority,
   issue a second repair, or turn `NotApplicable` into checkpoint acceptance.
   The current generic `RepairReplay` envelope has no typed operation to reuse;
   adding raw owner stamps or picking the nearest boundary is insufficient.
2. Review and version the evidence catalog compatibility boundary explicitly.
   Preserve old profile/hash interpretations and historical records. Emit exact
   read-only references on the actual grant/settlement paths only from the
   Coordinator-authored binding, with no changes to dispatch, retry cadence,
   retirement, route publication, liveness or recovery decisions.
3. Require that validated binding in the consumer. Match the hold/request's retained
   leg checkpoint to retained-leg evidence; match commit/disposition durable
   generation, identity and fingerprint to the exact transition receipt. Validate
   active lease clearing separately. Reject wrong/absent owners, swapped checkpoint
   roles, stale receipts, checkpoint ABA, replacement, peer ambiguity and conflicts.
   Do not use an acceptance-specific continuity stamp as a generic owner label.
4. Run focused producer and consumer negatives, then rebuild only the joined fake-
   peer test and capture current evidence. Verify actual typed operations and exact
   references before full/compact/direct/indexed/shared GUI PS5/PS7 comparisons.
   Stop at the next missing proof predicate; no full matrix or live run is needed
   for this bounded evidence change.
5. Keep A `SettledStalled`, B's retirement unchanged and the obligation unresolved.
   `SourceSuperseded`, if eventually proven, remains diagnostic metadata under the
   existing policy; it does not grant recovery or completed-transfer acceptance.

## Final evidence and scope

`artifacts/checkpoint-role-binding-20260912/` contains the full run ledger.
`Inspect-CheckpointRoles.ps1` pins the capture and independently verifies the role
values, absent grant stamps, empty repair operations and absent continuity origins
under both hosts. `review-ps5.json` and `review-ps7.json` agree byte-for-byte.
`final-owner-negative.trx` passes; exploratory candidate runs are not final gates.

No producer/runtime change, portable rebuild, GUI scenario, NKN run, full report,
commit or push was performed in this review. Earlier dirty changes are preserved.
Live attempt count remains 12; retained-report count remains 6. The stalled episode
continues to count as `1/1/0/1`, with source disposition unproven.

## Implemented repair-owner evidence contract

`FileTransferCoordinator.CaptureRepairOwnerEvidence` validates the already-issued
receiver repair against the current repair decision and publication, then emits
one version-1 `fallback_frontier_repair_owner` operation with a
`repair_owner_evidence` receipt. It preserves the state object, snapshot generation,
runtime authority and checkpoint-acceptance origin. Rejected or unbound inputs
produce neither a snapshot nor a reference. No second repair decision is issued.

The immutable payload binds Coordinator/session/transfer/direction, publication,
runtime owner, data session, retained-leg checkpoint, active lease, repair ranges,
generation, predecessor and primary request/correlation aliases. The service stores
the reference on the repair contract and logs that exact issuance reference on its
grant and settlement. A later settlement reference does **not** assert current
authority. Both producer and analyzer exclude these receipts from physical effects.

The current catalog appends `-repair-owner-v1` to the prior
`...-source-callback-v1` profile. Its hash is
`02ecc01ad2fae32da2da0688930b6720a55d5a4833f59e986aeee90e521039d7`;
the parent retains
`0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd`.
All prior catalog definitions and hashes remain unchanged. The optional payload is
omitted when null, preserving old envelope shapes. The redactor explicitly admits
the new catalog and selected grant/settlement profile, not arbitrary profile text.

For the new profile, the consumer requires exact Coordinator/transition/operation/
receipt bindings for A's grant and settlement and B's grant and retirement, plus
the independently validated source publication. It matches the hold to the typed
retained-leg generation, the commit/disposition to the typed durable checkpoint
generation/identity/fingerprint, and the active lease to the aggregate scope.
Intervening checkpoint mutations/ABA, replacement, conflicting owners, missing or
swapped references remain fail-closed. No nearby snapshot or raw owner stamp grants
ownership. Historical profile behavior is not rewritten.

### Current capture and result

`artifacts/repair-owner-evidence-20260912/joined-repair-owner-01.log`, SHA-256
`2FF0096BC5BFEF8D6552D997B12748E3FB62A8CE37E1AA16DF3F95DAD531E13A`,
was captured after rebuilding the targeted fake-peer test with the current producer.
Two typed receipts bind all four actual grant/settlement references under PS5/PS7.
Hold 558 retains checkpoint 0; both repair scopes have active lease 0; the exact
commit/disposition durable checkpoint is 1. A settles stalled at 734; B is retired
at 810; Coordinator recovery publication is 826. This proves source replacement,
not repair progress or successful transfer completion.

Full/independently compact snapshots, direct/indexed frontier consumers and the
actual shared GUI helpers agree across both hosts. Strict routes and exact cleanup
pass. Focused negatives cover absent/swapped receipts, stale A references on B,
foreign peers, data/publication replacement, role swaps and checkpoint ABA.
The run ledger is `artifacts/repair-owner-evidence-20260912/RESULTS.md`.

The pre-commit review found two bounded issues: five embedded GUI test harnesses
did not load the new relative dependency from the repository, and repair-owner
correlation incorrectly assumed that the asynchronous snapshot drain precedes
the synchronous grant log. The harnesses now load the actual shared scripts and
their snapshot dependency without changing scenario assertions.

For the repair-owner profile, causal ordering uses the exact referenced
Coordinator state generations: hold-observed state < A < B <= commit-before
state. Each new repair decision advances Coordinator state; the evidence receipt
itself is read-only. The source-history/ABA check uses that same authored interval,
so delayed logging neither rejects a valid grant nor imports a pre-hold state
mutation. Exact receipt, publication, owner, retained-leg/data-session bindings,
raw repair lifecycle ordering and old-profile behavior remain unchanged. No
synchronous flush, new runtime authority or catalog revision was added.

Focused regression coverage includes delayed grant/settlement snapshot drains,
receipts outside the authored interval, pre-hold delayed history, checkpoint ABA,
and all eight previously failing GUI-harness cases. The verification ledger is
`artifacts/precommit-fixes-20260913/RESULTS.md`. Do not change the unresolved policy
or start a full/live acceptance run based on this diagnostic result. No commit,
push, portable package, live NKN/GUI run or retained full report was performed.
