# Frontier publication: live-boundary contract review

2026-09-15. Reviewed HEAD `2691fc70f98d668bae943ebd41c6f8540156ec57`
plus the uncommitted A1-A5 changes. This is a review and fix plan, not live
enablement or a new correctness disposition.

## Conclusion

The primary-owned offline mapping is verified, but is not ready for live use.
The smallest next implementation is **episode-local witness selection under the
existing native custody**. Keep the 64-line limit and original primary verdict.
Boundary clocks and an owned payload/parent channel are separate later gates;
changing the injected clock label or passing a JSON path does not close them.

The existing delayed-publication contract remains diagnostic-only. In particular,
`D` is the original stall deadline, `W1` is the first qualifying contiguous
advance, and `P = W1 + 1000 ms` is frozen. Neither runtime timing nor existing
`Recovered`/`Superseded` accounting is changed by this review.

## 1. Witness selection includes unrelated history

`tools/FileTransferOps/LocalPrimaryFrontierPublicationWitness.ps1:178-185`
selects the complete matching same-transfer diagnostic history. Lines 216-224
also select every record for the owned Coordinator instance, not just links
needed by the selected obligation. Full-input validation is necessary; exporting
all those records as witnesses is a separate, broader operation.

Read-only inspection of the final A5 retained sources confirms the consequence:

| Final fixture | Retained lines | Receipt marker | Later transition Snapshots | Later state-send diagnostics | Selected witnesses |
| --- | ---: | ---: | ---: | ---: | ---: |
| `owned-bounded-0b4fbf22f05246739534ce3a1e9febab` | 66 | 59 | 2 | 1 | 52 |
| `owned-overflow-7840695f4aa944df8901373003ad3178` | 124 | 59 | 26 | 9 | omitted |

Both directories are under `artifacts/frontier-publication-primary-20260915`;
the table reads their `primary-7/gui/valid/filetransfer-retained-log-slice.log`
and `primary-7/witness.json`, without invoking an evaluator. The fixture adds
eight ordinary sends after closure and asserts that the original receipt object
is unchanged (`SessionFileTransferFrontierPublicationEvidenceTests.cs:236`).
The recorded A5 gate identifies the omission as `frontier_witness_budget`.

This is correct fail-closed behavior, but it does not establish a usable bounded
selection for a longer live stream. It is not evidence of a runtime send defect.

Required next boundary:

- Keep full native origin/writer validation and the original completed full/
  compact views. Do not rerun authority or narrow those inputs to a convenient
  time window.
- Derive a closed dependency set from the primary's exact failure and its owned
  repair/recent/stall/commit/frame/completion chain, plus required stream anchors.
  Preserve all source invalidations and contradictions relevant to that chain.
- Select exact required physical lines only. The original state-send diagnostic
  occurs after the receipt marker, so a blanket marker cutoff is incorrect.
- Keep ambiguity, missing links, conflicting copies and more than 64 genuinely
  required lines as omissions. Never choose a nearby successful receipt, truncate
  necessary evidence, increase the cap or turn the detached result into authority.

## 2. Live boundary timing is not specified or captured

`FileTransferCoordinator.FrontierPublicationEvidence.cs:46,226` uses an injected
`Func<DateTimeOffset>` and rejects backward samples. It has no live clock/process
identity or elapsed-clock evidence. At `PullTransferSessionV6.cs:5333`, the
observer samples after contiguous commit but before the separate runtime
`PullLastCommittedProgressUtc` assignment. Frame selection and accepted
completion have their own observer samples. A writer timestamp is later still.

The original stall descriptor is created from runtime UTC at lines 7606-7672;
the wake delay is calculated from that UTC deadline at line 7723. Completion
observation runs after the existing accepted Coordinator decision is adopted
(`SessionFileTransferService.cs:20313`). Its Snapshot decision time is not an
interchangeable timestamp for these other boundaries.

A live profile must name the exact owner and sampling point for original
deadline creation, W1, first covering frame and accepted completion. Preserve
the original accepted envelope; no extra authorization, Apply or favorable
post-hoc timestamp selection. Existing injected payloads remain permanently
ineligible, with their current schema/hash domains unchanged.

Recommended clock-design candidate: a process/clock-instance-bound monotonic
stamp with a UTC companion captured at those boundaries. **The conversion from
the original UTC runtime deadline to that observation clock must be reviewed
before implementing this profile.** It cannot silently replace D with a later
sample plus 750 ms, or redefine the old UTC-based contract as an elapsed-time
guarantee. Specify rounding, clock discontinuity and ambiguous-boundary omission
without inventing a tolerance. Do not change runtime clocks or wake scheduling.

There is also an uncovered lifecycle case to resolve before live capture:
`RefreshInboundV6FrontierStallGraceWakeForHighestProgressLocked` at line 7675
creates a same-deadline stall-generation successor without an observer hook.
The observer records only the initial stall generation; a pre-W1 zero-advance
commit updates its highest value without recording that successor. Either bind
the exact passive successor chain or explicitly omit such an episode. Do not
assume recent-grace rearm coverage also covers the distinct stall family.

Normal finalization and untimed invalidation entries do not establish timed
coverage through P. Keep timed absence unsupported unless that coverage is
separately established; no timer, sleep, send or retry may be added to produce it.

## 3. Digest retention is not a live payload channel

`FileTransferCoordinator.FrontierPublicationWriterBinding.cs:14-32` emits a
digest-only marker for the offline canonical payload. Payload serialization is
used by fixtures; `AnalyzerOrchestration.ps1:2761,6405` accepts an explicit
optional receipt string. The app and live runner do not supply it.

