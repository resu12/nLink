# Canonical witness-state diagnostics and capacity decision

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.

Status: **test-only admission diagnostics implemented; 8 focused tests pass.
The state-capacity fix is held for an explicit diagnostic-capacity decision.**
No native transfer, live NKN or GUI run occurred in this step.

## Implemented

`LocalFrontierPrimaryWitnesses.cs` now has a private partial-method observation
hook at the original resolver boundaries. Its implementation exists only in the
`NLINK_TEST_ATTEMPT_DISPATCH` fixture. Without that implementation the compiler
erases the calls and arguments. Normal compilation retains its original bare
catch, return values, original predicates, one-shot checks and cleanup.

The test observer records the first rejection stage, a fixed internal error code
or exception type, call count, evaluation state, full/compact counts, elapsed work
ticks and visit count. It never calls a currentness predicate again, invokes a
callback, reads source files, resets work, changes a limit, or retains input,
evaluation, exception or payload objects. Arbitrary callback exception text is
not exported. Later rejection/retirement cannot overwrite the first observation.

The canonical primary probe emits one JSON-escaped scalar record and adds it to
the existing diagnostic observation. Original resolver omission, report verdict,
same-pair gate and cleanup checks remain unchanged. The record is diagnostic-only
and cannot serve as custody, route or publication authority.

Stages distinguish ownership admission, availability/currentness, diagnostic-state
capture, selection callback, selection validation, physical witness capture,
final currentness and acceptance. A grouped predicate's ordinary false return
is reported as `rejected`, not attributed to an unobserved subcondition. If the
PowerShell resolver exits before calling the native method, the record remains
`not-entered`; its zero counters are not measurements of an earlier custody check.

## Verification

Eight .NET cases passed with no failures/skips, covering Windows PowerShell and
PowerShell 7:

- Two exact-method control-flow tests: twelve inert cases per host compare normal
  and observed compilation for result, cleanup count, currentness/callback calls,
  one-shot state, visit count and retirement. They also check stage/reason, first
  rejection retention, pre-cleanup scalar capture and absence of the observer
  method from normal compilation. Capacity rejection never reaches the callback.
- Two actual fixture-loader compilation checks ensure the real resolver and
  observer compile together and expose only the eight intended scalar fields.
- Two original diagnostic-state tests retain the seven existing hostile-property,
  scalar and capacity controls, plus four pinned capacity boundaries and seven
  event/array/order/note-property mutation checks.
- Two canonical-probe integration tests retain original receipt/batch identity,
  failed route verdict and cleanup behavior.

For minimal synthetic metadata, 21,825 events still exactly fit 65,536 cells;
21,826, 65,536 and 73,885 events still fail with
`frontier_diagnostic_state_capacity`. This is intentional: the implementation
did not silently relax the current limit. The actual diagnostic-state source is
unchanged. These synthetic observations are not another native/authority run.

The focused build used command-local `UsedAvaloniaProducts` telemetry opt-out.
Warnings were outside the changed files; no project settings were changed.
The scoped diff whitespace check passed. No aggregate test lane was run because
only test instrumentation and its directly affected controls changed.

## Capacity contract review — awaiting decision

The prior fresh 73,885-event capture cannot fit the unchanged 65,536-cell state
guard. Moving events outside that counter is a resource-contract change even if
it preserves every acceptance and mutation check. It must not be described as an
unchanged budget or smuggled in as a mere optimization.

Recommended bounded revision:

1. Track original event identities in a dedicated typed representation capped by
   the existing 262,144 input-record ceiling, with exact array identity, order,
   event references, original `RawLine`/`FilePath` note-property identities and
   values. Preserve raw-line reference equality and scalar type/value semantics.
   Do not filter, deduplicate, hash-substitute, or keep only witness-selected rows.
2. Keep 65,536 cells for failure/proof/view metadata. Keep all existing per-array,
   text, scalar, note-property and boundary limits. Account explicitly for the
   separate event-storage maximum; this is the changed resource contract.
3. Preserve 20,000 ms cumulative active work, 2,097,152 visits, 1–64 witnesses,
   original owner/pair checks, single attempt, disposal and non-authority flags.
   Event checks must remain charged; no counter reset or unmetered validation.
4. Test limits, mutations, original failure/report parity and cleanup offline
   before a rebuilt native attempt. Capacity admission alone does not guarantee
   the existing work/visit limits will admit the full native fixture. Stop on any
   newly exposed first edge; do not enlarge those limits to obtain a pass.

This revision affects diagnostic storage, **not route acceptance**. The original
late-publication failure must remain failed. The last native result remains
`native_frontier_unavailable`, zero witnesses and no same-pair join; these new
diagnostics cannot retrospectively identify its first internal rejection.

A user decision was requested before implementing the capacity revision. Until
answered, keep the current capacity behavior and do not rerun the native fixture
merely to reproduce a known capacity blocker.

## Preservation and next action

Entry verification matched all 4,738 prior pins and 577 dirty entries. Expected
changes are the resolver observation hook, three existing test-support edits,
three new diagnostic test files, the rebuilt Core test DLL, this report and
task-local artifacts. The preservation audit checked all 4,738 previous pins:
only the three expected previously pinned sources and rebuilt Core test DLL
changed. All other pinned source, binaries and capture evidence are unchanged.
The current dirty count is 583, including six newly dirty paths from this step.

No commit, push, acceptance change, deadline extension, production state-capacity
change, file deletion or process termination was performed.

Artifacts: `artifacts/canonical-witness-state-diagnostics-20260929/` (focused
TRX/log, preservation audit and final verification seal).

Next action after approval: implement only the reviewed state representation,
run its focused mutation/budget controls, and reassess readiness for one rebuilt
strictly preflighted native gate. Full GUI/live-256 testing is not ready yet.
