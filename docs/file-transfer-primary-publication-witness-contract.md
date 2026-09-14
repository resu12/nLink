# C1k: owned primary publication-witness preparation

Date: 2026-09-14. Base HEAD `ef78e9d7`, with accumulated uncommitted work.
This connects C1j's private input/view context to verified writer membership and
the approved primary-selected failure witness. Completion issuance and live export
remain disabled.

Follow-up: [C1l private evaluation custody](file-transfer-primary-evaluation-custody-contract.md)
now gates the actual primary call site using an immutable invocation pair held
outside this mutable context. This resolver's direct-call result remains diagnostic
preparation, not a publication-completion capability.

[C1m](file-transfer-primary-publication-completion-contract.md) reuses the two
independently derived canonical row strings and detached witness/provenance
components. The diagnostic `Json` output remains unchanged and is not an issuer
input. Completion is owned separately by the private primary custody scope.

## First-failure seam and preserved primary behavior

The compact primary uses its existing `firstCompactProofChainFailure`, without
changing classification, ordering or candidate selection. When C1j has an admitted
source/writer-bound input context, the primary enables its existing exact witness
selector even without an external witness output reference. External output-ref
eligibility is tracked separately: an already assigned reference is not overwritten.
No failure, an unsupported first failure or a missing exact selection performs no
writer verification. A later convenient publication use is never substituted.

The new resolver runs at that first-failure seam, before any compact primary report
is published. It uses the actual full/reduced objects already associated with the
original evaluator invocations. It performs no authority evaluation, nearest-use
search, file-based index construction, view reconstruction or private-view clone.
The detached prepared JSON is kept in `private:localPublicationWitness`; it is not
returned, written to a report, printed, exported or accepted by a parent.

`PublicationAttempted` makes preparation one-shot within the existing input
context. Preparation failure omits this local result without changing the primary
verdict, report fields, external receipt or original watchdog. Primary cleanup
invalidates the native token, disposes any retained writer reader and drops the
private result on both normal and exceptional exits.

## Reusing the exact semantic checks without reloading authority

The existing structured-field parser is moved verbatim into
`tools/GuiSmokeStructuredFields.ps1`. The existing integer check, A/B1 resolvers,
canonical JSON/hash helpers and selected row projection are moved verbatim into
`LocalPublicationRows.ps1`. Their previous entrypoints import these definitions,
preserving file-based C0/C1d behavior. No interpretation of publication authority
is added or relaxed.

The new primary module imports only this pure helper layer. In particular it does
not import `GuiSmokeLogLedger.ps1`, `CoordinatorSnapshotEvidence.ps1`, or the C1d
consumer and thereby redefine the already loaded evaluator or its caches. Existing
Snapshot token/effect-binding helpers are supplied by the primary's original
environment. Tests pin evaluator script-block identity across optional loading.

## Input, writer and row checks

The selected failure must be the exact event object at its original full-native
position, with matching report class/reason, event name, source path/physical line,
writer sequence and raw-content hash. Current native arrays are rechecked against
C1j's immutable string references and the primary event index. Full origins must
remain increasing and equal to parser physical lines; compact origins must map to
the exact same full raw strings and remain increasing. Physical source lines are
not confused with full/reduced native positions.

Both completed views must be complete passing Snapshot V1 views, with matching
contract/catalog/boundary identities and the route runtime/transition families.
The C1i worker then opens its one-shot writer candidate under its exact completed
source-reader lifetime. The candidate's original file identity and size are checked
by C1i; C1e verifies strict decoding, byte hash, line count and EOF. Matching metadata
alone is not verification. A replaced, missing, changed, cancelled or partial writer
cannot provide a result.

The admitted writer traversal collects bounded raw strings under C1e's 250-ms
clock. Parsing happens after verified EOF, while that same read-only lease remains
held. Writer IDs, contiguous sequences, original start scope and final counts are
checked against the immutable accepted GUI context. Every full-native event must
have exact raw-string membership in that verified writer, with no repeated writer/
sequence membership. The compact input remains an exact bound subsequence.

