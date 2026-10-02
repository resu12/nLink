# Native content acquisition implementation and remaining RCA

2026-10-01. Base commit `7ddbe15c3b894cf2497207cc39a08189b7578cc0` on
`v0.7.0`. This is a partial implementation of the
[production integration review](file-transfer-content-production-integration-review-20261001.md),
not completion of its acquisition contract.

The default-off native input-selection primitive passes its small offline
custody regressions. The large-input gate fails before that primitive starts:
the original owned-cursor parser rejects at an outer interval just over five
seconds, consistent with its separate five-second guard.
Production integration, complete content acquisition and acceptance promotion
remain unready. The failing scale test remains failing; it is not converted to
an expected success or waived.

## Implemented input selection

[NativeContentAcquisition.cs](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisition.cs)
extends native evaluation custody only under the test compilation symbol
`NLINK_TEST_NATIVE_CONTENT_ACQUISITION`. The
[test loader](../tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1)
compiles that extension with the original native sources. Production loaders
and tools do not load it. Passing `enabled=false` returns without selection,
inspection charges or a callback.

The enabled primitive requires the original native owner, issued pair,
context, arrays and completed-view references. It checks original event/raw-line
membership before and after primary-owned selection. It admits 1–64 distinct,
ordered full ordinals, requires the selected event, and maps witnesses through
the privately held physical origins and compact origins. Detached witnesses
contain ordinals and source-line hashes, not raw lines or custody capabilities.
An ended query cursor cannot be reused.

The primitive shares the original cumulative work clock, seeds prior native
record visits and source code units, and charges its index inspections, path
comparisons, selected lines and origin lookups. It enforces the existing
producer ceilings, including 20 seconds; it does not borrow the evaluator's
60-second allowance. No operation history is queried in this slice.

Its separate one-shot selection slot does not consume the existing frontier
payload/witness/resolution operations. Ordinary missing, substituted or
over-budget inputs do not independently close valid shared custody. Genuine
owner invalidation remains a rejection.

This establishes input identity and mapping only. The test views are inert
identity fixtures, not new authority evaluations. The primitive does not bind
the chosen event to an already reported `file_tuna_data_path_mismatch` candidate
or acquire complete security/publication histories. Every result states
`ContentCoverageProven=false` and
`ContentCoverageMissingEdge=content_family_coverage_unavailable`: the native
semantic packet does not retain the event-family selection needed to prove
that those histories survived filtering. Source membership and sparse positive
witnesses cannot replace that completeness proof.

## Focused verification

The focused run passed 15 cases: 11 new input-selection cases and four directly
affected existing native evaluation/witness cases. New coverage includes both
PowerShell 5.1 and 7, copied arrays/views/events, an omitted selected event,
over-cap selection, inherited record/time exhaustion, a stale owner and mutation
during selection. Positive cases verify gapped physical origins, detached array
isolation and cursor retirement. The final test-profile follow-up passed both
positive host cases; its scale case failed as described below.

The [verification receipt](../artifacts/native-content-acquisition-20261001/verification/verification.json)
pins the focused results, this slice and preserved evidence. Development
failures remain available, including the initial fixture preparation timeout
and a corrected test-profile compilation error. They are not application
failures or successful gates. The original first/middle-slot content-query
fixtures are preserved from the prior checkpoint, not revalidated by the new
identity-only cases.

## Large input RCA

The generated source contains 102,215 size rows, including 5,524 labelled shape
markers, written through the genuine operational writer before original native
registration. Its final source is 208,546,933 bytes, exceeding the historical
173,102,783-byte retained-input size. Those markers are deliberately not
coordinator boundaries, operations or content evidence. This is a size/custody
probe, not the reviewed complete-history fixture.

After increasing only this fixture's preparation/handshake waits, the ordinary
production registration path still rejected during ledger processing. A
[test-only stage probe](../tests/nLink.SmokeTests.Core/Support/NativeContentScaleProbe.cs)
then repeated the original source, processing, final-capture and matching
operations with unchanged checks and budgets. Its extra stopwatch measures
the parse interval; it grants no allowance or custody.

The measured first failure is `owned_cursor_parse`, with
`stream_source_not_current`, parse time 5,007 ms and original cumulative
registration work 7,276 ms. The parser's
[buffered-work guard](../tools/GuiSmokeLogLedgerStreamingProcessor.cs)
rejects after 5,000 ms, separately from the 10,000 ms registration limit.
Registration produced only partial retained/semantic/writer outputs. Native
primary attachment and content selection never began.

This identifies the first failed gate on the generated input. The five-second
guard is the leading cause, not a directly observed rejected predicate: the
measurement surrounds the processing call, while `stream_source_not_current`
can also report a failed custody check. The probe does not capture the parser's
private clock or distinguish those predicates. It does not locate the dominant
CPU cost, prove a historical live-parser failure, or justify raising any limit.

## Next bounded fix plan

1. Directly observe the original rejection predicate and profile owned-cursor
   processing on the pinned generated source. Distinguish the local clock,
   custody failure, parsing, hashing/output writes and native checks before
   proposing a change. Do not replace native scope checks with observations.
2. Review and implement only the measured processing bottleneck, preserving
   complete row inspection, writer/chronology validation, origin capture,
   cancellation and all existing limits. Verify byte/result/origin parity and
   the directly affected rejection controls, then rerun this scale case.
3. After original registration passes, address the separate semantic-family
   provenance and reported-candidate binding edges. Reuse original primary
   indexes and custody; do not add an evaluator, reopen saved hashes as authority
   or borrow the unrelated frontier payload operation.
4. Complete the real-sized content/history and first/middle-slot acquisition
   gates before adding a production option or scheduling new live validation.

No production source or tools changed in this slice. Existing accumulated
runtime changes and all 99 historical live artifact pins are preserved. The
strict exact-resend acceptance, `Recovered`, route authority and historical
verdicts remain unchanged. No portable rebuild, live GUI/NKN run, commit or push
was performed. The test assembly was rebuilt for the new offline fixtures.
