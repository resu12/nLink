# Fallback V6 startup checkpoint fix

Date: 2026-09-26. Branch `v0.7.0`; HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result

The remaining actual-Service fallback lost-completion reproduction now passes.
There are **42 distinct applicable passing checks, zero failing and zero skipped**.
The final startup/fallback rerun passes all 13 directly affected cases.

Startup now establishes the existing coordinator checkpoint exchange before
ordinary data can flow. The completion guard still requires a recovered sender
leg; terminal-ready is still emitted only after destination finalization. No
wire format, route-cycle acceptance rule, or coordinator algorithm was relaxed.

## Implementation

- `SessionFileTransferService.PullTransferSessionV6.cs` captures the initial
  coordinator runtime owner, leg, transport epoch and data-session generation.
  It rejects a changed startup scope after manifest send and before receiver-state
  processing or checkpoint registration. The data pump is not created until
  checkpoint registration is attempted, so rejected startup continuations do not
  leave a detached pump using their stream.
- A successful manifest send is not treated as receiver readiness. The first
  matching manifest-derived receiver state schedules the existing checkpoint
  queue after that state is processed. Only the coordinator's matching checkpoint
  response can grant data permission.
- The native V6 receiver now routes a same-epoch fallback state-refresh request
  through the existing coordinator-backed inbound checkpoint registration and
  receiver-state response. Unrelated V6 control frames retain their prior handling.
- Before dispatch, the Service records the captured coordinator-issued startup
  runtime owner as a restriction, not a new authority. While it applies,
  missing/wrong IDs and stale epochs are rejected before progress/request/liveness
  effects. Ordinary current-peer-state supersession cannot satisfy this initial
  exchange. Exact coordinator acceptance clears the restriction; other recovery
  supersession rules are unchanged.
- Ordinary payload dequeue and pre-send checks honor the coordinator leg's data
  permission even without a checkpoint ID. Separately authorized frontier-repair
  contracts retain their existing validation path.
- Startup checkpoint IDs/recovery metadata are not copied onto ordinary data
  requests. Doing so mislabeled normal data as repair data and caused avoidable
  repair resends. No retry interval, queue size, chunk size, or throughput policy
  was changed.

The only production files edited in this step are the V6 Service partial and
`SessionFileTransferService.cs`. The completion-boundary test was strengthened;
`SessionFileTransferFallbackStartupCheckpointTests.cs` adds ten focused cases.

## Verification and evidence

Results are in `artifacts/fallback-startup-checkpoint-fix-20260926/results/`.

| Final applicable evidence | Result | Coverage |
| --- | --- | --- |
| `affected-contract-final.trx` | 42 passed | Completion boundary/finalization scope, startup gate/proof/scope, existing completion and stale-callback witnesses, native guard Service round trip, and three directly affected coordinator checkpoint tests. |
| `rejected-proof-effects-final.trx` | 3 passed | Stronger negative proof frames claim full progress and terminal-ready; missing ID, wrong ID and stale epoch still cannot advance progress or complete. |
| `startup-final.trx` | 13 passed | All ten startup tests and the three fallback completion cases after deferring pump creation until startup registration. |
| `pre-dispatch-binding-final.trx` | 4 passed | The startup owner restriction is already installed when the checkpoint request is dispatched; matching, mismatched and duplicate responses retain their expected effects. |

These overlap; they are not summed. The 42-case lane was run once. Only affected
cases were rerun after follow-up edits. The ten startup cases cover closed/no-ID
dequeue, a prepared batch after leg closure, missing/wrong/stale proof, duplicate
proof, and delayed manifest/receiver-state continuations after session generation
or runtime-owner replacement. Negative replacement mutations are test-only and
never grant production authority.

The final completion witness requires an actual matching receiver response before
the first fallback payload, not merely eventual completion. With the destination
finalizer held, neither side may complete and no terminal-ready success may be
sent. After release, success, destination collision, and dropped lifecycle/data
completion messages all reach their expected terminal outcomes.

The observed lost-completion sequence is:

1. Sender and receiver start with closed pending legs and no checkpoint ID.
2. Receiver sends initial state, epoch 1, terminal-ready false.
3. Sender registers and sends `v6-regular-nkn-state-refresh:1` under its coordinator
   lease; the receiver returns epoch 2 with that ID and transport epoch 1.
4. Sender's coordinator accepts the proof: `RecoveryActive`, data permission true,
   pending request ID cleared. Ordinary payload has no repair ID.
5. Only after destination finalization does the receiver send terminal-ready; the
   sender completes even though lifecycle and data-complete messages are dropped.

This proves the sender startup/completion path in an actual-Service loopback
exchange. It does not claim a live NKN handoff, that both peers' checkpoint leases
have retired, or GUI route-cycle acceptance. Mixed-version completion safety also
remains separate.

### Intermediate results retained honestly

- `closed-leg-red.trx` reproduces ordinary dequeue with a closed no-ID leg.
- Early `startup-first`, `manifest-ready-boundary`, and `matching-proof-red` runs
  passed completion but did not prove a matching checkpoint response. They are
  not acceptance evidence. The last filename describes its intended diagnostic
  role, not its actual pass/fail result.
- `exact-response-red.trx` fails the strengthened response assertion: native V6
  originally ignored the request and ordinary peer state retired it instead.
- `startup-scope-and-proof.trx` includes test reflection type mistakes (`int`
  receiver-state epoch read as `long`) and a too-short test wait. Those harness
  errors were corrected without changing production timing.
- `startup-scope-and-proof-v2.trx` exposed ordinary payload carrying checkpoint
  metadata and requiring a repair resend; the final tests require one data batch.
- `empty-transfer-final.trx` is an invalid exploratory fixture: the existing public
  send API rejects zero-byte descriptors. The test and speculative empty-file
  branch were removed; positive-size policy was not changed.
- Other intermediate green runs remain recorded but do not supersede the final
  applicable evidence above. A final build succeeded with zero warnings/errors.

## Preservation and next milestone

All 89 prior verification pins matched before implementation. Three pre-existing
dirty paths were intentionally modified; the other 257 are unchanged. The new test
and this report bring the dirty count from 260 to 262. Previous reports and result
artifacts are retained. `git -c core.safecrlf=false diff --check` passes.

Seal: `artifacts/fallback-startup-checkpoint-fix-20260926/FINAL-VERIFICATION.json`.

The completion blocker no longer prevents the next milestone: resume the native
receiver-side original-app destination handoff proof. This is not yet a reason to
run the full GUI matrix. No portable rebuild, live GUI/NKN run, commit, or push
occurred. The second coordinator-published `file_tuna_v4` requirement remains strict.
