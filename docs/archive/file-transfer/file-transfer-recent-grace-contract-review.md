# Recent-progress publication contract review

2026-09-15. Reviewed at HEAD
`2691fc70f98d668bae943ebd41c6f8540156ec57`.

## Decision

**Do not approve delayed-publication recovery under the existing contract.**
Neither extending its deadline nor importing a stall reducer's `Recovered`
flag preserves the required proof. The smallest implementation-ready change is
the **same-deadline recent-wake rearm chain**, restricted to chains whose writes
and matching receiver publication already meet the existing stall deadline.

This separates an incorrect wake-order restriction from a genuinely unmet
publication predicate. It does not claim that the latest live obligation, or
the second unresolved obligation, will recover after the wake-chain correction.

The review covers the current source and the original September 15 capture
identified in `file-transfer-recent-grace-ordering-rca.md`. No primary evaluator
was rerun, no historical evidence was rewritten, and no runtime/analyzer behavior
changed during this review.

## Findings

### 1. Stall recovery is not a publication certificate

`tools/FileTransferSoak/ArtifactWriters.ps1:59138` admits a pre-deadline sparse
commit as natural stall progress. Its `RecoveryEvent` can therefore be
`filetransfer_v6_sparse_write_committed`, with no subsequent receiver-state
publication. The recent-progress helper explicitly requires publication
(`tools/FileTransferFrontierGraceHandoff.ps1:1496`). Their boolean results do not
have interchangeable meanings.

A read-only probe using the existing fresh-service-receipt test fixture confirms:

| Fixture | Stall recovered | Recent recovered | Stall recovery witness |
| --- | --- | --- | --- |
| On-time writes and publication | Yes | Yes | Sparse commit |
| On-time writes, late publication | Yes | No | Sparse commit |
| On-time writes, publication absent | Yes | No | Sparse commit |

Reject any contract that closes recent progress using an aggregate stall count,
`Recovered=true`, or merely the identity of a recovered child stall. Even an
exact child identity would not supply the missing publication proof. This is a
semantic mismatch, not evidence that the existing stall contract must be changed.

### 2. The observed delay includes intentional coalescing

In the immutable GUI writer ledger for run `20260915T121111Z`, receiver writer
`p56952rc951ad05e4b347ba` records:

- Ledger line 41250 / sequence 15958: `filetransfer_v6_receiver_state_coalesced`,
  frontier 4995, committed delta 3, last published/selected frontier 4992.
- Ledger line 41790 / sequence 15965: the same diagnostic at frontier 4998,
  committed delta 6, last published/selected frontier 4992.
- Ledger line 42774 / sequence 16082: receiver state 97 publishes frontier 5001
  at 12:13:59.1481682Z, after the stall deadline 12:13:58.9011858Z.

The runtime's ordinary state policy is separate from its grace policy:
`SessionFileTransferService.cs:444` defines a 750 ms state retry interval;
lines 451-452 define a 16-chunk force threshold and 500 ms progress force interval.
`ShouldForceInboundV6ReceiverStateAfterProgress` chooses whether to force;
`SendInboundV6ReceiverStateAsync` separately applies ordinary/forced coalescing.
The exact repair-progress receipt path bypasses the ordinary forced-coalescing
check, but a natural write is not automatically such a receipt.

Thus the capture is not proof of a missing timer or solely a blocked send.
Do not tune these constants, force ordinary traffic, or create another timer to
make the analyzer's deadline pass. Such changes would alter unrelated traffic
policy and would still not establish exact late-publication ownership.

### 3. Rolling recent-progress deadlines are not a bound for the old obligation

The first advancing write creates recent wake 179 with a new frontier 4995 and
deadline 12:13:59.3795252Z. Later writes create wakes 180 and 184 with later
frontiers and deadlines. These are different progress episodes, not extensions
of stall wake 258's 12:13:58.9011858Z deadline.

Taking the latest such deadline would allow continuing traffic to slide an old
unpublished obligation indefinitely. Even freezing the first new deadline would
introduce a new recovery rule; it is not implied by the existing one-second grace.
Neither rule is approved here.

### 4. Existing authority bindings have narrower roles

- `InboundFallbackFrontierProgressReceipt` is tied to an exact current repair
  contract, authority, request alias, transport and data session. Validation in
  `SessionFileTransferService.FallbackFrontierAuthority.cs:1400` rejects stale or
  unrelated receipts. It cannot be manufactured from a natural sparse write or
  reused for a different frontier progression.
- `Resolve-FileTransferPublicationEvidence` in
  `tools/FileTransferPublicationEvidence.ps1` binds an event to an already
  validated Coordinator route-publication token and its exact observed state.
  Route ownership proof alone does not prove that a particular receiver-state
  frame published a particular committed-write chain.
- `SendInboundV6ReceiverStateAsync` creates a state frame under the service gate,
  then performs an asynchronous send. Its `receiver_state_sent` record describes
  that captured frame. Joining a delayed record must not infer the original
  write/source identity from whichever owner happens to be current afterward.

No new authority evaluation, nearest-timestamp binding, route-cache inference,
old repair-receipt reuse or stale-callback action is an acceptable substitute.

## Implementation-ready bounded contract: rearm chain only

Target the arm-to-current-recheck join in
`Get-FileTransferRecentProgressGraceStallProgressClosure`.

1. Keep existing episode admission and the existing single-arm success path.
   Do not change candidate ownership, raw/distinct counts or which repair
   progress receipt owns the episode.
