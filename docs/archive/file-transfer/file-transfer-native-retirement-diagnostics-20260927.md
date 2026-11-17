# Native relay retirement RCA and bounded fix plan

Date: 2026-09-27. Status: twelve focused checks pass; one rebuilt offline
canonical attempt identifies unsuccessful native-pull cleanup as the first
retirement, specifically the currentness check after successful `MarkPulled`.

## Scope and preservation

Preflight verified all 5,491 pins in
`artifacts/native-acceptance-diagnostics-20260927/FINAL-VERIFICATION.json`,
SHA-256 `5faf37249853cb63a43269ea2aea786918fa88fb1ec79f9002cc1e8393644825`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`.
The 414 pre-existing dirty entries and previous evidence are preserved.

Only explicit test-profile diagnostics and their fixtures/tests change.
`LocalGuiWriterRelayTransfer.cs` uses conditional branches to retain the original
default implementation byte-for-byte after LF normalization (SHA-256
`983bf3a6138949c6da89e0c459d17d10cee2bd095ea612092c171937e12bdd78`).
No default-build behavior, acceptance rule or timer changes in this step.

The first native close now records compiler-provided caller identity and, for
request-level retirement, the source filename. It also snapshots the original
wrapper-exit rejection and pull phase. The original closed guard prevents later
cleanup from overwriting that first record. No stack inspection is needed.

Original wrapper-exit, post-pull, forward and transfer-release predicates retain
their evaluation order and values. Diagnostics label already-evaluated booleans;
they do not repeat currentness, scope, state, body or exit checks. Forward/pull
exceptions retain their original outcomes. The fixture prints the wrapper's
stored forward/release result after its original disposal, and the parent prints
its stored first retirement after the original take. No native diagnostic I/O,
new runtime polling, authority grant or callback suppression is added.

## Focused verification

Results: `artifacts/native-retirement-diagnostics-20260927/results`.

| Gate | Result |
| --- | --- |
| Default-source projection, acceptance ordering, retirement ordering, caller copy | 4 passed |
| Genuine valid handoffs, Windows PowerShell and PowerShell 7 | 2 passed |
| Native scope rejection, explicit disposal, wrapper-state rejection, cancellation | 4 passed |
| Default-loader isolation, both hosts | 2 passed |
| Smoke assembly rebuild before canonical attempt | Zero warnings/errors |

All twelve checks passed on their first run in this step. No unrelated aggregate
suite was rerun. Both valid handoffs accept once and reject repeated take. All
six native cases retain successful wrapper forwarding and `Close:release`.

The controlled refusals distinguish the actual first-close paths:

- Scope mismatch: `retire:LocalGuiWriterRelay.cs:TakeRelayAfterExit`.
- Explicit native disposal: `Dispose`.
- Original wrapper-exit callback after transfer-state mutation:
  `WrapperExited,wrapper-exit:transfer-state`.
- Original shared cancellation signal: `TransferCancelled` in this run.

The cancellation test permits the existing wrapper-exit callback to win the
same legitimate cancellation race with `transfer-current`; it does not require
an arbitrary callback scheduling order. Each negative case still refuses repeat
take and releases the payloads. Default-loader checks confirm that the native
retirement, pull and wrapper diagnostic surfaces remain absent.

## Latest canonical evidence

Exactly one offline canonical attempt followed the rebuild, failing after
approximately 120 seconds. Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-7daba3f5da38477f885b5feb1eef921c`.

Original wrapper evidence:

```text
native-relay-wrapper; forward=mark-forwarded; transfer_close=Forwarded:cancel
```

Original parent evidence:

```text
parent-payload-take; call=first; result=False; reason=native-parent-match; association=accepted-result; native_begin=closed; native_accept=closed; native_close=begin:not-called,accept:not-called,taking:False,accepted:False; native_retirement=retire:LocalGuiWriterRelayTransfer.cs:TryPullIfOffered,wrapper-exit:not-rejected,pull:post-pull-current
```

The first native close was not a cancellation/exit callback or post-take
cleanup. It was the `TryPullIfOffered` failure-finally path. `MarkPulled()`
returned true; the following original `current()` returned false. Cleanup then
retired the provisional custody before begin/accept could run. The later
`accepted-result` refusal remains correct for that closed, unaccepted custody.

