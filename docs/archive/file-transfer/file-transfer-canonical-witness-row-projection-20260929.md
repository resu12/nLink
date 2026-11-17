# Canonical witness input-row projection

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: bounded implementation complete; **56 distinct affected tests pass**.
Fresh native validation is still required. No native 256 MiB characterization,
live NKN run, portable GUI rerun or full matrix was started in this phase.

## Scope and preserved contract

The previous native RCA identified input-row materialization/parsing as the
first operation exhausting the remaining cumulative witness work budget.
Only that projection is now compiled in
`tools/FileTransferOps/LocalFrontierInputRows.cs`, loaded by the existing
`LocalInputReadLease.ps1` Add-Type call. The original resolver invokes it
inside the original selection callback.

The helper retains the original per-row visit before checks/parsing, raw-line
reference identity, case-sensitive path comparison, line/field/cumulative
bounds, structured-field parsing, first-value retention, duplicate flags,
scope/data-session diagnostics, failure-event reference identity and duplicate
failure detection. It returns typed diagnostic rows to the original list.
Partial rows and rejection ordering are preserved. It neither reuses the
generic index Fields dictionary nor filters/skips input rows.

Original state capture already requires inert note properties. The helper
also refuses substituted executable properties rather than invoking them.
No ownership selection, receipt matching, later witness scans, snapshots,
currentness validation, native custody, authority evaluation or publication
moved into the helper. Existing PowerShell scope readers remain unchanged.

The 20,000 ms cumulative work limit, 2,097,152 visit limit and all production
input capacities are unchanged. Compilation/loading remains in the existing
loader; projection itself remains inside original accounting. No coordinator,
transport, route policy, fallback, retry or throughput behavior was changed.

Diagnostic wrapper wording can now name the compiled `Project` call rather
than a PowerShell `Invoke`/throw. The original fixed failure codes, first
rejection, null-selection behavior and visit accounting remain tested; exact
localized exception-wrapper text is not claimed identical.

## Verification

The frozen differential oracle contains the exact original input-row loop,
confirmed by a source substring comparison with the before copy. Five changed
or added PowerShell scripts parse, and all seven changed/added code files pass
the trailing-whitespace check.

Each host passed 275 differential controls across en-US, tr-TR and sv-SE.
Coverage includes case/Unicode keys, first values and duplicate flags, prefixes,
whitespace, scope/data identity and redaction conflicts, path/raw/failure
identity, partial rows, line/field/cumulative bounds, and budget denial before
malformed input. The cumulative-overflow fixture uses an explicitly smaller
test-double capacity; production limits are unmodified.

Implementation controls caught and fixed two compatibility issues: unwrapping
PSCustomObject events loses distinct original object identity, and Windows
PowerShell versus PowerShell 7 use different literal-hashtable comparers for
some Unicode keys. The helper now compares original event references and
clones the caller's empty literal hashtable. Cross-host test-script Unicode
encoding/syntax issues were also corrected.

Final affected lane: **14/14 passed** (projection, scope/data-session and
witness-resolution/profile controls). One relevant custody/preparation
contract lane: **42/42 passed**, including original-input native small-fixture
joins, substitution/mutation rejection, capacity/budget, cancellation and
cleanup controls on both hosts. These small local custody fixtures are not
the 256 MiB native characterization or a GUI/network acceptance run.

Across development there were 76 test executions, including seven initial
failures retained in the artifacts. All 56 distinct tests have a passing latest
result. No unrelated aggregate lane or matrix was run. The final affected
build succeeded; its log contains existing repository warnings but none
identifying the new FrontierInputRows source/test files.

## Synthetic timing only

| Host | Original / compiled, 1,024 rows | Compiled, 73,955 rows | Charged visits |
| --- | ---: | ---: | ---: |
| PowerShell 7 | 699.1218 / 39.3050 ms | 1,476.2108 ms | 73,955 |
| Windows PowerShell | 512.3469 / 75.1247 ms | 5,285.7434 ms | 73,955 |

The large fixture repeats a projected/redacted diagnostic row. It is not
saved native evidence or a substitute for heterogeneous original input.
There is no timing-based test acceptance threshold. These observations show
bounded row projection completing, not that the complete native witness flow
fits the remaining budget. Later scans/currentness/output may expose another
first failing edge.

## Next gate and preservation

Next: rebuild/freeze the current inputs, run strict preflights outside the
restricted sandbox, and perform one fresh targeted native characterization
with the existing budgets and no automatic retry. Confirm that the first
input-row edge advances; report any later failure separately. Do not infer a
route/GUI acceptance pass from synthetic timings or SHA-correct transfer alone.

Entry verification matched 5,386 pinned files and 596 dirty entries against
the prior seal. Before copies, full test logs/TRX, synthetic control outputs,
source audit, preservation audit and final checkpoint are under
`artifacts/canonical-witness-row-projection-20260929/`.
Changes are limited to the projection/loader/resolver, directly affected tests
and this report. No commit, push, cleanup deletion, acceptance relaxation or
saved-capture re-evaluation occurred.
