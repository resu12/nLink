# Native content family contract review

The next bounded implementation should establish source-owned semantic-family
coverage through the existing native handoff. Do not implement the content query,
change acceptance or add a live run in that slice. Reported-candidate binding is
the following gate, not something the current input-selection result proves.

This is a review only, dated 2026-10-02, at commit
`7ddbe15c3b894cf2497207cc39a08189b7578cc0` on `v0.7.0`. The accumulated work
remains uncommitted. The [review receipt](../artifacts/native-content-family-contract-20261002/verification.json)
records preservation and source checks; it is not a coverage capability.

## Verified state and first missing edge

The [retained integration implementation](file-transfer-native-retained-integration-implementation-20261002.md)
and its [receipt](../artifacts/native-retained-integration-20261002/verification.json)
establish ordinary native input selection and subsequent retained custody.
Seventy distinct focused checks and one offline scale test passed. The latter
selected two witnesses from 102,218 semantic rows and 208,326,024 bytes, with
113.888 seconds of final cumulative work. Its rows are a neutral size and custody
fixture, not complete content, security and coordinator-publication histories.
It explicitly leaves content-family coverage and reported-candidate binding
unproven. Unchanged tests have not been rerun for this review.

The [semantic catalog](../tools/GuiSmokeEvidenceCatalog.ps1) requests legacy,
coordinator and diagnostic events, including security installation and retirement.
The [owned cursor](../tools/FileTransferOps/LocalGuiWriterCursorMatch.cs) clones
the requested names. The [stream processor](../tools/GuiSmokeLogLedgerStreamingProcessor.cs)
builds a case-insensitive selection set and writes matching raw rows with their
original physical origins while traversing the whole frozen source. Semantic
selection includes the prefix before the retained bookmark; it is not limited
to the retained tail.

However, the original capture and [sealed parent facts](../tools/FileTransferOps/LocalFrontierParentFacts.cs)
do not retain the actual selection set or a selection-policy version. Source
identity, semantic-output hashes, physical origins and writer closure prove
custody of what was selected, not that every family needed by this query was
eligible for selection. A present-day catalog, fixture assertion or detached
JSON cannot fill that missing invocation-specific proof.

Duplicate `event` fields are already included in the processor's duplicate
writer-evidence detection. This review does not identify a new duplicate-event
parser defect or an observed live omission. The coverage contract must still
decline when event selection for a relevant owned row is ambiguous or unknown;
existing malformed-writer rejection must not be weakened.

## Proposed family coverage contract

Record immutable coverage provenance during the original owned traversal,
using its effective case-insensitive event set, not a caller's later array.
Bind it to the original capture, selection-policy version, run and scenario,
frozen source identity and interval, semantic output identity and hash, origin
map and verified writer endpoints. Reuse existing custody facts rather than
creating an independently importable authority object.

For the reviewed content policy, mandatory selection families are:

- `filetransfer_data_path_send_result`, `filetransfer_data_path_receive_result`,
  `filetransfer_tuna_data_path_mismatch` and `filetransfer_data_frame_ignored`.
- `filetransfer_security_context_installed` and
  `filetransfer_security_context_retired`.
- All three snapshot stream families and the generation-stamped authority-effect
  family declared by the [coordinator catalog](../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1).
- `transfer_terminal`, `filetransfer_transfer_terminal` and
  `filetransfer_live_route_epoch_terminal`.

These are selection requirements, not claims that every family occurred or that
each selected record is valid. Zero occurrences can be complete only when the
family was eligible across the entire verified interval. Missing eligibility,
uncertain classification of a relevant owned row, incomplete endpoints or an
unsupported policy yields unproven coverage. Matched but malformed rows remain
available for later rejection; do not discard them to make a uniqueness or
absence check pass. Do not claim anything before the captured source begins or
after its frozen end. Writer start, drain, recovery and manifest validation
continue to use their existing original-source proof, not semantic membership.

Carry this provenance in the existing sealed parent-facts channel and validate
it against the original native packet and source/output pins in the worker.
The [current decoder](../tools/FileTransferOps/LocalFrontierParentFactsValidation.cs)
accepts only its exact version and rejects trailing data. Therefore any extension
needs explicit, bounded version handling; do not append fields and relax EOF
checking. An older supported handoff with no coverage provenance must keep its
ordinary behavior and report coverage unavailable, never infer completeness.
Unknown or malformed wire data still follows existing fail-closed custody rules.

The first slice should expose only a diagnostic coverage check over original
native custody. No public constructor, detached hash or deserialized catalog
assertion can recreate ownership. Ordinary missing-family or optional-budget
rejection must not dispose valid shared custody, consume payload/witness slots
or change ordinary reports. Genuine owner, file, scope or channel invalidation
continues to fail closed. Neither selection provenance nor writer readiness
grants coordinator, route, retry, recovery or runtime authority.

