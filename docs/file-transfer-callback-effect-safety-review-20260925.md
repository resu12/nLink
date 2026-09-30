# Callback-effect safety: review and next test boundary

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, including the existing uncommitted
diagnostic work. This step reviews current code and runs focused existing tests;
it does not change runtime behavior or implement a safety classifier.

## Decision

No route-guard defect was reproduced. The 13 directly selected dispatch-guard
and Service publication cases pass with workspace-local operational logs.
The recorded-security-span consumer remains a diagnostic result, not execution
lifetime or absence-of-effects proof. Combining its result with the recorded
rejection chain and zero matching dispatch observations does not strengthen that
claim into `proven_stale_rejected`.

The next bounded step is test-only: strengthen direct effect assertions for the
existing held, prepared payload callback. Measure the callback from an explicit
post-publication, pre-dispatch baseline. Keep earlier preparation separate.
Do not add another log correlator, production event, runtime scheduling hook,
authority evaluation, retention extension or acceptance exemption for this step.

## Current evidence and its actual boundaries

The preceding seal is
`artifacts/publication-security-lifecycle-consumer-20260925/FINAL-VERIFICATION.json`,
SHA-256 `475a5f2064a59cb9093b86a36c6931ab546b5314974e3afda58c0a9ac52b5c6a`.
All 44 pins match. Its latest genuine two-process, FakeNKN Service-yield capture
is `3951a7d05d9647b29bd85d94cdcb4746` under that artifact directory. Saved results
remain 343 full records, 162 compact records, 71 passing boundaries per view,
three original evaluations and 24 witnesses. These consumers were not rerun here.
The deliberate `missing_receive` fixture still reports
`missing_exact_tuna_receive`, unrecovered; it is not a newly diagnosed live fault.

| Boundary | Established by current code/tests | Not established |
| --- | --- | --- |
| Ingress rejection | Regular v4 payload rejected under current Tuna-v4 authority returns before adapter tracking, recovery observation and work creation | Every concurrent interleaving; other frame kinds; all callbacks |
| Prepared callback rejection | Current-publication recheck returns before fallback receive recording, dispatch-entry logging and data-session delivery | No effects during earlier preparation; dispatch scheduler/reset behavior |
| Service callback controls | Measured Coordinator state, publication, sent frames/hints and transition execution remain unchanged for exercised handoff/replay controls | Every external side effect or an arbitrary original-capture callback |
| Recorded security span | Selected diagnostic rows lie between matching recorded install/retire markers | Execution-time key validity or Coordinator publication retirement |

### Ingress and preparation

`NknSignalingTransport.FileTransferTransportChannel.cs:4446` calls the ingress
guard before `TryValidateAndTrackFileTransferDataFrame` at line 4451 and
`ObserveFileTransferReceiveRecoveryProof` at line 4483. The latter is not a
diagnostic-only log call in all configurations: the real-adapter path can retain
an exact recovery observation and invoke its subscribers.

Both the latest two-writer fixture and the held-callback test use `FakeNknClient`.
`NknSignalingTransport.Acceleration.cs:1993` requires `RealNknClientAdapter` and an
exact bridge-recovery execution command before that observation path can run.
Therefore those fixtures cannot establish behavior of the real-adapter recovery
branch merely by asserting no observed recovery callback.

The Service handles that observation through exact bridge/command matching and
`FileTransferCoordinator.ObserveReceiveRecoveryProof`
(`SessionFileTransferService.Liveness.cs:498`). Its observation is not itself
permission to recover or dispatch retry. This review neither bypasses that
authority boundary nor establishes a defect there.

### Dispatch and downstream effects

`NknSignalingTransport.FileTransferTransportChannel.cs:4550` captures the current
route authority and matching diagnostic publication; line 4552 rechecks the
Regular-v4/Tuna-v4 payload mismatch. On rejection it returns before
`RecordTunaFallbackFileTransferDataFrameReceived`, `dispatch_entered`, and
`DeliverFileTransferDataFrame`.

This is a specific payload-route guard, not a universal stale-callback fence.
Tuna payload and non-payload control frames are deliberately excluded; absence
of this particular route authority is not treated as this mismatch. The seven
existing test cases include these permitted paths and an omitted diagnostic
publication case. Diagnostic availability must not alter the guard result.

Fallback receive recording can update `TunaFallbackProofState.FileNknFramesReceived`
and attempt fallback proof completion (`NknSignalingTransport.Acceleration.cs:11290`).
Delivery may create/reuse a data session and enter its queue
(`NknSignalingTransport.FileTransferTransportChannel.cs:7401`). The current
dispatch test measures adapter transfer state, queue count/bytes, delivery and
publication evidence. It does not directly snapshot fallback bookkeeping,
session identity/availability, or availability notifications around rejection.

The test invokes the genuine prepared `Dispatch` action directly. It does not
exercise `DispatchInboundFileTransferChunk`'s lifecycle barrier/generation fence
or `ProcessInboundFileTransferControlDispatchQueueAsync`'s generation check
(lines 3560 and 3590). Do not relabel its passing result as scheduler/reset safety.
Nor does a gate-protected authority lookup imply a lock spanning all later work.

