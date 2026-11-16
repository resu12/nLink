# Accumulated initial-Tuna changes: pre-commit review

2026-09-21. Reviewed against HEAD
`bdfd45b964956867b02f3f00ae8b12981a8a270a` and the accumulated working tree,
including the untracked production partials and their tests.

## Result

No new actionable correctness finding identified in this review. The changes
are ready for a local source checkpoint, not for a live-acceptance or release
claim. No production/test code was changed by the review. No tests, portable
build, live run, commit, push or historical authority reevaluation was performed.

## Reviewed boundaries

- Core preparation is a separate state machine. Registration is bound to an
  exclusive attachment and actual verified security incarnation. First admission
  closes the callback fence; reset/reattach cannot reopen the same incarnation.
  Preparation permits and observations cannot create file-route authority.
- Service admission reserves and claims exact evidence. Adapter lease reads,
  retention and activation run outside the service gate; adoption recaptures
  the reservation and uses the actual Coordinator route-publication token.
  Missing readiness preserves ordinary outbound selection and strict inbound
  initial-Tuna rejection. Duplicate offers retain their existing context path.
- The three authenticated preparation message types are disjoint from ordinary
  acceleration. Public unlock selection retains fallback/retry/transition
  precedence. Queued work carries its original security epoch and permit;
  accepted preparation replies cannot refresh transfer liveness or fallback
  control evidence.
- Physical preparation, retained admission and activated transfer ownership
  rotate exact cleanup receipts. Late preparation/provisional events remain
  isolated. Retirement disposes only captured resources; it cannot stop a
  successor through a shared-lane stop. Core cancellation callbacks execute
  asynchronously, outside the Core gate owner.
- The smoke-only diagnostic bridge exposes non-consuming observations, not
  permits. The GUI gate checks both processes, boot identities, roles, session
  scope, request correlation, readiness expiry and shared episode fingerprint.
  This gate remains a precondition, never route-publication authority.
- Initial source binding uses the current publication, runtime owner, leg,
  agreement and data-session scope. A missing commit is accepted only for first
  publication, epoch zero, no prior commit/epoch and no terminal generation.
  Committed sources still require current commit validation. Operation-specific
  provider/session, pause, hold, membership and settlement checks remain.
- The analyzer requires the complete initial Core owner/publication chain and
  matching effect. It accepts only the first initial-Tuna publication; a second
  same-owner Tuna publication still needs route-transition proof. Existing
  Regular republication handling and full/compact parity remain covered.

## Verification record checked

This review inspected the saved TRX counters and the focused tests; these are
existing results, not new test executions. Counts from different checkpoints
overlap and must not be added into a campaign total.

- Paired diagnostics checkpoint: `preparation-diagnostics-affected.trx` completed
  with 171/171 passing. Final harness follow-up completed with 9/9 passing.
- Initial-owner checkpoint: final owner/feedback selection completed with 10/10
  passing, and affected route/handshake selection with 46/46. Four feedback
  cases at that checkpoint were blocker diagnostics, subsequently converted
  into positive payload tests by the source-binding work.
- Source-binding checkpoint: 11/11 final Core cases, 2/2 evidence cases and
  4/4 payload cases completed successfully. Its later six joined cases also
  passed in the focused run whose 11 Core fixtures initially failed; those
  fixture-ordering failures were corrected and rerun separately.
- The combined bootstrap run is **not green**: its TRX run outcome is `Failed`
  after explicit termination, despite 120 passed test results and zero failed
  test cases. The isolated bounded joined run completed with 7/7 passing.
  The checkpoint records 125 distinct affected passes across those runs and
  144 distinct passes including the new Core/joined/evidence cases. Preserve
  this distinction from a single successfully completed 125-case run. The
  earlier stall's cause remains unestablished.
- `git diff --check` passes. No deleted tracked files or stale references to
  the renamed initial-publication analyzer helper were found. No `testhost`,
  `nLink` or `nlink-tuna-sidecar` process was observed at the review check.

Detailed red/green and intermediate outcomes remain in
`artifacts/session-preparation-diagnostics-20260921/RESULTS.md`,
`artifacts/initial-tuna-owner-fix-20260921/RESULTS.md` and
`artifacts/initial-tuna-source-binding-20260921/RESULTS.md`.

## Remaining validation boundary

1. Create the local checkpoint with all required new source/test files and
   confirm clean source provenance. Do not push as part of this step.
2. Build a fresh portable package from that checkpoint. The previous portable
   predates the initial-owner/source-binding fixes and cannot validate them.
3. Run only `active-tuna-v4-64mb`, with strict bridge/network and scenario
   preflights outside the restricted sandbox, using a new artifact directory.
   Do not bypass failed preflights or rerun the full matrix at this boundary.
4. Require actual current Coordinator publications, payload completion and
   terminal proof. Report correctness separately from measured goodput.

Four-byte fake-network payload delivery does not establish 64 MiB completion,
live throughput, initial-Tuna fallback or the live-256 route cycle. Initial-Tuna
fallback admission remains intentionally disabled by the existing committed
source guards. The previous live failure, original matrix result and separate
live-256 exact-resend/Regular inbound-terminal gaps remain unchanged.
