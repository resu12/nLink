# Follow-up RCA and bounded fix plan: rejected-ingress verification gates

## Decision and scope

Fix the **legacy GUI evidence selector and its newly exposed recovery-admission
hazard first**, then repair the **single contradictory retained-report fixture**.
Keep these as separate changes with separate focused verification.

The latest evidence is `implementation-rejected-ingress/affected-01.trx` and
`repairs-01.trx`, against base `5e790d11` plus the current uncommitted evidence-only
implementation. The new implementation has 59 distinct passing tests and a
passing two-case dual-host generated contract. Two older gates remain failed.

This is not another runtime RCA. Do not change Coordinator authority, transport
routing, guard behavior, recovery policy, wire/DTOs, Snapshot profiles/catalogs,
timeouts, or throughput. Preserve all current worktree changes. No portable
rebuild, live run, historical report, or historical acceptance is included.

Historical helper **9564** in `20260910T060311Z` remains unresolved. Its exact
Tuna receive is absent; new envelope provenance cannot reconstruct that receipt.
Saved quartet **25/25/18/7**, scalar **14**, and Phase-5 failure remain unchanged.

## 1. GUI evidence selection drops the records its reducer requires

### Evidence and RCA

Failing gate:

`GuiSmokeFailureClassification_FileTunaV4RejectedReceiveAllowsInterleavedSidecarTelemetry`

The first execution failed because this extracted test bundle did not include
`FileTransferFrontierGraceHandoff.ps1`, which production already loads. The
existing one-line test import repairs that dependency. The next recorded failure
in `repairs-01` is semantic: `interleaved exact ignore did not recover the receive candidate`.

The fixture contains an initial Regular-to-Tuna transition, a source send, one
rejected receive, its exact mismatch and ignore, 12 unrelated sidecar records,
then a terminal. Its intended positive result is recovered; removing the ignore
must remain unresolved.

The `FileTunaDataPath` early predicate catalog and the reducer disagree:

| Required by the reducer | Current selector |
|---|---|
| `filetransfer_tuna_data_path_mismatch` | Selects `filetransfer_data_path_transport_mismatch` instead |
| `filetransfer_data_frame_ignored` | Selects `filetransfer_data_path_frame_ignored` instead |
| `filetransfer_route_transitioned` | Missing |

The early index discards the actual transition/mismatch/ignore before calling
`Get-TunaGuiFileTunaV4DataPathMismatchFailure`. Source inspection therefore leaves
the candidate without current-transition or companion proof; the later terminal
poisons the open candidate. This is an evidence-routing defect, not a failed
payload guard or missing production ignore.

Diagnostic RCA label (not a new production taxonomy requirement):
`gui_data_path_index_omitted_exact_rejection_companions_and_route_transition`.
The existing surfaced classification remains `file_tuna_data_path_mismatch` in
`route_runtime` until the exact chain is admitted.

### Coupled safety finding: a catalog-only correction is insufficient

The GUI reducer currently records any inbound `new_route=file_tuna_v4` transition
with matching scope. It does not distinguish an initial Regular-to-Tuna transition
from PostFallback-to-Tuna, and does not enforce the latter's exact Tuna resend and
receive requirement. Merely making the missing records visible could therefore
close a second-cycle case that the retained matcher correctly leaves unresolved.

Its transition dictionary also retains historical scope keys. Gating only new
insertions would leave old initial-transition eligibility reusable after another
transition, terminalization, or an A-to-B-to-A restoration.

This risk is established by source inspection; no counterfactual evaluator run
or newly passing result is claimed.

### Narrow safe fix

1. Correct the two exact event-name entries and add `filetransfer_route_transitioned`
   to the existing GUI partition. Do not translate unknown events into aliases,
   rename valid fixture events, import the broad retained-analysis catalog, or
   expand unrelated partitions.
2. Keep the repaired test helper import. Add a structural assertion that this
   partition contains every exact event name consumed by the data-path reducer,
   including its terminal boundaries. Preserve original evidence lines and
   `gui_evidence_sequence`; selecting extra records must not renumber evidence.
3. Limit the newly enabled recovered branch to the verified initial shape:
   `previous_route=regular_nkn_v4_fast`, `route=new_route=file_tuna_v4`, protocol 4,
   inbound direction, and the complete existing matching scope.
4. Maintain only current initial-transition eligibility per writer/session/transfer.
   A same-scope route replacement must revoke the earlier eligibility before
   evaluating the replacement. Terminalization also revokes it and prevents
   borrowing pre-terminal source evidence. Preserve poisoning of already-open
   candidates. Unrelated transfers/writers must remain independent.
5. Keep PostFallback, missing-predecessor, and unknown-predecessor transitions
   unresolved in this legacy GUI branch. This includes a second-cycle case that
   supplies a Tuna receipt: exact second-cycle GUI recovery is deliberately
   deferred to a separate shared-matcher integration, not reimplemented here.
