# Native relay pull-order fix and next primary-source boundary

Date: 2026-09-27. Status: the deterministic ordering regression is repaired;
36 distinct focused/native contract checks pass. One rebuilt offline canonical
attempt passes native handoff and reaches primary analysis, which fails with
`frontier_source_not_current` before any analyzer evaluation. The canonical
integration gate and live acceptance remain incomplete.

## Proven cause and bounded change

The old native pull called `MarkPulled()` and then registration `current()`.
Publishing the pulled signal allows the original wrapper to forward. Successful
forwarding writes the legacy completion length and state 2, making the
registration's pre-forward scope predicate obsolete.

A bounded test-only rendezvous now lets the original wrapper finish its original
`Forward` before the parent continues. The wrapper stays alive until the parent
records the observation. The worker verifies original native transfer state 3.
No completion, accepted custody, body, clock or currentness result is fabricated.

The first test attempt hit a fixture compilation error (CS0649 on a
reflection-assigned field), not the runtime bug. Explicit null initialization
fixed that fixture issue. The compiled regression then failed under the old
guard with wrapper alive, legacy state 2, 785 completion bytes, 68 ms cumulative
work, and first retirement:

```text
retire:LocalGuiWriterRelayTransfer.cs:TryPullIfOffered,wrapper-exit:not-rejected,pull:post-pull-current
```

Thus legitimate forwarding, not wrapper exit or the five-second work allowance,
reproduces the erroneous retirement. The production fix is only:

```csharp
if (!current() || !transfer.MarkPulled() || transferWork.ElapsedMilliseconds > 5000) return null;
```

The final pre-forward registration validation precedes publication. Afterward,
the original transfer-channel post-signal validation and cumulative work limit
remain. Custody is still provisional until the original successful wrapper exit,
BeginTake/Accept, exact scope/body/facts checks and one-shot detach. Cancellation,
disposal, owner identity, state 3, cleanup and terminal acceptance remain intact.
No timer allowance, retry, fallback or transport setting changed.

The explicit test profile has a bounded 2,000 ms rendezvous only for this fixture.
It returns the original `MarkPulled` result. Default-loader tests verify that
this field/helper and the diagnostic surfaces are absent.

The LF-normalized default source projection is
`aaed22abc1d5f0781134963730181b4e9629e5270032e5afd4fb44ea2f3a3ea3`.
A source-contract test reverses only this guard and its two explanatory comments
and recovers the prior default-source SHA-256
`983bf3a6138949c6da89e0c459d17d10cee2bd095ea612092c171937e12bdd78`.
It does not merely substitute a new expected hash for arbitrary source changes.

## Verification

Results: `artifacts/native-pull-order-fix-20260927/results`.

| Gate | Result |
| --- | --- |
| Compiled regression before fix, PowerShell 7 | Expected failure at post-pull currentness |
| Fixed ordering regression, both PowerShell hosts | 2 passed |
| Default-source, native acceptance/retirement, parent-take, default-loader checks | 6 passed |
| Remaining native relay contract cases, both hosts, one lane | 28 passed |
| Smoke assembly rebuild before canonical | Zero warnings/errors |
| One offline canonical attempt | Failed at new primary-source boundary |

The fixed ordering cases observe wrapper alive, legacy state 2, nonempty
completion, native state 3 and retirement open: 128 ms on Windows PowerShell,
49 ms on PowerShell 7. The other cases cover valid/guarded adoption, nonzero
wrapper exit, no take, cancellation, request/registration disposal, body mismatch,
analysis/currentness, map/event collisions, transfer tamper and exhausted budget.
No unrelated aggregate suite or second canonical attempt was run.

## Canonical progress and first new failure

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-50bf484c10c7483289f02b94b82d0fbc`.

```text
native-relay-wrapper; forward=forwarded; transfer_close=Close:release
parent-payload-take; call=first; result=True; reason=accepted; association=matched; native_begin=ready; native_accept=accepted; native_close=open; native_retirement=open
parent-payload-take; call=repeat; result=False; reason=already-attempted
```

The original parent validation now completes and primary process 27672 launches.
Primary stdout points to enumeration of the owned source at
`tools/FileTransferSoak/LogParsing.ps1:168`. Primary stderr is:

```text
Parent analyzer source/evaluations changed: 0/1 : frontier_source_not_current
```

Here 0/1 means zero evaluator calls and one native parser/read invocation.
It does not say how many source lines were read or establish a complete read.
The outer `Original incomplete-proof rejection changed` assertion is downstream
of this source failure, not its cause.

The retained-analysis receipt records exit 1 without timeout. The 73,725-line
input is unchanged before/after, SHA-256
`c82c9be59e7a591934228a65287472153b23eef222f8534111168c36eba024c0`.
No completed proof artifacts or coordinator parity verdict were produced.

Read-only source inspection identifies two relevant rejection locations in
`LocalFrontierPrimarySource.cs`: the direct `ReadSource` guard and the
`CheckSource` callback used by the native line reader. The direct guard checks
closed state, cumulative producer work and original worker/parent liveness.
`ResourceCurrent` additionally checks original scope, file channel and pins.
The existing message does not distinguish those original conditions.
Elapsed wall time alone does not prove active-work exhaustion, and the unchanged
input hash does not prove native ownership/currentness.

## Next bounded step

1. Identify the first original rejecting condition at primary source reading,
   including existing close/liveness provenance and the cumulative work value.
   If current artifacts cannot distinguish it, add narrowly test-profile
   diagnostics at the original short-circuit evaluations; never re-evaluate
   authority-bearing predicates just for logging.
2. Preserve original evaluation order, source identity, native file pins,
   cumulative budgets, cancellation and failure outcomes. Record how far the
   original reader progressed, without inventing completion or an accepted
   snapshot. Keep diagnostics out of default production behavior.
3. Verify only affected source/reader controls, rebuild, then make one targeted
   offline canonical attempt if diagnostics require it. Fix only the proven
   first edge in a subsequent bounded change.

Do not increase budgets, waive currentness, replace the owned reader with a path
read, suppress cancellation or claim live acceptance from payload integrity.

## Cleanup and preservation

GUI PID 32476 exited zero with complete output capture, no timeout or deadline
extension. Its recorded active work was 6,729.1759 ms of the unchanged 10,000 ms
allowance. Native PIDs 40700 and 5584 each reported 268,435,456 bytes on
`post_tuna_fallback_v6`. Independent checks confirm both payloads have that
length, SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
and released exclusive read/write access. All four recorded processes, including
the primary worker, are no longer running.

Source/retained/writer/semantic line counts are
101,873 / 101,871 / 101,873 / 73,725. These are offline evidence and cleanup
facts. A second coordinator-published `file_tuna_v4` remains necessary for the
live route-cycle contract.

Preflight verified all 5,667 prior pins, with zero mismatches, from
`artifacts/native-retirement-diagnostics-20260927/FINAL-VERIFICATION.json`
(SHA-256 `5aa4f823099f4fa4854d38ed6d7b7689916a2fd7dbf31b46cd4945bfae94418e`).
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
The 415 pre-existing dirty entries are preserved.

The post-run prior-pin audit finds exactly four intentionally changed source
pins plus the rebuilt smoke assembly. Three additional modified fixture/test
files were previously clean and therefore outside that prior pin set.
Older artifact pins are unchanged. The current checkpoint records the complete
dirty-file comparison and newly captured evidence:
`artifacts/native-pull-order-fix-20260927/FINAL-VERIFICATION.json`.

No live GUI/NKN run, portable-package rebuild, full matrix, commit or push occurred.
