# Late-publication fixed hold: bounded implementation verified

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result and scope

Implemented the separately approved fixed **4000 ms** data-retention policy only
for the small offline `frontier-late-publication` profile. The default scheduler
and positive `frontier-schedule` profile remain **3000 ms**. All **71 focused
tests passed**, with no failures or skips. This is test-fixture work, not a
production latency fix or an acceptance-policy change.

Four existing test/support files changed. No production source, primary analyzer,
native publication contract, route authority or earlier capture was changed.
No original-process characterization, live NKN, live-256 or GUI run occurred.
No commit or push.

## Implementation

- `AttemptOfflineFrameSchedule` selects a readonly cap at construction using a
  closed boolean choice. The default is unchanged; the late choice rejects
  non-Debug builds. No arbitrary duration, environment override or adaptive
  extension was added.
- Both budget reporting and enforced expiry use the same cap and each frame's
  original `Held.Started`. Reads, later arrivals and releases do not renew it.
  Expiry retains the 10 ms timer and clears held bytes without delivery.
- The receiver construction uses its existing explicit `latePublication`
  selection. Existing small-only, incompatible-profile and Debug gates remain.
- Construction logs exactly one fixed policy marker:
  `frontier-fixed-3s-v1; max_hold_ms=3000` or
  `late-publication-fixed-4s-v1; max_hold_ms=4000`.
  The original-pair test now checks the receiver's exact marker on completion.
  That integration assertion was compiled but not exercised by a new pair run.
- Three-frame/1 MiB limits, descriptor/byte identity, release order 2,1,4,
  cancellation and disposal behavior are unchanged.

The private expiry helper now receives the TickCount64 sample taken under its
existing lock. Exact-boundary unit controls invoke only this owned test helper
with synthetic ticks; no production clock or retained timestamp is replaced.
Separate real-timer controls verify original 3-second expiry and late-profile
survival beyond 3 seconds followed by original 4-second expiry.

## Focused verification

One Debug build/test invocation:

```powershell
dotnet test tests/nLink.SmokeTests.Core/nLink.SmokeTests.Core.csproj --no-restore -c Debug -p:UsedAvaloniaProducts= --filter 'FullyQualifiedName~AttemptOfflineFrameScheduleTests|FullyQualifiedName~AttemptLatePublicationTests.LateWindow_|FullyQualifiedName~AttemptLatePublicationTests.HoldBudget_' --logger 'trx;LogFileName=focused.trx' --results-directory artifacts/late-publication-fixed-hold-implementation-20260928
```

- 38 scheduler/admission cases: default and explicit policy selection,
  readonly cap, one diagnostic marker, exact expiry boundaries for both caps,
  real-timer non-renewal, byte/order/capacity parity, cancellation/disposal,
  invalid metadata and incompatible/full-size profile rejection before carrier
  access.
- 31 window cases: unchanged shape, budget and original-write checks; the
  existing exact-boundary diagnostics; and two clearly synthetic 3s-rejection /
  4s-fit cases. The latter do not claim a native receipt.
- 2 read/release non-renewal cases, one per profile.

`focused.trx` records 71 passed, 0 failed, 0 skipped; `focused.log` records the
successful Debug build and test run. The 338 compiler/analyzer warnings match
the preceding diagnostic build after source line-number normalization.
No aggregate suite was run. Scoped whitespace review passed.

## Invariants and next gate

The native 750 ms stall deadline, S+250 chunk release, S+800 copy release,
1000 ms post-write bound, ordinary direct-copy timers, reserves and earliest
publication-owner expiry remain unchanged. The scheduler's extra second is
not an extra second for the publication owner. Original primary selection,
witnesses, evaluations, output custody and acceptance remain unchanged.

Next: separately gate **one rebuilt, isolated small original-process
`frontier-late-publication` characterization**, with fresh source/binary pins,
a create-new run marker and policy-tagged original evidence. No automatic retry.
If it fails, preserve the original diagnostics and identify the first missing
proof edge before any further change. Do not increase the cap adaptively.

Even a successful revised run would not retroactively pass either failed
3-second capture, establish same-pair live-256 proof or authorize the full GUI
matrix. This implementation stops before that original-process run.

## Preservation

Entry matched all 2,046 prior pins and 512 dirty entries. The four test/support
sources and rebuilt Debug test DLL are the only intentional prior-pin
replacements; hash-matching before-images are retained. The other 2,041 prior
pins and all unrelated dirty contents/statuses are unchanged. The only new
non-ignored file is this handoff (513 dirty entries total). The final seal and
independent check are under
`artifacts/late-publication-fixed-hold-implementation-20260928/`.
