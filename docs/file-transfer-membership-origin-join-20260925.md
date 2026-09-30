# Owned writer-membership and line-origin join

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-capture-timing-alignment-20260925.md` and implements
the next bounded, offline/test-only provenance connection.

## First missing connection

The original native verifier already checks the entire retained writer stream,
its hash, writer sequence/scope and exact membership of every original full-input
record. However, it discarded the physical writer positions after verification.
The separately owned processor-origin map therefore could not be checked against
those exact verified positions without another read or reconstruction.

The shared verifier now retains its existing `position + 1` result for each
original full-input record **only when both** `NLINK_TEST_TERMINAL_REQUEST` and
`NLINK_FRONTIER_LEDGER` are defined. It neither reopens nor rereads a file, reparses
records, hashes again, or evaluates authority again. The bounded position array
is attached by an internal constructor to the original native membership result,
with exact writer/full-input/snapshot/work-clock identity. No caller-authored map
or serialized diagnostic can replace it.

This changes shared source (`LocalRetainedSourceRequest.cs`) under test-only
guards and makes `NativeMembership` partial. Normal production-compiled behavior,
the original constructor and public API remain unchanged. Both production host
loaders were verified to omit the new collector fields/methods and retain the
original three membership properties.

## Implemented join

The existing original `CollectorMembershipForTest` owns the new join. Its method
requires the exact worker, four-output owner, source reader, primary custody,
completed evaluation pair, context, full/compact arrays, views, full-origin array,
original snapshot and original running cumulative clock. Fourteen substituted
or copied arguments are rejected before any join-record visit, without consuming
the original eligible attempt.

For every full-input record, the join checks complete original semantic-source
coverage and compares the processor's writer coordinate with the native
verifier's retained exact-match position. Only a fully successful pass retains
the read-only coordinate rows. One eligible attempt is consumed on success or
coordinate failure; a corrected retry cannot issue a result. Copied-result
disposal cannot retire the original. Native file/primary cleanup invalidates
both the membership and joined-row access.

The new integer/reference traversal is charged at one additional record visit
per input row against the fixture's existing combined 4,096-visit ceiling:
`7 * full + 4 * compact + membership visits + join visits`. The original running
20-second cumulative limit is checked throughout. Native byte/code-unit,
membership-time, capture and completion budgets are unchanged. No additional
character-content scan is performed.

The result remains private to the original worker's lifecycle. Output-only
diagnostics appear under `CollectorWriterMembership.OriginJoin`; there is no
input API for those rows and no new publication, failure-witness, route,
terminal-integrity or completion authority.

## Focused evidence

Both original positive membership tests pass with the new join:

- `cbf35039-65a4-4b5e-a5c0-7243aea29438`: 93 joined full-input rows, 135 writer
  records, 363 membership visits, 93 join visits, 1,171 combined visits; 200 ms
  diagnostic cumulative work.
- `8db76bb6-9273-4f33-ad3c-e65424b75fe9`: the same row/visit counts; 274 ms work.

Both preserve the original three authority evaluations, two genuine terminal
rows and unchanged input views. They perform zero additional writer reads and
retire the join during original cleanup. Post-run assertions independently
corroborate the joined coordinates against the already-checked processor map
and original producer/retained/writer/semantic/copy files; those later reads do
not enter the primary as evidence.

Two negative tests corrupt the original live map's first semantic coordinate
to another valid, in-range writer coordinate while leaving file bytes and
native membership unchanged. The join rejects on its first record, publishes
no rows, and rejects retry even after the test restores the original coordinate:

- `c2b80702-a843-46fe-b416-bc6f3acbbf7c`.
- `fe98e5b8-2f4e-49cd-9ddf-36d470e90542`.

This is a deliberate test-only in-memory corruption control, not a claim that
reflection is a production security boundary or that a malformed packet was
injected. Multi-writer interleaving, forced join-budget exhaustion and process
death during this new join were not separately injected.

Results are in `artifacts/collector-membership-origins-20260925/results/`:

- `membership-origins-focused.trx`: four passes (both positive hosts and both
  production-loader checks), two diagnostic-serialization failures.
- `membership-origins-empty-rows.trx`: both corrected negative cases pass.
- `membership-origins-regression.trx`: four passes: foreign/partial membership
  still withholds the join, and non-membership collector-primary cases remain
  unchanged on both hosts. This directly affected lane ran once.

The final evidence covers 10 passing tests. Across all 12 attempts, 10 passed
and two failed; the failed diagnostic-development attempts remain recorded.

The initial negative cases correctly rejected the coordinate substitution but
their empty diagnostic row list serialized as null/object rather than an array
across the two PowerShell hosts. The fix uses an explicit empty `object[]`.
Only those two cases needed rerunning; neither join logic nor acceptance was
changed. Their original failed records are retained, not reclassified.

## Remaining boundary

The new optional post-report join is not yet consumed by the original primary's
integrity/acceptance gate. Its original verdict therefore remains
`file_writer_provenance_unproven`, terminal integrity `unproven`,
`GuiIntegrityOk=false` and `FAIL_PROTOCOL_OR_INTEGRITY` / `fail`.
`WriterCopyProvenanceProven`, `PrimaryProvenanceHandoffProven`, payload-integrity
join and canonical scenario proof are not promoted by this milestone. No
eligible failure witness or completion is manufactured.

Next: review how the original primary should consume this owned joined
provenance together with terminal/source/destination integrity, without another
authority evaluation or changing the reviewed acceptance contract implicitly.
No live-256 or full-matrix run is justified by this test-only result alone.

## Preservation

All 9,025 prior file pins matched before work. Six previously dirty test files
changed; the other 147 dirty files are preserved. The shared verifier was
previously clean. Three new test/support files and this document were added;
the rebuilt test DLL is the only other changed prior pin. Prior artifacts,
timing-alignment code, runtime/coordinator code and acceptance predicates are
unchanged. No commit, push, live/network run, portable build or full matrix was
performed. The test assembly was built once; guarded support is compiled fresh
in each isolated PowerShell process.

The final verification record is
`artifacts/collector-membership-origins-20260925/FINAL-VERIFICATION.json`.
