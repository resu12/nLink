# Terminal/source binding: implemented probe and first missing edge

2026-09-25, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Test-only work following `file-transfer-same-attempt-integration-contract-20260925.md`.
The full binding milestone is **not complete**. Its first producer gap is now
reproduced by a real transfer, not inferred from separately owned artifacts.

## Result and RCA

The genuine 256 KiB Regular-v4 transfer completes on both Services, without
errors, and its source and persisted destination have identical complete
size/hash. The isolated writer also finishes successfully. However, both
`transfer_terminal` records replace `session_id` and `transfer_id` with
`[redacted]`. Exact terminal-to-transfer identity therefore cannot be established
from those records. This is an evidence-correlation gap, not a failed transfer.

Final capture:
`artifacts/terminal-source-binding-20260925/abd097f2-1202-4f23-a522-060d8511ffd9/`.
The immutable observation contains 135 physical records, one genuine writer,
continuous sequences 1-135 and terminal records at lines 127 and 131.
Its measured log is 165,959 bytes. The associated real transfer files are at
`artifacts/terminal-integrity-evidence-20260925/10514df6fa5e4c4bb9e90c24a6b40e64/`.
The standalone diagnostic explicitly reports `Status=unproven`,
`FirstMissingEdge=terminal_scope_redacted`, `SameAttemptBindingProven=false`.

Cause:

- `SessionFileTransferService.cs:24050` formats terminal messages with raw
  session/transfer IDs through `LogTransferInfo`, then `LocalOperationalLog.Info`.
- `SensitiveDataRedactor.cs:110` applies its long-secret-token rule. The existing
  fixture's unmodified IDs meet that rule, so terminal scope is erased.
- The snapshot identity protection at `SensitiveDataRedactor.cs:290` applies
  only to `filetransfer_coordinator_` records. Existing data-path/security
  evidence protections do not cover `transfer_terminal` either.

Neither physical source identity, matching payload hashes, a clean terminal
pair nor a shared scenario label recovers the missing per-transfer identity.
The inbound saved path also contains a redacted directory component; that path
is not used as a substitute identity. No raw fields were rewritten, identifiers
shortened, redaction disabled or test-authored success injected.

## Implemented and verified

`FileTransferTerminalIntegrityEvidenceTests.SourceBinding.cs` runs the existing
real-transfer helper in a fresh child test host, with its private log scope set
before logger initialization. It observes both actual terminal records before
draining, then reads the frozen log under a Windows read lease. It verifies
native file identity, length/hash, original physical lines, actual writer ID,
continuous writer sequences and the final drain scope/sequence. A post-capture
writer-sequence check also rules out later write attempts during that interval.

The test rejects the incomplete-writer state and a detached copied owner. It
confirms that write and move attempts against the leased source fail, and that
the file identity/hash remain unchanged. The genuine redacted terminal pair
must return the missing-identity result, never a binding pass. Exported JSON
is diagnostic output only; the parent checks the real child exit and does not
import JSON as an owner-issued gate result.

Final focused receipt:
`artifacts/terminal-source-binding-implementation-20260925/results/source-binding-final.trx`:
**1 passed, 0 failed**. This is a passing regression for rejection of insufficient
evidence, not a passing same-attempt integration. The existing eight terminal
tests and their helpers are unchanged; they were not rerun for a `partial`
class modifier. No aggregate lane, portable build or live test ran.

The first development run failed in the new parser because unrelated log
records can repeat descriptive `route` fields. The test now parses only writer
envelope fields for unrelated records, while still requiring unique fields in
the terminal/start/drain records it consumes. The original log is unmodified.
That corrected run passed; the final run above adds the writer-sequence check.
Both earlier receipts are retained as `source-binding-probe.trx` and
`source-binding-boundary.trx`.

## Recommended bounded fix plan (not implemented)

This next step crosses the current test-only boundary: it requires a small
production diagnostic producer change, not acceptance or transport changes.

1. Add separately named terminal session/transfer evidence tokens, derived
   from the actual terminal context at the existing Service logging call.
   Reuse the established `fte1-` SHA-256-derived evidence-token convention
   (`SessionFileTransferService.CoordinatorSnapshots.cs:3836`). Keep raw IDs
   and paths subject to current redaction; do not broadly exempt terminal text.
   These tokens are correlation evidence, not route/runtime authority and not
   sufficient ownership proof by themselves.
2. Verify exact token-format preservation and continued secret/raw-ID redaction.
   Reject absent, malformed, swapped or foreign terminal tokens. Do not change
   the existing primary parser/classifier or interpret old redacted records as
   newly proven. If a redactor change is needed, scope it to the exact terminal
   event and reserved token format, never arbitrary values.
3. Complete the same-attempt test's owner/source/file join using genuine new
   records. Add the pending real positive, foreign-attempt replay, missing or
   duplicate/wrong-direction records, changed/replaced log and changed-output
   checks. Retain a negative for terminal records without usable identity.
4. Run only the affected producer/redaction tests and terminal/source tests.
   Keep scenario scope, full route cycle and independent failure accounting
   unproven until their separate same-attempt obligations are implemented.

Do not build a cross-process scenario receipt or run live-256 to work around
this gap. The existing independent live-route proof failure remains unchanged.
No authority evaluation, runtime route mutation or acceptance cutover is part
of this fix plan.

## Preservation

Of 92 baseline dirty files, 91 are byte-for-byte unchanged. The only change to
the existing terminal test is the `partial` modifier; reversing that modifier
reproduces its baseline SHA-256 exactly. This milestone adds the adjacent test
file and this document. Of 59 prior pins, only that test source and the rebuilt
test DLL changed. Historical artifacts, production code and acceptance remain
unchanged. `git diff --check` passes. The verification seal is
`artifacts/terminal-source-binding-implementation-20260925/FINAL-VERIFICATION.json`.
No commit or push was made.
