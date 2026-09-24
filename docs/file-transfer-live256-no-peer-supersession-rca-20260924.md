# Live-256 proof failures: RCA and bounded fix plan

2026-09-24. Reviewed HEAD: `5f7a13dc047fa50da0702713db92b0903cb9e7c0`, branch `v0.7.0`.
The original RCA step below was planning only: no runtime/analyzer/test changes,
new test run, historical authority evaluation, package, live run, commit or push.
The subsequent bounded implementation is recorded separately at the end.

## Result and evidence boundary

The latest targeted campaign is
`artifacts/analyzer-fixes-live-256-20260924-5f7a13dc/live/20260924T163917Z`.
The 256 MiB GUI transfer and SHA check passed; acceptance remains **FAIL**.
Its single primary analysis exited 0 but recorded `proof_incomplete`, not acceptance.
There were no retries and no finalized selected attempt. Performance PASS had a
zero goodput floor and does not establish a 3.6 MB/s target.

All 135 files in the existing live seal were reverified. Seal SHA-256:
`554963fd17412fcaa285acfd63c840c6059d511b4fbc2d26a312cc49e836533b`.
The primary retained input remains 68,021 lines, SHA-256:
`da49a8b87ff02a4228a1f6dd472e2e3c42f7f9e1afd1ce97bceab55e1f048632`.

`artifacts/live-256-proof-rca-20260924/Extract-Evidence.ps1` captured 20 exact
diagnostic source lines, bounded at 64, including the two primary-selected
failure locations and literal subject history. It verifies snapshot payload
hashes but calls no analyzer, reducer or authority evaluator.
`RCA-EVIDENCE.json` SHA-256:
`1d0c1727b5c626f453f06776fe0d9b39b8905bb98c892edcab8e5956fe84c92a`.
The selected history is diagnostic context, not a substitute for the complete
failed primary proof. No historical result has been reclassified.

## 1. First failure: resend timing does not satisfy the existing contract

The primary first failure is `file_tuna_data_path_mismatch`, retained line 28711,
receiver writer `p36588rae69d3158f3d4234`, sequence 15018. Its variant is
`second_tuna_rejected_regular_ingress_exact_tuna_resend_missing`.
Primary accounting remains 62 distinct cases, 43 recovered and 19 unresolved.

The exact selected range is chunks 4155–4157, 64,512 bytes:

| Retained line | Literal observation |
| --- | --- |
| 17319 | Original regular-v4 send, epoch 0/publication 1, security sequence 1771. |
| 28336 / 28684 | Sender/receiver projections of the second Tuna-v4 transition, epoch 3/publication 4. |
| 28711 / 28712 / 28714 | Delayed regular-NKN receive, explicit mismatch and ignored-frame companion; `rejected_before_dispatch`, `authority_effect=none`. |
| 28955 | Exact-range Tuna-v4 send reports `sent`, but **after** the rejected receive. |
| 30832 / 38734 | Later fallback-v6 send/receive for that range, epoch 4/publication 5; receive reports `dispatch_entered`. |

Line 28713 is an interleaved snapshot, not the ignored-frame companion.
The original send and rejected receive have the same envelope/payload fingerprints
and security sequence. Those are local captured facts, not complete cross-peer
publication lineage or a new stale-safety/durability verdict.

The cause of this selected diagnostic is the existing predicate in
`Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult`
(`tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1`): for reactivation
from fallback, the exact Tuna send and receive must occur after the original
regular send and **before the rejected receive**. Line 28955 lies outside that
window. The later fallback receive and final SHA do not meet this earlier
obligation. This does not establish that the runtime never resent the range.

The receiver guard in
`NknSignalingTransport.FileTransferTransportChannel.cs`,
`ShouldRejectFileTunaV4PayloadReceiveWithAuthority`, deliberately logs and returns
before dispatch when a v4 payload arrives over regular NKN while the captured
publication requires Tuna. The selected evidence is consistent with that guard;
it does not show wrong-route admission or authority granted by a stale callback.

This is the already-reviewed strict contract, not a newly discovered missing
variant or formatter bug. The 2026-09-21 rejected-ingress contract review explicitly
keeps later fallback separate and parks the stronger stale-rejection classifier.
The existing second-Tuna diagnostic and generated Service/adapter tests preserve
that distinction. They were inspected, not rerun in this documentation step.

