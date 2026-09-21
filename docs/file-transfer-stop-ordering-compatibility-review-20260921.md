# Stop-ordering compatibility review and fixture repair plan

Base: `c426b2efacd3b36863e127552eb9d148fa5351fe`.
Scope: the uncommitted initial-Tuna local-stop ordering fix described in
`file-transfer-initial-tuna-stop-ordering-rca-20260921.md`.

## Review outcome

No new blocking defect was found in the bounded production diff. This is not a
green full-compatibility gate or live acceptance. The four failing legacy
controls all reproduce without the production diff. Their contracts/setup need
repair; weakening runtime guards to satisfy them would be incorrect.

The two production files were temporarily restored exactly to HEAD for the
baseline run, then their full normalized Git diff was restored and compared
equal to the saved pre-review diff. No production or test edits were made by
this review. The restored build completed the five selected changed-path and
blocked-notification controls successfully.

## Completed diagnostic runs

Artifacts: `artifacts/initial-tuna-stop-ordering-20260921/review`.

- `baseline.trx`: finished normally, three tests, one pass and two assertion
  failures; no aborted tests. The blocked-notification control passed.
- `restored.trx`: completed, five passed: both six-callback capture cases, both
  already-reset source cases, and the blocked-notification control.
- The previous two legacy assertion failures remain supported by the matched
  `controls.trx` / `baseline-controls.trx` pair in the parent artifact directory.

The baseline harness inactivity bound was 90 seconds, not the previous 45,
because the tests themselves wait 45 or 60 seconds. Application settings and
the tests' own deadlines were unchanged. This lets their actual assertions
finish instead of the collector terminating them first. The earlier collector
abort is not an application crash or a demonstrated deadlock.

## Findings on the legacy controls

| Control | Actual boundary | Disposition |
| --- | --- | --- |
| `FileTransferRouteStatus_PendingPostTunaFallbackSurvivesUserStopCleanupUntilConsumed` | Assertion at test line 2808 expects fallback selection to clear after a generic completion notification. The opened transfer ends in `_cleanup`, while the supplied notification ends in `_completed`. `ObserveFileTransferRouteCompleted` does not retire the fallback proof/owner for this notification. | Pre-existing stale cleanup expectation and mismatched identity. Do not restore unscoped adapter cleanup. |
| `TransportAccelerationStop_FreshPeerRuntimeUnlockClearsUserStoppedSessionGuard` | Assertion at line 28292 waits for ordinary `peer_payer_intent` after an unbound file-capable runtime-unlock request. | Pre-existing fixture/request-contract mismatch. |
| `TransportAccelerationStop_RemoteDownDoesNotBlockPeerPaidReunlock` | With HEAD production, the test finishes after approximately 61 seconds and fails at line 28364 waiting for both adapters to become active. | Ordinary timeout, not a hang; same missing preparation/source binding. |
| `TransportAccelerationStop_RuntimeUnlockClearsUserStoppedSessionGuard` | With HEAD production, the test finishes after approximately 47 seconds and fails at line 28433 waiting for both adapters to become active. Its earlier assertion that the local stopped-session guard clears succeeds. | Ordinary timeout; the previously unclassified fourth failure is now baseline-confirmed. |

The three re-unlock fixtures construct adapters and call
`SetAccelerationAcceptedForTests`, but attach no preparation Service and create
no real Coordinator transfer publication. `SelectSessionPreparationRequest`
therefore returns `preparation_binding_unavailable`; the retained baseline log
records that rejection with `authority_effect=none` at lines 50, 99 and 106.
The public request clears local user-stop flags before selection, but cannot
start source-less file activation or generate ordinary payer-intent traffic
when selection is unavailable.

This is required by `file-transfer-session-preparation-contract.md`, section 1
and the public runtime-unlock selection checkpoint: missing attachment is
fail-closed, and unavailable preparation must not fall through to activation.
Preparation is a distinct file-only readiness episode and does not send
ordinary payer-intent messages or publish an active transfer route. Consequently
adding an attachment alone would not justify the tests' old mixed-lane active
assertions. The first completion test also cannot be repaired merely by making
its two string IDs equal and expecting generic evidence to grant retirement.

## Production-diff checks

- Exact local-stop source copies still require mutual correlation selection,
  current session/provider and a Core-issued publication. Initial and committed
  source handling remains separate.
