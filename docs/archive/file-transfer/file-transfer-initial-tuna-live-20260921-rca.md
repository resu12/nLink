# Initial-Tuna live validation: evidence boundary and next fix plan

2026-09-21. Fresh portable from HEAD
`bdfd45b964956867b02f3f00ae8b12981a8a270a` plus the accumulated working tree.
No runtime code changed during this live checkpoint.

## Outcome

The single `active-tuna-v4-64mb` run failed. Freshness, global bridge readiness,
scenario isolation, and the new paired read-only preparation gate passed.
The two peers reported the same authenticated preparation episode, then emitted
initial Tuna-v4 route-publication records. That does not override the primary
snapshot-consistency rejection or prove payload completion.

Source artifacts: `artifacts/session-preparation-live-20260921/RESULTS.md` and
`live/20260921T115012Z/active-tuna-v4-64mb` beneath that checkpoint.
Run ID: `567908ad-73d7-41e8-9c3b-bcd86da6b711`.

## A. Primary-selected snapshot boundary

The existing GUI failure classifier completed once and selected
`filetransfer_route_consistency_analysis_parity_invalid` /
`snapshot_authoritative_route_boundary_invalid`, reason
`snapshot_authoritative_route_consistency_invalid`.

Its first selected boundary is `fte1-341723b2112b58a13b1eca8d`, hash
`f61c1c90dccaa5b8c1fb6aa7d1adee544253a39e1b270c7d1a3dff9de42a5bdf`,
writer `p28684r65206da55d954e37`, writer sequence 299. It is retained at physical
line 317 of `filetransfer-gui-semantic-evidence-slice.log`; the next line is
the inbound route-publication event. The original 1/1/0/1 counts are preserved.

Read-only decoding of that stored snapshot shows an accepted inbound initial
`RoutePublication`, state generation 2 -> 3, route `file_tuna_v4`, protocol 4,
live route epoch 0, runtime generation 1, leg generation 1, publication
generation 1 and route-commit generation 0. No new authority evaluator was run.

Source inspection identifies a concrete contract mismatch to reproduce:

- `CoordinatorSnapshotEvidence.ps1:15598` routes the first epoch-zero publication
  through `Get-FileTransferCoordinatorSnapshotInitialRegularPublications` to
  associate the pre-existing runtime owner.
- That helper (`:7255`, route predicate `:7304`) permits only
  `regular_nkn_v4_fast`, with a complete generation/hash chain, exact accepted
  `StartTransfer` owner birth and matching publication effects.
- An initial prepared `file_tuna_v4` owner cannot enter that regular-only map.
  The caller then requires exactly one owner and reports the missing-owner
  finding at `:15672`.

This source-level mismatch is consistent with the primary's selected boundary.
The classifier does not export its individual predicate finding in its response,
so establish the exact rejection with a focused deterministic reproduction
before changing it. Do not treat raw route logs, preparation readiness or a
transport cache as substitute owner authority.

## B. Separate observed no-progress edge

The GUI timed out after 180 seconds without progress plus the unchanged
30-second terminal grace. In the retained semantic slice:

- Lines 295 and 318 contain outbound/inbound initial Tuna-v4 publication logs.
- Line 358 records the manifest send over the regular control lane; line 377
  records manifest receipt. Both file runtimes started.
- No `filetransfer_v4_state_sent` or `filetransfer_v4_state_received` record
  exists in the 5048-line semantic input.
- The final sender pump record (writer sequence 2180) still has zero available
  credit, zero accepted chunks and zero raw bytes sent. A zero-byte receive part
  was observed before normal test cleanup.

The next missing observable edge is manifest receipt -> initial V4 state/credit
delivery. Its exact runtime cause is NOT established by the snapshot finding.
Do not tune throughput, queue depth, chunks, retries, or timeouts; do not weaken
feedback/publication guards to make data move.

## Bounded fix and verification sequence

1. Add an exact initial prepared-Tuna owner/publication regression using actual
   Core-issued snapshots and effects. Reproduce the primary-selected owner
   association failure without reevaluating the historical artifact's verdict.
