# C1y: bounded process-cleanup outcome diagnostic

2026-09-15. C1w's sender writer had no drain marker. Source inspection confirms
that `Program.Main` emits the marker in its finally block after application
shutdown. The GUI harness requests window close, waits up to a shared 7000 ms,
then requests forced termination for apps still running. C1w did not persist
which close/wait/stop branches each app took, so a forced app stop remains a
hypothesis, not an established explanation of that run's missing drain.

## Bounded implementation

Observe the existing `Cleanup-ProcessesWithOperationalLogDrain` actions without
adding or reordering process actions, extra exit polling, waits or retries.
Keep the shared stopwatch start after all close requests and the original
`max(1, TimeoutMs - elapsed)` wait allocation. The 7000-ms default is unchanged.

An optional fresh reference receives a fixed outcome for each of the scenario's
two app targets: target ordinal, best-effort process ID, close outcome, wait
outcome, allocated wait milliseconds and stop outcome. Capture at most two
targets; omit diagnostics for larger sets while still cleaning every target.
Outcomes use fixed enums, not exception messages, process paths or raw logs.

Close outcomes: `not_observed`, `already_exited`, `accepted`, `not_accepted`,
`exception`. Wait outcomes: `not_observed`, `already_exited`, `exited`,
`elapsed`, `exception`. Stop outcomes: `not_observed`, `already_exited`,
`requested`, `exception`. A stop request is not proof that termination succeeded;
process exit is not proof of a writer drain. Null process ID/zero wait budget
mean those values were not observed, not a successful operation.
`accepted` means only that `CloseMainWindow()` returned true; the app may still
delay or cancel closing. `exception` describes the phase, including its existing
exit-state check, and does not assert that the subsequent action was invoked.

The existing finalizer callback writes the optional
`filetransfer-gui-process-cleanup-diagnostic.json` after cleanup, using the
existing atomic JSON writer. Its schema is version 1 with `authorityEffect=none`,
the shared wait budget, and the two bounded rows. It is at most 4 KiB serialized
UTF-8. Capture/persistence failure must not change the returned process array,
redirected-output EOF wait, immutable log capture, writer validation or original
scenario error. An already-populated reference is not overwritten.

This diagnostic is not a writer drain, source-completion receipt, authority
token, acceptance input or replacement for the C1v publication companion. Do
not add it to any authority parser or change missing-drain rejection. No app,
transport, coordinator, logger, timeout or route behavior change is authorized.

## Verification

Run the production cleanup body with in-memory fake process objects only.
Compare the action sequence with diagnostic capture enabled and disabled;
cover close rejection/exception, clean exit, already-exited app, wait expiration/
exception, stop request/exception, shared-budget exhaustion, duplicate targets,
oversized target set and occupied output reference. Exercise the actual
finalizer callback's successful write and failed-write paths without GUI use.
Run the directly affected writer/finalizer tests and the existing FIFO drain
test. Do not rerun a live 256 MiB scenario or fabricate markers in its artifacts.

## Verification result

The missing outcome was reproduced before the edit on both PowerShell hosts.
The implementation and final controls pass on 5.1 and 7. Eight outcome scenarios
compare capture-on/off action traces against the original expected ordering and
three exit-state reads per target. Additional cases cover budget exhaustion,
zero-budget clamping, duplicate/null/oversized targets, occupied output,
successful/failed persistence and unchanged bridge-cleanup exceptions.

The directly affected lane passed 6/6, including redirected-output draining,
strict missing-writer-drain rejection and FIFO logger drain behavior. Source
comparison with `ed8ca1a6` also confirms unchanged counts of process actions,
exit checks, shared stopwatch creation and the 7000-ms default.

This closes the cleanup-outcome reporting gap, not C1w's actual shutdown/drain
failure. No historical outcome was backfilled. See
`artifacts/publication-evidence-finalization-20260915/PROCESS-CLEANUP-RESULTS.md`.
