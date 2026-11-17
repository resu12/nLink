# Collector output custody in the original primary

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-outputs-20260925.md`.
Offline and test-only. Production, acceptance and runtime authority are unchanged.

## Implemented boundary

The preceding milestone retained the four processor outputs through successful
GUI child exit, but deliberately released them before the primary. The new
opt-in fixture keeps that exact accepted output owner, binds it once to the
original retained-source request, and carries four read-only native handles into
the original primary worker. It does not reopen the output paths for analysis.

The original parent source hash is replaced, not supplemented, by one hash over
the held semantic-source handle. The existing native hash helper has a five-second
cap here; using its I/O implementation does not assert full-frontier authority.
The original request, accepted GUI completion and exact returned hash lease are
required for binding. Copied objects, another lease over the same file, and
repeated binding/hash attempts are rejected.

The additional stdin packet is bounded to 16 KiB and bound to the original
request, actual worker PID/creation time and original descriptor digest. It carries
the four handle values and fixed output identities, lengths, hashes and line
counts. The worker validates these against its original writer context and source
identity, then against the actual native files, before the existing adoption ACK.
Descriptor hashing covers bounded metadata, not another source-file pass.

After that ACK, the parent closes its four output handles and both parent source
duplicates. Actual write-open checks still fail with Windows sharing violation
32, demonstrating protection by the primary's adopted handles. The existing
three producer-file handoff, release confirmation and held process lifetime remain.
The primary replaces only its ordinary source path open with a native duplicate;
the original one-shot `SourceReader`, read completion, input owners, evaluation
custody, result pair and views remain in use.

## Raw-evidence read and the discovered conflict

The first positive tests reached the original analysis but exposed another
pathname read: the existing raw bridge-recovery scanner used `File.ReadLines`
on the protected semantic source. Releasing the file, loosening sharing, skipping
that scanner, or claiming full-frontier authority would not preserve this contract.

The opt-in seam instead uses the scanner's existing raw-source input parameter.
Before the original evaluation, it verifies exact original input references,
source path and line origins, and requires complete physical-line coverage in
order. It supplies the same original full-line array, without another file read
or authority evaluation. These reference/origin checks use the original
cumulative work clock and fail closed on missing coverage. The private input
context is not visible to the evaluator; both PowerShell hosts check this.
No production analyzer or native codec was edited.

## Verification

**15 targeted tests pass**, with no failures or skips in the final evidence:

- Four new tests: original-primary custody on Windows PowerShell and PowerShell
  7; wrong output identity and truncated packet rejection before evaluation.
- Eleven directly affected regression tests: four collector-output tests, five
  native-primary tests, and both original GUI parent/finalizer fixtures with
  25 cases per host. This lane ran once after the implementation stabilized.

Final TRX evidence is in `artifacts/collector-primary-20260925/results/`:

- `collector-primary-private-context.trx`: two final positive-host tests.
- `collector-primary-focused-final.trx`: two final rejection tests; its earlier
  two positive tests passed but are superseded by the private-context rerun.
- `collector-primary-contract.trx`: eleven regression tests.

The final positive fixtures are `50ef335b-8c31-4a1e-aa54-879948fde78a` and
`d19bcfcb-d21b-431a-94d4-efdd61b4055c`. Each retains two genuine terminal rows,
93 full and 16 compact records, unchanged inputs and the original three authority
evaluations. Diagnostic work took 114 ms and 108 ms respectively. The existing
4,096-record, 256-boundary, 64-witness and 20-second cumulative limits are unchanged.

Each positive fixture checks nine new parent binding controls, nine substitutions
against the exact primary ownership join, and eight actual write/rename denials
after parent release. Existing request/file controls remain. Copied workers and
repeated source opens are rejected. All four outputs admit write-open after
cleanup. Independent output-hash corroboration occurs only after that cleanup;
it is not an analysis input or another primary hash pass.

Both malformed-packet cases withhold ACK, exit unsuccessfully and perform zero
authority evaluations. The parent keeps its file custody until rejection cleanup,
then all four outputs are released. After worker exit, the request binding is
correctly no longer authoritative even while the parent still owns those files.

The initial four-test lane failed: two positives hit the raw pathname-read
conflict, and two negatives exposed an incorrect fixture expectation of request
authority after worker exit. A single-host error probe also failed and preserved
the underlying raw scanner exception. These five development failures remain
failed, separate from final evidence. The two superseded passing positives are
also recorded separately, not counted twice.

## Remaining proof boundary

This proves the bounded native custody handoff, not full writer provenance.
The first missing edge remains `file_writer_provenance_unproven`; terminal
integrity remains `unproven`, `GuiIntegrityOk=false`, and the primary verdict is
still `FAIL_PROTOCOL_OR_INTEGRITY` / `fail`. `WriterCopyProvenanceProven`,
`PrimaryProvenanceHandoffProven`, `PrimaryFileIntegrityJoinProven` and
`WriterMembershipVerified` remain false. No historical outcome is reclassified.

Next is the per-line producer-to-collector-to-processor/witness provenance join
and writer membership, using the original owners and existing evaluation results.
The present 262,144-byte Regular-v4 loopback has no eligible selected failure
witness. Do not invent one, rerun authority to obtain one, or treat held-file
custody alone as witness provenance. The original writer-witness path still
needs native integration when an actual eligible witness is available.

Forced process death, handle-loss injection, mid-read cancellation, ACK timeout,
foreign-request packet injection and incomplete raw-coverage injection were not
separately exercised on the new path. Older generic controls are not substitutes.
This is not a live-256, canonical route-cycle, full-matrix or production security
boundary claim.

## Preservation

All 4,962 preceding pins matched before work. Thirteen previously dirty test
files changed; the other 120 dirty files remain byte-for-byte unchanged. Four
test files and this document were added. Expected previous-pin changes are those
thirteen test files and the rebuilt test DLL. Production sources, native codecs,
previous artifacts and the preceding verification seal are unchanged.

The final build disabled only the optional Avalonia telemetry task for that
invocation with `-p:UsedAvaloniaProducts=`; no project or user setting changed.
Final verification is recorded in
`artifacts/collector-primary-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network test, full matrix, commit
or push was performed.
