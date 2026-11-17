# Fallback receiver-state epoch collision: RCA and bounded fix plan

Date: 2026-09-27. Status: cause characterized; runtime fix not implemented.
Baseline: `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.

## Outcome

The first unclosed edge is **application of the current fallback receiver state
after coordinator checkpoint acceptance**, not route publication or an absent
frontier request. The sparse state path carries Tuna V4's last receiver-state
epoch into fallback V6. In the failed run both that inherited floor and the
first usable fallback state are `2`. The state is consequently called a
duplicate, so its unpaused flag and 512-chunk credit do not replace the retained
bootstrap state's paused flag and zero credit. The sender cannot send payload.

This mechanism is supported by the newest original ledger, a prior successful
transfer control with the same receive-install rejection, current source, and
a deterministic three-case characterization. It is not yet a passing regression
through an actual coordinator route transition, nor an end-to-end fix proof.

The earlier `receive_install_missing` lead is not a sufficient explanation:
it occurs in both runs and later current receiver state reaches the sender.
Do not relax receive-install or stale-owner checks to address this failure.

## Evidence and provenance

Primary source:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-5b4c5875a1d74caba18ffb45fc800667/live-operational-log/nlink.log`.
It has 2,629 physical lines / 2,656,917 bytes; SHA-256
`fcbd5b38d57313e7632bf27fbd56e33b4c542ca3619df69b4218474bc47cc511`.
This is the most recent failed **offline named-pipe carrier** attempt, not a
live NKN or interactive GUI run. The subsequent origin-capacity implementation
did not modify the transfer-runtime files involved here.

Sender writer: `p55788r0187aa41fb444554`.
Receiver writer: `p38040rb40a048ef8f54f11`.
The following numbers are physical lines in that immutable original ledger:

| Lines | Observation and implication |
| --- | --- |
| 220, 595 | Regular state epoch 1 applies, then Tuna state epoch 2 applies under runtime/leg 2. The shared sparse epoch floor is now 2. |
| 811, 835, 873 | Coordinator publishes fallback V6 under runtime/leg 3; outbound sparse installation 3 is installed. |
| 890 | Receiver emits unpaused V6 epoch 1 with a 512-chunk window and the correlated state-refresh checkpoint ID. |
| 915, 923 | The **receiver** writer queues the **sender-originated state-refresh request** on control/bulk lanes. These are not evidence of receiver payload demand admitted at the sender. |
| 926, 943 | Retained bootstrap proof is applied through the V6 handler: epoch 3, paused, zero credit. The receipt identifies `fallback_bootstrap_state.v6`. |
| 955, 957 | Sender reports zero credit/zero sent bytes; a receive captured without an install is rejected. This does not grant permission to rebind it. |
| 990, 995 | Later peer state supersedes the checkpoint, and the fallback checkpoint is accepted for leg 3/transport epoch 1. |
| 996, 1000 | That current fallback state has epoch 2 / previous epoch 2; both copies are `duplicate=1; applied=0`, despite carrying credit 512. |
| 1039 | Receiver's corresponding state-send completion records epoch 2 and `transfer_paused=0`. Asynchronous completion logging need not precede receiver consumption. |
| 1799 | Receiver times out after about 90 seconds with zero payload progress. |

The state fingerprints differ between the Tuna frame at 595 and fallback frame
at 996. They are not duplicate wire states; only their numeric state epochs
collide across the protocol transition. The duplicate at 1000 does match 996.
No fallback epoch-floor-reset event is present in this failed ledger.

Control source:
`artifacts/payload-fallback-primary-20260927/pwsh-aa77cfa82e0d455593f05bf4e5a9f4d8/live-operational-log/nlink.log`, SHA-256
`95baece822f619c5bb77c863c4b4c20c049a61ccb996474136356b8e7007eacc`.
It has the same bootstrap pause (901) and `receive_install_missing` (918), but
fallback state 2 sees previous epoch **1** and applies at 955; the repeat is
correctly duplicate at 961. This earlier transfer completed, then hit the former
origin-reference cap. It is a comparative control, not a new acceptance result.

Exact selected lines and both source hashes are retained in
`artifacts/fallback-state-epoch-rca-20260927/source-witnesses.json`.
That file is derived RCA evidence, not native custody or primary analysis.

## Source-level causal chain

1. `SessionFileTransferService.RouteTransitionFallback.cs:2820` applies the
   coordinator route decision, then the target runtime at `:5429` initializes
   fallback recovery. `ResetOutboundPostTunaFallbackLegOwnedStateLocked` in
   `SessionFileTransferService.PullTransferSession.cs:5724` clears old credit
   and leg-owned queues, but does not reset/scope `V4LastStateEpoch`.
2. Retained bootstrap replay (`RouteTransitionFallback.cs:3456`) projects the
   paused bootstrap state via `CreateRetainedBootstrapReceiverState`
   (`BootstrapCorrelation.cs:363`) into `ApplyOutboundV6ReceiverState`
   (`PullTransferSessionV6.cs:443`). This updates `V6LastReceiverStateEpoch`,
   shared peer pause and credit, but not the sparse `V4LastStateEpoch`.
3. The sparse receive dispatch routes a V6 receiver state through
   `ApplyOutboundV4StateAsync` (`PullTransferSessionV4.cs:13399`). Existing exact
   receive-install/owner checks remain applicable. Checkpoint acceptance is
   evaluated before the numeric duplicate branch.
4. The fallback epoch-floor recovery at `:13494` only handles
   `state.Epoch < context.V4LastStateEpoch`. Equality bypasses it and enters the
   duplicate branch. Normal credit and peer-pause updates occur only in the
   fresh-state branch (`:13653`, `:13734`).