**Disposition:** retain the unresolved finding. Do not widen the temporal window,
waive exact receive proof, force runtime resend timing, or use final SHA to make
it pass. An alternative acceptance treatment requires a separate explicit
contract decision and exact coordinator-owned lineage/continuity evidence.
This RCA does not authorize that change and does not prove the other 18 cases.

## 2. Independent artifact blocker: no-peer supersession omitted from the matcher

The primary snapshot route report's first invalid boundary references transition
`fte1-105c766b26a9d9b53d984d5d`, writer `p25964r35fe8ce858254cd4`, sequence 30619.
It resolves to retained line **31036**, not snapshot ordinal 3217.
The decision is accepted `FallbackTailQuiescent`, state generation 362→363,
with only the `fallback_tail_yield` family changed.

Its four coordinator-authored operation descriptors are:

| Subject | State transition | Typed receipt stages |
| --- | --- | --- |
| Failed current, generation 2 | `YieldIssued` → `None` | `yield` |
| Quiescent successor, generation 3 | `None` → `YieldIssued` | `yield`, `failed_supersession` |
| Failed terminal, generation 2 | `None` → `Invalidated` | `yield`, `invalidation` |
| Earlier Quiescent terminal, generation 1 | `Invalidated` → `None` | Preserved historical yield/local/peer/paired-release/invalidation receipts |

The first three subjects have no peer proof. The successor explicitly records
`failedSupersessionCarriedPeerProof=false` and the exact Failed predecessor ID.
The displaced old terminal **does** retain its earlier peer proof and release;
those historical fields must not be cleared or treated as the successor's proof.
Its terminal birth appears at line 28323. The new Failed operation is born at
line 30741, generation 361→362, with no peer proof, then superseded at line 31036.

The producer supports this branch intentionally:

- `IsFailedFallbackTailYieldEligibleForQuiescentSupersession` accepts either
  `YieldIssued` or `PeerProofObserved`, while excluding local proof, paired release,
  active reservation and incompatible pending work.
- `ApplyFailedFallbackTailYieldQuiescentSupersession` creates a `YieldIssued`
  successor when no peer proof is carried and records the supersession binding.
- The snapshot projector exports the Failed predecessor, successor receipt and
  terminal invalidation without inventing a peer receipt.

The consumer's `Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape`
in `tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1` instead requires
`PeerProofObserved` for both current subjects, a true carried-peer flag and
2/3/3 receipt counts including peer receipts. Its first state filters therefore
exclude this no-peer 1/2/2-receipt shape. `Test-...FallbackTailQuiescent` then cannot
admit the four operations through its remaining one-/two-operation branches.
The diagnostic labels it `failed_to_quiescent_terminal_slot_rollover_rejected_as_extra_operation`.
That label is less precise than the actual missing no-peer supersession branch.

The existing history reducer already tracks Failed supersession origins born in
either state. However, candidate registration depends on the narrower shape
matcher, so this captured boundary never reaches those guarded history checks.
The downstream hash/generation/effect/stream failures are consequences to review
after the first catalog edge is corrected, not independent runtime defects to tune.

Coverage gap: `CreateFailedQuiescentTerminalRolloverSnapshotFixture` uses
`CreateCheckpointFailedYieldSource`, which always retains peer proof first.
The current inbound/outbound and prior-terminal cases therefore exercise only
the carried-peer branch; they do not cover this production no-peer shape.

## Bounded implementation plan

The next safe implementation slice is the **no-peer catalog branch only**.
It does not resolve or relax finding 1 and cannot promise a passing live-256 gate.

1. Add a real-Coordinator fixture path that omits the peer-proof observation,
   covering inbound/outbound with and without a prior terminal. Keep existing
   carried-peer fixtures unchanged. Assert prior-state/envelope immutability,
   exact predecessor/successor generations, terminal replacement, absent new
   proof/release/reservation, and the genuine 1/2/2 receipt shape. Do not obtain
   the positive case by editing serialized production snapshots.
2. Extend the existing shared shape matcher with two explicit, internally
   consistent modes. Preserve every carried-peer predicate. The no-peer mode
   requires `YieldIssued` on both current subjects, false carried-peer flag,
   absent peer identities/fingerprints and zero peer generation on the three
   new subjects, exact 1/2/2 receipt sets and exact receipt projection. The prior
   current projects its yield receipt; the successor projects supersession;
   the Failed terminal projects invalidation. Keep owner/pair/scope, positive
   generations, operation keys and complete boundary checks strict.
