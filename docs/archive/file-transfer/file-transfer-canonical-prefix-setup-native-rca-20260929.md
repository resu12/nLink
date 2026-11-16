# Canonical prefix setup: native capture result and next RCA

Date: 2026-09-29. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus preserved worktree.
Status: **transfer and bounded tail sequence completed; the complete native test
failed at its original 600-second outer timeout. Partial analysis also reports
one unresolved semantic proof. Not accepted as a full native or route-cycle pass.**

## Single-run result

The Debug rebuild and both outside-sandbox preflights passed. The capture used
the same canonical test, original 180-second carrier lifetime, 600-second outer
timeout, 1,800-second analyzer timeout and 20,000 ms native-work bound. The outer
timeout was not extended to accommodate the analyzer.

Test:
`NLink.SmokeTests.FrontierCanonicalTailNativeTests.OriginalApps_CarryCanonicalTailIntoSamePairPrimary`.

TRX outcome: **Failed**, duration **600.2485414 seconds**. The failure message is
`PowerShell harness timed out after 600s`. There was exactly one attempt, no retry,
no production/test-source edit and no additional primary authority evaluation by
this review.

Fresh fixture:
`artifacts/canonical-tail-native-characterization-20260929/pwsh-646a050ab737427aa2be805713604e79/`.

This is the offline native-process fixture. GUI-named harness artifacts do not
mean the desktop GUI matrix or live NKN ran.

## Progress established by this capture

The sender passed the previously failing setup boundary. Evidence shows one
pre-arm at offset 268,219,392, one receiver prefix export and one sender consume
at exactly 12,476 chunks / 268,283,904 bytes. Receiver stdout places export before
the first physical tail; the unchanged sender guard requires consumption before
ordinary tail output. No cached-tail bypass or setup-pause rejection was reported.

| Original receiver observation | Tick (ms) |
| --- | ---: |
| First held frame, chunk 12477 | 460413593 |
| Exact tail ready | 460413656 |
| Original first-held expiry | 460417593 |

Held-to-ready time was **63 ms**, leaving **3,937 ms** of the original four-second
hold at readiness. The earlier pre-barrier measurement was 2,719 ms. This is one
capture, not a throughput target or repeatability guarantee.

The helper reached exact repair, recent-grace recheck, native late-publication
closure and all three physical copies. It recorded
`closed-within-bound; physical-copies=3; root=unchanged`.
Both peers reported completion of 268,435,456 bytes on `post_tuna_fallback_v6`
with matching original session/transfer identity.

Read-only cleanup independently opened both source and received destination with
exclusive sharing and recomputed SHA-256:

`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.

These observations establish the bounded transfer progress, not successful parent
analysis or the second coordinator-published Tuna route required by the full
live activation-cycle contract.

## Two unresolved analysis observations

### Execution blocker: efficiency-summary stage did not finish

The retained analysis parsed 73,946 source events, with zero synthetic events.
Recorded parse time was 65,227 ms and index time 10,109 ms; 44 correlators completed.
The final stage diagnostic remained:

- `current_correlator=efficiency_summary`, `correlator_status=started`.
- Started at 06:30:15.1904086 UTC; no completed timestamp.
- The stage's zero elapsed/visit fields are its unfinished initial values, not
  evidence that it did no work.
- The retained analysis completion output and same-pair observation are absent.

The original primary process was still active during inspection (PID 33268,
380.5625 CPU seconds at the sampled check). The 600-second outer timeout eventually
terminated the harness. This is not evidence of another transfer stall.

`tools/FileTransferOps/AnalyzerOrchestration.ps1:12085` marks the start of the
whole efficiency stage. That scope includes semantic selection, indexed metrics
and subsequent report work; existing diagnostics do not identify the exact slow
call. Do not attribute the timeout to one helper without a bounded measurement.

### First reported semantic edge: recent-grace recheck not accepted

Before the timeout, the partial compact/stability output reported
`FAIL_PROTOCOL_OR_INTEGRITY`, hard-failure count zero, and one unresolved
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`.

Its first witness is retained-log **line 73754**, receiver writer
`p39532r6db1cfefe0b24a92`, writer sequence **42480**:
`filetransfer_v6_receiver_state_deferred`, frontier 12477, highest 12483.

Later records from that same writer exist:

| Retained line / writer sequence | Observation |
| --- | --- |
| 73759 / 42483 | Frontier request deferred after recent grace; stall grace armed for frontier 12477. |
| 73762 / 42484 | Recent-grace recheck decided: `rechecked / stall_grace_armed`, frontier 12477. |
| 73773 / 42493 | New recent-progress grace at frontier 12480. |
| 73777 / 42498 | Earlier stall-grace callback rejected as `stale / frontier_scope_changed`. |

The presence of a recheck log is not proof that the analyzer's exact obligation,
deadline and scope match. Conversely, the partial verdict alone does not justify
a production runtime change. Determine why the existing matching/closure logic
does not accept the relevant proof before choosing a fix. The final stale callback
must remain diagnostic-only, never a substitute successful recheck.

The partial terminal summary also reports `sha_ok=0`, despite both files being
hash-correct in the independent cleanup check. This discrepancy and the incomplete
parent run remain explicit; do not convert local hash success into analyzer or
route acceptance.

## Bounded next step

1. Review the exact recent-grace obligation and matching/closure predicates against
   the retained receiver records, including progress, wake generation/deadline,
   route/runtime/data-session identity and subsequent supersession. Build a focused
   deterministic diagnostic regression for the first mismatched edge; do not infer
   recovery from terminal success, a nearby event or a stale callback.
2. Localize the efficiency-summary delay with bounded substage timings/counters or
   a deterministic diagnostic fixture from this saved capture. Preserve event
   identity, output meaning, original acceptance and original time budgets. Do not
   run another full native capture merely to obtain finer analyzer diagnostics.
3. Propose and implement only the demonstrated analyzer/fixture correction, with
   directly affected tests and negative scope/deadline controls. Production code
   requires separate current evidence of a production defect.
4. Rebuild and retry the single native gate only after the semantic and execution
   blockers are addressed. No full GUI matrix or deadline increase now.

This is an RCA boundary and fix plan, not a claim that the exact analyzer root
cause has already been isolated.

## Preservation and cleanup

Cleanup at 06:36:16 UTC found no conflicting native/test processes and no remaining
sender, receiver or primary PIDs. Both 256 MiB files were unlocked and hash-correct;
no user files were deleted. Capture and partial analysis artifacts remain retained.

Post-run verification matched all **4,563 frozen pins** and **571 existing dirty
entries**. The 139 saved affected test results were verified by preflight, not
rerun. This report is the only new nonignored file. No commit or push was made.

Rebuild/preflight results, failed TRX, exact transfer observations, partial semantic
witness records, cleanup verification and the final preservation seal are under
`artifacts/canonical-prefix-setup-native-20260929/`.