## Reported candidate binding gate

After family provenance is verified, bind only the already reported unrecovered
`file_tuna_data_path_mismatch` candidate inside the original primary, after its
ordinary reports and before custody cleanup. Require exact reference membership
in the original effective candidate records, the selected failure and index,
the original evaluation pair, full and compact arrays and completed views.
Failure class, reason and source origin must agree with that reported candidate.
Copied objects, a different candidate or a detached report are not substitutes.
Do not rerun an evaluator or correlator, or choose a more convenient failure.

The [production primary](../tools/FileTransferOps/AnalyzerOrchestration.ps1)
already calls `TryCapturePayloads` immediately after taking the evaluation pair,
before candidate reporting. Its existing post-report native diagnostic handles
a different frontier failure. The [payload operation](../tools/FileTransferOps/LocalFrontierPrimaryPayloads.cs)
is single-use; an unsuccessful repeated capture can close inputs. A later
content diagnostic must reuse the primary's original owned payload set when
needed, not call capture again or dispose a borrowed set. The existing frontier
witness operation must remain available on its own original contract.

The [legacy content probe](../tests/nLink.SmokeTests.Core/Support/PublicationContentCoverageProbe.ps1)
is not that native seam. It uses a legacy context, fixture source coverage,
whole-input parsed copies and whole-view serialization. Do not copy its observer
injection or query scaffolding into production. Native semantic, full, compact
and original physical ordinals remain distinct; physical line minus one is not
an index into a filtered array.

The later query still requires complete competing-send, publication,
supersession and recorded-security histories, plus independent full and compact
bindings. Select the unique containing second-Tuna-V4 batch by range and
publication before comparing canonical sender commitments. Require the original
rejection chain and a union of 1–64 exact supporting witnesses. Family coverage
alone proves none of these relationships and cannot produce a content pass.

## Bounds and verification sequence

The approved scoped policy supersedes only the time assumptions in the
[earlier integration review](file-transfer-content-production-integration-review-20261001.md):
20,000 milliseconds for admission, 150,000 for optional selection and 180,000
for registered retained worker work on the same original cumulative clock.
Exact ordinary scale waits remain 210,000 and 240,000 milliseconds. Shared
20,000/60,000 constants, independent parent clocks, fresh resource checks,
record, character, line, origin, metadata and operation bounds remain unchanged.
Added provenance work must be charged; the neutral scale result does not measure
headroom for a content query. No further limit increase is proposed here.

1. Implement only original-traversal family provenance and its sealed handoff.
   Verify genuine native capture; actual-set binding despite caller mutation;
   omitted and zero-occurrence families; prefix and bookmark scope; selection
   ambiguity; preserved malformed-row evidence; source/origin/output substitution;
   writer gaps and missing drains; bounded metadata; version compatibility; and
   missing coverage without retiring valid custody or consuming existing slots.
   Use small owned fixtures first, with no additional authority evaluation and
   unchanged ordinary/default-off behavior on PowerShell 5.1 and 7.
2. Bind the already reported candidate in a separate subsequent slice. Verify
   substituted/copy/recovered/wrong-first-candidate rejection, exact origin joins,
   original full/compact parity and original payload reuse after reporting.
   Then extend the bounded query without a second whole-source parsed copy or
   repeated whole-history query per view. Inspect and charge all relevant history.
3. Before production promotion, require a family-shaped offline fixture at least
   as large as the historical 102,215-row, 5,524-boundary, 173,102,783-byte shape,
   with complete relevant histories, positive content cases and ambiguous,
   malformed and exhausted negative controls. A neutral size fixture or universal
   early rejection cannot satisfy this gate. After production integration and
   focused verification, rebuild the portable package and run the targeted
   live-256 scenario with strict escalated preflights. Acceptance cutover and a
   full GUI matrix remain separate gates.

The diagnostic remains `pre_rejection_tuna_content_coverage_v1`, not recovery,
durable-storage proof or a route-cycle pass. Strict exact-resend acceptance,
`Recovered`, failure counts, route/progress state, warning policy, ordinary
verdicts and process exits are unchanged. The coordinator remains the sole
authority; active `file_tuna_v6` remains invalid.

## Review verification

The receipt verifies all 131 pre-existing dirty-file pins, the latest 110-source
and three-assembly checkpoint, focused and scale test receipts, current scale
artifacts and both preserved historical scale artifact sets. The earlier strict
failure is not relabeled. Only this review document is added to the tracked work;
the review's audit files live in the ignored artifact directory. No source/test
implementation, test run, build, GUI/NKN run, commit or push was performed.
