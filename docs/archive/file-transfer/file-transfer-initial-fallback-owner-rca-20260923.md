# Initial fallback-v6 owner association: RCA and bounded fix contract

2026-09-23. Base HEAD `9e74e8181894c67f6aa8cf5db8cc2402702dbc93`, branch `v0.7.0`.
Status: RCA reproduced; production/analyzer implementation unchanged.

## Finding

The first selected route-consistency failure is an **analyzer contract omission**,
not evidence that Core failed to create the initial runtime owner. The initial
publication resolver recognizes epoch-zero Regular-v4 and prepared Tuna-v4,
but not epoch-zero `post_tuna_fallback_v6` / protocol 6. Consequently a legitimate
Core owner is excluded from its proof map and the route consumer reports that
the publication has no unique current owner.

This explains only the selected initial-owner finding. It does not explain away
the missing second-transfer Tuna-v4 publication or prove the entire live scenario
correct. The original live verdict remains FAIL/correctness/incomplete_proof.

## Current primary evidence

Source campaign:
`artifacts/recent-progress-supersession-live-20260923-9e74e818/live/20260923T173432Z/second-transfer-after-reactivation`.

The completed primary's stored route-consistency result selects boundary
`fte1-13f39cbe61c36e18491b28b2`, writer `p30756r31869de2c9e34e0c`, sequence
17471, physical retained-input line 30637. Full/compact route parity is pass.
The existing taxonomy name contains `analysis_parity_invalid`, but parity did
not fail; its reason is `snapshot_authoritative_route_consistency_invalid`.

Read-only source context is bounded to lines 30606–30669 (64 lines). Decoding
uses the actual base64url format, not ordinary padded base64. No historical
snapshot evaluator, reducer, correlator, or primary analysis was rerun.

| Retained line | Inbound coordinator evidence |
| --- | --- |
| 30634 | Accepted `PinFallbackBootstrapOffer`, aggregate generation 0 → 1, no runtime owner yet. |
| 30635 | Accepted `StartTransfer`, 1 → 2, one applied `RuntimeOwner` birth (`None` → `Current`), runtime 1 / leg 1. The additional attempted-operation descriptor is not applied authority. |
| 30637 | Accepted `RoutePublication`, 2 → 3, publication 1 / runtime 1 / leg 1, fallback-v6 protocol 6, live epoch 0, commit 0, terminal 0. Its before-state fingerprint equals the preceding owner's after-state fingerprint. |
| 30639 | Matching `route_publication_forwarded` effect, writer sequence 17473, bound to the same publication operation, receipt, boundary and generation. |

The same pattern exists outbound at lines 30622/30623/30625/30627. The initial
fallback choice is already present before owner birth; no observed readiness,
ACK, route cache, or transport dictionary is being promoted to owner authority.

## Source-level causal chain

1. `FileTransferCoordinator.cs:24915` advances the runtime generation and
   `:24931` issues `CurrentRuntimeOwner` in `StartTransfer`. The actual fixture
   verifies the owner remains current through the accepted publication.
2. `FileTransferCoordinator.Snapshots.cs:5896` emits the owner descriptor from
   coordinator state; `:5956` emits the accepted publication descriptor.
3. `CoordinatorSnapshotEvidence.ps1:7373` restricts `$initialRoute` to Regular-v4
   or first prepared Tuna-v4, both protocol 4. The birth check at `:7385` poisons
   an otherwise complete initial fallback stream solely because this route law
   is excluded.
4. The first epoch-zero publication at `:15759` replaces same-boundary owner
   candidates with this exact initial-publication map. The absent entry leads
   to the missing-current-owner finding at `:15830`.
5. The shared resolver is also used by the aggregate evaluator at `:12519`;
   `:12679` reports missing epoch projection when no initial proof exists. The
   fresh initial-only fixture exposes that second consequence. Do not assert
   it was the live primary's generic first failure: that field was `(none)`.

## Deterministic reproduction

Added `FileTransferInitialFallbackRouteEvidenceTests` and
`Support/InitialFallbackRouteSnapshotEvidence.ps1`. Four characterization cases
pass: inbound/outbound, with runtime/leg generations 1/1 and independently 7/13.
Every case creates a new Core-pinned offer, actual `StartTransfer`, actual
accepted/current `RoutePublicationToken`, and production-emitted snapshots.
Forwarding effects are modeled through the production emitter, not exercised
through a real service callback or network delivery.

