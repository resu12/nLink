# Authoritative GUI diagnostic companion: implementation results

Implemented from `9bde1bae` on 2026-09-10 under the approved three-active-hour
[plan](file-transfer-authoritative-gui-diagnostic-companion-plan.md).
The implementation checks pass. The original joined gate was
**19/20**; its sole pre-existing structural-test failure has been reviewed and
corrected, with a focused **3/3** follow-up passing. The joined gate was not rerun.

## Implemented contract

- The immutable primary classifier supplies an optional, read-only eligibility
  carrier. It reuses the existing full/compact Snapshot comparison and route
  comparison, requires complete authoritative streams, and remains ineligible
  after existing evidence, authority and bootstrap-boundary early returns.
- The carrier identifies the exact paths and decoded lines read by the primary.
  Hash domain is explicitly `utf8-lines-lf-v1`, with a separate line count; it is
  not a byte-for-byte file hash. Hash failures disable diagnostics only.
- Primary enrichment adopts a valid classification even if its optional carrier
  is absent or malformed. Its original stopwatch and budget are retained in
  memory; no timer/budget is restarted for the companion.
- The GUI failure path atomically saves the completed primary error summary
  before starting a separate hidden diagnostic process. The process uses only
  the remaining original budget, bounded further by the existing finalization
  deadline and ten-second reserve. Timeout cleanup stops/drains/disposes it before
  the caller continues. Late output is not adopted.
- The child initializes the existing parser/shadow bundle in isolation, validates
  input identity, parses once and builds one shared matcher index. It never
  evaluates Snapshot authority or imports the broad ledger/reporting bundle.
- The optional `nonAuthorityDataPathDiagnostics` sibling records its own status,
  reason and identical-receive-record counts. `sharedMatcherRecovered` is not GUI
  recovery, a Coordinator verdict, a retained scalar, or an acceptance decision.
- Candidate records distinguish physical input-file path/line from original
  writer identity/sequence. `evidenceSequence` is populated only from explicit
  `gui_evidence_sequence`; otherwise it is null, never a compact ordinal presented
  as an original position. Existing internal matcher ordering is unchanged.
- The additive writer clones the primary summary rather than mutating it. An
  optional-write failure preserves the already-saved primary. The original
  scenario exception and primary enrichment receipt remain unchanged.
- The existing dependency-free SHA routine was moved unchanged to
  `tools/GuiSmokeContentHash.ps1`, shared by the ledger and companion. Its function
  text matches HEAD after line-ending normalization. New files are included in
  the script dependency/parser inventory.

Successful GUI scenarios remain unchanged. No runtime/Core/Infra behavior,
legacy GUI recovery predicate, Snapshot catalog/profile, deadline setting,
five-file provenance-manifest schema, portable payload or wrapper policy changed.

## Verification

- Final Release build passed: zero errors, 221 existing warnings. The initial
  sandbox build failed on Avalonia's existing user-profile log permission; the
  approved build outside that restriction passed. No portable package was built.
- Six affected scripts pass parsing; the new runtime fixture executes on PS5.1
  and PS7, including the actual GUI finalization branch and real child processes.
- Final focused invocation: **3/3 passed**, 25.008 seconds including test-run setup.
- One joined invocation: **19/20 passed**, 154.714 seconds including setup. No
  aggregate analyzer/Smoke suite, historical report, or live scenario was run.
- Test-only cross-file follow-up: **2/2 passed**, 25.439 seconds including setup.
  Evidence split across two immutable inputs preserves diagnostic parity and maps
  each record to its correct source file and physical line. Only companion tests
  were rerun after this coverage addition; the joined gate was not repeated.
- Test-only finalizer-assertion follow-up: **3/3 passed**, 1.932 seconds including
  setup, after a Release rebuild (15.77 seconds, zero errors, 221 existing warnings).
  The corrected scenario check, canonical-input ownership and monotonic-stage
  journal checks pass. In-memory count controls verify one, missing and duplicate
  finalizer calls yield counts 1, 0 and 2 respectively. No production code changed.
- Passing coverage includes recovered and unresolved diagnostics, full/regenerated
  compact diagnostic JSON/hash parity, source provenance, input mutation and
  unrequested paths, missing/malformed eligibility, Snapshot invalid/incomplete/
  parity/boundary outcomes, exhausted/frozen clocks and finalization reserve,
  real child failure/missing/malformed/count/identity/late-output cases, child
  cleanup, additive serialization and optional-write failure.
- Existing 20-case shared/GUI characterization, 46-case GUI selection safety,
  nine-case route/proof guards, rejection diagnostics, generated transport/redaction
  parity, primary enrichment, atomic capture and selected provenance tests pass.

Development logs retain the initial missing `Get-FileHash` dependency and a test
fixture BOM issue. Compaction moved a BOM-prefixed record to byte zero, where
`ReadAllLines` consumes the BOM; the corrected fixture binds persisted decoded
lines exactly as the primary does. Production identity checks were not relaxed.
One development filter matched zero tests and is not counted as validation.

TRX files: `artifacts/validation/consolidation-bounded-20260907/implementation-gui-diagnostic-companion/`.
All thirteen retained invocations total 287.542 seconds, including the zero-test
filter invocation. The prior read-only review's console-only three-test invocation
took 1.6286 seconds: two guards passed and the stale assertion reproduced its
failure. These durations are separate from the conservative active-work upper
bound of 80 minutes (65 implementation plus 15 review/correction), within the
approved 180-minute phase.

## Resolved stale joined-gate assertion

`GuiSmokeTunaScenario_WritesDurableLogLedgerEvidenceOnce` originally failed at
`FileTransferGuiHarnessScriptTests.cs:1854`: it required the literal
`Write-GuiSmokeLogLedgerRangeCapture` in `GuiSmoke-Windows.ps1`.

Source comparison against `9bde1bae` independently confirms that the baseline
test already requires this string while the baseline GUI script does not contain
it. History identifies `1a1d7090` as the single-pass finalization change: it removed
the helper call and updated the finalization-journal test to forbid that call,
but left this scenario assertion behind. No clean-checkout execution is claimed.

The approved test-only correction replaces the obsolete whole-file helper-name
requirement with an anchored, exact-name invocation count scoped to the scenario:
`Write-FileTransferGuiLogLedgerEvidence` must be called exactly once. Existing
capture-before-enrichment, startup/bookmark ordering, writer-integrity and durable
evidence checks remain unchanged. The corrected test passes with both adjacent
ownership/finalization checks; the original joined result remains historical 19/20.

The new real-process regression verifies primary adoption/persistence, companion
execution, additive output and original-exception preservation through the actual
current finalization branch. Its saved result passes on both hosts; it was not
rerun for the unrelated test-only assertion correction.

## Handoff and preserved state

Implementation and reviewed test correction complete. This changeset contains
the companion, focused tests and status documentation; unrelated frontier work
and ArtifactWriters changes are excluded. No new full joined-gate result or
clean-checkout gate is claimed. Recorded tests are pre-commit worktree evidence.

Unrelated frontier diagnostic files and the two ArtifactWriters null guards remain
unchanged. Historical `20260910T060311Z` retained input SHA remains
`478b7f7aaf8802458f8cb57d1d8a0019696db9810c3334a57d4656d5e2c27683`.
Helper 9564 remains unresolved; its saved `25/25/18/7` and scalar `14` are not
reclassified. Historical report allowance remains three consumed and the live
campaign 6/6 exhausted. No new report or live attempt is authorized.
