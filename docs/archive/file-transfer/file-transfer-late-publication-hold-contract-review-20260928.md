# Late-publication hold contract: bounded revision proposed

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Decision requested

Recommend a **fixed 4000 ms per-frame retention cap only for the small offline
`frontier-late-publication` profile**, selected before the first held frame.
Keep the existing positive `frontier-schedule` profile and default scheduler at
3000 ms. Keep the native deadlines, publication delay, route/receipt checks and
primary acceptance unchanged.

This is an explicit relaxation of a **test-fixture resource bound**, not a
production fix or an acceptance-policy relaxation. It requires separate approval
before implementation. The current code still uses 3000 ms for both profiles.
This review ran no build, test, analyzer, transfer, live NKN or GUI scenario;
it changed no source, binary, timer or previous result.

## Why a simple reordering is not established

The latest original run (`6f37e470add047c586ac19e58f9e5c6d`) failed at window
admission. Its original diagnostic proves the conservative hold budget ended at
S+676.0408 ms, before the required late-completion boundary D=S+750 ms. Arming
was timely. This establishes unavailability of that episode under the current
contract, not impossibility of every future episode.

Source review identifies no supported local ordering adjustment that supplies
the missing time while retaining the same required proof:

- Holds start on receipt of complete original pipe frames, at
  `AttemptOfflineFrameSchedule.cs:153`; both `ReadBudget` (line 130) and expiration
  (line 187) use those original start times. Moving retained bytes to another
  queue or restarting the clock when the root appears would hide/renew retention.
- The initial gate requires all eight ordinary frames and frontier 0/highest 7
  before the exact repair of chunk 0. The sparse write of chunk 2 cannot replace
  the real recent-grace wait: the native observation requires one second from
  committed repair progress, then the original stall/recheck. See
  `AttemptOfflineLatePublicationProgress.cs:33-79` and
  `SessionFileTransferService.FrontierPublicationBoundary.cs:207-289`.
- Releasing chunk 1 before the original recheck loses the required first-write
  relationship. Releasing chunk 4 before chunk 1 changes the selected 1-to-4
  geometry; releasing it while publication is pending breaks the reviewed
  closure/copy-before-release contract and risks a competing state. The existing
  exact release order is 2,1,4; chunk 4 waits until original closure and all three
  physical-copy checks (`AttemptOfflineLatePublicationProgress.cs:133-151`).
- The pipe serializes writes and reads complete framed messages. Moving a wait
  into its writer/read loop does not create a reorder facility and can block
  the traffic needed for the original repair. A new sender-side reorder queue,
  fabricated repair request, skipped grace, altered chunk geometry or production
  scheduling optimization is a larger, different intervention, not this fix.
- Earlier copy release cannot be a predeadline success: the target remains
  `W < D <= C < W+1000 ms`, with both native UTC/QPC checks. For this episode even
  eliminating the test completion reserves would leave the conservative hold
  deadline before D. Repeating unchanged runs in search of favorable timing is
  not the recommended next step.

These findings do not claim a general theorem ruling out all alternative test
designs. A materially different packet/repair scenario would need its own proof
contract; no such replacement is demonstrated by this review.

## Proposed fixed policy and arithmetic

Choose 4000 ms as a bounded policy proposal: the existing 3000 ms fixture ceiling
plus one complete 1000 ms publication-observation interval. It is not a uniquely
derived minimum or a promise that future timing will fit. Do not expose an
arbitrary duration, environment override, retry-dependent increment or adaptive
extension. Do not keep increasing the cap if a later run fails.

Read-only arithmetic using the latest original counter inputs gives:

| Quantity relative to S.CounterAfter | Current 3000 ms | Proposed 4000 ms |
| --- | ---: | ---: |
| Conservative data-hold end | 676.0408 ms | 1676.0408 ms |
| Margin after G+200 ms | -323.9592 ms | 676.0408 ms |
| Absolute publication-owner expiry | 576.0408 ms | 1249.9999 ms |
| Margin after G+100 ms | -323.9592 ms | 349.9999 ms |