Each case runs independently under Windows PowerShell and PowerShell 7. Both
full and compact consumers agree, and normalized cross-host outputs match:
artifact evidence pass, exact initial proof count 0, route consistency fail,
missing-owner finding, and initial epoch-projection failure. These are passing
**reproduction assertions**, not passing route acceptance.

Artifacts: `artifacts/initial-fallback-owner-rca-20260923/initial-fallback-baseline.trx`
and four fresh per-case capture directories (eight host output files).

The first build was blocked by Avalonia's telemetry log write outside the sandbox;
no tests ran in that attempt. Retrying with command-line `-p:UsedAvaloniaProducts=`
disabled only the package's conditional stats target and allowed the focused
tests to run in the normal sandbox. No project setting or runtime code changed.

## Bounded implementation contract

Extend only `Get-FileTransferCoordinatorSnapshotInitialPublications` to recognize
the **first** fallback-v6/protocol-6 publication at epoch zero, using the same
complete coordinator-owned proof as the existing initial routes:

- Exact coordinator/writer/session/transfer/direction scope and complete,
  contiguous aggregate generation, fingerprint and boundary history.
- One accepted `StartTransfer` and one applied `RuntimeOwner` birth, exact
  owner identity/token fingerprint/lifetime. Runtime and leg generations remain
  independent of publication generation; do not equate them.
- Exact legal route/protocol pair; zero live epoch, route-commit generation and
  terminal generation. No route change or intervening owner replacement.
- One accepted initial `RoutePublication`, publication generation 1, validated
  receipt, matching operation/token fingerprint and exactly one bound forwarded
  effect. Missing, duplicate, rejected, stale or mismatched evidence fails closed.
- Preserve all existing regular-route and prepared-Tuna behavior. Do not grant
  same-owner fallback republication, later-epoch fallback admission, or a new
  fallback-to-Tuna transition based on this initial-owner association.

The intended production diff is confined to the route/protocol eligibility
predicate and its explanatory comment. No new catalog profile, owner token,
runtime fallback, timer, retry, queue, transport or parent verdict override is
needed. Keep the complete proof checks intact; a bare fallback-route allowlist
outside this resolver would be insufficient.

Replace the characterization rejection assertions with positive contract
coverage during implementation. Required negative controls cover missing or
duplicate owner birth, stale/replaced owner, rejected birth/publication, scope
mixing, generation/hash gaps, route/protocol mismatch (including Tuna-v6),
nonzero/missing/negative epoch, commit/terminal changes, runtime/leg mutation,
missing/duplicate/mismatched effects or receipts, and readiness/diagnostic-only
evidence. A second same-owner fallback publication must still fail. Existing
regular-to-fallback mutated-evidence controls remain negative; they are not
legitimate initial fallback streams.

Run the focused new cases first, then the directly affected initial Regular,
initial Tuna and snapshot route-consistency cases. Verify full/compact and GUI
consumer agreement on fresh fixture evidence. At most one broader contract lane
is needed after stabilization; no unrelated aggregate suites now.

## Independent blockers and acceptance boundary

Later same-route fallback publications are a separate contract. The current
successor validator (`CoordinatorSnapshotEvidence.ps1:15297`) requires an exact
prior committed Tuna-to-fallback transition and retained bootstrap proof. An
initial fallback transfer has no such prior transition. Do not remove that
requirement as part of this fix or assume that adding the initial proof makes
later publication findings disappear. Inspect the next primary-selected edge
or reproduce the specific successor path separately before extending it.

The second transfer must still publish coordinator-authorized `file_tuna_v4`.
An existing 16 MiB destination file, fallback completion or the first transfer's
SHA cannot satisfy that requirement. The recent-progress supersession case was
not exercised by the live run and remains offline-validated only.

The parent selection rejection is expected for this run: the direct manifest
has finalized artifacts, valid receipt, clean source, and passing cursor/writer
integrity, but GUI exit code 1. `Run-FileTransferTunaGuiSmoke.ps1:2453` explicitly
requires exit 0 for `currentRuntimeProof`; the parent at
`Run-FileTransferRouteAcceptance.ps1:9636` therefore reports
`current_runtime_proof_missing`. Do not weaken that acceptance gate or describe
it as receipt corruption.

No live rerun, runtime/analyzer fix, commit, or push was performed in this RCA
step. Implement and verify the bounded initial-owner fix next; rebuild before
the next targeted GUI test. The full matrix remains gated.
