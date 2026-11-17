# Canonical same-pair native witness: bounded integration contract

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`, plus the preserved worktree.
Disposition: **proposed test-only implementation contract; no implementation or
transfer authorized/performed by this review**. The tail geometry below is
structurally feasible from the current source. Native timing, first-failure
selection and full-size work-budget compliance remain execution gates, not claims.

## Objective and boundary

On one original offline 256 MiB transfer, carry its protected source/destination
pair and its genuine late-publication receipt through the existing GUI, wrapper,
parent and original primary. Verify that the primary's own selected failure and
issued exact native witnesses belong to that same evaluation-owned pair.
Preserve the original failed route verdict and every authority flag.

Do not repeat the successful small witness test or ordinary canonical fixture
as the new milestone. Do not combine their artifacts. Live dispatch, selected-
failure pre-verdict consumption, portable packaging and live acceptance remain
later gates. No production route, authority, timer, queue, chunk size, retry,
throughput, wallet, installer or GUI acceptance change is proposed.

## One explicit new profile

Use a separately named, default-off Debug/Windows offline profile, provisionally
`canonical-tail-late-publication`. It must opt into the existing actual-file-pair
handoffs and the ordinary native diagnostic catalog in the same original attempt.
Keep the existing small-profile/transfer-pair exclusion unchanged; admit only the
new exact composite profile, not arbitrary combinations of old flags.

Explicit departures from the current small profile are limited to:

- The real held canonical file is the sender's source; its actual destination is
  offered through the existing native destination evidence path.
- The eight-frame fault window is at a fixed suffix offset, not chunk zero.
- The existing Debug single-segment override is armed only near that suffix.
- The original primary's existing pair binding and native witness observations
  coexist in one test worker, with an ownership/scope assertion between them.

Existing canonical, small, no-selected-failure and negative profiles retain their
guards and expectations. The new worker uses the test-only attempt loader;
normal loaders and live scenario dispatch are unchanged. Do not introduce a
generic fault framework or generalized profile matrix for this one case.

## Concrete suffix geometry

The preserved canonical metadata (physical line 143 of its operational log)
records 268,435,456 bytes, 21,504-byte chunks and 12,484 chunks. The new profile
must require that actual geometry; mismatch means unavailable, not adaptation.
Let N=12,484 and B=N-8=12,476. Chunks B through B+6 are full; B+7 is 1,024 bytes.
The prefix below B contains 268,283,904 actual source bytes.

| Original ordinary frame | Fixed receive action |
| --- | --- |
| Before B | Deliver unchanged; no fault payload retained. |
| B | Drop once after its actual complete pipe write/read. |
| B+1 | Hold; release only at the original stall's fixed S+250 ms target. |
| B+2 | Hold; release during actual recent grace to obtain a fresh noncommitting write. |
| B+3 | Deliver unchanged. |
| B+4 | Hold until original accepted late publication and all three physical copies finish. |
| B+5, B+6, B+7 | Deliver unchanged, including the short final chunk. |

This uses the existing release order [2,1,4] relative to B. Descriptor identities,
original bytes, encrypted envelopes, actual pipe writes and normal application
delivery remain unchanged. Never trim, split, reserialize or relabel a captured
frame. Repair/control traffic remains on its ordinary path.

The receiver must naturally reach frontier B/highest B+7, then the original
coordinator-authorized repair of exactly [B,1] must advance B -> B+1. A previously
source-bound or closed frontier observation, competing repair, prefix gap,
different range, partial geometry or changed source makes the profile unavailable.
Do not reset the observation or choose a later episode. An existing unbound
observation may already contain ordinary prefix-progress observations; that is
not permission to clear or replace it.

The receiver waits for the first original suffix frame under the **already
running 180-second full-size carrier deadline** before entering the existing
eight-second episode waits. Do not start the small profile's first eight-second
wait at the beginning of a 256 MiB transfer, and do not restart the overall clock
at the suffix. Each held frame's four-second expiry still starts at its actual
receipt, independently of when the controller observes it.

After repair, release B+2 only while the original recent-grace wake is current.
Require the real receiver-state deferral at frontier B+1/highest B+7. Wait for
the original recent recheck and unchanged stall root, then release B+1 at the
existing target. The subsequent first write must commit three chunks,
B+1 -> B+4 (12,477 -> 12,480), with highest B+7. Its receiver-state frame must
report 268,369,920 committed bytes and remain nonterminal/unpaused. Exactly
65,536 bytes remain, so this write does not fall into terminal finalization.

Use the existing all-copy late-publication owner unchanged: three original
physical copies, original UTC/QPC stamps, S+800 ms copy target, native write before
S+750 ms, and accepted completion before the original W+1000 ms bound. Keep
the four-second per-frame hold, conservative reserves and earliest expiry;
reading a budget or waiting must not renew it. Require the original producer's
`PublishedWithinObservationBound` outcome, then release B+4 and finish the real
file. An ordinary retry-state publication or changed root/source is a failure,
not something to suppress.

## Localized packetization admission

Default multi-chunk batches cannot simply be retained across the first repair:
overlap with the repair range makes an old whole batch contract-bound. Reuse the
existing Debug single-segment mechanism only for this explicit test suffix.
Do not enable it for the entire 256 MiB stream.

Proposed arming point: before the first actual fallback-runtime source read at
chunk A=B-3=12,473 (byte offset 268,219,392). The existing batch builder resolves
its segment limit before loading that batch's chunks and caps the old limit at
three. A sequential old batch reaching A can end no later than B-1; subsequent
batches see the single-segment setting. This is a structural bound, not an
assumption that callbacks always arrive in an ideal order.

Implement the arming observation in the test wrapper of the fourth, already
coordinator-current retained reader. Do not change `RuntimeReads` admission,
its four-open limit, metadata reads, retirement checks, source bytes/length,
seek behavior or read results. The observation must never block a read or pump,
wait for the receiver, add another reader, or create an acknowledgment. Keep
one isolated sender process, require the original default batching setting,
record one arming transition, and restore the exact previous process setting in
finally. No user/machine environment change is allowed.

`LoadChunkBytesForSendAsync` can return cached bytes without invoking the stream.
Therefore the arming callback alone is **not** geometry proof. The bounded
metadata observer must verify eight unique original single-chunk ordinary frames
at B..B+7, with exact session/transfer, transport epoch, hashes and lengths before
the episode can succeed. Reject a frame crossing B, unexpected target geometry,
missing arming, replay or duplicate; do not retry with an earlier arming point.
Before-window frames pass unchanged and consume no retained descriptor slots.
Out-of-window repair/control frames remain unchanged; do not attach fake hints.

This localized reuse is a new test-profile exception requiring approval with
implementation. It is not production performance tuning. Deterministic tests
must cover partial reads, seeks/cache-bypass admission, the old three-segment
batch spanning A, cancellation and exact environment restoration.

## Same-pair ownership assertion in the original primary

Reuse the canonical source/destination adoption order: offer the actual pair
while the original GUI writer join is current, then complete native writer
copy/persist/prepare/complete once. Do not separately run the small diagnostic
finalizer after it; those paths retire the shared owner. Preserve protected
duplication before each wrapper/parent retirement and native cleanup afterward.

The combined test worker must keep the original analyzer, parser, full/compact
arrays, evaluation pair, views and first selected failure. Bind the actual pair
once at the existing Capture-stage seam. Let the original conditional resolver
run once, only for its own supported unresolved recent-grace failure. Do not
substitute a failure when another class is first, force eligibility, or capture
another payload set/evaluation pair.

Immediately after successful original resolution, before diagnostic output and
retirement, verify the pair and batch together inside their original lifetime:

- The exact issued transfer evaluation and exact issued `NativeWitnessBatch`
  share the original `EvaluationCustody` and `EvaluationPair`. Check native
  ownership/currentness, not only public booleans, copied fields or equal hashes.
- The batch's original payload is owned by that custody's original payload set.
  Reuse its already captured/decoded native receipt for comparisons; JSON read
  from an artifact path cannot supply custody or a replacement receipt.
- Hash the held pair's original session/transfer strings in the existing evidence
  domain and compare to the receipt's source identities. The receipt/payload
  writer, process identity/creation and completion/marker sequence ranges must
  belong to the same retained receiver writer manifest, not merely either peer.
  This canonical direction remains helpee sender / helper receiver.
- Both original distinct held file identities, exact 268,435,456-byte lengths,
  and canonical SHA `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`
  remain bound. Preserve original source/destination SHA checks; do not rehash
  the whole files inside the primary as a new lifetime-expanding operation.
- Retain the original selected-failure object, exact witness line hashes and
  physical origins, original completion/marker ranges and unchanged evaluated
  views. Reject foreign/copy/retired owners, foreign pair/batch, repeated join,
  changed session/transfer or receiver lineage, and caller-created substitutes.

Prefer a small test-only assertion/helper in the existing partial custody/probe
code. No production API, new transferable capability or JSON-to-authority parser
is required. Ownership checks and the trusted probe's scope comparisons must
all occur before success is recorded, charged to the existing work budget with
currentness checked before/after. A boolean assertion or saved report is only
test evidence, never runtime or transfer-integrity authority. Keep all existing
authority flags false and clear retained references on every exit.

## Hard limits and result semantics

Retain eight descriptor slots, three held data frames, one MiB aggregate held
data/descriptor capacity, existing publication-copy capacity, one selected
publication and 1-64 witnesses. Keep native receipt size 262,144 bytes and the
separate actual-pair channel 8,192 bytes. Actual transfer files are not diagnostic
payload entries. No partial source log, pruned primary input or truncated witness
set may be used to fit a budget.

Keep 180-second full-size carrier, eight-second route/edge waits, 600-second
canonical outer fixture, original 1,800-second analyzer timeout, all existing
GUI/parent limits, and 20,000 ms cumulative native-primary active work. Input
record/code-unit and producer visit budgets remain unchanged. New admission and
join work must use the original clocks, never a fresh allowance.

The earlier canonical checkpoint used about 13.9568441 seconds before later
custody work, with 73,713 semantic rows; it is not a final-clock measurement or
a promise that the new resolver fits. Its saved source files are 69,851,353 bytes
(semantic) and 92,084,838 bytes (operational). The small 41-witness pass does not
establish full-input work cost. A budget refusal is a new first-edge result to
preserve, not permission to raise limits, filter input or retry automatically.

Success requires original parent 2/2/1 checks, three evaluations, one native read,
one resolver, matching full files, same-pair witness assertion, original output
commit, unchanged ten reports, original failed route verdict, false authority
flags, zero retained pins/text and independent process/file cleanup. Give this
new profile its own exact stop-before-route-acceptance sentinel. Do not reuse
canonical-fallback's expected `INCONCLUSIVE` result: this profile requires the
original supported unresolved failure and `FAIL_PROTOCOL_OR_INTEGRITY` / `fail`.

## Implementation and verification sequence after approval

1. Implement only the new test profile, fixed suffix geometry and read-side arming
   observation, reusing the existing bounded scheduler/publication owner. Keep
   the small geometry as its unchanged default; no whole-file packetization mode.
   Run focused geometry, byte preservation, capacity, expiry, partial-read,
   foreign-peer, failure and cleanup tests before integrating a transfer.
2. Compose the existing actual-pair handoffs and original late-witness probe in
   this one test worker. Add focused ownership/scope negatives and exact original
   call-count/report/cleanup guards, including full-size rejection in old profiles.
   Verify ordinary loaders still exclude these APIs and scripts parse. Do not
   repeatedly rerun unrelated aggregate lanes or old full-size captures.
3. After focused tests and review stabilize, separately gate one rebuilt
   `pwsh` canonical offline run with strict escalated preflights and a create-new
   marker. Freeze source/binary hashes; no automatic retry. Stop at the first
   unavailable geometry/timing, unsupported first failure, custody, budget or
   cleanup edge; preserve the original verdict. Live-256/full matrix remain out
   of scope even if the new offline integration passes.

## Review evidence and preservation

Source anchors: `FrontierAttemptNknCarrierTests.cs`, `AttemptSenderRuntimeReads.cs`,
`FrontierAttemptRuntimeReadTests.cs`, `AttemptOfflineFrameSchedule.cs`,
`AttemptOfflineLatePublicationProgress.cs`, `AttemptLatePublicationOwner.cs`,
`SessionFileTransferService.PullTransferSessionV4.cs:19071,19188`,
`SessionFileTransferService.SenderRepairCache.cs:268`,
`SessionFileTransferService.FrontierPublicationBoundary.cs:207`,
`FrontierAttemptDestinationJoin.ps1`, `FrontierAttemptPayloadPrimary.cs`,
`FrontierLatePublicationProbeGuards.ps1`, `LocalFrontierPrimaryWitnesses.cs` and
`LocalNativeFrontierWitness.ps1`. Paths/hashes are recorded in the evidence file.

All 3,611 prior pins and 533 dirty entries matched at entry. No tests, builds,
analyzers/resolvers or transfers ran. Only this contract and ignored review
evidence/seals were added; prior code, binaries and captures remain unchanged
(534 dirty entries). Final evidence/check:
`artifacts/canonical-same-pair-witness-contract-20260928/`.