On the wrapper, all earlier `Forwarded` guards passed, but the original
`transfer.MarkForwarded(body, check)` returned false. The wrapper followed its
existing cancel cleanup. The trace does not retain the inner MarkForwarded
predicate or a cross-process ordering of its failure versus parent cleanup;
the wrapper failure must not be claimed as the proven initiating cause.

The parent remains in the `valid` case, with no injected negative fault. Its
existing failed-outcome assertion still prevents repeated take, AnalysisBoundary,
the file-protection probe and primary analysis. No copied primary worker,
primary stdout or primary receipt exists. No canonical retry was made.

## RCA: a pre-forward predicate after releasing the peer

The proven failing boundary is:
`MarkPulled() == true` followed by `current() == false` in native pull.
The current lambda combines the cumulative 5,000 ms work allowance with
`FrontierRelayRegistration.IsCurrent`.

Source review shows a lifecycle mismatch in that placement:

1. `MarkPulled` writes transfer state 2 and signals `pulled`, allowing the
   wrapper to finish its offer and enter the original `Forward`.
2. The registration's `IsCurrent` is explicitly pre-forward: it requires a
   live wrapper and `RelayScopeMatches`, which rejects legacy state greater
   than 1 or a nonzero completion length.
3. Successful `Forward` writes the completion length and legacy state 2 before
   attempting native `MarkForwarded`. Thus legitimate peer progress can make
   the registration predicate false after the parent has released that peer.

The existing later parent guard also passed the same cumulative work allowance
before reaching native take. The fixture checks that this is the original relay
Stopwatch; the valid path does not reset it. This supports the inference that
the post-pull registration-currentness side failed, rather than an exhausted
work allowance. The exact registration sub-predicate was not recorded, so this
run alone does not prove scope transition rather than another registration
invalidation. A deterministic ordering regression is the next required proof.

## Bounded fix plan

1. Reproduce the phase crossing using original native handoff objects: after
   the original pulled signal, let the wrapper perform `Forward` before the
   parent executes its post-pull check. Keep the wrapper alive at that boundary
   to distinguish the legacy scope transition from process exit. A bounded,
   test-only rendezvous must not fabricate acceptance, replace custody, reset
   clocks or relax the five-second allowance. Require this regression to fail
   under the current implementation.
2. If that regression confirms the mismatch, keep the last pre-forward
   registration check before `MarkPulled` publishes the signal. After successful
   `MarkPulled`, retain the work-budget check and the transfer channel's existing
   post-signal validation, which permits its original monotonic 2-to-3 state
   transition. Do not reuse the now-obsolete pre-forward scope as post-signal
   authority. The candidate change is limited to this check placement; no new
   general currentness API or fallback is needed.
3. Leave custody provisional. Original cancellation/owner/exit watches, native
   BeginTake/Accept, exact body/facts/scope checks, wrapper exit zero, state 3,
   one-shot detach and cleanup remain required. A pulled signal, successful
   Forward or SHA match must not itself establish accepted native custody.
4. Verify the deterministic crossing and directly affected cancellation,
   nonzero exit, explicit disposal, budget, tamper and one-shot controls. Run
   the relevant native contract lane once after stabilization; rebuild, then
   make one targeted offline canonical attempt. No live test or full matrix
   is needed to establish this local handoff edge.

This is a proposed behavioral fix, not an implemented or proven repair. Do not
globally relax registration currentness, suppress cancellation, increase budgets,
accept a closed custody, bypass `accepted-result`, or tune transport throughput.

## Cleanup and checkpoint

GUI PID 49908 exits zero with complete output capture, no timeout and no deadline
extension. GUI cumulative active work is 6,719.8552 ms of its unchanged 10,000 ms
allowance. Native PIDs 41912 and 33568 each report 268,435,456 bytes on
`post_tuna_fallback_v6`, with matching session, transfer and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent post-run checks confirm matching lengths/hashes, exclusive read/write
access to both payloads and all three processes no longer running.

Source/retained/writer/semantic line counts are
101,853 / 101,851 / 101,853 / 73,711. These are offline progress/cleanup facts,
not live acceptance; the second coordinator-published `file_tuna_v4` remains
required for the route-cycle contract.

The post-run preservation audit found exactly the six intended source-file
changes and rebuilt smoke assembly among the prior pins; older evidence is
unchanged. No live GUI/NKN run, portable rebuild, full matrix, behavioral fix,
timer change, acceptance change, commit or push occurred. Checkpoint:
`artifacts/native-retirement-diagnostics-20260927/FINAL-VERIFICATION.json`.