- The early helper only reserves observation identity and delivers evidence;
  Core still owns hold preparation, callback capture, settlement and publication.
  Core callbacks occur outside adapter state locks.
- Source send eligibility remains intact until the synchronous hold capture.
  Merely postponing physical retirement would leave the negotiation-reset race.
- The reset path reuses the prepared observation and epoch instead of replacing
  them underneath the hold.
- No-source stops retain early retirement. Already-reset negotiation retains
  its original exact-source delivery path. The blocked-down-notification case
  still completes stop before the remote notification is released.
- No callback predicate, stale-effect guard, Core authority schema, protocol,
  analyzer, retry cadence, queue setting, installer or performance policy changed.

## Bounded fixture repair plan

1. Keep the existing unbound setups as explicit negative coverage: no readiness,
   ordinary payer-intent send, active file publication or source-less activation
   may follow a missing preparation/transfer authority. Retain the successful
   immediate local user-stop-flag clearing assertion.
2. Preserve the positive stop/re-unlock intents in separate authority-correct
   fixtures, using real Service/Coordinator preparation or current transfer
   publication as appropriate. Test readiness separately from activation and
   require actual publication for route-active claims. Do not silently change
   a mixed/file test to screen-only merely to get a pass.
3. For pending fallback cleanup, retain wrong-transfer and generic-notification
   rejection checks. The positive retirement check must use the current exact
   Coordinator-owned lifecycle/receipt and verify that stale completion cannot
   affect a successor. Do not manufacture a commit or use a route cache as owner.
4. Run only the rewritten controls and their directly affected preparation or
   retirement cases. Review those fixture changes separately from the already
   bounded production fix; do not erase the retained baseline failures or count
   them as passes.
5. After disposition of that gate, checkpoint/rebuild and rerun only the same
   targeted 64 MiB scenario with strict preflights. The prior classifier timeout
   and missing parent-owned finalized analysis remain separate issues. No
   secondary authoritative analysis, full GUI matrix or live-256 run is implied.

No commit, push, portable build, live NKN/GUI test or fixture contract change was
performed in this review.

## Implemented fixture repair

The subsequent offline step completed the bounded fixture repair. The review
and baseline failures above are historical evidence, not erased or reclassified
as passes. Current artifacts: `artifacts/initial-tuna-stop-ordering-20260921/fixture-repair`.

- The three unbound re-unlock fixtures retain their mixed Chat/File/Screen
  capabilities and now explicitly require missing-binding rejection, no
  readiness/publication/offer/retry activation, no queued ordinary payer intent,
  and no new dial. Public local unlock still clears its own stop flag; an
  unbound peer request cannot clear the other endpoint's local stop flag.
  Automatic negotiation is suppressed in the previously unsuppressed fixture
  so the test isolates the public request rather than unrelated startup work.
- Four new `TransportAccelerationStop_BoundUnlockPreparesBeforeActualAdmission`
  cases cover both payer roles and local/remote stop. They wait for both lane
  stops, use real attached Services and public preparation, assert readiness
  without file effects, then actual admission and both initial Tuna4 publications.
  Only the old transport observation is seeded; no transfer authority is forged.
  The unchanged installed-Service retry control separately verifies real
  offer/probe/ACK and Coordinator publication. These are distinct purposes,
  not restoration of the old source-less mixed-lane activation expectation.
- The renamed pending-fallback control preserves proof generation and pending
  ownership after both wrong-transfer and matching generic completion events.
  Both joined six-callback cases now require exact committed disposition to
  retire pending proof and replay an old drain callback against the successor,
  asserting no owner/publication/persistent-state/progress/proof changes.

`affected.trx` completed 31/31; `final-controls.trx` completed 6/6. Both have
zero failures, skips or aborts. This closes the scoped offline fixture gate,
not a full compatibility suite or live acceptance gate. Development failures
and the removed seeded-baseline retry experiment are recorded in the artifact
results; they are not counted as passing coverage.

The production diff is exactly unchanged from before fixture repair and
`git diff --check` passes. No new authority, timer or performance changes, no
commit/push, no portable build and no live run. The next gate is checkpoint,
fresh portable build and the same targeted 64 MiB scenario with strict preflights;
it is not a full matrix or live-256 run. Prior classifier/finalization limitations
remain separate.
