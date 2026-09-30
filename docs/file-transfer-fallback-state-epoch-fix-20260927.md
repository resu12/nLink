# Fallback receiver-state epoch fix — 2026-09-27

Status: scoped runtime fix implemented; **44 focused/contract cases pass**.
The single new offline canonical attempt completed its 256 MiB transfer and
destination adoption, then failed at a later payload-offer/handoff gate. It is
not an acceptance pass, and no live test is due at this boundary.

## Bounded implementation

`SessionFileTransferService.RouteTransitionFallback.cs:2825` now resets only
`V4LastStateEpoch` to `-1` when adopting the coordinator-committed
`TunaV4ToPostTunaFallbackV6` successor. This is a six-line production addition.

The existing commit entry validates the exact source runtime owner, publication,
leg, data session, durable checkpoint and current coordinator transition before
commit. Once the successor is installed, the source-owner check refuses a
repeat. Consequently the reset does not require another generation counter,
observation-based authority, or a new coordinator contract. It also runs if a
coordinator commit is already known but its source-to-target local projection
has not yet occurred; it is not conditioned solely on a non-idempotent decision.

No other epoch field, durable progress, user pause, checkpoint policy, route
ownership, runtime unlock, stale callback rule, credit algorithm, retry cadence,
chunk size, queue depth, timeout, acceptance rule or origin-capacity limit changed.
In particular the duplicate comparison and `receive_install_missing` rejection
remain untouched. The reset is not in the general recovery initializer.

## Regression and fixture repairs

`SessionFileTransferFallbackStateEpochTests.cs` adds three transition cases with
predecessor epochs 1, 2 and 3. They apply the predecessor state, prepare/drain and
commit the coordinator transition through the service, stage/commit the current
sparse install, actually dispatch its checkpoint, and replay the exact retained
paused bootstrap frame. Current fallback state 2 must then:

- apply once, clear peer pause, grant 512 chunks and satisfy the leg's send gate;
- leave repair authority absent;
- remain idempotent on repetition;
- reject the predecessor owner/install and a missing current install;
- retain its epoch floor, pause/credit state and owner/publication after a
  repeated old commit is rejected.

These are deterministic unit fixtures with coordinator-produced transitions,
not a native bootstrap-exchange or live-network acceptance claim. They establish
state application and send eligibility; the canonical attempt below establishes
actual payload transfer.

The existing transition fixture accepts an optional retained frame and derives
checkpoint epoch/chunk count from its context, preserving its original defaults.
Three directly selected older controls required fixture-only migration:

- The lower-epoch checkpoint test now invokes the existing instance method and
  asserts its return; its telemetry assertion tolerates intervening scope fields.
- The old-transport checkpoint test likewise uses the existing instance method.
- The ordinary out-of-order state test matches its event and epoch fields on the
  same line instead of assuming their old adjacency.

Production behavior was not weakened to make those controls pass.

## Verification

Results are under `artifacts/fallback-state-epoch-fix-20260927/results`.

| Check | Final outcome |
| --- | --- |
| New transition regression, predecessor epochs 1/2/3 | 3 passed |
| Migrated lower-epoch checkpoint control | 1 passed |
| Adjacent stale/duplicate/source-hold/install/checkpoint guards | 17 passed |
| Coordinator `FileTransferRouteTransition` contract lane, run once | 23 passed |
| Fresh offline canonical 256 MiB attempt, run once | Failed at later payload-offer gate |

The meaningful pre-fix result is `regression-red-bound.trx`: epoch 2 returned
`Idempotent` instead of `Applied`; epoch 3 applied state but retained a closed
checkpoint send gate; epoch 1 passed. The same production change resolves both
failure cases. `regression-green.trx` has four passes; `regression-final.trx`
confirms the final three cases with actual checkpoint dispatch rather than an
already-queued test hint. The migrated lower-epoch control's pass remains valid.

