# Captured dispatch-observation consumer

2026-09-25. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Implemented the bounded test-only query reviewed in
`file-transfer-dispatch-window-review-20260925.md`. It is default-off behind
`NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER=1`, after the original reciprocal
yield and rejection-chain matches. Optional failure leaves those earlier
results and the original primary outcome intact.

## Scope and ownership

The query reuses the active original primary custody, complete semantic-source
array and exact origins, both completed authority views, native publication
indexes, and the already verified writer reader. It checks source-reader
completion/count and the original semantic output's path, hash, bytes and lines.
Full origins must be exactly 1 through the complete source count. Original owner,
reader, writer-context, descriptor-list and descriptor references are checked
before and after the query. No source/writer reader is reopened and no membership
or authority evaluation is added.

The selected writer's original `LastSequence` supplies the upper bound. The
original bounded writer list must total the verified writer-output line count.
The lower bound is the publication boundary **record**, with its exact forwarding
sequence reported separately. Neither endpoint is an inferred activation or
retirement instant. The final retained receiver row has its own source-line
witness; the later capture EOF remains separately labeled original metadata.

The scan covers all retained selected-writer records through capture EOF, including
after ignore and Snapshot completion. Candidate-publication uses before the
declared boundary and rows beyond the capture endpoint are unproven. Existing
4,096-record, 256-boundary/view, 64-operation/boundary, cumulative work and
64-shared-witness bounds remain in force; results are never truncated to pass.

## Matching rules

Each candidate dispatch entry resolves its own `receive_dispatch` use and exact
forwarding evidence against each original view. Its stable publication owner,
boundary, operation, token, authored generation and route tuple must match.
Observed state may advance only when the existing resolver independently validates
that state. Full and compact normalized results must agree.

Authenticated payload, establishment, security sequence, local installation,
outer envelope, canonical scope and v4 frame/range remain distinct identities.
Conflicting or missing candidate evidence is unproven, including a substituted
outer envelope under the same authenticated payload. A rejection label does not
skip those checks. Separately resolved different payloads or publications are
outside the exact candidate.

Receive-result entries count once. A same-identity Tuna peer-visible receive
companion does not add a dispatch; orphan, duplicate or ambiguous companions are
unproven. A generic frame-dispatched event cannot borrow authentication or route
identity from another row. Unless its own fields clearly exclude it, it blocks a
clean no-match result. `transport=nkn` is not a Regular/Tuna discriminator.

Separate statuses are `observed_matching_bound_dispatch`,
`no_matching_bound_dispatch_observed_in_capture`, or `unproven` with a first
missing edge. None enables the stale-rejection classifier, recovers a primary
failure, changes candidate accounting, establishes durable delivery or changes
acceptance. Even a matching entry is not attribution to the rejected callback.

## Verification

The final original-capture case is
`artifacts/publication-dispatch-window-20260925/615bdd74e3b04a8c931d62274e6993f3/`.
Both Windows PowerShell 5 and PowerShell 7 report:

- Original yield, reciprocal and rejection-chain matches retained.
- No matching bound dispatch observed in receiver sequence interval 188–230;
  forwarding sequence 201, last retained receiver sequence 226 at semantic-source
  line 339, and 33 retained records in the inclusive interval.
- Full/compact agreement, unchanged original inputs, 21 shared witnesses,
  exactly three original evaluations, and `missing_exact_tuna_receive` unrecovered.
- Nine detached positive/exclusion controls and 41 negative controls, plus the
  actual original-custody replacement check. These controls change cloned rows
  only, use the existing local resolver, and issue no authority evaluations.
  Constructed dispatches are not genuine authenticated unsafe histories.

Controls cover before/after ignore and after Snapshot completion; a valid later
observed state; peer companion counting; different writer/payload/publication;
generic ambiguity; identity/range/disposition errors; duplicate origins/sequences;
unsupported state; capture/source coverage; ordering, parity and shared-witness
overflow. Actual prior custody/origin omission controls also remain active.

The optional-prerequisite omission case is
`artifacts/publication-dispatch-window-20260925/b8bd6bf6ac0a4cdc8e60aec613c40c43/`.
Both hosts report `dispatch_requires_rejection_chain` without replacing the
original yield/reciprocal matches or primary failure. Later matcher-only changes
do not execute on this omitted path.

Test receipts are under `artifacts/publication-dispatch-window-20260925/results/`.
The initial two-test run and the subsequent affected-integration run passed.
The final stable integration plus three default-off regressions passed (4/4):
the original rejection chain, its optional omission, and the small primary-only
observer. Across these receipts, all five distinct focused tests passed on both
PowerShell hosts. Final results and file hashes are recorded in
`artifacts/publication-dispatch-window-20260925/FINAL-VERIFICATION.json`.
Earlier passing development captures are retained, not presented as final pins.

## Change boundary

This milestone changes five existing test files and adds four support scripts
plus this document. All 50 other pre-existing dirty files, including all runtime
changes from earlier milestones, remain byte-for-byte unchanged. Previous seals
remain unchanged; their source pins differ only at the five explicitly edited
test files. No production analyzer, runtime, retention, classifier, acceptance,
package, live NKN/GUI run, commit or push is part of this milestone.

This completes the reviewed observation query, not general stale-safety proof or
live-256 readiness. Any acceptance/classifier change still requires its own
evidence-backed contract; this diagnostic must not become an exemption by itself.
