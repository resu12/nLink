# Generic dispatch range exclusion: test-only proof gap

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus existing uncommitted work.
Continues the bounded next step identified in
`file-transfer-tuna-payload-pairs-20260925.md`.

## Cause and reviewed rule

The genuine second-Tuna payload at chunks 20-21 produced a generic
`filetransfer_data_frame_dispatched` record. The selected rejected Regular
payload covers chunks 12-13. The test-only dispatch-window query could exclude
known different frame types or usable different scopes, but not disjoint ranges.
It therefore returned `dispatch_unbound_generic_observation` even though this
record cannot describe dispatch of the selected range.

The producer at `NknSignalingTransport.FileTransferTransportChannel.cs:9498`
logs the decoded frame's range. `GetFileTransferDataFrameChunkIndex` at line
10004 uses inclusive endpoints: start through start + segment count - 1.
The v4 codec validates the segment/count agreement and range; protocol limits
are 1-3 chunks, with end-exclusive at most 1,500,000. The new focused C# test pins
these constants so a protocol change cannot silently widen this diagnostic rule.

The added predicate applies only to the exact generic event and exact
`filetransfer.chunk_batch.v4` frame on both records. Both ranges must use
canonical unsigned ASCII decimal endpoints, be within the codec bounds and
strictly disjoint. The selected payload must also pass the existing complete
range/count/byte consistency check. Missing, malformed, reversed, overlapping,
duplicate, out-of-bounds or enriched generic records cannot use this new branch.
Existing independently sufficient known-other-frame/explicit-other-scope
exclusions remain unchanged.

A canonical disjoint range is sufficient negative identity evidence even when
scope identifiers are redacted or missing: it cannot be the selected v4 range.
It does not attribute this observation to a nearby authenticated receive,
envelope, route or publication. Authenticated receive/peer-visible events still
require their own binding. No absence of unlogged effects, runtime retirement,
security lifetime or general stale-packet safety is inferred.

## Verified result

The fresh positive two-pair capture now passes `recorded_rejections`,
`candidate_coverage`, `first_tuna_payload` and `second_tuna_payload` in the
separate test-only alternative shadow. Its dispatch window reports
`no_matching_bound_dispatch_observed_in_capture`, with zero matching dispatches.
Strict primary recovery remains `missing_tuna_resend`, `Recovered=false`.
`SecurityLifetimeProven`, `RuntimeRetirementProven` and
`GeneralStaleSafetyCertified` remain false.

Overall verdict remains **unproven**, with first missing edge
`owned_scenario_gate_results_unavailable`. Scenario scope, full route cycle,
terminal integrity and remaining failures are not supplied by this exclusion.
With the second receive omitted, that payload gate remains unproven with
`tuna_pair_receive_missing`; the first payload and bounded rejection still pass.

Verification: **5 focused test cases passed, 0 failed**. Two detached cases run
75 controls each on PowerShell 5.1 and 7. Three original-custody integration
cases each exercise both hosts; the dispatch integration passes 13 positive
and 54 negative structural controls per host, and the positive pair integration
retains its 17 detached negative controls per host.

Accepted receipts:

- `artifacts/dispatch-range-exclusion-20260925/results/dispatch-range-final-controls.trx`
- `artifacts/dispatch-range-exclusion-20260925/results/dispatch-range-owned-final.trx`

The deliberate pre-fix red receipt is retained as `dispatch-range-red.trx`:
both hosts rejected the positive 20-21 exclusion. The intermediate green receipt
predates the additional enriched-record controls; it is not the final gate.
Only the affected test project was rebuilt, with optional Avalonia build
telemetry disabled via `-p:UsedAvaloniaProducts=`. No portable package was built.

Fresh original captures:

- Dispatch integration: `artifacts/publication-dispatch-window-20260925/e595d2025db54ba08835cc56bac28435/`
- Independent pairs: `artifacts/publication-tuna-pairs-20260925/37b7d3a3ae0a42b389cecdfc11f5a499/`
- Omitted receive: `artifacts/publication-tuna-pairs-20260925/70813e84b9134097a53741b4b4ca8e28/`

The six host captures retain three original evaluations each, complete streams,
71 Coordinator boundaries per view, passing full/compact parity and unchanged
inputs. All 66 receipt-bound output hashes and each source hash/count verify.
Witness counts are 21, 26 and 24 respectively, below the unchanged 64 limit.
Pair-shadow cumulative work is 4,650-7,702 ms, below the unchanged 20,000 ms bound.
No source reopen, additional authority evaluation or witness-budget increase
was added to the query. Post-run hash inspection is verification, not authority.

## Preservation and next boundary

Only three pre-existing test/support files changed: the dispatch matcher, its
structural controls and the pair integration assertions. The other 82 baseline
dirty-file contents are unchanged. Added files are the detached range test,
its controls and this document. The prior seal and historical captures remain
unchanged; the new test build intentionally replaces the generated test DLL.
PowerShell parsing and `git diff --check` pass. Hashes and verification metadata
are recorded in `artifacts/dispatch-range-exclusion-20260925/FINAL-VERIFICATION.json`.

Next is a bounded review of the whole-scenario owner-issued gate handoff,
starting with genuine terminal/integrity evidence and complete failure
accounting described in `file-transfer-scenario-gate-handoff-review-20260925.md`.
Do not promote the small offline fixture, a copied all-pass object, or the
existing local-publication receipt into canonical live-256 acceptance.
Production runtime, strict classifiers, acceptance and receipt profile are
unchanged. No live GUI/NKN run, full matrix, historical reclassification,
commit or push was performed.
