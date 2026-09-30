# Production wrapper-to-GUI attempt dispatch

2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree changes.

## Outcome

The normal wrapper now forwards the parent's bound canonical attempt into its
original GUI request. The GUI opens and consumes that original scope once,
retains it privately, and closes it with its owner. This completes the attempt
link, **not** the protected transfer-pair integration. The actual live scenario
call remains Context-only; it still receives no `-FrontierOwner`.

The existing diagnostic opt-in remains default-off. No acceptance rule,
coordinator authority, resource limit, throughput setting, retry policy or
route invariant changed. No GUI automation, live NKN, portable rebuild, full
matrix, commit or push ran.

## Changes and boundaries

- `LocalGuiWriterCanonicalDispatch.cs` shares the former test-only forwarding
  and consumption checks with the normal loader. There is one original
  dispatch object and one ownership chain, not a parallel test implementation.
- The wrapper compares the actual child environment, executable and timeout
  with the parent's exact bound projection before the original launch. It
  resolves and pins the GUI host. Missing or mismatched scope is omitted without
  replacing the ordinary launch, watchdog, output readers or exit result.
- GUI consumption requires the current original owner and parent process,
  matching process creation times, request identifiers, registration bytes,
  canonical attempt fields and unchanged source/forwarded bytes. Copied objects,
  wrong attempts, changed scope and repeated consumption are rejected.
- The entry block only attempts consumption for the single canonical GUI
  scenario. It retains the dispatch in a private variable, never in the UI
  context, environment, formatter or a serialized receipt.
- Owner disposal now retires the dispatch's native ownership and handles.
  Explicit dispatch disposal closes test-only role resources outside the owner
  critical section, avoiding the reverse role/owner lock order. That partial
  resource hook is absent from the normal profile.
  Six downstream test partials have only a mechanical dispatch-type rename.
- The fixture forwarding adapter now exercises the production wrapper call,
  rather than injecting a second forwarding implementation.

The payload/writer/role test APIs remain excluded from the normal loader. A
consumed attempt grants neither route ownership nor writer custody, transfer
integrity, publication completion or live-proof eligibility. The single primary
evaluation/resolver ordering and terminal writer reservation are unchanged.

## Verification

Evidence is under
`artifacts/production-attempt-gui-dispatch-20260929/`.

The test harness build passed with 343 existing warnings and zero errors.
The pre-implementation normal-loader test failed on both hosts for the expected
missing production dispatch API.

Eighteen distinct new cases have passing results:

- Normal-loader API/default-profile checks on Windows PowerShell and PowerShell 7.
- The actual parent and wrapper function bodies, plus the actual guarded GUI
  consumption entry, run with headless child scripts and no application UI.
- Enabled and disabled capture, wrong size, second attempt, changed wrapper
  environment, changed GUI environment, wrong host and nonzero GUI exit, on both
  hosts.
- Embedded controls reject changed values in every projection slot, copied
  owner/ticket objects, multiple scenarios and repeated forwarding/consumption.
  They verify owner-linked retirement and that the live scenario remains gated.

The first new chain run passed 16/18. The two disabled cases exposed a fixture
error: it expected an ownership handoff when diagnostics were off. Only that
expectation was corrected; both default-off reruns passed. Two enabled cases
were rerun after strengthening the duplicate-forward assertion and passed.
These reruns are not counted as additional distinct tests.

The compatibility lane passed once: the existing 22 dispatch controls,
two original-role launch cases and four protected-source valid/cancel cases.
The source fixtures check local protected-file custody; they do not perform a
new live or offline end-to-end 256 MiB network transfer.

Final review identified a lock-order risk, not an observed test failure: owner
teardown held its lock while invoking a role-resource hook whose callers can
take the locks in the opposite order. Native ownership retirement is now
separate from explicit role-resource disposal. Ten directly affected enabled,
owner/parent-cancel and protected-source cases passed on recheck; they are
already included in the 46 distinct cases, not an additional aggregate lane.

Final result: **46 distinct green tests**, with no remaining failures or skips.
The compatibility lane took 8m06s; the final cleanup subset took 3m22s. Script
syntax, whitespace, child-process cleanup and preserved-file hashes are checked
again by the final verification script.

## Preservation and next step

At entry, all 8,913 prior pins and 626 dirty entries matched
`artifacts/production-attempt-binding-20260929/FINAL-VERIFICATION.json`
(SHA-256 `dfd174d69f071852e86890eabe4defa43cd2f5d248e248e9566bdf235f667854`).
Before-images preserve the twelve edited existing files and previous test DLL.
Six new repository files, including this report, and three newly dirty existing
files bring the expected dirty-entry count to 635. The final seal records exact
hashes, verification results and child cleanup.

Next: connect the consumed attempt to the original helpee/helper launches and
protected source/destination custody. Keep the live scenario gated until those
files can traverse the wrapper/parent and original-primary handoffs with the
same checked ownership. Do not enable it merely by appending an owner argument.

Then perform the reviewed clean checkpoint, fresh portable build/provenance and
strict escalated preflights before the separately scoped live-256 attempt.
The second coordinator-published `file_tuna_v4` route remains required; SHA or
fallback completion alone does not satisfy the route-cycle contract.
