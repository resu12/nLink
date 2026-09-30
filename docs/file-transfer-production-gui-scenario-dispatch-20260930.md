# Guarded production GUI scenario dispatch

The top-level GUI dispatch now passes the original owner and canonical attempt
to the existing file-transfer scenario, but only after one-use native admission.
Thirty-eight distinct offline tests passed. Capture-off and rejected attempts
retain the ordinary scenario path. This phase did not run the live GUI or NKN,
change route acceptance, rebuild the portable package, commit, or push.

## Starting checkpoint and reproduced gap

This phase began on 2026-09-30 from
`artifacts/production-analyzer-pair-20260930/FINAL-VERIFICATION.json`,
SHA256 `0099cb7eaadfc361736ed8c476111a0d91b4cef2622cd59cc59ee7b44ad72dd5`.
All 15,844 pins and 675 dirty entries matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
Before-images preserve ten edited text files and five pinned build outputs.

The prior production entry consumed the canonical attempt but called the
scenario with only its ordinary context. Four regression checks first failed
on both PowerShell hosts with `Production GUI attempt dispatch missing` or
`Private scenario forwarding missing`. These failures are retained with the
passing results for the same tests.

## Production changes

Only two production files changed. `LocalGuiWriterCanonicalDispatch.cs` adds
`TryTakeScenarioOwner`. It returns the original owner only for the original,
still-current, already-consumed attempt. An atomic one-use guard rejects
repeated dispatch. Existing native identity and lifetime checks reject copied,
retired and parent-cancelled tickets. Rejection does not spend an unconsumed
original ticket or let a copied ticket spend its original.

`GuiSmoke-Windows.ps1` explicitly forwards the private attempt through
`Invoke-Scenario` into the canonical action. The action obtains the owner from
the native admission method and passes that exact pair to the existing
scenario. If admission fails, both arguments are null. The owner and attempt
stay private rather than being placed in the ordinary context or environment.
The existing single-canonical-scenario consumption gate remains in place.

Ordinary actions still receive no extra arguments. The runner retains its
deadline setup and restoration, exception propagation, and completion check.
No role-launch, payload, analyzer, route, timeout, throughput, or coordinator
logic changed. Diagnostic ownership does not grant live-proof eligibility,
native-writer authority, or publication-completion authority.

## Offline verification

The final build passed with 339 existing warnings and zero errors. Six focused
checks passed, including the normal native loader, runner privacy and ordinary
action compatibility, and the enabled production chain on both hosts.

The actual top-level runner and action were executed in 24 normal-loader
parent-wrapper-child cases. The scenario body alone was replaced with assertions;
these cases did not open a desktop GUI, use NKN, delete app logs, or launch apps.
The cases cover enabled capture, capture-off, wrong size, repeated parent attempt,
altered wrapper and GUI environments, wrong host, nonzero GUI child exit, copied
ticket, unconsumed ticket, retired ticket, and parent cancellation on both hosts.
Each action runs twice: the original pair reaches the scenario only on its first
eligible invocation, and the second invocation receives neither owner nor ticket.
The probes verify exact object identity, private callback scope, deadline
restoration, unchanged context, false authority flags, and retirement.

The affected lane initially passed 28 of 30 cases. The two failures were an
outdated source-text assertion in the role wrapper fixture, before its GUI child
launch. Its component evidence remains separate from top-level dispatch proof.
After updating that seam, only the affected valid and unconsumed role cases were
rerun; all four passed. The latest result for every tested case is passing.
Ordinary default launches, prior attempt dispatch, canonical role launches and
parent binding remain covered without rerunning an aggregate suite.

Evidence is retained under
`artifacts/production-gui-scenario-dispatch-20260930` and the referenced scenario
artifact directories. The phase verifier checks predecessor pins, before-images,
frozen inputs, exact dirty-state preservation, resolved test failures, build
results, guarded source shape, recorded child exit, syntax and whitespace.
Earlier reports and seals remain unchanged.

## Remaining preparation before live execution

Review the accumulated change set for a local checkpoint. Then rebuild the
portable package, pin its provenance, and run strict escalated preflights before
the separately scoped targeted live-256 attempt. No new live result is claimed
here; the previous failed route verdict is not converted into acceptance.

The second coordinator-published `file_tuna_v4` remains mandatory for the route
cycle. A full GUI matrix must wait until targeted route correctness passes.