The physical source is C1i's frozen analyzer output of the accepted GUI finalizer,
not an arbitrary caller-supplied source array. Skipped non-event physical lines are
not separately reread by this resolver; original whole-file provenance is supplied
by that accepted native-output/source-hash association. This does not authorize
using the resolver with a fabricated index or a source-only request.

Each view's boundary index is built independently from its actual private rows.
Every boundary is checked against its own exact native transition/hash/writer
location. All observed-state alternatives are preserved, with bounded ambiguity.
The unchanged A/B1 checks independently resolve the selected use and exact
forwarding effect in both views. Canonical selected semantic rows must agree, as
must original publication/effect physical source positions. Different full/reduced
native positions are preserved in detached provenance.

## Bounded work and output

The resolver resumes C1j's same cooperative 2,000-ms optional-work clock; it does
not charge original evaluations or intervening primary analysis to that clock.
It checks source/native ownership at phase and record boundaries. C1e's unchanged
250-ms writer verification budget also includes collection-loop pauses. Neither
clock is a hard scheduling guarantee or a live-capacity claim.

Before allocating the writer list, admission requires C1j's record ceiling plus
full/compact membership visits plus twice the original writer count to fit 262,144.
Further boundary raw-record visits also consume that ceiling. Code-unit accounting
starts with C1c's hashed units and adds writer collection/parsing, native parsing and
boundary parsing, capped at 67,108,864. Individual lines are capped at 262,144 code
units. Bounded splitting admits at most 256 semicolon segments before the unchanged
field parser runs; no unbounded field-array allocation is added.

Full/reduced boundaries and effects together are capped at 8,192 rows; admitted
operations at 4,096, with at most 64 per boundary and 64 observations per key.
Counters measure admitted records/units/rows, not every interpreter operation,
reference comparison, dictionary allocation or repeated selected lookup. Compact
origin lookups are bounded binary searches within the already capped full array.
Selected A/B1 reparsing is bounded by the previously admitted raw lines and row
limits. Canonical JSON is capped at 262,144 ASCII characters before reports.

Extra retained memory is bounded writer strings, writer/native membership
dictionaries and references to existing boundary/effect rows. There is no whole
private-view serialization/copy. The formatter sees detached scalar data only,
not the primary context. After primary reports, only existing bounded cleanup runs:
no scan, hash, serializer startup, file/network I/O, IPC, wait or retry is added.

## Not a completion issuer

The result has `PublicationCompletionIssued=false`. Its context and prepared JSON
are private mutable orchestration data, not an authenticated public capability.
Direct helper calls with consistently fabricated views do not prove an evaluator
invocation. The actual primary call site and accepted file owners establish this
preparation association; they do not independently grant Coordinator authority.
No route/runtime effects, accepted/recovered verdict or peer-lineage proof is
inferred. Scope stays `local_publication_forwarding`, peer lineage `not_evaluated`.

Next: bind the private one-shot completion issuer and bounded dynamic expectation
channel to this prepared context, then finish the upstream process relay and
unchanged post-receipt adoption. No live caller is enabled, and C1a's pre-known-hash
slot is still disconnected. A new live 256 MiB test is not yet required.

Verification and interruption recovery are recorded in
`artifacts/publication-primary-witness-20260914/RESULTS.md`.

The final gate passed 11 unique xUnit tests: the new generated test's 60 actual
parent/worker cases, eight directly related input/report/A/B1/C0/C1d regressions,
and two fresh-checkout checks. Each of the 16 positive worker cases agrees with
the independent C1d reference consumer's selected row and original source/native
positions. Generated failure labels are synthetic selection controls, not claims
that the successful transfer fixture reported those failures. Each worker uses
the actual compact input-assembly/evaluation statements with exactly two original
calls; existing full primary-workflow tests separately preserve report behavior.
Measured combined optional work was 447–835 ms for the positive fixture cases.
This does not establish live-scale capacity or enable completion issuance.