`affected-guards.trx` contains 15 passes and the two stale fixture failures;
`affected-fixtures-fixed.trx` supplies the final two passes. The relevant lane
has 23 passes in `route-transition-contract.trx`. Final unique passing cases
total 44, without counting repeated executions twice.

Earlier setup failures are preserved: missing canonical bootstrap IDs, an
incorrect hard-coded target transport epoch, and older fixture/log bindings.
They precede the meaningful red regression and are not runtime bug evidence.
The test bootstrap target now comes from the actual predecessor leg.
Changed tracked files pass `git diff --check` (only CRLF conversion warnings).

## Single canonical attempt: transfer succeeds, later boundary fails

Directory:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-002e658a73fc424bb1787ae6f7e7e8a1`.
The existing fixture chooses this directory; prior attempts were not overwritten.
This was an offline named-pipe carrier process tree using the rebuilt Debug
Core service and existing production paths, not a portable GUI or live NKN run.

The original ledger has **101,847 lines / 92,028,303 bytes**, SHA-256
`f4781ded2c402c8003230d1ab22df6b805acca7284a2a30360f084e3f7ea9028`.
At physical line **949**, fallback receiver state 2 applies with previous epoch
`-1` and credit 512. Its matching repeat at **954** remains duplicate, applied 0.
This new attempt did not log an applied Tuna V4 state before fallback; the exact
inherited-epoch-2 collision is established by the deterministic regression,
not inferred from the successful transfer alone.

Both original apps report **268,435,456 bytes**, `post_tuna_fallback_v6`, the same
session/transfer, and SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
Independent source/destination hashing agrees. The GUI parent records
`attempt-destination-gui-adoption-ok` and `attempt-sender-source-delivered`.
Both payload files are released for exclusive access; the two app processes and
GUI process exited. Finalization reports exit 1, `timedOut=false`.

All three ledger outputs exist:

| Output | Lines |
| --- | ---: |
| Retained | 101,845 |
| Writer | 101,847 |
| Semantic | 73,705 |
| Aggregate | **277,397** |

These counts exceed the former 262,144 aggregate cap and match the expected
source/writer and bookmark-suffix cardinalities. Output files and counts alone
do not prove native facts serialization, custody handoff or primary acceptance.
Their hashes and the exact selected source lines are preserved in
`artifacts/fallback-state-epoch-fix-20260927/canonical-observation.json`.

The first observed later failure is
`FrontierAttemptPayloadWrapperProbe.ps1:35`: **Payload offer result/repeat changed**.
That combined assertion does not distinguish an original-offer refusal from an
unexpected repeat result. The subsequent normal-exit and parent-handoff errors
are downstream. No native writer-handoff success marker or original primary
analysis was produced. Do not attribute the gate failure to a byte/work limit,
origin mapping, or runtime authority without further evidence.

## Next bounded step

Investigate the original payload-offer/repeat gate using this latest run and
the current code. If the retained artifacts cannot distinguish its branches,
add failure-only diagnostic capture of the already-evaluated offer/repeat
results and existing custody/work state, without another authority evaluation,
then make at most one targeted offline attempt. Preserve the cumulative work
clock, original owners, exact mappings, serialization limit and acceptance.
No throughput tuning or further epoch change is indicated by this run.

Native writer handoff and original primary evaluation remain required. The
strict route cycle still needs a second coordinator-published `file_tuna_v4`;
neither this fallback completion nor SHA success establishes that contract.

## Preservation

All 2,159 previous pins matched before implementation. Existing unrelated dirty
work and historical reports/artifacts remain intact. Only one previously dirty
source file (`SessionFileTransferV4SenderTests.cs`) was edited; the production
transition file and shared transition fixture were previously clean. Rebuilt
Core/test assemblies replace their prior pinned build outputs as expected.

The checkpoint is
`artifacts/fallback-state-epoch-fix-20260927/FINAL-VERIFICATION.json`.
No portable rebuild, live network run, full matrix, commit or push occurred.
