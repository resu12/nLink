# Payload listener lifetime — prerequisite results and bounded follow-up

Date: 2026-09-27. Status: **implementation paused at the required cleanup gate**.

## Outcome

The approved `payload-listener-lifetime-v1` change has **not** been applied.
Its prerequisite controls found an existing connected-pipe cancellation defect
on Windows PowerShell. The reviewed plan explicitly requires retaining such a
failure and reviewing its local correction before widening the one-wait change.

Latest results for the 24 new original-lifetime controls: **23 pass, 1 fails**.
The failure is the Windows PowerShell connected peer that sends no packet.
The same control passes under PowerShell 7. No transfer or canonical replay was
run in this step, and no result was promoted to route acceptance.

## Tests added and executed

`FrontierPayloadWrapperTests.OriginalListener_NoOfferRetiresOnOriginalLifetime`
uses the original relay, original request, original native GUI launch reports
and original payload adopter. The small GUI child attaches the genuine original
owner, then performs only the selected lifetime action. No app transfer or NKN
connection is started. Named events control test scheduling only; they do not
grant scope, custody or acceptance.

Both hosts cover request close, genuine request disposal, normal/nonzero/killed
GUI exit, explicit adopter disposal, wrapper-finally unwind, the existing
watchdog throw/unwind seam, startup pipe collision, cancellation racing a
connection, a stalled connected peer, and a wrong-OS-PID peer. The watchdog
control invokes the existing failure seam; it does not claim to have waited for
the full scenario watchdog interval. Startup collision must retire without an
adoption task; the other controls require the actual task to finish unsuccessfully.

Checks require retired state, no committed/taken/provisional custody, closed
listener and any payload handles, unchanged original parent-work clock, no
restart/terminal take, refusal of a new pipe connection, and child/output cleanup.
For the failing stall, the required pre-finally task-completion assertion fails;
later wrapper cleanup is not counted as deadline success.

Execution history (old failures retained):

| Run | Passed | Failed | Meaning |
| --- | ---: | ---: | --- |
| `listener-request-dispose-before.trx` | 2 | 0 | Real request disposal on both hosts |
| `listener-cleanup-before.trx` | 19 | 3 | One real stall failure; two new fixture exit-code errors |
| `listener-nonzero-fixture-corrected.trx` | 2 | 0 | Only the two exit-code controls rerun |

The fixture initially put `exit 7` in a dot-sourced helper. The outer GUI fixture
then exited 0. Moving the selected exit to the outer script corrected the test;
both nonzero controls now pass. No listener/runtime fix was made for that error.
Totals are 26 executions: 23 passes and three retained failures, with 23/24
distinct controls passing after the fixture correction.

Two delayed authenticated-offer tests were also prepared and compile, but were
**not executed** after the prerequisite failed. They use the existing full-case
210-second allowance, wait until at least 105,000 ms after listener startup,
do not start/stop/reset either work clock, and retain the actual offer/commit,
normal-exit, verification and exclusive-file-release checks. Their before/after
runtime proof remains outstanding; the timestamp alone is not claimed as it.

## First failure and RCA

Artifact:
`artifacts/listener-lifetime-implementation-20260927/powershell-stall-packet-6f4f8d8eb0bb4e338d3634c21be254a0`.

The authentic original GUI child connects but sends no packet length. The
adoption task does not finish within the fixture's 15-second observation window.
It later reports `stage=packet_length`, `EndOfStreamException`, elapsed 17,772 ms,
`lifetime_cancelled=True`, parent work 0/stopped, during failure cleanup. This
is not evidence of a timely 10-second deadline.

PowerShell 7 counterpart:
`pwsh-stall-packet-74665576625944e0933aa519a5f06945` in the same artifact root.
It records a 10,016 ms connected-deadline observation and task failure at
`packet_length` with `OperationCanceledException`, lifetime not cancelled and
parent work 0/stopped. All retirement assertions pass.

The adopter source is unchanged: after connection it sets
`deadline.CancelAfter(10000)` and passes that linked token to pipe reads.
An isolated local named-pipe API probe uses that same asynchronous server/read
pattern, a connected silent peer, and the same 10,000 ms cancellation interval.
At 11.5 seconds it observes:

| Host | Runtime | Token cancelled | Read completed | After pipe disposal |
| --- | --- | --- | --- | --- |
| Windows PowerShell 5.1.26100.9549 | 4.0.30319.42000 | true | false (`WaitingForActivation`) | completed |
| PowerShell 7.6.6 | 10.0.12 | true | true (`Canceled`) | completed |

Source: `artifacts/listener-lifetime-implementation-20260927/Probe-PipeReadCancellation.ps1`.
This directly reproduces the host-specific pending-read behavior without the
application or fixture. The missing edge is therefore **connected deadline
cancellation → pending pipe I/O termination**. It is not a throughput, NKN,
parent-work, identity or route-cycle issue.

## Proposed local correction — review before implementation

Recommend a bounded `payload-connected-pipe-abort-v1` extension:

1. During the existing connected exchange only, register cancellation of its
   existing linked deadline to close that exact pipe, so Framework pending I/O
   is released. Dispose the registration on exit. The callback must not perform
   scope evaluations, issue authority, commit custody or touch another endpoint.
2. Keep the existing 10,000 ms server and whole-client deadlines, original
   request/GUI lifecycle, parent 5,000 ms work clock, GUI 10,000 ms work clock,
   packet/native-file bounds, OS peer identities and all custody gates. Do not
   add retries, replace ownership, or increase any deadline.
3. Verify stalled packet and commit waits plus cancellation/disposal races on
   both hosts. Treat explicit pipe closure as the interruption mechanism, not
   as proof of a successful exchange. Retain any next first failure.
4. Once this prerequisite passes, resume the approved listener-wait change:
   establish genuine before/after delayed-offer evidence beyond 100 seconds,
   run the stabilized affected wrapper lane and native-writer positive/default
   exclusion gates, then rebuild and make one offline canonical attempt.

The proposed interruption must remain scoped to the exact connected exchange;
its implementation and success/failure race review are not completed here.
No separate client/runtime or transport changes are inferred from the API probe.

## Preservation and remaining work

Initial audit: all 3,974 prior checkpoint pins matched on branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, with 405 dirty entries. Existing work
was preserved. Changes here are tests/probes and this report; generated smoke
assemblies were rebuilt. `FrontierAttemptPayloadWrapper.cs` still hashes to
`75aae5df27f1b7ee48335d079d834273ad62c12e32851ae4a113646e9efb9877`.

Neither the 100-second wait nor any runtime/coordinator/transport/acceptance
logic was changed. No aggregate lane, old recovery lane, portable rebuild, live
GUI/NKN run, full matrix, canonical rerun, commit or push occurred. The prior
canonical failure remains the latest transfer artifact, not a new result.
The second coordinator-published `file_tuna_v4` is still required.

Checkpoint: `artifacts/listener-lifetime-implementation-20260927/FINAL-VERIFICATION.json`.
Next decision: approve the connected-pipe interruption correction before
resuming the already approved listener-lifetime implementation.