2. Extend initial-owner association only for the legitimate prepared Tuna-v4
   contract. Preserve complete-chain continuity, exact owner identity/lifetime,
   receipt/effect matching, generation checks and all regular-route controls.
   Reject incomplete chains, wrong/stale owners, missing effects, protocol 6,
   later route commits and preparation-only evidence.
3. Independently extend the preparation/admission integration test past offer
   admission to acceptance, manifest receipt, initial state/credit and at least
   one payload chunk. Identify the first rejected/absent Core-issued operation
   before selecting a runtime fix; existing readiness/admission tests alone
   do not prove this feedback path.
4. Run only new focused tests and directly affected Core/Infra/analyzer lanes.
   Prepare a reviewed clean checkpoint, rebuild the portable, then repeat only
   this targeted case with strict preflights. Only advance payer/direction or
   full-matrix coverage after the correctness edge passes or advances.

## Acceptance limits

The parent reports FAIL, with 9 correctness entries and one missing-goodput
performance failure. Source provenance and manifest validation fail because the
working tree is dirty and a selected finalized scenario attempt is missing.
Freshness and passing network preflights do not erase those failures. Retained
analysis was not started after the wrapper failed; no secondary authority
evaluation, forced manifest repair or verdict replacement was performed.
The earlier six-scenario matrix and separate live-256/regular-terminal gaps
remain unchanged. No further GUI run or production fix was attempted here.

## Deterministic follow-up: initial-owner analyzer fix

The next checkpoint reproduced both `filetransfer_live_route_epoch_projection_missing`
and `snapshot route publication has no unique current runtime owner` using actual
Core-issued preparation admission, `StartTransfer`, `StartTransferLeg`, and
`RoutePublication` snapshots, projected by the production emitter. All four
inbound/outbound and independent-generation cases failed before the fix.
No historical live artifact was reevaluated.

`Get-FileTransferCoordinatorSnapshotInitialPublications` now admits the first
epoch-zero `file_tuna_v4` publication through the same complete-chain, exact-owner,
validated-receipt and forwarded-effect checks used for initial Regular. It does
not authorize same-owner Tuna republication, later route commits, protocol 6,
missing source history or preparation observations alone. The existing Regular
republication path remains unchanged. Six new analyzer cases include two real
same-owner Tuna republication rejection controls; each of the four initial-route
positives additionally exercises 28 invalid-evidence mutations.

Verification and commands: `artifacts/initial-tuna-owner-fix-20260921/RESULTS.md`.
The historical live verdict remains FAIL; this fixes analyzer behavior, not
payload transport or dirty-source provenance.

## Deterministic follow-up: first missing runtime operation

Four joined-service diagnostic cases now reach actual accept/manifest receipt
after the public preparation request, for both payer roles and send directions.
Only physical Tuna delivery is faked; the authenticated negotiation, services,
Coordinator admission/publication, and manifest processing are real. The fake
lane is linked only when the diagnostic explicitly requests it.

At the receiver, the publication is current `file_tuna_v4`, protocol 4, epoch 0,
publication generation 1, with no route-commit token. Bootstrap correlation is
negotiated and enabled. The data session supports physical submission and has a
positive provider generation; the source identity and current leg exist, and
neither a transport/user pause nor a source hold blocks the operation.

`TryRegisterInboundBootstrapFeedbackLocked` nevertheless returns false and
issues no feedback operation because it requires `CurrentRouteCommit`. This is
the reproduced first missing runtime operation. The new diagnostic asserts this
rejection explicitly; a green diagnostic is NOT successful initial credit or
payload delivery. It must become a positive credit/payload regression when the
source-binding contract is implemented.

The same commit-only assumption also appears in:

- inbound payload source capture (`SessionFileTransferService.BootstrapWriteTracking.cs`);
- Core source validation (`FileTransferBootstrapInitiation.cs`);
- outbound source-callback issuance (`FileTransferBootstrapSourceCallback.cs`);
- source-hold capture and the adapter's local-stop observation capture.

These additional sites are source-inspection findings, not separately executed
live failures. Skipping feedback tracking, fabricating a commit, or disabling
bootstrap correlation would conceal the mismatch and lose stale-callback/drain
protection. No runtime guard was changed in this checkpoint.

### Next bounded contract and implementation