6. Preserve the existing exact source, range/byte/count, writer, scope, mismatch,
   ignore, duplicate, and terminal requirements. Neither envelope fingerprints,
   an ignore alone, nor terminal SHA can grant recovery. Use existing indexes and
   bounded per-transfer state; no per-candidate whole-stream rescans.

This is conservative diagnostic admission, not new file-transfer authority. If
the focused negatives expose another permissive recovery edge, stop and report
it rather than broadening this into a general GUI correlator rewrite.

### Focused tests and exit

- Initial exact chain with interleaved unrelated records: expected `1/1/1/0`.
- Missing source, transition, mismatch or ignore: expected `1/1/0/1`.
- Wrong writer/session/transfer, range, bytes, publication or runtime scope;
  duplicate/conflicting companions; terminal before/inside the chain; route
  replacement and ABA restoration: fail closed.
- Second-cycle inputs with and without an exact Tuna receipt remain explicitly
  deferred/unresolved in this GUI branch. The retained full/compact second-cycle
  positive and missing-receive negative remain unchanged.
- Empty/singleton partition, exact catalog membership, original candidate
  location, and unrelated-telemetry independence.
- Run the repaired test and the added focused GUI matrix on PS5.1 and PS7;
  compare selected versus unfiltered reducer input for the supported initial
  shape. Do not increase GUI budgets.

The production authoritative GUI path returns at lines 7217–7270, before this
legacy reducer and partition. **This fix does not establish authoritative
GUI/retained data-path parity**, change Snapshot arbitration, or repair the
historical live attempt. Keep that broader integration explicitly out of scope.

## 2. The retained positive fixture claims a static Tuna start and an earlier Regular send

### Evidence and RCA

Failing gate:

`AnalyzeRetained_FileTunaV4ReceiveSideRegularNknFrameRejectedFailClosed_DoesNotFail`

`BuildFileTunaV4RejectedReceiveFixture` inserts its Regular-NKN source send into
`BuildFileTunaV4DataPathProofFixture`. That wrapper has already selected a static
Tuna route and started its runtime. The fixture therefore asks the route checker
to accept a Regular send under the selected Tuna route.

Saved compact evidence reports:

`route token mismatch: selected_route=file_tuna_v4; event_route=regular_nkn_v4_fast`

The independent data-path proof is already recovered: scalar mismatch **0**,
quartet **1/1/1/0**, and no selected data-path proof failure. The overall compact
`protocol_or_integrity` classification is caused by the route setup, not by the
new rejection diagnostic.

The previous experiment moved the source earlier and added a Regular selection.
It removed that mismatch but exposed `route changed before prior terminal`:
the original Tuna selection still said `handoff_kind=none`. That experiment was
reverted. Simply moving a line or adding an intermediate terminal is not a fix.

For Regular-to-Tuna, the existing route checker requires
`normal_to_tuna_activation`, `file_tuna_active=1`, and
`post_tuna_fallback_active=0`. Epoch zero alone is not the rejecting predicate;
do not misdiagnose it or weaken generation checks to pass the test.

Diagnostic RCA label: `static_tuna_fixture_contains_regular_source_send_without_live_activation_history`.
This is a test-contract defect. Do not introduce a production exception or
reinterpret the compact failure as a successful run.

### Narrow fixture-only fix

Build a coherent initial-activation prefix **only for this affected test**:

`Regular selection/runtime → original Regular send → explicit Tuna activation selections → matching route-transitioned diagnostics → Tuna setup → exact rejection chain → terminals`

- Reuse the existing source/rejection payload and route-aware fixture helpers.
  Keep default output for other callers unchanged, using a dedicated local
  fixture or explicit opt-in only where needed.
- Move/replace the static Tuna selections; do not leave conflicting copies.
  Set the real initial-activation handoff/active flags, protocol 4, Tuna target,
  `file_tuna_v4_fast` profile, and `tuna_strict` bridge policy. Bind publication,
  runtime, terminal, leg and commit fields to the exact existing transition and
  rejection scopes; do not manufacture a new successor relationship.
- In this legacy log fixture, explicit Tuna selection must precede its ordinary
  route-aware `filetransfer_route_transitioned` diagnostic. The route selector
  otherwise still compares that diagnostic with Regular. Keep the essential
  recovery order: source send before receiver transition before rejected ingress.
- Correct receive and receive-mismatch direction to inbound in this opt-in
  fixture; the generic payload builder currently supplies outbound direction.
- Preserve the original source writer/sequence, exact payload/range/byte values,
  rejection companions, unrelated telemetry, and BOM parser coverage. Update
  physical line timestamps/order consistently rather than changing records to
  fit a stale static selection.
