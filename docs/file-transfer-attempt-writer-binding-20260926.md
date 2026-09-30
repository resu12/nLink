# Test-only attempt-bound Core writer roles

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Follows `file-transfer-attempt-role-binding-20260926.md`.

## Outcome and limits

The explicit attempt test profile now joins each named original app request
to its independently owned native manifest and validated Core writer closure.
The association follows request GUID and actual app PID/creation identity,
not manifest arrival order, writer-array order or matching log labels.

This is a borrowed, test-only role-to-writer join. It grants no live
eligibility, publication completion or acceptance permission. It does not
establish actual source/finalized-destination transfer-file custody, perform
a 256 MiB transfer, or connect the original selected-witness analyzer.

## Original ownership path

The existing parent-attempt, wrapper and GUI entry/dispatch fixture remains
in use. For these cases only, its actual app executable is PowerShell 7 so
the children can host .NET 8 Core assemblies; the parent/wrapper/GUI are each
tested on Windows PowerShell and PowerShell 7. That distinct app executable
is carried in the original launch projection, not substituted after binding.

Each headless child starts the real `FrontierAppLifetime`, registers a real
Core producer through the existing loopback fixture, and completes the real
Core log writer. The producer uses a 1 KiB descriptor that reaches awaiting
acceptance and is reset; it is not the measured transfer. No NKN transport,
paid Tuna session or packaged nLink GUI is started.

The original shutdown function performs native file adoption, acknowledgment
and exit checks. Only window-close behavior is simulated with fixture events;
processes, request bindings, output readers and Core completion are genuine.
After original report drain, the fixture seals the held source, captures its
original final cursor, reads it through the owned native reader, and invokes
the existing typed streaming processor and writer matcher. There is no log
reopen by pathname, copied-source substitute or reset of processing budgets.

The new join requires that exact finalizer binding and original writer match
to belong to the attempt's GUI owner. Each manifest must still be retained by
that owner's original adoption, match its named request and native process,
and agree with the immutable validated writer ID and terminal sequence.
Both original app attachments, successful exits and output drains remain
mandatory. A successful join is issued once; rejected foreign candidates
cannot spend it or retire the original source.

The result has no public constructor or setter. Copied joins cannot match or
dispose the original. Disposal retires the borrowed join only; source lifetime
remains with the existing finalizer. Cancellation, replaced manifests or lost
retained outputs invalidate it, and observed retirement is latched even if
test state is restored. Scalars returned by the join are diagnostic only.

## Verification

The initial two host positives passed, followed by 14 focused cases. During
review, the reversed-arrival fixture's delay was replaced with an explicit
helper-completion event; its two host cases then passed the follow-up rerun.
That event controls fixture order, not writer approval: the original native
adoption and closure checks remain required.

Final results and hashes are recorded in
`artifacts/frontier-attempt-writers-20260926/FINAL-VERIFICATION.json`.

- `focused-final.trx`: 14 passes, covering genuine two-writer closure,
  reversed adoption, changed app identity/terminal sequence, parent
  cancellation, closed retained output and default-loader isolation.
  Native cases also check changed request GUID/creation, copied binding,
  match, attempt and join objects, swapped roles, duplicate use, and retirement.
- `deterministic-order.trx`: both reversed-adoption cases pass again using
  completion-event ordering. No unrelated focused case was rerun for this
  case-specific fixture change.
- `compatibility.trx`: eight existing passes: two role positives, four
  scenario valid/default cases and two writer-only cleanup/adoption cases.

There are 22 distinct final passing tests and 24 final test executions, with
zero failures or skips. The two initial positives are repeats and are not
added to that count. Syntax, whitespace and the unchanged live-dispatch guard
were checked. The join implementation itself did not change after the
initial positives; the post-focused edit changed only ordering in the
reversed-adoption fixture and clarified the no-NKN comment.

The real parent retained-analysis and reconciliation calls remain isolated
stand-ins, and the ordinary GUI finalizer is exercised only through its
existing cleanup/report-drain block plus explicit native reader/matcher APIs.
This is not full finalizer-to-parent/analyzer integration.

## Preservation and next boundary

All 358 preceding verification pins matched before editing. Only four
pre-existing test/support files changed: the explicit loader, parent fixture,
GUI dispatch fixture and scenario fixture's test-only executable selection
(plus its no-NKN comment). The other 226 pre-existing dirty files remain
unchanged. Five new test/support files and this report bring the dirty-entry
count from 230 to 236.

Production code, the Context-only live dispatch, coordinator authority,
acceptance rules and selected-failure pre-verdict reservation are unchanged.
No portable build, live GUI/NKN run, full matrix, commit or push occurred.

Next: establish actual transfer source and finalized-destination custody,
bound to this original attempt and named app/writer identities. That requires
a separate transfer-payload channel; the existing small diagnostic-payload
catalog must not be relabeled or enlarged. Continuous parent adoption and
original full-native selected-witness consumption remain subsequent gates.
The second coordinator-published `file_tuna_v4` requirement is unchanged.