`LocalGuiWriterRequest.cs:339-375` decodes three fixed native output descriptors
and the retained semantic copy. It has no receipt-payload descriptor. A separate
versioned extension must own bounded immutable payload bytes, their exact writer
marker/hash, request/run/scenario/process incarnation and completed source file
identity/read lease. Define strict UTF-8 byte and count limits, cleanup and
partial/cancelled/foreign-output rejection. The current string code-unit cap is
not automatically the byte budget of a new transport.

The receipt contains richer internal source/token material than the projected
log marker. Review its export projection before persistence; do not dump the raw
receipt into operational logs or discover a payload by scanning for a matching
or newest file. Capture immutable evidence under the existing gate, but do not
add payload file I/O there. Current receipt hashing/serialization already runs
inside the accepted completion path; future live export needs bounded cost and
failure-containment tests so optional diagnostics cannot alter send settlement.

`LocalPrimaryPublicationCompletion.cs:38-100` issues the existing
`nlink-local-publication-forwarding-v2` capability for a different selection
contract. Do not reuse it for frontier receipts. Any frontier parent channel
must separately bind the original primary custody and completed evaluations,
recheck ownership across preparation/publication, and reject replay or revoked
ownership. The parent must not reconstruct authority from detached JSON or run
another authority evaluation. Initial consumption remains diagnostic-only.

## Bounded fix and verification sequence

1. Implement episode-local witness selection only. Use fresh real writer/native
   primary fixtures on PowerShell 5.1/7: unrelated earlier/later history must not
   exhaust the cap; a genuinely over-cap dependency chain must still omit.
   Cover relevant contradictions, ambiguous receipts, source revocation and
   exact physical-origin parity. Preserve primary failures/counts/views, existing
   evaluation sequence and zero additional authority evaluations. Replace the
   current unrelated-send overflow expectation; retain a real required-chain
   overflow control. No clock, payload, runtime or acceptance edits in this step.
2. Settle the live clock/deadline mapping and stall-successor contract, then add
   opt-in passive capture with focused fresh offline tests. Cover exact D/P
   boundaries, discontinuities, delayed logging, same-deadline successors,
   source/pause ABA, first-frame rejection and enabled/disabled non-interference.
3. Review and implement the bounded writer-owned payload and separate primary/
   parent channel. Test exact bytes/identity, tampering, partial output, capacity,
   cancellation after preparation, replay and export failure without changing
   primary or runtime outcomes. Retain old-channel compatibility.
4. Only after these diagnostic paths pass, review any newly versioned
   correctness requirement separately. Then determine whether a checkpoint,
   portable rebuild, strict escalated preflight and the single targeted live-256
   test are warranted. No full matrix or historical reclassification.

## Review verification

Source and final A5 artifacts were inspected read-only. Prior verification is
27/27 selected tests in the A5 RESULTS file, not a new run in this review.
This change adds documentation only; `git diff --check` is the review edit gate.
No runtime change, evaluator replay, rebuild, live test, commit or push occurred.
The saved live result remains `FAIL/incomplete_proof`.

## Follow-up: selection step implemented

A6 implements step 1's bounded episode selection, with full-input validation and
the 64-line cap unchanged. See the A6 checkpoint in
[the delayed-publication contract](file-transfer-delayed-publication-contract.md)
and `artifacts/frontier-publication-episode-20260915/RESULTS.md` for verification.
The history/cap finding above describes the reviewed A5 tree, not the updated
mapper. Live clock/deadline semantics, stall-successor coverage, and owned payload/
parent consumption remain unresolved; this follow-up does not enable them.

## Follow-up: clock and successor design settled

The [boundary-clock and stall-successor contract](file-transfer-publication-boundary-clock-contract.md)
now resolves the design choice: preserve UTC deadlines and require conservative
elapsed-counter agreement, without translating D or using Snapshot decision time.
It specifies actual successor capture, the recent-recheck inter-gate race, and
the missing V4-compatible write/flush coverage. These are finalized diagnostic
requirements, not implemented live support. The next implementation is the pure
timing predicate and immutable stamps only; payload/parent and correctness gates
remain separate. No authority evaluation or historical reclassification was run.

## Follow-up: private boundary validation and producer preparation

A7a-A7c implemented the isolated clock helper, opt-in actual boundary capture and
private bounded input-line mapping. A8a now prepares a separate privacy-projected
producer payload independently of that completed input log. Its final focused
gate passed 21/21; this is not native payload transport or live acceptance.

The [payload/channel contract](file-transfer-frontier-payload-channel-contract.md)
records the remaining native app/writer/GUI ownership links, transport limits and
separate primary/parent channel. The original injected path and saved live verdict
remain unchanged. No live rerun or historical reclassification followed.

## Follow-up: isolated native app-process request

A8b1 adds only the separate native request/lease primitive, verified with actual
PowerShell child processes on both supported hosts. The four final test rows
pass, including 46 child-process scenarios and native parent-death rejection.
It does not activate nLink capture, publish payloads, establish writer/source
custody or connect the original GUI request. See the A8b1 checkpoint in the
[payload/channel contract](file-transfer-frontier-payload-channel-contract.md)
and `artifacts/frontier-native-app-20260915/RESULTS.md`.

Payload catalog/admission/work limits and exact producer/writer/GUI ownership
remain the next gate. No old channel or historical result was upgraded, and no
new live-256 run, portable rebuild, commit or push occurred.