1. Define one Core-owned current-Tuna source binding that distinguishes an
   admitted initial publication (epoch/commit zero, no commit token) from a
   transition-committed publication. Require the exact current owner, leg,
   publication, session/transfer/direction and data-session lifetime. Keep all
   existing committed-route checks; do not make zero values generally valid.
2. Use that binding for feedback, writes and outbound callbacks, with exact
   physical-submission/finalization and invalidation guards intact. Review its
   local-stop/source-hold and wire descriptor consumers as part of the same
   source-lifetime contract; observations still require Coordinator validation.
3. Reject missing/stale/copied or mismatched ownership, revoked preparation,
   replaced sessions/providers, pause/hold races, terminal callbacks and invalid
   V6/route tuples. Do not create a commit or route epoch merely to satisfy a
   downstream predicate.
4. Convert the four diagnostic cases to prove initial credit and at least one
   payload chunk, then cover cancellation/source retirement and the directly
   affected committed-Tuna bootstrap lane. Keep this separate from throughput.
5. Only after those tests and a reviewed clean checkpoint: fresh portable,
   strict preflights, and the single targeted 64 MiB GUI case. No full matrix or
   live-256 run is due at the current blocked-credit boundary.

## Initial-source binding implementation checkpoint

The Core source binding is now implemented in
`CaptureFallbackBootstrapSourceScope`. It projects the existing source-scope
type from a current Core owner/leg/publication and enabled authenticated
bootstrap agreement. It does not mint a commit or consult readiness/route caches.
The no-commit branch requires the exact first `RoutePublication`, epoch zero,
no current/historical route commit or live epoch, and terminal generation zero.
It rejects republication, incomplete current ownership and route/protocol drift.
The committed branch still requires the exact current commit/publication pair.

Feedback registration, inbound write capture, outbound callback issuance and
the existing committed-source-hold construction use this one projection. Their
data-session identity/generation, physical provider, stream/layout, pause, hold,
exact operation membership and physical-finalization guards are retained.
Existing source validation compares the full projected scope; a captured scope
is not authority for a successor session or publication.

The four former diagnostic tests were converted to positive tests. They failed
before the fix at feedback registration, and now observe initial V4 credit and
the actual four-byte payload at the destination for both payer roles and send
directions. Two additional joined tests reset the receiver while feedback is
admitted but not submitted, then prove that its late completion sends no state,
writes no payload and does not revive the retired admission. Eleven focused
Core cases cover source/callback invalidation, including independent generations
and 34 embedded invalid-state/scope controls per source positive. Two analyzer
cases verify current and terminal-stale initial callbacks with independent
consumers under Windows PowerShell and PowerShell 7; no analyzer change was
needed for these callbacks.

The directly affected regression selection contains 125 cases. One combined run
was stopped after 120 passes and no further progress; the remaining joined lane
was rerun with a 60-second per-test hang limit and all seven cases passed. An
explicit discovered-test/result comparison confirms 125/125 distinct selected
cases covered, with none missing. The combined run remains recorded as aborted,
not green. Including 19 new/converted cases, distinct passing coverage is 144.
See `artifacts/initial-tuna-source-binding-20260921/RESULTS.md` for commands and
intermediate outcomes.

### Boundaries deliberately unchanged

- This resolves steady initial-Tuna credit/payload source binding, not the
  separate initial-Tuna-to-fallback exchange contract. `CanStartFallbackBootstrapExchange`
  still requires an accepted transition-committed source, and adapter local-stop
  capture remains commit-only. The new data binding cannot authorize that exchange.
- No wire format, snapshot catalog, route-commit/epoch allocation, timeout,
  goodput, queue, chunk-size or retry policy changed.
- The four-byte deterministic payload proof is not 64 MiB live correctness,
  throughput, route-cycle or live-256 acceptance. Existing live artifacts stay FAIL.
- No portable build, live NKN/GUI run, commit or push was performed here. The
  accumulated dirty working tree still needs pre-commit review and a clean
  checkpoint before strict targeted live acceptance.

Next: review the accumulated change set and checkpoint it, rebuild the portable,
then repeat only `active-tuna-v4-64mb` with strict preflights. Stop at the first
new proof edge; do not advance to the full matrix or live-256 on unit evidence.
