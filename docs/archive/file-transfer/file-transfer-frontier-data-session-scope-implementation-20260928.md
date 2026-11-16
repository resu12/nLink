# Native frontier diagnostic data-session implementation

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Status: **implementation and focused verification complete**.
All 63 unique focused test cases have a passing latest result. This is not an
acceptance promotion or a successful rerun of the original characterization.

## Implemented boundary

Implemented the reviewed contract in
`docs/file-transfer-frontier-data-session-scope-contract-20260928.md`.

- A three-line addition to the existing stateless formatter projects the original
  typed data-session identity into the existing `fte1-` evidence domain.
- Exactly five logging lines in `SessionFileTransferService.PullTransferSessionV6.cs`
  gain `frontier_scope_data_session_identity`. The current-context site computes
  its existing identity once and reuses it for both fields. Three sites retain
  their captured wake; the forced-state site retains its validated progress
  receipt. Raw fields and generation values are unchanged.
- The optional native resolver derives a per-row diagnostic record without
  editing source, index, receipt or evaluated views. It validates explicit
  projected/legacy modes, raw agreement, known redaction forms and duplicates.
- Required wake comparisons and conditional watchdog scope comparisons use the
  same normalized identity. Generation checks remain separate. Same-owner
  changed identity is rejected, not filtered away as a foreign event.
- Relevant regular opaque diagnostics reject identity duplicates and invalid
  projections. Unrelated rows and coordinator/marker paths do not acquire a
  global data-session parsing requirement. Existing tolerated range duplicates
  and all terminal/pause invalidators remain unchanged.

No coordinator authority, native payload, producer raw-ID validation, route
invariant, deadline, queue, retry, redactor, progress UI or acceptance change.
No source migration, backfilled log, reconstructed receipt or additional
authority evaluation was introduced. These tokens remain diagnostic evidence,
not authority or anonymity.

## Verification

Evidence: `artifacts/frontier-data-session-scope-implementation-20260928/`.
The precise results are in `test-summary.json`; earlier failed test artifacts
are retained alongside the passing reruns.

| Focused group | Unique passing cases |
| --- | ---: |
| Formatter, real/repeated redaction, exact emitter sources, both scope readers | 32 |
| Original redacted data-session native join and contradiction controls | 14 |
| Existing native identity/custody/replay/capacity/cancellation compatibility | 15 |
| Directly affected checkpoint-continuity fixture compatibility | 2 |
| Total, latest result per exact case | 63 |

The reader first failed as expected in both PowerShell hosts because the new
reader did not yet exist. After implementation, the cases pass in `pwsh` and
Windows PowerShell. New formatter tests verify literal UTF-8 hashing, sentinels,
token-looking inputs, genuine long-ID redaction to `[redacted]:1`, repeated
redaction and the five exact typed sources.

Original native-join fixtures use the real production log/receipt/custody path
with long transfer IDs. Their positive cases match the receipt's original
data-session token while retaining the original unresolved primary failure and
false authority flags. They include an unrelated writer's repeated raw identity
field in an already-retained fixture event. No positive native witness is made
by replacing captured source or receipt text.

Negative-only local reader/decoded-row injections verify wrong wake identity,
independent generation changes, same-generation watchdog identity changes,
missing identity, unknown watched events, opaque duplicate/conflict rejection,
and terminal/pause precedence. These injections do not mutate original native
strings, physical origins, receipts or evaluated views.

Existing native compatibility controls cover original redacted session IDs,
two transfers sharing one writer, duplicate receipt rejection, original source
identity, replay, witness capacity and cancellation. Native joins retain two
original evaluations, unchanged evaluated views/proof, bounded witnesses and
the existing custody/lifetime checks. Full ten-report parity for a newly rebuilt
original-process characterization remains a later gate, not a claim from these
unit fixtures. The previous run's reports are unchanged.

### Test setup corrections, not production fixes

- The initial build stopped when optional Avalonia build telemetry attempted an
  out-of-workspace write. Subsequent builds used the command-local
  `-p:UsedAvaloniaProducts=` setting; no application configuration changed.
- The new native fixture initially waited for an unredacted transfer ID. Its
  test-only lookup now uses the existing redactor's expected value.
- New fixture assertions were corrected to check every original recorded recent
  arm, rather than assuming one, and to place the unrelated duplicate control
  in the fixture's existing retained event set. No native catalog was widened.
- Two existing in-process continuity tests could only read an unstamped recent
  ring at the sandbox-inaccessible default log path. Their isolated rerun used
  the existing process-local acceptance-log settings under the workspace; both
  passed without a source change. This was not a live NKN/setup RCA.

No unrelated aggregate suites were run. The build emitted existing test-project
warnings; this report does not claim a warning-free clean build.

## Review, preservation and next gate

The incremental diff is saved separately from the accumulated branch changes.
The production emitter comparison exactly reduces to five inserted fields and
one reused pure identity local; whitespace checks passed. All 3,136 prior pins
were checked at entry. Eleven intended source/test files and five rebuilt pinned
binaries changed, with verified before-images retained. All other prior pins
and dirty contents/statuses are unchanged. The new fault helper and this report
bring the dirty-entry count from 529 to 531.

No commit, push, portable rebuild, original-process characterization, live NKN,
live-256 or full GUI matrix was performed. Native unit fixtures are not a live
transfer acceptance test.

Next: separately gate one rebuilt small offline original-process
characterization with strict preflights and no automatic retry. Preserve the
original verdict and stop at any newly exposed first proof edge. Live-256 and
the full GUI matrix are not the next validation step.