3. Reuse candidate registration, exact slot-history validation, displaced-terminal
   identity and original provenance, successor effect binding and forbidden
   displaced-terminal-effect checks. Do not create a second reducer or promote
   shape recognition directly to authority. The old terminal's proof remains
   historical; no new peer-proof origin may be registered for the no-peer successor.
4. Add focused negative controls: mixed states/flags, fabricated peer or local
   proof, extra/missing/mismatched receipt, wrong predecessor/generation/scope,
   reused terminal, missing prior birth, missing successor effect, and forged
   displaced-terminal effect. Preserve reordered/exact-duplicate behavior and
   existing carried-peer controls. Include later genuine peer observation on
   the new successor so absent initial proof is not confused with forbidden
   future proof. Any genuinely different producer branch stays out of scope.
5. Verify the new red→green regression first, then the directly affected
   coordinator snapshot and full/independently compact/GUI-classifier lanes on
   PowerShell 5.1 and 7. Preserve incomplete-route fixture findings instead of
   inventing missing route history. No unrelated aggregate suite or historical
   rerun is required. Report any normal-stack limitation separately.

No changes to runtime/Service/Infra authority, emitter, route policy, snapshot
catalog version/hash, throughput policy, queueing, retries, wallet or GUI timing
are proposed. If a focused reproduction exposes a different first edge, stop
and report it rather than widening this plan.

## Live-test boundary

Do not run a full matrix or another live-256 merely to seek a favorable ordering
under the unchanged resend predicate. Complete and verify the bounded catalog
fix offline, then review the independent acceptance-contract question. Any future
runtime validation still requires a fresh portable build and strict preflights;
the second coordinator-published Tuna route and all canonical proofs remain mandatory.

At the end of the original RCA step, only this document was newly added to the
working tree. The raw capture, primary outputs, prior seals and production/test
sources were unchanged at that point.

## Bounded implementation follow-up (2026-09-24)

Implemented the no-peer branch in the existing shared supersession matcher.
Both current subjects must agree on `YieldIssued` or `PeerProofObserved`; mixed
branches are rejected. The no-peer branch requires explicit Boolean false flags,
absent peer identity/fingerprint fields, zero peer generation and exact 1/2/2
receipt sets. Its prior current projects the yield receipt. Carried-peer matching,
terminal history, displaced-terminal identity, fresh successor effect binding and
forbidden displaced effects retain their existing requirements. No reducer or
runtime authority was added, and the old terminal's historical proof is preserved.

The positive fixtures are produced by real Coordinator calls for both directions,
with and without a prior terminal. They also observe a genuine peer proof later
on the no-peer successor, pair it and retire it without changing the original
false carried-proof binding. Prior snapshots and state fingerprints stay immutable.

Verification in `artifacts/no-peer-supersession-fix-20260924/results`:

- `red.trx`: all four new analyzer cases reproduced
  `No-peer production catalog rejected.` before the matcher changed.
- `green-attempt-2.trx`: all four new analyzer cases pass on PowerShell 5.1 and 7.
  Each fixture rejects 38 hostile shape controls. Full/independently compacted
  evidence and the shared GUI classifier agree; the production-width case also
  checks reordered/duplicate records, missing births/effects, forged displaced
  effects and terminal identity/state corruption.
- `affected-regressions.trx`: 17/17 pass: 12 Coordinator cases plus five existing
  carried-peer provenance/terminal-rollover analyzer cases, including both hosts.
- Final result: **21 focused tests pass**, with no stack-size override. No generic
  aggregate lane, portable build, historical authority evaluation or live run.

The first green attempt is retained: its Coordinator cases passed, while the new
test harness needed deterministic JSON property ordering and exact expectations
for its partial route history. Those fixture findings remain FAIL, including the
later `StartTransfer` publication lacking a route-owner handoff contract; they
were not relaxed in production or treated as complete live route proof.

`Verify-Fix.ps1` records the final source/test pins. Of the prior live seal's 135
pins, 134 remain unchanged; only the active analyzer source has the intended new
hash. The raw retained input, primary outputs, historical seals and extracted RCA
evidence remain unchanged. The RCA seal's five changed source/document pins are
the planned edits; its other 12 pins remain unchanged. Receiver catalog version
and hash, runtime/Service/Infra code and the resend acceptance predicate are unchanged.

The historical live verdict remains **FAIL**, with its independent 19 unresolved
resend cases untouched. This offline fix does not reclassify that run or establish
current live acceptance. Next: review the independent pre-rejection resend contract
before authorizing any different acceptance treatment or another targeted live run.
No commit or push was made in this implementation step.
