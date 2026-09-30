# Native frontier diagnostic event-scope implementation

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

Status: implementation and focused verification complete. All 58 unique focused
test cases have a passing latest result; no acceptance promotion.

## Change

Implemented the diagnostic-only identity contract in
`docs/file-transfer-frontier-event-scope-contract-20260928.md`.

- One stateless Core formatter emits the existing `fte1-` token domain from
  original typed session/transfer IDs. Unavailable inputs emit an unusable pair.
- Exactly 81 registered logging lines across eight files gain the two scope
  fields. Their existing raw fields, formatting and redaction are unchanged.
- The optional native resolver reads a per-row scope without editing source
  strings, index events, receipts or evaluated views. It rejects duplicate,
  malformed, unavailable or conflicting identity and retains literal legacy
  matching only when both projected fields are absent.
- Receipt matching and episode partitioning use the exact pair plus the
  original writer. Relevant lifecycle, competing, terminal, pause and repair-link
  rows must resolve before ownership filtering; missing identity cannot hide them.
- Original lifecycle, physical-origin, custody, route/protocol, work-budget,
  witness-count, watchdog-interval and authority checks remain in place.

No coordinator, native payload schema, producer raw-ID validation, route policy,
retry, queue, progress UI, deadline, redactor or acceptance change was made.
Tokens provide diagnostic association, not anonymity or authority.

## Verification

Focused tests cover the token domain, real/repeated redaction, malformed and
duplicate fields, legacy/projected partitioning, and the 81 typed emitter sites.
Native-custody fixtures exercise original redacted-session logging, two transfers
sharing one writer, receipt ambiguity, relevant-event omission and existing
source/replay/capacity/cancellation guards. Parsed-row and captured-receipt faults
are negative-only; no successful witness is manufactured by those faults.

The initial missing-reader regression failed as intended. Two subsequent fixture
issues were corrected: PowerShell array-expression grouping and synthetic IDs
that did not trigger whole-ID redaction. Production redaction was not changed.

The first 27-case native-negative run passed 22 cases; five long-named cases
stopped in native fixture setup before reaching the resolver. Their native
temporary paths reached or exceeded the normal Windows path limit (260--267
characters for finalizer paths). Only new scope-fault artifact directory names
were shortened. Original failed artifacts are retained; persistence, custody
limits and production code were not relaxed.

The new two-transfer cases also required explicitly registering their two-payload
expectation in the test harness; the negative duplicate-receipt injector needed
an explicit PowerShell string conversion. These were fixture-only corrections.
Passing cases were not repeatedly rerun after unrelated fixture edits.

| Focused coverage | Passing cases |
| --- | ---: |
| Formatter, real redaction and registered emitter coverage | 14 |
| Scope reader on both PowerShell hosts | 2 |
| Existing original watchdog native baseline | 2 |
| Original redacted-session native join | 2 |
| Foreign/conflicting/unscoped identity and all terminal/pause families | 27 |
| Two original transfers per writer and duplicate receipt rejection | 3 |
| Existing source, replay, capacity and cancellation custody guards | 8 |
| Total distinct cases, latest result | 58 |

`artifacts/frontier-event-scope-implementation-20260928/TEST-RESULTS.json`
records every run, including failed setup attempts, and reconciles by test ID
(some long test display names are truncated). The latest compiler output has
the same 124 normalized warnings as the prior baseline and no new warnings.

The final preservation seal is
`artifacts/frontier-event-scope-implementation-20260928/FINAL-VERIFICATION.json`.
All 2,443 previous pins are accounted for: 2,423 unchanged at their original
paths, and 20 changed/rebuilt pinned inputs preserved in exact before-images.
There are 21 before-images overall, including the previously clean, unpinned
test-harness file. All 517 prior dirty entries retain their statuses; only the
declared implementation inputs changed. The workspace now has 527 dirty entries
(four additional tracked files and six new files); no unrelated edits were made.

## Limits and next gate

These are deterministic/local native-custody fixtures, not live NKN or a GUI
transfer. The original small-transfer failure has not been rerun or reclassified.
The positive redaction fixture covers an original redacted session ID; both-ID
redaction is covered by the scope reader and formatter controls, not a claim that
all later repair/data-session checks in the original run now pass.
Full original-report hash parity remains part of the next characterization gate;
these native join fixtures verify original evaluated views/proof and custody.

Next is one separately authorized, rebuilt small offline original-process
characterization after this verification is sealed, with strict preflights and
no automatic retry. Preserve the original failure and stop at any newly exposed
proof edge. Live-256 and the full GUI matrix are not the next test.

No commit, push, original-process characterization, live NKN or GUI run was
performed in this implementation step. Existing unrelated work is preserved.
