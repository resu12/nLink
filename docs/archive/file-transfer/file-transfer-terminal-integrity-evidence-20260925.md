# Genuine offline terminal and whole-file integrity evidence

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, with existing uncommitted work.
Implements the bounded terminal-only milestone from
`file-transfer-whole-scenario-evidence-review-20260925.md`.

## Implemented result

`FileTransferTerminalIntegrityEvidenceTests.cs` transfers a deterministic
262,144-byte source through two fresh `SessionFileTransferService` instances
and the existing Regular-v4 loopback transport. It uses public send/accept APIs
and the existing temporary-file/final-move destination helper. No destination
prefill, seeded progress or manually assigned terminal state is used.

The fixture retains both actual terminal snapshots before Service disposal,
then measures the persisted destination after finalization and closure. The
private check requires exact attempt ownership, session/transfer and opposite
directions, both `Completed` states without errors, exact size/progress and
reported hashes, plus independently measured source/destination size and
SHA-256 equality. Extra bytes are rejected, not ignored as a valid prefix.

The source remains read-locked across initial hashing and transfer. Windows
read-sharing leases exclude writers/replacement during post-transfer hashing.
Paths come from the fresh fixture's owned directory; no supplied JSON, path or
`integrityOk` Boolean is accepted as evidence. A capture also retains the exact
private fixture-owner reference: another successful attempt cannot reuse it,
even with identical file bytes or a relabeled attempt ID.

Output scope is explicitly `offline_service_terminal_integrity`, schema
`nlink.test-terminal-integrity/v1`. These files are test diagnostics, not a
production capability or an issuer-bound whole-scenario receipt. A completed
capture describes that measurement interval; it does not certify the file can
never change afterward. Fresh observation rejects subsequent changes.

## Verification

Final focused run: **10 passed, 0 failed**, receipt
`artifacts/terminal-integrity-implementation-20260925/results/terminal-integrity-final.trx`.

- One real positive transfer plus 26 detached controls: missing/failed/nonterminal
  peers, errors, wrong session/transfer/direction/attempt, inconsistent size,
  progress or reported hash, wrong source/destination identity and missing output.
- Five fresh completed transfers followed by real file changes: missing
  destination, truncation, trailing byte, same-length corruption and changed
  source. All fresh checks reject with the specific expected reason.
- One real canceled transfer while its first payload submission is pending.
  Both peers become `Canceled`, with zero delivered bytes and no finalized
  destination. This is not a mid-file cancellation or recovery claim.
- One cross-attempt test runs two independent completed transfers with identical
  payload hashes and rejects replaying the old capture, including a relabeled copy.
- Both existing publication-use real-transfer regressions pass, with and
  without the authority-session interface.

The final run creates nine fixture directories (the cross-attempt case uses two).
The standalone positive and detached controls are at
`artifacts/terminal-integrity-evidence-20260925/14ce0f20a88147e299b0a480296012f3/`.
Cancellation evidence is at
`artifacts/terminal-integrity-evidence-20260925/4db1dd57b84f49c5b301142f67f9bfa9/`.
All latest source/destination measurements (16 existing files) independently
match their retained size/hash diagnostics. Intentionally altered files retain
separate before/after diagnostics; original observations are not rewritten.
The final verification seal lists every final fixture and pins its artifacts.

The initial run passed seven of eight new tests. Its changed-source control
encountered a remaining read handle: sparse-pump disposal runs asynchronously
after Service disposal. The fixture now tracks only the source streams it
created and waits for the Service to close them naturally before returning.
It does not force-close active streams, loosen sharing, sleep for an assumed
duration or change runtime cleanup. The five file-change tests then passed,
followed by the final ten-case run. Development receipts are retained:
`terminal-integrity-initial.trx` and `terminal-integrity-file-controls.trx`.

Only the test project was rebuilt, with optional Avalonia build telemetry
disabled through `-p:UsedAvaloniaProducts=`. No aggregate analyzer/contract suite,
portable build or GUI matrix was run.

## Limits and preservation

This proves a small offline terminal/integrity predicate with genuine Service
completion and persisted bytes. It does not exercise Tuna, authenticate a
network payload, complete the five-route cycle or prove canonical live-256
scope. It must not be joined to the earlier two-writer capture: those are
different attempts. The existing alternative shadow remains unchanged and
unproven; all strict results and `Recovered` semantics remain unchanged.

Next is the same-attempt handoff/integration boundary from the review: actual
scenario scope, complete Coordinator-published route cycle, terminal evidence
from that same transfer, and a closed inventory of independent failure factors.
In particular, the earlier two-pair capture still has a failed live-route proof;
admitting its recorded rejection cannot waive that failure. Do not promote this
terminal-only test to a whole-scenario positive or run live-256 on that basis.

All 89 baseline dirty-file contents and all 33 pins from the preceding review
seal are preserved. Only the new test file and this document were added;
the generated test DLL changed through the required build. `git diff --check`
passes. Verification and hashes are recorded at
`artifacts/terminal-integrity-implementation-20260925/FINAL-VERIFICATION.json`.
No production code, acceptance policy, receipt profile, historical verdict,
diagnostic budget, live run, commit or push changed in this milestone.
