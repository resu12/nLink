# Production parent-attempt binding

2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree changes.

## Outcome

The first production ownership link is implemented: the normal parent launcher
can bind its original canonical attempt and exact outgoing arguments to its
native wrapper request. It is default-off under the existing explicit
`CaptureLocalPublicationDiagnostics` request. **The production GUI diagnostic
entry remains disabled.** This is not completion of the downstream protected-pair
handoff or permission to run live-256.

Ten focused tests and 28 directly affected compatibility tests pass. Four of the
ten focused cases were rerun after the final host-binding/test refinements and
also pass: **38 distinct green tests**, no final failures or skips. No aggregate
suite, 256 MiB transfer, GUI automation, live NKN, portable rebuild, commit or
push ran in this step.

## Bounded implementation

`tools/FileTransferOps/LocalGuiWriterCanonicalAttempt.cs` reuses the tested
bounded request serialization/admission logic from the old test partial. The
normal loader now includes this implementation. The old
`TryBindAttemptForTest` remains a test-only adapter to the shared private binder;
the normal loader still excludes that test API, attempt consumer, role/pair
extensions and same-pair assertion.

The production entry `TryBindCanonicalAttempt` accepts only the original relay
request, attempt 1, its exact run/canonical-scenario identity, and
`RegularActivationCycle`. It selects the non-secret projection from the exact
28-element wrapper argument array: executable, direction, payer, fault, route,
size, ordered toggles and both timeouts. It checks the actual artifact directory
against the request root and requires RouteAcceptance as analysis owner. Missing,
extra, duplicated or unsupported argument names fail closed. Wallet/password and
sidecar arguments are not retained in the projection.

The existing 4 KiB reservation, registration-size guard, one-shot binding,
pre-worker-start restriction, original parent/request identifiers and creation
time remain intact. Header/registration and completion regions are unchanged.
This metadata is diagnostic scope, not coordinator authority or an acceptance
receipt. No resource limit, route state, transfer schedule or retry behavior
changes.

`Invoke-TunaRouteAcceptanceRun` carries the explicit attempt index from both the
initial and rerun call sites. Only the initial attempt can request this binding;
reruns are not silently relabeled as attempt 1. The original child-launcher binds
the same argument array used to construct its command line, before binding the
worker. It records the resolved child host and uses that exact executable path
for the accepted opt-in launch. This handles a PowerShell 7 parent launching the
existing Windows PowerShell wrapper without equating the two host paths.

Scope omission preserves the ordinary launch and child outcome. The new code
does not append `-FrontierOwner` to the live GUI call, forward a test profile,
load test-only helpers in production, or consume a one-shot diagnostic writer.
The original acceptance and post-report resolver ordering remain unchanged.

## Verification

Evidence: `artifacts/production-attempt-binding-20260929/`.

| Check | Result |
| --- | --- |
| Pre-implementation normal-loader regression | Both hosts failed for the expected missing production binder |
| Normal-loader controls and actual parent launch | 10 passed |
| Final affected subset after host-path refinement | 4 passed; included in the ten distinct focused cases |
| Existing attempt dispatch/default-loader controls | 22 passed |
| Existing relay valid, nonzero-child and host-output-failure cases | 6 passed |
| Test harness build | Passed, 343 existing warnings, zero errors |
| PowerShell syntax and whitespace | Passed |

The normal-loader controls cover original/copy/ordinary/foreign-scope/retired/
already-launched requests, repeat binding, wrong attempt/proof/host, changed
scope fields, malformed/duplicate/unknown argument names, credential exclusion
and unchanged original memory regions. Internal assertions are not counted as
additional xUnit tests.

The launch tests execute the actual parent function bodies, argument construction
and native launch/binding code. They substitute only a headless child script
and script-root resolution. The child uses the normal loader and original
process-bound relay, then verifies the received bytes against its actual arguments
and host. The fixture stops before analysis, creates no publication and verifies
child exit. It tests enabled capture, disabled capture, wrong size and a second
attempt on both parent hosts. The headless child is not the real GUI wrapper's
downstream consumer; that integration remains explicitly unfinished.

The affected compatibility lane ran once (7m24s). No unrelated coordinator,
analyzer, transport or full-matrix lane was rerun. Tests compile the current
PowerShell-loaded C# source in fresh hosts; no second harness build was needed
after the script/helper-only implementation edits.

## Preservation and next link

Entry verified all 8,494 prior pins and 619 dirty entries against
`artifacts/live-readiness-after-canonical-witness-20260929/FINAL-VERIFICATION.json`
(SHA-256 `7d31bab045220e59b48711eb756a9d3d09c4dabf567306145c9ebb7f4fa5b996`).
Before-images preserve the three edited existing source/test files and prior
test DLL. The other pre-existing changes and captures are preserved. Six new
repository files, including this report, plus the previously clean parent
launcher now bring the dirty-entry count to 626.
The final verification/seal records the exact changes, tests and child cleanup.
Its first check caught this new-file versus newly-dirty-file counting distinction;
the evidence checker was corrected without changing implementation or rerunning
tests.

Next: integrate forwarding and original one-shot consumption of this bound
attempt in the production wrapper and GUI, with the same private role custody
and rejection checks already established offline. Keep the live scenario owner
disabled until the source/destination, wrapper/parent and original-primary
protected-pair handoffs are ready. Do not remove the guard by merely adding an
argument to the live call. No additional acceptance prototype or budget increase
is needed for this next link.

After the complete ownership integration passes its focused gate, the previously
reviewed sequence still applies: reviewed clean local checkpoint, fresh portable
build/provenance, strict escalated preflights, then the separately scoped single
live-256 attempt. The second coordinator-published Tuna-v4 route remains required.