The proposed column is **counterfactual arithmetic only**, not a rerun or a
reinterpreted receipt. The original first-write/copy times do not exist in this
failed run. The strictly required retention cap for these captured admission
inputs would exceed 3323.9592 ms; that threshold is not an implementation value.
At 4000 ms the unchanged S.CounterBefore+1250 ms owner limit becomes tighter than
the data-hold limit. The actual write and each original direct-copy deadline
still require their existing independent checks.

`artifacts/late-publication-hold-contract-review-20260928/budget-arithmetic.json`
records this calculation and the exact original input hash. No app class,
coordinator evaluator, analyzer or native receipt producer was invoked.

## Exact bounded implementation, if approved

1. Give the test scheduler a closed, immutable choice between the original
   3000 ms policy and this profile's 4000 ms policy. Default remains 3000 ms.
   Use the same selected value in the existing budget snapshot and expiry check.
   Both policies start each clock at the original `Held.Started` capture;
   observing, releasing another frame or binding a publication never renews it.
2. Select the longer policy only at the receiver scheduler construction in
   `FrontierAttemptNknCarrierTests.cs:339-342`, using its existing explicit
   `latePublication` selection. Preserve small-only and Debug admission. No new
   live/GUI/Release switch or reusable runtime configuration is added.
3. Record the selected fixed policy/cap once in test diagnostics, for example
   `late-publication-fixed-4s-v1`, so future evidence cannot be mistaken for the
   old 3-second profile. Leave earlier artifacts and verdicts untouched.
4. Keep the scheduler's three-frame/1 MiB bounds, original descriptor/byte
   identity, one-shot release order, 10 ms expiry checks, cancellation and
   fail-closed clearing. Expiry discards held data; it does not release it as
   success. Keep the publication primitive's shared 1 MiB accounting.

Expected code scope: the scheduler, its existing carrier construction site and
directly affected scheduler/window tests. No new queue, abstraction layer,
transport seam or source-side reordering is needed. If implementation requires
a production-path change, stop and revise this scope rather than bundling it in.

## Invariants not revised

- Original repair [0,1), recent grace, same process/source/observation/root,
  no successor, first write 1-to-4/highest 7, and original accepted completion.
- S+250 chunk release, S+800 all-copy release, native 750 ms stall deadline,
  1000 ms post-write bound, ordinary 750 ms direct-copy timers started before
  waiting, and 100 ms completion plus 100 ms data-hold reserve.
- The unchanged window check and earliest-bound owner expiry. The new data-hold
  cap does not grant an extra second to the publication owner or native receipt.
- Exact two control/one bulk physical writes, original copy results, currentness
  checks, rejection/retirement cleanup and no selected late write after cleanup.
- Coordinator-only route/runtime authority and strict Regular-v4/Tuna-v4/fallback-v6
  route invariants. No production timer, retry, queue/concurrency, wallet,
  throughput or GUI behavior changes.
- Original primary failure selection, 1-64 original witnesses, three evaluations
  and one native source read, report/view/verdict preservation, original output
  commit and cleanup. A late receipt or transfer SHA alone is not acceptance.

## Verification and next gate

After approval, implement and run only directly affected deterministic controls:
default/positive 3-second expiry, explicit late-profile 4-second expiry, immutable
selection, unchanged original start times across reads/later frames/releases,
byte/order/capacity parity and expiry/cancellation/disposal without delivery.
Include exact-boundary checks and verify budget reporting agrees with enforced
expiry. Retain original 3-second insufficient-budget rejection and add a clearly
synthetic 4-second arithmetic-fit case without presenting it as a native receipt.
Check invalid/non-late/full-size profile admission cannot select the new policy.

Review the stabilized diff and focused results once; do not rerun unrelated
aggregate lanes. Then stop before proposing one rebuilt, isolated small original
pair run with fresh pins, policy-tagged evidence and a create-new run marker.
No automatic retry. Any newly observed missing edge receives its own RCA.

Even a successful revised run would not pass the old 3-second characterization,
validate production latency, establish canonical same-pair live-256 proof or
authorize the full GUI matrix. Those remain separate gates.

## Preservation

Entry matched all 2,042 previous pins and 511 dirty entries. This review adds only
this proposal and ignored arithmetic/preservation records. Original source,
tests, binaries, both failed characterizations and all earlier reports remain
unchanged; the final independent seal checks them. No commit or push.