### Service and file assertions

The original two-writer replay is enclosed by Service state, frames, hints,
transitions, transferred-byte and frontier assertions at
`SessionFileTransferSecondTunaPublicationTests.cs:189`. It is not adapter-only
coverage. However, `NknFileTransferTransportTests.TwoWriterIngress.cs:150` disables
the separate post-commit callback replay block, and the selected latest fixture
does not request final durable fallback.

The six standalone Service cases run here include the successful second
publication with repeated post-commit handoff and drain calls, plus five missing
or mismatched proof controls. Their assertions are additional, separate test
evidence. They do not retroactively add those executions to the sealed two-writer
capture. The durable-fallback file/hash fixture was inspected but not rerun or
claimed as fresh validation.

## Smallest next implementation contract

Extend `NknFileTransferTransportTests.DispatchRouteGuard.cs`, without changing the
production guard or broadening the diagnostic consumer:

1. Keep the exact prepared callback object. Record preparation state separately,
   publish the selected successor, then take the effect baseline immediately
   before invoking that callback. Never attribute earlier preparation changes
   to rejected dispatch, or require preparation to have had no effects.
2. For the rejected regular-v4 payload cases, compare values rather than mutable
   object aliases: transfer state; current route authority/publication; data
   session identity, generation, availability and queued frames/bytes; and
   relevant fallback proof/counter state. Subscribe to the existing availability
   event and count emissions. Retain the current exact rejection diagnostics as
   corroboration, not the source of the no-effect assertion.
3. Use a non-vacuous, existing fallback fixture/setup where needed: an allowed
   Regular payload control must exercise the measured counter/delivery path.
   Do not manufacture Coordinator authority or alter production timing merely
   to activate the assertion. If existing lawful setup cannot expose a specific
   effect, report it as untested rather than comparing two empty/default states
   and claiming coverage.
4. Preserve permitted Regular/fallback, Tuna and non-payload control cases, and
   rejection with omitted publication diagnostics. No blanket ban on all late
   or control traffic. Name the resulting claim narrowly: no measured effects
   from this rejected prepared callback after its dispatch baseline.
5. Rerun the directly changed theory cases with owned workspace-local logs.
   Run any directly reused fixture regression only if that fixture changes.
   No aggregate analyzer/contract lane, two-host consumer rerun, portable build,
   full GUI matrix or live-256 test is required for test-only assertions.

This contract deliberately does not claim real-adapter recovery coverage,
execution-time security validity, runtime/publication retirement, arbitrary
effect attribution, or all callback families. A stronger claim needs a separately
specified execution boundary and a deterministic test of that actual branch;
additional log matching alone is insufficient. Do not make these broad claims
implicit prerequisites for ordinary diagnostic reporting either.

## Verification and test-log correction

The initial sandboxed test invocation used the ordinary default log destination
outside the writable workspace. All seven dispatch cases failed the retained
slice assertion at line 79; the run was interrupted while subsequent Service
cases were still waiting. No completed TRX exists for that interrupted attempt.
The precise underlying default-path persistence failure was not captured; do not
turn this into a runtime RCA or claim a specific OS error.

The same built test assembly and filter then passed with only the existing
acceptance-log environment configuration set to the workspace artifact directory:

```powershell
$root = Join-Path (Get-Location) 'artifacts/callback-effect-safety-review-20260925/workspace-log-run'
$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $root
$env:NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH = Join-Path $root 'nlink.log'
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'callback-effect-review-offline'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'dispatch-and-service-guards'
dotnet test tests/nLink.SmokeTests.Core/nLink.SmokeTests.Core.csproj --no-build --no-restore -p:UsedAvaloniaProducts= --filter "FullyQualifiedName~NknTransport_DispatchRechecksCurrentPublicationBeforePayloadDelivery|FullyQualifiedName~SecondTunaPublication_ServiceRequiresPairedYieldBeforeResponderCommit" --logger "trx;LogFileName=callback-effect-workspace-log.trx" --results-directory artifacts/callback-effect-safety-review-20260925/results
```

Final receipt: `artifacts/callback-effect-safety-review-20260925/results/callback-effect-workspace-log.trx`:
13 executed, 13 passed, zero failed/skipped (seven dispatch and six Service cases).
These are deterministic offline tests, not GUI or live NKN acceptance.

All 70 pre-existing dirty-file contents and all 44 prior seal pins are preserved.
This step adds only this review document and ignored verification artifacts under
`artifacts/callback-effect-safety-review-20260925/`. No runtime/test-source change,
consumer evaluation, classifier/acceptance change, portable rebuild, live run,
commit or push occurred. The proposed assertion extension is not yet implemented.
Strict exact-Tuna payload, second-publication route-cycle, integrity and terminal
obligations remain unchanged; live-256 readiness is not established here.