5. The sender pump's peer-pause gate (`:10899`) and zero-credit ceiling prevent
   payload scheduling. This explains the stall without changing timeouts,
   bandwidth targets, queue capacity, or the origin-count policy.

The selected source paths are under `src/nLink.Core/FileTransfer/` and are
unchanged by this RCA. An unconditional `<` to `<=` change is unsafe: it would
also promote real same-scope duplicate states to fresh applications.

## Deterministic characterization and focused controls

`artifacts/fallback-state-epoch-rca-20260927/Characterize-EpochCollision.ps1`
loads the existing Core and smoke-test assemblies, uses the existing fallback
context factory, applies a paused/zero-credit bootstrap state through the V6
handler, then applies the same unpaused/credit-512 state at epoch 2 through the
sparse handler. Only the inherited V4 floor varies. The final script asserts:

| Inherited V4 floor | First fallback state 2 | Peer paused afterward | Credit afterward |
| --- | --- | --- | --- |
| 1 | Applied | false | 512 |
| 2 | Idempotent / duplicate | true | 0 |
| 3 | Applied via existing lower-epoch recovery | false | 512 |

All three repeated state-2 applications are idempotent and leave the sampled
state unchanged. Results and exact assembly hashes are in
`characterization-result.json`. This synthetic primitive characterization does
not exercise original receive-install admission, the actual route transition,
payload transport, native custody, or acceptance. It reproduces the harmful
state-application distinction only.

Initial diagnostic-helper setup errors were corrected locally: the existing
snapshot stream must be enabled, the internal leg enum needs reflection, and
all required artifact-log environment variables must be set. These errors did
not cause production changes or live-network attempts.

One focused existing-test invocation produced **3 passed, 1 failed**:

- PASS: `PostTunaFallbackV6CurrentReceiverStateWithOldV4EpochAndMissingRange_YieldsAfterCurrentFallbackProgress`.
- PASS: `PostTunaFallbackV6DuplicateReceiverState_IsEvidenceOnlyForRepairScheduling`.
- PASS: `OutboundSparseReceive_StateSideEffectsCannotCrossSuccessorInstall`.
- FAIL before state application: `FileTunaV4_LiveSwitchOffRecovery_AppliesFallbackCheckpointBelowOldV4StateEpoch`.

The last failure is a stale fixture: `SessionFileTransferV4SenderTests.cs:15498`
looks up `MarkOutboundFallbackCheckpointRequestedLocked` as static, whereas
`SessionFileTransferService.cs:21599` defines it as an instance method. It throws
`NullReferenceException` before its floor-reset assertion. This is not evidence
against the collision mechanism and is not counted as a pass. Preserve the
failed TRX at `results/epoch-controls.trx`; migrate this fixture during the
bounded implementation, without restoring a production static shim.

## Bounded fix plan — not implemented

1. **Add the actual transition regression and repair the directly relevant
   stale fixture.** Reproduce Tuna epoch 2 -> coordinator-committed fallback
   successor -> retained paused bootstrap -> current authorized fallback epoch
   2. Require fresh application exactly once, pause cleared and credit 512;
   verify the existing sender path can proceed without adding repair authority.
   Verify: the new regression fails on the present runtime for the documented
   reason, and the migrated lower-epoch fixture reaches its assertions.
2. **Give the sparse receiver-state epoch floor the correct successor scope.**
   Prefer initializing the floor once at the existing coordinator-authorized
   Tuna-V4-to-fallback-V6 target-runtime adoption boundary. Use the existing
   committed authority/runtime/leg identity to make repeated adoption a no-op;
   add only the minimal local bookkeeping if existing identity is insufficient.
   Do not reset from frame arrival, transport observations, a route-token string,
   a retry, or every call to the general recovery initializer. Keep durable
   progress, user pause, route ownership and runtime unlock under their existing
   contracts; reset only the state-stream freshness bookkeeping that belongs
   to the successor. Verify equal, lower and higher predecessor-floor cases.
3. **Protect idempotence and stale refusal before one canonical validation.**
   Cover same-scope duplicates, repeated committed target adoption, old install/
   runtime/leg callbacks, wrong checkpoint/transport scope, source hold and
   terminal context. A rejected callback must remain diagnostic-only. Run only
   the directly affected service/coordinator controls and at most one relevant
   broader contract lane once stable. Then one fresh offline canonical 256 MiB
   attempt must complete transfer/SHA, finalized destination adoption, expanded
   origin processing, native handoff and original primary evaluation under
   unchanged limits. Stop at any new first proof edge; no retries to seek green.

Do not relax the missing-install guard, globally admit equal epochs, authorize
repair from a state fingerprint, merge bootstrap and ordinary state counters
without a reviewed ownership rule, or clear pause merely because a transport
reports ready. No bridge/concurrency/chunk/retry/performance tuning is involved.

This plan does not authorize a live test or full matrix. A future runtime GUI
validation requires a fresh portable rebuild and successful escalated preflight.
The strict route-cycle contract still requires the second coordinator-published
`file_tuna_v4`; neither this RCA nor SHA completion substitutes for that proof.

## Preservation and next boundary

The previous 2,150 checkpoint pins and all 386 pre-existing dirty entries were
verified before this work. Existing code, tests, prior reports and artifacts
are preserved. New work is confined to this report and its RCA artifacts.
The chained verification record is
`artifacts/fallback-state-epoch-rca-20260927/FINAL-VERIFICATION.json`.

No runtime/acceptance change, canonical retry, portable build, live network run,
GUI matrix, commit or push was performed. The next bounded step is implementation
of the scoped epoch fix and its transition regression, not a live rerun.
