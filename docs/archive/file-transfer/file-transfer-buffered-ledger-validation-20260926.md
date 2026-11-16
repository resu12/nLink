# Native buffered-ledger validation: implementation and verification

2026-09-26, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-ledger-validation-contract-review-20260926.md`.

## Outcome

The approved native-reader-only buffered-validation contract is implemented.
Both original-process 256 MiB offline positives now pass, including the
original source-custody and closed-writer joins that previously exceeded the
cumulative five-second budget. Neither five-second limit was extended,
replaced, stopped early, or reset by this change.

The user's subsequent "proceed with the next step" was treated as approval of
the preceding review's bounded implementation request and its explicitly
described detection-granularity tradeoff. That approval does not change live
acceptance, runtime authority, or the remaining GUI/parent custody requirements.

## Implementation

- `LocalFrontierFileSeal.cs` adds a rejection-only buffered-work guard under
  the existing adopter lock. It checks original adopter/reader identity,
  retirement, verified-source state, the active one-shot processing phase,
  the running cumulative work clock, its existing 5000 ms limit, and the
  original cancellation signal. It does not traverse native ownership.
- `LocalGuiWriterFrontierSource.cs` exposes that guard internally for the
  exact original reader. Its public currentness, readability, and read
  validation remain unchanged.
- `GuiSmokeLogLedgerStreamingProcessor.cs` selects the reduced checks only
  for that exact native type in the frontier compilation. Buffered line
  processing and origin insertion still check both the cumulative and local
  deadlines and known cancellation. Generic streams/wrappers retain full
  validation at their existing checkpoints.
- Full checks remain around native reads and parser/cursor refills. Mandatory
  checks cover EOF/completed parsing, before and after retained-output sealing,
  capture construction, and final return. The final return rechecks the output
  capture as well as the source. These checks are outside optional provenance
  catches. Failure closes tentative output ownership and retires the original
  source; copied readers cannot retire the original.

No parser format, source/line/origin limit, route token, protocol version,
runtime decision, retry policy, chunk size, logging policy, or throughput
threshold changes. The earlier conditional terminal-request test binding in
the processor is preserved, not introduced by this step.

Persistent external ownership loss that has not yet caused local cancellation
can now be detected at the next mandatory full boundary instead of the next
buffered record. Intervening work remains tentative. Transient external changes
restored between polls may be missed; there is no continuous-ownership or
atomic cross-process publication guarantee. Returned captures retain their
existing live validation. The cheap guard is never a custody receipt or route
authority, and generic callers cannot select it with a flag or callback.

## Focused verification

Results are retained under `artifacts/buffered-ledger-20260926/results/`.

| Run | Result | Meaning |
| --- | --- | --- |
| `native-buffered-before.trx` | 1 expected failure | Old code still traversed full ownership during already-buffered work. |
| `buffered-focused.trx` | 21 pass, 3 fail | Development run; two expected-text read-sharing errors and one unhandled closed-handle exception in the test harness. |
| `buffered-followup.trx` | 26 pass | Corrected focused cases on both PowerShell hosts. |
| `buffered-publication-final.trx` | 8 pass | Final positive/capture/return cases, including closed output at return. |
| `buffered-compatibility.trx` | 37 pass | Selected existing parser, processing-budget, source, retained-output, and cursor/join cases, run once. |
| `full-size-buffered-final.trx` | 2 pass | Both original-process 256 MiB offline positives. |

There are **28 distinct focused cases** across the two successful focused runs,
plus 37 compatibility cases and two full-size positives: 67 distinct validated
cases. The positive case was renamed from `valid` to `positive`; this is not
an additional test. A follow-up `DisplayName~valid` filter unexpectedly matched
the method's `Validation` suffix and reran all 26 cases; those repetitions are
not counted twice. `buffered-before.trx` selected zero tests because of an
earlier malformed filter and is not verification.

The new test-only loader compiles `BufferedLedgerProbe` and boundary hooks only
under `NLINK_TEST_BUFFERED_LEDGER`; ordinary loaders do not enable this profile.
Deterministic faults exercise local/cumulative expiry with buffered bytes,
original cancellation, persistent external closure, wrapper/copy rejection,
before-begin rejection, EOF expiry, cancellation during output duplication,
capture invalidation, and source/output invalidation immediately before return.
The native ordinary/cursor tests parse another buffered line and insert 512
origins with no additional outer full check, then require rejection at the
next full check. They do not claim an exact count of nested native checks.

Harness corrections do not weaken the production contract: expected-text reads
use compatible read-only sharing while retained handles are held; negative
cases recognize `ObjectDisposedException` while still requiring injected fault,
no returned result, source invalidation, and exclusive-open cleanup of outputs.
Selected compatibility cases preserve generic parsing, origin/cursor facts,
complete-read requirements, one-shot processing, and original custody joins.

## Full-size evidence

Both current runs process 54648 log lines and complete the original writer and
source joins while the unchanged cumulative budget is enforced:

| Host | Artifact directory suffix | Source log bytes |
| --- | --- | --- |
| Windows PowerShell | `powershell-writer-source-read-cross-process-valid-bebf7e1282eb415da7bfcbd0440b6712` | 40814297 |
| PowerShell 7 | `pwsh-writer-source-read-cross-process-valid-6b70859e9e674f65bb8b0ba4f881e241` | 40814301 |

Directories are under `artifacts/frontier-cross-process-receive-20260926/`.
Both contain `attempt-source-custody-ok` and `attempt-role-writers-ok`, with
empty GUI-worker stderr. Both original sender and receiver report 268435456
bytes and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The receiver hashes the retained native destination in the original process.
The successful path does not emit exact processing milliseconds, so no new
timing measurement or performance-margin claim is made.

These are the explicit DevLocal named-pipe fixtures. The scripts' historical
GUI/route-mode labels do not make these live GUI/NKN or Tuna acceptance tests.
No live network, portable rebuild, full matrix, commit, or push occurs here.

## Preservation and next milestone

All 294 pins in the previous current checkpoint matched before implementation.
Of 281 pre-existing dirty files, 280 remain byte-for-byte unchanged; only the
existing dirty ledger processor is intentionally updated. Three previously
clean tracked files, three new test/support files, and this report bring the
dirty inventory to 288. Existing work and artifacts are retained. Source and
new-file whitespace checks pass. Recovery hashes and all test outcomes are in
`artifacts/buffered-ledger-20260926/FINAL-VERIFICATION.json`, chained to the
review-only checkpoint.

The log-processing edge is closed for these two offline full-size cases.
Next is the outstanding original native destination-evidence handoff through
the GUI/parent path, with its original-owner and failure-closure checks. This
step does not implement or certify that handoff. Do not substitute saved log
lines, matching hashes, copied objects, or reopened paths for original custody.
Live testing is not the next gate yet. The eventual route-cycle gate still
requires a second coordinator-published `file_tuna_v4`; SHA success or fallback
completion alone remains insufficient.
