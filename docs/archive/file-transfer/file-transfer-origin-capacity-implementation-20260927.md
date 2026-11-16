# Origin-capacity implementation — 2026-09-27

Status: approved count-policy revision implemented; 28 focused tests pass.
The single fresh canonical fallback integration failed **before ledger
processing**, so end-to-end validation of the new capacity remains unproven.
No live run is due at this boundary.

## Scope and implementation

The user's “Yes” approved the bounded proposal in
`docs/file-transfer-origin-capacity-contract-review-20260927.md`. That review
remains an immutable historical record, not the current approval status.

- The processor allows `3 * MaximumLines` = **786,432** total origin references
  and rejects a role exceeding **262,144** before appending.
- Semantic-copy validation remains capped at 262,144, not the aggregate cap.
- Native facts decoding shares the processor's aggregate constant and bounds
  each role by the actual source-line count and remaining total capacity before
  allocating/reading its origins.
- The flat Int32 format/version, fixed **4 MiB** serializer/channel envelope,
  source offsets, exact output counts, ordered physical origins, bookmark suffix,
  native identity joins, work/cancellation gates and primary ownership are unchanged.
- No transfer runtime, coordinator, bridge, catalog, witness-selection, acceptance,
  chunk/retry/queue policy or elapsed-work budget was changed.

Production edits are confined to `GuiSmokeLogLedgerStreamingProcessor.cs`,
`LocalFrontierParentFacts.cs` and `LocalFrontierParentFactsValidation.cs`.
The cap-specific integration test now requires full outputs, native handoff and
the original primary evaluation; a later refusal cannot count as a passing test.

## Focused verification

Both `pwsh` and Windows `powershell` were exercised, without aggregate smoke or
GUI-matrix reruns. Results are under
`artifacts/origin-capacity-implementation-20260927/results`.

| Lane | Final result |
| --- | --- |
| Stream processor: original/legacy outputs, above-old-cap counts and overflow | 2 passed |
| Retained semantic copy: identity, exact counts, overflow and cancellation | 2 passed |
| Native parent facts: original receipt and expanded codec controls | 2 passed |
| Affected source, facts, primary-origin mutation/cancellation/work guards | 22 passed |
| Fresh canonical fallback process tree | 1 failed; upstream transfer did not complete |

Count-only controls reach all three per-role maxima and the 786,432 aggregate
maximum, reject the next append without changing counts, and independently test
the aggregate guard. Stream processing admits both 262,144 and 262,146 references.
Codec controls round-trip 262,144, 262,145 and 277,488 references, retain an empty
map and the per-role maximum, and reject malformed counts, origins, retained
suffixes, truncation and cancellation. The former decoder's bounded-count reader
still refuses above-old-cap counts when called with its former aggregate rule.
The all-maximum dense packet is still rejected by the 4 MiB limit.

Expanded codec bytes are synthetic consistency controls only. They never enter
a native custody channel and do not prove an original-custody large serialization.
The existing original below-cap native receipt continues to pass.

Two test-helper defects were fixed during verification: a dot-sourced variable
collision and the initial committed offset when the bookmark equals EOF.
Their failed results (`count-boundaries.trx`, `facts-boundaries.trx`) are retained.
`count-boundaries.trx` also contains the four passing processor/copy cases;
`facts-boundaries-fixed.trx` contains the final two facts passes and
`affected-guards.trx` the remaining 22. No production workaround was added for
either test-helper failure.

## Fresh canonical attempt: observed first gap

Run: `pwsh-canonical-5b4c5875a1d74caba18ffb45fc800667` under the implementation
artifact directory. This was a headless, offline named-pipe carrier process tree
using the original app-owned service and production Core/NKN paths, **not live
NKN or the interactive GUI matrix**. There was exactly one attempt, with the
existing 180-second app cancellation deadline and all original work limits.

The original ledger contains 2,629 lines / 2,656,917 bytes, SHA-256
`fcbd5b38d57313e7632bf27fbd56e33b4c542ca3619df69b4218474bc47cc511`.
Selected physical source lines:

| Line | Observation |
| --- | --- |
| 811 / 835 | Inbound/outbound coordinator route publication: `post_tuna_fallback_v6`, protocol 6 |
| 873 | Outbound fallback sparse-worker installation 3 recorded |
| 891 | Receiver requests a 512-chunk window |
| 915 / 923 | Correlated frontier request queued on control/bulk lanes; queuing alone grants no authority |
| 957 | Outbound receive-result rejection: `receive_install_missing`, installation 0 |
| 1041 | Receiver emits a frontier-stall repair window |
| 1799 | Receiver peer-silence timeout, 90,000 ms, zero transferred bytes |

The app exceptions occur at `FrontierAttemptNknCarrierTests.cs:256` (sender
waiting for terminal transfer state) and `:264` (receiver waiting for destination
rename), when the existing fixture deadline cancels. The subsequent writer
shutdown and parent-handoff errors are downstream consequences, not evidence
of an origin-cap or processor-work failure.

No `frontier-cursor-*` outputs, finalized destination, destination-to-GUI
adoption marker, native writer handoff, or primary analysis output was produced.
The 256 MiB source was released for exclusive access, and the two app and GUI
processes exited. The GUI capture itself did not report a watchdog timeout;
its child exited with failure after the app-level cancellation.

The first unclosed segment is fallback request consumption/data progress after
route publication. `receive_install_missing` is a useful diagnostic lead, **not
yet a proven cause**: the logs alone do not establish that this rejection caused
the stall or that a stale frame should have been accepted. The count-policy
code was not reached, and this attempt does not validate or disprove its native
large-input work-budget behavior.

## Next bounded step

Prepare the fallback receive-install RCA from this newest run: correlate the
original request/frame, captured receive-install descriptor and coordinator
publication across the Regular → Tuna → fallback handoff. Determine whether
the rejected receive had no original install, an intentionally stale install,
or whether the current worker failed to consume a subsequent valid request.
Use a deterministic regression for that first edge before proposing runtime
changes. Preserve stale-callback refusal, coordinator-only authority and all
current limits; do not turn queue acceptance into runtime authority.

Do not increase the 180-second fixture deadline, the 90-second peer timeout,
the 5/10-second processor/GUI budgets, or retry this transfer automatically to
obtain a green result. Native above-old-cap processing, handoff and original
primary evaluation still need one successful canonical attempt after the
upstream issue is resolved. No second Tuna publication or route-cycle acceptance
has been established by this work.

## Preservation

Baseline: `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, 378 dirty
entries. All 1,474 prior checkpoint pins matched before editing. Existing
unrelated work and earlier successful/failed artifacts are preserved. The new
checkpoint is `artifacts/origin-capacity-implementation-20260927/FINAL-VERIFICATION.json`.
No portable rebuild, live network run, full matrix, commit or push was performed.
