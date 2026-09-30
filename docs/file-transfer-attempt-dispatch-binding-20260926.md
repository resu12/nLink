# Test-only parent-attempt to GUI dispatch binding

2026-09-26, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Implements the first bounded step from
`file-transfer-gui-transfer-handoff-contract-review-20260926.md`.

## Outcome and limits

The explicit test profile carries the original first parent attempt's launch
projection through its native wrapper and GUI requests to a one-shot dispatch
binding. The original GUI entry startup statements and scenario dispatch
expression are exercised with two native headless app children. Only the test
copy of that expression receives the consumed original `FrontierOwner`.

The checked-in live call remains `Run-ScenarioFileTransferTunaHandoffFallback
-Context $ctx`. Default loaders expose none of the new APIs. There is no
runtime, route-authority, acceptance or pre-verdict change.

This verifies the canonical 256 MiB **settings**, not a 256 MiB transfer. The
fixture replaces UI interaction and the transfer cycle with assertions. Its
headless app leases are launch identities, not two completed Core log writers.
Retained analysis and reconciliation are isolated stand-ins; no real analyzer,
terminal-integrity decision, live eligibility or acceptance verdict is issued.

## Binding and lifetime

The parent fixture invokes the original scenario definition, attempt identity
scope, `Invoke-TunaRouteAcceptanceRun` argument construction and native child
launcher. The attempt index comes from that original identity scope. The
wrapper executes the original non-secret environment assignment statements
and the original GUI launcher, deadlines and output readers.

The test-only partial type uses a bounded 4096-byte slot in unused request
space, refusing overlap with registration or completion bytes. It retains
only the executable path, direction, payer, fault, route, payload-size setting,
toggle sequence, cycle/progress timeouts, proof mode, measured phase and host
image path. The measured phase is evaluated from the original GUI assignment.
Wallet/password/sidecar arguments are excluded. The ordinary loaders and
native request layout consumers are not changed.

The original parent request binds once, before child launch. Its genuine relay
can forward once to the original fresh GUI request with matching run, scenario
and artifact registration. Copied parent requests cannot bind. Copied relays
are refused by the original-object check. There is no public deserialization
or JSON-to-owner import.

The GUI acquisition requires its current original owner and exact projection.
It retains the original parent process handle and native request mapping,
checks parent/wrapper/GUI PID and creation identities, and compares the full
original registration and request metadata. Both the parent's cancellation
event and process exit invalidate the binding even while the wrapper remains
alive. This is a trusted local-workflow capability, not a same-account security
boundary or an executable-content attestation.

Consumption requires the same original owner, attempt and projection. A copied
binding cannot consume or dispose the original. Successful consumption is
one-shot; refusal cannot grant native app requests. Ordinary launch and report
cleanup continue without an owner on the rejected test path, and an explicit
assertion checks that this does not change the owner's existing lifetime.
Fixture cleanup disposes the additional parent handles and mapping.

## Surgical fixture changes

Two existing support files gain the explicit profile branch; all other new
implementation and test files use the `FrontierAttemptDispatch` prefix.
The existing native scenario fixture still owns its two app processes,
startup signals, output readers and cleanup assertions.

The fixture's function extraction list was stale: current `Start-AppInstance`
calls `Set-GuiPreparationProcessIdentity`, but the list did not load it. The
test now loads that unchanged original helper. No application fix was needed.
The new profile also distinguishes an omitted owner from the older fixture's
deliberately passed disposed owner; default-profile assertions remain active.

## Verification

Final results and source/artifact hashes are recorded in
`artifacts/frontier-attempt-dispatch-20260926/FINAL-VERIFICATION.json`.

- `focused-final.trx`: 22 passing cases on Windows PowerShell and PowerShell 7:
  the positive path, changed argv/environment, cancellation at the GUI owner
  and original parent, wrong PID/creation, changed request bytes, nonzero GUI
  exit, killed GUI child, and default/mixed-profile guards. Each applicable
  native case also checks all 12 projection fields, another attempt, copied
  parent/GUI/binding objects, duplicate use and copy disposal.
- `compatibility.trx`: 34 passing existing cases: all 22 scenario-launch
  cases, eight selected GUI-entry cases, and four wrapper/relay cases.
- `offline-safety-recheck.trx`: both host positives pass again after moving
  the test-wrapper selection ahead of optional native setup. This ensures
  even a setup failure cannot select the real live wrapper from the fixture.

There are 56 distinct final passing tests, with no failures or skips in these
three runs. The last recheck repeats two positives, not two additional cases.
Each offline run keeps its children on its selected PowerShell host; this is
not a cross-host or packaged-nLink executable test. PowerShell syntax and
whitespace checks pass. No aggregate contract suite or GUI matrix was run.

All 216 pre-existing dirty files remain byte-for-byte unchanged. Two formerly
clean fixture files changed, and seven new test/support files plus this report
bring the dirty-entry count to 226. Production source and the reviewed live
dispatch remain unchanged.

Development failures are retained: initial optional-type compiler-option and
visibility errors, the stale fixture import, and the new profile's cleanup
expectation mismatch. A rebuild also encountered a sandbox-denied Avalonia
telemetry log; the subsequent build disabled only that telemetry target using
`UsedAvaloniaProducts=`. None of these failures was used as live-network RCA.

## Next boundary

Bind the two independently owned app/writer roles to the measured transfer,
then establish actual source/finalized-destination file custody through the
parent and original analyzer. The current scope does not hold either transfer
file, finalize writers, or enable the full-native selected-witness reader.
Keep the separate selected-failure pre-verdict reservation and canonical
second coordinator-published `file_tuna_v4` requirement unchanged.

No portable package, live GUI/NKN run, full matrix, commit or push is part of
this step.