- Do not add a terminal between source send and activation, synthesize a missing
  Tuna receive, import a complete five-route scenario, or change the recovery
  matcher, route checker, operator arbitration, or default fixture builder.

Existing `BuildRouteAwareLiveRegularActivationCycleFixture` and the initial
activation route-law test are field/order references, not reasons to run the full
matrix. Avoid blindly reusing `PublicationEffectMessage` for a Tuna selection:
its bridge-policy text is not the `tuna_strict` value required here.

### Focused tests and exit

- Corrected full and compact fixture: route consistency `pass`, mismatch count
  **0**, data-path quartet **1/1/1/0**, scalar **0**, no data-path failure/variant,
  and no `protocol_or_integrity` caused by this setup. Preserve existing
  full/compact no-failure sentinel conventions.
- Missing/wrong activation handoff or active flags must still fail route
  consistency; a Regular send after Tuna selection must still fail.
- Missing source/transition/ignore must still fail data-path recovery.
- No change to other fixture callers, second-cycle missing-receive diagnostics,
  or the passing generated envelope/redaction/compact contract.

## Verification order and stop

1. Implement GUI selection plus its bounded initial-only safety guard; parse
   affected PowerShell and run only its focused dual-host tests.
2. Implement the fixture-only history repair; Release-build the smoke project and
   run that test in independent full/compact modes plus directly relevant route
   and missing-evidence negatives. Use the existing command-local Avalonia build
   telemetry workaround if the sandbox still denies that non-product log.
3. After both stabilize, run one small joined regression selection including the
   two previously failing gates and the existing two-case provenance contract.
   Do not rerun aggregate Smoke/Contract or a production-width historical report.
4. Require both old gates green, preserved provenance semantics, and the explicit
   second-cycle/authoritative-GUI limitations. If another boundary appears, stop
   for its own RCA; do not extend scope or increase timeouts.
5. Review before any commit. Portable/preflight/live or standalone historical
   report resumption needs a separate bounded authorization; the three-report
   and six-attempt campaign allowances remain exhausted.

## References and evidence limits

- [Recorded failures/results](<../artifacts/validation/consolidation-bounded-20260907/implementation-rejected-ingress/RESULTS.md>)
- [GUI reducer](<../tools/GuiSmoke-Windows.ps1#L7301>)
- [GUI event partition](<../tools/GuiSmoke-Windows.ps1#L19238>)
- [GUI early indexing](<../tools/GuiSmoke-Windows.ps1#L22279>)
- [Authoritative GUI early return](<../tools/GuiSmoke-Windows.ps1#L7217>)
- [Existing GUI fixture](<../tests/nLink.SmokeTests.Core/FileTransferGuiHarnessScriptTests.cs#L12809>)
- [Retained positive test](<../tests/nLink.SmokeTests.Core/FileTransferRetainedAuthorityProofAnalyzerTests.cs#L1207>)
- [Static Tuna wrapper](<../tests/nLink.SmokeTests.Core/FileTransferRetainedAuthorityProofAnalyzerTests.cs#L5012>)
- [Rejected-ingress fixture](<../tests/nLink.SmokeTests.Core/FileTransferRetainedAuthorityProofAnalyzerTests.cs#L5033>)
- [Allowed live route transition](<../tools/FileTransferSoak/SoakSummaryExtraction.ps1#L1039>)
- [Existing route selection checks](<../tools/FileTransferSoak/SoakSummaryExtraction.ps1#L1278>)

This follow-up preparation used source and saved finalized test outputs only.
No implementation, tests, evaluator/report invocation, build, commit, package,
preflight, live run, timeout change, or historical evidence reconstruction was
performed. Proposed test outcomes above are acceptance targets, not new results.

## Implementation outcome — 2026-09-10

Implemented in the recommended order. The legacy GUI selector and initial-only
admission passed46 focused cases onPS5.1/PS7, preserving original evidence
locations. The dedicated route-history fixture passed independent full/compact
checks plus9 dual-host guard cases. One joined gate passed4/4, including the two
formerly failing tests and existing provenance/full-compact contract.

The clean compact fixture uses its actual `(none)` no-failure sentinel; the older
`none` expectation above came from the contradictory fixture and was corrected
in the test, not production. Each generated route-matrix case resets the existing
query cache before evaluation. Full-only report fields are asserted only on the
full writer's report.

Second-cycle GUI recovery and authoritative-GUI integration remain deferred.
Conflict checks cover records contributing to candidate evidence, not arbitrary
conflicting invalidator identities. Historical helper9564 remains unresolved;
no package, live run, historical report, commit, or reconstructed finalization.
See [implementation results](<../artifacts/validation/consolidation-bounded-20260907/implementation-rejected-ingress-follow-up/RESULTS.md>).
