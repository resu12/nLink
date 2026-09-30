# Terminal correlation tokens and offline source binding

2026-09-25, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the approved narrow diagnostic fix from
`file-transfer-terminal-source-binding-rca-20260925.md`.

## Completed result

The same-transfer offline source/terminal binding now has a genuine positive.
`LogTransferInfo` adds `terminal_session_id` and `terminal_transfer_id` only to
`transfer_terminal` messages, using the existing `fte1-` evidence-token helper
(first 24 lowercase hex characters of SHA-256 over the original UTF-8 identity).
The values come from the actual Service terminal context. They are diagnostic
correlation, not ownership capabilities or Coordinator authority.

The redactor is unchanged. The real fixture's raw session and transfer IDs
remain `[redacted]`; raw paths and secrets retain their existing redaction.
No token fields are emitted on nonterminal records. The production change is
four added lines and an appended logging suffix in
`SessionFileTransferService.cs`; there is no route, transfer-state or transport
behavior change.

The test captures the same real 256 KiB transfer, terminal snapshots, completed
writer and persisted files. Its private owner binds original physical log lines,
writer identity/sequences, native log-file identity and size/hash. Checks reopen
and lease the same log, reject changed origin lines/text, validate both terminal
tokens and directions, and freshly verify complete source/destination bytes
while the log remains leased. Tokens or matching hashes alone cannot supply
another owner's capture. Serialized diagnostics are outputs only.

Latest positive:
`artifacts/terminal-source-tokens-20260925/8e8f4f5a-6756-4038-8f81-5e43c4b9f958/`.
It contains 135 original records, terminal lines 127 and 131, and 32 detached
negative controls. Status is `verified_offline_source_terminal_binding` under
scope `offline_service_source_terminal_binding`.

## Verification

The stabilized focused lane passed **18 tests, 0 failures**:
eight source-binding/redaction cases, eight existing terminal-integrity cases,
and both existing real-transfer publication-use cases. Receipt:
`artifacts/terminal-source-tokens-implementation-20260925/results/terminal-tokens-final.trx`.
After strengthening the swapped-token and original-line controls, only the
affected positive/control test was rerun; it passed in
`terminal-tokens-origin-controls.trx`. The 18 tests above are distinct cases;
the follow-up is an additional execution of one of them, not a nineteenth case.

| Source-binding case | Required observed result |
| --- | --- |
| Actual completed transfer | Verified offline binding; raw IDs still redacted |
| Two genuine transfers with identical payload bytes | Each own capture passes; four foreign-owner/rebinding/relabeling/row-replay checks reject |
| Actual same-length log corruption | `log_changed` |
| Byte-identical replacement log | `log_identity` |
| Removed log, retained at an adjacent test path | `log_missing` |
| Actual same-length destination corruption | `destination_hash` |
| Actual canceled transfer | `terminal_not_completed` |

The two replay transfers share one isolated writer scope and have distinct
private transfer owners, sessions and transfer IDs. This is not an independent-
peer or cross-process primary-capability test. Each file-mutation case first
proves a real positive, then changes the actual test file and rejects a fresh
check. Before/after diagnostics are retained separately. The result's captured
descriptors describe the original observation, not the subsequently altered file.

Detached controls cover missing/duplicate terminal records, absent/malformed/
duplicate/foreign/swapped tokens, wrong event/direction, errors, incomplete
state, size/progress mismatch, copied owner and changed physical origin text
or line. Actual source leases also reject writing and moving during observation.
The earlier terminal-only file-integrity and real cancellation controls remain.

Two development receipts contain redaction-test assertion failures, not runtime
failures: the test initially assumed the precise replacement layout of an
all-letter ID and then a slash-separated path. Assertions now require tokens
to survive while complete raw IDs, sensitive path components and secrets do
not, without imposing a new redaction layout. The seven actual binding cases
passed before that correction; the final full focused lane passed afterward.
Development receipts `terminal-tokens-initial.trx` and
`terminal-tokens-focused.trx` are retained, not treated as all-pass receipts.

## Scope and next boundary

This closes the narrow offline producer/binding gap. It does not complete the
original-primary handoff, canonical scenario scope, five-route cycle or closed
independent-failure inventory. All diagnostics explicitly leave
`CanonicalScenarioProven=false`, `WholeScenarioHandoffProven=false` and
`AcceptanceChanged=false`. Older redacted captures are not reclassified.
The existing strict mismatch, independent route-cycle failure and alternative
shadow remain unchanged.

Next is the owner-bound handoff into the original primary custody, preserving
its original evaluations and every independent failure factor. This result does
not justify an acceptance cutover or a live-256 run. No analyzer authority
evaluation, portable build, live test, aggregate matrix, commit or push ran.

## Preservation

Only the existing Service logging method and source-binding test file changed;
this document was added. All other 92 baseline dirty files remain byte-for-byte
unchanged. Reversing the scoped logging hunk with its original line endings
reproduces the Service file's baseline SHA-256, confirming earlier work in that
file was retained. Prior pinned artifacts remain unchanged except the intended
Service/test sources and rebuilt test DLL. `git diff --check` passes.
The seal is
`artifacts/terminal-source-tokens-implementation-20260925/FINAL-VERIFICATION.json`.