2. For a later-rearmed current wake, select the latest pre-diagnostic arm in
   that same episode before validating its result. Do not skip a contradictory
   latest arm to borrow an earlier convenient one. Require at least one such
   arm; a callback alone never opens or closes an obligation.
3. Validate every intervening recent-arm link through the current recheck.
   Writer/session/transfer, direction, route/protocol, live/runtime/terminal/leg,
   publication/commit, transport/bridge/checkpoint and data-session scope must
   remain exact. Frontier, committed-progress timestamp and original recent
   deadline must remain unchanged. Each new arm must precede the original
   deadline, increment the **recent** generation by one and strictly advance
   highest received. The final arm must match the current callback exactly.
4. Reject missing links, duplicate-generation contradictions, identity changes,
   backward clocks, moved deadlines, generation/highest regression, pause,
   terminal, source replacement and ambiguity. Preserve same-writer conflicting
   copies before filtering by session/transfer so a changed identity cannot hide
   a contradiction. Identical copies remain harmless.
5. Only a current `rechecked/stall_grace_armed` callback may link to the exact
   resulting stall arm. Preserve the existing cross-family distinction:
   recent and stall generation numbers need not match and never imply ownership.
   Old `stale/wake_generation_replaced` callbacks remain diagnostic-only and are
   not required as positive closure authority.
6. Leave the rest of the natural-progress closure unchanged: exact contiguous
   write accounting, monotone highest/clock, increasing unpaused state epoch,
   exact frontier/highest publication, source/terminal/pause rejection, and
   **writes and publication strictly before the original stall deadline**.
   Do not import a stall reducer result as recovery evidence.
7. Keep the change in the shared helper plus directly affected fixture/tests.
   Change indexed support selection only if a focused partition-parity test
   demonstrates a missing required record. No runtime, Coordinator policy,
   protocol, grace duration, scheduling, queue or throughput changes.

Acceptance: the current `rearm_only` characterization becomes the failing
implementation regression and then passes. `late_only`, `live_ordering` and
`no_publication` remain unresolved. Add the link/scope/clock/conflict negatives
above, including a same-valued stall generation and a missing recent link.
Verify flat/indexed, full/compact GUI and narrow/multi-frontier partition parity
on PowerShell 5.1 and 7 with fresh service-issued authority fixtures. Preserve
the original live verdict; do not replay its primary evaluator to claim closure.

## Separate delayed-publication work, not approved implementation

Before admitting late publication, a separate design must specify an exact
Coordinator-bound committed-progress disposition and its receiver-state
publication binding. It must answer all of the following, with a real-receipt
positive fixture and rejection controls before implementation:

- Which existing Coordinator-issued token/validated receipt owns the original
  obligation, accepted write, captured state frame and final publication?
- How are the original deadline, pre-deadline progress and a finite non-sliding
  publication bound represented without changing runtime grace or send policy?
- What is the first publication subject, and how are intervening writes, state
  coalescing, source ABA, terminal/pause and asynchronous owner changes handled?
- What distinct disposition is reported when publication is absent, late beyond
  the new bound, rejected or bound to another source? None may silently become
  `Recovered`, `Superseded`, or a new runtime authority.
- How does one primary evaluation retain and consume the exact source/receipt
  witnesses, with full/compact parity and no backfill or second authority pass?

This is a genuine contract expansion, not part of the rearm-only correction.
Do not start another live run after only the rearm change: the known publication
gap still requires its own design and focused offline validation.

## Verification for this review

Read current runtime, shared reducers, indexed merge/orchestration and publication
evidence resolver. Inspected only bounded records from the latest raw ledger.
The existing 16-case ordering probe passed on PowerShell 7, followed by the three
read-only composition counterexamples above. Their input was
`artifacts/recent-grace-ordering-20260915/stable-test.log`, generated by the fresh
service unit fixture, not the historical live capture. No new runtime tests,
aggregate suite, portable build, live test, commit or push was performed.

## Implementation follow-up, 2026-09-15

Implemented the rearm-only contract in the shared
`Get-FileTransferRecentProgressGraceStallProgressClosure` helper. The existing
single-arm path remains available when there are no later arms; once later arms
exist, an old-generation callback cannot bypass complete chain validation.
Conflicting copies are checked before session/transfer filtering, but conflicts
belonging entirely to another transfer do not poison this episode. No indexed
support-selection change was needed.

The original regression failed at `rearm_only` on both PowerShell hosts before
the implementation. The final fixture passes 69 cases per host on PowerShell
5.1 and 7, checking flat/indexed, serialized full/compact, actual full/compact
GUI parsing, and narrow/multi-frontier partition boundaries. Seven existing
handoff tests also passed; after the final duplicate-scope adjustment only the
affected ordering tests were rerun. `late_only`, `live_ordering` and absent
publication remain unresolved. Verification history and exact source hashes are
recorded in `artifacts/recent-grace-rearm-20260915/RESULTS.md`.

All three original live evidence slice hashes still match the RCA. No primary
evaluator replay, historical verdict change, runtime/policy edit, portable
rebuild, live test, commit or push was performed. The next step remains the
separate delayed-publication contract design above, not another live gate.

The follow-up proposal is now recorded in
`file-transfer-delayed-publication-contract.md`. It recommends evidence-only
commit/frame/completion receipt capture first, with a proposed fixed 1000 ms
observation window anchored to the first pre-deadline commit. That is a new
diagnostic contract, not an extension of existing runtime grace or approval to
count late publication as recovered. A fresh real-receipt positive fixture and
source/lifecycle rejection controls are still required before consumer adoption.
