# Full-consumer budget review — 2026-09-29

## Decision-ready recommendation (not implemented)

Raise the native frontier evaluation visit cap to **1,073,741,824 (2^30)** and introduce a separate **60,000 ms cumulative evaluation-work allowance** after original native evaluation custody has been successfully created. Keep the existing producer/source-admission budget, startup waits, rendezvous waits, and unrelated publication paths at **20,000 ms**.

Use the **same accumulated Stopwatch**: no reset, fresh allowance, per-row refill, or second evaluation. The proposed 60 seconds includes earlier source work; it is not 20 + 60 seconds. Keep all input/file/text/packet capacities, producer-record admission, witness count, metadata storage, ownership checks, and route acceptance unchanged.

This is a finite resource-contract revision, not a correctness relaxation. It permits up to 40 seconds more cumulative native work after successful custody creation. The visit cap is a work counter, not an allocation or throughput target. Inputs that exhaust the independent time cap must still fail closed. **Neither this review nor the proposed time allowance guarantees completion of every maximum-size admissible input.**

No production/test source or limits changed in this phase. No build, dotnet suite, analyzer evaluation, native transfer, GUI/live test, commit, or push was performed.

## Evidence and scope

The current seal `artifacts/canonical-witness-verification-scope-native-20260929/FINAL-VERIFICATION.json` was verified: 6,967 pins, 612 original dirty entries, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`. The latest native attempt remains the failed fixture `pwsh-2246b17104064afc8325c3939ac10bda`; its first ownership check reached 4,194,305 visits at 19,456.0739 ms.

The model below audits one successful **existing canonical-tail consumer sequence**, including its test-only checks, from native evaluation creation through diagnostic commit and cleanup. It does not promise unlimited calls to public currentness accessors. Additional/replayed calls remain cumulative and can exhaust the cap.

The canonical path costs more than production: production does not run the per-witness probe or same-pair test join. Parent adoption/decoding uses its own custody/work clock and does not increment the worker's `evaluationVisits`.

## Charged state size

Let:

- `N <= 262,144`: original semantic source lines; `F <= N`: full rows; `C <= F`: compact rows.
- `K <= 65,536`: captured metadata cells (fields plus metadata-array elements).
- `W = 1..64`: witnesses; `M = 0..W`: witnesses with compact counterparts.
- `L <= 32`: payloads; `P = 10 + L <= 42`: native descriptors (a successful selected-payload path needs at least 11).
- `B = ceil(packetBytes / 32,768) <= 128`, from the unchanged 4 MiB packet cap.

Before diagnostic state is bound, a whole-array check costs `Q0 = F + C`.

Diagnostic-state capture charges `T = 3F + K`. State revalidation charges `D = 3F + K + 8`: seven metadata-sequence length checks and one event-array length check, in addition to original fields/elements and three checks per event.

After binding, every whole-state check costs:

`Q = Q0 + D = 4F + C + K + 8 <= 1,376,264`.

These charges come from `LocalFrontierDiagnosticState.cs` and `LocalFrontierPrimaryEvaluations.cs`; there is no additional authority evaluation.

## Post-acceptance call inventory

Count starts at the resolver's native “accepted” observation, not at the beginning of the PowerShell wrapper.

| Consumer | Whole-state checks | Other visits |
| --- | ---: | ---: |
| Resolver GetWitnesses + IsCurrent | 2 | 0 |
| Probe GetWitnesses + per-row ownership/full/compact origin | 1 + 2W + M | 0 |
| Canonical same-pair join | 2 | 2 |
| PrepareDiagnostic, entry and exit | 2 | P |
| OfferDiagnostic initial guard | 1 | 0 |
| Packet Encode: start, each descriptor, each witness, finish | P + W + 2 | 0 |
| Channel Offer: start, each 32 KiB block, finish | B + 2 | 0 |
| CommitDiagnostic plus Channel Exact/Commit | B + 3 | 0 |
| Idle adoption wait, AfterOptional reflection checks, disposal | 0 | 0 |

Thus canonical post-acceptance cost is:

`R = (15 + 3W + M + P + 2B)Q + P + 2`.

At all listed maxima: **569 whole-state checks / 783,094,260 visits**. This includes the same-pair join omitted from the previous lower-bound estimate. Packet CopyInputs/CopyRows are internal copies, not public currentness accessors; they add no hidden scans.

For comparison, the production-only post-acceptance cost is `(12 + W + P + 2B)Q + P`. Parent Decode/Adopt/Take callbacks are not worker evaluation visits and must not be added to this counter.

## Prefix bound and complete count

Before TryResolveWitnesses, the native creation/capture code and canonical Bind/Capture probes cost at most `N + 19Q0 + L`. This includes the binding probe's GetPayloads, TryBindTransferPair, and three live reads (IsCurrent, ByteCount, Sha256); foreign/repeated copies reject before charged scans.

The successful resolver adds five Q0 scans, one state capture, two state revalidations, callback visits J, W witness-row visits, and two range visits.

A conservative callback bound is `J <= 76F + 65,632`:

- Six full-row passes, including projection and the optional watchdog pass.
- At most 70 owned-row passes: four fixed Named scans, up to 64 total RecentArmed/StallSuccessor/Write scans, and two other owned scans. Owned rows never exceed F.
- Two candidate/unresolved snapshots (at most 32,768 visits), two full/compact boundary passes (at most 32,768), 32 payloads, and 64 lifecycle entries.

Therefore accepted-prefix visits are bounded by:

`A <= N + 24(F+C) + L + T + 2D + J + W + 2 <= 35,389,650`.

Complete canonical path:

`A + R <= 35,389,650 + 783,094,260 = 818,483,910`.

The proposed 2^30 cap leaves **255,257,914 visits (~31.2%)** above that conservative bound. Some maxima cannot coexist because of other capacities; overcounting is intentional. A smaller cap based only on today's 27 rows would not cover the supported 1–64 witness and packet limits.

## Reconciliation and time allowance

The arithmetic model exactly reproduces the latest native counters:

| Quantity | Model / observed |
| --- | ---: |
| Metadata cells | 30,194 |
| State capture / revalidation | 252,059 / 252,067 |
| Whole-state check Q | 327,605 |
| Selection callback visits | 439,400 |
| Native resolver accepted | 3,082,449 |
| Before first probe ownership check | 4,065,264 |

The three complete post-acceptance scans took approximately **51.82 ms each** in that run. With today's state shape, assuming all 27 witnesses have compact counterparts and the packet reaches its maximum size, the audited path projects to **130,848,412 visits / about 39.5 seconds cumulative work**. Using the maximum 64-witness/42-descriptor tail with the same observed state and prefix gives about **48.8 seconds**.

Those timings are **projections, not completed runs or worst-case timing guarantees**. They omit unmeasured additional encoding/output overhead, and the 64-row illustration holds the observed prefix fixed. The proposed 60 seconds supplies bounded headroom for this workload while retaining a hard stop. At the maximum admitted state shape, scan cost is much higher; the time cap can legitimately refuse the optional diagnostic even though its visit demand fits. Do not use this model to claim that all maximum-size inputs complete.

Changing shared `ProducerMilliseconds` would also lengthen startup and rendezvous waits and affect unrelated publication APIs. Avoid that. Original wrapper/route parents already poll `TryAdoptIfOffered` under their existing watchdog; preserve that sequence and all idle wait values.

## Bounded implementation and verification plan

1. Add an evaluation-only 60,000 ms constant alongside the 2^30 visit cap. Keep the existing producer `ResourceCurrent` predicate at 20 seconds: raw SourceOrigin and SourceComplete also use it, so a global lifecycle-based switch would extend unintended APIs. Share its original liveness/scope/files/pins checks through a private native implementation, with an evaluation-specific caller using 60 seconds only for the original EvaluationCustody.Current path. VisitEvaluation must stay at 20 seconds during BeginEvaluation and use 60 seconds only after that method successfully stores original custody. Do not let a public caller parameter, copied token, observation, or replay activate the larger allowance.
2. Keep original source admission/read/origin checks, parent work limits, startup/rendezvous constants, and all unrelated consumers unchanged. Update the test-profile resource observer to report that same selected limit without changing the source-read observer or clock accounting.
3. Add exact visit-cap and phase/time-boundary controls: at-limit versus plus-one, no new custody after expired producer work, accumulated time preserved across transition, source operations not extended, and failed/stale/copied/repeated calls cannot reopen or refill. Exercise both PowerShell hosts. Existing state-capacity tests must not loop through ~1 billion visits merely to reach exhaustion; use an inert budget receiver near the boundary after separately proving the per-pass charge.
4. Run only changed boundary controls and directly affected evaluation, preparation/output, source-diagnostic, and canonical same-pair checks. Review existing literal source assertions/hashes explicitly; do not blindly update them. No aggregate matrix is needed for sizing or implementation.
5. Rebuild; freeze inputs; require strict preflights; run one fresh targeted offline native attempt. Require complete witness verification, same-pair join, original diagnostic commit, unchanged ten reports/failed route verdict, and closed handles/text. A successful diagnostic milestone still is not route-cycle acceptance. No automatic retry or live/full GUI matrix.

Review artifacts are under `artifacts/canonical-full-consumer-budget-review-20260929`. `budget-model.ps1` is arithmetic/source checking only: **12 source anchors and 512 witness/compact/descriptor/packet corner cases passed**, plus exact native-counter reconciliation. These are not new production regression or live-test results.
